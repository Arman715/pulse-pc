using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using PulsePC.Panels;

namespace PulsePC.Services;

/// <summary>Тип беспроводного устройства (значок в оверлее и разделе).</summary>
public enum DeviceKind { Headset, Mouse, Keyboard, Gamepad, Other }

/// <summary>Заряд одного устройства. Percent = null — заряд не прочитан (почему — в Note).</summary>
public sealed class DeviceCharge
{
    /// <summary>Постоянный ключ устройства (для «не чаще раза на устройство»).</summary>
    public string Key { get; init; } = "";
    /// <summary>Понятное название: «Мышь Logitech G Pro X Superlight».</summary>
    public string Name { get; init; } = "";
    public DeviceKind Kind { get; init; }
    public int? Percent { get; init; }
    public bool? Charging { get; init; }
    /// <summary>Проценты приблизительные: по напряжению батареи или по грубому уровню («мало», «средне»).</summary>
    public bool Approx { get; init; }
    /// <summary>Откуда заряд: «Windows (Bluetooth)», «HID», «Logitech HID++», «Xbox (XInput)».</summary>
    public string Source { get; init; } = "";
    public string Note { get; init; } = "";
    public bool Known => Percent != null;
    /// <summary>VID:PID («046D:C547») и адрес Bluetooth — чтобы одно устройство из двух источников не показывалось дважды.</summary>
    internal string Hw { get; set; } = "";
    internal string Mac { get; set; } = "";
}

/// <summary>
/// Заряд беспроводных устройств — гарнитур, мышей, клавиатур, геймпадов. Только чтение: ничего не ставится, настройки устройств не меняются.
/// Источники:
///  • то, что знает Windows: свойство устройства «уровень заряда» ({104EA319-6EE2-4701-BD47-8DDBF425BBE5},2 — его показывают «Параметры»
///    для Bluetooth-устройств, в том числе Bluetooth LE с сервисом Battery), через SetupAPI;
///  • HID-устройства со стандартным значением заряда (Generic Device Controls 0x06 / Battery Strength 0x20, Battery System 0x85 / 0x64, 0x65):
///    устройство открывается без прав на чтение и запись, читается входной или feature-отчёт;
///  • Logitech (HID++ 2.0, открытый протокол — Solaar, libratbag): ping, поиск функции, «Unified Battery» 0x1004, 0x1000, 0x1001, 0x1F20 —
///    только запросы чтения, те же, что делает G HUB;
///  • геймпады Xbox — XInputGetBatteryInformation (грубый уровень).
/// Остальные марки со своим закрытым протоколом не опрашиваются: устройство узнаётся по VID/PID, а человеку пишется, где смотреть заряд.
/// Опрос — раз в <see cref="PollMinutes"/> минуты в фоне (и в игре так же редко): частые запросы будили бы устройства.
/// </summary>
public static class DeviceBattery
{
    // ───────────────────────── пороги и настройки ─────────────────────────

    /// <summary>Как часто спрашивать заряд. Заряд меняется медленно; частые запросы будят мышь и гарнитуру и сами тратят батарею.</summary>
    public const int PollMinutes = 4;
    /// <summary>В оверлее красным — заряд 15 % и ниже: при таком остатке у большинства мышей и гарнитур час-два работы.</summary>
    public const int OverlayRedPct = 15;
    /// <summary>Уведомление перед игрой — как предупреждение, если у какого-то устройства 20 % и меньше (на долгую игру может не хватить).</summary>
    public const int WarnPct = 20;
    /// <summary>Во время игры — одно уведомление, если заряд упал до 10 %: обычно это последние десятки минут.</summary>
    public const int CriticalPct = 10;
    /// <summary>Сколько устройств показывать в оверлее.</summary>
    public const int OverlayMax = 3;
    public const string KRemind = "battery.remind", KInGame = "battery.ingame";
    public const string SectionTitle = "Заряд устройств";

    private static PanelHost host;
    private static Timer timer;
    private static readonly object scanGate = new(), noteGate = new();
    private static volatile IReadOnlyList<DeviceCharge> current = Array.Empty<DeviceCharge>();
    private static volatile DeviceCharge[] overlay = Array.Empty<DeviceCharge>();
    private static int startNotifiedPid;
    private static readonly HashSet<string> criticalNotified = new();

    /// <summary>Последний опрос (новые сначала — с известным зарядом, от меньшего к большему).</summary>
    public static IReadOnlyList<DeviceCharge> Current => current;
    public static DateTime LastScan { get; private set; } = DateTime.MinValue;
    /// <summary>Хотя бы у одного устройства известен заряд.</summary>
    public static bool AnyKnown => overlay.Length > 0;
    /// <summary>Опрос закончился (вызывается в фоновом потоке).</summary>
    public static event Action Changed;
    /// <summary>Проверочный режим со подставными устройствами (снимки): PULSEPC_BATTERY_DEMO=1.</summary>
    public static bool Demo => Environment.GetEnvironmentVariable("PULSEPC_BATTERY_DEMO") == "1";

    public static void Init(PanelHost panelHost)
    {
        host = panelHost;
        GameHooks.Started += OnGameStarted;
        GameHooks.Ended += () => { lock (noteGate) criticalNotified.Clear(); };
        // Первый опрос — не сразу после запуска программы, чтобы не задерживать её старт.
        timer = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(25), TimeSpan.FromMinutes(PollMinutes));
    }

    /// <summary>Устройства для оверлея: с известным зарядом, самые разряженные первыми, не больше трёх. Дёшево — из памяти.</summary>
    public static DeviceCharge[] ForOverlay() => overlay;

    public static DeviceCharge[] PickForOverlay(IEnumerable<DeviceCharge> list) =>
        list.Where(d => d.Known).OrderBy(d => d.Percent).ThenBy(d => d.Name, StringComparer.CurrentCulture).Take(OverlayMax).ToArray();

    /// <summary>Красным в оверлее и разделе: 15 % и ниже, если устройство не заряжается.</summary>
    public static bool IsLow(DeviceCharge d) => d?.Percent is int p && p <= OverlayRedPct && d.Charging != true;

    /// <summary>Опросить сейчас (кнопка «Обновить», начало игры). Повторный вызов во время опроса ждёт его и отдаёт тот же результат.</summary>
    public static IReadOnlyList<DeviceCharge> ScanNow()
    {
        lock (scanGate)
        {
            if ((DateTime.Now - LastScan).TotalSeconds < 5) return current;
            List<DeviceCharge> list;
            try { list = Demo ? Sample() : Scan(null); }
            catch (Exception ex) { Log.Error("заряд устройств", ex); list = current.ToList(); }
            current = Sorted(list);
            overlay = PickForOverlay(current);
            LastScan = DateTime.Now;
        }
        try { Changed?.Invoke(); } catch { }
        return current;
    }

    private static IReadOnlyList<DeviceCharge> Sorted(IEnumerable<DeviceCharge> list) =>
        list.OrderBy(d => d.Known ? 0 : 1).ThenBy(d => d.Percent ?? 0).ThenBy(d => d.Name, StringComparer.CurrentCulture).ToList();

    private static void Poll()
    {
        try
        {
            var list = ScanNow();
            if (host == null || !GameHooks.InGame || !host.Settings.GetBool(KInGame, true)) return;
            List<(string Key, string Title, string Text)> alerts;
            lock (noteGate) alerts = CriticalAlerts(list, criticalNotified);
            foreach (var a in alerts) host.Notify(a.Title, a.Text, true, () => host.Open("sys:" + SectionTitle));
        }
        catch (Exception ex) { Log.Error("заряд устройств: опрос", ex); }
    }

    private static void OnGameStarted(string game, int pid)
    {
        if (host == null) return;
        // Обработчики игры идут по очереди: опрос устройств не должен задерживать остальные (схему питания и т. п.).
        Task.Run(() =>
        {
            try
            {
                // Всегда новый опрос (ScanNow сам не повторяет опрос чаще раза в 5 с): гарнитуры, которые в игре не опрашиваются, читаются сейчас, пока игра загружается.
                var list = ScanNow();
                if (!GameHooks.IsCurrent(pid)) return;
                bool remind = host.Settings.GetBool(KRemind, true);
                lock (noteGate)
                {
                    criticalNotified.Clear();
                    // Уже разряженные к началу игры попадают в уведомление перед игрой — второе «скоро выключится» о них не нужно.
                    // Только если это уведомление вообще показывается и устройство не на зарядке: иначе о почти пустой мыши
                    // человек не узнал бы ни перед игрой, ни во время неё.
                    if (remind) foreach (var d in list.Where(d => d.Percent <= CriticalPct && d.Charging != true)) criticalNotified.Add(d.Key);
                    if (startNotifiedPid == pid) return;
                    startNotifiedPid = pid;
                }
                if (!remind) return;
                var n = BeforeGame(list);
                if (n != null) host.Notify(n.Value.Title, n.Value.Text, n.Value.Warning, () => host.Open("sys:" + SectionTitle));
            }
            catch (Exception ex) { Log.Error("заряд устройств: начало игры", ex); }
        });
    }

    // ───────────────────────── тексты ─────────────────────────

    public static string KindTitle(DeviceKind k) => k switch
    {
        DeviceKind.Headset => L.T("Гарнитура"),
        DeviceKind.Mouse => L.T("Мышь"),
        DeviceKind.Keyboard => L.T("Клавиатура"),
        DeviceKind.Gamepad => L.T("Геймпад"),
        _ => L.T("Устройство"),
    };

    private static string KindWord(DeviceKind k) => k switch
    {
        DeviceKind.Headset => L.T("гарнитура"),
        DeviceKind.Mouse => L.T("мышь"),
        DeviceKind.Keyboard => L.T("клавиатура"),
        DeviceKind.Gamepad => L.T("геймпад"),
        _ => L.T("устройство"),
    };

    /// <summary>Винительный падеж: «подзарядите мышь / гарнитуру».</summary>
    private static string KindWhom(DeviceKind k) => k switch
    {
        DeviceKind.Headset => L.T("гарнитуру"),
        DeviceKind.Mouse => L.T("мышь"),
        DeviceKind.Keyboard => L.T("клавиатуру"),
        DeviceKind.Gamepad => L.T("геймпад"),
        _ => L.T("устройство"),
    };

    /// <summary>«35 %», «≈ 60 %».</summary>
    public static string PercentText(DeviceCharge d) =>
        d?.Percent is int p ? (d.Approx ? "≈\u00A0" : "") + p.ToString(L.Culture) + L.Pick("\u00A0%", "%") : "—";

    /// <summary>Короткое имя в уведомлении: «мышь», а если мышей две — «мышь G502».</summary>
    private static string ShortName(DeviceCharge d, IReadOnlyCollection<DeviceCharge> all, Func<DeviceKind, string> word)
    {
        string w = word(d.Kind);
        if (all.Count(x => x.Kind == d.Kind) < 2) return w;
        string model = d.Name.StartsWith(KindTitle(d.Kind) + " ", StringComparison.CurrentCulture) ? d.Name[(KindTitle(d.Kind).Length + 1)..] : d.Name;
        return w + " " + model;
    }

    /// <summary>Уведомление перед игрой; null — ни у одного устройства заряд не известен.</summary>
    public static (string Title, string Text, bool Warning)? BeforeGame(IReadOnlyList<DeviceCharge> list)
    {
        var known = (list ?? Array.Empty<DeviceCharge>()).Where(d => d.Known).OrderBy(d => d.Percent).ToList();
        if (known.Count == 0) return null;
        string parts = string.Join(", ", known.Take(4).Select(d =>
            ShortName(d, known, KindWord) + " " + PercentText(d) + (d.Charging == true ? L.T(" (заряжается)") : "")));
        var low = known.Where(d => d.Percent <= WarnPct && d.Charging != true).ToList();
        string text = L.F("Заряд: {0}", parts);
        if (low.Count > 0) text += L.F(" — подзарядите {0}", string.Join(", ", low.Select(d => ShortName(d, known, KindWhom))));
        return (L.T("Заряд устройств"), text, low.Count > 0);
    }

    /// <summary>Во время игры: «Мышь: 10 % — скоро выключится», не чаще раза на устройство за игру (notified — уже сказанные).</summary>
    public static List<(string Key, string Title, string Text)> CriticalAlerts(IReadOnlyList<DeviceCharge> list, HashSet<string> notified)
    {
        var res = new List<(string, string, string)>();
        foreach (var d in list ?? Array.Empty<DeviceCharge>())
        {
            if (d.Percent is not int p || p > CriticalPct || d.Charging == true || !notified.Add(d.Key)) continue;
            res.Add((d.Key, L.T("Заряд на исходе"), L.F("{0}: {1} — скоро выключится", d.Name, PercentText(d))));
        }
        return res;
    }

    /// <summary>Одна фраза для раздела.</summary>
    public static string Verdict(IReadOnlyList<DeviceCharge> list, bool scanned)
    {
        if (!scanned) return L.T("Спрашиваю заряд устройств…");
        var known = list.Where(d => d.Known).OrderBy(d => d.Percent).ToList();
        if (known.Count > 0)
        {
            var low = known[0];
            if (IsLow(low)) return L.F("{0}: {1} — пора зарядить.", low.Name, PercentText(low));
            return known.Count == 1
                ? L.F("{0}: заряд {1}.", low.Name, PercentText(low))
                : L.F("Меньше всего заряда — {0}: {1}.", low.Name, PercentText(low));
        }
        return list.Count > 0
            ? L.T("Заряд пока не прочитан ни у одного устройства — причина у каждого в строке ниже.")
            : L.T("Беспроводных устройств с зарядом не найдено.");
    }

    // ───────────────────────── значки ─────────────────────────

    private static readonly Dictionary<DeviceKind, Geometry> Icons = new()
    {
        [DeviceKind.Headset] = Freeze("M3.5,12.5 V10 A6.5,6.5 0 0,1 16.5,10 V12.5 M2.5,11.5 H5.5 V17.5 H2.5 Z M14.5,11.5 H17.5 V17.5 H14.5 Z"),
        [DeviceKind.Mouse] = Freeze("M5.5,7 A4.5,4.5 0 0,1 14.5,7 V13 A4.5,4.5 0 0,1 5.5,13 Z M10,2.5 V7.5"),
        [DeviceKind.Keyboard] = Freeze("M1.5,5.5 H18.5 V14.5 H1.5 Z M4,8.25 H5.5 M7.25,8.25 H8.75 M10.5,8.25 H12 M13.75,8.25 H15.25 M4,11.25 H5.5 M7.25,11.75 H14.5 M15.75,11.25 H16.5"),
        [DeviceKind.Gamepad] = Freeze("M6,6 H14 C17,6 18.5,9 18.5,12.5 C18.5,15 17,16 15.5,16 C14,16 13.5,14.5 12.5,13.5 H7.5 C6.5,14.5 6,16 4.5,16 C3,16 1.5,15 1.5,12.5 C1.5,9 3,6 6,6 Z M5.5,9 V12 M4,10.5 H7 M13.5,10 h0.1 M15.5,11.5 h0.1"),
        [DeviceKind.Other] = Freeze("M2.5,6.5 H16 V13.5 H2.5 Z M16,8.5 H17.5 V11.5 H16"),
    };

    /// <summary>Молния «заряжается» (заливка).</summary>
    public static readonly Geometry BoltIcon = Freeze("M11.5,1.5 L4.5,11 H10 L8.5,18.5 L15.5,9 H10 Z");

    public static Geometry Icon(DeviceKind k) => Icons.TryGetValue(k, out var g) ? g : Icons[DeviceKind.Other];

    private static Geometry Freeze(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    // ───────────────────────── подставные данные ─────────────────────────

    /// <summary>Подставные устройства: предпросмотр оверлея, проверочные снимки.</summary>
    public static List<DeviceCharge> Sample() => new()
    {
        new() { Key = "demo:headset", Name = FriendlyName(DeviceKind.Headset, "HyperX Cloud III Wireless", ""), Kind = DeviceKind.Headset, Percent = 64, Charging = false, Source = L.F("протокол {0}", "HyperX") },
        new() { Key = "demo:mouse", Name = FriendlyName(DeviceKind.Mouse, "Logitech G Pro X Superlight", ""), Kind = DeviceKind.Mouse, Percent = 12, Charging = false, Source = "Logitech HID++" },
        new() { Key = "demo:pad", Name = FriendlyName(DeviceKind.Gamepad, "Xbox", ""), Kind = DeviceKind.Gamepad, Percent = 60, Approx = true, Charging = true, Source = L.T("Xbox (XInput)") },
        new() { Key = "demo:razer", Name = FriendlyName(DeviceKind.Mouse, "Razer DeathAdder V3 Pro", ""), Kind = DeviceKind.Mouse, Percent = 81, Charging = false, Source = L.F("протокол {0}", "Razer") },
    };

    /// <summary>Для оверлея: заряд гарнитуры, мыши и геймпада (заряжается).</summary>
    public static DeviceCharge[] SampleOverlay() => PickForOverlay(Sample());

    // ───────────────────────── опрос ─────────────────────────

    /// <summary>Один опрос всех источников. log — подробности для самопроверки (null — без них).</summary>
    internal static List<DeviceCharge> Scan(List<string> log)
    {
        var result = new List<DeviceCharge>();
        void Safe(string what, Action a)
        {
            try { a(); }
            catch (Exception ex) { log?.Add($"{what}: ошибка {ex.Message}"); }
        }
        List<HidIface> hid = new();
        Safe("HID", () => hid = EnumerateHid(log));
        Safe("Windows", () => result.AddRange(ReadWindowsProperty(log)));
        Safe("HID Battery Strength", () => result.AddRange(ReadHidBattery(hid, log)));
        Safe("Logitech HID++", () => result.AddRange(ReadLogitech(hid, log)));
        Safe("протоколы марок", () => result.AddRange(VendorBattery.Read(hid, GameHooks.InGame, log)));
        Safe("XInput", () => result.AddRange(ReadXInput(log)));
        Safe("марки", () => result.AddRange(BrandNotes(hid, result, InstalledApps(), log)));
        // Одно устройство могло прийти из двух источников (Windows и HID) — оставляем запись с зарядом.
        return MergeDuplicates(result.GroupBy(d => d.Key).Select(g => g.OrderByDescending(d => d.Known).First()).ToList());
    }

    /// <summary>
    /// Одно устройство из разных источников под разными ключами: Logitech через HID++ и через HID Battery Strength (тот же VID:PID),
    /// Bluetooth-устройство через свойство Windows и через HID (тот же адрес). Остаётся запись с зарядом; из равных — HID++ (знает и
    /// про зарядку), потом HID, потом Windows. Разные устройства на одном приёмнике Logitech (одинаковый VID:PID, оба HID++) не сливаются.
    /// </summary>
    internal static List<DeviceCharge> MergeDuplicates(List<DeviceCharge> list)
    {
        static int Rank(DeviceCharge d) => d.Key.StartsWith("logi:", StringComparison.Ordinal) ? 0 : d.Key.StartsWith("hid", StringComparison.Ordinal) ? 1 : 2;
        var keep = new List<DeviceCharge>();
        foreach (var d in list.OrderByDescending(x => x.Known).ThenBy(Rank))
        {
            bool dup = keep.Any(k => k.Known && k.Key != d.Key && (
                (d.Hw.Length > 0 && d.Hw == k.Hw && d.Hw.StartsWith("046D:", StringComparison.Ordinal) && Rank(d) != Rank(k)) ||
                (d.Mac.Length > 0 && d.Mac == k.Mac)));
            if (!dup) keep.Add(d);
        }
        return keep;
    }

    /// <summary>Адрес Bluetooth LE из пути HID («…_rev&amp;0007_d4e5f6a1b2c3&amp;col01#…»); у USB и обычного Bluetooth его в пути нет.</summary>
    internal static string MacFromHidPath(string path)
    {
        var m = Regex.Match(path ?? "", @"_([0-9a-f]{12})(?=[&#]|$)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : "";
    }

    // ───────────────────────── 1. Windows: свойство «уровень заряда» ─────────────────────────

    private static readonly DEVPROPKEY BatteryKey = new(new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);
    private static readonly DEVPROPKEY FriendlyNameKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly DEVPROPKEY DeviceDescKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
    private static readonly DEVPROPKEY ClassOfDeviceKey = new(new Guid("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A"), 10);
    private const uint DevpropTypeByte = 0x03, DevpropTypeUint32 = 0x07, DevpropTypeString = 0x12;

    /// <summary>Значение свойства «уровень заряда»: один байт 0–100; остальное — не заряд.</summary>
    internal static int? ParseWindowsBattery(uint type, byte[] data, int size) =>
        type == DevpropTypeByte && size >= 1 && data[0] <= 100 ? data[0] : null;

    /// <summary>Тип по классу Bluetooth-устройства (Class of Device): аудио — гарнитура, периферия — мышь, клавиатура, геймпад.</summary>
    internal static DeviceKind? KindFromClassOfDevice(uint cod)
    {
        if (cod == 0) return null;
        uint major = (cod >> 8) & 0x1F, minor = (cod >> 2) & 0x3F;
        if (major == 0x04) return DeviceKind.Headset;                       // Audio/Video: гарнитура, наушники, колонка
        if (major != 0x05) return null;                                      // Peripheral
        uint kbdPtr = (minor >> 4) & 0x3, sub = minor & 0x0F;
        if (sub is 0x01 or 0x02) return DeviceKind.Gamepad;                  // джойстик, геймпад
        if (kbdPtr == 0x2) return DeviceKind.Mouse;
        if (kbdPtr is 0x1 or 0x3) return DeviceKind.Keyboard;
        return null;
    }

    /// <summary>Тип по названию (если больше ничего нет).</summary>
    internal static DeviceKind KindFromName(string name)
    {
        string n = (name ?? "").ToLowerInvariant();
        if (Regex.IsMatch(n, @"headset|headphone|earbud|buds|airpods|наушник|гарнитур|hands-free|\bcloud\b|arctis|kraken|barracuda|nari|virtuoso|\bwh-|\bwf-"))
            return DeviceKind.Headset;
        if (Regex.IsMatch(n, @"mouse|мыш|pulsefire|deathadder|viper|basilisk|superlight|g502|g305|g703|g903|rival|aerox")) return DeviceKind.Mouse;
        if (Regex.IsMatch(n, @"keyboard|клавиатур|alloy|huntsman|apex|k\d{2,3}\b")) return DeviceKind.Keyboard;
        if (Regex.IsMatch(n, @"controller|gamepad|геймпад|джойстик|joystick|xbox|dualsense|dualshock|8bitdo|pro controller")) return DeviceKind.Gamepad;
        return DeviceKind.Other;
    }

    private static List<DeviceCharge> ReadWindowsProperty(List<string> log)
    {
        var res = new List<DeviceCharge>();
        IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DigcfPresent | DigcfAllClasses);
        if (set == new IntPtr(-1)) { log?.Add("Windows: список устройств не получен"); return res; }
        int seen = 0;
        try
        {
            var buf = new byte[1024];
            for (uint i = 0; ; i++)
            {
                var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref info)) break;
                seen++;
                var key = BatteryKey;
                if (!SetupDiGetDeviceProperty(set, ref info, ref key, out uint type, buf, (uint)buf.Length, out uint size, 0)) continue;
                int? pct = ParseWindowsBattery(type, buf, (int)size);
                if (pct == null) continue;
                string instance = InstanceId(set, ref info);
                string name = StringProp(set, ref info, FriendlyNameKey);
                if (string.IsNullOrWhiteSpace(name)) name = StringProp(set, ref info, DeviceDescKey);
                uint cod = 0;
                var codKey = ClassOfDeviceKey;
                if (SetupDiGetDeviceProperty(set, ref info, ref codKey, out uint ct, buf, (uint)buf.Length, out uint cs, 0) && ct == DevpropTypeUint32 && cs >= 4)
                    cod = BitConverter.ToUInt32(buf, 0);
                var kind = KindFromClassOfDevice(cod) ?? KindFromName(name);
                bool bt = instance.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) || instance.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase);
                // Bluetooth-устройство даёт несколько узлов (само устройство, «Hands-Free», A2DP) — один ключ на адрес.
                var mac = Regex.Match(instance, @"(?:DEV_|_)([0-9A-F]{12})\b", RegexOptions.IgnoreCase);
                string id = mac.Success ? "win:" + mac.Groups[1].Value.ToUpperInvariant() : "win:" + (name ?? instance).ToLowerInvariant();
                res.Add(new DeviceCharge
                {
                    Key = id, Kind = kind, Percent = pct, Name = FriendlyName(kind, CleanModel(name), ""),
                    Source = bt ? L.T("Windows (Bluetooth)") : L.T("Windows"),
                    Mac = mac.Success ? mac.Groups[1].Value.ToUpperInvariant() : "",
                });
                log?.Add($"Windows: «{name}» ({instance}) — {pct} %, класс {cod:X6}");
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        log?.Add($"Windows: устройств просмотрено {seen}, со свойством «уровень заряда» — {res.Count}");
        return res.GroupBy(d => d.Key).Select(g => g.OrderBy(d => d.Name.Length).First()).ToList();
    }

    private static string InstanceId(IntPtr set, ref SP_DEVINFO_DATA info)
    {
        var sb = new StringBuilder(512);
        return SetupDiGetDeviceInstanceId(set, ref info, sb, sb.Capacity, out _) ? sb.ToString() : "";
    }

    private static string StringProp(IntPtr set, ref SP_DEVINFO_DATA info, DEVPROPKEY key)
    {
        var buf = new byte[1024];
        if (!SetupDiGetDeviceProperty(set, ref info, ref key, out uint type, buf, (uint)buf.Length, out uint size, 0) || type != DevpropTypeString || size < 2) return "";
        return Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0').Trim();
    }

    // ───────────────────────── HID: перечисление ─────────────────────────

    /// <summary>Коллекция HID (одно устройство даёт несколько коллекций: мышь, клавиатура, служебная фирменная).</summary>
    internal sealed class HidIface
    {
        public string Path = "";
        public int Vid = -1, Pid = -1;
        public bool Bluetooth;
        public ushort UsagePage, Usage;
        public int InLen, OutLen, FeatLen;
        public string Product = "";
        /// <summary>Одно физическое подключение (интерфейс USB) — общий ключ всех его коллекций.</summary>
        public string Group = "";
        public List<ValueCap> Battery = new();
        public HashSet<byte> OutputIds = new();
    }

    /// <summary>Значение из описания отчётов HID (то, что нужно для заряда).</summary>
    internal readonly record struct ValueCap(int ReportType, byte ReportId, ushort UsagePage, ushort Usage, ushort LinkCollection, int LogicalMin, int LogicalMax);

    /// <summary>Ключ подключения из пути коллекции: без «&amp;col0N» и номера коллекции в конце.</summary>
    internal static string GroupKey(string path)
    {
        var parts = (path ?? "").ToLowerInvariant().Split('#');
        if (parts.Length < 3) return (path ?? "").ToLowerInvariant();
        string a = Regex.Replace(parts[1], @"&col[0-9a-f]+", "");
        string b = Regex.Replace(parts[2], @"&[0-9a-f]{4}$", "");
        return a + "#" + b;
    }

    /// <summary>Разбор массива HIDP_VALUE_CAPS (по 72 байта): страница, отчёт, коллекция, границы, использование или диапазон.</summary>
    internal static List<ValueCap> ParseValueCaps(byte[] raw, int count, int reportType)
    {
        var list = new List<ValueCap>();
        for (int i = 0; i < count && (i + 1) * ValueCapSize <= raw.Length; i++)
        {
            int o = i * ValueCapSize;
            ushort page = BitConverter.ToUInt16(raw, o);
            byte reportId = raw[o + 2];
            ushort link = BitConverter.ToUInt16(raw, o + 6);
            bool isRange = raw[o + 12] != 0;
            int lmin = BitConverter.ToInt32(raw, o + 40), lmax = BitConverter.ToInt32(raw, o + 44);
            ushort u1 = BitConverter.ToUInt16(raw, o + 56), u2 = BitConverter.ToUInt16(raw, o + 58);
            if (isRange) { for (int u = u1; u <= u2 && u - u1 < 64; u++) list.Add(new ValueCap(reportType, reportId, page, (ushort)u, link, lmin, lmax)); }
            else list.Add(new ValueCap(reportType, reportId, page, u1, link, lmin, lmax));
        }
        return list;
    }

    private const int ValueCapSize = 72;

    /// <summary>Это значение — заряд батареи: Battery Strength (0x06/0x20) или Battery System (0x85) Relative/Absolute State Of Charge.</summary>
    internal static bool IsBatteryUsage(ushort page, ushort usage) =>
        (page == 0x06 && usage == 0x20) || (page == 0x85 && usage is 0x64 or 0x65);

    /// <summary>Сырое значение → проценты по границам из описания отчётов; вне границ — не заряд.</summary>
    internal static int? ScaleBattery(long raw, int min, int max)
    {
        if (max <= min) { min = 0; max = 100; }
        if (raw < min || raw > max) return null;
        return (int)Math.Round((raw - min) * 100.0 / (max - min));
    }

    private static List<HidIface> EnumerateHid(List<string> log)
    {
        var list = new List<HidIface>();
        HidD_GetHidGuid(out Guid guid);
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1)) return list;
        try
        {
            for (uint i = 0; ; i++)
            {
                var did = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref did)) break;
                SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out uint need, IntPtr.Zero);
                if (need == 0 || need > 4096) continue;
                IntPtr detail = Marshal.AllocHGlobal((int)need);
                string path = null;
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(set, ref did, detail, need, out _, IntPtr.Zero)) path = Marshal.PtrToStringUni(detail + 4);
                }
                finally { Marshal.FreeHGlobal(detail); }
                if (string.IsNullOrEmpty(path)) continue;
                var d = DescribeHid(path);
                if (d != null) list.Add(d);
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        log?.Add($"HID: коллекций {list.Count}");
        if (log != null)
            foreach (var d in list.Where(x => x.Vid >= 0).OrderBy(x => x.Vid).ThenBy(x => x.Pid).ThenBy(x => x.UsagePage))
                log.Add($"  HID {d.Vid:X4}:{d.Pid:X4} страница {d.UsagePage:X4}/{d.Usage:X4}, отчёты: вход {d.InLen}, выход {d.OutLen}, feature {d.FeatLen}" +
                        $"{(d.Battery.Count > 0 ? ", есть значение заряда" : "")}{(d.OutputIds.Count > 0 ? ", выходные id " + string.Join(" ", d.OutputIds.Select(i => i.ToString("X2"))) : "")} «{d.Product}»");
        return list;
    }

    /// <summary>Описание коллекции. Открывается без прав на чтение и запись: так можно спросить описание и отчёты даже у мыши и клавиатуры.</summary>
    private static HidIface DescribeHid(string path)
    {
        using var h = CreateFile(path, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var d = new HidIface { Path = path, Group = GroupKey(path) };
        var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        if (HidD_GetAttributes(h, ref attr)) { d.Vid = attr.VendorID; d.Pid = attr.ProductID; }
        var (_, _, bt) = InputTuning.ParseDevicePath(path);
        d.Bluetooth = bt;
        if (!HidD_GetPreparsedData(h, out IntPtr pp)) return d;
        try
        {
            if (HidP_GetCaps(pp, out HIDP_CAPS caps) != HidpStatusSuccess) return d;
            d.UsagePage = caps.UsagePage; d.Usage = caps.Usage;
            d.InLen = caps.InputReportByteLength; d.OutLen = caps.OutputReportByteLength; d.FeatLen = caps.FeatureReportByteLength;
            foreach (var (type, n) in new[] { (0, caps.NumberInputValueCaps), (2, caps.NumberFeatureValueCaps), (1, caps.NumberOutputValueCaps) })
            {
                if (n == 0 || n > 512) continue;
                var raw = new byte[n * ValueCapSize];
                ushort len = n;
                if (HidP_GetValueCaps(type, raw, ref len, pp) != HidpStatusSuccess) continue;
                var vc = ParseValueCaps(raw, len, type);
                if (type == 1) { foreach (var c in vc) d.OutputIds.Add(c.ReportId); continue; }
                d.Battery.AddRange(vc.Where(c => IsBatteryUsage(c.UsagePage, c.Usage)));
            }
        }
        finally { HidD_FreePreparsedData(pp); }
        var buf = new byte[512];
        try
        {
            if (HidD_GetProductString(h, buf, (uint)buf.Length))
            {
                string s = Encoding.Unicode.GetString(buf);
                int z = s.IndexOf('\0');
                d.Product = (z >= 0 ? s[..z] : s).Trim();
            }
        }
        catch { }
        return d;
    }

    /// <summary>Тип по коллекциям подключения: мышь, клавиатура, геймпад; одни «аудио-кнопки» (громкость, телефония) — гарнитура.</summary>
    internal static DeviceKind KindFromCollections(IEnumerable<(ushort Page, ushort Usage)> cols, string product)
    {
        var byName = KindFromName(product);
        if (byName != DeviceKind.Other) return byName;
        var set = cols.ToList();
        if (set.Any(c => c.Page == 1 && c.Usage == 2)) return DeviceKind.Mouse;
        if (set.Any(c => c.Page == 1 && c.Usage is 4 or 5)) return DeviceKind.Gamepad;
        if (set.Any(c => c.Page == 1 && c.Usage == 6)) return DeviceKind.Keyboard;
        if (set.Any(c => c.Page == 0x0B) || set.Any(c => c.Page == 0x0C)) return DeviceKind.Headset;
        return DeviceKind.Other;
    }

    // ───────────────────────── 2. HID Battery Strength ─────────────────────────

    private static List<DeviceCharge> ReadHidBattery(List<HidIface> hid, List<string> log)
    {
        var res = new List<DeviceCharge>();
        foreach (var d in hid.Where(x => x.Battery.Count > 0))
        {
            // ИБП и батарея ноутбука (Power Device 0x84, Battery System 0x85 на верхнем уровне) — это питание компьютера, а не беспроводное устройство.
            if (d.UsagePage is 0x84 or 0x85) { log?.Add($"HID: {d.Vid:X4}:{d.Pid:X4} — источник питания (ИБП/батарея), пропущен"); continue; }
            int? pct = null;
            string how = "";
            using var h = CreateFile(d.Path, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;
            if (!HidD_GetPreparsedData(h, out IntPtr pp)) continue;
            try
            {
                foreach (var cap in d.Battery.OrderBy(c => c.ReportType == 2 ? 0 : 1))
                {
                    int len = cap.ReportType == 2 ? d.FeatLen : d.InLen;
                    if (len <= 0) continue;
                    var report = new byte[len];
                    report[0] = cap.ReportId;
                    bool ok = cap.ReportType == 2 ? HidD_GetFeature(h, report, len) : HidD_GetInputReport(h, report, len);
                    if (!ok) { how = $"отчёт {cap.ReportId} не прочитан"; continue; }
                    if (HidP_GetUsageValue(cap.ReportType, cap.UsagePage, cap.LinkCollection, cap.Usage, out uint v, pp, report, (uint)len) != HidpStatusSuccess) continue;
                    pct = ScaleBattery(v, cap.LogicalMin, cap.LogicalMax);
                    how = $"{(cap.ReportType == 2 ? "feature" : "input")}-отчёт {cap.ReportId}, значение {v} из {cap.LogicalMin}…{cap.LogicalMax}";
                    if (pct != null) break;
                }
            }
            finally { HidD_FreePreparsedData(pp); }
            var cols = hid.Where(x => x.Group == d.Group).Select(x => (x.UsagePage, x.Usage));
            string product = d.Product.Length > 0 ? d.Product : hid.Where(x => x.Group == d.Group).Select(x => x.Product).FirstOrDefault(p => p.Length > 0) ?? "";
            var kind = KindFromCollections(cols, product);
            string brand = InputTuning.BrandName(d.Vid, product);
            log?.Add($"HID Battery Strength: {d.Vid:X4}:{d.Pid:X4} «{product}» — {(pct is int p ? p + " %" : "не прочитано")} ({how})");
            if (pct == null) continue;
            res.Add(new DeviceCharge
            {
                Key = d.Bluetooth ? $"hid-bt:{d.Vid:X4}:{d.Pid:X4}" : $"hid:{d.Vid:X4}:{d.Pid:X4}",
                Kind = kind, Percent = pct, Name = FriendlyName(kind, CleanModel(product), brand),
                Source = d.Bluetooth ? L.T("HID через Bluetooth") : "HID",
                Hw = $"{d.Vid:X4}:{d.Pid:X4}", Mac = d.Bluetooth ? MacFromHidPath(d.Path) : "",
            });
        }
        return res.GroupBy(d => d.Key).Select(g => g.First()).ToList();
    }

    // ───────────────────────── 3. Logitech HID++ 2.0 ─────────────────────────

    public const int LogitechVid = 0x046D;
    private const byte SwId = 0x0B;                     // свой номер в запросах: ответы другим программам (G HUB) не путаются с нашими
    internal const ushort FeatRoot = 0x0000, FeatName = 0x0005, FeatUnified = 0x1004, FeatStatus = 0x1000, FeatVoltage = 0x1001, FeatAdc = 0x1F20;

    /// <summary>Что узнали об устройстве на приёмнике или напрямую (кэш на полчаса: имя и номер функции не меняются).</summary>
    private sealed class LogiInfo
    {
        public string Name = "";
        public DeviceKind Kind = DeviceKind.Other;
        public byte BatteryIndex;
        public ushort BatteryFeature;
        public bool PercentSupported = true;
        public DateTime At;
    }

    private static readonly Dictionary<string, LogiInfo> logiCache = new();

    /// <summary>Кусок ответа HID++: 1 — ответ на наш запрос (payload — параметры), 2 — ошибка HID++ 2.0, 3 — ошибка HID++ 1.0, 0 — чужое.</summary>
    internal static int MatchReply(byte[] r, int n, byte dev, byte feat, byte funcSw, out byte[] payload, out int error)
    {
        payload = Array.Empty<byte>(); error = 0;
        if (r == null || n < 7 || r[0] is not (0x10 or 0x11 or 0x12) || r[1] != dev) return 0;
        if (r[2] == feat && r[3] == funcSw) { payload = r.AsSpan(4, n - 4).ToArray(); return 1; }
        if (r[2] == 0xFF && r[3] == feat && r[4] == funcSw) { error = r[5]; return 2; }
        if (r[2] == 0x8F && r[3] == feat && r[4] == funcSw) { error = r[5]; return 3; }
        return 0;
    }

    /// <summary>Запрос HID++: короткий (7 байт) или длинный (20 байт).</summary>
    internal static byte[] BuildRequest(bool longReport, byte dev, byte feat, byte func, params byte[] args)
    {
        var m = new byte[longReport ? 20 : 7];
        m[0] = (byte)(longReport ? 0x11 : 0x10);
        m[1] = dev; m[2] = feat; m[3] = (byte)((func << 4) | SwId);
        if (args != null) Array.Copy(args, 0, m, 4, Math.Min(args.Length, m.Length - 4));
        return m;
    }

    /// <summary>Unified Battery (0x1004), get_status: заряд %, уровень, зарядка. Без процентов — уровень «полный/хороший/мало/критично».</summary>
    internal static (int? Percent, bool? Charging, bool Approx) ParseUnified(byte[] p, bool percentSupported)
    {
        if (p == null || p.Length < 3) return (null, null, false);
        bool? charging = p[2] switch { 1 or 2 => true, 3 => true, 0 => false, _ => null };    // 3 — заряжено, ещё на кабеле
        // Ноль процентов при уровне выше «критично» — устройство проценты не считает, берём уровень.
        if (percentSupported && p[0] <= 100 && (p[0] > 0 || p[1] <= 0x01)) return (p[0], charging, false);
        int? byLevel = (p[1] & 0x08) != 0 ? 90 : (p[1] & 0x04) != 0 ? 50 : (p[1] & 0x02) != 0 ? 20 : (p[1] & 0x01) != 0 ? 5 : null;
        return (byLevel, charging, true);
    }

    /// <summary>Battery Status (0x1000): заряд %, следующий уровень, состояние (0 — разряжается, 1–4 — заряжается/заряжено).</summary>
    internal static (int? Percent, bool? Charging) ParseStatus1000(byte[] p)
    {
        if (p == null || p.Length < 3 || p[0] > 100) return (null, null);
        bool? charging = p[2] switch { 0 => false, 1 or 2 or 3 or 4 => true, _ => null };
        if (p[2] is 5 or 6) return (null, null);           // неисправная батарея, перегрев
        return (p[0] == 0 && p[2] == 3 ? 100 : p[0], charging);
    }

    /// <summary>Напряжение Li-ion → примерный заряд (таблица, как в Solaar). Это оценка: кривая у разных батарей немного своя.</summary>
    internal static int VoltageToPercent(int mv)
    {
        int[,] t = { { 4186, 100 }, { 4067, 90 }, { 3989, 80 }, { 3922, 70 }, { 3859, 60 }, { 3811, 50 }, { 3778, 40 }, { 3751, 30 }, { 3717, 20 }, { 3671, 10 }, { 3646, 5 }, { 3579, 2 }, { 3500, 0 } };
        if (mv >= t[0, 0]) return 100;
        for (int i = 1; i < t.GetLength(0); i++)
            if (mv >= t[i, 0])
                return (int)Math.Round(t[i, 1] + (t[i - 1, 1] - t[i, 1]) * (double)(mv - t[i, 0]) / (t[i - 1, 0] - t[i, 0]));
        return 0;
    }

    /// <summary>Battery Voltage (0x1001): напряжение мВ, флаги (бит 7 — на зарядке).</summary>
    internal static (int? Percent, bool? Charging) ParseVoltage1001(byte[] p)
    {
        if (p == null || p.Length < 3) return (null, null);
        int mv = (p[0] << 8) | p[1];
        if (mv < 2500 || mv > 5000) return (null, null);
        return (VoltageToPercent(mv), (p[2] & 0x80) != 0);
    }

    /// <summary>ADC Measurement (0x1F20, у гарнитур): напряжение мВ, флаги (бит 0 — измерение есть, бит 1 — заряжается).</summary>
    internal static (int? Percent, bool? Charging) ParseAdc1F20(byte[] p)
    {
        if (p == null || p.Length < 3 || (p[2] & 0x01) == 0) return (null, null);
        int mv = (p[0] << 8) | p[1];
        if (mv < 2500 || mv > 5000) return (null, null);
        return (VoltageToPercent(mv), (p[2] & 0x02) != 0);
    }

    /// <summary>Тип из функции Device Name (0x0005, getDeviceType).</summary>
    internal static DeviceKind KindFromLogiType(int t) => t switch
    {
        0 or 2 => DeviceKind.Keyboard,
        3 or 4 or 5 => DeviceKind.Mouse,
        8 or 15 => DeviceKind.Headset,
        11 or 12 => DeviceKind.Gamepad,
        _ => DeviceKind.Other,
    };

    private static List<DeviceCharge> ReadLogitech(List<HidIface> hid, List<string> log)
    {
        var res = new List<DeviceCharge>();
        // Служебные коллекции HID++: страница 0xFF00 или 0xFF43, отчёты 0x10 (короткий) и 0x11 (длинный).
        var groups = hid.Where(d => d.Vid == LogitechVid && d.UsagePage is 0xFF00 or 0xFF43 && (d.InLen is 7 or 20 || d.OutLen is 7 or 20))
                        .GroupBy(d => d.Group).ToList();
        if (groups.Count == 0) { log?.Add("Logitech HID++: служебных коллекций нет"); return res; }
        foreach (var g in groups)
        {
            var members = g.ToList();
            var all = hid.Where(x => x.Group == g.Key).ToList();
            string product = all.Select(x => x.Product).FirstOrDefault(p => p.Length > 0) ?? "";
            int pid = members[0].Pid;
            string hw = $"{LogitechVid:X4}:{pid:X4}", mac = members[0].Bluetooth ? MacFromHidPath(members[0].Path) : "";
            bool canWrite = members.Any(m => (m.OutLen == 20 && (m.OutputIds.Count == 0 || m.OutputIds.Contains(0x11))) || (m.OutLen == 7 && (m.OutputIds.Count == 0 || m.OutputIds.Contains(0x10))));
            if (!canWrite) { log?.Add($"Logitech HID++ {LogitechVid:X4}:{pid:X4}: нет отчётов HID++ для запроса"); continue; }
            HidppPort port;
            try { port = HidppPort.Open(members); }
            catch (Exception ex)
            {
                log?.Add($"Logitech HID++ {LogitechVid:X4}:{pid:X4} «{product}»: не открылось ({ex.Message})");
                res.Add(new DeviceCharge
                {
                    Key = $"logi:{pid:X4}", Kind = KindFromName(product), Name = FriendlyName(KindFromName(product), CleanModel(product), "Logitech"),
                    Source = "Logitech HID++", Note = L.T("Не удалось прочитать: устройство занято другой программой (например, Logitech G HUB). Заряд виден в G HUB."),
                    Hw = hw, Mac = mac,
                });
                continue;
            }
            using (port)
            {
                var found = new List<DeviceCharge>();
                // Сначала само устройство (0xFF); если ответил приёмник протокола HID++ 1.0 — опрашиваем его места 1–6.
                var direct = ProbeIndex(port, g.Key, 0xFF, product, log, out int directErr);
                bool receiver = direct == null && directErr is 2 or 3;
                if (direct != null) found.Add(direct);
                if (receiver)
                    for (byte slot = 1; slot <= 6; slot++)
                        if (ProbeIndex(port, g.Key, slot, "", log, out _) is { } d) found.Add(d);
                if (found.Count == 0 && directErr == 0 && !receiver)
                {
                    log?.Add($"Logitech HID++ {LogitechVid:X4}:{pid:X4} «{product}»: батареи нет (проводное устройство)");
                    continue;
                }
                if (found.Count == 0)
                {
                    var kind = receiver ? DeviceKind.Other : KindFromName(product);
                    res.Add(new DeviceCharge
                    {
                        Key = $"logi:{pid:X4}", Kind = kind, Source = "Logitech HID++",
                        Name = receiver ? L.T("Приёмник Logitech") : FriendlyName(kind, CleanModel(product), "Logitech"),
                        Note = receiver ? L.T("Устройства на этом приёмнике сейчас не отвечают — наверное, выключены или спят. Включите их и нажмите «Обновить».")
                                        : L.T("Не удалось прочитать: устройство не ответило на запрос заряда. Если оно включено и запущен Logitech G HUB, заряд виден в нём."),
                        Hw = hw, Mac = mac,
                    });
                }
                foreach (var f in found) { f.Hw = hw; f.Mac = mac; }
                res.AddRange(found);
            }
        }
        return res;
    }

    /// <summary>
    /// Узнать устройство с номером dev и прочитать заряд. err: 0 — ответило, 1 — молчит, 2/3 — ошибка HID++ 2.0/1.0
    /// (у 0xFF ошибка HID++ 1.0 значит «это приёмник»). null — заряда нет или устройства нет.
    /// </summary>
    private static DeviceCharge ProbeIndex(HidppPort port, string group, byte dev, string product, List<string> log, out int err)
    {
        string cacheKey = group + "#" + dev;
        LogiInfo info;
        lock (logiCache) logiCache.TryGetValue(cacheKey, out info);
        if (info == null || (DateTime.Now - info.At).TotalMinutes > 30)
        {
            info = Discover(port, dev, product, log, out err);
            lock (logiCache) { if (info != null) logiCache[cacheKey] = info; else logiCache.Remove(cacheKey); }
            if (info == null) return null;
        }
        err = 0;
        var (pct, charging, approx) = ReadLogiBattery(port, dev, info, out int rerr);
        if (rerr != 0)
        {
            lock (logiCache) logiCache.Remove(cacheKey);
            log?.Add($"Logitech HID++ [{dev:X2}] «{info.Name}»: заряд не прочитан (ошибка {rerr})");
            err = 1;
            return new DeviceCharge
            {
                Key = $"logi:{group}:{dev}", Kind = info.Kind, Name = FriendlyName(info.Kind, CleanModel(info.Name), "Logitech"), Source = "Logitech HID++",
                Note = L.T("Устройство сейчас не отвечает — наверное, выключено или спит. Включите его и нажмите «Обновить»."),
            };
        }
        log?.Add($"Logitech HID++ [{dev:X2}] «{info.Name}»: функция {info.BatteryFeature:X4} → {(pct is int p ? p + " %" : "—")}{(charging == true ? ", заряжается" : "")}{(approx ? ", оценка" : "")}");
        return new DeviceCharge
        {
            Key = $"logi:{group}:{dev}", Kind = info.Kind, Name = FriendlyName(info.Kind, CleanModel(info.Name), "Logitech"),
            Percent = pct, Charging = charging, Approx = approx,
            Source = "Logitech HID++" + (info.BatteryFeature is FeatVoltage or FeatAdc ? L.T(" (по напряжению)") : ""),
            Note = pct == null ? L.T("Устройство ответило, но заряд не сообщило.") : "",
        };
    }

    private static LogiInfo Discover(HidppPort port, byte dev, string product, List<string> log, out int err)
    {
        // ping (функция 1 корневой функции): версия протокола. HID++ 1.0 (старые приёмники) отвечает ошибкой.
        var ping = port.Request(dev, 0x00, 0x1, new byte[] { 0, 0, 0xAA }, 600, out err);
        if (ping == null)
        {
            log?.Add($"Logitech HID++ [{dev:X2}]: ping — {(err == 3 ? "ответ HID++ 1.0 (приёмник или пустое место)" : err == 2 ? "ошибка HID++ 2.0" : "нет ответа")}");
            err = err == 0 ? 1 : err;
            return null;
        }
        if (ping.Length < 2 || ping[0] < 2) { log?.Add($"Logitech HID++ [{dev:X2}]: протокол {ping.ElementAtOrDefault(0)} — не 2.0"); err = 3; return null; }
        byte Feature(ushort id)
        {
            var r = port.Request(dev, 0x00, 0x0, new[] { (byte)(id >> 8), (byte)id }, 600, out _);
            return r is { Length: > 0 } ? r[0] : (byte)0;
        }
        var info = new LogiInfo { At = DateTime.Now, Name = product };
        byte nameIdx = Feature(FeatName);
        if (nameIdx != 0)
        {
            var cnt = port.Request(dev, nameIdx, 0x0, null, 600, out _);
            int total = cnt is { Length: > 0 } ? cnt[0] : 0;
            var name = new List<byte>();
            for (int guard = 0; name.Count < total && guard < 8; guard++)
            {
                var part = port.Request(dev, nameIdx, 0x1, new[] { (byte)name.Count }, 600, out _);
                if (part == null || part.Length == 0) break;
                name.AddRange(part.Take(total - name.Count).TakeWhile(b => b != 0));
                if (part.Contains((byte)0)) break;
            }
            string s = Encoding.ASCII.GetString(name.ToArray()).Trim();
            if (s.Length > 1) info.Name = s;
            var type = port.Request(dev, nameIdx, 0x2, null, 600, out _);
            if (type is { Length: > 0 }) info.Kind = KindFromLogiType(type[0]);
        }
        if (info.Kind == DeviceKind.Other) info.Kind = KindFromName(info.Name);
        foreach (var id in new[] { FeatUnified, FeatStatus, FeatVoltage, FeatAdc })
        {
            byte idx = Feature(id);
            if (idx == 0) continue;
            info.BatteryIndex = idx; info.BatteryFeature = id;
            break;
        }
        log?.Add($"Logitech HID++ [{dev:X2}]: протокол {ping[0]}.{ping.ElementAtOrDefault(1)}, «{info.Name}», тип {info.Kind}, функция заряда {(info.BatteryIndex == 0 ? "нет" : $"{info.BatteryFeature:X4} (номер {info.BatteryIndex})")}");
        if (info.BatteryIndex == 0) { err = 0; return null; }
        if (info.BatteryFeature == FeatUnified)
        {
            var caps = port.Request(dev, info.BatteryIndex, 0x0, null, 600, out _);
            info.PercentSupported = caps is not { Length: > 1 } || (caps[1] & 0x02) != 0;
        }
        err = 0;
        return info;
    }

    private static (int? Percent, bool? Charging, bool Approx) ReadLogiBattery(HidppPort port, byte dev, LogiInfo info, out int err)
    {
        byte func = info.BatteryFeature == FeatUnified ? (byte)0x1 : (byte)0x0;
        var p = port.Request(dev, info.BatteryIndex, func, null, 800, out err);
        if (p == null) { err = err == 0 ? 1 : err; return (null, null, false); }
        err = 0;
        switch (info.BatteryFeature)
        {
            case FeatUnified: return ParseUnified(p, info.PercentSupported);
            case FeatStatus: { var (a, b) = ParseStatus1000(p); return (a, b, false); }
            case FeatVoltage: { var (a, b) = ParseVoltage1001(p); return (a, b, true); }
            default: { var (a, b) = ParseAdc1F20(p); return (a, b, true); }
        }
    }

    /// <summary>
    /// Канал HID++: служебные коллекции одного подключения, открытые на чтение и запись (совместно — G HUB может держать их же).
    /// Чтение — асинхронное, на всех коллекциях сразу: короткий ответ об ошибке приходит в короткую коллекцию, длинный — в длинную.
    /// </summary>
    internal sealed class HidppPort : IDisposable
    {
        private sealed class Pipe
        {
            public SafeFileHandle Handle;
            public int InLen, OutLen;
            /// <summary>Номера выходных отчётов коллекции (пусто — без номеров в описании).</summary>
            public HashSet<byte> OutIds = new();
            public IntPtr Buffer, Ovl, Event;
            /// <summary>В эту коллекцию можно отправить отчёт HID++ с этим номером и длиной (0x10 — 7 байт, 0x11 — 20).</summary>
            public bool Takes(byte id, int len) => OutLen == len && (OutIds.Count == 0 || OutIds.Contains(id));
            public bool Pending;
        }

        private readonly List<Pipe> pipes = new();

        public static HidppPort Open(IEnumerable<HidIface> members)
        {
            var port = new HidppPort();
            Exception last = null;
            foreach (var m in members)
            {
                var h = CreateFile(m.Path, GenericRead | GenericWrite, FileShareReadWrite, IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
                if (h.IsInvalid) { last = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); h.Dispose(); continue; }
                int ovlSize = IntPtr.Size * 3 + 8;
                var p = new Pipe { Handle = h, InLen = m.InLen, OutLen = m.OutLen, OutIds = m.OutputIds, Buffer = Marshal.AllocHGlobal(Math.Max(64, m.InLen)), Ovl = Marshal.AllocHGlobal(ovlSize), Event = CreateEvent(IntPtr.Zero, true, false, null) };
                port.pipes.Add(p);
            }
            if (port.pipes.Count == 0) { port.Dispose(); throw last ?? new IOException("нет коллекций"); }
            return port;
        }

        private static void ZeroOvl(Pipe p, IntPtr ovl, IntPtr evt)
        {
            int size = IntPtr.Size * 3 + 8;
            for (int i = 0; i < size; i++) Marshal.WriteByte(ovl, i, 0);
            Marshal.WriteIntPtr(ovl, IntPtr.Size * 2 + 8, evt);
        }

        private void Arm(Pipe p)
        {
            if (p.Pending || p.InLen <= 0) return;
            ZeroOvl(p, p.Ovl, p.Event);
            if (ReadFile(p.Handle, p.Buffer, p.InLen, IntPtr.Zero, p.Ovl) || Marshal.GetLastWin32Error() == ErrorIoPending) p.Pending = true;
        }

        private bool Write(byte[] msg)
        {
            // Только в коллекцию, где описан такой отчёт HID++ (номер и длина): в чужие отчёты ничего не пишется.
            var pipe = pipes.FirstOrDefault(p => p.Takes(msg[0], msg.Length));
            if (pipe == null) return false;
            IntPtr buf = Marshal.AllocHGlobal(msg.Length), ovl = Marshal.AllocHGlobal(IntPtr.Size * 3 + 8), evt = CreateEvent(IntPtr.Zero, true, false, null);
            try
            {
                Marshal.Copy(msg, 0, buf, msg.Length);
                ZeroOvl(pipe, ovl, evt);
                if (!WriteFile(pipe.Handle, buf, msg.Length, IntPtr.Zero, ovl) && Marshal.GetLastWin32Error() != ErrorIoPending) return false;
                if (WaitForSingleObject(evt, 1000) != 0) { CancelIoEx(pipe.Handle, ovl); GetOverlappedResult(pipe.Handle, ovl, out _, true); return false; }
                return GetOverlappedResult(pipe.Handle, ovl, out int n, false) && n > 0;
            }
            finally { Marshal.FreeHGlobal(buf); Marshal.FreeHGlobal(ovl); CloseHandle(evt); }
        }

        /// <summary>Запрос и ожидание ответа. null — ответа нет (error = 0) или ошибка HID++ (error = 2 или 3).</summary>
        public byte[] Request(byte dev, byte feat, byte func, byte[] args, int timeoutMs, out int error)
        {
            error = 0;
            foreach (var p in pipes) Arm(p);
            bool longOk = pipes.Any(p => p.Takes(0x11, 20));
            var msg = BuildRequest(longOk, dev, feat, func, args ?? Array.Empty<byte>());
            if (!Write(msg)) return null;
            byte funcSw = msg[3];
            var deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                long left = deadline - Environment.TickCount64;
                if (left <= 0) return null;
                var armed = pipes.Where(p => p.Pending).ToList();
                if (armed.Count == 0) return null;
                uint w = WaitForMultipleObjects((uint)armed.Count, armed.Select(p => p.Event).ToArray(), false, (uint)left);
                if (w >= armed.Count) return null;             // время вышло
                var pipe = armed[(int)w];
                pipe.Pending = false;
                if (!GetOverlappedResult(pipe.Handle, pipe.Ovl, out int n, false) || n <= 0) { Arm(pipe); continue; }
                var data = new byte[n];
                Marshal.Copy(pipe.Buffer, data, 0, n);
                Arm(pipe);
                int kind = MatchReply(data, n, dev, feat, funcSw, out var payload, out int code);
                if (kind == 1) return payload;
                if (kind is 2 or 3) { error = kind; return null; }
            }
        }

        public void Dispose()
        {
            foreach (var p in pipes)
            {
                try
                {
                    if (p.Pending) { CancelIoEx(p.Handle, p.Ovl); GetOverlappedResult(p.Handle, p.Ovl, out _, true); }
                }
                catch { }
                try { p.Handle.Dispose(); } catch { }
                if (p.Buffer != IntPtr.Zero) Marshal.FreeHGlobal(p.Buffer);
                if (p.Ovl != IntPtr.Zero) Marshal.FreeHGlobal(p.Ovl);
                if (p.Event != IntPtr.Zero) CloseHandle(p.Event);
            }
            pipes.Clear();
        }
    }

    // ───────────────────────── 4. Xbox (XInput) ─────────────────────────

    /// <summary>Грубый уровень XInput → примерные проценты: пусто 5, мало 20, средне 60, полный 95.</summary>
    internal static int? XInputLevelPercent(byte type, byte level) =>
        type is 0 or 1 ? null : level switch { 0 => 5, 1 => 20, 2 => 60, 3 => 95, _ => null };

    private static List<DeviceCharge> ReadXInput(List<string> log)
    {
        var res = new List<DeviceCharge>();
        try
        {
            for (int user = 0; user < 4; user++)
                foreach (byte devType in new byte[] { 0, 1 })      // 0 — сам геймпад, 1 — гарнитура, подключённая к нему
                {
                    if (XInputGetBatteryInformation(user, devType, out var b) != 0) continue;
                    int? pct = XInputLevelPercent(b.BatteryType, b.BatteryLevel);
                    log?.Add($"XInput: игрок {user + 1}, {(devType == 0 ? "геймпад" : "гарнитура геймпада")} — тип батареи {b.BatteryType}, уровень {b.BatteryLevel}");
                    if (pct == null) continue;
                    var kind = devType == 0 ? DeviceKind.Gamepad : DeviceKind.Headset;
                    res.Add(new DeviceCharge
                    {
                        Key = $"xinput:{user}:{devType}", Kind = kind, Percent = pct, Approx = true,
                        Name = devType == 0 ? L.F("Геймпад Xbox (игрок {0})", user + 1) : L.F("Гарнитура геймпада Xbox (игрок {0})", user + 1),
                        Source = L.T("Xbox (XInput)"),
                        Note = L.T("Геймпады Xbox сообщают только уровень: пусто, мало, средне, полный — проценты примерные."),
                    });
                }
        }
        catch (DllNotFoundException) { log?.Add("XInput: xinput1_4.dll нет"); }
        catch (EntryPointNotFoundException) { log?.Add("XInput: функции нет"); }
        if (log != null && res.Count == 0) log.Add("XInput: геймпадов Xbox на батарейках не подключено");
        return res;
    }

    // ───────────────────────── 5. Марки со своим протоколом ─────────────────────────

    /// <summary>Марка → её программа и признаки установленной программы (имя в «Установленных приложениях» или пакета Store).</summary>
    internal sealed record VendorApp(string Brand, string App, string[] Marks);

    internal static readonly Dictionary<int, VendorApp> VendorApps = new()
    {
        // Logitech — только для устройств без HID++ (например, гарнитуры G435 со своим протоколом): у остальных заряд читается сам.
        [0x046D] = new("Logitech", "Logitech G HUB", new[] { "logitech g hub", "lghub" }),
        [0x03F0] = new("HyperX", "HyperX NGENUITY", new[] { "ngenuity" }),
        [0x0951] = new("HyperX", "HyperX NGENUITY", new[] { "ngenuity" }),
        [0x1532] = new("Razer", "Razer Synapse", new[] { "razer synapse", "razer central", "razersynapse" }),
        [0x1038] = new("SteelSeries", "SteelSeries GG", new[] { "steelseries gg", "steelseries engine" }),
        [0x1B1C] = new("Corsair", "Corsair iCUE", new[] { "icue" }),
        [0x0B05] = new("ASUS", "Armoury Crate", new[] { "armoury crate", "armourycrate" }),
        [0x1E7D] = new("ROCCAT", "Turtle Beach Swarm II", new[] { "swarm" }),
        [0x10F5] = new("Turtle Beach", "Turtle Beach Swarm II", new[] { "swarm" }),
        [0x2516] = new("Cooler Master", "Cooler Master MasterPlus+", new[] { "masterplus" }),
        [0x054C] = new("Sony", "", Array.Empty<string>()),
    };

    /// <summary>Похоже на беспроводное устройство (по названию из самого устройства).</summary>
    internal static bool LooksWireless(string product) =>
        Regex.IsMatch(product ?? "", @"wireless|dongle|receiver|2\.4|lightspeed|hyperspeed|slipstream|quantum|беспровод|dualsense|dualshock|wireless controller", RegexOptions.IgnoreCase);

    /// <summary>Пояснение для устройства, заряд которого Windows не сообщает.</summary>
    internal static string BrandNote(VendorApp v, bool installed, DeviceKind kind)
    {
        if (v.App.Length == 0)
            return kind == DeviceKind.Gamepad
                ? L.T("Заряд этого геймпада Windows не сообщает. Его показывает Steam, когда геймпад подключён к нему.")
                : L.T("Заряд этого устройства Windows не сообщает.");
        return installed
            ? L.F("Заряд этого устройства Windows не сообщает — смотрите в {0} (она у вас установлена).", v.App)
            : L.F("Заряд этого устройства Windows не сообщает — смотрите в {0}.", v.App);
    }

    private static List<DeviceCharge> BrandNotes(List<HidIface> hid, List<DeviceCharge> have, string installedApps, List<string> log)
    {
        var res = new List<DeviceCharge>();
        foreach (var model in hid.Where(d => VendorApps.ContainsKey(d.Vid)).GroupBy(d => (d.Vid, d.Pid)))
        {
            var v = VendorApps[model.Key.Vid];
            var all = model.ToList();
            // Logitech с коллекциями HID++ опрошен выше (там и запись о нём, если заряд не прочитан).
            if (model.Key.Vid == LogitechVid && hid.Any(x => x.Vid == LogitechVid && x.Pid == model.Key.Pid && x.UsagePage is 0xFF00 or 0xFF43)) continue;
            string product = all.Select(x => x.Product).FirstOrDefault(p => p.Length > 0) ?? "";
            var kind = KindFromCollections(all.Select(x => (x.UsagePage, x.Usage)), product);
            bool wireless = LooksWireless(product) || (v.Brand == "Sony" && kind == DeviceKind.Gamepad);
            log?.Add($"марка {v.Brand}: {model.Key.Vid:X4}:{model.Key.Pid:X4} «{product}» — {(wireless ? "похоже на беспроводное" : "похоже на проводное, пропущено")}");
            if (!wireless) continue;
            string key = $"hid:{model.Key.Vid:X4}:{model.Key.Pid:X4}";
            if (have.Any(h => h.Key == key && h.Known)) continue;
            bool installed = v.Marks.Any(m => installedApps.Contains(m, StringComparison.OrdinalIgnoreCase));
            res.Add(new DeviceCharge
            {
                Key = key, Kind = kind, Name = FriendlyName(kind, CleanModel(product), InputTuning.BrandName(model.Key.Vid, product) is { Length: > 0 } b && !b.Contains('/') ? b : v.Brand),
                Source = L.T("определена по VID/PID"), Note = BrandNote(v, installed, kind),
            });
        }
        return res;
    }

    /// <summary>Имена установленных программ и пакетов Microsoft Store (одной строкой, для поиска). Только чтение реестра.</summary>
    internal static string InstalledApps()
    {
        var sb = new StringBuilder();
        foreach (var (hive, path) in new[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        })
        {
            try
            {
                using var k = hive.OpenSubKey(path);
                if (k == null) continue;
                foreach (var name in k.GetSubKeyNames())
                {
                    try { using var s = k.OpenSubKey(name); sb.Append(s?.GetValue("DisplayName") as string).Append('\n'); } catch { }
                }
            }
            catch { }
        }
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (k != null) foreach (var name in k.GetSubKeyNames()) sb.Append(name).Append('\n');
        }
        catch { }
        return sb.ToString();
    }

    // ───────────────────────── имена ─────────────────────────

    /// <summary>Модель без общих слов в конце: «G435 Wireless Gaming Headset» → «G435 Wireless».</summary>
    internal static string CleanModel(string name)
    {
        string s = Regex.Replace((name ?? "").Trim(), @"\s+", " ");
        s = Regex.Replace(s, @"^(HP,? Inc\.?|Logitech,? Inc\.?)\s+", "", RegexOptions.IgnoreCase);
        for (int guard = 0; guard < 4; guard++)
        {
            string t = Regex.Replace(s, @"\s+(gaming|headset|headphones|mouse|keyboard|gamepad|controller)$", "", RegexOptions.IgnoreCase);
            if (t == s || t.Length < 2) break;
            s = t;
        }
        return s;
    }

    /// <summary>«Мышь Logitech G Pro X Superlight»: тип + марка (если её нет в названии) + модель.</summary>
    internal static string FriendlyName(DeviceKind kind, string model, string brand)
    {
        model = (model ?? "").Trim();
        brand = (brand ?? "").Trim();
        string title = KindTitle(kind);
        if (model.Length == 0) return brand.Length > 0 ? title + " " + brand : title;
        if (brand.Length > 0 && !model.Contains(brand.Split(' ')[0], StringComparison.OrdinalIgnoreCase)) model = brand + " " + model;
        if (model.StartsWith(title, StringComparison.CurrentCultureIgnoreCase)) return model;
        // По-английски тип после модели: «Logitech G435 Wireless headset».
        return L.En ? model + " " + KindWord(kind) : title + " " + model;
    }

    // ───────────────────────── самопроверка ─────────────────────────

    /// <summary>Разбор на подставных байтах (свойство Windows, HID Battery Strength, ответы HID++, XInput), тексты уведомлений и что видно на этом ПК.</summary>
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        var fails = new List<string>();
        void Check(string what, bool ok) { sb.AppendLine((ok ? "  ok  " : "  FAIL ") + what); if (!ok) fails.Add(what); }

        // 0. Протоколы марок (HyperX, Logitech G435 и др.)
        foreach (var (name, ok) in VendorBattery.Checks()) Check(name, ok);

        // 1. Свойство Windows
        Check("свойство Windows: байт 55 → 55 %", ParseWindowsBattery(DevpropTypeByte, new byte[] { 55 }, 1) == 55);
        Check("свойство Windows: 0xFF и не байт — не заряд", ParseWindowsBattery(DevpropTypeByte, new byte[] { 0xFF }, 1) == null && ParseWindowsBattery(DevpropTypeUint32, new byte[] { 55, 0, 0, 0 }, 4) == null);
        Check("класс Bluetooth 0x240404 — гарнитура", KindFromClassOfDevice(0x240404) == DeviceKind.Headset);
        Check("класс Bluetooth 0x002580 — мышь, 0x002540 — клавиатура, 0x002508 — геймпад",
              KindFromClassOfDevice(0x002580) == DeviceKind.Mouse && KindFromClassOfDevice(0x002540) == DeviceKind.Keyboard && KindFromClassOfDevice(0x002508) == DeviceKind.Gamepad);
        Check("по названию: WH-1000XM4 — гарнитура, Xbox Wireless Controller — геймпад, Logitech Wireless Mouse — мышь",
              KindFromName("WH-1000XM4") == DeviceKind.Headset && KindFromName("Xbox Wireless Controller") == DeviceKind.Gamepad && KindFromName("Logitech Wireless Mouse") == DeviceKind.Mouse);

        // 2. HID Battery Strength: подставной HIDP_VALUE_CAPS (страница 0x06, использование 0x20, отчёт 4, 0…100)
        var raw = new byte[ValueCapSize * 2];
        void Cap(int i, ushort page, byte id, ushort usage, int min, int max, bool range = false, ushort umax = 0)
        {
            int o = i * ValueCapSize;
            BitConverter.GetBytes(page).CopyTo(raw, o); raw[o + 2] = id; raw[o + 12] = (byte)(range ? 1 : 0);
            BitConverter.GetBytes(min).CopyTo(raw, o + 40); BitConverter.GetBytes(max).CopyTo(raw, o + 44);
            BitConverter.GetBytes(usage).CopyTo(raw, o + 56); BitConverter.GetBytes(range ? umax : (ushort)0).CopyTo(raw, o + 58);
        }
        Cap(0, 0x06, 4, 0x20, 0, 100);
        Cap(1, 0x85, 5, 0x60, 0, 255, range: true, umax: 0x66);
        var caps = ParseValueCaps(raw, 2, 2);
        Check("HID: разбор описания отчётов — Battery Strength в отчёте 4",
              caps.Any(c => c.UsagePage == 0x06 && c.Usage == 0x20 && c.ReportId == 4 && c.LogicalMax == 100 && IsBatteryUsage(c.UsagePage, c.Usage)));
        Check("HID: диапазон Battery System 0x60…0x66 раскрыт, 0x65 — заряд", caps.Count(c => c.UsagePage == 0x85) == 7 && caps.Any(c => c.Usage == 0x65 && IsBatteryUsage(0x85, 0x65)));
        Check("HID: 47 из 0…100 → 47 %, 128 из 0…255 → 50 %, вне границ — нет", ScaleBattery(47, 0, 100) == 47 && ScaleBattery(128, 0, 255) == 50 && ScaleBattery(300, 0, 255) == null);
        Check("HID: ключ подключения общий у коллекций",
              GroupKey(@"\\?\hid#vid_046d&pid_0acb&mi_03&col03#7&64d5b1&0&0002#{4d1e55b2-f16f-11cf-88cb-001111000030}")
              == GroupKey(@"\\?\HID#VID_046D&PID_0ACB&MI_03&Col04#7&64d5b1&0&0003#{4d1e55b2-f16f-11cf-88cb-001111000030}"));
        Check("HID: мышь+клавиатура — мышь, только громкость/телефония — гарнитура",
              KindFromCollections(new[] { ((ushort)1, (ushort)6), ((ushort)1, (ushort)2) }, "") == DeviceKind.Mouse
              && KindFromCollections(new[] { ((ushort)0x0C, (ushort)1), ((ushort)0x0B, (ushort)5) }, "") == DeviceKind.Headset);

        // 3. HID++ на подставных байтах
        var req = BuildRequest(true, 0x01, 0x00, 0x1, 0, 0, 0xAA);
        Check("HID++: запрос ping длинный: 11 01 00 1B 00 00 AA", req.Length == 20 && req[0] == 0x11 && req[1] == 1 && req[3] == 0x1B && req[6] == 0xAA);
        var pong = new byte[20]; pong[0] = 0x11; pong[1] = 0x01; pong[2] = 0x00; pong[3] = 0x1B; pong[4] = 4; pong[5] = 2; pong[6] = 0xAA;
        Check("HID++: ответ ping — протокол 4.2", MatchReply(pong, 20, 1, 0, 0x1B, out var pp, out _) == 1 && pp[0] == 4 && pp[1] == 2);
        var other = (byte[])pong.Clone(); other[3] = 0x1A;
        Check("HID++: ответ другой программе (другой номер) — не наш", MatchReply(other, 20, 1, 0, 0x1B, out _, out _) == 0);
        var e20 = new byte[20]; e20[0] = 0x11; e20[1] = 1; e20[2] = 0xFF; e20[3] = 0x05; e20[4] = 0x1B; e20[5] = 0x02;
        Check("HID++: ошибка 2.0 (код 2)", MatchReply(e20, 20, 1, 0x05, 0x1B, out _, out int ec) == 2 && ec == 2);
        var e10 = new byte[] { 0x10, 0xFF, 0x8F, 0x00, 0x1B, 0x01, 0x00 };
        Check("HID++: ошибка 1.0 у приёмника — значит, опрашивать места 1–6", MatchReply(e10, 7, 0xFF, 0x00, 0x1B, out _, out _) == 3);
        Check("HID++ 0x1004: 35 %, заряжается", ParseUnified(new byte[] { 35, 0x04, 1, 1 }, true) is (35, true, false));
        Check("HID++ 0x1004 без процентов: «мало» ≈ 20 %, разряжается", ParseUnified(new byte[] { 0, 0x02, 0, 0 }, false) is (20, false, true));
        Check("HID++ 0x1000: 55 %, разряжается", ParseStatus1000(new byte[] { 55, 30, 0 }) is (55, false));
        Check("HID++ 0x1000: неисправная батарея — заряда нет", ParseStatus1000(new byte[] { 0, 0, 5 }).Percent == null);
        var (vp, vc) = ParseVoltage1001(new byte[] { 0x0F, 0x1A, 0x80 });   // 3866 мВ, на зарядке
        Check($"HID++ 0x1001: 3866 мВ ≈ {vp} %, заряжается", vp is >= 58 and <= 63 && vc == true);
        Check("HID++ 0x1F20: нет измерения — заряда нет; 4100 мВ ≈ 93 %", ParseAdc1F20(new byte[] { 0x10, 0x04, 0x00 }).Percent == null && ParseAdc1F20(new byte[] { 0x10, 0x04, 0x01 }).Percent is >= 90 and <= 95);
        Check("напряжение: 4,2 В — 100 %, 3,5 В — 0 %", VoltageToPercent(4200) == 100 && VoltageToPercent(3500) == 0 && VoltageToPercent(3300) == 0);
        Check("HID++ тип: 3 — мышь, 8 — гарнитура, 0 — клавиатура, 12 — геймпад",
              KindFromLogiType(3) == DeviceKind.Mouse && KindFromLogiType(8) == DeviceKind.Headset && KindFromLogiType(0) == DeviceKind.Keyboard && KindFromLogiType(12) == DeviceKind.Gamepad);

        // 4. XInput
        Check("XInput: проводной и отключённый — без заряда; «мало» ≈ 20 %", XInputLevelPercent(1, 3) == null && XInputLevelPercent(0, 3) == null && XInputLevelPercent(2, 1) == 20);

        // 5. Имена и марки
        Check("имя: «G435 Wireless Gaming Headset» → «Гарнитура Logitech G435 Wireless»",
              FriendlyName(DeviceKind.Headset, CleanModel("G435 Wireless Gaming Headset"), "Logitech") == (L.En ? "Logitech G435 Wireless headset" : "Гарнитура Logitech G435 Wireless"));
        Check("имя: марка не дублируется — «Мышь HyperX Pulsefire Haste Wireless»",
              FriendlyName(DeviceKind.Mouse, CleanModel("HyperX Pulsefire Haste Wireless"), "HyperX") == (L.En ? "HyperX Pulsefire Haste Wireless mouse" : "Мышь HyperX Pulsefire Haste Wireless"));
        Check("HyperX с установленной NGENUITY — «смотрите в HyperX NGENUITY (она у вас установлена)»",
              BrandNote(VendorApps[0x03F0], true, DeviceKind.Mouse).Contains("HyperX NGENUITY") && BrandNote(VendorApps[0x03F0], true, DeviceKind.Mouse) != BrandNote(VendorApps[0x03F0], false, DeviceKind.Mouse));
        Check("беспроводное по названию: «Pulsefire Haste Wireless» — да, «Pulsefire Haste» — нет", LooksWireless("HyperX Pulsefire Haste Wireless") && !LooksWireless("HyperX Pulsefire Haste"));
        HidIface Fake(int vid, int pid, ushort page, ushort usage, string product) => new() { Vid = vid, Pid = pid, UsagePage = page, Usage = usage, Product = product, Group = $"{vid:X4}{pid:X4}" };
        var fakeHid = new List<HidIface>
        {
            Fake(0x046D, 0x0ACB, 0x0B, 1, "G435 Wireless Gaming Headset"), Fake(0x046D, 0x0ACB, 0x0C, 1, "G435 Wireless Gaming Headset"), Fake(0x046D, 0x0ACB, 0xFFC3, 1, "G435 Wireless Gaming Headset"),
            Fake(0x046D, 0xC547, 0xFF00, 2, "USB Receiver"),
            Fake(0x03F0, 0x028E, 1, 2, "HyperX Pulsefire Haste Wireless"), Fake(0x03F0, 0x028E, 1, 6, "HyperX Pulsefire Haste Wireless"),
            Fake(0x0951, 0x16E5, 1, 6, "HyperX Alloy Origins"),
            Fake(0x1532, 0x00B6, 1, 2, "Razer DeathAdder V3 Pro"),
        };
        var notes = BrandNotes(fakeHid, new List<DeviceCharge>(), "33C30B79.HyperXNGenuity_5.39.0.0_x64__0a78dr3hq0pvt\n", null);
        Check("марки: Logitech без HID++ (G435) — гарнитура, «смотрите в Logitech G HUB»",
              notes.Any(d => d.Kind == DeviceKind.Headset && d.Name.Contains("G435") && d.Note == BrandNote(VendorApps[LogitechVid], false, DeviceKind.Headset)));
        Check("марки: приёмник Logitech с HID++ здесь не повторяется, проводная клавиатура HyperX — тоже",
              !notes.Any(d => d.Key.Contains("C547")) && !notes.Any(d => d.Name.Contains("Alloy")));
        Check("марки: мышь HyperX — NGENUITY установлена; Razer — Razer Synapse (не установлена)",
              notes.Any(d => d.Kind == DeviceKind.Mouse && d.Note == BrandNote(VendorApps[0x03F0], true, DeviceKind.Mouse))
              && notes.Count(d => d.Name.Contains("DeathAdder")) == 0);

        // 6. Уведомления и оверлей
        var list = new List<DeviceCharge>
        {
            new() { Key = "a", Name = "Гарнитура X", Kind = DeviceKind.Headset, Percent = 35 },
            new() { Key = "b", Name = "Мышь Y", Kind = DeviceKind.Mouse, Percent = 12 },
            new() { Key = "c", Name = "Клавиатура Z", Kind = DeviceKind.Keyboard },
        };
        var n = BeforeGame(list);
        // Ожидаемый текст — теми же переводами и тем же форматом процентов (по-английски «12%», по-русски «12 %»).
        string Pct(int v) => PercentText(new DeviceCharge { Percent = v });
        string expect = L.F("Заряд: {0}", L.T("мышь") + " " + Pct(12) + ", " + L.T("гарнитура") + " " + Pct(35)) + L.F(" — подзарядите {0}", L.T("мышь"));
        Check($"перед игрой: «{n?.Text}» — предупреждение", n != null && n.Value.Warning && n.Value.Text == expect);
        var calm = BeforeGame(new[] { new DeviceCharge { Key = "a", Kind = DeviceKind.Headset, Percent = 80 } });
        Check("перед игрой: всё заряжено — обычное уведомление", calm != null && !calm.Value.Warning);
        Check("перед игрой: заряд не известен ни у одного — уведомления нет", BeforeGame(new[] { list[2] }) == null && BeforeGame(Array.Empty<DeviceCharge>()) == null);
        var charging = BeforeGame(new[] { new DeviceCharge { Key = "m", Kind = DeviceKind.Mouse, Percent = 8, Charging = true } });
        Check("перед игрой: 8 %, но заряжается — не предупреждение", charging != null && !charging.Value.Warning);
        var notified = new HashSet<string>();
        var crit = new List<DeviceCharge> { new() { Key = "m", Name = L.T("Мышь"), Kind = DeviceKind.Mouse, Percent = 10 }, new() { Key = "h", Name = "H", Kind = DeviceKind.Headset, Percent = 11 } };
        var a1 = CriticalAlerts(crit, notified);
        var a2 = CriticalAlerts(crit, notified);
        Check($"в игре: «{a1.FirstOrDefault().Text}», второй раз за игру — нет, 11 % — нет", a1.Count == 1 && a1[0].Key == "m" && a2.Count == 0
              && a1[0].Text == L.F("{0}: {1} — скоро выключится", L.T("Мышь"), Pct(10)));
        var ov = PickForOverlay(Sample().Concat(new[] { new DeviceCharge { Key = "k", Kind = DeviceKind.Keyboard, Percent = 90, Name = "K" } }));
        Check("оверлей: не больше трёх, самые низкие первыми, без неизвестных", ov.Length == 3 && ov[0].Percent == 12 && ov.All(d => d.Known) && ov.All(d => d.Percent != 90));
        Check("красным — 15 % и ниже, но не на зарядке", IsLow(new DeviceCharge { Percent = 15 }) && !IsLow(new DeviceCharge { Percent = 16 }) && !IsLow(new DeviceCharge { Percent = 5, Charging = true }));
        Check("значки есть у всех типов", Enum.GetValues<DeviceKind>().All(k => Icon(k) != null));
        Check("адрес Bluetooth LE из пути HID; у USB — нет",
              MacFromHidPath(@"\\?\hid#{00001812-0000-1000-8000-00805f9b34fb}_dev_vid&02046d_pid&b023_rev&0007_d4e5f6a1b2c3&col02#a&2b3c&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}") == "D4E5F6A1B2C3"
              && MacFromHidPath(@"\\?\hid#vid_046d&pid_c547&mi_02&col01#7&64d5b1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}") == "");
        var dups = MergeDuplicates(new List<DeviceCharge>
        {
            new() { Key = "hid:046D:C547", Kind = DeviceKind.Mouse, Percent = 70, Hw = "046D:C547" },
            new() { Key = "logi:g1:1", Kind = DeviceKind.Mouse, Percent = 71, Charging = false, Hw = "046D:C547" },
            new() { Key = "logi:g1:2", Kind = DeviceKind.Keyboard, Percent = 40, Hw = "046D:C547" },
            new() { Key = "win:D4E5F6A1B2C3", Kind = DeviceKind.Mouse, Percent = 55, Mac = "D4E5F6A1B2C3" },
            new() { Key = "logi:g2:255", Kind = DeviceKind.Mouse, Percent = 55, Hw = "046D:B023", Mac = "D4E5F6A1B2C3" },
            new() { Key = "hid:03F0:028E", Kind = DeviceKind.Mouse, Percent = 30, Hw = "03F0:028E" },
        });
        Check("одно устройство из двух источников — одна запись (HID++ важнее HID и Windows); мышь и клавиатура на одном приёмнике — обе",
              dups.Count == 4 && dups.Any(d => d.Key == "logi:g1:1") && dups.Any(d => d.Key == "logi:g1:2") && dups.Any(d => d.Key == "logi:g2:255")
              && dups.Any(d => d.Key == "hid:03F0:028E") && !dups.Any(d => d.Key is "hid:046D:C547" or "win:D4E5F6A1B2C3"));

        // 7. Что видно на этом ПК (только чтение; Logitech — стандартные запросы чтения HID++)
        sb.AppendLine("Что видно на этом ПК (только чтение):");
        var log = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<DeviceCharge> here;
        var quiet = AudioQuiet.IsQuiet("G435");
        if (quiet != null) log.Add("звук в гарнитуре G435: " + (quiet == true ? "тишина" : "идёт звук"));
        try { here = Scan(log); }
        catch (Exception ex) { here = new(); log.Add("ошибка опроса: " + ex.Message); }
        sw.Stop();
        foreach (var line in log) sb.AppendLine("  " + line);
        sb.AppendLine($"  опрос занял {sw.ElapsedMilliseconds} мс");
        if (here.Count == 0) sb.AppendLine("  устройств с зарядом и известных беспроводных устройств нет");
        foreach (var d in Sorted(here))
            sb.AppendLine($"  → {d.Name}: {(d.Known ? PercentText(d) + (d.Charging == true ? ", заряжается" : "") : "заряд не прочитан")} · {d.Source}{(d.Note.Length > 0 ? " · " + d.Note : "")}");
        var lgHub = System.Diagnostics.Process.GetProcessesByName("lghub").Length + System.Diagnostics.Process.GetProcessesByName("lghub_agent").Length;
        sb.AppendLine($"  Logitech G HUB запущен: {(lgHub > 0 ? "да" : "нет")}");

        return (fails.Count == 0 ? "OK" : "FAIL: " + string.Join("; ", fails)) + Environment.NewLine + sb;
    }

    // ───────────────────────── Windows API ─────────────────────────

    private const uint DigcfPresent = 0x2, DigcfAllClasses = 0x4, DigcfDeviceInterface = 0x10;
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, FileShareReadWrite = 0x3, OpenExisting = 3, FileFlagOverlapped = 0x40000000;
    private const int ErrorIoPending = 997, HidpStatusSuccess = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid; public uint pid;
        public DEVPROPKEY(Guid g, uint p) { fmtid = g; pid = p; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputBattery { public byte BatteryType, BatteryLevel; }

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", SetLastError = true)]
    private static extern bool SetupDiGetDeviceProperty(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key, out uint type, byte[] buffer, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, int size, out int required);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr data, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA iface);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA iface, IntPtr detail, uint size, out uint required, IntPtr info);

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] private static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buffer, uint length);
    [DllImport("hid.dll")] private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);
    [DllImport("hid.dll")] private static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] buffer, int length);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
    [DllImport("hid.dll")] private static extern int HidP_GetValueCaps(int type, byte[] caps, ref ushort length, IntPtr pp);
    [DllImport("hid.dll")] private static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value, IntPtr pp, byte[] report, uint length);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadFile(SafeFileHandle h, IntPtr buffer, int length, IntPtr read, IntPtr ovl);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(SafeFileHandle h, IntPtr buffer, int length, IntPtr written, IntPtr ovl);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr ovl, out int bytes, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelIoEx(SafeFileHandle h, IntPtr ovl);
    [DllImport("kernel32.dll", EntryPoint = "CreateEventW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr security, bool manualReset, bool initialState, string name);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] private static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool all, uint ms);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    [DllImport("xinput1_4.dll")] private static extern int XInputGetBatteryInformation(int user, byte devType, out XInputBattery info);
}
