using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using PulsePC.Panels;

namespace PulsePC.Services;

/// <summary>Одна настройка профиля драйвера: что сейчас, что предлагается и почему.</summary>
public class NvRow : Notifier
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>Что делает настройка и зачем — одной фразой.</summary>
    public string Why { get; set; } = "";
    public string NowText { get; set; } = "";
    public string ToText { get; set; } = "";
    /// <summary>Почему менять не предлагается.</summary>
    public string Remark { get; set; } = "";
    /// <summary>Что записать в профиль: номер настройки драйвера и значение. Пусто — менять нечего.</summary>
    internal List<(uint Id, uint Value)> Writes = new();

    public bool CanChange => Writes.Count > 0;
    public string Line => CanChange ? $"{NowText}  →  {ToText}" : NowText + (Remark == "" ? "" : "  ·  " + Remark);

    private bool selected;
    public bool Selected { get => selected; set => Set(ref selected, value); }
}

/// <summary>Общие настройки драйвера или профиль одной игры.</summary>
public class NvTarget
{
    /// <summary>Название игры или «Все игры (общие настройки)» — по нему профиль ищется после перечитывания и называется в драйвере; не переводится.</summary>
    public string Title { get; set; } = "";
    public string TitleText => L.T(Title);
    public string Caption { get; set; } = "";
    public bool Global;
    /// <summary>Имя запускаемого файла игры: по нему драйвер узнаёт игру.</summary>
    public string Exe = "", ExePath = "";
    public string ProfileName = "";
    public bool HasProfile;
    /// <summary>Есть копия исходных значений: программа уже меняла этот профиль.</summary>
    public bool HasBackup;
    public string Note = "";
    public List<NvRow> Rows = new();
    internal string Key => Global ? "base" : "app:" + Exe.ToLowerInvariant();
    public int Changes => Rows.Count(r => r.CanChange);
}

public sealed class NvAdvice
{
    /// <summary>Заголовок по-русски: с ним сверяется самопроверка; на экране — TitleText.</summary>
    public string Title { get; set; } = "";
    public string TitleText => L.T(Title);
    public string Text { get; set; } = "";
    /// <summary>Что открыть кнопкой (программа, ярлык или адрес приложения); пусто — кнопки нет.</summary>
    public string Launch { get; set; } = "";
    public string LaunchText { get; set; } = "";
    public bool HasLaunch => Launch != "";
}

public sealed class NvState
{
    /// <summary>Драйвер NVIDIA отвечает, профили читаются.</summary>
    public bool Available;
    /// <summary>Почему раздел недоступен — одной фразой. Для AMD и Intel — по-русски (с ним сверяется самопроверка); на экране — ReasonText.</summary>
    public string Reason = "";
    public string ReasonText => L.T(Reason);
    /// <summary>Советы общего вида для видеокарт AMD и Intel.</summary>
    public List<NvAdvice> Advice = new();
    /// <summary>Производитель главной видеокарты: NVIDIA, AMD, Intel или пусто.</summary>
    public string Vendor = "";
    public List<NvTarget> Targets = new();
}

/// <summary>
/// Профили драйвера NVIDIA — то же, что «Параметры 3D» в панели управления NVIDIA: общие настройки и настройки по играм.
/// Работает через официальный NVAPI (nvapi64.dll, DRS). Это настройки на уровне драйвера: файлы игр и сами игры не затрагиваются.
/// Перед записью исходные значения сохраняются в файл; «Вернуть как было» восстанавливает именно их.
///
/// Номера функций — NVIDIA/nvapi, nvapi_interface.h; номера настроек и значения — NvApiDriverSettings.h;
/// раскладка структур — nvapi.h (сверено с NvapiDrsWrapper.cs из nvidiaProfileInspector).
/// Режим низкой задержки в заголовках NVIDIA не описан: номера двух его настроек взяты из CustomSettingNames.xml того же nvidiaProfileInspector.
/// Открытые функции NvAPI_DRS_GetSetting/SetSetting эти две настройки не видят (отвечают «настройка не найдена»), поэтому только для них
/// используются расширенные варианты тех же функций — их номера взяты из NvapiDrsWrapper.cs (nvidiaProfileInspector). Если в драйвере их нет,
/// режим читается и пишется одной открытой настройкой — длиной очереди кадров.
/// </summary>
public static class NvidiaProfiles
{
    // --- настройки драйвера (NvApiDriverSettings.h)
    private const uint PrerenderLimit = 0x007BA09E;     // PRERENDERLIMIT_ID: очередь кадров, 0 — решает приложение
    private const uint FrameLimit = 0x10835002;         // FRL_FPS_ID: 0 — выключен, иначе кадров в секунду
    private const uint PowerMode = 0x1057EB71;          // PREFERRED_PSTATE_ID
    private const uint VSync = 0x00A879CF;              // VSYNCMODE_ID
    private const uint TextureQuality = 0x00CE2691;     // QUALITY_ENHANCEMENTS_ID
    private const uint ShaderCacheSize = 0x00AC8497;    // PS_SHADERDISKCACHE_MAX_SIZE_ID: мегабайты, 0xFFFFFFFF — без ограничения
    // --- режим низкой задержки (nvidiaProfileInspector, CustomSettingNames.xml)
    private const uint LowLatencyState = 0x0005F543;    // «Ultra Low Latency - CPL State»: 0 выкл, 1 вкл, 2 ультра
    private const uint LowLatencyUltra = 0x10835000;    // «Ultra Low Latency - Enabled»: 1 только для «ультра»
    /// <summary>Настройка есть в драйвере, но не в открытом списке: доступна только через расширенные функции.</summary>
    private static bool IsHidden(uint id) => id == LowLatencyState || id == LowLatencyUltra;

    private const uint PowerMax = 1, PowerNormal = 5;                                   // PREFERRED_PSTATE_PREFER_MAX, _OPTIMAL_POWER
    private const uint VsApp = 0x60925292, VsOff = 0x08416747, VsOn = 0x47814940, VsFast = 0x18888888;
    private const uint VsHalf = 0x32610244, VsThird = 0x71271021, VsQuarter = 0x13245256;
    private const uint TexHighQuality = 0xFFFFFFF6, TexQuality = 0, TexPerformance = 0x0A, TexHighPerformance = 0x14;
    private const uint CacheUnlimited = 0xFFFFFFFF, CacheTenGb = 0x2800;

    // --- коды ответа NVAPI (nvapi_lite_common.h)
    private const int NvOk = 0, NvSettingNotFound = -160, NvInvalidUserPrivilege = -137, NvAccessDenied = -175;

    private static readonly object Gate = new();
    private static PanelHost host;

    /// <summary>
    /// Где лежит копия исходных значений. По умолчанию — в защищённой папке (по копии программа с правами администратора
    /// пишет в профили драйвера); для проверки можно указать временный файл.
    /// </summary>
    public static string BackupFile { get; set; }
    private const string BackupName = "nvidia-profiles-backup.txt";
    private static bool migrated;
    private static string BackupPath
    {
        get
        {
            if (BackupFile != null) return BackupFile;
            if (!migrated) { SecureStore.Migrate(BackupName); migrated = true; }
            return SecureStore.PathOf(BackupName);
        }
    }

    /// <summary>Настройки, которые программа меняет, — только их можно вернуть по копии.</summary>
    private static readonly HashSet<uint> KnownIds = new() { PrerenderLimit, FrameLimit, PowerMode, VSync, TextureQuality, ShaderCacheSize, LowLatencyState, LowLatencyUltra };

    public static void Init(PanelHost h) => host = h;      // профиль меняется только по кнопке и насовсем: убирать после сбоя нечего

    // ================================================================== чтение
    /// <summary>Общие настройки и профили установленных игр с рекомендациями. Только чтение; вызывать в фоне.</summary>
    public static NvState Scan(IReadOnlyList<GameSession> sessions)
    {
        var state = new NvState();
        List<GameEntry> games;
        try { games = GameLibrary.Load().GroupBy(g => g.Title, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList(); }
        catch { games = new List<GameEntry>(); }

        lock (Gate)
        {
            if (!Nv.Ready(out string why)) { Unavailable(state, why); return state; }
            using var drs = Drs.Open(out int st);
            if (drs == null) { Unavailable(state, L.F("Драйвер NVIDIA не открыл свои настройки ({0}). Переустановите драйвер и откройте раздел снова.", Nv.Error(st))); return state; }

            var ctx = Context.Make(sessions);
            state.Available = true;
            state.Targets.Add(BuildTarget(drs, ctx, "Все игры (общие настройки)", null, true));
            foreach (var g in games)
            {
                try
                {
                    string exe = FindGameExe(drs, g.InstallDir, g.Title);
                    state.Targets.Add(BuildTarget(drs, ctx, g.Title, exe, false));
                }
                catch { }
            }
        }
        return state;
    }

    /// <summary>Профиль по имени запускаемого файла (для проверки на выдуманном файле). Только чтение.</summary>
    public static NvTarget TargetForExe(string title, string exe)
    {
        lock (Gate)
        {
            if (!Nv.Ready(out _)) return null;
            using var drs = Drs.Open(out _);
            return drs == null ? null : BuildTarget(drs, Context.Make(host?.Sessions() ?? Array.Empty<GameSession>()), title, exe, false);
        }
    }

    private static void Unavailable(NvState state, string why)
    {
        GpuDriverInfo gpu = null;
        try { gpu = GpuDriver.ReadAdapters().FirstOrDefault(); } catch { }
        int cap = GameTuning.FrameCap(), hz = GameTuning.Refresh();
        string app = "";
        try { app = GpuVendors.FindControlApp(gpu?.Vendor ?? ""); } catch { }
        bool battery = false;
        try { battery = LaptopCheck.Applies; } catch { }   // общий признак ноутбука
        Advise(state, gpu?.Vendor ?? "", gpu?.Name ?? "", gpu?.Integrated == true, gpu?.Basic == true, why, cap, hz, app, battery);
    }

    /// <summary>
    /// Что показать вместо профилей NVIDIA. Чистая функция: проверяется на выдуманных видеокартах (SelfTest.GpuVendorChecks).
    /// В драйверы AMD и Intel программа не пишет — только подсказывает, что поменять в приложении производителя, и открывает его.
    /// </summary>
    public static void Advise(NvState state, string vendor, string name, bool integrated, bool basic, string why, int cap, int hz, string app, bool battery)
    {
        state.Vendor = vendor;
        string limit = hz > 60 ? L.F("{0} FPS (монитор {1} Гц)", cap, hz) : L.T("под частоту монитора");
        string appName = GpuVendors.ControlApp(vendor);
        // Причина остаётся по-русски (с ней сверяется самопроверка) и переводится при показе: NvState.ReasonText.
        state.Reason = vendor == "NVIDIA" ? why
            : name == "" ? "Видеокарта с драйвером не найдена — настраивать нечего."
            : basic ? $"Драйвер для {name} не установлен: настройки по играм появятся вместе с ним."
            : vendor == "AMD" ? "Видеокарта AMD: настройки по играм задаются в AMD Software"
            : vendor == "Intel" ? "Графика Intel: настройки по играм задаются в Intel Graphics Software"
            : $"Для {name} программа даёт только общие советы";
        if (basic) return;

        string noApp = app == "" && appName != "" ? " " + L.F("Если {0} не установлен — он ставится вместе с драйвером с сайта {1}.", appName, vendor) : "";
        // Заголовки советов — по-русски (с ними сверяется самопроверка), на экране — NvAdvice.TitleText.
        if (vendor == "AMD")
            state.Advice.Add(new NvAdvice
            {
                Title = "В приложении AMD Software",
                Text = L.F("«Игры» → выберите игру. Radeon Anti-Lag — включить: он сокращает задержку от мыши до экрана. Radeon Boost — выключить: он на ходу снижает разрешение, и картинка теряет чёткость. Лимит FPS {0} ставьте в самой игре — второй не нужен. Если в игре его нет, задайте здесь через Radeon Chill с одинаковым минимумом и максимумом.", cap) + noApp,
                Launch = app, LaunchText = L.T("Открыть AMD Software"),
            });
        else if (vendor == "Intel")
            state.Advice.Add(new NvAdvice
            {
                Title = "В приложении Intel Graphics Software",
                Text = L.F("«Графика» → выберите игру. Режим низкой задержки — включить. Лимит FPS {0} ставьте в самой игре, а если в игре его нет — здесь, в ограничении частоты кадров.", cap) + noApp,
                Launch = app, LaunchText = L.T("Открыть настройки Intel"),
            });
        string upscale = vendor == "NVIDIA" ? "" : integrated ? "" : " " + L.T("Если FPS не хватает — включите FSR или XeSS в режиме «Качество».");
        state.Advice.Add(new NvAdvice
        {
            Title = "В самих играх",
            Text = L.F("Вертикальная синхронизация — выключить, лимит FPS — {0}, размытие в движении — выключить.", limit) + upscale + " " +
                   L.T("Для установленных игр программа предлагает это сама: вкладка «Оптимизация», блок «Настройки графики игр»."),
        });
        if (integrated)
            state.Advice.Add(new NvAdvice
            {
                Title = "Встроенная графика",
                Text = L.T("Она забирает часть оперативной памяти и делит питание с процессором. В играх снижайте разрешение или включайте FSR / XeSS, а перед игрой закрывайте браузер. Две планки памяти вместо одной заметно ускоряют встроенную графику."),
            });
        if (battery)
            state.Advice.Add(new NvAdvice
            {
                Title = "Электропитание",
                Text = L.T("Играйте от сети: от батареи видеокарта и процессор работают вполсилы, какими бы ни были настройки драйвера, а заряд уходит за час-два."),
            });
    }

    /// <summary>Что нужно знать для рекомендаций: монитор, записанные игры, место на диске.</summary>
    private sealed class Context
    {
        public int Hz, Cap;
        public double FreeGb;
        public Dictionary<string, GameProfile> Profiles = new(StringComparer.OrdinalIgnoreCase);

        public static Context Make(IReadOnlyList<GameSession> sessions)
        {
            var c = new Context { Hz = GameTuning.Refresh(), Cap = GameTuning.FrameCap() };
            try { c.Profiles = GameTuning.Profiles(sessions ?? Array.Empty<GameSession>()); } catch { }
            try { c.FreeGb = Cleaner.SystemDriveFree() / Fmt.Gib; } catch { }
            return c;
        }
    }

    private sealed class Values
    {
        public int Latency;         // 0 выкл, 1 вкл, 2 ультра
        public uint Cap, Power = PowerNormal, Sync = VsApp, Texture = TexQuality, CacheMb;
        public bool CacheKnown;
    }

    private static Values Read(Drs drs, IntPtr profile)
    {
        var v = new Values();
        var state = drs.Get(profile, LowLatencyState);
        var ultra = drs.Get(profile, LowLatencyUltra);
        var queue = drs.Get(profile, PrerenderLimit);
        // Панель NVIDIA пишет состояние режима отдельной настройкой; без неё судим по тому, что режим делает на деле: очередь в один кадр.
        v.Latency = state.Ok ? (int)Math.Min(state.Value, 2) : ultra.Ok && ultra.Value == 1 ? 2 : queue.Ok && queue.Value == 1 ? 1 : 0;
        var s = drs.Get(profile, FrameLimit); if (s.Ok) v.Cap = s.Value;
        s = drs.Get(profile, PowerMode); if (s.Ok) v.Power = s.Value;
        s = drs.Get(profile, VSync); if (s.Ok) v.Sync = s.Value;
        s = drs.Get(profile, TextureQuality); if (s.Ok) v.Texture = s.Value;
        s = drs.Get(profile, ShaderCacheSize); if (s.Ok) { v.CacheMb = s.Value; v.CacheKnown = true; }
        return v;
    }

    private static NvTarget BuildTarget(Drs drs, Context ctx, string title, string exePath, bool global)
    {
        var t = new NvTarget { Title = title, Global = global };
        IntPtr profile = IntPtr.Zero;
        if (global)
        {
            drs.Base(out profile);
            t.HasProfile = true;
            t.ProfileName = "общий профиль драйвера";
        }
        else if (string.IsNullOrEmpty(exePath))
        {
            t.Caption = L.T("файл игры не найден");
            t.Note = L.T("Программа не нашла запускаемый файл этой игры в папке установки, поэтому не знает, какой профиль драйвера к ней относится. Настройте игру в панели управления NVIDIA.");
            return t;
        }
        else
        {
            t.ExePath = exePath;
            t.Exe = Path.GetFileName(exePath);
            profile = FindProfile(drs, t);
            t.HasProfile = profile != IntPtr.Zero;
            if (t.HasProfile) t.ProfileName = drs.Info(profile).Name;
        }

        // Игра без своего профиля работает на общих настройках — их и показываем как текущие.
        IntPtr source = profile;
        if (source == IntPtr.Zero) drs.Base(out source);
        var v = Read(drs, source);
        ctx.Profiles.TryGetValue(title, out var played);
        t.Rows = Recommend(t, v, ctx, played);
        foreach (var r in t.Rows) r.Selected = r.CanChange;
        t.HasBackup = LoadBackup().Any(l => l[0] == t.Key);

        int n = t.Changes;
        string advice = n == 0 ? L.T("менять нечего") : Fmt.Plural(n, "рекомендация", "рекомендации", "рекомендаций");
        t.Caption = global ? L.F("для всех игр и программ · {0}", advice)
            : (t.HasProfile ? L.T("профиль есть") : L.T("профиля ещё нет")) + " · " + advice;
        if (!global && !t.HasProfile)
            t.Note = L.F("Своего профиля у этой игры в драйвере нет — она работает на общих настройках. Профиль для {0} будет создан, только когда вы нажмёте «Применить отмеченные».", t.Exe);
        return t;
    }

    private static IntPtr FindProfile(Drs drs, NvTarget t)
    {
        // Сначала по полному пути (так драйвер различает одноимённые файлы разных игр), затем по одному имени.
        if (t.ExePath.Contains('\\') && drs.Find(t.ExePath, out IntPtr p) == NvOk) return p;
        return drs.Find(t.Exe, out p) == NvOk ? p : IntPtr.Zero;
    }

    // ================================================================== рекомендации
    // Игры, в которых есть NVIDIA Reflex: он управляет задержкой сам и главнее режима низкой задержки драйвера.
    private static readonly string[] ReflexGames =
    {
        "VALORANT", "Counter-Strike 2", "Deadlock", "PUBG", "Fortnite", "Apex Legends", "Overwatch", "Call of Duty", "Rainbow Six",
        "Escape from Tarkov", "Destiny 2", "Marvel Rivals", "THE FINALS", "FragPunk", "Dota 2", "Warface", "NARAKA", "Cyberpunk 2077",
        "Battlefield", "War Thunder", "Enlisted", "Delta Force",
    };

    private static bool HasReflex(string title) =>
        ReflexGames.Any(g => title.Contains(g, StringComparison.OrdinalIgnoreCase)) || title.Equals("Rust", StringComparison.OrdinalIgnoreCase);

    private static List<NvRow> Recommend(NvTarget t, Values v, Context ctx, GameProfile played)
    {
        var rows = new List<NvRow>();
        bool game = !t.Global;

        // --- режим низкой задержки
        var latency = new NvRow { Key = "latency", Title = L.T("Режим низкой задержки"), NowText = LatencyText(v.Latency) };
        if (game && HasReflex(t.Title))
        {
            latency.Why = L.F("В {0} есть NVIDIA Reflex: он делает то же самое точнее и заменяет этот режим. Включите Reflex в настройках игры — здесь менять нечего.", t.TitleText);
            latency.Remark = L.T("в игре есть Reflex");
        }
        else
        {
            latency.Why = L.T("Кадры не копятся в очереди перед видеокартой — мышь откликается быстрее, когда видеокарта загружена полностью.") + " "
                          + (game ? L.T("Если в игре есть NVIDIA Reflex, включайте его: он заменяет этот режим.") : L.T("В играх с NVIDIA Reflex включайте его: он заменяет этот режим."));
            if (v.Latency == 0) { latency.ToText = LatencyText(1); latency.Writes = LatencyWrites(1); }
            else latency.Remark = L.T("менять не нужно");
        }
        rows.Add(latency);

        // --- ограничение частоты кадров
        var cap = new NvRow { Key = "cap", Title = L.T("Лимит FPS"), NowText = v.Cap == 0 ? L.T("нет") : v.Cap + " FPS" };
        if (!game)
        {
            cap.Why = L.T("Общий лимит действует на все программы сразу, в том числе на те, которым он мешает. Задавайте его по играм.");
            cap.Remark = v.Cap == 0 ? L.T("менять не нужно") : L.T("действует на все программы");
        }
        else if (ctx.Hz <= 60)
        {
            cap.Why = L.F("Монитор работает на {0} Гц: лимит драйвера здесь ничего не даст — хватит вертикальной синхронизации или лимита в самой игре.", ctx.Hz);
            cap.Remark = L.T("менять не нужно");
        }
        else
        {
            cap.Why = L.F("Чуть ниже частоты монитора ({0} Гц): кадры идут ровнее, а видеокарта не греется впустую.", ctx.Hz) + " " +
                      L.T("Лимит программа предлагает только здесь, а не в файлах игр: так он один на игру и не сбрасывается её обновлениями. Если лимит стоит и в самой игре, действует меньший из двух.");
            if (v.Cap == 0 || v.Cap > ctx.Cap + 1) { cap.ToText = ctx.Cap + " FPS"; cap.Writes.Add((FrameLimit, (uint)ctx.Cap)); }
            else cap.Remark = L.T("менять не нужно");
        }
        rows.Add(cap);

        // --- режим электропитания
        var power = new NvRow { Key = "power", Title = L.T("Режим электропитания"), NowText = PowerText(v.Power) };
        if (game)
        {
            power.Why = L.T("Видеокарта не сбрасывает частоту в лёгких сценах — меньше рывков, когда нагрузка резко растёт. Действует, только пока запущена эта игра.");
            if (v.Power != PowerMax) { power.ToText = PowerText(PowerMax); power.Writes.Add((PowerMode, PowerMax)); }
            else power.Remark = L.T("менять не нужно");
        }
        else
        {
            power.Why = L.T("В общих настройках лучше обычный режим: с максимальной производительностью видеокарта держит высокую частоту и вне игр — лишние тепло и шум. Максимальную производительность включайте для отдельных игр.");
            if (v.Power == PowerMax) { power.ToText = PowerText(PowerNormal); power.Writes.Add((PowerMode, PowerNormal)); }
            else power.Remark = L.T("менять не нужно");
        }
        rows.Add(power);

        // --- вертикальная синхронизация
        var sync = new NvRow { Key = "vsync", Title = L.T("Вертикальная синхронизация"), NowText = SyncText(v.Sync) };
        if (v.Sync == VsHalf || v.Sync == VsThird || v.Sync == VsQuarter)
        {
            sync.Why = L.T("Сейчас драйвер держит FPS на половине частоты монитора или ниже — игра идёт заметно хуже, чем могла бы. Верните выбор игре.");
            sync.ToText = SyncText(VsApp);
            sync.Writes.Add((VSync, VsApp));
        }
        else
        {
            sync.Why = L.T("Решает, кто согласует кадры с монитором: игра или драйвер. С лимитом FPS и переменной частотой обновления (G-SYNC) разрывов картинки нет и без синхронизации, поэтому менять не нужно.");
            sync.Remark = L.T("менять не нужно");
        }
        rows.Add(sync);

        // --- качество фильтрации текстур
        var tex = new NvRow { Key = "texture", Title = L.T("Качество фильтрации текстур"), NowText = TextureText(v.Texture) };
        bool gpuBound = played?.GpuBound >= 50;
        if (game && v.Texture == TexHighQuality && gpuBound)
        {
            tex.Why = L.F("В ваших сессиях эту игру ограничивала видеокарта ({0:0} % времени). «Качество» выглядит почти так же, а видеокарте чуть легче.", played.GpuBound);
            tex.ToText = TextureText(TexQuality);
            tex.Writes.Add((TextureQuality, TexQuality));
        }
        else
        {
            tex.Why = L.T("Насколько точно сглаживаются текстуры под углом. На FPS влияет на 1–2 %, поэтому менять ради скорости незачем.");
            tex.Remark = L.T("менять не нужно");
        }
        rows.Add(tex);

        // --- размер кэша шейдеров: общий для всех игр
        if (!game)
        {
            var cache = new NvRow { Key = "cache", Title = L.T("Размер кэша шейдеров"), NowText = !v.CacheKnown ? L.T("по умолчанию") : CacheText(v.CacheMb) };
            cache.Why = L.T("Место на диске под готовые шейдеры. Когда его мало, шейдеры крупных игр вытесняют друг друга и собираются заново — отсюда рывки.");
            bool small = v.CacheKnown && v.CacheMb != CacheUnlimited && v.CacheMb < CacheTenGb;
            if (small && ctx.FreeGb >= 40) { cache.ToText = CacheText(CacheTenGb); cache.Writes.Add((ShaderCacheSize, CacheTenGb)); }
            else cache.Remark = small ? L.T("на диске мало места") : L.T("менять не нужно");
            rows.Add(cache);
        }
        return rows;
    }

    // «Выкл.» — очередь решает игра; «вкл.» — очередь в один кадр; «ультра» — кадр отдаётся видеокарте в последний момент.
    private static List<(uint, uint)> LatencyWrites(int mode) => new()
    {
        (LowLatencyState, (uint)mode), (LowLatencyUltra, mode == 2 ? 1u : 0u), (PrerenderLimit, mode == 0 ? 0u : 1u),
    };

    // Значения — как в панели управления NVIDIA (по-английски — её же названия).
    private static string LatencyText(int mode) => mode switch { 0 => L.T("выкл."), 1 => L.T("вкл."), _ => L.T("ультра") };
    private static string PowerText(uint v) => v switch
    {
        0 => L.T("адаптивный"), 1 => L.T("максимальная производительность"), 2 => L.T("на усмотрение драйвера"), 3 => L.T("стабильная производительность"),
        4 => L.T("экономия энергии"), 5 => L.T("обычный"), _ => L.F("другой ({0})", v),
    };
    private static string SyncText(uint v) => v switch
    {
        VsApp => L.T("как в игре"), VsOff => L.T("выкл."), VsOn => L.T("вкл."), VsFast => L.T("быстрая"),
        VsHalf => L.T("половина частоты монитора"), VsThird => L.T("треть частоты монитора"), VsQuarter => L.T("четверть частоты монитора"), _ => L.T("другая"),
    };
    private static string TextureText(uint v) => v switch
    {
        TexHighQuality => L.T("высокое качество"), TexQuality => L.T("качество"), TexPerformance => L.T("производительность"), TexHighPerformance => L.T("высокая производительность"), _ => L.T("другое"),
    };
    private static string CacheText(uint mb) => mb == CacheUnlimited ? L.T("без ограничения") : mb == 0 ? L.T("выключен")
        : mb >= 1024 ? (mb / 1024.0).ToString("0.#", L.Culture) + " " + Fmt.GbUnit : mb + " " + Fmt.MbUnit;

    // ================================================================== запускаемый файл игры
    private static readonly string[] SkipExeParts =
    {
        "crash", "launcher", "execpubg", "_be", "-be", "_zk", "easyanticheat", "anticheat", "_eac", "-eac", "eac_", "beservice", "battleye", "start_protected",
        "webhelper", "cefprocess", "cef", "overlay", "updater", "update", "installer", "install", "setup", "unins", "redist", "prereq",
        "dxsetup", "dotnet", "report", "helper", "bootstrap", "console", "editor", "server", "benchmark", "config", "diagnos", "vconsole",
    };
    private static readonly string[] SkipDirParts = { @"\_commonredist\", @"\redist\", @"\redistributables\", @"\directx\", @"\support\", @"\engine\extras\", @"\easyanticheat\", @"\battleye\", @"\tools\", @"\sdk\" };

    /// <summary>Запускаемые файлы игры в папке установки, самые вероятные первыми.</summary>
    public static List<string> ExeCandidates(string dir, string title)
    {
        var scored = new List<(string Path, double Score)>();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return new List<string>();
        string root = dir.TrimEnd('\\');
        string want = Norm(title), folder = Norm(Path.GetFileName(root));
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (string file in Directory.EnumerateFiles(root, "*.exe", options).Take(1500))
            {
                string name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                string lower = file.ToLowerInvariant();
                if (SkipExeParts.Any(name.Contains) || SkipDirParts.Any(lower.Contains)) continue;
                long size = 0;
                try { size = new FileInfo(file).Length; } catch { }
                string stem = Norm(name);
                double score = Math.Min(size / 1048576.0, 300);
                if (name.EndsWith("-win64-shipping")) score += 1000;
                if (lower.Contains(@"\binaries\win64\") || lower.Contains(@"\bin\win64\") || lower.Contains(@"\bin64")) score += 200;
                if (stem.Length >= 3 && (want.Contains(stem) || stem.Contains(want) || folder.Contains(stem) || stem.Contains(folder))) score += 300;
                score -= 10 * lower[root.Length..].Count(c => c == '\\');
                scored.Add((file, score));
            }
        }
        catch { }
        return scored.OrderByDescending(s => s.Score).Select(s => s.Path).ToList();
    }

    private static string Norm(string s) => Regex.Replace((s ?? "").ToLowerInvariant(), @"[^a-z0-9а-я]", "");

    // Драйвер знает большинство игр по имени файла: если для одного из файлов есть профиль — это и есть игра.
    private static string FindGameExe(Drs drs, string dir, string title)
    {
        var candidates = ExeCandidates(dir, title);
        foreach (string c in candidates.Take(12))
            if (drs.Find(c, out _) == NvOk) return c;
        return candidates.FirstOrDefault();
    }

    // ================================================================== запись и возврат
    /// <summary>Записать отмеченные настройки в профиль. Исходные значения перед этим сохраняются; первая копия не затирается.</summary>
    public static string Apply(NvTarget t, IEnumerable<NvRow> rows)
    {
        var writes = new Dictionary<uint, uint>();
        foreach (var r in rows.Where(r => r.CanChange))
            foreach (var (id, value) in r.Writes) writes[id] = value;
        if (t == null || writes.Count == 0) return L.T("Ничего не отмечено.");
        if (!t.Global && t.Exe == "") return L.T("Запускаемый файл игры не найден — записывать некуда.");

        lock (Gate)
        {
            if (!Nv.Ready(out string why)) return why;
            using var drs = Drs.Open(out int st);
            if (drs == null) return L.F("Драйвер NVIDIA не открыл свои настройки: {0}.", Nv.Error(st));

            IntPtr profile;
            string createdName = null;
            if (t.Global) st = drs.Base(out profile);
            else
            {
                profile = FindProfile(drs, t);
                st = NvOk;
                if (profile == IntPtr.Zero)
                {
                    // У игры нет профиля: создаём свой и привязываем к нему её запускаемый файл.
                    createdName = t.Title;
                    if (drs.FindByName(createdName, out _) == NvOk) createdName = t.Title + " (Пульс ПК)";
                    if (drs.FindByName(createdName, out _) == NvOk) createdName = t.Title + " (Пульс ПК, " + t.Exe + ")";
                    st = drs.CreateProfile(createdName, out profile);
                    if (st == NvOk) st = drs.CreateApp(profile, t.Exe.ToLowerInvariant(), t.Title);
                }
            }
            if (st != NvOk || profile == IntPtr.Zero) return L.F("Не удалось открыть профиль драйвера: {0}.", Nv.Error(st));

            // Копия «как было» — до записи. Что уже сохранено раньше, не трогаем: там настоящие исходные значения.
            var present = createdName != null ? new Dictionary<uint, (uint Value, bool Predefined)>() : drs.Enum(profile);
            var backup = LoadBackup();
            var added = new List<string[]>();
            if (createdName != null) added.Add(new[] { t.Key, "created", "1", createdName });
            foreach (uint id in writes.Keys)
            {
                if (backup.Any(l => l[0] == t.Key && l[1] == id.ToString("X8"))) continue;
                // Своё значение профиля (задано пользователем) запоминаем как есть; иначе — пометка «было по умолчанию».
                var (own, was) = createdName != null ? (false, 0u) : OwnValue(drs, profile, t.Global, id, present);
                added.Add(new[] { t.Key, id.ToString("X8"), own ? "0" : "1", (own ? was : 0).ToString("X8") });
            }
            backup.AddRange(added);
            SaveBackup(backup);

            foreach (var (id, value) in writes)
            {
                st = drs.Set(profile, id, value);
                if (st == NvSettingNotFound && IsHidden(id))
                {
                    // Этот драйвер не знает скрытой настройки режима задержки: обходимся открытой — длиной очереди кадров.
                    added.RemoveAll(l => l[1] == id.ToString("X8"));
                    backup.RemoveAll(l => l[0] == t.Key && l[1] == id.ToString("X8"));
                    st = NvOk;
                    continue;
                }
                if (st != NvOk) break;
            }
            if (st == NvOk) SaveBackup(backup);
            if (st == NvOk) st = drs.Save();
            if (st != NvOk)
            {
                // В драйвер ничего не попало (изменения живут в памяти до сохранения) — убираем и только что добавленные строки копии.
                backup.RemoveAll(added.Contains);
                SaveBackup(backup);
                return st == NvInvalidUserPrivilege || st == NvAccessDenied
                    ? L.T("Драйвер не дал записать настройки: нужны права администратора.")
                    : L.F("Драйвер не принял настройки: {0}. Ничего не изменено.", Nv.Error(st));
            }
        }
        string what = Fmt.Plural(rows.Count(r => r.CanChange), "значение", "значения", "значений");
        return L.F("Готово: в профиле драйвера изменено {0}.", what) + " " + (t.Global ? L.T("Подействует на программы, запущенные после этого.") : L.T("Подействует при следующем запуске игры."))
               + " " + L.T("Исходные значения сохранены — «Вернуть как было» восстановит их.");
    }

    /// <summary>Вернуть настройки профиля к тому, что было до первого изменения программой.</summary>
    public static string Restore(NvTarget t)
    {
        if (t == null) return "";
        lock (Gate)
        {
            var backup = LoadBackup();
            var mine = backup.Where(l => l[0] == t.Key).ToList();
            if (mine.Count == 0) return L.T("Копии нет — программа этот профиль не меняла.");
            if (!Nv.Ready(out string why)) return why;
            using var drs = Drs.Open(out int st);
            if (drs == null) return L.F("Драйвер NVIDIA не открыл свои настройки: {0}.", Nv.Error(st));

            IntPtr profile;
            if (t.Global) drs.Base(out profile); else profile = FindProfile(drs, t);
            if (profile == IntPtr.Zero)
            {
                backup.RemoveAll(mine.Contains);
                SaveBackup(backup);
                return L.T("Профиля этой игры в драйвере уже нет — возвращать нечего.");
            }

            bool created = mine.Any(l => l[1] == "created");
            foreach (var l in mine.Where(l => l[1] != "created"))
            {
                uint id = Convert.ToUInt32(l[1], 16);
                if (l[2] == "1")
                {
                    // Было по умолчанию: убираем своё значение, профиль снова берёт заводское или общее.
                    st = drs.RestoreDefault(profile, id);
                    if (!t.Global && OwnValue(drs, profile, false, id, drs.Enum(profile)).Own) st = drs.DeleteSetting(profile, id);
                    if (st == NvSettingNotFound) st = NvOk;
                }
                else st = drs.Set(profile, id, Convert.ToUInt32(l[3], 16));
                if (st != NvOk) break;
            }
            bool removed = false;
            if (st == NvOk && created)
            {
                // Профиль создавала программа: если в нём не осталось чужих настроек, убираем и его.
                var info = drs.Info(profile);
                if (!info.Predefined && info.Settings == 0) { st = drs.DeleteProfile(profile); removed = st == NvOk; }
            }
            if (st == NvOk) st = drs.Save();
            if (st != NvOk)
                return st == NvInvalidUserPrivilege || st == NvAccessDenied
                    ? L.T("Драйвер не дал записать настройки: нужны права администратора.")
                    : L.F("Драйвер не принял настройки: {0}. Ничего не изменено.", Nv.Error(st));

            backup.RemoveAll(mine.Contains);
            SaveBackup(backup);
            return removed ? L.T("Настройки возвращены, созданный программой профиль игры удалён из драйвера.")
                : L.T("Настройки профиля возвращены к тем, что были до изменения.");
        }
    }

    /// <summary>Задано ли значение в самом профиле (а не унаследовано и не заводское) и чему оно равно.</summary>
    private static (bool Own, uint Value) OwnValue(Drs drs, IntPtr profile, bool isBase, uint id, Dictionary<uint, (uint Value, bool Predefined)> present)
    {
        if (IsHidden(id))
        {
            // Скрытых настроек нет в перечислении профиля: судим по тому, откуда драйвер взял значение (0 — из самого профиля).
            var g = drs.Get(profile, id);
            return (g.Ok && (isBase || g.Location == 0) && !g.Predefined, g.Value);
        }
        return present.TryGetValue(id, out var was) && !was.Predefined ? (true, was.Value) : (false, 0u);
    }

    // Строки копии: «профиль|номер настройки|было по умолчанию (1/0)|значение»; строка «профиль|created|1|имя» — профиль создан программой.
    // Строки проверяются при чтении: известная настройка, флаг 0/1, значение — 8 шестнадцатеричных цифр. Остальное отбрасывается.
    private static List<string[]> LoadBackup()
    {
        try
        {
            string text = AtomicFile.Read(BackupPath, t => t);
            return (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Split('|', 4)).Where(ValidLine).ToList();
        }
        catch { return new List<string[]>(); }
    }

    internal static bool ValidLine(string[] p)
    {
        if (p.Length != 4 || string.IsNullOrEmpty(p[0]) || p[0].Length > 512 || p[0].Any(char.IsControl)) return false;
        if (p[1] == "created") return p[2] == "1" && p[3].Length <= 512 && !p[3].Any(char.IsControl);
        return p[1].Length == 8 && uint.TryParse(p[1], System.Globalization.NumberStyles.HexNumber, null, out uint id) && KnownIds.Contains(id)
               && p[2] is "0" or "1"
               && p[3].Length == 8 && uint.TryParse(p[3], System.Globalization.NumberStyles.HexNumber, null, out _);
    }

    private static void SaveBackup(List<string[]> lines)
    {
        string path = BackupPath;
        if (lines.Count == 0) { foreach (var f in new[] { path, path + ".bak" }) { try { File.Delete(f); } catch { } } return; }
        AtomicFile.WriteAllText(path, string.Join("\n", lines.Select(l => string.Join("|", l))) + "\n");
    }

    // ================================================================== самопроверка (только чтение)
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        string fail = null;
        try
        {
            bool nvidiaCard = GpuDriver.ReadAdapters().Any(a => a.Vendor == "NVIDIA" && !a.Basic);
            string dll = Path.Combine(Environment.SystemDirectory, "nvapi64.dll");
            sb.AppendLine($"Видеокарта NVIDIA: {(nvidiaCard ? "есть" : "нет")}; nvapi64.dll: {(File.Exists(dll) ? "есть" : "нет")}");
            var state = Scan(host?.Sessions() ?? SessionStore.Load());
            if (!state.Available)
            {
                sb.AppendLine("Раздел недоступен: " + state.Reason);
                foreach (var a in state.Advice) sb.AppendLine($"  совет: {a.Title} — {a.Text}");
                if (nvidiaCard) fail = "видеокарта NVIDIA есть, а профили не читаются";
            }
            else
            {
                sb.AppendLine("Режим низкой задержки: " + (Nv.GetHidden != null && Nv.SetHidden != null ? "читается и пишется так же, как в панели NVIDIA (расширенные функции есть)" : "только через длину очереди кадров (расширенных функций в драйвере нет)"));
                sb.AppendLine($"NVAPI: инициализация и DRS-сессия — успешно; монитор {GameTuning.Refresh()} Гц, лимит по формуле — {GameTuning.FrameCap()} кадров/с");
                foreach (var t in state.Targets)
                {
                    sb.AppendLine($"{t.Title} — {t.Caption}" + (t.Global ? "" : $"; файл: {(t.ExePath == "" ? "не найден" : t.ExePath)}; профиль: {(t.HasProfile ? "«" + t.ProfileName + "»" : "нет")}")
                                  + (t.HasBackup ? "; есть копия «как было»" : ""));
                    foreach (var r in t.Rows) sb.AppendLine($"    {r.Title}: {r.Line}");
                }
                if (state.Targets.Count == 0 || state.Targets[0].Rows.Count < 6) fail = "общий профиль прочитан не полностью";
            }
            var dummy = state.Available ? TargetForExe("Проверка", "pulsepc-selftest-dummy.exe") : null;
            if (dummy != null) sb.AppendLine("Профиль проверочного файла pulsepc-selftest-dummy.exe: " + (dummy.HasProfile ? "ЕСТЬ (остался от проверки записи)" : "нет, как и должно быть"));
            if (dummy?.HasProfile == true) fail ??= "в драйвере остался проверочный профиль";
            sb.AppendLine("Файл копии: " + BackupPath + (File.Exists(BackupPath) ? " — есть" : " — нет (программа профили не меняла)"));
        }
        catch (Exception ex) { fail = ex.GetType().Name + ": " + ex.Message; }
        return (fail == null ? "OK" : "FAIL: " + fail) + "\n" + sb;
    }

    // ================================================================== NVAPI
    /// <summary>Функции nvapi64.dll: библиотека отдаёт их по номерам через nvapi_QueryInterface (номера — nvapi_interface.h).</summary>
    private static class Nv
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr QueryInterface(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int Plain();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int OutHandle(out IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int OnSession(IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int GetProfile(IntPtr session, out IntPtr profile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int FindApp(IntPtr session, IntPtr name, out IntPtr profile, IntPtr application);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int FindProfile(IntPtr session, IntPtr name, out IntPtr profile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int GetSetting(IntPtr session, IntPtr profile, uint id, IntPtr setting);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int OnProfileData(IntPtr session, IntPtr profile, IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int OnProfile(IntPtr session, IntPtr profile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int OnProfileSetting(IntPtr session, IntPtr profile, uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int EnumSettings(IntPtr session, IntPtr profile, uint start, ref uint count, IntPtr settings);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int NewProfile(IntPtr session, IntPtr info, out IntPtr profile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int ErrorMessage(int status, IntPtr text);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int GetSettingEx(IntPtr session, IntPtr profile, uint id, IntPtr setting, ref uint extra);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int SetSettingEx(IntPtr session, IntPtr profile, IntPtr setting, uint extra1, uint extra2);

        public static OutHandle CreateSession;                  // NvAPI_DRS_CreateSession
        public static OnSession DestroySession, LoadSettings, SaveSettings;
        public static GetProfile GetBaseProfile;                // NvAPI_DRS_GetBaseProfile
        public static FindApp FindApplicationByName;
        public static FindProfile FindProfileByName;
        public static GetSetting Get;                           // NvAPI_DRS_GetSetting
        public static OnProfileData Set, GetProfileInfo, CreateApplication;
        public static OnProfile DeleteProfile;
        public static OnProfileSetting RestoreProfileDefaultSetting, DeleteProfileSetting;
        public static EnumSettings Enum;
        public static NewProfile CreateProfile;
        private static ErrorMessage getError;
        // Расширенные варианты для настроек вне открытого списка (номера — nvidiaProfileInspector). Могут отсутствовать: тогда null.
        public static GetSettingEx GetHidden;
        public static SetSettingEx SetHidden;
        public static OnProfileSetting RestoreHidden, DeleteHidden;

        private static bool tried, ready;
        private static string reason = "";
        private static QueryInterface query;

        /// <summary>Библиотека загружена и NvAPI_Initialize прошёл. Иначе why — понятная причина.</summary>
        public static bool Ready(out string why)
        {
            lock (Gate)
            {
                if (!tried)
                {
                    tried = true;
                    try { ready = Load(out reason); }
                    catch (Exception ex) { ready = false; reason = L.F("Драйвер NVIDIA не отвечает ({0}).", ex.Message); }
                }
                why = reason;
                return ready;
            }
        }

        private static bool Load(out string why)
        {
            why = "";
            // Только из системной папки и по полному пути: туда библиотеку кладёт установщик драйвера.
            string dll = Path.Combine(Environment.SystemDirectory, "nvapi64.dll");
            if (!Environment.Is64BitProcess || !File.Exists(dll)) { why = L.T("Драйвер NVIDIA не установлен: его библиотеки настроек (nvapi64.dll) в системе нет."); return false; }
            if (!NativeLibrary.TryLoad(dll, out IntPtr lib) || !NativeLibrary.TryGetExport(lib, "nvapi_QueryInterface", out IntPtr entry))
            { why = L.T("Библиотека драйвера NVIDIA не загрузилась. Переустановите драйвер."); return false; }
            query = Marshal.GetDelegateForFunctionPointer<QueryInterface>(entry);

            var init = Fn<Plain>(0x0150E828);                       // NvAPI_Initialize
            int st = init == null ? -1 : init();
            if (st != NvOk) { why = L.T("Драйвер NVIDIA не отвечает: видеокарта NVIDIA не найдена или драйвер не запущен."); return false; }

            getError = Fn<ErrorMessage>(0x6C2D048C);                // NvAPI_GetErrorMessage
            CreateSession = Fn<OutHandle>(0x0694D52E);              // NvAPI_DRS_CreateSession
            DestroySession = Fn<OnSession>(0xDAD9CFF8);             // NvAPI_DRS_DestroySession
            LoadSettings = Fn<OnSession>(0x375DBD6B);               // NvAPI_DRS_LoadSettings
            SaveSettings = Fn<OnSession>(0xFCBC7E14);               // NvAPI_DRS_SaveSettings
            GetBaseProfile = Fn<GetProfile>(0xDA8466A0);            // NvAPI_DRS_GetBaseProfile
            FindApplicationByName = Fn<FindApp>(0xEEE566B2);        // NvAPI_DRS_FindApplicationByName
            FindProfileByName = Fn<FindProfile>(0x7E4A9A0B);        // NvAPI_DRS_FindProfileByName
            Get = Fn<GetSetting>(0x73BF8338);                       // NvAPI_DRS_GetSetting
            Set = Fn<OnProfileData>(0x577DD202);                    // NvAPI_DRS_SetSetting
            GetProfileInfo = Fn<OnProfileData>(0x61CD6FD6);         // NvAPI_DRS_GetProfileInfo
            Enum = Fn<EnumSettings>(0xAE3039DA);                    // NvAPI_DRS_EnumSettings
            CreateProfile = Fn<NewProfile>(0xCC176068);             // NvAPI_DRS_CreateProfile
            DeleteProfile = Fn<OnProfile>(0x17093206);              // NvAPI_DRS_DeleteProfile
            CreateApplication = Fn<OnProfileData>(0x4347A9DE);      // NvAPI_DRS_CreateApplication
            RestoreProfileDefaultSetting = Fn<OnProfileSetting>(0x53F0381E);    // NvAPI_DRS_RestoreProfileDefaultSetting
            DeleteProfileSetting = Fn<OnProfileSetting>(0xE4A26362);            // NvAPI_DRS_DeleteProfileSetting
            GetHidden = Fn<GetSettingEx>(0xEA99498D);               // расширенный GetSetting
            SetHidden = Fn<SetSettingEx>(0x8A2CF5F5);               // расширенный SetSetting
            RestoreHidden = Fn<OnProfileSetting>(0x7DD5B261);       // расширенный RestoreProfileDefaultSetting
            DeleteHidden = Fn<OnProfileSetting>(0xD20D29DF);        // расширенный DeleteProfileSetting

            bool all = CreateSession != null && DestroySession != null && LoadSettings != null && SaveSettings != null && GetBaseProfile != null
                       && FindApplicationByName != null && FindProfileByName != null && Get != null && Set != null && GetProfileInfo != null && Enum != null
                       && CreateProfile != null && DeleteProfile != null && CreateApplication != null && RestoreProfileDefaultSetting != null && DeleteProfileSetting != null;
            if (!all) why = L.T("Установленный драйвер NVIDIA слишком старый: в нём нет нужных функций. Обновите драйвер.");
            return all;
        }

        private static T Fn<T>(uint id) where T : Delegate
        {
            IntPtr p = query(id);
            return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
        }

        /// <summary>Текст ошибки драйвера с её кодом.</summary>
        public static string Error(int status)
        {
            string text = "";
            if (getError != null)
            {
                IntPtr buf = Marshal.AllocHGlobal(64);      // NvAPI_ShortString — 64 байта
                try
                {
                    Marshal.WriteByte(buf, 0, 0);
                    if (getError(status, buf) == NvOk) text = Marshal.PtrToStringAnsi(buf) ?? "";
                }
                catch { }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return text == "" ? L.F("код {0}", status) : L.F("{0}, код {1}", text, status);
        }
    }

    /// <summary>
    /// Сеанс работы с настройками драйвера (DRS). Изменения живут в памяти сеанса, пока не вызван Save.
    /// Структуры NVAPI собираются в неуправляемой памяти по смещениям из nvapi.h (выравнивание 8, все поля — NvU32 и строки NvU16[2048]).
    /// </summary>
    private sealed class Drs : IDisposable
    {
        private const int StringBytes = 4096;                   // NvAPI_UnicodeString: NvU16[2048]
        // NVDRS_SETTING_V1: version, settingName[2048], settingId, settingType, settingLocation, isCurrentPredefined, isPredefinedValid, predefined (4100), current (4100)
        private const int SettingSize = 12320, SettingId = 4100, SettingType = 4104, SettingLocation = 4108, SettingIsPredefined = 4112, SettingCurrent = 8220;
        // NVDRS_PROFILE_V1: version, profileName[2048], gpuSupport, isPredefined, numOfApps, numOfSettings
        private const int ProfileSize = 4116, ProfileGpu = 4100, ProfilePredefined = 4104, ProfileSettings = 4112;
        // NVDRS_APPLICATION_V1: version, isPredefined, appName[2048], userFriendlyName[2048], launcher[2048]
        private const int AppSize = 12296, AppName = 8, AppFriendly = 8 + StringBytes;
        private static int Version(int size) => size | (1 << 16);       // MAKE_NVAPI_VERSION(тип, 1)

        private IntPtr session;

        public static Drs Open(out int status)
        {
            status = Nv.CreateSession(out IntPtr s);
            if (status != NvOk) return null;
            status = Nv.LoadSettings(s);
            if (status != NvOk) { Nv.DestroySession(s); return null; }
            return new Drs { session = s };
        }

        public void Dispose()
        {
            if (session != IntPtr.Zero) { try { Nv.DestroySession(session); } catch { } session = IntPtr.Zero; }
        }

        private static IntPtr Alloc(int size)
        {
            IntPtr p = Marshal.AllocHGlobal(size);
            Marshal.Copy(new byte[size], 0, p, size);
            return p;
        }

        private static void PutString(IntPtr at, string text)
        {
            byte[] b = Encoding.Unicode.GetBytes(text ?? "");
            Marshal.Copy(b, 0, at, Math.Min(b.Length, StringBytes - 2));       // последний символ остаётся нулём
        }

        public int Base(out IntPtr profile) => Nv.GetBaseProfile(session, out profile);

        public int Find(string exe, out IntPtr profile)
        {
            profile = IntPtr.Zero;
            IntPtr name = Alloc(StringBytes), app = Alloc(AppSize);
            try
            {
                PutString(name, exe);
                Marshal.WriteInt32(app, 0, Version(AppSize));
                int st = Nv.FindApplicationByName(session, name, out profile, app);
                if (st != NvOk) profile = IntPtr.Zero;
                return st;
            }
            finally { Marshal.FreeHGlobal(name); Marshal.FreeHGlobal(app); }
        }

        public int FindByName(string profileName, out IntPtr profile)
        {
            IntPtr name = Alloc(StringBytes);
            try { PutString(name, profileName); return Nv.FindProfileByName(session, name, out profile); }
            finally { Marshal.FreeHGlobal(name); }
        }

        public (string Name, bool Predefined, int Settings, uint Gpu) Info(IntPtr profile)
        {
            IntPtr info = Alloc(ProfileSize);
            try
            {
                Marshal.WriteInt32(info, 0, Version(ProfileSize));
                if (Nv.GetProfileInfo(session, profile, info) != NvOk) return ("", false, -1, 0);
                return (Marshal.PtrToStringUni(info + 4) ?? "", Marshal.ReadInt32(info, ProfilePredefined) != 0, Marshal.ReadInt32(info, ProfileSettings), (uint)Marshal.ReadInt32(info, ProfileGpu));
            }
            finally { Marshal.FreeHGlobal(info); }
        }

        /// <summary>Действующее значение настройки для профиля (с учётом общих настроек и заводских значений).</summary>
        public (bool Ok, uint Value, int Status, int Location, bool Predefined) Get(IntPtr profile, uint id)
        {
            IntPtr s = Alloc(SettingSize);
            try
            {
                Marshal.WriteInt32(s, 0, Version(SettingSize));
                uint extra = 0;
                int st = IsHidden(id) && Nv.GetHidden != null ? Nv.GetHidden(session, profile, id, s, ref extra) : Nv.Get(session, profile, id, s);
                return (st == NvOk && Marshal.ReadInt32(s, SettingType) == 0, (uint)Marshal.ReadInt32(s, SettingCurrent), st,
                    Marshal.ReadInt32(s, SettingLocation), Marshal.ReadInt32(s, SettingIsPredefined) != 0);
            }
            finally { Marshal.FreeHGlobal(s); }
        }

        /// <summary>Настройки, записанные в самом профиле: значение и признак «это заводское значение NVIDIA».</summary>
        public Dictionary<uint, (uint Value, bool Predefined)> Enum(IntPtr profile)
        {
            var map = new Dictionary<uint, (uint, bool)>();
            int n = Info(profile).Settings;
            if (n <= 0) return map;
            n = Math.Min(n + 8, 512);
            IntPtr arr = Alloc(n * SettingSize);
            try
            {
                for (int i = 0; i < n; i++) Marshal.WriteInt32(arr + i * SettingSize, 0, Version(SettingSize));
                uint count = (uint)n;
                if (Nv.Enum(session, profile, 0, ref count, arr) != NvOk) return map;
                for (int i = 0; i < count && i < n; i++)
                {
                    IntPtr e = arr + i * SettingSize;
                    map[(uint)Marshal.ReadInt32(e, SettingId)] = ((uint)Marshal.ReadInt32(e, SettingCurrent), Marshal.ReadInt32(e, SettingIsPredefined) != 0);
                }
            }
            finally { Marshal.FreeHGlobal(arr); }
            return map;
        }

        public int Set(IntPtr profile, uint id, uint value)
        {
            IntPtr s = Alloc(SettingSize);
            try
            {
                Marshal.WriteInt32(s, 0, Version(SettingSize));
                Marshal.WriteInt32(s, SettingId, (int)id);
                Marshal.WriteInt32(s, SettingType, 0);              // NVDRS_DWORD_TYPE
                Marshal.WriteInt32(s, SettingCurrent, (int)value);
                if (!IsHidden(id)) return Nv.Set(session, profile, s);
                return Nv.SetHidden != null ? Nv.SetHidden(session, profile, s, 0, 0) : NvSettingNotFound;
            }
            finally { Marshal.FreeHGlobal(s); }
        }

        public int RestoreDefault(IntPtr profile, uint id) =>
            IsHidden(id) ? (Nv.RestoreHidden != null ? Nv.RestoreHidden(session, profile, id) : NvSettingNotFound) : Nv.RestoreProfileDefaultSetting(session, profile, id);
        public int DeleteSetting(IntPtr profile, uint id) =>
            IsHidden(id) ? (Nv.DeleteHidden != null ? Nv.DeleteHidden(session, profile, id) : NvSettingNotFound) : Nv.DeleteProfileSetting(session, profile, id);
        public int DeleteProfile(IntPtr profile) => Nv.DeleteProfile(session, profile);
        public int Save() => Nv.SaveSettings(session);

        public int CreateProfile(string name, out IntPtr profile)
        {
            profile = IntPtr.Zero;
            // Для каких видеокарт профиль — как у общего профиля этого драйвера.
            uint gpu = Base(out IntPtr basic) == NvOk ? Info(basic).Gpu : 0;
            IntPtr info = Alloc(ProfileSize);
            try
            {
                Marshal.WriteInt32(info, 0, Version(ProfileSize));
                PutString(info + 4, name);
                Marshal.WriteInt32(info, ProfileGpu, (int)(gpu == 0 ? 1 : gpu));
                return Nv.CreateProfile(session, info, out profile);
            }
            finally { Marshal.FreeHGlobal(info); }
        }

        public int CreateApp(IntPtr profile, string exe, string friendly)
        {
            IntPtr app = Alloc(AppSize);
            try
            {
                Marshal.WriteInt32(app, 0, Version(AppSize));
                PutString(app + AppName, exe);
                PutString(app + AppFriendly, friendly);
                return Nv.CreateApplication(session, profile, app);
            }
            finally { Marshal.FreeHGlobal(app); }
        }
    }
}
