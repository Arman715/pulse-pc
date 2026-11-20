using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PulsePC.Services;

namespace PulsePC.Panels;

/// <summary>
/// Кривая вентиляторов на миллиметровке: по горизонтали температура видеокарты (25–95 °C), по вертикали скорость (0–100 %).
/// Точки тянутся мышью; с клавиатуры — Tab выбирает график, PageUp/PageDown — точку, стрелки двигают её.
/// Зона от 84 °C заштрихована: там вентиляторы всегда на полной скорости. Ниже предела карты (обычно 30 %) — бледная полоса.
/// Красная точка с перекрестием — где видеокарта сейчас.
/// </summary>
public sealed class FanCurveView : FrameworkElement
{
    private const double TMin = 25, TMax = 95;
    private const double PadL = 34, PadR = 10, PadT = 10, PadB = 22;
    private static readonly Typeface Face = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Pen Minor = Ink.Pen(Palette.GridMinor, 1), Major = Ink.Pen(Palette.GridMajor, 1), Axis = Ink.Pen(Palette.LineStrong, 1);
    private static readonly Pen CurvePen = Ink.Pen(Palette.Accent, 1.6), CurveOff = Ink.Pen(Palette.Ink4, 1.4), HandlePen = Ink.Pen(Palette.Accent, 1.4);
    private static readonly Pen NowPen = Ink.Pen(Palette.Ink2, 1, dashed: true), FocusPen = Ink.Pen(Palette.Ink1, 1, dashed: true);
    private static readonly Brush SafeBand = MakeHatch(), MinBand = Make(Palette.Track, 0.7);

    private FanPoint[] points = FanControl.Balanced;
    private int drag = -1, selected = -1;

    /// <summary>Пользователь изменил кривую (вызывается при отпускании точки и после каждой клавиши).</summary>
    public event Action<FanPoint[]> Edited;

    public FanCurveView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Arrow;
        SnapsToDevicePixels = true;
        IsEnabledChanged += (_, _) => InvalidateVisual();
        GotKeyboardFocus += (_, _) => { if (selected < 0) selected = 0; InvalidateVisual(); };
        LostKeyboardFocus += (_, _) => InvalidateVisual();
    }

    public FanPoint[] Points
    {
        get => points;
        set { points = FanControl.Normalize(value); InvalidateVisual(); }
    }

    /// <summary>Предел карты снизу, %: медленнее вентиляторы не крутятся.</summary>
    public int MinLevel { get; set; } = 30;

    private double nowT = double.NaN, nowL = double.NaN;
    /// <summary>Где видеокарта сейчас (NaN — не показывать).</summary>
    public void SetNow(double t, double l) { nowT = t; nowL = l; InvalidateVisual(); }

    protected override Size MeasureOverride(Size available) =>
        new(double.IsInfinity(available.Width) ? 600 : available.Width, 220);

    private Rect Plot => new(PadL, PadT, Math.Max(10, ActualWidth - PadL - PadR), Math.Max(10, ActualHeight - PadT - PadB));
    private double X(double t) { var p = Plot; return p.Left + (t - TMin) / (TMax - TMin) * p.Width; }
    private double Y(double l) { var p = Plot; return p.Bottom - l / 100 * p.Height; }

    protected override void OnRender(DrawingContext dc)
    {
        var p = Plot;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));      // ловить мышь по всей площади

        // Зоны: ниже предела карты и «всегда 100 %».
        dc.DrawRectangle(MinBand, null, new Rect(p.Left, Y(MinLevel), p.Width, p.Bottom - Y(MinLevel)));
        dc.DrawRectangle(SafeBand, null, new Rect(X(FanControl.SafeTemp), p.Top, p.Right - X(FanControl.SafeTemp), p.Height));

        // Миллиметровка: каждые 5 °C и 10 %, крупно — каждые 10 °C и 50 %.
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int t = 25; t <= 95; t += 5)
        {
            double x = Snap(X(t));
            dc.DrawLine(t % 10 == 0 ? Major : Minor, new Point(x, p.Top), new Point(x, p.Bottom));
            if (t % 10 == 0 && t < 95) Text(dc, t + "°", x, p.Bottom + 5, dpi, center: true);
        }
        for (int l = 0; l <= 100; l += 10)
        {
            double y = Snap(Y(l));
            dc.DrawLine(l % 50 == 0 ? Major : Minor, new Point(p.Left, y), new Point(p.Right, y));
            if (l % 50 == 0) Text(dc, l + "%", p.Left - 6, y - 7, dpi, right: true);
        }
        dc.DrawLine(Axis, new Point(p.Left, Snap(p.Bottom)), new Point(p.Right, Snap(p.Bottom)));
        dc.DrawLine(Axis, new Point(Snap(p.Left), p.Top), new Point(Snap(p.Left), p.Bottom));

        // Кривая: от левого края на уровне первой точки, через точки, дальше 100 % до правого края.
        var pen = IsEnabled ? CurvePen : CurveOff;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(p.Left, Y(points[0].L)), false, false);
            foreach (var pt in points) g.LineTo(new Point(X(pt.T), Y(pt.L)), true, true);
            g.LineTo(new Point(p.Right, Y(100)), true, true);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);

        for (int i = 0; i < points.Length; i++)
        {
            var c = new Point(X(points[i].T), Y(points[i].L));
            bool hot = i == drag || (IsKeyboardFocused && i == selected);
            double r = hot ? 5 : 4;
            dc.DrawRectangle(IsEnabled ? Palette.Raised : Palette.Base, IsEnabled ? HandlePen : CurveOff, new Rect(c.X - r, c.Y - r, 2 * r, 2 * r));
            if (hot) Text(dc, $"{points[i].T}° · {points[i].L}%", c.X, c.Y - 22, dpi, center: true, ink: Palette.Ink1);
        }

        // Где видеокарта сейчас.
        if (!double.IsNaN(nowT) && nowT >= TMin && nowT <= TMax)
        {
            double x = X(nowT);
            dc.DrawLine(NowPen, new Point(x, p.Top), new Point(x, p.Bottom));
            if (!double.IsNaN(nowL)) dc.DrawEllipse(Palette.Accent, null, new Point(x, Y(Math.Clamp(nowL, 0, 100))), 3.5, 3.5);
        }

        if (IsKeyboardFocused) dc.DrawRectangle(null, FocusPen, new Rect(0.5, 0.5, ActualWidth - 1, ActualHeight - 1));
    }

    private static double Snap(double v) => Math.Round(v) + 0.5;

    private static void Text(DrawingContext dc, string s, double x, double y, double dpi, bool center = false, bool right = false, Brush ink = null)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10, ink ?? Palette.Ink3, dpi);
        double left = center ? x - ft.Width / 2 : right ? x - ft.Width : x;
        dc.DrawText(ft, new Point(left, y));
    }

    // ------------------------------------------------------------------ мышь
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsEnabled) return;
        Focus();
        var m = e.GetPosition(this);
        int best = -1; double bestD = 12;
        for (int i = 0; i < points.Length; i++)
        {
            double d = (new Point(X(points[i].T), Y(points[i].L)) - m).Length;
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best < 0) return;
        drag = selected = best;
        CaptureMouse();
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var m = e.GetPosition(this);
        if (drag < 0)
        {
            bool near = IsEnabled && points.Any(pt => (new Point(X(pt.T), Y(pt.L)) - m).Length < 12);
            Cursor = near ? Cursors.SizeAll : Cursors.Arrow;
            return;
        }
        var p = Plot;
        int t = (int)Math.Round(TMin + (m.X - p.Left) / p.Width * (TMax - TMin));
        int l = (int)Math.Round((p.Bottom - m.Y) / p.Height * 100);
        Move(drag, t, l);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (drag < 0) return;
        drag = -1;
        ReleaseMouseCapture();
        InvalidateVisual();
        Edited?.Invoke(points);
    }

    /// <summary>Сдвинуть точку, не ломая порядок: температура между соседями, скорость не ниже левой и не выше правой. Последняя — всегда 100 %.</summary>
    private void Move(int i, int t, int l)
    {
        var a = points.ToArray();
        int lo = i > 0 ? a[i - 1].T + 1 : 25, hi = i < a.Length - 1 ? a[i + 1].T - 1 : 85;
        int lMin = i > 0 ? a[i - 1].L : 30, lMax = i < a.Length - 1 ? a[i + 1].L : 100;
        t = Math.Clamp(t, lo, Math.Max(lo, hi));
        l = i == a.Length - 1 ? 100 : Math.Clamp(l, Math.Max(30, lMin), lMax);
        a[i] = new FanPoint(t, l);
        points = a;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ клавиатура
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!IsEnabled || points.Length == 0) return;
        if (selected < 0) selected = 0;
        var pt = points[selected];
        switch (e.Key)
        {
            case Key.PageDown: selected = (selected + 1) % points.Length; InvalidateVisual(); break;
            case Key.PageUp: selected = (selected + points.Length - 1) % points.Length; InvalidateVisual(); break;
            case Key.Left: Move(selected, pt.T - 1, pt.L); Edited?.Invoke(points); break;
            case Key.Right: Move(selected, pt.T + 1, pt.L); Edited?.Invoke(points); break;
            case Key.Up: Move(selected, pt.T, pt.L + 2); Edited?.Invoke(points); break;
            case Key.Down: Move(selected, pt.T, pt.L - 2); Edited?.Invoke(points); break;
            default: return;
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------------ кисти
    private static Brush Make(Brush b, double opacity)
    {
        var c = new SolidColorBrush(((SolidColorBrush)b).Color) { Opacity = opacity };
        c.Freeze();
        return c;
    }

    private static Brush MakeHatch()
    {
        var ink = new SolidColorBrush(((SolidColorBrush)Palette.Accent).Color) { Opacity = 0.28 };
        var b = new DrawingBrush(new GeometryDrawing(null, new Pen(ink, 1), new LineGeometry(new Point(0, 6), new Point(6, 0))))
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 6, 6), ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 6, 6), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None,
        };
        b.Freeze();
        return b;
    }
}
