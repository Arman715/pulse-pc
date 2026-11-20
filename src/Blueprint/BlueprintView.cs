using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PulsePC.Blueprint;

public enum Scene { Pc, Gpu, Cpu, Ram, Storage, Router, Gamepad }
public enum Part { None, Cpu, Gpu, Ram, Storage, Motherboard, Psu }

/// <summary>
/// Технический рисунок (тушь на бумаге) сцены: общий вид системного блока или разнесённый вид детали.
/// Геометрия строится один раз на сцену и кэшируется в экранных координатах для каждого размера;
/// прорисовка (Progress), разнесение (Explode) и подсветка только перерисовывают готовые контуры.
/// </summary>
public sealed class BlueprintView : FrameworkElement
{
    // ───────────────────────────── свойства ─────────────────────────────

    public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(nameof(Scene), typeof(Scene), typeof(BlueprintView),
        new FrameworkPropertyMetadata(Scene.Pc, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((BlueprintView)d).OnSceneChanged()));
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(BlueprintView),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ExplodeProperty = DependencyProperty.Register(nameof(Explode), typeof(double), typeof(BlueprintView),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(nameof(Highlight), typeof(Part), typeof(BlueprintView),
        new FrameworkPropertyMetadata(Part.None, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((BlueprintView)d).OnHighlightChanged((Part)e.OldValue)));

    // внутренние «часы» анимаций
    private static readonly DependencyProperty HiMixProperty = DependencyProperty.Register("HiMix", typeof(double), typeof(BlueprintView),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty ClockProperty = DependencyProperty.Register("Clock", typeof(double), typeof(BlueprintView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty FadeProperty = DependencyProperty.Register("Fade", typeof(double), typeof(BlueprintView),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty CalloutOpacityProperty = DependencyProperty.Register("CalloutOpacity", typeof(double), typeof(BlueprintView),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public Scene Scene { get => (Scene)GetValue(SceneProperty); set => SetValue(SceneProperty, value); }
    /// <summary>0..1 — прорисовка: линии растут по длине, детали по очереди, грани проявляются.</summary>
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    /// <summary>0 — собрано, 1 — разнесено (сцены деталей). Линии проекции растут вместе с разнесением.</summary>
    public double Explode { get => (double)GetValue(ExplodeProperty); set => SetValue(ExplodeProperty, value); }
    /// <summary>Подсвеченная деталь (сцена Pc): красный контур 2,0 и заливка RedTint 70 %. Смена плавная (140/200 мс).</summary>
    public Part Highlight { get => (Part)GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }

    /// <summary>Наведение и клик по деталям (сцена Pc). Курсор — рука над деталью или её выноской.</summary>
    public bool Interactive { get => interactive; set { interactive = value; InvalidateVisual(); } }
    private bool interactive;

    /// <summary>Сцена Pc: все детали, кроме Highlight, рисуются «призраками» (0,8 Ink4, штрих 4 3, без заливки).</summary>
    public bool GhostOthers { get => ghostOthers; set { if (ghostOthers == value) return; ghostOthers = value; staticCache = null; InvalidateVisual(); } }
    private bool ghostOthers;

    /// <summary>Поля вокруг рисунка (место под подписи). По умолчанию: Pc — 130 слева и 215 справа, детали — 13 % ширины с каждой стороны.</summary>
    public Thickness? DrawingPadding { get => padding; set { padding = value; layout = null; InvalidateVisual(); } }
    private Thickness? padding;

    public event Action<Part> PartHovered;
    public event Action<Part> PartClicked;

    /// <summary>Настоящий угол проекции сцены (наклон оси глубины к горизонтали на экране), градусы — для угловой метки.</summary>
    public double CameraAngle => EnsureModel().Angle;

    /// <summary>Идентификаторы точек привязки текущей сцены.</summary>
    public IReadOnlyList<string> Anchors => EnsureModel().Anchors.Select(a => a.Id).ToList();

    /// <summary>Сцены, которые уже прорисовывались в этом сеансе: повторный Play() только проявляет рисунок.</summary>
    private static readonly HashSet<Scene> drawnOnce = new();

    public BlueprintView()
    {
        SnapsToDevicePixels = false;
        RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);
        Focusable = false;
        // Надписи на деталях пришли из паспорта компьютера — перестроить сцену. Подписка — только пока вид в окне:
        // статическое событие держало бы закрытое окно осмотра со всеми его элементами до выхода из программы.
        Loaded += (_, _) =>
        {
            BlueprintLabels.Changed -= OnLabelsChanged;
            BlueprintLabels.Changed += OnLabelsChanged;
            if (labelsSeen != BlueprintLabels.Version) ResetLabels();     // надписи сменились, пока вид был вне окна
        };
        Unloaded += (_, _) => BlueprintLabels.Changed -= OnLabelsChanged;
    }

    private int labelsSeen = BlueprintLabels.Version;

    private void OnLabelsChanged() => Dispatcher.BeginInvoke(new Action(ResetLabels));

    private void ResetLabels()
    {
        labelsSeen = BlueprintLabels.Version;
        model = null; layout = null; staticCache = null;
        InvalidateVisual();
    }

    // ───────────────────────────── модель и раскладка ─────────────────────────────

    private Model model;
    private Scene modelScene;
    private Layout layout;
    private DrawingGroup staticCache;
    private (double e, Part hi, double mix, bool ghost, int ver) staticKey;

    private Model EnsureModel()
    {
        if (model == null || modelScene != Scene) { model = Scenes.Get(Scene); modelScene = Scene; layout = null; staticCache = null; }
        return model;
    }

    private void OnSceneChanged()
    {
        StopAll();
        callouts.Clear();
        dimText = null;
        model = null; layout = null; staticCache = null;
        hovered = Part.None;
        Fade = 1; SetValue(CalloutOpacityProperty, 1.0);
    }

    private double Fade { get => (double)GetValue(FadeProperty); set => SetValue(FadeProperty, value); }

    private sealed class Run
    {
        public byte Kind;              // 0 заливка, 1 линии, 2 трубка, 3 надпись
        public Shade Shade; public Ink Ink; public bool Fine;
        public Geometry G;
        public List<Point[]> Polys; public List<bool> Closed;
        public Pen TubeOuter, TubeInner, TubeRed;
    }

    private sealed class ItemCache
    {
        public Item It; public Run[] Runs; public Rect Bounds;
        public Point A, B;   // линии проекции
    }

    private sealed class Layout
    {
        public Size Size; public int Version; public double S; public Vector O; public Thickness Pad;
        public ItemCache[] Items; public Vector[] Layers;
        public double[] RowMin, RowMax; public const double RowH = 2;
        public Rect Bounds0, Bounds1;
        public bool Small;   // мелкий рисунок (до ~220 px): контуры тоньше, гравировки нет
        public Point Map(Point p) => new(p.X * S + O.X, p.Y * S + O.Y);
    }

    private Layout EnsureLayout()
    {
        var m = EnsureModel();
        var size = new Size(ActualWidth, ActualHeight);
        if (layout != null && layout.Size == size && layout.Version == BlueprintPalette.Version) return layout;
        layout = BuildLayout(m, size);
        staticCache = null;
        calloutsDirty = true;
        return layout;
    }

    private Thickness PadFor(Size size)
    {
        if (padding.HasValue) return padding.Value;
        // Pc: слева место под подписи (x = 0), справа — колонка «край корпуса + 40» шириной ~170.
        if (Scene == Scene.Pc) return new Thickness(130, 12, 215, 12);
        double side = Math.Clamp(size.Width * 0.13, 70, 170);
        return new Thickness(side, 16, side, 16);
    }

    private Layout BuildLayout(Model m, Size size)
    {
        var L = new Layout { Size = size, Version = BlueprintPalette.Version, Pad = PadFor(size) };
        // Границы в единицах модели: собранный и разнесённый вид вместе, чтобы рисунок не прыгал.
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        double ax0 = x0, ay0 = y0, ax1 = x1, ay1 = y1;
        void Acc(Point p, Vector off)
        {
            if (p.X < ax0) ax0 = p.X; if (p.X > ax1) ax1 = p.X; if (p.Y < ay0) ay0 = p.Y; if (p.Y > ay1) ay1 = p.Y;
            var q = p + off;
            double xa = Math.Min(p.X, q.X), xb = Math.Max(p.X, q.X), ya = Math.Min(p.Y, q.Y), yb = Math.Max(p.Y, q.Y);
            if (xa < x0) x0 = xa; if (xb > x1) x1 = xb; if (ya < y0) y0 = ya; if (yb > y1) y1 = yb;
        }
        foreach (var it in m.Items)
        {
            if (it.IsProjection) continue;
            var off = m.Layers[it.Layer];
            foreach (var op in it.Ops)
            {
                if (op is FillOp f) foreach (var p in f.Pts) Acc(p, off);
                else if (op is StrokeOp s) { double r = s.Tube / 2; foreach (var p in s.Pts) { Acc(p + new Vector(-r, -r), off); Acc(p + new Vector(r, r), off); } }
                else if (op is GlyphOp g) { var b = g.Geometry.Bounds; Acc(b.TopLeft, off); Acc(b.BottomRight, off); }
            }
        }
        if (x1 <= x0) { x0 = 0; x1 = 1; y0 = 0; y1 = 1; }
        var pad = L.Pad;
        double aw = Math.Max(10, size.Width - pad.Left - pad.Right), ah = Math.Max(10, size.Height - pad.Top - pad.Bottom);
        double sc = Math.Min(aw / (x1 - x0), ah / (y1 - y0));
        L.S = sc;
        double cx = pad.Left + aw / 2 - (x0 + x1) / 2 * sc, cy = pad.Top + ah / 2 - (y0 + y1) / 2 * sc;
        L.O = new Vector(Math.Round(cx), Math.Round(cy));
        L.Layers = m.Layers.Select(v => v * sc).ToArray();
        L.Bounds1 = new Rect(L.Map(new Point(x0, y0)), L.Map(new Point(x1, y1)));
        L.Bounds0 = ax1 > ax0 ? new Rect(L.Map(new Point(ax0, ay0)), L.Map(new Point(ax1, ay1))) : L.Bounds1;
        L.Small = L.Bounds1.Height < 220;

        var tubePens = new Dictionary<double, (Pen, Pen, Pen)>();
        L.Items = new ItemCache[m.Items.Count];
        for (int i = 0; i < m.Items.Count; i++)
        {
            var it = m.Items[i];
            var c = new ItemCache { It = it };
            L.Items[i] = c;
            if (it.IsProjection) { c.A = L.Map(it.A); c.B = L.Map(it.B); continue; }
            var runs = new List<Run>();
            var bounds = Rect.Empty;
            Run cur = null;
            foreach (var op in it.Ops)
            {
                switch (op)
                {
                    case FillOp f:
                    {
                        var pts = f.Pts.Select(L.Map).ToArray();
                        foreach (var p in pts) bounds.Union(p);
                        if (f.Holes != null)
                        {
                            var g = new StreamGeometry { FillRule = FillRule.EvenOdd };
                            using (var ctx = g.Open())
                            {
                                Fig(ctx, pts, true, true);
                                foreach (var h in f.Holes) Fig(ctx, h.Select(L.Map).ToArray(), true, true);
                            }
                            g.Freeze();
                            runs.Add(new Run { Kind = 0, Shade = f.Shade, G = g, Polys = new List<Point[]> { pts } });
                            cur = null;
                        }
                        else
                        {
                            if (cur == null || cur.Kind != 0 || cur.Shade != f.Shade || cur.G != null) { cur = new Run { Kind = 0, Shade = f.Shade, Polys = new() }; runs.Add(cur); }
                            cur.Polys.Add(pts);
                        }
                        break;
                    }
                    case StrokeOp s when s.Tube > 0:
                    {
                        var pts = s.Pts.Select(L.Map).ToArray();
                        double w = s.Tube * L.S;
                        foreach (var p in pts) bounds.Union(new Rect(p.X - w / 2, p.Y - w / 2, w, w));
                        double key = Math.Round(w * 4) / 4 + (int)s.TubeShade * 1000;
                        if (!tubePens.TryGetValue(key, out var pens))
                        {
                            var outer = BlueprintPalette.P(BlueprintPalette.InkBrush, w + 2 * BlueprintPalette.DetailWidth + 0.4);
                            var inner = BlueprintPalette.P(BlueprintPalette.ShadeBrushes[(int)s.TubeShade] ?? BlueprintPalette.PaperBrush, Math.Max(0.5, w - 0.4));
                            var red = BlueprintPalette.P(BlueprintPalette.RedBrush, w + 2 * BlueprintPalette.HighlightWidth);
                            pens = (outer, inner, red); tubePens[key] = pens;
                        }
                        var r = new Run { Kind = 2, Polys = new List<Point[]> { pts }, Closed = new List<bool> { false }, TubeOuter = pens.Item1, TubeInner = pens.Item2, TubeRed = pens.Item3 };
                        r.G = PolyGeom(r.Polys, r.Closed, false);
                        runs.Add(r); cur = null;
                        break;
                    }
                    case StrokeOp s:
                    {
                        var pts = s.Pts.Select(L.Map).ToArray();
                        foreach (var p in pts) bounds.Union(p);
                        if (cur == null || cur.Kind != 1 || cur.Ink != s.Ink) { cur = new Run { Kind = 1, Ink = s.Ink, Polys = new(), Closed = new() }; runs.Add(cur); }
                        cur.Polys.Add(pts); cur.Closed.Add(s.Closed);
                        break;
                    }
                    case GlyphOp g:
                    {
                        var geo = g.Geometry.Clone();
                        geo.Transform = new MatrixTransform(L.S, 0, 0, L.S, L.O.X, L.O.Y);
                        geo.Freeze();
                        bounds.Union(geo.Bounds);
                        runs.Add(new Run { Kind = 3, G = geo, Fine = g.Fine });
                        cur = null;
                        break;
                    }
                }
            }
            foreach (var r in runs)
            {
                if (r.G != null) continue;
                r.G = r.Kind == 0 ? PolyGeom(r.Polys, null, true) : PolyGeom(r.Polys, r.Closed, false);
            }
            c.Runs = runs.ToArray();
            c.Bounds = bounds;
        }
        BuildProfile(L, m);
        return L;

    }

    private static void Fig(StreamGeometryContext ctx, Point[] pts, bool filled, bool closed)
    {
        if (pts.Length == 0) return;
        ctx.BeginFigure(pts[0], filled, closed);
        if (pts.Length > 1) ctx.PolyLineTo(pts.Skip(1).ToArray(), true, true);
    }

    private static Geometry PolyGeom(List<Point[]> polys, List<bool> closed, bool filled)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
            for (int i = 0; i < polys.Count; i++) Fig(ctx, polys[i], filled, filled || (closed != null && closed[i]));
        g.Freeze();
        return g;
    }

    /// <summary>Профиль рисунка по строкам (разнесённый вид): где по X начинается и кончается рисунок — для подписей.</summary>
    private static void BuildProfile(Layout L, Model m)
    {
        int rows = (int)(L.Size.Height / Layout.RowH) + 2;
        L.RowMin = Enumerable.Repeat(double.MaxValue, rows).ToArray();
        L.RowMax = Enumerable.Repeat(double.MinValue, rows).ToArray();
        void Seg(Point a, Point b, double grow)
        {
            double ya = Math.Min(a.Y, b.Y) - grow, yb = Math.Max(a.Y, b.Y) + grow;
            int r0 = Math.Max(0, (int)Math.Floor(ya / Layout.RowH)), r1 = Math.Min(rows - 1, (int)Math.Ceiling(yb / Layout.RowH));
            for (int r = r0; r <= r1; r++)
            {
                double y = r * Layout.RowH;
                double x;
                if (Math.Abs(b.Y - a.Y) < 1e-6) { Upd(r, Math.Min(a.X, b.X) - grow); Upd(r, Math.Max(a.X, b.X) + grow); continue; }
                double t = Math.Clamp((y - a.Y) / (b.Y - a.Y), 0, 1);
                x = a.X + (b.X - a.X) * t;
                Upd(r, x - grow); Upd(r, x + grow);
            }
        }
        void Upd(int r, double x) { if (x < L.RowMin[r]) L.RowMin[r] = x; if (x > L.RowMax[r]) L.RowMax[r] = x; }
        foreach (var c in L.Items)
        {
            if (c.It.IsProjection) continue;
            var off = L.Layers[c.It.Layer];
            foreach (var r in c.Runs)
            {
                if (r.Polys == null) { var b = r.G.Bounds; b.Offset(off); Seg(b.TopLeft, b.BottomLeft, 0); Seg(b.TopRight, b.BottomRight, 0); continue; }
                double grow = r.Kind == 2 ? r.TubeOuter.Thickness / 2 : 1;
                foreach (var poly in r.Polys)
                    for (int i = 0; i < poly.Length; i++)
                    {
                        var a = poly[i] + off; var b = poly[(i + 1) % poly.Length] + off;
                        if (poly.Length == 1) b = a;
                        Seg(a, b, grow);
                    }
            }
        }
    }

    // ───────────────────────────── отрисовка ─────────────────────────────

    private double HiMix => (double)GetValue(HiMixProperty);
    private Part prevHighlight = Part.None;

    private void OnHighlightChanged(Part old)
    {
        prevHighlight = old;
        staticCache = null;
        if (Motion.On && IsLoaded) Anim(HiMixProperty, 0, 1, 200, null, () => { staticCache = null; InvalidateVisual(); }, linear: true);
        else Stop(HiMixProperty, 1);
    }

    /// <summary>Уровень подсветки детали 0..1 (с плавной сменой).</summary>
    private double HiLevel(Part p)
    {
        if (p == Part.None) return 0;
        double mix = HiMix;
        if (p == Highlight) return Motion.Out.Ease(Math.Clamp(mix * 200 / 140, 0, 1));
        if (p == prevHighlight) return 1 - Motion.In.Ease(Math.Clamp(mix, 0, 1));
        return 0;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 4 || ActualHeight < 4) return;
        var L = EnsureLayout();
        if (interactive) dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        double p = Math.Clamp(Progress, 0, 1), e = model.Explodes ? Math.Clamp(Explode, 0, 1) : 0, fade = Fade;
        if (fade > 0)
        {
            if (fade < 1) dc.PushOpacity(fade);
            bool idle = p >= 1 && phase == Phase.Idle && HiMix >= 1;
            if (idle)
            {
                var key = (e, Highlight, HiMix, ghostOthers && Scene == Scene.Pc, BlueprintPalette.Version);
                if (staticCache == null || staticKey != key)
                {
                    var g = new DrawingGroup();
                    using (var gc = g.Open()) DrawScene(gc, L, 1, e);
                    g.Freeze();
                    staticCache = g; staticKey = key;
                }
                dc.DrawDrawing(staticCache);
            }
            else DrawScene(dc, L, p, e);
            if (fade < 1) dc.Pop();
        }
        DrawDimension(dc, L, e);
        DrawCallouts(dc, L, e);
    }

    // ───────────────────────────── размерная линия ─────────────────────────────

    private string dimText;
    private FormattedText dimFt; private string dimKey;

    /// <summary>
    /// Габаритный размер сцены с подписью (сейчас есть у Ram: «через оба модуля»). null или "" — скрыть.
    /// Рисуется по spec §4: выносные 1 Ink3, размерная 0,8 Ink2, залитые стрелки 8×3, число Cascadia Mono 10,5 на бумаге.
    /// </summary>
    public void SetDimension(string text) { dimText = text; calloutsDirty = true; InvalidateVisual(); }

    /// <summary>Концы размера и высота размерной линии (над рисунком между концами).</summary>
    private static (Point a, Point b, double y) DimGeometry(Layout L, DimDef dim, double e)
    {
        var a = L.Map(dim.A) + L.Layers[dim.LayerA] * e;
        var b = L.Map(dim.B) + L.Layers[dim.LayerB] * e;
        if (a.X > b.X) (a, b) = (b, a);
        double top = Math.Min(a.Y, b.Y);
        for (int r = 0; r < L.RowMin.Length; r++)
            if (L.RowMin[r] <= b.X && L.RowMax[r] >= a.X) { top = Math.Min(top, r * Layout.RowH); break; }
        return (a, b, Math.Round(Math.Max(16, top - 22)) + 0.5);
    }

    private void DrawDimension(DrawingContext dc, Layout L, double e)
    {
        var dim = model.Dimension;
        if (dim == null || string.IsNullOrEmpty(dimText) || phase == Phase.Drawing) return;
        double alpha = (double)GetValue(CalloutOpacityProperty) * (model.Explodes ? Math.Clamp((e - 0.6) / 0.4, 0, 1) : 1);
        if (alpha <= 0) return;
        var (a, b, y) = DimGeometry(L, dim, e);
        if (alpha < 1) dc.PushOpacity(alpha);
        var ext = BlueprintPalette.P(BlueprintPalette.Ink3Brush, 1.0);
        var line = BlueprintPalette.P(BlueprintPalette.Ink2Brush, 0.8);
        dc.DrawLine(ext, new Point(Math.Round(a.X) + 0.5, a.Y - 4), new Point(Math.Round(a.X) + 0.5, y - 4));
        dc.DrawLine(ext, new Point(Math.Round(b.X) + 0.5, b.Y - 4), new Point(Math.Round(b.X) + 0.5, y - 4));
        double x0 = Math.Round(a.X) + 0.5, x1 = Math.Round(b.X) + 0.5;
        dc.DrawLine(line, new Point(x0, y), new Point(x1, y));
        foreach (var (tip, dir) in new[] { (x0, 1.0), (x1, -1.0) })
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(tip, y), true, true);
                c.LineTo(new Point(tip + dir * 8, y - 1.5), false, false);
                c.LineTo(new Point(tip + dir * 8, y + 1.5), false, false);
            }
            g.Freeze();
            dc.DrawGeometry(BlueprintPalette.Ink2Brush, null, g);
        }
        string key = dimText + BlueprintPalette.Version;
        if (dimKey != key) { dimFt = Text(dimText, "Cascadia Mono, Consolas", 10.5, FontWeights.Normal, BlueprintPalette.InkBrush); dimKey = key; }
        var tp = new Point(Math.Round((x0 + x1) / 2 - dimFt.Width / 2), y - 3 - dimFt.Height);
        dc.DrawRectangle(BlueprintPalette.PaperBrush, null, new Rect(tp.X - 3, tp.Y, dimFt.Width + 6, dimFt.Height));
        dc.DrawText(dimFt, tp);
        if (alpha < 1) dc.Pop();
    }

    private static double LocalT(int group, int groups, double p)
    {
        if (p >= 1) return 1;
        const double span = 0.55;
        double start = groups <= 1 ? 0 : (double)group / (groups - 1) * (1 - span);
        double t = Math.Clamp((p - start) / span, 0, 1);
        return 1 - Math.Pow(1 - t, 3);
    }

    private void DrawScene(DrawingContext dc, Layout L, double p, double e)
    {
        var m = model;
        bool ghostMode = ghostOthers && Scene == Scene.Pc;
        var shifts = new Transform[L.Layers.Length];
        for (int i = 0; i < shifts.Length; i++)
        {
            var v = L.Layers[i] * e;
            if (v.LengthSquared > 1e-4) { var t = new TranslateTransform(v.X, v.Y); t.Freeze(); shifts[i] = t; }
        }
        var levels = new Dictionary<Part, double>();
        foreach (var c in L.Items)
        {
            var it = c.It;
            double t = LocalT(it.Group, m.Groups, p);
            if (t <= 0) continue;
            if (it.IsProjection) { DrawProjection(dc, L, c, e, t); continue; }
            if (!levels.TryGetValue(it.Part, out double hl)) { hl = HiLevel(it.Part); levels[it.Part] = hl; }
            bool ghost = ghostMode && (it.Part != Highlight || it.Part == Part.None);
            if (ghostMode) hl = 0;
            var sh = shifts[it.Layer];
            if (sh != null) dc.PushTransform(sh);
            DrawItem(dc, c, t, hl, ghost);
            if (sh != null) dc.Pop();
        }
    }

    private Pen hiPen; private double hiPenLevel = -1; private int hiPenVer;
    private Pen HiPen(double level)
    {
        if (hiPen != null && Math.Abs(level - hiPenLevel) < 0.02 && hiPenVer == BlueprintPalette.Version) return hiPen;
        var a = BlueprintPalette.Ink1; var b = BlueprintPalette.Red;
        var c = Color.FromRgb((byte)(a.R + (b.R - a.R) * level), (byte)(a.G + (b.G - a.G) * level), (byte)(a.B + (b.B - a.B) * level));
        double w = BlueprintPalette.OutlineWidth + (BlueprintPalette.HighlightWidth - BlueprintPalette.OutlineWidth) * level;
        hiPen = BlueprintPalette.P(BlueprintPalette.B(c), w); hiPenLevel = level; hiPenVer = BlueprintPalette.Version;
        return hiPen;
    }

    private Pen smallPen; private int smallPenVer;
    private Pen SmallPen
    {
        get
        {
            if (smallPen == null || smallPenVer != BlueprintPalette.Version)
            { smallPen = BlueprintPalette.P(BlueprintPalette.InkBrush, Math.Max(0.9, BlueprintPalette.OutlineWidth * 0.66)); smallPenVer = BlueprintPalette.Version; }
            return smallPen;
        }
    }

    private void DrawItem(DrawingContext dc, ItemCache c, double t, double hl, bool ghost)
    {
        bool small = layout != null && layout.Small;
        double fa = t >= 1 ? 1 : Math.Clamp((t - 0.2) / 0.8, 0, 1);
        foreach (var r in c.Runs)
        {
            switch (r.Kind)
            {
                case 0:
                {
                    if (ghost || fa <= 0) break;
                    var brush = BlueprintPalette.ShadeBrushes[(int)r.Shade];
                    if (brush == null) break;
                    if (fa < 1) dc.PushOpacity(fa);
                    dc.DrawGeometry(brush, null, r.G);
                    if (hl > 0.001 && r.Shade != Shade.Dark)
                    {
                        dc.PushOpacity(hl * BlueprintPalette.HighlightTintOpacity);
                        dc.DrawGeometry(BlueprintPalette.TintBrush, null, r.G);
                        dc.Pop();
                    }
                    if (fa < 1) dc.Pop();
                    break;
                }
                case 1:
                {
                    Pen pen;
                    if (ghost) pen = r.Ink == Ink.Fine ? null : BlueprintPalette.GhostPen;
                    else if (small) pen = r.Ink switch
                    {
                        Ink.Outline => hl > 0.001 ? HiPen(hl) : SmallPen,
                        Ink.Detail => BlueprintPalette.FinePen,
                        Ink.Fine => null,
                        _ => BlueprintPalette.GhostPen,
                    };
                    else pen = r.Ink switch
                    {
                        Ink.Outline => hl > 0.001 ? HiPen(hl) : BlueprintPalette.OutlinePen,
                        Ink.Detail => BlueprintPalette.DetailPen,
                        Ink.Fine => BlueprintPalette.FinePen,
                        _ => BlueprintPalette.GhostPen,
                    };
                    if (pen == null) break;
                    dc.DrawGeometry(null, pen, t >= 1 ? r.G : Truncate(r.Polys, r.Closed, t));
                    break;
                }
                case 2:
                {
                    var g = t >= 1 ? r.G : Truncate(r.Polys, r.Closed, t);
                    if (ghost) { dc.DrawGeometry(null, BlueprintPalette.GhostPen, g); break; }
                    if (hl > 0.5) dc.DrawGeometry(null, r.TubeRed, g);
                    dc.DrawGeometry(null, r.TubeOuter, g);
                    dc.DrawGeometry(null, r.TubeInner, g);
                    break;
                }
                case 3:
                {
                    if (ghost || fa <= 0 || (small && r.Fine)) break;
                    if (fa < 1) dc.PushOpacity(fa);
                    dc.DrawGeometry(r.Fine ? BlueprintPalette.Ink2Brush : BlueprintPalette.InkBrush, null, r.G);
                    if (fa < 1) dc.Pop();
                    break;
                }
            }
        }
    }

    /// <summary>Ломаные, обрезанные до доли t своей длины.</summary>
    private static Geometry Truncate(List<Point[]> polys, List<bool> closed, double t)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            for (int i = 0; i < polys.Count; i++)
            {
                var pts = polys[i];
                if (pts.Length < 2) continue;
                bool cl = closed != null && closed[i];
                int n = cl ? pts.Length + 1 : pts.Length;
                double total = 0;
                for (int k = 1; k < n; k++) total += (pts[k % pts.Length] - pts[k - 1]).Length;
                double target = total * t, acc = 0;
                ctx.BeginFigure(pts[0], false, false);
                for (int k = 1; k < n; k++)
                {
                    var a = pts[k - 1]; var b = pts[k % pts.Length];
                    double l = (b - a).Length;
                    if (acc + l >= target) { ctx.LineTo(a + (b - a) * ((target - acc) / Math.Max(l, 1e-9)), true, true); break; }
                    ctx.LineTo(b, true, true);
                    acc += l;
                }
            }
        }
        g.Freeze();
        return g;
    }

    private void DrawProjection(DrawingContext dc, Layout L, ItemCache c, double e, double t)
    {
        var a = c.A + L.Layers[c.It.Layer] * e;
        var b = c.B + L.Layers[c.It.LayerB] * e;
        double len = (b - a).Length;
        if (len < 3 || e <= 0.02) return;
        // Линия растёт от нижнего конца, кружки проявляются.
        double grow = Math.Clamp((e - 0.25) / 0.75, 0, 1) * t;
        if (grow <= 0) return;
        var end = a + (b - a) * grow;
        dc.DrawLine(BlueprintPalette.ProjPen, a, end);
        double r = 2.5;
        dc.PushOpacity(Math.Clamp(grow * 3, 0, 1));
        dc.DrawEllipse(BlueprintPalette.PaperBrush, BlueprintPalette.CirclePen, a, r, r);
        if (grow >= 0.98) dc.DrawEllipse(BlueprintPalette.PaperBrush, BlueprintPalette.CirclePen, b, r, r);
        dc.Pop();
    }

    // ───────────────────────────── выноски ─────────────────────────────

    private sealed class Callout
    {
        public string Anchor, Label, Value; public bool Warn, Compact;
        public double AppearAt = double.NaN;   // мс на часах выносок; NaN — ждёт своей очереди
        public bool Shown;
        // раскладка
        public bool Placed, Left; public double X, Top, ShelfY, ShelfLen, BlockW, BlockH;
        public FormattedText LabelText, ValueText; public string LabelKey, ValueKey;
        public bool NameOnly => !Compact && string.IsNullOrEmpty(Value);
    }

    private readonly List<Callout> callouts = new();
    private bool calloutsDirty = true;
    private double lastScheduled = double.NegativeInfinity;

    /// <summary>
    /// Добавить или обновить выноску. Обновление только значения не повторяет анимацию.
    /// compact — выноска общего вида (имя 15 SemiBold + подпись 12 Ink3, линия Ink2; красная, если деталь подсвечена).
    /// Пустое value в обычном режиме — выноска «только имя» (подпись Ink3, линия 1 Ink3).
    /// </summary>
    public void SetCallout(string anchor, string label, string value, bool warn = false, bool compact = false)
    {
        var c = callouts.FirstOrDefault(x => x.Anchor == anchor);
        if (c != null)
        {
            bool relayout = c.Label != label || c.Compact != compact || string.IsNullOrEmpty(c.Value) != string.IsNullOrEmpty(value);
            c.Label = label; c.Value = value; c.Warn = warn; c.Compact = compact;
            if (relayout) calloutsDirty = true;
            InvalidateVisual();
            return;
        }
        c = new Callout { Anchor = anchor, Label = label, Value = value, Warn = warn, Compact = compact };
        callouts.Add(c);
        calloutsDirty = true;
        if (!Motion.On) c.Shown = true;
        else if (phase == Phase.Idle) ScheduleCallouts();
        InvalidateVisual();
    }

    public void ClearCallouts() { callouts.Clear(); calloutsDirty = true; InvalidateVisual(); }

    private double Clock => (double)GetValue(ClockProperty);
    private bool clockRunning;

    /// <summary>Запланировать появление ожидающих выносок (задержка 90 мс между ними) и запустить часы.</summary>
    private void ScheduleCallouts()
    {
        var pending = callouts.Where(c => !c.Shown && double.IsNaN(c.AppearAt)).ToList();
        if (pending.Count == 0) return;
        if (!Motion.On) { foreach (var c in pending) c.Shown = true; InvalidateVisual(); return; }
        double now = clockRunning ? Clock : 0;
        if (!clockRunning) lastScheduled = double.NegativeInfinity;
        // по порядку сверху вниз, если раскладка уже есть
        EnsureCalloutLayout();
        foreach (var c in pending.OrderBy(c => c.Placed ? c.Top : 0))
        {
            double at = Math.Max(now, lastScheduled + 90);
            c.AppearAt = at; lastScheduled = at;
        }
        double end = lastScheduled + CalloutDuration;
        clockRunning = true;
        Anim(ClockProperty, now, end, (int)Math.Max(1, end - now), null, () =>
        {
            clockRunning = false;
            foreach (var c in callouts) if (!double.IsNaN(c.AppearAt)) c.Shown = true;
            InvalidateVisual();
        }, linear: true);
    }

    private const double CalloutDuration = 460;

    private double Dpi => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    private FormattedText Text(string s, string font, double size, FontWeight weight, Brush brush) =>
        new(s ?? "", CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight,
            new Typeface(new FontFamily(font), FontStyles.Normal, weight, FontStretches.Normal), size, brush, Dpi);

    private void PrepareText(Callout c)
    {
        bool nameOnly = c.NameOnly;
        string lk = (c.Compact ? "c" : nameOnly ? "n" : "v") + c.Label + BlueprintPalette.Version;
        if (c.LabelKey != lk)
        {
            c.LabelText = nameOnly
                ? Text(c.Label, BlueprintPalette.CaptionFont, BlueprintPalette.CaptionSize + 1, FontWeights.Normal, BlueprintPalette.Ink3Brush)
                : Text(c.Label, BlueprintPalette.LabelFont, BlueprintPalette.LabelSize, FontWeights.SemiBold, BlueprintPalette.InkBrush);
            c.LabelKey = lk;
        }
        string vk = (c.Compact ? "c" : "v") + c.Value + c.Warn + BlueprintPalette.Version;
        if (c.ValueKey != vk)
        {
            c.ValueText = string.IsNullOrEmpty(c.Value) ? null : c.Compact
                ? Text(c.Value, BlueprintPalette.CaptionFont, BlueprintPalette.CaptionSize, FontWeights.Normal, BlueprintPalette.Ink3Brush)
                : Text(c.Value, BlueprintPalette.ValueFont, BlueprintPalette.ValueSize, FontWeights.SemiBold, c.Warn ? BlueprintPalette.WarnBrush : BlueprintPalette.InkBrush);
            c.ValueKey = vk;
        }
    }

    private AnchorDef FindAnchor(string id) => model?.Anchors.FirstOrDefault(a => a.Id == id);

    private Point AnchorPos(Layout L, AnchorDef a, double e) => L.Map(a.P) + L.Layers[a.Layer] * e;

    private void EnsureCalloutLayout()
    {
        if (!calloutsDirty && layout != null) return;
        if (ActualWidth < 4) return;
        var L = EnsureLayout();
        calloutsDirty = false;
        double e = model.Explodes ? 1 : 0;   // подписи стоят там, где окажутся детали после разнесения
        var list = new List<(Callout c, Point a, int side)>();
        foreach (var c in callouts)
        {
            c.Placed = false;
            var def = FindAnchor(c.Anchor);
            if (def == null) continue;
            PrepareText(c);
            list.Add((c, AnchorPos(L, def, e), def.Side));
        }
        if (list.Count == 0) return;
        var drawing = L.Bounds1;
        double mid = (drawing.Left + drawing.Right) / 2;
        foreach (var (c, a, side) in list) c.Left = side != 0 ? side < 0 : a.X < mid;
        // Баланс сторон: не больше трёх с одной стороны, если другая свободнее.
        for (int guard = 0; guard < 4; guard++)
        {
            var lefts = list.Where(x => x.c.Left && x.side == 0).ToList(); var rights = list.Where(x => !x.c.Left && x.side == 0).ToList();
            if (lefts.Count == 0 && rights.Count == 0) break;
            int nl = list.Count(x => x.c.Left), nr = list.Count - nl;
            if (nl > 3 && nl > nr + 1 && lefts.Count > 0) lefts.OrderByDescending(x => x.a.X).First().c.Left = false;
            else if (nr > 3 && nr > nl + 1 && rights.Count > 0) rights.OrderBy(x => x.a.X).First().c.Left = true;
            else break;
        }
        var placed = new List<Rect>();
        if (model.Dimension != null && !string.IsNullOrEmpty(dimText))
        {
            var (da, db, dy0) = DimGeometry(L, model.Dimension, e);
            placed.Add(new Rect(da.X - 6, dy0 - 20, db.X - da.X + 12, 26));
        }
        foreach (bool left in new[] { true, false })
        {
            double prevBottom = -1e9;
            foreach (var (c, a, _) in list.Where(x => x.c.Left == left).OrderBy(x => x.a.Y))
            {
                double lw = c.LabelText.WidthIncludingTrailingWhitespace, lh = c.LabelText.Height;
                double vw = c.ValueText?.WidthIncludingTrailingWhitespace ?? 0;
                double vb = c.ValueText?.Baseline ?? 0, vh = c.ValueText?.Height ?? 0;
                double textH = c.ValueText == null ? lh : (c.Compact ? lh + vh : lh + vb + 2);
                c.BlockW = Math.Max(lw, vw);
                c.ShelfLen = c.BlockW + 12;
                c.BlockH = textH + 5;
                double best = double.MaxValue; double bestY = a.Y - 40, bestX = 0;
                double want = c.Compact ? -30 : -36;
                for (double dy = -220; dy <= 220; dy += 3)
                {
                    double shelf = a.Y + dy;
                    double top = shelf - c.BlockH, bottom = shelf + 3;
                    if (top < 2 || bottom > ActualHeight - 2) continue;
                    if (top < prevBottom + 10) continue;
                    double x; double cost = Math.Abs(dy - want);
                    if (c.Compact)
                        x = left ? 0 : Math.Min(ActualWidth - c.ShelfLen, drawing.Right + 40);
                    else
                    {
                        // свободное место рядом с рисунком в этих строках
                        double edge = left ? double.MaxValue : double.MinValue;
                        int r0 = Math.Max(0, (int)((top - 6) / Layout.RowH)), r1 = Math.Min(L.RowMin.Length - 1, (int)((bottom + 6) / Layout.RowH) + 1);
                        for (int r = r0; r <= r1; r++)
                        {
                            if (L.RowMin[r] == double.MaxValue) continue;
                            if (left) edge = Math.Min(edge, L.RowMin[r]); else edge = Math.Max(edge, L.RowMax[r]);
                        }
                        if (left)
                        {
                            if (edge == double.MaxValue) edge = Math.Min(a.X, drawing.Left + 40);
                            x = Math.Min(edge - 20 - c.ShelfLen, a.X - 30 - c.ShelfLen);
                            if (x < 4) { cost += (4 - x) * 6; x = 4; }
                        }
                        else
                        {
                            if (edge == double.MinValue) edge = Math.Max(a.X, drawing.Right - 40);
                            x = Math.Max(edge + 20, a.X + 30);
                            double maxX = ActualWidth - c.ShelfLen - 4;
                            if (x > maxX) { cost += (x - maxX) * 6; x = maxX; }
                        }
                        cost += Math.Abs(left ? (a.X - (x + c.ShelfLen)) : (x - a.X)) * 0.04;   // короче линия — лучше
                    }
                    var rect = new Rect(x - 4, top - 4, c.ShelfLen + 8, c.BlockH + 8);
                    if (placed.Any(p => p.IntersectsWith(rect))) continue;
                    if (cost < best) { best = cost; bestY = shelf; bestX = x; }
                }
                if (best == double.MaxValue)
                {
                    bestY = Math.Max(prevBottom + 10 + c.BlockH, a.Y - 40);
                    bestX = c.Compact ? (left ? 0 : drawing.Right + 40) : (left ? 4 : ActualWidth - c.ShelfLen - 4);
                }
                c.X = bestX; c.ShelfY = Math.Round(bestY) + 0.5; c.Top = c.ShelfY - c.BlockH; c.Placed = true;
                prevBottom = c.ShelfY + 3;
                placed.Add(new Rect(c.X, c.Top, c.ShelfLen, c.BlockH + 3));
            }
        }
    }

    private void DrawCallouts(DrawingContext dc, Layout L, double e)
    {
        if (callouts.Count == 0) return;
        // живое значение стало шире места под ним — переложить подписи (без повтора анимации)
        foreach (var c in callouts)
        {
            if (!c.Placed) continue;
            PrepareText(c);
            double w = Math.Max(c.LabelText.WidthIncludingTrailingWhitespace, c.ValueText?.WidthIncludingTrailingWhitespace ?? 0);
            if (w > c.BlockW + 0.5) { calloutsDirty = true; break; }
        }
        EnsureCalloutLayout();
        double gop = (double)GetValue(CalloutOpacityProperty);
        if (gop <= 0) return;
        if (gop < 1) dc.PushOpacity(gop);
        double clock = Clock;
        foreach (var c in callouts)
        {
            if (!c.Placed) continue;
            double age = c.Shown ? 1e9 : double.IsNaN(c.AppearAt) ? -1 : clock - c.AppearAt;
            if (age < 0) continue;
            var def = FindAnchor(c.Anchor);
            if (def == null) continue;
            PrepareText(c);
            var a = AnchorPos(L, def, e);

            // стиль линии
            Brush lineBrush; double lw, dotD;
            if (c.Compact)
            {
                double hl = HiLevel(def.Part);
                if (Scene == Scene.Pc && ghostOthers) hl = def.Part == Highlight ? 1 : 0;
                lineBrush = hl > 0.5 ? BlueprintPalette.RedBrush : BlueprintPalette.Ink2Brush;
                lw = hl > 0.5 ? BlueprintPalette.LeaderWidth : BlueprintPalette.CompactLeaderWidth;
                dotD = hl > 0.5 ? BlueprintPalette.LeaderDot : BlueprintPalette.CompactDot;
            }
            else if (c.NameOnly) { lineBrush = BlueprintPalette.Ink3Brush; lw = 1.0; dotD = BlueprintPalette.CompactDot; }
            else { lineBrush = BlueprintPalette.RedBrush; lw = BlueprintPalette.LeaderWidth; dotD = BlueprintPalette.LeaderDot; }
            var pen = new Pen(lineBrush, lw) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Flat, LineJoin = PenLineJoin.Round };
            pen.Freeze();

            // ломаная: точка → излом (45°) → полка под текстом
            double sy = c.ShelfY;
            double dyAbs = Math.Abs(a.Y - sy);
            Point elbow, far;
            if (c.Left)
            {
                double inner = c.X + c.ShelfLen;
                double ex = Math.Max(inner, a.X - dyAbs);
                if (ex > a.X) ex = a.X;
                elbow = new Point(ex, sy); far = new Point(c.X, sy);
            }
            else
            {
                double inner = c.X;
                double ex = Math.Min(inner, a.X + dyAbs);
                if (ex < a.X) ex = a.X;
                elbow = new Point(ex, sy); far = new Point(c.X + c.ShelfLen, sy);
            }
            double l1 = (elbow - a).Length, l2 = (far - elbow).Length, total = l1 + l2;
            double lt = Ease((age - 100) / 220) * total;
            if (lt > 0)
            {
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(a, false, false);
                    if (lt <= l1) ctx.LineTo(a + (elbow - a) * (lt / Math.Max(l1, 1e-6)), true, true);
                    else { ctx.LineTo(elbow, true, true); ctx.LineTo(elbow + (far - elbow) * ((lt - l1) / Math.Max(l2, 1e-6)), true, true); }
                }
                g.Freeze();
                dc.DrawGeometry(null, pen, g);
            }
            double ds = Ease(age / 120);
            if (ds > 0) dc.DrawEllipse(lineBrush, null, a, dotD / 2 * ds, dotD / 2 * ds);

            double ta = Ease((age - 260) / 180);
            if (ta > 0)
            {
                double dx = (c.Left ? -6 : 6) * (1 - ta);
                if (ta < 1) dc.PushOpacity(ta);
                double x = c.X + dx;
                double y = c.Top;
                dc.DrawText(c.LabelText, new Point(x, y));
                if (c.ValueText != null)
                {
                    double vy = c.Compact ? y + c.LabelText.Height : sy - 3 - c.ValueText.Baseline;
                    dc.DrawText(c.ValueText, new Point(x, vy));
                }
                if (ta < 1) dc.Pop();
            }
        }
        if (gop < 1) dc.Pop();
    }

    private static double Ease(double t) { t = Math.Clamp(t, 0, 1); return 1 - Math.Pow(1 - t, 3); }

    // ───────────────────────────── анимация ─────────────────────────────

    private enum Phase { Idle, Drawing, Exploding, Collapsing }
    private Phase phase = Phase.Idle;
    private int gen, animCounter;
    private Action collapseDone;

    /// <summary>Анимация свойства from→to (60 кадров/с). done вызывается, только если её не сменила другая.</summary>
    private void Anim(DependencyProperty dp, double from, double to, int ms, IEasingFunction ease, Action done, bool linear = false, int delay = 0)
    {
        int id = ++animCounter;
        animGens[dp] = id;
        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(Math.Max(1, ms)))
        {
            EasingFunction = linear ? null : ease ?? Motion.Out,
            FillBehavior = FillBehavior.HoldEnd,
            BeginTime = TimeSpan.FromMilliseconds(delay),
        };
        Timeline.SetDesiredFrameRate(anim, 60);
        anim.Completed += (_, _) =>
        {
            if (!IsCurrent(dp, id)) return;
            animGens[dp] = -1;
            BeginAnimation(dp, null);
            SetValue(dp, to);
            done?.Invoke();
        };
        SetValue(dp, from);
        BeginAnimation(dp, anim);
    }

    private readonly Dictionary<DependencyProperty, int> animGens = new();
    private bool IsCurrent(DependencyProperty dp, int g) => animGens.TryGetValue(dp, out var v) && v == g;

    private void Stop(DependencyProperty dp, double value)
    {
        animGens[dp] = -1;
        BeginAnimation(dp, null);
        SetValue(dp, value);
    }

    private void StopAll()
    {
        gen++;
        Stop(ProgressProperty, 1); Stop(ExplodeProperty, 1); Stop(FadeProperty, 1); Stop(CalloutOpacityProperty, 1);
        Stop(ClockProperty, 0); clockRunning = false;
        phase = Phase.Idle;
    }

    /// <summary>
    /// Вступление: прорисовка (только при первом показе сцены за сеанс; иначе проявление 200 мс),
    /// затем разнесение (сцены деталей), затем выноски. Если анимация выключена — сразу конечное состояние.
    /// </summary>
    public void Play()
    {
        if (drawnOnce.Contains(Scene)) PlayInternal(false); else PlayFull();
    }

    /// <summary>То же, что Play, но прорисовка штрихов всегда.</summary>
    public void PlayFull() => PlayInternal(true);

    private void PlayInternal(bool drawOn)
    {
        EnsureModel();
        drawnOnce.Add(Scene);
        if (!Motion.On) { Finish(); return; }
        StopAll();
        int my = ++gen;
        foreach (var c in callouts) { c.Shown = false; c.AppearAt = double.NaN; }
        SetValue(CalloutOpacityProperty, 1.0);
        bool explodes = model.Explodes;
        Explode = explodes ? 0 : 1;
        if (drawOn)
        {
            phase = Phase.Drawing;
            Fade = 1;
            int ms = Scene == Scene.Pc ? 1150 : 1050;
            Anim(ProgressProperty, 0, 1, ms, null, () => { if (my == gen) AfterDraw(my, explodes); }, linear: true);
        }
        else
        {
            phase = Phase.Exploding;
            Progress = 1;
            Anim(FadeProperty, 0, 1, 200, Motion.Out, explodes ? null : () => { if (my == gen) StartCallouts(); });
            if (explodes) StartExplode(my, 100);
        }
        InvalidateVisual();
    }

    private void AfterDraw(int my, bool explodes)
    {
        if (explodes) StartExplode(my, 0);
        else StartCallouts();
    }

    private static readonly IEasingFunction InOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseInOut });
    private static readonly IEasingFunction InEase = Freeze(new QuadraticEase { EasingMode = EasingMode.EaseIn });
    private static IEasingFunction Freeze(EasingFunctionBase e) { e.Freeze(); return e; }

    private void StartExplode(int my, int delay)
    {
        phase = Phase.Exploding;
        Anim(ExplodeProperty, 0, 1, 520, InOut, () => { if (my == gen) StartCallouts(); }, delay: delay);
    }

    private void StartCallouts()
    {
        phase = Phase.Idle;
        staticCache = null;
        ScheduleCallouts();
        InvalidateVisual();
    }

    /// <summary>Досрочно завершить вступление (клик или клавиша во время прорисовки) или сборку.</summary>
    public void Finish()
    {
        var done = collapseDone; collapseDone = null;
        bool collapsing = phase == Phase.Collapsing;
        gen++;
        Stop(ProgressProperty, 1); Stop(FadeProperty, 1); Stop(ClockProperty, 0); clockRunning = false;
        Stop(ExplodeProperty, collapsing ? 0 : 1);
        Stop(CalloutOpacityProperty, collapsing ? 0 : 1);
        if (!collapsing) foreach (var c in callouts) c.Shown = true;
        phase = Phase.Idle;
        staticCache = null;
        InvalidateVisual();
        done?.Invoke();
    }

    /// <summary>Обратное Play: выноски гаснут (120 мс), детали собираются (300 мс), затем done.</summary>
    public void Collapse(Action done = null)
    {
        if (phase == Phase.Collapsing) { var prev = collapseDone; collapseDone = prev + done; return; }
        gen++;
        int my = gen;
        if (!Motion.On || model == null)
        {
            Stop(CalloutOpacityProperty, 0); Stop(ExplodeProperty, 0); Stop(ProgressProperty, 1);
            phase = Phase.Idle; staticCache = null; InvalidateVisual();
            done?.Invoke();
            return;
        }
        phase = Phase.Collapsing;
        collapseDone = done;
        Stop(ProgressProperty, 1); Stop(FadeProperty, 1);
        double ce = Explode;
        Anim(CalloutOpacityProperty, (double)GetValue(CalloutOpacityProperty), 0, 120, InEase, null);
        Action finish = () =>
        {
            if (my != gen) return;
            phase = Phase.Idle; staticCache = null;
            var d = collapseDone; collapseDone = null;
            InvalidateVisual();
            d?.Invoke();
        };
        if (model.Explodes && ce > 0) Anim(ExplodeProperty, ce, 0, 300, InOut, finish, delay: 80);
        else Anim(FadeProperty, 1, 1, 160, null, finish);
    }

    // ───────────────────────────── попадание мышью ─────────────────────────────

    private Part hovered = Part.None, pressed = Part.None;

    /// <summary>Деталь под точкой (координаты элемента). Учитывает перекрытие: побеждает верхняя.</summary>
    public Part PartAt(Point pt)
    {
        if (ActualWidth < 4) return Part.None;
        var L = EnsureLayout();
        EnsureCalloutLayout();
        foreach (var c in callouts)
            if (c.Placed && (c.Shown || !double.IsNaN(c.AppearAt)) && new Rect(c.X, c.Top, c.ShelfLen, c.BlockH + 4).Contains(pt))
            {
                var def = FindAnchor(c.Anchor);
                if (def != null && def.Part != Part.None) return def.Part;
            }
        double e = model.Explodes ? Math.Clamp(Explode, 0, 1) : 0;
        for (int i = L.Items.Length - 1; i >= 0; i--)
        {
            var c = L.Items[i];
            if (c.It.IsProjection || c.It.NoHit) continue;
            var q = pt - L.Layers[c.It.Layer] * e;
            if (!c.Bounds.Contains(q)) continue;
            foreach (var r in c.Runs)
                if (r.Kind == 0 && r.Shade != Shade.None && r.G.FillContains(q)) return c.It.Part;
        }
        return Part.None;
    }

    /// <summary>Рамка проекции детали в координатах элемента (сцена Pc), для «наезда камеры».</summary>
    public Rect? PartBounds(Part part)
    {
        if (ActualWidth < 4 || part == Part.None) return null;
        var L = EnsureLayout();
        double e = model.Explodes ? Math.Clamp(Explode, 0, 1) : 0;
        var r = Rect.Empty;
        foreach (var c in L.Items)
        {
            if (c.It.IsProjection || c.It.Part != part || c.Bounds.IsEmpty) continue;
            var b = c.Bounds; b.Offset(L.Layers[c.It.Layer] * e);
            r.Union(b);
        }
        return r.IsEmpty ? null : r;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!interactive) return;
        var p = PartAt(e.GetPosition(this));
        Cursor = p != Part.None ? Cursors.Hand : null;
        if (p != hovered) { hovered = p; PartHovered?.Invoke(p); }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        Cursor = null;
        if (hovered != Part.None) { hovered = Part.None; PartHovered?.Invoke(Part.None); }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!interactive) return;
        pressed = PartAt(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!interactive) return;
        var p = PartAt(e.GetPosition(this));
        if (p != Part.None && p == pressed) PartClicked?.Invoke(p);
        pressed = Part.None;
    }

    // для RenderHarness: стоимость кадра на стороне ЦП
    internal int DebugItemCount => EnsureModel().Items.Count;
    internal void DebugRender(double progress, double explode)
    {
        Progress = progress; Explode = explode;
        var saved = phase;
        if (explode < 1) phase = Phase.Exploding;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) OnRender(dc);
        phase = saved;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        calloutsDirty = true;
    }
}
