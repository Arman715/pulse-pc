using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PulsePC.Services;

/// <summary>Замечание осмотра: что не так и где это исправить.</summary>
public sealed class ScanFinding
{
    public Severity Severity { get; set; }
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    /// <summary>Раздел программы: 0 Обзор, 1 Сессии, 2 История, 3 Сеть, 4 Диагностика, 5 Очистка, 6 Оптимизация, 7 Автозагрузка; −1 — нет.</summary>
    public int TargetPage { get; set; } = -1;
    /// <summary>Раздел вкладки «Система» («Драйвер», «Питание»…); если задан, ссылка ведёт туда, а не на TargetPage.</summary>
    public string TargetSection { get; set; } = "";
    /// <summary>Шаг осмотра, на котором найдено.</summary>
    public string Step { get; set; } = "";
    public string PageName => TargetSection != "" ? TargetSection : SystemScan.PageName(TargetPage);
}

/// <summary>Строка паспорта ПК. Значение может быть в несколько строк (через \n).</summary>
public sealed class PassportLine
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>Ход осмотра: шаг начат (Done = false) или закончен (Done = true, Summary — итог в одну строку).</summary>
public sealed class ScanProgress
{
    public int Index { get; set; }
    public int Count { get; set; }
    public string Title { get; set; } = "";
    public bool Done { get; set; }
    public bool Failed { get; set; }
    public string Summary { get; set; } = "";
    /// <summary>Самое серьёзное замечание шага (Ok — замечаний нет).</summary>
    public Severity Worst { get; set; } = Severity.Ok;
}

public sealed class ScanStepResult
{
    public int Index { get; set; }
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public TimeSpan Took { get; set; }
    public string Error { get; set; }
    public Severity Worst { get; set; } = Severity.Ok;
}

public sealed class ScanGpu
{
    public string Name = "", Vendor = "", Driver = "", DriverShort = "", Pnp = "";
    public DateTime DriverDate;
    public long VramBytes;
    public bool Discrete, Basic;
}

public sealed class ScanRamModule
{
    public string Slot = "", Maker = "", Part = "";
    public long Bytes;
    public int RatedMhz, ConfiguredMhz, PartMhz, Type, FormFactor;
}

public sealed class ScanDisk
{
    public string Number = "", Name = "", Media = "", Bus = "", Health = "";
    public long Bytes;
    public bool IsSystem;
    public List<string> Letters = new();
    public double? Temp, Wear, Hours, ReadErrors;
    /// <summary>Понятный тип: «NVMe SSD», «SATA SSD», «HDD», «USB»…</summary>
    public string Kind = "";
}

public sealed class ScanVolume
{
    public string Letter = "";
    public long Total, Free;
    public bool IsSystem;
    public double FreePct => Total > 0 ? 100.0 * Free / Total : 0;
}

public sealed class ScanMonitor
{
    public string Device = "", Name = "";
    public int Width, Height, Hz, MaxHz, NativeWidth, NativeHeight;
    public bool Primary;
}

public sealed class ScanNet
{
    public bool Connected, WiFi;
    public string Adapter = "", Gateway = "";
    public long SpeedBps;
    public double RouterAvg = double.NaN, RouterJitter = double.NaN, RouterLoss = double.NaN;
    public double InternetAvg = double.NaN, InternetJitter = double.NaN, InternetLoss = double.NaN;
    public string InternetHost = "1.1.1.1";
}

public sealed class ScanProcess
{
    public string Name = "";
    public long Bytes;
}

/// <summary>Итог осмотра: паспорт ПК, замечания и подробности по шагам.</summary>
public sealed class ScanReport
{
    public DateTime At { get; set; } = DateTime.Now;
    public TimeSpan Duration { get; set; }
    public bool IsAdmin { get; set; }
    public bool IsLaptop { get; set; }
    public List<PassportLine> Passport { get; } = new();
    public List<ScanFinding> Findings { get; } = new();
    public List<ScanStepResult> Steps { get; } = new();

    public string CpuName = "";
    public int Cores, Threads, BaseMhz;
    public List<ScanGpu> Gpus = new();
    public double RamTotalGb;
    public string RamType = "";
    public List<ScanRamModule> RamModules = new();
    public string BoardName = "", BiosVersion = "";
    public DateTime BiosDate;
    public List<ScanDisk> Disks = new();
    public List<ScanVolume> Volumes = new();
    public List<ScanMonitor> Monitors = new();
    public string Os = "", OsBuild = "", PowerPlan = "";
    public TimeSpan Uptime;
    public List<string> Games = new();
    public int GamesWithTips;
    public int ProgramsInstalled, StartupEnabled;
    public List<ScanProcess> TopMemory = new();
    public ScanNet Net = new();
    public long JunkBytes = -1;
    public int Bsods, Whea, WheaCorrected, UnexpectedShutdowns, DisplayDriverErrors, DiskErrors, AppCrashes;
    /// <summary>Сколько видов записей журнала отмечено в «Диагностике» как просмотренные (считаются только новые).</summary>
    public int HiddenKinds;

    public int Count(Severity s) => Findings.Count(f => f.Severity == s);
}

/// <summary>
/// Первичный осмотр: читает железо, настройки Windows, игры, сеть и журналы и составляет паспорт ПК с замечаниями.
/// Ничего не меняет. Каждый шаг обёрнут в try/catch — сбой одного не останавливает осмотр.
/// Тяжёлые запросы (CIM, накопители, пинг, подсчёт мусора, журналы) стартуют сразу и идут параллельно,
/// шаги лишь забирают их результаты по порядку.
/// «Безопасность Windows» (Defender) не запрашивается ни в каком виде.
/// </summary>
public static class SystemScan
{
    public static readonly string[] StepTitles =
    {
        "Процессор", "Видеокарта", "Память", "Материнская плата", "Диски", "Монитор",
        "Windows", "Игры", "Программы", "Сеть", "Мусор", "Журнал Windows",
    };

    public static string PageName(int page) => page switch
    {
        0 => "Обзор", 1 => "Сессии", 2 => "История", 3 => "Сеть", 4 => "Диагностика",
        5 => "Очистка", 6 => "Оптимизация", 7 => "Автозагрузка", _ => "",
    };

    private const int JunkBudgetMs = 12000;

    /// <summary>Осмотр в фоне. progress вызывается из фонового потока. minStepMs — минимальная длительность шага (для показа).</summary>
    public static Task<ScanReport> RunAsync(Action<ScanProgress> progress = null, CancellationToken cancel = default, int minStepMs = 0) =>
        Task.Run(() => Run(progress, cancel, minStepMs));

    public static ScanReport Run(Action<ScanProgress> progress = null, CancellationToken cancel = default, int minStepMs = 0)
    {
        var total = Stopwatch.StartNew();
        var ctx = new Ctx { R = new ScanReport { IsAdmin = Shell.IsAdmin() } };
        ctx.Start();

        Func<Ctx, string>[] steps = { StepCpu, StepGpu, StepRam, StepBoard, StepDisks, StepMonitors, StepWindows, StepGames, StepPrograms, StepNet, StepJunk, StepLog };
        for (int i = 0; i < steps.Length; i++)
        {
            if (cancel.IsCancellationRequested) break;
            string title = StepTitles[i];
            ctx.Step = title;
            Report(progress, new ScanProgress { Index = i, Count = steps.Length, Title = title });
            var sw = Stopwatch.StartNew();
            var result = new ScanStepResult { Index = i, Title = title };
            int before = ctx.R.Findings.Count;
            try { result.Summary = steps[i](ctx) ?? ""; }
            catch (Exception ex) { result.Error = ex.GetType().Name + ": " + ex.Message; result.Summary = L.T("не удалось проверить"); }
            var added = ctx.R.Findings.Skip(before).ToList();
            result.Worst = added.Count == 0 ? Severity.Ok : added.Min(f => f.Severity);
            int rest = minStepMs - (int)sw.ElapsedMilliseconds;
            if (rest > 0 && !cancel.IsCancellationRequested) Thread.Sleep(rest);
            result.Took = sw.Elapsed;
            ctx.R.Steps.Add(result);
            Report(progress, new ScanProgress
            {
                Index = i, Count = steps.Length, Title = title, Done = true, Failed = result.Error != null,
                Summary = result.Summary, Worst = result.Worst,
            });
        }

        var r = ctx.R;
        var ordered = r.Findings.Select((f, n) => (f, n)).OrderBy(x => x.f.Severity).ThenBy(x => x.n).Select(x => x.f).ToList();
        r.Findings.Clear();
        r.Findings.AddRange(ordered);
        r.Duration = total.Elapsed;
        return r;
    }

    private static void Report(Action<ScanProgress> progress, ScanProgress p)
    {
        try { progress?.Invoke(p); } catch { }
    }

    // ───────────────────────────── общий контекст и фоновые запросы ─────────────────────────────

    private sealed class Ctx
    {
        public ScanReport R;
        public string Step = "";
        public Task<JsonElement?> Hw, Storage;
        public Task<ScanNet> Net;
        public Task<List<TweakItem>> Tweaks;
        public Task<Dictionary<string, long>> Junk;
        public Task Log;
        public DateTime Started = DateTime.Now;

        public void Start()
        {
            Hw = Task.Run(() => QueryJson(HwScript, 45000));
            Storage = Task.Run(() => QueryJson(StorageScript, 45000));
            Net = Task.Run(MeasureNet);
            Tweaks = Task.Run(() =>
            {
                var items = Services.Tweaks.BuildItems();
                foreach (var t in items) { try { t.Applied = t.Check(); } catch { t.Applied = null; } }
                return items;
            });
            Junk = Task.Run(ScanJunk);
            Log = Task.Run(() => ReadLogs(R));
        }

        public void Add(Severity s, string title, string detail, int page = -1) =>
            R.Findings.Add(new ScanFinding { Severity = s, Title = title, Detail = detail, TargetPage = page, Step = Step });

        /// <summary>Замечание со ссылкой на раздел вкладки «Система».</summary>
        public void AddSection(Severity s, string title, string detail, string section) =>
            R.Findings.Add(new ScanFinding { Severity = s, Title = title, Detail = detail, TargetPage = 7, TargetSection = section, Step = Step });

        public void Passport(string label, string value) =>
            R.Passport.Add(new PassportLine { Label = label, Value = string.IsNullOrWhiteSpace(value) ? L.T("нет данных") : value.Trim() });

        public JsonElement? HwData => Wait(Hw, 50000);
        public JsonElement? StorageData => Wait(Storage, 50000);

        public static T Wait<T>(Task<T> t, int ms)
        {
            try { return t.Wait(ms) ? t.Result : default; } catch { return default; }
        }
    }

    private static JsonElement? QueryJson(string script, int timeout)
    {
        var (code, output) = Shell.PowerShell(script, timeout);
        if (string.IsNullOrWhiteSpace(output)) return null;
        int start = output.IndexOf('{');
        int end = output.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(output[start..(end + 1)]);
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    private const string HwScript =
        "$ErrorActionPreference='SilentlyContinue'; " +
        "function D($d){ if ($d) { try { ([datetime]$d).ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture) } catch { '' } } else { '' } } " +
        "$p=@(Get-CimInstance Win32_Processor); $v=@(Get-CimInstance Win32_VideoController); $m=@(Get-CimInstance Win32_PhysicalMemory); " +
        "$b=Get-CimInstance Win32_BaseBoard; $bi=Get-CimInstance Win32_BIOS; $cs=Get-CimInstance Win32_ComputerSystem; $os=Get-CimInstance Win32_OperatingSystem; " +
        "$bat=@(Get-CimInstance Win32_Battery).Count; " +
        "$mon=@(Get-CimInstance -Namespace root\\wmi -ClassName WmiMonitorID); " +
        "[pscustomobject]@{ " +
        "Cpu=@($p | ForEach-Object { [pscustomobject]@{ Name=\"$($_.Name)\".Trim(); Cores=[int]$_.NumberOfCores; Threads=[int]$_.NumberOfLogicalProcessors; Mhz=[int]$_.MaxClockSpeed } }); " +
        "Gpu=@($v | ForEach-Object { [pscustomobject]@{ Name=\"$($_.Name)\".Trim(); Driver=\"$($_.DriverVersion)\"; Date=(D $_.DriverDate); Ram=[double]$_.AdapterRAM; Pnp=\"$($_.PNPDeviceID)\" } }); " +
        "Ram=@($m | ForEach-Object { [pscustomobject]@{ Slot=\"$($_.DeviceLocator)\"; Maker=\"$($_.Manufacturer)\".Trim(); Part=\"$($_.PartNumber)\".Trim(); Bytes=[double]$_.Capacity; " +
        "Speed=[int]$_.Speed; Conf=[int]$_.ConfiguredClockSpeed; Type=[int]$_.SMBIOSMemoryType; Form=[int]$_.FormFactor } }); " +
        "Board=\"$($b.Product)\".Trim(); BoardMaker=\"$($b.Manufacturer)\".Trim(); Bios=\"$($bi.SMBIOSBIOSVersion)\".Trim(); BiosDate=(D $bi.ReleaseDate); " +
        "CsMaker=\"$($cs.Manufacturer)\".Trim(); CsModel=\"$($cs.Model)\".Trim(); PcType=[int]$cs.PCSystemType; Battery=[int]$bat; " +
        "Os=\"$($os.Caption)\".Trim(); Build=\"$($os.BuildNumber)\"; " +
        "Mon=@($mon | ForEach-Object { [pscustomobject]@{ Name=((@($_.UserFriendlyName) | Where-Object { $_ -ne 0 } | ForEach-Object { [char]$_ }) -join ''); Inst=\"$($_.InstanceName)\" } }) " +
        "} | ConvertTo-Json -Compress -Depth 4";

    private const string StorageScript =
        "$ErrorActionPreference='SilentlyContinue'; " +
        "$sys=(Get-Partition -DriveLetter $env:SystemDrive.Substring(0,1)).DiskNumber; " +
        "$parts=@(Get-Partition | Where-Object { $_.DriveLetter } | ForEach-Object { [pscustomobject]@{ Letter=\"$($_.DriveLetter)\"; Disk=[int]$_.DiskNumber } }); " +
        "$d=@(Get-PhysicalDisk | ForEach-Object { $r=$null; $r = $_ | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue; " +
        "[pscustomobject]@{ Id=\"$($_.DeviceId)\"; Name=\"$($_.FriendlyName)\".Trim(); Media=\"$($_.MediaType)\"; Bus=\"$($_.BusType)\"; Bytes=[double]$_.Size; " +
        "Health=\"$($_.HealthStatus)\"; Spindle=\"$($_.SpindleSpeed)\"; Temp=$r.Temperature; Wear=$r.Wear; Hours=$r.PowerOnHours; ReadErrors=$r.ReadErrorsUncorrected } }); " +
        "[pscustomobject]@{ Sys=$sys; Parts=$parts; Disks=$d } | ConvertTo-Json -Compress -Depth 4";

    // ───────────────────────────── JSON ─────────────────────────────

    private static string S(JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString().Trim() : "";

    private static double N(JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static double? Opt(JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static IEnumerable<JsonElement> Arr(JsonElement? e, string n)
    {
        if (e is not JsonElement el || el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(n, out var v)) return Enumerable.Empty<JsonElement>();
        if (v.ValueKind == JsonValueKind.Array) return v.EnumerateArray().ToList();
        if (v.ValueKind == JsonValueKind.Object) return new[] { v };
        return Enumerable.Empty<JsonElement>();
    }

    private static DateTime Date(string s) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.MinValue;

    // Заглушки производителей вместо названий.
    private static string Clean(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim();
        string[] junk = { "To be filled by O.E.M.", "Default string", "System Product Name", "System manufacturer", "Not Applicable", "None", "Undefined", "O.E.M.", "Base Board" };
        return junk.Any(j => s.Equals(j, StringComparison.OrdinalIgnoreCase)) ? "" : s;
    }

    private static string Tidy(string s) =>
        Regex.Replace(Regex.Replace(s ?? "", @"\((R|TM|tm|r)\)|®|™", ""), @"\s{2,}", " ").Trim();

    private static string Ru(double v, string fmt) => v.ToString(fmt, L.Culture);

    /// <summary>Кавычки вокруг названия: «…» по-русски, “…” по-английски.</summary>
    private static string Q(string s) => L.En ? "“" + s + "”" : "«" + s + "»";

    private static string GbText(double gb) => gb >= 10 || Math.Abs(gb - Math.Round(gb)) < 0.05 ? Ru(Math.Round(gb), "0") + " " + Fmt.GbUnit : Ru(gb, "0.0") + " " + Fmt.GbUnit;

    /// <summary>Объём накопителя как на коробке: 1 ТБ, 512 ГБ.</summary>
    private static string DiskSize(long bytes)
    {
        double gb = bytes / 1e9;
        if (gb >= 1000) return Ru(Math.Round(gb / 1000, gb >= 10000 ? 0 : 1), "0.#") + " " + L.T("ТБ");
        return Ru(Math.Round(gb), "0") + " " + Fmt.GbUnit;
    }

    /// <summary>Свободное место: «335 ГБ», «1,7 ТБ», «850 МБ».</summary>
    private static string FreeSize(long bytes)
    {
        double gb = bytes / Fmt.Gib;
        if (gb >= 1000) return Ru(gb / 1024, "0.0") + " " + L.T("ТБ");
        if (gb >= 10) return Ru(gb, "0") + " " + Fmt.GbUnit;
        return Fmt.Size(bytes);
    }

    private static string ShortMaker(string maker)
    {
        maker = Clean(maker);
        if (maker == "") return "";
        var map = new (string Key, string Name)[]
        {
            ("Micro-Star", "MSI"), ("ASUSTeK", "ASUS"), ("Gigabyte", "Gigabyte"), ("ASRock", "ASRock"), ("Hewlett", "HP"), ("HP", "HP"),
            ("Dell", "Dell"), ("LENOVO", "Lenovo"), ("Acer", "Acer"), ("BIOSTAR", "Biostar"), ("Huawei", "Huawei"), ("HONOR", "Honor"),
            ("Microsoft", "Microsoft"), ("Samsung", "Samsung"), ("Apple", "Apple"), ("Razer", "Razer"), ("Intel", "Intel"), ("Colorful", "Colorful"),
            ("MAXSUN", "Maxsun"), ("NZXT", "NZXT"), ("Xiaomi", "Xiaomi"), ("Machenike", "Machenike"), ("Thunderobot", "Thunderobot"), ("Gigabyte", "Gigabyte"),
        };
        foreach (var (k, n) in map) if (maker.StartsWith(k, StringComparison.OrdinalIgnoreCase)) return n;
        string first = maker.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)[0];
        return first.Length > 2 && first.All(c => !char.IsLower(c)) ? char.ToUpper(first[0]) + first[1..].ToLowerInvariant() : first;
    }

    // ───────────────────────────── 1. Процессор ─────────────────────────────

    private static string StepCpu(Ctx c)
    {
        var r = c.R;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            r.CpuName = (key?.GetValue("ProcessorNameString") as string ?? "").Trim();
            r.BaseMhz = Convert.ToInt32(key?.GetValue("~MHz") ?? 0);
        }
        catch { }
        r.Threads = Environment.ProcessorCount;

        var cpus = Arr(c.HwData, "Cpu").ToList();
        if (cpus.Count > 0)
        {
            var p = cpus[0];
            if (r.CpuName == "") r.CpuName = S(p, "Name");
            int cores = cpus.Sum(x => (int)N(x, "Cores")), threads = cpus.Sum(x => (int)N(x, "Threads"));
            if (cores > 0) r.Cores = cores;
            if (threads > 0) r.Threads = threads;
            if (N(p, "Mhz") > 0) r.BaseMhz = (int)N(p, "Mhz");
        }

        string name = CpuShort(r.CpuName);
        string counts = r.Cores > 0 ? $"{Fmt.Plural(r.Cores, "ядро", "ядра", "ядер")}, {Fmt.Plural(r.Threads, "поток", "потока", "потоков")}" : Fmt.Plural(r.Threads, "поток", "потока", "потоков");
        string clock = r.BaseMhz > 0 ? " · " + L.F("{0:0.0#} ГГц", r.BaseMhz / 1000.0) : "";
        c.Passport(L.T("Процессор"), name + "\n" + counts + clock);
        return r.Cores > 0 ? $"{name} · {r.Cores}/{r.Threads}" : name;
    }

    /// <summary>«12th Gen Intel(R) Core(TM) i5-12400F» → «Intel Core i5-12400F»; «AMD Ryzen 7 5800X3D 8-Core Processor» → «AMD Ryzen 7 5800X3D».</summary>
    public static string CpuShort(string name)
    {
        string s = Tidy(name);
        s = Regex.Replace(s, @"^\d+th Gen\s+", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+CPU\b", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s*@\s*[\d.]+\s*GHz", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+\d+-Core Processor", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+with Radeon.*$", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+Processor$", "", RegexOptions.IgnoreCase);
        return s.Trim() == "" ? L.T("процессор") : s.Trim();
    }

    // ───────────────────────────── 2. Видеокарта ─────────────────────────────

    private static string StepGpu(Ctx c)
    {
        var r = c.R;
        foreach (var g in Arr(c.HwData, "Gpu"))
        {
            string pnp = S(g, "Pnp");
            string name = Tidy(S(g, "Name"));
            if (name == "") continue;
            bool basic = name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || name.Contains("Базовый видеоадаптер", StringComparison.OrdinalIgnoreCase);
            // Виртуальные адаптеры (удалённый доступ, второй экран по сети) — не видеокарты.
            if (!basic && !pnp.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase)) continue;

            var gpu = new ScanGpu { Name = name, Pnp = pnp, Driver = S(g, "Driver"), DriverDate = Date(S(g, "Date")), Basic = basic };
            string ven = Regex.Match(pnp, @"VEN_([0-9A-F]{4})", RegexOptions.IgnoreCase).Groups[1].Value.ToUpperInvariant();
            gpu.Vendor = ven switch { "10DE" => "NVIDIA", "1002" or "1022" => "AMD", "8086" => "Intel", "5143" => "Qualcomm", _ => "" };
            gpu.VramBytes = VramFromRegistry(S(g, "Name"));
            if (gpu.VramBytes <= 0) gpu.VramBytes = (long)N(g, "Ram");
            gpu.DriverShort = gpu.Vendor == "NVIDIA" ? NvidiaVersion(gpu.Driver) : gpu.Driver;
            gpu.Discrete = gpu.Vendor switch
            {
                "NVIDIA" => true,
                "AMD" => !Regex.IsMatch(name, @"Radeon\s*(Graphics|Vega|\(?TM\)?\s*Graphics|\d{3,4}[MS]\b)|Vega \d+", RegexOptions.IgnoreCase),
                "Intel" => Regex.IsMatch(name, @"Arc.*\b[AB]\d{3}\b|Iris Xe MAX|DG1", RegexOptions.IgnoreCase),
                _ => false,
            };
            r.Gpus.Add(gpu);
        }
        r.Gpus = r.Gpus.OrderByDescending(x => x.Discrete).ThenByDescending(x => x.VramBytes).ToList();

        if (r.Gpus.Count == 0)
        {
            c.Passport(L.T("Видеокарта"), "");
            return L.T("не найдена");
        }

        var lines = new List<string>();
        foreach (var g in r.Gpus)
        {
            string vram = g.VramBytes >= 256L << 20 ? " · " + GbText(g.VramBytes / Fmt.Gib) : "";
            string kind = !g.Discrete && r.Gpus.Count > 1 ? " · " + L.T("встроенная") : "";
            string drv = g.Basic ? L.T("драйвер не установлен")
                : g.DriverDate > DateTime.MinValue ? L.F("драйвер {0} от {1:d}", g.DriverShort, g.DriverDate) : L.F("драйвер {0}", g.DriverShort);
            lines.Add(g.Name + vram + kind + "\n" + drv);
        }
        c.Passport(L.T("Видеокарта"), string.Join("\n", lines));

        var main = r.Gpus[0];
        if (r.Gpus.Any(g => g.Basic))
            c.Add(Severity.Bad, L.T("Не установлен драйвер видеокарты"),
                L.T("Windows работает через «Базовый видеоадаптер»: игры не запустятся или будут идти очень медленно. Скачайте драйвер на сайте производителя видеокарты или ноутбука."));
        else if (main.DriverDate > DateTime.MinValue)
        {
            int days = (int)(DateTime.Now - main.DriverDate).TotalDays;
            string where = main.Vendor switch
            {
                "NVIDIA" => L.T("Новый можно установить через NVIDIA App или с nvidia.com."),
                "AMD" => L.T("Новый можно установить через AMD Software: Adrenalin Edition или с amd.com."),
                "Intel" => L.T("Новый можно установить через Intel Driver & Support Assistant или с сайта производителя ноутбука."),
                _ => L.T("Новый можно скачать на сайте производителя видеокарты."),
            };
            if (days > 365)
                c.Add(Severity.Warn, L.T("Драйвер видеокарты старше года"),
                    L.F("Драйвер {0} от {1:d}. В новых версиях — исправления и оптимизации для свежих игр. {2}", main.Name, main.DriverDate, where));
            else if (days > 183)
                c.Add(Severity.Info, L.T("Драйверу видеокарты больше полугода"),
                    L.F("Драйвер {0} от {1:d}. Для новых игр его стоит обновить. {2}", main.Name, main.DriverDate, where));
        }

        string vr = main.VramBytes >= 256L << 20 ? " · " + GbText(main.VramBytes / Fmt.Gib) : "";
        return Regex.Replace(main.Name, @"^(NVIDIA|AMD|Intel)\s+", "") + vr;
    }

    /// <summary>Видеопамять из ключа класса видеоадаптеров: в WMI AdapterRAM ограничен 4 ГБ.</summary>
    private static long VramFromRegistry(string driverDesc)
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return 0;
            foreach (string sub in cls.GetSubKeyNames())
            {
                if (!Regex.IsMatch(sub, @"^\d{4}$")) continue;
                try
                {
                    using var k = cls.OpenSubKey(sub);
                    if (!string.Equals((k?.GetValue("DriverDesc") as string ?? "").Trim(), driverDesc.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                    object q = k.GetValue("HardwareInformation.qwMemorySize");
                    if (q is long l && l > 0) return l;
                    if (q is byte[] qb && qb.Length >= 8) return BitConverter.ToInt64(qb, 0);
                    object m = k.GetValue("HardwareInformation.MemorySize");
                    if (m is int i && i != 0) return (uint)i;
                    if (m is long ml && ml > 0) return ml;
                    if (m is byte[] mb && mb.Length >= 4) return mb.Length >= 8 ? BitConverter.ToInt64(mb, 0) : BitConverter.ToUInt32(mb, 0);
                }
                catch { }
            }
        }
        catch { }
        return 0;
    }

    /// <summary>«32.0.15.6094» → «560.94» (так NVIDIA называет версию драйвера).</summary>
    public static string NvidiaVersion(string v)
    {
        var parts = (v ?? "").Split('.');
        if (parts.Length < 4) return v ?? "";
        string digits = parts[2] + parts[3].PadLeft(4, '0');
        if (digits.Length < 5) return v;
        digits = digits[^5..];
        return digits[..3].TrimStart('0') + "." + digits[3..];
    }

    // ───────────────────────────── 3. Память ─────────────────────────────

    private static string StepRam(Ctx c)
    {
        var r = c.R;
        var hw = c.HwData;
        // Батарея — по Windows (BatteryInfo): ИБП у настольного компьютера Win32_Battery тоже показывает батареей,
        // и компьютер с ИБП назывался «ноутбуком», а замечания для него смягчались.
        bool battery = false;
        try { battery = BatteryInfo.Read().HasBattery; } catch { }
        r.IsLaptop = hw is JsonElement h && N(h, "PcType") == 2 || battery;

        foreach (var m in Arr(hw, "Ram"))
        {
            var mod = new ScanRamModule
            {
                Slot = S(m, "Slot"), Maker = Clean(S(m, "Maker")), Part = Clean(S(m, "Part")), Bytes = (long)N(m, "Bytes"),
                RatedMhz = (int)N(m, "Speed"), ConfiguredMhz = (int)N(m, "Conf"), Type = (int)N(m, "Type"), FormFactor = (int)N(m, "Form"),
            };
            if (mod.Bytes <= 0) continue;
            // Старые платы с DDR3 отдают настроенную частоту в МГц, а не в МТ/с.
            if (mod.Type <= 24 && mod.ConfiguredMhz > 0 && mod.RatedMhz > 0 && Math.Abs(mod.ConfiguredMhz * 2 - mod.RatedMhz) <= mod.RatedMhz * 0.03)
                mod.ConfiguredMhz *= 2;
            mod.PartMhz = PartSpeed(mod.Part);
            r.RamModules.Add(mod);
        }

        var (osTotal, osAvail) = Metrics.ReadRam();
        r.RamTotalGb = r.RamModules.Count > 0 ? r.RamModules.Sum(m => m.Bytes) / Fmt.Gib : Math.Round(osTotal);
        int type = r.RamModules.Select(m => m.Type).FirstOrDefault(t => t > 0);
        r.RamType = type switch { 20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5", _ => "" };
        bool lp = r.RamType.StartsWith("LP");
        int conf = r.RamModules.Where(m => m.ConfiguredMhz > 0).Select(m => m.ConfiguredMhz).DefaultIfEmpty(0).Min();
        int rated = r.RamModules.Select(m => m.RatedMhz).DefaultIfEmpty(0).Max();
        int partMhz = r.RamModules.Select(m => m.PartMhz).DefaultIfEmpty(0).Max();
        int evidence = Math.Max(rated, partMhz);

        string layout = r.RamModules.Count == 0 ? ""
            : r.RamModules.GroupBy(m => Math.Round(m.Bytes / Fmt.Gib)).Count() == 1
                ? $" · {r.RamModules.Count} × {GbText(r.RamModules[0].Bytes / Fmt.Gib)}"
                : " · " + string.Join(" + ", r.RamModules.Select(m => GbText(m.Bytes / Fmt.Gib)));
        string speed = conf > 0 ? L.F("{0} МГц", conf) + (evidence > conf + 100 ? " " + L.F("(планки рассчитаны на {0})", evidence) : "") : "";
        string makers = string.Join(", ", r.RamModules.Select(m => m.Maker).Where(x => x != "" && !Regex.IsMatch(x, @"^[0-9A-F]{4,}$")).Distinct().Take(2));
        c.Passport(L.T("Память"), $"{GbText(r.RamTotalGb)} {r.RamType}{layout}".Trim() + "\n" + string.Join(" · ", new[] { speed, makers }.Where(x => x != "")));

        // Объём
        double gbTotal = Math.Round(r.RamTotalGb);
        if (gbTotal < 8)
            c.Add(Severity.Bad, L.F("Мало памяти: {0}", GbText(r.RamTotalGb)),
                L.T("Современным играм нужно от 16 ГБ. С таким объёмом Windows постоянно использует файл подкачки — это рывки и долгие загрузки."));
        else if (gbTotal < 15)
            c.Add(Severity.Warn, L.F("Памяти {0} — меньше, чем просят игры", GbText(r.RamTotalGb)),
                L.T("Современным играм нужно 16 ГБ. Пока памяти мало, перед игрой закрывайте браузер и другие тяжёлые программы."));

        // Каналы
        if (!lp && r.RamModules.Count == 1)
            c.Add(Severity.Warn, L.T("Одна планка памяти"),
                L.T("Память работает в одноканальном режиме, и в играх, где много считает процессор, это заметно снижает FPS. Вторая такая же планка включит двухканальный режим."));
        else if (!lp && r.RamModules.Count >= 2 && r.RamModules.Select(m => m.Bytes).Distinct().Count() > 1)
            c.Add(Severity.Info, L.T("Планки памяти разного объёма"),
                L.T("Часть памяти может работать в одноканальном режиме. Лучше всего ставить одинаковые планки парами."));

        // XMP / EXPO
        if (!lp && conf > 0 && evidence >= conf + 200)
            c.Add(Severity.Warn, L.T("Профиль XMP/EXPO не включён"),
                L.F("Планки рассчитаны на {0} МГц, а работают на {1} МГц. Включите в BIOS профиль XMP (Intel) или EXPO/DOCP (AMD): память заработает на своей частоте, если её поддерживают плата и процессор.", evidence, conf));
        else if (!lp && !r.IsLaptop && conf > 0 && ((r.RamType == "DDR4" && conf <= 2666) || (r.RamType == "DDR5" && conf <= 4800)))
            c.Add(Severity.Info, L.F("Память работает на базовой частоте {0} МГц", conf),
                L.F("Это стандартная частота {0} без разгона. Если на планках или в их характеристиках указано больше (например, {1}), включите в BIOS профиль XMP или EXPO.",
                    r.RamType, r.RamType == "DDR4" ? L.T("3200 или 3600") : "6000"));

        string sum = $"{GbText(r.RamTotalGb)} {r.RamType}".Trim();
        return conf > 0 ? sum + " · " + L.F("{0} МГц", conf) : sum;
    }

    /// <summary>Частота из маркировки планки (у популярных производителей она зашита в номер). 0 — не распознано.</summary>
    public static int PartSpeed(string part)
    {
        if (string.IsNullOrWhiteSpace(part)) return 0;
        part = part.Trim().ToUpperInvariant();
        Match m;
        if ((m = Regex.Match(part, @"^F[345]-(\d{4})C")).Success) return int.Parse(m.Groups[1].Value);              // G.Skill F4-3600C16
        if ((m = Regex.Match(part, @"^CM\w*?X[345]M\d[A-Z](\d{4})C\d")).Success) return int.Parse(m.Groups[1].Value); // Corsair CMK16GX4M2B3200C16
        if ((m = Regex.Match(part, @"^(?:KF|HX)[345](\d{2})C\d")).Success) return int.Parse(m.Groups[1].Value) * 100; // Kingston KF432C16, HX426C16
        if ((m = Regex.Match(part, @"^(?:KF|HX)(\d{4})C\d")).Success) return int.Parse(m.Groups[1].Value);            // Kingston KF3200C16D4/8GX
        if ((m = Regex.Match(part, @"^BL\d+G(\d{2})C\d")).Success) return int.Parse(m.Groups[1].Value) * 100;        // Crucial Ballistix BL8G32C16
        if ((m = Regex.Match(part, @"^T[A-Z0-9]+?(\d{4})HC\d")).Success) return int.Parse(m.Groups[1].Value);         // TeamGroup …3200HC16…
        return 0;
    }

    // ───────────────────────────── 4. Материнская плата ─────────────────────────────

    private static string StepBoard(Ctx c)
    {
        var r = c.R;
        var hw = c.HwData;
        if (hw is not JsonElement h) { c.Passport(L.T("Плата"), ""); return L.T("нет данных"); }

        string board = Regex.Replace(Clean(S(h, "Board")), @"\s*\(MS-\w+\)\s*$", "");
        string boardMaker = ShortMaker(S(h, "BoardMaker"));
        string csMaker = ShortMaker(S(h, "CsMaker")), csModel = Clean(S(h, "CsModel"));
        string name = r.IsLaptop && csModel != ""
            ? L.F("Ноутбук {0}", csModel.StartsWith(csMaker, StringComparison.OrdinalIgnoreCase) ? csModel : (csMaker + " " + csModel).Trim())
            : (boardMaker + " " + board).Trim();
        if (name == "" && csModel != "") name = (csMaker + " " + csModel).Trim();
        r.BoardName = name;
        r.BiosVersion = Clean(S(h, "Bios"));
        r.BiosDate = Date(S(h, "BiosDate"));
        string bios = r.BiosVersion == "" ? "" : r.BiosDate > DateTime.MinValue ? L.F("BIOS {0} от {1:d}", r.BiosVersion, r.BiosDate) : "BIOS " + r.BiosVersion;
        c.Passport(L.T("Плата"), string.Join("\n", new[] { name, bios }.Where(x => x != "")));

        if (r.BiosDate > DateTime.MinValue && (DateTime.Now - r.BiosDate).TotalDays > 730)
            c.Add(Severity.Info, L.F("BIOS от {0:MM.yyyy}", r.BiosDate),
                L.F("{0} мог выпустить новую версию: обычно в ней лучше совместимость с памятью, стабильность и защита. Обновляйте строго по инструкции с его сайта и не выключайте компьютер во время обновления.",
                    r.IsLaptop ? L.T("Производитель ноутбука") : L.T("Производитель платы")));

        return name == "" ? L.T("нет данных") : name;
    }

    // ───────────────────────────── 5. Накопители ─────────────────────────────

    private static string StepDisks(Ctx c)
    {
        var r = c.R;
        string sysLetter = (Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\")[..1].ToUpperInvariant();

        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                r.Volumes.Add(new ScanVolume { Letter = d.Name[..1].ToUpperInvariant(), Total = d.TotalSize, Free = d.AvailableFreeSpace, IsSystem = d.Name[..1].Equals(sysLetter, StringComparison.OrdinalIgnoreCase) });
            }
            catch { }
        }

        var st = c.StorageData;
        string sysDisk = st is JsonElement s0 && s0.TryGetProperty("Sys", out var sv) && sv.ValueKind == JsonValueKind.Number ? sv.GetInt32().ToString() : "";
        var parts = Arr(st, "Parts").Select(p => (Letter: S(p, "Letter").ToUpperInvariant(), Disk: ((int)N(p, "Disk")).ToString())).ToList();
        foreach (var e in Arr(st, "Disks"))
        {
            var disk = new ScanDisk
            {
                Number = S(e, "Id"), Name = Tidy(S(e, "Name")), Media = S(e, "Media"), Bus = S(e, "Bus"), Health = S(e, "Health"),
                Bytes = (long)N(e, "Bytes"), Temp = Opt(e, "Temp"), Wear = Opt(e, "Wear"), Hours = Opt(e, "Hours"), ReadErrors = Opt(e, "ReadErrors"),
            };
            disk.IsSystem = disk.Number != "" && disk.Number == sysDisk;
            disk.Letters = parts.Where(p => p.Disk == disk.Number && p.Letter != "").Select(p => p.Letter + ":").ToList();
            bool ssd = disk.Media.Equals("SSD", StringComparison.OrdinalIgnoreCase) || disk.Bus.Equals("NVMe", StringComparison.OrdinalIgnoreCase);
            bool hdd = disk.Media.Equals("HDD", StringComparison.OrdinalIgnoreCase);
            if (!ssd && !hdd)
            {
                // Тип не указан (часто у старых SATA-дисков): по скорости вращения и по модели.
                double.TryParse(S(e, "Spindle"), NumberStyles.Float, CultureInfo.InvariantCulture, out double spindle);
                if (spindle > 0 && spindle < 100000) hdd = true;
                else if (Regex.IsMatch(disk.Name, @"\bSSD\b|NVMe", RegexOptions.IgnoreCase)) ssd = true;
                else if (Regex.IsMatch(disk.Name, @"^(WDC\s+WD|ST\d{3,}|HGST|HDS|HDT|Hitachi|TOSHIBA\s+(DT|MQ|HDW|MG)|SAMSUNG\s+HD)", RegexOptions.IgnoreCase)) hdd = true;
            }
            disk.Kind = disk.Bus.ToUpperInvariant() switch
            {
                "NVME" => "NVMe SSD",
                "USB" => L.T("USB-диск"),
                "SATA" or "ATA" => ssd ? "SATA SSD" : hdd ? "HDD" : "SATA",
                "FILE BACKED VIRTUAL" => L.T("виртуальный диск"),
                "RAID" => ssd ? "SSD (RAID)" : hdd ? "HDD (RAID)" : "RAID",
                _ => ssd ? "SSD" : hdd ? "HDD" : L.T("диск"),
            };
            r.Disks.Add(disk);
        }
        r.Disks = r.Disks.OrderByDescending(d => d.IsSystem).ThenBy(d => d.Bus == "USB").ThenBy(d => d.Number).ToList();

        // Одна строка на диск: тип, объём и свободное место его томов. Модели — в отчёте (Disks).
        string Free(ScanVolume v) => L.F("{0}: свободно {1}", v.Letter, FreeSize(v.Free));
        var lines = new List<string>();
        var shown = new HashSet<string>();
        foreach (var d in r.Disks)
        {
            var vols = r.Volumes.Where(v => d.Letters.Contains(v.Letter + ":")).ToList();
            foreach (var v in vols) shown.Add(v.Letter);
            lines.Add($"{d.Kind} {DiskSize(d.Bytes)}" + (vols.Count > 0 ? " · " + string.Join(", ", vols.Select(Free)) : ""));
        }
        foreach (var v in r.Volumes.Where(v => !shown.Contains(v.Letter)).OrderBy(v => v.Letter)) lines.Add(Free(v));
        c.Passport(L.T("Диски"), string.Join("\n", lines));

        // Система на жёстком диске
        var sys = r.Disks.FirstOrDefault(d => d.IsSystem);
        if (sys != null && sys.Kind.StartsWith("HDD"))
            c.Add(Severity.Warn, L.T("Windows стоит на жёстком диске (HDD)"),
                L.T("С жёсткого диска Windows и игры загружаются в несколько раз дольше, чем с SSD, а в открытых мирах бывают подгрузки и рывки. Самое заметное улучшение для этого компьютера — SSD под систему и игры."));

        // Место
        foreach (var v in r.Volumes.OrderByDescending(v => v.IsSystem))
        {
            string text = L.F("Свободно {0} из {1}.", Fmt.Size(v.Free), Fmt.Size(v.Total));
            if (v.IsSystem && (v.FreePct < 10 || v.Free < 10L << 30))
                c.Add(Severity.Bad, L.F("На системном диске {0}: почти нет места", v.Letter),
                    text + " " + L.T("Windows и играм нужно место под обновления, файл подкачки и кэш шейдеров. Освободите хотя бы 15 %."), 5);
            else if (v.IsSystem && v.FreePct < 15)
                c.Add(Severity.Warn, L.F("На системном диске {0}: мало места", v.Letter), text + " " + L.T("Когда свободно меньше 10 %, Windows начинает работать медленнее."), 5);
            else if (!v.IsSystem && v.FreePct < 10)
                c.Add(Severity.Warn, L.F("Диск {0}: заполнен на {1:0} %", v.Letter, 100 - v.FreePct), text + " " + L.T("Игры на почти заполненном диске могут не обновиться: обновлениям нужно свободное место."), -1);
        }

        // Состояние
        foreach (var d in r.Disks)
        {
            if (d.Health != "" && !d.Health.Equals("Healthy", StringComparison.OrdinalIgnoreCase))
                c.Add(Severity.Bad, L.F("Диск {0} сообщает о неисправности", d.Name),
                    L.F("Состояние — «{0}». Скопируйте важные данные на другой диск как можно скорее.", d.Health), 4);
            else if (d.ReadErrors > 0)
                c.Add(Severity.Warn, L.F("Диск {0}: ошибки чтения", d.Name),
                    L.F("Неисправленных ошибок чтения: {0:0}. Не храните на нём единственную копию важного.", d.ReadErrors), 4);
            else if (d.Wear >= 80)
                c.Add(Severity.Warn, L.F("Диск {0} изношен на {1:0} %", d.Name, d.Wear), L.T("Ресурс записи подходит к концу. Сделайте копию важных данных и присмотрите замену."), 4);
        }

        var sysVol = r.Volumes.FirstOrDefault(v => v.IsSystem);
        string count = r.Disks.Count > 0 ? Fmt.Plural(r.Disks.Count, "диск", "диска", "дисков") : Fmt.Plural(r.Volumes.Count, "том", "тома", "томов");
        return sysVol != null ? count + " · " + L.F("{0}: свободно {1:0} %", sysVol.Letter, sysVol.FreePct) : count;
    }

    // ───────────────────────────── 6. Монитор ─────────────────────────────

    private static string StepMonitors(Ctx c)
    {
        var r = c.R;
        var wmiNames = Arr(c.HwData, "Mon").Select(m => (Name: S(m, "Name"), Inst: S(m, "Inst"))).Where(m => m.Name != "").ToList();

        for (uint i = 0; i < 16; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & 0x1) == 0 || (dd.StateFlags & 0x8) != 0) continue;   // не на рабочем столе или зеркалирующий драйвер

            var cur = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(dd.DeviceName, -1, ref cur)) continue;
            var mon = new ScanMonitor { Device = dd.DeviceName, Width = cur.dmPelsWidth, Height = cur.dmPelsHeight, Hz = cur.dmDisplayFrequency, Primary = (dd.StateFlags & 0x4) != 0 };
            mon.MaxHz = mon.Hz; mon.NativeWidth = mon.Width; mon.NativeHeight = mon.Height;
            for (int n = 0; n < 4000; n++)
            {
                var m = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                if (!EnumDisplaySettings(dd.DeviceName, n, ref m)) break;
                if ((m.dmDisplayFlags & 0x2) != 0) continue;   // чересстрочные режимы
                if (m.dmPelsWidth == mon.Width && m.dmPelsHeight == mon.Height && m.dmDisplayFrequency > mon.MaxHz) mon.MaxHz = m.dmDisplayFrequency;
                if ((long)m.dmPelsWidth * m.dmPelsHeight > (long)mon.NativeWidth * mon.NativeHeight) { mon.NativeWidth = m.dmPelsWidth; mon.NativeHeight = m.dmPelsHeight; }
            }

            // Название из EDID: путь интерфейса монитора совпадает с InstanceName в WmiMonitorID.
            var md = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (EnumDisplayDevices(dd.DeviceName, 0, ref md, 1))
            {
                string id = md.DeviceID ?? "";
                if (id.StartsWith(@"\\?\")) id = id[4..];
                int cut = id.IndexOf("#{", StringComparison.Ordinal);
                if (cut > 0) id = id[..cut];
                id = id.Replace('#', '\\');
                var hit = wmiNames.FirstOrDefault(w => id.Length > 8 && w.Inst.StartsWith(id, StringComparison.OrdinalIgnoreCase));
                if (hit.Name != null) mon.Name = hit.Name.Trim();
                else if (!string.IsNullOrWhiteSpace(md.DeviceString) && !Regex.IsMatch(md.DeviceString, "PnP|Generic|Универсальный", RegexOptions.IgnoreCase))
                    mon.Name = md.DeviceString.Trim();
            }
            r.Monitors.Add(mon);
        }
        if (r.Monitors.Count == 1 && r.Monitors[0].Name == "" && wmiNames.Count == 1) r.Monitors[0].Name = wmiNames[0].Name;
        r.Monitors = r.Monitors.OrderByDescending(m => m.Primary).ToList();
        for (int i = 0; i < r.Monitors.Count; i++)
            if (r.Monitors[i].Name == "") r.Monitors[i].Name = r.Monitors.Count == 1 ? L.T("Монитор") : L.F("Монитор {0}", i + 1);

        c.Passport(L.T("Монитор"), string.Join("\n", r.Monitors.Select(m =>
            $"{m.Name} · {m.Width} × {m.Height} · " + L.F("{0} Гц", m.Hz) + (m.MaxHz > m.Hz ? " " + L.F("(можно {0})", m.MaxHz) : ""))));

        foreach (var m in r.Monitors)
        {
            if (m.MaxHz - m.Hz >= 15)
            {
                var level = m.Hz <= 75 && m.MaxHz >= 100 ? Severity.Bad : Severity.Warn;
                c.Add(level, L.F("Монитор работает на {0} Гц вместо {1}", m.Hz, m.MaxHz),
                    L.F("{0} поддерживает {1} Гц при {2} × {3}, а Windows выставила {4} Гц, и картинка в играх менее плавная. Как исправить: Параметры → Система → Дисплей → Расширенные параметры дисплея → Частота обновления → {1} Гц.", m.Name, m.MaxHz, m.Width, m.Height, m.Hz));
            }
            if ((long)m.Width * m.Height < (long)m.NativeWidth * m.NativeHeight && m.NativeWidth > 0)
                c.Add(Severity.Info, L.F("Разрешение ниже родного: {0} × {1}", m.Width, m.Height),
                    L.F("{0} поддерживает {1} × {2}. Если меньшее разрешение выбрано не намеренно, верните родное: Параметры → Система → Дисплей. Картинка станет чётче.", m.Name, m.NativeWidth, m.NativeHeight));
        }

        var p = r.Monitors.FirstOrDefault();
        return p == null ? L.T("не найден") : $"{p.Width}×{p.Height} · " + L.F("{0} Гц", p.Hz) + (r.Monitors.Count > 1 ? " · " + L.F("ещё {0}", r.Monitors.Count - 1) : "");
    }

    // ───────────────────────────── 7. Windows ─────────────────────────────

    private static string StepWindows(Ctx c)
    {
        var r = c.R;
        string caption = "", build = "", display = "";
        int ubr = 0;
        if (c.HwData is JsonElement h) { caption = S(h, "Os"); build = S(h, "Build"); }
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            display = k?.GetValue("DisplayVersion") as string ?? "";
            ubr = k?.GetValue("UBR") is int u ? u : 0;
            if (build == "") build = k?.GetValue("CurrentBuild") as string ?? "";
            if (caption == "") caption = k?.GetValue("ProductName") as string ?? "";
        }
        catch { }
        caption = Regex.Replace(caption, @"^(Microsoft|Майкрософт)\s+", "", RegexOptions.IgnoreCase);
        int.TryParse(build, out int buildNo);
        if (buildNo >= 22000) caption = caption.Replace("Windows 10", "Windows 11");
        r.Os = (caption + " " + display).Trim();
        r.OsBuild = build + (ubr > 0 ? "." + ubr : "");
        r.Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        string scheme = ActivePlan(out Guid planGuid);
        r.PowerPlan = scheme;

        var tweaks = Ctx.Wait(c.Tweaks, 40000) ?? new List<TweakItem>();
        bool? Tw(string part) => tweaks.FirstOrDefault(t => t.Title.Contains(part, StringComparison.OrdinalIgnoreCase))?.Applied;
        string OnOff(bool? v, bool invert = false) => v == null ? "?" : (v.Value ^ invert) ? L.T("вкл.") : L.T("выкл.");
        var gameMode = Tw("Игровой режим"); var dvrOff = Tw("Фоновая запись"); var hags = Tw("HAGS");
        c.Passport("Windows", $"{r.Os} ({r.OsBuild})\n" + L.F("питание: «{0}»", scheme == "" ? "?" : scheme) + "\n" +
                              L.F("игровой режим {0} · HAGS {1}", OnOff(gameMode), OnOff(hags)) + "\n" + L.F("запись игр в фоне {0}", OnOff(dvrOff, true)));

        // Схема питания
        if (planGuid == new Guid("a1841308-3541-4fab-bc81-f71556f20b4a"))
            c.AddSection(r.IsLaptop ? Severity.Info : Severity.Warn, L.T("Схема питания «Экономия энергии»"),
                L.T("Процессор и видеокарта ограничены ради экономии энергии. В разделе «Питание» можно включить полную скорость на время игры или насовсем."), "Питание");

        // Игровые настройки Windows
        var checkedItems = tweaks.Where(t => t.Applied != null).ToList();
        var missing = checkedItems.Where(t => t.Applied == false).ToList();
        if (missing.Count > 0)
        {
            string names = string.Join(", ", missing.Take(3).Select(t => Q(TweakName(t.Title)))) + (missing.Count > 3 ? " " + L.F("и ещё {0}", missing.Count - 3) : "");
            c.Add(missing.Count >= 3 ? Severity.Warn : Severity.Info, L.F("Игровые настройки Windows: {0} из {1}", checkedItems.Count - missing.Count, checkedItems.Count),
                L.F("Не включены: {0}. Включить их можно на вкладке «Оптимизация», там же их можно и выключить.", names), 6);
        }

        if (PendingReboot())
            c.Add(Severity.Warn, L.T("Windows ждёт перезагрузки"), L.T("Обновление или установка закончится только после перезагрузки."));
        if (r.Uptime.TotalDays >= 7)
            c.Add(Severity.Info, L.F("Компьютер не перезагружался {0}", Fmt.Plural((int)r.Uptime.TotalDays, "день", "дня", "дней")),
                L.T("Перезагрузка освобождает память, которую не вернули программы, и устанавливает отложенные обновления."));
        if (buildNo > 0 && buildNo < 22000)
            c.Add(Severity.Info, L.T("Windows 10 больше не обновляется"),
                L.T("Обычная поддержка Windows 10 закончилась в октябре 2025 года. Если компьютер подходит, стоит перейти на Windows 11."));
        if (FastStartup())
            c.Add(Severity.Info, L.T("Включён быстрый запуск Windows"),
                L.T("При «Завершении работы» Windows не выключается начисто, а сохраняет своё ядро на диск. Если после включения что-то работает странно, выбирайте «Перезагрузку» — она всегда полная."));
        if (!r.IsAdmin)
            c.Add(Severity.Info, L.T("Осмотр без прав администратора"),
                L.T("Состояние дисков (износ, ошибки чтения) и часть журналов доступны только с правами администратора."));

        return r.Os == "" ? "Windows" : r.Os;
    }

    /// <summary>Название игровой настройки для замечания: по-русски без «включён/выключено», по-английски — перевод названия.</summary>
    private static string TweakName(string title) => L.En ? L.T(title) : ShortTweak(title);

    private static string ShortTweak(string title) => Regex.Replace(title, @"\s+(включён|включено|выключена|выключено|выключены|выключен)$", "", RegexOptions.IgnoreCase);

    private static bool PendingReboot()
    {
        try
        {
            using var a = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            using var b = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            return a != null || b != null;
        }
        catch { return false; }
    }

    private static bool FastStartup()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
            return k?.GetValue("HiberbootEnabled") is int v && v == 1;
        }
        catch { return false; }
    }

    /// <summary>Имя активной схемы питания (на языке системы).</summary>
    private static string ActivePlan(out Guid guid)
    {
        guid = Guid.Empty;
        IntPtr ptr = IntPtr.Zero;
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out ptr) != 0 || ptr == IntPtr.Zero) return "";
            guid = Marshal.PtrToStructure<Guid>(ptr);
            var buf = new byte[2048];
            uint size = (uint)buf.Length;
            if (PowerReadFriendlyName(IntPtr.Zero, ref guid, IntPtr.Zero, IntPtr.Zero, buf, ref size) != 0) return "";
            return Encoding.Unicode.GetString(buf, 0, (int)Math.Min(size, (uint)buf.Length)).TrimEnd('\0').Trim();
        }
        catch { return ""; }
        finally { if (ptr != IntPtr.Zero) LocalFree(ptr); }
    }

    // ───────────────────────────── 8. Игры ─────────────────────────────

    private static string StepGames(Ctx c)
    {
        var r = c.R;
        var games = GameLibrary.Load();
        r.Games = games.Select(g => g.Title).ToList();
        List<GameTune> tunes = new();
        try { tunes = GameTuning.Scan(games, new Dictionary<string, GameProfile>()); } catch { }
        var withTips = tunes.Where(t => t.CanEdit && t.Changes.Count > 0).ToList();
        r.GamesWithTips = withTips.Count;

        string list = r.Games.Count == 0 ? L.T("не найдены (ищу в Steam, Epic Games, Riot, Astrum Play)")
            : Fmt.Plural(r.Games.Count, "игра", "игры", "игр") + ": " + string.Join(", ", r.Games.Take(3)) + (r.Games.Count > 3 ? " " + L.F("и ещё {0}", r.Games.Count - 3) : "");
        c.Passport(L.T("Игры"), list + (withTips.Count > 0 ? "\n" + L.F("рекомендации по графике — для {0}", GamesFor(withTips.Count)) : ""));

        if (withTips.Count > 0)
            c.Add(Severity.Info, L.F("Настройки графики: есть рекомендации для {0}", GamesFor(withTips.Count)),
                L.F("{0}. Что поменять и почему — на вкладке «Оптимизация»; перед изменением программа сохраняет копию файла настроек.",
                    string.Join(", ", withTips.Take(3).Select(t => t.Game)) + (withTips.Count > 3 ? " " + L.F("и ещё {0}", withTips.Count - 3) : "")), 6);

        return r.Games.Count == 0 ? L.T("игры не найдены") : Fmt.Plural(r.Games.Count, "игра", "игры", "игр");
    }

    /// <summary>«для 3 игр»: по-русски родительный падеж, по-английски обычное «3 games».</summary>
    private static string GamesFor(int n) => L.En ? Fmt.Plural(n, "игра", "игры", "игр") : Fmt.Plural(n, "игры", "игр", "игр");

    // ───────────────────────────── 9. Программы ─────────────────────────────

    private static readonly HashSet<string> SystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle", "System", "Registry", "Memory Compression", "Secure System", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "LsaIso",
        "svchost", "dwm", "fontdrvhost", "spoolsv", "WUDFHost", "audiodg", "conhost", "sihost", "taskhostw", "RuntimeBroker", "dllhost", "ctfmon",
        "SearchIndexer", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost", "MoUsoCoreWorker", "TiWorker", "TrustedInstaller",
        "WmiPrvSE", "powershell", "PulsePC", "vmmem", "vmmemWSL",
        // «Безопасность Windows» и антивирус не трогаем и не читаем.
        "MsMpEng", "NisSrv", "MpDefenderCoreService", "MpCmdRun", "MsSense", "SenseNdr", "SecurityHealthService", "SecurityHealthSystray",
        "SecurityHealthHost", "SecHealthUI", "smartscreen", "SgrmBroker",
    };

    private static string StepPrograms(Ctx c)
    {
        var r = c.R;
        r.ProgramsInstalled = InstalledPrograms();
        int startup = 0;
        try { startup = StartupManager.Load().Count(i => i.Enabled && !i.Locked); } catch { }
        r.StartupEnabled = startup;

        // Кто сейчас держит больше всего памяти (по имени процесса, все экземпляры вместе).
        var byName = new Dictionary<string, (long Bytes, int Pid)>(StringComparer.OrdinalIgnoreCase);
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    string name = p.ProcessName;
                    if (p.Id == self || p.Id <= 4 || SystemProcesses.Contains(name)) continue;
                    long ws = p.WorkingSet64;
                    byName[name] = byName.TryGetValue(name, out var cur) ? (cur.Bytes + ws, cur.Pid) : (ws, p.Id);
                }
                catch { }
            }
        }
        foreach (var (name, v) in byName.OrderByDescending(x => x.Value.Bytes).Take(5))
            r.TopMemory.Add(new ScanProcess { Name = Describe(name, v.Pid), Bytes = v.Bytes });

        string top = r.TopMemory.Count > 0 ? "\n" + L.F("больше всего памяти: {0}", string.Join(", ", r.TopMemory.Take(3).Select(t => $"{t.Name} {Fmt.Size(t.Bytes)}"))) : "";
        c.Passport(L.T("Программы"), L.F("установлено {0} · в автозагрузке {1}", r.ProgramsInstalled, startup) + top);

        if (startup > 8)
            c.Add(Severity.Warn, L.F("В автозагрузке {0}", Fmt.Plural(startup, "программа", "программы", "программ")),
                L.T("Все они запускаются вместе с Windows: загрузка дольше, а в игре они занимают память и процессор. Выключить лишнее можно на вкладке «Система», раздел «Автозагрузка»."), 7);

        var (totalGb, availGb) = Metrics.ReadRam();
        if (totalGb > 0 && availGb / totalGb < 0.25 && r.TopMemory.Count > 0)
            c.Add(Severity.Info, L.F("Сейчас свободно {0} памяти из {1}", Fmt.Gb(availGb), Fmt.Gb(totalGb)),
                L.F("Больше всего занимают: {0}. Перед игрой их стоит закрыть.", string.Join(", ", r.TopMemory.Take(3).Select(t => $"{t.Name} ({Fmt.Size(t.Bytes)})"))));

        return startup > 8 ? L.F("{0} · в автозагрузке {1}", r.ProgramsInstalled, startup) : Fmt.Plural(r.ProgramsInstalled, "программа", "программы", "программ");
    }

    private static string Describe(string processName, int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            string file = p.MainModule?.FileName;
            if (!string.IsNullOrEmpty(file))
            {
                string desc = FileVersionInfo.GetVersionInfo(file).FileDescription?.Trim();
                if (!string.IsNullOrEmpty(desc) && desc.Length <= 40) return desc;
            }
        }
        catch { }
        return processName;
    }

    /// <summary>Установленные программы, как в «Установке и удалении программ»: без системных компонентов и обновлений.</summary>
    private static int InstalledPrograms()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Read(RegistryHive hive, RegistryView view)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var un = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (un == null) return;
                foreach (string sub in un.GetSubKeyNames())
                {
                    try
                    {
                        using var k = un.OpenSubKey(sub);
                        string name = (k?.GetValue("DisplayName") as string)?.Trim();
                        if (string.IsNullOrEmpty(name)) continue;
                        if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                        if (k.GetValue("ParentKeyName") != null) continue;
                        string rel = k.GetValue("ReleaseType") as string ?? "";
                        if (rel.Contains("Update", StringComparison.OrdinalIgnoreCase) || rel.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)) continue;
                        names.Add(name);
                    }
                    catch { }
                }
            }
            catch { }
        }
        Read(RegistryHive.LocalMachine, RegistryView.Registry64);
        Read(RegistryHive.LocalMachine, RegistryView.Registry32);
        Read(RegistryHive.CurrentUser, RegistryView.Registry64);
        return names.Count;
    }

    // ───────────────────────────── 10. Сеть ─────────────────────────────

    private static string StepNet(Ctx c)
    {
        var r = c.R;
        var n = Ctx.Wait(c.Net, 20000) ?? new ScanNet();
        r.Net = n;
        if (!n.Connected)
        {
            c.Passport(L.T("Сеть"), L.T("нет подключения к сети"));
            c.Add(Severity.Warn, L.T("Нет подключения к сети"), L.T("Компьютер не подключён к роутеру. Сетевые проверки пропущены."), 3);
            return L.T("нет подключения");
        }

        string speed = n.SpeedBps <= 0 ? "" : n.SpeedBps >= 1_000_000_000 ? " · " + L.F("{0:0.#} Гбит/с", n.SpeedBps / 1e9) : " · " + L.F("{0:0} Мбит/с", n.SpeedBps / 1e6);
        string Ms(double v) => double.IsNaN(v) ? L.T("нет ответа") : L.F("{0:0} мс", v);
        double jitMax = new[] { n.RouterJitter, n.InternetJitter }.Where(x => !double.IsNaN(x)).DefaultIfEmpty(double.NaN).Max();
        double lossMax = new[] { n.RouterLoss, n.InternetLoss }.Where(x => !double.IsNaN(x)).DefaultIfEmpty(0).Max();
        c.Passport(L.T("Сеть"), (n.WiFi ? "Wi-Fi" : L.T("кабель")) + speed + "\n" +
                          L.F("пинг: роутер {0}, {1} — {2}", Ms(n.RouterAvg), n.InternetHost, Ms(n.InternetAvg)) + "\n" +
                          (double.IsNaN(jitMax) ? "" : jitMax < 1 ? L.T("разброс меньше 1 мс, ") : L.F("разброс до {0:0} мс, ", jitMax)) + (lossMax > 0 ? L.F("потери {0:0} %", lossMax) : L.T("потерь нет")));

        if (n.WiFi)
            c.Add(Severity.Info, L.T("Подключение по Wi-Fi"),
                L.T("Для игр лучше кабель: по Wi-Fi пинг скачет, а пакеты теряются от помех и соседних сетей. Если кабель протянуть нельзя — держите роутер поближе и подключайтесь на 5 ГГц."), 3);
        else if (n.SpeedBps > 0 && n.SpeedBps <= 100_000_000 && Regex.IsMatch(n.Adapter, @"GbE|Gigabit|2\.5G|I21\d|I22\d|E2[0-9]00|E3100|Killer|1G", RegexOptions.IgnoreCase))
            c.Add(Severity.Info, L.T("Сетевая карта работает на 100 Мбит/с"),
                L.T("Карта гигабитная, а соединение установилось на 100 Мбит/с. Обычно виноват кабель (нужен с восемью жилами, категории 5e или 6) или порт роутера."), 3);

        double rl = double.IsNaN(n.RouterLoss) ? 0 : n.RouterLoss, il = double.IsNaN(n.InternetLoss) ? 0 : n.InternetLoss;
        if (!double.IsNaN(n.RouterAvg) && rl >= 5)
            c.Add(Severity.Bad, L.F("Потери до роутера: {0:0} %", rl),
                L.T("Пакеты теряются уже между компьютером и роутером — в играх это «телепорты» и запоздалые действия. Проверьте кабель, а для Wi-Fi — расстояние и помехи."), 3);
        else if (!double.IsNaN(n.InternetAvg) && il >= 5)
            c.Add(Severity.Bad, L.F("Потери до интернета: {0:0} %", il),
                L.T("До роутера всё в порядке, пакеты теряются дальше — у провайдера. Если это повторяется, напишите провайдеру."), 3);
        if (!double.IsNaN(n.RouterJitter) && n.RouterJitter > (n.WiFi ? 15 : 5))
            c.Add(Severity.Warn, L.F("Пинг до роутера скачет на {0:0} мс", n.RouterJitter),
                n.WiFi ? L.T("Wi-Fi ловит помехи: стены, соседние сети, микроволновка. С кабелем этой проблемы нет.") : L.T("Для кабеля это много: проверьте кабель и порт роутера и выключите энергосбережение сетевой карты на вкладке «Оптимизация»."), 3);
        else if (!double.IsNaN(n.InternetJitter) && n.InternetJitter > 15)
            c.Add(Severity.Warn, L.F("Пинг до интернета скачет на {0:0} мс", n.InternetJitter),
                L.T("Ровный пинг важнее низкого. Если в это время ничего не скачивалось, понаблюдайте на вкладке «Сеть», в какие часы это повторяется."), 3);
        if (double.IsNaN(n.InternetAvg) && !double.IsNaN(n.RouterAvg))
            c.Add(Severity.Info, L.T("Интернет не отвечает на пинг"),
                L.T("Роутер отвечает, а 1.1.1.1 и 8.8.8.8 — нет. Возможно, нет интернета или провайдер блокирует пинг."), 3);

        return (n.WiFi ? "Wi-Fi" : L.T("кабель")) + (double.IsNaN(n.InternetAvg) ? "" : " · " + L.F("{0:0} мс", n.InternetAvg));
    }

    private static ScanNet MeasureNet()
    {
        var n = new ScanNet();
        try
        {
            IPAddress local = null;
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect("1.1.1.1", 53);
                local = (socket.LocalEndPoint as IPEndPoint)?.Address;
            }
            catch { }

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = nic.GetIPProperties();
                if (local != null && !props.UnicastAddresses.Any(u => u.Address.Equals(local))) continue;
                var gw = props.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
                if (gw == null) continue;
                n.Connected = true;
                n.WiFi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                n.Adapter = Tidy(nic.Description);
                n.SpeedBps = nic.Speed;
                n.Gateway = gw.ToString();
                break;
            }
            if (!n.Connected) return n;

            var router = Task.Run(() => PingSeries(n.Gateway, 12));
            var inet = Task.Run(() => PingSeries("1.1.1.1", 20));
            (n.RouterAvg, n.RouterJitter, n.RouterLoss) = router.Result;
            (n.InternetAvg, n.InternetJitter, n.InternetLoss) = inet.Result;
            if (double.IsNaN(n.InternetAvg))
            {
                n.InternetHost = "8.8.8.8";
                (n.InternetAvg, n.InternetJitter, n.InternetLoss) = PingSeries("8.8.8.8", 10);
            }
        }
        catch { }
        return n;
    }

    private static (double Avg, double Jitter, double LossPct) PingSeries(string host, int count)
    {
        var values = new List<double>();
        int lost = 0;
        using var ping = new Ping();
        for (int i = 0; i < count; i++)
        {
            var started = Stopwatch.StartNew();
            try
            {
                var reply = ping.Send(host, 1000);
                if (reply.Status == IPStatus.Success) values.Add(Math.Max(reply.RoundtripTime, 0.5)); else lost++;
            }
            catch { lost++; }
            int rest = 100 - (int)started.ElapsedMilliseconds;
            if (rest > 0) Thread.Sleep(rest);
        }
        if (values.Count == 0) return (double.NaN, double.NaN, 100);
        double jitter = 0;
        for (int i = 1; i < values.Count; i++) jitter += Math.Abs(values[i] - values[i - 1]);
        jitter = values.Count > 1 ? jitter / (values.Count - 1) : 0;
        return (values.Average(), jitter, 100.0 * lost / count);
    }

    // ───────────────────────────── 11. Мусор ─────────────────────────────

    private static Dictionary<string, long> ScanJunk()
    {
        var sizes = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
        List<CleanItem> items;
        try { items = Cleaner.BuildItems().Where(i => !i.SizeUnknown).ToList(); } catch { return new(); }
        var tasks = items.Select(i => Task.Run(() => { try { sizes[i.Title] = Math.Max(0, i.Scan()); } catch { } })).ToArray();
        // Медленные пункты не ждём бесконечно: что не успело — не считается.
        try { Task.WaitAll(tasks, JunkBudgetMs); } catch { }
        return new Dictionary<string, long>(sizes);
    }

    private static string StepJunk(Ctx c)
    {
        var r = c.R;
        int left = JunkBudgetMs + 2000 - (int)(DateTime.Now - c.Started).TotalMilliseconds;
        var sizes = Ctx.Wait(c.Junk, Math.Max(500, left)) ?? new Dictionary<string, long>();
        r.JunkBytes = sizes.Values.Sum();
        if (r.JunkBytes >= 2L << 30)
        {
            var top = sizes.Where(x => x.Value >= 100L << 20).OrderByDescending(x => x.Value).Take(2).ToList();
            var sysVol = r.Volumes.FirstOrDefault(v => v.IsSystem);
            bool tight = sysVol != null && sysVol.FreePct < 15;
            c.Add(r.JunkBytes >= 10L << 30 || tight ? Severity.Warn : Severity.Info, L.F("Временные файлы и кэш: {0}", Fmt.Size(r.JunkBytes)),
                (top.Count > 0 ? L.F("Больше всего — {0}.", string.Join(L.Pick(" и ", " and "), top.Select(x => Q(L.T(x.Key)) + $" ({Fmt.Size(x.Value)})"))) + " " : "") +
                L.T("Посмотреть по видам и удалить — на вкладке «Очистка»."), 5);
        }
        return sizes.Count == 0 ? L.T("не удалось посчитать") : r.JunkBytes > 0 ? Fmt.Size(r.JunkBytes) : L.T("мусора нет");
    }

    // ───────────────────────────── 12. Журнал Windows ─────────────────────────────

    private const int LogDays = 7;

    /// <summary>
    /// Журналы за 7 дней по тем же правилам, что и «Диагностика»: виды, отмеченные там как просмотренные,
    /// считаются только после отметки. Журналы Windows не меняются.
    /// </summary>
    private static void ReadLogs(ScanReport r)
    {
        DateTime From(string kind) => LogMarks.Since(kind, LogDays);
        r.UnexpectedShutdowns = Diagnostics.CountSince(LogMarks.Shutdown, From(LogMarks.Shutdown));
        r.Bsods = Diagnostics.CountSince(LogMarks.Bsod, From(LogMarks.Bsod));
        r.Whea = Diagnostics.CountSince(LogMarks.Whea, From(LogMarks.Whea), "(Level=1 or Level=2)");
        r.WheaCorrected = Diagnostics.CountSince(LogMarks.Whea, From(LogMarks.Whea), "(Level=3)");
        r.DisplayDriverErrors = Diagnostics.CountSince(LogMarks.Gpu, From(LogMarks.Gpu));
        r.DiskErrors = Diagnostics.CountSince(LogMarks.Disk, From(LogMarks.Disk));
        r.AppCrashes = Diagnostics.CountSince(LogMarks.Crash, From(LogMarks.Crash));
        r.HiddenKinds = LogMarks.Kinds.Keys.Count(k => LogMarks.SeenWithin(k, LogDays) != null);
    }

    private static string StepLog(Ctx c)
    {
        var r = c.R;
        try { c.Log.Wait(20000); } catch { }
        // У отмеченного вида период — «после отметки 19 ноября», у остальных — «за 7 дней».
        string Days(string kind) => LogMarks.SeenWithin(kind, LogDays) is DateTime seen ? LogMarks.SeenText(seen) : L.F("за {0} дней", LogDays);
        string days = L.F("за {0} дней", LogDays);
        if (r.Bsods > 0)
            c.Add(Severity.Bad, L.F("Синие экраны: {0} {1}", r.Bsods, Days(LogMarks.Bsod)),
                L.T("Windows аварийно останавливалась. Обычно виноваты драйвер, нестабильный разгон или память. Подробности — на вкладке «Диагностика»."), 4);
        if (r.Whea > 0)
            c.Add(Severity.Bad, L.F("Аппаратные ошибки: {0} {1}", r.Whea, Days(LogMarks.Whea)),
                L.T("Процессор, память или шина PCIe сообщили об ошибке. Если включён разгон или XMP — проверьте стабильность без него."), 4);
        if (r.UnexpectedShutdowns > 0)
            c.Add(Severity.Warn, L.F("Аварийные выключения: {0} {1}", r.UnexpectedShutdowns, Days(LogMarks.Shutdown)),
                L.T("Компьютер выключался без завершения работы: зависание, сбой питания или выключение кнопкой. Если выключали не вы — проверьте блок питания и охлаждение."), 4);
        if (r.DisplayDriverErrors > 0)
            c.AddSection(Severity.Warn, L.F("Сбои драйвера видеокарты: {0} {1}", r.DisplayDriverErrors, Days(LogMarks.Gpu)),
                L.T("Драйвер видеокарты сообщал о сбоях. Что это за записи, были ли они во время игр и что делать — на вкладке «Система», раздел «Драйвер»."), "Драйвер");
        if (r.DiskErrors > 0)
            c.Add(Severity.Bad, L.F("Ошибки дисков в журнале: {0} {1}", r.DiskErrors, Days(LogMarks.Disk)),
                L.T("Windows не смогла прочитать или записать данные. Сделайте копию важного и проверьте диски на вкладке «Диагностика»."), 4);
        if (r.WheaCorrected > 0 && r.Whea == 0)
            c.Add(Severity.Info, L.F("Исправленные аппаратные ошибки: {0} {1}", r.WheaCorrected, Days(LogMarks.Whea)),
                L.T("Оборудование само исправило ошибки, на работу это не повлияло. Если их сотни — проверьте разгон и обновите BIOS."), 4);

        int serious = r.Bsods + r.Whea + r.UnexpectedShutdowns + r.DisplayDriverErrors + r.DiskErrors;
        if (serious == 0) return r.HiddenKinds > 0 ? L.T("новых сбоев после отметки нет") : L.F("сбоев нет {0}", days);
        var parts = new List<string>();
        if (r.Bsods > 0) parts.Add(Fmt.Plural(r.Bsods, "синий экран", "синих экрана", "синих экранов"));
        if (r.UnexpectedShutdowns > 0) parts.Add(Fmt.Plural(r.UnexpectedShutdowns, "аварийное выключение", "аварийных выключения", "аварийных выключений"));
        if (r.DisplayDriverErrors > 0) parts.Add(Fmt.Plural(r.DisplayDriverErrors, "сбой драйвера видеокарты", "сбоя драйвера видеокарты", "сбоев драйвера видеокарты"));
        if (r.Whea > 0) parts.Add(Fmt.Plural(r.Whea, "аппаратная ошибка", "аппаратные ошибки", "аппаратных ошибок"));
        if (r.DiskErrors > 0) parts.Add(Fmt.Plural(r.DiskErrors, "ошибка диска", "ошибки диска", "ошибок диска"));
        return string.Join(", ", parts.Take(2));
    }

    // ───────────────────────────── самопроверка ─────────────────────────────

    /// <summary>Провести осмотр и вернуть текст: что найдено на этом ПК, сколько занял каждый шаг.</summary>
    public static string SelfCheck()
    {
        var r = Run();
        var sb = new StringBuilder();
        sb.AppendLine($"Первичный осмотр {r.At:dd.MM.yyyy HH:mm:ss} · {r.Duration.TotalSeconds:0.0} с · права администратора: {(r.IsAdmin ? "да" : "нет")} · ноутбук: {(r.IsLaptop ? "да" : "нет")}");
        sb.AppendLine();
        sb.AppendLine("ШАГИ");
        foreach (var s in r.Steps)
            sb.AppendLine($"  {s.Index + 1,2}. {s.Title,-18} {s.Took.TotalMilliseconds,6:0} мс  {s.Summary}" + (s.Error != null ? "  ОШИБКА: " + s.Error : "") + (s.Worst != Severity.Ok ? $"  [{s.Worst}]" : ""));
        sb.AppendLine();
        sb.AppendLine("ПАСПОРТ");
        foreach (var p in r.Passport)
        {
            var lines = p.Value.Split('\n');
            sb.AppendLine($"  {p.Label,-12} {lines[0]}");
            foreach (var l in lines.Skip(1)) sb.AppendLine($"  {"",-12} {l}");
        }
        sb.AppendLine();
        sb.AppendLine($"ЗАМЕЧАНИЯ ({r.Findings.Count}: серьёзных {r.Count(Severity.Bad)}, внимание {r.Count(Severity.Warn)}, к сведению {r.Count(Severity.Info)})");
        foreach (var f in r.Findings)
            sb.AppendLine($"  [{f.Severity}] {f.Title}" + (f.TargetPage >= 0 ? $"  → {f.PageName}" : "") + $"\n         {f.Detail}");
        sb.AppendLine();
        sb.AppendLine("ПОДРОБНО");
        foreach (var g in r.Gpus) sb.AppendLine($"  GPU: {g.Name} · {g.Vendor} · {(g.Discrete ? "дискретная" : "встроенная")} · VRAM {g.VramBytes / Fmt.Gib:0.0} ГБ · драйвер {g.Driver} ({g.DriverShort}) от {g.DriverDate:dd.MM.yyyy}");
        foreach (var m in r.RamModules) sb.AppendLine($"  RAM: {m.Slot} · {m.Maker} {m.Part} · {m.Bytes / Fmt.Gib:0} ГБ · тип {m.Type} · заявлено {m.RatedMhz} · работает {m.ConfiguredMhz} · по маркировке {m.PartMhz}");
        foreach (var d in r.Disks) sb.AppendLine($"  Диск {d.Number}: {d.Name} · {d.Kind} ({d.Media}/{d.Bus}) · {DiskSize(d.Bytes)} · {d.Health} · системный {d.IsSystem} · {string.Join(" ", d.Letters)} · темп {d.Temp?.ToString() ?? "—"} износ {d.Wear?.ToString() ?? "—"} ошибки {d.ReadErrors?.ToString() ?? "—"}");
        foreach (var m in r.Monitors) sb.AppendLine($"  Монитор {m.Device}: {m.Name} · {m.Width}×{m.Height} @ {m.Hz} (макс. {m.MaxHz}) · родное {m.NativeWidth}×{m.NativeHeight} · основной {m.Primary}");
        sb.AppendLine($"  Сеть: {(r.Net.WiFi ? "Wi-Fi" : "кабель")} · {r.Net.Adapter} · {r.Net.SpeedBps / 1e6:0} Мбит/с · шлюз {r.Net.Gateway} · роутер {r.Net.RouterAvg:0.0}/{r.Net.RouterJitter:0.0}/{r.Net.RouterLoss:0}% · {r.Net.InternetHost} {r.Net.InternetAvg:0.0}/{r.Net.InternetJitter:0.0}/{r.Net.InternetLoss:0}%");
        sb.AppendLine($"  Журнал: BSOD {r.Bsods}, WHEA {r.Whea} (+{r.WheaCorrected} испр.), Kernel-Power 41: {r.UnexpectedShutdowns}, видеодрайвер {r.DisplayDriverErrors}, диски {r.DiskErrors}, падения программ {r.AppCrashes}");
        sb.AppendLine($"  Мусор: {Fmt.Size(Math.Max(0, r.JunkBytes))} · игр {r.Games.Count} (с рекомендациями {r.GamesWithTips}) · программ {r.ProgramsInstalled} · автозагрузка {r.StartupEnabled}");
        return sb.ToString();
    }

    // ───────────────────────────── P/Invoke ─────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroup, IntPtr setting, byte[] buffer, ref uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);
}
