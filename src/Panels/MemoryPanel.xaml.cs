using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PulsePC.Services;

namespace PulsePC.Panels;

/// <summary>
/// Раздел «Память в игре»: из чего сейчас складывается память (занято, кэш ожидания, пусто), кто её занимает,
/// кнопка и авторежим «Освободить память» (служба MemoryBoost) и сколько памяти оставалось в записанных играх.
/// При открытии только читает; освобождает — по кнопке или по включённому авторежиму, с правами администратора.
/// </summary>
public partial class MemoryPanel : UserControl, ISystemPanel
{
    private const string Nb = " ";
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(2) };
    private PanelHost host;
    private int tickCount;
    private volatile bool reading;
    private bool loading;

    public MemoryPanel()
    {
        InitializeComponent();
        SwatchStandby.Fill = SegmentBar.Pattern(0, true);
        foreach (int gb in MemoryBoost.Thresholds)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Range"), GroupName = "MemThreshold", Content = $"{gb}{Nb}ГБ", Tag = gb };
            rb.Checked += Threshold_Checked;
            ThresholdChoice.Children.Add(rb);
        }
        tick.Tick += (_, _) => Refresh(++tickCount % 3 == 0);
        IsVisibleChanged += (_, _) => { if (IsVisible && host != null) { tick.Start(); Refresh(true); } else tick.Stop(); };
        MemoryBoost.Done += r => host?.Ui(() => ShowResult(r));
    }

    public void Attach(PanelHost h)
    {
        host = h;
        MemoryBoost.Init(h);        // обычно уже вызван при запуске; повторный вызов ничего не делает
        foreach (var c in new Control[] { TrimToggle, AutoToggle, ExcludeBox, BtnBoost })
            c.IsEnabled = h.Admin;
        foreach (RadioButton rb in ThresholdChoice.Children) rb.IsEnabled = h.Admin;
        AdminNote.Visibility = h.Admin ? Visibility.Collapsed : Visibility.Visible;
        LoadSaved();
        if (IsVisible) tick.Start();
    }

    public void Shown()
    {
        if (host == null) return;
        LoadSaved();
        Refresh(true);
        FillGames();
        if (IsVisible) tick.Start();
    }

    // ================================================================== настройки
    private void LoadSaved()
    {
        loading = true;
        var s = host.Settings;
        TrimToggle.IsChecked = s.GetBool(MemoryBoost.KTrim);
        AutoToggle.IsChecked = s.GetBool(MemoryBoost.KAuto);
        ExcludeBox.Text = s.Get(MemoryBoost.KExclude, MemoryBoost.DefaultExclude);
        ExcludeBlock.Visibility = TrimToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        int t = MemoryBoost.Threshold(s);
        foreach (RadioButton rb in ThresholdChoice.Children) rb.IsChecked = (int)rb.Tag == t;
        loading = false;

        // Последний итог (кнопкой или авторежимом) — если был недавно.
        if (DateTime.TryParse(s.Get(MemoryBoost.KLastAt), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            && (DateTime.Now - at).TotalDays < 2 && s.Get(MemoryBoost.KLast) is { Length: > 0 } last)
        {
            Result.Text = $"{When(at)}: {last}";
            Result.Visibility = Visibility.Visible;
        }

        // Сколько было доступно в момент запуска прошлой игры: по этому числу выбирают порог.
        if (double.TryParse(s.Get(MemoryBoost.KStartGb), NumberStyles.Float, CultureInfo.InvariantCulture, out double startGb)
            && DateTime.TryParse(s.Get(MemoryBoost.KStartAt), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var startAt))
        {
            string game = s.Get(MemoryBoost.KStartGame);
            StartNote.Text = $"При запуске прошлой игры{(game.Length > 0 ? $" ({game}, {When(startAt).ToLower(Ru)})" : "")} было доступно {MemoryBoost.Gb(startGb)}.";
            StartNote.Visibility = Visibility.Visible;
        }
        UpdateThresholdKey();
    }

    private static string When(DateTime t) =>
        t.Date == DateTime.Today ? $"Сегодня в {t:HH:mm}" : t.Date == DateTime.Today.AddDays(-1) ? $"Вчера в {t:HH:mm}" : t.ToString("d MMMM, HH:mm", Ru);

    private void Trim_Click(object sender, RoutedEventArgs e)
    {
        if (loading || host == null || !host.Admin) return;
        host.Settings.SetBool(MemoryBoost.KTrim, TrimToggle.IsChecked == true);
        host.Settings.Save();
        ExcludeBlock.Visibility = TrimToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Auto_Click(object sender, RoutedEventArgs e)
    {
        if (loading || host == null || !host.Admin) return;
        host.Settings.SetBool(MemoryBoost.KAuto, AutoToggle.IsChecked == true);
        host.Settings.Save();
        UpdateThresholdKey();
    }

    private void Threshold_Checked(object sender, RoutedEventArgs e)
    {
        if (loading || host == null || !host.Admin || sender is not RadioButton { Tag: int gb }) return;
        host.Settings.Set(MemoryBoost.KAutoGb, gb.ToString(CultureInfo.InvariantCulture));
        host.Settings.Save();
        UpdateThresholdKey();
    }

    private void Exclude_Changed(object sender, RoutedEventArgs e) => SaveExclude();
    private void Exclude_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) SaveExclude(); }

    private void SaveExclude()
    {
        if (loading || host == null || !host.Admin) return;
        var names = MemoryBoost.ParseExclude(ExcludeBox.Text);
        string text = string.Join(", ", names);
        host.Settings.Set(MemoryBoost.KExclude, text);
        host.Settings.Save();
        ExcludeBox.Text = text;
    }

    private void UpdateThresholdKey()
    {
        if (host == null) return;
        bool on = host.Settings.GetBool(MemoryBoost.KAuto);
        int t = MemoryBoost.Threshold(host.Settings);
        Bar.Threshold = on ? t : double.NaN;
        ThresholdKey.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ThresholdKeyText.Text = $"порог авторежима: доступно {t}{Nb}ГБ";
    }

    // ================================================================== сейчас
    private void Refresh(bool withProcesses)
    {
        if (host == null || reading) return;
        reading = true;
        Task.Run(() =>
        {
            try
            {
                var st = MemoryBoost.Read();
                var top = withProcesses ? MemoryBoost.Top(8) : null;
                host.Ui(() => { ShowState(st); if (top != null) ShowTop(top); });
            }
            finally { reading = false; }
        });
    }

    private void ShowState(MemState st)
    {
        if (!st.Ok) { State.Text = "Память не читается."; return; }
        double avail = st.AvailGb, total = st.TotalGb;
        State.Text = avail < 1.5 ? $"Памяти мало: программам доступно {MemoryBoost.Num(avail)} из {MemoryBoost.Gb(total)}."
                   : avail < 3 ? $"Памяти впритык: доступно {MemoryBoost.Num(avail)} из {MemoryBoost.Gb(total)}."
                   : $"Сейчас программам доступно {MemoryBoost.Num(avail)} из {MemoryBoost.Gb(total)}.";

        NowUsed.Value = st.UsedGb;
        NowAvail.Value = avail;
        NowStandby.Value = st.HasLists ? st.StandbyGb : double.NaN;
        NowFree.Value = st.HasLists ? st.FreeGb : double.NaN;
        NowAvail.Foreground = avail < 1.5 ? Palette.Warn : Palette.Ink1;
        TotalDim.Text = $"{MemoryBoost.Num(total)} ГБ";

        if (st.HasLists)
        {
            // Кэш и пусто вместе — это и есть «доступно»; если счётчики разошлись с Windows, верим Windows.
            double standby = Math.Clamp(st.StandbyGb, 0, avail), free = Math.Clamp(avail - standby, 0, avail);
            Bar.Set(total, st.UsedGb, standby, free);
            NowNote.Text = st.StandbyGb >= st.FreeGb
                ? $"Почти всё доступное сейчас — кэш ожидания ({MemoryBoost.Gb(st.StandbyGb)}): это файлы, которые недавно читались с диска. Windows держит их про запас и отдаёт программам сразу, как только память понадобится, — поэтому кэш считается доступной памятью."
                : $"Пустой памяти {MemoryBoost.Gb(st.FreeGb)}, кэша ожидания {MemoryBoost.Gb(st.StandbyGb)}. Кэш — файлы, которые недавно читались с диска: Windows отдаёт его программам по первому требованию.";
        }
        else
        {
            Bar.Set(total, st.UsedGb, 0, avail);
            NowNote.Text = "Счётчики Windows не отдали разбивку на кэш и пустую память — видно только, сколько доступно.";
        }
    }

    private sealed class ProcItem
    {
        public string Name { get; init; }
        public string Count { get; init; }
        public string Note { get; init; }
        public double Share { get; init; }
        public string Size { get; init; }
    }

    private void ShowTop(List<(string Name, int Count, long Bytes)> top)
    {
        long max = top.Count > 0 ? Math.Max(1, top[0].Bytes) : 1;
        ProcList.ItemsSource = top.Select(t => new ProcItem
        {
            Name = ProcessMemory.Pretty(t.Name),
            Count = t.Count > 1 ? "×" + t.Count : "",
            Note = t.Name.Equals("Memory Compression", StringComparison.OrdinalIgnoreCase) ? "сжатая память Windows"
                 : MemoryBoost.IsSystem(t.Name) ? "системное, не сжимается" : "",
            Share = (double)t.Bytes / max,
            Size = t.Bytes >= 1000L << 20 ? MemoryBoost.Gb(t.Bytes / Fmt.Gib) : Fmt.Size(t.Bytes).Replace(" ", Nb),
        }).ToList();
    }

    // ================================================================== действие
    private void Boost_Click(object sender, RoutedEventArgs e)
    {
        if (host == null || !host.Admin || MemoryBoost.Running) return;
        BtnBoost.IsEnabled = false;
        BtnBoost.Content = "Освобождаю…";
        host.BeginBusy();
        bool trim = TrimToggle.IsChecked == true;
        int pid = GameHooks.InGame ? GameHooks.Pid : 0;
        Task.Run(() =>
        {
            var r = MemoryBoost.Boost(trim, pid, "", false);
            MemoryBoost.Remember(host.Settings, r);
            host.Ui(() =>
            {
                host.EndBusy();
                BtnBoost.Content = "Освободить память";
                BtnBoost.IsEnabled = host.Admin;
                Refresh(true);
            });
        });
    }

    private void ShowResult(BoostResult r)
    {
        Result.Text = $"{When(r.At)}: {MemoryBoost.Describe(r)}";
        Result.Foreground = r.Ok ? Palette.Ink2 : Palette.Warn;
        Result.Visibility = Visibility.Visible;
    }

    // ================================================================== в играх
    private sealed class GameItem
    {
        public string Game { get; init; }
        public string Count { get; init; }
        public string Short { get; init; }
        public Visibility MarkVisible { get; init; }
        public string Worst { get; init; }
        public Brush WorstInk { get; init; }
    }

    private void FillGames()
    {
        var all = host.Sessions().Where(s => s.MinRamGb > 0).ToList();
        var sh = MemoryBoost.Shortage(all);
        GamesEmpty.Visibility = sh.Measured == 0 ? Visibility.Visible : Visibility.Collapsed;
        GamesBody.Visibility = sh.Measured == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (sh.Measured == 0) return;

        ShortCount.Text = $"{sh.Short} из {sh.Measured}";
        ShortCount.Foreground = sh.Short > 0 && sh.Short * 5 >= sh.Measured ? Palette.Warn : Palette.Ink1;
        WorstMin.Value = sh.Worst;
        WorstMin.Foreground = sh.Worst < MemoryBoost.ShortGb ? Palette.Warn : Palette.Ink1;
        AvgMin.Value = sh.AvgMin;

        var chrono = all.OrderBy(s => s.Start).TakeLast(MinRamStrip.MaxBars).ToList();
        Strip.SetData(chrono.Select(s => s.MinRamGb).ToArray(), MemoryBoost.ShortGb);
        StripFrom.Text = chrono.Count > 0 ? chrono[0].Start.ToString("d MMMM", Ru) : "";
        StripTo.Text = chrono.Count > 0 ? chrono[^1].Start.ToString("d MMMM", Ru) + (all.Count > chrono.Count ? $" · последние {chrono.Count} из {all.Count}" : "") : "";

        GameList.ItemsSource = all.GroupBy(s => s.Game)
            .Select(g =>
            {
                int n = g.Count(), bad = g.Count(s => s.MinRamGb < MemoryBoost.ShortGb);
                double worst = g.Min(s => s.MinRamGb);
                return new GameItem
                {
                    Game = g.Key,
                    Count = Fmt.Plural(n, "сессия", "сессии", "сессий") + " · " + Fmt.Duration(TimeSpan.FromSeconds(g.Sum(s => s.Duration.TotalSeconds))),
                    Short = $"{bad} из {n}",
                    MarkVisible = bad > 0 ? Visibility.Visible : Visibility.Collapsed,
                    // 0,95 не округляется до «1,0»: иначе число выглядит выше порога, хотя ниже.
                    Worst = worst < MemoryBoost.ShortGb ? worst.ToString("0.00", Ru) + Nb + "ГБ" : MemoryBoost.Gb(worst),
                    WorstInk = worst < MemoryBoost.ShortGb ? Palette.Warn : Palette.Ink1,
                };
            })
            .OrderByDescending(x => x.MarkVisible == Visibility.Visible).ThenBy(x => x.Game)
            .ToList();
        UpgradeLink.Visibility = sh.Short > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpgradeLink_Click(object sender, RoutedEventArgs e) => host?.Open("sys:Совет по апгрейду");
}

/// <summary>
/// Полоса-сечение памяти: занято — тушь, кэш ожидания — штриховка 45°, пусто — бумага. Рамка 1 тушью.
/// Порог авторежима (если включён) — охристая пунктирная вертикаль: левее неё доступной памяти меньше порога.
/// </summary>
public sealed class MemoryBar : FrameworkElement
{
    private double total, used, standby, free;
    private static readonly Pen Frame = Ink.Pen(Palette.Ink1, 1);
    private static readonly Brush Hatch = SegmentBar.Pattern(0, true);
    private double threshold = double.NaN;

    public double Threshold { get => threshold; set { threshold = value; InvalidateVisual(); } }

    public void Set(double totalGb, double usedGb, double standbyGb, double freeGb)
    {
        total = totalGb; used = Math.Max(0, usedGb); standby = Math.Max(0, standbyGb); free = Math.Max(0, freeGb);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size s) => new(0, double.IsNaN(Height) ? 16 : Height);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 2 || h <= 2) return;
        if (total > 0)
        {
            double sum = Math.Max(total, used + standby + free);
            double xUsed = Math.Round(w * used / sum), xStandby = Math.Round(w * (used + standby) / sum);
            if (xUsed > 0) dc.DrawRectangle(Palette.Ink1, null, new Rect(0, 0, xUsed, h));
            if (xStandby > xUsed)
            {
                dc.DrawRectangle(Hatch, null, new Rect(xUsed, 0, xStandby - xUsed, h));
                dc.DrawRectangle(Palette.Ink1, null, new Rect(xStandby, 0, 1, h));
            }
            if (!double.IsNaN(threshold) && threshold < total)
            {
                double x = Math.Round(w * (total - threshold) / sum) + 0.5;
                var pen = new Pen(Palette.Warn, 1.5) { DashStyle = new DashStyle(new[] { 2.0, 1.5 }, 0) };
                pen.Freeze();
                dc.DrawLine(pen, new Point(x, -5), new Point(x, h + 5));
            }
        }
        dc.DrawRectangle(null, Frame, new Rect(0.5, 0.5, w - 1, h - 1));
    }
}

/// <summary>
/// Столбики «сколько памяти оставалось доступно» по сессиям, старые слева. Порог (1 ГБ) — охристая пунктирная линия
/// с полосой WarnTint под ней; столбики ниже порога — штриховка охрой (состояние не передаётся одним цветом).
/// </summary>
public sealed class MinRamStrip : FrameworkElement
{
    public const int MaxBars = 40;
    private double[] values = Array.Empty<double>();
    private double limit = 1;
    private static readonly Typeface Axis = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public void SetData(double[] v, double threshold)
    {
        values = v ?? Array.Empty<double>();
        limit = threshold;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size s) => new(0, double.IsNaN(Height) ? 110 : Height);

    private static Brush warnHatch;
    private static Brush WarnHatch
    {
        get
        {
            if (warnHatch != null) return warnHatch;
            var g = new GeometryGroup();
            g.Children.Add(new LineGeometry(new Point(0, 4), new Point(4, 0)));
            var b = new DrawingBrush(new GeometryDrawing(null, new Pen(Palette.Warn, 1), g))
            {
                TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 4, 4), ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, 4, 4), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None,
            };
            b.Freeze();
            return warnHatch = b;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 40 || h <= 20) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        const double left = 34, top = 6, bottom = 1;
        double plotW = w - left, plotH = h - top - bottom;
        double max = Math.Max(4, Math.Ceiling(values.DefaultIfEmpty(0).Max()));
        double Y(double gb) => Math.Round(top + plotH * (1 - Math.Clamp(gb, 0, max) / max));

        // Сетка по гигабайтам (через 1 или 2 ГБ) и подписи слева.
        int step = max > 8 ? 2 : 1;
        var grid = Ink.Pen(Palette.GridMinor, 1);
        for (int g = 0; g <= max; g += step)
        {
            double y = Y(g) + 0.5;
            if (g > 0) dc.DrawLine(grid, new Point(left, y), new Point(w, y));
            var t = new FormattedText(g.ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Axis, 10.5, Palette.Ink3, dpi);
            dc.DrawText(t, new Point(left - 8 - t.Width, y - t.Height / 2));
        }
        // Порог: полоса под ним и пунктир.
        double yl = Y(limit);
        dc.DrawRectangle(Palette.WarnTint, null, new Rect(left, yl, plotW, Y(0) - yl));
        var dash = new Pen(Palette.Warn, 1) { DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0) };
        dash.Freeze();
        dc.DrawLine(dash, new Point(left, yl + 0.5), new Point(w, yl + 0.5));

        // Столбики.
        int n = values.Length;
        if (n > 0)
        {
            double slot = plotW / Math.Max(n, 12);
            double bw = Math.Max(3, Math.Min(18, Math.Round(slot * 0.55)));
            double x0 = w - n * slot;     // прижаты вправо: справа — последние
            var warnPen = Ink.Pen(Palette.Warn, 1);
            for (int i = 0; i < n; i++)
            {
                double x = Math.Round(x0 + i * slot + (slot - bw) / 2);
                double y = Y(values[i]), yb = Y(0);
                if (yb - y < 1) y = yb - 1;
                var r = new Rect(x, y, bw, yb - y);
                if (values[i] < limit)
                {
                    dc.DrawRectangle(WarnHatch, null, r);
                    dc.DrawRectangle(null, warnPen, new Rect(x + 0.5, y + 0.5, bw - 1, Math.Max(0, yb - y - 1)));
                }
                else dc.DrawRectangle(Palette.Ink1, null, r);
            }
        }
        // Ось.
        dc.DrawRectangle(Palette.LineStrong, null, new Rect(left, Y(0), plotW, 1));
    }
}
