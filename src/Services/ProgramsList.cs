using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PulsePC.Services;

/// <summary>Что можно сделать с программой из списка.</summary>
public enum ProgramKind
{
    /// <summary>Обычная программа: «Удалить» запускает её деинсталлятор.</summary>
    Normal,
    /// <summary>Компонент, нужный другим программам и играм (Visual C++, .NET, DirectX…): удалить можно, но с предупреждением.</summary>
    Runtime,
    /// <summary>Антивирус или защитная программа: кнопки нет — удалять их установщиком или средством производителя.</summary>
    Security,
    /// <summary>Драйвер видеокарты, чипсета, звука, сети: кнопки нет — удалять их установщиком.</summary>
    Driver,
    /// <summary>Сам «Пульс ПК»: удаляется в «Параметрах» Windows.</summary>
    Self,
    /// <summary>Деинсталлятора нет или он запрещён (NoRemove).</summary>
    NoUninstaller,
}

/// <summary>Программа из «Установки и удаления программ» (раздел реестра …\Uninstall).</summary>
public sealed class InstalledProgram
{
    public string Name { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTime? Installed { get; set; }
    public long SizeBytes { get; set; }
    /// <summary>HKLM64, HKLM32 или HKCU.</summary>
    public string Hive { get; set; } = "HKLM64";
    public string KeyName { get; set; } = "";
    public string UninstallString { get; set; } = "";
    public string InstallLocation { get; set; } = "";
    public bool SystemComponent { get; set; }
    public string ParentKeyName { get; set; } = "";
    public string ReleaseType { get; set; } = "";
    public bool NoRemove { get; set; }
    public ProgramKind Kind { get; set; }
    /// <summary>Установлена для всех пользователей: удалять — только с правами администратора.</summary>
    public bool Machine => !Hive.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Приложение Microsoft Store (только для просмотра: удаляется в «Параметрах → Приложения»).</summary>
public sealed class StoreApp
{
    public string Name { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTime? Installed { get; set; }
    public string FullName { get; set; } = "";
}

/// <summary>
/// Раздел «Установленные программы»: список из HKLM (64 и 32 бита) и HKCU …\Windows\CurrentVersion\Uninstall — имя, издатель, версия,
/// дата установки, размер. Скрыто то же, что скрывает Windows: системные компоненты, обновления KB, части других программ (ParentKeyName).
/// «Удалить» запускает официальный деинсталлятор программы (UninstallString, обычный, не тихий режим) — только после подтверждения.
/// Антивирусы и защитные программы, драйверы и сам «Пульс ПК» кнопки не получают. Приложения Microsoft Store — отдельным списком,
/// только просмотр (их удаляют в «Параметрах»).
/// Для проверки без настоящего удаления: PULSEPC_PROGRAMS_FAKE=&lt;файл.json&gt; — подставной список, деинсталлятор тогда не запускается
/// (как и при PULSEPC_UNINSTALL_DRYRUN=1).
/// </summary>
public static class ProgramsList
{
    private const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public static string FakeFile => Environment.GetEnvironmentVariable("PULSEPC_PROGRAMS_FAKE") is { Length: > 0 } f ? f : null;
    public static bool DryRun => FakeFile != null || Environment.GetEnvironmentVariable("PULSEPC_UNINSTALL_DRYRUN") == "1";

    private sealed class FakeData
    {
        public List<InstalledProgram> Programs { get; set; } = new();
        public List<StoreApp> Store { get; set; } = new();
    }

    private static FakeData Fake()
    {
        try { return JsonSerializer.Deserialize<FakeData>(File.ReadAllText(FakeFile), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (Exception ex) { Log.Error("подставной список программ", ex); return new FakeData(); }
    }

    // ================================================================== чтение
    /// <summary>Видимые программы (как в «Параметрах»): без системных компонентов, обновлений и частей других программ, без повторов.</summary>
    public static List<InstalledProgram> Load()
    {
        var raw = FakeFile != null ? Fake().Programs : ReadRegistry();
        return Visible(raw);
    }

    internal static List<InstalledProgram> Visible(IEnumerable<InstalledProgram> raw)
    {
        var list = new List<InstalledProgram>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in raw)
        {
            if (Hidden(p)) continue;
            // Одна программа в 64- и 32-битной ветке (или дважды в одной) — одна строка.
            if (!seen.Add(p.Name.Trim() + "|" + p.Version.Trim() + "|" + (p.Machine ? "m" : "u"))) continue;
            p.Kind = Classify(p);
            list.Add(p);
        }
        return list;
    }

    /// <summary>То, что Windows не показывает в списке программ.</summary>
    internal static bool Hidden(InstalledProgram p)
    {
        if (string.IsNullOrWhiteSpace(p.Name)) return true;
        if (p.SystemComponent) return true;
        if (!string.IsNullOrEmpty(p.ParentKeyName)) return true;
        if (p.ReleaseType is { Length: > 0 } rt && Regex.IsMatch(rt, "update|hotfix|service ?pack", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(p.Name, @"\bKB\d{6,7}\b") || Regex.IsMatch(p.KeyName ?? "", @"^KB\d{6,7}$", RegexOptions.IgnoreCase)) return true;
        return false;
    }

    private static List<InstalledProgram> ReadRegistry()
    {
        var list = new List<InstalledProgram>();
        foreach (var (hive, view, tag) in new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM64"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM32"),
            (RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU"),
        })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(UninstallPath);
                if (uninstall == null) continue;
                foreach (string name in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var k = uninstall.OpenSubKey(name);
                        if (k == null) continue;
                        string S(string v) => (k.GetValue(v) as string)?.Trim() ?? "";
                        long size = k.GetValue("EstimatedSize") is int kb && kb > 0 ? kb * 1024L : 0;
                        list.Add(new InstalledProgram
                        {
                            Name = S("DisplayName"), Publisher = S("Publisher"), Version = S("DisplayVersion"),
                            Installed = ParseDate(S("InstallDate")), SizeBytes = size, Hive = tag, KeyName = name,
                            UninstallString = S("UninstallString"), InstallLocation = S("InstallLocation"),
                            SystemComponent = k.GetValue("SystemComponent") is int sc && sc == 1,
                            ParentKeyName = S("ParentKeyName"), ReleaseType = S("ReleaseType"),
                            NoRemove = k.GetValue("NoRemove") is int nr && nr == 1,
                        });
                    }
                    catch { }
                }
            }
            catch { }
        }
        return list;
    }

    /// <summary>Дата установки из реестра: «20261117», иногда «17.11.2026» или «2026-11-17».</summary>
    internal static DateTime? ParseDate(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTime.TryParseExact(s.Trim(), new[] { "yyyyMMdd", "yyyy-MM-dd", "dd.MM.yyyy", "M/d/yyyy", "MM/dd/yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            && d.Year > 1995 && d <= DateTime.Now.AddDays(2)) return d;
        return null;
    }

    // ================================================================== что можно удалять
    private static readonly string[] SecurityParts =
    {
        "Kaspersky", "Касперск", "ESET", "Dr.Web", "Doctor Web", "Avast", "AVG ", "AVG Technologies", "Avira", "Bitdefender", "Norton", "McAfee", "Malwarebytes",
        "Sophos", "Trend Micro", "Panda Security", "Comodo", "360 Total Security", "Qihoo", "Windows Defender", "Microsoft Defender", "F-Secure", "G DATA", "Emsisoft",
        "Webroot", "BullGuard", "Quick Heal", "K7 ", "Zillya", "Secure Endpoint", "CrowdStrike", "SentinelOne", "Cylance", "Carbon Black", "Antivirus", "Anti-Virus",
        "Антивирус", "Internet Security", "Total Security", "Endpoint Security", "Endpoint Protection",
    };

    private static readonly string[] DriverVendors = { "NVIDIA", "Advanced Micro Devices", "AMD", "Intel", "Realtek", "Qualcomm", "MediaTek", "Broadcom", "Killer", "Rivet" };
    private static readonly string[] DriverParts =
    {
        "Driver", "Драйвер", "Chipset", "Management Engine", "Serial IO", "Rapid Storage", "Graphics", "Графическ", "Radeon", "AMD Software", "Bluetooth",
        "Wireless", "Wi-Fi", "Ethernet", "Audio", "Аудио", "HD Audio", "Thunderbolt", "Optane", "Dynamic Tuning", "Innovation Platform",
    };

    private static readonly string[] RuntimeParts =
    {
        "Visual C++", "Redistributable", ".NET", "Desktop Runtime", "ASP.NET", "WebView2", "DirectX", "Java ", "Java(TM)", "Microsoft GameInput",
        "XNA Framework", "OpenAL", "PhysX", "Vulkan Run", "Windows App Runtime", "Windows App SDK",
    };

    internal static ProgramKind Classify(InstalledProgram p)
    {
        string name = p.Name ?? "", pub = p.Publisher ?? "";
        if (IsSelf(p)) return ProgramKind.Self;
        if (SecurityParts.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase) || pub.Contains(s, StringComparison.OrdinalIgnoreCase))) return ProgramKind.Security;
        if (DriverVendors.Any(v => pub.Contains(v, StringComparison.OrdinalIgnoreCase) || name.StartsWith(v, StringComparison.OrdinalIgnoreCase))
            && DriverParts.Any(d => name.Contains(d, StringComparison.OrdinalIgnoreCase))
            && !name.Contains("PhysX", StringComparison.OrdinalIgnoreCase) && !name.Contains("App", StringComparison.Ordinal)
            && !name.Contains("FrameView", StringComparison.OrdinalIgnoreCase)) return ProgramKind.Driver;
        if (p.NoRemove || string.IsNullOrWhiteSpace(p.UninstallString)) return ProgramKind.NoUninstaller;
        if (RuntimeParts.Any(r => name.Contains(r, StringComparison.OrdinalIgnoreCase))) return ProgramKind.Runtime;
        return ProgramKind.Normal;
    }

    private static bool IsSelf(InstalledProgram p)
    {
        if (p.Name is "Пульс ПК" or "Pulse PC" || p.KeyName.Equals("PulsePC", StringComparison.OrdinalIgnoreCase)) return true;
        string here = AppContext.BaseDirectory.TrimEnd('\\');
        return p.InstallLocation.Length > 3 && here.StartsWith(p.InstallLocation.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
               && File.Exists(Path.Combine(p.InstallLocation, "PulsePC.exe"));
    }

    /// <summary>Почему у программы нет кнопки «Удалить» (или null — кнопка есть).</summary>
    public static string NoButtonReason(InstalledProgram p) => p.Kind switch
    {
        ProgramKind.Security => L.T("Защитная программа: удаляйте её средством самого производителя, иначе в системе останутся её драйверы."),
        ProgramKind.Driver => L.T("Драйвер: удаляйте и обновляйте его установщиком производителя (для видеокарты — раздел «Драйвер»)."),
        ProgramKind.Self => L.T("Сам «Пульс ПК» удаляется в «Параметрах» Windows: «Приложения → Установленные приложения»."),
        ProgramKind.NoUninstaller => L.T("Программа не оставила деинсталлятора — удалить её можно только в «Параметрах» Windows или её установщиком."),
        _ => null,
    };

    // ================================================================== деинсталлятор
    /// <summary>
    /// Разбор строки удаления в файл и параметры. Кавычки, пути с пробелами без кавычек («C:\Program Files\X\uninstall.exe /arg»),
    /// rundll32/msiexec без пути. MsiExec /I{код} заменяется на /X{код} — удаление, а не «изменение» (обычный режим, с окнами).
    /// </summary>
    public static (string File, string Args)? ParseCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string c = command.Trim();
        string file, args;
        if (c.StartsWith('"'))
        {
            int end = c.IndexOf('"', 1);
            if (end < 0) return null;
            file = c[1..end];
            args = c[(end + 1)..].Trim();
        }
        else
        {
            // Без кавычек: самый короткий префикс, который заканчивается на .exe и существует, иначе — до первого пробела.
            file = null; args = "";
            foreach (Match m in Regex.Matches(c, @"\.exe(?=\s|$)", RegexOptions.IgnoreCase))
            {
                string candidate = c[..(m.Index + 4)];
                if (File.Exists(candidate) || !candidate.Contains('\\')) { file = candidate; args = c[(m.Index + 4)..].Trim(); break; }
            }
            if (file == null)
            {
                int sp = c.IndexOf(' ');
                file = sp < 0 ? c : c[..sp];
                args = sp < 0 ? "" : c[(sp + 1)..].Trim();
            }
        }
        string bare = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
        if (!file.Contains('\\') && bare is "msiexec" or "rundll32" or "cmd" or "regsvr32")
            file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), bare + ".exe");
        if (bare == "msiexec")
        {
            var code = Regex.Match(args, @"\{[0-9A-Fa-f\-]{36}\}");
            if (!code.Success) return null;
            args = "/X" + code.Value;
        }
        return (file, args);
    }

    /// <summary>
    /// Запустить деинсталлятор программы (обычный, не тихий режим). Возвращает текст ошибки или null.
    /// started — описание запущенного (для проверки и журнала). Не вызывать без подтверждения пользователя.
    /// </summary>
    public static string Uninstall(InstalledProgram p, bool admin, out string started)
    {
        started = null;
        if (p.Kind is ProgramKind.Security or ProgramKind.Driver or ProgramKind.Self or ProgramKind.NoUninstaller) return NoButtonReason(p);
        if (p.Machine && !admin) return L.T("Программа установлена для всех пользователей — удалять её можно только с правами администратора.");
        var cmd = ParseCommand(p.UninstallString);
        if (cmd == null) return L.T("Не удалось разобрать команду удаления этой программы. Удалите её в «Параметрах» Windows.");
        var (file, args) = cmd.Value;
        started = $"\"{file}\" {args}".Trim();
        // С правами администратора деинсталлятор наследовал бы их. Напрямую так запускаются только MsiExec /X{код} и файлы из
        // Program Files (писать туда может только администратор). Программы «только для вас» и деинсталляторы из папок, куда может
        // писать обычный пользователь (D:\Games, профиль, ProgramData), а также rundll32/cmd с чужими файлами в параметрах —
        // через Проводник с обычными правами: если деинсталлятору нужны права, Windows спросит сама и покажет, чей это файл.
        bool asUser = admin && !ElevationSafe(p, file);
        if (DryRun) { Log.Info("удаление программы (проверка, не запускается): " + p.Name + " → " + started + (asUser ? " [с правами пользователя]" : "")); return null; }
        if (!Path.IsPathFullyQualified(file) || !File.Exists(file)) return L.F("Деинсталлятор не найден: {0}. Удалите программу в «Параметрах» Windows.", file);
        try
        {
            Log.Info("удаление программы: " + p.Name + " → " + started + (asUser ? " [с правами пользователя]" : ""));
            if (asUser) GameLibrary.Launch(new GameEntry { Title = p.Name, Target = file, Arguments = args });
            else using (Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(file) ?? "" })) { }
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { started = null; return L.T("Запуск деинсталлятора отменён."); }
        catch (Exception ex) { started = null; return L.F("Деинсталлятор не запустился: {0}", ex.Message); }
    }

    /// <summary>
    /// Можно ли запускать деинсталлятор с правами администратора: программа для всех пользователей и это MsiExec /X{код}
    /// (берёт копию установщика из защищённой папки Windows) или файл из Program Files.
    /// </summary>
    internal static bool ElevationSafe(InstalledProgram p, string file)
    {
        if (!p.Machine || string.IsNullOrEmpty(file)) return false;
        try
        {
            string full = Path.GetFullPath(file);
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (full.Equals(Path.Combine(sys, "msiexec.exe"), StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                                         Environment.GetEnvironmentVariable("ProgramW6432") })
                if (!string.IsNullOrEmpty(root) && full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>Запись программы ещё в реестре (деинсталлятор не закончил или его отменили). С подставным списком — «уже нет».</summary>
    public static bool StillInstalled(InstalledProgram p)
    {
        if (FakeFile != null) return false;
        try
        {
            var (hive, view) = p.Hive switch
            {
                "HKLM32" => (RegistryHive.LocalMachine, RegistryView.Registry32),
                "HKCU" => (RegistryHive.CurrentUser, RegistryView.Registry64),
                _ => (RegistryHive.LocalMachine, RegistryView.Registry64),
            };
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var k = baseKey.OpenSubKey(UninstallPath + "\\" + p.KeyName);
            return k != null;
        }
        catch { return true; }
    }

    // ================================================================== Microsoft Store
    /// <summary>
    /// Приложения Microsoft Store текущего пользователя — по хранилищу пакетов в реестре (без PowerShell): пакеты с приложениями,
    /// без библиотек (Framework=1), языковых частей и системных приложений из C:\Windows\SystemApps.
    /// </summary>
    public static List<StoreApp> LoadStore()
    {
        if (FakeFile != null) return Fake().Store;
        var list = new List<StoreApp>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (root == null) return list;
            string sysApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps");
            foreach (string full in root.GetSubKeyNames())
            {
                try
                {
                    using var k = root.OpenSubKey(full);
                    if (k == null || k.GetValue("Framework") is int fw && fw == 1 || k.SubKeyCount == 0) continue;
                    string folder = k.GetValue("PackageRootFolder") as string ?? "";
                    // «Безопасность Windows» (SecHealthUI) программа не трогает и не показывает; языковые пакеты — не приложения.
                    if (folder.StartsWith(sysApps, StringComparison.OrdinalIgnoreCase) || full.Contains("_neutral_split.", StringComparison.OrdinalIgnoreCase)
                        || full.StartsWith("Microsoft.SecHealthUI", StringComparison.OrdinalIgnoreCase)
                        || full.StartsWith("Microsoft.LanguageExperiencePack", StringComparison.OrdinalIgnoreCase)) continue;
                    string id = full.Split('_')[0];
                    string name = ResolveName(k.GetValue("DisplayName") as string, full, id);
                    if (string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase)) name = id;
                    var parts = full.Split('_');
                    DateTime? installed = null;
                    try { if (Directory.Exists(folder)) installed = Directory.GetCreationTime(folder); } catch { }
                    list.Add(new StoreApp
                    {
                        Name = name, FullName = full, Version = parts.Length > 1 ? parts[1] : "",
                        Publisher = id.Contains('.') ? id[..id.IndexOf('.')] : "", Installed = installed,
                    });
                }
                catch { }
            }
        }
        catch (Exception ex) { Log.Error("приложения Microsoft Store", ex); }
        return list.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(a => a.Version).First()).ToList();
    }

    // Название пакета: «@{пакет?ms-resource://…}» и «ms-resource:Ключ» разворачивает Windows (SHLoadIndirectString).
    private static string ResolveName(string raw, string full, string id)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (!raw.StartsWith("@{") && !raw.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return raw;
        var tries = new List<string>();
        if (raw.StartsWith("@{")) tries.Add(raw);
        else
        {
            string key = raw["ms-resource:".Length..];
            if (key.StartsWith("//")) tries.Add($"@{{{full}?ms-resource:{key}}}");
            else
            {
                tries.Add($"@{{{full}?ms-resource://{id}/resources/{key}}}");
                tries.Add($"@{{{full}?ms-resource://{id}/{key}}}");
            }
        }
        foreach (string t in tries)
        {
            var sb = new StringBuilder(512);
            if (SHLoadIndirectString(t, sb, sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0) return sb.ToString();
        }
        return null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output, int size, IntPtr reserved);

    // ================================================================== самопроверка
    /// <summary>
    /// Самопроверка на подставном списке (ничего не удаляется, деинсталляторы не запускаются): что скрывается, что получает кнопку,
    /// разбор строк удаления. Затем — что видно на этом ПК (только чтение реестра).
    /// </summary>
    public static string SelfTest()
    {
        var fails = new List<string>();
        var lines = new List<string>();
        void Check(string what, bool ok) { if (!ok) fails.Add(what); }

        InstalledProgram P(string name, string pub = "", string uninst = "\"C:\\X\\unins000.exe\"", string hive = "HKLM64", bool sys = false, string parent = "",
                           string rt = "", string key = "") =>
            new() { Name = name, Publisher = pub, UninstallString = uninst, Hive = hive, SystemComponent = sys, ParentKeyName = parent, ReleaseType = rt, KeyName = key == "" ? name : key };
        var raw = new List<InstalledProgram>
        {
            P("7-Zip 24.08 (x64)", "Igor Pavlov"),
            P("7-Zip 24.08 (x64)", "Igor Pavlov", hive: "HKLM32"),           // та же программа во второй ветке — одна строка
            P("Microsoft Visual C++ 2022 X64 Minimum Runtime", sys: true),
            P("Security Update for Microsoft Office (KB5002700)", "Microsoft", rt: "Security Update"),
            P("Обновление KB5034441", key: "KB5034441"),
            P("Часть пакета", parent: "{00000000-0000-0000-0000-000000000000}"),
            P("", "Пусто"),
            P("Kaspersky Free", "АО «Лаборатория Касперского»"),
            P("ESET Security", "ESET, spol. s r.o."),
            P("NVIDIA Графический драйвер 621.24", "NVIDIA Corporation"),
            P("NVIDIA App 11.0.9.585", "NVIDIA Corporation"),
            P("NVIDIA Системное программное обеспечение PhysX 9.26.0904", "NVIDIA Corporation"),
            P("Intel(R) Chipset Device Software", "Intel(R) Corporation"),
            P("Realtek Audio Driver", "Realtek Semiconductor Corp."),
            P("AMD Software", "Advanced Micro Devices, Inc."),
            P("Microsoft Visual C++ v14 Redistributable (x64) - 14.51.36247", "Microsoft Corporation"),
            P("Пульс ПК", Installer.Publisher, key: "PulsePC"),          // издатель — латиницей (Installer.Publisher), узнаётся по разделу
            P("Без деинсталлятора", "Кто-то", uninst: ""),
            P("Discord", "Discord Inc.", hive: "HKCU"),
        };
        var visible = Visible(raw);
        ProgramKind KindOf(string n) => visible.FirstOrDefault(p => p.Name == n)?.Kind ?? (ProgramKind)(-1);
        Check("повтор в 64- и 32-битной ветке — одна строка", visible.Count(p => p.Name.StartsWith("7-Zip")) == 1);
        Check("скрыты системные компоненты, обновления KB, части других программ и записи без имени",
            !visible.Any(p => p.Name.Contains("Minimum Runtime") || p.Name.Contains("KB5") || p.Name == "Часть пакета" || p.Name == ""));
        Check("антивирусы без кнопки", KindOf("Kaspersky Free") == ProgramKind.Security && KindOf("ESET Security") == ProgramKind.Security);
        Check("драйверы без кнопки", KindOf("NVIDIA Графический драйвер 621.24") == ProgramKind.Driver && KindOf("Intel(R) Chipset Device Software") == ProgramKind.Driver
                                     && KindOf("Realtek Audio Driver") == ProgramKind.Driver && KindOf("AMD Software") == ProgramKind.Driver);
        Check("программы производителей видеокарт, не драйверы, — с кнопкой", KindOf("NVIDIA App 11.0.9.585") == ProgramKind.Normal
                                     && KindOf("NVIDIA Системное программное обеспечение PhysX 9.26.0904") == ProgramKind.Runtime);
        Check("распространяемые пакеты — с предупреждением", KindOf("Microsoft Visual C++ v14 Redistributable (x64) - 14.51.36247") == ProgramKind.Runtime);
        Check("сам «Пульс ПК» — через «Параметры»", KindOf("Пульс ПК") == ProgramKind.Self);
        Check("без деинсталлятора — без кнопки", KindOf("Без деинсталлятора") == ProgramKind.NoUninstaller);
        Check("программа пользователя — без прав администратора", visible.First(p => p.Name == "Discord").Machine == false && visible.First(p => p.Name.StartsWith("7-Zip")).Machine);

        string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var c1 = ParseCommand("\"C:\\Program Files\\X\\unins000.exe\" /SILENTFLAG");
        Check("кавычки", c1?.File == @"C:\Program Files\X\unins000.exe" && c1?.Args == "/SILENTFLAG");
        var c2 = ParseCommand("MsiExec.exe /I{12345678-1234-1234-1234-123456789ABC}");
        Check("MsiExec /I → /X (удаление, обычный режим)", c2?.File == Path.Combine(sys32, "msiexec.exe") && c2?.Args == "/X{12345678-1234-1234-1234-123456789ABC}");
        var c3 = ParseCommand("rundll32 url.dll,FileProtocolHandler astrumplay://uninstall/0.1177?controlpanel=1");
        Check("rundll32 без пути", c3?.File == Path.Combine(sys32, "rundll32.exe") && c3?.Args.StartsWith("url.dll") == true);
        string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
        var c4 = ParseCommand(notepad + " /x y");
        Check("путь без кавычек", c4?.File == notepad && c4?.Args == "/x y");
        Check("пустая строка", ParseCommand("") == null && ParseCommand("MsiExec.exe /X") == null);
        Check("дата установки", ParseDate("20261117") == new DateTime(2026, 11, 17) && ParseDate("17.11.2026") == new DateTime(2026, 11, 17) && ParseDate("abc") == null);
        {
            string err = Uninstall(visible.First(p => p.Kind == ProgramKind.Security), true, out var st);
            Check("защитную программу не удалить даже по прямому вызову", err != null && st == null);
            err = Uninstall(visible.First(p => p.Name.StartsWith("7-Zip")), false, out st);
            Check("программу для всех пользователей без прав не удалить", err != null && st == null);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var machine = P("X", "Y"); var user = P("X", "Y", hive: "HKCU");
            Check("с правами администратора напрямую — только MsiExec и Program Files; «только для вас», D:\\Games, rundll32 — с правами пользователя",
                ElevationSafe(machine, Path.Combine(sys32, "msiexec.exe")) && ElevationSafe(machine, Path.Combine(pf, "X", "unins000.exe")) &&
                !ElevationSafe(user, Path.Combine(pf, "X", "unins000.exe")) && !ElevationSafe(machine, @"D:\Games\X\unins000.exe") &&
                !ElevationSafe(machine, Path.Combine(sys32, "rundll32.exe")) && !ElevationSafe(machine, @"C:\Users\Public\msiexec.exe") &&
                !ElevationSafe(machine, pf + "Evil\\x.exe"));
        }
        lines.Add($"Подставной список: {raw.Count} записей → видно {visible.Count}; без кнопки: {visible.Count(p => NoButtonReason(p) != null)}");

        // На этом ПК — только чтение.
        try
        {
            var real = Load();
            lines.Add($"На этом ПК: {real.Count} программ ({real.Count(p => p.Machine)} для всех пользователей, {real.Count(p => !p.Machine)} — только для этого)," +
                      $" размер известен у {real.Count(p => p.SizeBytes > 0)}");
            foreach (var g in real.GroupBy(p => p.Kind).OrderBy(g => g.Key))
                lines.Add($"  {g.Key}: {g.Count()}" + (g.Key != ProgramKind.Normal ? " — " + string.Join(", ", g.Select(p => p.Name).Take(12)) : ""));
            int unparsed = real.Count(p => p.Kind is ProgramKind.Normal or ProgramKind.Runtime && ParseCommand(p.UninstallString) == null);
            lines.Add($"  строк удаления не разобрано: {unparsed}");
            var store = LoadStore();
            lines.Add($"Приложения Microsoft Store: {store.Count} — " + string.Join(", ", store.Select(a => a.Name).OrderBy(n => n).Take(15)) + (store.Count > 15 ? "…" : ""));
        }
        catch (Exception ex) { lines.Add("чтение на этом ПК: " + ex.Message); }

        return (fails.Count == 0 ? "OK" : "FAIL: " + string.Join("; ", fails)) + "\n" + string.Join("\n", lines);
    }
}
