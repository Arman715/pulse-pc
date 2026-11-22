using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PulsePC.Panels;

namespace PulsePC.Services;

/// <summary>
/// Ноутбук или настольный компьютер — общий признак для всей программы, и служба раздела «Ноутбук»:
/// здоровье батареи, режим питания Windows (только чтение), фирменная программа производителя, уведомление «игра от батареи».
/// Признак: мобильная платформа (как PCSystemType = 2 в первичном осмотре), корпус ноутбука по SMBIOS или батарея ноутбука
/// (батарея ИБП у настольного компьютера не в счёт). Для проверки: PULSEPC_TEST_LAPTOP=1 / 0.
/// </summary>
public static class LaptopCheck
{
    private const string Nb = " ";
    public const string AlertKey = "laptop.batteryAlert";

    // ================================================================== признак
    private static readonly object gate = new();
    private static bool? cached;
    private static string why = "";

    /// <summary>Это ноутбук. Читается один раз (быстро: без WMI и PowerShell), дальше — из памяти.</summary>
    public static bool Applies
    {
        get
        {
            if (cached is bool known) return known;
            lock (gate)
            {
                if (cached is bool again) return again;
                uint role = 0; int chassis = 0; bool battery = false;
                try { role = PowerDeterminePlatformRoleEx(2); } catch { }
                try { chassis = ChassisType(); } catch { }
                try { battery = BatteryInfo.Read().HasBattery; } catch { }
                var (laptop, reason) = Detect(Environment.GetEnvironmentVariable("PULSEPC_TEST_LAPTOP"), role, chassis, battery);
                why = reason;
                cached = laptop;
                return laptop;
            }
        }
    }

    /// <summary>Почему программа так решила (для самопроверки и «Состояния программы»).</summary>
    public static string Reason { get { _ = Applies; return why; } }

    /// <summary>Типы корпуса SMBIOS, которые означают переносной компьютер: 8 Portable, 9 Laptop, 10 Notebook, 11 Hand Held,
    /// 14 Sub Notebook, 30 Tablet, 31 Convertible, 32 Detachable.</summary>
    private static readonly int[] LaptopChassis = { 8, 9, 10, 11, 14, 30, 31, 32 };

    /// <summary>Чистая функция: подмена из переменной, мобильная платформа (2 Mobile, 8 Slate), корпус ноутбука или батарея.</summary>
    public static (bool Laptop, string Reason) Detect(string env, uint platformRole, int chassis, bool battery)
    {
        env = (env ?? "").Trim();
        if (env == "1") return (true, "подмена PULSEPC_TEST_LAPTOP=1");
        if (env == "0") return (false, "подмена PULSEPC_TEST_LAPTOP=0");
        if (platformRole is 2 or 8) return (true, $"мобильная платформа (роль {platformRole})");
        if (Array.IndexOf(LaptopChassis, chassis) >= 0) return (true, $"корпус ноутбука (тип {chassis})");
        if (battery) return (true, "батарея ноутбука");
        return (false, $"настольный (роль {platformRole}, корпус {chassis}, батареи нет)");
    }

    /// <summary>Тип корпуса из таблицы SMBIOS (структура 3, байт 5); 0 — не прочитан.</summary>
    private static int ChassisType()
    {
        const uint Rsmb = 0x52534D42;   // 'RSMB'
        uint size = GetSystemFirmwareTable(Rsmb, 0, null, 0);
        if (size == 0 || size > 1 << 20) return 0;
        var buf = new byte[size];
        if (GetSystemFirmwareTable(Rsmb, 0, buf, size) != size) return 0;
        return ChassisFromSmbios(buf);
    }

    /// <summary>Разбор RawSMBIOSData: 8 байт заголовка, дальше структуры (тип, длина, …, строки до двойного нуля).</summary>
    internal static int ChassisFromSmbios(byte[] buf)
    {
        if (buf == null || buf.Length < 8) return 0;
        long length = BitConverter.ToUInt32(buf, 4);
        int end = (int)Math.Min(buf.Length, 8 + length);
        int i = 8;
        while (i + 4 <= end)
        {
            int type = buf[i], len = buf[i + 1];
            if (len < 4) break;
            if (type == 3 && len > 5 && i + 5 < end) return buf[i + 5] & 0x7F;
            if (type == 127) break;
            int j = i + len;
            while (j + 1 < end && !(buf[j] == 0 && buf[j + 1] == 0)) j++;
            i = j + 2;
        }
        return 0;
    }

    /// <summary>Совет про охлаждение там, где у настольного написано «проверьте обдув корпуса»: у ноутбука корпуса с вентиляторами нет.</summary>
    public static string CoolingTip() => Applies
        ? L.T("Поставьте ноутбук на стол или подставку — отверстия снизу не должны быть закрыты. Если не помогает, почистите его от пыли в сервисе.")
        : L.T("Проверьте обдув корпуса.");

    // ================================================================== батарея
    public sealed class BatteryHealth
    {
        public bool Read;              // удалось прочитать
        public long DesignMwh, FullMwh;
        public int Cycles;             // 0 — ноутбук не сообщает
        public int Count;
        public string Error = "";
        public double WearPct => DesignMwh > 0 && FullMwh > 0 ? Math.Max(0, 100.0 * (1 - (double)FullMwh / DesignMwh)) : double.NaN;
    }

    private const string BatteryScript =
        "$ErrorActionPreference='SilentlyContinue'; $Error.Clear(); " +
        "$s=@(Get-CimInstance -Namespace root\\wmi -ClassName BatteryStaticData); " +
        "$f=@(Get-CimInstance -Namespace root\\wmi -ClassName BatteryFullChargedCapacity); " +
        "$c=@(Get-CimInstance -Namespace root\\wmi -ClassName BatteryCycleCount); " +
        "[pscustomobject]@{ Design=[double](($s | Measure-Object DesignedCapacity -Sum).Sum); Full=[double](($f | Measure-Object FullChargedCapacity -Sum).Sum); " +
        "Cycles=[int](($c | Measure-Object CycleCount -Maximum).Maximum); N=[int]$s.Count; " +
        "Err=\"$(if ($Error.Count) { $Error[0].Exception.GetType().Name })\" } | ConvertTo-Json -Compress";

    /// <summary>Ёмкость батареи из root\wmi (только чтение, в фоне: PowerShell ~1 с).</summary>
    public static BatteryHealth ReadBattery()
    {
        var h = new BatteryHealth();
        try
        {
            var (_, output) = Shell.PowerShell(BatteryScript, 20000);
            int a = output?.IndexOf('{') ?? -1, b = output?.LastIndexOf('}') ?? -1;
            if (a < 0 || b <= a) { h.Error = "нет ответа"; return h; }
            using var doc = JsonDocument.Parse(output[a..(b + 1)]);
            var r = doc.RootElement;
            double Num(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            h.DesignMwh = (long)Num("Design");
            h.FullMwh = (long)Num("Full");
            h.Cycles = (int)Num("Cycles");
            h.Count = (int)Num("N");
            h.Error = r.TryGetProperty("Err", out var e) ? e.GetString() ?? "" : "";
            h.Read = h.DesignMwh > 0 && h.FullMwh > 0;
        }
        catch (Exception ex) { h.Error = ex.Message; }
        return h;
    }

    /// <summary>Чистая функция: вывод о батарее по износу.</summary>
    public static (Severity Level, string Title, string Text) BatteryVerdict(BatteryHealth h)
    {
        if (h == null || !h.Read)
            return (Severity.Info, L.T("Износ батареи прочитать не удалось"),
                h != null && h.Error.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
                    ? L.T("Windows отдаёт ёмкость батареи только с правами администратора. Отчёт можно получить и самому: в командной строке выполните powercfg /batteryreport — откроется файл с ёмкостью и историей.")
                    : L.T("Ноутбук не сообщает ёмкость батареи. Отчёт можно получить и самому: в командной строке выполните powercfg /batteryreport."));
        double wear = h.WearPct;
        string cycles = h.Cycles > 0 ? L.F(" Циклов зарядки: {0}.", h.Cycles) : "";
        string caps = L.F("Сейчас батарея вмещает {0} из {1} заводских.", Wh(h.FullMwh), Wh(h.DesignMwh));
        if (wear < 20)
            return (Severity.Ok, L.T("Батарея в хорошем состоянии"), caps + cycles);
        if (wear < 40)
            return (Severity.Info, L.F("Батарея износилась на {0:0} %", wear),
                caps + cycles + L.T(" Это обычный износ за несколько лет: от батареи ноутбук работает меньше, чем новый. На игры от зарядки это не влияет."));
        return (Severity.Warn, L.F("Батарея сильно изношена: {0:0} %", wear),
            caps + cycles + L.T(" Ноутбук может выключаться раньше, чем покажет 0 %. Замену батареи лучше делать в сервисе: подойдёт только модель для вашего ноутбука."));
    }

    private static string Wh(long mwh) => (mwh / 1000.0).ToString(mwh >= 10000 ? "0" : "0.#", L.Culture) + Nb + L.Pick("Вт·ч", "Wh");

    // ================================================================== режим питания Windows
    public enum PowerMode { Unknown, Efficiency, Balanced, Performance, BestPerformance }

    /// <summary>Режимы ползунка «Режим питания» (overlay-схемы Windows).</summary>
    public static PowerMode ModeFromGuid(Guid g)
    {
        string s = g.ToString().ToLowerInvariant();
        return s switch
        {
            "961cc777-2547-4f9d-8174-7d86181b8a7a" => PowerMode.Efficiency,
            "00000000-0000-0000-0000-000000000000" => PowerMode.Balanced,
            "381b4222-f694-41f0-9685-ff5bb260df2e" => PowerMode.Balanced,
            "3af9b8d9-7c97-431d-ad78-34a8bfea439f" => PowerMode.Performance,
            "ded574b5-45a0-4f42-8737-46345c09c238" => PowerMode.BestPerformance,
            _ => PowerMode.Unknown,
        };
    }

    public static string ModeName(PowerMode m) => m switch
    {
        // Названия — как у самой Windows 11 в «Параметры → Система → Питание». По-английски — свои: общий перевод
        // «Максимальная производительность» относится к схеме питания (Ultimate Performance), а это режим питания (Best performance).
        PowerMode.Efficiency => L.Pick("Максимальная экономия энергии", "Best power efficiency"),
        PowerMode.Balanced => L.Pick("Сбалансированный", "Balanced"),
        PowerMode.Performance => L.Pick("Повышенная производительность", "Better performance"),
        PowerMode.BestPerformance => L.Pick("Максимальная производительность", "Best performance"),
        _ => L.T("не удалось прочитать"),
    };

    /// <summary>Действующий режим питания Windows (с учётом экономии заряда). Только чтение.</summary>
    public static PowerMode ReadMode()
    {
        try { if (PowerGetEffectiveOverlayScheme(out Guid g) == 0) return ModeFromGuid(g); } catch { }
        try { if (PowerGetActualOverlayScheme(out Guid g) == 0) return ModeFromGuid(g); } catch { }
        return PowerMode.Unknown;
    }

    /// <summary>Включена ли «Экономия заряда».</summary>
    public static bool BatterySaver()
    {
        try { return GetSystemPowerStatus(out var st) && st.SystemStatusFlag == 1; } catch { return false; }
    }

    // ================================================================== фирменные программы
    public sealed class OemApp
    {
        public string Maker = "", Name = "", Hint = "";
        public bool Installed;
    }

    /// <summary>Программа, признак (регулярное выражение по именам установленных программ и пакетов Store), где включить «Турбо».</summary>
    private static readonly (string Maker, string Name, string Pattern, string Hint)[] OemApps =
    {
        ("ASUS", "Armoury Crate", @"armou?ry\s*crate|B9ECED6F\.ArmouryCrate",
            "Armoury Crate → «Главная» → «Режим работы»: «Турбо» или «Производительность». На многих моделях ROG и TUF режимы переключает Fn+F5."),
        ("ASUS", "MyASUS", @"\bmyasus\b|B9ECED6F\.ASUSPCAssistant",
            "MyASUS → «Настройки» → режим вентилятора: «Высокая производительность»."),
        ("Lenovo", "Legion Space / Legion Zone", @"legion\s*(space|zone|toolkit)|E046963F\.LenovoLegion",
            "На ноутбуках Legion режим переключает Fn+Q: красный цвет кнопки питания — «Производительность», белый — сбалансированный, синий — тихий."),
        ("Lenovo", "Lenovo Vantage", @"lenovo\s*vantage|E046963F\.LenovoCompanion|E046963F\.LenovoSettings",
            "Lenovo Vantage → «Устройство» → «Питание» → режим «Производительность» (на Legion — ещё и Fn+Q)."),
        ("MSI", "MSI Center", @"msi\s*center|dragon\s*center|MICRO-STARINTERNATION\.(MSICenter|DragonCenter)",
            "MSI Center → «Функции» → «Пользовательский сценарий» → «Экстремальная производительность»."),
        ("HP", "OMEN Gaming Hub", @"omen\s*(gaming\s*hub|command\s*center)|AD2F1837\.OMEN",
            "OMEN Gaming Hub → «Производительность» → режим «Производительность»."),
        ("HP", "HP Command Center", @"hp\s*command\s*center|AD2F1837\.HPCommandCenter|AD2F1837\.HPThermalControl",
            "HP Command Center → «Управление производительностью» → «Производительность»."),
        ("Acer", "NitroSense / PredatorSense", @"nitro\s*sense|predator\s*sense|AcerIncorporated\.(NitroSense|PredatorSense)",
            "NitroSense или PredatorSense → режим питания «Турбо» или «Производительность». Программу открывает отдельная кнопка на клавиатуре."),
        ("Dell", "Alienware Command Center", @"alienware\s*command\s*center|DellInc\.AlienwareCommandCenter",
            "Alienware Command Center → «Режимы» (Fusion) → «Производительность»."),
        ("Dell", "Dell Power Manager / Dell Optimizer", @"dell\s*(power\s*manager|optimizer)|DellInc\.(DellPowerManager|DellOptimizer)",
            "Dell Power Manager → «Управление температурой» → «Сверхпроизводительность»."),
        ("Gigabyte", "GIGABYTE Control Center", @"gigabyte\s*control\s*center|aorus\s*control\s*center",
            "GIGABYTE Control Center → режим «Турбо» или «Игровой»."),
        ("Razer", "Razer Synapse", @"razer\s*synapse",
            "Razer Synapse → «Система» → «Производительность» → «Высокая»."),
    };

    /// <summary>Чистая функция: какие фирменные программы установлены; если ни одной — подсказка по производителю ноутбука.</summary>
    public static List<OemApp> FindOem(string maker, IEnumerable<string> installed)
    {
        var names = installed?.Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? new List<string>();
        var found = new List<OemApp>();
        foreach (var a in OemApps)
            if (names.Any(n => Regex.IsMatch(n, a.Pattern, RegexOptions.IgnoreCase)))
                found.Add(new OemApp { Maker = a.Maker, Name = a.Name, Hint = L.T(a.Hint), Installed = true });
        if (found.Count > 0) return found;

        string m = (maker ?? "").ToUpperInvariant();
        string brand = m.Contains("ASUS") ? "ASUS" : m.Contains("LENOVO") ? "Lenovo" : m.Contains("MICRO-STAR") || m.StartsWith("MSI") ? "MSI"
            : m.StartsWith("HP") || m.Contains("HEWLETT") ? "HP" : m.Contains("ACER") ? "Acer" : m.Contains("DELL") || m.Contains("ALIENWARE") ? "Dell"
            : m.Contains("GIGABYTE") || m.Contains("AORUS") ? "Gigabyte" : m.Contains("RAZER") ? "Razer" : "";
        if (brand == "") return found;
        var first = OemApps.First(a => a.Maker == brand);
        found.Add(new OemApp { Maker = brand, Name = first.Name, Hint = L.T(first.Hint), Installed = false });
        return found;
    }

    /// <summary>Имена установленных программ (Uninstall) и пакетов Microsoft Store текущего пользователя. Только чтение.</summary>
    public static List<string> InstalledNames()
    {
        var list = new List<string>();
        void Scan(RegistryKey root, string path)
        {
            try
            {
                using var k = root.OpenSubKey(path);
                if (k == null) return;
                foreach (var sub in k.GetSubKeyNames())
                {
                    try { using var s = k.OpenSubKey(sub); if (s?.GetValue("DisplayName") is string n) list.Add(n); } catch { }
                }
            }
            catch { }
        }
        Scan(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        Scan(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
        Scan(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        try
        {
            using var p = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (p != null) list.AddRange(p.GetSubKeyNames());
        }
        catch { }
        return list;
    }

    /// <summary>Производитель и модель компьютера из реестра (то же, что SMBIOS). Только чтение.</summary>
    public static (string Maker, string Model) Machine()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            return ((k?.GetValue("SystemManufacturer") as string ?? "").Trim(), (k?.GetValue("SystemProductName") as string ?? "").Trim());
        }
        catch { return ("", ""); }
    }

    // ================================================================== состояние для раздела
    public sealed class Advice
    {
        public Severity Level;
        public string Title = "", Text = "";
    }

    public sealed class State
    {
        public BatteryInfo Battery;
        public bool Saver;
        public PowerMode Mode;
        public BatteryHealth Health;
        public List<OemApp> Oem = new();
        public string Maker = "", Model = "";
        public bool HasDiscrete;
        public string DiscreteName = "";
    }

    /// <summary>Всё для раздела «Ноутбук» (в фоне, только чтение).</summary>
    public static State Read(HardwareInfo hw)
    {
        var st = new State { Battery = BatteryInfo.Read(), Saver = BatterySaver(), Mode = ReadMode() };
        (st.Maker, st.Model) = Machine();
        try { st.Oem = FindOem(st.Maker, InstalledNames()); } catch { }
        try
        {
            var d = hw?.Gpus?.FirstOrDefault(g => !g.Integrated);
            st.HasDiscrete = d != null && hw.Gpus.Count > 1;
            st.DiscreteName = d?.Name ?? "";
        }
        catch { }
        st.Health = ReadBattery();
        return st;
    }

    /// <summary>Чистая функция: советы для ноутбука по состоянию — что требует внимания, раньше.</summary>
    public static List<Advice> Advise(State s)
    {
        var list = new List<Advice>();
        if (s.Battery.OnBattery)
            list.Add(new Advice
            {
                Level = Severity.Warn, Title = L.T("Ноутбук работает от батареи"),
                Text = L.T("От батареи Windows и драйверы урезают частоты процессора и видеокарты — FPS будет заметно ниже, а заряд уйдёт за час-два. Для игр подключите зарядку."),
            });
        if (s.Saver)
            list.Add(new Advice
            {
                Level = Severity.Warn, Title = L.T("Включена экономия заряда"),
                Text = L.T("Экономия заряда ограничивает процессор и фоновые программы. Выключить: Параметры → Система → Питание и батарея → «Экономия заряда»."),
            });
        if (!s.Battery.OnBattery && s.Mode is PowerMode.Efficiency or PowerMode.Balanced)
            list.Add(new Advice
            {
                Level = Severity.Info, Title = L.F("Режим питания Windows — «{0}»", ModeName(s.Mode)),
                Text = L.Pick("Для игр от зарядки выберите «Максимальная производительность»: Параметры → Система → Питание и батарея → «Режим питания». Программа этот режим не меняет.",
                              "For gaming on the charger, choose “Best performance”: Settings → System → Power & battery → “Power mode”. The app doesn't change this mode."),
            });
        foreach (var o in s.Oem)
            list.Add(new Advice
            {
                Level = Severity.Info,
                Title = o.Installed ? L.F("Установлена {0}", o.Name) : L.F("Фирменная программа {0}: {1}", o.Maker, o.Name),
                Text = o.Installed
                    ? L.T("Режим «Турбо» или «Производительность» в ней поднимает предел мощности процессора и видеокарты сильнее, чем режим Windows. ") + o.Hint
                    : L.T("Она не найдена. В ней обычно есть режим «Турбо» или «Производительность» — скачайте с сайта поддержки ноутбука или из Microsoft Store. ") + o.Hint,
            });
        if (s.HasDiscrete)
            list.Add(new Advice
            {
                Level = Severity.Info, Title = L.T("Игры — на отдельной видеокарте"),
                Text = L.F("В ноутбуке две графики, и Windows сама решает, на какой идёт игра. Чтобы игра шла на {0}: Параметры → Система → Дисплей → Графика → выберите игру → «Высокая производительность». Отчёт после игры скажет, если игра шла на встроенной графике.", HardwareInfo.ShortGpu(s.DiscreteName)),
            });
        list.Add(new Advice
        {
            Level = Severity.Info, Title = L.T("Охлаждение"),
            Text = L.T("Ставьте ноутбук на стол или подставку: на кровати и коленях закрываются отверстия снизу, и процессор с видеокартой сбрасывают частоту от нагрева. Подставка с наклоном или приподнятый задний край дают воздуху пройти. Раз в год-два вентиляторы стоит почистить от пыли в сервисе."),
        });
        return list;
    }

    // ================================================================== уведомление «игра от батареи»
    private static PanelHost host;
    private static System.Threading.Timer watch;
    private static volatile bool warned;

    public static void Init(PanelHost h)
    {
        host = h;
        if (!Applies) return;
        GameHooks.Started += (_, _) =>
        {
            warned = false;
            Check();
            watch?.Dispose();
            watch = new System.Threading.Timer(_ => Check(), null, 60000, 60000);
        };
        GameHooks.Ended += () => { watch?.Dispose(); watch = null; };
    }

    /// <summary>Чистая функция: предупредить ли сейчас (раз за игру).</summary>
    public static bool ShouldWarn(bool laptop, bool inGame, bool onBattery, bool enabled, bool alreadyWarned) =>
        laptop && inGame && onBattery && enabled && !alreadyWarned;

    private static void Check()
    {
        try
        {
            var h = host;
            if (h == null) return;
            if (!ShouldWarn(Applies, GameHooks.InGame, BatteryInfo.Read().OnBattery, h.Settings.GetBool(AlertKey, true), warned)) return;
            warned = true;
            string game = GameHooks.Game;
            h.Notify(L.T("Игра от батареи"),
                (game != "" ? L.F("{0}: ", game) : "") + L.T("ноутбук работает от батареи, видеокарта и процессор урезаны. Подключите зарядку."),
                true, () => h.Open("sys:Ноутбук"));
        }
        catch { }
    }

    // ================================================================== самопроверка
    public static string SelfTest()
    {
        var fails = new List<string>();
        var log = new StringBuilder();
        int n = 0;
        void Check(string what, bool ok) { n++; if (!ok) fails.Add(what); }

        Check("подмена 1", Detect("1", 1, 3, false).Laptop);
        Check("подмена 0 сильнее батареи", !Detect("0", 2, 10, true).Laptop);
        Check("мобильная платформа", Detect(null, 2, 0, false).Laptop);
        Check("планшет (Slate)", Detect("", 8, 0, false).Laptop);
        Check("корпус Notebook", Detect(null, 1, 10, false).Laptop);
        Check("корпус Convertible", Detect(null, 1, 31, false).Laptop);
        Check("батарея ноутбука", Detect(null, 1, 3, true).Laptop);
        Check("настольный: Desktop, корпус 3, ИБП не батарея", !Detect(null, 1, 3, false).Laptop);
        Check("настольный Tower", !Detect(null, 1, 7, false).Laptop);

        // SMBIOS: структура 0 (BIOS) со строкой, затем структура 3 с типом корпуса 10 (+ бит замка 0x80).
        var smb = new List<byte> { 0, 3, 0, 0, 0, 0, 0, 0 };
        smb.AddRange(new byte[] { 0, 6, 0, 0, 1, 0 }); smb.AddRange(Encoding.ASCII.GetBytes("BIOS")); smb.AddRange(new byte[] { 0, 0 });
        smb.AddRange(new byte[] { 3, 9, 1, 0, 1, 0x8A, 0, 0, 0 }); smb.AddRange(new byte[] { 0, 0 });
        smb.AddRange(new byte[] { 127, 4, 2, 0, 0, 0 });
        var raw = smb.ToArray();
        BitConverter.GetBytes((uint)(raw.Length - 8)).CopyTo(raw, 4);
        Check($"разбор SMBIOS: корпус 10 (получено {ChassisFromSmbios(raw)})", ChassisFromSmbios(raw) == 10);
        Check("пустой SMBIOS", ChassisFromSmbios(new byte[8]) == 0 && ChassisFromSmbios(null) == 0);

        var fresh = new BatteryHealth { Read = true, DesignMwh = 90000, FullMwh = 86000, Cycles = 120 };
        var worn = new BatteryHealth { Read = true, DesignMwh = 90000, FullMwh = 63000 };
        var dead = new BatteryHealth { Read = true, DesignMwh = 50000, FullMwh = 25000, Cycles = 900 };
        var over = new BatteryHealth { Read = true, DesignMwh = 50000, FullMwh = 52000 };
        Check("износ 4 %", Math.Abs(fresh.WearPct - 4.44) < 0.1 && BatteryVerdict(fresh).Level == Severity.Ok);
        Check("износ 30 % — сведения", BatteryVerdict(worn).Level == Severity.Info);
        Check("износ 50 % — предупреждение", BatteryVerdict(dead).Level == Severity.Warn && BatteryVerdict(dead).Text.Contains("900"));
        Check("ёмкость больше заводской — износ 0", over.WearPct == 0);
        Check("не прочитано — без чисел", BatteryVerdict(new BatteryHealth()).Level == Severity.Info);
        Check("без прав — про права", BatteryVerdict(new BatteryHealth { Error = "UnauthorizedAccessException" }).Text is { Length: > 0 } rightsText && (L.Code != "ru" || rightsText.Contains("администратора")));

        Check("режим: экономия", ModeFromGuid(new Guid("961CC777-2547-4F9D-8174-7D86181B8A7A")) == PowerMode.Efficiency);
        Check("режим: сбалансированный", ModeFromGuid(Guid.Empty) == PowerMode.Balanced);
        Check("режим: максимальная", ModeFromGuid(new Guid("DED574B5-45A0-4F42-8737-46345C09C238")) == PowerMode.BestPerformance);
        Check("режим: неизвестный", ModeFromGuid(Guid.NewGuid()) == PowerMode.Unknown);

        var oem = FindOem("ASUSTeK COMPUTER INC.", new[] { "Steam", "B9ECED6F.ArmouryCrate_1.2.3_x64__qmba6cd70vzyy" });
        Check("найдена Armoury Crate", oem.Count == 1 && oem[0].Installed && oem[0].Name == "Armoury Crate");
        var none = FindOem("LENOVO", new[] { "Steam" });
        Check("Lenovo без программ — подсказка", none.Count == 1 && !none[0].Installed && none[0].Maker == "Lenovo");
        Check("неизвестный производитель — ничего", FindOem("Contoso", new[] { "Steam" }).Count == 0);
        Check("MSI по имени платформы", FindOem("Micro-Star International Co., Ltd.", Array.Empty<string>()).FirstOrDefault()?.Maker == "MSI");

        Check("уведомление: ноутбук, игра, батарея", ShouldWarn(true, true, true, true, false));
        Check("уведомление: только раз за игру", !ShouldWarn(true, true, true, true, true));
        Check("уведомление: от зарядки — нет", !ShouldWarn(true, true, false, true, false));
        Check("уведомление: выключено — нет", !ShouldWarn(true, true, true, false, false));
        Check("уведомление: настольный — нет", !ShouldWarn(false, true, true, true, false));

        var adv = Advise(new State { Battery = new BatteryInfo { HasBattery = true, OnBattery = true, Percent = 40 }, Saver = true, Mode = PowerMode.Balanced, Oem = oem, HasDiscrete = true, DiscreteName = "NVIDIA GeForce RTX 4060 Laptop GPU" });
        Check($"советы: батарея первой, режим Windows не советуется от батареи ({adv.Count})",
            adv.Count == 5 && adv[0].Level == Severity.Warn && adv[1].Level == Severity.Warn && !adv.Any(a => a.Title.Contains("Режим питания")));
        var adv2 = Advise(new State { Battery = new BatteryInfo { HasBattery = true }, Mode = PowerMode.Balanced });
        Check("советы от зарядки: режим и охлаждение", adv2.Count == 2 && (L.Code != "ru" || adv2[0].Title.Contains("Сбалансированный")));

        // Что видно на этом ПК (только чтение).
        uint role = 0; int chassis = 0;
        try { role = PowerDeterminePlatformRoleEx(2); } catch { }
        try { chassis = ChassisType(); } catch { }
        var bat = BatteryInfo.Read();
        var (maker, model) = Machine();
        log.AppendLine($"на этом ПК: ноутбук — {(Applies ? "да" : "нет")} ({Reason}); роль платформы {role}, корпус SMBIOS {chassis}, батарея {(bat.HasBattery ? "есть" : "нет")}");
        log.AppendLine($"  компьютер: {maker} {model}; режим питания Windows: {ModeName(ReadMode())}; экономия заряда: {(BatterySaver() ? "вкл." : "выкл.")}");
        try
        {
            var found = FindOem(maker, InstalledNames());
            log.AppendLine("  фирменные программы: " + (found.Count == 0 ? "нет" : string.Join(", ", found.Select(f => f.Name + (f.Installed ? "" : " (не установлена)")))));
        }
        catch (Exception ex) { log.AppendLine("  фирменные программы: ошибка " + ex.Message); }
        if (bat.HasBattery)
        {
            var hh = ReadBattery();
            log.AppendLine(hh.Read ? $"  батарея: {hh.FullMwh} из {hh.DesignMwh} мВт·ч, износ {hh.WearPct:0} %, циклов {hh.Cycles}" : $"  батарея: не прочитана ({hh.Error})");
        }

        return (fails.Count == 0 ? $"OK ({n} проверок)" : "FAIL: " + string.Join("; ", fails)) + "\n" + log;
    }

    // ================================================================== Windows API
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
    [DllImport("kernel32.dll")] private static extern uint GetSystemFirmwareTable(uint provider, uint id, byte[] buffer, uint size);
    [DllImport("powrprof.dll")] private static extern uint PowerDeterminePlatformRoleEx(uint version);
    [DllImport("powrprof.dll")] private static extern uint PowerGetEffectiveOverlayScheme(out Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerGetActualOverlayScheme(out Guid scheme);
}
