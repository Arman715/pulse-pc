using System.Globalization;
using System.Text;
using System.Text.Json;
using PulsePC.Panels;

namespace PulsePC.Services;

/// <summary>Что снижает оценку: заголовок находки, пояснение, сколько очков снято и куда ведёт ссылка.</summary>
public sealed class HealthPenalty
{
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public int Points { get; set; }
    public Severity Level { get; set; } = Severity.Warn;
    /// <summary>diag — находка «Диагностики» (и осмотра железа), games — по записанным играм.</summary>
    public string Source { get; set; } = "diag";
    public string Link { get; set; } = "";
}

/// <summary>Оценка здоровья ПК на момент расчёта.</summary>
public sealed class HealthResult
{
    public DateTime At { get; set; }
    public int Score { get; set; }
    public List<HealthPenalty> Penalties { get; set; } = new();
    /// <summary>Сколько находок «Диагностики» учтено и сколько игр за 14 дней.</summary>
    public int Findings { get; set; }
    public int Games { get; set; }
    /// <summary>Язык, на котором записаны тексты: после смены языка оценка пересчитывается.</summary>
    public string Lang { get; set; } = "ru";
}

/// <summary>Оценка одного дня для графика.</summary>
public sealed class HealthDay
{
    public string Date { get; set; } = "";
    public int Score { get; set; }
}

public sealed class HealthFile
{
    public HealthResult Last { get; set; }
    public List<HealthDay> Days { get; set; } = new();
}

/// <summary>
/// Оценка здоровья ПК 0–100. Считается по находкам «Диагностики» (журналы Windows, диски, память, видеокарта, проверки железа
/// из первичного осмотра — HardwareChecks) и по играм за 14 дней (плавность, перегрев видеокарты, нехватка памяти, встроенная
/// графика). Каждая находка снимает очки: серьёзная — 15, замечание — 6; сведения не снимают ничего. Что именно снижает — список.
/// Раз в сутки (и после каждой проверки на вкладке «Диагностика») — запись дня в health.json; график — за 30 дней.
/// Ничего не меняет в системе.
/// </summary>
public static class HealthScore
{
    public const int BadPoints = 15, WarnPoints = 6;
    public const int GamesDays = 14, ChartDays = 30;
    private static readonly object Gate = new();

    /// <summary>Новая оценка записана (из любого потока).</summary>
    public static event Action Changed;

    private static string FilePath => Path.Combine(SessionStore.Folder, "health.json");

    // ================================================================== расчёт

    /// <summary>Оценка по находкам и играм. Чистая функция — проверяется на подставных данных.</summary>
    public static HealthResult Compute(IEnumerable<DiagItem> findings, IEnumerable<GameSession> sessions, DateTime now)
    {
        var r = new HealthResult { At = now, Lang = L.Code };
        var items = (findings ?? Array.Empty<DiagItem>()).ToList();
        r.Findings = items.Count;
        var seen = new HashSet<string>();
        // Одна и та же беда разными строками «Диагностики» очки дважды не снимает:
        // • синий экран сам перезагружает компьютер, и Windows пишет ещё и «аварийное выключение» (Kernel-Power 41) — если
        //   аварийных выключений не больше, чем синих экранов, они все от синих экранов;
        // • «после обновления драйвера сбоев стало больше» сделан из тех же сбросов драйвера, что и строка «Сбои драйвера видеокарты».
        var skip = new HashSet<DiagItem>();
        var serious = items.OfType<DiagFinding>().Where(i => i.Level is Severity.Bad or Severity.Warn).ToList();
        int bsods = serious.Where(i => i.Kind == LogMarks.Bsod).Select(i => int.TryParse(i.Count, out int n) ? n : 0).DefaultIfEmpty(0).Max();
        foreach (var k in serious.Where(i => i.Kind == LogMarks.Shutdown && int.TryParse(i.Count, out int n) && n <= bsods)) skip.Add(k);
        if (serious.Any(i => i.Title == L.T("После обновления драйвера видеокарты сбоев стало больше")))
            foreach (var k in serious.Where(i => i.Kind == LogMarks.Gpu)) skip.Add(k);
        foreach (var f in items.Where(i => i.Level is Severity.Bad or Severity.Warn).OrderBy(i => i.Level))
        {
            if (skip.Contains(f)) continue;
            if (!seen.Add(f.Title ?? "")) continue;          // один и тот же вывод не считается дважды
            r.Penalties.Add(new HealthPenalty
            {
                Title = f.Title ?? "", Detail = f.Detail ?? "", Level = f.Level, Source = "diag",
                Points = f.Level == Severity.Bad ? BadPoints : WarnPoints,
                Link = (f as DiagFinding)?.Link ?? "",
            });
        }

        var recent = (sessions ?? Array.Empty<GameSession>()).Where(s => s != null && s.End >= now.AddDays(-GamesDays) && s.End <= now.AddHours(1)).ToList();
        r.Games = recent.Count;
        void Game(Severity level, int points, string title, string detail, string link = "tab:Сессии") =>
            r.Penalties.Add(new HealthPenalty { Title = title, Detail = detail, Level = level, Points = points, Source = "games", Link = link });

        // Плавность: нужна хотя бы пара игр с замером кадров.
        var smooth = recent.Where(s => s.Smoothness is double).Select(s => s.Smoothness.Value).ToList();
        if (smooth.Count >= 2)
        {
            double avg = smooth.Average();
            if (avg < 60) Game(Severity.Warn, 8, L.T("Игры идут рывками"), L.F("Средняя плавность — {0} из 100 (игр с замером кадров: {1}). Что мешало — в отчётах на вкладке «Сессии».", Math.Round(avg), smooth.Count));
            else if (avg < 75) Game(Severity.Warn, 4, L.T("В играх бывают рывки"), L.F("Средняя плавность — {0} из 100 (игр с замером кадров: {1}).", Math.Round(avg), smooth.Count));
        }

        // Перегрев видеокарты в играх (температура процессора не учитывается никогда).
        var hot = recent.Where(s => s.MaxTemp >= 87).ToList();
        // Предел температуры ниже 80 °C — не перегрев (так его задаёт ноутбук или программа разгона): в оценку не идёт.
        var throttled = recent.Where(s => s.GpuThermalLimitPct >= 10 && (s.GpuThermalTempC ?? s.MaxTemp) >= SessionAnalysis.TempLimitHotC(LaptopCheck.Applies)).ToList();
        if (hot.Count > 0 || throttled.Count > 0)
        {
            double max = recent.Max(s => s.MaxTemp);
            Game(Severity.Warn, 6, L.T("Видеокарта перегревается в играх"),
                (hot.Count > 0 ? L.F("Нагревалась до {0} °C (игр с перегревом: {1}).", max.ToString("0", L.Culture), hot.Count) + " " : "")
                + (throttled.Count > 0 ? L.T("Из-за нагрева видеокарта сбрасывала частоту.") + " " : "")
                + L.T("Проверьте вентиляторы и пыль."), "sys:Вентиляторы");
        }

        // Нехватка памяти в играх.
        var (measured, shortCount, _, _) = MemoryBoost.Shortage(recent);
        if (measured >= 2 && shortCount * 2 >= measured)
            Game(Severity.Warn, 5, L.T("В играх не хватает памяти"), L.F("Игр, где свободной памяти оставалось меньше 1 ГБ: {0} из {1}.", shortCount, measured), "sys:Память в игре");

        // Игра шла на встроенной графике, хотя есть отдельная видеокарта (если это уже объяснила «Диагностика» — не повторяем).
        bool cable = items.Any(i => i.Level <= Severity.Warn && i.Title == L.T("Кабель монитора включён в материнскую плату"));
        var igpu = recent.Where(s => s.GpuUsedIntegrated == true && !string.IsNullOrEmpty(s.GpuIdle)).ToList();
        if (igpu.Count > 0 && !cable)
            Game(Severity.Bad, BadPoints, L.T("Игры шли на встроенной графике"),
                L.F("Игр на встроенной графике: {0}, хотя в компьютере есть {1}. Как переключить — в отчёте игры.", igpu.Count, igpu[0].GpuIdle));

        r.Penalties = r.Penalties.OrderByDescending(p => p.Points).ToList();
        r.Score = Math.Clamp(100 - r.Penalties.Sum(p => p.Points), 0, 100);
        return r;
    }

    /// <summary>Словами: «в хорошем состоянии» … «нужна помощь».</summary>
    public static string Verdict(int score) =>
        score >= 90 ? L.T("Компьютер в хорошем состоянии")
        : score >= 75 ? L.T("В целом в порядке, есть замечания")
        : score >= 50 ? L.T("Есть проблемы, которые стоит решить")
        : L.T("Компьютеру нужна помощь");

    public static Severity LevelOf(int score) => score >= 90 ? Severity.Ok : score >= 75 ? Severity.Info : score >= 50 ? Severity.Warn : Severity.Bad;

    /// <summary>Честно о данных: мало игр, мало дней истории.</summary>
    public static string DataNote(HealthResult r, int historyDays)
    {
        var parts = new List<string>();
        if (r.Games == 0) parts.Add(L.F("Игр за {0} дней не было — оценка только по проверке системы.", GamesDays));
        else if (r.Games < 3) parts.Add(L.F("Игр за {0} дней мало ({1}) — выводы по играм приблизительные.", GamesDays, r.Games));
        if (historyDays < 3) parts.Add(L.T("Оценка записывается раз в день — график наберётся за несколько дней."));
        return string.Join(" ", parts);
    }

    // ================================================================== хранение

    public static HealthFile Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return new HealthFile();
                var f = JsonSerializer.Deserialize<HealthFile>(File.ReadAllText(FilePath));
                if (f == null) return new HealthFile();
                f.Days ??= new();
                f.Days = f.Days.Where(d => d != null && DateTime.TryParseExact(d.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)).ToList();
                return f;
            }
            catch (Exception ex) { Log.Error("оценка здоровья: чтение", ex); return new HealthFile(); }
        }
    }

    /// <summary>Записать оценку: последний расчёт и оценку дня (за день — последняя). Хранится 120 дней.</summary>
    public static void Record(HealthResult r)
    {
        if (r == null) return;
        lock (Gate)
        {
            var f = Load();
            f.Last = r;
            string day = r.At.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            f.Days.RemoveAll(d => d.Date == day);
            f.Days.Add(new HealthDay { Date = day, Score = r.Score });
            f.Days = f.Days.OrderBy(d => d.Date, StringComparer.Ordinal).TakeLast(120).ToList();
            try { AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(f, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) { Log.Error("оценка здоровья: запись", ex); }
        }
        try { Changed?.Invoke(); } catch { }
    }

    /// <summary>Оценки за последние days дней по порядку; дни без оценки — null.</summary>
    public static int?[] Series(HealthFile f, DateTime today, int days = ChartDays)
    {
        var map = (f?.Days ?? new()).ToDictionary(d => d.Date, d => d.Score);
        var series = new int?[days];
        for (int i = 0; i < days; i++)
        {
            string key = today.Date.AddDays(i - days + 1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            series[i] = map.TryGetValue(key, out int v) ? v : null;
        }
        return series;
    }

    /// <summary>Пора считать: не считалось, прошло 20 часов, другой день или язык сменился.</summary>
    public static bool Due(HealthFile f, DateTime now) =>
        f?.Last == null || (now - f.Last.At).TotalHours >= 20 || f.Last.At.Date != now.Date || f.Last.Lang != L.Code;

    // ================================================================== самопроверка

    public static string SelfTest()
    {
        var lines = new List<string>();
        var fails = new List<string>();
        void Check(string what, bool ok, string detail = "")
        {
            lines.Add((ok ? "  да   " : "  НЕТ  ") + what + (detail != "" ? " — " + detail : ""));
            if (!ok) fails.Add(what);
        }
        var now = new DateTime(2026, 11, 21, 18, 0, 0);
        DiagItem D(Severity l, string t) => new DiagFinding { Level = l, Title = t, Detail = "…" };

        var clean = Compute(new[] { D(Severity.Ok, "Диски исправны"), D(Severity.Info, "16 ГБ памяти — впритык"), D(Severity.Ok, "Синих экранов нет") }, Array.Empty<GameSession>(), now);
        Check("всё в порядке, игр нет — 100, «только по проверке системы»", clean.Score == 100 && clean.Penalties.Count == 0 && DataNote(clean, 0) != "", DataNote(clean, 0));

        var bad = Compute(new[] { D(Severity.Bad, "Синие экраны"), D(Severity.Warn, "Мало свободной памяти"), D(Severity.Warn, "Мало свободной памяти"), D(Severity.Info, "Давно не было перезагрузки") }, null, now);
        Check("синие экраны −15, замечание −6, повтор не считается, сведения не снимают", bad.Score == 79 && bad.Penalties.Count == 2 && bad.Penalties[0].Points == 15, $"{bad.Score}");
        DiagItem K(Severity l, string t, string kind, string count) => new DiagFinding { Level = l, Title = t, Detail = "…", Kind = kind, Count = count };
        var crash = Compute(new[] { K(Severity.Bad, "Синие экраны", LogMarks.Bsod, "2"), K(Severity.Warn, "Аварийные выключения", LogMarks.Shutdown, "2") }, null, now);
        var crashMore = Compute(new[] { K(Severity.Bad, "Синие экраны", LogMarks.Bsod, "1"), K(Severity.Warn, "Аварийные выключения", LogMarks.Shutdown, "3") }, null, now);
        Check("аварийные выключения от тех же синих экранов не снимают очки второй раз; если их больше — снимают", crash.Score == 85 && crashMore.Score == 79, $"{crash.Score}/{crashMore.Score}");
        var drv = Compute(new[] { K(Severity.Warn, L.T("После обновления драйвера видеокарты сбоев стало больше"), "", "5"), K(Severity.Warn, "Сбои драйвера видеокарты", LogMarks.Gpu, "5") }, null, now);
        Check("«драйвер стал хуже» и «сбои драйвера» — одна беда, очки один раз", drv.Score == 94 && drv.Penalties.Count == 1, $"{drv.Score}");

        GameSession S(int daysAgo, double? smooth = null, double maxTemp = 70, double minRam = 3, bool igpu = false) => new()
        {
            Game = "Игра", Start = now.AddDays(-daysAgo).AddHours(-1), End = now.AddDays(-daysAgo), Smoothness = smooth, MaxTemp = maxTemp, MinRamGb = minRam,
            GpuUsedIntegrated = igpu ? true : null, GpuIdle = igpu ? "RTX 4060" : null,
        };
        var games = new[] { S(1, 52, 89, 0.6), S(2, 55, 80, 0.8), S(3, 58, 75, 2.5), S(30, 10, 99, 0.1) };
        var g = Compute(Array.Empty<DiagItem>(), games, now);
        Check("игры: рывки −8, перегрев видеокарты −6, нехватка памяти −5; старше 14 дней не учитываются",
            g.Score == 81 && g.Games == 3 && g.Penalties.Any(p => p.Points == 8) && g.Penalties.Any(p => p.Link == "sys:Вентиляторы") && g.Penalties.Any(p => p.Link == "sys:Память в игре"), $"{g.Score}: " + string.Join("; ", g.Penalties.Select(p => $"{p.Points} {p.Title}")));
        var one = Compute(Array.Empty<DiagItem>(), new[] { S(1, 20) }, now);
        Check("одна игра с плохой плавностью — по плавности не судим", one.Penalties.All(p => p.Points != 8 && p.Points != 4));
        var ig = Compute(Array.Empty<DiagItem>(), new[] { S(1, igpu: true) }, now);
        var igCable = Compute(new[] { D(Severity.Bad, L.T("Кабель монитора включён в материнскую плату")) }, new[] { S(1, igpu: true) }, now);
        Check("встроенная графика в игре −15, но не вдобавок к «кабель в материнской плате»", ig.Score == 85 && igCable.Score == 85 && igCable.Penalties.Count == 1);
        var floor = Compute(Enumerable.Range(0, 9).Select(i => D(Severity.Bad, "Сбой " + i)), null, now);
        Check("оценка не уходит ниже нуля", floor.Score == 0);
        Check("словами: 95 — хорошо, 40 — нужна помощь", Verdict(95) == L.T("Компьютер в хорошем состоянии") && Verdict(40) == L.T("Компьютеру нужна помощь") && LevelOf(40) == Severity.Bad);

        // Хранение — во временной папке.
        string dir = Path.Combine(Path.GetTempPath(), "PulsePC-health-test-" + Guid.NewGuid().ToString("N"));
        string old = Environment.GetEnvironmentVariable("PULSEPC_DATA");
        try
        {
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("PULSEPC_DATA", dir);
            Record(new HealthResult { At = now.AddDays(-2), Score = 70, Lang = L.Code });
            Record(new HealthResult { At = now.AddDays(-2).AddHours(1), Score = 72, Lang = L.Code });
            Record(new HealthResult { At = now, Score = 88, Lang = L.Code });
            var f = Load();
            var series = Series(f, now.Date, 5);
            Check("день записывается один раз (последняя оценка дня), график с пропусками",
                f.Days.Count == 2 && series[2] == 72 && series[3] == null && series[4] == 88 && f.Last.Score == 88, string.Join(",", series.Select(x => x?.ToString() ?? "·")));
            Check("пора считать: на следующий день — да, через час — нет", Due(f, now.AddDays(1)) && !Due(f, now.AddHours(1)));
            File.WriteAllText(Path.Combine(dir, "health.json"), "{ испорчен");
            Check("испорченный файл не ломает программу", Load().Days.Count == 0);
        }
        catch (Exception ex) { Check("хранение", false, ex.Message); }
        finally
        {
            Environment.SetEnvironmentVariable("PULSEPC_DATA", old);
            try { Directory.Delete(dir, true); } catch { }
        }

        // Что видно на этом ПК (только чтение).
        try
        {
            var f = Load();
            lines.Add(f.Last == null ? "На этом ПК оценка ещё не считалась."
                : $"На этом ПК: {f.Last.Score} ({f.Last.At:dd.MM HH:mm}), дней в истории — {f.Days.Count}, снижают: " + string.Join("; ", f.Last.Penalties.Select(p => $"−{p.Points} {p.Title}")));
        }
        catch (Exception ex) { lines.Add("Оценку этого ПК прочитать не удалось: " + ex.Message); }

        lines.Insert(0, fails.Count == 0 ? "OK" : "FAIL: " + string.Join("; ", fails));
        return string.Join(Environment.NewLine, lines);
    }
}
