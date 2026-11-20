using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PulsePC.Services;

namespace PulsePC.Panels;

/// <summary>
/// Раздел «Электричество»: расход компьютера и его цена — за сутки, неделю и месяц (прогноз по средним), в играх и без игр,
/// по каждой игре. Видеокарта — измерено её датчиком, процессор и остальное — оценка. Ничего не меняет в системе:
/// тариф и мощность монитора хранятся в настройках программы.
/// </summary>
public partial class PowerCostPanel : UserControl, ISystemPanel
{
    private const string Nb = " ";
    private readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(2) };
    private PanelHost host;
    private PowerReport report;
    private bool loading;

    public PowerCostPanel()
    {
        InitializeComponent();
        LegendHatch.Fill = PowerBar.Hatch;
        tick.Tick += (_, _) => RefreshNow();
        IsVisibleChanged += (_, _) => { if (IsVisible && host != null) { tick.Start(); RefreshNow(); } else tick.Stop(); };
    }

    public void Attach(PanelHost h)
    {
        host = h;
        // Тариф и монитор — настройки самой программы, систему они не меняют: права администратора не нужны.
        AdminNote.Visibility = Visibility.Collapsed;
        LoadSaved();
        if (IsVisible) tick.Start();
    }

    public void Shown()
    {
        if (host == null) return;
        LoadSaved();
        Recalc();
        if (IsVisible) tick.Start();
    }

    // ================================================================== тариф
    private void LoadSaved()
    {
        loading = true;
        TariffBox.Text = PowerCost.Show(PowerCost.Tariff(host.Settings));
        MonitorBox.Text = PowerCost.Show(PowerCost.Monitor(host.Settings));
        loading = false;
        UpdateSave();
    }

    private void UpdateSave()
    {
        if (host == null) return;
        bool same = Norm(TariffBox.Text) == Norm(PowerCost.Show(PowerCost.Tariff(host.Settings)))
                    && Norm(MonitorBox.Text) == Norm(PowerCost.Show(PowerCost.Monitor(host.Settings)));
        BtnSave.IsEnabled = !same;
        static string Norm(string s) => (s ?? "").Trim().Replace('.', ',');
    }

    private void Box_TextChanged(object sender, TextChangedEventArgs e) { if (!loading) UpdateSave(); }

    private void Box_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && BtnSave.IsEnabled) { Save_Click(sender, e); e.Handled = true; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (host == null) return;
        string tariffText = TariffBox.Text.Trim();
        double tariff = double.NaN;
        if (tariffText.Length > 0 && !PowerCost.TryParseTariff(tariffText, out tariff))
        {
            ShowResult("Тариф не понят: впишите число от 0,1 до 100, например 6,43.", true);
            TariffBox.Focus();
            return;
        }
        if (!PowerCost.TryParseMonitor(MonitorBox.Text, out double monitor))
        {
            ShowResult("Мощность монитора не понята: впишите число от 5 до 300 или оставьте поле пустым.", true);
            MonitorBox.Focus();
            return;
        }
        host.Settings.Set(PowerCost.KTariff, double.IsNaN(tariff) ? null : tariff.ToString(CultureInfo.InvariantCulture));
        host.Settings.Set(PowerCost.KMonitor, monitor > 0 ? monitor.ToString(CultureInfo.InvariantCulture) : null);
        host.Settings.Save();
        LoadSaved();
        ShowResult(double.IsNaN(tariff) ? "Тариф убран: расход показывается только в киловатт-часах."
                   : $"Сохранено: {PowerCost.Show(tariff)}{Nb}₽ за кВт·ч" + (monitor > 0 ? $", монитор {monitor:0}{Nb}Вт." : ", без монитора."), false);
        Recalc();
    }

    private void ShowResult(string text, bool warn)
    {
        Result.Text = text;
        Result.Foreground = warn ? Palette.Warn : Palette.Ink3;
        Result.Visibility = Visibility.Visible;
    }

    // ================================================================== расчёт
    private void Recalc()
    {
        if (host == null) return;
        var h = host;
        Task.Run(() =>
        {
            PowerReport r;
            try { r = PowerCost.Current(h); } catch { return; }
            h.Ui(() => Show(r));
        });
    }

    private void Show(PowerReport r)
    {
        report = r;
        var g = r.Game; var i = r.Idle;
        double month = r.KwhDay * 30, gameMonth = (double.IsNaN(g.KwhPerDay) ? 0 : g.KwhPerDay) * 30;

        // ---- вывод
        if (r.HasForecast && r.HasTariff)
            State.Text = gameMonth > 0
                ? $"Около {PowerCost.Money(r.Rub(month))}{Nb}₽ в месяц, из них на игры — {PowerCost.Money(r.Rub(gameMonth))}{Nb}₽."
                : $"Около {PowerCost.Money(r.Rub(month))}{Nb}₽ в месяц. Игр за эти дни не было.";
        else if (r.HasForecast)
            State.Text = $"Около {PowerCost.Kwh(month)}{Nb}кВт·ч в месяц. Впишите тариф, чтобы перевести это в рубли.";
        else if (r.Games.Any(x => !double.IsNaN(x.TotalW)))
            State.Text = "Расход по играм уже виден, а прогноз на месяц появится, когда наберётся статистика за день.";
        else
            State.Text = "Данных пока мало: расход появится, когда программа поработает хотя бы час.";

        // ---- сутки, неделя, месяц
        ForecastBody.Visibility = r.HasForecast ? Visibility.Visible : Visibility.Collapsed;
        ForecastEmpty.Visibility = r.HasForecast ? Visibility.Collapsed : Visibility.Visible;
        ForecastEmpty.Text = "Прогноз строится по тому, сколько часов в сутки работает компьютер и сколько из них идёт игра. "
                           + "Статистика копится, пока программа запущена, в том числе свёрнутая к часам.";
        if (r.HasForecast)
        {
            Period(DayMain, DaySub, r, r.KwhDay, "за сутки");
            Period(WeekMain, WeekSub, r, r.KwhDay * 7, "за неделю");
            Period(MonthMain, MonthSub, r, month, "за месяц");
            string days = Fmt.Plural(r.Days, "день", "дня", "дней");
            ForecastNote.Text = r.IdleKnown
                ? $"Прогноз по средним за {days}: компьютер работал {Hours(g.HoursPerDay + i.HoursPerDay)} в сутки, из них игры — {Hours(g.HoursPerDay)}. "
                  + "Время, когда программа была закрыта, не учтено, поэтому настоящий расход может быть больше."
                : $"Время игр — по записанным сессиям за {days}. Сколько компьютер работает без игр, пока неизвестно: в прогнозе только игры.";

            ModeRow(g, r, GameWhat, GameHours, GameW, GameKwh, GameRub);
            ModeRow(i, r, IdleWhat, IdleHours, IdleW, IdleKwh, IdleRub);
            if (!r.IdleKnown) { IdleWhat.Text = "нет статистики"; IdleHours.Text = IdleKwh.Text = IdleRub.Text = "—"; }
        }

        // ---- из чего складывается
        bool parts = !double.IsNaN(g.GpuW);
        PartsBlock.Visibility = parts ? Visibility.Visible : Visibility.Collapsed;
        if (parts)
        {
            var list = new List<PartRow>
            {
                new("Видеокарта", r.Games.Count > 0 ? "средняя за игры по записанным сессиям" : "средняя за минуты игры", g.GpuW, "измерено", PowerBar.Solid),
                new("Процессор", $"{r.Tdp}{Nb}Вт по паспорту × загрузка {g.CpuLoad:0}{Nb}%" + (g.LoadMeasured ? "" : " (принято, нет записей)") + (r.TdpKnown ? "" : "; модель не узнана"),
                    g.CpuW, "оценка", PowerBar.Hatch),
                new("Плата, память, диски, вентиляторы", "обычно 40–60" + Nb + "Вт", g.BaseW, "оценка", PowerBar.HatchLight),
                new("Потери в блоке питания", "около 10" + Nb + "% от всего остального", g.LossW, "оценка", PowerBar.HatchLight),
            };
            if (g.MonitorW > 0) list.Add(new("Монитор", "по вашим данным", g.MonitorW, "вписано вами", PowerBar.HatchLight));
            Parts.ItemsSource = list;
            Bar.SetParts(list.Select(p => (p.W, p.Swatch)).ToList(), g.TotalW);
        }

        // ---- по играм
        var rows = r.Games.Select(c => new GameRow
        {
            Game = c.Game,
            Count = Fmt.Plural(c.Sessions, "сессия", "сессии", "сессий") + " · " + Fmt.Duration(TimeSpan.FromHours(c.Hours))
                    + (double.IsNaN(c.TotalW) ? "" : $" · {PowerCost.Kwh(c.Kwh)}{Nb}кВт·ч" + (r.HasTariff ? $", {PowerCost.Money(r.Rub(c.Kwh))}{Nb}₽" : "")),
            Gpu = double.IsNaN(c.GpuW) ? "—" : $"{c.GpuW:0}{Nb}Вт",
            Total = double.IsNaN(c.TotalW) ? "—" : $"{c.TotalW:0}{Nb}Вт",
            KwhHour = double.IsNaN(c.TotalW) ? "—" : PowerCost.Kwh(c.KwhPerHour),
            RubHour = double.IsNaN(c.TotalW) || !r.HasTariff ? "—" : $"{PowerCost.Money(r.Rub(c.KwhPerHour))}{Nb}₽",
        }).ToList();
        GamesBlock.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GameList.ItemsSource = rows;
        GamesNote.Text = (r.HasTariff ? "" : "Впишите тариф, чтобы увидеть рубли. ")
                       + "У сессий без датчика видеокарты расход не считается — там прочерк.";
        GamesNote.Visibility = r.HasTariff && r.Games.All(c => !double.IsNaN(c.GpuW)) ? Visibility.Collapsed : Visibility.Visible;

        // ---- как считается
        string cpu = ShortCpu(r.CpuName);
        HowCpu.Text = r.TdpKnown
            ? $"Процессор — оценка: паспортная мощность {(cpu.Length > 0 ? cpu + " — " : "")}{r.Tdp}{Nb}Вт, умноженная на его среднюю загрузку. Под нагрузкой настоящий расход бывает и больше, и меньше."
            : $"Процессор — оценка: модель {(cpu.Length > 0 ? "«" + cpu + "» " : "")}программа не знает, поэтому взято {r.Tdp}{Nb}Вт, как у обычного настольного процессора, и умножено на среднюю загрузку.";
        NowCpuCaption.Text = "процессор · оценка";
        RefreshNow();
    }

    private static void Period(NumberText main, TextBlock sub, PowerReport r, double kwh, string when)
    {
        if (r.HasTariff)
        {
            double rub = r.Rub(kwh);
            main.Unit = "₽";
            main.Format = rub < 10 ? "0.00" : rub < 100 ? "0.0" : "0";
            main.Value = rub;
            sub.Text = $"{when} · {PowerCost.Kwh(kwh)}{Nb}кВт·ч";
        }
        else
        {
            main.Unit = "кВт·ч";
            main.Format = kwh < 10 ? "0.00" : kwh < 100 ? "0.0" : "0";
            main.Value = kwh;
            sub.Text = when;
        }
    }

    private static void ModeRow(PowerMode p, PowerReport r, TextBlock what, TextBlock hours, TextBlock w, TextBlock kwh, TextBlock rub)
    {
        what.Text = double.IsNaN(p.GpuW) ? "видеокарта не сообщает потребление"
                  : $"видеокарта {p.GpuW:0}{Nb}Вт · " + (p.GpuMeasured ? "измерено" : "оценка");
        hours.Text = double.IsNaN(p.HoursPerDay) ? "—" : Hours(p.HoursPerDay);
        w.Text = double.IsNaN(p.TotalW) ? "—" : $"{p.TotalW:0}{Nb}Вт";
        kwh.Text = PowerCost.Kwh(p.KwhPerDay);
        rub.Text = r.HasTariff && !double.IsNaN(p.KwhPerDay) ? $"{PowerCost.Money(r.Rub(p.KwhPerDay * 30))}{Nb}₽" : "—";
    }

    private static string Hours(double h) => h <= 0 || double.IsNaN(h) ? "0 мин" : Fmt.Duration(TimeSpan.FromHours(h));

    /// <summary>«12th Gen Intel(R) Core(TM) i5-12400F» → «Intel Core i5-12400F».</summary>
    private static string ShortCpu(string name)
    {
        string s = Regex.Replace(name ?? "", @"\((R|TM)\)|®|™", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"^\s*\d+(th|st|nd|rd) Gen\s+", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+(CPU\s*)?@.*$|\s+\d+-Core Processor.*$|\s+with Radeon.*$|\s+Processor$", "", RegexOptions.IgnoreCase);
        return Regex.Replace(s, @"\s{2,}", " ").Trim();
    }

    // ================================================================== сейчас
    private void RefreshNow()
    {
        if (host == null || report == null) return;
        var p = PowerCost.Now(host.Live(), report.Tdp, report.MonitorW);
        NowGpu.Value = p.GpuW;
        NowCpu.Value = p.CpuW;
        NowRest.Value = double.IsNaN(p.LossW) ? p.BaseW : p.BaseW + p.LossW + p.MonitorW;
        NowTotal.Value = p.TotalW;
        NowNote.Text = double.IsNaN(p.GpuW)
            ? "Видеокарта сейчас не сообщает потребление, поэтому общий расход не посчитан."
            : double.IsNaN(p.CpuLoad) ? "" : $"Процессор загружен на {p.CpuLoad:0}{Nb}%: {report.Tdp}{Nb}Вт × {p.CpuLoad:0}{Nb}% ≈ {p.CpuW:0}{Nb}Вт.";
        NowNote.Visibility = NowNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================================================================== строки
    private sealed class PartRow
    {
        public PartRow(string name, string note, double w, string kind, Brush swatch) { Name = name; Note = note; W = w; Kind = kind; Swatch = swatch; }
        public string Name { get; }
        public string Note { get; }
        public double W { get; }
        public string Watts => double.IsNaN(W) ? "—" : $"{W:0}{Nb}Вт";
        public string Kind { get; }
        public Brush Swatch { get; }
    }

    private sealed class GameRow
    {
        public string Game { get; init; }
        public string Count { get; init; }
        public string Gpu { get; init; }
        public string Total { get; init; }
        public string KwhHour { get; init; }
        public string RubHour { get; init; }
    }
}

/// <summary>
/// Полоса «из чего складывается расход»: части по ширине пропорционально ваттам, измеренное — сплошной тушью,
/// оценённое — штриховкой. Сверху размерная линия с общим расходом, снизу ватты частей, если помещаются.
/// </summary>
public sealed class PowerBar : FrameworkElement
{
    public static readonly Brush Solid = Palette.Ink1;
    public static readonly Brush Hatch = MakeHatch(Palette.Ink2, 4);
    public static readonly Brush HatchLight = MakeHatch(Palette.Ink4, 4);
    private static readonly Typeface Face = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Pen Edge = Freeze(new Pen(Palette.Ink2, 1));
    private static readonly Pen Dim = Freeze(new Pen(Palette.Ink3, 1));
    private List<(double W, Brush Fill)> parts = new();
    private double total = double.NaN;

    private static Pen Freeze(Pen p) { p.Freeze(); return p; }

    private static Brush MakeHatch(Brush ink, double step)
    {
        var b = new DrawingBrush(new GeometryDrawing(null, new Pen(ink, 1), new LineGeometry(new Point(0, step), new Point(step, 0))))
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, step, step), ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, step, step), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None,
        };
        b.Freeze();
        return b;
    }

    public void SetParts(List<(double W, Brush Fill)> list, double totalW)
    {
        parts = list.Where(p => !double.IsNaN(p.W) && p.W > 0).ToList();
        total = totalW;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size available) => new(double.IsInfinity(available.Width) ? 600 : available.Width, 66);

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double sum = parts.Sum(p => p.W);
        if (sum <= 0 || width < 50) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        const double barTop = 22, barH = 18;

        // Размерная линия: засечки на концах и число посередине, как на чертеже.
        double y = 10;
        dc.DrawLine(Dim, new Point(0.5, y), new Point(width - 0.5, y));
        dc.DrawLine(Dim, new Point(0.5, y - 5), new Point(0.5, barTop - 2));
        dc.DrawLine(Dim, new Point(width - 0.5, y - 5), new Point(width - 0.5, barTop - 2));
        dc.DrawLine(Dim, new Point(-3, y + 3), new Point(4, y - 3));
        dc.DrawLine(Dim, new Point(width - 4, y + 3), new Point(width + 3, y - 3));
        string label = $"≈ {(double.IsNaN(total) ? sum : total):0} Вт из розетки";
        var ft = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11.5, Palette.Ink1, dpi);
        double lx = (width - ft.Width) / 2;
        dc.DrawRectangle(Palette.Base, null, new Rect(lx - 8, y - ft.Height / 2, ft.Width + 16, ft.Height));
        dc.DrawText(ft, new Point(lx, y - ft.Height / 2));

        // Части
        double x = 0;
        foreach (var (w, fill) in parts)
        {
            double pw = Math.Max(1, width * w / sum);
            var r = new Rect(Math.Round(x) + 0.5, barTop + 0.5, Math.Max(1, Math.Round(x + pw) - Math.Round(x) - 1), barH - 1);
            dc.DrawRectangle(fill, Edge, r);
            if (pw >= 34)
            {
                var t = new FormattedText($"{w:0}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10.5, Palette.Ink3, dpi);
                dc.DrawText(t, new Point(x + (pw - t.Width) / 2, barTop + barH + 5));
            }
            x += pw;
        }
    }
}
