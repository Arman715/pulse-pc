using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using PulsePC.Panels;

namespace PulsePC.Services;

/// <summary>Один замер: когда, с какой меткой, при каких условиях и что получилось (ключ показателя → значение).</summary>
public sealed class BenchResult
{
    public DateTime At { get; set; }
    public string Label { get; set; } = "";
    public string PowerPlan { get; set; } = "";
    public int OpenApps { get; set; }
    /// <summary>Сколько процессора (в процентах) занимали другие программы, пока мерилась скорость: помеха замеру.</summary>
    public double OtherLoad { get; set; }
    public Dictionary<string, double> Values { get; set; } = new();
    /// <summary>Замеру заметно мешали: скорость процессора и памяти могла получиться заниженной.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Disturbed => OtherLoad >= Bench.DisturbedFrom;
}

/// <summary>Показатель замера и его обычный разброс: изменение меньше разброса — не изменение.</summary>
public sealed class BenchMetric
{
    public string Key { get; init; }
    public string Title { get; init; }
    /// <summary>Что это значит простыми словами (подсказка в таблице).</summary>
    public string Hint { get; init; }
    public string Unit { get; init; }
    public string Format { get; init; } = "0";
    public bool HigherIsBetter { get; init; }
    /// <summary>Разброс между замерами подряд: доля от значения и абсолютная величина. Порог — большее из двух.</summary>
    public double NoiseRel { get; init; }
    public double NoiseAbs { get; init; }
    /// <summary>Изменение показывать разностью («−1,2 п. п.», «+0,36 мс»), а не процентами: у величин около нуля проценты бессмысленны.</summary>
    public bool AbsoluteChange { get; init; }
    public string ChangeUnit { get; init; } = "";

    // Название, подсказка и единица хранятся по-русски (их нет в bench.json — там только ключ показателя);
    // на экран — на языке интерфейса.
    public string TitleText => L.T(Title);
    public string HintText => L.T(Hint);
    public string UnitText => L.T(Unit);
    public string ChangeUnitText => L.T(ChangeUnit.Length > 0 ? ChangeUnit : Unit);
}

/// <summary>Сравнение одного показателя в двух замерах. Verdict: 1 — лучше, −1 — хуже, 0 — в пределах разброса.</summary>
public sealed class BenchDelta
{
    public BenchMetric Metric { get; init; }
    public double Before { get; init; }
    public double After { get; init; }
    public int Verdict { get; init; }
    public string BeforeText => Bench.Format(Metric, Before);
    public string AfterText => Bench.Format(Metric, After);
    /// <summary>«+4,2 %» или «−1,2 п. п.».</summary>
    public string ChangeText { get; init; }
}

public sealed class BenchProgress
{
    public int Stage { get; init; }
    public int Stages { get; init; }
    public string Title { get; init; }
    /// <summary>Доля всего замера, 0..1.</summary>
    public double Fraction { get; init; }
}

/// <summary>
/// «Замер до и после»: за полминуты снимает состояние системы вокруг игры — фоновую нагрузку, точность коротких пауз,
/// скорость процессора, памяти и системного диска. Видеокарту не нагружает и в системе ничего не меняет:
/// единственный след — временный файл в папке программы, который удаляется сам вместе с закрытием.
/// Результаты — в bench.json (последние 20). Во время игры замер не запускается, а начавшаяся игра его прерывает.
/// </summary>
public static class Bench
{
    public const int Keep = 20;
    /// <summary>С какой чужой нагрузки (в процентах процессора) замер скорости считается потревоженным.</summary>
    public const double DisturbedFrom = 8;
    private const string TempName = "bench-temp.bin";

    // Пороги «без изменений» подобраны по сериям замеров подряд на одном компьютере без перемен между ними:
    // порог каждого показателя в два-три раза шире разброса, который он показывал сам по себе.
    public static readonly BenchMetric[] Metrics =
    {
        new() { Key = "bg.cpu", Title = "Фоновая нагрузка на процессор", Hint = "сколько процессора занимают другие программы, когда компьютер ничего не делает",
                Unit = "%", Format = "0.0", HigherIsBetter = false, NoiseRel = 0.30, NoiseAbs = 2.5, AbsoluteChange = true, ChangeUnit = "п. п." },
        new() { Key = "bg.ram", Title = "Свободная память", Hint = "сколько памяти достанется игре",
                Unit = "ГБ", Format = "0.0", HigherIsBetter = true, NoiseRel = 0.05, NoiseAbs = 0.3 },
        new() { Key = "bg.proc", Title = "Запущено процессов", Hint = "программы и службы, которые работают в фоне и будут работать рядом с игрой",
                Unit = "", Format = "0", HigherIsBetter = false, NoiseRel = 0.03, NoiseAbs = 6 },
        new() { Key = "timer.med", Title = "Опоздание таймера, обычное", Hint = "насколько позже срока Windows будит программу после паузы в 1 мс; чем меньше, тем ровнее идут кадры",
                Unit = "мс", Format = "0.00", HigherIsBetter = false, NoiseRel = 0.20, NoiseAbs = 0.15, AbsoluteChange = true },
        new() { Key = "timer.p99", Title = "Опоздание таймера, худшее", Hint = "то же в худшем случае из ста",
                Unit = "мс", Format = "0.00", HigherIsBetter = false, NoiseRel = 0.40, NoiseAbs = 0.4, AbsoluteChange = true },
        new() { Key = "cpu.one", Title = "Скорость одного ядра", Hint = "один и тот же расчёт на одном ядре; больше баллов — быстрее",
                Unit = "баллов", Format = "0", HigherIsBetter = true, NoiseRel = 0.03 },
        new() { Key = "cpu.all", Title = "Скорость всех ядер", Hint = "тот же расчёт на всех ядрах сразу",
                Unit = "баллов", Format = "0", HigherIsBetter = true, NoiseRel = 0.05 },
        new() { Key = "mem.copy", Title = "Скорость памяти", Hint = "копирование блока в 128 МБ",
                Unit = "ГБ/с", Format = "0.0", HigherIsBetter = true, NoiseRel = 0.06 },
        new() { Key = "disk.write", Title = "Системный диск, запись", Hint = "запись временного файла в папке программы",
                Unit = "МБ/с", Format = "0", HigherIsBetter = true, NoiseRel = 0.12 },
        new() { Key = "disk.read", Title = "Системный диск, чтение", Hint = "чтение того же файла мимо кэша Windows",
                Unit = "МБ/с", Format = "0", HigherIsBetter = true, NoiseRel = 0.10 },
    };

    private static readonly string[] StageTitles =
    {
        "Пауза, чтобы система успокоилась", "Фоновая нагрузка", "Точность таймера",
        "Скорость одного ядра", "Скорость всех ядер", "Скорость памяти", "Системный диск",
    };
    // Длительность этапов в секундах — для хода замера.
    private static readonly double[] StageSeconds = { 2, 10, 4, 3.5, 3.5, 2.2, 1.5 };

    private static int running;
    private static CancellationTokenSource active;

    public static bool Running => Volatile.Read(ref running) != 0;

    // ───────────── Договор службы ─────────────

    public static void Init(PanelHost host)
    {
        // Игра началась посреди замера — замер прерывается, чтобы не отнимать у неё процессор и диск.
        GameHooks.Started += (_, _) => { try { active?.Cancel(); } catch { } };
        // Если программа в прошлый раз закрылась посреди замера диска, временный файл мог остаться.
        Task.Run(DeleteTemp);
    }

    private static string StoreFile
    {
        get
        {
            string test = Environment.GetEnvironmentVariable("PULSEPC_BENCH_FILE");      // для проверки: не трогать настоящий список
            return string.IsNullOrWhiteSpace(test) ? Path.Combine(SessionStore.Folder, "bench.json") : test;
        }
    }

    // Имя временного файла случайное на каждый замер, файл только создаётся (CreateNew): папка данных доступна пользователю на запись,
    // и заранее подложенная там ссылка с известным именем заставила бы программу с правами администратора писать и удалять чужой файл.
    private static string tempFile;
    private static string TempFile => tempFile ?? Path.Combine(SessionStore.Folder, TempName);

    private static void DeleteTemp()
    {
        try { if (!Running && File.Exists(TempFile)) File.Delete(TempFile); } catch { }
    }

    // ───────────── Хранение ─────────────

    private static readonly object StoreGate = new();

    /// <summary>Сохранённые замеры, новые первыми.</summary>
    public static List<BenchResult> Load()
    {
        lock (StoreGate)
        {
            try
            {
                if (!File.Exists(StoreFile)) return new();
                return JsonSerializer.Deserialize<List<BenchResult>>(File.ReadAllText(StoreFile)) ?? new();
            }
            catch { return new(); }
        }
    }

    public static void Add(BenchResult result)
    {
        lock (StoreGate)
        {
            var all = Load();
            all.Insert(0, result);
            if (all.Count > Keep) all.RemoveRange(Keep, all.Count - Keep);
            Write(all);
        }
    }

    public static void Remove(DateTime at)
    {
        lock (StoreGate)
        {
            var all = Load();
            if (all.RemoveAll(r => r.At == at) > 0) Write(all);
        }
    }

    private static void Write(List<BenchResult> all)
    {
        try { File.WriteAllText(StoreFile, JsonSerializer.Serialize(all)); } catch { }
    }

    // ───────────── Сравнение ─────────────

    public static string Format(BenchMetric m, double value) =>
        double.IsNaN(value) ? "—" : value.ToString(m.Format, CultureInfo.CurrentCulture) + Unit(m.UnitText);

    /// <summary>Единица после числа через узкий пробел; по-английски знак процента без пробела («12.5%»).</summary>
    private static string Unit(string unit) => unit.Length == 0 ? "" : unit == "%" && L.En ? "%" : " " + unit;

    /// <summary>Порог «без изменений» для показателя при данном исходном значении.</summary>
    public static double Noise(BenchMetric m, double before) => Math.Max(m.NoiseAbs, m.NoiseRel * Math.Abs(before));

    /// <summary>Показатели, которые есть в обоих замерах, в порядке таблицы.</summary>
    public static List<BenchDelta> Compare(BenchResult before, BenchResult after)
    {
        var list = new List<BenchDelta>();
        if (before == null || after == null) return list;
        foreach (var m in Metrics)
        {
            if (!before.Values.TryGetValue(m.Key, out double a) || !after.Values.TryGetValue(m.Key, out double b)) continue;
            if (double.IsNaN(a) || double.IsNaN(b)) continue;
            double diff = b - a;
            int verdict = Math.Abs(diff) <= Noise(m, a) ? 0 : (diff > 0) == m.HigherIsBetter ? 1 : -1;
            string change;
            if (m.AbsoluteChange || Math.Abs(a) < 1e-9)
                change = Signed(diff, m.Format) + " " + m.ChangeUnitText;
            else
            {
                double pct = 100 * diff / Math.Abs(a);
                change = Signed(pct, Math.Abs(pct) < 10 ? "0.0" : "0") + Unit("%");
            }
            list.Add(new BenchDelta { Metric = m, Before = a, After = b, Verdict = verdict, ChangeText = change });
        }
        return list;
    }

    private static string Signed(double v, string format)
    {
        string s = Math.Abs(v).ToString(format, CultureInfo.CurrentCulture);
        if (double.Parse(s, CultureInfo.CurrentCulture) == 0) return s;
        return (v > 0 ? "+" : "−") + s;
    }

    /// <summary>Вывод одной-двумя фразами простыми словами.</summary>
    public static string Summary(BenchResult before, BenchResult after, List<BenchDelta> deltas)
    {
        if (deltas.Count == 0) return L.T("Сравнивать нечего: в этих замерах нет общих показателей.");
        var better = deltas.Where(d => d.Verdict > 0).ToList();
        var worse = deltas.Where(d => d.Verdict < 0).ToList();
        static string Name(BenchDelta d)
        {
            string t = d.Metric.TitleText;
            return t.Length > 1 && char.IsUpper(t[1]) ? t : char.ToLower(t[0], L.Culture) + t[1..];
        }
        static string Items(List<BenchDelta> list)
        {
            string shown = string.Join("; ", list.Take(3).Select(d => $"{Name(d)} — {d.BeforeText} → {d.AfterText}"));
            return list.Count > 3 ? string.Format(L.Culture, L.Pick("{0} и ещё {1}", "{0} and {1} more"), shown, list.Count - 3) : shown;
        }

        var sb = new StringBuilder();
        if (better.Count == 0 && worse.Count == 0)
            sb.Append(L.T("Заметной разницы нет: все показатели в пределах обычного разброса между замерами."));
        else if (worse.Count == 0)
            sb.Append(L.F("Стало лучше: {0}. Остальное не изменилось.", Items(better)));
        else if (better.Count == 0)
            sb.Append(L.F("Стало хуже: {0}.", Items(worse)) + (after.Disturbed ? "" : " " + L.T("Проверьте, не работала ли во время второго замера лишняя программа, и повторите его.")));
        else
            sb.Append(L.F("Лучше: {0}. Хуже: {1}.", Items(better), Items(worse)));

        if (before.PowerPlan != after.PowerPlan && before.PowerPlan.Length > 0 && after.PowerPlan.Length > 0)
            sb.Append(' ').Append(L.F("Между замерами сменилась схема питания: «{0}» → «{1}».", before.PowerPlan, after.PowerPlan));
        if (Math.Abs(before.OpenApps - after.OpenApps) >= 3)
            sb.Append(' ').Append(L.F("Открытых программ было {0}, стало {1} — часть разницы может быть из-за этого.", before.OpenApps, after.OpenApps));
        if (before.Disturbed || after.Disturbed)
        {
            string which = before.Disturbed && after.Disturbed
                ? L.F("Во время обоих замеров другие программы занимали {0:0} % и {1:0} % процессора — поэтому скорость процессора и памяти занижена. Если эти программы запустились случайно, повторите замер без них.", before.OtherLoad, after.OtherLoad)
                : before.Disturbed
                    ? L.F("Во время замера «было» другие программы занимали {0:0} % процессора — поэтому скорость процессора и памяти занижена. Если эти программы запустились случайно, повторите замер без них.", before.OtherLoad)
                    : L.F("Во время замера «стало» другие программы занимали {0:0} % процессора — поэтому скорость процессора и памяти занижена. Если эти программы запустились случайно, повторите замер без них.", after.OtherLoad);
            sb.Append(' ').Append(which);
        }
        return sb.ToString();
    }

    // ───────────── Замер ─────────────

    /// <summary>
    /// Сделать замер (около 30 секунд, вызывать из фонового потока). Результат не сохраняется — это делает Add.
    /// InvalidOperationException — идёт игра или другой замер; OperationCanceledException — отменён или началась игра.
    /// </summary>
    /// <param name="scale">Доля обычной длительности: 1 — полный замер, меньше — быстрая самопроверка.</param>
    public static BenchResult Run(string label, Action<BenchProgress> progress, CancellationToken cancel, double scale = 1, List<string> log = null)
    {
        // Текст исключения показывается в разделе как есть — поэтому на языке интерфейса.
        if (GameHooks.InGame) throw new InvalidOperationException(L.T("Идёт игра. Сделайте замер, когда она закроется."));
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new InvalidOperationException(L.T("Замер уже идёт."));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        active = linked;
        var ct = linked.Token;
        try
        {
            double done = 0, all = StageSeconds.Sum();
            int stage = -1;
            void Next()
            {
                if (stage >= 0) done += StageSeconds[stage];
                stage++;
                ct.ThrowIfCancellationRequested();
                Report(0);
            }
            void Report(double part) => progress?.Invoke(new BenchProgress
            {
                Stage = stage + 1, Stages = StageTitles.Length, Title = L.T(StageTitles[stage]),
                Fraction = Math.Min(1, (done + StageSeconds[stage] * Math.Clamp(part, 0, 1)) / all),
            });

            var r = new BenchResult { At = DateTime.Now, Label = label ?? "", PowerPlan = ActivePowerPlan(), OpenApps = OpenApps() };

            Next(); Wait(2 * scale, ct, Report);
            Next(); Background(r, 10 * scale, ct, Report, log);
            Next(); TimerAccuracy(r, 4 * scale, ct, Report, log);
            // Пока мерится скорость, следим, сколько процессора забирают другие программы: это помеха замеру.
            double other = 0;
            void Watched(Action measure)
            {
                using var self = Process.GetCurrentProcess();
                GetSystemTimes(out long idle0, out long kernel0, out long user0);
                long own0 = self.TotalProcessorTime.Ticks;
                measure();
                GetSystemTimes(out long idle1, out long kernel1, out long user1);
                self.Refresh();
                double now = Others(idle1 - idle0, kernel1 - kernel0, user1 - user0, self.TotalProcessorTime.Ticks - own0);
                other = Math.Max(other, now);
            }
            Next(); Watched(() => CpuSpeed(r, "cpu.one", 1, scale, ct, Report));
            Next(); Watched(() => CpuSpeed(r, "cpu.all", Environment.ProcessorCount, scale, ct, Report));
            Next(); Watched(() => MemorySpeed(r, 2 * scale, ct, Report, log));
            r.OtherLoad = Math.Round(other, 1);
            log?.Add($"  чужая нагрузка во время замера скорости (наибольшая по этапам): {r.OtherLoad.ToString("0.0", CultureInfo.InvariantCulture)} %");
            Next(); DiskSpeed(r, scale, ct, Report, log);
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(new BenchProgress { Stage = StageTitles.Length, Stages = StageTitles.Length, Title = L.T("Готово"), Fraction = 1 });
            return r;
        }
        finally
        {
            active = null;
            GC.Collect();           // вернуть системе блоки, занятые под замер памяти и диска
            Volatile.Write(ref running, 0);
        }
    }

    private static void Wait(double seconds, CancellationToken ct, Action<double> report)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            ct.ThrowIfCancellationRequested();
            Thread.Sleep(100);
            report(watch.Elapsed.TotalSeconds / seconds);
        }
    }

    // 1. Фоновая нагрузка: сколько процессора занято, пока мы сами спим (свою долю вычитаем), память, процессы.
    private static void Background(BenchResult r, double seconds, CancellationToken ct, Action<double> report, List<string> log)
    {
        using var self = Process.GetCurrentProcess();
        var windows = new List<double>();
        GetSystemTimes(out long idle0, out long kernel0, out long user0);
        long own0 = self.TotalProcessorTime.Ticks;
        long idleStart = idle0, kernelStart = kernel0, userStart = user0, ownStart = own0;
        var watch = Stopwatch.StartNew();
        double nextWindow = seconds / 10;
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            ct.ThrowIfCancellationRequested();
            Thread.Sleep(50);
            report(watch.Elapsed.TotalSeconds / seconds);
            if (watch.Elapsed.TotalSeconds < nextWindow) continue;
            nextWindow += seconds / 10;
            GetSystemTimes(out long idle, out long kernel, out long user);
            self.Refresh();
            long own = self.TotalProcessorTime.Ticks;
            windows.Add(Others(idle - idle0, kernel - kernel0, user - user0, own - own0));
            (idle0, kernel0, user0, own0) = (idle, kernel, user, own);
        }
        GetSystemTimes(out long idleEnd, out long kernelEnd, out long userEnd);
        self.Refresh();
        double whole = Others(idleEnd - idleStart, kernelEnd - kernelStart, userEnd - userStart, self.TotalProcessorTime.Ticks - ownStart);
        // В зачёт идёт медиана десяти окон, а не среднее за всё время: одиночный всплеск (проверка обновлений, антивирус)
        // случается в одном замере и не случается в другом, а сравнивать нужно постоянный фон.
        var sorted = windows.OrderBy(w => w).ToList();
        r.Values["bg.cpu"] = sorted.Count >= 4 ? (sorted[(sorted.Count - 1) / 2] + sorted[sorted.Count / 2]) / 2 : whole;
        log?.Add("  нагрузка по окнам, %: " + string.Join(" ", windows.Select(w => w.ToString("0.0", CultureInfo.InvariantCulture)))
                 + $" | среднее за всё время {whole.ToString("0.00", CultureInfo.InvariantCulture)}, медиана окон {r.Values["bg.cpu"].ToString("0.00", CultureInfo.InvariantCulture)}");

        var memory = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref memory)) r.Values["bg.ram"] = memory.ullAvailPhys / Fmt.Gib;
        try
        {
            var all = Process.GetProcesses();
            r.Values["bg.proc"] = all.Length;
            foreach (var p in all) p.Dispose();
        }
        catch { }
    }

    /// <summary>Доля процессора, занятая всеми, кроме нас, в процентах. Время ядра от системы включает простой.</summary>
    private static double Others(long idle, long kernel, long user, long own)
    {
        long total = kernel + user;
        if (total <= 0) return 0;
        return 100.0 * Math.Max(0, total - idle - Math.Max(0, own)) / total;
    }

    // 2. Точность пауз: просим разбудить через 1 мс и смотрим, на сколько позже это случилось.
    private static void TimerAccuracy(BenchResult r, double seconds, CancellationToken ct, Action<double> report, List<string> log)
    {
        var late = new List<double>(4096);
        var marks = new List<int>();            // границы восьми равных отрезков времени
        // Таймер высокой точности (Windows 10 1803 и новее) не меняет частоту системного таймера для остальных программ.
        IntPtr timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0x00000002, 0x1F0003);
        bool raised = false;
        if (timer == IntPtr.Zero) raised = timeBeginPeriod(1) == 0;       // старые Windows: как делают игры, только на время этапа
        try
        {
            var watch = Stopwatch.StartNew();
            int n = 0;
            while (watch.Elapsed.TotalSeconds < seconds)
            {
                long t0 = Stopwatch.GetTimestamp();
                if (timer != IntPtr.Zero)
                {
                    long due = -10_000;                                     // 1 мс в единицах по 100 нс, от текущего момента
                    SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
                    WaitForSingleObject(timer, 200);
                }
                else Thread.Sleep(1);
                late.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds - 1.0);
                if ((++n & 63) == 0) { ct.ThrowIfCancellationRequested(); report(watch.Elapsed.TotalSeconds / seconds); }
                if (watch.Elapsed.TotalSeconds >= seconds * (marks.Count + 1) / 8) marks.Add(late.Count);
            }
        }
        finally
        {
            if (timer != IntPtr.Zero) CloseHandle(timer);
            if (raised) timeEndPeriod(1);
        }
        if (late.Count < 20) return;
        // Худший случай из ста считается в каждом из восьми отрезков отдельно, в зачёт идёт медиана отрезков:
        // один посторонний всплеск за замер не выдаётся за свойство системы.
        var tails = new List<double>();
        int first = 0;
        foreach (int next in marks)
        {
            if (next - first >= 20)
            {
                var part = late.GetRange(first, next - first);
                part.Sort();
                tails.Add(part[Math.Min(part.Count - 1, (int)(part.Count * 0.99))]);
            }
            first = next;
        }
        late.Sort();
        double rawTail = late[Math.Min(late.Count - 1, (int)(late.Count * 0.99))];
        tails.Sort();
        r.Values["timer.med"] = Math.Max(0, late[late.Count / 2]);
        r.Values["timer.p99"] = Math.Max(0, tails.Count >= 4 ? (tails[(tails.Count - 1) / 2] + tails[tails.Count / 2]) / 2 : rawTail);
        log?.Add($"  паузы: {late.Count} замеров, худший из ста по отрезкам: " + string.Join(" ", tails.Select(t => t.ToString("0.00", CultureInfo.InvariantCulture)))
                 + $" | по всему ряду {rawTail.ToString("0.00", CultureInfo.InvariantCulture)}, 95-й {late[(int)(late.Count * 0.95)].ToString("0.00", CultureInfo.InvariantCulture)}, наибольшее {late[^1].ToString("0.00", CultureInfo.InvariantCulture)}");
    }

    // 3–4. Скорость процессора: один и тот же расчёт, считаем, сколько раз он успел выполниться.
    private static void CpuSpeed(BenchResult r, string key, int threads, double scale, CancellationToken ct, Action<double> report)
    {
        const int slices = 6;
        double warm = 0.4 * scale, slice = 0.5 * scale;
        var counts = new long[threads, slices];
        long start = Stopwatch.GetTimestamp() + (long)(0.05 * Stopwatch.Frequency);      // общий старт для всех потоков
        long Tick(double seconds) => start + (long)(seconds * Stopwatch.Frequency);

        var workers = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            int id = t;
            workers[t] = new Thread(() =>
            {
                var scratch = new double[2048];
                for (int i = 0; i < scratch.Length; i++) scratch[i] = 1.0 + i * 0.001;
                ulong x = 88172645463325252UL + (ulong)id;
                while (Stopwatch.GetTimestamp() < Tick(warm)) x = WorkUnit(x, scratch);          // разгон: частота процессора и компиляция
                for (int s = 0; s < slices && !ct.IsCancellationRequested; s++)
                {
                    long until = Tick(warm + slice * (s + 1)), n = 0;
                    while (Stopwatch.GetTimestamp() < until) { x = WorkUnit(x, scratch); n++; }
                    counts[id, s] = n;
                }
                GC.KeepAlive(x);
            }) { IsBackground = true, Name = "PulsePC bench" };
        }
        foreach (var w in workers) w.Start();
        double total = warm + slice * slices + 0.05;
        var watch = Stopwatch.StartNew();
        while (workers.Any(w => w.IsAlive))
        {
            Thread.Sleep(50);
            report(watch.Elapsed.TotalSeconds / total);
        }
        ct.ThrowIfCancellationRequested();

        // Медиана по отрезкам: короткая помеха от другой программы не портит результат.
        var rates = new List<double>();
        for (int s = 0; s < slices; s++)
        {
            long sum = 0;
            for (int t = 0; t < threads; t++) sum += counts[t, s];
            rates.Add(sum / slice);
        }
        rates.Sort();
        r.Values[key] = (rates[slices / 2 - 1] + rates[slices / 2]) / 2 / 10;           // баллы: десятки расчётов в секунду
    }

    /// <summary>Единица работы: целочисленное перемешивание и вещественная арифметика над небольшим массивом.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
    private static ulong WorkUnit(ulong x, double[] f)
    {
        for (int i = 0; i < 12000; i++)
        {
            x ^= x << 13; x ^= x >> 7; x ^= x << 17;
            x = x * 0x9E3779B97F4A7C15UL + (x >> 31);
        }
        double s = 0;
        for (int i = 0; i < f.Length; i++)
        {
            double v = f[i] + (x & 1023) * 1e-6;
            s += Math.Sqrt(v * v + 1.0) / (1.0 + v);
            f[i] = v > 1e6 ? 1.0 : v;
            x += (ulong)i;
        }
        return x ^ (ulong)BitConverter.DoubleToInt64Bits(s);
    }

    // 5. Скорость памяти: копирование блока, который заведомо не помещается в кэш процессора.
    private static void MemorySpeed(BenchResult r, double seconds, CancellationToken ct, Action<double> report, List<string> log)
    {
        const int size = 128 * 1024 * 1024;
        var memory = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref memory) && memory.ullAvailPhys < 3UL * size) return;       // памяти совсем нет — не добивать систему
        byte[] a, b;
        try { a = GC.AllocateUninitializedArray<byte>(size); b = GC.AllocateUninitializedArray<byte>(size); }
        catch (OutOfMemoryException) { return; }
        a.AsSpan().Fill(0x5A); b.AsSpan().Clear();                                               // страницы должны быть выданы до замера

        var rates = new List<double>();
        var watch = Stopwatch.StartNew();
        while ((watch.Elapsed.TotalSeconds < seconds || rates.Count < 5) && rates.Count < 200)
        {
            ct.ThrowIfCancellationRequested();
            long t0 = Stopwatch.GetTimestamp();
            a.AsSpan().CopyTo(b);
            double dt = Stopwatch.GetElapsedTime(t0).TotalSeconds;
            if (dt > 0) rates.Add(size / Fmt.Gib / dt);
            (a, b) = (b, a);
            report(watch.Elapsed.TotalSeconds / seconds);
        }
        // Помеха от других программ копирование только замедляет, поэтому в зачёт идут лучшие проходы (девятая десятая):
        // так показатель повторяется от замера к замеру в пределах процента, а медиана гуляла на 8 %.
        rates.Sort();
        r.Values["mem.copy"] = rates[Math.Min(rates.Count - 1, rates.Count * 9 / 10)];
        log?.Add($"  память: {rates.Count} проходов, медиана {rates[rates.Count / 2].ToString("0.00", CultureInfo.InvariantCulture)}, девятая десятая {r.Values["mem.copy"].ToString("0.00", CultureInfo.InvariantCulture)}, лучший {rates[^1].ToString("0.00", CultureInfo.InvariantCulture)}");
    }

    // 6. Системный диск: запись и чтение временного файла мимо кэша Windows. Файл удаляется сам при закрытии.
    private static void DiskSpeed(BenchResult r, double scale, CancellationToken ct, Action<double> report, List<string> log)
    {
        const int block = 4 * 1024 * 1024, align = 4096;
        long limit = (long)(256L * 1024 * 1024 * Math.Max(scale, 0.125));
        double budget = 2.0 * Math.Max(scale, 0.25);
        string folder = SessionStore.Folder;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder)));
            if (drive.AvailableFreeSpace < 4L * limit) { log?.Add("  диск: мало свободного места, этап пропущен"); return; }
        }
        catch { }

        // Буфер по адресу, кратному 4 КБ: так требует чтение и запись мимо кэша.
        byte[] raw = GC.AllocateUninitializedArray<byte>(block + align, pinned: true);
        int offset = (int)((align - Marshal.UnsafeAddrOfPinnedArrayElement(raw, 0).ToInt64() % align) % align);
        var buffer = raw.AsMemory(offset, block);
        new Random(12345).NextBytes(buffer.Span);                   // несжимаемые данные: накопитель не схитрит

        const FileOptions noBuffering = (FileOptions)0x20000000;
        SafeFileHandle file;
        try
        {
            tempFile = Path.Combine(SessionStore.Folder, "bench-" + Guid.NewGuid().ToString("N") + ".tmp");
            file = File.OpenHandle(TempFile, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                FileOptions.WriteThrough | FileOptions.DeleteOnClose | noBuffering, limit);
        }
        catch (Exception ex) { log?.Add("  диск: временный файл не создан — " + ex.Message); return; }

        using (file)
        {
            // Запись: быстрый накопитель пишет файл за доли секунды, поэтому проходов до трёх и в зачёт идёт лучший
            // (помеха запись только замедляет); медленному хватает одного. Больше 768 МБ за замер не пишется.
            long written = 0;
            var writeRates = new List<double>();
            double writeSeconds = 0;
            for (int pass = 0; pass < 3; pass++)
            {
                long doneBytes = 0, goal = pass == 0 ? limit : written;
                var watch = Stopwatch.StartNew();
                while (doneBytes < goal && (pass > 0 || watch.Elapsed.TotalSeconds < budget || doneBytes < 8 * block))
                {
                    ct.ThrowIfCancellationRequested();
                    RandomAccess.Write(file, buffer.Span, doneBytes);
                    doneBytes += block;
                    report(0.5 * Math.Min(1, (pass + Math.Max((double)doneBytes / goal, pass == 0 ? watch.Elapsed.TotalSeconds / budget : 0)) / 3));
                }
                double seconds = watch.Elapsed.TotalSeconds;
                writeSeconds += seconds;
                if (pass == 0) written = doneBytes;
                if (seconds > 0) writeRates.Add(doneBytes / 1048576.0 / seconds);
                if (seconds > budget / 3) break;
            }
            if (writeRates.Count > 0)
            {
                log?.Add("  диск, запись по проходам, МБ/с: " + string.Join(" ", writeRates.Select(w => w.ToString("0", CultureInfo.InvariantCulture))));
                writeRates.Sort();
                r.Values["disk.write"] = writeRates[^1];
            }

            // Чтение: несколько проходов по записанному, пока есть время; в зачёт идёт медиана.
            var rates = new List<double>();
            var whole = Stopwatch.StartNew();
            while (rates.Count < 9 && (rates.Count == 0 || whole.Elapsed.TotalSeconds < budget))
            {
                long read = 0;
                var pass = Stopwatch.StartNew();
                while (read < written)
                {
                    ct.ThrowIfCancellationRequested();
                    int got = RandomAccess.Read(file, buffer.Span, read);
                    if (got <= 0) break;
                    read += got;
                    if (rates.Count == 0 && pass.Elapsed.TotalSeconds > 2 * budget) break;      // очень медленный диск: хватит части файла
                }
                if (pass.Elapsed.TotalSeconds > 0 && read > 0) rates.Add(read / 1048576.0 / pass.Elapsed.TotalSeconds);
                report(0.5 + 0.5 * Math.Min(1, whole.Elapsed.TotalSeconds / budget));
            }
            if (rates.Count > 0)
            {
                rates.Sort();
                r.Values["disk.read"] = rates[rates.Count / 2];
            }
            log?.Add($"  диск: файл {written / 1048576} МБ, проходов записи {writeRates.Count} за {writeSeconds:0.00} с, проходов чтения {rates.Count}");
        }
    }

    // ───────────── Условия замера ─────────────

    /// <summary>Название активной схемы питания на языке системы (только чтение).</summary>
    public static string ActivePowerPlan()
    {
        IntPtr ptr = IntPtr.Zero;
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out ptr) != 0 || ptr == IntPtr.Zero) return "";
            Guid guid = Marshal.PtrToStructure<Guid>(ptr);
            var buffer = new byte[2048];
            uint size = (uint)buffer.Length;
            if (PowerReadFriendlyName(IntPtr.Zero, ref guid, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0) return "";
            return Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, (uint)buffer.Length)).TrimEnd('\0').Trim();
        }
        catch { return ""; }
        finally { if (ptr != IntPtr.Zero) LocalFree(ptr); }
    }

    /// <summary>Сколько программ открыто: процессы, у которых есть видимое окно с заголовком (кроме самой программы).</summary>
    public static int OpenApps()
    {
        var pids = new HashSet<uint>();
        uint self = (uint)Environment.ProcessId;
        try
        {
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) == 0) return true;
                if (GetWindow(hwnd, 4) != IntPtr.Zero) return true;                                  // окно-спутник другого окна
                if ((GetWindowLong(hwnd, -20) & 0x80) != 0) return true;                             // служебное окно без кнопки на панели задач
                if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, 4) == 0 && cloaked != 0) return true;   // скрытое окно приложения из Магазина
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != self) pids.Add(pid);
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return pids.Count;
    }

    // ───────────── Самопроверка ─────────────

    /// <summary>
    /// Быстрый прогон всех этапов (несколько секунд вместо тридцати) и проверка сравнения. В системе ничего не меняет,
    /// в список замеров ничего не пишет. PULSEPC_BENCH_TEST=full — полный замер; вместе с PULSEPC_BENCH_FILE результат
    /// дописывается в указанный файл с меткой из PULSEPC_BENCH_LABEL (так подбирались пороги разброса).
    /// </summary>
    public static string SelfTest()
    {
        var lines = new List<string>();
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            lines.Add((ok ? "  да   " : "  НЕТ  ") + name + (detail.Length > 0 ? " — " + detail : ""));
            if (!ok) fails.Add(name);
        }
        bool full = Environment.GetEnvironmentVariable("PULSEPC_BENCH_TEST") == "full";
        string testFile = Environment.GetEnvironmentVariable("PULSEPC_BENCH_FILE");

        lines.Add($"Схема питания: {ActivePowerPlan()}");
        lines.Add($"Открыто программ: {OpenApps()}; ядер: {Environment.ProcessorCount}");
        try
        {
            var log = new List<string>();
            var stages = new List<int>();
            double lastFraction = -1; bool monotone = true;
            var watch = Stopwatch.StartNew();
            var r = Run(full ? Environment.GetEnvironmentVariable("PULSEPC_BENCH_LABEL") ?? "проверка" : "проверка", p =>
            {
                if (!stages.Contains(p.Stage)) stages.Add(p.Stage);
                if (p.Fraction < lastFraction - 1e-9) monotone = false;
                lastFraction = p.Fraction;
            }, CancellationToken.None, full ? 1 : 0.12, log);
            lines.Add($"{(full ? "Полный" : "Быстрый")} замер за {watch.Elapsed.TotalSeconds:0.0} с:");
            foreach (var m in Metrics)
            {
                bool has = r.Values.TryGetValue(m.Key, out double v);
                bool ok = has && !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0 && (m.Key.StartsWith("bg.cpu") || m.Key.StartsWith("timer") || v > 0);
                Check($"{m.Title}: {(has ? v.ToString("0.###", CultureInfo.InvariantCulture) : "нет")} {m.Unit}", ok);
            }
            lines.AddRange(log);
            Check("ход замера: все этапы по порядку", stages.SequenceEqual(Enumerable.Range(1, StageTitles.Length)) && monotone && Math.Abs(lastFraction - 1) < 1e-9);
            Check("временный файл удалён", !File.Exists(TempFile));
            if (full && !string.IsNullOrWhiteSpace(testFile)) { Add(r); lines.Add("Результат дописан в " + testFile); }

            // Сравнение: шум не выдаётся за результат, направление «лучше/хуже» верное.
            var after = new BenchResult { At = r.At.AddMinutes(5), Label = "после", PowerPlan = r.PowerPlan, OpenApps = r.OpenApps, Values = new(r.Values) };
            Check("тот же замер — без изменений", Compare(r, after).All(d => d.Verdict == 0));
            foreach (var m in Metrics)
            {
                if (!r.Values.TryGetValue(m.Key, out double v)) continue;
                after.Values[m.Key] = v + 0.6 * Noise(m, v);
            }
            Check("сдвиг внутри разброса — без изменений", Compare(r, after).All(d => d.Verdict == 0));
            foreach (var m in Metrics)
            {
                if (!r.Values.TryGetValue(m.Key, out double v)) continue;
                after.Values[m.Key] = v + (m.HigherIsBetter ? 2.5 : -2.5) * Noise(m, v);
            }
            var up = Compare(r, after);
            Check("сдвиг в лучшую сторону — «лучше»", up.Count > 0 && up.All(d => d.Verdict == 1 || (!d.Metric.HigherIsBetter && d.Before - 2.5 * Noise(d.Metric, d.Before) < 0)), Summary(r, after, up));
            foreach (var m in Metrics)
            {
                if (!r.Values.TryGetValue(m.Key, out double v)) continue;
                after.Values[m.Key] = v + (m.HigherIsBetter ? -2.5 : 2.5) * Noise(m, v);
            }
            var down = Compare(r, after);
            Check("сдвиг в худшую сторону — «хуже»", down.Count > 0 && down.All(d => d.Verdict == -1), Summary(r, after, down));
        }
        catch (Exception ex) { Check("замер", false, ex.Message); }

        // Отмена: замер останавливается сразу и освобождает место для следующего.
        try
        {
            using var cts = new CancellationTokenSource(300);
            bool cancelled = false;
            try { Run("отмена", null, cts.Token, 0.5); } catch (OperationCanceledException) { cancelled = true; }
            Check("отмена прерывает замер", cancelled && !Running && !File.Exists(TempFile));
        }
        catch (Exception ex) { Check("отмена", false, ex.Message); }

        // Хранение: во временном файле, настоящий список не трогаем.
        if (string.IsNullOrWhiteSpace(testFile))
        {
            string temp = Path.Combine(Path.GetTempPath(), "PulsePC-bench-selftest-" + Environment.ProcessId + ".json");
            try
            {
                Environment.SetEnvironmentVariable("PULSEPC_BENCH_FILE", temp);
                for (int i = 0; i < Keep + 3; i++)
                    Add(new BenchResult { At = new DateTime(2026, 1, 1).AddMinutes(i), Label = "№" + i, Values = { ["cpu.one"] = 1000 + i } });
                var all = Load();
                Check("хранится не больше 20, новые первыми", all.Count == Keep && all[0].Label == "№" + (Keep + 2) && all[0].Values["cpu.one"] == 1000 + Keep + 2);
                Remove(all[0].At);
                Check("удаление одного замера", Load().Count == Keep - 1);
            }
            catch (Exception ex) { Check("хранение", false, ex.Message); }
            finally
            {
                Environment.SetEnvironmentVariable("PULSEPC_BENCH_FILE", null);
                try { File.Delete(temp); } catch { }
            }
        }
        lines.Add($"Сохранённых замеров: {Load().Count}");

        lines.Insert(0, fails.Count == 0 ? "OK" : "FAIL: " + string.Join("; ", fails));
        return string.Join(Environment.NewLine, lines);
    }

    // ───────────── Windows ─────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string name, uint flags, uint access);
    [DllImport("kernel32.dll")] private static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr routine, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr guid);
    [DllImport("powrprof.dll")] private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, byte[] buffer, ref uint size);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
