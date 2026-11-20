using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PulsePC.Services;

/// <summary>Одно предлагаемое изменение в файле настроек игры.</summary>
public class TuneChange : Notifier
{
    public string Section { get; set; } = "";   // раздел ini (для Unreal); пусто для других форматов
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Why { get; set; } = "";
    public string FromText { get; set; } = "";  // как показать значения человеку
    public string ToText { get; set; } = "";
    public string Line => L.F("сейчас: {0} → станет: {1}", FromText, ToText);

    private bool selected = true;
    public bool Selected { get => selected; set => Set(ref selected, value); }
}

/// <summary>Игра и что программа может сделать с её настройками.</summary>
public class GameTune
{
    public string Game { get; set; } = "";
    public string Engine { get; set; } = "";
    public string InstallDir { get; set; }
    public string ConfigPath { get; set; }
    public string Format { get; set; } = "";     // ini, kv (Source 2), mc (Minecraft) или пусто
    public bool CanEdit => ConfigPath != null && Format != "";
    public string Basis { get; set; } = "";      // на чём основаны рекомендации
    public string Note { get; set; } = "";       // что программа не меняет и почему
    public List<TuneChange> Changes { get; set; } = new();
    public bool HasBackup { get; set; }

    public string Title => Game;
    public string Caption => !CanEdit ? Engine + " · " + (Note.Length > 0 ? L.T("только советы") : L.T("настройки не найдены"))
        : Changes.Count == 0 ? Engine + " · " + L.T("настройки в порядке")
        : Engine + " · " + Fmt.Plural(Changes.Count, "рекомендация", "рекомендации", "рекомендаций");
}

/// <summary>Что известно о прошлых сессиях игры: на что она опиралась на этом ПК.</summary>
public class GameProfile
{
    public double? CpuBound, GpuBound;
    public bool VramFull;
    public int Sessions;
}

/// <summary>
/// Настройки графики игр. Движок определяется по папке установки — поэтому работает и для игр, установленных позже.
/// Unreal Engine: GameUserSettings.ini в %LOCALAPPDATA%\&lt;проект&gt;\Saved\Config; Source 2: game\&lt;мод&gt;\cfg\video.txt;
/// Minecraft: options.txt. Меняются только ключи, которые в файле уже есть; перед записью делается копия.
/// </summary>
public static class GameTuning
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    /// <summary>
    /// Копии файлов настроек игр — в защищённой папке (%ProgramData%\PulsePC\game-backups): программа с правами администратора
    /// копирует их поверх настроек игры, а в папке пользователя их (или ссылку вместо них) мог бы подменить кто угодно.
    /// </summary>
    private static string BackupRoot
    {
        get
        {
            string root = SecureStore.PathOf("game-backups");
            lock (MigrateGate)
            {
                if (!backupsMigrated) { backupsMigrated = true; MigrateBackups(root); }
            }
            return root;
        }
    }

    private static readonly object MigrateGate = new();
    private static bool backupsMigrated;

    /// <summary>
    /// Один раз перенести копии прежних версий из %LOCALAPPDATA%\PulsePC\game-backups (папки по играм сохраняются).
    /// Ссылки (точки соединения, символические) не переносятся и не раскрываются: через них подсунули бы чужой файл.
    /// </summary>
    private static void MigrateBackups(string root)
    {
        try
        {
            string old = Path.Combine(SessionStore.Folder, "game-backups");
            if (string.Equals(Path.GetFullPath(old).TrimEnd('\\'), Path.GetFullPath(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
            var from = new DirectoryInfo(old);
            if (!from.Exists) return;
            if (from.Attributes.HasFlag(FileAttributes.ReparsePoint)) { from.Delete(); return; }      // удаляется сама ссылка
            int moved = 0;
            foreach (var game in from.EnumerateDirectories())
            {
                if (game.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                string to = Path.Combine(root, game.Name);
                foreach (var f in game.EnumerateFiles())
                {
                    if (f.Attributes.HasFlag(FileAttributes.ReparsePoint) || f.Length > 16 * 1024 * 1024) continue;
                    string dst = Path.Combine(to, f.Name);
                    if (File.Exists(dst)) continue;
                    Directory.CreateDirectory(to);
                    f.CopyTo(dst, false);
                    File.SetAttributes(dst, FileAttributes.Normal);
                    moved++;
                }
            }
            from.Delete(true);      // ссылки внутри удаляются как ссылки, по ним Directory.Delete не идёт
            if (moved > 0) Log.Info($"копии настроек игр перенесены в защищённую папку: {moved}");
        }
        catch (Exception ex) { Log.Error("перенос копий настроек игр", ex); }
    }

    /// <summary>Профили игр по записанным сессиям (последние пять каждой игры).</summary>
    public static Dictionary<string, GameProfile> Profiles(IEnumerable<GameSession> sessions)
    {
        var result = new Dictionary<string, GameProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in sessions.GroupBy(s => s.Game))
        {
            var recent = g.OrderByDescending(s => s.Start).Take(5).ToList();
            // У старых сессий подробного разбора нет — для них признак по средней загрузке, как в их выводах.
            double? Cpu(GameSession s) => s.BoundCpuPct ?? (s.AvgGpu > 0 && s.AvgGpu < 75 && s.AvgCpu >= 40 ? 60 : s.AvgGpu > 0 ? 10 : null);
            double? Gpu(GameSession s) => s.BoundGpuPct ?? (s.AvgGpu >= 93 ? 70 : s.AvgGpu > 0 ? 20 : null);
            var cpu = recent.Select(Cpu).Where(v => v != null).ToList();
            var gpu = recent.Select(Gpu).Where(v => v != null).ToList();
            result[g.Key] = new GameProfile
            {
                Sessions = recent.Count,
                CpuBound = cpu.Count > 0 ? cpu.Average() : null,
                GpuBound = gpu.Count > 0 ? gpu.Average() : null,
                VramFull = recent.Any(s => s.VramTotalGb > 0 && s.MaxVramGb >= 0.92 * s.VramTotalGb),
            };
        }
        return result;
    }

    public static List<GameTune> Scan(List<GameEntry> games, Dictionary<string, GameProfile> profiles)
    {
        int cap = FrameCap();
        var list = new List<GameTune>();
        foreach (var g in games)
        {
            try
            {
                profiles.TryGetValue(g.Title, out var profile);
                list.Add(Inspect(g.Title, g.InstallDir, profile, cap));
            }
            catch { }
        }
        // Minecraft ставится не через магазины, ищем отдельно.
        string mc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "options.txt");
        if (File.Exists(mc) && !list.Any(t => t.Game == "Minecraft"))
        {
            profiles.TryGetValue("Minecraft", out var p);
            var t = new GameTune { Game = "Minecraft", Engine = "Minecraft", ConfigPath = mc, Format = "mc" };
            Recommend(t, p, cap);
            list.Add(t);
        }
        foreach (var t in list) t.HasBackup = LatestBackup(t) != null;
        // Сначала игры, где есть что улучшить, затем остальные.
        return list.OrderByDescending(t => t.CanEdit && t.Changes.Count > 0).ThenByDescending(t => t.CanEdit).ThenBy(t => t.Game).ToList();
    }

    /// <summary>Рекомендации для известного файла (для самопроверки).</summary>
    public static GameTune ForFile(string game, string format, string path, GameProfile profile)
    {
        var t = new GameTune { Game = game, Engine = "проверка", ConfigPath = path, Format = format, InstallDir = Path.GetDirectoryName(path) };
        Recommend(t, profile, FrameCap());
        t.HasBackup = LatestBackup(t) != null;
        return t;
    }

    // ------------------------------------------------------------------ определение движка и файла
    private static GameTune Inspect(string game, string dir, GameProfile profile, int cap)
    {
        var t = new GameTune { Game = game, InstallDir = dir, Engine = L.T("движок не определён") };
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            t.Note = L.T("Папка игры не найдена.");
            return t;
        }

        if (game.Equals("VALORANT", StringComparison.OrdinalIgnoreCase))
        {
            t.Engine = "Unreal Engine";
            t.Note = L.T("VALORANT хранит настройки на серверах Riot и при входе перезаписывает файл, поэтому программа его не меняет. Поставьте в самой игре: «Вертикальная синхронизация» — выкл., ")
                     + (CapInDriver ? "" : L.F("«Ограничение FPS» — {0}, ", cap)) + (IsNvidia ? L.T("NVIDIA Reflex — «Вкл. + усиление», ") : "") + L.T("размытие — выкл.")
                     + (CapInDriver ? " " + CapNote(cap) : "")
                     + (GpuVendors.MainVendor == "AMD" ? L.T(" Задержку от мыши до экрана снизит Radeon Anti-Lag — он включается в AMD Software, в профиле игры.") : "");
            return t;
        }

        // Source 2 (Deadlock, Counter-Strike 2): video.txt в папке мода.
        string gameDir = Path.Combine(dir, "game");
        if (Directory.Exists(Path.Combine(gameDir, "bin")))
        {
            var video = SafeDirs(gameDir).Select(d => Path.Combine(d, "cfg", "video.txt")).FirstOrDefault(File.Exists);
            t.Engine = "Source 2";
            if (video != null) { t.ConfigPath = video; t.Format = "kv"; Recommend(t, profile, cap); }
            else t.Note = L.T("Файл настроек не найден: он появится после первого запуска игры.");
            return t;
        }

        // Unity: настройки хранятся в реестре по-своему у каждой игры.
        if (File.Exists(Path.Combine(dir, "UnityPlayer.dll")) || SafeDirs(dir).Any(d => d.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)))
        {
            t.Engine = "Unity";
            t.Note = L.T("Игры на Unity хранят настройки каждая по-своему, поэтому программа их не меняет. Поставьте в самой игре: вертикальная синхронизация — выкл., ")
                     + (CapInDriver ? L.T("размытие в движении — выкл. ") + CapNote(cap) : L.F("лимит FPS — {0}, размытие в движении — выкл.", cap));
            return t;
        }

        // Unreal Engine: проекты — папки с Binaries\Win64 (кроме самого движка).
        var projects = SafeDirs(dir).Where(d => Directory.Exists(Path.Combine(d, "Binaries")) && !Path.GetFileName(d).Equals("Engine", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName).ToList();
        bool unreal = projects.Count > 0 || Directory.Exists(Path.Combine(dir, "Engine", "Binaries"));
        if (unreal)
        {
            t.Engine = "Unreal Engine";
            // Имена, под которыми игра может хранить настройки: проект, папка установки, имя запускаемого файла.
            var names = new HashSet<string>(projects, StringComparer.OrdinalIgnoreCase) { Path.GetFileName(dir.TrimEnd('\\')) };
            foreach (var exe in SafeFiles(dir, "*.exe"))
            {
                string stem = Path.GetFileNameWithoutExtension(exe);
                names.Add(stem);
                names.Add(Regex.Replace(stem, "(Client|Game|-Win64-Shipping)$", "", RegexOptions.IgnoreCase));
            }
            string ini = names.SelectMany(n => FindIni(Path.Combine(Local, n)))
                .Concat(names.SelectMany(n => FindIni(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", n))))
                .Where(p => p.IndexOf(@"\WindowsEditor\", StringComparison.OrdinalIgnoreCase) < 0)     // настройки редактора Unreal, не игры
                .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
            if (ini != null) { t.ConfigPath = ini; t.Format = "ini"; Recommend(t, profile, cap); }
            else t.Note = L.T("Файл настроек не найден: он появится после первого запуска игры.");
            return t;
        }

        t.Note = L.T("Программа не знает, как эта игра хранит настройки. Поставьте в самой игре: вертикальная синхронизация — выкл.")
                 + (CapInDriver ? " " + CapNote(cap) : L.F(", лимит FPS — {0}.", cap));
        return t;
    }

    private static IEnumerable<string> FindIni(string root)
    {
        if (!Directory.Exists(root)) return Enumerable.Empty<string>();
        try
        {
            return Directory.EnumerateFiles(root, "GameUserSettings.ini", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true })
                .Where(p => p.Contains(@"\Saved\Config\", StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch { return Enumerable.Empty<string>(); }
    }

    private static string[] SafeDirs(string dir) { try { return Directory.GetDirectories(dir); } catch { return Array.Empty<string>(); } }
    private static string[] SafeFiles(string dir, string mask) { try { return Directory.GetFiles(dir, mask); } catch { return Array.Empty<string>(); } }

    // ------------------------------------------------------------------ рекомендации
    private static void Recommend(GameTune t, GameProfile p, int cap)
    {
        string text = ReadText(t.ConfigPath, out _);
        bool cpu = p?.CpuBound >= 40 && p.CpuBound >= (p.GpuBound ?? 0);
        bool gpu = p?.GpuBound >= 50;
        t.Basis = p == null || p.Sessions == 0 || (p.CpuBound == null && p.GpuBound == null)
            ? L.F("Записанных сессий этой игры ещё нет, поэтому советы общие — под монитор {0} Гц. После первой игры программа увидит, что ограничивает FPS, и уточнит советы.", Refresh())
            : cpu ? L.F("В ваших сессиях FPS упирался в процессор ({0:0} % времени), поэтому снижаем то, что считает процессор: дальность, растительность, тени, эффекты.", p.CpuBound)
            : gpu ? L.F("В ваших сессиях FPS упирался в видеокарту ({0:0} % времени), поэтому снижаем самое тяжёлое для неё: тени, постобработку, освещение.", p.GpuBound)
            : L.T("В ваших сессиях запас был и у процессора, и у видеокарты — графику снижать незачем, советы только общие.");

        if (t.Format == "ini") RecommendUnreal(t, text, cpu, gpu, p?.VramFull == true, cap);
        else if (t.Format == "kv") RecommendSource(t, text, cpu, gpu);
        else if (t.Format == "mc") RecommendMinecraft(t, text, cpu, cap);
    }

    private static string WhyVsync => L.T("Вертикальная синхронизация добавляет задержку. Плавную картинку без задержки даёт лимит FPS чуть ниже частоты монитора вместе с G-SYNC / FreeSync, если монитор их поддерживает.");
    private static string WhyCap(int cap) => L.F("Чуть ниже частоты монитора ({0} Гц): кадры идут ровно, задержка минимальна, а видеокарта не греется впустую.", Refresh());
    private static string WhyBlur => L.T("Размытие смазывает картинку при поворотах камеры и мешает замечать противников. На FPS почти не влияет.");

    private static void RecommendUnreal(GameTune t, string text, bool cpu, bool gpu, bool vram, int cap)
    {
        var ini = IniValues(text);
        void Bool(string key, string want, string title, string why)
        {
            foreach (var (section, value) in ini.Where(x => x.Key == key).Select(x => (x.Section, x.Value)))
                if (!value.Equals(want, StringComparison.OrdinalIgnoreCase))
                    t.Changes.Add(new TuneChange { Section = section, Key = key, Title = title, From = value, To = want, FromText = OnOff(value), ToText = OnOff(want), Why = why });
        }
        void Max(string key, int max, string title, string why)
        {
            foreach (var (section, value) in ini.Where(x => x.Key == key).Select(x => (x.Section, x.Value)))
                if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) && v > max)
                    t.Changes.Add(new TuneChange { Section = section, Key = key, Title = title, From = value, To = max.ToString(), FromText = Quality(v), ToText = Quality(max), Why = why });
        }

        Bool("bUseVSync", "False", L.T("Вертикальная синхронизация"), WhyVsync);
        // Свой ограничитель кадров у игры (как у PUBG) — тогда стандартный ключ движка не трогаем.
        bool ownLimiter = ini.Any(x => x.Key.Contains("FrameRateLimitType", StringComparison.OrdinalIgnoreCase));
        // Видеокарта NVIDIA: лимит один — в профиле драйвера; в файле игры его не предлагаем, чтобы не было двух лимитов сразу.
        if (CapInDriver) t.Note = CapNote(cap);
        else if (ownLimiter) t.Note = L.F("У этой игры свой лимит FPS, он задаётся в её меню: выберите «Свой» и {0}. Программа эту настройку не меняет.", cap);
        if (!ownLimiter && !CapInDriver)
            foreach (var (section, value) in ini.Where(x => x.Key == "FrameRateLimit").Select(x => (x.Section, x.Value)))
                if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) && (v == 0 || v > cap + 1))
                    t.Changes.Add(new TuneChange { Section = section, Key = "FrameRateLimit", Title = L.T("Лимит FPS"), From = value, To = cap + ".000000",
                        FromText = v == 0 ? L.T("без лимита") : v.ToString("0"), ToText = cap.ToString(), Why = WhyCap(cap) });
        Bool("bMotionBlur", "False", L.T("Размытие в движении"), WhyBlur);
        foreach (var (section, value) in ini.Where(x => x.Key == "r.MotionBlur.Amount").Select(x => (x.Section, x.Value)))
            if (value.Trim() != "0")
                t.Changes.Add(new TuneChange { Section = section, Key = "r.MotionBlur.Amount", Title = L.T("Размытие в движении"), From = value, To = "0", FromText = L.T("вкл."), ToText = L.T("выкл."), Why = WhyBlur });

        if (cpu)
        {
            Max("sg.ViewDistanceQuality", 2, L.T("Дальность прорисовки"), L.T("Каждый дальний объект процессор готовит к отрисовке отдельно — это главная нагрузка на него."));
            Max("sg.FoliageQuality", 2, L.T("Растительность"), L.T("Трава и кусты — тысячи объектов для процессора."));
            Max("sg.GrassQuality", 2, L.T("Трава"), L.T("Тысячи мелких объектов для процессора."));
            Max("sg.ShadowQuality", 2, L.T("Тени"), L.T("Тени рисуются заново для каждого источника света — это нагрузка и на процессор."));
            Max("sg.EffectsQuality", 2, L.T("Эффекты"), L.T("Частицы, дым и взрывы просчитывает процессор."));
        }
        if (gpu)
        {
            Max("sg.ShadowQuality", 2, L.T("Тени"), L.T("Самая тяжёлая для видеокарты настройка, а на «высоких» тени выглядят почти так же."));
            Max("sg.PostProcessQuality", 2, L.T("Постобработка"), L.T("Размытие и эффекты камеры поверх готового кадра — заметная нагрузка на видеокарту без пользы для игры."));
            Max("sg.GlobalIlluminationQuality", 2, L.T("Глобальное освещение"), L.T("Просчёт отражённого света — одна из самых тяжёлых настроек Unreal Engine 5."));
            Max("sg.ReflectionQuality", 2, L.T("Отражения"), L.T("Тяжёлая настройка для видеокарты, а в динамичной игре отражений почти не видно."));
            if (IsNvidia) Bool("bNvidiaDLSS", "True", "DLSS", L.T("Видеокарта рисует кадр в меньшем разрешении, а нейросеть дорисовывает его до разрешения экрана — FPS заметно выше при почти той же картинке."));
            if (IsNvidia) foreach (var (section, value) in ini.Where(x => x.Key == "r.NGX.DLSS.Enable").Select(x => (x.Section, x.Value)))
                if (value.Trim() == "0")
                    t.Changes.Add(new TuneChange { Section = section, Key = "r.NGX.DLSS.Enable", Title = "DLSS", From = value, To = "1", FromText = L.T("выкл."), ToText = L.T("вкл."), Why = L.T("Нейросеть дорисовывает кадр до разрешения экрана — FPS выше при почти той же картинке.") });
        }
        if (vram)
            Max("sg.TextureQuality", 3, L.T("Качество текстур"), L.T("Видеопамять в ваших сессиях заполнялась почти полностью — отсюда подгрузки и рывки."));

        // Одна и та же настройка могла попасть дважды (тени — и для процессора, и для видеокарты).
        t.Changes = t.Changes.GroupBy(c => c.Section + "|" + c.Key).Select(g => g.First()).ToList();
    }

    private static void RecommendSource(GameTune t, string text, bool cpu, bool gpu)
    {
        var kv = KvValues(text);
        void Set(string key, Func<string, bool> bad, string to, string title, string fromText, string toText, string why)
        {
            if (kv.TryGetValue(key, out var v) && bad(v))
                t.Changes.Add(new TuneChange { Key = key, Title = title, From = v, To = to, FromText = fromText ?? v, ToText = toText ?? to, Why = why });
        }
        static bool On(string v) => v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        static int N(string v) => int.TryParse(v, out int n) ? n : 0;

        Set("setting.mat_vsync", On, "0", L.T("Вертикальная синхронизация"), L.T("вкл."), L.T("выкл."), WhyVsync);
        if (IsNvidia) Set("setting.r_low_latency", v => v == "0", "1", "NVIDIA Reflex", L.T("выкл."), L.T("вкл."), L.T("Убирает очередь кадров между процессором и видеокартой: задержка от мыши до экрана заметно ниже."));
        Set("setting.r_citadel_motion_blur", v => N(v) > 0, "0", L.T("Размытие в движении"), L.T("вкл."), L.T("выкл."), WhyBlur);
        Set("setting.r_depth_of_field", On, "false", L.T("Глубина резкости"), L.T("вкл."), L.T("выкл."), L.T("Размывает всё, что не в фокусе камеры, — дальних противников видно хуже."));
        if (cpu || gpu)
            Set("setting.r_citadel_shadow_quality", v => N(v) > 2, "2", L.T("Тени"), null, "2", L.T("Тени — тяжёлая настройка и для процессора, и для видеокарты; разница с максимальными почти незаметна."));
        if (gpu)
        {
            Set("setting.r_citadel_ssao_quality", v => N(v) > 2, "2", L.T("Затенение (SSAO)"), null, "2", L.T("Тяжёлый эффект для видеокарты, в бою почти не виден."));
            Set("setting.r_citadel_fog_quality", v => N(v) > 1, "1", L.T("Туман"), null, "1", L.T("Объёмный туман дорого обходится видеокарте."));
        }
    }

    private static void RecommendMinecraft(GameTune t, string text, bool cpu, int cap)
    {
        var map = text.Split('\n').Select(l => l.Trim()).Where(l => l.Contains(':'))
            .Select(l => (Key: l[..l.IndexOf(':')], Value: l[(l.IndexOf(':') + 1)..])).GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().Value);
        if (map.TryGetValue("enableVsync", out var vs) && vs == "true")
            t.Changes.Add(new TuneChange { Key = "enableVsync", Title = L.T("Вертикальная синхронизация"), From = vs, To = "false", FromText = L.T("вкл."), ToText = L.T("выкл."), Why = WhyVsync });
        if (map.TryGetValue("maxFps", out var fps) && int.TryParse(fps, out int f) && f > cap + 1)
            t.Changes.Add(new TuneChange { Key = "maxFps", Title = L.T("Лимит FPS"), From = fps, To = cap.ToString(), FromText = f >= 260 ? L.T("без лимита") : fps, ToText = cap.ToString(), Why = WhyCap(cap) });
        if (cpu && map.TryGetValue("renderDistance", out var rd) && int.TryParse(rd, out int r) && r > 12)
            t.Changes.Add(new TuneChange { Key = "renderDistance", Title = L.T("Дальность прорисовки"), From = rd, To = "12", FromText = L.F("{0} чанков", rd), ToText = L.F("{0} чанков", 12), Why = L.T("Каждый чанк процессор строит и обновляет, а в ваших сессиях FPS упирался именно в него.") });
    }

    private static string OnOff(string v) => v.Equals("True", StringComparison.OrdinalIgnoreCase) || v == "1" ? L.T("вкл.") : L.T("выкл.");
    private static string Quality(double v) => v switch { <= 0 => L.T("низкое"), 1 => L.T("среднее"), 2 => L.T("высокое"), 3 => L.T("очень высокое"), _ => L.T("максимальное") };

    // ------------------------------------------------------------------ чтение форматов
    private static List<(string Section, string Key, string Value)> IniValues(string text)
    {
        var list = new List<(string, string, string)>();
        string section = "";
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line; continue; }
            int eq = line.IndexOf('=');
            if (eq > 0 && !line.StartsWith(';')) list.Add((section, line[..eq].Trim(), line[(eq + 1)..].Trim()));
        }
        return list;
    }

    private static Dictionary<string, string> KvValues(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(text, "\"([^\"]+)\"\\s+\"([^\"]*)\""))
            map[m.Groups[1].Value] = m.Groups[2].Value;
        return map;
    }

    // ------------------------------------------------------------------ запись
    /// <summary>Применить отмеченные изменения: копия файла, затем запись с сохранением кодировки и атрибутов.</summary>
    public static string Apply(GameTune t, IEnumerable<TuneChange> changes)
    {
        var list = changes.ToList();
        if (!t.CanEdit || list.Count == 0) return L.T("Менять нечего.");
        if (GameDetector.IsRunningFrom(t.InstallDir)) return L.F("Закройте {0}: при выходе игра перезапишет файл своими настройками.", t.Game);

        string text = ReadText(t.ConfigPath, out var encoding);
        string backup = Backup(t);
        foreach (var c in list)
        {
            text = t.Format switch
            {
                "ini" => SetIni(text, c.Section, c.Key, c.To),
                "kv" => Regex.Replace(text, $"(\"{Regex.Escape(c.Key)}\"\\s+\")[^\"]*(\")", m => m.Groups[1].Value + c.To + m.Groups[2].Value),
                "mc" => Regex.Replace(text, $"(?m)^{Regex.Escape(c.Key)}:.*$", c.Key + ":" + c.To),
                _ => text,
            };
        }
        WriteText(t.ConfigPath, text, encoding);
        return L.F("Готово: изменено {0}. Копия прежнего файла сохранена — кнопка «Вернуть как было» отменит изменения.", Fmt.Plural(list.Count, "значение", "значения", "значений"));
    }

    /// <summary>
    /// Вернуть файл к виду до первого изменения программой (копия «.original»). После возврата копии этого файла
    /// удаляются: следующее применение снова начнёт с исходного файла.
    /// </summary>
    public static string Restore(GameTune t)
    {
        string backup = LatestBackup(t);
        if (backup == null) return L.T("Копии нет — программа этот файл не меняла.");
        if (GameDetector.IsRunningFrom(t.InstallDir)) return L.F("Закройте {0}: при выходе игра перезапишет файл.", t.Game);
        if (File.GetAttributes(backup).HasFlag(FileAttributes.ReparsePoint) || File.GetAttributes(Path.GetDirectoryName(backup)).HasFlag(FileAttributes.ReparsePoint))
            return L.T("Копия подменена ссылкой — файл не тронут.");
        var attrs = File.GetAttributes(t.ConfigPath);
        File.SetAttributes(t.ConfigPath, attrs & ~FileAttributes.ReadOnly);
        File.Copy(backup, t.ConfigPath, true);
        File.SetAttributes(t.ConfigPath, attrs);
        foreach (var f in Snapshots(t).Append(OriginalPath(t)))
        {
            try { if (File.Exists(f)) { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } } catch { }
        }
        return L.T("Готово: файл настроек вернулся к виду до изменений программы.");
    }

    private static string SetIni(string text, string section, string key, string value)
    {
        var lines = text.Split('\n').ToList();
        string current = "";
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i].TrimEnd('\r').Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { current = line; continue; }
            int eq = line.IndexOf('=');
            if (current == section && eq > 0 && line[..eq].Trim() == key)
            {
                bool cr = lines[i].EndsWith('\r');
                lines[i] = key + "=" + value + (cr ? "\r" : "");
            }
        }
        return string.Join("\n", lines);
    }

    private static string ReadText(string path, out Encoding encoding)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { encoding = Encoding.Unicode; return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2); }
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { encoding = new UTF8Encoding(true); return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3); }
        encoding = new UTF8Encoding(false);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void WriteText(string path, string text, Encoding encoding)
    {
        // Игроки иногда ставят файлу «только чтение», чтобы игра его не сбрасывала: атрибут сохраняется.
        var attrs = File.GetAttributes(path);
        File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        File.WriteAllText(path, text, encoding);
        File.SetAttributes(path, attrs);
    }

    private static string BackupDir(GameTune t) => Path.Combine(BackupRoot, Regex.Replace(t.Game, @"[^\w\-]+", "_"));

    /// <summary>Исходный файл — до первого изменения программой. Отдельно от снимков, ротацией не удаляется.</summary>
    private static string OriginalPath(GameTune t) => Path.Combine(BackupDir(t), Path.GetFileName(t.ConfigPath) + ".original");

    /// <summary>Снимки этого файла перед каждым применением («имя.ГГГГММДД-ччммсс»), новые первыми.</summary>
    private static List<string> Snapshots(GameTune t)
    {
        string dir = BackupDir(t), name = Path.GetFileName(t.ConfigPath);
        if (!Directory.Exists(dir)) return new List<string>();
        var rx = new Regex("^" + Regex.Escape(name) + @"\.\d{8}-\d{6}$", RegexOptions.IgnoreCase);
        return Directory.GetFiles(dir, name + ".*").Where(f => rx.IsMatch(Path.GetFileName(f))).OrderByDescending(f => f, StringComparer.Ordinal).ToList();
    }

    private static string Backup(GameTune t)
    {
        string dir = BackupDir(t);
        Directory.CreateDirectory(dir);
        // Исходный файл запоминается один раз: повторное применение копирует уже изменённый файл, и «Вернуть как было»
        // по такой копии вернуло бы не исходные настройки. Копии прежней версии программы (без «.original»): самая старая
        // из них ближе всего к исходному файлу.
        string original = OriginalPath(t);
        if (!File.Exists(original))
        {
            string oldest = Snapshots(t).LastOrDefault();
            File.Copy(oldest ?? t.ConfigPath, original, false);
            File.SetAttributes(original, FileAttributes.Normal);
        }
        string file = Path.Combine(dir, $"{Path.GetFileName(t.ConfigPath)}.{DateTime.Now:yyyyMMdd-HHmmss}");
        File.Copy(t.ConfigPath, file, true);
        File.SetAttributes(file, FileAttributes.Normal);    // копия файла «только для чтения» иначе тоже была бы неудаляемой
        // Храним пять последних снимков этого файла; исходный («.original») и копии других файлов игры не трогаются.
        foreach (var old in Snapshots(t).Skip(5)) { try { File.Delete(old); } catch { } }
        return file;
    }

    /// <summary>
    /// Копия для «Вернуть как было»: исходный файл, а если его нет (копии прежней версии) — самый старый снимок: он сделан
    /// перед первым из сохранившихся изменений и ближе всего к исходному файлу (после возврата снимки удаляются все).
    /// </summary>
    private static string LatestBackup(GameTune t)
    {
        if (!t.CanEdit) return null;
        string original = OriginalPath(t);
        if (File.Exists(original)) return original;
        return Snapshots(t).LastOrDefault();
    }

    // ------------------------------------------------------------------ монитор
    /// <summary>Частота основного монитора, Гц.</summary>
    public static int Refresh()
    {
        try
        {
            var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettings(null, -1, ref mode) && mode.dmDisplayFrequency > 1) return mode.dmDisplayFrequency;
        }
        catch { }
        return 60;
    }

    /// <summary>
    /// Видеокарта NVIDIA: советы про DLSS и Reflex имеют смысл только для неё. Задаётся из паспорта компьютера;
    /// пока паспорт не прочитан — по драйверу главной видеокарты в реестре.
    /// </summary>
    public static bool IsNvidia { get => isNvidia ?? GpuVendors.MainVendor == "NVIDIA"; set => isNvidia = value; }
    private static bool? isNvidia;

    /// <summary>
    /// Где живёт лимит FPS. Для видеокарт NVIDIA — в профиле драйвера (раздел «Настройки NVIDIA по играм»): он действует в любой игре,
    /// и в тех, чьи файлы программа не умеет менять, и не сбрасывается обновлениями игры. Тогда в файлах игр лимит не предлагается —
    /// два лимита сразу ничего не дают. Для остальных видеокарт лимит предлагается в файле игры, как раньше.
    /// Исключение — Minecraft: он не попадает в список игр раздела NVIDIA, поэтому его лимит остаётся в options.txt.
    /// </summary>
    public static bool CapInDriver => IsNvidia;

    private static string CapNote(int cap) =>
        L.F("С видеокартой NVIDIA лимит FPS ({0}) ставится в одном месте — в профиле драйвера: вкладка «Система», раздел «{1}». В самой игре его ставить не нужно.", cap, L.T(GpuVendors.SectionTitle("NVIDIA")));

    /// <summary>Лимит FPS под переменную частоту: формула, по которой его выставляет NVIDIA Reflex (165 Гц → 157).</summary>
    public static int FrameCap() => FrameCapFor(Refresh());

    /// <summary>Лимит для заданной частоты: 60 → 59, 75 → 73, 120 → 116, 144 → 138, 165 → 157, 240 → 224, 360 → 324.</summary>
    public static int FrameCapFor(int hz) => Math.Max(30, (int)Math.Floor(hz - hz * hz / 3600.0));

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
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
}
