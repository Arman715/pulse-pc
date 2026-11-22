using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace PulsePC.Services;

/// <summary>
/// Запись файлов, которые нельзя терять (сессии, настройки, история): сначала во временный файл рядом с записью на диск
/// мимо кэша, затем подмена с копией «.bak». Обрыв питания посреди записи оставляет либо старый файл, либо новый, но не обрезок.
/// </summary>
public static class AtomicFile
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private static readonly ConcurrentDictionary<string, object> locks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Замок на файл: одновременные сохранения из разных потоков идут по очереди.</summary>
    public static object LockFor(string path) => locks.GetOrAdd(Path.GetFullPath(path), _ => new object());

    public static void WriteAllText(string path, string text, Encoding encoding = null)
    {
        lock (LockFor(path))
        {
            string tmp = path + ".tmp";
            byte[] bytes = (encoding ?? Utf8).GetBytes(text);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true); }
                catch (IOException) { File.Copy(path, path + ".bak", true); File.Move(tmp, path, true); }
            }
            else File.Move(tmp, path);
        }
    }

    /// <summary>
    /// Прочитать файл через parse; если он испорчен или его нет — копию «.bak». Испорченный файл откладывается
    /// с отметкой времени (не перезаписывая прошлые), чтобы его можно было разобрать. null — нет ни того ни другого.
    /// </summary>
    public static T Read<T>(string path, Func<string, T> parse) where T : class
    {
        foreach (var p in new[] { path, path + ".bak" })
        {
            if (!File.Exists(p)) continue;
            // Занятый файл (антивирус, OneDrive, чтение из другого потока) — не испорченный: ждём, пока освободится,
            // иначе взяли бы более старую копию «.bak», и следующая запись молча затёрла бы то, что было только в файле.
            string text;
            try { text = ReadRetrying(p); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("чтение " + Path.GetFileName(p), ex);
                continue;
            }
            try
            {
                var v = parse(text);
                if (v != null)
                {
                    if (p != path) Log.Info($"{Path.GetFileName(path)} испорчен — данные взяты из резервной копии");
                    return v;
                }
            }
            catch (Exception ex)
            {
                Log.Error("чтение " + Path.GetFileName(p), ex);
                if (p == path)
                {
                    try { File.Copy(p, $"{path}.broken-{DateTime.Now:yyyyMMdd-HHmmss}", false); } catch { }
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Прочитать файл; если он занят другим процессом — несколько попыток с нарастающей паузой (всего около 1,5 с).
    /// Нет файла — исключение сразу.
    /// </summary>
    internal static string ReadRetrying(string path, int attempts = 8)
    {
        for (int i = 1; ; i++)
        {
            try { return File.ReadAllText(path); }
            catch (Exception ex) when (i < attempts && ex is not FileNotFoundException and not DirectoryNotFoundException
                                       && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50 * i);
            }
        }
    }

    /// <summary>Самопроверка во временной папке: занятый на время файл ждётся, испорченный — берётся из «.bak». Систему не трогает.</summary>
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        string fail = null;
        void Check(string what, bool ok) { sb.AppendLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) fail ??= what; }
        string dir = Path.Combine(Path.GetTempPath(), "pulsepc-atomic-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "data.txt");
            WriteAllText(file, "old");
            WriteAllText(file, "new");          // теперь «.bak» — прежнее содержимое
            Check("запись: в файле новое, в «.bak» прежнее", File.ReadAllText(file) == "new" && File.ReadAllText(file + ".bak") == "old");

            string got;
            using (var hold = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var release = Task.Run(() => { Thread.Sleep(300); hold.Dispose(); });
                got = Read(file, t => t);
                release.Wait();
            }
            Check("файл ненадолго занят — прочитан он сам, а не прежняя копия «.bak»", got == "new");

            File.WriteAllText(file, "{испорчен");
            got = Read(file, t => t.StartsWith('{') ? throw new FormatException("испорчен") : t);
            Check("испорченный файл — данные из «.bak»", got == "old");
        }
        catch (Exception ex) { Check("исключение: " + ex.Message, false); }
        finally { try { Directory.Delete(dir, true); } catch { } }
        return (fail == null ? "OK" : "FAIL: " + fail) + Environment.NewLine + sb;
    }
}

/// <summary>
/// Папка для того, что влияет на действия с правами администратора (копии для отката, источник обновлений, загрузки):
/// %ProgramData%\PulsePC, писать в неё могут только администраторы и система. В папку пользователя (%LOCALAPPDATA%)
/// может писать любая программа без прав — значит, оттуда нельзя брать то, что потом исполняется от администратора.
/// Без прав администратора (версия для просмотра) — обычная папка данных: там и действий с правами нет.
/// </summary>
public static class SecureStore
{
    private static string folder;
    private static readonly object gate = new();

    public static string Folder
    {
        get
        {
            lock (gate) return folder ??= Make();
        }
    }

    public static string PathOf(string name) => Path.Combine(Folder, name);

    /// <summary>
    /// Программа с правами администратора, а защищённую папку подготовить не удалось — SecureStore отдаёт папку пользователя.
    /// Тогда то, что исполняется от администратора (установщик обновления, PresentMon, описание задачи автозапуска),
    /// оттуда не берётся: такие части отказываются работать, а не тихо идут в папку пользователя.
    /// </summary>
    public static bool FellBack
    {
        get { _ = Folder; return fellBack; }
    }

    private static bool fellBack;

    private const string PartName = "Защищённая папка";

    private static bool IsAdmin()
    {
        try { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    private static SecurityIdentifier Admins => new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private static string Make()
    {
        // Переносная версия с правами администратора: папка Data лежит там, куда пишет обычный пользователь (рабочий стол,
        // «Загрузки», флешка), — PresentMon и копии для отката оттуда исполнялись бы от администратора. Поэтому защищённая
        // часть и у неё — %ProgramData%\PulsePC; история и настройки остаются в Data.
        if (Environment.GetEnvironmentVariable("PULSEPC_DATA") is { Length: > 0 } test && !(Portable.Enabled && IsAdmin()))
        {
            string t = Path.Combine(test, "secure");
            Directory.CreateDirectory(t);
            return t;
        }
        if (!IsAdmin()) return SessionStore.Folder;
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PulsePC");
        try
        {
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(true, false);           // без наследования от ProgramData (там у пользователей есть запись)
            foreach (var sid in new[] { Admins, system })
                sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            sec.SetOwner(Admins);

            var di = new DirectoryInfo(dir);
            // Чужая точка соединения или ссылка на месте папки: не идём по ней, а убираем саму ссылку.
            if (di.Exists && di.Attributes.HasFlag(FileAttributes.ReparsePoint)) di.Delete();
            di.Refresh();
            if (!di.Exists) di.Create(sec);
            else di.SetAccessControl(sec);                      // папку мог заранее создать кто-то другой — права наши
            di.Refresh();
            if (di.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("папку подменили ссылкой");
            // Права верхней папки на уже лежащее внутри не действуют: кто-то без прав мог заранее создать подпапки
            // (update\run, PresentMon) с «Все: полный доступ» и собой во владельцах. Приводим в порядок всё дерево.
            LockTree(di);
            return dir;
        }
        catch (Exception ex)
        {
            fellBack = true;
            Log.Error("защищённая папка " + dir, ex);
            Health.Fail(PartName, L.F("{0} не удалось закрыть от записи без прав администратора ({1}). Обновление программы, запись кадров и автозапуск отключены, копии для отката лежат в папке пользователя.", dir, ex.Message));
            return SessionStore.Folder;
        }
    }

    /// <summary>
    /// Всё внутри защищённой папки (как Installer.LockTree): ссылки (точки соединения, символические) удаляются, не раскрываясь;
    /// у файлов и папок владелец — «Администраторы», собственные разрешения снимаются, остаются только унаследованные.
    /// Что не удалось закрыть, удаляется; не удалось и это — исключение (папка считается незащищённой).
    /// </summary>
    public static void LockTree(DirectoryInfo dir)
    {
        foreach (var e in dir.EnumerateFileSystemInfos())
        {
            if (e.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                e.Delete();             // у DirectoryInfo без recursive удаляется сама ссылка, не то, на что она ведёт
                continue;
            }
            try
            {
                if (e is DirectoryInfo sub)
                {
                    var s = sub.GetAccessControl();
                    Inherit(s);
                    sub.SetAccessControl(s);
                    LockTree(sub);
                }
                else if (e is FileInfo f)
                {
                    if (f.IsReadOnly) f.IsReadOnly = false;
                    var s = f.GetAccessControl();
                    Inherit(s);
                    f.SetAccessControl(s);
                }
            }
            catch (Exception ex)
            {
                // Чужой владелец без доступа для администраторов: права не поменять — тогда убрать.
                try
                {
                    if (e is DirectoryInfo d) DeleteTree(d.FullName); else { e.Attributes = FileAttributes.Normal; e.Delete(); }
                    Log.Info($"защищённая папка: «{e.FullName}» не удалось закрыть ({ex.Message}) — удалено");
                }
                catch (Exception ex2) { throw new IOException($"«{e.FullName}»: права не закрыть ({ex.Message}), удалить тоже нельзя ({ex2.Message})"); }
            }
        }
    }

    private static void Inherit(FileSystemSecurity s)
    {
        foreach (FileSystemAccessRule r in s.GetAccessRules(true, false, typeof(SecurityIdentifier))) s.RemoveAccessRuleSpecific(r);
        s.SetAccessRuleProtection(false, false);
        s.SetOwner(Admins);
    }

    /// <summary>
    /// Удалить папку целиком, не раскрывая ссылок: ссылка на её месте удаляется сама (Directory.Delete с recursive
    /// внутри папки тоже удаляет ссылки, а не то, на что они ведут).
    /// </summary>
    public static void DeleteTree(string path)
    {
        var di = new DirectoryInfo(path);
        if (!di.Exists) return;
        if (di.Attributes.HasFlag(FileAttributes.ReparsePoint)) { di.Delete(); return; }
        foreach (var f in di.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            try { if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes = FileAttributes.Normal; } catch { }
        di.Delete(true);
    }

    /// <summary>
    /// Пустая папка заново: прежняя удаляется целиком (ссылки не раскрываются) и создаётся новая — с правами по наследству
    /// от защищённой папки. Для папки, из которой сразу что-то запускается (распаковка установщика).
    /// </summary>
    public static void Recreate(string path)
    {
        DeleteTree(path);
        var di = new DirectoryInfo(path);
        di.Create();
        di.Refresh();
        if (di.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("папку «" + path + "» подменили ссылкой");
    }

    /// <summary>
    /// Перенести старый файл из папки пользователя в защищённую (один раз). Содержимое всё равно проверяется при чтении:
    /// до переноса файл лежал там, куда мог писать кто угодно.
    /// </summary>
    public static void Migrate(string name)
    {
        try
        {
            string from = Path.Combine(SessionStore.Folder, name), to = PathOf(name);
            if (string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(from) && !File.Exists(to)) File.Copy(from, to);
            if (File.Exists(from)) File.Delete(from);
        }
        catch (Exception ex) { Log.Error("перенос " + name, ex); }
    }
}

/// <summary>
/// Журнал ошибок (%LOCALAPPDATA%\PulsePC\log.txt, до 512 КБ, затем log.1.txt) и состояние частей программы для экрана
/// «Состояние программы». Раньше ошибки глотались молча — теперь каждая попадает сюда.
/// </summary>
public static class Log
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object gate = new();
    private static readonly ConcurrentDictionary<string, DateTime> lastSame = new();

    public static string FilePath => Path.Combine(SessionStore.Folder, "log.txt");

    public static void Info(string text) => Write("инфо", text);

    /// <summary>Ошибка с местом. Одинаковые сообщения пишутся не чаще раза в 10 минут, чтобы журнал не забивался.</summary>
    public static void Error(string where, Exception ex = null)
    {
        string msg = ex == null ? where : $"{where}: {ex.GetType().Name}: {ex.Message}";
        var now = DateTime.Now;
        if (lastSame.TryGetValue(msg, out var t) && (now - t).TotalMinutes < 10) return;
        lastSame[msg] = now;
        if (lastSame.Count > 500) lastSame.Clear();
        Write("ошибка", msg + (ex?.StackTrace is { } st ? Environment.NewLine + "    " + st.Split('\n').FirstOrDefault()?.Trim() : ""));
    }

    private static void Write(string level, string text)
    {
        try
        {
            lock (gate)
            {
                string path = FilePath;
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxBytes) File.Move(path, Path.Combine(SessionStore.Folder, "log.1.txt"), true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {text}{Environment.NewLine}");
            }
        }
        catch { }
    }

    /// <summary>Последние строки журнала (для экрана состояния и кнопки «Скопировать отчёт»).</summary>
    public static List<string> Tail(int lines)
    {
        try { lock (gate) return File.Exists(FilePath) ? File.ReadLines(FilePath).TakeLast(lines).ToList() : new List<string>(); }
        catch { return new List<string>(); }
    }
}

/// <summary>
/// Здоровье частей программы: когда часть последний раз отработала и чем закончилась последняя ошибка.
/// Части сами отмечаются (Health.Ok / Health.Fail), экран «Состояние программы» показывает сводку.
/// </summary>
public static class Health
{
    public sealed class Part
    {
        public string Name;
        public DateTime LastOk;
        public DateTime LastFail;
        public string Error;
    }

    private static readonly ConcurrentDictionary<string, Part> parts = new();

    public static void Ok(string name) => parts.AddOrUpdate(name, n => new Part { Name = n, LastOk = DateTime.Now }, (_, p) => { p.LastOk = DateTime.Now; return p; });

    public static void Fail(string name, string error)
    {
        var p = parts.GetOrAdd(name, n => new Part { Name = n });
        bool fresh = p.Error != error;
        p.LastFail = DateTime.Now; p.Error = error;
        if (fresh) Log.Error(name + ": " + error);
    }

    public static List<Part> All() => parts.Values.OrderBy(p => p.Name).Select(p => new Part { Name = p.Name, LastOk = p.LastOk, LastFail = p.LastFail, Error = p.Error }).ToList();

    private static readonly ConcurrentDictionary<string, TimeSpan> periods = new();

    /// <summary>
    /// Часть отмечается редко и по расписанию (диски — раз в 30 минут): сторож здоровья не считает её молчание в пределах
    /// этого срока признаком «нет свежих данных».
    /// </summary>
    public static void Expect(string name, TimeSpan period) => periods[name] = period;

    /// <summary>Объявленный частью промежуток между отметками (TimeSpan.Zero — не объявлен).</summary>
    public static TimeSpan ExpectedPeriod(string name) => periods.TryGetValue(name, out var p) ? p : TimeSpan.Zero;
}
