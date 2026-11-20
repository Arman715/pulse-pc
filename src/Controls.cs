using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PulsePC;

/// <summary>
/// Показание прибора: число с единицей измерения и необязательной подписью слева, всё на одной базовой линии.
/// При заметном изменении число «доезжает» до нового значения; мелкие изменения и первое показание подставляются сразу.
/// </summary>
public class NumberText : TextBlock
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(NumberText), new PropertyMetadata(double.NaN, OnValueChanged));
    public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(
        nameof(Format), typeof(string), typeof(NumberText), new PropertyMetadata("0", (d, _) => ((NumberText)d).UpdateText()));
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(NumberText), new PropertyMetadata(1.0));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(NumberText), new PropertyMetadata("", (d, _) => ((NumberText)d).Rebuild()));
    public static readonly DependencyProperty PrefixProperty = DependencyProperty.Register(
        nameof(Prefix), typeof(string), typeof(NumberText), new PropertyMetadata("", (d, _) => ((NumberText)d).Rebuild()));
    public static readonly DependencyProperty UnitSizeProperty = DependencyProperty.Register(
        nameof(UnitSize), typeof(double), typeof(NumberText), new PropertyMetadata(14.0, (d, _) => ((NumberText)d).Rebuild()));
    private static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        "Shown", typeof(double), typeof(NumberText), new PropertyMetadata(double.NaN, (d, _) => ((NumberText)d).UpdateText()));

    private static readonly FontFamily Din = new("Bahnschrift");
    private Run numberRun, unitRun;

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Format { get => (string)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
    /// <summary>Шаг отображения (1 для целых, 0.1 для десятых): анимируем только изменение от трёх шагов.</summary>
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string Prefix { get => (string)GetValue(PrefixProperty); set => SetValue(PrefixProperty, value); }
    public double UnitSize { get => (double)GetValue(UnitSizeProperty); set => SetValue(UnitSizeProperty, value); }

    public NumberText()
    {
        Typography.SetNumeralAlignment(this, FontNumeralAlignment.Tabular);
        Rebuild();
    }

    private void Rebuild()
    {
        Inlines.Clear();
        if (!string.IsNullOrEmpty(Prefix))
            Inlines.Add(new Run(Prefix + "  ")
            {
                FontFamily = Din, FontWeight = FontWeights.SemiBold, FontStretch = FontStretches.Normal,
                FontSize = 11.5, Foreground = Palette.Ink3,
            });
        numberRun = new Run("—");
        Inlines.Add(numberRun);
        unitRun = null;
        if (!string.IsNullOrEmpty(Unit))
        {
            unitRun = new Run("\u202F" + Unit) { FontSize = UnitSize };
            Inlines.Add(unitRun);
        }
        UpdateText();
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var t = (NumberText)d;
        double to = (double)e.NewValue;
        double shown = (double)t.GetValue(ShownProperty);
        bool tween = Motion.On && t.IsVisible && !double.IsNaN(to) && !double.IsNaN(shown)
                     && Math.Abs(to - shown) >= 3 * t.Step;
        if (tween) Motion.To(t, ShownProperty, to, Motion.Data, Motion.Out, 30);
        else Motion.Snap(t, ShownProperty, to);
    }

    private void UpdateText()
    {
        if (numberRun == null) return;
        double v = (double)GetValue(ShownProperty);
        string s = double.IsNaN(v) ? "—" : v.ToString(Format, CultureInfo.CurrentCulture);
        if (numberRun.Text != s) numberRun.Text = s;
    }
}

/// <summary>
/// Масштаб по времени, общий для одного или нескольких графиков с одной осью времени.
/// Видимое окно — [Start, Start + Span] в долях всего диапазона: 0 — самое старое показание, 1 — самое новое.
/// </summary>
public sealed class ChartZoom
{
    private TimeSpan full = TimeSpan.FromSeconds(60);
    private int points = 41;

    public double Start { get; private set; }
    public double Span { get; private set; } = 1;

    /// <summary>Длительность всего диапазона — для подписи масштаба («60 с», «48 ч»).</summary>
    public TimeSpan Full { get => full; set { if (full == value) return; full = value; Changed?.Invoke(false); } }

    /// <summary>«Живой» график: окно всегда кончается на последнем показании, сдвигать его нельзя.</summary>
    public bool Live { get; set; }

    /// <summary>Кнопки «−»/«+» держат правый край, пока окно доходит до последнего показания (история: «сейчас»).</summary>
    public bool PreferEnd { get; set; }

    /// <summary>Точка, вокруг которой масштабируют кнопки: середина окна или, при PreferEnd, его правый край.</summary>
    public double KeyAnchor => Live || PreferEnd && Start + Span >= 0.999 ? 1 : 0.5;

    /// <summary>Сколько точек на всём диапазоне: уже шести точек окно не сужается.</summary>
    public int Points { get => points; set { if (points == value) return; points = Math.Max(2, value); Clamp(); } }

    public double MinSpan => Math.Clamp(6.0 / (points - 1), 0.002, 1);
    public bool IsZoomed => Span < 0.999;
    public bool CanZoomIn => Span > MinSpan * 1.02;
    public bool CanZoomOut => Span < 0.999;

    /// <summary>Окно изменилось; аргумент — анимировать ли переход.</summary>
    public event Action<bool> Changed;

    /// <summary>Крупнее (factor &gt; 1) или мельче вокруг точки at — доли видимого окна (0 — левый край, 1 — правый).</summary>
    public void ZoomBy(double factor, double at = 0.5, bool animate = true)
    {
        if (Live) at = 1;
        double span = Math.Clamp(Span / factor, MinSpan, 1);
        double anchor = Start + Math.Clamp(at, 0, 1) * Span;
        Set(anchor - at * span, span, animate);
    }

    /// <summary>Сдвинуть окно на du долей всего диапазона (перетаскивание).</summary>
    public void PanBy(double du)
    {
        if (Live || !IsZoomed) return;
        Set(Start + du, Span, false);
    }

    public void Reset(bool animate = true) => Set(0, 1, animate);

    public void Set(double start, double span, bool animate)
    {
        span = Math.Clamp(span, MinSpan, 1);
        start = Live ? 1 - span : Math.Clamp(start, 0, 1 - span);
        if (Math.Abs(start - Start) < 1e-9 && Math.Abs(span - Span) < 1e-9) return;
        Start = start; Span = span;
        Changed?.Invoke(animate);
    }

    private void Clamp()
    {
        if (Span >= MinSpan) return;
        Set(Start, MinSpan, false);
    }

    /// <summary>Видимое окно словами: «15 с», «2 мин 30 с», «2 ч 30 мин», «48 ч».</summary>
    public string Caption => FormatSpan(TimeSpan.FromTicks((long)(full.Ticks * Span)));

    public static string FormatSpan(TimeSpan t)
    {
        double s = t.TotalSeconds;
        if (s < 119.5) return $"{Math.Max(1, Math.Round(s)):0} с";
        if (s < 300)
        {
            int total = (int)(Math.Round(s / 5) * 5), m = total / 60, sec = total % 60;
            return sec == 0 ? $"{m} мин" : $"{m} мин {sec} с";
        }
        double min = Math.Round(t.TotalMinutes);
        if (min < 60) return $"{min:0} мин";
        if (min < 600)
        {
            int h = (int)(min / 60), m = (int)(min % 60);
            return m == 0 ? $"{h} ч" : $"{h} ч {m} мин";
        }
        return $"{Math.Round(t.TotalHours):0} ч";
    }
}

/// <summary>
/// График на миллиметровке: основная линия — тушь, вторая (видеокарта) — красная. Работает в двух режимах:
/// «живой» — показания добавляются через Push, и линия сдвигается влево на шаг;
/// «отчёт» — готовые ряды задаются через SetData, растягиваются на всю ширину и открываются слева направо.
/// С заданным Zoom ось времени масштабируется: колесо — крупнее/мельче у курсора, перетаскивание — сдвиг,
/// двойной щелчок — весь график. Линии строятся один раз в единицах данных, кадр анимации только меняет матрицу.
/// </summary>
public class LoadChart : FrameworkElement
{
    private readonly List<double> cpu = new(), gpu = new();
    private readonly TranslateTransform shift = new();
    private bool report;
    private static readonly Pen MinorPen = MakePen(Palette.GridMinor, 1);
    private static readonly Pen MajorPen = MakePen(Palette.GridMajor, 1);
    private static readonly Pen AxisPen = MakePen(Palette.LineStrong, 1);
    private static readonly Pen InkPen = MakePen(Palette.Ink1, 1.5);
    private static readonly Pen RedPen = MakePen(Palette.Accent, 1.5);
    private static readonly Pen InkThin = MakePen(Palette.Ink1, 1.2);
    private static readonly Pen RedThin = MakePen(Palette.Accent, 1.2);
    private static readonly Pen LossPen = MakePen(Palette.Bad, 1.5);
    private static readonly Pen WarnDash = MakeDash(Palette.Warn);
    private static readonly Brush WarnBand = MakeBand();
    private static readonly Typeface AxisFace = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly double[] NiceTops = { 10.0, 20, 50, 100, 200, 500, 1000, 2000 };
    private static readonly double[] Levels = { 0.0, 0.5, 1.0 };

    public static readonly DependencyProperty RevealProperty = DependencyProperty.Register(
        nameof(Reveal), typeof(double), typeof(LoadChart), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>Какая доля графика открыта (появление слева направо).</summary>
    public double Reveal { get => (double)GetValue(RevealProperty); set => SetValue(RevealProperty, value); }

    // Ход перехода между окнами масштаба 0..1 (анимируется через Motion).
    private static readonly DependencyProperty ZoomTProperty = DependencyProperty.Register(
        "ZoomT", typeof(double), typeof(LoadChart), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Сколько показаний помещается по ширине в «живом» режиме.</summary>
    public int Capacity { get; set; } = 41;     // 60 секунд при шаге 1,5 с
    /// <summary>Верх шкалы. При AutoScale подбирается по видимым данным.</summary>
    public double Max { get; set; } = 100;
    public bool AutoScale { get; set; }
    /// <summary>Единица шкалы, подписывается над верхней отметкой, например «мс».</summary>
    public string Unit { get; set; } = "";
    /// <summary>Основная линия красная (используется для мини-графика температуры видеокарты).</summary>
    public bool Accent { get; set; }
    /// <summary>Отмечать пропуски в основной линии (потерянные пакеты) кармином.</summary>
    public bool ShowGaps { get; set; }
    /// <summary>Мини-график без сетки и подписей (нижняя полоса главной страницы).</summary>
    public bool Bare { get; set; }
    /// <summary>Нижняя граница шкалы.</summary>
    public double Min { get; set; }
    /// <summary>Порог (например, 80 °C): пунктир охрой и бледная полоса выше него.</summary>
    public double Threshold { get; set; } = double.NaN;
    /// <summary>Вертикальных делений клетки (крупное — каждое пятое).</summary>
    public int Columns { get; set; } = 20;
    /// <summary>Штриховать участки без данных (история: программа не была запущена).</summary>
    public bool HatchGaps { get; set; }

    /// <summary>Масштаб по времени (может быть общим у нескольких графиков). Без него график не масштабируется.</summary>
    public ChartZoom Zoom
    {
        get => zoom;
        set
        {
            if (zoom == value) return;
            if (zoom != null) zoom.Changed -= OnZoomChanged;
            zoom = value;
            if (zoom != null) { zoom.Changed += OnZoomChanged; ReportPoints(); }
            SyncZoom(false);
        }
    }
    private ChartZoom zoom;
    private double fromS, fromW = 1, toS, toW = 1;

    private static readonly Brush GapHatch = MakeGapHatch();
    private static Brush MakeGapHatch()
    {
        var ink = new SolidColorBrush(((SolidColorBrush)Palette.Ink4).Color) { Opacity = 0.4 };
        var b = new DrawingBrush(new GeometryDrawing(null, new Pen(ink, 1), new LineGeometry(new Point(0, 6), new Point(6, 0))))
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 6, 6), ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 6, 6), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None,
        };
        b.Freeze();
        return b;
    }

    /// <summary>Задать готовые ряды целиком (режим отчёта). reveal — линия открывается слева направо.</summary>
    public void SetData(double[] main, double[] second = null, bool reveal = true)
    {
        report = true;
        cpu.Clear(); gpu.Clear();
        cpu.AddRange(main ?? Array.Empty<double>());
        if (second != null) gpu.AddRange(second);
        geoDirty = true;
        ReportPoints();
        shift.BeginAnimation(TranslateTransform.XProperty, null);
        if (reveal && Motion.On && IsVisible) { Motion.Snap(this, RevealProperty, 0); Motion.To(this, RevealProperty, 1, 500); }
        else Motion.Snap(this, RevealProperty, 1);
        InvalidateVisual();
    }

    /// <summary>Заменить содержимое «живого» графика без анимации (например, последние замеры пинга).</summary>
    public void SetLive(double[] main)
    {
        report = false;
        cpu.Clear(); gpu.Clear();
        cpu.AddRange(main ?? Array.Empty<double>());
        geoDirty = true;
        shift.BeginAnimation(TranslateTransform.XProperty, null);
        InvalidateVisual();
    }

    private void ReportPoints()
    {
        if (zoom == null) return;
        int n = report ? cpu.Count : Capacity;
        if (n >= 2) zoom.Points = n;
    }

    private static Pen MakePen(Brush brush, double width)
    {
        var p = new Pen(brush, width) { LineJoin = PenLineJoin.Round };
        p.Freeze();
        return p;
    }

    private static Pen MakeDash(Brush brush)
    {
        var p = new Pen(brush, 1) { DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0) };
        p.Freeze();
        return p;
    }

    private static Brush MakeBand()
    {
        var b = new SolidColorBrush(((SolidColorBrush)Palette.WarnTint).Color) { Opacity = 0.6 };
        b.Freeze();
        return b;
    }

    /// <summary>Добавить показание. Линия сдвигается влево на один шаг, а не перерисовывается рывком.</summary>
    public void Push(double cpuLoad, double gpuLoad, bool animate)
    {
        report = false;
        cpu.Add(cpuLoad); gpu.Add(gpuLoad);
        // Храним на одну точку больше: самая левая уходит за край, и сдвиг не даёт «прыжка».
        while (cpu.Count > Capacity + 1) { cpu.RemoveAt(0); gpu.RemoveAt(0); }
        geoDirty = true;
        InvalidateVisual();

        double step = PlotRect().Width / (Capacity - 1) / CurrentView().w;
        if (animate && Motion.On && cpu.Count > 2 && step > 0)
        {
            var slide = new DoubleAnimation(step, 0, TimeSpan.FromMilliseconds(Motion.Data)) { EasingFunction = Motion.Out, FillBehavior = FillBehavior.Stop };
            Timeline.SetDesiredFrameRate(slide, 30);
            shift.BeginAnimation(TranslateTransform.XProperty, slide);
        }
        else shift.BeginAnimation(TranslateTransform.XProperty, null);
    }

    // ------------------------------------------------------------------ масштаб
    private void OnZoomChanged(bool animate) => SyncZoom(animate);

    private void SyncZoom(bool animate)
    {
        double s = zoom?.Start ?? 0, w = zoom?.Span ?? 1;
        Cursor = zoom != null && zoom.IsZoomed && !zoom.Live ? Cursors.SizeWE : null;
        if (Math.Abs(s - toS) < 1e-9 && Math.Abs(w - toW) < 1e-9) { InvalidateVisual(); return; }
        (fromS, fromW) = CurrentView();
        toS = s; toW = w;
        Motion.Snap(this, ZoomTProperty, 0);
        if (animate && IsVisible) Motion.To(this, ZoomTProperty, 1, Motion.Normal, Motion.Out);
        else Motion.Snap(this, ZoomTProperty, 1);
    }

    /// <summary>
    /// Окно на текущем кадре перехода. Ширина меняется по геометрической прогрессии, а неподвижная точка
    /// перехода (та, что под курсором) остаётся на месте — масштаб «растёт» из неё, а не съезжает.
    /// </summary>
    private (double s, double w) CurrentView()
    {
        double t = (double)GetValue(ZoomTProperty);
        if (t >= 1) return (toS, toW);
        if (t <= 0) return (fromS, fromW);
        double w = fromW * Math.Pow(toW / fromW, t);
        if (Math.Abs(fromW - toW) < 1e-9) return (fromS + (toS - fromS) * t, w);
        double a = (toS * fromW - fromS * toW) / (fromW - toW);
        double p = (a - fromS) / fromW;
        return (a - p * w, w);
    }

    private bool dragging;
    private double dragX;

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (zoom == null) { base.OnMouseWheel(e); return; }
        double factor = Math.Pow(1.25, e.Delta / 120.0);
        // На краю масштаба колесо отдаётся странице: её можно прокручивать дальше.
        if (factor > 1 ? !zoom.CanZoomIn : !zoom.CanZoomOut) { base.OnMouseWheel(e); return; }
        var r = PlotRect();
        double at = r.Width > 0 ? Math.Clamp((e.GetPosition(this).X - r.Left) / r.Width, 0, 1) : 0.5;
        zoom.ZoomBy(factor, at);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (zoom == null) { base.OnMouseLeftButtonDown(e); return; }
        if (e.ClickCount == 2) { zoom.Reset(); e.Handled = true; return; }
        if (zoom.IsZoomed && !zoom.Live && CaptureMouse())
        {
            dragging = true;
            dragX = e.GetPosition(this).X;
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!dragging || zoom == null) return;
        double x = e.GetPosition(this).X, width = PlotRect().Width;
        if (width <= 0) return;
        zoom.PanBy(-(x - dragX) / width * zoom.Span);
        dragX = x;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (dragging) { dragging = false; ReleaseMouseCapture(); e.Handled = true; return; }
        base.OnMouseLeftButtonUp(e);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) { dragging = false; base.OnLostMouseCapture(e); }

    // ------------------------------------------------------------------ отрисовка
    // Поле графика: слева место под подписи шкалы, снизу ось.
    private Rect PlotRect()
    {
        double w = ActualWidth, h = ActualHeight;
        if (Bare) return new Rect(0, 1, Math.Max(0, w), Math.Max(0, h - 2));
        const double left = 34;
        return new Rect(left, 6, Math.Max(0, w - left - 6), Math.Max(0, h - 7));
    }

    // Положение показания i на оси времени в долях всего диапазона: u = ua·i + ub.
    private (double ua, double ub) TimeMap(int count)
    {
        if (report) return count > 1 ? (1.0 / (count - 1), 0) : (0, 0);
        double ua = 1.0 / Math.Max(1, Capacity - 1);
        return (ua, 1 - (count - 1) * ua);
    }

    private double XOf(int index, int count, Rect r, double vs, double vw)
    {
        var (ua, ub) = TimeMap(count);
        return r.Left + (ua * index + ub - vs) / vw * r.Width;
    }

    private double ScaleMax(double vs, double vw)
    {
        if (!AutoScale) return Max;
        // Шкала по видимому окну: при увеличении просадки и пики занимают всю высоту.
        double peak = 0;
        var (ua, ub) = TimeMap(cpu.Count);
        int i0 = 0, i1 = cpu.Count - 1;
        if (ua > 0)
        {
            i0 = Math.Max(0, (int)Math.Floor((vs - ub) / ua));
            i1 = Math.Min(cpu.Count - 1, (int)Math.Ceiling((vs + vw - ub) / ua));
        }
        for (int i = i0; i <= i1; i++) { double v = cpu[i]; if (!double.IsNaN(v) && v > peak) peak = v; }
        foreach (double nice in NiceTops)
            if (peak <= nice * 0.85) return nice;
        return 5000;
    }

    // Линии в единицах данных (x — номер показания, y — значение): строятся при смене данных, кадр только меняет матрицу.
    private readonly StreamGeometry[] geo = new StreamGeometry[2];
    private readonly MatrixTransform[] geoTf = { new(), new() };
    private readonly Point[] geoLast = new Point[2];
    private readonly bool[] geoAny = new bool[2];
    private bool geoDirty = true;
    private double geoMin = double.NaN, geoMax = double.NaN;
    private readonly RectangleGeometry clip = new();

    private void BuildGeometry(int k, List<double> values, double min, double max, bool clamp)
    {
        geoAny[k] = false;
        if (values.Count < 2 || max <= min) { geo[k] = null; return; }
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            bool started = false;
            for (int i = 0; i < values.Count; i++)
            {
                double v = values[i];
                if (double.IsNaN(v)) { started = false; continue; }
                if (clamp) v = Math.Clamp(v, min, max);
                var p = new Point(i, v);
                if (!started) { ctx.BeginFigure(p, false, false); started = true; }
                else ctx.LineTo(p, true, true);
                geoLast[k] = p; geoAny[k] = true;
            }
        }
        g.Transform = geoTf[k];
        geo[k] = g;
    }

    // Подписи шкалы — пока не сменились границы, используются готовые.
    private readonly FormattedText[] axisText = new FormattedText[3];
    private (double max, double min, string unit, double dpi) axisKey = (double.NaN, double.NaN, null, 0);

    private void EnsureAxisText(double max, double dpi)
    {
        if (axisKey == (max, Min, Unit, dpi) && axisText[0] != null) return;
        axisKey = (max, Min, Unit, dpi);
        for (int k = 0; k < Levels.Length; k++)
        {
            double v = Min + (max - Min) * Levels[k];
            string s = v.ToString(max - Min < 10 ? "0.#" : "0", CultureInfo.CurrentCulture);
            if (Levels[k] == 1.0 && Unit.Length > 0) s = s + " " + Unit;
            axisText[k] = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, AxisFace, 10.5, Palette.Ink3, dpi);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var r = PlotRect();
        var (vs, vw) = CurrentView();
        if (vw <= 0) vw = 1;
        double max = ScaleMax(vs, vw);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        int n = cpu.Count;

        // Прозрачная подложка ловит колесо и перетаскивание по всему полю.
        if (zoom != null) dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        if (!Bare)
        {
            // Миллиметровка: 10 мелких делений по высоте, крупное — каждое пятое; по ширине — Columns на всё окно.
            for (int i = 1; i < 10; i++)
            {
                double y = Math.Round(r.Bottom - r.Height * i / 10) + 0.5;
                dc.DrawLine(i % 5 == 0 ? MajorPen : MinorPen, new Point(r.Left, y), new Point(r.Right, y));
            }
            // По времени клетка привязана к данным: при увеличении она мельчает вдвое и едет вместе с линией.
            int level = vw < 0.999 ? Math.Clamp((int)Math.Round(Math.Log2(1 / vw)), 0, 16) : 0;
            double stepU = 1.0 / (Columns * (double)(1 << level));
            long j0 = (long)Math.Ceiling(vs / stepU - 1e-6), j1 = (long)Math.Floor((vs + vw) / stepU + 1e-6);
            for (long j = j0; j <= j1; j++)
            {
                double xr = r.Left + (j * stepU - vs) / vw * r.Width;
                if (xr < r.Left + 1) continue;
                double x = Math.Round(xr) + 0.5;
                dc.DrawLine(j % 5 == 0 ? MajorPen : MinorPen, new Point(x, r.Top), new Point(x, r.Bottom));
            }
            // Порог: бледная полоса выше него и пунктир.
            if (!double.IsNaN(Threshold) && Threshold > Min && Threshold < max)
            {
                double ty = Math.Round(r.Bottom - r.Height * (Threshold - Min) / (max - Min)) + 0.5;
                dc.DrawRectangle(WarnBand, null, new Rect(r.Left, r.Top, r.Width, Math.Max(0, ty - r.Top)));
                dc.DrawLine(WarnDash, new Point(r.Left, ty), new Point(r.Right, ty));
            }
            // Оси: только левая и нижняя.
            double ax = Math.Round(r.Left) + 0.5, ay = Math.Round(r.Bottom) + 0.5;
            dc.DrawLine(AxisPen, new Point(ax, r.Top), new Point(ax, ay));
            dc.DrawLine(AxisPen, new Point(ax, ay), new Point(r.Right, ay));
            // Подписи у крупных делений.
            EnsureAxisText(max, dpi);
            for (int k = 0; k < Levels.Length; k++)
            {
                var t = axisText[k];
                double y = r.Bottom - r.Height * Levels[k] - t.Height / 2;
                if (Levels[k] == 1.0 && Unit.Length > 0)
                    dc.DrawText(t, new Point(r.Left + 4, Math.Max(0, r.Top - 1)));
                else
                    dc.DrawText(t, new Point(r.Left - 5 - t.Width, Math.Clamp(y, -2, h - t.Height)));
            }
        }

        // Слева и справа линия обрезается по полю, если там продолжение графика за окном.
        double padL = vs > 1e-6 ? 0 : 3, padR = vs + vw < 1 - 1e-6 ? 0 : 3;

        // Участки без данных заштрихованы: видно, что программа не работала, а не что значение было нулём.
        if (HatchGaps && report && n > 1)
        {
            int i = 0;
            while (i < n)
            {
                if (!double.IsNaN(cpu[i])) { i++; continue; }
                int from = i;
                while (i < n && double.IsNaN(cpu[i])) i++;
                double x0 = from == 0 ? XOf(0, n, r, vs, vw) : XOf(from - 1, n, r, vs, vw);
                double x1 = i >= n ? XOf(n - 1, n, r, vs, vw) : XOf(i, n, r, vs, vw);
                x0 = Math.Max(x0, r.Left); x1 = Math.Min(x1, r.Right);
                if (x1 - x0 >= 2) dc.DrawRectangle(GapHatch, null, new Rect(x0, r.Top, x1 - x0, r.Height));
            }
        }

        // Линии: геометрия в единицах данных строится заново только при новых данных или новой шкале.
        bool clampMain = !AutoScale;
        if (geoDirty || (clampMain && (geoMin != Min || geoMax != max)))
        {
            BuildGeometry(0, cpu, Min, max, clampMain);
            BuildGeometry(1, gpu, 0, 100, true);
            geoMin = Min; geoMax = max; geoDirty = false;
        }
        var (ua, ub) = TimeMap(n);
        double kx = ua / vw * r.Width, bx = r.Left + (ub - vs) / vw * r.Width;
        SetMatrix(0, kx, bx, r, Min, max);
        SetMatrix(1, kx, bx, r, 0, 100);

        double reveal = report ? Math.Clamp(Reveal, 0, 1) : 1;
        double cl = r.Left - padL, cw = r.Width + padL + padR;
        var clipRect = new Rect(cl, 0, Math.Max(0, cw * reveal), h);
        if (clip.Rect != clipRect) clip.Rect = clipRect;
        dc.PushClip(clip);
        dc.PushTransform(shift);
        Pen second = Bare ? RedThin : RedPen;
        Pen main = Accent ? (Bare ? RedThin : RedPen) : (Bare ? InkThin : InkPen);
        DrawSeries(dc, 1, second);
        DrawSeries(dc, 0, main);
        if (ShowGaps) DrawGaps(dc, r, vs, vw);
        dc.Pop();
        dc.Pop();
        DrawMarks(dc, r, dpi, vs, vw);
    }

    private void SetMatrix(int k, double kx, double bx, Rect r, double min, double max)
    {
        if (max <= min) return;
        double ky = -r.Height / (max - min), by = r.Bottom + min * r.Height / (max - min);
        var m = new Matrix(kx, 0, 0, ky, bx, by);
        if (geoTf[k].Matrix != m) geoTf[k].Matrix = m;
    }

    private void DrawSeries(DrawingContext dc, int k, Pen pen)
    {
        if (geo[k] == null) return;
        dc.DrawGeometry(null, pen, geo[k]);
        // Точка на конце линии — где значение сейчас (только у полноразмерных «живых» графиков).
        if (geoAny[k] && !Bare && !report) dc.DrawEllipse(pen.Brush, null, geoTf[k].Matrix.Transform(geoLast[k]), 2.5, 2.5);
    }

    /// <summary>Отметки на оси времени (доли ширины 0..1), пронумерованные как позиции на чертеже — для просадок FPS.</summary>
    public double[] Marks { get => marks; set { marks = value; markText = null; InvalidateVisual(); } }
    private double[] marks;
    private FormattedText[] markText;
    private double markDpi;
    private static readonly Pen MarkPen = MakeMarkPen();
    private static Pen MakeMarkPen()
    {
        var p = new Pen(Palette.Accent, 1) { DashStyle = new DashStyle(new[] { 3.0, 3.0 }, 0) };
        p.Freeze();
        return p;
    }

    private void DrawMarks(DrawingContext dc, Rect r, double dpi, double vs, double vw)
    {
        if (marks == null || marks.Length == 0) return;
        if (markText == null || markText.Length != marks.Length || markDpi != dpi)
        {
            markText = new FormattedText[marks.Length];
            for (int i = 0; i < marks.Length; i++)
                markText[i] = new FormattedText((i + 1).ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, AxisFace, 10, Palette.Accent, dpi);
            markDpi = dpi;
        }
        for (int i = 0; i < marks.Length; i++)
        {
            double xr = r.Left + (Math.Clamp(marks[i], 0, 1) - vs) / vw * r.Width;
            if (xr < r.Left - 0.5 || xr > r.Right + 0.5) continue;
            double x = Math.Round(xr) + 0.5;
            dc.DrawLine(MarkPen, new Point(x, r.Top + 12), new Point(x, r.Bottom));
            var t = markText[i];
            dc.DrawText(t, new Point(x - t.Width / 2, r.Top - 1));
        }
    }

    // Потерянный пакет — вертикаль кармином на всю высоту и крестик сверху: потери видно сразу, даже одиночные.
    private void DrawGaps(DrawingContext dc, Rect r, double vs, double vw)
    {
        for (int i = 0; i < cpu.Count; i++)
            if (double.IsNaN(cpu[i]))
            {
                double x = Math.Round(XOf(i, cpu.Count, r, vs, vw)) + 0.5;
                dc.DrawLine(LossPen, new Point(x, r.Top + 6), new Point(x, r.Bottom));
                dc.DrawLine(LossPen, new Point(x - 2.5, r.Top), new Point(x + 2.5, r.Top + 5));
                dc.DrawLine(LossPen, new Point(x + 2.5, r.Top), new Point(x - 2.5, r.Top + 5));
            }
    }
}

/// <summary>
/// Подпись графика с масштабом: «НАГРУЗКА · 15 с» и справа кнопки «−», «+», «весь» тонкими линиями.
/// Подпись масштаба и доступность кнопок следуют за общим ChartZoom.
/// </summary>
public class ChartZoomBar : Grid
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    private readonly TrackedText title = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock scale = new()
    {
        FontFamily = Mono, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
        Foreground = Palette.Ink2, TextWrapping = TextWrapping.NoWrap,
    };
    private readonly ZoomKey minus = new("−", "Мельче — виден больший отрезок времени. То же делает колесо мыши над графиком"),
                             plus = new("+", "Крупнее — виден меньший отрезок. То же делает колесо мыши над графиком"),
                             all = new("весь", "Показать весь график. То же делает двойной щелчок по графику");
    private ChartZoom zoom;

    public ChartZoomBar()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var head = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(title);
        head.Children.Add(scale);
        Children.Add(head);
        var keys = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        plus.Margin = all.Margin = new Thickness(4, 0, 0, 0);
        keys.Children.Add(minus); keys.Children.Add(plus); keys.Children.Add(all);
        SetColumn(keys, 2);
        Children.Add(keys);
        minus.Click += (_, _) => zoom?.ZoomBy(0.5, zoom.KeyAnchor);
        plus.Click += (_, _) => zoom?.ZoomBy(2, zoom.KeyAnchor);
        all.Click += (_, _) => zoom?.Reset();
        Update();
    }

    /// <summary>Подпись графика (капсом, с разрядкой).</summary>
    public string Title { get => title.Source; set => title.Source = value; }
    public Style TitleStyle { get => title.Style; set => title.Style = value; }

    public ChartZoom Zoom
    {
        get => zoom;
        set
        {
            if (zoom != null) zoom.Changed -= OnChanged;
            zoom = value;
            if (zoom != null) zoom.Changed += OnChanged;
            Update();
        }
    }

    private void OnChanged(bool _) => Update();

    private void Update()
    {
        string s = zoom == null ? "" : "· " + zoom.Caption;
        if (scale.Text != s) scale.Text = s;
        minus.IsEnabled = zoom?.CanZoomOut == true;
        plus.IsEnabled = zoom?.CanZoomIn == true;
        all.IsEnabled = zoom?.IsZoomed == true;
    }
}

/// <summary>Кнопка масштаба: тонкая рамка, знак линиями или подпись мелким шрифтом, без заливки и скруглений.</summary>
public class ZoomKey : Button
{
    private static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Small, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private readonly string label;
    private FormattedText text;
    private Brush textBrush;

    static ZoomKey()
    {
        // Без шаблона темы: кнопка рисует себя сама.
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ZoomKey), new FrameworkPropertyMetadata(typeof(ZoomKey)));
    }

    public ZoomKey(string label, string tip)
    {
        this.label = label;
        Content = label == "−" ? "Мельче" : label == "+" ? "Крупнее" : "Весь график";
        ToolTip = tip;
        Cursor = Cursors.Hand;
        Focusable = true;
        SetResourceReference(FocusVisualStyleProperty, "FocusDash");
        IsEnabledChanged += (_, _) => InvalidateVisual();
    }

    protected override Size MeasureOverride(Size constraint) => new(label.Length > 1 ? 40 : 20, 18);
    protected override Size ArrangeOverride(Size arrangeBounds) => arrangeBounds;

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); InvalidateVisual(); }
    protected override void OnIsPressedChanged(DependencyPropertyChangedEventArgs e) { base.OnIsPressedChanged(e); InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        bool hot = IsEnabled && (IsMouseOver || IsPressed);
        Brush ink = !IsEnabled ? Palette.Ink4 : IsPressed ? Palette.Accent : hot ? Palette.Ink1 : Palette.Ink2;
        Brush frame = !IsEnabled ? Palette.Line : hot ? Palette.Ink1 : Palette.LineStrong;
        dc.DrawRectangle(Brushes.Transparent, new Pen(frame, 1), new Rect(0.5, 0.5, w - 1, h - 1));
        double cx = Math.Round(w / 2) + 0.5, cy = Math.Round(h / 2) + 0.5;
        var pen = new Pen(ink, 1);
        if (label == "−" || label == "+")
        {
            dc.DrawLine(pen, new Point(cx - 4, cy), new Point(cx + 4, cy));
            if (label == "+") dc.DrawLine(pen, new Point(cx, cy - 4), new Point(cx, cy + 4));
            return;
        }
        if (text == null || !ReferenceEquals(textBrush, ink))
        {
            text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11.5, ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            textBrush = ink;
        }
        dc.DrawText(text, new Point(Math.Round((w - text.Width) / 2), Math.Round((h - text.Height) / 2)));
    }
}
/// <summary>Базовый класс для полос: Target задаёт новое значение, Value плавно к нему приходит.</summary>
public abstract class Gauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(Gauge), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(double), typeof(Gauge), new PropertyMetadata(double.NaN, OnTargetChanged));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Target { get => (double)GetValue(TargetProperty); set => SetValue(TargetProperty, value); }

    /// <summary>Изменение меньше этой величины не анимируется.</summary>
    protected virtual double Threshold => 0.005;

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var g = (Gauge)d;
        double to = (double)e.NewValue, current = g.Value;
        if (double.IsNaN(current) || double.IsNaN(to) || !g.IsVisible || Math.Abs(to - current) < g.Threshold)
            Motion.Snap(g, ValueProperty, to);
        else
            Motion.To(g, ValueProperty, to, Motion.Data, Motion.Out, 30);
    }
}

/// <summary>Полоса из ячеек: одна ячейка — одна единица (например, гигабайт). Последняя заполняется частично.</summary>
public class CellBar : Gauge
{
    private const double Gap = 3;
    public int Cells { get; set; } = 16;
    /// <summary>Скольким ячейкам соответствует полная ширина: так 8 ячеек видеопамяти честно вдвое короче 16 ячеек памяти.</summary>
    public int LayoutCells { get; set; } = 16;
    public Brush Fill { get; set; } = Palette.Ink1;

    protected override double Threshold => 0.02;
    private static readonly Pen CellPen = FrozenPen(Palette.Ink3, 1);
    private static Pen FrozenPen(Brush b, double w) { var p = new Pen(b, w); p.Freeze(); return p; }
    protected override Size MeasureOverride(Size availableSize) => new(0, double.IsNaN(Height) ? 12 : Height);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double cellW = (w - Gap * (LayoutCells - 1)) / LayoutCells;
        if (cellW <= 0) return;
        double value = double.IsNaN(Value) ? 0 : Value;

        for (int i = 0; i < Cells; i++)
        {
            double x = Math.Round(i * (cellW + Gap));
            double cw = Math.Round(cellW);
            dc.DrawRectangle(null, CellPen, new Rect(x + 0.5, 0.5, cw - 1, h - 1));
            double part = Math.Clamp(value - i, 0, 1);
            if (part > 0) dc.DrawRectangle(Fill, null, new Rect(x, 0, Math.Max(1, Math.Round(cw * part)), h));
        }
    }
}

/// <summary>Тонкая полоса заполнения 0..1.</summary>
public class ThinBar : Gauge
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(ThinBar), new FrameworkPropertyMetadata(Palette.Ink1, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(0, double.IsNaN(Height) ? 4 : Height);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double mid = Math.Round(h / 2);
        dc.DrawRectangle(Palette.Line, null, new Rect(0, mid, w, 1));
        double v = double.IsNaN(Value) ? 0 : Math.Clamp(Value, 0, 1);
        if (v > 0) dc.DrawRectangle(Fill, null, new Rect(0, 0, Math.Max(1, Math.Round(w * v)), h));
    }
}

/// <summary>Шкала температуры с делениями и отметкой текущего значения; зона 80–90 °C выделена.</summary>
public class TickScale : Gauge
{
    public double Min { get; set; } = 30;
    public double Max { get; set; } = 90;
    public double WarnFrom { get; set; } = 80;

    private static readonly Typeface LabelFace = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Pen TickPen = MakePen(Palette.Ink3, 1);
    private static readonly Pen RedPen = MakePen(Palette.Accent, 1);

    private static Pen MakePen(Brush brush, double width) { var p = new Pen(brush, width); p.Freeze(); return p; }

    protected override double Threshold => 0.3;
    protected override Size MeasureOverride(Size availableSize) => new(0, double.IsNaN(Height) ? 40 : Height);

    // Линейка: засечки через 2 °C и 10 °C, зона перегрева заштрихована охрой, текущее значение — красный треугольник с чертой.
    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        if (w <= 20) return;
        const double pad = 8, top = 8;
        double X(double v) => pad + (w - 2 * pad) * (Math.Clamp(v, Min, Max) - Min) / (Max - Min);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var zone = new Rect(X(WarnFrom), top, X(Max) - X(WarnFrom), 6);
        dc.PushClip(new RectangleGeometry(zone));
        var hatch = MakePen(Palette.Warn, 1);
        for (double x = zone.Left - 6; x < zone.Right; x += 3) dc.DrawLine(hatch, new Point(x, zone.Bottom), new Point(x + 6, zone.Top));
        dc.Pop();

        for (double v = Min; v <= Max + 0.01; v += 2)
        {
            bool major = Math.Abs(v % 10) < 0.01;
            double x = Math.Round(X(v)) + 0.5;
            dc.DrawLine(TickPen, new Point(x, top), new Point(x, top + (major ? 12 : 6)));
            if (!major) continue;
            string s = v.ToString("0") + (v + 10 > Max ? " °C" : "");
            var label = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 10.5,
                v >= WarnFrom ? Palette.Warn : Palette.Ink3, dpi);
            double lx = x - (v + 10 > Max ? 10 : label.Width / 2);
            dc.DrawText(label, new Point(Math.Min(lx, w - label.Width), top + 15));
        }

        if (!double.IsNaN(Value))
        {
            double x = Math.Round(X(Value)) + 0.5;
            var tri = new StreamGeometry();
            using (var c = tri.Open())
            {
                c.BeginFigure(new Point(x - 4, 0), true, true);
                c.LineTo(new Point(x + 4, 0), true, false);
                c.LineTo(new Point(x, 6), true, false);
            }
            tri.Freeze();
            dc.DrawGeometry(Palette.Accent, null, tri);
            dc.DrawLine(RedPen, new Point(x, 6), new Point(x, top + 14));
        }
    }
}

/// <summary>
/// Полоса-сечение очистки: у каждой категории своя штриховка (как материалы на разрезе), без цветов.
/// Неотмеченные категории — бледная штриховка. Сегменты разделены тушью, вокруг рамка.
/// </summary>
public class SegmentBar : FrameworkElement
{
    private List<(double Weight, bool Selected)> segments = new();
    private static readonly Pen Frame = Freeze(new Pen(Palette.Ink1, 1));

    public void SetSegments(IEnumerable<(double Weight, bool Selected)> items)
    {
        segments = items.Where(s => s.Weight > 0).ToList();
        InvalidateVisual();
    }

    private static Pen Freeze(Pen p) { p.Freeze(); return p; }

    /// <summary>Образец штриховки категории с номером index (по убыванию размера) — им же рисуется легенда в строке.</summary>
    public static Brush Pattern(int index, bool selected)
    {
        Brush ink = selected ? Palette.Ink1 : Palette.Ink4;
        var pen = new Pen(ink, 1);
        var g = new GeometryGroup();
        double s;
        switch (index % 8)
        {
            case 0: s = 4; g.Children.Add(new LineGeometry(new Point(0, s), new Point(s, 0))); break;
            case 1: s = 4; g.Children.Add(new LineGeometry(new Point(0, 0), new Point(s, s))); break;
            case 2: s = 6; g.Children.Add(new LineGeometry(new Point(0, s), new Point(s, 0))); g.Children.Add(new LineGeometry(new Point(0, 0), new Point(s, s))); break;
            case 3: s = 3; g.Children.Add(new LineGeometry(new Point(0, 0.5), new Point(s, 0.5))); break;
            case 4: s = 3; g.Children.Add(new LineGeometry(new Point(0.5, 0), new Point(0.5, s))); break;
            case 5: s = 4; g.Children.Add(new RectangleGeometry(new Rect(1.5, 1.5, 1, 1))); break;
            case 6: s = 8; g.Children.Add(new LineGeometry(new Point(0, s), new Point(s, 0))); break;
            default:
                var solid = selected ? Palette.Ink2 : Palette.Ink4;
                return solid;
        }
        var drawing = new GeometryDrawing(index % 8 == 5 ? ink : null, pen, g);
        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, s, s), ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, s, s), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, double.IsNaN(Height) ? 14 : Height);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        if (segments.Count == 0)
        {
            dc.DrawRectangle(null, Frame, new Rect(0.5, 0.5, w - 1, h - 1));
            dc.DrawRectangle(Palette.Line, null, new Rect(1, Math.Round(h / 2), w - 2, 1));
            return;
        }
        double total = segments.Sum(s => s.Weight);
        double x = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            var (weight, selected) = segments[i];
            double sw = Math.Max(3, w * weight / total);
            dc.DrawRectangle(Pattern(i, selected), null, new Rect(Math.Round(x), 0, Math.Max(1, Math.Round(sw)), h));
            x += sw;
            if (i < segments.Count - 1 && x < w - 1) dc.DrawLine(Frame, new Point(Math.Round(x) + 0.5, 0), new Point(Math.Round(x) + 0.5, h));
        }
        dc.DrawRectangle(null, Frame, new Rect(0.5, 0.5, w - 1, h - 1));
    }
}