using System.Diagnostics;
using System.Globalization;

namespace PulsePC.Services;

public class FpsResult
{
    public double Avg, Low1, Low01;
    public long Frames;
    public double[] Series = Array.Empty<double>();

    // Время кадров (с версии 1.5): по секундам по часам Windows, отдельные рывки и паузы (свёрнутая игра, загрузка).
    public List<FrameSecond> PerSecond = new();
    public List<FrameSpike> Spikes = new();
    /// <summary>Рывков за всю запись (кадр вдвое дольше обычного и дольше хотя бы на 8 мс).</summary>
    public long Stutters;
    /// <summary>Обычное (медианное) время кадра и время кадра в худшем 1 %, мс.</summary>
    public double MedianMs, P99Ms;
    public List<(DateTime From, DateTime To)> Pauses = new();
}

/// <summary>Одна секунда записи кадров: сколько кадров, обычное и худшее время кадра, сколько рывков.</summary>
public struct FrameSecond
{
    public DateTime At;
    public int Frames, Stutters;
    public float P50, P99, Max;
}

/// <summary>Рывок: кадр, который шёл заметно дольше обычного.</summary>
public struct FrameSpike
{
    public DateTime At;
    public float Ms, NormalMs;
}

/// <summary>Кадры за последние секунды (для оверлея): сколько кадров в окне, худший 1 % и обычный FPS, время кадра по отрезкам, мс.</summary>
public readonly record struct RecentFrames(int Count, double Low1Fps, double MedianFps, float[] FrameMs);

/// <summary>
/// Идущая игра для оверлея: процесс, запись кадров (null — кадры не пишутся: нет прав администратора или игра с античитом
/// уровня ядра) и пинг до сервера игры (NaN — сервер не найден или не отвечает на ping).
/// </summary>
public sealed class LiveGameFeed
{
    public int Pid { get; init; }
    public string Game { get; init; } = "";
    /// <summary>Имя процесса игры без .exe (окно может принадлежать другому процессу с тем же именем).</summary>
    public string Image { get; init; } = "";
    public FpsCapture Frames { get; init; }
    public Func<double> ServerPingMs { get; init; } = () => double.NaN;
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
    /// <summary>
    /// То же, но только кадры, пока игра на переднем плане. Свёрнутую игру Windows и сама игра притормаживают (30 FPS в фоне) —
    /// в «худший 1 %» и средний FPS такие кадры попадать не должны, иначе отчёт пишет «просадки» там, где человек не играл.
    /// </summary>
    private readonly int[] histogramFocus = new int[2001];
    private long framesFocus;
    private double sumMsFocus;
    /// <summary>Игра сейчас не на переднем плане (ставит запись сессии на каждом замере).</summary>
    public volatile bool Away;
    private readonly List<double> series = new();            // средний FPS по пятисекундным отрезкам
    private Process process;
    private string exe;
    private bool headerSeen;
    private int colFrame = -1, colTime = -1;
    private long frames;
    private double sumMs, clock;
    private double bucketStart = double.NaN, secondStart = double.NaN;
    private int bucketFrames, secondFrames;

    // Время кадров по секундам (для графика «время кадра» и оценки плавности). Кадры одной секунды копятся в небольшом
    // списке и сворачиваются в три числа, когда секунда кончилась: работы — одна сортировка пары сотен чисел в секунду.
    private readonly List<float> secFrames = new(512);
    private readonly List<FrameSecond> perSecond = new();
    private readonly List<FrameSpike> spikes = new();
    private readonly List<(DateTime, DateTime)> pauses = new();
    private double statSecStart = double.NaN, tBase = double.NaN;
    private DateTime wallBase;
    private float refMedian;
    private int secStutters;
    private long stutters;
    /// <summary>Больше рывков поштучно не хранится (дальше они только считаются).</summary>
    public const int MaxSpikes = 4000;

    // Скользящее окно для оверлея (1.5): время последних кадров в кольце. На итог записи не влияет.
    // 32 768 кадров — это 30 с даже при 1000 FPS; памяти — около 400 КБ на запись.
    private const int RecentCap = 1 << 15;
    private readonly float[] recentMs = new float[RecentCap];
    private readonly double[] recentT = new double[RecentCap];
    private int recentPos, recentCount;
    /// <summary>Часы для привязки кадров ко времени (подменяются в самопроверке).</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    /// <summary>
    /// Рывок — кадр, который шёл хотя бы вдвое дольше обычного (медиана прошлой секунды) и при этом дольше хотя бы на 8 мс:
    /// при 300 FPS лишние 3 мс глазом не видны, а при 60 FPS лишние 17 мс — заметная запинка.
    /// </summary>
    public static bool IsStutter(double ms, double normalMs) => normalMs > 0 && ms >= 2 * normalMs && ms - normalMs >= 8;

    private DateTime Wall(double t) => wallBase.AddSeconds(t - tBase);

    /// <summary>Свернуть накопленную секунду в обычное, худшее и максимальное время кадра.</summary>
    private void FlushSecond()
    {
        int n = secFrames.Count;
        if (n == 0 || double.IsNaN(statSecStart)) { secFrames.Clear(); secStutters = 0; return; }
        secFrames.Sort();
        float p50 = secFrames[n / 2], p99 = secFrames[Math.Clamp((int)Math.Ceiling(n * 0.99) - 1, 0, n - 1)], max = secFrames[n - 1];
        perSecond.Add(new FrameSecond { At = Wall(statSecStart), Frames = n, Stutters = secStutters, P50 = p50, P99 = p99, Max = max });
        // Обычное время кадра — по секундам, где кадров достаточно: секунда загрузки с двумя кадрами меркой не служит.
        if (n >= 10) refMedian = p50;
        secFrames.Clear(); secStutters = 0;
    }

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
                // Пауза в кадрах (игра свёрнута, загрузка) — отдельной отметкой; секунда до неё закрывается, после — начинается заново.
                double tp = colTime >= 0 && colTime < parts.Length && double.TryParse(parts[colTime], NumberStyles.Float, CultureInfo.InvariantCulture, out double pt)
                    ? pt : clock + ms / 1000.0;
                FlushSecond();
                statSecStart = double.NaN;
                if (!double.IsNaN(tBase) && pauses.Count < 500) pauses.Add((Wall(tp - ms / 1000.0), Wall(tp)));
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
            if (!Away) { framesFocus++; sumMsFocus += ms; histogramFocus[Math.Min(2000, (int)(ms * 10))]++; }
            recentMs[recentPos] = (float)ms;
            recentT[recentPos] = t;
            recentPos = (recentPos + 1) & (RecentCap - 1);
            if (recentCount < RecentCap) recentCount++;

            if (double.IsNaN(tBase)) { tBase = t; wallBase = Clock(); }
            if (double.IsNaN(statSecStart)) statSecStart = t;
            if (t - statSecStart >= 1) { FlushSecond(); statSecStart = t; }
            secFrames.Add((float)ms);
            if (IsStutter(ms, refMedian))
            {
                stutters++; secStutters++;
                if (spikes.Count < MaxSpikes) spikes.Add(new FrameSpike { At = Wall(t), Ms = (float)ms, NormalMs = refMedian });
            }

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

    /// <summary>
    /// Скользящее окно для оверлея: худший 1 % и обычный FPS за последние windowSec секунд по времени кадров
    /// и время кадра за последние graphSec секунд по отрезкам (в каждом — самый долгий кадр, NaN — кадров не было).
    /// Отсчёт — от последнего кадра. Кадров в окне меньше 30 — FPS неизвестен (NaN). Итог записи не меняется.
    /// </summary>
    public RecentFrames Recent(double windowSec = 30, double graphSec = 10, int buckets = 60)
    {
        var window = new List<float>(4096);
        var graph = new float[Math.Max(1, buckets)];
        Array.Fill(graph, float.NaN);
        lock (gate)
        {
            if (recentCount == 0) return new RecentFrames(0, double.NaN, double.NaN, graph);
            double now = recentT[(recentPos - 1) & (RecentCap - 1)], from = now - windowSec, graphFrom = now - graphSec;
            double step = graphSec / graph.Length;
            for (int i = 1; i <= recentCount; i++)
            {
                int idx = (recentPos - i) & (RecentCap - 1);
                double t = recentT[idx];
                if (t <= from && t <= graphFrom) break;          // дальше — только старше окна
                float ms = recentMs[idx];
                if (t > from) window.Add(ms);
                if (t > graphFrom)
                {
                    int b = Math.Clamp((int)((t - graphFrom) / step), 0, graph.Length - 1);
                    if (float.IsNaN(graph[b]) || ms > graph[b]) graph[b] = ms;
                }
            }
        }
        int n = window.Count;
        if (n < 30) return new RecentFrames(n, double.NaN, double.NaN, graph);
        window.Sort();
        double p99 = window[Math.Clamp((int)Math.Ceiling(n * 0.99) - 1, 0, n - 1)], p50 = window[n / 2];
        return new RecentFrames(n, p99 > 0 ? 1000.0 / p99 : double.NaN, p50 > 0 ? 1000.0 / p50 : double.NaN, graph);
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
            var seconds = perSecond.ToList();
            if (secFrames.Count > 0 && !double.IsNaN(statSecStart))
            {
                // Незаконченная секунда — копией, без изменения состояния: Result() вызывается и посреди записи (выход из программы).
                var copy = secFrames.ToList();
                copy.Sort();
                int n = copy.Count;
                seconds.Add(new FrameSecond { At = Wall(statSecStart), Frames = n, Stutters = secStutters, P50 = copy[n / 2],
                                              P99 = copy[Math.Clamp((int)Math.Ceiling(n * 0.99) - 1, 0, n - 1)], Max = copy[n - 1] });
            }
            // Только кадры «в игре», если их большинство (признак переднего плана надёжен) и были кадры в фоне.
            bool focus = framesFocus >= 500 && framesFocus < frames && framesFocus >= 0.5 * frames;
            var hist = focus ? histogramFocus : histogram;
            long count = focus ? framesFocus : frames;
            return new FpsResult
            {
                Frames = frames,
                Avg = 1000.0 * count / (focus ? sumMsFocus : sumMs),
                Low1 = FpsAtPercentile(hist, count, 0.99),
                Low01 = FpsAtPercentile(hist, count, 0.999),
                Series = series.ToArray(),
                PerSecond = seconds,
                Spikes = spikes.ToList(),
                Stutters = stutters,
                MedianMs = MsAtPercentile(hist, count, 0.5),
                P99Ms = MsAtPercentile(hist, count, 0.99),
                Pauses = pauses.ToList(),
            };
        }
    }

    // Время кадра на заданной доле кадров (0,5 — обычное, 0,99 — худший 1 %), мс.
    private static double MsAtPercentile(int[] histogram, long frames, double p)
    {
        long target = (long)Math.Ceiling(frames * p), seen = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            seen += histogram[i];
            if (seen >= target) return (i + 0.5) / 10.0;
        }
        return 0;
    }

    // «1 % low»: FPS, ниже которого игра опускалась в худшем проценте кадров.
    private static double FpsAtPercentile(int[] histogram, long frames, double p)
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
