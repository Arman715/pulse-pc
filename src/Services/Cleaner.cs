using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PulsePC.Services;

/// <summary>
/// Подсчёт и удаление файлов без перехода по ссылкам (junction/symlink): ссылка удаляется сама,
/// её цель не затрагивается. Занятые и недоступные файлы пропускаются.
/// </summary>
public static class SafeFs
{
    public static (long Bytes, DateTime Newest) Size(string root)
    {
        long total = 0; DateTime newest = DateTime.MinValue;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    try { var fi = new FileInfo(f); total += fi.Length; if (fi.LastWriteTime > newest) newest = fi.LastWriteTime; } catch { }
                }
            }
            catch { }
            try
            {
                foreach (var s in Directory.EnumerateDirectories(dir))
                {
                    try { if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) == 0) stack.Push(s); } catch { }
                }
            }
            catch { }
        }
        return (total, newest);
    }

    public static long Delete(string path)
    {
        long freed = 0;
        try
        {
            var attr = File.GetAttributes(path);
            if ((attr & FileAttributes.Directory) == 0) return DeleteFile(path);
            if ((attr & FileAttributes.ReparsePoint) != 0)
            {
                try { Directory.Delete(path, false); } catch { }
                return 0;
            }
            string[] files = Array.Empty<string>(); try { files = Directory.GetFiles(path); } catch { }
            foreach (var f in files) freed += DeleteFile(f);
            string[] dirs = Array.Empty<string>(); try { dirs = Directory.GetDirectories(path); } catch { }
            foreach (var d in dirs) freed += Delete(d);
            try { Directory.Delete(path, false); } catch { }
        }
        catch { }
        return freed;
    }

    private static long DeleteFile(string file)
    {
        try
        {
            var fi = new FileInfo(file);
            long len = fi.Length;
            if ((fi.Attributes & FileAttributes.ReadOnly) != 0) fi.Attributes = FileAttributes.Normal;
            fi.Delete();
            return len;
        }
        catch { return 0; }
    }

    /// <summary>Защита от ошибки в пути: чистим только вложенные папки системного диска, никогда не корни.</summary>
    public static bool IsSafe(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = Path.GetFullPath(path).TrimEnd('\\'); } catch { return false; }

        string sysDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
        if (!full.StartsWith(sysDrive, StringComparison.OrdinalIgnoreCase)) return false;
        if (full.Split('\\').Length < 3) return false;

        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string[] deny =
        {
            win, Path.Combine(win, "System32"), Path.Combine(win, "SysWOW64"), Path.Combine(win, "WinSxS"), Path.Combine(win, "Installer"),
            user, Path.Combine(user, "AppData"), Path.Combine(user, "Documents"), Path.Combine(user, "Desktop"), Path.Combine(user, "Downloads"),
            Path.Combine(user, "OneDrive"), Path.Combine(user, "OneDrive", "Desktop"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(sysDrive, "Users"), Path.Combine(sysDrive, "Users", "Public"),
        };
        return !deny.Any(d => string.Equals(d.TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Правило очистки: содержимое папки, с отбором по возрасту, исключениями и маске.</summary>
public record CleanRule(string Dir, int OlderThanDays = 0, string[] Exclude = null, string Pattern = "*");

public static class Cleaner
{
    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string Win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string ProgData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static readonly string Temp = Path.GetTempPath().TrimEnd('\\');

    /// <summary>
    /// Ключ настроек (AppSettings.Values): пользователь сам включил плановую очистку раз в неделю. Без него у пунктов
    /// нет отметки Weekly — плановая очистка ничего не удаляет, даже если старая настройка AutoCleanWeekly включена
    /// по умолчанию. Ставит переключатель плановой очистки (SetWeeklyOptIn).
    /// </summary>
    public const string WeeklyOptInKey = "clean.weekly.optin";

    /// <summary>Записать решение пользователя о плановой очистке (вызывать из обработчика переключателя, затем Save()).</summary>
    public static void SetWeeklyOptIn(AppSettings s, bool on) => s.Set(WeeklyOptInKey, on ? "1" : null);

    public static bool WeeklyOptedIn(AppSettings s) => s?.GetBool(WeeklyOptInKey) == true;

    /// <summary>Пункты для плановой очистки: пусто, если пользователь её явно не включал.</summary>
    public static List<CleanItem> WeeklyItems(AppSettings s) =>
        WeeklyOptedIn(s) ? BuildItems(true).Where(i => i.Weekly).ToList() : new List<CleanItem>();

    /// <summary>Все пункты «Очистки». Отметка Weekly ставится, только если плановую очистку включил сам пользователь.</summary>
    public static List<CleanItem> BuildItems() => BuildItems(WeeklyOptedIn(SafeLoadSettings()));

    private static AppSettings SafeLoadSettings()
    {
        try { return AppSettings.Load(); } catch { return null; }
    }

    private static List<CleanItem> BuildItems(bool weekly)
    {
        var temp = new[]
        {
            new CleanRule(Temp, 1, new[] { "claude" }),
            new CleanRule(Path.Combine(Win, "Temp"), 1),
        };
        var dumps = new[]
        {
            new CleanRule(Path.Combine(Local, "CrashDumps")),
            new CleanRule(Path.Combine(ProgData, @"Microsoft\Windows\WER\ReportArchive")),
            new CleanRule(Path.Combine(ProgData, @"Microsoft\Windows\WER\ReportQueue")),
            new CleanRule(Path.Combine(Local, @"Microsoft\Windows\WER\ReportArchive")),
        };
        var logs = new[]
        {
            new CleanRule(Path.Combine(Win, @"Logs\CBS"), 0, null, "CbsPersist*"),
            new CleanRule(Path.Combine(Local, @"Microsoft\OneDrive\logs"), 1),
        };
        var dev = new[]
        {
            new CleanRule(Path.Combine(Local, @"pip\Cache")),
            new CleanRule(Path.Combine(Local, @"npm-cache\_cacache")),
        };
        var nvidia = new[]
        {
            new CleanRule(Path.Combine(ProgData, @"NVIDIA Corporation\NVIDIA App\UpdateFramework\ota-artifacts\grd")),
        };
        var updates = new[]
        {
            new CleanRule(Path.Combine(Win, @"SoftwareDistribution\Download")),
        };

        return new List<CleanItem>
        {
            new()
            {
                Title = "Временные файлы", Weekly = weekly, Selected = true,
                Hint = "Файлы во временных папках, не менявшиеся больше суток. Файлы запущенных программ не трогаются.",
                Scan = () => ScanRules(temp), Clean = () => CleanRules(temp),
            },
            new()
            {
                Title = "Старые черновики сессий Claude", Selected = true,
                Hint = "Рабочие файлы прошлых сессий Claude старше 3 дней. Текущая и недавние сессии остаются.",
                Scan = () => ClaudeDrafts(false), Clean = () => ClaudeDrafts(true),
            },
            // Не для плановой очистки: дампы и отчёты нужны, чтобы разобрать недавний вылет игры, — удаляются только по кнопке.
            new()
            {
                Title = "Дампы сбоев и отчёты об ошибках", Selected = true,
                Hint = "Снимки памяти упавших программ и отчёты для Microsoft. Нужны только для разбора конкретного сбоя.",
                Scan = () => ScanRules(dumps), Clean = () => CleanRules(dumps),
            },
            new()
            {
                Title = "Старые журналы", Weekly = weekly, Selected = true,
                Hint = "Архивные журналы обслуживания Windows и журналы OneDrive старше суток.",
                Scan = () => ScanRules(logs), Clean = () => CleanRules(logs),
            },
            new()
            {
                Title = "Корзина", Selected = true, Irreversible = true,
                Hint = "Всё, что лежит в корзине.",
                Scan = RecycleBinSize, Clean = EmptyRecycleBin,
            },
            new()
            {
                Title = "Кэш обновлений Windows",
                Hint = "Скачанные файлы обновлений и кэш оптимизации доставки. Если понадобятся, Windows скачает их снова.",
                Scan = () => ScanRules(updates),
                Clean = CleanUpdateCache,
            },
            new()
            {
                Title = "Установщики драйверов NVIDIA", Weekly = weekly,
                Hint = "Пакеты драйверов, которые скачала NVIDIA App. Установленный драйвер остаётся, а для переустановки пакет скачается заново.",
                Scan = () => ScanRules(nvidia), Clean = () => CleanRules(nvidia),
            },
            // Тот же кэш и тот же код, что в разделе «Драйвер» (оттуда сюда ведёт ссылка): папки NVIDIA, AMD, Intel и DirectX.
            new()
            {
                Title = ShaderCacheTitle,
                Hint = "Кэш NVIDIA, AMD, Intel и DirectX. Чистить стоит после обновления драйвера или если картинка дёргается и искажается. " +
                       "Игры соберут кэш заново: первый запуск каждой игры будет дольше, в первом матче возможны рывки. Во время игры не чистится.",
                Scan = GpuDriver.CacheBytes, Clean = () => GpuDriver.ClearShaderCacheEx().Freed,
            },
            new()
            {
                Title = "Кэши разработки (pip, npm, uv)",
                Hint = "Скачанные пакеты Python и Node. Проекты продолжат работать, а пакеты скачаются снова при следующей установке. В кэше uv удаляется только неиспользуемое.",
                Scan = () => ScanRules(dev),
                Clean = () =>
                {
                    long freed = CleanRules(dev);
                    // uv — только найденный в известных папках установки и только с правами обычного пользователя:
                    // программа работает от администратора, а файл в папке пользователя может подменить кто угодно.
                    string uv = FindUv();
                    if (uv != null)
                    {
                        var (code, error) = Shell.RunAsUser(uv, "cache prune", 300000);
                        if (code != 0) Log.Info($"Очистка: uv cache prune не выполнен ({code}{(error == "" ? "" : ", " + error)})");
                    }
                    return freed;
                },
            },
            new()
            {
                Title = "Старые версии обновлений Windows", SizeUnknown = true,
                Hint = "Windows (DISM) удаляет старые версии системных файлов, оставшиеся после обновлений. Занимает 5–15 минут, обычно освобождает несколько гигабайт.",
                Scan = () => 0,
                Clean = () =>
                {
                    long before = SystemDriveFree();
                    Shell.Run("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup", 45 * 60 * 1000);
                    return Math.Max(0, SystemDriveFree() - before);
                },
            },
        }
        // Пункт про Claude показывается только там, где Claude Code установлен.
        .Where(i => !i.Title.Contains("Claude") || Directory.Exists(Path.Combine(Temp, "claude")))
        // Установщики NVIDIA — только там, где стоит NVIDIA App (у AMD и Intel пакеты драйверов программа не трогает).
        .Where(i => !i.Title.Contains("драйверов NVIDIA") || Directory.Exists(Path.Combine(ProgData, @"NVIDIA Corporation\NVIDIA App"))).ToList();
    }

    /// <summary>
    /// Кэш обновлений Windows. Служба обновлений останавливается на время удаления, а зависящие от неё работающие службы —
    /// вместе с ней; после удаления всё, что работало, запускается снова (раньше «net stop /y» оставлял их остановленными).
    /// </summary>
    private static long CleanUpdateCache()
    {
        var updates = new[] { new CleanRule(Path.Combine(Win, @"SoftwareDistribution\Download")) };
        const string Wu = "wuauserv";
        var dependents = UpdateGuard.ActiveDependents(Wu);
        bool wasRunning = UpdateGuard.Query(Wu) is "running" or "starting";
        var stopped = new List<string>();
        long freed = 0;
        try
        {
            foreach (var d in dependents) { if (UpdateGuard.StopService(d).Ok) stopped.Add(d); }
            if (!UpdateGuard.StopService(Wu).Ok) Log.Info("Очистка: служба обновлений не остановилась — удаляется только то, что не занято");
            freed = CleanRules(updates);
            Shell.PowerShell("Delete-DeliveryOptimizationCache -Force", 120000);
        }
        finally
        {
            if (wasRunning) UpdateGuard.StartService(Wu);
            // Зависящие запускаются после службы, от которой зависят, в обратном порядке остановки.
            for (int i = stopped.Count - 1; i >= 0; i--)
            {
                var (ok, _, error) = UpdateGuard.StartService(stopped[i]);
                if (!ok) Log.Info($"Очистка: служба {stopped[i]} не запустилась снова — {error}");
            }
        }
        return freed;
    }

    /// <summary>uv.exe в известных папках установки (официальный установщик, cargo, winget, scoop, pip). null — не найден.</summary>
    internal static string FindUv()
    {
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var candidates = new List<string>
            {
                Path.Combine(home, @".local\bin\uv.exe"),
                Path.Combine(home, @".cargo\bin\uv.exe"),
                Path.Combine(Local, @"Microsoft\WinGet\Links\uv.exe"),
                Path.Combine(home, @"scoop\shims\uv.exe"),
            };
            foreach (var root in new[] { Path.Combine(Local, @"Programs\Python"), Path.Combine(roaming, "Python") })
            {
                try
                {
                    if (Directory.Exists(root))
                        candidates.AddRange(Directory.GetDirectories(root, "Python3*").Select(d => Path.Combine(d, @"Scripts\uv.exe")));
                }
                catch { }
            }
            return candidates.FirstOrDefault(File.Exists);
        }
        catch { return null; }
    }

    /// <summary>Название пункта кэша шейдеров: по нему раздел «Драйвер» открывает «Очистку» на этом пункте.</summary>
    public const string ShaderCacheTitle = "Кэш шейдеров видеокарты";

    public static long SystemDriveFree()
    {
        try { return new DriveInfo(Path.GetPathRoot(Win)).AvailableFreeSpace; } catch { return 0; }
    }

    /// <summary>Для самопроверки: применить одно правило.</summary>
    internal static long RunRule(CleanRule rule, bool delete) => Process(rule, delete);

    private static long ScanRules(IEnumerable<CleanRule> rules) => rules.Sum(r => Process(r, false));
    private static long CleanRules(IEnumerable<CleanRule> rules) => rules.Sum(r => Process(r, true));

    private static long Process(CleanRule rule, bool delete)
    {
        if (!SafeFs.IsSafe(rule.Dir) || !Directory.Exists(rule.Dir)) return 0;
        DateTime cutoff = DateTime.Now.AddDays(-rule.OlderThanDays);
        long total = 0;

        string[] entries;
        try { entries = Directory.GetFileSystemEntries(rule.Dir, rule.Pattern); } catch { return 0; }

        foreach (var entry in entries)
        {
            string name = Path.GetFileName(entry);
            if (rule.Exclude != null && rule.Exclude.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            try
            {
                long size; DateTime newest;
                if (Directory.Exists(entry))
                {
                    (size, newest) = SafeFs.Size(entry);
                    if (newest == DateTime.MinValue) newest = Directory.GetLastWriteTime(entry);
                }
                else
                {
                    var fi = new FileInfo(entry);
                    size = fi.Length; newest = fi.LastWriteTime;
                }
                if (rule.OlderThanDays > 0 && newest > cutoff) continue;
                total += delete ? SafeFs.Delete(entry) : size;
            }
            catch { }
        }
        return total;
    }

    /// <summary>Папки вида Temp\claude\[16 hex]\[сессия], в которых ничего не менялось больше 3 дней.</summary>
    private static long ClaudeDrafts(bool delete)
    {
        string root = Path.Combine(Temp, "claude");
        if (!SafeFs.IsSafe(root) || !Directory.Exists(root)) return 0;
        DateTime cutoff = DateTime.Now.AddDays(-3);
        long total = 0;

        foreach (var hashDir in Directory.GetDirectories(root))
        {
            if (!Regex.IsMatch(Path.GetFileName(hashDir), "^[0-9a-f]{16}$")) continue;
            string[] sessions; try { sessions = Directory.GetDirectories(hashDir); } catch { continue; }
            foreach (var session in sessions)
            {
                var (size, newest) = SafeFs.Size(session);
                if (newest == DateTime.MinValue) newest = Directory.GetLastWriteTime(session);
                if (newest > cutoff) continue;      // свежие сессии, включая текущую, остаются
                total += delete ? SafeFs.Delete(session) : size;
            }
            if (delete)
            {
                try { if (!Directory.EnumerateFileSystemEntries(hashDir).Any()) Directory.Delete(hashDir, false); } catch { }
            }
        }
        return total;
    }

    // --- Корзина ---
    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO { public uint cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string root, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);

    private static long RecycleBinSize()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>() };
            return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
        }
        catch { return 0; }
    }

    private static long EmptyRecycleBin()
    {
        long before = RecycleBinSize();
        try { SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4); } catch { }   // без вопросов, окна и звука
        return Math.Max(0, before - RecycleBinSize());
    }
}
