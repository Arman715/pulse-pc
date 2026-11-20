using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PulsePC.Services;

public enum SetupMode { Install, Uninstall }

/// <summary>Что второстепенное при установке не вышло (ярлык, автозапуск).</summary>
public enum SetupWarningKind { StartMenu, Desktop, OldDesktop, Autostart, OldAutostart }

/// <summary>
/// Предупреждение установки: вид — для решений окна (где лежат ярлыки), текст «что не вышло: причина» — на языке интерфейса.
/// ToString — текст (так он попадает в журнал установки без окна).
/// </summary>
public sealed record SetupWarning(SetupWarningKind Kind, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// Как запущен файл: установщик, удаление или обычная программа (тогда Parse возвращает null).
/// Установщик — тот же PulsePC.exe: имя файла начинается с «PulsePC-Setup» или ключ --setup; удаление — --uninstall.
/// Ключи проверки без прав администратора: --setup-test &lt;папка&gt; и --uninstall-test &lt;папка&gt;.
/// --ready-event &lt;имя&gt; — событие, которым установщик сообщает запустившей его программе, что его окно открылось.
/// </summary>
public sealed class SetupRequest
{
    public SetupMode Mode;
    public bool Quiet;          // без окна (QuietUninstallString, проверка)
    public string TestDir;      // не null — проверочная установка в эту папку (HKCU, ярлыки рядом)
    public string Log;          // куда записать итог в режиме без окна
    public bool PurgeData;      // без окна: удалить и историю с настройками
    public bool NoDesktop;      // без окна: без ярлыка на рабочем столе
    public bool Autostart;      // без окна: включить автозапуск
    public string ReadyEvent;   // «PulsePC.Setup.Ready.&lt;guid&gt;» — подать знак, когда окно открылось

    public static SetupRequest Parse(string[] args, string processPath)
    {
        string Value(string key)
        {
            int i = Array.FindIndex(args, a => a.Equals(key, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        bool Has(string key) => args.Any(a => a.Equals(key, StringComparison.OrdinalIgnoreCase));

        SetupRequest r = null;
        if (Has("--uninstall-test")) r = new SetupRequest { Mode = SetupMode.Uninstall, TestDir = Value("--uninstall-test") };
        else if (Has("--uninstall")) r = new SetupRequest { Mode = SetupMode.Uninstall };
        else if (Has("--setup-test")) r = new SetupRequest { Mode = SetupMode.Install, TestDir = Value("--setup-test") };
        else if (Has("--setup") || Path.GetFileName(processPath ?? "").StartsWith("PulsePC-Setup", StringComparison.OrdinalIgnoreCase))
            r = new SetupRequest { Mode = SetupMode.Install };
        if (r == null) return null;
        if (r.TestDir != null && (r.TestDir.StartsWith("--") || !Path.IsPathFullyQualified(r.TestDir))) r.TestDir = null;
        if ((Has("--setup-test") || Has("--uninstall-test")) && r.TestDir == null) return null;   // проверочный режим без папки не запускаем
        r.Quiet = Has("--quiet");
        r.Log = Value("--log");
        r.PurgeData = Has("--purge");
        r.NoDesktop = Has("--no-desktop");
        r.Autostart = Has("--autostart");
        string ready = Value("--ready-event");
        if (ready != null && Regex.IsMatch(ready, @"^PulsePC\.Setup\.Ready\.[0-9a-f]{32}$")) r.ReadyEvent = ready;
        return r;
    }
}

/// <summary>
/// Куда ставить. Обычная установка — Program Files, меню «Пуск» для всех, рабочий стол пользователя, HKLM.
/// Проверочная — всё в отдельных папках и в HKCU, чтобы проверить установку и удаление без прав администратора.
/// </summary>
public sealed class SetupTarget
{
    public string Dir;
    public bool Test;
    public string StartMenuDir, DesktopDir, UserDataDir;
    /// <summary>Защищённая папка программы (%ProgramData%\PulsePC: копии для отката, загрузки обновлений).</summary>
    public string SecureDir;
    public RegistryHive Hive;
    public string KeyName;

    public string Exe => Path.Combine(Dir, Installer.ExeName);

    public static SetupTarget Real(string dir) => new()
    {
        Dir = Path.GetFullPath(dir),
        StartMenuDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        DesktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        UserDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulsePC"),
        SecureDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PulsePC"),
        Hive = RegistryHive.LocalMachine,
        KeyName = "PulsePC",
    };

    /// <summary>Проверка: ярлыки и «данные пользователя» — в соседней папке «&lt;папка&gt;-env», запись — в HKCU.</summary>
    public static SetupTarget ForTest(string dir)
    {
        string full = Path.GetFullPath(dir).TrimEnd('\\');
        string env = full + "-env";
        return new SetupTarget
        {
            Dir = full, Test = true,
            StartMenuDir = Path.Combine(env, "StartMenu"),
            DesktopDir = Path.Combine(env, "Desktop"),
            UserDataDir = Path.Combine(env, "UserData"),
            SecureDir = Path.Combine(env, "Secure"),
            Hive = RegistryHive.CurrentUser,
            KeyName = "PulsePC.Test",
        };
    }
}

/// <summary>
/// Установка и удаление «Пульс ПК». Ничего, кроме перечисленного здесь, не создаёт и не удаляет.
///
/// Раскладка установленной программы. Установщик (PulsePC-Setup-x.exe) — однофайловая сборка, которая при запуске распаковывает
/// свои библиотеки WPF во временную папку. Установленной программе так нельзя: она запускается с правами администратора
/// при каждом входе, а %TEMP% доступна на запись любой программе пользователя (подменённая DLL выполнилась бы с правами
/// администратора). Поэтому в установщик встроен архив PulsePC.app.zip (собирает build-release.ps1) — та же программа,
/// собранная без распаковки: PulsePC.exe и рядом её библиотеки (*_cor3.dll). Установщик кладёт их в папку установки,
/// закрытую от записи для всех, кроме администраторов и системы. Без архива (сборка для разработки) копируется сам запущенный файл.
/// </summary>
public static class Installer
{
    public const string AppName = "Пульс ПК";
    public const string ExeName = "PulsePC.exe";
    public const string ShortcutName = AppName + ".lnk";
    private const string UninstallRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ThirdPartyFile = "THIRD-PARTY.txt";
    /// <summary>Встроенный архив установленной программы (см. описание класса).</summary>
    private const string PayloadResource = "PulsePC.app.zip";
    /// <summary>Список файлов программы, положенных из архива (для удаления и следующего обновления).</summary>
    private const string FileListName = "PulsePC.files";

    /// <summary>Файлы, которые ставит установщик (сборка для разработки — ещё dll и её описания). Удаляются только они и файлы из PulsePC.files.</summary>
    private static readonly string[] AppFiles =
    {
        ExeName, "PulsePC.dll", "PulsePC.runtimeconfig.json", "PulsePC.deps.json", "PulsePC.pdb", ThirdPartyFile, FileListName,
        @"PresentMon\PresentMon.exe", @"PresentMon\LICENSE.txt",
    };

    /// <summary>Файлы сборки для разработки: при установке из архива они больше не нужны и убираются.</summary>
    private static readonly string[] DevFiles = { "PulsePC.dll", "PulsePC.runtimeconfig.json", "PulsePC.deps.json", "PulsePC.pdb" };

    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);

    /// <summary>
    /// Версия программы. Четвёртая часть показывается, если она не 0 («1.4.1.1»): иначе установленная 1.4.1.1 выглядела бы
    /// как 1.4.1 и выпуск 1.4.1.1 предлагался бы снова и снова. Сравнение — VersionCheck.CompareVersions (недостающие части = 0).
    /// </summary>
    public static string Version
    {
        get
        {
            var v = typeof(Installer).Assembly.GetName().Version ?? new Version(1, 0, 0);
            return v.Revision > 0 ? v.ToString(4) : v.ToString(3);
        }
    }

    public static string[] InstallSteps(bool autostart) => new[]
    {
        L.T("Закрыть запущенную программу"),
        L.T("Скопировать программу"),
        L.T("Распаковать PresentMon"),
        autostart ? L.T("Создать ярлыки и автозапуск") : L.T("Создать ярлыки"),
        L.T("Добавить в список установленных программ"),
    };

    public static string[] UninstallSteps(bool purge)
    {
        var steps = new List<string>
        {
            L.T("Закрыть запущенную программу"),
            L.T("Удалить ярлыки"),
            L.T("Убрать из списка программ, автозапуска и брандмауэра"),
            L.T("Удалить файлы программы и загрузки"),
        };
        if (purge) steps.Add(L.T("Удалить историю, настройки и копии для отката"));
        return steps.ToArray();
    }

    /// <summary>Подать знак запустившей программе: окно установщика открылось, ей можно выходить.</summary>
    public static void SignalReady(SetupRequest r)
    {
        if (r?.ReadyEvent == null) return;
        try
        {
            if (EventWaitHandle.TryOpenExisting(r.ReadyEvent, out var ev)) { ev.Set(); ev.Dispose(); }
        }
        catch { }
    }

    // ------------------------------------------------------------------ сведения

    /// <summary>Папка уже установленной копии (из записи в списке программ) или null.</summary>
    public static string InstalledDir(SetupTarget probe)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(probe.Hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(UninstallRoot + probe.KeyName);
            string dir = key?.GetValue("InstallLocation") as string;
            return string.IsNullOrWhiteSpace(dir) ? null : dir;
        }
        catch { return null; }
    }

    /// <summary>Цель удаления: папка из записи в списке программ, иначе папка, где лежит запущенный файл.</summary>
    public static SetupTarget UninstallTarget(SetupRequest r)
    {
        if (r.TestDir != null) return SetupTarget.ForTest(r.TestDir);
        var probe = SetupTarget.Real(DefaultDir);
        string dir = InstalledDir(probe) ?? Path.GetDirectoryName(Environment.ProcessPath) ?? DefaultDir;
        return SetupTarget.Real(dir);
    }

    /// <summary>Размер того, что будет установлено (сама программа и PresentMon), байт.</summary>
    public static long PayloadBytes()
    {
        long total = 0;
        try
        {
            using var s = PayloadStream();
            if (s != null)
            {
                using var zip = new ZipArchive(s, ZipArchiveMode.Read);
                total += zip.Entries.Sum(e => e.Length);
            }
            else foreach (string f in SourceFiles()) try { total += new FileInfo(f.Split('|')[0]).Length; } catch { }
        }
        catch { }
        try
        {
            using var s = typeof(FpsCapture).Assembly.GetManifestResourceStream("PresentMon.exe");
            if (s != null) total += s.Length;
        }
        catch { }
        return total;
    }

    /// <summary>Размер установленных файлов программы, байт.</summary>
    public static long InstalledBytes(SetupTarget t)
    {
        try { return AppFiles.Concat(ListedFiles(t)).Distinct(StringComparer.OrdinalIgnoreCase).Select(f => Path.Combine(t.Dir, f)).Where(File.Exists).Sum(f => new FileInfo(f).Length); }
        catch { return 0; }
    }

    /// <summary>Размер папок с историей, настройками и копиями для отката, байт (0 — если их нет).</summary>
    public static long UserDataBytes(SetupTarget t)
    {
        long total = 0;
        foreach (string dir in new[] { t.UserDataDir, t.SecureDir })
        {
            try
            {
                if (Directory.Exists(dir)) total += new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch { }
        }
        return total;
    }

    // ------------------------------------------------------------------ установка

    /// <summary>
    /// Установка. Возвращает предупреждения: ярлык или автозапуск, которые Windows или антивирус не дали создать.
    /// Из-за них установка не прерывается — программа ставится до конца и попадает в список установленных.
    /// </summary>
    public static List<SetupWarning> Install(SetupTarget t, bool desktopShortcut, bool autostart, Action<int> step)
    {
        var warnings = new List<SetupWarning>();
        step?.Invoke(0);
        CloseRunning(t);

        step?.Invoke(1);
        string problem = CheckInstallDir(t.Dir, t.Test);
        if (problem != null) throw new InvalidOperationException(problem);
        PrepareDir(t);          // папка закрыта от записи до того, как в неё ляжет хоть один файл
        CopyProgram(t);

        step?.Invoke(2);
        string pmDir = Path.Combine(t.Dir, "PresentMon");
        if (FpsCapture.Extract(pmDir) == null) throw new InvalidOperationException(L.T("В установщике нет встроенного PresentMon: скачайте установщик заново."));
        File.WriteAllText(Path.Combine(t.Dir, ThirdPartyFile),
            "Пульс ПК использует сторонний компонент:\r\n\r\n" +
            "PresentMon — Intel Corporation, лицензия MIT.\r\n" +
            "Файлы: PresentMon\\PresentMon.exe, текст лицензии: PresentMon\\LICENSE.txt.\r\n" +
            "Исходный код: https://github.com/GameTechDev/PresentMon\r\n", new UTF8Encoding(true));
        if (!t.Test) LockTree(new DirectoryInfo(t.Dir));       // ещё раз: всё, что легло, — владелец «Администраторы», права только по наследству

        step?.Invoke(3);
        // Что не вышло — по виду (окно установщика решает по нему, где лежат ярлыки) и текстом на языке интерфейса.
        void Try(SetupWarningKind kind, string what, Action action)
        {
            try { action(); }
            catch (Exception ex) { warnings.Add(new SetupWarning(kind, what + ": " + Reason(ex))); Log.Error("установка: " + kind, ex); }
        }
        Try(SetupWarningKind.StartMenu, L.T("ярлык в меню «Пуск» не создан"), () =>
        {
            Directory.CreateDirectory(t.StartMenuDir);
            CreateShortcut(Path.Combine(t.StartMenuDir, ShortcutName), t.Exe, t.Dir);
        });
        string desk = Path.Combine(t.DesktopDir, ShortcutName);
        if (desktopShortcut)
            Try(SetupWarningKind.Desktop, L.T("ярлык на рабочем столе не создан"), () =>
            {
                Directory.CreateDirectory(t.DesktopDir);
                CreateShortcut(desk, t.Exe, t.Dir);
            });
        else Try(SetupWarningKind.OldDesktop, L.T("старый ярлык на рабочем столе не удалён"), () => DeleteShortcutIfOurs(desk, t.Exe));    // при обновлении без ярлыка старый убираем
        if (autostart) Try(SetupWarningKind.Autostart, L.T("автозапуск не включён"), () => CreateAutostart(t));
        else Try(SetupWarningKind.OldAutostart, L.T("старый автозапуск не убран"), () => DeleteAutostart(t));

        step?.Invoke(4);
        WriteUninstallKey(t);
        return warnings;
    }

    /// <summary>
    /// Понятная причина ошибки: настоящая ошибка из-под обёрток .NET (TargetInvocationException, AggregateException)
    /// и расшифровка частых кодов Windows. Раньше в окне оставалось «Exception has been thrown by the target of an invocation».
    /// </summary>
    public static string Reason(Exception ex)
    {
        while (ex is TargetInvocationException or AggregateException && ex.InnerException != null) ex = ex.InnerException;
        int code = ex.HResult;
        string hint = code switch
        {
            unchecked((int)0x80070005) => L.T("Windows или антивирус запретили запись (нет доступа). Если включён «Контролируемый доступ к папкам» или сторонний антивирус, разрешите в нём программу Пульс ПК либо снимите отметку «Ярлык на рабочем столе»."),
            unchecked((int)0x80070020) or unchecked((int)0x80070021) => L.T("файл занят другой программой. Закройте её и нажмите «Повторить»."),
            unchecked((int)0x80070003) => L.T("папка не найдена."),
            unchecked((int)0x800700E1) => L.T("антивирус заблокировал действие как подозрительное. Разрешите в нём Пульс ПК."),
            _ => null,
        };
        if (hint != null) return hint;
        if (ex is UnauthorizedAccessException) return L.T("нет доступа: Windows или антивирус запретили запись.");
        string msg = (ex.Message ?? "").Trim();
        return msg.Length > 0 ? msg : ex.GetType().Name + $" (0x{code:X8})";
    }

    // ------------------------------------------------------------------ папка установки и права

    /// <summary>
    /// Можно ли ставить в эту папку (null — можно). Нельзя: сетевая папка, диск без прав доступа (FAT, exFAT) — там файлы
    /// программы, которую автозапуск запускает с правами администратора, мог бы заменить кто угодно; ссылка вместо папки;
    /// чужие программы и библиотеки в папке (их подхватила бы наша программа).
    /// </summary>
    public static string CheckInstallDir(string dir, bool test = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathFullyQualified(dir)) return L.T("Папка установки указана не полностью.");
            string full = Path.GetFullPath(dir);
            if (full.StartsWith(@"\\")) return L.T("Сетевая папка не подходит: программу ставят на диск этого компьютера.");
            if (!test)
            {
                var drive = new DriveInfo(Path.GetPathRoot(full));
                if (drive.DriveType is DriveType.Network or DriveType.CDRom) return L.T("Этот диск не подходит: программу ставят на диск этого компьютера.");
                string fmt = drive.IsReady ? drive.DriveFormat : "";
                if (!fmt.Equals("NTFS", StringComparison.OrdinalIgnoreCase) && !fmt.Equals("ReFS", StringComparison.OrdinalIgnoreCase))
                {
                    string name = drive.Name.TrimEnd('\\'), example = Path.GetPathRoot(DefaultDir).TrimEnd('\\');
                    return fmt == ""
                        ? L.F("Диск {0} недоступен: на нём нельзя запретить другим программам менять файлы. Выберите диск NTFS, например {1}.", name, example)
                        : L.F("Диск {0} в формате {1}: на нём нельзя запретить другим программам менять файлы. Выберите диск NTFS, например {2}.", name, fmt, example);
                }
            }
            for (var d = new DirectoryInfo(full); d != null; d = d.Parent)
                if (d.Exists && d.Attributes.HasFlag(FileAttributes.ReparsePoint) && d.Parent != null)
                    return L.F("«{0}» — ссылка на другую папку. Выберите обычную папку.", d.FullName);
            if (Directory.Exists(full))
            {
                var ours = new HashSet<string>(AppFiles.Select(Path.GetFileName).Concat(ListedFiles(full)), StringComparer.OrdinalIgnoreCase);
                var foreign = Directory.EnumerateFiles(full)
                    .Select(Path.GetFileName)
                    .Where(n => !ours.Contains(n) && !ours.Contains(Regex.Replace(n, @"\.(new|bak)$", "", RegexOptions.IgnoreCase)))
                    .Where(n => Regex.IsMatch(n, @"\.(dll|exe|json|config|manifest|local)$", RegexOptions.IgnoreCase) || n.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
                    .Take(4).ToList();
                if (foreign.Count > 0)
                    return L.F("В папке уже лежат чужие программы или библиотеки ({0}). Ставить рядом с ними небезопасно — выберите пустую папку.", string.Join(", ", foreign));
            }
            return null;
        }
        catch (Exception ex) { return L.F("Папку проверить не удалось: {0}", ex.Message); }
    }

    private static SecurityIdentifier Admins => new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>
    /// Права папки установки: «Администраторы» и SYSTEM — полный доступ, «Пользователи» — чтение и запуск, без наследования
    /// от родительской папки (своя папка на D:\ иначе получила бы «Пользователи: изменение»). Владелец — «Администраторы».
    /// </summary>
    private static DirectorySecurity InstallDirSecurity()
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(true, false);
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, all, PropagationFlags.None, AccessControlType.Allow));
        sec.SetOwner(Admins);
        return sec;
    }

    /// <summary>Создать папку установки сразу с закрытыми правами (или закрыть существующую) и привести в порядок всё, что в ней уже лежит.</summary>
    private static void PrepareDir(SetupTarget t)
    {
        if (t.Test) { Directory.CreateDirectory(t.Dir); return; }
        var di = new DirectoryInfo(t.Dir);
        if (di.Exists && di.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException(L.F("«{0}» — ссылка на другую папку. Установка остановлена.", t.Dir));
        var sec = InstallDirSecurity();
        if (!di.Exists)
        {
            if (di.Parent != null) Directory.CreateDirectory(di.Parent.FullName);
            di.Create(sec);
        }
        else di.SetAccessControl(sec);
        di.Refresh();
        if (di.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException(L.F("«{0}» подменили ссылкой во время установки. Установка остановлена.", t.Dir));
        LockTree(di);
    }

    /// <summary>
    /// Всё внутри папки: ссылки (точки соединения, символические) удаляются — своих ссылок программа не делает; у файлов и папок
    /// владелец — «Администраторы», собственные разрешения снимаются, остаются только унаследованные от папки установки.
    /// </summary>
    private static void LockTree(DirectoryInfo dir)
    {
        foreach (var e in dir.EnumerateFileSystemInfos())
        {
            if (e.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (e is DirectoryInfo link) link.Delete(); else e.Delete();     // удаляется сама ссылка, не то, на что она ведёт
                continue;
            }
            if (e is DirectoryInfo sub)
            {
                var s = sub.GetAccessControl();
                foreach (FileSystemAccessRule r in s.GetAccessRules(true, false, typeof(SecurityIdentifier))) s.RemoveAccessRuleSpecific(r);
                s.SetAccessRuleProtection(false, false);
                s.SetOwner(Admins);
                sub.SetAccessControl(s);
                LockTree(sub);
            }
            else if (e is FileInfo f)
            {
                if (f.IsReadOnly) f.IsReadOnly = false;
                var s = f.GetAccessControl();
                foreach (FileSystemAccessRule r in s.GetAccessRules(true, false, typeof(SecurityIdentifier))) s.RemoveAccessRuleSpecific(r);
                s.SetAccessRuleProtection(false, false);
                s.SetOwner(Admins);
                f.SetAccessControl(s);
            }
        }
    }

    // ------------------------------------------------------------------ файлы программы

    private static Stream PayloadStream() => typeof(Installer).Assembly.GetManifestResourceStream(PayloadResource);

    /// <summary>Имена из списка PulsePC.files: только простые имена .exe/.dll прямо в папке установки.</summary>
    private static IEnumerable<string> ListedFiles(SetupTarget t) => ListedFiles(t.Dir);

    private static IEnumerable<string> ListedFiles(string dir)
    {
        try
        {
            string list = Path.Combine(dir, FileListName);
            if (!File.Exists(list)) return Array.Empty<string>();
            return File.ReadAllLines(list).Select(l => l.Trim()).Where(SafeName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    private static bool SafeName(string name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 100 && Path.GetFileName(name) == name && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && Regex.IsMatch(name, @"^[A-Za-z0-9._-]+\.(exe|dll)$");

    /// <summary>Файл для замены: имя в папке установки и как открыть его содержимое.</summary>
    private sealed record Item(string Name, Func<Stream> Open);

    /// <summary>
    /// Положить программу в папку установки. Каждый файл сначала пишется рядом как «.new», затем подменяет старый через
    /// File.Replace с копией «.bak». Не удалось заменить хоть один — все уже заменённые возвращаются из «.bak», новые убираются.
    /// </summary>
    private static void CopyProgram(SetupTarget t)
    {
        var oldList = ListedFiles(t).ToList();
        using var payload = PayloadStream();
        using var zip = payload == null ? null : new ZipArchive(payload, ZipArchiveMode.Read);
        var items = new List<Item>();
        if (zip != null)
        {
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith("/")) continue;
                if (!SafeName(entry.FullName)) throw new InvalidDataException(L.F("В установщике испорчен архив программы ({0}): скачайте установщик заново.", entry.FullName));
                items.Add(new Item(entry.FullName, entry.Open));
            }
            if (!items.Any(i => i.Name.Equals(ExeName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(L.F("В установщике нет программы {0}: скачайте установщик заново.", ExeName));
        }
        else
        {
            foreach (string pair in SourceFiles())
            {
                string[] p = pair.Split('|');
                if (SamePath(p[0], Path.Combine(t.Dir, p[1]))) continue;     // запущено из папки установки: файл уже на месте
                string src = p[0];
                items.Add(new Item(p[1], () => new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)));
            }
        }

        // 1. Новые файлы рядом со старыми.
        foreach (var it in items)
        {
            string tmp = Path.Combine(t.Dir, it.Name + ".new");
            DeleteFile(tmp);
            using var src = it.Open();
            using var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            src.CopyTo(dst);
            dst.Flush(true);
        }

        // 2. Подмена с копией .bak; при сбое — откат всего, что уже подменили.
        var done = new List<(string Dst, bool HadOld)>();
        try
        {
            foreach (var it in items)
            {
                string dst = Path.Combine(t.Dir, it.Name), tmp = dst + ".new", bak = dst + ".bak";
                bool hadOld = File.Exists(dst);
                if (hadOld) { DeleteFile(bak); WithRetry(() => { File.SetAttributes(dst, FileAttributes.Normal); File.Replace(tmp, dst, bak, ignoreMetadataErrors: true); }); }
                else File.Move(tmp, dst);
                done.Add((dst, hadOld));
            }
        }
        catch (Exception ex)
        {
            foreach (var (dst, hadOld) in Enumerable.Reverse(done))
            {
                try
                {
                    if (hadOld) WithRetry(() => File.Move(dst + ".bak", dst, overwrite: true));
                    else DeleteFile(dst);
                }
                catch (Exception back) { Log.Error("откат файла " + dst, back); }
            }
            foreach (var it in items) DeleteFile(Path.Combine(t.Dir, it.Name + ".new"));
            throw new IOException(L.F("Не удалось заменить файлы программы ({0}). Прежняя версия возвращена на место.", ex.Message), ex);
        }

        // 3. Готово: копии .bak и то, что осталось от прежней раскладки, больше не нужны.
        foreach (var (dst, hadOld) in done) if (hadOld) DeleteFile(dst + ".bak");
        if (zip != null)
        {
            var names = items.Select(i => i.Name).ToList();
            foreach (string gone in oldList.Except(names, StringComparer.OrdinalIgnoreCase)) DeleteFile(Path.Combine(t.Dir, gone));
            foreach (string dev in DevFiles) DeleteFile(Path.Combine(t.Dir, dev));
            File.WriteAllLines(Path.Combine(t.Dir, FileListName), names);
        }
    }

    /// <summary>Старая копия может ещё выходить и держать файл — повторить до ~15 секунд.</summary>
    private static void WithRetry(Action action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { action(); return; }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 30)
            {
                Thread.Sleep(500);
            }
        }
    }

    private static void DeleteFile(string f)
    {
        try
        {
            if (!File.Exists(f)) return;
            File.SetAttributes(f, FileAttributes.Normal);
            File.Delete(f);
        }
        catch { }
    }

    /// <summary>Что копировать без встроенного архива: «источник|имя в папке установки». Однофайловая сборка — один exe.</summary>
    private static IEnumerable<string> SourceFiles()
    {
        string self = Environment.ProcessPath;
        yield return self + "|" + ExeName;
        // Сборка для разработки (не однофайловая): рядом с запускающим файлом лежит сама программа в dll.
#pragma warning disable IL3000   // в однофайловой сборке Location пуст — так и отличаем её от сборки для разработки
        string asm = typeof(Installer).Assembly.Location;
#pragma warning restore IL3000
        if (string.IsNullOrEmpty(asm)) yield break;
        string dir = Path.GetDirectoryName(asm);
        foreach (string f in new[] { "PulsePC.dll", "PulsePC.runtimeconfig.json", "PulsePC.deps.json" })
            if (File.Exists(Path.Combine(dir, f))) yield return Path.Combine(dir, f) + "|" + f;
    }

    private static void WriteUninstallKey(SetupTarget t)
    {
        using var root = RegistryKey.OpenBaseKey(t.Hive, RegistryView.Registry64);
        using var key = root.CreateSubKey(UninstallRoot + t.KeyName, true)
                        ?? throw new InvalidOperationException(L.T("Не удалось создать запись в списке программ."));
        string exe = t.Exe;
        string uninstall = t.Test ? $"\"{exe}\" --uninstall-test \"{t.Dir}\"" : $"\"{exe}\" --uninstall";
        long bytes = InstalledBytes(t);

        key.SetValue("DisplayName", t.Test ? AppName + " (проверка установщика)" : AppName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", AppName);
        key.SetValue("DisplayIcon", exe + ",0");
        key.SetValue("InstallLocation", t.Dir);
        key.SetValue("UninstallString", uninstall);
        key.SetValue("QuietUninstallString", uninstall + " --quiet");
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, bytes / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
    }

    // ------------------------------------------------------------------ удаление

    /// <summary>Команда, которая дочистит папку после выхода (запущенный exe сам себя удалить не может).</summary>
    private static string pendingCleanup;

    /// <summary>
    /// Удаление. Всегда: ярлыки, запись в списке программ, автозапуск, правила брандмауэра для PulsePC.exe (их создаёт Windows,
    /// когда пользователь разрешает доступ панели на телефоне), файлы программы, скачанные обновления и распакованный PresentMon.
    /// removeUserData — ещё история и настройки (%LOCALAPPDATA%\PulsePC) и копии для отката (%ProgramData%\PulsePC).
    /// </summary>
    public static void Uninstall(SetupTarget t, bool removeUserData, Action<int> step)
    {
        step?.Invoke(0);
        CloseRunning(t);

        step?.Invoke(1);
        DeleteShortcutIfOurs(Path.Combine(t.StartMenuDir, ShortcutName), t.Exe);
        DeleteShortcutIfOurs(Path.Combine(t.DesktopDir, ShortcutName), t.Exe);
        if (!t.Test)
        {
            // Ярлык мог оказаться и на общем рабочем столе — только если он ведёт на эту программу.
            DeleteShortcutIfOurs(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), ShortcutName), t.Exe);
        }

        step?.Invoke(2);
        using (var root = RegistryKey.OpenBaseKey(t.Hive, RegistryView.Registry64))
            root.DeleteSubKeyTree(UninstallRoot + t.KeyName, false);
        DeleteAutostart(t);
        if (!t.Test) DeleteFirewallRules(t.Exe);

        step?.Invoke(3);
        if (LooksInstalled(t)) DeleteAppFiles(t);
        // Загрузки обновлений и распакованный PresentMon — не данные пользователя, их не оставляем.
        DeleteFolder(Path.Combine(t.SecureDir, "update"));
        DeleteFolder(Path.Combine(t.UserDataDir, "update"));
        DeleteFolder(Path.Combine(t.UserDataDir, "PresentMon"));

        if (removeUserData)
        {
            step?.Invoke(4);
            DeleteFolder(t.UserDataDir, strict: true);
            DeleteFolder(t.SecureDir, strict: true);
        }
    }

    /// <summary>
    /// Удалить папку. Если на её месте ссылка — удаляется только ссылка (по ней не идём); внутри папки ссылки тоже
    /// не раскрываются (так устроено Directory.Delete). strict — ошибка удаления доходит до окна, иначе молча.
    /// </summary>
    private static void DeleteFolder(string dir, bool strict = false)
    {
        try
        {
            var di = new DirectoryInfo(dir);
            if (!di.Exists) return;
            if (di.Attributes.HasFlag(FileAttributes.ReparsePoint)) { di.Delete(); return; }
            // Родительская папка подменена ссылкой (например, %LOCALAPPDATA%\PulsePC → чужая папка) — не трогаем ничего.
            if (di.Parent is { Exists: true } parent && parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            di.Delete(true);
        }
        catch (Exception ex) when (!strict) { Log.Error("удаление " + dir, ex); }
    }

    /// <summary>Правила брандмауэра Windows для этой программы (netsh из System32, путь к программе в кавычках).</summary>
    private static void DeleteFirewallRules(string exe)
    {
        if (exe.Contains('"')) return;
        string netsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");
        // Нет ни одного правила — netsh отвечает ошибкой, это нормально.
        Shell.Run(netsh, $"advfirewall firewall delete rule name=all program=\"{exe}\"", 30000);
    }

    /// <summary>
    /// Папку удаляем, только если это действительно папка установки: она записана в списке программ
    /// или в ней лежит наш THIRD-PARTY.txt рядом с PresentMon. Иначе файлы не трогаем вовсе.
    /// </summary>
    private static bool LooksInstalled(SetupTarget t)
    {
        if (!Directory.Exists(t.Dir)) return false;
        if (t.Test) return true;
        return File.Exists(Path.Combine(t.Dir, ThirdPartyFile)) && Directory.Exists(Path.Combine(t.Dir, "PresentMon"));
    }

    private static void DeleteAppFiles(SetupTarget t)
    {
        var locked = new List<string>();
        var names = AppFiles.Concat(ListedFiles(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (string rel in names.SelectMany(n => new[] { n, n + ".new", n + ".bak" }))
        {
            string f = Path.Combine(t.Dir, rel);
            if (!File.Exists(f)) continue;
            try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { locked.Add(f); }
        }
        string pm = Path.Combine(t.Dir, "PresentMon");
        TryRemoveEmpty(pm);
        TryRemoveEmpty(t.Dir);
        if (locked.Count == 0 && !Directory.Exists(t.Dir)) return;

        // Запущенный файл (это и есть удаляющий) занят до выхода. После выхода его дочищает cmd:
        // до минуты повторяет удаление только этих файлов и пустых папок. Чужие файлы в папке останутся.
        var sb = new StringBuilder("for /l %i in (1,1,60) do @(ping -n 2 127.0.0.1 >nul");
        foreach (string f in locked) sb.Append($" & del /f /q \"{f}\" 2>nul");
        sb.Append($" & rmdir \"{pm}\" 2>nul & rmdir \"{t.Dir}\" 2>nul & if not exist \"{t.Dir}\" exit)");
        pendingCleanup = sb.ToString();
    }

    private static void TryRemoveEmpty(string dir)
    {
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
    }

    /// <summary>Вызывается при выходе из программы: запускает скрытую дочистку, если она нужна.</summary>
    public static void RunPendingCleanup()
    {
        string cmd = Interlocked.Exchange(ref pendingCleanup, null);
        if (cmd == null) return;
        try
        {
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            Process.Start(new ProcessStartInfo(Path.Combine(sys, "cmd.exe"), "/d /s /c \"" + cmd + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = sys,      // текущая папка не должна держать удаляемую
            })?.Dispose();
        }
        catch { }
    }

    // ------------------------------------------------------------------ автозапуск

    /// <summary>
    /// Автозапуск — задача планировщика «при входе в систему» с наивысшими правами: программа требует прав администратора,
    /// а запись в разделе Run такие программы при входе не запускает. Окно не показывается (--minimized), только значок у часов.
    /// В проверочном режиме задача «PulsePC.Test» создаётся с обычными правами.
    /// Описание задачи пишется в защищённую папку (%ProgramData%\PulsePC), а не в %TEMP%: пока schtasks читает файл,
    /// подменить его там некому.
    /// </summary>
    private static void CreateAutostart(SetupTarget t)
    {
        string user = WindowsIdentity.GetCurrent().Name;
        string Esc(string s) => System.Security.SecurityElement.Escape(s);
        string xml =
$@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>{Esc(AppName)}: запуск при входе в систему, значок у часов</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{Esc(user)}</UserId><Delay>PT15S</Delay></LogonTrigger></Triggers>
  <Principals><Principal id=""Author""><UserId>{Esc(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>{(t.Test ? "LeastPrivilege" : "HighestAvailable")}</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context=""Author""><Exec><Command>{Esc(t.Exe)}</Command><Arguments>--minimized</Arguments><WorkingDirectory>{Esc(t.Dir)}</WorkingDirectory></Exec></Actions>
</Task>";
        string dir = t.Test ? t.SecureDir : SecureStore.Folder;
        // SecureStore при сбое отдаёт папку пользователя — туда описание задачи с правами администратора не пишем.
        if (!t.Test && Environment.GetEnvironmentVariable("PULSEPC_DATA") is not { Length: > 0 }
            && !string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(t.SecureDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.F("Защищённая папка {0} недоступна: автозапуск не создан.", t.SecureDir));
        Directory.CreateDirectory(dir);
        if (File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException(L.F("Папка «{0}» подменена ссылкой: автозапуск не создан.", dir));
        string file = Path.Combine(dir, "task-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            using (var fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                byte[] bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray();
                fs.Write(bytes, 0, bytes.Length);
            }
            var (code, output) = Shell.Run(SchTasks, $"/Create /TN \"{TaskName(t)}\" /XML \"{file}\" /F", 30000);
            if (code != 0) throw new InvalidOperationException(L.F("Не удалось создать задачу автозапуска: {0}", output.Trim()));
        }
        finally { try { File.Delete(file); } catch { } }
    }

    /// <summary>Убрать автозапуск: задачу планировщика (если она запускает PulsePC.exe) и старую запись Run, если она наша.</summary>
    private static void DeleteAutostart(SetupTarget t)
    {
        var (code, output) = Shell.Run(SchTasks, $"/Query /TN \"{TaskName(t)}\" /XML", 15000);
        if (code == 0 && output.Contains(ExeName, StringComparison.OrdinalIgnoreCase) && output.Contains("--minimized"))
            Shell.Run(SchTasks, $"/Delete /TN \"{TaskName(t)}\" /F", 15000);
        if (t.Test) return;
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (run?.GetValue("PulsePC") is string cmd && cmd.Contains(ExeName, StringComparison.OrdinalIgnoreCase))
                run.DeleteValue("PulsePC", false);
        }
        catch { }
    }

    /// <summary>Есть ли задача автозапуска (для начального состояния флажка при обновлении).</summary>
    public static bool AutostartExists(SetupTarget t) => Shell.Run(SchTasks, $"/Query /TN \"{TaskName(t)}\"", 15000).Code == 0;

    private static string TaskName(SetupTarget t) => t.Test ? "PulsePC.Test" : "PulsePC";
    private static string SchTasks => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");

    // ------------------------------------------------------------------ запущенные копии

    /// <summary>Сколько ждать, пока программа выйдет сама (отдаёт вентиляторы драйверу, дописывает сессию), прежде чем завершить её.</summary>
    private const int GracefulExitMs = 20000;

    /// <summary>
    /// Закрыть работающие копии: сначала просим выйти (событие PulsePC.Exit.* — программа сохраняет сессию и выходит сама,
    /// плюс просьба закрыть окно), ждём до 20 секунд и только потом завершаем принудительно.
    /// В проверочном режиме — только копии из проверочной папки, настоящую программу не трогаем.
    /// </summary>
    private static void CloseRunning(SetupTarget t)
    {
        int self = Environment.ProcessId;
        List<Process> Others()
        {
            var list = new List<Process>();
            foreach (var p in Process.GetProcessesByName("PulsePC"))
            {
                if (p.Id == self) { p.Dispose(); continue; }
                if (t.Test)
                {
                    string path = null;
                    try { path = p.MainModule?.FileName; } catch { }
                    if (path == null || !path.StartsWith(t.Dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) { p.Dispose(); continue; }
                }
                list.Add(p);
            }
            return list;
        }

        var running = Others();
        if (!t.Test)
        {
            bool any = running.Count > 0;
            foreach (string suffix in new[] { "Admin", "User" })
            {
                try { if (Mutex.TryOpenExisting("PulsePC.Single." + suffix, out var m)) { m.Dispose(); any = true; } } catch { any = true; }
                try { if (EventWaitHandle.TryOpenExisting("PulsePC.Exit." + suffix, out var ev)) { ev.Set(); ev.Dispose(); } } catch { }
            }
            if (!any) return;
        }
        if (running.Count == 0) return;

        foreach (var p in running) { try { p.CloseMainWindow(); } catch { } }
        var deadline = DateTime.UtcNow.AddMilliseconds(GracefulExitMs);
        foreach (var p in running)
        {
            try
            {
                int left = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
                if (!p.WaitForExit(left))
                {
                    Log.Info($"PulsePC (процесс {p.Id}) не вышел за {GracefulExitMs / 1000} с — завершается принудительно");
                    p.Kill(true);
                    p.WaitForExit(5000);
                }
            }
            catch { }
            finally { p.Dispose(); }
        }
    }

    // ------------------------------------------------------------------ файлы и ярлыки

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ярлык через системный интерфейс IShellLink — так же, как его создаёт Проводник. Раньше ярлыки делались через
    /// WScript.Shell (сценарии Windows): часть антивирусов такое блокирует, а ошибка доходила до окна невнятной обёрткой.
    /// </summary>
    private static void CreateShortcut(string lnk, string target, string workDir)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(workDir);
            link.SetIconLocation(target, 0);
            link.SetDescription(L.T("Пульс ПК — состояние компьютера и игровые сессии"));
            if (File.Exists(lnk))
            {
                // Ярлык прошлой версии мог остаться «только для чтения» — тогда сохранить поверх нельзя.
                var attrs = File.GetAttributes(lnk);
                if (attrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(lnk, attrs & ~FileAttributes.ReadOnly);
            }
            ((IPersistFile)link).Save(lnk, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    /// <summary>Удалить ярлык, только если он ведёт на эту программу.</summary>
    private static void DeleteShortcutIfOurs(string lnk, string exe)
    {
        if (!File.Exists(lnk)) return;
        string target = ShortcutTarget(lnk);
        if (target != null && SamePath(target, exe)) File.Delete(lnk);
    }

    /// <summary>Куда ведёт ярлык (null — не прочитать).</summary>
    internal static string ShortcutTarget(string lnk)
    {
        object obj = null;
        try
        {
            obj = new ShellLink();
            ((IPersistFile)obj).Load(lnk, 0);
            var sb = new StringBuilder(1024);
            ((IShellLinkW)obj).GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch { return null; }
        finally { if (obj != null) Marshal.FinalReleaseComObject(obj); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
