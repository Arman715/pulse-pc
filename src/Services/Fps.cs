using System.Diagnostics;
using System.Globalization;

namespace PulsePC.Services;

public class FpsResult
{
    public double Avg, Low1, Low01;
    public long Frames;
    public double[] Series = Array.Empty<double>();
}

/// <summary>
/// Запись кадров игры через PresentMon (тот же инструмент, на котором работает CapFrameX).
/// PresentMon только слушает события Windows о выводе кадров и в процесс игры не вмешивается.
/// Кадры читаются потоком и сразу сворачиваются в статистику — файлов на диске не остаётся.
/// </summary>
public sealed class FpsCapture
{
    /// <summary>Своё имя сеанса трассировки у каждой записи: остановка прошлой игры не задевает следующую.</summary>
    private readonly string sessionName = EtwSessions.NewName("Fps");
    private readonly object gate = new();
    private readonly int[] histogram = new int[2001];        // время кадра с шагом 0,1 мс; последняя ячейка - всё, что дольше 200 мс
    private readonly List<double> series = new();            // средний FPS по пятисекундным отрезкам
    private Process process;
    private string exe;
    private bool headerSeen;
    private int colFrame = -1, colTime = -1;
    private long frames;
    private double sumMs, clock;
    private double bucketStart = double.NaN, secondStart = double.NaN;
    private int bucketFrames, secondFrames;

    /// <summary>FPS за последнюю секунду (NaN, пока данных нет).</summary>
    public double LiveFps { get; private set; } = double.NaN;
    /// <summary>Когда LiveFps обновлялся последний раз (игра свёрнута — кадры не идут, значение устаревает).</summary>
    public DateTime LiveAt { get; private set; } = DateTime.MinValue;

    /// <summary>PresentMon запущен и ещё работает (для отметки здоровья во время игры).</summary>
    public bool Running
    {
        get
        {
            var p = process;
            try { return p != null && !p.HasExited; } catch { return false; }
        }
    }

    /// <summary>
    /// PresentMon рядом с программой (так его кладёт установщик) — ровно PresentMon.exe, а не первый попавшийся exe в папке;
    /// иначе — распаковывается из самой программы в защищённую папку (%ProgramData%\PulsePC\PresentMon, писать туда могут
    /// только администраторы): PresentMon запускается с правами администратора, подложить его из папки пользователя нельзя.
    /// </summary>
    public static string FindExe()
    {
        try
        {
            string near = Path.Combine(AppContext.BaseDirectory, "PresentMon", "PresentMon.exe");
            if (File.Exists(near)) return near;
            // Защищённую папку подготовить не удалось — SecureStore отдал папку пользователя: оттуда PresentMon с правами
            // администратора не запускаем (его мог подменить кто угодно).
            if (SecureStore.FellBack)
            {
                Health.Fail("Кадры (PresentMon)", "защищённая папка %ProgramData%\\PulsePC недоступна — PresentMon из папки пользователя не запускается");
                return null;
            }
            string dir = SecureStore.PathOf("PresentMon");
            var di = new DirectoryInfo(dir);
            if (di.Exists && di.Attributes.HasFlag(FileAttributes.ReparsePoint)) di.Delete();      // ссылку убираем, по ней не идём
            return Extract(dir);
        }
        catch (Exception ex) { Health.Fail("Кадры (PresentMon)", "не удалось подготовить PresentMon: " + ex.Message); return null; }
    }

    private static readonly Lazy<Dictionary<string, byte[]>> resourceHashes = new(() =>
    {
        var d = new Dictionary<string, byte[]>();
        foreach (string r in new[] { "PresentMon.exe", "PresentMon.LICENSE.txt" })
        {
            using var s = typeof(FpsCapture).Assembly.GetManifestResourceStream(r);
            if (s != null) d[r] = System.Security.Cryptography.SHA256.HashData(s);
        }
        return d;
    });

    private static bool SameHash(string file, byte[] expected)
    {
        try
        {
            if (!File.Exists(file)) return false;
            using var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            return System.Security.Cryptography.SHA256.HashData(f).AsSpan().SequenceEqual(expected);
        }
        catch { return false; }
    }

    /// <summary>
    /// Распаковать встроенный PresentMon в папку (используется и установщиком). Возвращает путь к exe.
    /// Уже лежащий файл проверяется по SHA-256 со встроенным: размер совпал бы и у подменённого.
    /// </summary>
    public static string Extract(string dir)
    {
        var asm = typeof(FpsCapture).Assembly;
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, "PresentMon.exe");
        foreach (var (resource, file) in new[] { ("PresentMon.exe", exe), ("PresentMon.LICENSE.txt", Path.Combine(dir, "LICENSE.txt")) })
        {
            if (!resourceHashes.Value.TryGetValue(resource, out var hash)) return null;
            if (SameHash(file, hash)) continue;     // уже распакован и не изменён
            using (var src = asm.GetManifestResourceStream(resource))
            {
                if (src == null) return null;
                string tmp = file + ".tmp";
                using (var dst = File.Create(tmp)) src.CopyTo(dst);
                File.Move(tmp, file, true);
            }
            if (!SameHash(file, hash)) throw new IOException("распакованный " + Path.GetFileName(file) + " не совпадает со встроенным");
        }
        return exe;
    }

    public bool Start(int pid)
    {
        exe = FindExe();
        if (exe == null) return false;
        try
        {
            // --terminate_on_proc_exit: игра закрылась — PresentMon выходит сам и не висит, даже если программа не успела его остановить.
            var psi = new ProcessStartInfo(exe,
                $"--process_id {pid} --output_stdout --no_console_stats --v1_metrics --no_track_input --no_track_gpu --session_name {sessionName} --stop_existing_session --terminate_on_proc_exit")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            };
            process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) Feed(e.Data); };
            process.ErrorDataReceived += (_, _) => { };
            process.Start();
            ChildJob.Add(process);          // программа закрылась или упала — Windows завершит и PresentMon
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            Health.Ok("Кадры (PresentMon)");
            return true;
        }
        catch (Exception ex)
        {
            Health.Fail("Кадры (PresentMon)", "не запустился: " + ex.Message);
            process = null;
            return false;
        }
    }

    /// <summary>Разобрать одну строку вывода PresentMon. Публичный ради самопроверки.</summary>
    public void Feed(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        string[] parts = line.Split(',');

        if (!headerSeen)
        {
            // Заголовок: ищем столбцы времени кадра и времени события (названия отличаются в разных версиях).
            colFrame = IndexOf(parts, "msBetweenPresents", "FrameTime", "MsBetweenPresents");
            colTime = IndexOf(parts, "TimeInSeconds", "CPUStartTime");
            if (colFrame >= 0) headerSeen = true;
            return;
        }
        if (colFrame >= parts.Length) return;
        if (!double.TryParse(parts[colFrame], NumberStyles.Float, CultureInfo.InvariantCulture, out double ms)) return;
        if (ms <= 0) return;
        if (ms > 10000)
        {
            // Первый кадр и паузы (загрузки, свёрнутое окно) в статистику не идут. Отрезки для FPS «сейчас» и для ряда
            // начинаются заново: иначе первая секунда после паузы делилась бы на всю паузу — FPS около нуля, а в отчёте
            // ложная просадка «до 0 FPS» после каждого сворачивания игры.
            lock (gate)
            {
                bucketStart = secondStart = double.NaN;
                bucketFrames = secondFrames = 0;
                if (colTime < 0) clock += ms / 1000.0;
            }
            return;
        }

        lock (gate)
        {
            double t;
            if (colTime >= 0 && colTime < parts.Length && double.TryParse(parts[colTime], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)) t = parsed;
            else t = clock + ms / 1000.0;
            clock = t;

            frames++;
            sumMs += ms;
            histogram[Math.Min(2000, (int)(ms * 10))]++;

            if (double.IsNaN(bucketStart)) bucketStart = t;
            if (t - bucketStart >= 5)
            {
                series.Add(bucketFrames / (t - bucketStart));
                bucketStart = t; bucketFrames = 0;
            }
            bucketFrames++;

            if (double.IsNaN(secondStart)) secondStart = t;
            if (t - secondStart >= 1)
            {
                LiveFps = secondFrames / (t - secondStart);
                LiveAt = DateTime.Now;
                secondStart = t; secondFrames = 0;
            }
            secondFrames++;
        }
    }

    private static int IndexOf(string[] header, params string[] names)
    {
        for (int i = 0; i < header.Length; i++)
            foreach (string n in names)
                if (header[i].Trim().Equals(n, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>Остановить запись и вернуть итог (null, если кадров почти не было). Может занять несколько секунд.</summary>
    public FpsResult Stop()
    {
        try
        {
            if (process != null && exe != null)
            {
                // Вежливая остановка: просим PresentMon завершить сеанс трассировки, иначе он остался бы висеть в системе.
                // Останавливается только свой сеанс (имя уникально для этой записи).
                try
                {
                    using var stopper = Process.Start(new ProcessStartInfo(exe, $"--session_name {sessionName} --terminate_existing_session")
                        { UseShellExecute = false, CreateNoWindow = true });
                    ChildJob.Add(stopper);
                    stopper?.WaitForExit(5000);
                }
                catch (Exception ex) { Log.Error("остановка PresentMon", ex); }
                if (!process.WaitForExit(3000)) { try { process.Kill(true); } catch { } }
                process.Dispose();
                process = null;
                // PresentMon убит, не успев закрыть сеанс, — закрываем сами, иначе он остался бы в системе.
                EtwSessions.Stop(sessionName);
            }
        }
        catch (Exception ex) { Log.Error("остановка записи кадров", ex); }
        return Result();
    }

    public FpsResult Result()
    {
        lock (gate)
        {
            if (frames < 300 || sumMs <= 0) return null;
            return new FpsResult
            {
                Frames = frames,
                Avg = 1000.0 * frames / sumMs,
                Low1 = FpsAtPercentile(0.99),
                Low01 = FpsAtPercentile(0.999),
                Series = series.ToArray(),
            };
        }
    }

    // «1 % low»: FPS, ниже которого игра опускалась в худшем проценте кадров.
    private double FpsAtPercentile(double p)
    {
        long target = (long)Math.Ceiling(frames * p), seen = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            seen += histogram[i];
            if (seen >= target) return 1000.0 / ((i + 0.5) / 10.0);
        }
        return 0;
    }
}
