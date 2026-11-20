using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PulsePC.Services;

/// <summary>
/// Видеокарты любых производителей: кто выпустил, встроенная или отдельная, какая главная.
/// Все решения, зависящие от производителя, — чистые функции: их можно проверить на выдуманных данных (SelfTest.GpuVendorChecks).
/// </summary>
public static class GpuVendors
{
    /// <summary>Производитель по коду PCI: 10DE — NVIDIA, 1002/1022 — AMD, 8086 — Intel, 5143/4D4F4351 — Qualcomm.</summary>
    public static string FromPciId(uint id) => id switch
    {
        0x10DE => "NVIDIA",
        0x1002 or 0x1022 => "AMD",
        0x8086 or 0x8087 => "Intel",
        0x5143 or 0x4D4F4351 or 0x51434F4D => "Qualcomm",
        _ => "",
    };

    /// <summary>Производитель по коду устройства Windows: «PCI\VEN_10DE&amp;DEV_2803…».</summary>
    public static string FromPnp(string pnp)
    {
        var m = Regex.Match(pnp ?? "", @"VEN_([0-9A-F]{4})", RegexOptions.IgnoreCase);
        return m.Success ? FromPciId(uint.Parse(m.Groups[1].Value, NumberStyles.HexNumber)) : "";
    }

    /// <summary>Производитель по названию (когда кода устройства нет).</summary>
    public static string FromName(string name)
    {
        name ??= "";
        if (Regex.IsMatch(name, @"NVIDIA|GeForce|Quadro|\bRTX\b|\bGTX\b|Tesla", RegexOptions.IgnoreCase)) return "NVIDIA";
        if (Regex.IsMatch(name, @"\bAMD\b|Radeon|\bATI\b|FirePro", RegexOptions.IgnoreCase)) return "AMD";
        if (Regex.IsMatch(name, @"Intel|\bArc\b|Iris|UHD Graphics|HD Graphics", RegexOptions.IgnoreCase)) return "Intel";
        if (Regex.IsMatch(name, @"Qualcomm|Adreno|Snapdragon", RegexOptions.IgnoreCase)) return "Qualcomm";
        return "";
    }

    /// <summary>
    /// Встроенная в процессор графика. NVIDIA — всегда отдельная. AMD: «Radeon Graphics», «Radeon 780M», «Vega 8 Graphics» — встроенные,
    /// «Radeon RX 7800 XT», «RX 7600M XT», «Radeon Pro» — отдельные. Intel: отдельные только Arc A/B (A770, A370M, B580) и Iris Xe MAX;
    /// «Arc Graphics» и «Arc 140V» — встроенные в Core Ultra.
    /// </summary>
    public static bool IsIntegrated(string vendor, string name)
    {
        name ??= "";
        switch (vendor)
        {
            case "NVIDIA": return false;
            case "Qualcomm": return true;
            case "Intel":
                return !Regex.IsMatch(name, @"Arc[^A-Za-z0-9]*(\(TM\)\s*)?(Pro\s+)?[AB]\d{2,3}M?\b|Iris\(?R?\)?\s*Xe\s*MAX|\bDG[12]\b", RegexOptions.IgnoreCase);
            case "AMD":
                if (Regex.IsMatch(name, @"Vega\s*\d+\s*Graphics|\b\d{3}M\b|\b\d{4}S\b|Radeon(\s*\(TM\))?\s*(R\d\s*)?Graphics\b", RegexOptions.IgnoreCase)
                    && !Regex.IsMatch(name, @"\bRX\s*\d{3,4}", RegexOptions.IgnoreCase))
                    return true;
                return false;
            default: return false;
        }
    }

    /// <summary>Не видеокарта, а программный или виртуальный адаптер: базовый драйвер, удалённый рабочий стол, Parsec, DisplayLink и т. п.</summary>
    public static bool IsVirtual(string name) =>
        Regex.IsMatch(name ?? "", @"Basic Display|Basic Render|Базовый видеоадаптер|Remote Display|Remote Desktop|Parsec|Virtual Display|Virtual Monitor|IddCx|Indirect Display|DisplayLink|spacedesk|Hyper-V|VMware|VirtualBox|Citrix|Splashtop|Mirage|Meta Virtual|Duet", RegexOptions.IgnoreCase);

    /// <summary>
    /// Главная видеокарта: настоящая (не виртуальная), отдельная раньше встроенной, затем с большей видеопамятью.
    /// На ноутбуке со встроенной и отдельной это отдельная — на ней идут игры.
    /// </summary>
    public static GpuAdapter PickMain(IEnumerable<GpuAdapter> adapters) =>
        adapters?.Where(a => a != null)
            .OrderBy(a => IsVirtual(a.Name) ? 1 : 0)
            .ThenBy(a => a.Vendor == "" ? 1 : 0)
            .ThenBy(a => a.Integrated ? 1 : 0)
            .ThenByDescending(a => a.VramGb)
            .FirstOrDefault();

    private static string mainVendor;
    /// <summary>Производитель главной видеокарты по драйверам в реестре (читается один раз, быстро). Пусто — не определён.</summary>
    public static string MainVendor
    {
        get
        {
            if (mainVendor == null)
            {
                try { mainVendor = GpuDriver.ReadAdapters().FirstOrDefault()?.Vendor ?? ""; } catch { mainVendor = ""; }
            }
            return mainVendor;
        }
    }

    /// <summary>
    /// Где запускается приложение настроек драйвера (AMD Software, Intel Graphics Software): путь к программе, ярлык или адрес
    /// приложения из Microsoft Store. Пусто — не установлено. Только чтение файлов и реестра.
    /// </summary>
    public static string FindControlApp(string vendor)
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] exes = vendor switch
        {
            "AMD" => new[] { Path.Combine(pf, @"AMD\CNext\CNext\RadeonSoftware.exe") },
            "Intel" => new[]
            {
                Path.Combine(pf, @"Intel\Intel Graphics Software\IntelGraphicsSoftware.exe"),
                Path.Combine(pf, @"Intel\Intel Arc Control\ArcControl.exe"),
            },
            _ => Array.Empty<string>(),
        };
        foreach (var e in exes) if (File.Exists(e)) return e;

        // Ярлык в меню «Пуск» — надёжнее путей: установщики производителей их не меняют.
        string pattern = vendor switch
        {
            "AMD" => @"^(AMD Software|AMD Radeon Software|Radeon Software)",
            "Intel" => @"^Intel.*(Graphics Software|Arc Control|Graphics Command Center)",
            _ => null,
        };
        if (pattern != null)
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) })
            {
                try
                {
                    var lnk = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true })
                        .FirstOrDefault(f => Regex.IsMatch(Path.GetFileNameWithoutExtension(f), pattern, RegexOptions.IgnoreCase));
                    if (lnk != null) return lnk;
                }
                catch { }
            }
        }

        // Intel Graphics Command Center ставится из Microsoft Store.
        if (vendor == "Intel")
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
                string pkg = k?.GetSubKeyNames().FirstOrDefault(n => n.StartsWith("AppUp.IntelGraphicsExperience_", StringComparison.OrdinalIgnoreCase));
                // «AppUp.IntelGraphicsExperience_1.100.5185.0_x64__8j3eq9eme6ctt» → семейство «AppUp.IntelGraphicsExperience_8j3eq9eme6ctt».
                int at = pkg?.LastIndexOf("__", StringComparison.Ordinal) ?? -1;
                if (at > 0) return @"shell:AppsFolder\AppUp.IntelGraphicsExperience_" + pkg[(at + 2)..] + "!App";
            }
            catch { }
        }
        return "";
    }

    /// <summary>Как называется приложение настроек драйвера у производителя.</summary>
    public static string ControlApp(string vendor) => vendor switch
    {
        "NVIDIA" => "NVIDIA App",
        "AMD" => "AMD Software: Adrenalin Edition",
        "Intel" => "Intel Graphics Software",
        _ => "",
    };

    /// <summary>Название раздела «Система» под производителя видеокарты.</summary>
    public static string SectionTitle(string vendor) => vendor == "NVIDIA" ? "Настройки NVIDIA по играм" : "Настройки видеокарты по играм";

    // ------------------------------------------------------------------ счётчики Windows «GPU Engine» / «GPU Adapter Memory»
    /// <summary>
    /// Разбор имени экземпляра счётчика: «pid_1234_luid_0x00000000_0x0000D1C5_phys_0_eng_0_engtype_3D» →
    /// адаптер «luid_0x00000000_0x0000D1C5», двигатель 0, тип «3D». Имена экземпляров в Windows не переводятся.
    /// </summary>
    public static (string Luid, int Engine, string Type) ParseEngine(string instance)
    {
        var m = Regex.Match(instance ?? "", @"(luid_0x[0-9A-F]+_0x[0-9A-F]+)(?:_phys_\d+)?(?:_eng_(\d+))?(?:_engtype_(.*))?$", RegexOptions.IgnoreCase);
        if (!m.Success) return ("", -1, "");
        return (m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : -1, m.Groups[3].Success ? m.Groups[3].Value : "");
    }

    /// <summary>Имя адаптера в счётчиках по его LUID: «luid_0x00000000_0x0000d1c5».</summary>
    public static string LuidKey(int high, uint low) => $"luid_0x{(uint)high:x8}_0x{low:x8}";

    /// <summary>
    /// Загрузка видеокарты, как в Диспетчере задач: по каждому двигателю 3D и вычислений — сумма процессов, берётся самый загруженный двигатель.
    /// Если адаптер не задан (luid пустой), — самый загруженный адаптер. NaN — счётчиков нет.
    /// </summary>
    public static double Load(IEnumerable<(string Instance, double Value)> samples, string luid)
    {
        var byEngine = new Dictionary<string, double>();
        foreach (var (inst, v) in samples)
        {
            var e = ParseEngine(inst);
            if (e.Luid == "" || !IsLoadEngine(e.Type)) continue;
            if (!string.IsNullOrEmpty(luid) && !e.Luid.Equals(luid, StringComparison.OrdinalIgnoreCase)) continue;
            if (double.IsNaN(v) || v < 0) continue;
            string key = e.Luid + "#" + e.Engine;
            byEngine[key] = byEngine.GetValueOrDefault(key) + v;
        }
        return byEngine.Count == 0 ? double.NaN : Math.Min(100, byEngine.Values.Max());
    }

    /// <summary>
    /// Двигатели, по которым считается загрузка: 3D и вычисления (игры считают на них параллельно). Копирование, видео и прочее не в счёт:
    /// иначе загрузку «давали» бы ролик в браузере и подкачка видеопамяти.
    /// </summary>
    public static bool IsLoadEngine(string type) =>
        Regex.IsMatch(type ?? "", @"^(High Priority )?(3D|Compute|Graphics|Cuda)", RegexOptions.IgnoreCase);

    // ------------------------------------------------------------------ несколько видеокарт NVIDIA
    /// <summary>
    /// Из строк nvidia-smi за один опрос (по одной на видеокарту) — та, на которой идёт игра: самая загруженная.
    /// Чтобы показания не прыгали, прежняя остаётся главной, пока другая не загружена заметно сильнее.
    /// </summary>
    public static int PickBusiest(IReadOnlyList<double> loads, int previous)
    {
        if (loads == null || loads.Count == 0) return -1;
        int best = 0;
        for (int i = 1; i < loads.Count; i++)
            if ((double.IsNaN(loads[best]) ? -1 : loads[best]) < (double.IsNaN(loads[i]) ? -1 : loads[i])) best = i;
        if (previous >= 0 && previous < loads.Count && !double.IsNaN(loads[previous])
            && (double.IsNaN(loads[best]) || loads[best] - loads[previous] < 15)) return previous;
        return best;
    }
}

/// <summary>Видеокарта глазами ядра графики Windows (D3DKMT): LUID, код производителя, видеопамять, тип.</summary>
public sealed class KmtAdapter
{
    public uint LuidLow;
    public int LuidHigh;
    public string Name = "";
    public uint VendorId, DeviceId;
    public ulong DedicatedBytes, SharedBytes;
    public uint TypeFlags;
    public int Node3D;          // номер двигателя 3D (обычно 0)

    public bool Render => (TypeFlags & 1) != 0;
    public bool Software => (TypeFlags & 4) != 0;
    public bool HybridDiscrete => (TypeFlags & 16) != 0;
    public bool HybridIntegrated => (TypeFlags & 32) != 0;
    public string Vendor => GpuVendors.FromPciId(VendorId) is string v && v != "" ? v : GpuVendors.FromName(Name);
    public string Key => GpuVendors.LuidKey(LuidHigh, LuidLow);
    public bool Integrated => HybridIntegrated || !HybridDiscrete && GpuVendors.IsIntegrated(Vendor, Name);

    public GpuAdapter ToAdapter() => new()
    {
        Name = Name, Vendor = Vendor, VramGb = DedicatedBytes / (1024.0 * 1024 * 1024), Integrated = Integrated,
    };
}

/// <summary>Показания датчиков видеокарты от Windows (WDDM 2.4+, как в Диспетчере задач). NaN — драйвер не сообщает.</summary>
public struct KmtPerf
{
    public double TempC, FanRpm, PowerPct, ClockMhz, MemClockMhz, MaxClockMhz;
}

/// <summary>
/// Обращение к ядру графики Windows через D3DKMT (gdi32.dll): список видеокарт и их датчики — температура, вентилятор, частоты.
/// Это те же данные, что показывает Диспетчер задач; прав администратора не нужно. Только чтение.
/// Номера запросов и раскладка структур — d3dkmthk.h (Windows SDK 10.0.17134+).
/// </summary>
public static class Kmt
{
    private const int GetSegmentSize = 3, AdapterRegistryInfo = 8, AdapterType = 15, NodeMetadata = 25,
                      PhysicalAdapterDeviceIds = 31, NodePerfData = 61, AdapterPerfData = 62, AdapterPerfDataCaps = 63;
    private const int EngineType3D = 1;     // DXGK_ENGINE_TYPE_3D

    /// <summary>Все видеокарты, которые видит Windows, без программных адаптеров. Ничего не держит открытым.</summary>
    public static List<KmtAdapter> Enumerate()
    {
        var list = new List<KmtAdapter>();
        try
        {
            var en = new ENUMADAPTERS2();
            if (D3DKMTEnumAdapters2(ref en) != 0 || en.NumAdapters == 0) return list;
            int size = Marshal.SizeOf<ADAPTERINFO>();
            IntPtr buf = Marshal.AllocHGlobal(size * (int)en.NumAdapters);
            try
            {
                en.pAdapters = buf;
                if (D3DKMTEnumAdapters2(ref en) != 0) return list;
                for (int i = 0; i < en.NumAdapters; i++)
                {
                    var info = Marshal.PtrToStructure<ADAPTERINFO>(buf + i * size);
                    try
                    {
                        var a = new KmtAdapter { LuidLow = info.LuidLow, LuidHigh = info.LuidHigh };
                        Describe(info.hAdapter, a);
                        if (!a.Software && a.Render) list.Add(a);
                    }
                    catch { }
                    finally { Close(info.hAdapter); }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { }
        return list;
    }

    private static void Describe(uint h, KmtAdapter a)
    {
        byte[] t = Query(h, AdapterType, 4);
        if (t != null) a.TypeFlags = BitConverter.ToUInt32(t, 0);
        byte[] reg = Query(h, AdapterRegistryInfo, 4 * 260 * 2);
        if (reg != null) a.Name = WString(reg, 0, 260);
        byte[] ids = Query(h, PhysicalAdapterDeviceIds, 28);
        if (ids != null) { a.VendorId = BitConverter.ToUInt32(ids, 4); a.DeviceId = BitConverter.ToUInt32(ids, 8); }
        byte[] seg = Query(h, GetSegmentSize, 24);
        if (seg != null) { a.DedicatedBytes = BitConverter.ToUInt64(seg, 0); a.SharedBytes = BitConverter.ToUInt64(seg, 16); }
        a.Node3D = 0;
        for (int node = 0; node < 64; node++)
        {
            var md = new byte[80];
            BitConverter.GetBytes((uint)node).CopyTo(md, 0);     // номер двигателя, физический адаптер 0
            if (!QueryInto(h, NodeMetadata, md)) break;
            if (BitConverter.ToInt32(md, 4) == EngineType3D) { a.Node3D = node; break; }
        }
    }

    /// <summary>Открыть видеокарту по LUID для опроса датчиков. 0 — не получилось.</summary>
    public static uint Open(int luidHigh, uint luidLow)
    {
        var o = new OPENFROMLUID { LuidLow = luidLow, LuidHigh = luidHigh };
        try { return D3DKMTOpenAdapterFromLuid(ref o) == 0 ? o.hAdapter : 0; } catch { return 0; }
    }

    public static void Close(uint h)
    {
        if (h == 0) return;
        var c = new CLOSEADAPTER { hAdapter = h };
        try { D3DKMTCloseAdapter(ref c); } catch { }
    }

    /// <summary>Датчики видеокарты. Нули драйвер отдаёт там, где датчика нет, — они становятся NaN.</summary>
    public static KmtPerf Read(uint h, int node3d)
    {
        var p = new KmtPerf { TempC = double.NaN, FanRpm = double.NaN, PowerPct = double.NaN, ClockMhz = double.NaN, MemClockMhz = double.NaN, MaxClockMhz = double.NaN };
        if (h == 0) return p;
        var perf = new byte[64];
        if (QueryInto(h, AdapterPerfData, perf)) Decode(perf, ref p);
        var node = new byte[56];
        BitConverter.GetBytes((uint)Math.Max(0, node3d)).CopyTo(node, 0);
        if (QueryInto(h, NodePerfData, node)) DecodeNode(node, ref p);
        return p;
    }

    /// <summary>Разбор D3DKMT_ADAPTER_PERFDATA (вынесено ради проверки на выдуманных байтах).</summary>
    public static void Decode(byte[] perf, ref KmtPerf p)
    {
        ulong memHz = BitConverter.ToUInt64(perf, 8);
        uint fan = BitConverter.ToUInt32(perf, 48), power = BitConverter.ToUInt32(perf, 52), temp = BitConverter.ToUInt32(perf, 56);
        p.TempC = TempFromDeci(temp);
        p.FanRpm = fan is > 0 and < 20000 ? fan : double.NaN;
        p.PowerPct = power is > 0 and <= 2000 ? power / 10.0 : double.NaN;
        p.MemClockMhz = memHz > 0 ? memHz / 1e6 : double.NaN;
    }

    /// <summary>Разбор D3DKMT_NODE_PERFDATA: частота двигателя 3D в герцах.</summary>
    public static void DecodeNode(byte[] node, ref KmtPerf p)
    {
        ulong hz = BitConverter.ToUInt64(node, 8), max = BitConverter.ToUInt64(node, 16);
        p.ClockMhz = hz is > 0 and < 10_000_000_000UL ? hz / 1e6 : double.NaN;
        p.MaxClockMhz = max is > 0 and < 10_000_000_000UL ? max / 1e6 : double.NaN;
    }

    /// <summary>Температура в десятых долях градуса → °C; 0 и явный мусор — «датчика нет».</summary>
    public static double TempFromDeci(uint deci) => deci is >= 50 and <= 1500 ? deci / 10.0 : double.NaN;

    /// <summary>Предельная и «тревожная» температура видеокарты по данным драйвера, °C (NaN — не сообщает).</summary>
    public static (double Max, double Warning, double MaxFanRpm) Caps(uint h)
    {
        var caps = new byte[40];
        if (h == 0 || !QueryInto(h, AdapterPerfDataCaps, caps)) return (double.NaN, double.NaN, double.NaN);
        uint fan = BitConverter.ToUInt32(caps, 24);
        return (TempFromDeci(BitConverter.ToUInt32(caps, 28)), TempFromDeci(BitConverter.ToUInt32(caps, 32)), fan is > 0 and < 20000 ? fan : double.NaN);
    }

    // ------------------------------------------------------------------ низкий уровень
    private static byte[] Query(uint h, int type, int size)
    {
        var buf = new byte[size];
        return QueryInto(h, type, buf) ? buf : null;
    }

    private static bool QueryInto(uint h, int type, byte[] buf)
    {
        var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            var q = new QUERYADAPTERINFO { hAdapter = h, Type = type, pPrivateDriverData = pin.AddrOfPinnedObject(), PrivateDriverDataSize = (uint)buf.Length };
            return D3DKMTQueryAdapterInfo(ref q) == 0;
        }
        catch { return false; }
        finally { pin.Free(); }
    }

    private static string WString(byte[] b, int offsetChars, int maxChars)
    {
        string s = System.Text.Encoding.Unicode.GetString(b, offsetChars * 2, maxChars * 2);
        int z = s.IndexOf('\0');
        return (z >= 0 ? s[..z] : s).Trim();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ENUMADAPTERS2 { public uint NumAdapters; public IntPtr pAdapters; }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ADAPTERINFO { public uint hAdapter; public uint LuidLow; public int LuidHigh; public uint NumOfSources; public int bPrecisePresentRegionsPreferred; }

    [StructLayout(LayoutKind.Sequential)]
    private struct QUERYADAPTERINFO { public uint hAdapter; public int Type; public IntPtr pPrivateDriverData; public uint PrivateDriverDataSize; }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct OPENFROMLUID { public uint LuidLow; public int LuidHigh; public uint hAdapter; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CLOSEADAPTER { public uint hAdapter; }

    [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref ENUMADAPTERS2 p);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref QUERYADAPTERINFO p);
    [DllImport("gdi32.dll")] private static extern int D3DKMTOpenAdapterFromLuid(ref OPENFROMLUID p);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref CLOSEADAPTER p);
}
