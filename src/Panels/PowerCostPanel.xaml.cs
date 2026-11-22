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
        // Единицы в разметке заданы по-русски; по-английски деньги — без знака валюты (тариф вписывает сам пользователь).
        NowGpu.Unit = NowCpu.Unit = NowRest.Unit = NowTotal.Unit = L.T("Вт");
        DayMain.Unit = WeekMain.Unit = MonthMain.Unit = Currency;
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
        PsuBox.Text = PsuSaved();
        // Блок питания — только у настольного ПК: у ноутбука вместо него адаптер, и вывод о запасе не показывается.
        PsuRow.Visibility = IsLaptop() ? Visibility.Collapsed : Visibility.Visible;
        if (IsLaptop()) HowBase.Text = L.T("Плата, память, SSD, вентиляторы и встроенный экран ноутбука — около 15 Вт; ещё около 10 % теряется в адаптере питания. Это тоже оценка.");
        loading = false;
        UpdateSave();
    }

    private string PsuSaved() => int.TryParse(host.Settings.Get(HardwareChecks.PsuKey), out int w) && w > 0 ? w.ToString(CultureInfo.InvariantCulture) : "";

    private static bool IsLaptop() => LaptopCheck.Applies;

    /// <summary>Мощность блока питания: пусто — 0 (не указана), иначе целое от 200 до 2000 Вт.</summary>
    internal static bool TryParsePsu(string text, out int watts)
    {
        watts = 0;
        text = (text ?? "").Trim();
        if (text.Length == 0) return true;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out watts) && watts >= 200 && watts <= 2000;
    }

    private void UpdateSave()
    {
        if (host == null) return;
        bool same = Norm(TariffBox.Text) == Norm(PowerCost.Show(PowerCost.Tariff(host.Settings)))
                    && Norm(MonitorBox.Text) == Norm(PowerCost.Show(PowerCost.Monitor(host.Settings)))
                    && Norm(PsuBox.Text) == Norm(PsuSaved());
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
            ShowResult(L.T("Тариф не понят: впишите число от 0,1 до 100, например 6,43."), true);
            TariffBox.Focus();
            return;
        }
        if (!PowerCost.TryParseMonitor(MonitorBox.Text, out double monitor))
        {
            ShowResult(L.T("Мощность монитора не понята: впишите число от 5 до 300 или оставьте поле пустым."), true);
            MonitorBox.Focus();
            return;
        }
        if (!TryParsePsu(PsuBox.Text, out int psu))
        {
            ShowResult(L.T("Мощность блока питания не понята: впишите число от 200 до 2000 или оставьте поле пустым."), true);
            PsuBox.Focus();
            return;
        }
        host.Settings.Set(PowerCost.KTariff, double.IsNaN(tariff) ? null : tariff.ToString(CultureInfo.InvariantCulture));
        host.Settings.Set(PowerCost.KMonitor, monitor > 0 ? monitor.ToString(CultureInfo.InvariantCulture) : null);
        host.Settings.Set(HardwareChecks.PsuKey, psu > 0 ? psu.ToString(CultureInfo.InvariantCulture) : null);
        host.Settings.Save();
        // Наклейка блока питания на чертеже «Обзора» — указанная мощность (или без числа).
        Blueprint.BlueprintLabels.Psu = Blueprint.BlueprintLabels.PsuLabel(psu > 0 ? psu.ToString(CultureInfo.InvariantCulture) : null);
        Blueprint.BlueprintLabels.Apply();
        LoadSaved();
        ShowResult(double.IsNaN(tariff) ? L.T("Тариф убран: расход показывается только в киловатт-часах.")
                   : L.F("Сохранено: {0} ₽ за кВт·ч", PowerCost.Show(tariff)) + (monitor > 0 ? L.F(", монитор {0:0} Вт.", monitor) : L.T(", без монитора.")), false);
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
                ? L.F("Около {0} ₽ в месяц, из них на игры — {1} ₽.", PowerCost.Money(r.Rub(month)), PowerCost.Money(r.Rub(gameMonth)))
                : L.F("Около {0} ₽ в месяц. Игр за эти дни не было.", PowerCost.Money(r.Rub(month)));
        else if (r.HasForecast)
            State.Text = L.F("Около {0} кВт·ч в месяц. Впишите тариф, чтобы перевести это в рубли.", PowerCost.Kwh(month));
        else if (r.Games.Any(x => !double.IsNaN(x.TotalW)))
            State.Text = L.T("Расход по играм уже виден, а прогноз на месяц появится, когда наберётся статистика за день.");
        else
            State.Text = L.T("Данных пока мало: расход появится, когда программа поработает хотя бы час.");

        // ---- сутки, неделя, месяц
        ForecastBody.Visibility = r.HasForecast ? Visibility.Visible : Visibility.Collapsed;
        ForecastEmpty.Visibility = r.HasForecast ? Visibility.Collapsed : Visibility.Visible;
        ForecastEmpty.Text = L.T("Прогноз строится по тому, сколько часов в сутки работает компьютер и сколько из них идёт игра. Статистика копится, пока программа запущена, в том числе свёрнутая к часам.");
        if (r.HasForecast)
        {
            Period(DayMain, DaySub, r, r.KwhDay, L.T("за сутки"));
            Period(WeekMain, WeekSub, r, r.KwhDay * 7, L.T("за неделю"));
            Period(MonthMain, MonthSub, r, month, L.T("за месяц"));
            string days = Fmt.Plural(r.Days, "день", "дня", "дней");
            ForecastNote.Text = r.IdleKnown
                ? L.F("Прогноз по средним за {0}: компьютер работал {1} в сутки, из них игры — {2}.", days, Hours(g.HoursPerDay + i.HoursPerDay), Hours(g.HoursPerDay))
                  + " " + L.T("Время, когда программа была закрыта, не учтено, поэтому настоящий расход может быть больше.")
                : L.F("Время игр — по записанным сессиям за {0}. Сколько компьютер работает без игр, пока неизвестно: в прогнозе только игры.", days);

            ModeRow(g, r, GameWhat, GameHours, GameW, GameKwh, GameRub);
            ModeRow(i, r, IdleWhat, IdleHours, IdleW, IdleKwh, IdleRub);
            if (!r.IdleKnown) { IdleWhat.Text = L.T("нет статистики"); IdleHours.Text = IdleKwh.Text = IdleRub.Text = "—"; }
        }

        // ---- из чего складывается
        bool parts = !double.IsNaN(g.GpuW);
        PartsBlock.Visibility = parts ? Visibility.Visible : Visibility.Collapsed;
        if (parts)
        {
            var list = new List<PartRow>
            {
                new(L.T("Видеокарта"), r.Games.Count > 0 ? L.T("средняя за игры по записанным сессиям") : L.T("средняя за минуты игры"), g.GpuW, L.T("измерено"), PowerBar.Solid),
                new(L.T("Процессор"), L.F("{0} Вт по паспорту × загрузка {1:0} %", r.Tdp, g.CpuLoad)
                                      + (g.LoadMeasured ? "" : " " + L.T("(принято, нет записей)")) + (r.TdpKnown ? "" : L.T("; модель не узнана")),
                    g.CpuW, L.T("оценка"), PowerBar.Hatch),
                // У ноутбука постоянная часть — около 15 Вт (PowerCost.LaptopBaseW), а потери — в адаптере питания.
                IsLaptop() ? new PartRow(L.T("Плата, память, SSD, экран ноутбука"), L.T("около 15 Вт"), g.BaseW, L.T("оценка"), PowerBar.HatchLight)
                           : new PartRow(L.T("Плата, память, диски, вентиляторы"), L.T("обычно 40–60 Вт"), g.BaseW, L.T("оценка"), PowerBar.HatchLight),
                new(IsLaptop() ? L.T("Потери в адаптере питания") : L.T("Потери в блоке питания"), L.T("около 10 % от всего остального"), g.LossW, L.T("оценка"), PowerBar.HatchLight),
            };
            if (g.MonitorW > 0) list.Add(new(L.T("Монитор"), L.T("по вашим данным"), g.MonitorW, L.T("вписано вами"), PowerBar.HatchLight));
            Parts.ItemsSource = list;
            Bar.SetParts(list.Select(p => (p.W, p.Swatch)).ToList(), g.TotalW);
        }

        // ---- по играм
        var rows = r.Games.Select(c => new GameRow
        {
            Game = c.Game,
            Count = Fmt.Plural(c.Sessions, "сессия", "сессии", "сессий") + " · " + Fmt.Duration(TimeSpan.FromHours(c.Hours))
                    + (double.IsNaN(c.TotalW) ? "" : " · " + PowerCost.Kwh(c.Kwh) + KwhUnit + (r.HasTariff ? ", " + Money(r.Rub(c.Kwh)) : "")),
            Gpu = double.IsNaN(c.GpuW) ? "—" : Watts(c.GpuW),
            Total = double.IsNaN(c.TotalW) ? "—" : Watts(c.TotalW),
            KwhHour = double.IsNaN(c.TotalW) ? "—" : PowerCost.Kwh(c.KwhPerHour),
            RubHour = double.IsNaN(c.TotalW) || !r.HasTariff ? "—" : Money(r.Rub(c.KwhPerHour)),
        }).ToList();
        GamesBlock.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GameList.ItemsSource = rows;
        GamesNote.Text = (r.HasTariff ? "" : L.T("Впишите тариф, чтобы увидеть рубли.") + " ")
                       + L.T("У сессий без датчика видеокарты расход не считается — там прочерк.");
        GamesNote.Visibility = r.HasTariff && r.Games.All(c => !double.IsNaN(c.GpuW)) ? Visibility.Collapsed : Visibility.Visible;

        // ---- как считается
        string cpu = ShortCpu(r.CpuName);
        HowCpu.Text = r.TdpKnown
            ? cpu.Length > 0
                ? L.F("Процессор — оценка: паспортная мощность {0} — {1} Вт, умноженная на его среднюю загрузку. Под нагрузкой настоящий расход бывает и больше, и меньше.", cpu, r.Tdp)
                : L.F("Процессор — оценка: паспортная мощность {0} Вт, умноженная на его среднюю загрузку. Под нагрузкой настоящий расход бывает и больше, и меньше.", r.Tdp)
            : cpu.Length > 0
                ? L.F("Процессор — оценка: модель «{0}» программа не знает, поэтому взято {1} Вт, как у обычного настольного процессора, и умножено на среднюю загрузку.", cpu, r.Tdp)
                : L.F("Процессор — оценка: модель программа не знает, поэтому взято {0} Вт, как у обычного настольного процессора, и умножено на среднюю загрузку.", r.Tdp);
        NowCpuCaption.Text = L.T("процессор · оценка");
        RefreshNow();
    }

    /// <summary>Знак валюты: рубль — только по-русски; по-английски тариф в валюте пользователя, сумма без знака.</summary>
    private static string Currency => L.En ? "" : "₽";

    /// <summary>«1 240 ₽» по-русски, «1,240» по-английски.</summary>
    private static string Money(double v) => L.En ? PowerCost.Money(v) : PowerCost.Money(v) + Nb + "₽";

    private static string KwhUnit => L.Pick(" кВт·ч", " kWh");

    private static string Watts(double w) => L.F("{0:0} Вт", w);

    private static void Period(NumberText main, TextBlock sub, PowerReport r, double kwh, string when)
    {
        if (r.HasTariff)
        {
            double rub = r.Rub(kwh);
            main.Unit = Currency;
            main.Format = rub < 10 ? "0.00" : rub < 100 ? "0.0" : "0";
            main.Value = rub;
            sub.Text = when + " · " + PowerCost.Kwh(kwh) + KwhUnit;
        }
        else
        {
            main.Unit = L.T("кВт·ч");
            main.Format = kwh < 10 ? "0.00" : kwh < 100 ? "0.0" : "0";
            main.Value = kwh;
            sub.Text = when;
        }
    }

    private static void ModeRow(PowerMode p, PowerReport r, TextBlock what, TextBlock hours, TextBlock w, TextBlock kwh, TextBlock rub)
    {
        what.Text = double.IsNaN(p.GpuW) ? L.T("видеокарта не сообщает потребление")
                  : L.F("видеокарта {0:0} Вт", p.GpuW) + " · " + (p.GpuMeasured ? L.T("измерено") : L.T("оценка"));
        hours.Text = double.IsNaN(p.HoursPerDay) ? "—" : Hours(p.HoursPerDay);
        w.Text = double.IsNaN(p.TotalW) ? "—" : Watts(p.TotalW);
        kwh.Text = PowerCost.Kwh(p.KwhPerDay);
        rub.Text = r.HasTariff && !double.IsNaN(p.KwhPerDay) ? Money(r.Rub(p.KwhPerDay * 30)) : "—";
    }

    private static string Hours(double h) => h <= 0 || double.IsNaN(h) ? L.T("0 мин") : Fmt.Duration(TimeSpan.FromHours(h));

    /// <summary>«12th Gen Intel(R) Core(TM) i5-12400F» → «Intel Core i5-12400F».</summary>
    private static string ShortCpu(string name)
    {
        return HardwareInfo.ShortCpu(name, withVendor: true);
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
            ? L.T("Видеокарта сейчас не сообщает потребление, поэтому общий расход не посчитан.")
            : double.IsNaN(p.CpuLoad) ? "" : L.F("Процессор загружен на {0:0} %: {1} Вт × {0:0} % ≈ {2:0} Вт.", p.CpuLoad, report.Tdp, p.CpuW);
        NowNote.Visibility = NowNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================================================================== строки
    private sealed class PartRow
    {
        public PartRow(string name, string note, double w, string kind, Brush swatch) { Name = name; Note = note; W = w; Kind = kind; Swatch = swatch; }
        public string Name { get; }
        public string Note { get; }
        public double W { get; }
        public string Watts => double.IsNaN(W) ? "—" : L.F("{0:0} Вт", W);
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
        string label = L.F("≈ {0:0} Вт из розетки", double.IsNaN(total) ? sum : total);
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
