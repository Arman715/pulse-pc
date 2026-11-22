using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PulsePC.Services;

// ======================================================================== данные отчёта (хранятся в sessions.json)

/// <summary>
/// Сжатый ряд времени кадров для графика: одинаковые отрезки по часам от начала до конца сессии (отрезок без кадров — NaN:
/// игра свёрнута или грузилась). Плюс самые заметные рывки поштучно.
/// </summary>
public class FrameTimeline
{
    /// <summary>Длина отрезка, секунд.</summary>
    public double BucketSec { get; set; }
    /// <summary>Обычное время кадра (медиана) на отрезке, мс.</summary>
    public double[] Median { get; set; } = Array.Empty<double>();
    /// <summary>Худший 1 % кадров на отрезке, мс.</summary>
    public double[] Worst { get; set; } = Array.Empty<double>();
    /// <summary>Самые заметные рывки: секунд от начала сессии и время кадра, мс.</summary>
    public List<FrameJolt> Jolts { get; set; } = new();
    /// <summary>Обычное время кадра и худший 1 % за всю сессию, мс.</summary>
    public double MedianMs { get; set; }
    public double P99Ms { get; set; }
    /// <summary>Сколько минут шли кадры (без пауз).</summary>
    public double PlayMinutes { get; set; }
}

public class FrameJolt
{
    public double T { get; set; }
    public double Ms { get; set; }
}

/// <summary>
/// Вид рывков: shader (сборка шейдеров), disk (подгрузка с диска), cpu (скачки процессора), memory (нехватка памяти),
/// vram (нехватка видеопамяти), other (датчики ничего не показали). Value — число для пояснения (см. SessionAnalysis.StutterText).
/// </summary>
public class StutterGroup
{
    public string Kind { get; set; } = "other";
    public int Count { get; set; }
    public double Value { get; set; }
}

/// <summary>Процесс, который мешал игре: имя файла без «.exe», процессор (% от всего процессора) и диск (МБ/с).</summary>
public class Culprit
{
    public string Name { get; set; } = "";
    public double Cpu { get; set; }
    public double DiskMBs { get; set; }
    /// <summary>Для фоновых программ за всю сессию — пик (средние — в Cpu и DiskMBs).</summary>
    public double PeakCpu { get; set; }
    public double PeakDiskMBs { get; set; }
}

/// <summary>Отметка на графиках сессии: minimized, pause, driver, ping, culprit.</summary>
public class SessionEvent
{
    public string Kind { get; set; } = "";
    public DateTime At { get; set; }
    public double Seconds { get; set; }
    /// <summary>Пинг, мс (ping).</summary>
    public double Value { get; set; }
    /// <summary>Процесс (culprit).</summary>
    public string Name { get; set; }
    public double Cpu { get; set; }
    public double DiskMBs { get; set; }
}

/// <summary>Процесс в одном замере: имя, процессор (% от всего процессора), диск (МБ/с).</summary>
public struct ProcShot
{
    public string Name;
    public float Cpu, Io;
}

// ======================================================================== лёгкий опрос процессов

/// <summary>
/// Кто ещё нагружает процессор и диск во время игры. Один системный вызов NtQuerySystemInformation(SystemProcessInformation) отдаёт
/// время процессора и объём чтения/записи всех процессов сразу — тот же источник, что у Диспетчера задач. Процессы не открываются,
/// дескрипторы не создаются, имена читаются только для новых процессов. Работа замеряется (CostMs) — для строки о нагрузке программы.
/// Объём «чтения/записи» Windows считает по всем файловым операциям процесса — в основном это диск.
/// </summary>
public sealed class ProcessActivity : IDisposable
{
    private IntPtr buffer;
    private int size = 1 << 20;
    // Счётчики прошлого замера и текущего: два словаря по очереди, без новых выделений памяти на каждом замере.
    private Dictionary<(int Pid, long Created), (long Cpu, long Io, string Name)> prev = new(), seen = new();
    private readonly Dictionary<string, (double Cpu, double Io)> byName = new(StringComparer.OrdinalIgnoreCase);
    private long prevTs;
    private readonly long cores = Math.Max(1, Environment.ProcessorCount);
    private readonly int selfPid = Environment.ProcessId;
    private readonly Stopwatch cost = new();

    /// <summary>Сколько времени ушло на опросы и сколько их было; отдельно — первый опрос (он читает имена всех процессов).</summary>
    public double CostMs => cost.Elapsed.TotalMilliseconds;
    public int Calls { get; private set; }
    public double FirstMs { get; private set; }
    /// <summary>Процессор, занятый самой программой и PresentMon в последнем замере, % от всего процессора.</summary>
    public double SelfCpu { get; private set; } = double.NaN;

    private const int SystemProcessInformation = 5;
    private const uint StatusInfoLengthMismatch = 0xC0000004;

    /// <summary>
    /// Замер с прошлого вызова: самые заметные процессы (без игры и самой программы), чтение/запись игры (МБ/с).
    /// Первый вызов только запоминает счётчики и возвращает пустой список.
    /// </summary>
    public ProcShot[] Sample(int gamePid, string gameImage, out double gameIoMBs)
    {
        gameIoMBs = double.NaN;
        if (IntPtr.Size != 8) return Array.Empty<ProcShot>();     // раскладка структуры ниже — для 64-битного процесса
        cost.Start();
        try
        {
            Calls++;
            if (buffer == IntPtr.Zero) buffer = Marshal.AllocHGlobal(size);
            int ret = 0;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                uint st = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out ret);
                if (st == 0) break;
                if (st != StatusInfoLengthMismatch) return Array.Empty<ProcShot>();
                Marshal.FreeHGlobal(buffer);
                size = Math.Max(size * 2, ret + (256 << 10));
                buffer = Marshal.AllocHGlobal(size);
                if (attempt == 3) return Array.Empty<ProcShot>();
            }
            long now = Stopwatch.GetTimestamp();
            double dt = prevTs == 0 ? 0 : (now - prevTs) / (double)Stopwatch.Frequency;
            prevTs = now;

            seen.Clear();
            byName.Clear();
            double self = 0, gameIo = 0;
            bool gameSeen = false;
            int off = 0;
            while (true)
            {
                IntPtr e = buffer + off;
                int next = Marshal.ReadInt32(e, 0);
                int pid = (int)Marshal.ReadInt64(e, 80);
                long created = Marshal.ReadInt64(e, 32);
                long cpuTime = Marshal.ReadInt64(e, 40) + Marshal.ReadInt64(e, 48);           // UserTime + KernelTime, по 100 нс
                long io = Marshal.ReadInt64(e, 232) + Marshal.ReadInt64(e, 240);             // ReadTransferCount + WriteTransferCount
                if (pid != 0)
                {
                    var key = (pid, created);
                    string name;
                    if (prev.TryGetValue(key, out var p)) name = p.Name;
                    else
                    {
                        ushort len = (ushort)Marshal.ReadInt16(e, 56);
                        IntPtr str = Marshal.ReadIntPtr(e, 64);
                        name = len > 0 && str != IntPtr.Zero ? Marshal.PtrToStringUni(str, len / 2) : "System";
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                    }
                    seen[key] = (cpuTime, io, name);
                    if (dt > 0.2 && prev.TryGetValue(key, out var was))
                    {
                        double c = Math.Max(0, cpuTime - was.Cpu) / (dt * 1e7 * cores) * 100;
                        double mbs = Math.Max(0, io - was.Io) / dt / (1024.0 * 1024);
                        if (pid == gamePid) { gameIo += mbs; gameSeen = true; }
                        else if (pid == selfPid || name.Equals("PresentMon", StringComparison.OrdinalIgnoreCase)) self += c;
                        else if (!(gameImage != null && name.Equals(gameImage, StringComparison.OrdinalIgnoreCase)))
                        {
                            var acc = byName.GetValueOrDefault(name);
                            byName[name] = (acc.Cpu + c, acc.Io + mbs);
                        }
                    }
                }
                if (next == 0) break;
                off += next;
                if (off >= size) break;
            }
            (prev, seen) = (seen, prev);
            if (dt <= 0.2) return Array.Empty<ProcShot>();
            SelfCpu = self;
            if (gameSeen) gameIoMBs = gameIo;
            return Top(byName);
        }
        catch { return Array.Empty<ProcShot>(); }
        finally { cost.Stop(); if (Calls == 1) FirstMs = cost.Elapsed.TotalMilliseconds; }
    }

    /// <summary>Самые заметные: до трёх по процессору и до трёх по диску (заметными считаются от 2 % процессора или 1 МБ/с).</summary>
    public static ProcShot[] Top(Dictionary<string, (double Cpu, double Io)> byName)
    {
        var all = byName.Where(x => x.Value.Cpu >= 2 || x.Value.Io >= 1).ToList();
        if (all.Count == 0) return Array.Empty<ProcShot>();
        var pick = all.OrderByDescending(x => x.Value.Cpu).Take(3)
                      .Concat(all.OrderByDescending(x => x.Value.Io).Take(3))
                      .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First());
        return pick.Select(x => new ProcShot { Name = x.Key, Cpu = (float)x.Value.Cpu, Io = (float)x.Value.Io }).ToArray();
    }

    public void Dispose()
    {
        if (buffer != IntPtr.Zero) { Marshal.FreeHGlobal(buffer); buffer = IntPtr.Zero; }
    }

    [DllImport("ntdll.dll")] private static extern uint NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);
}

// ======================================================================== какой видеоадаптер рисовал игру

/// <summary>
/// Какой видеоадаптер рисовал игру: счётчики Windows «GPU Engine» по процессу игры (экземпляр «pid_…_luid_…_engtype_3D»).
/// Опрос редкий — через 45 с после начала, через 3 минуты и дальше раз в 10 минут, в фоне с пониженным приоритетом:
/// две выборки категории с паузой в секунду.
/// </summary>
public sealed class GameGpuProbe
{
    private readonly Dictionary<string, int> votes = new();
    private DateTime next;
    private volatile bool busy;
    private double workMs;
    private int probes;

    /// <summary>Время работы опросов (без секундной паузы между выборками), мс, и сколько их было.</summary>
    public double WorkMs { get { lock (votes) return workMs; } }
    public int Probes { get { lock (votes) return probes; } }

    public void Tick(int pid, DateTime startedAt)
    {
        var now = DateTime.Now;
        if (busy || pid <= 0) return;
        if (next == default) next = startedAt.AddSeconds(45);
        if (now < next) return;
        // 45 с, 3 минуты, дальше раз в 10 минут: адаптер за игру не меняется, а выборка счётчиков — заметная работа (~0,3 с).
        next = now.AddMinutes(probesStarted++ == 0 ? 2.25 : 10);
        busy = true;
        // Отдельный поток с пониженным приоритетом: игре процессор нужнее.
        var thread = new Thread(() =>
        {
            var sw = new Stopwatch();
            try
            {
                string luid = Probe(pid, sw);
                lock (votes) { if (luid != null) votes[luid] = votes.GetValueOrDefault(luid) + 1; }
            }
            catch (Exception ex) { Log.Error("видеоадаптер игры", ex); }
            finally
            {
                lock (votes) { workMs += sw.Elapsed.TotalMilliseconds; probes++; }
                busy = false;
            }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "PulsePC game GPU probe" };
        thread.Start();
    }

    private int probesStarted;

    private static Dictionary<string, CounterSample> Read(string prefix, Stopwatch sw)
    {
        sw.Start();
        try { return ReadCore(prefix); }
        finally { sw.Stop(); }
    }

    private static Dictionary<string, CounterSample> ReadCore(string prefix)
    {
        var d = new Dictionary<string, CounterSample>();
        var all = new PerformanceCounterCategory("GPU Engine").ReadCategory();
        if (all == null || !all.Contains("Utilization Percentage")) return d;
        foreach (InstanceData x in all["Utilization Percentage"].Values)
            if (x.InstanceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && GpuVendors.IsLoadEngine(GpuVendors.ParseEngine(x.InstanceName).Type))
                d[x.InstanceName] = x.Sample;
        return d;
    }

    /// <summary>LUID адаптера, на котором больше всего работы у процесса, или null.</summary>
    private static string Probe(int pid, Stopwatch sw)
    {
        string prefix = $"pid_{pid}_";
        var a = Read(prefix, sw);
        if (a.Count == 0) return null;
        Thread.Sleep(1000);
        var b = Read(prefix, sw);
        var samples = new List<(string, double)>();
        foreach (var kv in b)
            if (a.TryGetValue(kv.Key, out var was)) { try { samples.Add((kv.Key, CounterSample.Calculate(was, kv.Value))); } catch { } }
        return Busiest(samples);
    }

    /// <summary>Из загрузки двигателей процесса — адаптер с самым загруженным двигателем (не меньше 1 %).</summary>
    public static string Busiest(IEnumerable<(string Instance, double Value)> samples)
    {
        var byLuid = new Dictionary<string, double>();
        foreach (var (inst, v) in samples)
        {
            var e = GpuVendors.ParseEngine(inst);
            if (e.Luid == "" || double.IsNaN(v)) continue;
            byLuid[e.Luid] = Math.Max(byLuid.GetValueOrDefault(e.Luid), v);
        }
        var best = byLuid.OrderByDescending(x => x.Value).FirstOrDefault();
        return best.Key != null && best.Value >= 1 ? best.Key : null;
    }

    /// <summary>Итог: какой адаптер рисовал чаще всего. null — счётчиков не было.</summary>
    public (string Used, bool Integrated, string Discrete, bool Hybrid)? Result(List<KmtAdapter> adapters = null)
    {
        string luid;
        lock (votes) luid = votes.OrderByDescending(x => x.Value).Select(x => x.Key).FirstOrDefault();
        if (luid == null) return null;
        adapters ??= Kmt.Enumerate();
        return Describe(luid, adapters);
    }

    public static (string Used, bool Integrated, string Discrete, bool Hybrid)? Describe(string luid, List<KmtAdapter> adapters)
    {
        var used = adapters.FirstOrDefault(x => x.Key.Equals(luid, StringComparison.OrdinalIgnoreCase));
        if (used == null) return null;
        // Виртуальные адаптеры (Parsec, Hyper-V, удалённый рабочий стол) — не «отдельная видеокарта, которая простаивала».
        var discrete = adapters.Where(x => !x.Integrated && !ReferenceEquals(x, used) && !GpuVendors.IsVirtual(x.Name))
                               .OrderByDescending(x => x.DedicatedBytes).FirstOrDefault();
        bool hybrid = adapters.Any(x => x.HybridDiscrete || x.HybridIntegrated);
        return (used.Name, used.Integrated, used.Integrated ? discrete?.Name : null, hybrid);
    }
}

// ======================================================================== разбор: виновники, отметки

/// <summary>
/// Что мешало игре: виновники просадок (кто занимал процессор и диск в этот момент), фоновые программы за всю сессию,
/// отметки на графиках (игра свёрнута, пауза в кадрах, сброс драйвера видеокарты, скачки пинга, виновники).
/// Защитник Windows и другие защитные программы только называются по имени процесса — их настройки не читаются.
/// </summary>
public static class SessionCulprits
{
    /// <summary>Процесс антивируса Защитника Windows (только по имени; его настройки не читаются).</summary>
    public static bool IsDefender(string name) =>
        (name ?? "").ToLowerInvariant() is "msmpeng" or "mpdefendercoreservice" or "mpcopyaccelerator";

    /// <summary>Понятное имя процесса. Неизвестные — как есть (имя файла без «.exe»).</summary>
    public static string Pretty(string name) => (name ?? "").ToLowerInvariant() switch
    {
        "msmpeng" or "mpdefendercoreservice" or "nissrv" or "mpcopyaccelerator" => L.T("Защитник Windows"),
        "onedrive" or "onedrive.sync.service" => "OneDrive",
        "searchindexer" or "searchprotocolhost" or "searchfilterhost" => L.T("Индексирование поиска Windows"),
        "tiworker" or "trustedinstaller" => L.T("Установка обновлений Windows"),
        "mousocoreworker" or "usoclient" or "wuauclt" or "musnotification" => L.T("Центр обновления Windows"),
        "compattelrunner" or "devicecensus" => L.T("Сбор данных совместимости Windows"),
        "svchost" => L.T("Службы Windows (svchost)"),
        "system" => L.T("Система Windows (драйверы, подкачка)"),
        "memory compression" => L.T("Сжатие памяти Windows"),
        "dwm" => L.T("Диспетчер окон Windows"),
        "explorer" => L.T("Проводник"),
        "audiodg" => L.T("Звук Windows"),
        "wmiprvse" => L.T("Служба WMI Windows"),
        "defrag" => L.T("Оптимизация дисков Windows"),
        "msiexec" => L.T("Установщик Windows"),
        "steam" or "steamwebhelper" or "steamservice" => "Steam",
        "epicgameslauncher" or "epicwebhelper" => "Epic Games Launcher",
        "discord" => "Discord",
        "obs64" or "obs32" => "OBS Studio",
        "chrome" => "Chrome",
        "msedge" or "msedgewebview2" => "Edge",
        "browser" => L.T("Яндекс Браузер"),
        "firefox" => "Firefox",
        "opera" => "Opera",
        "qbittorrent" => "qBittorrent",
        "utorrent" => "uTorrent",
        "googledrivefs" => "Google Drive",
        "dropbox" => "Dropbox",
        "wallpaper32" or "wallpaper64" => "Wallpaper Engine",
        "nvcontainer" or "nvidia app" or "nvidia overlay" or "nvidia share" => "NVIDIA App",
        "claude" => "Claude",
        _ => name ?? "",
    };

    /// <summary>
    /// Заполнить в отчёте виновников просадок, фоновые программы и отметки. driverResets — времена событий Display 4101
    /// (null — прочитать из журнала System за время сессии).
    /// </summary>
    public static void Apply(GameSession s, List<SessionSample> samples, FpsResult frames, List<DateTime> driverResets = null)
    {
        if (samples == null || samples.Count < 4) return;
        if (s.Dips != null) foreach (var d in s.Dips) d.Culprits = DipCulprits(d, samples);
        s.Background = Background(samples);
        var events = new List<SessionEvent>();
        events.AddRange(Minimized(samples, frames));
        driverResets ??= DriverResets(s.StartUtc ?? s.Start.ToUniversalTime(), s.EndUtc ?? s.End.ToUniversalTime());
        events.AddRange(driverResets.Select(t => new SessionEvent { Kind = "driver", At = t }));
        events.AddRange(PingSpikes(samples));
        events.AddRange(CulpritEvents(samples, s.Dips));
        s.Events = events.OrderBy(e => e.At).ToList();
    }

    // ------------------------------------------------------------------ виновники просадки
    public static List<Culprit> DipCulprits(FpsDip dip, List<SessionSample> samples)
    {
        var from = dip.At.AddSeconds(-1.6);
        var to = dip.At.AddSeconds(dip.Seconds + 1.6);
        var acc = new Dictionary<string, (double Cpu, double Io)>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in samples)
        {
            if (x.At < from || x.At > to || x.Top == null) continue;
            foreach (var p in x.Top)
            {
                var a = acc.GetValueOrDefault(p.Name);
                acc[p.Name] = (Math.Max(a.Cpu, p.Cpu), Math.Max(a.Io, p.Io));
            }
        }
        // Заметные: от 8 % всего процессора или от 15 МБ/с. Два самых тяжёлых.
        return acc.Where(x => x.Value.Cpu >= 8 || x.Value.Io >= 15)
                  .OrderByDescending(x => x.Value.Cpu / 10 + x.Value.Io / 20).Take(2)
                  .Select(x => new Culprit { Name = x.Key, Cpu = Math.Round(x.Value.Cpu, 1), DiskMBs = Math.Round(x.Value.Io, 1) }).ToList();
    }

    /// <summary>Текст про виновников: «OneDrive — диск 120 МБ/с; Защитник Windows — процессор 18 %».</summary>
    public static string Describe(IEnumerable<Culprit> list) =>
        string.Join("; ", (list ?? Enumerable.Empty<Culprit>()).Select(c => Pretty(c.Name) + " — " + Load(c.Cpu, c.DiskMBs)));

    /// <summary>«процессор 18 %, диск 120 МБ/с» — только заметное.</summary>
    public static string Load(double cpu, double disk)
    {
        var parts = new List<string>();
        if (cpu >= 5) parts.Add(L.F("процессор {0}", Pct(cpu)));
        if (disk >= 5) parts.Add(L.F("диск {0}", MBs(disk)));
        if (parts.Count == 0) parts.Add(cpu >= disk / 4 ? L.F("процессор {0}", Pct(cpu)) : L.F("диск {0}", MBs(disk)));
        return string.Join(", ", parts);
    }

    public static string Pct(double v) => v.ToString(v < 10 ? "0.#" : "0", L.Culture) + (L.En ? "%" : " %");
    public static string MBs(double v) => v.ToString(v < 10 ? "0.#" : "0", L.Culture) + " " + L.Pick("МБ/с", "MB/s");

    // ------------------------------------------------------------------ фоновые программы за всю сессию
    public static List<Culprit> Background(List<SessionSample> samples)
    {
        int n = samples.Count(x => x.Top != null);
        if (n < 4) return null;
        var acc = new Dictionary<string, (double Cpu, double Io, double PeakCpu, double PeakIo)>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in samples)
        {
            if (x.Top == null) continue;
            foreach (var p in x.Top)
            {
                var a = acc.GetValueOrDefault(p.Name);
                acc[p.Name] = (a.Cpu + p.Cpu, a.Io + p.Io, Math.Max(a.PeakCpu, p.Cpu), Math.Max(a.PeakIo, p.Io));
            }
        }
        // В средние входят и замеры, где процесса не было среди заметных (там он почти ничего не делал) — это нижняя оценка.
        return acc.Select(x => new Culprit
            {
                Name = x.Key, Cpu = Math.Round(x.Value.Cpu / n, 2), DiskMBs = Math.Round(x.Value.Io / n, 2),
                PeakCpu = Math.Round(x.Value.PeakCpu, 1), PeakDiskMBs = Math.Round(x.Value.PeakIo, 1),
            })
            .Where(c => c.Cpu >= 0.5 || c.DiskMBs >= 0.5 || c.PeakCpu >= 15 || c.PeakDiskMBs >= 30)
            .OrderByDescending(c => c.Cpu / 2 + c.DiskMBs / 4 + c.PeakCpu / 30 + c.PeakDiskMBs / 60).Take(6).ToList();
    }

    // ------------------------------------------------------------------ отметки
    /// <summary>Игра не на переднем плане (свёрнута, Alt+Tab) — от 3 секунд; паузы в кадрах, не совпавшие со сворачиванием, — отдельно.</summary>
    public static List<SessionEvent> Minimized(List<SessionSample> samples, FpsResult frames)
    {
        var list = new List<SessionEvent>();
        int unfocused = samples.Count(x => x.Unfocused);
        // Почти всю сессию «не на переднем плане» — значит, окно принадлежит другому процессу (лаунчер, оболочка): сворачиваний не видно.
        if (unfocused < 0.8 * samples.Count)
        {
            int i = 0;
            while (i < samples.Count)
            {
                if (!samples[i].Unfocused) { i++; continue; }
                int from = i;
                while (i < samples.Count && samples[i].Unfocused) i++;
                if (i - from >= 2)
                {
                    var end = i < samples.Count ? samples[i].At : samples[^1].At;
                    list.Add(new SessionEvent { Kind = "minimized", At = samples[from].At, Seconds = Math.Round((end - samples[from].At).TotalSeconds, 1) });
                }
            }
        }
        if (frames?.Pauses != null)
            foreach (var (from, to) in frames.Pauses)
            {
                if ((to - from).TotalSeconds < 10) continue;
                if (list.Any(m => m.Kind == "minimized" && from < m.At.AddSeconds(m.Seconds + 3) && to > m.At.AddSeconds(-3))) continue;
                list.Add(new SessionEvent { Kind = "pause", At = from, Seconds = Math.Round((to - from).TotalSeconds, 1) });
            }
        return list.OrderBy(e => e.At).Take(40).ToList();
    }

    /// <summary>Скачки пинга до интернета: заметно выше обычного (на 40 мс и вдвое), соседние замеры — одной отметкой.</summary>
    public static List<SessionEvent> PingSpikes(List<SessionSample> samples)
    {
        var pings = samples.Where(x => x.PingMs > 0).Select(x => x.PingMs).OrderBy(x => x).ToList();
        var list = new List<SessionEvent>();
        if (pings.Count < 10) return list;
        double median = pings[pings.Count / 2], limit = median + Math.Max(40, median);
        SessionEvent cur = null;
        DateTime last = default;
        foreach (var x in samples)
        {
            if (!(x.PingMs >= limit)) continue;
            if (cur != null && (x.At - last).TotalSeconds <= 4)
            {
                cur.Value = Math.Max(cur.Value, x.PingMs);
                cur.Seconds = Math.Round((x.At - cur.At).TotalSeconds + 1.5, 1);
            }
            else
            {
                cur = new SessionEvent { Kind = "ping", At = x.At, Seconds = 1.5, Value = x.PingMs };
                list.Add(cur);
            }
            last = x.At;
        }
        return list.OrderByDescending(e => e.Value).Take(20).OrderBy(e => e.At).ToList();
    }

    /// <summary>
    /// Виновники на графике: процесс занимал от 15 % процессора или от 30 МБ/с диска — два замера подряд или во время просадки.
    /// </summary>
    public static List<SessionEvent> CulpritEvents(List<SessionSample> samples, List<FpsDip> dips)
    {
        var list = new List<SessionEvent>();
        var open = new Dictionary<string, (SessionEvent E, int Last, int Count)>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < samples.Count; i++)
        {
            var top = samples[i].Top;
            if (top == null) continue;
            foreach (var p in top)
            {
                if (p.Cpu < 15 && p.Io < 30) continue;
                if (open.TryGetValue(p.Name, out var o) && i - o.Last <= 2)
                {
                    o.E.Cpu = Math.Max(o.E.Cpu, Math.Round(p.Cpu, 1));
                    o.E.DiskMBs = Math.Max(o.E.DiskMBs, Math.Round(p.Io, 1));
                    o.E.Seconds = Math.Round((samples[i].At - o.E.At).TotalSeconds + 1.5, 1);
                    open[p.Name] = (o.E, i, o.Count + 1);
                }
                else
                {
                    var e = new SessionEvent { Kind = "culprit", At = samples[i].At, Seconds = 1.5, Name = p.Name, Cpu = Math.Round(p.Cpu, 1), DiskMBs = Math.Round(p.Io, 1) };
                    open[p.Name] = (e, i, 1);
                    list.Add(e);
                }
            }
        }
        bool InDip(SessionEvent e) => dips != null && dips.Any(d => e.At <= d.At.AddSeconds(d.Seconds + 1.6) && e.At.AddSeconds(e.Seconds) >= d.At.AddSeconds(-1.6));
        var keep = list.Where(e => e.Seconds >= 3 || InDip(e)).ToList();
        return keep.OrderByDescending(e => (InDip(e) ? 1000 : 0) + e.Cpu / 10 + e.DiskMBs / 20).Take(20).OrderBy(e => e.At).ToList();
    }

    /// <summary>
    /// Сбросы драйвера видеокарты за время сессии: журнал System, источник Display, событие 4101 («драйвер перестал отвечать
    /// и был восстановлен»). Только чтение журнала; без доступа — пустой список.
    /// </summary>
    public static List<DateTime> DriverResets(DateTime fromUtc, DateTime toUtc)
    {
        var list = new List<DateTime>();
        try
        {
            string F(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            string xpath = $"*[System[Provider[@Name='Display'] and (EventID=4101) and TimeCreated[@SystemTime>='{F(fromUtc)}' and @SystemTime<='{F(toUtc.AddSeconds(5))}']]]";
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, xpath));
            for (EventRecord r; (r = reader.ReadEvent()) != null;)
                using (r) if (r.TimeCreated is DateTime t && list.Count < 50) list.Add(t);
        }
        catch (Exception ex) { Log.Info("журнал System (сброс драйвера): " + ex.Message); }
        return list;
    }

    /// <summary>Процесс окна на переднем плане (0 — не определить: экран блокировки, смена пользователя).</summary>
    public static int ForegroundPid()
    {
        try
        {
            IntPtr w = GetForegroundWindow();
            if (w == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(w, out uint pid);
            return (int)pid;
        }
        catch { return 0; }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    // ================================================================== самопроверка
    /// <summary>
    /// Самопроверка отчёта после игры на подставных данных: время кадров и плавность, вид рывков, виновники, отметки,
    /// лучшие условия, чтение старых сессий; плюс замер стоимости опросов на этом ПК. PulsePC.exe --feature-test SessionCulprits файл.txt
    /// </summary>
    public static string SelfTest() => SessionReportTest.Run();
}
