using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PulsePC.Services;

namespace PulsePC;

/// <summary>
/// Общее для графиков отчёта сессии, которые стоят под графиком кадров: то же поле (слева 34 под шкалу, справа 6),
/// тот же общий масштаб по времени (ChartZoom: колесо, перетаскивание, двойной щелчок).
/// </summary>
public abstract class SessionAxisElement : FrameworkElement
{
    protected static readonly Typeface AxisFace = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    protected static Pen MakePen(Brush b, double w, double[] dash = null)
    {
        var p = new Pen(b, w) { LineJoin = PenLineJoin.Round };
        if (dash != null) p.DashStyle = new DashStyle(dash, 0);
        p.Freeze();
        return p;
    }

    public ChartZoom Zoom
    {
        get => zoom;
        set
        {
            if (zoom == value) return;
            if (zoom != null) zoom.Changed -= OnZoom;
            zoom = value;
            if (zoom != null) zoom.Changed += OnZoom;
            InvalidateVisual();
        }
    }
    private ChartZoom zoom;
    private void OnZoom(bool _) => InvalidateVisual();

    protected (double s, double w) View() => (zoom?.Start ?? 0, Math.Max(1e-6, zoom?.Span ?? 1));

    protected virtual Rect Plot()
    {
        const double left = 34;
        return new Rect(left, 6, Math.Max(0, ActualWidth - left - 6), Math.Max(0, ActualHeight - 7));
    }

    /// <summary>Доля всего времени сессии (0..1) → x на экране.</summary>
    protected double X(double u, Rect r)
    {
        var (s, w) = View();
        return r.Left + (u - s) / w * r.Width;
    }

    protected double U(double x, Rect r)
    {
        var (s, w) = View();
        return r.Width > 0 ? s + (x - r.Left) / r.Width * w : 0;
    }

    private bool dragging;
    private double dragX;

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (zoom == null) { base.OnMouseWheel(e); return; }
        double factor = Math.Pow(1.25, e.Delta / 120.0);
        if (factor > 1 ? !zoom.CanZoomIn : !zoom.CanZoomOut) { base.OnMouseWheel(e); return; }
        var r = Plot();
        double at = r.Width > 0 ? Math.Clamp((e.GetPosition(this).X - r.Left) / r.Width, 0, 1) : 0.5;
        zoom.ZoomBy(factor, at);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (zoom == null) { base.OnMouseLeftButtonDown(e); return; }
        if (e.ClickCount == 2) { zoom.Reset(); e.Handled = true; return; }
        if (zoom.IsZoomed && CaptureMouse()) { dragging = true; dragX = e.GetPosition(this).X; e.Handled = true; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!dragging || zoom == null) return;
        double x = e.GetPosition(this).X, width = Plot().Width;
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
}

/// <summary>
/// График «время кадра»: обычный кадр (медиана, тушь), худший 1 % кадров (красный) по отрезкам сессии и отдельные рывки —
/// короткие черты охрой. Шкала подбирается по худшему 1 % видимого окна; рывки выше шкалы упираются в верх с засечкой.
/// </summary>
public sealed class FrameTimeChart : SessionAxisElement
{
    private static readonly Pen Minor = MakePen(Palette.GridMinor, 1);
    private static readonly Pen Major = MakePen(Palette.GridMajor, 1);
    private static readonly Pen Axis = MakePen(Palette.LineStrong, 1);
    private static readonly Pen MedianPen = MakePen(Palette.Ink1, 1.5);
    private static readonly Pen WorstPen = MakePen(Palette.Accent, 1.2);
    private static readonly Pen JoltPen = MakePen(Palette.Warn, 1.2);
    private static readonly double[] Tops = { 5.0, 10, 20, 40, 60, 100, 200, 500 };

    private double[] median = Array.Empty<double>(), worst = Array.Empty<double>();
    private (double U, double Ms)[] jolts = Array.Empty<(double, double)>();

    public void SetData(FrameTimeline f, double totalSec)
    {
        median = f?.Median ?? Array.Empty<double>();
        worst = f?.Worst ?? Array.Empty<double>();
        jolts = f?.Jolts?.Select(j => (Math.Clamp(j.T / Math.Max(1, totalSec), 0, 1), j.Ms)).ToArray() ?? Array.Empty<(double, double)>();
        InvalidateVisual();
    }

    private double U(int i, int n) => n <= 0 ? 0 : (i + 0.5) / n;

    /// <summary>Сверху — своя полоска под подпись шкалы («40 мс»): стрелки рывков у верхнего края поля её не закрывают.</summary>
    private const double TopStrip = 15;

    protected override Rect Plot()
    {
        var r = base.Plot();
        return new Rect(r.Left, r.Top + TopStrip - 6, r.Width, Math.Max(0, r.Height - (TopStrip - 6)));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var r = Plot();
        var (vs, vw) = View();
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        // Шкала — по худшему 1 % в видимом окне, с запасом.
        int n = worst.Length;
        double peak = 0;
        for (int i = 0; i < n; i++)
        {
            double u = U(i, n);
            if (u < vs - 0.01 || u > vs + vw + 0.01) continue;
            if (!double.IsNaN(worst[i])) peak = Math.Max(peak, worst[i]);
            if (i < median.Length && !double.IsNaN(median[i])) peak = Math.Max(peak, median[i] * 2.2);
        }
        double top = Tops.FirstOrDefault(t => peak <= t * 0.9);
        if (top == 0) top = 1000;

        for (int i = 1; i < 10; i++)
        {
            double y = Math.Round(r.Bottom - r.Height * i / 10) + 0.5;
            dc.DrawLine(i % 5 == 0 ? Major : Minor, new Point(r.Left, y), new Point(r.Right, y));
        }
        int level = vw < 0.999 ? Math.Clamp((int)Math.Round(Math.Log2(1 / vw)), 0, 16) : 0;
        double stepU = 1.0 / (20 * (double)(1 << level));
        long j0 = (long)Math.Ceiling(vs / stepU - 1e-6), j1 = (long)Math.Floor((vs + vw) / stepU + 1e-6);
        for (long j = j0; j <= j1; j++)
        {
            double xr = X(j * stepU, r);
            if (xr < r.Left + 1) continue;
            double x = Math.Round(xr) + 0.5;
            dc.DrawLine(j % 5 == 0 ? Major : Minor, new Point(x, r.Top), new Point(x, r.Bottom));
        }
        double ax = Math.Round(r.Left) + 0.5, ay = Math.Round(r.Bottom) + 0.5;
        dc.DrawLine(Axis, new Point(ax, r.Top), new Point(ax, ay));
        dc.DrawLine(Axis, new Point(ax, ay), new Point(r.Right, ay));
        FormattedText topText = null;
        foreach (double level01 in new[] { 0.0, 0.5, 1.0 })
        {
            double v = top * level01;
            string s = v.ToString("0.#", CultureInfo.CurrentCulture) + (level01 == 1.0 ? " " + L.Pick("мс", "ms") : "");
            var t = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, AxisFace, 10.5, Palette.Ink3, dpi);
            if (level01 == 1.0) topText = t;      // верхняя подпись — над полем графика, в своей полоске
            else dc.DrawText(t, new Point(r.Left - 5 - t.Width, Math.Clamp(r.Bottom - r.Height * level01 - t.Height / 2, -2, h - t.Height)));
        }

        dc.PushClip(new RectangleGeometry(new Rect(r.Left, 0, r.Width + 3, h)));
        double Y(double ms) => r.Bottom - r.Height * Math.Clamp(ms / top, 0, 1);
        void Line(double[] v, Pen pen)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                bool started = false;
                int m = v.Length;
                for (int i = 0; i < m; i++)
                {
                    double u = U(i, m);
                    if (double.IsNaN(v[i]) || u < vs - 0.02 || u > vs + vw + 0.02) { started = false; continue; }
                    var p = new Point(X(u, r), Y(v[i]));
                    if (!started) { ctx.BeginFigure(p, false, false); started = true; } else ctx.LineTo(p, true, true);
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
        }
        Line(worst, WorstPen);
        Line(median, MedianPen);
        foreach (var (u, ms) in jolts)
        {
            if (u < vs || u > vs + vw) continue;
            double x = Math.Round(X(u, r)) + 0.5, y = Y(ms);
            if (ms <= top) { dc.DrawLine(JoltPen, new Point(x, y), new Point(x, Math.Min(r.Bottom, y + 7))); continue; }
            // Выше шкалы — стрелка вверх у верхнего края: рывок был, но длиннее, чем помещается.
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(x, r.Top), true, true);
                c.LineTo(new Point(x + 3, r.Top + 5), true, false);
                c.LineTo(new Point(x - 3, r.Top + 5), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(Palette.Warn, null, g);
        }
        dc.Pop();
        if (topText != null)
        {
            // Над верхней линией сетки, у оси: на одной высоте с ней — засечка, сама подпись — в полоске над полем.
            double ty = Math.Max(0, r.Top - topText.Height - 1);
            dc.DrawLine(Axis, new Point(r.Left - 4, Math.Round(r.Top) + 0.5), new Point(r.Left, Math.Round(r.Top) + 0.5));
            dc.DrawText(topText, new Point(Math.Max(0, r.Left - 4), ty));
        }
    }
}

/// <summary>
/// Полоса отметок под графиком кадров: игра свёрнута или кадров не было (штрихованный отрезок), сброс драйвера видеокарты
/// (треугольник, кармин), скачок пинга (кружок), фоновая программа (ромб, охра). Подсказка — у ближайшей отметки под мышью.
/// </summary>
public sealed class EventStrip : SessionAxisElement
{
    public sealed class Item
    {
        public string Kind;
        public double From, To;
        public string Text;
    }

    private static readonly Pen Base = MakePen(Palette.LineStrong, 1);
    private static readonly Pen InkPen = MakePen(Palette.Ink1, 1.2);
    private static readonly Pen SpanPen = MakePen(Palette.Ink3, 1);
    private static readonly Pen WarnPen = MakePen(Palette.Warn, 1.2);
    private static readonly Brush Hatch = MakeHatch();

    private static Brush MakeHatch()
    {
        var ink = new SolidColorBrush(((SolidColorBrush)Palette.Ink3).Color) { Opacity = 0.55 };
        var b = new DrawingBrush(new GeometryDrawing(null, new Pen(ink, 1), new LineGeometry(new Point(0, 5), new Point(5, 0))))
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 5, 5), ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 5, 5), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None,
        };
        b.Freeze();
        return b;
    }

    private List<Item> items = new();

    private readonly ToolTip tip = new();

    public EventStrip()
    {
        ToolTipService.SetInitialShowDelay(this, 150);
        ToolTip = tip;
        // Подсказки нет, если мышь не у отметки.
        ToolTipOpening += (_, e) => { if (Hit(Mouse.GetPosition(this).X) is not { } hit) e.Handled = true; else tip.Content = hit.Text; };
    }

    public void SetItems(IEnumerable<Item> list) { items = list?.ToList() ?? new(); InvalidateVisual(); }

    protected override Rect Plot()
    {
        const double left = 34;
        return new Rect(left, 0, Math.Max(0, ActualWidth - left - 6), ActualHeight);
    }

    private Item Hit(double x)
    {
        var r = Plot();
        Item best = null; double bestD = 9;
        foreach (var it in items)
        {
            double x0 = X(it.From, r), x1 = X(it.To, r);
            double d = x >= x0 - 1 && x <= x1 + 1 ? 0 : Math.Min(Math.Abs(x - x0), Math.Abs(x - x1));
            if (d < bestD || (d == 0 && best != null && bestD == 0 && it.Kind != "minimized" && it.Kind != "pause")) { best = it; bestD = d; }
        }
        return best;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        // Мышь перешла к другой отметке — подсказка следует за ней; ушла от отметок — закрывается.
        var hit = Hit(e.GetPosition(this).X);
        if (hit != null) tip.Content = hit.Text;
        else if (tip.IsOpen) tip.IsOpen = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var r = Plot();
        var (vs, vw) = View();
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        double mid = Math.Round(h / 2) + 0.5;
        dc.DrawLine(Base, new Point(r.Left, mid), new Point(r.Right, mid));
        dc.PushClip(new RectangleGeometry(new Rect(r.Left - 6, 0, r.Width + 12, h)));
        foreach (var it in items.Where(i => i.Kind is "minimized" or "pause"))
        {
            if (it.To < vs || it.From > vs + vw) continue;
            double x0 = Math.Max(r.Left, X(it.From, r)), x1 = Math.Min(r.Right, Math.Max(X(it.To, r), X(it.From, r) + 3));
            var rect = new Rect(x0, mid - 6, Math.Max(3, x1 - x0), 12);
            dc.DrawRectangle(Hatch, it.Kind == "minimized" ? SpanPen : null, rect);
        }
        foreach (var it in items.Where(i => i.Kind is not ("minimized" or "pause")))
        {
            if (it.From < vs - 0.01 || it.From > vs + vw + 0.01) continue;
            double x = Math.Round(X(it.From, r)) + 0.5;
            switch (it.Kind)
            {
                case "driver":
                    var tri = new StreamGeometry();
                    using (var c = tri.Open())
                    {
                        c.BeginFigure(new Point(x, mid - 6), true, true);
                        c.LineTo(new Point(x + 6, mid + 5), true, false);
                        c.LineTo(new Point(x - 6, mid + 5), true, false);
                    }
                    tri.Freeze();
                    dc.DrawGeometry(Palette.Bad, null, tri);
                    break;
                case "ping":
                    dc.DrawEllipse(Palette.Base, InkPen, new Point(x, mid), 4, 4);
                    break;
                default:
                    var dia = new StreamGeometry();
                    using (var c = dia.Open())
                    {
                        c.BeginFigure(new Point(x, mid - 5), true, true);
                        c.LineTo(new Point(x + 5, mid), true, false);
                        c.LineTo(new Point(x, mid + 5), true, false);
                        c.LineTo(new Point(x - 5, mid), true, false);
                    }
                    dia.Freeze();
                    dc.DrawGeometry(Palette.Base, WarnPen, dia);
                    break;
            }
        }
        dc.Pop();
    }
}
