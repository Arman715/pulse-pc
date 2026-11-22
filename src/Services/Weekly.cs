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
                change = Math.Abs(pct) < 3 ? L.T("как раньше") : (pct > 0 ? "+" : "−") + Math.Abs(pct).ToString("0", L.Culture) + (L.En ? "%" : " %");
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
        w.Notes.Add(w.Games.Count > 1
            ? L.F("Больше всего — {0}: {1}. Всего за неделю — {2}.", top.Game, top.Hours, Fmt.Plural(w.Games.Count, "игра", "игры", "игр"))
            : L.F("Больше всего — {0}: {1}.", top.Game, top.Hours));
        if (w.PrevHours > 0.5)
        {
            double d = w.Hours - w.PrevHours;
            if (Math.Abs(d) >= 1)
                w.Notes.Add(d > 0
                    ? L.F("Играли на {0} больше, чем неделей раньше (тогда — {1}).", Hours(Math.Abs(d)), Hours(w.PrevHours))
                    : L.F("Играли на {0} меньше, чем неделей раньше (тогда — {1}).", Hours(Math.Abs(d)), Hours(w.PrevHours)));
        }
        foreach (var g in week.GroupBy(s => s.Game))
        {
            double? fps = WeightedFps(g), before = WeightedFps(prev.Where(s => s.Game == g.Key));
            if (fps > 0 && before > 0 && fps < before * 0.92)
                w.Notes.Add(L.F("{0}: FPS упал с {1:0} до {2:0}. Если игру не обновляли и настройки те же — посмотрите разбор просадок в её сессиях.", g.Key, before, fps));
        }
        var causes = week.Where(s => s.Dips != null).SelectMany(s => s.Dips).GroupBy(d => d.Cause).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (causes != null && causes.Key != "unknown" && causes.Count() >= 3)
            w.Notes.Add(L.F("Главная причина просадок за неделю — {0} ({1}).", SessionAnalysis.CauseTitle(causes.Key), SessionAnalysis.Times(causes.Count())));
        int lowRam = week.Count(s => s.MinRamGb > 0 && s.MinRamGb < 1.0);
        if (lowRam >= 2) w.Notes.Add(L.F("В {0} из {1} не хватало памяти. Перед игрой закрывайте тяжёлые программы (вкладка «Оптимизация») или добавьте памяти.", Fmt.Plural(lowRam, "сессии", "сессиях", "сессиях"), week.Count));
        // Тот же порог, что в замечаниях к сессии: у ноутбука видеокарта рассчитана на 87 °C, и 84 °C для него — не повод.
        if (w.MaxTemp >= SessionAnalysis.HotMaxTempC(LaptopCheck.Applies)) w.Notes.Add(LaptopCheck.Applies
            ? L.F("Видеокарта нагревалась до {0:0} °C.", w.MaxTemp) + " " + LaptopCheck.CoolingTip()
            :L.F("Видеокарта нагревалась до {0:0} °C — проверьте обдув корпуса.", w.MaxTemp));
        return w;
    }

    private static double? WeightedFps(IEnumerable<GameSession> sessions)
    {
        var list = sessions.Where(s => s.AvgFps > 0).ToList();
        double secs = list.Sum(s => s.Duration.TotalSeconds);
        return secs > 0 ? list.Sum(s => s.AvgFps.Value * s.Duration.TotalSeconds) / secs : null;
    }

    public static string Hours(double h) => h >= 1 ? L.F("{0:0.#} ч", h) : L.F("{0:0} мин", h * 60);
}
