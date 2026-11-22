using System.Runtime.InteropServices;
using System.Text.Json;

namespace PulsePC.Services;

/// <summary>Античит игры: название, работает ли он на уровне ядра и откуда это известно.</summary>
public sealed class AntiCheatInfo
{
    /// <summary>Название античита («BattlEye», «Riot Vanguard»…).</summary>
    public string Name { get; set; } = "";
    /// <summary>Античит уровня ядра (свой драйвер в Windows) — для таких игр включается осторожный режим.</summary>
    public bool Kernel { get; set; }
    /// <summary>Уровень ядра не подтверждён (по открытым источникам неясно) — «возможно». Режим всё равно включается: это перестраховка.</summary>
    public bool Possible { get; set; }
    /// <summary>Откуда известно: folder — файлы в папке игры, service — служба или драйвер запустились вместе с игрой, list — встроенный список.</summary>
    public string Source { get; set; } = "";
    /// <summary>Что именно найдено: файл, папка или служба (для подсказки).</summary>
    public string Detail { get; set; } = "";

    /// <summary>«BattlEye, уровня ядра» / «Riot Vanguard, возможно уровня ядра» / «PunkBuster».</summary>
    public string Describe() =>
        !Kernel ? Name : Possible ? L.F("{0}, возможно уровня ядра", Name) : L.F("{0}, уровня ядра", Name);

    public string SourceText() => Source switch
    {
        "folder" => L.T("по файлам в папке игры"),
        "service" => L.T("по службе, которая запустилась вместе с игрой"),
        "list" => L.T("по списку известных игр"),
        _ => L.T("записано в сессии"),
    };

    public AntiCheatInfo Copy() => new() { Name = Name, Kernel = Kernel, Possible = Possible, Source = Source, Detail = Detail };
}

/// <summary>
/// Определение античита игры: по файлам в папке игры (EasyAntiCheat, BattlEye, драйверы HoYoverse, Tencent ACE, Ricochet…),
/// по службам и драйверам, которые запустились вместе с игрой (только чтение списка служб Windows), и по встроенному списку
/// известных игр. Процесс игры не открывается, файлы игры только перечисляются по именам — ничего не читается и не меняется.
/// Решение «включать ли осторожный режим» — <see cref="CautiousMode"/>.
/// </summary>
public static class AntiCheat
{
    // ------------------------------------------------------------------ признаки в папке игры
    private sealed record Sign(string Name, bool Kernel, Func<string, bool, bool> Match);

    private static readonly Sign[] FolderSigns =
    {
        new("Easy Anti-Cheat", true, (n, dir) => dir ? n is "easyanticheat" or "easyanticheat_eos"
            : n is "easyanticheat_setup.exe" or "easyanticheat_eos_setup.exe" or "start_protected_game.exe" or "easyanticheat_x64.dll" or "easyanticheat_eos.sys"),
        new("BattlEye", true, (n, dir) => dir ? n == "battleye"
            : n is "beclient_x64.dll" or "beservice_x64.exe" or "bedaisy.sys" || n.EndsWith("_be.exe", StringComparison.Ordinal)),
        new("HoYoverse (mhyprot)", true, (n, dir) => !dir && (n.StartsWith("mhyprot", StringComparison.Ordinal) && n.EndsWith(".sys", StringComparison.Ordinal)
            || n is "mhypbase.dll" or "hoyokprotect.sys")),
        new("Tencent ACE", true, (n, dir) => dir ? n == "anticheatexpert"
            : n.StartsWith("ace-", StringComparison.Ordinal) && (n.EndsWith(".sys", StringComparison.Ordinal) || n.EndsWith(".exe", StringComparison.Ordinal))
              || n is "sguard64.exe" or "sguardsvc64.exe"),
        new("Ricochet", true, (n, dir) => !dir && n is "randgrid.sys"),
        new("nProtect GameGuard", true, (n, dir) => dir ? n == "gameguard" : n is "gameguard.des" or "gamemon.des" or "gamemon64.des"),
        new("XIGNCODE3", true, (n, dir) => dir ? n is "xigncode" or "xigncode3" : n is "x3.xem" or "xhunter1.sys"),
        new("EA Javelin", true, (n, dir) => dir ? n == "eaanticheat" : n.StartsWith("eaanticheat.", StringComparison.Ordinal)),
        new("NetEase Anti-Cheat", true, (n, dir) => !dir && n.StartsWith("neac", StringComparison.Ordinal) && n.EndsWith(".sys", StringComparison.Ordinal)),
        new("Denuvo Anti-Cheat", true, (n, dir) => n.StartsWith("denuvo-anticheat", StringComparison.Ordinal) || n.StartsWith("denuvo-anti-cheat", StringComparison.Ordinal)),
        new("PunkBuster", false, (n, dir) => !dir && n is "pnkbstra.exe" or "pnkbstrb.exe"),
    };

    // ------------------------------------------------------------------ службы и драйверы
    private sealed record ServiceSign(string Name, bool Kernel, bool AlwaysOn, Func<string, bool> Match);

    /// <summary>
    /// AlwaysOn — служба или драйвер работает всё время, а не только с игрой (Vanguard грузится вместе с Windows, FACEIT — с его
    /// клиентом). По ним одним игру не определить: Vanguard работал бы и в любой другой игре.
    /// </summary>
    private static readonly ServiceSign[] ServiceSigns =
    {
        new("Riot Vanguard", true, true, n => n is "vgk" or "vgc"),
        new("FACEIT", true, true, n => n is "faceit" or "faceitservice"),
        new("Easy Anti-Cheat", true, false, n => n is "easyanticheat" or "easyanticheat_eos" or "easyanticheatsys" or "easyanticheat_eossys"),
        new("BattlEye", true, false, n => n is "beservice" or "bedaisy"),
        new("HoYoverse (mhyprot)", true, false, n => n.StartsWith("mhyprot", StringComparison.Ordinal) || n == "hoyokprotect"),
        new("Tencent ACE", true, false, n => n.StartsWith("ace-", StringComparison.Ordinal) || n.StartsWith("anticheatexpert", StringComparison.Ordinal)),
        new("Ricochet", true, false, n => n.StartsWith("atvi-randgrid", StringComparison.Ordinal) || n == "randgrid"),
        new("nProtect GameGuard", true, false, n => n is "npggsvc" or "gameguard"),
        new("XIGNCODE3", true, false, n => n is "xhunter1" or "xigncode"),
        new("EA Javelin", true, false, n => n is "eaanticheatservice" or "eaanticheat"),
        new("NetEase Anti-Cheat", true, false, n => n.StartsWith("neac", StringComparison.Ordinal)),
        new("Denuvo Anti-Cheat", true, false, n => n.StartsWith("denuvo-anticheat", StringComparison.Ordinal)),
        new("PunkBuster", false, false, n => n is "pnkbstra" or "pnkbstrb"),
    };

    // ------------------------------------------------------------------ встроенный список
    /// <summary>
    /// Известные игры с античитом уровня ядра (по открытым источникам, ноябрь 2026). Ключи — названия без пробелов и знаков
    /// в нижнем регистре: «=» в начале — только точное совпадение. Possible — уровень ядра не подтверждён.
    /// </summary>
    private sealed record KnownGame(string[] Keys, string[] Exes, string AntiCheat, bool Possible = false);

    private static readonly KnownGame[] Known =
    {
        new(new[] { "valorant" }, new[] { "valorant-win64-shipping", "valorant" }, "Riot Vanguard"),
        new(new[] { "leagueoflegends" }, new[] { "league of legends" }, "Riot Vanguard"),
        new(new[] { "overwatch" }, new[] { "overwatch" }, "Blizzard Defense Matrix", Possible: true),
        new(new[] { "callofduty", "warzone" }, new[] { "cod", "cod22-cod", "cod23-cod", "cod24-cod", "cod25-cod", "blackops6", "blackopscoldwar", "modernwarfare" }, "Ricochet"),
        new(new[] { "fortnite" }, new[] { "fortniteclient-win64-shipping" }, "Easy Anti-Cheat / BattlEye"),
        new(new[] { "apexlegends" }, new[] { "r5apex", "r5apex_dx12" }, "EA Javelin"),
        new(new[] { "rainbowsix", "tomclancysrainbowsix" }, new[] { "rainbowsix", "rainbowsix_vulkan", "rainbowsix_dx11" }, "BattlEye"),
        new(new[] { "pubg", "playerunknown" }, new[] { "tslgame" }, "BattlEye"),
        new(new[] { "genshinimpact" }, new[] { "genshinimpact", "yuanshen" }, "HoYoverse (mhyprot)"),
        new(new[] { "honkaistarrail" }, new[] { "starrail" }, "HoYoverse (mhyprot)"),
        new(new[] { "zenlesszonezero" }, new[] { "zenlesszonezero" }, "HoYoverse (mhyprot)"),
        new(new[] { "wutheringwaves" }, Array.Empty<string>(), "Tencent ACE"),
        new(new[] { "toweroffantasy" }, new[] { "qrsl" }, "Tencent ACE", Possible: true),
        new(new[] { "deltaforce" }, new[] { "deltaforceclient-win64-shipping" }, "Tencent ACE"),
        new(new[] { "marvelrivals" }, Array.Empty<string>(), "Tencent ACE"),
        new(new[] { "arenabreakout" }, Array.Empty<string>(), "Tencent ACE"),
        new(new[] { "escapefromtarkov" }, new[] { "escapefromtarkov" }, "BattlEye"),
        new(new[] { "thefinals" }, new[] { "discovery" }, "Easy Anti-Cheat"),
        new(new[] { "battlefield" }, new[] { "bf6", "bf2042" }, "EA Javelin"),
        new(new[] { "easportsfc", "=fc24", "=fc25", "=fc26", "=eafc24", "=eafc25", "=eafc26" }, new[] { "fc24", "fc25", "fc26" }, "EA Javelin"),
        new(new[] { "destiny2" }, new[] { "destiny2" }, "BattlEye"),
        new(new[] { "=rust" }, new[] { "rustclient" }, "Easy Anti-Cheat"),
        new(new[] { "deadbydaylight" }, Array.Empty<string>(), "Easy Anti-Cheat"),
        new(new[] { "narakabladepoint", "naraka" }, Array.Empty<string>(), "Easy Anti-Cheat"),
        new(new[] { "lostark" }, new[] { "lostark" }, "Easy Anti-Cheat"),
        new(new[] { "grandtheftautov", "=gtav", "=gta5" }, new[] { "gta5", "gta5_enhanced" }, "BattlEye"),
        new(new[] { "huntshowdown" }, Array.Empty<string>(), "Easy Anti-Cheat"),
        new(new[] { "=dayz" }, new[] { "dayz_x64", "dayz_be" }, "BattlEye"),
        new(new[] { "arma3" }, new[] { "arma3_x64" }, "BattlEye"),
        new(new[] { "warthunder" }, new[] { "aces" }, "Easy Anti-Cheat"),
        new(new[] { "haloinfinite" }, new[] { "haloinfinite" }, "Easy Anti-Cheat"),
    };

    /// <summary>
    /// Строгий античит — осторожный режим включается сам: Riot Vanguard, HoYoverse, Tencent ACE, Ricochet, EA Javelin, NetEase,
    /// nProtect GameGuard, XIGNCODE3, Denuvo Anti-Cheat, FACEIT, Blizzard (Overwatch). EasyAntiCheat и BattlEye — не строгие:
    /// там по умолчанию полная запись (решение владельца программы, 1.5.0).
    /// </summary>
    public static bool IsStrict(string antiCheatName)
    {
        if (string.IsNullOrWhiteSpace(antiCheatName)) return false;
        string n = antiCheatName.ToLowerInvariant();
        if (n.Contains("easy anti-cheat") || n.Contains("easyanticheat") || n.Contains("battleye"))
            return false;   // в том числе «Easy Anti-Cheat / BattlEye» (Fortnite)
        return true;
    }

    /// <summary>Название без пробелов, знаков и регистра: «PUBG: BATTLEGROUNDS» → «pubgbattlegrounds».</summary>
    public static string Norm(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Из встроенного списка: по названию игры или имени её файла. null — игры в списке нет.</summary>
    public static AntiCheatInfo FromList(string game, string exePath = null)
    {
        string n = Norm(game);
        string exe = string.IsNullOrEmpty(exePath) ? "" : Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        foreach (var k in Known)
        {
            bool hit = n.Length > 0 && k.Keys.Any(key => key[0] == '=' ? n == key[1..] : n.StartsWith(key, StringComparison.Ordinal));
            if (!hit && exe.Length > 0) hit = k.Exes.Contains(exe);
            if (hit) return new AntiCheatInfo { Name = k.AntiCheat, Kernel = true, Possible = k.Possible, Source = "list", Detail = game ?? exe };
        }
        return null;
    }

    // ------------------------------------------------------------------ папка игры
    /// <summary>Имена в папке (файлы и папки, только первый уровень). Подменяется в самопроверке.</summary>
    public delegate IEnumerable<(string Name, bool Dir)> Lister(string dir);

    private static IEnumerable<(string, bool)> RealList(string dir)
    {
        var list = new List<(string, bool)>();
        try
        {
            var info = new DirectoryInfo(dir);
            if (!info.Exists) return list;
            // Символические ссылки и точки соединения пропускаются: иначе обход мог бы уйти из папки игры (ссылка на корень диска).
            var opt = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false };
            foreach (var e in info.EnumerateFileSystemInfos("*", opt))
            {
                list.Add((e.Name, (e.Attributes & FileAttributes.Directory) != 0));
                if (list.Count >= 400) break;      // папки с тысячами файлов (ресурсы) — дальше смотреть незачем
            }
        }
        catch { }
        return list;
    }

    private static AntiCheatInfo MatchEntries(string dir, IEnumerable<(string Name, bool Dir)> entries, List<string> subdirs)
    {
        AntiCheatInfo best = null;
        foreach (var (name, isDir) in entries)
        {
            string n = name.ToLowerInvariant();
            if (isDir) subdirs?.Add(Path.Combine(dir, name));
            foreach (var s in FolderSigns)
                if (s.Match(n, isDir) && (best == null || (s.Kernel && !best.Kernel)))
                    best = new AntiCheatInfo { Name = s.Name, Kernel = s.Kernel, Source = "folder", Detail = Path.Combine(dir, name) };
            if (best?.Kernel == true) return best;
        }
        return best;
    }

    /// <summary>Riot: VALORANT и League of Legends узнаются по пути — сам Vanguard ставится отдельно, в «Program Files».</summary>
    private static AntiCheatInfo FromPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var part in new[] { @"\Riot Games\VALORANT\", @"\Riot Games\League of Legends\" })
            if (path.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0)
                return new AntiCheatInfo { Name = "Riot Vanguard", Kernel = true, Source = "folder", Detail = path };
        return null;
    }

    /// <summary>Папку выше уже нельзя считать папкой игры: корень диска, «Program Files», «steamapps\common» и т. п.</summary>
    private static bool IsGameFolder(string dir) =>
        !string.IsNullOrEmpty(dir) && CustomGame.FolderUsable(dir) && !dir.TrimEnd('\\').EndsWith(@"\steamapps", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// По файлу игры: папка файла и папки выше, пока это ещё папка игры (только имена на первом уровне каждой), затем — если ничего
    /// не нашлось — обход вглубь от корня игры (не больше maxDirs папок и 5 уровней).
    /// </summary>
    public static AntiCheatInfo FromFolder(string exePath, Lister list = null, int maxDirs = 250)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        list ??= RealList;
        var byPath = FromPath(exePath);
        if (byPath != null) return byPath;
        string dir = Path.GetDirectoryName(exePath), root = null;
        AntiCheatInfo weak = null;
        for (int up = 0; up < 6 && IsGameFolder(dir); up++)
        {
            var hit = MatchEntries(dir, list(dir), null);
            if (hit?.Kernel == true) return hit;
            weak ??= hit;
            root = dir;
            dir = Path.GetDirectoryName(dir);
        }
        if (root == null) return weak;
        return FromTree(root, list, maxDirs) ?? weak;
    }

    /// <summary>По папке установки (библиотека игр): обход вширь, не глубже 5 уровней и не больше maxDirs папок.</summary>
    public static AntiCheatInfo FromTree(string root, Lister list = null, int maxDirs = 250)
    {
        if (string.IsNullOrEmpty(root) || !IsGameFolder(root)) return FromPath(root);
        list ??= RealList;
        var byPath = FromPath(root.TrimEnd('\\') + "\\");
        if (byPath != null) return byPath;
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        int seen = 0;
        AntiCheatInfo weak = null;
        while (queue.Count > 0 && seen < maxDirs)
        {
            var (d, depth) = queue.Dequeue();
            seen++;
            var subs = depth < 5 ? new List<string>() : null;
            var hit = MatchEntries(d, list(d), subs);
            if (hit?.Kernel == true) return hit;
            weak ??= hit;
            if (subs == null) continue;
            // Ресурсы игр (Content, Paks, StreamingAssets…) огромны, античита там нет — их не обходим.
            foreach (var s in subs)
            {
                string n = Path.GetFileName(s).ToLowerInvariant();
                if (n is "content" or "paks" or "streamingassets" or "movies" or "audio" or "sound" or "localization" or "shadercache" or "logs" or "saved" or "screenshots") continue;
                queue.Enqueue((s, depth + 1));
            }
        }
        return weak;
    }

    // ------------------------------------------------------------------ службы и драйверы, запущенные вместе с игрой
    /// <summary>Из запущенных служб и драйверов — античит, который запустился вместе с игрой (его не было в «до игры»).</summary>
    public static AntiCheatInfo FromServices(IEnumerable<string> running, ICollection<string> before)
    {
        AntiCheatInfo best = null;
        foreach (var raw in running ?? Enumerable.Empty<string>())
        {
            string n = raw.ToLowerInvariant();
            var s = ServiceSigns.FirstOrDefault(x => x.Match(n));
            if (s == null) continue;
            // Работало и до игры — не признак этой игры. Неизвестно, что было до игры, — верим только тем, что запускаются с игрой.
            if (before != null ? before.Contains(n) : s.AlwaysOn) continue;
            if (best == null || (s.Kernel && !best.Kernel))
                best = new AntiCheatInfo { Name = s.Name, Kernel = s.Kernel, Source = "service", Detail = raw };
        }
        return best;
    }

    /// <summary>Запущенные службы античитов (обычные службы и драйверы) — только чтение списка служб Windows.</summary>
    public static List<string> RunningAntiCheatServices()
    {
        var all = RunningServices();
        return all.Where(n => ServiceSigns.Any(s => s.Match(n.ToLowerInvariant()))).ToList();
    }

    private static List<string> RunningServices()
    {
        var names = new List<string>();
        IntPtr scm = OpenSCManagerW(null, null, ScManagerEnumerateService);
        if (scm == IntPtr.Zero) return names;
        try
        {
            uint resume = 0;
            EnumServicesStatusExW(scm, 0, ServiceDriver | ServiceWin32, ServiceActive, IntPtr.Zero, 0, out uint need, out _, ref resume, null);
            if (need == 0) return names;
            IntPtr buf = Marshal.AllocHGlobal((int)need + 4096);
            try
            {
                resume = 0;
                if (!EnumServicesStatusExW(scm, 0, ServiceDriver | ServiceWin32, ServiceActive, buf, need + 4096, out _, out uint count, ref resume, null)) return names;
                int size = Marshal.SizeOf<EnumServiceStatusProcess>();
                for (int i = 0; i < count; i++)
                {
                    var e = Marshal.PtrToStructure<EnumServiceStatusProcess>(buf + i * size);
                    string name = Marshal.PtrToStringUni(e.ServiceName);
                    if (!string.IsNullOrEmpty(name)) names.Add(name);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception ex) { Log.Error("список служб для античита", ex); }
        finally { CloseServiceHandle(scm); }
        return names;
    }

    // ------------------------------------------------------------------ наблюдение за идущей игрой и память о найденном
    private static readonly object Gate = new();
    private static readonly Dictionary<string, AntiCheatInfo> byExe = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, AntiCheatInfo> byGame = new(StringComparer.Ordinal);    // ключ — Norm(название)
    private static HashSet<string> baseline;          // службы античитов, которые работали без игры
    private static DateTime baselineAt = DateTime.MinValue, gameSeenAt = DateTime.MinValue, lastServiceCheck = DateTime.MinValue;
    private static string observedExe;
    private static AppSettings settings;
    private const string SeenKey = "anticheat.seen";

    /// <summary>Подключить настройки: что программа уже знает об античитах игр (переживает перезапуск).</summary>
    public static void UseSettings(AppSettings s)
    {
        if (s == null) return;
        lock (Gate)
        {
            settings = s;
            try
            {
                string json = s.Get(SeenKey);
                var saved = string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<Dictionary<string, AntiCheatInfo>>(json);
                if (saved != null) foreach (var kv in saved) if (kv.Value != null && !byGame.ContainsKey(kv.Key)) byGame[kv.Key] = kv.Value;
            }
            catch (Exception ex) { Log.Error("anticheat.seen", ex); }
        }
    }

    /// <summary>Что известно об античите игры по названию: найденное раньше (папка, служба), иначе встроенный список.</summary>
    public static AntiCheatInfo ForGame(string game)
    {
        string key = Norm(game);
        AntiCheatInfo a = null;
        lock (Gate) if (key.Length > 0 && byGame.TryGetValue(key, out var found)) a = found.Copy();
        if (a != null && !string.IsNullOrEmpty(a.Name)) return a;
        return FromList(game) ?? a;
    }

    /// <summary>Запомнить найденное для игры (null — «ничего нет», тоже запоминается, чтобы не искать снова).</summary>
    public static void Remember(string game, AntiCheatInfo info, bool save = true)
    {
        string key = Norm(game);
        if (key.Length == 0) return;
        info ??= new AntiCheatInfo();        // пустое имя — «античита не нашлось»
        bool changed;
        lock (Gate)
        {
            changed = !byGame.TryGetValue(key, out var was) || was.Name != info.Name || was.Kernel != info.Kernel || was.Source != info.Source;
            if (changed) byGame[key] = info.Copy();
        }
        if (changed && save) SaveSeen();
    }

    /// <summary>Записать память об античитах в настройки (одно сохранение после пачки Remember(save: false)).</summary>
    public static void SaveSeen()
    {
        AppSettings s; string json;
        lock (Gate)
        {
            s = settings;
            if (s == null) return;
            json = JsonSerializer.Serialize(byGame);
        }
        s.Set(SeenKey, json);
        s.Save();
    }

    /// <summary>Найдено ли что-то раньше (в том числе «ничего»): тогда папку заново не обходят.</summary>
    public static bool Seen(string game)
    {
        string key = Norm(game);
        lock (Gate) return key.Length > 0 && byGame.ContainsKey(key);
    }

    /// <summary>
    /// Полное определение для запущенной игры: папка (кэш по файлу), службы, запустившиеся с ней, список.
    /// Не открывает процесс игры. Вызывается из фонового потока поиска игры.
    /// </summary>
    public static AntiCheatInfo Detect(string game, string exePath, bool checkServices)
    {
        AntiCheatInfo folder = null;
        bool cached = false;
        if (!string.IsNullOrEmpty(exePath))
        {
            lock (Gate) cached = byExe.TryGetValue(exePath, out folder);
            if (!cached)
            {
                folder = FromFolder(exePath);
                lock (Gate) byExe[exePath] = folder;
            }
        }
        if (folder?.Kernel == true) return folder;
        AntiCheatInfo service = null;
        if (checkServices)
        {
            HashSet<string> before;
            lock (Gate) before = baseline != null && baselineAt < gameSeenAt ? new HashSet<string>(baseline) : null;
            try { service = FromServices(RunningAntiCheatServices(), before); } catch { }
        }
        if (service?.Kernel == true) return service;
        return FromList(game, exePath) ?? folder ?? service;
    }

    /// <summary>
    /// У файла есть античит уровня ядра — по папке (с кэшем) или по имени файла из списка. Для поиска игр по поведению:
    /// у такой программы «GPU Engine» по процессу не опрашивается.
    /// </summary>
    public static bool KernelByFolder(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return false;
        AntiCheatInfo folder;
        bool cached;
        lock (Gate) cached = byExe.TryGetValue(exePath, out folder);
        if (!cached)
        {
            folder = FromFolder(exePath);
            lock (Gate) byExe[exePath] = folder;
        }
        return folder?.Kernel == true || FromList(null, exePath)?.Kernel == true;
    }

    /// <summary>
    /// Такт поиска игры (раз в несколько секунд, в фоне). Игры нет — раз в 2 минуты запоминается, какие службы античитов работают
    /// без игры. Игра есть — первые 3 минуты проверяются службы, запустившиеся вместе с ней; найденное запоминается для игры.
    /// </summary>
    public static void Observe(string game, string exePath)
    {
        try
        {
            var now = DateTime.Now;
            if (string.IsNullOrEmpty(game))
            {
                observedExe = null;
                if ((now - baselineAt).TotalMinutes < 2) return;
                var running = RunningAntiCheatServices().Select(n => n.ToLowerInvariant());
                lock (Gate) { baseline = new HashSet<string>(running); baselineAt = now; }
                return;
            }
            if (!string.Equals(observedExe, exePath ?? game, StringComparison.OrdinalIgnoreCase))
            {
                observedExe = exePath ?? game;
                lock (Gate) gameSeenAt = now;
            }
            bool seen = Seen(game);
            var known = ForGame(game);
            bool early = (now - gameSeenAt).TotalMinutes < 3;
            if (seen && known?.Kernel == true && known.Source is "folder" or "service") return;     // уже известно надёжно
            if (seen && !early) return;                                                               // первые минуты игры прошли
            if (seen && (now - lastServiceCheck).TotalSeconds < 9) return;
            lastServiceCheck = now;
            var found = Detect(game, exePath, checkServices: early);
            if (found == null) { if (!seen) Remember(game, null); return; }
            // Хуже прежнего не записываем: найденный античит уровня ядра не «теряется» из-за пропавшей службы.
            if (seen && known?.Kernel == true && !found.Kernel) return;
            Remember(game, found);
        }
        catch (Exception ex) { Log.Error("определение античита", ex); }
    }

    // ------------------------------------------------------------------ WinAPI (только чтение списка служб)
    private const uint ScManagerEnumerateService = 0x0004, ServiceDriver = 0x0000000B, ServiceWin32 = 0x00000030, ServiceActive = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumServiceStatusProcess
    {
        public IntPtr ServiceName, DisplayName;
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string machine, string database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumServicesStatusExW(IntPtr scm, int infoLevel, uint serviceType, uint serviceState, IntPtr services, uint bufSize,
        out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string groupName);

    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr h);

    // ------------------------------------------------------------------ самопроверка
    /// <summary>Самопроверка на подставных папках, службах и списках (система не меняется, процессы не открываются).</summary>
    public static string SelfTest()
    {
        var lines = new List<string>();
        var fails = new List<string>();
        int checks = 0;
        void Check(string what, bool ok) { checks++; if (!ok) fails.Add(what); }
        try
        {
            // Подставная файловая система: словарь «папка → имена».
            var fs = new Dictionary<string, (string, bool)[]>(StringComparer.OrdinalIgnoreCase)
            {
                [@"D:\Games\PUBG\TslGame\Binaries\Win64"] = new[] { ("TslGame.exe", false), ("TslGame_BE.exe", false), ("BattlEye", true) },
                [@"D:\Games\PUBG\TslGame\Binaries"] = new[] { ("Win64", true) },
                [@"D:\Games\PUBG\TslGame"] = new[] { ("Binaries", true), ("Content", true) },
                [@"D:\Games\PUBG"] = new[] { ("TslGame", true) },
                [@"D:\Games\Genshin Impact Game"] = new[] { ("GenshinImpact.exe", false), ("mhypbase.dll", false), ("GenshinImpact_Data", true) },
                [@"E:\Wuthering Waves\Wuthering Waves Game\Client\Binaries\Win64"] = new[] { ("Client-Win64-Shipping.exe", false) },
                [@"E:\Wuthering Waves\Wuthering Waves Game\Client\Binaries"] = new[] { ("Win64", true) },
                [@"E:\Wuthering Waves\Wuthering Waves Game\Client"] = new[] { ("Binaries", true), ("Content", true) },
                [@"E:\Wuthering Waves\Wuthering Waves Game"] = new[] { ("Client", true), ("AntiCheatExpert", true), ("Wuthering Waves.exe", false) },
                [@"E:\Wuthering Waves"] = new[] { ("Wuthering Waves Game", true), ("launcher.exe", false) },
                [@"D:\Games\Indie"] = new[] { ("Indie.exe", false), ("UnityPlayer.dll", false), ("Indie_Data", true) },
                [@"D:\Games\Indie\Indie_Data"] = new[] { ("Managed", true) },
                [@"D:\Games\Old\Shooter"] = new[] { ("shooter.exe", false), ("pnkbstra.exe", false) },
                [@"D:\Games\CoD"] = new[] { ("cod.exe", false), ("randgrid.sys", false) },
                [@"D:\Games\Deep"] = new[] { ("bin", true) },
                [@"D:\Games\Deep\bin"] = new[] { ("game.exe", false) },
                [@"D:\Games\Deep\tools"] = Array.Empty<(string, bool)>(),
                [@"D:\Games\Lib\SomeGame"] = new[] { ("x64", true), ("EasyAntiCheat", true) },
                [@"D:\Games\Lib\SomeGame\x64"] = new[] { ("SomeGame.exe", false) },
            };
            IEnumerable<(string, bool)> Fake(string d) => fs.TryGetValue(d.TrimEnd('\\'), out var e) ? e : Array.Empty<(string, bool)>();

            var pubg = FromFolder(@"D:\Games\PUBG\TslGame\Binaries\Win64\TslGame.exe", Fake);
            Check("BattlEye рядом с exe (PUBG) — ядро, по папке", pubg is { Name: "BattlEye", Kernel: true, Source: "folder" });
            var genshin = FromFolder(@"D:\Games\Genshin Impact Game\GenshinImpact.exe", Fake);
            Check("mhypbase.dll (Genshin) — HoYoverse, ядро", genshin is { Kernel: true } && genshin.Name.StartsWith("HoYoverse"));
            var wuwa = FromFolder(@"E:\Wuthering Waves\Wuthering Waves Game\Client\Binaries\Win64\Client-Win64-Shipping.exe", Fake);
            Check("папка AntiCheatExpert тремя уровнями выше exe (Wuthering Waves) — Tencent ACE", wuwa is { Name: "Tencent ACE", Kernel: true });
            Check("игра на Unity без античита — ничего", FromFolder(@"D:\Games\Indie\Indie.exe", Fake) == null);
            var pb = FromFolder(@"D:\Games\Old\Shooter\shooter.exe", Fake);
            Check("PunkBuster — найден, но не уровня ядра", pb is { Name: "PunkBuster", Kernel: false });
            Check("randgrid.sys — Ricochet (Call of Duty)", FromFolder(@"D:\Games\CoD\cod.exe", Fake) is { Name: "Ricochet", Kernel: true });
            Check("VALORANT — Vanguard по пути Riot Games", FromFolder(@"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe", Fake) is { Name: "Riot Vanguard", Kernel: true });
            Check("обход от папки установки находит EasyAntiCheat", FromTree(@"D:\Games\Lib\SomeGame", Fake) is { Name: "Easy Anti-Cheat", Kernel: true });
            Check("корень диска и общие папки не обходятся", FromTree(@"D:\", Fake) == null && FromTree(@"D:\SteamLibrary\steamapps\common", Fake) == null);
            Check("служебная папка игры без признаков — ничего", FromFolder(@"D:\Games\Deep\bin\game.exe", Fake) == null);

            // Службы: до игры работал Vanguard, с игрой запустились BEService и BEDaisy.
            var before = new HashSet<string> { "vgk", "vgc" };
            var svc = FromServices(new[] { "vgk", "vgc", "BEService", "BEDaisy", "Audiosrv" }, before);
            Check("служба, запущенная вместе с игрой, — BattlEye", svc is { Name: "BattlEye", Kernel: true, Source: "service" });
            Check("Vanguard работал до игры — не признак этой игры", FromServices(new[] { "vgk", "vgc", "Audiosrv" }, before) == null);
            Check("без снимка «до игры» Vanguard и FACEIT не в счёт, mhyprot3 — в счёт",
                FromServices(new[] { "vgk", "FACEIT" }, null) == null && FromServices(new[] { "vgk", "mhyprot3" }, null) is { Kernel: true } m && m.Name.StartsWith("HoYoverse"));
            Check("PunkBuster из служб — не ядро", FromServices(new[] { "PnkBstrA" }, new HashSet<string>()) is { Kernel: false });

            // Список известных игр.
            Check("VALORANT и League of Legends — Vanguard по списку", FromList("VALORANT") is { Name: "Riot Vanguard", Source: "list" } && FromList("League of Legends") is { Kernel: true });
            Check("Overwatch 2 — «возможно» уровня ядра", FromList("Overwatch® 2") is { Kernel: true, Possible: true });
            Check("PUBG: BATTLEGROUNDS — BattlEye", FromList("PUBG: BATTLEGROUNDS")?.Name == "BattlEye");
            Check("Call of Duty® и по exe cod.exe — Ricochet", FromList("Call of Duty®")?.Name == "Ricochet" && FromList("Неизвестно", @"C:\x\cod.exe")?.Name == "Ricochet");
            Check("Genshin, Star Rail, ZZZ — HoYoverse", new[] { "Genshin Impact", "Honkai: Star Rail", "Zenless Zone Zero" }.All(g => FromList(g)?.Name?.StartsWith("HoYoverse") == true));
            Check("EA SPORTS FC™ 25 и FC 25 — Javelin", FromList("EA SPORTS FC™ 25")?.Name == "EA Javelin" && FromList("FC 25")?.Name == "EA Javelin");
            Check("Rust — точное совпадение (Rusted Warfare — нет)", FromList("Rust") != null && FromList("Rusted Warfare") == null);
            Check("Deadlock, Counter-Strike 2, Dota 2, Minecraft — не в списке",
                new[] { "Deadlock", "Counter-Strike 2", "Dota 2", "Minecraft" }.All(g => FromList(g) == null));

            // Решение режима (чистая функция).
            var none = Array.Empty<string>();
            var dv = CautiousMode.Decide("VALORANT", FromList("VALORANT"), none, none);
            Check("VALORANT по умолчанию — осторожный режим (как раньше)", dv.Cautious && dv.By == "auto");
            Check("в осторожном режиме PresentMon, сеть игры (ETW), DPC и «GPU Engine» по игре не запускаются",
                !dv.AllowFrames && !dv.AllowGameNet && !dv.AllowDpc && !dv.AllowGpuProbe && !dv.AllowProcessOpen);
            var dl = CautiousMode.Decide("Deadlock", null, none, none);
            Check("игра без античита — полная запись", !dl.Cautious && dl.AllowFrames && dl.AllowGameNet && dl.AllowGpuProbe && dl.By == "auto");
            var full = CautiousMode.Decide("VALORANT", FromList("VALORANT"), none, new[] { "valorant" });
            Check("ручное «полностью» побеждает автоматику", !full.Cautious && full.By == "full" && full.AllowFrames);
            var man = CautiousMode.Decide("Deadlock", null, new[] { "DEADLOCK" }, none);
            Check("ручной осторожный режим побеждает «античита нет»", man.Cautious && man.By == "manual" && !man.AllowFrames);
            Check("обычный (не ядерный) античит — полная запись", !CautiousMode.Decide("Old", pb, none, none).Cautious);
            Check("«возможно уровня ядра» — тоже осторожный режим (перестраховка)", CautiousMode.Decide("Overwatch 2", FromList("Overwatch 2"), none, none).Cautious);
            // Решение владельца (1.5.0): строгие античиты — осторожно сами; EasyAntiCheat и BattlEye — полная запись по умолчанию.
            Check("строгие сами: Genshin, Wuthering Waves, Call of Duty, Apex, Marvel Rivals — осторожный режим",
                new[] { "Genshin Impact", "Wuthering Waves", "Call of Duty", "Apex Legends", "Marvel Rivals" }.All(g => CautiousMode.Decide(g, FromList(g), none, none).Cautious));
            Check("EasyAntiCheat и BattlEye: PUBG, Fortnite, Rust, Tarkov, The Finals — полная запись по умолчанию",
                new[] { "PUBG: BATTLEGROUNDS", "Fortnite", "Rust", "Escape from Tarkov", "The Finals" }.All(g => !CautiousMode.Decide(g, FromList(g), none, none).Cautious));
            Check("BattlEye по папке (PUBG) — полная запись, но вручную можно включить осторожный",
                !CautiousMode.Decide("PUBG", pubg, none, none).Cautious && CautiousMode.Decide("PUBG", pubg, new[] { "pubg" }, none).Cautious);
            Check("NetEase по папке — строгий", IsStrict("NetEase Anti-Cheat") && !IsStrict("Easy Anti-Cheat / BattlEye") && !IsStrict("BattlEye"));
            // Строка в «Играх» для BattlEye не должна говорить «не на уровне ядра» — он уровня ядра, просто не строгий.
            string pubgLine = CautiousMode.StateLine(CautiousMode.Decide("PUBG", pubg, none, none));
            Check("строка состояния BattlEye: полная запись по умолчанию, без «не на уровне ядра»",
                !pubgLine.Contains("не на уровне ядра") && (L.Code != "ru" || pubgLine.Contains("по умолчанию")));
            // «Античита не нашлось» запоминается пустой записью — в строках не должно быть «(античит )».
            var nothing = new AntiCheatInfo();
            Check("ручной выбор для игры без античита: без пустого «(античит )»",
                CautiousMode.Decide("Deadlock", nothing, new[] { "deadlock" }, none).AntiCheat == null
                && !CautiousMode.StateLine(CautiousMode.Decide("Deadlock", nothing, none, new[] { "deadlock" })).Contains("античит )"));
            Check("строка состояния PunkBuster: «не на уровне ядра»", (L.Code != "ru" || CautiousMode.StateLine(CautiousMode.Decide("Old", pb, none, none)).Contains("не на уровне ядра")));

            // Ручной выбор: списки меняются без дублей, выбор «как автоматически» убирает ручную отметку.
            var c = new List<string>(); var f = new List<string> { "PUBG: BATTLEGROUNDS" };
            CautiousMode.ApplyChoice(c, f, "pubg battlegrounds", cautious: true, autoCautious: true);
            Check("выбор «как автоматически» убирает ручные отметки", c.Count == 0 && f.Count == 0);
            CautiousMode.ApplyChoice(c, f, "PUBG: BATTLEGROUNDS", cautious: false, autoCautious: true);
            Check("«полностью» для игры с античитом — в списке full", c.Count == 0 && f.Count == 1);
            CautiousMode.ApplyChoice(c, f, "Deadlock", cautious: true, autoCautious: false);
            CautiousMode.ApplyChoice(c, f, "Deadlock", cautious: true, autoCautious: false);
            Check("осторожный режим вручную — без дублей", c.Count == 1 && f.Count == 1);

            // Старые сессии (без новых полей): VALORANT без FPS — как раньше «осторожно», Overwatch с FPS — запись была полной.
            var oldVal = CautiousMode.ForSession(new GameSession { Game = "VALORANT" });
            Check("старая сессия VALORANT без FPS — отметка осторожного режима", oldVal.Cautious && oldVal.AntiCheat?.Name == "Riot Vanguard");
            var oldOw = CautiousMode.ForSession(new GameSession { Game = "Overwatch 2", AvgFps = 140 });
            Check("старая сессия Overwatch с FPS — запись была полной", !oldOw.Cautious);
            var newS = CautiousMode.ForSession(new GameSession { Game = "PUBG: BATTLEGROUNDS", Cautious = true, CautiousBy = "auto", AntiCheat = "BattlEye", AntiCheatKernel = true });
            Check("новая сессия хранит решение", newS.Cautious && newS.AntiCheat?.Name == "BattlEye");

            // Что видно на этом ПК (только чтение).
            var running = RunningAntiCheatServices();
            lines.Add("на этом ПК работают службы античитов: " + (running.Count == 0 ? "нет" : string.Join(", ", running)));
            int total = RunningServices().Count;
            lines.Add($"служб и драйверов в списке Windows (работающих): {total}");
            Check("список служб Windows читается", total > 0);
        }
        catch (Exception ex) { fails.Add("исключение: " + ex); }
        string head = fails.Count == 0 ? $"OK ({checks} проверок)" : "FAIL: " + string.Join("; ", fails);
        return head + (lines.Count > 0 ? "\n" + string.Join("\n", lines) : "");
    }
}

/// <summary>Решение для игры: осторожный режим или полная запись, почему, и что в нём разрешено.</summary>
public sealed class CautiousDecision
{
    public string Game { get; init; } = "";
    public bool Cautious { get; init; }
    /// <summary>auto — по античиту; manual — человек включил осторожный режим; full — человек разрешил полную запись.</summary>
    public string By { get; init; } = "auto";
    public AntiCheatInfo AntiCheat { get; init; }

    /// <summary>Запись кадров (PresentMon, трассировка Windows).</summary>
    public bool AllowFrames => !Cautious;
    /// <summary>Сетевые события игры (ETW Kernel-Network): сервер игры и пинг до него. Обычный пинг до интернета работает всегда.</summary>
    public bool AllowGameNet => !Cautious;
    /// <summary>Проверка задержек драйверов (трассировка ядра) во время игры.</summary>
    public bool AllowDpc => !Cautious;
    /// <summary>Счётчики «GPU Engine» по процессу игры (какой адаптер рисовал игру).</summary>
    public bool AllowGpuProbe => !Cautious;
    /// <summary>Открывать процесс игры с правами больше PROCESS_QUERY_LIMITED_INFORMATION (например, читать список его модулей).</summary>
    public bool AllowProcessOpen => !Cautious;
}

/// <summary>
/// Осторожный режим для игр с античитом уровня ядра: на время такой игры программа не включает запись кадров (PresentMon),
/// сетевые события игры (ETW), проверку задержек драйверов, не опрашивает «GPU Engine» по процессу игры и не открывает его
/// с правами больше минимальных. Остаются загрузка и температура видеокарты, процессор, память, диск, пинг до интернета, оверлей.
/// Ручной выбор — настройки anticheat.cautious (режим включён вручную) и anticheat.full (полная запись разрешена вручную):
/// названия игр через перевод строки. Ручной выбор побеждает автоматику.
/// </summary>
public static class CautiousMode
{
    public const string KeyCautious = "anticheat.cautious", KeyFull = "anticheat.full";

    /// <summary>Пояснение, одно на всю программу (отчёт, «Установленные программы», справка).</summary>
    public static string Explain => L.T("Запись кадров — обычная трассировка Windows (так же работают CapFrameX и NVIDIA FrameView), в игру ничего не встраивается; риск очень мал. Осторожный режим — перестраховка: в играх со строгим античитом программа не включает глубокую трассировку Windows.");

    /// <summary>Подтверждение «Записывать полностью»: что это значит, честно, без обещаний.</summary>
    public static string ConfirmFullText(string game) =>
        L.F("Записывать «{0}» полностью — с FPS, временем кадров и сервером игры?", game) + "\n\n" + Explain + "\n\n" +
        L.T("Обещать, что античит никогда ни к чему не придерётся, не может никто, поэтому решаете вы. Вернуть осторожный режим можно в любой момент.");

    private static AppSettings settings;
    private static volatile CautiousDecision current;
    private static int currentPid;
    // Игры, которые записывались до 1.5 без FPS из-за античита (старые сессии показываются как раньше).
    private static readonly string[] LegacyNoFps = { "VALORANT", "League of Legends" };

    public static event Action Changed;

    public static void UseSettings(AppSettings s)
    {
        settings = s;
        AntiCheat.UseSettings(s);
    }

    /// <summary>Решение для идущей записи (null — игры нет).</summary>
    public static CautiousDecision Current => current;

    /// <summary>Записать решение для начавшейся игры (рекордер) и сбросить, когда игра закончилась.</summary>
    public static void SetCurrent(CautiousDecision d, int pid) { current = d; currentPid = d == null ? 0 : pid; }

    /// <summary>Этот процесс — игра в осторожном режиме: открывать его можно только с минимальными правами.</summary>
    public static bool Protects(int pid) => pid != 0 && current is { Cautious: true } && pid == currentPid;

    private static List<string> ListOf(string key)
    {
        string raw = settings?.Get(key) ?? "";
        return raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>Чистое решение: ручное «полностью» и ручной осторожный режим побеждают автоматику (античит уровня ядра).</summary>
    public static CautiousDecision Decide(string game, AntiCheatInfo ac, IEnumerable<string> manualCautious, IEnumerable<string> manualFull)
    {
        string n = AntiCheat.Norm(game);
        // Запись «античита не нашлось» (пустое имя) — это не античит: иначе в строках было бы «(античит )» и «В игре античит ;».
        if (ac != null && string.IsNullOrEmpty(ac.Name)) ac = null;
        if (n.Length > 0 && manualFull.Any(x => AntiCheat.Norm(x) == n)) return new() { Game = game, Cautious = false, By = "full", AntiCheat = ac };
        if (n.Length > 0 && manualCautious.Any(x => AntiCheat.Norm(x) == n)) return new() { Game = game, Cautious = true, By = "manual", AntiCheat = ac };
        // Сам по себе осторожный режим включается только для строгих античитов. EasyAntiCheat и BattlEye (PUBG, Fortnite, Apex,
        // Rust, Tarkov…) — полная запись: в этих играх программы замера FPS годами работают без проблем; режим там — по кнопке.
        bool strict = ac != null && ac.Kernel && !string.IsNullOrEmpty(ac.Name) && AntiCheat.IsStrict(ac.Name);
        return new() { Game = game, Cautious = strict, By = "auto", AntiCheat = ac != null && !string.IsNullOrEmpty(ac.Name) ? ac : null };
    }

    /// <summary>Решение для игры сейчас: что известно о её античите + ручной выбор.</summary>
    public static CautiousDecision For(string game, string exePath = null)
    {
        var ac = AntiCheat.ForGame(game);
        if ((ac == null || string.IsNullOrEmpty(ac.Name)) && !string.IsNullOrEmpty(exePath)) ac = AntiCheat.FromList(game, exePath);
        return Decide(game, ac, ListOf(KeyCautious), ListOf(KeyFull));
    }

    /// <summary>Каким было решение для записанной сессии. Старые сессии (до 1.5): VALORANT и LoL без FPS — осторожно, как раньше.</summary>
    public static CautiousDecision ForSession(GameSession s)
    {
        if (s == null) return null;
        AntiCheatInfo ac = string.IsNullOrEmpty(s.AntiCheat) ? null
            : new AntiCheatInfo { Name = s.AntiCheat, Kernel = s.AntiCheatKernel == true, Source = "session" };
        if (s.Cautious is bool c) return new() { Game = s.Game, Cautious = c, By = s.CautiousBy ?? "auto", AntiCheat = ac };
        ac ??= AntiCheat.FromList(s.Game);
        bool legacy = !(s.AvgFps > 0) && LegacyNoFps.Contains(s.Game, StringComparer.OrdinalIgnoreCase);
        return new() { Game = s.Game, Cautious = legacy, By = "auto", AntiCheat = ac };
    }

    /// <summary>
    /// Чистая правка списков: выбор совпадает с автоматикой — ручные отметки убираются; иначе игра попадает в нужный список.
    /// </summary>
    public static void ApplyChoice(List<string> cautiousList, List<string> fullList, string game, bool cautious, bool autoCautious)
    {
        string n = AntiCheat.Norm(game);
        cautiousList.RemoveAll(x => AntiCheat.Norm(x) == n);
        fullList.RemoveAll(x => AntiCheat.Norm(x) == n);
        if (cautious == autoCautious) return;
        (cautious ? cautiousList : fullList).Add(game);
    }

    /// <summary>Выбор человека для игры: true — осторожный режим, false — полная запись. Сохраняется сразу.</summary>
    public static void Choose(string game, bool cautious)
    {
        var s = settings;
        if (s == null || string.IsNullOrWhiteSpace(game)) return;
        var ac = AntiCheat.ForGame(game);
        bool auto = Decide(game, ac, Array.Empty<string>(), Array.Empty<string>()).Cautious;
        lock (AppSettings.SyncRoot)
        {
            var c = ListOf(KeyCautious); var f = ListOf(KeyFull);
            ApplyChoice(c, f, game.Trim(), cautious, auto);
            s.Set(KeyCautious, c.Count == 0 ? null : string.Join("\n", c));
            s.Set(KeyFull, f.Count == 0 ? null : string.Join("\n", f));
        }
        s.Save();
        Log.Info($"осторожный режим для «{game}»: {(cautious ? "включён" : "полная запись")} вручную");
        try { Changed?.Invoke(); } catch { }
    }

    /// <summary>Строка-состояние для списка игр: что сейчас выбрано и почему.</summary>
    public static string StateLine(CautiousDecision d)
    {
        string ac = d.AntiCheat != null ? d.AntiCheat.Describe() : null;
        return d.By switch
        {
            "manual" => ac != null ? L.F("Осторожный режим — включили вы (античит {0}).", ac) : L.T("Осторожный режим — включили вы."),
            "full" => ac != null ? L.F("Полная запись — разрешили вы (античит {0}).", ac) : L.T("Полная запись — разрешили вы."),
            _ when d.Cautious => L.F("Осторожный режим: античит {0} — {1}.", ac, d.AntiCheat.SourceText()),
            // EasyAntiCheat и BattlEye работают на уровне ядра, но они не строгие: полная запись — по умолчанию, а не потому что «не ядро».
            _ when d.AntiCheat?.Kernel == true => L.F("Античит {0} — {1}; для EasyAntiCheat и BattlEye запись по умолчанию полная, осторожный режим — по переключателю.", ac, d.AntiCheat.SourceText()),
            _ when ac != null => L.F("Античит {0} — {1}; работает не на уровне ядра, запись полная.", ac, d.AntiCheat.SourceText()),
            _ => L.T("Античит не найден — запись полная."),
        };
    }
}
