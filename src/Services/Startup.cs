using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PulsePC.Services;

/// <summary>Запись автозагрузки с переключателем. Включение и выключение — тем же способом, что в Диспетчере задач.</summary>
public class StartupItem : Notifier
{
    public string Title { get; set; }
    /// <summary>Файл программы и откуда запускается.</summary>
    public string Caption { get; set; }
    /// <summary>Совет: можно ли отключать.</summary>
    public string Advice { get; set; } = "";
    /// <summary>Запись нельзя менять из программы (защита Windows).</summary>
    public bool Locked { get; set; }
    public bool CanToggle => !Locked;

    internal Action<bool> Write;

    private bool enabled;
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }

    /// <summary>Установить состояние без записи в систему (при чтении и при откате после ошибки).</summary>
    internal void SetSilently(bool value) { enabled = value; Raise(nameof(Enabled)); }
}

public static class StartupManager
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunPath32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
    private const string AppModel = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    // Известные записи: понятное имя и совет.
    private static readonly (string Key, string Name, string Advice, bool Locked)[] Known =
    {
        ("SecurityHealth", "Безопасность Windows", "защита Windows — программа её не трогает", true),
        ("RtkAudUService", "Звук Realtek", "нужен для звука — лучше оставить", false),
        ("Riot Vanguard", "Riot Vanguard", "античит VALORANT — нужен для игры", false),
        ("YandexBrowserAutoLaunch", "Яндекс Браузер", "можно выключить", false),
        ("MicrosoftEdgeAutoLaunch", "Microsoft Edge", "можно выключить", false),
        ("GoogleUpdater", "Обновление Google", "обновляет программы Google — можно выключить", false),
        ("OneDrive", "OneDrive", "нужен, если пользуетесь синхронизацией", false),
        ("Discord", "Discord", "можно выключить", false),
        ("Steam", "Steam", "можно выключить", false),
        ("EpicGamesLauncher", "Epic Games Launcher", "можно выключить", false),
        ("RiotClient", "Riot Client", "можно выключить", false),
        ("AstrumPlay", "Astrum Play", "можно выключить", false),
        ("Gemini", "Gemini", "можно выключить", false),
        ("Teams", "Microsoft Teams", "можно выключить", false),
        ("RadminVPN", "Radmin VPN", "можно выключить, если не играете по сети", false),
        ("HyperXNGenuity", "HyperX NGenuity", "можно выключить", false),
        ("MSICenter", "MSI Center", "можно выключить", false),
        ("Codex", "ChatGPT / Codex", "можно выключить", false),
        ("Claude", "Claude", "можно выключить", false),
    };

    public static List<StartupItem> Load()
    {
        var items = new List<StartupItem>();
        ReadRun(items, RegistryHive.CurrentUser, RunPath, "Run", L.T("для вас"));
        ReadRun(items, RegistryHive.LocalMachine, RunPath, "Run", L.T("для всех пользователей"));
        ReadRun(items, RegistryHive.LocalMachine, RunPath32, "Run32", L.T("для всех пользователей"));
        ReadFolder(items, RegistryHive.CurrentUser, Environment.GetFolderPath(Environment.SpecialFolder.Startup), L.T("папка автозагрузки"));
        ReadFolder(items, RegistryHive.LocalMachine, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), L.T("общая папка автозагрузки"));
        ReadStoreApps(items);
        return items.OrderByDescending(i => i.Enabled).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // --- Записи реестра Run ---
    private static void ReadRun(List<StartupItem> items, RegistryHive hive, string runPath, string approvedName, string scope)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var run = root.OpenSubKey(runPath);
            if (run == null) return;
            foreach (string name in run.GetValueNames())
            {
                if (string.IsNullOrEmpty(name)) continue;
                string command = Convert.ToString(run.GetValue(name)) ?? "";
                string exe = ExePath(command);
                var (title, advice, locked) = Describe(name, exe);
                bool exists = string.IsNullOrEmpty(exe) || File.Exists(exe);
                var item = new StartupItem
                {
                    Title = title,
                    Caption = (string.IsNullOrEmpty(exe) ? command : Path.GetFileName(exe)) + " · " + scope,
                    Advice = exists ? advice : L.T("программа удалена — запись ни на что не влияет"),
                    Locked = locked,
                };
                item.SetSilently(IsApproved(hive, approvedName, name));
                item.Write = on => SetApproved(hive, approvedName, name, on);
                items.Add(item);
            }
        }
        catch { }
    }

    // --- Ярлыки в папках автозагрузки ---
    private static void ReadFolder(List<StartupItem> items, RegistryHive hive, string folder, string scope)
    {
        try
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            foreach (string file in Directory.GetFiles(folder))
            {
                string fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var (title, advice, locked) = Describe(Path.GetFileNameWithoutExtension(file), file);
                var item = new StartupItem { Title = title, Caption = fileName + " · " + scope, Advice = advice, Locked = locked };
                item.SetSilently(IsApproved(hive, "StartupFolder", fileName));
                item.Write = on => SetApproved(hive, "StartupFolder", fileName, on);
                items.Add(item);
            }
        }
        catch { }
    }

    // --- Приложения из Магазина: состояние 2 = включено, 1 = выключено пользователем ---
    private static void ReadStoreApps(List<StartupItem> items)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(AppModel);
            if (root == null) return;
            foreach (string package in root.GetSubKeyNames())
            {
                using var pkg = root.OpenSubKey(package);
                if (pkg == null) continue;
                foreach (string task in pkg.GetSubKeyNames())
                {
                    using var key = pkg.OpenSubKey(task);
                    if (key?.GetValue("State") is not int state || (state != 1 && state != 2)) continue;

                    string family = Regex.Replace(package, "_[a-z0-9]{13}$", "");
                    var (title, advice, locked) = Describe(family.Replace(".", ""), null);
                    if (title == family.Replace(".", "")) title = Regex.Replace(family, @"^[0-9A-F]{6,}\.|^[A-Za-z0-9]+\.(?=[A-Z])", "");
                    string path = AppModel + "\\" + package + "\\" + task;
                    var item = new StartupItem { Title = title, Caption = L.T("приложение из Магазина · для вас"), Advice = advice, Locked = locked };
                    item.SetSilently(state == 2);
                    item.Write = on =>
                    {
                        using var k = Registry.CurrentUser.OpenSubKey(path, true);
                        k?.SetValue("State", on ? 2 : 1, RegistryValueKind.DWord);
                    };
                    items.Add(item);
                }
            }
        }
        catch { }
    }

    private static (string Title, string Advice, bool Locked) Describe(string name, string exe)
    {
        foreach (var k in Known)
            if (name.IndexOf(k.Key, StringComparison.OrdinalIgnoreCase) >= 0) return (L.T(k.Name), L.T(k.Advice), k.Locked);

        string title = Regex.Replace(name, "_[0-9A-Fa-f]{8,}$", "");
        try
        {
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe) && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var info = FileVersionInfo.GetVersionInfo(exe);
                if (!string.IsNullOrWhiteSpace(info.FileDescription)) title = info.FileDescription.Trim();
            }
        }
        catch { }
        return (title, "", false);
    }

    /// <summary>Путь к программе из команды запуска: в кавычках либо до первого «.exe».</summary>
    private static string ExePath(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        var quoted = Regex.Match(command, "^\"([^\"]+)\"");
        if (quoted.Success) return quoted.Groups[1].Value;
        int i = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? command[..(i + 4)] : "";
    }

    // Состояние хранится в ключе StartupApproved: первый байт чётный - включено, нечётный - выключено. Нет записи - включено.
    private static bool IsApproved(RegistryHive hive, string kind, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(Approved + kind);
            return key?.GetValue(name) is not byte[] data || data.Length == 0 || data[0] % 2 == 0;
        }
        catch { return true; }
    }

    private static void SetApproved(RegistryHive hive, string kind, string name, bool on)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.CreateSubKey(Approved + kind, true);
        var data = new byte[12];
        data[0] = (byte)(on ? 2 : 3);
        if (!on) BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(data, 4);
        key.SetValue(name, data, RegistryValueKind.Binary);
    }
}
