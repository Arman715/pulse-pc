using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace PulsePC;

/// <summary>
/// Чертёжные пометки: перекрестие, штриховка, сетка «плюсов», знак угла, линейка, штамп заголовка листа.
/// Всё рисуется тонкими линиями цвета туши; ни одна пометка не несёт данных — только ритм листа.
/// </summary>
public static class Ink
{
    public static Pen Pen(Brush brush, double width, bool dashed = false)
    {
        var p = new Pen(brush, width) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        if (dashed) p.DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0);
        p.Freeze();
        return p;
    }

    /// <summary>
    /// Разрядка заглавных (в WPF нет межбуквенного интервала): уровень 1 — волосяная шпация между буквами,
    /// уровень 2 — тонкая; между словами — полукруглые шпации.
    /// </summary>
    public static string Track(string text, int level = 1)
    {
        if (string.IsNullOrEmpty(text)) return text;
        string gap = level >= 2 ? " " : " ";
        string word = level >= 2 ? "  " : " ";
        var sb = new System.Text.StringBuilder(text.Length * 3);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == ' ') { sb.Append(word); continue; }
            sb.Append(c);
            if (i < text.Length - 1 && text[i + 1] != ' ') sb.Append(gap);
        }
        return sb.ToString();
    }
}

/// <summary>Текст с разрядкой: задаётся через Source, показывается с узкими пробелами между буквами.</summary>
public class TrackedText : TextBlock
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(string), typeof(TrackedText), new PropertyMetadata("", (d, _) => ((TrackedText)d).Update()));
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(int), typeof(TrackedText), new PropertyMetadata(1, (d, _) => ((TrackedText)d).Update()));

    public string Source { get => (string)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public int Spacing { get => (int)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    private void Update() => Text = Ink.Track(Source ?? "", Spacing);
}

/// <summary>
/// Маркер состояния 10×10: ошибка — сплошной кармин, предупреждение — охристая рамка со штриховкой,
/// сведения — кружок, в норме — галочка. Состояние никогда не передаётся одним цветом.
/// </summary>
public class StateMarker : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(Severity), typeof(StateMarker), new FrameworkPropertyMetadata(Severity.Info, FrameworkPropertyMetadataOptions.AffectsRender));
    public Severity Level { get => (Severity)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

    protected override Size MeasureOverride(Size s) => new(10, 10);

    protected override void OnRender(DrawingContext dc)
    {
        switch (Level)
        {
            case Severity.Bad:
                dc.DrawRectangle(Palette.Bad, null, new Rect(0, 0, 10, 10));
                break;
            case Severity.Warn:
                var pen = Ink.Pen(Palette.Warn, 1);
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, 10, 10)));
                for (double x = -10; x < 10; x += 3) dc.DrawLine(pen, new Point(x, 10), new Point(x + 10, 0));
                dc.Pop();
                dc.DrawRectangle(null, pen, new Rect(0.5, 0.5, 9, 9));
                break;
            case Severity.Info:
                dc.DrawEllipse(null, Ink.Pen(Palette.Ink2, 1), new Point(5, 5), 4, 4);
                break;
            default:
                var tick = new StreamGeometry();
                using (var c = tick.Open()) { c.BeginFigure(new Point(1, 5.5), false, false); c.LineTo(new Point(4, 8.5), true, false); c.LineTo(new Point(9.5, 1.5), true, false); }
                tick.Freeze();
                dc.DrawGeometry(null, Ink.Pen(Palette.Ink3, 1.3), tick);
                break;
        }
    }
}

/// <summary>
/// Размерная линия: выносные засечки по краям, линия с залитыми стрелками и число над ней.
/// Пока числа нет — линия пунктирная. Состояние участка (разброс, потери) окрашивает её охрой или кармином.
/// </summary>
public class DimensionLine : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(DimensionLine), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(Severity), typeof(DimensionLine), new FrameworkPropertyMetadata(Severity.Ok, FrameworkPropertyMetadataOptions.AffectsRender));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Severity Level { get => (Severity)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

    private static readonly Typeface Face = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    protected override Size MeasureOverride(Size s) => new(0, 34);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 30) return;
        Brush ink = Level == Severity.Bad ? Palette.Bad : Level == Severity.Warn ? Palette.Warn : Palette.Ink2;
        bool pending = string.IsNullOrEmpty(Text);
        double y = Math.Round(h - 10) + 0.5;
        var ext = Ink.Pen(Palette.Ink3, 1);
        dc.DrawLine(ext, new Point(0.5, y - 9), new Point(0.5, y + 5));
        dc.DrawLine(ext, new Point(w - 0.5, y - 9), new Point(w - 0.5, y + 5));
        var line = new Pen(ink, Level == Severity.Ok ? 0.8 : 1.5);
        if (pending) line.DashStyle = new DashStyle(new[] { 3.0, 3.0 }, 0);
        line.Freeze();
        dc.DrawLine(line, new Point(2, y), new Point(w - 2, y));
        void Arrow(double x, int dir)
        {
            var g = new StreamGeometry();
            using (var c = g.Open()) { c.BeginFigure(new Point(x, y), true, true); c.LineTo(new Point(x + 8 * dir, y - 3), true, false); c.LineTo(new Point(x + 8 * dir, y + 3), true, false); }
            g.Freeze();
            dc.DrawGeometry(ink, null, g);
        }
        Arrow(1, 1); Arrow(w - 1, -1);
        if (pending) return;
        var t = new FormattedText(Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 12,
            Level == Severity.Ok ? Palette.Ink1 : ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawRectangle(Palette.Base, null, new Rect((w - t.Width) / 2 - 4, y - t.Height - 3, t.Width + 8, t.Height));
        dc.DrawText(t, new Point((w - t.Width) / 2, y - t.Height - 3));
    }
}

/// <summary>Строительные линии — пунктирные заготовки строк, пока находок ещё нет.</summary>
public class ConstructionLines : FrameworkElement
{
    public int Lines { get; set; } = 6;
    protected override Size MeasureOverride(Size s) => new(0, Lines * 44);
    protected override void OnRender(DrawingContext dc)
    {
        double[] widths = { 1, 0.7, 0.85, 0.6, 0.9, 0.75 };
        var pen = new Pen(Palette.Line, 1) { DashStyle = new DashStyle(new[] { 4.0, 4.0 }, 0) };
        pen.Freeze();
        for (int i = 0; i < Lines; i++)
        {
            double y = 30 + i * 44 + 0.5;
            dc.DrawLine(pen, new Point(0, y), new Point(ActualWidth * widths[i % widths.Length], y));
        }
    }
}

/// <summary>Перекрестие-метка совмещения: окружность и крест, выходящий за неё.</summary>
public class Crosshair : FrameworkElement
{
    public Brush Stroke { get; set; } = Palette.Ink3;
    protected override Size MeasureOverride(Size s) => new(double.IsNaN(Width) ? 22 : Width, double.IsNaN(Height) ? 22 : Height);
    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, cx = Math.Round(w / 2) + 0.5, cy = Math.Round(h / 2) + 0.5, r = Math.Min(w, h) * 0.27;
        var pen = Ink.Pen(Stroke, 1);
        dc.DrawEllipse(null, pen, new Point(cx, cy), r, r);
        dc.DrawLine(pen, new Point(cx, 0), new Point(cx, h));
        dc.DrawLine(pen, new Point(0, cy), new Point(w, cy));
    }
}

/// <summary>Блок диагональной штриховки 45°.</summary>
public class Hatch : FrameworkElement
{
    public Brush Stroke { get; set; } = Palette.Ink3;
    public double Step { get; set; } = 6;
    protected override Size MeasureOverride(Size s) => new(double.IsNaN(Width) ? 90 : Width, double.IsNaN(Height) ? 28 : Height);
    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var pen = Ink.Pen(Stroke, 1);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
        for (double x = -h; x < w; x += Step) dc.DrawLine(pen, new Point(x, h), new Point(x + h, 0));
        dc.Pop();
    }
}

/// <summary>Сетка из «плюсов» — метки разбивки листа. Можно задать маску строк для «рваного» края, как на чертеже.</summary>
public class PlusGrid : FrameworkElement
{
    public Brush Stroke { get; set; } = Palette.Ink3;
    public int Rows { get; set; } = 5;
    public int Cols { get; set; } = 4;
    public double Step { get; set; } = 32;
    public string Mask { get; set; } = "";   // строки через «|», «x» — есть плюс, «.» — нет
    protected override Size MeasureOverride(Size s) => new((Cols - 1) * Step + 12, (Rows - 1) * Step + 12);
    protected override void OnRender(DrawingContext dc)
    {
        var pen = Ink.Pen(Stroke, 1);
        var rows = string.IsNullOrEmpty(Mask) ? null : Mask.Split('|');
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
            {
                if (rows != null && (r >= rows.Length || c >= rows[r].Length || rows[r][c] != 'x')) continue;
                double x = Math.Round(6 + c * Step) + 0.5, y = Math.Round(6 + r * Step) + 0.5;
                dc.DrawLine(pen, new Point(x - 4, y), new Point(x + 5, y));
                dc.DrawLine(pen, new Point(x, y - 4), new Point(x, y + 5));
            }
    }
}

/// <summary>Знак угла: две оси, дуга со стрелкой и подпись.</summary>
public class AngleMark : FrameworkElement
{
    public Brush Stroke { get; set; } = Palette.Ink2;
    private string text = "30°";
    public string Text { get => text; set { text = value; InvalidateVisual(); } }
    protected override Size MeasureOverride(Size s) => new(130, 90);
    protected override void OnRender(DrawingContext dc)
    {
        var pen = Ink.Pen(Stroke, 1);
        var o = new Point(12.5, 78.5);
        dc.DrawLine(pen, new Point(2, o.Y), new Point(124, o.Y));
        dc.DrawLine(pen, new Point(o.X, 70), new Point(o.X, 86));
        var tip = new Point(o.X + 70, o.Y - 70);
        dc.DrawLine(pen, new Point(o.X - 8, o.Y + 8), tip);
        dc.DrawLine(pen, tip, new Point(tip.X - 8, tip.Y + 2));
        dc.DrawLine(pen, tip, new Point(tip.X - 2, tip.Y + 8));
        var arc = new StreamGeometry();
        using (var c = arc.Open())
        {
            c.BeginFigure(new Point(o.X + 46, o.Y), false, false);
            c.ArcTo(new Point(o.X + 46 * Math.Cos(Math.PI / 4), o.Y - 46 * Math.Sin(Math.PI / 4)), new Size(46, 46), 0, false, SweepDirection.Counterclockwise, true, false);
        }
        arc.Freeze();
        dc.DrawGeometry(null, pen, arc);
        var t = new FormattedText(Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Small, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            13, Stroke, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(t, new Point(o.X + 50, o.Y - 46));
    }
}

/// <summary>Линейка: частые короткие штрихи, каждый пятый длиннее.</summary>
public class Ruler : FrameworkElement
{
    public Brush Stroke { get; set; } = Palette.Ink3;
    public double Step { get; set; } = 9;
    protected override Size MeasureOverride(Size s) => new(double.IsNaN(Width) ? 280 : Width, 18);
    protected override void OnRender(DrawingContext dc)
    {
        var pen = Ink.Pen(Stroke, 1);
        double h = ActualHeight;
        int i = 0;
        for (double x = 0.5; x < ActualWidth; x += Step, i++)
            dc.DrawLine(pen, new Point(Math.Round(x) + 0.5, i % 5 == 0 ? 0 : h * 0.45), new Point(Math.Round(x) + 0.5, h));
    }
}

/// <summary>
/// Штамп листа: крупное название, штриховка, вертикальная черта, две строки с разрядкой,
/// линия до края и перекрестие. При показе линия «прочерчивается» слева направо.
/// </summary>
public class SheetHeader : Grid
{
    private readonly TextBlock title = new();
    private readonly TrackedText line1 = new() { Spacing = 2 }, line2 = new() { Spacing = 2 };
    private readonly Rectangle rule = new() { Height = 1, VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true };
    private readonly Hatch hatch = new() { Width = 72, Height = 36, Step = 7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 4, 16, 0) };
    private readonly ScaleTransform ruleScale = new(1, 1);

    public string Title { get => title.Text; set => title.Text = value; }
    public string Line1 { get => line1.Source; set => line1.Source = value; }
    public string Line2 { get => line2.Source; set => line2.Source = value; }

    public SheetHeader()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        title.SetResourceReference(StyleProperty, "SheetTitle");
        Children.Add(title);

        SetColumn(hatch, 1);
        Children.Add(hatch);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        line1.SetResourceReference(StyleProperty, "Tracked");
        line2.SetResourceReference(StyleProperty, "Tracked");
        text.Children.Add(line1);
        text.Children.Add(line2);
        var block = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(16, 1, 18, 1), Child = text, VerticalAlignment = VerticalAlignment.Center, MinHeight = 36 };
        block.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
        SetColumn(block, 2);
        Children.Add(block);

        rule.SetResourceReference(Shape.FillProperty, "LineStrong");
        rule.RenderTransform = ruleScale;
        SetColumn(rule, 3);
        Children.Add(rule);

        var cross = new Crosshair { Width = 26, Height = 26, Stroke = Palette.Ink2, VerticalAlignment = VerticalAlignment.Center };
        SetColumn(cross, 4);
        Children.Add(cross);
    }

    /// <summary>Показ штампа: линия прочерчивается, текст проявляется.</summary>
    public void Play(string newTitle = null, string newLine1 = null, string newLine2 = null)
    {
        if (newTitle != null) Title = newTitle;
        if (newLine1 != null) Line1 = newLine1;
        if (newLine2 != null) Line2 = newLine2;
        if (!Motion.On) { ruleScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); ruleScale.ScaleX = 1; return; }
        var grow = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Motion.Data + 200)) { EasingFunction = Motion.Out, FillBehavior = FillBehavior.Stop };
        Timeline.SetDesiredFrameRate(grow, 60);
        ruleScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        Motion.Reveal(title, 0, 6);
        Motion.Reveal(line1, 60, 4);
        Motion.Reveal(line2, 110, 4);
    }
}
