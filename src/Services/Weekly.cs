namespace PulsePC.Services;

public class WeekGameRow
{
    public string Game { get; set; } = "";
    public string Hours { get; set; } = "";
    public string Sessions { get; set; } = "";
    public string Fps { get; set; } = "";
    public string Change { get; set; } = "";
    public System.Windows.Media.Brush ChangeInk { get; set; }
}

public class WeekSummary
{
    public DateTime From, To;
    public double Hours, PrevHours;
    public int Sessions;
    public int Dips;
    public double MaxTemp;
    public List<WeekGameRow> Games = new();
    public List<string> Notes = new();
    public bool Empty => Sessions == 0;
}

/// <summary>Сводка за последние 7 дней и сравнение с предыдущими семью — по записанным сессиям.</summary>
public static class Weekly
{
    public static WeekSummary Build(IReadOnlyCollection<GameSession> all, DateTime now)
    {
        var to = now;
        var from = now.Date.AddDays(-6);           // сегодня и шесть дней назад
        var prevFrom = from.AddDays(-7);
        var week = all.Where(s => s.Start >= from && s.Start <= to).ToList();
        var prev = all.Where(s => s.Start >= prevFrom && s.Start < from).ToList();

        var w = new WeekSummary
        {
            From = from, To = to,
            Hours = week.Sum(s => s.Duration.TotalHours),
            PrevHours = prev.Sum(s => s.Duration.TotalHours),
            Sessions = week.Count,
            Dips = week.Sum(s => s.Dips?.Count ?? 0),
            MaxTemp = week.Count > 0 ? week.Max(s => s.MaxTemp) : 0,
        };
        if (w.Empty) return w;

        foreach (var g in week.GroupBy(s => s.Game).OrderByDescending(g => g.Sum(s => s.Duration.TotalHours)))
        {
            double? fps = WeightedFps(g), before = WeightedFps(prev.Where(s => s.Game == g.Key));
            string change = "";
            System.Windows.Media.Brush ink = Palette.Ink3;
            if (fps > 0 && before > 0)
            {
                double pct = 100 * (fps.Value - before.Value) / before.Value;
                change = Math.Abs(pct) < 3 ? "как раньше" : $"{(pct > 0 ? "+" : "−")}{Math.Abs(pct):0} %";
                if (pct <= -8) ink = Palette.Warn;
            }
            w.Games.Add(new WeekGameRow
            {
                Game = g.Key,
                Hours = Hours(g.Sum(s => s.Duration.TotalHours)),
                Sessions = g.Count().ToString(),
                Fps = fps > 0 ? fps.Value.ToString("0") : "—",
                Change = change,
                ChangeInk = ink,
            });
        }

        // Выводы: только то, на что стоит обратить внимание.
        var top = w.Games[0];
        w.Notes.Add($"Больше всего — {top.Game}: {top.Hours}" + (w.Games.Count > 1 ? $". Всего за неделю — {Fmt.Plural(w.Games.Count, "игра", "игры", "игр")}." : "."));
        if (w.PrevHours > 0.5)
        {
            double d = w.Hours - w.PrevHours;
            if (Math.Abs(d) >= 1) w.Notes.Add($"Играли на {Hours(Math.Abs(d))} {(d > 0 ? "больше" : "меньше")}, чем неделей раньше (тогда — {Hours(w.PrevHours)}).");
        }
        foreach (var g in week.GroupBy(s => s.Game))
        {
            double? fps = WeightedFps(g), before = WeightedFps(prev.Where(s => s.Game == g.Key));
            if (fps > 0 && before > 0 && fps < before * 0.92)
                w.Notes.Add($"{g.Key}: FPS упал с {before:0} до {fps:0}. Если игру не обновляли и настройки те же — посмотрите разбор просадок в её сессиях.");
        }
        var causes = week.Where(s => s.Dips != null).SelectMany(s => s.Dips).GroupBy(d => d.Cause).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (causes != null && causes.Key != "unknown" && causes.Count() >= 3)
            w.Notes.Add($"Главная причина просадок за неделю — {SessionAnalysis.CauseTitle(causes.Key)} ({Fmt.Plural(causes.Count(), "раз", "раза", "раз")}).");
        int lowRam = week.Count(s => s.MinRamGb > 0 && s.MinRamGb < 1.0);
        if (lowRam >= 2) w.Notes.Add($"В {Fmt.Plural(lowRam, "сессии", "сессиях", "сессиях")} из {week.Count} не хватало памяти. Перед игрой закрывайте тяжёлые программы (вкладка «Оптимизация») или добавьте памяти.");
        if (w.MaxTemp >= 83) w.Notes.Add($"Видеокарта нагревалась до {w.MaxTemp:0} °C — проверьте обдув корпуса.");
        return w;
    }

    private static double? WeightedFps(IEnumerable<GameSession> sessions)
    {
        var list = sessions.Where(s => s.AvgFps > 0).ToList();
        double secs = list.Sum(s => s.Duration.TotalSeconds);
        return secs > 0 ? list.Sum(s => s.AvgFps.Value * s.Duration.TotalSeconds) / secs : null;
    }

    public static string Hours(double h) => h >= 1 ? $"{h:0.#} ч" : $"{h * 60:0} мин";
}
