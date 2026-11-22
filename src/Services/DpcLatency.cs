using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PulsePC.Services;

/// <summary>Один драйвер в итоге проверки задержек.</summary>
public sealed class DpcDriver
{
    public string Name { get; set; } = "";
    /// <summary>Самая долгая отложенная процедура (DPC) или обработка прерывания (ISR), мкс.</summary>
    public double MaxUs { get; set; }
    /// <summary>Сколько всего времени процессора драйвер занял в DPC и ISR за проверку, мс.</summary>
    public double TotalMs { get; set; }
    public long Count { get; set; }
    /// <summary>Самая долгая задержка пришлась на обработку прерывания (ISR), а не на DPC.</summary>
    public bool MaxIsIsr { get; set; }
}

/// <summary>Итог проверки задержек драйверов. Сохраняется в настройках (stab.dpc.last) и показывается в «Диагностике».</summary>
public sealed class DpcResult
{
    public DateTime At { get; set; }
    public double Seconds { get; set; }
    public long Events { get; set; }
    public long Lost { get; set; }
    public List<DpcDriver> Drivers { get; set; } = new();
    /// <summary>Почему не получилось (нет прав, трассировка занята…). null — получилось.</summary>
    public string Error { get; set; }
    public double WorstUs => Drivers.Count == 0 ? 0 : Drivers.Max(d => d.MaxUs);
}

/// <summary>
/// «Задержки драйверов» (как LatencyMon): 30 секунд трассировки ядра Windows (ETW, события DPC и ISR), затем для каждого драйвера —
/// самая долгая и суммарная задержка. Адрес процедуры сопоставляется с драйвером по списку модулей ядра (события загрузки образов
/// из той же трассировки, запасной путь — EnumDeviceDrivers). Нужны права администратора. Ничего в системе не меняется: сеанс
/// трассировки свой (имя PulsePC-…-Dpc), после проверки он останавливается.
/// </summary>
public static class DpcLatency
{
    public const int DefaultSeconds = 30;
    public const string SettingsKey = "stab.dpc.last";
    /// <summary>Пороги: до 500 мкс — норма, 500–2000 — заметно, от 2000 — может давать щелчки звука и рывки.</summary>
    public const double WarnUs = 500, BadUs = 2000;

    // ---------- агрегирование (отдельно от ETW — проверяется на подставных событиях) ----------
    public sealed class Aggregator
    {
        private readonly List<(ulong Base, ulong End, string Name)> modules = new();
        private bool sorted;
        private readonly Dictionary<string, DpcDriver> byName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ulong, string> cache = new();
        public long Events;

        public void AddModule(ulong baseAddr, ulong size, string name)
        {
            if (baseAddr == 0 || size == 0 || string.IsNullOrEmpty(name)) return;
            name = Path.GetFileName(name);
            if (modules.Any(m => m.Base == baseAddr)) return;
            modules.Add((baseAddr, baseAddr + size, name));
            sorted = false;
            cache.Clear();
        }

        public int ModuleCount => modules.Count;

        public string Resolve(ulong addr)
        {
            if (cache.TryGetValue(addr, out var n)) return n;
            if (!sorted) { modules.Sort((a, b) => a.Base.CompareTo(b.Base)); sorted = true; }
            string found = null;
            int lo = 0, hi = modules.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (addr < modules[mid].Base) hi = mid - 1;
                else if (addr >= modules[mid].End) lo = mid + 1;
                else { found = modules[mid].Name; break; }
            }
            found ??= L.T("неизвестный драйвер");
            if (cache.Count < 4096) cache[addr] = found;
            return found;
        }

        /// <summary>Одна процедура: длительность в микросекундах и адрес.</summary>
        public void Add(double us, ulong routine, bool isr)
        {
            if (!(us >= 0) || us > 1_000_000) return;   // мусор и перевёрнутые часы — мимо
            Events++;
            string name = Resolve(routine);
            if (!byName.TryGetValue(name, out var d)) byName[name] = d = new DpcDriver { Name = name };
            d.Count++;
            d.TotalMs += us / 1000;
            if (us > d.MaxUs) { d.MaxUs = us; d.MaxIsIsr = isr; }
        }

        public List<DpcDriver> Top(int n = 8) =>
            byName.Values.OrderByDescending(d => d.MaxUs).ThenByDescending(d => d.TotalMs).Take(n)
                  .Select(d => new DpcDriver { Name = d.Name, MaxUs = Math.Round(d.MaxUs, 1), TotalMs = Math.Round(d.TotalMs, 2), Count = d.Count, MaxIsIsr = d.MaxIsIsr }).ToList();
    }

    // ---------- разбор записи ETW (EVENT_RECORD, x64) ----------
    private static readonly Guid PerfInfo = new("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc");
    private static readonly Guid ImageLoad = new("2cb15d1d-5fc1-11d2-abe1-00a0c911f518");
    private const byte OpThreadDpc = 66, OpIsr = 67, OpDpc = 68, OpTimerDpc = 69;
    private const byte OpImageLoad = 10, OpImageDcStart = 3, OpImageDcEnd = 4;

    /// <summary>
    /// Разобрать одну запись: DPC/ISR (InitialTime, Routine) — длительность = время записи − InitialTime; загрузка образа ядра
    /// (ImageBase, ImageSize, …, FileName) — в список модулей. freq — частота часов трассировки (QPC).
    /// </summary>
    public static void Feed(IntPtr rec, Aggregator agg, double freq)
    {
        var provider = new Guid(ReadBytes(rec, 24, 16));
        byte opcode = Marshal.ReadByte(rec, 45);
        int len = (ushort)Marshal.ReadInt16(rec, 86);
        IntPtr data = Marshal.ReadIntPtr(rec, 96);
        if (data == IntPtr.Zero) return;
        if (provider == PerfInfo)
        {
            if (opcode is not (OpThreadDpc or OpIsr or OpDpc or OpTimerDpc) || len < 16) return;
            long stamp = Marshal.ReadInt64(rec, 16);
            long initial = Marshal.ReadInt64(data, 0);
            ulong routine = (ulong)Marshal.ReadInt64(data, 8);
            double us = (stamp - initial) * 1e6 / freq;
            agg.Add(us, routine, opcode == OpIsr);
        }
        else if (provider == ImageLoad)
        {
            if (opcode is not (OpImageLoad or OpImageDcStart or OpImageDcEnd) || len < 58) return;
            ulong imageBase = (ulong)Marshal.ReadInt64(data, 0);
            ulong size = (ulong)Marshal.ReadInt64(data, 8);
            if (imageBase < 0xFFFF800000000000UL) return;    // только модули ядра
            int chars = (len - 56) / 2;
            if (chars <= 0) return;
            string file = Marshal.PtrToStringUni(data + 56, chars).TrimEnd('\0');
            int z = file.IndexOf('\0');
            if (z >= 0) file = file[..z];
            agg.AddModule(imageBase, size, file);
        }
    }

    private static byte[] ReadBytes(IntPtr p, int off, int n)
    {
        var b = new byte[n];
        Marshal.Copy(p + off, b, 0, n);
        return b;
    }

    // ---------- запуск проверки ----------
    /// <summary>
    /// Проверка задержек: seconds секунд трассировки ядра. Вызывать в фоне; progress — сколько секунд прошло.
    /// Без прав администратора сразу возвращает ошибку «нужны права».
    /// </summary>
    public static DpcResult Run(int seconds, CancellationToken cancel, Action<int> progress = null)
    {
        var result = new DpcResult { At = DateTime.Now };
        if (!Shell.IsAdmin()) { result.Error = L.T("Нужны права администратора: трассировку ядра Windows разрешает только администратор."); return result; }
        // Осторожный режим (игра с античитом уровня ядра): трассировка ядра не включается совсем.
        if (CautiousMode.Current is { AllowDpc: false }) { result.Error = L.T("Идёт игра со строгим античитом (осторожный режим) — проверка задержек драйверов не запускается."); return result; }
        if (GameHooks.InGame) { result.Error = L.T("Идёт игра — проверку лучше сделать, когда игра закрыта."); return result; }

        var agg = new Aggregator();
        try { foreach (var (b, size, file) in KernelModules()) agg.AddModule(b, size, file); } catch { }

        string name = EtwSessions.NewName("Dpc");
        uint err = StartSession(name, systemLogger: true);
        bool ntKernel = false;
        if (err == 87 || err == 50)   // старые сборки Windows без «системного» сеанса — классический NT Kernel Logger
        {
            name = "NT Kernel Logger";
            err = StartSession(name, systemLogger: false);
            ntKernel = true;
            if (err == 183) { result.Error = L.T("Трассировку ядра сейчас занимает другая программа (например, LatencyMon или Process Monitor). Закройте её и повторите."); return result; }
        }
        if (err == 5) { result.Error = L.T("Нужны права администратора: трассировку ядра Windows разрешает только администратор."); return result; }
        if (err != 0) { result.Error = L.F("Трассировка ядра не запустилась (код {0}).", err); return result; }

        // Программу закрыли или она упала посреди проверки — сеанс трассировки ядра не должен остаться работать
        // (выход — ProcessExit, падение — StopActive из обработчика ошибок App).
        activeNt = ntKernel;
        Volatile.Write(ref activeName, name);
        EventHandler onExit = (_, _) => StopActive();
        AppDomain.CurrentDomain.ProcessExit += onExit;
        IntPtr logFile = IntPtr.Zero, loggerName = IntPtr.Zero;
        ulong handle = InvalidHandle;
        Thread thread = null;
        double freq = Stopwatch.Frequency;
        EventRecordCallback callback = rec => { try { Feed(rec, agg, freq); } catch { } };
        var watch = Stopwatch.StartNew();
        try
        {
            loggerName = Marshal.StringToHGlobalUni(name);
            logFile = Marshal.AllocHGlobal(LogFileSize);
            Zero(logFile, LogFileSize);
            Marshal.WriteIntPtr(logFile, 8, loggerName);                                              // LoggerName
            Marshal.WriteInt32(logFile, 28, ModeRealTime | ModeEventRecord | ModeRawTimestamp);       // ProcessTraceMode
            Marshal.WriteIntPtr(logFile, 424, Marshal.GetFunctionPointerForDelegate(callback));       // EventRecordCallback
            handle = OpenTraceW(logFile);
            if (handle == InvalidHandle || handle == InvalidHandle32)
            {
                result.Error = L.F("Не удалось прочитать трассировку ядра (код {0}).", Marshal.GetLastPInvokeError());
                handle = InvalidHandle;
                return result;
            }
            ulong h = handle;
            thread = new Thread(() => { try { ProcessTrace(new[] { h }, 1, IntPtr.Zero, IntPtr.Zero); } catch { } })
                { IsBackground = true, Name = "PulsePC DPC ETW" };
            thread.Start();

            for (int s = 0; s < seconds && !cancel.IsCancellationRequested; s++)
            {
                Thread.Sleep(1000);
                progress?.Invoke(s + 1);
                // Запись игры (и решение осторожного режима) начинается на такт раньше, чем GameHooks узнают об игре.
                if (GameHooks.InGame || CautiousMode.Current != null) { result.Error = L.T("Началась игра — проверка остановлена."); break; }
            }
        }
        finally
        {
            AppDomain.CurrentDomain.ProcessExit -= onExit;
            Interlocked.Exchange(ref activeName, null);
            result.Lost = StopSession(name, ntKernel);
            if (thread != null && !thread.Join(3000)) thread.Join(1000);
            if (handle != InvalidHandle) { try { CloseTrace(handle); } catch { } }
            if (thread == null || !thread.IsAlive)
            {
                if (logFile != IntPtr.Zero) Marshal.FreeHGlobal(logFile);
                if (loggerName != IntPtr.Zero) Marshal.FreeHGlobal(loggerName);
            }
            else lock (stuck) stuck.Add(callback);   // ProcessTrace ещё не вернулся: обратный вызов не должен быть собран сборщиком мусора
            GC.KeepAlive(callback);
        }
        result.Seconds = Math.Round(watch.Elapsed.TotalSeconds, 1);
        result.Events = agg.Events;
        result.Drivers = agg.Top();
        if (result.Error == null && agg.Events == 0)
            result.Error = L.T("Windows не прислала ни одного события DPC — такое бывает, если трассировку ядра ограничивает другая программа.");
        if (cancel.IsCancellationRequested && result.Error == null) result.Error = L.T("Проверка прервана.");
        return result;
    }

    private static string activeName;
    private static bool activeNt;
    private static readonly List<EventRecordCallback> stuck = new();

    /// <summary>Остановить идущий сеанс проверки, если он есть (выход или падение программы посреди проверки).</summary>
    public static void StopActive()
    {
        string n = Interlocked.Exchange(ref activeName, null);
        if (n == null) return;
        try { StopSession(n, activeNt); } catch { }
    }

    /// <summary>Модули ядра через EnumDeviceDrivers: размер — до начала следующего модуля (точнее Windows не даёт).</summary>
    private static List<(ulong Base, ulong Size, string Name)> KernelModules()
    {
        var res = new List<(ulong, ulong, string)>();
        EnablePrivilege("SeDebugPrivilege");   // в новых сборках Windows без неё адреса драйверов приходят нулями
        var arr = new IntPtr[2048];
        if (!EnumDeviceDrivers(arr, (uint)(arr.Length * IntPtr.Size), out uint needed)) return res;
        int n = (int)Math.Min(needed / (uint)IntPtr.Size, (uint)arr.Length);
        var list = new List<(ulong Base, string Name)>();
        var sb = new StringBuilder(260);
        for (int i = 0; i < n; i++)
        {
            ulong b = (ulong)arr[i].ToInt64();
            if (b < 0xFFFF800000000000UL) continue;
            sb.Clear();
            if (GetDeviceDriverBaseNameW(arr[i], sb, 260) > 0) list.Add((b, sb.ToString()));
        }
        list.Sort((a, b) => a.Base.CompareTo(b.Base));
        for (int i = 0; i < list.Count; i++)
        {
            ulong size = i + 1 < list.Count ? Math.Min(list[i + 1].Base - list[i].Base, 0x4000000UL) : 0x400000UL;
            res.Add((list[i].Base, size, list[i].Name));
        }
        return res;
    }

    // ---------- сохранение и вывод ----------
    public static void Save(AppSettings s, DpcResult r)
    {
        if (s == null || r == null || r.Error != null) return;
        try { s.Set(SettingsKey, JsonSerializer.Serialize(r)); s.Save(); } catch (Exception ex) { Log.Error("задержки драйверов: сохранение", ex); }
    }

    public static DpcResult Last(AppSettings s)
    {
        try { string j = s?.Get(SettingsKey) ?? ""; return j.Length == 0 ? null : JsonSerializer.Deserialize<DpcResult>(j); }
        catch { return null; }
    }

    public static string Us(double us) => us >= 1000
        ? (us / 1000).ToString("0.0#", L.Culture) + " " + L.Pick("мс", "ms")
        : us.ToString("0", L.Culture) + " " + L.Pick("мкс", "µs");

    /// <summary>Вывод одной фразой и советом — для раздела и для «Диагностики».</summary>
    public static DiagFinding Finding(DpcResult r)
    {
        if (r == null || r.Error != null || r.Drivers.Count == 0) return null;
        var worst = r.Drivers[0];
        var info = DriverNames.Describe(worst.Name);
        string when = L.F("Проверка {0:d MMMM, HH:mm}, {1:0} с.", r.At, r.Seconds);
        if (worst.MaxUs < WarnUs)
            return new DiagFinding { Level = Severity.Ok, Title = L.T("Задержки драйверов в норме"), Detail = L.F("худшая — {0}, {1:d MMMM}", Us(worst.MaxUs), r.At) };
        var f = new DiagFinding
        {
            Level = worst.MaxUs >= BadUs ? Severity.Warn : Severity.Info,
            Title = L.F("Задержки драйверов: {0}", worst.Name),
            Count = Us(worst.MaxUs),
            Detail = when + " " + L.F("Дольше всех занимал процессор {0} — {1}: до {2} за раз.", worst.Name, info.Role, Us(worst.MaxUs)) + " " +
                     (worst.MaxUs >= BadUs ? L.T("Такие задержки дают щелчки и заикание звука, рывки при нормальном FPS и задержку ввода.")
                                           : L.T("Это заметно, но обычно ещё не слышно и не видно в играх.")) + " " + info.Advice,
        };
        if (info.Link.Length > 0) { f.Link = info.Link; f.LinkText = info.LinkText; }
        return f;
    }

    // ---------- ETW: сеанс трассировки ядра ----------
    private const int PropsSize = 120, NameSpace = 1024 * 2, LogFileSize = 448;
    private const uint WnodeFlagTracedGuid = 0x00020000, RealTimeMode = 0x00000100, SystemLoggerMode = 0x02000000, ControlStop = 1;
    private const uint FlagImageLoad = 0x00000004, FlagDpc = 0x00000020, FlagInterrupt = 0x00000040;
    private const int ModeRealTime = 0x00000100, ModeEventRecord = 0x10000000, ModeRawTimestamp = 0x00001000;
    private const ulong InvalidHandle = ulong.MaxValue, InvalidHandle32 = 0x00000000FFFFFFFF;
    private static readonly Guid SystemTraceControl = new("9e814aad-3204-11d2-9a82-006008a86939");

    private static IntPtr AllocProps(bool systemLogger, bool ntKernel)
    {
        int size = PropsSize + NameSpace;
        IntPtr p = Marshal.AllocHGlobal(size);
        Zero(p, size);
        Marshal.WriteInt32(p, 0, size);                                                                   // Wnode.BufferSize
        Marshal.Copy((ntKernel ? SystemTraceControl : Guid.NewGuid()).ToByteArray(), 0, p + 24, 16);       // Wnode.Guid
        Marshal.WriteInt32(p, 40, 1);                                                                     // Wnode.ClientContext: QPC
        Marshal.WriteInt32(p, 44, unchecked((int)WnodeFlagTracedGuid));                                   // Wnode.Flags
        Marshal.WriteInt32(p, 48, 256);                                                                   // BufferSize, КБ
        Marshal.WriteInt32(p, 52, 16);                                                                    // MinimumBuffers
        Marshal.WriteInt32(p, 56, 128);                                                                   // MaximumBuffers
        Marshal.WriteInt32(p, 64, unchecked((int)(RealTimeMode | (systemLogger ? SystemLoggerMode : 0))));// LogFileMode
        Marshal.WriteInt32(p, 68, 1);                                                                     // FlushTimer, с
        Marshal.WriteInt32(p, 72, unchecked((int)(FlagDpc | FlagInterrupt | FlagImageLoad)));             // EnableFlags
        Marshal.WriteInt32(p, 112, 0);                                                                    // LogFileNameOffset
        Marshal.WriteInt32(p, 116, PropsSize);                                                            // LoggerNameOffset
        return p;
    }

    private static uint StartSession(string name, bool systemLogger)
    {
        IntPtr props = AllocProps(systemLogger, !systemLogger);
        try { return StartTraceW(out _, name, props); }
        catch { return uint.MaxValue; }
        finally { Marshal.FreeHGlobal(props); }
    }

    /// <summary>Остановить сеанс; вернуть число потерянных событий.</summary>
    private static long StopSession(string name, bool ntKernel)
    {
        IntPtr props = AllocProps(!ntKernel, ntKernel);
        try { ControlTraceW(0, name, props, ControlStop); return (uint)Marshal.ReadInt32(props, 88); }
        catch { return 0; }
        finally { Marshal.FreeHGlobal(props); }
    }

    private static void Zero(IntPtr p, int size) { for (int i = 0; i < size; i += 8) Marshal.WriteInt64(p, i, 0); }

    private static void EnablePrivilege(string priv)
    {
        try
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0020 | 0x0008, out IntPtr token)) return;
            try
            {
                if (!LookupPrivilegeValue(null, priv, out long luid)) return;
                var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
                AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally { CloseHandle(token); }
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges { public uint Count; public long Luid; public uint Attributes; }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EventRecordCallback(IntPtr eventRecord);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern uint StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern uint ControlTraceW(ulong sessionHandle, string sessionName, IntPtr properties, uint controlCode);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern ulong OpenTraceW(IntPtr logfile);
    [DllImport("advapi32.dll")] private static extern uint ProcessTrace(ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);
    [DllImport("advapi32.dll")] private static extern uint CloseTrace(ulong traceHandle);
    [DllImport("psapi.dll", SetLastError = true)] private static extern bool EnumDeviceDrivers([Out] IntPtr[] imageBases, uint cb, out uint needed);
    [DllImport("psapi.dll", CharSet = CharSet.Unicode)] private static extern uint GetDeviceDriverBaseNameW(IntPtr imageBase, StringBuilder name, uint size);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LookupPrivilegeValue(string system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, uint len, IntPtr prev, IntPtr retLen);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    // ---------- самопроверка: подставные записи EVENT_RECORD ----------
    /// <summary>Собрать в неуправляемой памяти запись EVENT_RECORD с данными (для самопроверки разбора). Освобождать — FreeFake.</summary>
    public static IntPtr FakeRecord(Guid provider, byte opcode, long stamp, byte[] payload)
    {
        IntPtr rec = Marshal.AllocHGlobal(112);
        Zero(rec, 112);
        Marshal.WriteInt64(rec, 16, stamp);
        Marshal.Copy(provider.ToByteArray(), 0, rec + 24, 16);
        Marshal.WriteByte(rec, 45, opcode);
        Marshal.WriteInt16(rec, 86, (short)payload.Length);
        IntPtr data = Marshal.AllocHGlobal(Math.Max(8, payload.Length));
        Marshal.Copy(payload, 0, data, payload.Length);
        Marshal.WriteIntPtr(rec, 96, data);
        return rec;
    }

    public static void FreeFake(IntPtr rec)
    {
        IntPtr data = Marshal.ReadIntPtr(rec, 96);
        if (data != IntPtr.Zero) Marshal.FreeHGlobal(data);
        Marshal.FreeHGlobal(rec);
    }

    public static byte[] DpcPayload(long initial, ulong routine)
    {
        var b = new byte[24];
        BitConverter.GetBytes(initial).CopyTo(b, 0);
        BitConverter.GetBytes(routine).CopyTo(b, 8);
        return b;
    }

    public static byte[] ImagePayload(ulong imageBase, ulong size, string file)
    {
        var name = Encoding.Unicode.GetBytes(file + "\0");
        var b = new byte[56 + name.Length];
        BitConverter.GetBytes(imageBase).CopyTo(b, 0);
        BitConverter.GetBytes(size).CopyTo(b, 8);
        name.CopyTo(b, 56);
        return b;
    }

    public static Guid PerfInfoGuid => PerfInfo;
    public static Guid ImageLoadGuid => ImageLoad;
}
