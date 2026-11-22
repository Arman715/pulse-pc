using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace PulsePC.Services;

public class GameEntry
{
    public string Title { get; set; }
    public string Caption { get; set; }
    /// <summary>Адрес запуска (steam://…) либо путь к программе.</summary>
    public string Target;
    public string Arguments = "";
    public DateTime LastPlayed;
    /// <summary>Папка установки игры (по ней определяется движок и где лежат её настройки).</summary>
    public string InstallDir;
    /// <summary>
    /// Откуда игра известна: steam, riot, astrum, epic, gog, ubisoft, xbox, itch, custom — магазины и ручное добавление;
    /// store — по записи магазина в «Установке и удалении программ»; disk — найдена на диске по файлам движка;
    /// behavior — найдена по поведению (рисовала кадры на весь экран). Для disk и behavior есть «Это не игра».
    /// </summary>
    public string Source = "";
    public bool CanRejectAsNotGame => Source is "disk" or "behavior" or "store";
}

/// <summary>
/// Установленные игры. Быстрые источники читаются здесь при каждом вызове (магазины: реестр и файлы-описания);
/// то, что требует обхода папок (игры EA, Battle.net, Rockstar, Wargaming/Lesta по записям в «Установке и удалении программ»,
/// игры на дисках, найденные по поведению), ищет в фоне <see cref="GameFinder"/> и отдаёт из своего кэша.
/// </summary>
public static class GameLibrary
{
    private static readonly string[] NotGames = { "Steamworks", "Proton", "Steam Linux", "Blender", "Godot", "Redistributable", "SDK", "Dedicated Server", "Wallpaper Engine" };

    public static List<GameEntry> Load()
    {
        var games = new List<GameEntry>();
        try { games.AddRange(Steam()); } catch { }
        try { AddRiot(games); } catch { }
        try { AddAstrum(games); } catch { }
        try { AddEpic(games); } catch { }
        try { AddGog(games); } catch { }
        try { AddUbisoft(games); } catch { }
        try { AddXbox(games); } catch { }
        try { AddItch(games); } catch { }
        try { AddMinecraft(games); } catch { }
        try { AddCustom(games); } catch { }
        try { AddFound(games); } catch { }
        return games.OrderByDescending(g => g.LastPlayed).ThenBy(g => g.Title).ToList();
    }

    /// <summary>Папки, которые уже знают магазины: поиск на дисках их не обходит (игры там уже в списке).</summary>
    public static List<string> StoreFolders()
    {
        var games = new List<GameEntry>();
        try { games.AddRange(Steam()); } catch { }
        try { AddRiot(games); } catch { }
        try { AddAstrum(games); } catch { }
        try { AddEpic(games); } catch { }
        try { AddGog(games); } catch { }
        try { AddUbisoft(games); } catch { }
        try { AddXbox(games); } catch { }
        try { AddItch(games); } catch { }
        var dirs = games.Select(g => g.InstallDir).Where(d => !string.IsNullOrEmpty(d)).ToList();
        try { dirs.AddRange(SteamLibraries().Select(l => Path.Combine(l, "steamapps"))); } catch { }
        return dirs.Select(Norm).Where(d => d != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Игра с таким названием или из той же папки уже в списке.</summary>
    private static bool Known(List<GameEntry> games, string title, string dir = null)
    {
        string d = Norm(dir);
        return games.Any(g => string.Equals(g.Title, title, StringComparison.OrdinalIgnoreCase)
                              || (d != null && Norm(g.InstallDir) is { } gd && (gd.Equals(d, StringComparison.OrdinalIgnoreCase)
                                  || d.StartsWith(gd + "\\", StringComparison.OrdinalIgnoreCase))));
    }

    internal static string Norm(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        try { return Path.GetFullPath(dir.Replace('/', '\\').Trim().Trim('"')).TrimEnd('\\'); } catch { return null; }
    }

    private static string StorePlayed(string store, DateTime last) =>
        store + " · " + (last > DateTime.MinValue ? L.F("играли {0}", last.ToString(L.Pick("d MMM", "MMM d"))) : L.T("ещё не запускалась"));

    // ------------------------------------------------------------------ Steam
    private static string SteamRoot()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        string steam = (key?.GetValue("SteamPath") as string)?.Replace('/', '\\');
        if (string.IsNullOrEmpty(steam))
        {
            using var lm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam") ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            steam = lm?.GetValue("InstallPath") as string;
        }
        return string.IsNullOrEmpty(steam) ? null : steam;
    }

    /// <summary>Все библиотеки Steam: папка самого Steam и каждая из libraryfolders.vdf.</summary>
    private static List<string> SteamLibraries()
    {
        string steam = SteamRoot();
        var libraries = new List<string>();
        if (steam == null) return libraries;
        libraries.Add(steam);
        foreach (string vdf in new[] { Path.Combine(steam, @"steamapps\libraryfolders.vdf"), Path.Combine(steam, @"config\libraryfolders.vdf") })
        {
            if (!File.Exists(vdf)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
            {
                string lib = m.Groups[1].Value.Replace(@"\\", @"\");
                if (!libraries.Contains(lib, StringComparer.OrdinalIgnoreCase)) libraries.Add(lib);
            }
        }
        return libraries;
    }

    private static IEnumerable<GameEntry> Steam()
    {
        foreach (string library in SteamLibraries())
        {
            string apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (string manifest in Directory.GetFiles(apps, "appmanifest_*.acf"))
            {
                string text;
                try { text = File.ReadAllText(manifest); } catch { continue; }
                string id = Regex.Match(text, "\"appid\"\\s+\"(\\d+)\"").Groups[1].Value;
                string name = Regex.Match(text, "\"name\"\\s+\"([^\"]+)\"").Groups[1].Value;
                string dir = Regex.Match(text, "\"installdir\"\\s+\"([^\"]+)\"").Groups[1].Value;
                if (id == "" || name == "" || NotGames.Any(x => name.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0)) continue;

                long.TryParse(Regex.Match(text, "\"LastPlayed\"\\s+\"(\\d+)\"").Groups[1].Value, out long played);
                DateTime last = played > 0 ? DateTimeOffset.FromUnixTimeSeconds(played).LocalDateTime : DateTime.MinValue;
                yield return new GameEntry
                {
                    Title = name,
                    Caption = StorePlayed("Steam", last),
                    Target = "steam://rungameid/" + id,
                    LastPlayed = last,
                    InstallDir = dir == "" ? null : Path.Combine(apps, "common", dir),
                    Source = "steam",
                };
            }
        }
    }

    // ------------------------------------------------------------------ Riot
    // Игры Riot (VALORANT, League of Legends) записаны в «Установке и удалении программ» как «Riot Game <продукт>.<канал>»:
    // там и папка игры, и путь к Riot Client — где бы его ни установили.
    private static void AddRiot(List<GameEntry> games)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var uninstall = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall == null) continue;
            foreach (string name in uninstall.GetSubKeyNames().Where(n => n.StartsWith("Riot Game ", StringComparison.OrdinalIgnoreCase)))
            {
                using var k = uninstall.OpenSubKey(name);
                string title = (k?.GetValue("DisplayName") as string)?.Trim();
                string dir = (k?.GetValue("InstallLocation") as string)?.Replace('/', '\\');
                string uninst = k?.GetValue("UninstallString") as string ?? "";
                var m = Regex.Match(uninst, "\"([^\"]+RiotClientServices\\.exe)\".*--uninstall-product=(\\w+).*--uninstall-patchline=(\\w*)");
                if (string.IsNullOrEmpty(title) || title == "Riot Client" || !m.Success || !Directory.Exists(dir) || games.Any(g => g.Title == title)) continue;
                DateTime last = DateTime.MinValue;
                try { last = Directory.GetLastWriteTime(dir); } catch { }
                games.Add(new GameEntry
                {
                    Title = title, Caption = "Riot Client", Target = m.Groups[1].Value,
                    Arguments = $"--launch-product={m.Groups[2].Value} --launch-patchline={(m.Groups[3].Value == "" ? "live" : m.Groups[3].Value)}",
                    LastPlayed = last, InstallDir = dir, Source = "riot",
                });
            }
        }
    }

    // ------------------------------------------------------------------ Epic
    // Игры Epic Games: у каждой установленной — файл-описание в общей папке лаунчера.
    private static void AddEpic(List<GameEntry> games)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
        if (!Directory.Exists(dir)) return;
        foreach (string file in Directory.GetFiles(dir, "*.item"))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : "";
                string title = S("DisplayName"), location = S("InstallLocation"), app = S("AppName");
                bool isApp = r.TryGetProperty("bIsApplication", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.True;
                if (title == "" || !isApp || !Directory.Exists(location) || games.Any(g => g.Title == title)) continue;
                if (NotGames.Any(x => title.Contains(x, StringComparison.OrdinalIgnoreCase)) || title.StartsWith("Unreal Engine", StringComparison.OrdinalIgnoreCase)) continue;
                games.Add(new GameEntry
                {
                    Title = title, Caption = "Epic Games",
                    Target = $"com.epicgames.launcher://apps/{app}?action=launch&silent=true",
                    InstallDir = location, Source = "epic",
                });
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ GOG
    // GOG Galaxy и офлайн-установщики GOG записывают каждую игру в HKLM\SOFTWARE\GOG.com\Games\<номер>: название, папку и файл запуска.
    // Дополнения (dependsOn) — не отдельные игры.
    private static void AddGog(List<GameEntry> games)
    {
        foreach (string root in new[] { @"SOFTWARE\WOW6432Node\GOG.com\Games", @"SOFTWARE\GOG.com\Games" })
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key == null) continue;
            foreach (string id in key.GetSubKeyNames())
            {
                using var k = key.OpenSubKey(id);
                string title = (k?.GetValue("gameName") as string)?.Trim();
                string dir = k?.GetValue("path") as string;
                string exe = k?.GetValue("exe") as string;
                if (string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(k.GetValue("dependsOn") as string)) continue;
                if (!File.Exists(exe) || Known(games, title, dir)) continue;
                games.Add(new GameEntry
                {
                    Title = title, Caption = "GOG", Target = exe, Arguments = (k.GetValue("launchParam") as string ?? "").Trim(),
                    InstallDir = dir, Source = "gog",
                });
            }
        }
    }

    // ------------------------------------------------------------------ Ubisoft Connect
    // HKLM\SOFTWARE\Ubisoft\Launcher\Installs\<номер> — папка игры; название — в записи «Uplay Install <номер>» в «Установке и удалении программ».
    private static void AddUbisoft(List<GameEntry> games)
    {
        foreach (string root in new[] { @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs", @"SOFTWARE\Ubisoft\Launcher\Installs" })
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key == null) continue;
            foreach (string id in key.GetSubKeyNames())
            {
                using var k = key.OpenSubKey(id);
                string dir = (k?.GetValue("InstallDir") as string)?.Replace('/', '\\');
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                string title = UninstallName("Uplay Install " + id) ?? Path.GetFileName(dir.TrimEnd('\\'));
                if (string.IsNullOrEmpty(title) || Known(games, title, dir)) continue;
                games.Add(new GameEntry { Title = title, Caption = "Ubisoft Connect", Target = $"uplay://launch/{id}/0", InstallDir = dir, Source = "ubisoft" });
            }
        }
    }

    private static string UninstallName(string keyName)
    {
        foreach (string root in new[] { @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var k = Registry.LocalMachine.OpenSubKey(root + "\\" + keyName);
            if (k?.GetValue("DisplayName") is string s && s.Trim().Length > 0) return s.Trim();
        }
        return null;
    }

    // ------------------------------------------------------------------ Xbox / Game Pass
    // Игры приложения Xbox ставятся в папку XboxGames на любом диске (список папок — в скрытом файле .GamingRoot в корне диска).
    // В каждой игре — Content\MicrosoftGame.config: название, пакет и файл запуска. Запуск — через «shell:AppsFolder\<пакет>!<приложение>».
    private static void AddXbox(List<GameEntry> games)
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                foreach (string folder in GamingRoots(drive.RootDirectory.FullName))
                {
                    if (!Directory.Exists(folder)) continue;
                    foreach (string game in Directory.EnumerateDirectories(folder))
                    {
                        string config = Path.Combine(game, "Content", "MicrosoftGame.config");
                        if (!File.Exists(config)) continue;
                        var x = XboxConfig(File.ReadAllText(config));
                        string title = x.Title is { Length: > 0 } t && !t.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase) ? t : Path.GetFileName(game);
                        string content = Path.Combine(game, "Content");
                        if (Known(games, title, content)) continue;
                        string target = x.Name != null && x.Publisher != null
                            ? $"shell:AppsFolder\\{x.Name}_{PublisherId(x.Publisher)}!{x.AppId ?? "Game"}"
                            : x.Exe != null ? Path.Combine(content, x.Exe) : null;
                        if (target == null) continue;
                        games.Add(new GameEntry { Title = title, Caption = "Xbox", Target = target, InstallDir = content, Source = "xbox" });
                    }
                }
            }
            catch { }
        }
    }

    /// <summary>Папки игр Xbox на диске: из файла .GamingRoot («RGBX», число, строки UTF-16 с нулём на конце), иначе «XboxGames».</summary>
    internal static List<string> GamingRoots(string driveRoot)
    {
        var list = new List<string>();
        try
        {
            string file = Path.Combine(driveRoot, ".GamingRoot");
            if (File.Exists(file)) list.AddRange(ParseGamingRoot(File.ReadAllBytes(file)).Select(r => Path.Combine(driveRoot, r.TrimStart('\\'))));
        }
        catch { }
        string standard = Path.Combine(driveRoot, "XboxGames");
        if (!list.Contains(standard, StringComparer.OrdinalIgnoreCase)) list.Add(standard);
        return list;
    }

    internal static List<string> ParseGamingRoot(byte[] data)
    {
        var list = new List<string>();
        if (data == null || data.Length < 10 || Encoding.ASCII.GetString(data, 0, 4) != "RGBX") return list;
        string text = Encoding.Unicode.GetString(data, 8, (data.Length - 8) / 2 * 2);
        foreach (string part in text.Split('\0'))
        {
            string p = part.Trim();
            if (p.Length > 0 && p.IndexOfAny(Path.GetInvalidPathChars()) < 0) list.Add(p);
        }
        return list;
    }

    internal static (string Title, string Name, string Publisher, string AppId, string Exe) XboxConfig(string xml)
    {
        var doc = XDocument.Parse(xml);
        XElement First(string local) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == local);
        var identity = First("Identity");
        var exe = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Executable");
        var visuals = First("ShellVisuals");
        return ((string)visuals?.Attribute("DefaultDisplayName"), (string)identity?.Attribute("Name"), (string)identity?.Attribute("Publisher"),
                (string)exe?.Attribute("Id"), (string)exe?.Attribute("Name"));
    }

    /// <summary>
    /// Короткий код издателя в имени пакета Microsoft Store (13 знаков, «8wekyb3d8bbwe» у Microsoft): первые 8 байт SHA-256
    /// от строки издателя в UTF-16, дополненные одним нулевым битом и записанные по 5 бит алфавитом Крокфорда.
    /// </summary>
    internal static string PublisherId(string publisher)
    {
        const string alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
        byte[] hash = SHA256.HashData(Encoding.Unicode.GetBytes(publisher));
        var sb = new StringBuilder(13);
        for (int i = 0; i < 13; i++)
        {
            int value = 0;
            for (int b = 0; b < 5; b++)
            {
                int bit = i * 5 + b;                                   // 65-й бит (номер 64) — добавленный ноль
                int v = bit < 64 ? (hash[bit / 8] >> (7 - bit % 8)) & 1 : 0;
                value = (value << 1) | v;
            }
            sb.Append(alphabet[value]);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ itch.io
    // Приложение itch ставит игры в %APPDATA%\itch\apps\<папка>; в .itch\receipt.json.gz — название игры.
    private static void AddItch(List<GameEntry> games)
    {
        string apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "itch", "apps");
        if (!Directory.Exists(apps)) return;
        foreach (string dir in Directory.EnumerateDirectories(apps))
        {
            try
            {
                string title = Path.GetFileName(dir);
                string receipt = Path.Combine(dir, ".itch", "receipt.json.gz");
                if (File.Exists(receipt))
                {
                    using var gz = new System.IO.Compression.GZipStream(File.OpenRead(receipt), System.IO.Compression.CompressionMode.Decompress);
                    using var doc = System.Text.Json.JsonDocument.Parse(gz);
                    if (doc.RootElement.TryGetProperty("game", out var g) && g.TryGetProperty("title", out var t) && t.GetString() is { Length: > 0 } s) title = s;
                }
                string exe = GameFinder.MainExe(dir);
                if (exe == null || Known(games, title, dir)) continue;
                games.Add(new GameEntry { Title = title, Caption = "itch.io", Target = exe, InstallDir = dir, Source = "itch" });
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ Minecraft
    // Сама игра — Java в папке .minecraft (её запись узнаёт GameDetector); в список для запуска — лаунчер: TLauncher,
    // классический Minecraft Launcher или приложение из Microsoft Store.
    private static void AddMinecraft(List<GameEntry> games)
    {
        string mc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
        if (!Directory.Exists(mc) || Known(games, "Minecraft")) return;
        string x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), x64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string exe = new[] { Path.Combine(mc, "TLauncher.exe"), Path.Combine(x86, "Minecraft Launcher", "MinecraftLauncher.exe"), Path.Combine(x64, "Minecraft Launcher", "MinecraftLauncher.exe") }
            .FirstOrDefault(File.Exists);
        string target = exe, caption = exe == null ? null : exe.EndsWith("TLauncher.exe", StringComparison.OrdinalIgnoreCase) ? "TLauncher" : "Minecraft Launcher";
        if (target == null)
        {
            using var store = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (store?.GetSubKeyNames().Any(n => n.StartsWith("Microsoft.4297127D64EC6_", StringComparison.OrdinalIgnoreCase)) == true)
            {
                target = @"shell:AppsFolder\Microsoft.4297127D64EC6_8wekyb3d8bbwe!Minecraft";
                caption = "Minecraft Launcher";
            }
        }
        if (target == null) return;
        games.Add(new GameEntry { Title = "Minecraft", Caption = caption, Target = target, InstallDir = mc, Source = "minecraft" });
    }

    // ------------------------------------------------------------------ вручную, найденные в фоне
    // Игры, добавленные вручную на вкладке «Сессии».
    private static void AddCustom(List<GameEntry> games)
    {
        foreach (var g in AppSettings.CurrentCustom())
        {
            if (string.IsNullOrWhiteSpace(g.Name) || !File.Exists(g.Exe) || games.Any(x => x.Title.Equals(g.Name, StringComparison.OrdinalIgnoreCase))) continue;
            DateTime last = DateTime.MinValue;
            try { last = File.GetLastAccessTime(g.Exe); } catch { }
            games.Add(new GameEntry
            {
                Title = g.Name, Caption = L.T("добавлена вручную"), Target = g.Exe,
                LastPlayed = last, InstallDir = Path.GetDirectoryName(g.Exe), Source = "custom",
            });
        }
    }

    // Найденные в фоне: игры магазинов по записям «Установки и удаления программ», игры на дисках, найденные по поведению.
    private static void AddFound(List<GameEntry> games)
    {
        foreach (var f in GameFinder.Found())
        {
            if (string.IsNullOrEmpty(f.Title) || !File.Exists(f.Exe) || Known(games, f.Title, f.Source == "behavior" ? null : f.Dir)) continue;
            string caption = f.Source switch
            {
                "behavior" => L.T("найдена по поведению"),
                "store" when f.Store is { Length: > 0 } => f.Store,
                _ => L.F("найдена на диске {0}", (Path.GetPathRoot(f.Dir) ?? "").TrimEnd('\\')),
            };
            games.Add(new GameEntry
            {
                Title = f.Title, Caption = caption, Target = f.Exe, InstallDir = f.Source == "behavior" ? Path.GetDirectoryName(f.Exe) : f.Dir,
                Source = f.Source,
            });
        }
    }

    // Игры Astrum Play (бывший игровой центр VK Play) записаны в «Установке и удалении программ» под ключами вида gcgame_<номер>.
    private static void AddAstrum(List<GameEntry> games)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var uninstall = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall == null) continue;
            foreach (string name in uninstall.GetSubKeyNames().Where(n => n.StartsWith("gcgame_", StringComparison.OrdinalIgnoreCase)))
            {
                using var k = uninstall.OpenSubKey(name);
                string title = k?.GetValue("DisplayName") as string;
                if (string.IsNullOrEmpty(title) || games.Any(g => g.Title == title)) continue;
                string uninst = k.GetValue("UninstallString") as string ?? "";
                // Старые установки игрового центра VK Play открывают его по адресу vkplay://, новые — astrumplay://.
                bool vk = uninst.Contains("vkplay://", StringComparison.OrdinalIgnoreCase) || uninst.Contains("gamecenter", StringComparison.OrdinalIgnoreCase);
                games.Add(new GameEntry
                {
                    Title = title, Caption = vk ? "VK Play" : "Astrum Play",
                    Target = (vk ? "vkplay://play/" : "astrumplay://play/") + name["gcgame_".Length..],
                    InstallDir = k.GetValue("InstallLocation") as string, Source = "astrum",
                });
            }
        }
    }

    // ------------------------------------------------------------------ запуск
    public static void Launch(GameEntry game)
    {
        if (string.IsNullOrEmpty(game.Arguments)) OpenAsUser(game.Target);
        else
        {
            // Проводник не передаёт программе параметры, поэтому запускаем через временный ярлык. Имя случайное: папка TEMP
            // доступна пользователю на запись, и подложенная по известному имени ссылка перенаправила бы запись программы
            // с правами администратора в чужой файл. Ярлык удаляется через минуту, когда игра уже запущена.
            string lnk = Path.Combine(Path.GetTempPath(), "PulsePC-launch-" + Guid.NewGuid().ToString("N") + ".lnk");
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic shortcut = shell.CreateShortcut(lnk);
            shortcut.TargetPath = game.Target;
            shortcut.Arguments = game.Arguments;
            shortcut.WorkingDirectory = Path.GetDirectoryName(game.Target);
            shortcut.Save();
            OpenAsUser(lnk);
            DeleteLater(lnk);
        }
    }

    /// <summary>
    /// Открыть адрес или программу через Проводник. Так запуск идёт с обычными правами пользователя,
    /// а не с правами администратора, с которыми работает сама программа: игры и лаунчеры не должны их наследовать.
    /// Проводник обрезает свой параметр на 259 знаках, поэтому длинный адрес (письмо обратной связи с текстом)
    /// открывается через временный файл-ссылку .url: его путь короткий, а адрес внутри — целиком.
    /// </summary>
    public static void OpenAsUser(string target)
    {
        if (NeedsShortcut(target))
        {
            string url = Path.Combine(Path.GetTempPath(), "PulsePC-link-" + Guid.NewGuid().ToString("N") + ".url");
            // CreateNew: имя случайное, и файл с таким именем (подложенный заранее) не перезаписывается.
            using (var fs = new FileStream(url, FileMode.CreateNew, FileAccess.Write))
            {
                byte[] bytes = Encoding.ASCII.GetBytes(UrlShortcutText(target));
                fs.Write(bytes, 0, bytes.Length);
            }
            StartExplorer(url);
            DeleteLater(url);
            return;
        }
        StartExplorer(target);
    }

    private static void StartExplorer(string target) =>
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + target + "\"") { UseShellExecute = false })?.Dispose();     // описатель процесса не держим

    private static void DeleteLater(string file) =>
        _ = Task.Delay(TimeSpan.FromMinutes(1)).ContinueWith(_ => { try { File.Delete(file); } catch { } });

    /// <summary>Адрес (не путь к файлу) длиннее, чем Проводник передаёт без обрезки.</summary>
    internal static bool NeedsShortcut(string target) =>
        target != null && target.Length > 200 && Regex.IsMatch(target, @"^[A-Za-z][A-Za-z0-9+.\-]+:");

    /// <summary>Содержимое файла .url. Адрес — только ASCII: всё прочее и переводы строк кодируются %XX (UTF-8).</summary>
    internal static string UrlShortcutText(string url)
    {
        var sb = new StringBuilder(url.Length + 32);
        foreach (byte b in Encoding.UTF8.GetBytes(url))
        {
            if (b < 0x20 || b >= 0x7F) sb.Append('%').Append(b.ToString("X2"));
            else sb.Append((char)b);
        }
        return "[InternetShortcut]\r\nURL=" + sb + "\r\n";
    }
}
