using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PulsePC.Services;

namespace PulsePC.Panels;

/// <summary>
/// Раздел «Вентиляторы»: скорость вентиляторов видеокарты сейчас, режим управления (драйвер, своя кривая, постоянная скорость),
/// статистика за час, сутки и неделю — в играх и вне их, по каждой игре. Вентиляторы корпуса — только объяснение, где их настроить.
/// Режим меняется кнопкой «Применить»: до неё правки только на экране.
/// </summary>
public partial class FansPanel : UserControl, ISystemPanel
{
    private const string Nb = " ";
    private readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(2) };
    private PanelHost host;
    private int range = 1440, statsTick;
    private bool statsShown, loading;

    // Правки на экране (до «Применить»)
    private FanControl.Mode mode;
    private FanPoint[] curve = FanControl.Balanced;
    private int fixedLevel = 50;
    private bool gamesOnly, stopCold = true;

    public FansPanel()
    {
        InitializeComponent();
        tick.Tick += (_, _) => { RefreshNow(); if (++statsTick % 15 == 0) { RefreshStats(false); RefreshDust(); } };
        IsVisibleChanged += (_, _) => { if (IsVisible && host != null) { tick.Start(); RefreshNow(); } else tick.Stop(); };
        Curve.Edited += pts => { curve = pts; UpdatePending(); };
        // Новый замер раз в 2 секунды. Пока раздел скрыт в окне, обновлять незачем.
        FanControl.Changed += () => host?.Ui(() => { if (IsVisible || !IsLoaded) RefreshNow(); });
        if (StatsChart.Zoom != null) StatsChart.Zoom.Changed += _ => UpdateAxis();
    }

    public void Attach(PanelHost h)
    {
        host = h;
        FanControl.Init(h);             // обычно уже вызван при запуске; повторный вызов ничего не делает
        bool admin = h.Admin;
        foreach (var c in new Control[] { ModeDriver, ModeCurve, ModeFixed, PresetQuiet, PresetBalanced, PresetCool, StopCold, GamesOnly, FixedDown, FixedUp, BtnApply, BtnTest })
            c.IsEnabled = admin;
        Curve.IsEnabled = admin;
        BtnCleaned.IsEnabled = admin;
        DustAdminNote.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
        AdminNote.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
        LoadSaved();
        FillCase();
        if (IsVisible) tick.Start();
        RefreshNow();
    }

    public void Shown()
    {
        if (host == null) return;
        LoadSaved();
        RefreshNow();
        RefreshStats(!statsShown);
        RefreshDust();
        statsShown = true;
        if (IsVisible) tick.Start();
    }

    // ================================================================== сейчас
    private void RefreshNow()
    {
        if (host == null) return;
        var v = FanControl.Snapshot();
        if (!v.Ready) { NoControl.Visibility = Visibility.Collapsed; return; }

        NowRpm.Value = v.Rpm;
        NowLevel.Value = v.Level;
        NowTemp.Value = v.Temp;
        NowTemp.Foreground = v.Temp >= 83 ? Palette.Warn : Palette.Ink1;
        NowWho.Text = !v.CanControl ? (v.HasReading ? L.T("драйвер") : "—") : v.Testing ? L.T("проверка") : v.Held ? L.T("программа") : L.T("драйвер");
        NowReason.Text = v.CanControl ? v.Reason : "";
        NowReason.Visibility = NowReason.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        State.Text = !v.HasReading
            ? (v.Vendor == "NVIDIA" || v.Vendor == "" ? L.T("Вентиляторы видеокарты не читаются.") : L.T("Видеокарта не сообщает скорость вентиляторов."))
            : v.Rpm < 1 && !double.IsNaN(v.Rpm)
                ? (double.IsNaN(v.Temp) ? L.T("Вентиляторы видеокарты стоят.") : L.F("Вентиляторы видеокарты стоят: видеокарта холодная, {0:0} °C.", v.Temp))
                : Spinning(v.Level, v.Rpm);

        NoControl.Visibility = v.CanControl ? Visibility.Collapsed : Visibility.Visible;
        NoControlText.Text = v.Why;
        ControlBlock.Visibility = v.CanControl ? Visibility.Visible : Visibility.Collapsed;

        Curve.MinLevel = v.Min;
        Curve.SetNow(v.Temp, v.Level);

        if (v.LastAction.Length > 0 && (DateTime.Now - v.LastActionAt).TotalMinutes < 30)
        {
            Result.Text = v.LastAction + $" ({v.LastActionAt:HH:mm})";
            Result.Visibility = Visibility.Visible;
        }
    }

    private static string Spinning(double level, double rpm) =>
        double.IsNaN(level)
            ? (double.IsNaN(rpm) ? L.T("Вентиляторы видеокарты крутятся.") : L.F("Вентиляторы видеокарты крутятся, {0:0} об/мин.", rpm))
            : (double.IsNaN(rpm) ? L.F("Вентиляторы видеокарты крутятся на {0:0} %.", level) : L.F("Вентиляторы видеокарты крутятся на {0:0} %, {1:0} об/мин.", level, rpm));

    // ================================================================== управление
    private void LoadSaved()
    {
        loading = true;
        var s = host.Settings;
        mode = FanControl.CurrentMode(s);
        curve = FanControl.CurrentCurve(s);
        fixedLevel = FanControl.CurrentFixed(s);
        var v = FanControl.Snapshot();
        gamesOnly = v.GamesOnly; stopCold = v.StopCold;
        ShowEdits();
        loading = false;
        UpdatePending();
    }

    private void ShowEdits()
    {
        ModeDriver.IsChecked = mode == FanControl.Mode.Driver;
        ModeCurve.IsChecked = mode == FanControl.Mode.Curve;
        ModeFixed.IsChecked = mode == FanControl.Mode.Fixed;
        CurveBlock.Visibility = mode == FanControl.Mode.Curve ? Visibility.Visible : Visibility.Collapsed;
        FixedBlock.Visibility = mode == FanControl.Mode.Fixed ? Visibility.Visible : Visibility.Collapsed;
        GamesRow.Visibility = mode == FanControl.Mode.Driver ? Visibility.Collapsed : Visibility.Visible;
        Curve.Points = curve;
        FixedValue.Value = fixedLevel;
        GamesOnly.IsChecked = gamesOnly;
        StopCold.IsChecked = stopCold;
    }

    private void UpdatePending()
    {
        if (loading || host == null) return;
        var s = host.Settings;
        var v = FanControl.Snapshot();
        bool same = mode == FanControl.CurrentMode(s)
                    && (mode != FanControl.Mode.Curve || FanControl.FormatCurve(FanControl.Normalize(curve)) == FanControl.FormatCurve(FanControl.CurrentCurve(s)))
                    && (mode != FanControl.Mode.Fixed || fixedLevel == FanControl.CurrentFixed(s))
                    && (mode == FanControl.Mode.Driver || gamesOnly == v.GamesOnly)
                    && (mode != FanControl.Mode.Curve || stopCold == v.StopCold);
        Pending.Visibility = same ? Visibility.Collapsed : Visibility.Visible;
        BtnApply.IsEnabled = host.Admin && !same;
    }

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        mode = sender == ModeCurve ? FanControl.Mode.Curve : sender == ModeFixed ? FanControl.Mode.Fixed : FanControl.Mode.Driver;
        ShowEdits();
        UpdatePending();
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        curve = sender == PresetQuiet ? FanControl.Quiet : sender == PresetCool ? FanControl.Cool : FanControl.Balanced;
        Curve.Points = curve;
        UpdatePending();
    }

    private void Fixed_Click(object sender, RoutedEventArgs e)
    {
        fixedLevel = Math.Clamp(fixedLevel + (sender == FixedUp ? 5 : -5), 30, 100);
        FixedValue.Value = fixedLevel;
        UpdatePending();
    }

    private void Option_Click(object sender, RoutedEventArgs e)
    {
        gamesOnly = GamesOnly.IsChecked == true;
        stopCold = StopCold.IsChecked == true;
        UpdatePending();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (host == null || !host.Admin) return;
        FanControl.Configure(mode, curve, fixedLevel, gamesOnly, stopCold);
        Result.Text = mode switch
        {
            FanControl.Mode.Curve => gamesOnly ? L.T("Кривая сохранена: скорость меняется через пару секунд, когда запустится игра.")
                                               : L.T("Кривая сохранена: скорость меняется через пару секунд."),
            FanControl.Mode.Fixed => gamesOnly ? L.F("Постоянная скорость {0} % сохранена: включится, когда запустится игра.", fixedLevel)
                                               : L.F("Постоянная скорость {0} % сохранена.", fixedLevel),
            _ => L.T("Скорость снова решает драйвер видеокарты."),
        };
        Result.Visibility = Visibility.Visible;
        UpdatePending();
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        if (host == null || !host.Admin) return;
        FanControl.Test();
        Result.Text = L.T("Проверка: вентиляторы разгоняются до полной скорости и через 6 секунд вернутся к выбранному режиму.");
        Result.Visibility = Visibility.Visible;
    }

    // ================================================================== статистика
    private void Range_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out int m)) range = m;
        StatsChart?.Zoom?.Reset(false);
        if (host != null) RefreshStats(true);
    }

    private void RefreshStats(bool reveal)
    {
        if (host == null) return;
        var w = FanControl.Window(range);
        bool any = w.Fan.Any(x => !double.IsNaN(x)) || w.Temp.Any(x => !double.IsNaN(x));
        StatsEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        StatsBody.Visibility = any ? Visibility.Visible : Visibility.Collapsed;

        if (any)
        {
            if (StatsChart.Zoom != null) StatsChart.Zoom.Full = TimeSpan.FromMinutes(range);
            StatsChart.SetData(w.Temp, w.Fan, reveal);
            statsFrom = w.From;
            UpdateAxis();

            var game = FanControl.Summary(range, true);
            var idle = FanControl.Summary(range, false);
            Row(game, GameTime, GameAvg, GameMax, GameRpm, GameStop);
            Row(idle, IdleTime, IdleAvg, IdleMax, IdleRpm, IdleStop);
            StatsNote.Text = Advice(game, idle);
            StatsNote.Visibility = StatsNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        FillGames();
    }

    private DateTime statsFrom;

    private void UpdateAxis()
    {
        var z = StatsChart.Zoom;
        if (z == null || statsFrom == default) return;
        static string At(DateTime t) => t.ToString(t.Date == DateTime.Today ? "HH:mm" : L.En ? "MMM d, HH:mm" : "d MMM, HH:mm", L.Culture);
        AxisFrom.Text = At(statsFrom.AddMinutes(range * z.Start));
        AxisTo.Text = z.Start + z.Span >= 0.999 ? L.T("сейчас") : At(statsFrom.AddMinutes(range * (z.Start + z.Span)));
    }

    private static void Row(FanSummary s, TextBlock time, TextBlock avg, TextBlock max, TextBlock rpm, TextBlock stop)
    {
        time.Text = s.Minutes == 0 ? L.T("не было") : Fmt.Duration(TimeSpan.FromMinutes(s.Minutes));
        avg.Text = Pct(s.AvgFan);
        max.Text = Pct(s.MaxFan);
        rpm.Text = double.IsNaN(s.AvgRpm) ? "—" : L.En ? s.AvgRpm.ToString("#,##0", L.Culture) : $"{s.AvgRpm:### ##0}".Trim().Replace(' ', ' ');
        stop.Text = Pct(s.StopPct);
    }

    private static string Pct(double v) => double.IsNaN(v) ? "—" : $"{v:0}{Nb}%";

    /// <summary>Одна-две строки вывода по статистике: только то, что из неё действительно следует.</summary>
    private static string Advice(FanSummary game, FanSummary idle)
    {
        var lines = new List<string>();
        if (game.Minutes >= 10 && game.MaxTemp >= 80 && game.AvgFan < 65)
            lines.Add(L.F("В играх видеокарта нагревается до {0:0} °C, а вентиляторы крутятся в среднем на {1:0} %. Запас есть: кривая «Прохладнее» снизит температуру ценой шума.", game.MaxTemp, game.AvgFan));
        else if (game.Minutes >= 10 && game.MaxTemp < 70 && game.AvgFan >= 60)
            lines.Add(L.F("В играх видеокарта не горячее {0:0} °C, а вентиляторы крутятся на {1:0} % — можно тише: кривая «Тише» оставит температуру в норме.", game.MaxTemp, game.AvgFan));
        if (idle.Minutes >= 30 && idle.StopPct >= 50)
            lines.Add(L.F("Вне игр вентиляторы стояли {0:0} % времени: видеокарте хватает пассивного охлаждения, так и задумано.", idle.StopPct));
        return string.Join(" ", lines);
    }

    private sealed class GameFanRow
    {
        public string Game { get; init; }
        public string Count { get; init; }
        public string Avg { get; init; }
        public string Max { get; init; }
        public string Temp { get; init; }
        public Brush TempInk { get; init; }
    }

    private void FillGames()
    {
        var rows = host.Sessions()
            .Where(s => s.AvgFanPct.HasValue)
            .GroupBy(s => s.Game)
            .Select(g =>
            {
                double maxTemp = g.Max(s => s.MaxTemp);
                return new
                {
                    Avg = g.Average(s => s.AvgFanPct.Value),
                    Row = new GameFanRow
                    {
                        Game = g.Key,
                        Count = Fmt.Plural(g.Count(), "сессия", "сессии", "сессий") + " · " + Fmt.Duration(TimeSpan.FromSeconds(g.Sum(s => s.Duration.TotalSeconds))),
                        Avg = Pct(g.Average(s => s.AvgFanPct.Value)),
                        Max = Pct(g.Max(s => s.MaxFanPct ?? double.NaN)),
                        Temp = maxTemp > 0 ? $"{maxTemp:0}{Nb}°C" : "—",
                        TempInk = maxTemp >= 83 ? Palette.Warn : Palette.Ink1,
                    },
                };
            })
            .OrderByDescending(x => x.Avg)
            .Select(x => x.Row)
            .ToList();
        GamesBlock.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GameList.ItemsSource = rows;
    }

    // ================================================================== пыль
    private void RefreshDust()
    {
        if (host == null) return;
        Task.Run(() =>
        {
            DustView v;
            try { v = FanControl.Dust(notify: false); } catch { return; }
            host.Ui(() => ShowDust(v));
        });
    }

    private void ShowDust(DustView v)
    {
        if (v == null || v.Text.Length == 0) return;
        DustText.Text = v.Text;
        DustMarker.Level = v.Dusty ? Severity.Warn : v.BaseReady && v.Now.Enough ? Severity.Ok : Severity.Info;
        DustAlert.Visibility = v.Dusty ? Visibility.Visible : Visibility.Collapsed;
        DustAlertText.Text = L.T("Похоже, пора почистить компьютер от пыли: видеокарта в играх стала горячее, чем раньше.");
        if (v.Dusty) BtnCleaned.Style = (Style)FindResource("Primary"); else BtnCleaned.ClearValue(StyleProperty);

        var b = v.Base; var c = v.Now;
        DustTable.Visibility = b.Minutes > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (b.Minutes == 0) return;
        DustBaseWhen.Text = (v.BaseReady ? Span(b.From, b.To) : L.F("копится с {0}", Day(v.Since))) + " · " + PlayTime(b.Minutes);
        DustNowWhen.Text = !v.BaseReady ? L.T("после опорного периода")
                         : c.Minutes == 0 ? L.F("последние {0} дней · пока нет", FanControl.DustDays)
                         : L.F("последние {0} дней · {1}", FanControl.DustDays, PlayTime(c.Minutes));
        DustCells(b, DustBaseGame, DustBaseIdle, DustBaseDelta, DustBaseFan, DustBaseWatts);
        DustCells(v.BaseReady ? c : new DustPeriod(), DustNowGame, DustNowIdle, DustNowDelta, DustNowFan, DustNowWatts);
        // Что выросло настолько, что это заметно, — цветом предупреждения.
        DustNowDelta.Foreground = v.Dusty || v.TempRise >= FanControl.DustTempRise ? Palette.Warn : Palette.Ink1;
        DustNowFan.Foreground = v.Dusty && v.FanRise >= FanControl.DustFanRise ? Palette.Warn : Palette.Ink1;
    }

    private static void DustCells(DustPeriod p, TextBlock game, TextBlock idle, TextBlock delta, TextBlock fan, TextBlock watts)
    {
        bool any = p.Minutes > 0;
        game.Text = any ? $"{p.GameTemp:0}{Nb}°C" : "—";
        idle.Text = any ? $"{p.IdleTemp:0}{Nb}°C" : "—";
        delta.Text = any ? $"{p.Delta:0}{Nb}°C" : "—";
        fan.Text = any && !double.IsNaN(p.Fan) ? $"{p.Fan:0}{Nb}%" : "—";
        watts.Text = any && !double.IsNaN(p.Watts) ? L.F("{0:0} Вт", p.Watts) : "—";
    }

    /// <summary>«20 нояб.» / «Nov 20».</summary>
    private static string Day(DateTime t) => t.ToString(L.En ? "MMM d" : "d MMM", L.Culture);

    private static string Span(DateTime from, DateTime to) =>
        from.Date == to.Date ? Day(from)
        : from.Month == to.Month ? (L.En ? Day(from) + "–" + to.ToString("%d", L.Culture) : from.ToString("%d", L.Culture) + "–" + Day(to))
        : Day(from) + " – " + Day(to);

    private static string PlayTime(int minutes) => Fmt.Duration(TimeSpan.FromMinutes(minutes)) + L.Pick(" игры", " of gaming");

    private void Cleaned_Click(object sender, RoutedEventArgs e)
    {
        if (host == null || !host.Admin) return;
        FanControl.DustReset();
        DustResult.Text = L.F("Отмечено: компьютер почищен. Следующие {0} дней станут новым образцом «как было».", FanControl.DustDays);
        DustResult.Visibility = Visibility.Visible;
        RefreshDust();
    }

    private void DustMore_Click(object sender, RoutedEventArgs e) => DustHeader.BringIntoView(new Rect(0, 0, 1, 400));

    // ================================================================== корпус
    private void FillCase()
    {
        var hw = host.Hardware();
        string maker = hw?.MakerShort ?? "";
        string board = hw?.BoardShort;
        bool laptop = BatteryInfo.Read().HasBattery;

        if (laptop)
        {
            CaseText.Text = L.T("В ноутбуке вентиляторами управляет сам ноутбук. Windows не даёт программам доступа к ним, поэтому их скорость здесь не видна.");
            CaseHow.Text = L.T("Режимы охлаждения ноутбука обычно есть в фирменной программе производителя: «тихий», «сбалансированный», «производительный». В неё стоит заглянуть, если ноутбук в играх сильно шумит или греется.");
            return;
        }

        CaseText.Text = (string.IsNullOrEmpty(board) ? L.T("Их скорость задаёт материнская плата.") : L.F("Их скорость задаёт материнская плата {0}.", board)) + " "
                      + L.T("Windows не даёт программам доступа к её датчикам без отдельного драйвера, поэтому обороты этих вентиляторов программа не видит и не меняет.");
        CaseHow.Text = maker switch
        {
            "MSI" => L.T("Настроить их можно в BIOS: при включении компьютера нажимайте Delete, затем откройте «Hardware Monitor». Там у каждого разъёма своя кривая; режим «Smart Fan» меняет скорость по температуре процессора."),
            "ASUS" => L.T("Настроить их можно в BIOS: при включении компьютера нажимайте Delete, затем F6 — «QFan Control». Там у каждого разъёма своя кривая и готовые режимы: тихий, стандартный, турбо."),
            "GIGABYTE" => L.T("Настроить их можно в BIOS: при включении компьютера нажимайте Delete, затем F6 — «Smart Fan». Там у каждого разъёма своя кривая."),
            "ASRock" => L.T("Настроить их можно в BIOS: при включении компьютера нажимайте Delete или F2, затем «H/W Monitor» → «FAN-Tastic Tuning»."),
            _ => L.T("Настроить их можно в BIOS платы: при включении компьютера нажимайте Delete или F2 и найдите раздел о вентиляторах — он называется «Fan», «Smart Fan», «Q-Fan» или «Hardware Monitor»."),
        };
    }
}
