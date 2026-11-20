using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using PulsePC.Panels;

namespace PulsePC.Services;

/// <summary>Один подключённый экран: что про него честно читается из Windows.</summary>
public sealed class ScreenInfo
{
    /// <summary>Ключ экрана для настроек и копии «как было»: код производителя и модели по EDID и номер разъёма.</summary>
    public string Key = "";
    /// <summary>Имя источника для EnumDisplaySettings / ChangeDisplaySettingsEx («\\.\DISPLAY1»).</summary>
    public string Gdi = "";
    /// <summary>Порядковый номер экрана в списке, с единицы.</summary>
    public int Number;
    /// <summary>Модель из EDID («Odyssey G52A»); пусто — монитор её не сообщает.</summary>
    public string Model = "";
    /// <summary>Производитель по трёхбуквенному коду EDID («Samsung»); пусто — код неизвестен.</summary>
    public string Maker = "";
    public string EdidCode = "";
    public int ProductCode;
    public bool Primary, Internal;
    /// <summary>Разъём: DisplayPort, HDMI, DVI…; пусто — Windows не сообщает.</summary>
    public string Connection = "";
    /// <summary>Разрешение рабочего стола сейчас.</summary>
    public int Width, Height;
    /// <summary>Точная частота сейчас, Гц (например 164,96). NaN — не прочитана.</summary>
    public double Hz = double.NaN;
    /// <summary>Частоты, которые драйвер разрешает при текущем разрешении, по возрастанию (целые, без чересстрочных).</summary>
    public int[] Rates = Array.Empty<int>();
    /// <summary>Наибольшая частота при родном разрешении (если оно другое).</summary>
    public int NativeMaxHz;
    /// <summary>Родное (предпочтительное) разрешение экрана. 0 — не прочитано.</summary>
    public int NativeWidth, NativeHeight;
    /// <summary>Бит на канал цвета; 0 — не прочитано.</summary>
    public int Bits;
    /// <summary>Кодирование цвета: RGB, YCbCr 4:4:4 и т. п.; пусто — не прочитано.</summary>
    public string Encoding = "";
    /// <summary>Читается ли HDR вообще, поддерживает ли его экран, включён ли.</summary>
    public bool HdrKnown, HdrSupported, HdrOn;

    public int MaxHz => Rates.Length > 0 ? Rates[^1] : 0;
    public string Title => (Maker.Length > 0 && !Model.StartsWith(Maker, StringComparison.OrdinalIgnoreCase) ? Maker + " " : "") + (Model.Length > 0 ? Model : Internal ? L.T("Встроенный экран") : L.T("Монитор"));
    public bool NativeKnown => NativeWidth > 0 && NativeHeight > 0;
    public bool AtNative => !NativeKnown || (NativeWidth == Width && NativeHeight == Height);
}

/// <summary>Переменная частота обновления: что удаётся прочитать без догадок.</summary>
public sealed class VrrInfo
{
    /// <summary>«Оптимизации для оконных игр» (SwapEffectUpgradeEnable): true/false, null — не задано, решает Windows.</summary>
    public bool? WindowedOptimizations;
    /// <summary>«Переменная частота обновления» Windows (VRROptimizeEnable): true/false, null — не задано.</summary>
    public bool? WindowsVrr;
    /// <summary>Строка из реестра как есть (для самопроверки).</summary>
    public string Raw = "";
    /// <summary>Есть ли драйвер NVIDIA с настройками (DRS).</summary>
    public bool Nvidia;
    /// <summary>Режим G-SYNC в общем профиле драйвера: 0 — выключен, 1 — только полноэкранный, 2 — полноэкранный и оконный; -1 — не задан (заводское значение); -2 — не прочитан.</summary>
    public int GsyncMode = -2;
    /// <summary>Имя настройки, которое вернул драйвер (по нему проверяется, что номер настройки верный).</summary>
    public string GsyncName = "";
}

/// <summary>Отложенное подтверждение новой частоты: если картинки нет, через несколько секунд вернётся прежняя.</summary>
public sealed class DisplayPending
{
    public string Key = "", Title = "";
    public int FromHz, ToHz, Width, Height;
    public DateTime Deadline;
    /// <summary>Подставное ожидание для снимка раздела: откат ничего не меняет.</summary>
    internal bool Fake;
}

/// <summary>
/// «Монитор»: на какой частоте работает каждый экран и сколько он умеет, разрешение, глубина цвета, HDR,
/// переменная частота (оптимизации оконных игр, VRR Windows, режим G-SYNC в драйвере NVIDIA).
/// Меняет только частоту — по кнопке, с правами администратора, с подтверждением и копией «как было».
/// Следит при запуске программы, при смене режима экрана и в начале игры: если частота ниже максимальной —
/// уведомление у часов, не чаще раза в сутки на экран.
/// </summary>
public static class DisplayCheck
{
    public const string WatchKey = "display.watch";
    private const string WarnedPrefix = "display.warned.";
    /// <summary>Сколько секунд ждать подтверждения новой частоты, прежде чем вернуть прежнюю.</summary>
    public const int ConfirmSeconds = 15;


    private static PanelHost host;
    private static bool inited;
    private static readonly object Gate = new();
    private static Timer watchTimer, confirmTimer;
    private static DisplayPending pending;
    private static string lastAction = "";
    private static DateTime lastActionAt;

    /// <summary>Что-то изменилось (новая частота, подтверждение, откат). Вызывается из фонового потока.</summary>
    public static event Action Changed;

    private const string BackupName = "display-backup.txt", PendingName = "display-pending.txt";
    private static bool migrated;

    /// <summary>
    /// Копия «как было» — в защищённой папке: по ней программа с правами администратора меняет режим экрана.
    /// Старая копия из папки пользователя переносится один раз (содержимое всё равно проверяется при чтении).
    /// </summary>
    public static string BackupFile
    {
        get
        {
            lock (Gate) { if (!migrated) { SecureStore.Migrate(BackupName); migrated = true; } }
            return SecureStore.PathOf(BackupName);
        }
    }

    /// <summary>
    /// Отметка «частота сменена, подтверждения ещё нет»: «ключ|ширина|высота|прежняя|новая». Новая частота включается без записи
    /// в реестр и записывается только по «Оставить»; если программа упала или её закрыли до ответа, при следующем запуске
    /// по отметке возвращается режим из реестра — то есть прежний.
    /// </summary>
    private static string PendingFile => SecureStore.PathOf(PendingName);

    // ================================================================== запуск и слежение
    public static void Init(PanelHost h)
    {
        lock (Gate)
        {
            host = h;
            if (inited) return;
            inited = true;
        }
        watchTimer = new Timer(_ => Watch(), null, Timeout.Infinite, Timeout.Infinite);
        // Снимок раздела (--render-panel) ничего в системе не меняет — откат делает только сама программа.
        if (!Environment.GetCommandLineArgs().Contains("--render-panel")) Task.Run(RollBackUnconfirmed);
        try { SystemEvents.DisplaySettingsChanged += (_, _) => { if (!GameHooks.InGame) Schedule(4000); }; } catch { }
        GameHooks.Started += (_, _) => Schedule(8000);
        Schedule(20000);   // при запуске программы — когда окно уже открылось и всё прочее прочитано
    }

    /// <summary>
    /// Прошлый запуск сменил частоту и не дождался «Оставить» (сбой, завершение процесса, выключение): вернуть режим,
    /// записанный в реестре, — новая частота туда не попадала. После перезагрузки Windows уже сама стоит на нём.
    /// </summary>
    private static void RollBackUnconfirmed()
    {
        try
        {
            var p = LoadPendingMark(PendingFile);
            if (p == null) { ClearPendingMark(); return; }
            var s = ReadScreens().FirstOrDefault(x => x.Key == p.Value.Key);
            if (s != null && !Applied(s.Hz, p.Value.FromHz))
            {
                int code = ResetToRegistry(s.Gdi);
                Log.Info($"Монитор: частота {p.Value.ToHz} Гц не была подтверждена до закрытия программы — режим из реестра возвращён (код {code})");
                Done(code == 0 ? L.F("«{0}»: {1}\u00A0Гц не было подтверждено до закрытия программы — вернул прежнюю частоту.", s.Title, p.Value.ToHz)
                               : L.F("Не удалось вернуть прежнюю частоту «{0}»: {1}. Она вернётся после перезагрузки.", s.Title, Explain(code)));
            }
            ClearPendingMark();
        }
        catch (Exception ex) { Log.Error("монитор: откат неподтверждённой частоты", ex); }
    }

    private static void Schedule(int ms)
    {
        try { watchTimer?.Change(ms, Timeout.Infinite); } catch { }
    }

    /// <summary>Пора ли предупреждать об этом экране сегодня. Чистая функция: ключ дня последнего предупреждения и сегодняшний.</summary>
    internal static bool ShouldWarn(string lastDay, DateTime today) => lastDay != today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static void Watch()
    {
        var h = host;
        if (h == null || !h.Settings.GetBool(WatchKey, true)) return;
        if (Pending != null) return;
        List<ScreenInfo> screens;
        try { screens = ReadScreens(); } catch { return; }
        foreach (var s in screens)
        {
            var (low, target) = Judge(s.Hz, s.Rates);
            if (!low) continue;
            string key = WarnedPrefix + s.Key;
            if (!ShouldWarn(h.Settings.Get(key), DateTime.Today)) continue;
            h.Settings.Set(key, DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            SaveSettings(h);
            bool game = GameHooks.InGame;
            string title = game ? L.F("В игре монитор работает на {0}\u00A0Гц", HzText(s.Hz)) : L.F("Монитор работает на {0}\u00A0Гц", HzText(s.Hz));
            string text = game
                ? L.F("«{0}» умеет {1}\u00A0Гц. Если в настройках игры есть частота обновления, выберите там {1}\u00A0Гц; иначе включите её в разделе «Монитор».", s.Title, target)
                : L.F("«{0}» умеет {1}\u00A0Гц. Включить можно в разделе «Монитор».", s.Title, target);
            h.Ui(() => h.Notify(title, text, true, () => h.Open("sys:Монитор")));
        }
    }

    private static void SaveSettings(PanelHost h)
    {
        var values = h.Settings.Values;
        if (values != null) lock (values) h.Settings.Save(); else h.Settings.Save();
    }

    // ================================================================== решения (чистые функции)
    /// <summary>
    /// Частота ниже возможной? hz — точная частота сейчас, rates — частоты при этом разрешении.
    /// 164,96 при максимуме 165 и 59,94 при 60 — это одна и та же частота, а не «ниже».
    /// </summary>
    public static (bool Low, int Target) Judge(double hz, IReadOnlyList<int> rates)
    {
        if (double.IsNaN(hz) || rates == null || rates.Count == 0) return (false, 0);
        int max = rates.Max();
        return hz + 1.5 < max ? (true, max) : (false, max);
    }

    /// <summary>Частота применилась: прочитанная отличается от заданной не больше чем на 1 Гц.</summary>
    internal static bool Applied(double read, int target) => !double.IsNaN(read) && Math.Abs(read - target) <= 1.0;

    /// <summary>Частоты одного разрешения из списка режимов драйвера: без чересстрочных и служебных «0» и «1», по возрастанию.</summary>
    internal static int[] RatesFor(IEnumerable<(int W, int H, int Hz, bool Interlaced)> modes, int w, int h) =>
        modes.Where(m => m.W == w && m.H == h && !m.Interlaced && m.Hz > 1).Select(m => m.Hz).Distinct().OrderBy(x => x).ToArray();

    /// <summary>«164,96», «60», «59,94» — частота без лишних нулей.</summary>
    public static string HzText(double hz) => double.IsNaN(hz) ? "—" : Math.Round(hz, 2).ToString("0.##", L.Culture);

    /// <summary>Трёхбуквенный код производителя из EDID (два байта, старший первым; Windows отдаёт их переставленными).</summary>
    internal static string EdidMaker(ushort raw)
    {
        static string Decode(int v)
        {
            char a = (char)('A' - 1 + ((v >> 10) & 31)), b = (char)('A' - 1 + ((v >> 5) & 31)), c = (char)('A' - 1 + (v & 31));
            return a is >= 'A' and <= 'Z' && b is >= 'A' and <= 'Z' && c is >= 'A' and <= 'Z' ? $"{a}{b}{c}" : "";
        }
        string swapped = Decode(((raw & 0xFF) << 8) | (raw >> 8));
        return swapped.Length > 0 ? swapped : Decode(raw);
    }

    internal static ushort EdidEncode(string code)
    {
        int v = ((code[0] - 'A' + 1) << 10) | ((code[1] - 'A' + 1) << 5) | (code[2] - 'A' + 1);
        return (ushort)(((v & 0xFF) << 8) | (v >> 8));
    }

    /// <summary>Распространённые коды производителей мониторов (PNP ID). Неизвестный код — производитель не называется.</summary>
    private static readonly Dictionary<string, string> Makers = new()
    {
        ["SAM"] = "Samsung", ["SEC"] = "Samsung", ["DEL"] = "Dell", ["GSM"] = "LG", ["LGD"] = "LG", ["ACR"] = "Acer", ["ACI"] = "ASUS", ["AUS"] = "ASUS",
        ["BNQ"] = "BenQ", ["AOC"] = "AOC", ["PHL"] = "Philips", ["HWP"] = "HP", ["HPN"] = "HP", ["LEN"] = "Lenovo", ["MSI"] = "MSI", ["VSC"] = "ViewSonic",
        ["GBT"] = "Gigabyte", ["SNY"] = "Sony", ["IVM"] = "iiyama", ["NEC"] = "NEC", ["ENC"] = "EIZO", ["XMI"] = "Xiaomi", ["HKC"] = "HKC",
        ["AUO"] = "AU Optronics", ["BOE"] = "BOE", ["CMN"] = "Innolux", ["SHP"] = "Sharp", ["APP"] = "Apple", ["MEI"] = "Panasonic", ["TCL"] = "TCL",
        ["HSI"] = "Hisense", ["ALI"] = "Alienware", ["KTC"] = "KTC", ["PNP"] = "",
    };

    internal static string MakerName(string code) => code != null && Makers.TryGetValue(code, out var name) ? name : "";

    /// <summary>Разбор строки DirectXUserGlobalSettings: «VRROptimizeEnable=1;SwapEffectUpgradeEnable=1;».</summary>
    internal static (bool? Windowed, bool? Vrr) ParseDirectX(string raw)
    {
        bool? windowed = null, vrr = null;
        foreach (var part in (raw ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            bool? v = kv[1].Trim() switch { "1" => true, "0" => false, _ => null };
            if (kv[0].Trim().Equals("SwapEffectUpgradeEnable", StringComparison.OrdinalIgnoreCase)) windowed = v;
            else if (kv[0].Trim().Equals("VRROptimizeEnable", StringComparison.OrdinalIgnoreCase)) vrr = v;
        }
        return (windowed, vrr);
    }

    private static string Technology(uint t) => t switch
    {
        0 => "VGA",
        4 => "DVI",
        5 => "HDMI",
        6 or 11 or 13 or 0x80000000 => L.T("встроенный"),
        10 => "DisplayPort",
        12 => "UDI",
        15 => L.T("беспроводной (Miracast)"),
        16 or 17 => "",
        18 => "USB-C (DisplayPort)",
        _ => "",
    };

    private static string ColorEncoding(int e) => e switch { 0 => "RGB", 1 => "YCbCr 4:4:4", 2 => "YCbCr 4:2:2", 3 => "YCbCr 4:2:0", _ => "" };

    // ================================================================== чтение
    /// <summary>Все активные экраны. Только чтение.</summary>
    public static List<ScreenInfo> ReadScreens()
    {
        var list = new List<ScreenInfo>();
        if (!Query(out var paths, out var modes)) return list;
        var seen = new HashSet<string>();
        foreach (var p in paths)
        {
            var s = new ScreenInfo();
            uint techRaw = p.Target.Tech;
            s.Internal = techRaw is 6 or 11 or 13 or 0x80000000;
            s.Connection = Technology(techRaw);

            // Имя монитора и коды EDID
            var name = TargetName(p.Target.Adapter, p.Target.Id);
            s.Model = name.Friendly;
            s.EdidCode = name.EdidValid ? EdidMaker(name.Maker) : "";
            s.ProductCode = name.EdidValid ? name.Product : 0;
            s.Maker = MakerName(s.EdidCode);
            s.Gdi = SourceName(p.Source.Adapter, p.Source.Id);
            s.Key = (s.EdidCode.Length > 0 ? $"{s.EdidCode}{s.ProductCode:X4}" : $"T{p.Target.Id:X}") + "-" + name.Connector;
            if (!seen.Add(s.Key + s.Gdi)) continue;

            // Разрешение и положение — из режима источника, точная частота — из режима сигнала
            if (p.Source.ModeIdx < modes.Length && modes[p.Source.ModeIdx].InfoType == 1)
            {
                var m = modes[p.Source.ModeIdx];
                s.Width = (int)m.SrcWidth; s.Height = (int)m.SrcHeight;
                s.Primary = m.SrcX == 0 && m.SrcY == 0;
            }
            if (p.Target.ModeIdx < modes.Length && modes[p.Target.ModeIdx].InfoType == 2 && modes[p.Target.ModeIdx].VSyncDen != 0)
                s.Hz = modes[p.Target.ModeIdx].VSyncNum / (double)modes[p.Target.ModeIdx].VSyncDen;
            else if (p.Target.RefreshDen != 0)
                s.Hz = p.Target.RefreshNum / (double)p.Target.RefreshDen;

            // Родное разрешение — предпочтительный режим экрана
            var pref = Preferred(p.Target.Adapter, p.Target.Id);
            s.NativeWidth = pref.W; s.NativeHeight = pref.H;

            // HDR и глубина цвета
            ReadColor(p.Target.Adapter, p.Target.Id, s);

            // Частоты из списка режимов драйвера
            var all = Modes(s.Gdi);
            if (s.Width == 0 && Current(s.Gdi, out var cur)) { s.Width = (int)cur.dmPelsWidth; s.Height = (int)cur.dmPelsHeight; }
            s.Rates = RatesFor(all, s.Width, s.Height);
            if (s.NativeKnown && !s.AtNative)
            {
                var native = RatesFor(all, s.NativeWidth, s.NativeHeight);
                s.NativeMaxHz = native.Length > 0 ? native[^1] : 0;
            }
            list.Add(s);
        }
        list = list.OrderByDescending(x => x.Primary).ThenBy(x => x.Gdi, StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = 0; i < list.Count; i++) list[i].Number = i + 1;
        TestOverride(list);
        return list;
    }

    /// <summary>
    /// Только для снимков раздела: PULSEPC_TEST_DISPLAY=60 показывает частоту первого экрана как 60 Гц (в системе ничего не меняется),
    /// «60;pending» — ещё и ожидание подтверждения. Без переменной ничего не делает.
    /// </summary>
    private static void TestOverride(List<ScreenInfo> list)
    {
        string v = Environment.GetEnvironmentVariable("PULSEPC_TEST_DISPLAY");
        if (string.IsNullOrEmpty(v) || list.Count == 0) return;
        var parts = v.Split(';');
        if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double hz)) list[0].Hz = hz;
        if (parts.Contains("pending"))
            lock (Gate)
                pending ??= new DisplayPending
                {
                    Key = list[0].Key, Title = list[0].Title, FromHz = 60, ToHz = list[0].MaxHz, Width = list[0].Width, Height = list[0].Height,
                    Deadline = DateTime.Now.AddSeconds(ConfirmSeconds), Fake = true,
                };
    }

    /// <summary>Переменная частота: реестр DirectX текущего пользователя и режим G-SYNC в общем профиле драйвера NVIDIA. Только чтение.</summary>
    public static VrrInfo ReadVrr()
    {
        var v = new VrrInfo();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            v.Raw = key?.GetValue("DirectXUserGlobalSettings") as string ?? "";
            (v.WindowedOptimizations, v.WindowsVrr) = ParseDirectX(v.Raw);
        }
        catch { }
        try { (v.Nvidia, v.GsyncMode, v.GsyncName) = NvGsync.Read(); } catch { }
        return v;
    }

    /// <summary>Понятное описание режима G-SYNC из драйвера.</summary>
    public static string GsyncText(int mode) => mode switch
    {
        0 => "выключен",
        1 => "включён для полноэкранного режима",
        2 => "включён для полноэкранного и оконного режимов",
        -1 => "не задан в общем профиле — действует заводское значение драйвера",
        _ => "не прочитан",
    };

    // ================================================================== изменение
    public static DisplayPending Pending { get { lock (Gate) return pending; } }
    public static (string Text, DateTime At) LastAction { get { lock (Gate) return (lastAction, lastActionAt); } }

    /// <summary>Есть ли копия «как было» для этого экрана.</summary>
    public static bool HasBackup(string key) => LoadBackup(BackupFile).ContainsKey(key);

    /// <summary>
    /// Включить частоту hz на экране. Прежний режим запоминается в файл (первую копию не затирает). После смены частота
    /// перечитывается; если она применилась, ждём подтверждения ConfirmSeconds секунд, иначе — вернём прежнюю сами.
    /// Только с правами администратора. Вызывать не из потока интерфейса.
    /// </summary>
    public static (bool Ok, string Message) Apply(string screenKey, int hz)
    {
        var h = host;
        if (h == null || !h.Admin) return (false, L.T("Менять частоту можно только с правами администратора."));
        var s = ReadScreens().FirstOrDefault(x => x.Key == screenKey);
        if (s == null) return (false, L.T("Экран не найден: возможно, его отключили. Откройте раздел ещё раз."));
        if (!s.Rates.Contains(hz)) return (false, L.F("При разрешении {0}×{1} драйвер не предлагает {2}\u00A0Гц.", s.Width, s.Height, hz));
        if (!Current(s.Gdi, out var was)) return (false, L.T("Windows не отдала текущий режим экрана."));

        var backup = LoadBackup(BackupFile);
        if (!backup.ContainsKey(s.Key))
        {
            backup[s.Key] = ((int)was.dmPelsWidth, (int)was.dmPelsHeight, (int)was.dmDisplayFrequency);
            SaveBackup(BackupFile, backup);
        }

        int code = SetMode(s.Gdi, (int)was.dmPelsWidth, (int)was.dmPelsHeight, hz, test: true);
        if (code != 0) return (false, L.F("Драйвер не принял {0}\u00A0Гц ({1}). Частота не менялась.", hz, Explain(code)));
        // Сначала отметка «ждём подтверждения», потом смена: при сбое между ними следующий запуск вернёт режим из реестра.
        try { SavePendingMark(PendingFile, s.Key, (int)was.dmPelsWidth, (int)was.dmPelsHeight, (int)was.dmDisplayFrequency, hz); }
        catch (Exception ex) { return (false, L.F("Не удалось записать отметку для отката, поэтому частота не менялась: {0}", ex.Message)); }
        // Без записи в реестр: до «Оставить» новая частота живёт только в текущем сеансе.
        code = SetMode(s.Gdi, (int)was.dmPelsWidth, (int)was.dmPelsHeight, hz, test: false, persist: false);
        if (code != 0) { ClearPendingMark(); return (false, L.F("Не удалось включить {0}\u00A0Гц: {1}.", hz, Explain(code))); }

        Thread.Sleep(1500);
        var now = ReadScreens().FirstOrDefault(x => x.Key == screenKey);
        double read = now?.Hz ?? double.NaN;
        if (!Applied(read, hz))
        {
            ResetToRegistry(s.Gdi);
            ClearPendingMark();
            Done(L.F("Windows приняла {0}\u00A0Гц, но экран работает на {1}\u00A0Гц. Вернул прежнюю частоту.", hz, HzText(read)));
            return (false, LastAction.Text);
        }

        lock (Gate)
        {
            pending = new DisplayPending
            {
                Key = s.Key, Title = s.Title, FromHz = (int)was.dmDisplayFrequency, ToHz = hz,
                Width = (int)was.dmPelsWidth, Height = (int)was.dmPelsHeight, Deadline = DateTime.Now.AddSeconds(ConfirmSeconds),
            };
            confirmTimer?.Dispose();
            confirmTimer = new Timer(_ => ConfirmExpired(), null, ConfirmSeconds * 1000, Timeout.Infinite);
        }
        Changed?.Invoke();
        return (true, L.F("Экран работает на {0}\u00A0Гц. Если картинка в порядке, нажмите «Оставить».", HzText(read)));
    }

    /// <summary>Пользователь видит картинку: новая частота остаётся — только теперь она записывается в реестр.</summary>
    public static void Confirm()
    {
        DisplayPending p;
        lock (Gate) { p = pending; pending = null; confirmTimer?.Dispose(); confirmTimer = null; }
        if (p == null) return;
        if (p.Fake) { Done(L.F("«{0}»: оставлено {1}\u00A0Гц.", p.Title, p.ToHz)); return; }
        var s = ReadScreens().FirstOrDefault(x => x.Key == p.Key);
        int code = s == null ? -1 : SetMode(s.Gdi, p.Width, p.Height, p.ToHz, test: false, persist: true);
        ClearPendingMark();
        Done(code == 0 ? L.F("«{0}»: оставлено {1}\u00A0Гц.", p.Title, p.ToHz)
                       : L.F("«{0}»: {1}\u00A0Гц работает сейчас, но Windows не записала его ({2}) — после перезагрузки вернётся {3}\u00A0Гц.", p.Title, p.ToHz, Explain(code), p.FromHz));
    }

    /// <summary>Отказ от новой частоты до истечения времени (кнопка «Вернуть»).</summary>
    public static void Reject() => RevertPending(L.T("Вернул прежнюю частоту по вашей кнопке."));

    private static void ConfirmExpired() => RevertPending(L.T("Подтверждения не было — вернул прежнюю частоту."));

    private static void RevertPending(string why)
    {
        DisplayPending p;
        lock (Gate) { p = pending; pending = null; confirmTimer?.Dispose(); confirmTimer = null; }
        if (p == null) return;
        if (p.Fake) { Done(L.T("Проверка: подставное ожидание снято, в системе ничего не менялось.")); return; }
        var s = ReadScreens().FirstOrDefault(x => x.Key == p.Key);
        // Новая частота в реестр не записывалась: возвращаем режим из реестра, то есть прежний.
        int code = s == null ? -1 : ResetToRegistry(s.Gdi);
        if (code == 0 && s != null && !Applied(ReadScreens().FirstOrDefault(x => x.Key == p.Key)?.Hz ?? double.NaN, p.FromHz))
            code = SetMode(s.Gdi, p.Width, p.Height, p.FromHz, test: false, persist: false);
        if (code == 0) ClearPendingMark();
        Done(code == 0 ? L.F("«{0}»: {1}\u00A0Гц. {2}", p.Title, p.FromHz, why) : L.F("Не удалось вернуть {0}\u00A0Гц: {1}. Верните частоту кнопкой «Вернуть как было».", p.FromHz, Explain(code)));
    }

    /// <summary>Вернуть режим из копии «как было» и удалить копию этого экрана. Только с правами администратора.</summary>
    public static (bool Ok, string Message) Restore(string screenKey)
    {
        var h = host;
        if (h == null || !h.Admin) return (false, L.T("Менять частоту можно только с правами администратора."));
        lock (Gate) { if (pending?.Key == screenKey) { pending = null; confirmTimer?.Dispose(); confirmTimer = null; } }
        var backup = LoadBackup(BackupFile);
        if (!backup.TryGetValue(screenKey, out var was)) return (false, L.T("Копии «как было» нет: программа частоту этого экрана не меняла."));
        var s = ReadScreens().FirstOrDefault(x => x.Key == screenKey);
        if (s == null) return (false, L.T("Экран не найден: возможно, его отключили."));
        int code = SetMode(s.Gdi, was.W, was.H, was.Hz, test: false, persist: true);
        // Отметка «ждём подтверждения» снимается, только если она про этот экран: отметку другого экрана, который ещё
        // ждёт «Оставить», стирать нельзя — без неё после сбоя его неподтверждённая частота не вернулась бы.
        if (code == 0 && LoadPendingMark(PendingFile)?.Key == screenKey) ClearPendingMark();
        if (code != 0) return (false, L.F("Не удалось вернуть {0}\u00A0Гц: {1}. Копия сохранена.", was.Hz, Explain(code)));
        backup.Remove(screenKey);
        SaveBackup(BackupFile, backup);
        Done(L.F("«{0}»: вернул {1}×{2}, {3}\u00A0Гц — как было до изменений.", s.Title, was.W, was.H, was.Hz));
        return (true, LastAction.Text);
    }

    private static void Done(string text)
    {
        lock (Gate) { lastAction = text; lastActionAt = DateTime.Now; }
        Changed?.Invoke();
    }

    private static string Explain(int code) => code switch
    {
        1 => L.T("нужна перезагрузка"),
        -1 => L.T("драйвер отказал"),
        -2 => L.T("такого режима нет"),
        -3 => L.T("Windows не записала настройку"),
        -4 => L.T("неверные параметры"),
        -5 => L.T("неверные параметры"),
        -6 => L.T("настройку блокирует политика"),
        _ => L.F("код {0}", code),
    };

    // ---------- копия «как было»: «ключ|ширина|высота|частота» в строке ----------

    /// <summary>Режим из копии: ключ без «|» и управляющих символов, разрешение и частота в разумных пределах.</summary>
    internal static bool ValidMode(string key, int w, int h, int hz) =>
        !string.IsNullOrEmpty(key) && key.Length <= 128 && !key.Any(c => char.IsControl(c) || c == '|')
        && w is > 0 and <= 16384 && h is > 0 and <= 16384 && hz is > 1 and <= 1000;

    internal static Dictionary<string, (int W, int H, int Hz)> LoadBackup(string file)
    {
        var map = new Dictionary<string, (int, int, int)>();
        try
        {
            string text = AtomicFile.Read(file, t => t);
            foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var p = line.Split('|');
                if (p.Length == 4 && int.TryParse(p[1], out int w) && int.TryParse(p[2], out int h) && int.TryParse(p[3], out int hz) && ValidMode(p[0], w, h, hz))
                    map[p[0]] = (w, h, hz);
            }
        }
        catch { }
        return map;
    }

    internal static void SaveBackup(string file, Dictionary<string, (int W, int H, int Hz)> map)
    {
        if (map.Count == 0) { foreach (var f in new[] { file, file + ".bak" }) { try { File.Delete(f); } catch { } } return; }
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        AtomicFile.WriteAllText(file, string.Join("\n", map.Select(kv => $"{kv.Key}|{kv.Value.W}|{kv.Value.H}|{kv.Value.Hz}")) + "\n");
    }

    internal static void SavePendingMark(string file, string key, int w, int h, int fromHz, int toHz)
    {
        if (!ValidMode(key, w, h, fromHz) || !ValidMode(key, w, h, toHz)) throw new InvalidOperationException(L.T("неверный режим экрана"));
        AtomicFile.WriteAllText(file, $"{key}|{w}|{h}|{fromHz}|{toHz}\n");
    }

    internal static (string Key, int W, int H, int FromHz, int ToHz)? LoadPendingMark(string file)
    {
        string text = AtomicFile.Read(file, t => t)?.Trim();
        var p = (text ?? "").Split('|');
        if (p.Length == 5 && int.TryParse(p[1], out int w) && int.TryParse(p[2], out int h) && int.TryParse(p[3], out int from)
            && int.TryParse(p[4], out int to) && ValidMode(p[0], w, h, from) && ValidMode(p[0], w, h, to))
            return (p[0], w, h, from, to);
        return null;
    }

    private static void ClearPendingMark()
    {
        string file = PendingFile;
        foreach (var f in new[] { file, file + ".bak" }) { try { File.Delete(f); } catch { } }
    }

    // ================================================================== самопроверка
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        string fail = null;
        void Check(string what, bool ok) { sb.AppendLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) fail ??= what; }

        int[] g = { 60, 75, 100, 120, 144, 165 };
        Check("60 Гц при максимуме 165 — ниже, включать 165", Judge(60, g) == (true, 165));
        Check("164,96 Гц при максимуме 165 — это и есть 165", !Judge(164.956, g).Low);
        Check("59,94 Гц при максимуме 60 — это 60", !Judge(59.94, new[] { 59, 60 }).Low);
        Check("143,98 Гц при максимуме 144 — норма", !Judge(143.98, new[] { 60, 120, 144 }).Low);
        Check("119,88 Гц при максимуме 165 — ниже", Judge(119.88, g) == (true, 165));
        Check("монитор умеет только 60 — предупреждать не о чем", !Judge(60, new[] { 60 }).Low);
        Check("частота не прочитана — молчать", !Judge(double.NaN, g).Low && !Judge(60, Array.Empty<int>()).Low);

        var modes = new List<(int, int, int, bool)>
        {
            (2560, 1440, 60, false), (2560, 1440, 165, false), (2560, 1440, 144, false), (2560, 1440, 144, false),
            (1920, 1080, 240, false), (2560, 1440, 1, false), (2560, 1440, 120, true), (2560, 1440, 100, false),
        };
        Check("частоты разрешения: без чересстрочных, без «1», без повторов, по возрастанию",
            RatesFor(modes, 2560, 1440).SequenceEqual(new[] { 60, 100, 144, 165 }));
        Check("другое разрешение не подмешивается", RatesFor(modes, 1920, 1080).SequenceEqual(new[] { 240 }));
        Check("проверка применения: 164,96 при цели 165 — применилось, 60 — нет", Applied(164.96, 165) && !Applied(60, 165) && !Applied(double.NaN, 165));
        Check("подпись частоты: 164,96 / 60 / 59,94", HzText(164.956) == L.Pick("164,96", "164.96") && HzText(60.0001) == "60" && HzText(59.94) == L.Pick("59,94", "59.94"));

        Check("код производителя EDID: SAM ↔ Samsung", EdidMaker(EdidEncode("SAM")) == "SAM" && MakerName("SAM") == "Samsung" && MakerName("ZZZ") == "");
        var dx = ParseDirectX("VRROptimizeEnable=0;SwapEffectUpgradeEnable=1;");
        Check("реестр DirectX: оконные оптимизации вкл., VRR выкл.", dx.Windowed == true && dx.Vrr == false);
        Check("реестр DirectX: пусто — не задано", ParseDirectX("").Windowed == null && ParseDirectX(null).Vrr == null);

        Check("уведомление: сегодня ещё не было — показать, было — нет",
            ShouldWarn("", new DateTime(2026, 11, 19)) && ShouldWarn("2026-11-18", new DateTime(2026, 11, 19)) && !ShouldWarn("2026-11-19", new DateTime(2026, 11, 19)));

        string tmp = Path.Combine(Path.GetTempPath(), "pulsepc-display-test-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            SaveBackup(tmp, new Dictionary<string, (int, int, int)> { ["SAM7259-0"] = (2560, 1440, 60) });
            var back = LoadBackup(tmp);
            Check("копия «как было»: запись и чтение", back.Count == 1 && back["SAM7259-0"] == (2560, 1440, 60));
            back.Remove("SAM7259-0");
            SaveBackup(tmp, back);
            Check("копия «как было»: пустая удаляется", !File.Exists(tmp));
            File.WriteAllText(tmp, "X|2560|1440|60\nY|0|1440|60\nZ|2560|1440|99999\n");
            Check("копия «как было»: неверные размеры и частоты отбрасываются", LoadBackup(tmp).Keys.SequenceEqual(new[] { "X" }));
            File.Delete(tmp);
            SavePendingMark(tmp, "SAM7259-0", 2560, 1440, 60, 165);
            Check("отметка «ждём подтверждения»: запись и чтение", LoadPendingMark(tmp) == ("SAM7259-0", 2560, 1440, 60, 165));
            File.WriteAllText(tmp, "SAM7259-0|2560|1440|60|abc");
            Check("испорченная отметка не читается", LoadPendingMark(tmp) == null);
        }
        finally { foreach (var f in new[] { tmp, tmp + ".bak", tmp + ".tmp" }) { try { File.Delete(f); } catch { } } }

        sb.AppendLine();
        sb.AppendLine("Что видно на этом ПК (только чтение):");
        var screens = ReadScreens();
        if (screens.Count == 0) { sb.AppendLine("  экраны не прочитаны"); fail ??= "QueryDisplayConfig не вернул экранов"; }
        foreach (var s in screens)
        {
            var (low, target) = Judge(s.Hz, s.Rates);
            sb.AppendLine($"  Экран {s.Number}{(s.Primary ? " (основной)" : "")}: {s.Title}; EDID {s.EdidCode} 0x{s.ProductCode:X4}; ключ {s.Key}; {s.Gdi}; разъём {(s.Connection.Length > 0 ? s.Connection : "не сообщается")}");
            sb.AppendLine($"    сейчас {s.Width}×{s.Height}, {HzText(s.Hz)} Гц (точно {s.Hz:0.#####}); частоты при этом разрешении: {string.Join(", ", s.Rates)}");
            sb.AppendLine($"    родное разрешение: {(s.NativeKnown ? $"{s.NativeWidth}×{s.NativeHeight}" : "не прочитано")}{(s.NativeMaxHz > 0 ? $", при нём до {s.NativeMaxHz} Гц" : "")}");
            sb.AppendLine($"    цвет: {(s.Bits > 0 ? s.Bits + " бит на канал" : "глубина не прочитана")}{(s.Encoding.Length > 0 ? ", " + s.Encoding : "")}; HDR: {(!s.HdrKnown ? "не читается" : !s.HdrSupported ? "не поддерживается" : s.HdrOn ? "включён" : "поддерживается, выключен")}");
            sb.AppendLine($"    вывод: {(low ? $"ниже возможной, можно {target} Гц" : "частота максимальная для этого разрешения")}; копия «как было»: {(HasBackup(s.Key) ? "есть" : "нет")}");
        }
        var vrr = ReadVrr();
        sb.AppendLine($"  DirectXUserGlobalSettings: «{vrr.Raw}» → оконные оптимизации {(vrr.WindowedOptimizations is bool w ? (w ? "вкл." : "выкл.") : "не заданы")}, VRR Windows {(vrr.WindowsVrr is bool r ? (r ? "вкл." : "выкл.") : "не задана")}");
        sb.AppendLine($"  G-SYNC в драйвере: {(vrr.Nvidia ? GsyncText(vrr.GsyncMode) + (vrr.GsyncName.Length > 0 ? $" (настройка «{vrr.GsyncName}»)" : "") : "драйвера NVIDIA нет")}");
        sb.AppendLine($"  Слежение: {(host?.Settings.GetBool(WatchKey, true) ?? true ? "включено" : "выключено")}; копия «как было»: {(File.Exists(BackupFile) ? BackupFile : "нет")}");
        return (fail == null ? "OK" : "FAIL: " + fail) + "\n" + sb;
    }

    // ================================================================== Windows
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathSource { public Luid Adapter; public uint Id; public uint ModeIdx; public uint Status; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathTarget
    {
        public Luid Adapter; public uint Id; public uint ModeIdx; public uint Tech; public uint Rotation; public uint Scaling;
        public uint RefreshNum, RefreshDen; public uint ScanLine; public int Available; public uint Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo { public PathSource Source; public PathTarget Target; public uint Flags; }

    /// <summary>DISPLAYCONFIG_MODE_INFO: 64 байта; объединение режимов начинается со смещения 16.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct ModeInfo
    {
        [FieldOffset(0)] public uint InfoType;          // 1 — источник, 2 — сигнал, 3 — изображение рабочего стола
        [FieldOffset(4)] public uint Id;
        [FieldOffset(8)] public Luid Adapter;
        // DISPLAYCONFIG_TARGET_MODE.targetVideoSignalInfo
        [FieldOffset(16)] public ulong PixelRate;
        [FieldOffset(32)] public uint VSyncNum;
        [FieldOffset(36)] public uint VSyncDen;
        // DISPLAYCONFIG_SOURCE_MODE
        [FieldOffset(16)] public uint SrcWidth;
        [FieldOffset(20)] public uint SrcHeight;
        [FieldOffset(24)] public uint SrcFormat;
        [FieldOffset(28)] public int SrcX;
        [FieldOffset(32)] public int SrcY;
    }

    private const uint QdcOnlyActivePaths = 2;

    private static bool Query(out PathInfo[] paths, out ModeInfo[] modes)
    {
        paths = Array.Empty<PathInfo>(); modes = Array.Empty<ModeInfo>();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint np, out uint nm) != 0) return false;
            paths = new PathInfo[np]; modes = new ModeInfo[nm];
            int r = QueryDisplayConfig(QdcOnlyActivePaths, ref np, paths, ref nm, modes, IntPtr.Zero);
            if (r == 122) continue;                         // ERROR_INSUFFICIENT_BUFFER: экраны поменялись между вызовами
            if (r != 0) return false;
            Array.Resize(ref paths, (int)np); Array.Resize(ref modes, (int)nm);
            return true;
        }
        return false;
    }

    /// <summary>Запрос DisplayConfigGetDeviceInfo в буфере нужного размера; заголовок: тип, размер, адаптер, номер.</summary>
    private static IntPtr Info(uint type, int size, Luid adapter, uint id)
    {
        IntPtr buf = Marshal.AllocHGlobal(size);
        Marshal.Copy(new byte[size], 0, buf, size);
        Marshal.WriteInt32(buf, 0, (int)type);
        Marshal.WriteInt32(buf, 4, size);
        Marshal.WriteInt32(buf, 8, (int)adapter.Low);
        Marshal.WriteInt32(buf, 12, adapter.High);
        Marshal.WriteInt32(buf, 16, (int)id);
        if (DisplayConfigGetDeviceInfo(buf) == 0) return buf;
        Marshal.FreeHGlobal(buf);
        return IntPtr.Zero;
    }

    private static (string Friendly, ushort Maker, ushort Product, bool EdidValid, uint Connector) TargetName(Luid adapter, uint id)
    {
        // DISPLAYCONFIG_TARGET_DEVICE_NAME: заголовок 20, flags 4, outputTechnology 4, edidManufactureId 2, edidProductCodeId 2,
        // connectorInstance 4, monitorFriendlyDeviceName[64], monitorDevicePath[128]
        IntPtr buf = Info(2, 420, adapter, id);
        if (buf == IntPtr.Zero) return ("", 0, 0, false, 0);
        try
        {
            uint flags = (uint)Marshal.ReadInt32(buf, 20);
            ushort maker = (ushort)Marshal.ReadInt16(buf, 28), product = (ushort)Marshal.ReadInt16(buf, 30);
            uint connector = (uint)Marshal.ReadInt32(buf, 32);
            string friendly = (flags & 1) != 0 ? (Marshal.PtrToStringUni(buf + 36) ?? "").Trim() : "";
            return (friendly, maker, product, (flags & 4) != 0, connector);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static string SourceName(Luid adapter, uint id)
    {
        IntPtr buf = Info(1, 84, adapter, id);       // DISPLAYCONFIG_SOURCE_DEVICE_NAME: заголовок 20 + viewGdiDeviceName[32]
        if (buf == IntPtr.Zero) return "";
        try { return Marshal.PtrToStringUni(buf + 20) ?? ""; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static (int W, int H) Preferred(Luid adapter, uint id)
    {
        IntPtr buf = Info(3, 80, adapter, id);       // DISPLAYCONFIG_TARGET_PREFERRED_MODE: заголовок 20, width 4, height 4, targetMode с 32
        if (buf == IntPtr.Zero) return (0, 0);
        try { return (Marshal.ReadInt32(buf, 20), Marshal.ReadInt32(buf, 24)); }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static void ReadColor(Luid adapter, uint id, ScreenInfo s)
    {
        // Windows 11 24H2 и новее: DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 — отдельно HDR и «широкий цвет».
        IntPtr buf = Info(15, 36, adapter, id);
        if (buf != IntPtr.Zero)
        {
            try
            {
                uint v = (uint)Marshal.ReadInt32(buf, 20);
                s.HdrKnown = true;
                s.HdrSupported = (v & 0x10) != 0;                        // highDynamicRangeSupported
                s.HdrOn = Marshal.ReadInt32(buf, 32) == 2;               // activeColorMode: 2 — HDR
                s.Encoding = ColorEncoding(Marshal.ReadInt32(buf, 24));
                s.Bits = Marshal.ReadInt32(buf, 28);
                return;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        // Раньше: DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO — «расширенный цвет», под которым почти всегда HDR.
        buf = Info(9, 32, adapter, id);
        if (buf == IntPtr.Zero) return;
        try
        {
            uint v = (uint)Marshal.ReadInt32(buf, 20);
            s.HdrKnown = true;
            s.HdrSupported = (v & 1) != 0;
            s.HdrOn = (v & 2) != 0 && (v & 4) == 0;                      // включён и это не принудительный «широкий цвет»
            s.Encoding = ColorEncoding(Marshal.ReadInt32(buf, 24));
            s.Bits = Marshal.ReadInt32(buf, 28);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    private const int EnumCurrentSettings = -1;
    private const uint DmPelsWidth = 0x80000, DmPelsHeight = 0x100000, DmDisplayFrequency = 0x400000;
    private const uint CdsUpdateRegistry = 0x1, CdsTest = 0x2;

    private static DevMode NewMode() => new() { dmSize = (ushort)Marshal.SizeOf<DevMode>() };

    private static bool Current(string gdi, out DevMode mode)
    {
        mode = NewMode();
        return gdi.Length > 0 && EnumDisplaySettings(gdi, EnumCurrentSettings, ref mode);
    }

    private static List<(int W, int H, int Hz, bool Interlaced)> Modes(string gdi)
    {
        var list = new List<(int, int, int, bool)>();
        if (gdi.Length == 0) return list;
        var m = NewMode();
        for (int i = 0; i < 4000 && EnumDisplaySettings(gdi, i, ref m); i++)
        {
            list.Add(((int)m.dmPelsWidth, (int)m.dmPelsHeight, (int)m.dmDisplayFrequency, (m.dmDisplayFlags & 2) != 0));
            m = NewMode();
        }
        return list;
    }

    /// <summary>
    /// Сменить режим экрана: разрешение остаётся прежним, меняется частота. test — только проверить, примет ли драйвер;
    /// persist — записать режим в реестр (иначе он действует до перезагрузки или до возврата режима из реестра).
    /// </summary>
    private static int SetMode(string gdi, int w, int h, int hz, bool test, bool persist = true)
    {
        if (!Current(gdi, out var m)) return -1;
        m.dmPelsWidth = (uint)w; m.dmPelsHeight = (uint)h; m.dmDisplayFrequency = (uint)hz;
        m.dmFields = DmPelsWidth | DmPelsHeight | DmDisplayFrequency;
        return ChangeDisplaySettingsEx(gdi, ref m, IntPtr.Zero, test ? CdsTest : persist ? CdsUpdateRegistry : 0, IntPtr.Zero);
    }

    /// <summary>Вернуть экрану режим, записанный в реестре (отменить смену без записи в реестр).</summary>
    private static int ResetToRegistry(string gdi) =>
        gdi.Length == 0 ? -1 : ChangeDisplaySettingsExReset(gdi, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, [Out] ModeInfo[] modes, IntPtr topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(IntPtr packet);
    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DevMode devMode);
    [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsEx(string device, ref DevMode devMode, IntPtr hwnd, uint flags, IntPtr param);
    [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsExReset(string device, IntPtr devMode, IntPtr hwnd, uint flags, IntPtr param);

    // ================================================================== G-SYNC (только чтение настроек драйвера NVIDIA)
    /// <summary>
    /// Режим G-SYNC из общего профиля драйвера (DRS, настройка VRR_MODE 0x1194F158). Номер проверяется по имени, которое
    /// возвращает сам драйвер: если оно не про G-SYNC, значение не показывается.
    /// </summary>
    private static class NvGsync
    {
        private const uint VrrMode = 0x1194F158;
        private const int NvOk = 0, NvSettingNotFound = -160;
        private const int StringBytes = 4096, SettingSize = 12320, SettingCurrent = 8220;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr QueryInterface(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Plain();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OutHandle(out IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OnSession(IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetProfile(IntPtr session, out IntPtr profile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetSetting(IntPtr session, IntPtr profile, uint id, IntPtr setting);

        public static (bool Nvidia, int Mode, string Name) Read()
        {
            string dll = Path.Combine(Environment.SystemDirectory, "nvapi64.dll");
            if (!Environment.Is64BitProcess || !File.Exists(dll)) return (false, -2, "");
            if (!NativeLibrary.TryLoad(dll, out IntPtr lib) || !NativeLibrary.TryGetExport(lib, "nvapi_QueryInterface", out IntPtr entry)) return (false, -2, "");
            var query = Marshal.GetDelegateForFunctionPointer<QueryInterface>(entry);
            T Fn<T>(uint id) where T : Delegate { IntPtr p = query(id); return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p); }

            var init = Fn<Plain>(0x0150E828);                   // NvAPI_Initialize
            if (init == null || init() != NvOk) return (false, -2, "");
            var create = Fn<OutHandle>(0x0694D52E);             // NvAPI_DRS_CreateSession
            var destroy = Fn<OnSession>(0xDAD9CFF8);            // NvAPI_DRS_DestroySession
            var load = Fn<OnSession>(0x375DBD6B);               // NvAPI_DRS_LoadSettings
            var baseProfile = Fn<GetProfile>(0xDA8466A0);       // NvAPI_DRS_GetBaseProfile
            var get = Fn<GetSetting>(0x73BF8338);               // NvAPI_DRS_GetSetting
            if (create == null || destroy == null || load == null || baseProfile == null || get == null) return (true, -2, "");

            if (create(out IntPtr session) != NvOk) return (true, -2, "");
            IntPtr s = Marshal.AllocHGlobal(SettingSize);
            try
            {
                if (load(session) != NvOk || baseProfile(session, out IntPtr profile) != NvOk) return (true, -2, "");
                Marshal.Copy(new byte[SettingSize], 0, s, SettingSize);
                Marshal.WriteInt32(s, 0, SettingSize | (1 << 16));          // NVDRS_SETTING_VER1
                int st = get(session, profile, VrrMode, s);
                if (st == NvSettingNotFound) return (true, -1, "");
                if (st != NvOk) return (true, -2, "");
                string name = Marshal.PtrToStringUni(s + 4) ?? "";
                uint value = (uint)Marshal.ReadInt32(s, SettingCurrent);
                bool trusted = name.Contains("SYNC", StringComparison.OrdinalIgnoreCase) || name.Contains("VRR", StringComparison.OrdinalIgnoreCase)
                               || name.Contains("variable refresh", StringComparison.OrdinalIgnoreCase);
                return trusted && value <= 2 ? (true, (int)value, name) : (true, -2, name);
            }
            finally
            {
                Marshal.FreeHGlobal(s);
                try { destroy(session); } catch { }
            }
        }
    }
}
