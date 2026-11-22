using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PulsePC.Services;

public class RamModule
{
    public string Slot = "", Maker = "", Part = "";
    public double SizeGb;
    public int Mhz, RatedMhz;
}

public class GpuAdapter
{
    public string Name = "", Driver = "", Vendor = "";   // Vendor: NVIDIA, AMD, Intel, Qualcomm или пусто
    public double VramGb;
    public bool Integrated;
    /// <summary>Код устройства Windows («PCI\VEN_10DE&amp;DEV_2803…»); у виртуальных адаптеров — не PCI.</summary>
    public string Pnp = "";
}

/// <summary>
/// Паспорт компьютера: процессор, видеокарты, плата, BIOS, память, системный диск. Читается один раз,
/// прав администратора не требует и не зависит от производителя железа.
/// </summary>
public sealed class HardwareInfo
{
    public string CpuName = "", Board = "", BoardMaker = "", Bios = "", BiosDate = "", Socket = "", RamType = "", SystemDisk = "";
    public int Cores, Threads, MaxMhz;
    public List<RamModule> Ram = new();
    public List<GpuAdapter> Gpus = new();
    public bool Loaded;

    public static HardwareInfo Query()
    {
        var info = new HardwareInfo();
        const string script =
            "$b = Get-CimInstance Win32_BaseBoard; $bi = Get-CimInstance Win32_BIOS; $p = Get-CimInstance Win32_Processor | Select-Object -First 1; " +
            "$m = @(Get-CimInstance Win32_PhysicalMemory | ForEach-Object { [pscustomobject]@{ Slot=\"$($_.DeviceLocator)\"; Maker=\"$($_.Manufacturer)\"; " +
            "Part=\"$($_.PartNumber)\".Trim(); SizeGb=[math]::Round($_.Capacity/1GB); Mhz=[int]$_.ConfiguredClockSpeed; Rated=[int]$_.Speed; Type=[int]$_.SMBIOSMemoryType } }); " +
            "$g = @(Get-CimInstance Win32_VideoController | ForEach-Object { [pscustomobject]@{ Name=\"$($_.Name)\"; Driver=\"$($_.DriverVersion)\"; Pnp=\"$($_.PNPDeviceID)\" } }); " +
            "$sd = ''; try { $sd = (Get-Partition -DriveLetter $env:SystemDrive[0] -ErrorAction Stop | Get-Disk).FriendlyName } catch {}; " +
            "[pscustomobject]@{ Cpu=\"$($p.Name)\"; Board=\"$($b.Product)\"; BoardMaker=\"$($b.Manufacturer)\"; Bios=\"$($bi.SMBIOSBIOSVersion)\"; " +
            "BiosDate=$(if ($bi.ReleaseDate) { $bi.ReleaseDate.ToString('dd.MM.yyyy', [Globalization.CultureInfo]::InvariantCulture) } else { '' }); Socket=\"$($p.SocketDesignation)\"; " +
            "Cores=[int]$p.NumberOfCores; Threads=[int]$p.NumberOfLogicalProcessors; MaxMhz=[int]$p.MaxClockSpeed; Ram=$m; Gpu=$g; SysDisk=\"$sd\" } | ConvertTo-Json -Compress -Depth 3";
        try
        {
            var (code, output) = Shell.PowerShell(script, 60000);
            int start = output?.IndexOf('{') ?? -1;
            if (code != 0 || start < 0) return info;
            using var doc = JsonDocument.Parse(output[start..]);
            var r = doc.RootElement;
            static string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString().Trim() : "";
            static double N(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            static List<JsonElement> Arr(JsonElement r, string n) =>
                !r.TryGetProperty(n, out var a) ? new() : a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToList() : a.ValueKind == JsonValueKind.Object ? new() { a } : new();

            info.CpuName = S(r, "Cpu"); info.Board = S(r, "Board"); info.BoardMaker = S(r, "BoardMaker"); info.Bios = S(r, "Bios"); info.BiosDate = S(r, "BiosDate");
            info.Socket = S(r, "Socket"); info.Cores = (int)N(r, "Cores"); info.Threads = (int)N(r, "Threads"); info.MaxMhz = (int)N(r, "MaxMhz");
            info.SystemDisk = S(r, "SysDisk");
            int memType = 0;
            var rawRam = Arr(r, "Ram").ToList();
            foreach (var m in rawRam) memType = Math.Max(memType, (int)N(m, "Type"));
            foreach (var m in rawRam)
            {
                if (N(m, "SizeGb") <= 0) continue;      // пустые записи бывают у распаянной памяти и в виртуальных машинах
                info.Ram.Add(new RamModule
                {
                    Slot = S(m, "Slot"), Maker = RamMaker(S(m, "Maker")), Part = S(m, "Part"), SizeGb = N(m, "SizeGb"),
                    Mhz = RamMts(memType, (int)N(m, "Mhz"), (int)N(m, "Rated")), RatedMhz = RamMts(memType, (int)N(m, "Rated"), 0),
                });
            }
            info.RamType = RamTypeFor(memType, info.Ram.Select(x => Math.Max(x.Mhz, x.RatedMhz)).DefaultIfEmpty(0).Max(), info.Ram.Select(x => x.Part));
            var vram = VramFromRegistry();
            foreach (var g in Arr(r, "Gpu"))
            {
                string name = S(g, "Name"), pnp = S(g, "Pnp");
                if (name == "" || GpuVendors.IsVirtual(name)) continue;
                // Не PCI — значит, не видеокарта: второй экран по сети, удалённый доступ, USB-адаптер монитора.
                if (pnp != "" && !pnp.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) && !pnp.StartsWith(@"ACPI\", StringComparison.OrdinalIgnoreCase)) continue;
                var a = new GpuAdapter { Name = name, Driver = S(g, "Driver"), Pnp = pnp };
                a.Vendor = GpuVendors.FromPnp(pnp) is string ven && ven != "" ? ven : VendorOf(name);
                a.VramGb = vram.TryGetValue(name, out double v) ? v : 0;
                a.Integrated = GpuVendors.IsIntegrated(a.Vendor, name);
                info.Gpus.Add(a);
            }
            info.Loaded = true;
        }
        catch { }
        return info;
    }

    /// <summary>
    /// Главная видеокарта — на ней идут игры: настоящая (не виртуальная), отдельная раньше встроенной, затем с большей видеопамятью.
    /// На ноутбуке со встроенной и отдельной графикой — отдельная; на компьютере без отдельной — встроенная.
    /// </summary>
    public GpuAdapter MainGpu => GpuVendors.PickMain(Gpus);

    /// <summary>Видеокарта только встроенная (отдельной нет).</summary>
    public bool OnlyIntegratedGpu => Gpus.Count > 0 && Gpus.All(g => g.Integrated);

    // ------------------------------------------------------------------ память
    /// <summary>
    /// Тип памяти по коду SMBIOS (Win32_PhysicalMemory.SMBIOSMemoryType): 24 DDR3, 26 DDR4, 29 LPDDR3, 30 LPDDR4, 34 DDR5, 35 LPDDR5 (и LPDDR5X).
    /// Если кода нет — по маркировке планок и скорости.
    /// </summary>
    public static string RamTypeFor(int smbiosType, int mts, IEnumerable<string> parts = null)
    {
        string byCode = smbiosType switch
        {
            18 => "DDR", 19 => "DDR2", 24 => "DDR3", 26 => "DDR4", 27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4",
            34 => "DDR5", 35 => "LPDDR5", _ => "",
        };
        if (byCode != "") return byCode;
        // G.Skill «F5-6000…», Corsair «CMK32GX5M2…», Kingston Fury «KF556…» — маркировка DDR5; «F4-», «X4M» — DDR4.
        foreach (string p in parts ?? Enumerable.Empty<string>())
        {
            if (Regex.IsMatch(p ?? "", @"^F5-|X5M\d|^KF5[4-8]\d|DDR5", RegexOptions.IgnoreCase)) return "DDR5";
            if (Regex.IsMatch(p ?? "", @"^F4-|X4M\d|^KF4\d|DDR4", RegexOptions.IgnoreCase)) return "DDR4";
        }
        return mts >= 4800 ? "DDR5" : mts >= 2133 ? "DDR4" : mts > 0 ? "DDR3" : "";
    }

    /// <summary>
    /// Скорость памяти в МТ/с (так её пишут на планках: DDR4-3200, DDR5-6000). Старые BIOS отдают половину — реальную частоту в МГц;
    /// для DDR4 и DDR5 её легко узнать: таких низких скоростей у этих типов не бывает. 0 настроенной скорости — берём паспортную.
    /// </summary>
    public static int RamMts(int smbiosType, int configured, int rated)
    {
        int v = configured > 0 ? configured : rated;
        if (v <= 0) return 0;
        bool ddr4 = smbiosType is 26 or 30, ddr5 = smbiosType is 34 or 35;
        if (ddr4 && v < 1300) v *= 2;
        if (ddr5 && v < 3000) v *= 2;
        return v;
    }

    /// <summary>Производитель планки: Windows часто отдаёт код JEDEC («80CE» — Samsung) или заглушку («Unknown»).</summary>
    public static string RamMaker(string raw)
    {
        string m = (raw ?? "").Trim();
        if (m == "" || Regex.IsMatch(m, @"^(Unknown|Undefined|Not Specified|Manufacturer\s*\d*|To be filled.*|0+|F+)$", RegexOptions.IgnoreCase)) return "";
        string hex = Regex.Replace(m, @"^0x", "", RegexOptions.IgnoreCase).ToUpperInvariant();
        (string Code, string Name)[] jedec =
        {
            ("80CE", "Samsung"), ("80AD", "SK hynix"), ("802C", "Micron"), ("859B", "Crucial"), ("04CD", "G.Skill"), ("0198", "Kingston"),
            ("029E", "Corsair"), ("04EF", "TeamGroup"), ("04CB", "ADATA"), ("8502", "Patriot"), ("0783", "Transcend"), ("051A", "Lexar"),
        };
        foreach (var (code, name) in jedec)
            if (Regex.IsMatch(hex, "^(" + code + ")+(0000)?(" + code + ")?$") || hex.StartsWith(code + "0000")) return name;
        if (m.StartsWith("Hynix", StringComparison.OrdinalIgnoreCase) || m.StartsWith("SK Hynix", StringComparison.OrdinalIgnoreCase)) return "SK hynix";
        if (m.StartsWith("Micron", StringComparison.OrdinalIgnoreCase)) return "Micron";
        if (m.StartsWith("Samsung", StringComparison.OrdinalIgnoreCase)) return "Samsung";
        if (Regex.IsMatch(m, @"^G\.?\s?Skill", RegexOptions.IgnoreCase)) return "G.Skill";
        if (Regex.IsMatch(m, @"^Team\b", RegexOptions.IgnoreCase)) return "TeamGroup";
        return m;
    }

    public static string VendorOf(string name) => GpuVendors.FromName(name);

    /// <summary>Паспорт этого компьютера так, как его видит программа (только чтение). PulsePC.exe --feature-test HardwareInfo файл.txt</summary>
    public static string SelfTest()
    {
        var i = Query();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(i.Loaded ? "OK" : "FAIL: паспорт не прочитан");
        sb.AppendLine($"Процессор: {i.CpuName} → «{ShortCpu(i.CpuName)}», {CpuVendor(i.CpuName)}, {i.Cores} ядер / {i.Threads} потоков, до {i.MaxMhz} МГц; " +
                      $"разъём (Windows) «{i.Socket}» → {i.SocketName}");
        sb.AppendLine($"Плата: {i.BoardMaker} {i.Board} → {i.BoardShort}; чипсет {i.Chipset}; BIOS {i.Bios} от {i.BiosDate}");
        sb.AppendLine($"Память: {i.RamType}, планок {i.Ram.Count}");
        foreach (var m in i.Ram) sb.AppendLine($"  {m.Slot}: {m.SizeGb:0} ГБ, {m.Maker} {m.Part}, {m.Mhz} МТ/с (паспорт {m.RatedMhz})");
        sb.AppendLine($"Видеокарт: {i.Gpus.Count}; главная — {i.MainGpu?.Name ?? "нет"}; только встроенная: {i.OnlyIntegratedGpu}");
        foreach (var g in i.Gpus) sb.AppendLine($"  {g.Name} [{g.Vendor}, {(g.Integrated ? "встроенная" : "отдельная")}] {g.VramGb:0.#} ГБ, драйвер {g.Driver}, {g.Pnp}");
        sb.AppendLine($"Системный диск: {i.SystemDisk}");
        return sb.ToString();
    }

    /// <summary>Объём видеопамяти по имени адаптера (Win32_VideoController ограничен 4 ГБ, поэтому из реестра драйвера).</summary>
    private static Dictionary<string, double> VramFromRegistry()
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return map;
            foreach (var sub in cls.GetSubKeyNames().Where(n => Regex.IsMatch(n, @"^\d{4}$")))
            {
                try
                {
                    using var k = cls.OpenSubKey(sub);
                    string desc = k?.GetValue("DriverDesc") as string;
                    object raw = k?.GetValue("HardwareInformation.qwMemorySize") ?? k?.GetValue("HardwareInformation.MemorySize");
                    long bytes = raw switch { long l => l, int i => (uint)i, byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0), byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0), _ => 0 };
                    if (!string.IsNullOrEmpty(desc) && bytes > 0) map[desc] = Math.Round(bytes / (1024.0 * 1024 * 1024), 1);
                }
                catch { }
            }
        }
        catch { }
        return map;
    }

    // ------------------------------------------------------------------ короткие имена для экрана
    /// <summary>«12th Gen Intel(R) Core(TM) i5-12400F» → «Core i5-12400F»; «AMD Ryzen 7 7800X3D 8-Core Processor» → «Ryzen 7 7800X3D».</summary>
    /// <remarks>
    /// Единственная функция короткого имени процессора в программе (раньше их было четыре, и одна резала «12-Core» до «12-»).
    /// withVendor — с производителем впереди: «Intel Core i5-12400F», «AMD Ryzen 9 5900X».
    /// Модель не теряется никогда: если после чистки ничего не осталось, возвращается исходная строка без мусора.
    /// </remarks>
    public static string ShortCpu(string name, bool withVendor = false)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        string s = Regex.Replace(name, @"\((R|TM|tm|r)\)|®|™", " ");
        // «Snapdragon(R) X Elite - X1E78100 - Qualcomm(R) Oryon(TM) CPU» → «Snapdragon X Elite X1E78100».
        var sd = Regex.Match(s, @"(Snapdragon\s+[^-]+?)\s*-\s*(\w+)", RegexOptions.IgnoreCase);
        if (sd.Success)
        {
            string snap = Regex.Replace(sd.Groups[1].Value + " " + sd.Groups[2].Value, @"\s+", " ").Trim();
            return withVendor ? "Qualcomm " + snap : snap;
        }
        string vendor = Regex.IsMatch(s, @"\bIntel\b", RegexOptions.IgnoreCase) ? "Intel"
                      : Regex.IsMatch(s, @"\bAMD\b", RegexOptions.IgnoreCase) ? "AMD" : "";
        s = Regex.Replace(s, @"\s*@.*$", "");                                                     // «@ 3.20GHz»
        s = Regex.Replace(s, @"\s+(with|w/)\s+(AMD\s+)?Radeon\b.*$", "", RegexOptions.IgnoreCase); // встроенная графика AMD
        s = Regex.Replace(s, @"\b\d+(th|nd|rd|st)\s+Gen(eration)?\b", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\b\d+-Cores?\b", "", RegexOptions.IgnoreCase);                    // «12-Core», «64-Cores»
        s = Regex.Replace(s, @"\b(Processor|CPU|Intel|AMD|Genuine)\b", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+", " ").Trim(' ', '-', ',');
        if (s == "") s = Regex.Replace(Regex.Replace(name, @"\((R|TM|tm|r)\)|®|™", " "), @"\s+", " ").Trim();
        return withVendor && vendor != "" && !s.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) ? vendor + " " + s : s;
    }

    /// <summary>Подпись процессора на чертеже: «Core i5-12400F» → «i5-12400F» (коротко), но «Core Ultra 7 155H» и «Core 5 120U» — целиком.</summary>
    public static string BlueprintCpu(string name) => Regex.Replace(ShortCpu(name), @"^Core\s+(?=i\d)", "");

    /// <summary>«NVIDIA GeForce RTX 4060 Ti» → «RTX 4060 Ti»; «AMD Radeon RX 7800 XT» → «RX 7800 XT».</summary>
    public static string ShortGpu(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        string s = Regex.Replace(name, @"\((R|TM)\)|®|™", "");
        s = Regex.Replace(s, @"\bNVIDIA\b|\bGeForce\b|\bAMD\b|\bRadeon\b(?=\s+RX)|\bIntel\b|\bGraphics\b(?=\s+\w)", "", RegexOptions.IgnoreCase);
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// <summary>Надпись бренда на кожухе для чертежа.</summary>
    public static string GpuBrand(string name) =>
        Regex.IsMatch(name ?? "", "RTX", RegexOptions.IgnoreCase) ? "GEFORCE RTX"
        : Regex.IsMatch(name ?? "", "GTX|GeForce", RegexOptions.IgnoreCase) ? "GEFORCE"
        : Regex.IsMatch(name ?? "", "Radeon|RX ", RegexOptions.IgnoreCase) ? "RADEON"
        : Regex.IsMatch(name ?? "", "Arc", RegexOptions.IgnoreCase) ? "INTEL ARC" : "";

    /// <summary>Производитель платы коротко: «Micro-Star International Co., Ltd.» → «MSI».</summary>
    public string MakerShort
    {
        get
        {
            string m = BoardMaker ?? "";
            if (m.StartsWith("Micro-Star", StringComparison.OrdinalIgnoreCase)) return "MSI";
            if (m.StartsWith("ASUSTeK", StringComparison.OrdinalIgnoreCase)) return "ASUS";
            if (m.StartsWith("Gigabyte", StringComparison.OrdinalIgnoreCase)) return "GIGABYTE";
            if (m.StartsWith("ASRock", StringComparison.OrdinalIgnoreCase)) return "ASRock";
            if (m.StartsWith("Hewlett", StringComparison.OrdinalIgnoreCase) || m.Equals("HP", StringComparison.OrdinalIgnoreCase)) return "HP";
            if (m.StartsWith("LENOVO", StringComparison.OrdinalIgnoreCase)) return "Lenovo";
            if (m.StartsWith("Dell", StringComparison.OrdinalIgnoreCase)) return "Dell";
            return m.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        }
    }

    public string BoardShort => string.IsNullOrEmpty(MakerShort) ? Board : MakerShort + " " + Board;

    /// <summary>Разъём процессора: Windows часто называет его кодом платы («U3E1»), поэтому по модели процессора.</summary>
    public string SocketName => SocketFor(CpuName, Socket);

    /// <summary>Производитель процессора по названию: Intel, AMD, Qualcomm или пусто.</summary>
    public static string CpuVendor(string cpu) =>
        Regex.IsMatch(cpu ?? "", @"\bAMD\b|Ryzen|Athlon|Threadripper|EPYC", RegexOptions.IgnoreCase) ? "AMD"
        : Regex.IsMatch(cpu ?? "", @"Intel|\bCore\b|Pentium|Celeron|Xeon", RegexOptions.IgnoreCase) ? "Intel"
        : Regex.IsMatch(cpu ?? "", @"Snapdragon|Qualcomm|Oryon", RegexOptions.IgnoreCase) ? "Qualcomm" : "";

    /// <summary>
    /// Разъём по модели процессора. Процессоры ноутбуков (суффиксы H, HX, HS, U, P, V, Y, G7 и т. п.) распаяны на плате — «BGA».
    /// Пустая строка — определить не удалось (тогда на экране прочерк).
    /// </summary>
    public static string SocketFor(string cpu, string wmiSocket = "")
    {
        string c = Regex.Replace(cpu ?? "", @"\((R|TM|tm|r)\)|®|™", " ");
        c = Regex.Replace(c, @"\s+", " ").Trim();
        Match m;
        if (Regex.IsMatch(c, @"Snapdragon|Qualcomm|Oryon", RegexOptions.IgnoreCase)) return "BGA";

        // --- AMD
        if ((m = Regex.Match(c, @"Threadripper (?:PRO )?(\d)\d{3}(WX)?", RegexOptions.IgnoreCase)).Success)
        {
            char gen = m.Groups[1].Value[0];
            bool wx = m.Groups[2].Success;
            return gen is '1' or '2' ? "TR4" : gen == '3' ? (wx ? "sWRX8" : "sTRX4") : gen == '5' ? "sWRX8" : "sTR5";
        }
        if (Regex.IsMatch(c, @"Ryzen AI|Ryzen Z\d", RegexOptions.IgnoreCase)) return "BGA";
        if ((m = Regex.Match(c, @"Ryzen \d (?:PRO )?(\d)(\d{3})([A-Z0-9]*)", RegexOptions.IgnoreCase)).Success)
        {
            string suffix = m.Groups[3].Value.ToUpperInvariant();
            if (Regex.IsMatch(suffix, @"^(H|HS|HX|U|M|HX3D|C)$")) return "BGA";
            char gen = m.Groups[1].Value[0];
            if (gen is >= '1' and <= '5') return "AM4";
            if (gen is '7' or '8' or '9') return "AM5";
            return "BGA";       // 6000 — только ноутбуки
        }
        if ((m = Regex.Match(c, @"Athlon (?:Gold |Silver |PRO )?(\d{3,4})([A-Z]*)", RegexOptions.IgnoreCase)).Success)
            return Regex.IsMatch(m.Groups[2].Value, "^(U|H|C)$", RegexOptions.IgnoreCase) ? "BGA" : "AM4";

        // --- Intel
        if ((m = Regex.Match(c, @"Core Ultra \d (\d)(\d{2})([A-Z]*)", RegexOptions.IgnoreCase)).Success)
        {
            string suffix = m.Groups[3].Value.ToUpperInvariant();
            bool desktop = m.Groups[1].Value == "2" && Regex.IsMatch(suffix, "^(K|KF|F|T|)$");
            return desktop ? "LGA1851" : "BGA";
        }
        if ((m = Regex.Match(c, @"\bi[3579]-(\d{4,5})([A-Z]*)(\d?)", RegexOptions.IgnoreCase)).Success)
        {
            string num = m.Groups[1].Value, suffix = m.Groups[2].Value.ToUpperInvariant();
            bool desktopSuffix = Regex.IsMatch(suffix, "^(K|KF|KS|F|T|S|P?)$") && m.Groups[3].Value == "" && suffix != "P";
            if (num.Length == 5)
            {
                if (!desktopSuffix) return "BGA";
                return num.StartsWith("1") && num[1] is '2' or '3' or '4' ? "LGA1700" : num[1] is '0' or '1' ? "LGA1200" : "";
            }
            // четыре цифры: 2000–9000 — настольные и ноутбучные, 1000–1999 (i5-1235U, i7-1165G7) — ноутбуки
            if (num[0] == '1' || !desktopSuffix) return "BGA";
            return num[0] switch { '2' or '3' => "LGA1155", '4' => "LGA1150", '6' or '7' or '8' or '9' => "LGA1151", _ => "" };
        }
        if ((m = Regex.Match(c, @"\bCore (\d) (\d)(\d{2})([A-Z]*)", RegexOptions.IgnoreCase)).Success)     // Core 5 120F, Core 7 150U
            return Regex.IsMatch(m.Groups[4].Value, "^(F|)$", RegexOptions.IgnoreCase) && m.Groups[2].Value == "1" ? "LGA1700" : "BGA";
        if (Regex.IsMatch(c, @"\bIntel N\d{2,3}\b|\bN\d{3,4}\b|Celeron N|Pentium (Silver|Gold \d{4}Y)|Atom", RegexOptions.IgnoreCase)) return "BGA";

        // --- то, что сообщила Windows, если это похоже на название разъёма
        string w = (wmiSocket ?? "").Trim();
        if ((m = Regex.Match(w, @"(?:FC)?LGA\s?(\d{3,4})", RegexOptions.IgnoreCase)).Success) return "LGA" + m.Groups[1].Value;
        if ((m = Regex.Match(w, @"^(AM\d\+?|sTRX4|sTR5|sWRX8|SP[356]|TR4|FM2\+?)$", RegexOptions.IgnoreCase)).Success) return m.Groups[1].Value;
        if (Regex.IsMatch(w, @"^(BGA|FP\d|FT\d|FL1)", RegexOptions.IgnoreCase)) return "BGA";
        return "";
    }

    /// <summary>Чипсет по названию платы: «PRO B760-P WIFI» → «Intel B760», «B650 AORUS» → «AMD B650», «X670E-E» → «AMD X670E».</summary>
    public string Chipset => ChipsetFor(CpuName, Board);

    private static readonly string[] AmdChipsets =
        { "A320", "A520", "A620", "B350", "B450", "B550", "B650", "B840", "B850", "X370", "X470", "X570", "X670", "X870", "X399" };
    private static readonly string[] IntelChipsets =
    {
        "H110", "B150", "H170", "Z170", "Q170", "B250", "H270", "Z270", "Q270", "H310", "B360", "B365", "H370", "Z370", "Z390", "Q370",
        "H410", "B460", "H470", "Z490", "Q470", "W480", "H510", "B560", "H570", "Z590", "Q570", "W580", "H610", "B660", "H670", "Z690",
        "Q670", "W680", "B760", "H770", "Z790", "W790", "H810", "B860", "Z890", "Q870", "W880",
    };

    /// <summary>Чипсет по названию платы и процессору. Только известные чипсеты: «X515EA» у ноутбука — не чипсет. Пусто — не определить.</summary>
    public static string ChipsetFor(string cpu, string board)
    {
        string vendor = CpuVendor(cpu);
        foreach (Match m in Regex.Matches((board ?? "").ToUpperInvariant(), @"(?<![A-Z0-9])([ABHXZQW]\d{3})(E|S)?(?=[MIT]?(?:[^A-Z0-9]|$))"))
        {
            string code = m.Groups[1].Value, plus = m.Groups[2].Value;
            bool amd = AmdChipsets.Contains(code), intel = IntelChipsets.Contains(code);
            if (amd && vendor != "Intel") return "AMD " + code + plus;
            if (intel && vendor != "AMD") return "Intel " + code;
        }
        return "";
    }

}
