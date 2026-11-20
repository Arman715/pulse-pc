using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace PulsePC.Services;

public class GameNetStats
{
    public string Server;            // "185.25.182.77" или null, пока сервер не найден
    public int ServerPort;
    public double? AvgPingMs, JitterMs, LossPct;  // ICMP до сервера; null, если ответов не было вовсе
    public bool PingBlocked;         // сервер ни разу не ответил на ICMP (для игровых серверов обычное дело) - не неисправность
    public int Gaps;                 // паузы входящего потока от сервера дольше 250 мс, пока игра была активна (за всю сессию)
    public double MaxGapSec;         // самая длинная такая пауза
    public double InPps, OutPps;     // среднее число пакетов в секунду от сервера и к серверу (за время обмена с ним)
    public List<DateTime> GapTimes = new();  // местное время начала каждой паузы (для сопоставления с просадками FPS), не больше 200
}

/// <summary>
/// Сервер игры и качество связи с ним. Слушает системные события Windows о сетевых пакетах
/// (ETW, поставщик Microsoft-Windows-Kernel-Network) и отбирает пакеты процесса игры.
/// Как и PresentMon, в процесс игры не вмешивается. Нужны права администратора.
/// Сервер - внешний адрес, с которым игра обменивается больше всего пакетов за последние ~10 с.
/// До сервера раз в 2 с идёт обычный пинг (многие игровые серверы на него не отвечают - это нормально).
/// </summary>
public sealed class GameNetMonitor : IDisposable
{
    /// <summary>Своё имя сеанса у каждой записи: остановка сеанса прошлой игры (идёт в фоне) не обрывает сеанс следующей.</summary>
    private readonly string SessionName = EtwSessions.NewName("Net");
    private static readonly Guid ProviderGuid = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    private static readonly long ProvLo = BitConverter.ToInt64(ProviderGuid.ToByteArray(), 0);
    private static readonly long ProvHi = BitConverter.ToInt64(ProviderGuid.ToByteArray(), 8);

    // Ключевые слова и номера событий - из манифеста поставщика (wevtutil gp Microsoft-Windows-Kernel-Network /ge /gm).
    private const ulong KeywordIpv4 = 0x10, KeywordIpv6 = 0x20;
    private const int TcpSend4 = 10, TcpRecv4 = 11, TcpSend6 = 26, TcpRecv6 = 27;
    private const int UdpSend4 = 42, UdpRecv4 = 43, UdpSend6 = 58, UdpRecv6 = 59;

    private const long Second = TimeSpan.TicksPerSecond;           // время событий - FILETIME, шаг 100 нс
    private const long GapTicks = 250 * TimeSpan.TicksPerMillisecond;
    private const int Buckets = 5;                                 // окно выбора сервера: 5 отрезков по 2 с
    private const int MaxRemotes = 2048, MaxGapTimes = 200, Ring = 16;

    /// <summary>Адрес: IPv6 как два 64-битных числа, IPv4 - в виде ::ffff:a.b.c.d.</summary>
    internal readonly record struct Addr(ulong Hi, ulong Lo);

    /// <summary>Разобранное событие о пакете.</summary>
    internal struct Raw
    {
        public int Pid;
        public bool Inbound;
        public Addr D, S;
        public int DPort, SPort;
    }

    private sealed class Remote
    {
        public Addr Key;
        public int Port;
        public long In, Out, First, Last, LastIn, LastOut;
        public readonly long[] InTimes = new long[Ring];   // времена последних входящих пакетов (кольцо)
        public int RingPos;
        public readonly int[] Window = new int[Buckets];
        public int Gaps;
        public long MaxGap;
        public readonly List<long> GapStarts = new();
        public bool WasServer;
        public IPAddress Ip;
        public PingTarget Ping;
    }

    private readonly object gate = new();
    private readonly Dictionary<Addr, Remote> remotes = new();
    private volatile HashSet<Addr> localAddrs = new();
    private int pid, bucket;
    private Remote current;
    private long lastInTime;                 // последний входящий пакет игры от любого адреса
    private Addr lastInFrom;

    private EventRecordCallback callback;    // держим ссылку, иначе сборщик мусора уберёт делегат под ногами у ETW
    private ulong traceHandle = InvalidHandle;
    private IntPtr logFile, loggerName;
    private Thread thread;
    private CancellationTokenSource cts;
    private Task loop;
    private bool running;

    /// <summary>Сеанс запущен и поток чтения событий жив (для отметки здоровья во время игры).</summary>
    public bool Running => running && thread is { IsAlive: true };

    /// <summary>Код последней ошибки ETW (для самопроверки).</summary>
    internal uint LastError { get; private set; }

    public bool Start(int pid)
    {
        bool ok = StartCore(pid);
        // Отказ при правах администратора — неисправность; без прав сеанс и не должен запускаться.
        if (ok) Health.Ok("Сеть игры");
        else if (LastError != 5) Health.Fail("Сеть игры", $"сеанс трассировки не запустился, код {LastError}");
        return ok;
    }

    private bool StartCore(int pid)
    {
        try
        {
            if (running) Stop();
            this.pid = pid;
            localAddrs = LocalAddresses();

            StopSession();   // на всякий случай: имя своё, но этот же объект мог запускаться раньше (остатки чужих запусков убирает EtwSessions.CleanupStale)
            uint err = StartSession(true);
            if (err == ErrAlreadyExists) { StopSession(); err = StartSession(true); }
            if (err != 0) err = StartSession(false);   // на случай, если общий буфер не поддерживается
            if (err != 0) { LastError = err; return false; }

            var provider = ProviderGuid;
            err = EnableTraceEx2(sessionHandle, ref provider, EnableProvider, LevelInformation, KeywordIpv4 | KeywordIpv6, 0, 0, IntPtr.Zero);
            if (err != 0) { LastError = err; StopSession(); return false; }

            // Потребитель: EVENT_TRACE_LOGFILEW (448 байт на x64), заполняем по смещениям.
            callback = OnEvent;
            loggerName = Marshal.StringToHGlobalUni(SessionName);
            logFile = Marshal.AllocHGlobal(LogFileSize);
            Zero(logFile, LogFileSize);
            Marshal.WriteIntPtr(logFile, 8, loggerName);                                   // LoggerName
            Marshal.WriteInt32(logFile, 28, ModeRealTime | ModeEventRecord);               // ProcessTraceMode
            Marshal.WriteIntPtr(logFile, 424, Marshal.GetFunctionPointerForDelegate(callback)); // EventRecordCallback
            traceHandle = OpenTraceW(logFile);
            if (traceHandle == InvalidHandle || traceHandle == InvalidHandle32)
            {
                LastError = (uint)Marshal.GetLastPInvokeError();
                traceHandle = InvalidHandle;
                StopSession(); FreeNative();
                return false;
            }

            ulong handle = traceHandle;
            thread = new Thread(() => { try { ProcessTrace(new[] { handle }, 1, IntPtr.Zero, IntPtr.Zero); } catch { } })
                { IsBackground = true, Name = "PulsePC-Net ETW" };
            thread.Start();

            cts = new CancellationTokenSource();
            var token = cts.Token;
            loop = Task.Run(() => Loop(token));
            running = true;
            return true;
        }
        catch (Exception ex) { Log.Error("сеть игры: запуск сеанса", ex); try { StopSession(); } catch { } return false; }
    }

    /// <summary>Остановить запись и вернуть итог. Не дольше ~3 с.</summary>
    public GameNetStats Stop()
    {
        try
        {
            if (running)
            {
                running = false;
                try { cts?.Cancel(); } catch { }
                StopSession();   // сеанс отдаёт оставшиеся события, и ProcessTrace завершается сам
                bool done = thread == null || thread.Join(1500);
                if (traceHandle != InvalidHandle) { try { CloseTrace(traceHandle); } catch { } traceHandle = InvalidHandle; }
                if (!done) done = thread.Join(500);
                try { loop?.Wait(800); } catch { }
                if (done) FreeNative();   // если поток так и не вышел, лучше оставить пару сотен байт, чем рисковать
                thread = null;
            }
        }
        catch { }
        try { Evaluate(); } catch { }
        return Snapshot();
    }

    public void Dispose() { try { Stop(); } catch { } }

    public GameNetStats Snapshot()
    {
        try
        {
            lock (gate)
            {
                Remote r = null;
                foreach (var x in remotes.Values)
                    if (x.WasServer && (r == null || x.In + x.Out > r.In + r.Out)) r = x;
                var s = new GameNetStats();
                if (r == null) return s;

                s.Server = Format(r.Key);
                s.ServerPort = r.Port;
                double span = Math.Max(1.0, (r.Last - r.First) / (double)Second);
                s.InPps = r.In / span;
                s.OutPps = r.Out / span;
                s.Gaps = r.Gaps;
                s.MaxGapSec = r.MaxGap / (double)Second;
                foreach (long ft in r.GapStarts) s.GapTimes.Add(DateTime.FromFileTimeUtc(ft).ToLocalTime());

                var c = r.Ping?.Counters ?? default;
                if (c.Received > 0)
                {
                    s.AvgPingMs = c.SumMs / c.Received;
                    s.JitterMs = c.Received > 1 ? c.SumAbsDiff / (c.Received - 1) : null;
                    s.LossPct = 100.0 * c.Lost / c.Sent;
                }
                else s.PingBlocked = c.Sent >= 5;
                return s;
            }
        }
        catch { return new GameNetStats(); }
    }

    // ---------- Обработка событий ----------

    // Вызывается из ProcessTrace на каждое сетевое событие системы: только чтение пары полей и счётчики.
    private void OnEvent(IntPtr rec)
    {
        try
        {
            // EVENT_RECORD (x64): ProviderId @24, EventDescriptor.Id @40, TimeStamp @16, UserDataLength @86, UserData @96.
            if (Marshal.ReadInt64(rec, 24) != ProvLo || Marshal.ReadInt64(rec, 32) != ProvHi) return;
            int len = (ushort)Marshal.ReadInt16(rec, 86);
            IntPtr data = Marshal.ReadIntPtr(rec, 96);
            if (len < 20 || data == IntPtr.Zero || Marshal.ReadInt32(data) != pid) return;   // PID процесса - в самом событии
            if (!Parse((ushort)Marshal.ReadInt16(rec, 40), data, len, out var p)) return;
            Feed(p, Marshal.ReadInt64(rec, 16));
        }
        catch { }
    }

    /// <summary>
    /// Разбор полезной нагрузки (шаблоны манифеста):
    /// IPv4: PID u32 @0, size u32 @4, daddr @8, saddr @12, dport u16 @16, sport u16 @18 (адреса и порты - в сетевом порядке байт).
    /// IPv6: PID @0, size @4, daddr 16 байт @8, saddr @24, dport @40, sport @42.
    /// </summary>
    internal static bool Parse(int id, IntPtr data, int len, out Raw p)
    {
        p = default;
        bool v6, inbound;
        switch (id)
        {
            case TcpSend4: case UdpSend4: v6 = false; inbound = false; break;
            case TcpRecv4: case UdpRecv4: v6 = false; inbound = true; break;
            case TcpSend6: case UdpSend6: v6 = true; inbound = false; break;
            case TcpRecv6: case UdpRecv6: v6 = true; inbound = true; break;
            default: return false;
        }
        if (data == IntPtr.Zero || len < (v6 ? 44 : 20)) return false;

        p.Pid = Marshal.ReadInt32(data, 0);
        p.Inbound = inbound;
        if (!v6)
        {
            p.D = V4(BinaryPrimitives.ReverseEndianness((uint)Marshal.ReadInt32(data, 8)));
            p.S = V4(BinaryPrimitives.ReverseEndianness((uint)Marshal.ReadInt32(data, 12)));
            p.DPort = BinaryPrimitives.ReverseEndianness((ushort)Marshal.ReadInt16(data, 16));
            p.SPort = BinaryPrimitives.ReverseEndianness((ushort)Marshal.ReadInt16(data, 18));
        }
        else
        {
            p.D = new Addr(Be64(data, 8), Be64(data, 16));
            p.S = new Addr(Be64(data, 24), Be64(data, 32));
            p.DPort = BinaryPrimitives.ReverseEndianness((ushort)Marshal.ReadInt16(data, 40));
            p.SPort = BinaryPrimitives.ReverseEndianness((ushort)Marshal.ReadInt16(data, 42));
        }
        return true;
    }

    /// <summary>
    /// Чей адрес удалённый. По манифесту у входящих источник - saddr, но разные версии Windows (и старый NT Kernel Logger)
    /// называют поля по соединению, а не по направлению. Поэтому сначала смотрим, какой из адресов - свой.
    /// </summary>
    internal static void ChooseRemote(in Raw p, HashSet<Addr> local, out Addr remote, out int port)
    {
        bool dLocal = local != null && local.Contains(p.D), sLocal = local != null && local.Contains(p.S);
        bool remoteIsSource = dLocal != sLocal ? dLocal : p.Inbound;
        remote = remoteIsSource ? p.S : p.D;
        port = remoteIsSource ? p.SPort : p.DPort;
    }

    /// <summary>Учесть пакет игры. time - FILETIME (UTC).</summary>
    internal void Feed(in Raw p, long time)
    {
        ChooseRemote(p, localAddrs, out var key, out int port);
        if (!IsPublic(key)) return;
        lock (gate)
        {
            if (!remotes.TryGetValue(key, out var r))
            {
                if (remotes.Count >= MaxRemotes) return;
                r = new Remote { Key = key, First = time, Last = time };
                remotes[key] = r;
            }
            r.Port = port;
            r.Window[bucket]++;
            if (time > r.Last) r.Last = time;
            if (time < r.First) r.First = time;

            if (!p.Inbound)
            {
                r.Out++;
                if (time > r.LastOut) r.LastOut = time;
                return;
            }

            r.In++;
            long prev = r.LastIn;
            if (time <= prev) return;   // событие пришло не по порядку - на паузы не влияет
            if (prev != 0 && time - prev > GapTicks)
            {
                // До паузы сервер слал не меньше 10 пакетов/с: 10 предыдущих входящих уложились в секунду.
                long tenBack = r.InTimes[(r.RingPos - 11 + Ring * 2) % Ring];
                bool busy = tenBack != 0 && prev - tenBack <= Second;
                // Игра в это время жила: что-то отправляла серверу или получала от других адресов.
                bool active = r.LastOut > prev || (lastInTime > prev && lastInFrom != key);
                if (busy && active)
                {
                    r.Gaps++;
                    r.MaxGap = Math.Max(r.MaxGap, time - prev);
                    if (r.GapStarts.Count < MaxGapTimes) r.GapStarts.Add(prev);
                }
            }
            r.LastIn = time;
            r.InTimes[r.RingPos] = time;
            r.RingPos = (r.RingPos + 1) % Ring;
            if (time > lastInTime) { lastInTime = time; lastInFrom = key; }
        }
    }

    /// <summary>Выбрать текущий сервер: больше всего пакетов за последние ~10 с. Сдвигает окно на один отрезок.</summary>
    private Remote Evaluate()
    {
        lock (gate)
        {
            Remote best = null;
            int bestN = 0;
            foreach (var r in remotes.Values)
            {
                int n = 0;
                foreach (int v in r.Window) n += v;
                if (n > bestN) { bestN = n; best = r; }
            }
            bucket = (bucket + 1) % Buckets;
            foreach (var r in remotes.Values) r.Window[bucket] = 0;

            if (best != null && bestN >= 10)
            {
                current = best;
                best.WasServer = true;
                best.Ip ??= IPAddress.Parse(Format(best.Key));
                best.Ping ??= new PingTarget { Host = Format(best.Key) };
            }
            return current;
        }
    }

    private async Task Loop(CancellationToken token)
    {
        using var ping = new Ping();
        int n = 0;
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(2000, token); } catch { break; }
            try
            {
                if (++n % 15 == 0) localAddrs = LocalAddresses();   // адрес компьютера мог смениться
                var target = Evaluate();
                if (target?.Ip == null) continue;
                double ms;
                try
                {
                    var reply = await ping.SendPingAsync(target.Ip, 1000);
                    ms = reply.Status == IPStatus.Success ? Math.Max(reply.RoundtripTime, 0.5) : double.NaN;
                }
                catch { ms = double.NaN; }
                if (!token.IsCancellationRequested) target.Ping.Add(ms);
            }
            catch { }
        }
    }

    // ---------- Адреса ----------

    private static Addr V4(uint ip) => new(0, 0xFFFF_0000_0000UL | ip);
    private static bool IsV4(Addr a) => a.Hi == 0 && (a.Lo >> 32) == 0xFFFF;
    private static ulong Be64(IntPtr data, int offset) => BinaryPrimitives.ReverseEndianness((ulong)Marshal.ReadInt64(data, offset));

    /// <summary>Внешний ли адрес: локальные сети, VPN, служебные и групповые адреса отбрасываются.</summary>
    internal static bool IsPublic(Addr a)
    {
        if (IsV4(a))
        {
            uint ip = (uint)a.Lo;
            uint b0 = ip >> 24, b1 = (ip >> 16) & 0xFF;
            return !(b0 == 0 || b0 == 10 || b0 == 127
                     || b0 == 25 || b0 == 26                    // Hamachi и Radmin VPN
                     || b0 >= 224                               // групповые, зарезервированные, широковещательный
                     || (b0 == 172 && (b1 & 0xF0) == 16)
                     || (b0 == 192 && b1 == 168)
                     || (b0 == 100 && (b1 & 0xC0) == 64)        // 100.64.0.0/10 - NAT провайдера
                     || (b0 == 169 && b1 == 254));
        }
        if (a.Hi == 0) return false;                            // ::, ::1 и прочее служебное
        uint top = (uint)(a.Hi >> 48);
        return (top & 0xFFC0) != 0xFE80                         // локальный для канала
            && (top & 0xFE00) != 0xFC00                         // частная сеть IPv6
            && (top & 0xFF00) != 0xFF00;                        // групповой
    }

    internal static string Format(Addr a)
    {
        if (IsV4(a))
        {
            uint ip = (uint)a.Lo;
            return $"{ip >> 24}.{(ip >> 16) & 255}.{(ip >> 8) & 255}.{ip & 255}";
        }
        var b = new byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(b, a.Hi);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(8), a.Lo);
        return new IPAddress(b).ToString();
    }

    internal static Addr FromIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        byte[] b = ip.GetAddressBytes();
        return b.Length == 4
            ? V4(BinaryPrimitives.ReadUInt32BigEndian(b))
            : new Addr(BinaryPrimitives.ReadUInt64BigEndian(b), BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(8)));
    }

    private static HashSet<Addr> LocalAddresses()
    {
        var set = new HashSet<Addr>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var u in nic.GetIPProperties().UnicastAddresses)
                    set.Add(FromIp(u.Address));
        }
        catch { }
        return set;
    }

    // ---------- ETW ----------

    private const int PropsSize = 120, NameSpace = 1024 * 2, LogFileSize = 448;
    private const uint WnodeFlagTracedGuid = 0x00020000, RealTimeMode = 0x00000100, NoPerProcessorBuffering = 0x10000000;
    private const int ModeRealTime = 0x00000100, ModeEventRecord = 0x10000000;
    private const uint ControlStop = 1, EnableProvider = 1, ErrAlreadyExists = 183;
    private const byte LevelInformation = 4;
    private const ulong InvalidHandle = ulong.MaxValue, InvalidHandle32 = 0x00000000FFFFFFFF;
    private ulong sessionHandle;

    /// <summary>EVENT_TRACE_PROPERTIES (120 байт на x64) и место под имя сеанса сразу за ней.</summary>
    private static IntPtr AllocProps(uint mode)
    {
        int size = PropsSize + NameSpace;
        IntPtr p = Marshal.AllocHGlobal(size);
        Zero(p, size);
        Marshal.WriteInt32(p, 0, size);                                   // Wnode.BufferSize
        Marshal.Copy(Guid.NewGuid().ToByteArray(), 0, p + 24, 16);        // Wnode.Guid
        Marshal.WriteInt32(p, 40, 1);                                     // Wnode.ClientContext: QPC
        Marshal.WriteInt32(p, 44, unchecked((int)WnodeFlagTracedGuid));   // Wnode.Flags
        Marshal.WriteInt32(p, 48, 64);                                    // BufferSize, КБ
        Marshal.WriteInt32(p, 52, 8);                                     // MinimumBuffers
        Marshal.WriteInt32(p, 56, 64);                                    // MaximumBuffers
        Marshal.WriteInt32(p, 64, unchecked((int)mode));                  // LogFileMode
        Marshal.WriteInt32(p, 68, 1);                                     // FlushTimer, с
        Marshal.WriteInt32(p, 112, 0);                                    // LogFileNameOffset: файла нет
        Marshal.WriteInt32(p, 116, PropsSize);                            // LoggerNameOffset
        return p;
    }

    // Общий буфер для всех ядер: события приходят в порядке времени, иначе паузы могли бы мерещиться.
    private uint StartSession(bool commonBuffer)
    {
        IntPtr props = AllocProps(RealTimeMode | (commonBuffer ? NoPerProcessorBuffering : 0));
        try { return StartTraceW(out sessionHandle, SessionName, props); }
        finally { Marshal.FreeHGlobal(props); }
    }

    private void StopSession()
    {
        IntPtr props = AllocProps(RealTimeMode);
        try { ControlTraceW(0, SessionName, props, ControlStop); }
        catch { }
        finally { Marshal.FreeHGlobal(props); }
    }

    private void FreeNative()
    {
        if (logFile != IntPtr.Zero) { Marshal.FreeHGlobal(logFile); logFile = IntPtr.Zero; }
        if (loggerName != IntPtr.Zero) { Marshal.FreeHGlobal(loggerName); loggerName = IntPtr.Zero; }
    }

    private static void Zero(IntPtr p, int size)
    {
        for (int i = 0; i < size; i += 8) Marshal.WriteInt64(p, i, 0);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EventRecordCallback(IntPtr eventRecord);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ControlTraceW(ulong sessionHandle, string sessionName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll")]
    private static extern uint EnableTraceEx2(ulong traceHandle, ref Guid providerId, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern ulong OpenTraceW(IntPtr logfile);

    [DllImport("advapi32.dll")]
    private static extern uint ProcessTrace(ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll")]
    private static extern uint CloseTrace(ulong traceHandle);

    // ---------- Самопроверка ----------

    /// <summary>
    /// Разбор событий на поддельных данных, поиск пауз на искусственном потоке, затем живая проверка:
    /// DNS-запросы этого процесса к 1.1.1.1 и 8.8.8.8 (к первому втрое больше) - сервером должен оказаться 1.1.1.1.
    /// </summary>
    public static string SelfTest()
    {
        var log = new StringBuilder();
        log.AppendLine("--- Сеть игры (ETW Kernel-Network), на общий итог не влияет ---");
        void Check(string name, bool ok) => log.AppendLine((ok ? "OK    " : "FAIL  ") + name);
        try
        {
            OfflineChecks(Check);
            LiveCheck(log);
        }
        catch (Exception ex) { log.AppendLine("EXCEPTION  " + ex.Message); }
        return log.ToString();
    }

    private static void OfflineChecks(Action<string, bool> check)
    {
        // Входящий UDPv4: от 185.25.182.77:27015 на 192.168.1.5:50000, PID 1234.
        var buf = new byte[28];
        BinaryPrimitives.WriteInt32LittleEndian(buf, 1234);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), 100);
        new byte[] { 192, 168, 1, 5 }.CopyTo(buf, 8);
        new byte[] { 185, 25, 182, 77 }.CopyTo(buf, 12);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(16), 50000);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(18), 27015);
        bool ok = WithNative(buf, (d, l) => Parse(UdpRecv4, d, l, out var p) ? p : (Raw?)null) is Raw r4
                  && r4.Pid == 1234 && r4.Inbound && Format(r4.S) == "185.25.182.77" && Format(r4.D) == "192.168.1.5"
                  && r4.SPort == 27015 && r4.DPort == 50000;
        check("разбор UDPv4 (событие 43): PID, адреса, порты в сетевом порядке", ok);

        var local = new HashSet<Addr> { V4(0xC0A80105) };   // 192.168.1.5
        var raw = WithNative(buf, (d, l) => { Parse(UdpRecv4, d, l, out var p); return p; });
        ChooseRemote(raw, local, out var remote, out int port);
        check("удалённый адрес входящего: 185.25.182.77:27015", Format(remote) == "185.25.182.77" && port == 27015);
        var swapped = raw; swapped.D = raw.S; swapped.S = raw.D; swapped.DPort = raw.SPort; swapped.SPort = raw.DPort;
        ChooseRemote(swapped, local, out remote, out port);
        check("удалённый адрес при обратном порядке полей определяется по своему адресу", Format(remote) == "185.25.182.77" && port == 27015);
        ChooseRemote(raw, null, out remote, out _);
        check("без списка своих адресов входящий берётся по saddr", Format(remote) == "185.25.182.77");
        check("короткое событие и чужой номер отклоняются",
            !WithNative(buf, (d, l) => Parse(UdpRecv4, d, 19, out _)) && !WithNative(buf, (d, l) => Parse(49, d, l, out _)));

        // Исходящий UDPv6: на 2a00:1450::1 порт 443.
        var buf6 = new byte[52];
        BinaryPrimitives.WriteInt32LittleEndian(buf6, 77);
        IPAddress.Parse("2a00:1450::1").GetAddressBytes().CopyTo(buf6, 8);
        IPAddress.Parse("fe80::1").GetAddressBytes().CopyTo(buf6, 24);
        BinaryPrimitives.WriteUInt16BigEndian(buf6.AsSpan(40), 443);
        BinaryPrimitives.WriteUInt16BigEndian(buf6.AsSpan(42), 60000);
        var r6 = WithNative(buf6, (d, l) => { Parse(UdpSend6, d, l, out var p); return p; });
        ChooseRemote(r6, null, out remote, out port);
        check("разбор UDPv6 (событие 58): 2a00:1450::1:443", r6.Pid == 77 && !r6.Inbound && Format(remote) == "2a00:1450::1" && port == 443 && IsPublic(remote));

        bool filters = !IsPublic(V4(0x0A000001)) && !IsPublic(V4(0x1A010203)) && !IsPublic(V4(0xAC140001)) && IsPublic(V4(0xAC200001))
                       && !IsPublic(V4(0x64400101)) && IsPublic(V4(0x64800001)) && !IsPublic(V4(0xE0000001)) && !IsPublic(V4(0xFFFFFFFF))
                       && !IsPublic(V4(0x7F000001)) && IsPublic(V4(0x08080808)) && !IsPublic(r6.S) && !IsPublic(FromIp(IPAddress.IPv6Loopback));
        check("фильтр адресов: локальные сети, VPN 26.x, 100.64/10, групповые отброшены", filters);

        // Поток: сервер шлёт 50 пакетов/с, игра отвечает; после 1,98 с входящий поток молчит до 2,4 с (420 мс).
        var mon = new GameNetMonitor { pid = 1234, localAddrs = local };
        var srv = raw;
        var outPkt = raw; outPkt.Inbound = false; outPkt.D = raw.S; outPkt.S = raw.D; outPkt.DPort = raw.SPort; outPkt.SPort = raw.DPort;
        long t0 = DateTime.UtcNow.ToFileTimeUtc() - 10 * Second, ms = TimeSpan.TicksPerMillisecond;
        for (long t = 0; t < 5000; t += 20)
        {
            mon.Feed(outPkt, t0 + t * ms);
            if (t < 2000 || t >= 2400) mon.Feed(srv, t0 + t * ms);
        }
        var st = mon.Stop();
        check($"сервер найден по потоку (получено {st.Server}:{st.ServerPort})", st.Server == "185.25.182.77" && st.ServerPort == 27015);
        check($"пауза 420 мс найдена (пауз {st.Gaps}, самая длинная {st.MaxGapSec:0.00} с)", st.Gaps == 1 && Math.Abs(st.MaxGapSec - 0.42) < 0.01 && st.GapTimes.Count == 1);
        check($"пакетов в секунду около 50 (вход {st.InPps:0.0}, выход {st.OutPps:0.0})", Math.Abs(st.OutPps - 50) < 2 && Math.Abs(st.InPps - 46) < 3);
    }

    private static T WithNative<T>(byte[] bytes, Func<IntPtr, int, T> f)
    {
        IntPtr p = Marshal.AllocHGlobal(bytes.Length);
        try { Marshal.Copy(bytes, 0, p, bytes.Length); return f(p, bytes.Length); }
        finally { Marshal.FreeHGlobal(p); }
    }

    private static void LiveCheck(StringBuilder log)
    {
        bool admin;
        try { admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { admin = false; }

        using var mon = new GameNetMonitor();
        if (!mon.Start(Environment.ProcessId))
        {
            log.AppendLine(!admin || mon.LastError == 5
                ? $"ETW недоступен: нужны права администратора (код {mon.LastError}). Живая проверка пропущена."
                : $"FAIL  ETW не запустился, код ошибки {mon.LastError}");
            return;
        }
        log.AppendLine("OK    сеанс ETW запущен");

        // DNS-запрос «example.com, A».
        byte[] query = { 0x12, 0x34, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0,
                         7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', 3, (byte)'c', (byte)'o', (byte)'m', 0, 0, 1, 0, 1 };
        using (var udp = new UdpClient(AddressFamily.InterNetwork))
        {
            var one = new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53);
            var eight = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53);
            for (int i = 0; i < 40; i++)
            {
                try { udp.Send(query, query.Length, i % 4 == 3 ? eight : one); } catch { }
                Thread.Sleep(100);
                try { while (udp.Available > 0) { var from = new IPEndPoint(IPAddress.Any, 0); udp.Receive(ref from); } } catch { }
            }
        }
        Thread.Sleep(1500);   // буферы ETW сбрасываются раз в секунду
        var st = mon.Stop();

        lock (mon.gate)
        {
            if (mon.remotes.Count == 0) log.AppendLine("FAIL  пакетов этого процесса не увидено");
            foreach (var r in mon.remotes.Values.OrderByDescending(x => x.In + x.Out))
                log.AppendLine($"      {Format(r.Key)}:{r.Port} - отправлено {r.Out}, получено {r.In}");
        }
        log.AppendLine((st.Server == "1.1.1.1" ? "OK    " : "FAIL  ") + $"сервер: {st.Server ?? "не найден"} (ожидался 1.1.1.1)");
        log.AppendLine(st.AvgPingMs is double avg
            ? $"      пинг {avg:0} мс, разброс {(st.JitterMs is double j ? j.ToString("0.0") : "-")} мс, потери {st.LossPct:0} %"
            : st.PingBlocked ? "      сервер не отвечает на пинг" : "      замеров пинга нет");
        log.AppendLine($"      пауз {st.Gaps}, пакетов/с: вход {st.InPps:0.0}, выход {st.OutPps:0.0}");
    }
}
