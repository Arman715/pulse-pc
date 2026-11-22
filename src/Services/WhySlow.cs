using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using PulsePC.Panels;

namespace PulsePC.Services;

/// <summary>Программа в замере «Почему тормозит»: все процессы с одним именем вместе.</summary>
public sealed class SlowProc
{
    public string Name = "";
    /// <summary>Процессор, % от всего процессора; диск (чтение и запись), МБ/с; память (рабочие наборы), байт.</summary>
    public double Cpu, DiskMBs;
    public long MemBytes;
    public int Count = 1;
    /// <summary>У программы есть окно в сеансе пользователя (её можно закрыть как обычную программу).</summary>
    public bool HasWindow;
    /// <summary>Это идущая игра.</summary>
    public bool IsGame;
    public string Pretty => WhySlow.Pretty(Name);
    /// <summary>Закрыть можно только обычную программу с окном: не системную, не защитную, не игру и не саму программу.</summary>
    public bool CanClose => HasWindow && !IsGame && WhySlow.Closable(Name);
}

/// <summary>Пять секунд наблюдения: средние показания за это время. NaN — показания нет.</summary>
public sealed class SlowSample
{
    public double Seconds = 5;
    public double Cpu = double.NaN, MaxCore = double.NaN, CpuGhz = double.NaN;
    public double Gpu = double.NaN, GpuTemp = double.NaN, VramUsedGb = double.NaN, VramTotalGb = double.NaN;
    public double RamTotalGb = double.NaN, RamAvailGb = double.NaN, CommitPct = double.NaN, PagesIn = double.NaN;
    /// <summary>Самый занятый диск: занятость, %, и его буквы.</summary>
    public double DiskBusy = double.NaN, DiskMBs = double.NaN;
    public string DiskName = "";
    public double NetDownMBs = double.NaN, NetUpMBs = double.NaN, PingMs = double.NaN;
    public string Game = "";
    public bool OnBattery, PowerSaver;
    public double UptimeDays;
    public List<SlowProc> Procs = new();
}

/// <summary>Что делать: закрыть программу, освободить память или перейти в раздел программы.</summary>
public sealed class SlowAction
{
    public const string Close = "close", Free = "free", Open = "open";
    public string Kind = Open;
    public string Label = "";
    /// <summary>Имя процесса (close) или ссылка «tab:…» / «sys:…» (open).</summary>
    public string Target = "";
}

/// <summary>Одна причина: заголовок простыми словами, пояснение и действия.</summary>
public sealed class SlowCause
{
    public string Kind = "";
    public Severity Level = Severity.Warn;
    public string Title = "", Text = "";
    public List<SlowAction> Actions = new();
}

public sealed class SlowReport
{
    public Severity Level = Severity.Ok;
    public string Title = "", Text = "";
    public List<SlowCause> Causes = new();
    /// <summary>Показания за пять секунд: подпись, значение, требует ли внимания.</summary>
    public List<(string Label, string Value, bool Warn)> Readings = new();
    public List<SlowProc> Top = new();
}

/// <summary>
/// «Почему тормозит прямо сейчас»: пять секунд наблюдения за процессором, видеокартой, памятью, дисками, сетью и самыми
/// активными программами — и вывод простыми словами с действиями. Только чтение: программа сама ничего не закрывает,
/// закрыть можно только обычную программу с окном и только по кнопке с подтверждением. Защитные программы (Защитник Windows,
/// античиты) и системные процессы только называются — их настройки не читаются, закрыть их нельзя.
/// </summary>
public static class WhySlow
{
    public const int ObserveSeconds = 5;
    private const string Nb = " ";
    private const string SaverGuid = "a1841308-3541-4fab-bc81-f71556f20b4a";

    // ================================================================== наблюдение

    /// <summary>
    /// Наблюдать seconds секунд (в фоне). live — показания видеокарты и пинга от окна программы. progress — доля 0..1.
    /// Нагрузка: два чтения счётчиков Windows и два снимка процессов (NtQuerySystemInformation) плюс опрос раз в полсекунды.
    /// </summary>
    public static SlowSample Observe(Func<LiveData> live, int seconds = ObserveSeconds, Action<double> progress = null, CancellationToken cancel = default)
    {
        var s = new SlowSample { Seconds = seconds };
        using var activity = new ProcessActivity();
        activity.Sample(-1, null, out _);
        bool sysOk = GetSystemTimes(out long idle0, out long kernel0, out long user0);
        var disk0 = ReadCategory("PhysicalDisk");
        var cores0 = ReadCategory("Processor Information");
        var mem0 = ReadCategory("Memory");
        var (down0, up0) = NetBytes();
        var watch = Stopwatch.StartNew();

        var gpu = new List<double>(); var gpuT = new List<double>(); var vram = new List<double>(); var ping = new List<double>(); var ghz = new List<double>();
        int steps = Math.Max(1, seconds * 2);
        for (int i = 1; i <= steps; i++)
        {
            cancel.WaitHandle.WaitOne(500);
            cancel.ThrowIfCancellationRequested();
            var d = live?.Invoke();
            if (d != null)
            {
                Add(gpu, d.GpuLoad); Add(gpuT, d.GpuTemp); Add(vram, d.VramUsedGb); Add(ping, d.PingMs); Add(ghz, d.CpuGhz);
                if (d.Recording && !string.IsNullOrEmpty(d.Game)) s.Game = d.Game;
            }
            progress?.Invoke((double)i / steps);
        }
        double dt = Math.Max(0.5, watch.Elapsed.TotalSeconds);

        // Процессор: доля времени «не простоя» по всем ядрам.
        if (sysOk && GetSystemTimes(out long idle1, out long kernel1, out long user1))
        {
            double total = (kernel1 - kernel0) + (user1 - user0), idle = idle1 - idle0;
            if (total > 0) s.Cpu = Math.Clamp(100 * (total - idle) / total, 0, 100);
        }
        var cores1 = ReadCategory("Processor Information");
        s.MaxCore = MaxOver(cores0, cores1, "% Processor Utility", "_Total");
        if (!double.IsNaN(s.MaxCore)) s.MaxCore = Math.Clamp(s.MaxCore, 0, 100);

        // Диски: самый занятый физический диск.
        var disk1 = ReadCategory("PhysicalDisk");
        if (disk0 != null && disk1 != null)
        {
            foreach (var (inst, idle) in Each(disk0, disk1, "% Idle Time"))
            {
                if (inst.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue;
                double busy = Math.Clamp(100 - idle, 0, 100);
                if (double.IsNaN(s.DiskBusy) || busy > s.DiskBusy)
                {
                    s.DiskBusy = busy;
                    s.DiskName = DiskLabel(inst);
                    s.DiskMBs = Value(disk0, disk1, "Disk Bytes/sec", inst) / (1024.0 * 1024);
                }
            }
        }
        var mem1 = ReadCategory("Memory");
        s.PagesIn = Value(mem0, mem1, "Pages Input/sec", "");

        var (down1, up1) = NetBytes();
        if (down0 >= 0 && down1 >= down0) s.NetDownMBs = (down1 - down0) / dt / (1024.0 * 1024);
        if (up0 >= 0 && up1 >= up0) s.NetUpMBs = (up1 - up0) / dt / (1024.0 * 1024);

        s.Gpu = Avg(gpu); s.GpuTemp = Max(gpuT); s.VramUsedGb = Avg(vram); s.PingMs = Avg(ping); s.CpuGhz = Avg(ghz);
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref m) && m.ullTotalPhys > 0)
        {
            s.RamTotalGb = m.ullTotalPhys / Fmt.Gib;
            s.RamAvailGb = m.ullAvailPhys / Fmt.Gib;
            if (m.ullTotalPageFile > 0) s.CommitPct = 100.0 * (m.ullTotalPageFile - m.ullAvailPageFile) / m.ullTotalPageFile;
        }

        // Программы: процессор и диск за всё время наблюдения, память — сейчас.
        var shots = activity.Sample(-1, null, out _);
        var game = SafeFindGame();
        if (s.Game == "" && game != null) s.Game = game.Name;
        s.Procs = MergeProcs(shots, MemoryBoost.Top(8), WindowedNames(), game);
        // Общий признак батареи ноутбука: ИБП настольного компьютера (батарея «кратковременная») ноутбуком не считается.
        try { s.OnBattery = BatteryInfo.Read().OnBattery; } catch { }
        try { s.PowerSaver = PowerAuto.ActiveGuid() == SaverGuid; } catch { }
        s.UptimeDays = Environment.TickCount64 / 86400000.0;
        return s;
    }

    private static void Add(List<double> list, double v) { if (!double.IsNaN(v) && !double.IsInfinity(v)) list.Add(v); }
    private static double Avg(List<double> v) => v.Count == 0 ? double.NaN : v.Average();
    private static double Max(List<double> v) => v.Count == 0 ? double.NaN : v.Max();

    private static DetectedGame SafeFindGame()
    {
        try { return GameDetector.Find(); } catch { return null; }
    }

    /// <summary>Имена программ, у которых есть окно в сеансе пользователя.</summary>
    private static HashSet<string> WindowedNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int session = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcesses())
        {
            try { if (p.SessionId == session && p.MainWindowHandle != IntPtr.Zero) set.Add(p.ProcessName); }
            catch { }
            finally { p.Dispose(); }
        }
        return set;
    }

    /// <summary>Свести вместе процессор и диск (из ProcessActivity) и память (рабочие наборы) по имени программы.</summary>
    public static List<SlowProc> MergeProcs(IEnumerable<ProcShot> shots, IEnumerable<(string Name, int Count, long Bytes)> memory, ISet<string> windowed, DetectedGame game)
    {
        var map = new Dictionary<string, SlowProc>(StringComparer.OrdinalIgnoreCase);
        SlowProc Get(string name) => map.TryGetValue(name, out var p) ? p : map[name] = new SlowProc { Name = name };
        foreach (var x in shots ?? Array.Empty<ProcShot>())
        {
            if (string.IsNullOrEmpty(x.Name) || x.Name == "Idle") continue;
            var p = Get(x.Name);
            p.Cpu += x.Cpu; p.DiskMBs += x.Io;
        }
        foreach (var (name, count, bytes) in memory ?? Array.Empty<(string, int, long)>())
        {
            if (string.IsNullOrEmpty(name) || name == "Idle" || name.Equals("PulsePC", StringComparison.OrdinalIgnoreCase)) continue;
            var p = Get(name);
            p.MemBytes = bytes; p.Count = Math.Max(1, count);
        }
        foreach (var p in map.Values)
        {
            p.HasWindow = windowed?.Contains(p.Name) ?? false;
            p.IsGame = game != null && IsGameName(p.Name, game);
        }
        return map.Values.OrderByDescending(p => p.Cpu + p.DiskMBs / 4 + p.MemBytes / (double)(1L << 30) * 5).ToList();
    }

    private static bool IsGameName(string name, DetectedGame game)
    {
        try
        {
            using var p = Process.GetProcessById(game.Pid);
            return p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ счётчики Windows (одно чтение категории — все экземпляры)

    private static InstanceDataCollectionCollection ReadCategory(string name)
    {
        try { return new PerformanceCounterCategory(name).ReadCategory(); } catch { return null; }
    }

    private static IEnumerable<(string Instance, double Value)> Each(InstanceDataCollectionCollection a, InstanceDataCollectionCollection b, string counter)
    {
        var list = new List<(string, double)>();
        try
        {
            var ca = a[counter]; var cb = b[counter];
            if (ca == null || cb == null) return list;
            foreach (InstanceData d in cb.Values)
            {
                var before = ca[d.InstanceName];
                if (before == null) continue;
                double v = CounterSample.Calculate(before.Sample, d.Sample);
                if (!double.IsNaN(v) && !double.IsInfinity(v)) list.Add((d.InstanceName, v));
            }
        }
        catch { }
        return list;
    }

    private static double Value(InstanceDataCollectionCollection a, InstanceDataCollectionCollection b, string counter, string instance)
    {
        if (a == null || b == null) return double.NaN;
        foreach (var (inst, v) in Each(a, b, counter))
            if (instance == "" || inst.Equals(instance, StringComparison.OrdinalIgnoreCase)) return Math.Max(0, v);
        return double.NaN;
    }

    private static double MaxOver(InstanceDataCollectionCollection a, InstanceDataCollectionCollection b, string counter, string skip)
    {
        if (a == null || b == null) return double.NaN;
        double max = double.NaN;
        foreach (var (inst, v) in Each(a, b, counter))
        {
            if (inst.Contains(skip, StringComparison.OrdinalIgnoreCase)) continue;
            max = double.IsNaN(max) ? v : Math.Max(max, v);
        }
        return max;
    }

    /// <summary>«0 C: D:» → «C: D:»; без букв — «диск 0».</summary>
    public static string DiskLabel(string instance)
    {
        var parts = (instance ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var letters = parts.Where(p => p.EndsWith(':')).ToList();
        if (letters.Count > 0) return string.Join(" ", letters);
        return parts.Length > 0 ? L.F("диск {0}", parts[0]) : "";
    }

    /// <summary>Всего принято и отправлено по работающим подключениям (кроме петли), байт; −1 — не прочитано.</summary>
    private static (long Down, long Up) NetBytes()
    {
        try
        {
            long down = 0, up = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var st = nic.GetIPStatistics();
                down += st.BytesReceived; up += st.BytesSent;
            }
            return (down, up);
        }
        catch { return (-1, -1); }
    }

    // ================================================================== вывод

    /// <summary>Понятное имя процесса (общий словарь с отчётом сессии).</summary>
    public static string Pretty(string name)
    {
        string p = SessionCulprits.Pretty(name);
        return p == name ? ProcessMemory.Pretty(name) : p;
    }

    /// <summary>Можно ли вообще предлагать закрыть программу с таким именем: не системная, не защитная, не античит и не сама программа.</summary>
    public static bool Closable(string name) =>
        !string.IsNullOrWhiteSpace(name) && !MemoryBoost.IsSystem(name) && !ProtectedProcesses.IsSecurity(name) && !SessionCulprits.IsDefender(name)
        && !name.Equals("PulsePC", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("PulsePC-Setup", StringComparison.OrdinalIgnoreCase);

    private static string Pct(double v) => double.IsNaN(v) ? "—" : v.ToString("0", L.Culture) + (L.En ? "%" : Nb + "%");
    private static string MBs(double v) => double.IsNaN(v) ? "—" : (v >= 10 ? v.ToString("0", L.Culture) : v.ToString("0.0", L.Culture)) + Nb + L.Pick("МБ/с", "MB/s");
    private static string Gb(double v) => double.IsNaN(v) ? "—" : v.ToString("0.0", L.Culture) + Nb + Fmt.GbUnit;

    /// <summary>
    /// Вывод по замеру. Чистая функция (проверяется на подставных замерах). Причины — от самой серьёзной; у каждой — что сделать.
    /// Пороги: процессор от 85 %, свободной памяти меньше 10 % (или 1 ГБ), диск занят от 90 %, видеокарта от 95 % без игры.
    /// </summary>
    public static SlowReport Analyze(SlowSample s, bool admin)
    {
        var r = new SlowReport();
        var procs = s.Procs ?? new List<SlowProc>();
        bool game = !string.IsNullOrEmpty(s.Game);
        SlowProc TopBy(Func<SlowProc, double> key, double min) => procs.Where(p => key(p) >= min).OrderByDescending(key).FirstOrDefault();
        void CloseAction(SlowCause c, SlowProc p)
        {
            if (p != null && p.CanClose) c.Actions.Add(new SlowAction { Kind = SlowAction.Close, Target = p.Name, Label = L.F("Закрыть «{0}»", p.Pretty) });
        }
        string Who(SlowProc p, string what) =>
            p == null ? "" : p.IsGame ? L.F("Больше всех — сама игра «{0}» ({1}).", s.Game, what)
            : !Closable(p.Name) ? L.F("Больше всех — {0} ({1}). Это часть Windows или защитная программа: закрывать её не нужно, обычно она скоро заканчивает работу.", p.Pretty, what)
            : !p.HasWindow ? L.F("Больше всех — {0} ({1}). У неё нет окна: это фоновая программа, её можно закрыть у часов или в «Диспетчере задач».", p.Pretty, what)
            : L.F("Больше всех — {0} ({1}).", p.Pretty, what);

        // --- процессор ---
        if (s.Cpu >= 85)
        {
            var top = TopBy(p => p.Cpu, 5);
            var c = new SlowCause
            {
                Kind = "cpu", Level = s.Cpu >= 95 ? Severity.Bad : Severity.Warn,
                Title = L.F("Процессор загружен почти полностью — {0}", Pct(s.Cpu)),
                Text = (top != null ? Who(top, Pct(top.Cpu)) + " " : "")
                       + (game ? L.T("Во время игры это бывает нормой: игра забирает процессор себе. Мешают программы, которые работают рядом с ней.")
                               : L.T("Когда процессор занят целиком, всё остальное ждёт своей очереди — отсюда задержки.")),
            };
            CloseAction(c, top);
            r.Causes.Add(c);
        }
        else if (s.MaxCore >= 97 && s.Cpu < 60)
        {
            var top = TopBy(p => p.Cpu, 3);
            if (top != null)
                r.Causes.Add(new SlowCause
                {
                    Kind = "core", Level = Severity.Info,
                    Title = L.T("Одно ядро процессора загружено полностью"),
                    Text = L.F("Похоже, {0} упирается в одно ядро: весь процессор свободен, но эта программа работает только на одном. Обычно так ведут себя старые игры и программы — помогает только более быстрый процессор.", top.IsGame ? L.F("игра «{0}»", s.Game) : top.Pretty),
                });
        }

        // --- память ---
        double freePct = s.RamTotalGb > 0 ? 100 * s.RamAvailGb / s.RamTotalGb : double.NaN;
        bool ramBad = !double.IsNaN(freePct) && (freePct < 10 || s.RamAvailGb < 1.0);
        bool ramWarn = !ramBad && !double.IsNaN(freePct) && (freePct < 20 || s.RamAvailGb < 2.0);
        if (ramBad || ramWarn)
        {
            var top = procs.Where(p => p.MemBytes > 0 && !p.IsGame).OrderByDescending(p => p.MemBytes).FirstOrDefault();
            bool paging = s.PagesIn >= 300;
            var c = new SlowCause
            {
                Kind = "ram", Level = ramBad || paging ? Severity.Bad : Severity.Warn,
                Title = L.F("Мало свободной памяти — {0} из {1}", Gb(s.RamAvailGb), Gb(s.RamTotalGb)),
                Text = (paging ? L.T("Windows уже достаёт данные из файла подкачки на диске — это и есть подтормаживания.") + " " : "")
                       + (top != null ? L.F("Больше всех занимает {0} — {1}.", top.Pretty, Fmt.Size(top.MemBytes)) + " " : "")
                       + L.T("Закройте то, что сейчас не нужно (браузер с множеством вкладок — частый случай)."),
            };
            CloseAction(c, top);
            if (admin) c.Actions.Add(new SlowAction { Kind = SlowAction.Free, Label = L.T("Освободить память") });
            c.Actions.Add(new SlowAction { Kind = SlowAction.Open, Target = "sys:Память в игре", Label = L.T("Раздел «Память в игре»") });
            r.Causes.Add(c);
        }

        // --- диск ---
        if (s.DiskBusy >= 60)
        {
            var top = TopBy(p => p.DiskMBs, 1);
            string where = s.DiskName != "" ? L.F("Диск {0} занят на {1}", s.DiskName, Pct(s.DiskBusy)) : L.F("Диск занят на {0}", Pct(s.DiskBusy));
            var c = new SlowCause
            {
                Kind = "disk", Level = s.DiskBusy >= 90 ? Severity.Bad : Severity.Warn,
                Title = where,
                Text = (top != null ? Who(top, MBs(top.DiskMBs)) + " " : L.T("Какая программа — Windows сейчас не показала. ")) +
                       L.T("Пока диск занят, программы и игры дольше загружают свои файлы — это заметно как подвисания на секунду-две."),
            };
            CloseAction(c, top);
            r.Causes.Add(c);
        }

        // --- видеокарта без игры ---
        if (!game && s.Gpu >= 90)
            r.Causes.Add(new SlowCause
            {
                Kind = "gpu", Level = Severity.Warn,
                Title = L.F("Видеокарта загружена на {0}, хотя игра не запущена", Pct(s.Gpu)),
                Text = L.T("Её занимает какая-то программа: видео в браузере, обои с анимацией, запись экрана или майнер. Какая именно — видно в «Диспетчере задач» в столбце «ГП»."),
            });
        if (s.GpuTemp >= 87)
            r.Causes.Add(new SlowCause
            {
                Kind = "gpuhot", Level = Severity.Warn,
                Title = L.F("Видеокарта горячая — {0}{1}°C", s.GpuTemp.ToString("0", L.Culture), Nb),
                Text = L.T("При перегреве видеокарта сама снижает частоту, и FPS падает. Проверьте вентиляторы и пыль в корпусе."),
                Actions = { new SlowAction { Kind = SlowAction.Open, Target = "sys:Вентиляторы", Label = L.T("Раздел «Вентиляторы»") } },
            });

        // --- сеть ---
        if (s.NetDownMBs >= 3 || s.NetUpMBs >= 1.5)
        {
            bool up = s.NetUpMBs >= 1.5 && s.NetUpMBs * 2 >= s.NetDownMBs;
            r.Causes.Add(new SlowCause
            {
                Kind = "net", Level = s.PingMs >= 90 ? Severity.Warn : Severity.Info,
                Title = up ? L.F("Интернет занят: отправляется {0}", MBs(s.NetUpMBs)) : L.F("Интернет занят: скачивается {0}", MBs(s.NetDownMBs)),
                Text = L.T("Что-то качает или отправляет файлы: обновление игры или Windows, торрент, облачное хранилище. Пока канал занят, пинг в онлайн-играх растёт. Какая программа — видно в «Мониторе ресурсов» на вкладке «Сеть».")
                       + (!double.IsNaN(s.PingMs) ? " " + L.F("Пинг сейчас — {0}{1}мс.", s.PingMs.ToString("0", L.Culture), Nb) : ""),
                Actions = { new SlowAction { Kind = SlowAction.Open, Target = "tab:Сеть", Label = L.T("Вкладка «Сеть»") } },
            });
        }
        else if (s.PingMs >= 120)
            r.Causes.Add(new SlowCause
            {
                Kind = "ping", Level = Severity.Warn,
                Title = L.F("Высокий пинг — {0}{1}мс", s.PingMs.ToString("0", L.Culture), Nb),
                Text = L.T("Сам компьютер сеть не занимает: задержка где-то по пути — в роутере, Wi-Fi или у провайдера. Подробнее — на вкладке «Сеть»."),
                Actions = { new SlowAction { Kind = SlowAction.Open, Target = "tab:Сеть", Label = L.T("Вкладка «Сеть»") } },
            });

        // --- питание ---
        if (s.OnBattery)
            r.Causes.Add(new SlowCause
            {
                Kind = "battery", Level = Severity.Warn,
                Title = L.T("Ноутбук работает от батареи"),
                Text = L.T("От батареи процессор и видеокарта работают медленнее, чтобы дольше продержаться. Подключите зарядку."),
            });
        if (s.PowerSaver)
            r.Causes.Add(new SlowCause
            {
                Kind = "saver", Level = Severity.Warn,
                Title = L.T("Включена схема питания «Экономия энергии»"),
                Text = L.T("В ней процессор работает на пониженной частоте. Для игр и работы лучше сбалансированная схема или схема высокой производительности."),
                Actions = { new SlowAction { Kind = SlowAction.Open, Target = "sys:Питание", Label = L.T("Раздел «Питание»") } },
            });

        r.Causes = r.Causes.OrderBy(c => c.Level).ToList();
        if (r.Causes.Count == 0 && s.UptimeDays >= 7)
            r.Causes.Add(new SlowCause
            {
                Kind = "uptime", Level = Severity.Info,
                Title = L.F("Компьютер не перезагружался {0}", Fmt.Plural((int)Math.Round(s.UptimeDays), "день", "дня", "дней")),
                Text = L.T("Перезагрузка раз в несколько дней освобождает память и заканчивает установку обновлений."),
            });

        // --- итог ---
        var main = r.Causes.FirstOrDefault(c => c.Level <= Severity.Warn);
        if (main != null)
        {
            r.Level = r.Causes.Min(c => c.Level);
            r.Title = main.Kind switch
            {
                "cpu" => L.T("Тормозит из-за процессора"),
                "ram" => L.T("Тормозит из-за нехватки памяти"),
                "disk" => L.T("Тормозит из-за занятого диска"),
                "gpu" => L.T("Видеокарту занимает фоновая программа"),
                "gpuhot" => L.T("Видеокарта перегревается"),
                "net" or "ping" => L.T("Проблема в сети, а не в компьютере"),
                "battery" or "saver" => L.T("Компьютер в режиме экономии"),
                _ => main.Title,
            };
            int more = r.Causes.Count(c => c.Level <= Severity.Warn) - 1;
            r.Text = more > 0 ? L.F("Причин несколько ({0}): ниже они по порядку важности, у каждой — что можно сделать.", more + 1) : L.T("Ниже — что видно за пять секунд и что можно сделать.");
        }
        else
        {
            r.Level = Severity.Ok;
            r.Title = L.T("Сейчас компьютер не нагружен");
            r.Text = game
                ? L.F("Идёт игра «{0}», но процессор, память, диск и сеть не на пределе. Если в игре бывают рывки — после игры откройте её отчёт на вкладке «Сессии»: там видно, что мешало в момент просадки.", s.Game)
                : L.T("Процессор, память, диск и сеть не на пределе. Если тормозит в игре — запустите её: программа запишет сессию и после игры покажет, что мешало в момент просадки (вкладка «Сессии»). Если тормозило минуту назад — нажмите «Проверить ещё раз», когда это повторится.");
        }

        // --- показания ---
        r.Readings.Add((L.T("Процессор"), Pct(s.Cpu) + (!double.IsNaN(s.MaxCore) ? " · " + L.F("самое занятое ядро {0}", Pct(s.MaxCore)) : ""), s.Cpu >= 85));
        r.Readings.Add((L.T("Видеокарта"), double.IsNaN(s.Gpu) ? L.T("нет показаний") : Pct(s.Gpu) + (!double.IsNaN(s.GpuTemp) ? " · " + s.GpuTemp.ToString("0", L.Culture) + Nb + "°C" : ""), !game && s.Gpu >= 90 || s.GpuTemp >= 87));
        r.Readings.Add((L.T("Память"), double.IsNaN(s.RamAvailGb) ? "—" : L.F("свободно {0} из {1}", Gb(s.RamAvailGb), Gb(s.RamTotalGb)), ramBad || ramWarn));
        // «C: — занят на 12 %, 2,0 МБ/с»: сначала какой диск, потом насколько занят и сколько через него идёт.
        r.Readings.Add((L.T("Диск"), double.IsNaN(s.DiskBusy) ? L.T("нет показаний")
            : (s.DiskName != "" ? s.DiskName + " — " : "") + L.F("занят на {0}", Pct(s.DiskBusy))
              + (!double.IsNaN(s.DiskMBs) && s.DiskMBs >= 0.1 ? ", " + L.F("чтение и запись {0}", MBs(s.DiskMBs)) : ""), s.DiskBusy >= 60));
        r.Readings.Add((L.T("Сеть"), double.IsNaN(s.NetDownMBs) ? "—" : L.F("приём {0}, отдача {1}", MBs(s.NetDownMBs), MBs(s.NetUpMBs)) + (!double.IsNaN(s.PingMs) ? " · " + L.F("пинг {0}", s.PingMs.ToString("0", L.Culture) + Nb + L.Pick("мс", "ms")) : ""), s.NetDownMBs >= 3 || s.NetUpMBs >= 1.5 || s.PingMs >= 120));
        r.Top = procs.Where(p => p.Cpu >= 1 || p.DiskMBs >= 0.5 || p.MemBytes >= 200L << 20).Take(8).ToList();
        return r;
    }

    // ================================================================== действия (только по кнопке с подтверждением)

    /// <summary>
    /// Закрыть программу как обычно — «закрыть окно» (она может спросить о несохранённом). Ждёт до 8 секунд.
    /// Возвращает, сколько процессов с этим именем осталось. Системные, защитные и сама программа не трогаются никогда.
    /// </summary>
    public static int Close(string name)
    {
        if (!Closable(name)) return -1;
        int session = Process.GetCurrentProcess().SessionId;
        var found = Find(name, session);
        if (AnyGame(found)) return -1;
        foreach (var p in found)
        {
            try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); } catch { }
            finally { p.Dispose(); }
        }
        for (int i = 0; i < 16; i++)
        {
            Thread.Sleep(500);
            int left = Count(name, session);
            if (left == 0) return 0;
        }
        return Count(name, session);
    }

    /// <summary>Завершить все процессы программы в сеансе пользователя (вторая кнопка, когда программа не закрылась сама).</summary>
    public static int Kill(string name)
    {
        if (!Closable(name)) return -1;
        int session = Process.GetCurrentProcess().SessionId;
        var found = Find(name, session);
        if (AnyGame(found)) return -1;
        foreach (var p in found)
        {
            try { p.Kill(); } catch { }
            finally { p.Dispose(); }
        }
        Thread.Sleep(800);
        return Count(name, session);
    }

    /// <summary>
    /// Среди процессов — идущая игра (найденная сейчас или из папки известной игры): её не закрываем никогда, даже если при замере
    /// она ещё не была узнана (запустили позже или узналась не сразу). Если так — процессы освобождаются.
    /// </summary>
    private static bool AnyGame(List<Process> procs)
    {
        bool game = false;
        try { var g = GameDetector.Find(); game = g != null && procs.Any(p => p.Id == g.Pid); } catch { }
        foreach (var p in procs)
        {
            if (game) break;
            if (CautiousMode.Protects(p.Id)) { game = true; break; }     // игра в осторожном режиме: её модули не читаются
            // Путь — с минимальными правами: MainModule открывал бы процесс на чтение памяти (игры под античитом так открывать нельзя).
            try { game = GameDetector.LooksLikeGame(GameFinder.ProcessPath(p.Id)); } catch { }
        }
        if (game) foreach (var p in procs) p.Dispose();
        return game;
    }

    private static List<Process> Find(string name, int session)
    {
        var list = new List<Process>();
        foreach (var p in Process.GetProcessesByName(name))
        {
            bool mine = false;
            try { mine = p.SessionId == session && p.Id != Environment.ProcessId; } catch { }
            if (mine) list.Add(p); else p.Dispose();
        }
        return list;
    }

    private static int Count(string name, int session)
    {
        var found = Find(name, session);
        foreach (var p in found) p.Dispose();
        return found.Count;
    }

    // ================================================================== самопроверка

    public static string SelfTest()
    {
        var lines = new List<string>();
        var fails = new List<string>();
        void Check(string what, bool ok, string detail = "")
        {
            lines.Add((ok ? "  да   " : "  НЕТ  ") + what + (detail != "" ? " — " + detail : ""));
            if (!ok) fails.Add(what);
        }
        SlowSample Base() => new()
        {
            Cpu = 12, MaxCore = 30, Gpu = 3, GpuTemp = 45, RamTotalGb = 16, RamAvailGb = 8, PagesIn = 0, DiskBusy = 4, DiskName = "C:", DiskMBs = 0.4,
            NetDownMBs = 0.05, NetUpMBs = 0.01, PingMs = 18, UptimeDays = 1,
        };

        var ok = Analyze(Base(), true);
        Check("спокойный компьютер — «не нагружен», причин нет", ok.Level == Severity.Ok && ok.Causes.Count == 0, ok.Title);

        var cpu = Base(); cpu.Cpu = 97;
        cpu.Procs = new List<SlowProc> { new() { Name = "chrome", Cpu = 61, MemBytes = 3L << 30, Count = 24, HasWindow = true }, new() { Name = "MsMpEng", Cpu = 20 } };
        var rc = Analyze(cpu, true);
        var cc = rc.Causes.FirstOrDefault();
        Check("процессор 97 % — причина «процессор», виновник Chrome, кнопка «Закрыть»",
            cc?.Kind == "cpu" && cc.Level == Severity.Bad && cc.Text.Contains("Chrome") && cc.Actions.Any(a => a.Kind == SlowAction.Close && a.Target == "chrome"), cc?.Text);

        var def = Base(); def.DiskBusy = 99; def.DiskMBs = 140;
        def.Procs = new List<SlowProc> { new() { Name = "MsMpEng", DiskMBs = 120, Cpu = 15 } };
        var rd = Analyze(def, true).Causes.FirstOrDefault(c => c.Kind == "disk");
        Check("диск занят Защитником — называем по имени, закрыть не предлагаем", rd != null && rd.Text.Contains(L.T("Защитник Windows")) && rd.Actions.All(a => a.Kind != SlowAction.Close), rd?.Text);
        Check("защитные, системные и сама программа не закрываются", !Closable("MsMpEng") && !Closable("vgc") && !Closable("svchost") && !Closable("explorer") && !Closable("PulsePC") && Closable("chrome"));
        Check("Close/Kill отказываются от системных процессов", Close("csrss") == -1 && Kill("MsMpEng") == -1 && Kill("PulsePC") == -1);

        var ram = Base(); ram.RamAvailGb = 0.7; ram.PagesIn = 900;
        ram.Procs = new List<SlowProc> { new() { Name = "browser", MemBytes = 5L << 30, HasWindow = true, Count = 30 } };
        var rr = Analyze(ram, true).Causes.FirstOrDefault();
        Check("памяти 0,7 из 16 ГБ и подкачка — «нехватка памяти», «Освободить память», закрыть браузер",
            rr?.Kind == "ram" && rr.Level == Severity.Bad && rr.Actions.Any(a => a.Kind == SlowAction.Free) && rr.Actions.Any(a => a.Kind == SlowAction.Close && a.Target == "browser"), rr?.Title);
        var rrUser = Analyze(ram, false).Causes.FirstOrDefault();
        Check("без прав администратора «Освободить память» не предлагается", rrUser != null && rrUser.Actions.All(a => a.Kind != SlowAction.Free));

        var gm = Base(); gm.Game = "Deadlock"; gm.Cpu = 92; gm.Gpu = 99;
        gm.Procs = new List<SlowProc> { new() { Name = "deadlock", Cpu = 70, IsGame = true, HasWindow = true }, new() { Name = "Discord", Cpu = 6, HasWindow = true } };
        var rg = Analyze(gm, true);
        var gc = rg.Causes.FirstOrDefault(c => c.Kind == "cpu");
        Check("в игре: загрузка видеокарты не считается проблемой, игру закрыть не предлагаем",
            rg.Causes.All(c => c.Kind != "gpu") && gc != null && gc.Actions.All(a => a.Target != "deadlock") && gc.Text.Contains("Deadlock"), gc?.Text);

        var idleGpu = Base(); idleGpu.Gpu = 98;
        Check("видеокарта 98 % без игры — замечание", Analyze(idleGpu, true).Causes.Any(c => c.Kind == "gpu"));

        var net = Base(); net.NetDownMBs = 11; net.PingMs = 140;
        var rn = Analyze(net, true);
        Check("скачивание 11 МБ/с при пинге 140 — «сеть», а не компьютер", rn.Causes.FirstOrDefault()?.Kind == "net" && rn.Title == L.T("Проблема в сети, а не в компьютере"), rn.Title);

        var saver = Base(); saver.PowerSaver = true; saver.OnBattery = true;
        var rs = Analyze(saver, true);
        Check("батарея и «Экономия энергии» — две причины", rs.Causes.Count(c => c.Kind is "battery" or "saver") == 2);

        var up = Base(); up.UptimeDays = 12.3;
        Check("ничего не нагружено, но 12 дней без перезагрузки — подсказка, итог «не нагружен»", Analyze(up, true) is { Level: Severity.Ok } u && u.Causes.Any(c => c.Kind == "uptime"));
        Check("диск: «0 C: D:» → «C: D:»", DiskLabel("0 C: D:") == "C: D:" && DiskLabel("1") == L.F("диск {0}", "1"));

        var merged = MergeProcs(new[] { new ProcShot { Name = "chrome", Cpu = 10, Io = 1 }, new ProcShot { Name = "chrome", Cpu = 5, Io = 0 } },
            new[] { ("chrome", 12, 2L << 30), ("PulsePC", 1, 300L << 20) }, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "chrome" }, null);
        Check("программы сводятся по имени, сама программа не попадает в список",
            merged.Count == 1 && merged[0].Cpu == 15 && merged[0].MemBytes == 2L << 30 && merged[0].Count == 12 && merged[0].CanClose);

        // Что видно на этом ПК (только чтение, 2 секунды).
        try
        {
            var watch = Stopwatch.StartNew();
            var real = Observe(() => new LiveData(), 2);
            var rep = Analyze(real, Shell.IsAdmin());
            lines.Add($"Этот ПК за 2 с ({watch.ElapsedMilliseconds} мс): процессор {real.Cpu:0} %, ядро {real.MaxCore:0} %, память {real.RamAvailGb:0.0} из {real.RamTotalGb:0.0} ГБ, " +
                      $"диск {real.DiskName} {real.DiskBusy:0} %, сеть {real.NetDownMBs:0.00}/{real.NetUpMBs:0.00} МБ/с, подкачка {real.PagesIn:0}/с");
            lines.Add("  вывод: " + rep.Title + (rep.Causes.Count > 0 ? " (" + string.Join("; ", rep.Causes.Select(c => c.Title)) + ")" : ""));
            lines.Add("  активные: " + string.Join(", ", rep.Top.Take(5).Select(p => $"{p.Pretty} {p.Cpu:0}%/{p.DiskMBs:0.0}МБ/с/{Fmt.Size(p.MemBytes)}{(p.CanClose ? "" : " [не закрывается]")}")));
            Check("замер на этом ПК прочитал процессор и память", !double.IsNaN(real.Cpu) && real.RamTotalGb > 0);
        }
        catch (Exception ex) { Check("замер на этом ПК", false, ex.Message); }

        lines.Insert(0, fails.Count == 0 ? "OK" : "FAIL: " + string.Join("; ", fails));
        return string.Join(Environment.NewLine, lines);
    }

    // ================================================================== Windows

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
}
