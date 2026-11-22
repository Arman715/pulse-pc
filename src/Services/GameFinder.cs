using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using PulsePC.Panels;

namespace PulsePC.Services;

/// <summary>Игра, найденная не магазином-читателем, а поиском: по записи в «Установке и удалении программ», на диске или по поведению.</summary>
public sealed class FoundGame
{
    public string Title { get; set; } = "";
    /// <summary>Папка игры (корень). Для найденных по поведению — папка файла.</summary>
    public string Dir { get; set; } = "";
    /// <summary>Файл запуска (для найденных по поведению — тот, что рисовал кадры).</summary>
    public string Exe { get; set; } = "";
    /// <summary>Движок по признакам рядом с файлом: Unity, Unreal Engine, Godot… Пусто — не определён.</summary>
    public string Engine { get; set; } = "";
    /// <summary>store — запись магазина в «Установке и удалении программ»; disk — по файлам на диске; behavior — по поведению.</summary>
    public string Source { get; set; } = "";
    /// <summary>Подпись магазина для store: «EA app», «Battle.net»…</summary>
    public string Store { get; set; } = "";
    public DateTime Found { get; set; }
}

/// <summary>
/// Поиск игр, которых нет в магазинах-читателях <see cref="GameLibrary"/>:
/// 1) по записям «Установки и удаления программ» (EA app, Battle.net, Rockstar, Wargaming, Lesta, VK Play и любые программы,
///    в папке которых есть признаки игры);
/// 2) по папкам на всех несъёмных дисках — по признакам игры рядом с .exe (UnityPlayer.dll + *_Data, Binaries\Win64 и *.pak у Unreal,
///    GameAssembly.dll, steam_api, *.pck у Godot, renpy, bink2w64.dll, fmod*.dll и т. п.). Установщики, деинсталляторы, лаунчеры,
///    отчёты о сбоях, редакторы и папки разработки (*.uproject, project.godot, .git) не в счёт;
/// 3) по поведению: программа больше минуты рисует кадры на весь экран (полноэкранный или безрамочный режим) и заметно нагружает
///    видеокарту — и её нет в списке «не игр» (браузеры, плееры, Discord, OBS, лаунчеры, среды разработки, сам «Пульс ПК», Claude).
/// Обход дисков идёт в отдельном потоке с фоновым приоритетом процессора и диска (THREAD_MODE_BACKGROUND), с паузами, не во время игры;
/// результат — в кэше games-found.json, повтор — раз в неделю или кнопкой. Ничего на дисках не меняется.
/// </summary>
public static class GameFinder
{
    private const int CacheVersion = 1;
    private const string KBehavior = "games.behavior", KNot = "games.notgames", KBehaviorOn = "games.behavior.on", KDiskOn = "games.disk.on";
    private static readonly TimeSpan Rescan = TimeSpan.FromDays(7);

    private static readonly object Gate = new();
    private static AppSettings settings;
    private static PanelHost host;
    private static bool inited, loaded;
    private static List<FoundGame> scanned = new();
    private static List<FoundGame> behavior = new();
    private static List<string> notGames = new();
    private static IndexEntry[] index = Array.Empty<IndexEntry>();
    private static volatile bool scanning;

    /// <summary>Список игр изменился (поиск закончился, найдена игра по поведению, «Это не игра»). Из любого потока.</summary>
    public static event Action Changed;

    public static bool Scanning => scanning;
    public static DateTime LastScan { get; private set; } = DateTime.MinValue;
    public static double LastScanSeconds { get; private set; }
    public static int LastScanDirs { get; private set; }
    public static string LastScanNote { get; private set; } = "";

    private sealed record IndexEntry(string Prefix, string Exact, string Title, long MinMem);

    // ================================================================== запуск и состояние
    public static void Init(PanelHost h)
    {
        host = h;
        lock (Gate) { settings = h.Settings; inited = true; loaded = false; }
        Task.Run(() =>
        {
            try
            {
                EnsureLoaded();
                Rebuild();
                // Первый обход — через полторы минуты после запуска программы: запуск Windows и программы не нагружается.
                Thread.Sleep(TimeSpan.FromSeconds(90));
                if (DiskSearchOn && (DateTime.Now - LastScan > Rescan || LastScan > DateTime.Now)) StartScan();
            }
            catch (Exception ex) { Log.Error("поиск игр: запуск", ex); }
        });
        var watch = new Thread(BehaviorLoop) { IsBackground = true, Name = "PulsePC games by behavior", Priority = ThreadPriority.BelowNormal };
        watch.Start();
        // После игры — догнать отложенный обход (во время игры диски не обходятся).
        GameHooks.Ended += () => { if (pendingScan && !scanning) { pendingScan = false; Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => StartScan()); } };
    }

    /// <summary>Для разделов, открытых без запуска служб (проверка без окна): работать с этими настройками.</summary>
    public static void UseSettings(AppSettings s)
    {
        lock (Gate)
        {
            if (inited || s == null) return;
            settings = s; loaded = false;
        }
    }

    private static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (loaded) return;
            settings ??= AppSettings.Load();
            behavior = ReadList<FoundGame>(settings.Get(KBehavior));
            notGames = ReadList<string>(settings.Get(KNot));
            LoadCache();
            loaded = true;
        }
    }

    private static List<T> ReadList<T>(string json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<List<T>>(json) ?? new(); }
        catch { return new(); }
    }

    public static bool BehaviorOn => (settings ?? (settings = AppSettings.Load())).GetBool(KBehaviorOn, true);
    public static bool DiskSearchOn => (settings ?? (settings = AppSettings.Load())).GetBool(KDiskOn, true);

    public static void SetBehaviorOn(bool on) { settings.SetBool(KBehaviorOn, on); settings.Save(); }
    public static void SetDiskSearchOn(bool on) { settings.SetBool(KDiskOn, on); settings.Save(); }

    /// <summary>Найденные поиском и по поведению (без отмеченных «Это не игра»). Снимок — можно из любого потока.</summary>
    public static List<FoundGame> Found()
    {
        EnsureLoaded();
        lock (Gate)
        {
            var list = scanned.Concat(behavior).Where(g => !IsRejected(g.Source == "behavior" ? g.Exe : g.Dir)).ToList();
            return list.Select(Copy).ToList();
        }
    }

    private static FoundGame Copy(FoundGame g) => new()
    { Title = g.Title, Dir = g.Dir, Exe = g.Exe, Engine = g.Engine, Source = g.Source, Store = g.Store, Found = g.Found };

    /// <summary>Игра найдена по поведению (для пометки в отчёте сессии).</summary>
    public static FoundGame BehaviorGame(string title)
    {
        if (string.IsNullOrEmpty(title)) return null;
        EnsureLoaded();
        lock (Gate)
        {
            var g = behavior.FirstOrDefault(b => string.Equals(b.Title, title, StringComparison.OrdinalIgnoreCase) && !IsRejected(b.Exe));
            return g == null ? null : Copy(g);
        }
    }

    private static bool IsRejected(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        foreach (string n in notGames)
            if (string.Equals(path, n, StringComparison.OrdinalIgnoreCase) || path.StartsWith(n.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// «Это не игра»: путь (файл найденной по поведению или папка найденной на диске) больше не считается игрой — не записывается
    /// и при следующих поисках не находится. Записанные сессии не трогаются (их удаляет окно, если человек согласен).
    /// </summary>
    public static void MarkNotGame(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        EnsureLoaded();
        lock (Gate)
        {
            // Новый список, а не Add: IsRejected читает его без замка (Rebuild, обход дисков) — перебор старого списка не упадёт.
            if (!notGames.Contains(path, StringComparer.OrdinalIgnoreCase)) notGames = new List<string>(notGames) { path };
            behavior.RemoveAll(b => string.Equals(b.Exe, path, StringComparison.OrdinalIgnoreCase));
            settings.Set(KNot, JsonSerializer.Serialize(notGames));
            settings.Set(KBehavior, JsonSerializer.Serialize(behavior));
        }
        settings.Save();
        Rebuild();
        Raise();
    }

    /// <summary>Путь, по которому «Это не игра» убирает игру: файл для найденной по поведению, папка для остальных.</summary>
    public static string RejectPath(GameEntry g) => g.Source == "behavior" ? g.Target : g.InstallDir;

    private static void Raise() { try { Changed?.Invoke(); } catch { } }

    // ================================================================== узнавание запущенной игры
    public static bool HasIndex => Volatile.Read(ref index).Length > 0;

    /// <summary>
    /// Название игры для файла процесса, если он из папки найденной игры (любой .exe в ней, кроме служебных) или это тот самый файл,
    /// что нашёлся по поведению. mem — рабочий набор процесса: совсем лёгкие процессы (утилиты рядом с игрой) не в счёт.
    /// Магазины, которые уже узнаёт <see cref="GameDetector"/> (Steam, Epic, Riot, Astrum), здесь не повторяются.
    /// </summary>
    public static string Match(string path, long mem)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string file = Path.GetFileName(path);
        foreach (var e in Volatile.Read(ref index))
        {
            if (mem < e.MinMem) continue;
            if (e.Exact != null) { if (string.Equals(path, e.Exact, StringComparison.OrdinalIgnoreCase)) return e.Title; continue; }
            if (path.StartsWith(e.Prefix, StringComparison.OrdinalIgnoreCase) && !JunkExe(file)) return e.Title;
        }
        return null;
    }

    /// <summary>Пересобрать список папок и файлов для узнавания (после поиска, находки по поведению, «Это не игра»).</summary>
    public static void Rebuild()
    {
        try
        {
            var list = new List<IndexEntry>();
            foreach (var g in GameLibrary.Load())
            {
                if (g.Source is "steam" or "epic" or "riot" or "astrum" or "custom" or "minecraft") continue;
                if (g.Source == "behavior") { if (!string.IsNullOrEmpty(g.Target)) list.Add(new IndexEntry(null, g.Target, g.Title, 40L << 20)); continue; }
                string dir = GameLibrary.Norm(g.InstallDir);
                if (dir == null || !CustomGame.FolderUsable(dir) || IsRejected(dir)) continue;
                list.Add(new IndexEntry(dir + "\\", null, g.Title, (g.Source is "disk" or "store" ? 100L : 60L) << 20));
            }
            // Длинные пути — первыми: игра во вложенной папке другой игры узнаётся своим названием.
            Volatile.Write(ref index, list.OrderByDescending(e => (e.Prefix ?? e.Exact).Length).ToArray());
        }
        catch (Exception ex) { Log.Error("поиск игр: список для узнавания", ex); }
    }

    // ================================================================== поиск на дисках
    private static volatile bool pendingScan;
    private static string CacheFile => Path.Combine(SessionStore.Folder, "games-found.json");

    private sealed class CacheData
    {
        public int Version { get; set; }
        public DateTime Scanned { get; set; }
        public double Seconds { get; set; }
        public int Dirs { get; set; }
        public string Note { get; set; } = "";
        public List<FoundGame> Games { get; set; } = new();
    }

    private static void LoadCache()
    {
        try
        {
            var c = AtomicFile.Read(CacheFile, t => JsonSerializer.Deserialize<CacheData>(t));
            if (c == null || c.Version != CacheVersion) return;
            scanned = c.Games ?? new();
            LastScan = c.Scanned; LastScanSeconds = c.Seconds; LastScanDirs = c.Dirs; LastScanNote = c.Note ?? "";
        }
        catch (Exception ex) { Log.Error("поиск игр: кэш", ex); }
    }

    /// <summary>Начать поиск в фоне (если он уже идёт или идёт игра — ничего не делает, после игры поиск догонит).</summary>
    public static bool StartScan()
    {
        if (scanning) return false;
        if (GameHooks.InGame) { pendingScan = true; return false; }
        scanning = true;
        Raise();
        var t = new Thread(() =>
        {
            bool background = SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin);
            try
            {
                EnsureLoaded();
                var result = ScanAll(() => GameHooks.InGame);
                var cache = new CacheData
                {
                    Version = CacheVersion, Scanned = DateTime.Now, Seconds = Math.Round(result.Seconds, 1), Dirs = result.Dirs,
                    Note = result.Note, Games = result.Games,
                };
                try { AtomicFile.WriteAllText(CacheFile, JsonSerializer.Serialize(cache)); }
                catch (Exception ex) { Log.Error("поиск игр: запись кэша", ex); }
                lock (Gate) { scanned = result.Games; }
                LastScan = cache.Scanned; LastScanSeconds = cache.Seconds; LastScanDirs = cache.Dirs; LastScanNote = cache.Note;
                Log.Info($"поиск игр: {result.Games.Count} найдено, {result.Dirs} папок за {result.Seconds:0.0} с");
                Rebuild();
            }
            catch (OperationCanceledException) { pendingScan = true; Log.Info("поиск игр отложен: началась игра"); }
            catch (Exception ex) { Log.Error("поиск игр", ex); }
            finally
            {
                if (background) SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd);
                scanning = false;
                Raise();
            }
        })
        { IsBackground = true, Name = "PulsePC game search", Priority = ThreadPriority.Lowest };
        t.Start();
        return true;
    }

    public sealed class ScanResult
    {
        public List<FoundGame> Games = new();
        public int Dirs;
        public double Seconds;
        public string Note = "";
        /// <summary>Сколько папок просмотрено на каждом диске (для самопроверки).</summary>
        public string PerDrive = "";
    }

    /// <summary>
    /// Полный поиск: записи «Установки и удаления программ», затем папки всех несъёмных дисков. Только чтение.
    /// cancel — прервать (началась игра). Вызывать в фоновом потоке.
    /// </summary>
    public static ScanResult ScanAll(Func<bool> cancel = null)
    {
        var sw = Stopwatch.StartNew();
        var scanner = new Scanner(cancel ?? (() => false));
        foreach (string dir in GameLibrary.StoreFolders()) scanner.Skip(dir);
        List<string> rejected;
        lock (Gate) rejected = notGames.ToList();
        foreach (string n in rejected) scanner.Skip(n);

        // 1. Записи «Установки и удаления программ»: у них есть название и папка, где бы игра ни стояла.
        foreach (var u in UninstallCandidates())
        {
            scanner.Check();
            if (scanner.Covered(u.Dir)) continue;
            var det = scanner.ScanTree(u.Dir, maxDepth: 3, maxDirs: 300);
            string exe = det?.Exe;
            if (exe == null && u.Store != "") exe = scanner.LargestExe(u.Dir);       // у известного магазина — и без признаков движка
            if (exe == null) continue;
            scanner.AddFound(new FoundGame
            {
                Title = u.Title, Dir = u.Dir, Exe = exe, Engine = det?.Engine ?? "", Source = u.Store != "" ? "store" : "disk", Store = u.Store,
                Found = DateTime.Now,
            });
        }

        // 2. Диски.
        var notes = new List<string>();
        var perDrive = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                int before = scanner.Dirs;
                scanner.ScanDrive(drive.RootDirectory.FullName);
                perDrive.Add(drive.Name.TrimEnd('\\') + " " + (scanner.Dirs - before));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { notes.Add(drive.Name + ": " + ex.Message); }
        }
        if (scanner.Dirs >= Scanner.MaxDirsTotal) notes.Add(L.F("обход остановлен на {0} папках", Scanner.MaxDirsTotal));
        return new ScanResult { Games = scanner.Found, Dirs = scanner.Dirs, Seconds = sw.Elapsed.TotalSeconds, Note = string.Join("; ", notes), PerDrive = string.Join(", ", perDrive) };
    }

    private sealed record UninstallCandidate(string Title, string Dir, string Store);

    // Издатели магазинов-лаунчеров: их игры принимаются и без признаков движка (папка игры известна из записи магазина).
    private static string StoreOf(string publisher, string uninstall, string title)
    {
        string p = (publisher ?? "") + " " + (uninstall ?? "");
        if (Has(p, "Electronic Arts") || Has(p, "EAInstaller") || Has(p, "EA Desktop")) return "EA app";
        if (Has(p, "Blizzard") || Has(p, "Battle.net")) return "Battle.net";
        if (Has(p, "Rockstar Games")) return "Rockstar Games Launcher";
        if (Has(p, "Wargaming") || Has(p, "wgc.exe")) return "Wargaming Game Center";
        if (Has(p, "Lesta") || Has(p, "Леста") || Has(p, "lgc.exe")) return "Lesta Game Center";
        if (Has(p, "Ubisoft")) return "Ubisoft Connect";
        if (Has(p, "MY.GAMES") || Has(p, "VK Play") || Has(p, "vkplay")) return "VK Play";
        return "";
    }

    private static bool Has(string s, string part) => s?.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

    // Сами лаунчеры, службы и инструменты — не игры, даже если издатель игровой.
    private static readonly string[] NotGameTitleParts =
    {
        "Launcher", "Social Club", "Game Center", "Ubisoft Connect", "SDK", "Redistributable", "Runtime", "Driver", "Драйвер", "Editor",
        "Dedicated Server", "Unreal Engine", "Booster", "Overlay", "Anti-Cheat", "AntiCheat", "Vanguard", "EasyAntiCheat", "BattlEye",
        "Epic Games", "Epic Online", "GOG Galaxy", "Wallpaper Engine", "Lossless Scaling", "VTube Studio", "Soundpad", "Overwolf", "Parsec",
        "Moonlight", "GeForce", "NVIDIA", "Radeon", "Afterburner", "RivaTuner", "3DMark", "Benchmark", "Unity Hub", "Blender", "Godot",
        "Пульс ПК", "Pulse PC",
    };
    private static readonly string[] NotGameTitlesExact = { "Steam", "Battle.net", "EA app", "EA", "Origin", "Medal", "Sunshine", "Rockstar Games Launcher" };

    private static bool NotGameTitle(string title) =>
        NotGameTitlesExact.Contains(title, StringComparer.OrdinalIgnoreCase) || NotGameTitleParts.Any(t => title.Contains(t, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<UninstallCandidate> UninstallCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hiveKind, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Registry64) })
        {
            List<UninstallCandidate> batch = new();
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hiveKind, view);
                using var uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall == null) continue;
                foreach (string name in uninstall.GetSubKeyNames())
                {
                    // Эти записи уже читают магазины (Steam, Riot, Astrum/VK Play, Ubisoft).
                    if (name.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Riot Game ", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("gcgame_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Uplay Install ", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        using var k = uninstall.OpenSubKey(name);
                        if (k == null) continue;
                        string title = (k.GetValue("DisplayName") as string)?.Trim();
                        if (string.IsNullOrEmpty(title) || k.GetValue("SystemComponent") is int sc && sc == 1 || k.GetValue("ParentKeyName") is string { Length: > 0 }) continue;
                        if (NotGameTitle(title)) continue;
                        string dir = GameLibrary.Norm(k.GetValue("InstallLocation") as string);
                        if (dir == null || !Directory.Exists(dir) || !CustomGame.FolderUsable(dir) || IsSystemPath(dir)) continue;
                        if (!seen.Add(dir)) continue;
                        batch.Add(new UninstallCandidate(title, dir, StoreOf(k.GetValue("Publisher") as string, k.GetValue("UninstallString") as string, title)));
                    }
                    catch { }
                }
            }
            catch { }
            foreach (var c in batch) yield return c;
        }

        // Rockstar Games Launcher пишет игры ещё и в HKLM\SOFTWARE\Rockstar Games\<игра>\InstallFolder.
        foreach (string root in new[] { @"SOFTWARE\WOW6432Node\Rockstar Games", @"SOFTWARE\Rockstar Games" })
        {
            List<UninstallCandidate> batch = new();
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(root);
                if (key == null) continue;
                foreach (string name in key.GetSubKeyNames())
                {
                    if (NotGameTitle(name)) continue;
                    using var k = key.OpenSubKey(name);
                    string dir = GameLibrary.Norm(k?.GetValue("InstallFolder") as string);
                    if (dir == null || !Directory.Exists(dir) || !seen.Add(dir)) continue;
                    batch.Add(new UninstallCandidate(name, dir, "Rockstar Games Launcher"));
                }
            }
            catch { }
            foreach (var c in batch) yield return c;
        }
    }

    private static bool IsSystemPath(string dir)
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return dir.StartsWith(win, StringComparison.OrdinalIgnoreCase)
               || dir.Contains(@"\WindowsApps", StringComparison.OrdinalIgnoreCase)
               || dir.Contains(@"\Microsoft\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Главный файл игры в папке (по признакам движка, иначе самый крупный .exe, кроме служебных). null — нет.</summary>
    public static string MainExe(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        var s = new Scanner(() => false);
        return s.ScanTree(dir, maxDepth: 3, maxDirs: 200)?.Exe ?? s.LargestExe(dir);
    }

    // ------------------------------------------------------------------ признаки игры
    /// <summary>Служебный .exe: установщик, деинсталлятор, отчёт о сбое, античит, редактор, лаунчер и т. п.</summary>
    public static bool JunkExe(string file)
    {
        string n = Path.GetFileNameWithoutExtension(file ?? "").ToLowerInvariant();
        if (n.Length == 0) return true;
        string[] parts =
        {
            "unins", "setup", "install", "redist", "vcredist", "vc_redist", "dxsetup", "dxwebsetup", "crash", "bugreport", "bugtrap", "report",
            "updater", "update", "patcher", "prereq", "easyanticheat", "battleye", "beservice", "cleanup", "touchup", "activation", "register",
            "helper", "cefprocess", "webhelper", "dotnet", "vulkan", "dxdiag", "notification", "overlay", "server", "editor", "modkit", "sdk",
            "benchmark", "config", "settings", "launcher", "repair", "elevat", "createdump", "uninstall", "dxwnd", "7z", "winrar", "unitycrash",
            "start_protected", "service", "diagnostic", "feedback", "remover", "zfgamebrowser", "quicksfv", "oalinst", "physx_", "directx",
        };
        if (parts.Any(n.Contains)) return true;
        if (n.EndsWith("_be") || n.EndsWith("_eac") || n.StartsWith("eac") || n.StartsWith("crs-") || n == "unity" || n.StartsWith("ue4prereq")) return true;
        return false;
    }

    private static readonly string[] Inner =
        { "bin", "bin64", "bin32", "binaries", "win64", "win32", "x64", "x86", "x86_64", "amd64", "retail", "_retail_", "_classic_", "release", "shipping", "client", "game" };

    private static readonly string[] InnerPaths =
    {
        "bin", "bin64", @"bin\x64", @"bin\win64", @"bin\win_x64", @"bin\x86", "x64", "win64", @"binaries\win64", @"game\bin\win64", @"game\bin", "retail", "_retail_", "client", "game",
    };

    /// <summary>Папки, в которые обход не заходит: система, программы Microsoft и драйверов, служебные и разработческие.</summary>
    private static bool SkipName(string name)
    {
        if (name.Length == 0 || name[0] == '$' || name[0] == '.') return true;
        string[] exact =
        {
            "Windows", "System Volume Information", "Recovery", "PerfLogs", "ProgramData", "Users", "Documents and Settings", "Boot", "Config.Msi",
            "WindowsApps", "Common Files", "ModifiableWindowsApps", "Reference Assemblies", "dotnet", "MSBuild", "Package Cache", "Installer",
            "Uninstall Information", "WindowsPowerShell", "Internet Explorer", "Git", "nodejs", "Java", "Docker", "Oracle", "VirtualBox", "VMware",
            "node_modules", "site-packages", "__pycache__", "venv", "Intel", "AMD", "Realtek", "ATI Technologies", "Hyper-V", "IIS", "IIS Express",
            "steamapps", "XboxGames", "Riot Games", "Epic Games", "WpSystem", "OneDriveTemp", "inetpub", "MSOCache", "Drivers", "Intermediate",
            "DerivedDataCache", "Saved", "Logs", "Cache", "Temp", "tmp", "obj", "Source", "Plugins", "Mods", "mods", "Screenshots", "Videos", "Music",
            "Photos", "Pictures", "Documents", "Downloads", "Adobe", "JetBrains", "Android", "Python", "Steam", "Ubisoft", "Origin", "EA Desktop",
            "Battle.net", "Rockstar Games Launcher", "Wargaming.net", "GOG Galaxy", "AstrumPlay", "itch",
        };
        if (exact.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        string[] prefixes = { "Microsoft", "Windows ", "Windows.", "NVIDIA", "Python", "Autodesk", "Adobe", "MSI", "ASUS", "Razer", "Logitech" };
        return prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class Listing
    {
        public string Dir;
        public Dictionary<string, long> Files = new(StringComparer.OrdinalIgnoreCase);    // имя → размер
        public List<string> Dirs = new();                                                 // имена подпапок
        public HashSet<string> DirSet = new(StringComparer.OrdinalIgnoreCase);
        public bool HasFile(string n) => Files.ContainsKey(n);
        public bool Any(Func<string, bool> f) => Files.Keys.Any(f);
    }

    private sealed class Detection
    {
        public string Root, Exe, Engine;
        public int Points;
    }

    private sealed class Scanner
    {
        public const int MaxDirsTotal = 60000;
        /// <summary>Меньше — не программа (пустая заглушка недокачанной игры, ярлык).</summary>
        public const long MinExe = 16 * 1024;
        private readonly Func<bool> cancel;
        private readonly List<string> skip = new();
        public readonly List<FoundGame> Found = new();
        public int Dirs;

        public Scanner(Func<bool> cancel) => this.cancel = cancel;

        public void Skip(string dir) { var d = GameLibrary.Norm(dir); if (d != null) skip.Add(d); }

        public void Check() { if (cancel()) throw new OperationCanceledException(); }

        public bool Covered(string dir)
        {
            foreach (string s in skip) if (Inside(dir, s)) return true;
            foreach (var f in Found) if (Inside(dir, f.Dir)) return true;
            return false;
        }

        private static bool Inside(string dir, string root) =>
            string.Equals(dir, root, StringComparison.OrdinalIgnoreCase) || dir.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

        public void AddFound(FoundGame g)
        {
            // Папка, внутри которой уже найдена игра (или наоборот), — одна игра.
            if (Found.Any(f => Inside(g.Dir, f.Dir) || Inside(f.Dir, g.Dir))) return;
            Found.Add(g);
        }

        private Listing List(string dir)
        {
            Check();
            Dirs++;
            if (Dirs % 16 == 0) Thread.Sleep(3);       // без заметной нагрузки на диск и процессор
            var l = new Listing { Dir = dir };
            try
            {
                var opt = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false };
                int n = 0;
                foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opt))
                {
                    if (++n > 4000) break;
                    if (e is FileInfo f) l.Files[f.Name] = f.Length;
                    else { l.Dirs.Add(e.Name); l.DirSet.Add(e.Name); }
                }
            }
            catch { return null; }
            return l;
        }

        /// <summary>Проект разработки (Unreal, Godot, Unity, репозиторий) — не установленная игра.</summary>
        private static bool DevProject(Listing l) =>
            l.Any(f => f.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            || l.HasFile("project.godot") || l.DirSet.Contains(".git") || l.DirSet.Contains(".godot")
            || (l.DirSet.Contains("Assets") && l.DirSet.Contains("ProjectSettings"));

        /// <summary>Папка самого лаунчера (Steam, Epic, GOG Galaxy, EA app…): его игры знает магазин.</summary>
        private static bool LauncherFolder(Listing l) =>
            new[] { "steam.exe", "EpicGamesLauncher.exe", "GalaxyClient.exe", "UbisoftConnect.exe", "upc.exe", "EADesktop.exe", "Origin.exe", "Battle.net.exe",
                    "RiotClientServices.exe", "wgc.exe", "lgc.exe", "AstrumPlay.exe", "itch.exe", "Playnite.DesktopApp.exe", "PulsePC.exe" }.Any(l.HasFile);

        public void ScanDrive(string root)
        {
            var queue = new Queue<(string Dir, int Depth)>();
            queue.Enqueue((root.TrimEnd('\\') + "\\", 0));
            while (queue.Count > 0 && Dirs < MaxDirsTotal)
            {
                var (dir, depth) = queue.Dequeue();
                if (depth > 0 && Covered(dir.TrimEnd('\\'))) continue;
                var l = List(dir);
                if (l == null) continue;
                if (depth > 0 && (DevProject(l) || LauncherFolder(l))) continue;
                if (depth > 0)
                {
                    var det = Detect(dir.TrimEnd('\\'), l);
                    if (det != null) { AddDetection(det); continue; }
                }
                if (depth >= 6) continue;
                foreach (string sub in l.Dirs)
                    if (!SkipName(sub)) queue.Enqueue((Path.Combine(dir, sub), depth + 1));
            }
        }

        private void AddDetection(Detection d)
        {
            string root = d.Root;
            if (root == null || Covered(root)) return;
            AddFound(new FoundGame
            {
                Title = GuessTitle(d.Exe, root), Dir = root, Exe = d.Exe, Engine = d.Engine ?? "", Source = "disk", Found = DateTime.Now,
            });
        }

        /// <summary>Поиск игры внутри одной папки (папка установки из записи программы): первая найденная.</summary>
        public Detection ScanTree(string top, int maxDepth, int maxDirs)
        {
            var queue = new Queue<(string Dir, int Depth)>();
            queue.Enqueue((top, 0));
            int budget = maxDirs;
            while (queue.Count > 0 && budget-- > 0)
            {
                var (dir, depth) = queue.Dequeue();
                var l = List(dir);
                if (l == null) continue;
                if (DevProject(l) || (depth == 0 && LauncherFolder(l))) return null;
                var det = Detect(dir, l);
                if (det != null) { det.Root = top; return det; }
                if (depth >= maxDepth) continue;
                foreach (string sub in l.Dirs) if (!SkipName(sub)) queue.Enqueue((Path.Combine(dir, sub), depth + 1));
            }
            return null;
        }

        /// <summary>Самый крупный не служебный .exe в папке и на два уровня ниже.</summary>
        public string LargestExe(string top)
        {
            string best = null; long bestSize = 0;
            var queue = new Queue<(string, int)>();
            queue.Enqueue((top, 0));
            int budget = 60;
            while (queue.Count > 0 && budget-- > 0)
            {
                var (dir, depth) = queue.Dequeue();
                var l = List(dir);
                if (l == null) continue;
                foreach (var (f, size) in l.Files)
                    if (f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !JunkExe(f) && size >= MinExe && size > bestSize) { best = Path.Combine(dir, f); bestSize = size; }
                if (depth < 2) foreach (string sub in l.Dirs) if (!SkipName(sub)) queue.Enqueue((Path.Combine(dir, sub), depth + 1));
            }
            return best;
        }

        /// <summary>Игра в этой папке: признаки в ней самой, корень игры Unreal (Engine + &lt;Проект&gt;\Binaries\Win64) или типичные bin/x64.</summary>
        private Detection Detect(string dir, Listing l)
        {
            var d = Score(dir, l);
            if (d != null) { d.Root = ClimbInner(dir); return d; }

            if (l.DirSet.Contains("Engine"))
                foreach (string child in l.Dirs)
                {
                    if (child.Equals("Engine", StringComparison.OrdinalIgnoreCase)) continue;
                    string win64 = Path.Combine(dir, child, "Binaries", "Win64");
                    if (!Directory.Exists(win64)) continue;
                    var wl = List(win64);
                    var wd = wl == null ? null : Score(win64, wl);
                    if (wd == null) continue;
                    // Корневой «загрузчик» Unreal (<Игра>.exe рядом с Engine) запускает игру правильно — его и берём для запуска.
                    string boot = l.Files.Keys.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !JunkExe(f) && l.Files[f] >= MinExe)
                                   .OrderByDescending(f => l.Files[f]).FirstOrDefault();
                    return new Detection { Root = dir, Exe = boot != null ? Path.Combine(dir, boot) : wd.Exe, Engine = "Unreal Engine", Points = wd.Points };
                }

            foreach (string rel in InnerPaths)
            {
                string p = Path.Combine(dir, rel);
                if (!l.DirSet.Contains(rel.Split('\\')[0]) || !Directory.Exists(p)) continue;
                var il = List(p);
                var id = il == null ? null : Score(p, il);
                if (id != null) { id.Root = dir; return id; }
            }
            return null;
        }

        private static bool DriveRoot(string p) => p == null || string.Equals(Path.GetPathRoot(p)?.TrimEnd('\\'), p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        /// <summary>Из «…\Игра\bin\x64» — «…\Игра»: подниматься через типичные внутренние папки, но не до корня диска.</summary>
        private static string ClimbInner(string dir)
        {
            string cur = dir.TrimEnd('\\');
            for (int i = 0; i < 4; i++)
            {
                string parent = Path.GetDirectoryName(cur);
                if (DriveRoot(parent) || !Inner.Contains(Path.GetFileName(cur), StringComparer.OrdinalIgnoreCase)) break;
                cur = parent;
            }
            // Unreal: <Игра>\<Проект>\Binaries\Win64 → <Игра>, если рядом с проектом лежит Engine.
            string up = Path.GetDirectoryName(cur);
            if (!DriveRoot(up) && Directory.Exists(Path.Combine(up, "Engine")) && Directory.Exists(Path.Combine(cur, "Binaries"))) cur = up;
            return cur;
        }

        /// <summary>Признаки игры среди файлов одной папки: сильные — по 2, слабые — по 1 (не больше 2 в сумме). Игра — от 2 и есть не служебный .exe.</summary>
        private static Detection Score(string dir, Listing l)
        {
            var exes = l.Files.Keys.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !JunkExe(f) && l.Files[f] >= MinExe).ToList();
            if (exes.Count == 0) return null;
            int strong = 0, weak = 0;
            string engine = "", pick = null;
            string Stem(string f) => Path.GetFileNameWithoutExtension(f);

            var unityExe = exes.FirstOrDefault(e => l.DirSet.Contains(Stem(e) + "_Data"));
            if (l.HasFile("UnityPlayer.dll") && unityExe != null) { strong += 2; engine = "Unity"; pick = unityExe; }
            else if (unityExe != null && l.HasFile("GameAssembly.dll")) { strong += 2; engine = "Unity"; pick = unityExe; }
            if (l.HasFile("GameAssembly.dll")) strong += 2;
            var shipping = exes.FirstOrDefault(e => e.EndsWith("-Shipping.exe", StringComparison.OrdinalIgnoreCase));
            if (shipping != null || (Path.GetFileName(dir).Equals("Win64", StringComparison.OrdinalIgnoreCase)
                                     && Path.GetFileName(Path.GetDirectoryName(dir) ?? "").Equals("Binaries", StringComparison.OrdinalIgnoreCase)
                                     && exes.Count > 0 && Directory.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(dir)) ?? "", "Content"))))
            { strong += 2; if (engine == "") engine = "Unreal Engine"; pick ??= shipping; }
            var pck = exes.FirstOrDefault(e => l.HasFile(Stem(e) + ".pck"));
            if (pck != null) { strong += 2; engine = "Godot"; pick ??= pck; }
            if (l.DirSet.Contains("renpy") && l.DirSet.Contains("game")) { strong += 2; engine = "Ren'Py"; }
            if (l.HasFile("data.win")) { strong += 2; engine = "GameMaker"; }
            if (l.DirSet.Contains("www") && l.HasFile("nw.dll") && Directory.Exists(Path.Combine(dir, "www", "js"))) { strong += 2; engine = "RPG Maker"; }
            if (l.Any(f => f.EndsWith(".rgss3a", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".rgssad", StringComparison.OrdinalIgnoreCase)
                           || f.StartsWith("RGSS", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))) { strong += 2; engine = "RPG Maker"; }
            if (l.HasFile("steam_api.dll") || l.HasFile("steam_api64.dll")) strong += 2;
            if (new[] { "nvngx_dlss.dll", "sl.interposer.dll", "libxess.dll", "amd_fidelityfx_dx12.dll", "ffx_fsr2_api_x64.dll" }.Any(l.HasFile)) strong += 2;
            if (l.HasFile("Galaxy.dll") || l.HasFile("Galaxy64.dll")) strong += 2;
            if (l.Any(f => f.StartsWith("EOSSDK-Win", StringComparison.OrdinalIgnoreCase))) strong += 2;
            if (l.HasFile("discord_game_sdk.dll")) strong += 2;
            if (l.HasFile("love.dll")) { strong += 2; engine = "LÖVE"; }
            if (l.HasFile("MonoGame.Framework.dll") || l.HasFile("FNA.dll") || l.Any(f => f.StartsWith("Microsoft.Xna.Framework", StringComparison.OrdinalIgnoreCase))) { strong += 2; engine = "XNA / MonoGame"; }
            if (l.HasFile("CrySystem.dll")) { strong += 2; engine = "CryEngine"; }
            if (l.DirSet.Contains("EasyAntiCheat") || l.DirSet.Contains("BattlEye")) strong += 2;

            if (l.Any(f => f.StartsWith("bink", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))) weak++;
            if (l.Any(f => f.StartsWith("fmod", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))) weak++;
            if (l.Any(f => f.StartsWith("AkSoundEngine", StringComparison.OrdinalIgnoreCase) || f.StartsWith("Wwise", StringComparison.OrdinalIgnoreCase))) weak++;
            if (l.Any(f => (f.StartsWith("PhysX", StringComparison.OrdinalIgnoreCase) || f.StartsWith("APEX_", StringComparison.OrdinalIgnoreCase)) && f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))) weak++;
            if (l.Any(f => f.StartsWith("d3dx9_", StringComparison.OrdinalIgnoreCase) || f.StartsWith("d3dx11_", StringComparison.OrdinalIgnoreCase)
                           || f.Equals("xinput1_3.dll", StringComparison.OrdinalIgnoreCase) || f.Equals("XAudio2_7.dll", StringComparison.OrdinalIgnoreCase))) weak++;
            if (l.HasFile("OpenAL32.dll") || l.HasFile("SDL2.dll") || l.HasFile("SDL3.dll")) weak++;
            if (l.Any(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".bsa", StringComparison.OrdinalIgnoreCase)
                           || f.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".forge", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)
                           || f.EndsWith(".pk3", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".wad", StringComparison.OrdinalIgnoreCase))) weak++;

            int points = strong + Math.Min(weak, 2);
            if (points < 2) return null;
            pick ??= exes.OrderByDescending(e => l.Files[e]).First();
            return new Detection { Root = dir, Exe = Path.Combine(dir, pick), Engine = engine, Points = points };
        }
    }

    // Названия продуктов, которые дают движки, а не игры.
    private static readonly string[] GenericNames =
    {
        "BootstrapPackagedGame", "UnrealGame", "UE4Game", "UE4", "UE5", "Unreal Engine", "Unity", "Unity Player", "Godot Engine", "Ren'Py", "Game", "Launcher",
        "NW.js", "Electron", "Chromium", "GameMaker", "RPG Maker", "Steam", "Shipping", "Application", "Epic Games", "LÖVE", "MonoGame",
    };

    /// <summary>Название игры: «Название продукта» из свойств .exe, если это не имя движка, иначе имя папки игры.</summary>
    public static string GuessTitle(string exe, string root)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            string t = info.ProductName?.Trim();
            if (!string.IsNullOrEmpty(t) && t.Length <= 60 && !GenericNames.Any(g => t.Equals(g, StringComparison.OrdinalIgnoreCase))
                && !t.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) && !t.Contains("Unreal", StringComparison.OrdinalIgnoreCase)
                && !t.Contains("Engine", StringComparison.OrdinalIgnoreCase)) return t;
        }
        catch { }
        string name = Path.GetFileName((root ?? "").TrimEnd('\\'));
        return string.IsNullOrEmpty(name) ? Path.GetFileNameWithoutExtension(exe) : name.Replace('_', ' ');
    }

    // ================================================================== по поведению
    /// <summary>
    /// Процессы, которые рисуют на весь экран, но не игры: браузеры, плееры, общение и запись, лаунчеры, среды разработки,
    /// графические программы, удалённый доступ, сам «Пульс ПК» и Claude.
    /// </summary>
    private static readonly HashSet<string> NotGameProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "opera", "opera_gx", "browser", "brave", "vivaldi", "iexplore", "arc", "thorium", "chromium", "waterfox", "librewolf",
        "tor", "msedgewebview2", "yandex", "vlc", "mpc-hc", "mpc-hc64", "mpc-be", "mpc-be64", "potplayer", "potplayermini", "potplayermini64", "kmplayer",
        "wmplayer", "video.ui", "microsoft.media.player", "mpv", "smplayer", "gom", "aimp", "foobar2000", "spotify", "discord", "discordptb", "discordcanary",
        "obs64", "obs32", "obs", "streamlabs obs", "telegram", "teams", "ms-teams", "zoom", "skype", "whatsapp", "slack", "steam", "steamwebhelper",
        "epicgameslauncher", "galaxyclient", "upc", "ubisoftconnect", "eadesktop", "origin", "battle.net", "riotclientservices", "riotclientux", "astrumplay",
        "gamecenter", "vkplay", "wgc", "lgc", "itch", "rockstarservice", "playnite.desktopapp", "playnite.fullscreenapp", "xboxpcapp", "gamingservices",
        "devenv", "code", "cursor", "rider64", "idea64", "pycharm64", "clion64", "webstorm64", "studio64", "blender", "unity", "unity hub", "unrealeditor",
        "ue4editor", "sublime_text", "notepad++", "windowsterminal", "wt", "powershell", "pwsh", "cmd", "conhost", "pulsepc", "claude", "explorer",
        "applicationframehost", "textinputhost", "searchhost", "startmenuexperiencehost", "shellexperiencehost", "lockapp", "dwm", "logonui", "mstsc", "msrdc",
        "vmware", "vmware-vmx", "virtualbox", "virtualboxvm", "vmconnect", "parsecd", "moonlight", "sunshine", "nvidia share", "nvcontainer", "nvidia overlay",
        "powerpnt", "winword", "excel", "acrord32", "acrobat", "sumatrapdf", "photos", "microsoft.photos", "photoshop", "afterfx", "adobe premiere pro",
        "resolve", "3dsmax", "maya", "cinema 4d", "houdini", "zbrush", "substance painter", "davinci resolve", "screenclippinghost", "snippingtool",
        "wallpaper32", "wallpaper64", "lively", "losslessscaling", "medal", "overwolf", "nvidia app", "amdrsserv", "radeonsoftware",
        // Удалённый доступ на весь экран (картинку чужого экрана рисует он), медиацентры и презентации LibreOffice.
        "anydesk", "teamviewer", "rustdesk", "kodi", "plex", "plex htpc", "jellyfin media player", "stremio", "mpc-qt", "soffice",
        "floorp", "zen", "atom", "maxthon", "palemoon", "whale",
    };

    /// <summary>Причина, по которой процесс не может быть игрой по поведению, или null.</summary>
    public static string NotGameReason(string path)
    {
        if (string.IsNullOrEmpty(path)) return "нет пути";
        string name = Path.GetFileNameWithoutExtension(path);
        if (NotGameProcesses.Contains(name)) return "в списке «не игр»";
        string lower = name.ToLowerInvariant();
        if (lower.Contains("launcher") || lower.StartsWith("godot") || lower.Contains("editor") || path.EndsWith(".scr", StringComparison.OrdinalIgnoreCase)) return "лаунчер или редактор";
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (path.StartsWith(win, StringComparison.OrdinalIgnoreCase)) return "файл Windows";
        if (path.Contains(@"\WindowsApps\Microsoft.", StringComparison.OrdinalIgnoreCase)) return "приложение Windows";
        lock (Gate) if (IsRejected(path)) return "отмечено «Это не игра»";
        return null;
    }

    /// <summary>
    /// Учёт «кандидата»: один и тот же процесс подряд на весь экран и с загрузкой видеокарты. Перерывы до 15 с (свернули на миг)
    /// не сбрасывают счёт; другой процесс — начинает заново. Игра — от 60 с такой работы.
    /// </summary>
    public sealed class BehaviorTracker
    {
        public const double NeedSeconds = 60, GapSeconds = 15;
        private int pid;
        private string path;
        private double seconds;
        private DateTime lastBusy = DateTime.MinValue;

        /// <summary>Шаг наблюдения. Возвращает путь игры, когда набралось 60 с, иначе null.</summary>
        public string Step(int procId, string procPath, bool fullscreen, bool gpuBusy, DateTime now, double stepSeconds)
        {
            if (!fullscreen || procId == 0)
            {
                if (pid != 0 && (now - lastBusy).TotalSeconds > GapSeconds) Reset();
                return null;
            }
            if (procId != pid) { Reset(); pid = procId; path = procPath; }
            if (!gpuBusy)
            {
                if ((now - lastBusy).TotalSeconds > GapSeconds) seconds = 0;
                return null;
            }
            seconds += stepSeconds;
            lastBusy = now;
            if (seconds < NeedSeconds) return null;
            string found = path;
            Reset();
            return found;
        }

        public void Reset() { pid = 0; path = null; seconds = 0; lastBusy = DateTime.MinValue; }
        public double Seconds => seconds;
    }

    private static void BehaviorLoop()
    {
        var tracker = new BehaviorTracker();
        var prev = new Dictionary<string, CounterSample>();
        const int step = 5;
        while (true)
        {
            Thread.Sleep(step * 1000);
            try
            {
                if (!BehaviorOn || GameHooks.InGame || host?.Live().Recording == true) { tracker.Reset(); prev.Clear(); continue; }
                var fg = ForegroundFullscreen();
                if (fg.Pid == 0) { tracker.Step(0, null, false, false, DateTime.Now, step); prev.Clear(); continue; }
                string path = ProcessPath(fg.Pid);
                if (NotGameReason(path) != null || GameDetector.LooksLikeGame(path)) { tracker.Reset(); prev.Clear(); continue; }
                // Игра с античитом уровня ядра (осторожный режим): «GPU Engine» по её процессу не опрашивается — только общая загрузка.
                double load = AntiCheat.KernelByFolder(path) ? double.NaN : ProcessGpuLoad(fg.Pid, prev);
                // Нет счётчиков по процессам — по общей загрузке видеокарты (строже: её могли дать и другие программы).
                bool busy = double.IsNaN(load) ? (host?.Live().GpuLoad ?? double.NaN) >= 50 : load >= 15;
                string found = tracker.Step(fg.Pid, path, true, busy, DateTime.Now, step);
                if (found != null) AddBehavior(found);
            }
            catch (Exception ex) { Log.Error("игры по поведению", ex); Thread.Sleep(30000); }
        }
    }

    private static void AddBehavior(string exe)
    {
        EnsureLoaded();
        string title = CustomGame.GuessName(exe);
        lock (Gate)
        {
            if (behavior.Any(b => string.Equals(b.Exe, exe, StringComparison.OrdinalIgnoreCase))) return;
            // Название занято другой игрой — уточнить папкой, чтобы сессии не смешались.
            if (behavior.Any(b => string.Equals(b.Title, title, StringComparison.OrdinalIgnoreCase)))
                title += " (" + Path.GetFileName(Path.GetDirectoryName(exe)) + ")";
            behavior.Add(new FoundGame { Title = title, Exe = exe, Dir = Path.GetDirectoryName(exe), Source = "behavior", Found = DateTime.Now });
            settings.Set(KBehavior, JsonSerializer.Serialize(behavior));
        }
        settings.Save();
        Log.Info("игра найдена по поведению: " + exe);
        Rebuild();
        Raise();
        host?.Notify(L.T("Найдена новая игра"),
            L.F("«{0}» больше минуты работала на весь экран и нагружала видеокарту — теперь она записывается как игра. Если это не игра, отметьте это на вкладке «Сессии».", title),
            false, () => host.Open("tab:Сессии"));
    }

    // Загрузка видеокарты процессом по счётчикам Windows «GPU Engine» (двигатели 3D и вычислений), %; NaN — счётчиков нет или первая выборка.
    private static double ProcessGpuLoad(int pid, Dictionary<string, CounterSample> prev)
    {
        InstanceDataCollection engines;
        try
        {
            var all = new PerformanceCounterCategory("GPU Engine").ReadCategory();
            engines = all.Contains("Utilization Percentage") ? all["Utilization Percentage"] : null;
        }
        catch { return double.NaN; }
        if (engines == null) return double.NaN;
        string prefix = $"pid_{pid}_";
        var next = new Dictionary<string, CounterSample>();
        var byEngine = new Dictionary<string, double>();
        bool any = false;
        foreach (InstanceData d in engines.Values)
        {
            if (!d.InstanceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var e = GpuVendors.ParseEngine(d.InstanceName);
            if (!GpuVendors.IsLoadEngine(e.Type)) continue;
            next[d.InstanceName] = d.Sample;
            if (!prev.TryGetValue(d.InstanceName, out var p)) continue;
            try
            {
                double v = CounterSample.Calculate(p, d.Sample);
                string key = e.Luid + "#" + e.Engine;
                byEngine[key] = byEngine.GetValueOrDefault(key) + v;
                any = true;
            }
            catch { }
        }
        prev.Clear();
        foreach (var kv in next) prev[kv.Key] = kv.Value;
        return any ? Math.Min(100, byEngine.Values.DefaultIfEmpty(0).Max()) : double.NaN;
    }

    /// <summary>Окно на переднем плане закрывает весь монитор и без заголовка (полноэкранный или безрамочный режим): номер процесса или 0.</summary>
    private static (int Pid, IntPtr Hwnd) ForegroundFullscreen()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return (0, IntPtr.Zero);
        var cls = new StringBuilder(64);
        GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return (0, IntPtr.Zero);
        if (!GetWindowRect(hwnd, out var r)) return (0, IntPtr.Zero);
        var mi = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref mi)) return (0, IntPtr.Zero);
        long style = GetWindowLongPtr(hwnd, -16).ToInt64();
        if (!CoversMonitor(r, mi.Monitor, style)) return (0, IntPtr.Zero);
        GetWindowThreadProcessId(hwnd, out int pid);
        return (pid, hwnd);
    }

    /// <summary>Окно закрывает монитор целиком и у него нет заголовка (WS_CAPTION): так выглядят полноэкранные и безрамочные игры.</summary>
    internal static bool CoversMonitor(Rect w, Rect m, long style)
    {
        const long WsCaption = 0x00C00000;
        bool covers = w.Left <= m.Left && w.Top <= m.Top && w.Right >= m.Right && w.Bottom >= m.Bottom;
        return covers && (style & WsCaption) != WsCaption;
    }

    /// <summary>Файл процесса с правом PROCESS_QUERY_LIMITED_INFORMATION — так можно и для игр под защитой античита (в отличие от MainModule).</summary>
    internal static string ProcessPath(int pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr m, ref MonitorInfo mi);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int max);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern bool SetThreadPriority(IntPtr thread, int priority);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetPrivateProfileString(string section, string key, string def, StringBuilder ret, uint size, string file);
    private const int ThreadModeBackgroundBegin = 0x00010000, ThreadModeBackgroundEnd = 0x00020000;

    // ================================================================== самопроверка
    /// <summary>
    /// Самопроверка: признаки игр на подставном дереве папок во временной папке (создаётся и удаляется), служебные файлы, «не игры»,
    /// учёт по поведению, код издателя пакета Xbox, разбор .GamingRoot и MicrosoftGame.config, ссылка .url для длинного адреса.
    /// Затем — что видно на этом ПК (только чтение): игры магазинов, записи программ и полный обход дисков, без записи кэша.
    /// </summary>
    public static string SelfTest()
    {
        var fails = new List<string>();
        var lines = new List<string>();
        void Check(string what, bool ok) { if (!ok) fails.Add(what); }

        // --- подставное дерево
        string tmp = Path.Combine(Path.GetTempPath(), "PulsePC-gf-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            // У .exe — настоящий размер (от 20 КБ): пустые и крошечные файлы игрой не считаются.
            void F(string rel, int size = 16)
            {
                string p = Path.Combine(tmp, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllBytes(p, new byte[rel.EndsWith(".exe") ? 20000 + size : size]);
            }
            void D(string rel) => Directory.CreateDirectory(Path.Combine(tmp, rel));
            F(@"Games\UnityGame\UnityGame.exe", 800); F(@"Games\UnityGame\UnityPlayer.dll"); D(@"Games\UnityGame\UnityGame_Data");
            F(@"Games\UeGame\UeGame.exe", 300); D(@"Games\UeGame\Engine\Binaries"); F(@"Games\UeGame\UeGame\Binaries\Win64\UeGame-Win64-Shipping.exe", 9000);
            F(@"Games\UeGame\UeGame\Content\Paks\UeGame-Windows.pak");
            F(@"Games\GodotGame\gg.exe", 500); F(@"Games\GodotGame\gg.pck");
            F(@"Games\OnlySetup\setup.exe"); F(@"Games\OnlySetup\steam_api.dll");
            F(@"Games\BinkOnly\movie.exe"); F(@"Games\BinkOnly\bink2w64.dll");
            F(@"Games\BinkFmod\bf.exe", 700); F(@"Games\BinkFmod\bink2w64.dll"); F(@"Games\BinkFmod\fmod64.dll");
            F(@"Games\Repack\Marvel Game\bin\x64\game.exe", 900); F(@"Games\Repack\Marvel Game\bin\x64\steam_api64.dll"); F(@"Games\Repack\Marvel Game\bin\x64\unins000.exe");
            F(@"Games\Broken\steam_api64.dll"); F(@"Games\Broken\crs-handler.exe");
            File.WriteAllBytes(Path.Combine(tmp, @"Games\Broken\Broken.exe"), Array.Empty<byte>());
            F(@"Projects\MyUe\MyUe.uproject"); F(@"Projects\MyUe\Binaries\Win64\MyUe-Win64-Shipping.exe");
            F(@"Projects\Repo\.git\HEAD"); F(@"Projects\Repo\build\app.exe"); F(@"Projects\Repo\build\UnityPlayer.dll"); D(@"Projects\Repo\build\app_Data");
            F(@"Tools\SomeApp\app.exe"); F(@"Tools\SomeApp\Qt5Core.dll");
            F(@"Tools\Editor\UnityEditor.exe"); F(@"Tools\Editor\UnityPlayer.dll"); D(@"Tools\Editor\UnityEditor_Data");

            // Обход как с корня диска: корень подставного дерева — «диск».
            var s = new Scanner(() => false);
            s.ScanDrive(tmp);
            FoundGame Got(string name) => s.Found.FirstOrDefault(g => Path.GetFileName(g.Dir) == name);
            Check("Unity: UnityPlayer.dll + *_Data", Got("UnityGame")?.Engine == "Unity" && Got("UnityGame")?.Exe.EndsWith("UnityGame.exe") == true);
            Check("Unreal: Engine + Binaries/Win64, запуск через корневой .exe", Got("UeGame")?.Engine == "Unreal Engine" && Got("UeGame")?.Exe.EndsWith(@"UeGame\UeGame.exe") == true);
            Check("Godot: *.pck рядом с .exe", Got("GodotGame")?.Engine == "Godot");
            Check("только установщик — не игра", Got("OnlySetup") == null);
            Check("один слабый признак — не игра", Got("BinkOnly") == null);
            Check("два слабых признака — игра", Got("BinkFmod") != null);
            Check("пустой .exe и служебные — не игра", Got("Broken") == null);
            Check("bin/x64 внутри папки игры: корень — папка игры, запуск — не деинсталлятор",
                Got("Marvel Game") != null && Got("Marvel Game").Exe.EndsWith("game.exe"));
            Check("проект Unreal (.uproject) и репозиторий (.git) — не игры", Got("MyUe") == null && Got("Repo") == null);
            Check("обычная программа и редактор — не игры", Got("SomeApp") == null && Got("Editor") == null);
            Check("всего 5 игр", s.Found.Count == 5);
            lines.Add($"Подставное дерево: найдено {s.Found.Count} из 5, просмотрено {s.Dirs} папок — " + string.Join(", ", s.Found.Select(g => $"{g.Title} [{(g.Engine == "" ? "?" : g.Engine)}]")));

            // Папка из записи программы: корень — сама эта папка (так задана InstallLocation).
            var t = new Scanner(() => false).ScanTree(Path.Combine(tmp, @"Games\Repack"), 4, 100);
            Check("папка установки — корень игры", t != null && t.Root == Path.Combine(tmp, @"Games\Repack") && t.Exe.EndsWith("game.exe"));
        }
        catch (Exception ex) { fails.Add("подставное дерево: " + ex.Message); }
        finally { try { Directory.Delete(tmp, true); } catch { } }

        // --- служебные файлы и «не игры»
        Check("служебные .exe", JunkExe("unins000.exe") && JunkExe("UnityCrashHandler64.exe") && JunkExe("PUBG_BE.exe") && JunkExe("EasyAntiCheat_EOS_Setup.exe")
                                 && JunkExe("UE4PrereqSetup_x64.exe") && JunkExe("Launcher.exe") && !JunkExe("eldenring.exe") && !JunkExe("Cyberpunk2077.exe")
                                 && !JunkExe("Peacemaker.exe") && !JunkExe("RDR2.exe"));
        Check("не игры по поведению", NotGameReason(@"C:\Program Files\Google\Chrome\Application\chrome.exe") != null
                                      && NotGameReason(@"C:\Program Files\VideoLAN\VLC\vlc.exe") != null
                                      && NotGameReason(@"C:\Users\x\AppData\Local\Discord\app-1.0\Discord.exe") != null
                                      && NotGameReason(@"C:\Program Files\obs-studio\bin\64bit\obs64.exe") != null
                                      && NotGameReason(@"C:\Program Files\Пульс ПК\PulsePC.exe") != null
                                      && NotGameReason(@"C:\Users\x\AppData\Local\AnthropicClaude\claude.exe") != null
                                      && NotGameReason(@"C:\Program Files\Microsoft VS Code\Code.exe") != null
                                      && NotGameReason(@"E:\Games\Some Launcher\SomeLauncher.exe") != null
                                      && NotGameReason(@"E:\Games\Hollow\hollow_knight.exe") == null);

        // --- учёт по поведению
        {
            var t = new BehaviorTracker();
            var now = new DateTime(2026, 11, 21, 20, 0, 0);
            string got = null;
            for (int i = 0; i < 12 && got == null; i++) got = t.Step(10, @"E:\g\g.exe", true, true, now.AddSeconds(5 * i), 5);
            Check("60 с на весь экран с нагрузкой — игра", got == @"E:\g\g.exe");
            t.Reset(); got = null;
            for (int i = 0; i < 20 && got == null; i++) got = t.Step(10, @"E:\g\g.exe", true, false, now.AddSeconds(5 * i), 5);
            Check("без нагрузки на видеокарту — не игра", got == null);
            t.Reset(); got = null;
            for (int i = 0; i < 20 && got == null; i++) got = t.Step(i % 2 == 0 ? 10 : 11, @"E:\g\g.exe", true, true, now.AddSeconds(5 * i), 5);
            Check("смена процесса начинает счёт заново", got == null);
            t.Reset(); got = null;
            for (int i = 0; i < 6; i++) t.Step(10, @"E:\g\g.exe", true, true, now.AddSeconds(5 * i), 5);
            t.Step(10, @"E:\g\g.exe", false, false, now.AddSeconds(35), 5);          // свернули на 10 с
            for (int i = 0; i < 6 && got == null; i++) got = t.Step(10, @"E:\g\g.exe", true, true, now.AddSeconds(40 + 5 * i), 5);
            Check("короткий перерыв не сбрасывает счёт", got == @"E:\g\g.exe");
            Check("окно с заголовком на весь экран — не полноэкранная игра",
                !CoversMonitor(new Rect { Left = -8, Top = -8, Right = 1928, Bottom = 1088 }, new Rect { Right = 1920, Bottom = 1080 }, 0x00CF0000)
                && CoversMonitor(new Rect { Right = 1920, Bottom = 1080 }, new Rect { Right = 1920, Bottom = 1080 }, unchecked((long)0x96000000))
                && !CoversMonitor(new Rect { Right = 1280, Bottom = 720 }, new Rect { Right = 1920, Bottom = 1080 }, unchecked((long)0x96000000)));
        }

        // --- Xbox, ссылка .url
        Check("код издателя пакета Microsoft", GameLibrary.PublisherId("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US") == "8wekyb3d8bbwe");
        {
            var data = Encoding.ASCII.GetBytes("RGBX").Concat(BitConverter.GetBytes(1)).Concat(Encoding.Unicode.GetBytes("XboxGames\0")).ToArray();
            Check("разбор .GamingRoot", GameLibrary.ParseGamingRoot(data).SequenceEqual(new[] { "XboxGames" }));
            var x = GameLibrary.XboxConfig("<Game configVersion=\"1\"><Identity Name=\"Studio.Game\" Publisher=\"CN=Studio\" Version=\"1.0.0.0\"/>" +
                                           "<ExecutableList><Executable Name=\"Game.exe\" Id=\"Game\"/></ExecutableList><ShellVisuals DefaultDisplayName=\"Моя игра\"/></Game>");
            Check("разбор MicrosoftGame.config", x.Title == "Моя игра" && x.Name == "Studio.Game" && x.AppId == "Game" && x.Exe == "Game.exe");
        }
        {
            string longUrl = "https://mail.google.com/mail/?view=cm&fs=1&to=a%40b.c&su=test&body=" + string.Concat(Enumerable.Repeat("%D0%9F", 700)) + "КОНЕЦ";
            Check("короткий адрес и путь — без ярлыка", !GameLibrary.NeedsShortcut("https://example.com") && !GameLibrary.NeedsShortcut(@"C:\" + new string('a', 300) + ".exe"));
            Check("длинный адрес — через ярлык .url", GameLibrary.NeedsShortcut(longUrl) && GameLibrary.NeedsShortcut("mailto:x@y.z?body=" + new string('a', 300)));
            string file = Path.Combine(Path.GetTempPath(), "PulsePC-gf-test-" + Guid.NewGuid().ToString("N") + ".url");
            try
            {
                File.WriteAllText(file, GameLibrary.UrlShortcutText(longUrl), Encoding.ASCII);
                var sb = new StringBuilder(65536);
                GetPrivateProfileString("InternetShortcut", "URL", "", sb, (uint)sb.Capacity, file);
                string back = sb.ToString();
                Check("адрес в .url сохраняется целиком (кириллица — %XX)", back.Length > 4000 && back.EndsWith("%D0%9A%D0%9E%D0%9D%D0%95%D0%A6") && back.All(c => c < 128));
                lines.Add($"Ссылка .url: адрес {longUrl.Length} знаков, в файле {back.Length} (после кодирования кириллицы), путь к файлу {file.Length} знаков.");
            }
            finally { try { File.Delete(file); } catch { } }

            // Письмо обратной связи (исправление 1.4.4): длинный русский текст укорачивается так, чтобы адрес поместился в ссылку .url.
            string body = string.Concat(Enumerable.Repeat("Программа зависла при записи сессии. ", 60));
            foreach (var svc in Enum.GetValues<Feedback.Service>())
            {
                string u = Feedback.ComposeUrl(svc, "Pulse PC 1.5.0: problem — тест", body);
                Check($"письмо {svc}: адрес не длиннее {Feedback.MaxUrl}", u.Length <= Feedback.MaxUrl && u.Length > 1000);
                Check($"письмо {svc}: в конце — где полный текст", Uri.UnescapeDataString(u).Contains("Ctrl+V"));
            }
            string shortMail = Feedback.ComposeUrl(Feedback.Service.Gmail, "t", "Коротко.");
            Check("короткое письмо не меняется", Uri.UnescapeDataString(shortMail).EndsWith("Коротко.") && !shortMail.Contains("Ctrl"));
            lines.Add($"Письмо: текст {body.Length} знаков → адрес Gmail {Feedback.ComposeUrl(Feedback.Service.Gmail, "t", body).Length} знаков (предел {Feedback.MaxUrl}).");
        }

        // --- на этом ПК (только чтение)
        try
        {
            var lib = GameLibrary.Load();
            var old = lib.Where(g => g.Source is "steam" or "riot" or "astrum" or "epic" or "custom").ToList();
            var stores = lib.Where(g => g.Source is "gog" or "ubisoft" or "xbox" or "itch" or "minecraft").ToList();
            lines.Add($"На этом ПК магазины, которые программа читала и раньше (Steam, Riot, Astrum, Epic, вручную): {old.Count} — {string.Join(", ", old.Select(g => g.Title))}");
            lines.Add($"Новые источники (GOG, Ubisoft, Xbox, itch.io, Minecraft): {stores.Count}" + (stores.Count > 0 ? " — " + string.Join(", ", stores.Select(g => g.Title)) : ""));
            var cached = Found();
            lines.Add($"В кэше поиска ({(LastScan == DateTime.MinValue ? "поиск ещё не выполнялся" : LastScan.ToString("dd.MM HH:mm"))}): {cached.Count}");
            var r = ScanAll();
            lines.Add($"Полный обход сейчас (без записи кэша): {r.Games.Count} игр, {r.Dirs} папок ({r.PerDrive}) за {r.Seconds:0.0} с" + (r.Note != "" ? $" ({r.Note})" : ""));
            foreach (var g in r.Games) lines.Add($"  {g.Title} — {g.Dir} [{(g.Source == "store" ? g.Store : g.Engine == "" ? "признаки игры" : g.Engine)}]");
            lines.Add($"Итого было {old.Count}, стало {old.Count + stores.Count + r.Games.Count} (без найденных по поведению)");
        }
        catch (Exception ex) { lines.Add("чтение на этом ПК: " + ex.Message); }

        return (fails.Count == 0 ? "OK" : "FAIL: " + string.Join("; ", fails)) + "\n" + string.Join("\n", lines);
    }
}
