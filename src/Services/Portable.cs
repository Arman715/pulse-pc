namespace PulsePC.Services;

/// <summary>
/// Переносная версия: рядом с PulsePC.exe лежит файл portable.txt. Тогда история, настройки и журнал хранятся в папке Data рядом
/// с программой (её же берут SessionStore и SecureStore — через переменную PULSEPC_DATA, которую задаёт Apply до всего остального),
/// автозапуск и запись в «Установленных приложениях» программа не трогает (их делает только установщик), а новые версии
/// не ставятся сами — раздел «Новые версии» даёт ссылку на страницу выпусков.
/// </summary>
public static class Portable
{
    public const string MarkerName = "portable.txt";
    public const string DataName = "Data";
    /// <summary>Знак для перезапуска (смена языка или темы): новая копия наследует переменные и остаётся переносной.</summary>
    private const string EnvMark = "PULSEPC_PORTABLE";

    public static bool Enabled { get; private set; }
    public static string DataDir { get; private set; } = "";
    /// <summary>Почему переносной режим не включился, хотя portable.txt есть (папка только для чтения и т. п.).</summary>
    public static string Problem { get; private set; } = "";

    /// <summary>Страница выпусков: переносная версия обновляется вручную.</summary>
    public static string ReleasesUrl => "https://github.com/" + VersionCheck.DefaultFeed + "/releases/latest";

    /// <summary>
    /// Включить переносной режим, если рядом с программой лежит portable.txt. Вызывается первой строкой App.OnStartup.
    /// Отдельная папка проверки (PULSEPC_DATA без знака переносной версии) важнее: тогда ничего не меняется.
    /// </summary>
    public static void Apply(string exePath)
    {
        try
        {
            string data = Environment.GetEnvironmentVariable("PULSEPC_DATA");
            if (data is { Length: > 0 })
            {
                // Перезапуск переносной копии: переменные уже заданы.
                if (Environment.GetEnvironmentVariable(EnvMark) == "1") { Enabled = true; DataDir = data; }
                return;
            }
            var decision = Decide(exePath);
            if (decision.Dir == null)
            {
                Problem = decision.Problem;
                if (Problem != "") Log.Info("переносной режим не включён: " + Problem);
                return;
            }
            Environment.SetEnvironmentVariable("PULSEPC_DATA", decision.Dir);
            Environment.SetEnvironmentVariable(EnvMark, "1");
            Enabled = true;
            DataDir = decision.Dir;
        }
        catch (Exception ex) { Problem = ex.Message; }
    }

    /// <summary>
    /// Чистая часть решения (проверяется на тестовой папке): есть ли portable.txt рядом с exe и можно ли писать в папку Data.
    /// Dir = null — обычный режим; Problem — почему переносной не вышел.
    /// </summary>
    public static (string Dir, string Problem) Decide(string exePath)
    {
        string folder = Path.GetDirectoryName(exePath ?? "");
        if (string.IsNullOrEmpty(folder) || !File.Exists(Path.Combine(folder, MarkerName))) return (null, "");
        string dir = Path.Combine(folder, DataName);
        try
        {
            var di = new DirectoryInfo(dir);
            if (di.Exists && di.Attributes.HasFlag(FileAttributes.ReparsePoint)) return (null, L.F("Папка {0} — ссылка на другое место; переносной режим не включён.", dir));
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return (dir, "");
        }
        catch (Exception ex) { return (null, L.F("В папку {0} нельзя записать ({1}) — данные хранятся как обычно, в папке пользователя.", dir, ex.Message)); }
    }

    public static string SelfTest()
    {
        var lines = new List<string>();
        var fails = new List<string>();
        void Check(string what, bool ok, string detail = "")
        {
            lines.Add((ok ? "  да   " : "  НЕТ  ") + what + (detail != "" ? " — " + detail : ""));
            if (!ok) fails.Add(what);
        }
        string root = Path.Combine(Path.GetTempPath(), "PulsePC-portable-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            string exe = Path.Combine(root, "PulsePC.exe");
            Check("без portable.txt — обычный режим", Decide(exe).Dir == null && Decide(exe).Problem == "");
            File.WriteAllText(Path.Combine(root, MarkerName), "");
            var d = Decide(exe);
            Check("с portable.txt — папка Data рядом с программой", d.Dir == Path.Combine(root, DataName) && Directory.Exists(d.Dir), d.Dir);
            Check("в папке Data после проверки записи ничего не осталось", Directory.Exists(d.Dir) && Directory.GetFileSystemEntries(d.Dir).Length == 0);
            Check("пустой путь к программе — обычный режим", Decide("").Dir == null && Decide(null).Dir == null);
        }
        catch (Exception ex) { Check("проверка на временной папке", false, ex.Message); }
        finally { try { Directory.Delete(root, true); } catch { } }
        lines.Add(Enabled ? $"Эта копия — переносная, данные: {DataDir}" : "Эта копия — обычная" + (Problem != "" ? " (portable.txt есть, но: " + Problem + ")" : "") + ".");
        lines.Insert(0, fails.Count == 0 ? "OK" : "FAIL: " + string.Join("; ", fails));
        return string.Join(Environment.NewLine, lines);
    }
}
