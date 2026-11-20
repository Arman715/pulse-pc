using System.Diagnostics;
using System.Text.RegularExpressions;
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
}

/// <summary>Установленные игры: библиотеки Steam, Valorant, игры Astrum Play.</summary>
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
        return games.OrderByDescending(g => g.LastPlayed).ThenBy(g => g.Title).ToList();
    }

    private static IEnumerable<GameEntry> Steam()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        string steam = (key?.GetValue("SteamPath") as string)?.Replace('/', '\\');
        if (string.IsNullOrEmpty(steam)) yield break;

        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steam };
        string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));

        foreach (string library in libraries)
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
                    Caption = "Steam · " + (played > 0 ? "играли " + last.ToString("d MMM") : "ещё не запускалась"),
                    Target = "steam://rungameid/" + id,
                    LastPlayed = last,
                    InstallDir = dir == "" ? null : Path.Combine(apps, "common", dir),
                };
            }
        }
    }

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
                    LastPlayed = last, InstallDir = dir,
                });
            }
        }
    }

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
                    InstallDir = location,
                });
            }
            catch { }
        }
    }

    // Игры Astrum Play записаны в «Установке и удалении программ» под ключами вида gcgame_<номер>.
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
                games.Add(new GameEntry
                {
                    Title = title, Caption = "Astrum Play", Target = "astrumplay://play/" + name["gcgame_".Length..],
                    InstallDir = k.GetValue("InstallLocation") as string,
                });
            }
        }
    }

    public static void Launch(GameEntry game)
    {
        if (string.IsNullOrEmpty(game.Arguments)) OpenAsUser(game.Target);
        else
        {
            // Проводник не передаёт программе параметры, поэтому запускаем через временный ярлык.
            string lnk = Path.Combine(Path.GetTempPath(), "PulsePC-launch.lnk");
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic shortcut = shell.CreateShortcut(lnk);
            shortcut.TargetPath = game.Target;
            shortcut.Arguments = game.Arguments;
            shortcut.WorkingDirectory = Path.GetDirectoryName(game.Target);
            shortcut.Save();
            OpenAsUser(lnk);
        }
    }

    /// <summary>
    /// Открыть адрес или программу через Проводник. Так запуск идёт с обычными правами пользователя,
    /// а не с правами администратора, с которыми работает сама программа: игры и лаунчеры не должны их наследовать.
    /// </summary>
    public static void OpenAsUser(string target)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + target + "\"") { UseShellExecute = false });
    }
}
