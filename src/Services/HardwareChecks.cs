using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PulsePC.Panels;

namespace PulsePC.Services;

/// <summary>Вывод проверки железа: уже на языке интерфейса. Step — шаг первичного осмотра, Important — показывать и в осмотре.</summary>
public sealed class HwFinding
{
    public Severity Level = Severity.Info;
    public string Title = "", Detail = "", Count = "", Link = "", LinkText = "", Step = "";
    public bool Important;
}

/// <summary>Связь устройства PCI Express: ширина и поколение по сведениям Windows (как при включении) и, у NVIDIA, по nvidia-smi.</summary>
public sealed class PciLink
{
    public string Name = "", Instance = "", Child = "";
    public int BaseClass = -1, SubClass = -1;
    /// <summary>Поколение (1 — 2,5 ГТ/с … 5 — 32 ГТ/с) и ширина (линий): сейчас и максимум устройства.</summary>
    public int CurGen, MaxGen, CurWidth, MaxWidth;
}

/// <summary>Шина видеокарты: ширина, поколение, Resizable BAR.</summary>
public sealed class GpuLink
{
    public string Name = "", Vendor = "";
    public int CurWidth, MaxWidth;
    /// <summary>NVIDIA: максимум, о котором договорились плата и карта; максимум карты; максимум платы. 0 — не читается.</summary>
    public int Gen, GpuMaxGen, HostMaxGen;
    /// <summary>Окно BAR1, МиБ (NVIDIA); больше 256 — Resizable BAR включён. 0 — не читается.</summary>
    public int Bar1Mb;
}

/// <summary>Оценка пиковой нагрузки на блок питания.</summary>
public sealed class PsuEstimate
{
    public double Gpu, Cpu, Rest = 60, Total;
    public bool GpuMeasured, GpuKnown, CpuKnown;
    public int Recommended;
}

/// <summary>Игра на диске и её просадки из-за диска.</summary>
public sealed class GameOnDisk
{
    public string Title = "", Dir = "", Letter = "", Kind = "";
    public int DiskDips, Sessions;
}

/// <summary>
/// Проверки железа для «Диагностики» и первичного осмотра (версия 1.5): память (через MemoryProfile), кабель монитора в плате,
/// шина видеокарты и Resizable BAR, температура видеопамяти (только если её честно отдаёт драйвер), игры на жёстком диске,
/// запас блока питания (оценка), TRIM, износ и температура дисков, NVMe на медленной шине, пыль (из «Вентиляторов»).
/// Только чтение: ничего в системе не меняет, все советы — словами.
/// </summary>
public static class HardwareChecks
{
    private const string Nb = "\u00A0";
    public const string PsuKey = "hw.psu.watts";
    private static PanelHost host;

    public static void Init(PanelHost h) => host = h;

    // ================================================================== ноутбук
    /// <summary>
    /// Ноутбук ли это — общий признак LaptopCheck.Applies: мобильная платформа (как PCSystemType = 2 в первичном осмотре),
    /// корпус ноутбука или батарея ноутбука (ИБП батареей не считается).
    /// </summary>
    public static bool IsLaptop() => LaptopCheck.Applies;   // общий признак (тип корпуса, мобильная платформа или батарея; кэш и подмена — там)

    // ================================================================== единицы
    private static string W(double v) => v.ToString("0", L.Culture) + Nb + (L.En ? "W" : "Вт");
    private static string Deg(double v) => v.ToString("0", L.Culture) + Nb + "°C";
    private static string Pct(double v) => v.ToString("0", L.Culture) + (L.En ? "%" : Nb + "%");
    private static string GbText(double gb) => (gb >= 10 ? gb.ToString("0", L.Culture) : gb.ToString("0.#", L.Culture)) + Nb + Fmt.GbUnit;
    private static string Gen(int g) => g switch { 1 => "PCIe 1.1", 2 => "PCIe 2.0", 3 => "PCIe 3.0", 4 => "PCIe 4.0", 5 => "PCIe 5.0", 6 => "PCIe 6.0", _ => "PCIe" };

    // ================================================================== 1. монитор подключён к плате
    /// <summary>Экран: подключён ли он к встроенной графике и основной ли он.</summary>
    public sealed class Screen { public string Name = "", Adapter = ""; public bool Primary, OnIntegrated; }

    /// <summary>Чистая функция: есть отдельная видеокарта, а экран (основной) выводится через встроенную графику. На ноутбуке — норма.</summary>
    public static HwFinding MonitorFinding(bool isLaptop, bool hasDiscrete, string discreteName, IList<Screen> screens)
    {
        if (isLaptop || !hasDiscrete || screens == null || screens.Count == 0) return null;
        var bad = screens.Where(s => s.OnIntegrated).ToList();
        if (bad.Count == 0)
            return new HwFinding { Level = Severity.Ok, Title = L.T("Монитор подключён к видеокарте"), Detail = L.F("изображение выводит {0}", HardwareInfo.ShortGpu(discreteName)), Step = "Монитор" };
        bool primary = bad.Any(s => s.Primary) || bad.Count == screens.Count;
        string adapter = bad[0].Adapter.Length > 0 ? bad[0].Adapter : L.T("встроенная графика");
        if (primary)
            return new HwFinding
            {
                Level = Severity.Bad, Important = true, Step = "Монитор", Link = "sys:Монитор", LinkText = L.T("Подробнее о мониторе — в разделе «Монитор»"),
                Title = L.T("Кабель монитора включён в материнскую плату"),
                Detail = L.F("Основной экран выводит {0}, хотя в компьютере есть {1}. Игры при этом идут на встроенной графике или тратят силы на передачу кадров через неё — FPS заметно ниже. Выключите компьютер и переключите кабель монитора в разъём видеокарты (он ниже, на задней планке видеокарты, а не рядом с USB).", adapter, discreteName),
            };
        return new HwFinding
        {
            Level = Severity.Info, Step = "Монитор",
            Title = L.T("Второй монитор подключён к материнской плате"),
            Detail = L.F("Дополнительный экран выводит {0}. Для браузера и чата это нормально; если играете на нём — переключите его кабель в видеокарту.", adapter),
        };
    }

    /// <summary>Экраны и адаптеры, к которым они подключены (QueryDisplayConfig + D3DKMT). Только чтение.</summary>
    public static (List<Screen> Screens, bool HasDiscrete, string Discrete) ReadScreens()
    {
        var screens = new List<Screen>();
        var adapters = Kmt.Enumerate();
        var discrete = adapters.Where(a => !a.Integrated && a.Vendor != "" && !GpuVendors.IsVirtual(a.Name)).OrderByDescending(a => a.DedicatedBytes).FirstOrDefault();
        try
        {
            if (!QueryPaths(out var paths, out var modes)) return (screens, discrete != null, discrete?.Name ?? "");
            foreach (var p in paths)
            {
                var a = adapters.FirstOrDefault(x => x.LuidLow == p.Target.Adapter.Low && x.LuidHigh == p.Target.Adapter.High);
                bool primary = false;
                if (p.Source.ModeIdx < modes.Length && modes[p.Source.ModeIdx].InfoType == 1)
                    primary = modes[p.Source.ModeIdx].SrcX == 0 && modes[p.Source.ModeIdx].SrcY == 0;
                screens.Add(new Screen
                {
                    Name = TargetName(p.Target.Adapter, p.Target.Id), Adapter = a?.Name ?? "", Primary = primary,
                    OnIntegrated = a != null && a.Integrated && !GpuVendors.IsVirtual(a.Name),
                });
            }
        }
        catch { }
        return (screens, discrete != null, discrete?.Name ?? "");
    }

    // ================================================================== 2. шина видеокарты и Resizable BAR
    /// <summary>Чистая функция: выводы по шине видеокарты (на ноутбуке не проверяется: там шина задана производителем).</summary>
    public static List<HwFinding> GpuLinkFindings(bool isLaptop, GpuLink g)
    {
        var list = new List<HwFinding>();
        if (isLaptop || g == null) return list;
        string name = HardwareInfo.ShortGpu(g.Name);
        if (g.CurWidth > 0 && g.MaxWidth > 0 && g.CurWidth < g.MaxWidth)
            list.Add(new HwFinding
            {
                Level = Severity.Warn, Important = true, Step = "Видеокарта",
                Title = L.F("Видеокарта работает на x{0} вместо x{1}", g.CurWidth, g.MaxWidth),
                Detail = L.F("{0} умеет работать по {1} линиям PCI Express, а подключена по {2}. Так бывает, если она стоит в нижнем слоте, подключена через райзер или удлинитель, или слот делит линии с разъёмом M.2. Проверьте, что видеокарта стоит в верхнем длинном слоте (ближе к процессору) и вставлена до щелчка; если используется райзер — попробуйте без него.", name, g.MaxWidth, g.CurWidth),
            });
        else if (g.CurWidth > 0 && g.MaxWidth > 0)
            list.Add(new HwFinding { Level = Severity.Ok, Step = "Видеокарта", Title = L.F("Шина видеокарты: x{0}", g.CurWidth), Detail = L.F("все {0} линий PCI Express", g.MaxWidth) });

        if (g.GpuMaxGen > 0 && g.HostMaxGen > 0 && g.HostMaxGen < g.GpuMaxGen)
            list.Add(new HwFinding
            {
                Level = Severity.Info, Step = "Видеокарта",
                Title = L.F("Плата ограничивает шину видеокарты до {0}", Gen(g.HostMaxGen)),
                Detail = L.F("Видеокарта умеет {0}, а плата или слот — только {1}.", Gen(g.GpuMaxGen), Gen(g.HostMaxGen)) + " " +
                         (g.MaxWidth > 0 && g.MaxWidth <= 8
                             ? L.T("У видеокарт с восемью линиями это заметно, когда в игре кончается видеопамять: снизьте качество текстур на ступень.")
                             : L.T("В играх разница обычно в пределах нескольких процентов."))
                         + " " + L.T("Скорость шины в простое падает — это нормально; здесь сравниваются максимумы, о которых договорились плата и видеокарта."),
            });

        if (g.Vendor == "NVIDIA" && g.Bar1Mb > 0 && Regex.IsMatch(g.Name ?? "", @"RTX\s*(3\d{3}|4\d{3}|5\d{3})", RegexOptions.IgnoreCase))
        {
            if (g.Bar1Mb > 256)
                list.Add(new HwFinding { Level = Severity.Ok, Step = "Видеокарта", Title = L.T("Resizable BAR включён"), Detail = L.T("процессор видит всю видеопамять сразу") });
            else
                list.Add(new HwFinding
                {
                    Level = Severity.Info, Step = "Видеокарта",
                    Title = L.T("Resizable BAR выключен"),
                    Detail = L.T("Видеокарта его поддерживает, но процессор видит видеопамять маленьким окном. В части игр с ним немного выше FPS, в остальных разницы нет. Включается в BIOS: «Above 4G Decoding» и «Re-Size BAR Support». Важно: для этого нужен режим загрузки UEFI без CSM — если Windows установлена в старом режиме (MBR), после выключения CSM компьютер не загрузится. Если не уверены, оставьте как есть."),
                });
        }
        return list;
    }

    private static List<PciLink> pciCache;
    private static DateTime pciAt;

    /// <summary>Видеокарты и NVMe-контроллеры на шине PCI Express по сведениям Windows (SetupAPI, без прав). Только чтение.</summary>
    public static List<PciLink> ReadPci()
    {
        if (pciCache != null && DateTime.Now - pciAt < TimeSpan.FromMinutes(10)) return pciCache;
        var list = new List<PciLink>();
        IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, "PCI", IntPtr.Zero, 0x2 | 0x4);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; i < 2000 && SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                int bc = PciProp(set, ref data, 3), sc = PciProp(set, ref data, 4);
                if (!(bc == 3 || bc == 1 && sc == 8)) continue;          // видеокарты и контроллеры NVM Express
                var l = new PciLink
                {
                    BaseClass = bc, SubClass = sc, CurGen = PciProp(set, ref data, 9), CurWidth = PciProp(set, ref data, 10),
                    MaxGen = PciProp(set, ref data, 11), MaxWidth = PciProp(set, ref data, 12),
                    Name = DevString(set, ref data, DeviceKey(14)) is { Length: > 0 } fn ? fn : DevString(set, ref data, DeviceKey(2)),
                };
                var sb = new StringBuilder(512);
                if (SetupDiGetDeviceInstanceIdW(set, ref data, sb, sb.Capacity, out _)) l.Instance = sb.ToString();
                if (bc == 1 && CM_Get_Child(out uint child, data.DevInst, 0) == 0) l.Child = NodeString(child, DeviceKey(14)) is { Length: > 0 } cn ? cn : NodeString(child, DeviceKey(2));
                list.Add(l);
            }
        }
        catch { }
        finally { SetupDiDestroyDeviceInfoList(set); }
        pciCache = list; pciAt = DateTime.Now;
        return list;
    }

    /// <summary>Шина главной видеокарты: Windows (ширина) + nvidia-smi (поколение, Resizable BAR). laptop — nvidia-smi не будит отдельную видеокарту ноутбука.</summary>
    public static GpuLink ReadGpuLink(bool isLaptop)
    {
        var adapters = Kmt.Enumerate();
        var main = adapters.Where(a => !GpuVendors.IsVirtual(a.Name)).Select(a => (K: a, A: a.ToAdapter())).OrderBy(x => x.A.Integrated ? 1 : 0).ThenByDescending(x => x.A.VramGb).FirstOrDefault();
        if (main.K == null || main.A.Integrated) return null;
        var g = new GpuLink { Name = main.K.Name, Vendor = main.K.Vendor };
        string ven = main.K.VendorId.ToString("X4"), dev = main.K.DeviceId.ToString("X4");
        var pci = ReadPci().FirstOrDefault(p => p.BaseClass == 3 && p.Instance.Contains("VEN_" + ven, StringComparison.OrdinalIgnoreCase) && p.Instance.Contains("DEV_" + dev, StringComparison.OrdinalIgnoreCase));
        if (pci != null) { g.CurWidth = pci.CurWidth; g.MaxWidth = pci.MaxWidth; }
        if (g.Vendor == "NVIDIA" && !isLaptop)
        {
            var (code, outp) = Shell.Run("nvidia-smi", "--query-gpu=pcie.link.gen.max,pcie.link.gen.gpumax,pcie.link.gen.hostmax,pcie.link.width.current,pcie.link.width.max --format=csv,noheader,nounits", 8000);
            var first = (outp ?? "").Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) ?? "";
            var f = first.Split(',').Select(x => x.Trim()).ToArray();
            int N(int i) => i < f.Length && int.TryParse(f[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
            if (code == 0 && f.Length >= 5)
            {
                g.Gen = N(0); g.GpuMaxGen = N(1); g.HostMaxGen = N(2);
                if (g.CurWidth == 0) { g.CurWidth = N(3); g.MaxWidth = N(4); }
            }
            var (c2, q) = Shell.Run("nvidia-smi", "-q -d MEMORY", 8000);
            if (c2 == 0) g.Bar1Mb = ParseBar1(q);
        }
        return g;
    }

    /// <summary>Размер BAR1 из «nvidia-smi -q -d MEMORY», МиБ; 0 — не найден.</summary>
    public static int ParseBar1(string text)
    {
        var m = Regex.Match(text ?? "", @"BAR1 Memory Usage\s*[\r\n]+\s*Total\s*:\s*(\d+)\s*MiB", RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    // ================================================================== 3. температура видеопамяти
    /// <summary>GDDR6X (RTX 3070 Ti/3080/3090, RTX 4070 и старше) греется сильнее и сбрасывает скорость около 110 °C.</summary>
    public static bool IsGddr6X(string gpu) =>
        Regex.IsMatch(gpu ?? "", @"RTX\s*(3070\s*Ti|3080|3090|4070|4080|4090)", RegexOptions.IgnoreCase) && !Regex.IsMatch(gpu ?? "", @"Laptop", RegexOptions.IgnoreCase);

    /// <summary>Чистая функция. Пороги: GDDR6 рассчитана примерно до 95 °C, GDDR6X сбрасывает скорость около 110 °C (спецификации Micron).</summary>
    public static HwFinding VramTempFinding(double temp, string gpu)
    {
        if (double.IsNaN(temp) || temp <= 0 || temp > 150) return null;
        bool x = IsGddr6X(gpu);
        double warn = x ? 100 : 90, bad = x ? 106 : 95;
        string kind = x ? "GDDR6X" : "GDDR6";
        if (temp >= bad)
            return new HwFinding { Level = Severity.Bad, Important = true, Step = "Видеокарта", Count = Deg(temp), Title = L.T("Видеопамять перегревается"),
                Detail = L.F("Память видеокарты нагрелась до {0}. Для {1} это у верхнего предела (по спецификациям Micron: GDDR6 рассчитана примерно до 95 °C, GDDR6X сбрасывает скорость около 110 °C). Проверьте обдув корпуса и вентиляторы видеокарты; если это давно купленная карта — термопрокладки памяти могли высохнуть, их меняют в сервисе.", Deg(temp), kind) };
        if (temp >= warn)
            return new HwFinding { Level = Severity.Warn, Step = "Видеокарта", Count = Deg(temp), Title = L.T("Видеопамять горячая"),
                Detail = L.F("Память видеокарты — {0}. Для {1} это ещё допустимо, но запаса мало: проверьте обдув корпуса.", Deg(temp), kind) };
        return new HwFinding { Level = Severity.Ok, Step = "Видеокарта", Count = Deg(temp), Title = L.T("Температура видеопамяти в норме"), Detail = L.F("{0} — для {1} это нормально.", Deg(temp), kind) };
    }

    /// <summary>
    /// Температура видеопамяти, если драйвер её честно отдаёт: nvidia-smi temperature.memory или документированная NvAPI_GPU_GetThermalSettings
    /// (датчик «память»). Горячая точка (hotspot) документированными функциями не читается — не показывается. NaN — нет данных.
    /// </summary>
    public static double ReadVramTemp(bool isLaptop)
    {
        if (isLaptop || GpuVendors.MainVendor != "NVIDIA") return double.NaN;
        try
        {
            var (code, outp) = Shell.Run("nvidia-smi", "--query-gpu=temperature.memory --format=csv,noheader,nounits", 6000);
            if (code == 0 && double.TryParse((outp ?? "").Trim().Split('\n')[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double t) && t > 0) return t;
        }
        catch { }
        return NvMemoryTemp();
    }

    // ================================================================== 4. игры на жёстком диске
    private static string Norm(string s) => Regex.Replace((s ?? "").ToLowerInvariant(), @"[^\p{L}\p{Nd}]", "");

    /// <summary>Совпадает ли название сессии с названием игры из библиотеки («PUBG» и «PUBG: BATTLEGROUNDS»).</summary>
    public static bool SameGame(string a, string b)
    {
        string x = Norm(a), y = Norm(b);
        if (x.Length == 0 || y.Length == 0) return false;
        if (x == y) return true;
        return x.Length >= 4 && y.Length >= 4 && (x.Contains(y) || y.Contains(x));
    }

    /// <summary>Чистая функция: игры на HDD и сколько просадок из-за диска было в их сессиях.</summary>
    public static List<GameOnDisk> GamesOnHdd(IEnumerable<(string Title, string Dir)> games, IEnumerable<PhysDisk> disks, IEnumerable<GameSession> sessions)
    {
        var list = new List<GameOnDisk>();
        var dl = (disks ?? Enumerable.Empty<PhysDisk>()).ToList();
        var sl = (sessions ?? Enumerable.Empty<GameSession>()).ToList();
        foreach (var (title, dir) in games ?? Enumerable.Empty<(string, string)>())
        {
            if (string.IsNullOrEmpty(dir)) continue;
            string letter = (Path.GetPathRoot(dir) ?? "").TrimEnd('\\', ':').ToUpperInvariant();
            if (letter.Length != 1) continue;
            var disk = dl.FirstOrDefault(d => d.Letters.Contains(letter));
            if (disk == null || disk.Kind != "HDD") continue;
            var mine = sl.Where(s => SameGame(s.Game, title)).ToList();
            list.Add(new GameOnDisk
            {
                Title = title, Dir = dir, Letter = letter, Kind = disk.Kind, Sessions = mine.Count,
                DiskDips = mine.Sum(s => s.Dips?.Count(d => d.Cause == "disk") ?? 0),
            });
        }
        return list;
    }

    private static readonly Dictionary<string, (long Bytes, bool Done)> sizes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Размер папки (в фоне, не дольше budgetMs; повторный вызов берёт запомненное). Done = false — подсчёт не закончен.</summary>
    public static (long Bytes, bool Done) FolderSize(string dir, int budgetMs = 4000)
    {
        lock (sizes) if (sizes.TryGetValue(dir, out var c) && c.Done) return c;
        long total = 0; bool done = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", opt))
            {
                try { total += f.Length; } catch { }
                if (sw.ElapsedMilliseconds > budgetMs) { done = false; break; }
            }
        }
        catch { done = false; }
        lock (sizes) sizes[dir] = (total, done);
        return (total, done);
    }

    /// <summary>Вывод по игре на HDD: только если в её сессиях были просадки из-за диска. freeSsd — свободное место на SSD/NVMe-дисках (буква, байты).</summary>
    public static HwFinding GameHddFinding(GameOnDisk g, long sizeBytes, bool sizeDone, IList<(string Letter, string Kind, long Free)> freeSsd)
    {
        if (g == null || g.DiskDips == 0) return null;
        string size = sizeBytes > 0 ? (sizeDone ? Fmt.Size(sizeBytes) : L.F("больше {0}", Fmt.Size(sizeBytes))) : "";
        var fit = sizeBytes > 0 ? freeSsd?.Where(x => x.Free > sizeBytes * 1.1).OrderByDescending(x => x.Free).FirstOrDefault() : default;
        string where = fit is { Letter: not null } f && f.Letter != ""
            ? L.F("На диске {0}: ({1}) свободно {2} — хватит.", f.Letter, f.Kind, Fmt.Size(f.Free))
            : size.Length > 0 ? L.F("Нужен SSD, на котором свободно хотя бы {0}.", size) : "";
        return new HwFinding
        {
            Level = Severity.Warn, Important = true, Step = "Диски", Count = Fmt.Plural(g.DiskDips, "просадка", "просадки", "просадок"),
            Title = L.F("{0} стоит на жёстком диске", g.Title),
            Detail = L.F("Игра установлена на {0}: (HDD), и в её сессиях FPS проседал, пока она читала с диска.", g.Letter) + " " +
                     (size.Length > 0 ? L.F("Перенесите её на SSD — игра занимает {0}.", size) : L.T("Перенесите её на SSD.")) + (where.Length > 0 ? " " + where : "") + " " +
                     L.T("В Steam: свойства игры → «Установленные файлы» → «Переместить папку»; в других лаунчерах — похожая кнопка или переустановка на нужный диск. FPS от этого не вырастет, но пропадут рывки при подгрузке."),
        };
    }

    // ================================================================== 5. блок питания
    private static readonly (string Pattern, int Watts)[] GpuTbp =
    {
        // NVIDIA (порядок важен: «Ti Super» раньше «Ti», «Super» раньше голого номера)
        (@"RTX\s*5090", 575), (@"RTX\s*5080", 360), (@"RTX\s*5070\s*Ti", 300), (@"RTX\s*5070", 250), (@"RTX\s*5060\s*Ti", 180), (@"RTX\s*5060", 145), (@"RTX\s*5050", 130),
        (@"RTX\s*4090", 450), (@"RTX\s*4080", 320), (@"RTX\s*4070\s*Ti", 285), (@"RTX\s*4070\s*Super", 220), (@"RTX\s*4070", 200), (@"RTX\s*4060\s*Ti", 165), (@"RTX\s*4060", 115),
        (@"RTX\s*3090\s*Ti", 450), (@"RTX\s*3090", 350), (@"RTX\s*3080\s*Ti", 350), (@"RTX\s*3080", 320), (@"RTX\s*3070\s*Ti", 290), (@"RTX\s*3070", 220),
        (@"RTX\s*3060\s*Ti", 200), (@"RTX\s*3060", 170), (@"RTX\s*3050", 130),
        (@"RTX\s*2080\s*Ti", 250), (@"RTX\s*2080", 225), (@"RTX\s*2070", 190), (@"RTX\s*2060", 165),
        (@"GTX\s*1660", 125), (@"GTX\s*1650", 75), (@"GTX\s*1080\s*Ti", 250), (@"GTX\s*1080", 180), (@"GTX\s*1070", 160), (@"GTX\s*1060", 120), (@"GTX\s*1050", 75),
        // AMD
        (@"RX\s*9070\s*XT", 304), (@"RX\s*9070", 220), (@"RX\s*9060\s*XT", 160),
        (@"RX\s*7900\s*XTX", 355), (@"RX\s*7900\s*XT", 315), (@"RX\s*7900\s*GRE", 260), (@"RX\s*7800\s*XT", 263), (@"RX\s*7700\s*XT", 245), (@"RX\s*7600\s*XT", 190), (@"RX\s*7600", 165),
        (@"RX\s*6950\s*XT", 335), (@"RX\s*6900\s*XT", 300), (@"RX\s*6800\s*XT", 300), (@"RX\s*6800", 250), (@"RX\s*6750\s*XT", 250), (@"RX\s*6700\s*XT", 230), (@"RX\s*6700", 175),
        (@"RX\s*6650\s*XT", 180), (@"RX\s*6600\s*XT", 160), (@"RX\s*6600", 132), (@"RX\s*6500", 107), (@"RX\s*6400", 53),
        (@"RX\s*5700\s*XT", 225), (@"RX\s*5700", 180), (@"RX\s*5600", 150), (@"RX\s*5500", 130), (@"RX\s*590", 225), (@"RX\s*580", 185), (@"RX\s*570", 150),
        // Intel Arc
        (@"Arc.*B580", 190), (@"Arc.*B570", 150), (@"Arc.*A770", 225), (@"Arc.*A750", 225), (@"Arc.*A580", 185), (@"Arc.*A380", 75),
    };

    /// <summary>Паспортная мощность видеокарты по таблице моделей, Вт; 0 — модели нет в таблице.</summary>
    public static int GpuBoardPower(string gpu)
    {
        foreach (var (p, w) in GpuTbp) if (Regex.IsMatch(gpu ?? "", p, RegexOptions.IgnoreCase)) return w;
        return 0;
    }

    /// <summary>Чистая функция: пиковая нагрузка = видеокарта (измерено в играх или по паспорту) + процессор (пиковая мощность по TDP) + остальное ~60 Вт.</summary>
    public static PsuEstimate Estimate(string cpu, string gpu, double measuredGpuW)
    {
        var e = new PsuEstimate();
        int tbp = GpuBoardPower(gpu);
        e.GpuMeasured = measuredGpuW > 20;
        e.GpuKnown = tbp > 0 || e.GpuMeasured;
        e.Gpu = Math.Max(e.GpuMeasured ? measuredGpuW : 0, tbp);
        int tdp = PowerCost.CpuTdp(cpu, out e.CpuKnown);
        string vendor = HardwareInfo.CpuVendor(cpu);
        e.Cpu = vendor == "AMD" ? tdp * 1.35 : vendor == "Intel" ? (tdp >= 125 ? Math.Max(tdp * 2.0, 181) : tdp * 1.8) : tdp * 1.5;
        e.Total = e.Gpu + e.Cpu + e.Rest;
        e.Recommended = (int)(Math.Ceiling(e.Total / 0.8 / 50) * 50);
        return e;
    }

    /// <summary>Чистая функция: вывод по блоку питания. psuWatts — мощность из настроек (0 — не указана). На ноутбуке не показывается.</summary>
    public static HwFinding PsuFinding(bool isLaptop, PsuEstimate e, int psuWatts)
    {
        if (isLaptop || e == null || !e.GpuKnown) return null;
        string parts = L.F("видеокарта {0} ({1}), процессор до {2} ({3}), плата, память, диски и вентиляторы — около {4}",
            W(e.Gpu), e.GpuMeasured ? L.T("измерено в играх") : L.T("по паспорту"), W(e.Cpu), e.CpuKnown ? L.T("по паспорту") : L.T("приблизительно"), W(e.Rest));
        string est = L.F("Оценка пиковой нагрузки — около {0}: {1}.", W(e.Total), parts);
        if (psuWatts <= 0)
            return new HwFinding { Level = Severity.Ok, Step = "Видеокарта", Title = L.T("Блок питания: оценка нагрузки"), Count = W(e.Total),
                Detail = L.F("около {0}; хватит блока от {1} — это оценка, а не замер", W(e.Total), W(e.Recommended)) };
        double headroom = (psuWatts - e.Total) / psuWatts * 100;
        if (headroom < 0)
            return new HwFinding { Level = Severity.Bad, Important = true, Step = "Видеокарта", Title = L.F("Блок питания {0} может не потянуть", W(psuWatts)), Count = W(e.Total),
                Detail = est + " " + L.F("Это больше мощности блока. Если компьютер выключается или перезагружается в играх — первым подозревайте блок питания; для этой сборки нужен блок от {0}. Это оценка, а не замер.", W(e.Recommended)) };
        if (headroom < 20)
            return new HwFinding { Level = Severity.Warn, Important = true, Step = "Видеокарта", Title = L.F("У блока питания мало запаса: {0}", Pct(headroom)), Count = W(e.Total),
                Detail = est + " " + L.F("Блок на {0} работает почти на пределе: он сильнее греется и шумит, а при скачках нагрузки компьютер может выключиться. Спокойнее — блок от {1}. Это оценка, а не замер.", W(psuWatts), W(e.Recommended)) };
        return new HwFinding { Level = Severity.Ok, Step = "Видеокарта", Title = L.F("Блока питания хватает: запас {0}", Pct(headroom)), Count = W(e.Total),
            Detail = L.F("нагрузка около {0} из {1} — это оценка, а не замер", W(e.Total), W(psuWatts)) };
    }

    /// <summary>Наибольшее измеренное потребление видеокарты: среднее за сессию и поминутная статистика «Вентиляторов», Вт; 0 — нет данных.</summary>
    public static double MeasuredGpuWatts(IEnumerable<GameSession> sessions)
    {
        double w = (sessions ?? Enumerable.Empty<GameSession>()).Select(s => s.AvgPowerW).Where(x => x > 0 && x < 1000).DefaultIfEmpty(0).Max();
        try { w = Math.Max(w, FanControl.History().Where(m => m.Game && !float.IsNaN(m.Watts) && m.Watts < 1000).Select(m => (double)m.Watts).DefaultIfEmpty(0).Max()); } catch { }
        return w;
    }

    // ================================================================== 6. диски
    /// <summary>TRIM для NTFS: true — выключен, false — включён, null — не прочитать. fsutil behavior query (только чтение), иначе реестр.</summary>
    public static bool? TrimDisabled()
    {
        try
        {
            var (code, outp) = Shell.Run("fsutil", "behavior query DisableDeleteNotify", 6000);
            var v = ParseTrim(outp);
            if (code == 0 && v != null) return v;
        }
        catch { }
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem");
            object raw = k?.GetValue("DisableDeleteNotification");
            return raw is int i ? i == 1 : k != null ? false : null;
        }
        catch { return null; }
    }

    /// <summary>«NTFS DisableDeleteNotify = 0 (…)» → false (TRIM включён). Текст в скобках зависит от языка Windows, число — нет.</summary>
    public static bool? ParseTrim(string text)
    {
        var m = Regex.Match(text ?? "", @"NTFS\s+DisableDeleteNotify\s*=\s*(\d)", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match(text ?? "", @"DisableDeleteNotify\s*=\s*(\d)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value != "0" : null;
    }

    /// <summary>Чистая функция: TRIM, температура у предела, износ, NVMe на урезанной шине, нужны ли права.</summary>
    public static List<HwFinding> DiskFindings(IList<PhysDisk> disks, bool? trimOff, IList<PciLink> nvme, bool admin)
    {
        var list = new List<HwFinding>();
        disks ??= new List<PhysDisk>();
        bool anySsd = disks.Any(d => d.Kind is "SSD" or "NVMe");
        if (trimOff == true && (anySsd || disks.Count == 0))
            list.Add(new HwFinding
            {
                Level = Severity.Warn, Important = true, Step = "Диски", Title = L.T("TRIM для SSD выключен"),
                Detail = L.T("Windows не сообщает SSD, какие данные удалены, и со временем запись на него замедляется. Включить можно так: командная строка от имени администратора → fsutil behavior set DisableDeleteNotify 0. Программа сама эту настройку не меняет."),
            });
        else if (trimOff == false && anySsd)
            list.Add(new HwFinding { Level = Severity.Ok, Step = "Диски", Title = L.T("TRIM для SSD включён"), Detail = L.T("запись на SSD не замедляется со временем") });

        foreach (var d in disks)
        {
            bool otherWarning = !string.Equals(d.Health, "Healthy", StringComparison.OrdinalIgnoreCase) && d.Health != "" || d.ReadErrors > 0 || d.Wear >= 80 || d.Temp >= 60;
            if (!otherWarning && d.Temp is double t && d.TempMax is double max && max > 0 && t >= max - 3)
                list.Add(new HwFinding
                {
                    Level = Severity.Warn, Step = "Диски", Count = Deg(t), Title = L.F("Диск {0} у предела температуры", d.Name),
                    Detail = L.F("Сейчас {0}, а производитель допускает до {1}. Проверьте обдув: у NVMe-дисков помогает радиатор (часто он есть на плате), у дисков в корзине — вентилятор на вдув спереди.", Deg(t), Deg(max)),
                });
        }
        var worn = disks.Where(d => d.Kind is "SSD" or "NVMe" && (d.Wear != null || d.Temp != null)).ToList();
        if (worn.Count > 0)
            list.Add(new HwFinding
            {
                Level = Severity.Ok, Step = "Диски", Title = L.T("Износ и температура SSD"),
                Detail = string.Join("; ", worn.Select(d => d.Name + " — " + string.Join(", ", new[]
                {
                    d.Wear != null ? L.F("износ {0}", Pct(d.Wear.Value)) : "",
                    d.Temp != null ? (d.TempMax is double mx && mx > 0 ? L.F("{0} (предел {1})", Deg(d.Temp.Value), Deg(mx)) : Deg(d.Temp.Value)) : "",
                }.Where(x => x.Length > 0)))) + ".",
            });
        else if (!admin && disks.Count > 0)
            list.Add(new HwFinding { Level = Severity.Info, Step = "Диски", Title = L.T("Износ и температура дисков — нужны права администратора"), Detail = L.T("Windows отдаёт эти сведения только программам с правами администратора. Запустите «Пульс ПК» от имени администратора, чтобы их увидеть.") });

        foreach (var n in nvme ?? new List<PciLink>())
        {
            string name = n.Child.Length > 0 ? n.Child : n.Name;
            if (n.CurWidth > 0 && n.MaxWidth > 0 && n.CurWidth < n.MaxWidth)
                list.Add(new HwFinding
                {
                    Level = Severity.Warn, Step = "Диски", Title = L.F("NVMe-диск {0} работает на x{1} вместо x{2}", name, n.CurWidth, n.MaxWidth),
                    Detail = L.T("Диск подключён по меньшему числу линий, чем умеет, и читает медленнее. Так бывает, если разъём M.2 делит линии с другим разъёмом или слотом. В инструкции к плате посмотрите, какой разъём M.2 полноскоростной, и что отключается при занятых соседних слотах."),
                });
            else if (n.CurGen > 0 && n.MaxGen > 0 && n.CurGen < n.MaxGen)
                list.Add(new HwFinding
                {
                    Level = Severity.Info, Step = "Диски", Title = L.F("NVMe-диск {0} в разъёме {1}", name, Gen(n.CurGen)),
                    Detail = L.F("Диск умеет {0}, а разъём на плате — {1}: последовательное чтение ограничено. В играх разница почти не видна — загрузка быстрее на секунды.", Gen(n.MaxGen), Gen(n.CurGen)),
                });
        }
        return list;
    }

    /// <summary>Пояснение к «на диске почти нет места», если этот диск — SSD (для строки в «Диагностике»).</summary>
    public static string SsdFullNote(Metrics metrics, string driveName)
    {
        try
        {
            string letter = (driveName ?? "").TrimEnd('\\', ':').ToUpperInvariant();
            var disk = metrics?.Disks?.FirstOrDefault(d => d.Letters.Contains(letter));
            return disk != null && disk.Kind is "SSD" or "NVMe"
                ? " " + L.T("Это SSD: когда он заполнен больше чем на 90 %, запись на него заметно замедляется.")
                : "";
        }
        catch { return ""; }
    }

    // ================================================================== 7. память (MemoryProfile)
    private static HwFinding From(MemVerdict v) => v.Kind == MemKind.None ? null : new HwFinding
    {
        Level = v.Level, Step = "Память", Title = L.T(v.Title), Detail = v.Full, Important = v.Kind == MemKind.ProfileLikelyOff,
        Link = v.AdviseProfile ? "sys:Совет по апгрейду" : "", LinkText = v.AdviseProfile ? L.T("Что ещё ускорит компьютер — в разделе «Совет по апгрейду»") : "",
    };

    private static HwFinding From(ChannelVerdict v) => v.Kind == ChannelKind.None ? null : new HwFinding
    {
        Level = v.Level, Step = "Память", Title = L.T(v.Title), Detail = v.Full, Important = v.Level <= Severity.Warn,
    };

    // ================================================================== сборка
    /// <summary>Все выводы для «Диагностики». Долго (несколько секунд: WMI, nvidia-smi) — вызывается в фоне из Diagnostics.Run.</summary>
    public static List<DiagItem> Diagnose(Metrics metrics, AppSettings settings = null)
    {
        var all = new List<HwFinding>();
        void Try(Func<IEnumerable<HwFinding>> f) { try { all.AddRange(f().Where(x => x != null)); } catch (Exception ex) { Log.Error("проверка железа", ex); } }
        bool lap = IsLaptop();
        var sessions = host?.Sessions()?.ToList() ?? SafeSessions();

        Try(() =>
        {
            var mem = MemoryProfile.Current();
            return new[] { From(MemoryProfile.Speed(mem)), From(MemoryProfile.Channels(mem)) };
        });
        Try(() => { var (s, has, name) = ReadScreens(); return new[] { MonitorFinding(lap, has, name, s) }; });
        Try(() => GpuLinkFindings(lap, ReadGpuLink(lap)));
        Try(() => { var g = MainGpuName(); return new[] { VramTempFinding(ReadVramTemp(lap), g) }; });
        Try(() =>
        {
            var disks = metrics?.Disks is { Count: > 0 } md ? md : Metrics.QueryDisks();
            var res = new List<HwFinding>();
            var games = GameLibrary.Load().Where(g => !string.IsNullOrEmpty(g.InstallDir)).Select(g => (g.Title, g.InstallDir));
            var free = FreeSsd(disks);
            foreach (var g in GamesOnHdd(games, disks, sessions).Where(x => x.DiskDips > 0))
            {
                var (bytes, done) = FolderSize(g.Dir);
                res.Add(GameHddFinding(g, bytes, done, free));
            }
            res.AddRange(DiskFindings(disks, TrimDisabled(), ReadPci().Where(p => p.BaseClass == 1).ToList(), Shell.IsAdmin()));
            return res;
        });
        Try(() =>
        {
            var s = settings ?? host?.Settings ?? LogMarks.Settings ?? AppSettings.Load();
            int.TryParse(s.Get(PsuKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out int psu);
            return new[] { PsuFinding(lap, Estimate(MemoryProfile.CpuName(), MainGpuName(), MeasuredGpuWatts(sessions)), psu) };
        });
        Try(() =>
        {
            var v = FanControl.Dust(false);
            if (v == null || !v.Dusty) return Array.Empty<HwFinding>();
            string title = L.T("Похоже, пора почистить компьютер от пыли");
            string text = v.Text.StartsWith(title + ":") ? Upper(v.Text[(title.Length + 1)..].Trim()) : v.Text;
            return new[] { new HwFinding { Level = Severity.Warn, Step = "Видеокарта", Title = title, Detail = text, Link = "sys:Вентиляторы", LinkText = L.T("Подробнее — в разделе «Вентиляторы»") } };
        });

        return all.Select(f => (DiagItem)new DiagFinding { Level = f.Level, Title = f.Title, Detail = f.Detail, Count = f.Count, Link = f.Link, LinkText = f.LinkText }).ToList();
    }

    /// <summary>
    /// Важные выводы — и в первичный осмотр (память добавляет сам шаг «Память»): кабель монитора в плате, урезанная шина видеокарты,
    /// TRIM, игры на HDD с просадками, блок питания на пределе. Вызывается из SystemScan.Run после шагов.
    /// </summary>
    public static void AddToScan(ScanReport r)
    {
        if (r == null) return;
        var all = new List<HwFinding>();
        void Try(Func<IEnumerable<HwFinding>> f) { try { all.AddRange(f().Where(x => x != null && x.Important)); } catch { } }
        bool lap = r.IsLaptop;
        Try(() => { var (s, has, name) = ReadScreens(); return new[] { MonitorFinding(lap, has, name, s) }; });
        Try(() => GpuLinkFindings(lap, ReadGpuLink(lap)));
        Try(() => DiskFindings(new List<PhysDisk>(r.Disks.Select(d => new PhysDisk { Name = d.Name, Media = d.Media, BusType = d.Bus, Letters = d.Letters.Select(x => x.TrimEnd(':')).ToList() })), TrimDisabled(), new List<PciLink>(), r.IsAdmin));
        Try(() =>
        {
            var sessions = SafeSessions();
            var disks = r.Disks.Select(d => new PhysDisk { Name = d.Name, Media = d.Kind == "HDD" || d.Kind.StartsWith("HDD") ? "HDD" : d.Media, BusType = d.Bus, Letters = d.Letters.Select(x => x.TrimEnd(':')).ToList() }).ToList();
            var games = GameLibrary.Load().Where(g => !string.IsNullOrEmpty(g.InstallDir)).Select(g => (g.Title, g.InstallDir));
            return GamesOnHdd(games, disks, sessions).Where(x => x.DiskDips > 0).Select(g => { var (b, d) = FolderSize(g.Dir, 3000); return GameHddFinding(g, b, d, FreeSsd(disks)); });
        });
        Try(() =>
        {
            int.TryParse((LogMarks.Settings ?? AppSettings.Load()).Get(PsuKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out int psu);
            if (psu <= 0) return Array.Empty<HwFinding>();
            return new[] { PsuFinding(lap, Estimate(r.CpuName, r.Gpus.FirstOrDefault()?.Name ?? "", MeasuredGpuWatts(SafeSessions())), psu) };
        });

        foreach (var f in all)
        {
            int page = f.Link.StartsWith("sys:") ? 7 : 4;
            r.Findings.Add(new ScanFinding { Severity = f.Level, Title = f.Title, Detail = f.Detail, TargetPage = page, TargetSection = page == 7 ? f.Link[4..] : "", Step = f.Step });
            var step = r.Steps.FirstOrDefault(s => s.Title == f.Step);
            if (step != null && f.Level < step.Worst) step.Worst = f.Level;
        }
    }

    private static List<GameSession> SafeSessions() { try { return SessionStore.Load(); } catch { return new List<GameSession>(); } }

    private static List<(string Letter, string Kind, long Free)> FreeSsd(IEnumerable<PhysDisk> disks)
    {
        var res = new List<(string, string, long)>();
        foreach (var d in disks ?? Enumerable.Empty<PhysDisk>())
        {
            if (d.Kind is not ("SSD" or "NVMe")) continue;
            foreach (var l in d.Letters)
                try { var di = new DriveInfo(l + ":\\"); if (di.IsReady) res.Add((l, d.Kind == "NVMe" ? "NVMe" : "SSD", di.AvailableFreeSpace)); } catch { }
        }
        return res;
    }

    private static string MainGpuName()
    {
        try
        {
            var a = GpuVendors.PickMain(Kmt.Enumerate().Select(k => k.ToAdapter()));
            return a?.Name ?? "";
        }
        catch { return ""; }
    }

    private static string Upper(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0], L.Culture) + s[1..];

    // ================================================================== самопроверка
    /// <summary>PulsePC.exe --feature-test HardwareChecks файл.txt — выводы на подставных данных и что видно на этом ПК (только чтение).</summary>
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        string fail = null;
        bool en = L.En;
        void Check(string what, bool ok) { sb.AppendLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) fail ??= what; }

        // --- монитор
        var plate = MonitorFinding(false, true, "NVIDIA GeForce RTX 4060", new List<Screen> { new() { Name = "Odyssey", Primary = true, OnIntegrated = true, Adapter = "Intel(R) UHD Graphics 770" } });
        Check("монитор в плате при отдельной видеокарте — крупное предупреждение и в осмотре", plate?.Level == Severity.Bad && plate.Important && (en || plate.Detail.Contains("переключите кабель")));
        Check("ноутбук с гибридной графикой — не предупреждаем", MonitorFinding(true, true, "RTX 4060 Laptop", new List<Screen> { new() { Primary = true, OnIntegrated = true } }) == null);
        Check("монитор в видеокарте — «в порядке»", MonitorFinding(false, true, "RTX 4060", new List<Screen> { new() { Primary = true, OnIntegrated = false } })?.Level == Severity.Ok);
        Check("второй монитор в плате — только к сведению", MonitorFinding(false, true, "RTX 4060", new List<Screen> { new() { Primary = true }, new() { Primary = false, OnIntegrated = true } })?.Level == Severity.Info);
        Check("нет отдельной видеокарты — молчим", MonitorFinding(false, false, "", new List<Screen> { new() { Primary = true, OnIntegrated = true } }) == null);

        // --- шина видеокарты
        var x4 = GpuLinkFindings(false, new GpuLink { Name = "NVIDIA GeForce RTX 3070", Vendor = "NVIDIA", CurWidth = 4, MaxWidth = 16, Gen = 4, GpuMaxGen = 4, HostMaxGen = 4, Bar1Mb = 8192 });
        Check("x4 при x16 — совет проверить слот и райзер", x4.Any(f => f.Level == Severity.Warn && f.Important && (en || f.Detail.Contains("райзер"))));
        var gen3 = GpuLinkFindings(false, new GpuLink { Name = "NVIDIA GeForce RTX 4060 Ti", Vendor = "NVIDIA", CurWidth = 8, MaxWidth = 8, Gen = 3, GpuMaxGen = 4, HostMaxGen = 3, Bar1Mb = 256 });
        Check("x8-карта на плате PCIe 3.0 — к сведению, про видеопамять; ReBAR выключен — с оговоркой про CSM",
            gen3.Any(f => f.Level == Severity.Info && (en || f.Detail.Contains("видеопамять"))) && gen3.Any(f => (en || f.Title == "Resizable BAR выключен") && (en || f.Detail.Contains("CSM"))) && !gen3.Any(f => f.Level <= Severity.Warn));
        Check("ноутбук — шину не проверяем", GpuLinkFindings(true, new GpuLink { CurWidth = 4, MaxWidth = 16 }).Count == 0);
        Check("BAR1 из nvidia-smi: 8192 МиБ", ParseBar1("    BAR1 Memory Usage\r\n        Total                                          : 8192 MiB\r\n        Used : 1 MiB") == 8192);
        Check("GTX 1660 с BAR1 256 — про ReBAR молчим (не поддерживает)", !GpuLinkFindings(false, new GpuLink { Name = "NVIDIA GeForce GTX 1660", Vendor = "NVIDIA", Bar1Mb = 256 }).Any());

        // --- видеопамять
        Check("GDDR6X 108 °C — перегрев; GDDR6 92 °C — горячая; NaN — ничего не показываем",
            VramTempFinding(108, "NVIDIA GeForce RTX 3080")?.Level == Severity.Bad && VramTempFinding(92, "NVIDIA GeForce RTX 4060 Ti")?.Level == Severity.Warn && VramTempFinding(double.NaN, "x") == null);

        // --- игры на HDD
        var disks = new List<PhysDisk>
        {
            new() { Name = "WDC WD20EZBX", Media = "HDD", BusType = "SATA", Letters = new() { "D" } },
            new() { Name = "Samsung SSD 990 PRO", Media = "SSD", BusType = "NVMe", Letters = new() { "C" } },
        };
        var sess = new List<GameSession>
        {
            new() { Game = "PUBG", Start = DateTime.Today, End = DateTime.Today.AddHours(1), Dips = new() { new FpsDip { Cause = "disk" }, new FpsDip { Cause = "disk" }, new FpsDip { Cause = "gpu" } } },
            new() { Game = "Deadlock", Start = DateTime.Today, End = DateTime.Today.AddHours(1), Dips = new() { new FpsDip { Cause = "cpu" } } },
        };
        var hdd = GamesOnHdd(new[] { ("PUBG: BATTLEGROUNDS", @"D:\SteamLibrary\steamapps\common\PUBG"), ("Deadlock", @"D:\SteamLibrary\steamapps\common\Deadlock"), ("CS2", @"C:\Games\CS2") }, disks, sess);
        Check("игры на HDD: PUBG (2 просадки из-за диска), Deadlock (0); CS2 на NVMe — нет", hdd.Count == 2 && hdd.First(g => g.Title.StartsWith("PUBG")).DiskDips == 2 && hdd.First(g => g.Title == "Deadlock").DiskDips == 0);
        var hf = GameHddFinding(hdd[0], 40L << 30, true, new List<(string, string, long)> { ("C", "NVMe", 300L << 30) });
        Check("совет перенести PUBG на SSD с размером и где хватит места — " + hf?.Detail, hf?.Level == Severity.Warn && (en || hf.Detail.Contains($"40,0{Nb}ГБ") && hf.Detail.Contains("C:")));
        Check("игра на HDD без просадок из-за диска — совета нет (он в «Совете по апгрейду»)", GameHddFinding(hdd.First(g => g.Title == "Deadlock"), 1, true, null) == null);

        // --- блок питания
        var est = Estimate("12th Gen Intel(R) Core(TM) i5-12400F", "NVIDIA GeForce RTX 4060 Ti", 150);
        Check($"оценка: 4060 Ti 165 Вт (паспорт > замера 150) + i5-12400F 117 + 60 = {est.Total:0}, блок от {est.Recommended}", Math.Abs(est.Total - 342) < 1 && est.Recommended == 450 && est.Gpu == 165);
        Check("блок 400 Вт при 342 — запас 14,5 % — совет", PsuFinding(false, est, 400)?.Level == Severity.Warn);
        Check("блок 650 Вт — хватает", PsuFinding(false, est, 650)?.Level == Severity.Ok);
        Check("блок 300 Вт — может не потянуть", PsuFinding(false, est, 300)?.Level == Severity.Bad);
        Check("ноутбук — про блок питания молчим", PsuFinding(true, est, 300) == null);
        Check("мощность не указана — только оценка, с пометкой «оценка»", PsuFinding(false, est, 0) is { Level: Severity.Ok } p0 && (en || p0.Detail.Contains("оценка") && p0.Detail.Contains("450")));
        var est2 = Estimate("AMD Ryzen 7 7800X3D 8-Core Processor", "NVIDIA GeForce RTX 4090", 0);
        Check($"7800X3D + RTX 4090: {est2.Total:0} Вт, блок от {est2.Recommended}", est2.Gpu == 450 && Math.Abs(est2.Cpu - 162) < 1 && est2.Recommended == 850);

        // --- диски
        Check("fsutil: «NTFS DisableDeleteNotify = 0 (…)» — TRIM включён, «= 1» — выключен",
            ParseTrim("NTFS DisableDeleteNotify = 0  (Allows TRIM operations to be sent to the storage device)\r\nReFS DisableDeleteNotify = 0") == false &&
            ParseTrim("NTFS DisableDeleteNotify = 1  (Отключено)") == true && ParseTrim("ошибка") == null);
        var dd = DiskFindings(new List<PhysDisk>
        {
            new() { Name = "Samsung SSD 990 PRO", Media = "SSD", BusType = "NVMe", Letters = new() { "C" }, Health = "Healthy", Temp = 52, TempMax = 54, Wear = 3 },
        }, true, new List<PciLink> { new() { BaseClass = 1, SubClass = 8, Child = "Samsung SSD 990 PRO", CurWidth = 2, MaxWidth = 4, CurGen = 4, MaxGen = 4 } }, true);
        Check("TRIM выключен — совет (команда, программа сама не меняет)", dd.Any(f => f.Level == Severity.Warn && f.Important && (en || f.Detail.Contains("fsutil behavior set DisableDeleteNotify 0"))));
        Check("52 °C при пределе 54 — у предела", dd.Any(f => en ? f.Title.Contains("temperature") : f.Title.Contains("у предела температуры")));
        Check("NVMe на x2 вместо x4 — совет про разъём M.2", dd.Any(f => en ? f.Title.Contains("x2") : f.Title.Contains("x2 вместо x4")));
        Check("износ и температура SSD — строкой «в порядке»", dd.Any(f => f.Level == Severity.Ok && (en || f.Detail.Contains("износ 3"))));
        Check("без прав и без сведений — честное «нужны права»",
            DiskFindings(new List<PhysDisk> { new() { Name = "X", Media = "SSD", Letters = new() { "C" } } }, false, null, false).Any(f => en || f.Title.Contains("нужны права")));
        Check("горячий диск 65 °C уже есть в «Диагностике» — второй строки нет",
            !DiskFindings(new List<PhysDisk> { new() { Name = "X", Media = "SSD", BusType = "NVMe", Letters = new() { "C" }, Health = "Healthy", Temp = 65, TempMax = 70 } }, false, null, true).Any(f => f.Level == Severity.Warn));
        Check("одинаковые игры: «PUBG» и «PUBG: BATTLEGROUNDS» — да, «CS» и «CS2» — нет", SameGame("PUBG", "PUBG: BATTLEGROUNDS") && !SameGame("CS", "CS2"));

        // --- этот ПК, только чтение
        sb.AppendLine();
        sb.AppendLine("Что видно на этом ПК (только чтение):");
        bool lap = IsLaptop();
        sb.AppendLine($"  ноутбук: {(lap ? "да" : "нет")} ({LaptopCheck.Reason})");
        try
        {
            var (screens, has, dname) = ReadScreens();
            sb.AppendLine($"  экраны: {screens.Count}; отдельная видеокарта: {(has ? dname : "нет")}");
            foreach (var s in screens) sb.AppendLine($"    {s.Name}: адаптер «{s.Adapter}», {(s.OnIntegrated ? "встроенная графика" : "отдельная")}{(s.Primary ? ", основной" : "")}");
            var mf = MonitorFinding(lap, has, dname, screens);
            sb.AppendLine($"    вывод: {(mf == null ? "нет" : $"[{mf.Level}] {mf.Title}")}");
        }
        catch (Exception ex) { sb.AppendLine("  экраны: ошибка " + ex.Message); }
        foreach (var p in ReadPci())
            sb.AppendLine($"  PCI {p.BaseClass}/{p.SubClass}: {p.Name}{(p.Child.Length > 0 ? " → " + p.Child : "")} — x{p.CurWidth} из x{p.MaxWidth}, поколение {p.CurGen} из {p.MaxGen}");
        var gl = ReadGpuLink(lap);
        sb.AppendLine(gl == null ? "  шина видеокарты: отдельной видеокарты нет" : $"  шина видеокарты: {gl.Name} x{gl.CurWidth}/x{gl.MaxWidth}, договорились {gl.Gen}, карта {gl.GpuMaxGen}, плата {gl.HostMaxGen}, BAR1 {gl.Bar1Mb} МиБ");
        foreach (var f in GpuLinkFindings(lap, gl)) sb.AppendLine($"    [{f.Level}] {f.Title}");
        double vt = ReadVramTemp(lap);
        sb.AppendLine($"  температура видеопамяти: {(double.IsNaN(vt) ? "драйвер не отдаёт — не показывается" : vt.ToString("0") + " °C")}");
        var tr = TrimDisabled();
        sb.AppendLine($"  TRIM: {(tr == null ? "не прочитан" : tr.Value ? "выключен" : "включён")}");
        var real = SafeSessions();
        var est3 = Estimate(MemoryProfile.CpuName(), MainGpuName(), MeasuredGpuWatts(real));
        sb.AppendLine($"  блок питания (оценка): видеокарта {est3.Gpu:0} Вт ({(est3.GpuMeasured ? "измерено" : est3.GpuKnown ? "паспорт" : "неизвестно")}), процессор {est3.Cpu:0}, остальное {est3.Rest:0} → {est3.Total:0} Вт, блок от {est3.Recommended} Вт");
        try
        {
            var games = GameLibrary.Load().Where(g => !string.IsNullOrEmpty(g.InstallDir)).Select(g => (g.Title, g.InstallDir)).ToList();
            var gd = GamesOnHdd(games, Metrics.QueryDisks(), real);
            sb.AppendLine($"  игр в библиотеке: {games.Count}, на HDD: {gd.Count}{(gd.Count > 0 ? " (" + string.Join(", ", gd.Select(g => $"{g.Title}: просадок из-за диска {g.DiskDips}")) + ")" : "")}");
        }
        catch (Exception ex) { sb.AppendLine("  игры: ошибка " + ex.Message); }

        sb.AppendLine();
        sb.AppendLine("Строки «Диагностики» от проверок железа (только чтение):");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var d in Diagnose(null)) sb.AppendLine($"  [{d.Level}] {d.Title}{(d.Count.Length > 0 ? " · " + d.Count : "")}\n         {d.Detail}");
        sb.AppendLine($"  (заняло {sw.Elapsed.TotalSeconds:0.0} с)");
        // Полный первичный осмотр (только чтение, ~30 с) — по переменной PULSEPC_W10A_SCAN=1.
        if (Environment.GetEnvironmentVariable("PULSEPC_W10A_SCAN") == "1")
        {
            sb.AppendLine();
            sb.AppendLine(SystemScan.SelfCheck());
        }
        return (fail == null ? "OK" : "FAIL: " + fail) + "\n" + sb;
    }

    // ================================================================== Windows
    [DllImport("powrprof.dll")] private static extern uint PowerDeterminePlatformRoleEx(uint version);

    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct PathSource { public Luid Adapter; public uint Id; public uint ModeIdx; public uint Status; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PathTarget
    {
        public Luid Adapter; public uint Id; public uint ModeIdx; public uint Tech; public uint Rotation; public uint Scaling;
        public uint RefreshNum, RefreshDen; public uint ScanLine; public int Available; public uint Status;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PathInfo { public PathSource Source; public PathTarget Target; public uint Flags; }
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct ModeInfo
    {
        [FieldOffset(0)] public uint InfoType;
        [FieldOffset(4)] public uint Id;
        [FieldOffset(8)] public Luid Adapter;
        [FieldOffset(28)] public int SrcX;
        [FieldOffset(32)] public int SrcY;
    }

    private static bool QueryPaths(out PathInfo[] paths, out ModeInfo[] modes)
    {
        paths = Array.Empty<PathInfo>(); modes = Array.Empty<ModeInfo>();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(2, out uint np, out uint nm) != 0) return false;
            paths = new PathInfo[np]; modes = new ModeInfo[nm];
            int r = QueryDisplayConfig(2, ref np, paths, ref nm, modes, IntPtr.Zero);
            if (r == 122) continue;
            if (r != 0) return false;
            Array.Resize(ref paths, (int)np); Array.Resize(ref modes, (int)nm);
            return true;
        }
        return false;
    }

    private static string TargetName(Luid adapter, uint id)
    {
        const int size = 420;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buf, size);
            Marshal.WriteInt32(buf, 0, 2); Marshal.WriteInt32(buf, 4, size);
            Marshal.WriteInt32(buf, 8, (int)adapter.Low); Marshal.WriteInt32(buf, 12, adapter.High); Marshal.WriteInt32(buf, 16, (int)id);
            if (DisplayConfigGetDeviceInfo(buf) != 0) return "";
            uint flags = (uint)Marshal.ReadInt32(buf, 20);
            return (flags & 1) != 0 ? (Marshal.PtrToStringUni(buf + 36) ?? "").Trim() : "";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, [Out] ModeInfo[] modes, IntPtr topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(IntPtr packet);

    // --- SetupAPI: свойства PCI Express (DEVPKEY_PciDevice_*, devpkey.h)
    [StructLayout(LayoutKind.Sequential)] private struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct DEVPROPKEY { public Guid fmtid; public uint pid; }
    private static readonly Guid PciFmt = new("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62");
    private static readonly Guid DeviceFmt = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static DEVPROPKEY DeviceKey(uint pid) => new() { fmtid = DeviceFmt, pid = pid };

    private static int PciProp(IntPtr set, ref SP_DEVINFO_DATA data, uint pid)
    {
        var key = new DEVPROPKEY { fmtid = PciFmt, pid = pid };
        var buf = new byte[8];
        return SetupDiGetDevicePropertyW(set, ref data, ref key, out _, buf, (uint)buf.Length, out uint req, 0) && req >= 4 ? BitConverter.ToInt32(buf, 0) : -1;
    }

    private static string DevString(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
    {
        var buf = new byte[1024];
        return SetupDiGetDevicePropertyW(set, ref data, ref key, out _, buf, (uint)buf.Length, out uint req, 0) && req >= 2
            ? Encoding.Unicode.GetString(buf, 0, (int)Math.Min(req, (uint)buf.Length)).TrimEnd('\0').Trim() : "";
    }

    private static string NodeString(uint node, DEVPROPKEY key)
    {
        var buf = new byte[1024];
        uint size = (uint)buf.Length;
        return CM_Get_DevNode_PropertyW(node, ref key, out _, buf, ref size, 0) == 0 && size >= 2
            ? Encoding.Unicode.GetString(buf, 0, (int)Math.Min(size, (uint)buf.Length)).TrimEnd('\0').Trim() : "";
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key, out uint type, byte[] buffer, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, int size, out int required);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Get_Child(out uint child, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, byte[] buffer, ref uint size, uint flags);

    // --- NVAPI: только документированные функции (NvAPI_Initialize, NvAPI_EnumPhysicalGPUs, NvAPI_GPU_GetThermalSettings)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr NvQuery(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvPlain();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvEnum([Out] IntPtr[] handles, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvThermal(IntPtr gpu, int sensor, IntPtr data);

    /// <summary>Датчик «память» (NVAPI_THERMAL_TARGET_MEMORY = 2) из NvAPI_GPU_GetThermalSettings; у большинства GeForce его нет — тогда NaN.</summary>
    private static double NvMemoryTemp()
    {
        try
        {
            string dll = Path.Combine(Environment.SystemDirectory, "nvapi64.dll");
            if (!Environment.Is64BitProcess || !File.Exists(dll)) return double.NaN;
            if (!NativeLibrary.TryLoad(dll, out IntPtr lib) || !NativeLibrary.TryGetExport(lib, "nvapi_QueryInterface", out IntPtr entry)) return double.NaN;
            var query = Marshal.GetDelegateForFunctionPointer<NvQuery>(entry);
            T Fn<T>(uint id) where T : Delegate { IntPtr p = query(id); return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p); }
            var init = Fn<NvPlain>(0x0150E828);
            var en = Fn<NvEnum>(0xE5AC921F);
            var th = Fn<NvThermal>(0xE3640A56);
            if (init == null || en == null || th == null || init() != 0) return double.NaN;
            var handles = new IntPtr[64];
            if (en(handles, out int n) != 0 || n <= 0) return double.NaN;
            const int size = 8 + 3 * 20;
            IntPtr b = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(new byte[size], 0, b, size);
                Marshal.WriteInt32(b, 0, size | (2 << 16));
                if (th(handles[0], 15, b) != 0) return double.NaN;
                int count = Math.Min(Marshal.ReadInt32(b, 4), 3);
                for (int k = 0; k < count; k++)
                {
                    int o = 8 + k * 20;
                    if (Marshal.ReadInt32(b, o + 16) == 2) { int t = Marshal.ReadInt32(b, o + 12); return t is > 0 and < 150 ? t : double.NaN; }
                }
            }
            finally { Marshal.FreeHGlobal(b); }
        }
        catch { }
        return double.NaN;
    }
}
