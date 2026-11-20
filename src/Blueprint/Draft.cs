using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PulsePC.Blueprint;

// ───────────────────────────── 3D math ─────────────────────────────

internal readonly struct V3
{
    public readonly double X, Y, Z;
    public V3(double x, double y, double z) { X = x; Y = y; Z = z; }
    public static readonly V3 Zero = new(0, 0, 0), UX = new(1, 0, 0), UY = new(0, 1, 0), UZ = new(0, 0, 1);
    public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static V3 operator -(V3 a) => new(-a.X, -a.Y, -a.Z);
    public static V3 operator *(V3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    public static V3 operator *(double k, V3 a) => new(a.X * k, a.Y * k, a.Z * k);
    public static V3 operator /(V3 a, double k) => new(a.X / k, a.Y / k, a.Z / k);
    public double Dot(V3 b) => X * b.X + Y * b.Y + Z * b.Z;
    public V3 Cross(V3 b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public V3 Norm { get { double l = Length; return l < 1e-12 ? this : this / l; } }
    public static V3 Lerp(V3 a, V3 b, double t) => a + (b - a) * t;
    public override string ToString() => $"({X:0.##},{Y:0.##},{Z:0.##})";
}

/// <summary>Ортографическая камера: поворот вокруг вертикали (yaw), затем наклон вниз (pitch).</summary>
internal sealed class Camera
{
    private readonly double cy, sy, cp, sp;
    public Camera(double yawDeg, double pitchDeg)
    {
        double a = yawDeg * Math.PI / 180, b = pitchDeg * Math.PI / 180;
        cy = Math.Cos(a); sy = Math.Sin(a); cp = Math.Cos(b); sp = Math.Sin(b);
    }
    /// <summary>В пространство камеры: X вправо, Y вверх, Z к зрителю.</summary>
    public V3 View(V3 p)
    {
        double x1 = p.X * cy + p.Z * sy;
        double z1 = -p.X * sy + p.Z * cy;
        return new V3(x1, p.Y * cp - z1 * sp, p.Y * sp + z1 * cp);
    }
    public Point Project(V3 p) { var v = View(p); return new Point(v.X, -v.Y); }
}

// ───────────────────────────── Model ─────────────────────────────

internal enum Shade : byte { Paper, Side, Side2, Recess, Dark, None }
internal enum Ink : byte { Outline, Detail, Fine, Hidden }

internal abstract class Op { }

internal sealed class FillOp : Op
{
    public Point[] Pts; public Point[][] Holes; public Shade Shade;
}

internal sealed class StrokeOp : Op
{
    public Point[] Pts; public bool Closed; public Ink Ink;
    public double Tube;        // >0 — трубка: ширина в единицах модели (мм)
    public Shade TubeShade;
}

internal sealed class GlyphOp : Op
{
    public Geometry Geometry;  // в единицах модели, уже спроецирована
    public bool Fine;
}

internal sealed class Item
{
    public Part Part; public int Layer; public int Group;
    public double DepthSum; public int DepthCount; public double DepthBias;
    public readonly List<Op> Ops = new();
    public bool IsProjection; public Point A, B; public int LayerB;
    public bool NoHit;
    public double Depth => (DepthCount == 0 ? 0 : DepthSum / DepthCount) + DepthBias;
}

internal sealed class AnchorDef { public string Id; public Point P; public int Layer; public Part Part; public int Side; }
internal sealed class DimDef { public Point A, B; public int LayerA, LayerB; }

internal sealed class Model
{
    public readonly List<Item> Items = new();
    public readonly List<Vector> Layers = new() { new Vector() };
    public readonly List<AnchorDef> Anchors = new();
    public DimDef Dimension;
    public int Groups = 1;
    public double Angle;          // угол оси глубины на экране, градусы
    public bool Explodes => Layers.Count > 1;
}

/// <summary>Грань с локальными осями: точка (u, v) в мм от начала грани.</summary>
internal readonly struct Face
{
    public readonly V3 O, U, V, N; public readonly bool Visible;
    public Face(V3 o, V3 u, V3 v, V3 n, bool visible) { O = o; U = u; V = v; N = n; Visible = visible; }
    public V3 At(double u, double v) => O + U * u + V * v;
    public V3 At(double u, double v, double lift) => O + U * u + V * v + N * lift;
}

internal sealed class BoxH
{
    public Face Top, Bottom, Front, Back, Left, Right;
    public V3 Min, Max;   // в локальных координатах
}

// ───────────────────────────── Builder ─────────────────────────────

/// <summary>
/// Построитель чертежа. Порядок добавления = порядок рисования (художник): дальнее раньше.
/// Каждый примитив (Box, Cyl, Poly, Tube…) создаёт свой элемент; линии деталей (Line, Path, Hatch, Text)
/// добавляются к последнему элементу и рисуются поверх его заливки.
/// </summary>
internal sealed class Draft
{
    public readonly Model M = new();
    private readonly Camera cam;
    private readonly V3 light;
    private Item last, merge;
    private int group;

    public Part Part = Part.None;
    public int Layer;

    // Локальная система координат (стек).
    private V3 fo = V3.Zero, fx = V3.UX, fy = V3.UY, fz = V3.UZ;
    private readonly Stack<(V3, V3, V3, V3)> frames = new();

    public Draft(double yawDeg, double pitchDeg)
    {
        cam = new Camera(yawDeg, pitchDeg);
        light = new V3(-0.42, 0.78, 0.46).Norm;   // в пространстве камеры: сверху-слева-спереди
        // Угол проекции для угловой метки: наклон оси глубины (Z) к горизонтали на экране.
        var z = cam.Project(V3.UZ) - cam.Project(V3.Zero);
        M.Angle = Math.Round(Math.Atan2(Math.Abs(z.Y), Math.Abs(z.X)) * 180 / Math.PI);
    }

    // ─── frames ───
    public void Push(V3 origin, V3 ax, V3 ay, V3 az)
    {
        frames.Push((fo, fx, fy, fz));
        var o = W(origin); var x = WD(ax); var y = WD(ay); var z = WD(az);
        fo = o; fx = x; fy = y; fz = z;
    }
    public void Push(V3 origin) => Push(origin, V3.UX, V3.UY, V3.UZ);
    /// <summary>Поворот вокруг оси Y (локальной) на угол в градусах, с переносом.</summary>
    public void PushRotY(V3 origin, double deg)
    {
        double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        Push(origin, new V3(c, 0, -s), V3.UY, new V3(s, 0, c));
    }
    public void Pop() { (fo, fx, fy, fz) = frames.Pop(); }

    public V3 W(V3 p) => fo + fx * p.X + fy * p.Y + fz * p.Z;
    public V3 WD(V3 d) => fx * d.X + fy * d.Y + fz * d.Z;

    /// <summary>Грань в локальных координатах: начало o, оси u и v (единичные), нормаль n.</summary>
    public Face MakeFace(V3 o, V3 u, V3 v, V3 n) => new(W(o), WD(u), WD(v), WD(n), FacingL(n));
    /// <summary>Горизонтальная грань на высоте y: u вдоль X, v вдоль Z.</summary>
    public Face TopAt(double y) => MakeFace(new V3(0, y, 0), V3.UX, V3.UZ, V3.UY);

    // ─── state ───
    public void NextGroup() { group++; M.Groups = Math.Max(M.Groups, group + 1); }
    public int Group { get => group; set { group = value; M.Groups = Math.Max(M.Groups, group + 1); } }

    private List<Op> deferred;
    /// <summary>Мелкие детали на одной грани: сначала все заливки, потом все контуры (меньше вызовов отрисовки).</summary>
    public void Batch(Action body)
    {
        var outer = deferred; deferred = new List<Op>();
        try { body(); }
        finally { var list = deferred; deferred = outer; if (list.Count > 0) Tgt.Ops.AddRange(list); }
    }
    public int NewLayer(V3 offsetWorld)
    {
        var a = cam.Project(V3.Zero); var b = cam.Project(offsetWorld);
        M.Layers.Add(b - a);
        return M.Layers.Count - 1;
    }

    public bool Facing(V3 worldNormal) => cam.View(worldNormal).Z > 1e-6;
    public bool FacingL(V3 localNormal) => Facing(WD(localNormal));

    private Item NewItem()
    {
        if (merge != null) return merge;
        var it = new Item { Part = Part, Layer = Layer, Group = group };
        M.Items.Add(it);
        last = it;
        return it;
    }
    /// <summary>Пустой элемент: последующие линии рисуются поверх всего, что добавлено до него.</summary>
    public Item Begin() { var it = NewItem(); return it; }
    public Item Last => last;
    public void Bias(double depth) { if (last != null) last.DepthBias += depth; }
    public void NoHit() { if (last != null) last.NoHit = true; }

    /// <summary>Все примитивы внутри — один элемент (меньше вызовов отрисовки). Только для непересекающихся мелочей.</summary>
    public void Merge(Action body)
    {
        var it = new Item { Part = Part, Layer = Layer, Group = group };
        M.Items.Add(it); last = it;
        var prev = merge; merge = it;
        try { body(); } finally { merge = prev; last = it; }
    }

    /// <summary>Отсортировать элементы, добавленные внутри, от дальнего к ближнему.</summary>
    public void Sorted(Action body)
    {
        int start = M.Items.Count;
        body();
        var range = M.Items.GetRange(start, M.Items.Count - start);
        var sorted = range.Select((it, i) => (it, i)).OrderBy(t => t.it.Depth).ThenBy(t => t.i).Select(t => t.it).ToList();
        for (int i = 0; i < sorted.Count; i++) M.Items[start + i] = sorted[i];
    }

    /// <summary>Точка привязки выноски. side: −1 — подпись слева, +1 — справа, 0 — по положению.</summary>
    public void Anchor(string id, V3 local, int side = 0)
    {
        M.Anchors.RemoveAll(a => a.Id == id);
        M.Anchors.Add(new AnchorDef { Id = id, P = cam.Project(W(local)), Layer = Layer, Part = Part, Side = side });
    }

    // ─── projection helpers ───
    private Point Pj(Item it, V3 local)
    {
        var w = W(local);
        var v = cam.View(w);
        it.DepthSum += v.Z; it.DepthCount++;
        return new Point(v.X, -v.Y);
    }
    private Point[] Pj(Item it, IList<V3> pts) { var r = new Point[pts.Count]; for (int i = 0; i < r.Length; i++) r[i] = Pj(it, pts[i]); return r; }

    public Shade ShadeFor(V3 worldNormal, int bias = 0)
    {
        var n = cam.View(worldNormal.Norm);
        double b = n.Dot(light);
        int s = b >= 0.75 ? 0 : b >= 0.1 ? 1 : 2;
        s = Math.Clamp(s + bias, 0, 3);
        return (Shade)s;
    }

    // ─── primitives ───

    /// <summary>Плоский многоугольник (точки против часовой, если смотреть с лицевой стороны).</summary>
    public bool Poly(IList<V3> pts, Shade? shade = null, bool twoSided = false, Ink ink = Ink.Outline, int bias = 0, bool outline = true)
    {
        var n = WD(Newell(pts));
        bool vis = Facing(n);
        if (!vis && !twoSided) return false;
        var it = NewItem();
        var p = Pj(it, pts);
        var sh = shade ?? ShadeFor(vis ? n : -n, bias);
        if (sh != Shade.None) it.Ops.Add(new FillOp { Pts = p, Shade = sh });
        if (outline) it.Ops.Add(new StrokeOp { Pts = p, Closed = true, Ink = ink });
        return true;
    }

    public bool PolyHoles(IList<V3> outer, IList<IList<V3>> holes, Shade? shade = null, int bias = 0)
    {
        var n = WD(Newell(outer));
        if (!Facing(n)) return false;
        var it = NewItem();
        var p = Pj(it, outer);
        var hs = holes.Select(h => Pj(it, h)).ToArray();
        it.Ops.Add(new FillOp { Pts = p, Holes = hs, Shade = shade ?? ShadeFor(n, bias) });
        it.Ops.Add(new StrokeOp { Pts = p, Closed = true, Ink = Ink.Outline });
        foreach (var h in hs) it.Ops.Add(new StrokeOp { Pts = h, Closed = true, Ink = Ink.Detail });
        return true;
    }

    /// <summary>Прямоугольный брус от a до b (локальные координаты). Видимые грани, общие рёбра один раз.</summary>
    public BoxH Box(V3 a, V3 b, int bias = 0, Shade? shade = null, Ink ink = Ink.Outline)
    {
        var mn = new V3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
        var mx = new V3(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
        double x0 = mn.X, y0 = mn.Y, z0 = mn.Z, x1 = mx.X, y1 = mx.Y, z1 = mx.Z;
        var h = new BoxH { Min = mn, Max = mx };
        V3 X = WD(V3.UX), Y = WD(V3.UY), Z = WD(V3.UZ);
        Face F(V3 o, V3 u, V3 v, V3 n) => new(W(o), WD(u), WD(v), WD(n), Facing(WD(n)));
        h.Top = F(new V3(x0, y1, z0), V3.UX, V3.UZ, V3.UY);
        h.Bottom = F(new V3(x0, y0, z0), V3.UX, V3.UZ, -V3.UY);
        h.Front = F(new V3(x0, y0, z1), V3.UX, V3.UY, V3.UZ);
        h.Back = F(new V3(x0, y0, z0), V3.UX, V3.UY, -V3.UZ);
        h.Right = F(new V3(x1, y0, z0), V3.UZ, V3.UY, V3.UX);
        h.Left = F(new V3(x0, y0, z0), V3.UZ, V3.UY, -V3.UX);

        var c = new V3[8];
        for (int i = 0; i < 8; i++) c[i] = new V3((i & 1) == 0 ? x0 : x1, (i & 2) == 0 ? y0 : y1, (i & 4) == 0 ? z0 : z1);
        // грани: индексы углов против часовой снаружи
        int[][] faces =
        {
            new[] { 2, 6, 7, 3 }, // top +Y
            new[] { 0, 1, 5, 4 }, // bottom -Y
            new[] { 4, 5, 7, 6 }, // front +Z
            new[] { 0, 2, 3, 1 }, // back -Z
            new[] { 1, 3, 7, 5 }, // right +X
            new[] { 0, 4, 6, 2 }, // left -X
        };
        Face[] fs = { h.Top, h.Bottom, h.Front, h.Back, h.Right, h.Left };
        var it = NewItem();
        var pc = new Point[8];
        for (int i = 0; i < 8; i++) pc[i] = Pj(it, c[i]);
        var edges = new HashSet<int>();
        var strokes = new List<StrokeOp>();
        for (int f = 0; f < 6; f++)
        {
            if (!fs[f].Visible) continue;
            var idx = faces[f];
            it.Ops.Add(new FillOp { Pts = idx.Select(i => pc[i]).ToArray(), Shade = shade ?? ShadeFor(fs[f].N, bias) });
            for (int e = 0; e < 4; e++)
            {
                int i0 = idx[e], i1 = idx[(e + 1) % 4];
                int key = Math.Min(i0, i1) * 8 + Math.Max(i0, i1);
                if (edges.Add(key)) strokes.Add(new StrokeOp { Pts = new[] { pc[i0], pc[i1] }, Ink = ink });
            }
        }
        it.Ops.AddRange(JoinSegments(strokes));
        return h;
    }

    /// <summary>Призма: многоугольник основания, вытянутый вдоль dir. Подходит и для невыпуклых контуров.</summary>
    public void Extrude(IList<V3> basePoly, V3 dir, int bias = 0, Shade? capShade = null, Shade? sideShade = null, Ink ink = Ink.Outline, bool caps = true)
    {
        var pts = basePoly.ToList();
        if (Newell(pts).Dot(dir) < 0) pts.Reverse();
        int n = pts.Count;
        var top = pts.Select(p => p + dir).ToList();
        var nTop = WD(dir.Norm);
        bool topVis = caps && Facing(nTop), botVis = caps && Facing(-nTop);
        var it = NewItem();
        bool convex = IsConvex(pts, dir);

        var bot = pts.ToList(); bot.Reverse();
        if (botVis) EmitFace(it, bot, capShade ?? ShadeFor(-nTop, bias), ink);

        var sides = new List<(double depth, List<V3> quad, V3 n)>();
        for (int i = 0; i < n; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % n];
            var nn = WD((b - a).Cross(dir)).Norm;
            if (!Facing(nn)) continue;
            var q = new List<V3> { a, b, b + dir, a + dir };
            double d = q.Sum(p => cam.View(W(p)).Z) / 4;
            sides.Add((d, q, nn));
        }
        if (convex)
        {
            // Выпуклая призма: сначала все заливки, потом рёбра без повторов.
            var strokes = new List<StrokeOp>();
            var seen = new HashSet<(long, long, long, long)>();
            void Edge(Point p, Point q)
            {
                var k1 = (R(p.X), R(p.Y), R(q.X), R(q.Y)); var k2 = (R(q.X), R(q.Y), R(p.X), R(p.Y));
                if (seen.Contains(k1) || seen.Contains(k2)) return;
                seen.Add(k1); strokes.Add(new StrokeOp { Pts = new[] { p, q }, Ink = ink });
            }
            foreach (var s in sides.OrderBy(s => s.depth))
            {
                var pp = Pj(it, s.quad);
                it.Ops.Add(new FillOp { Pts = pp, Shade = sideShade ?? ShadeFor(s.n, bias) });
                for (int e = 0; e < 4; e++) Edge(pp[e], pp[(e + 1) % 4]);
            }
            if (topVis)
            {
                var pp = Pj(it, top);
                it.Ops.Add(new FillOp { Pts = pp, Shade = capShade ?? ShadeFor(nTop, bias) });
                for (int e = 0; e < pp.Length; e++) Edge(pp[e], pp[(e + 1) % pp.Length]);
            }
            it.Ops.AddRange(JoinSegments(strokes));
        }
        else
        {
            foreach (var s in sides.OrderBy(s => s.depth)) EmitFace(it, s.quad, sideShade ?? ShadeFor(s.n, bias), ink);
            if (topVis) EmitFace(it, top, capShade ?? ShadeFor(nTop, bias), ink);
        }
    }

    /// <summary>
    /// Гладкое тело: контур (кривая) вытянут вдоль dir. Боковина — одна плоская заливка без швов между гранями,
    /// контуром рисуются только нижняя кромка видимой части и вертикали на границе видимости; затем крышка.
    /// </summary>
    public void SmoothExtrude(IList<V3> basePoly, V3 dir, Shade sideShade = Shade.Side, Shade? capShade = null, Ink ink = Ink.Outline, bool cap = true)
    {
        var pts = basePoly.ToList();
        if (Newell(pts).Dot(dir) < 0) pts.Reverse();
        int n = pts.Count;
        var nTop = WD(dir.Norm);
        var it = NewItem();
        var vis = new bool[n];
        var quads = new List<(double depth, List<V3> q)>();
        for (int i = 0; i < n; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % n];
            vis[i] = Facing(WD((b - a).Cross(dir)));
            if (!vis[i]) continue;
            var q = new List<V3> { a, b, b + dir, a + dir };
            quads.Add((q.Sum(p => cam.View(W(p)).Z) / 4, q));
        }
        foreach (var (_, q) in quads.OrderBy(x => x.depth))
        {
            // чуть расширенный четырёхугольник, чтобы между соседними не просвечивали щели сглаживания
            it.Ops.Add(new FillOp { Pts = Pj(it, q), Shade = sideShade });
        }
        // нижняя кромка по видимым участкам и вертикали на границах видимости
        int start = Array.FindIndex(vis, v => !v);
        if (start < 0) start = 0;
        List<V3> run = null;
        for (int k = 1; k <= n; k++)
        {
            int i = (start + k) % n;
            if (vis[i]) { run ??= new List<V3> { pts[i] }; run.Add(pts[(i + 1) % n]); }
            else if (run != null) { Path(run, ink); run = null; }
            int prev = (i - 1 + n) % n;
            if (vis[i] != vis[prev]) Line(pts[i], pts[i] + dir, ink);
        }
        if (run != null) Path(run, ink);
        if (cap && Facing(nTop))
        {
            var top = pts.Select(p => p + dir).ToList();
            var pp = Pj(it, top);
            it.Ops.Add(new FillOp { Pts = pp, Shade = capShade ?? ShadeFor(nTop) });
            it.Ops.Add(new StrokeOp { Pts = pp, Closed = true, Ink = ink });
        }
    }

    private void EmitFace(Item it, IList<V3> poly, Shade shade, Ink ink)
    {
        var p = Pj(it, poly);
        it.Ops.Add(new FillOp { Pts = p, Shade = shade });
        it.Ops.Add(new StrokeOp { Pts = p, Closed = true, Ink = ink });
    }

    private static long R(double v) => (long)Math.Round(v * 100);

    /// <summary>Цилиндр: основание в c, ось axis (длина = высота). Видимый торец и силуэт.</summary>
    public void Cyl(V3 c, V3 axis, double r, int seg = 40, Shade? capShade = null, Shade? bodyShade = null, int bias = 0, Ink ink = Ink.Outline, bool capOutline = true)
    {
        double h = axis.Length; var ax = axis / h;
        var (u, v) = Basis(ax);
        var c0 = new List<V3>(seg); var c1 = new List<V3>(seg);
        for (int i = 0; i < seg; i++)
        {
            double t = 2 * Math.PI * i / seg;
            var p = c + (u * Math.Cos(t) + v * Math.Sin(t)) * r;
            c0.Add(p); c1.Add(p + ax * h);
        }
        var nTop = WD(ax);
        bool topVis = Facing(nTop), botVis = Facing(-nTop);
        var it = NewItem();
        var p0 = Pj(it, c0); var p1 = Pj(it, c1);
        var all = p0.Concat(p1).ToArray();
        var hull = Hull(all, out var onHull);
        it.Ops.Add(new FillOp { Pts = hull, Shade = bodyShade ?? (Shade)Math.Clamp(1 + bias, 0, 3) });
        var capPts = topVis ? p1 : botVis ? p0 : null;
        int off = topVis ? seg : 0;
        if (capPts != null) it.Ops.Add(new FillOp { Pts = capPts, Shade = capShade ?? ShadeFor(topVis ? nTop : -nTop, bias) });
        it.Ops.Add(new StrokeOp { Pts = hull, Closed = true, Ink = ink });
        if (capPts != null && capOutline)
        {
            // Дуга торца, не совпадающая с силуэтом.
            int start = -1;
            for (int i = 0; i < seg; i++) if (onHull[off + i] && onHull[off + (i + 1) % seg]) { start = i; break; }
            if (start < 0) it.Ops.Add(new StrokeOp { Pts = capPts, Closed = true, Ink = ink });
            else
            {
                List<Point> run = null;
                for (int k = 1; k <= seg; k++)
                {
                    int i = (start + k) % seg, j = (i + 1) % seg;
                    bool skip = onHull[off + i] && onHull[off + j];
                    if (!skip) { run ??= new List<Point> { capPts[i] }; run.Add(capPts[j]); }
                    else if (run != null) { it.Ops.Add(new StrokeOp { Pts = run.ToArray(), Ink = ink }); run = null; }
                }
                if (run != null) it.Ops.Add(new StrokeOp { Pts = run.ToArray(), Ink = ink });
            }
        }
    }

    /// <summary>Трубка постоянного радиуса вдоль ломаной (тепловые трубки, кабели, антенны).</summary>
    public void Tube(IList<V3> path, double radius, Shade shade = Shade.Paper, bool center = true)
    {
        var it = NewItem();
        var p = Pj(it, path);
        it.Ops.Add(new StrokeOp { Pts = p, Tube = radius * 2, TubeShade = shade, Ink = Ink.Outline });
        if (center && radius > 1.2)
            it.Ops.Add(new StrokeOp { Pts = p, Ink = Ink.Fine });
    }

    // ─── detail strokes on the last item ───
    private Item Tgt => merge ?? last ?? Begin();

    public void Line(V3 a, V3 b, Ink ink = Ink.Detail)
    {
        var it = Tgt; (deferred ?? it.Ops).Add(new StrokeOp { Pts = new[] { Pj(it, a), Pj(it, b) }, Ink = ink });
    }
    public void Path(IList<V3> pts, Ink ink = Ink.Detail, bool closed = false)
    {
        if (pts.Count < 2) return;
        var it = Tgt; (deferred ?? it.Ops).Add(new StrokeOp { Pts = Pj(it, pts), Closed = closed, Ink = ink });
    }
    public void FillOnly(IList<V3> pts, Shade shade)
    {
        var it = Tgt; it.Ops.Add(new FillOp { Pts = Pj(it, pts), Shade = shade });
    }
    /// <summary>Заливка + контур на последнем элементе (для мелких деталей на грани).</summary>
    public void Patch(IList<V3> pts, Shade shade, Ink ink = Ink.Detail)
    {
        var it = Tgt; var p = Pj(it, pts);
        if (shade != Shade.None) it.Ops.Add(new FillOp { Pts = p, Shade = shade });
        (deferred ?? it.Ops).Add(new StrokeOp { Pts = p, Closed = true, Ink = ink });
    }

    // ─── face helpers (локальные мм на грани) ───
    public void LineOn(Face f, double u0, double v0, double u1, double v1, Ink ink = Ink.Detail)
    { if (f.Visible) Line(Lw(f.At(u0, v0)), Lw(f.At(u1, v1)), ink); }

    public void RectOn(Face f, double u, double v, double w, double h, Shade shade = Shade.None, Ink ink = Ink.Detail)
    {
        if (!f.Visible) return;
        Patch(new[] { Lw(f.At(u, v)), Lw(f.At(u + w, v)), Lw(f.At(u + w, v + h)), Lw(f.At(u, v + h)) }, shade, ink);
    }
    public void RoundRectOn(Face f, double u, double v, double w, double h, double r, Shade shade = Shade.None, Ink ink = Ink.Detail)
    {
        if (!f.Visible) return;
        Patch(RoundRect2(u, v, w, h, r).Select(p => Lw(f.At(p.X, p.Y))).ToList(), shade, ink);
    }
    public void CircleOn(Face f, double u, double v, double r, Shade shade = Shade.None, Ink ink = Ink.Detail, int seg = 28)
    {
        if (!f.Visible) return;
        var pts = new List<V3>(seg);
        for (int i = 0; i < seg; i++) { double t = 2 * Math.PI * i / seg; pts.Add(Lw(f.At(u + r * Math.Cos(t), v + r * Math.Sin(t)))); }
        Patch(pts, shade, ink);
    }
    public void PolyOn(Face f, IList<Point> uv, Shade shade = Shade.None, Ink ink = Ink.Detail)
    {
        if (!f.Visible) return;
        Patch(uv.Select(p => Lw(f.At(p.X, p.Y))).ToList(), shade, ink);
    }
    public void PathOn(Face f, IList<Point> uv, Ink ink = Ink.Detail, bool closed = false)
    {
        if (!f.Visible) return;
        Path(uv.Select(p => Lw(f.At(p.X, p.Y))).ToList(), ink, closed);
    }
    /// <summary>Параллельные линии по грани: вдоль u (along=true) или вдоль v, шаг step, в пределах прямоугольника.</summary>
    public void LinesOn(Face f, double u, double v, double w, double h, double step, bool alongU, Ink ink = Ink.Fine, double inset = 0)
    {
        if (!f.Visible) return;
        if (alongU)
            for (double t = v + step / 2; t < v + h - 1e-6; t += step) LineOn(f, u + inset, t, u + w - inset, t, ink);
        else
            for (double t = u + step / 2; t < u + w - 1e-6; t += step) LineOn(f, t, v + inset, t, v + h - inset, ink);
    }
    /// <summary>Винт: круг и шлиц (крест) на грани.</summary>
    public void ScrewOn(Face f, double u, double v, double r, bool cross = true)
    {
        if (!f.Visible) return;
        CircleOn(f, u, v, r, Shade.Side, Ink.Detail, 20);
        double k = r * 0.55;
        LineOn(f, u - k, v, u + k, v, Ink.Detail);
        if (cross) LineOn(f, u, v - k, u, v + k, Ink.Detail);
    }
    /// <summary>Текст на грани: (u, v) — левый край базовой линии; высота букв size мм.</summary>
    public void TextOn(Face f, string text, double u, double v, double size, bool bold = false, bool center = false, bool fine = false, string font = "Bahnschrift")
    {
        if (!f.Visible || string.IsNullOrWhiteSpace(text)) return;     // пустая надпись (железо ещё не прочитано) не рисуется
        // Базис надписи без зеркала: «вправо» = U, «вверх» = N × U.
        Text(Lw(f.At(u, v)), WLD(f.U), WLD(f.N.Cross(f.U)), text, size, bold, center, fine, font);
    }

    // Грани хранят мировые координаты; эти функции возвращают их в «локальные» для Line/Path (которые снова применяют frame).
    private V3 Lw(V3 world) => Inverse(world);
    private V3 WLD(V3 worldDir) => InverseDir(worldDir);
    private V3 Inverse(V3 w) { var d = w - fo; return new V3(d.Dot(fx), d.Dot(fy), d.Dot(fz)); }
    private V3 InverseDir(V3 d) => new(d.Dot(fx), d.Dot(fy), d.Dot(fz));

    public void Text(V3 origin, V3 u, V3 v, string text, double size, bool bold = false, bool center = false, bool fine = false, string font = "Bahnschrift")
    {
        var tf = new Typeface(new FontFamily(font), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
        // Размер шрифта так, чтобы заглавные были высотой size.
        double em = size / 0.7;
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, em, Brushes.Black, 1.0);
        double dx = center ? -ft.Width / 2 : 0;
        // center: (origin) — середина надписи; иначе — левый край базовой линии.
        var g = ft.BuildGeometry(new Point(dx, -ft.Baseline + (center ? size / 2 : 0)));
        var it = Tgt;
        var o = Pj(it, origin);
        var pu = Pj(it, origin + u) - o; var pv = Pj(it, origin + v) - o;
        // «Верх» букв должен смотреть вверх на экране, а строка идти слева направо.
        if (pv.Y > 0) { pv = -pv; pu = -pu; }
        // текстовые координаты: x вправо, y вниз  →  P = origin + x·u − y·v
        var m = new Matrix(pu.X, pu.Y, -pv.X, -pv.Y, o.X, o.Y);
        // Переводим точки в плоскость грани вручную: Transform у геометрии потом заменит вид (масштаб под размер).
        var flat = g.GetFlattenedPathGeometry(0.02, ToleranceType.Absolute);
        var pg = new PathGeometry { FillRule = flat.FillRule };
        foreach (var fig in flat.Figures)
        {
            var nf = new PathFigure { StartPoint = m.Transform(fig.StartPoint), IsClosed = fig.IsClosed, IsFilled = true };
            foreach (var seg in fig.Segments)
            {
                if (seg is PolyLineSegment pl) nf.Segments.Add(new PolyLineSegment(pl.Points.Select(m.Transform), true));
                else if (seg is LineSegment ls) nf.Segments.Add(new LineSegment(m.Transform(ls.Point), true));
            }
            pg.Figures.Add(nf);
        }
        pg.Freeze();
        it.Ops.Add(new GlyphOp { Geometry = pg, Fine = fine });
    }

    /// <summary>Габаритный размер сцены (горизонтальный, над рисунком) между двумя точками; текст задаёт вид (SetDimension).</summary>
    public void Dimension(V3 a, int layerA, V3 b, int layerB) =>
        M.Dimension = new DimDef { A = cam.Project(W(a)), B = cam.Project(W(b)), LayerA = layerA, LayerB = layerB };

    /// <summary>Пунктирная линия проекции между слоями (растёт со взрывом), с кружками на концах.</summary>
    public void Proj(V3 a, int layerA, V3 b, int layerB)
    {
        var it = new Item { Part = Part.None, Layer = layerA, Group = group, IsProjection = true, LayerB = layerB, NoHit = true };
        it.A = cam.Project(W(a)); it.B = cam.Project(W(b));
        M.Items.Add(it);
    }

    // ─── math helpers ───
    public static V3 Newell(IList<V3> p)
    {
        double x = 0, y = 0, z = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i]; var b = p[(i + 1) % p.Count];
            x += (a.Y - b.Y) * (a.Z + b.Z); y += (a.Z - b.Z) * (a.X + b.X); z += (a.X - b.X) * (a.Y + b.Y);
        }
        return new V3(x, y, z);
    }

    private static bool IsConvex(IList<V3> p, V3 n)
    {
        int sign = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i]; var b = p[(i + 1) % p.Count]; var c = p[(i + 2) % p.Count];
            double s = (b - a).Cross(c - b).Dot(n);
            if (Math.Abs(s) < 1e-9) continue;
            int sg = Math.Sign(s);
            if (sign == 0) sign = sg; else if (sg != sign) return false;
        }
        return true;
    }

    public static (V3 u, V3 v) Basis(V3 n)
    {
        n = n.Norm;
        var t = Math.Abs(n.Y) < 0.9 ? V3.UY : V3.UX;
        var u = t.Cross(n).Norm; var v = n.Cross(u).Norm;
        return (u, v);
    }

    public static Point[] Hull(Point[] pts, out bool[] onHull)
    {
        int n = pts.Length;
        var idx = Enumerable.Range(0, n).OrderBy(i => pts[i].X).ThenBy(i => pts[i].Y).ToArray();
        var h = new int[2 * n]; int k = 0;
        double Cr(int o, int a, int b) => (pts[a].X - pts[o].X) * (pts[b].Y - pts[o].Y) - (pts[a].Y - pts[o].Y) * (pts[b].X - pts[o].X);
        for (int i = 0; i < n; i++) { while (k >= 2 && Cr(h[k - 2], h[k - 1], idx[i]) <= 1e-9) k--; h[k++] = idx[i]; }
        for (int i = n - 2, t = k + 1; i >= 0; i--) { while (k >= t && Cr(h[k - 2], h[k - 1], idx[i]) <= 1e-9) k--; h[k++] = idx[i]; }
        onHull = new bool[n];
        var res = new Point[Math.Max(0, k - 1)];
        for (int i = 0; i < k - 1; i++) { res[i] = pts[h[i]]; onHull[h[i]] = true; }
        return res;
    }

    /// <summary>Склеить отрезки в ломаные там, где конец одного = начало другого (рисование «пером»).</summary>
    private static IEnumerable<StrokeOp> JoinSegments(List<StrokeOp> segs)
    {
        var res = new List<StrokeOp>();
        var used = new bool[segs.Count];
        static bool Eq(Point a, Point b) => Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6;
        for (int i = 0; i < segs.Count; i++)
        {
            if (used[i]) continue;
            used[i] = true;
            var chain = new List<Point>(segs[i].Pts);
            bool grew = true;
            while (grew)
            {
                grew = false;
                for (int j = 0; j < segs.Count; j++)
                {
                    if (used[j] || segs[j].Ink != segs[i].Ink) continue;
                    var s = segs[j].Pts;
                    if (Eq(chain[^1], s[0])) { chain.Add(s[1]); used[j] = true; grew = true; }
                    else if (Eq(chain[^1], s[1])) { chain.Add(s[0]); used[j] = true; grew = true; }
                }
            }
            bool closed = chain.Count > 3 && Eq(chain[0], chain[^1]);
            if (closed) chain.RemoveAt(chain.Count - 1);
            res.Add(new StrokeOp { Pts = chain.ToArray(), Closed = closed, Ink = segs[i].Ink });
        }
        return res;
    }

    // ─── 2D shape helpers ───
    public static List<Point> RoundRect2(double x, double y, double w, double h, double r, int segPerCorner = 5)
    {
        r = Math.Min(r, Math.Min(w, h) / 2);
        var pts = new List<Point>();
        void Arc(double cx, double cy, double a0)
        {
            for (int i = 0; i <= segPerCorner; i++)
            {
                double a = (a0 + 90.0 * i / segPerCorner) * Math.PI / 180;
                pts.Add(new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
            }
        }
        if (r <= 0.01) return new List<Point> { new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h) };
        Arc(x + w - r, y + r, -90); Arc(x + w - r, y + h - r, 0); Arc(x + r, y + h - r, 90); Arc(x + r, y + r, 180);
        return pts;
    }

    public static List<Point> Circle2(double cx, double cy, double r, int seg = 32, double a0 = 0, double a1 = 360)
    {
        var pts = new List<Point>();
        bool full = Math.Abs(a1 - a0 - 360) < 1e-6;
        int n = full ? seg : seg + 1;
        for (int i = 0; i < n; i++)
        {
            double a = (a0 + (a1 - a0) * i / seg) * Math.PI / 180;
            pts.Add(new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
        }
        return pts;
    }

    public static List<Point> Bezier2(Point a, Point b, Point c, Point d, int seg = 16)
    {
        var pts = new List<Point>(seg + 1);
        for (int i = 0; i <= seg; i++)
        {
            double t = (double)i / seg, u = 1 - t;
            pts.Add(new Point(u * u * u * a.X + 3 * u * u * t * b.X + 3 * u * t * t * c.X + t * t * t * d.X,
                              u * u * u * a.Y + 3 * u * u * t * b.Y + 3 * u * t * t * c.Y + t * t * t * d.Y));
        }
        return pts;
    }

    public static List<V3> Bezier3(V3 a, V3 b, V3 c, V3 d, int seg = 16)
    {
        var pts = new List<V3>(seg + 1);
        for (int i = 0; i <= seg; i++)
        {
            double t = (double)i / seg, u = 1 - t;
            pts.Add(a * (u * u * u) + b * (3 * u * u * t) + c * (3 * u * t * t) + d * (t * t * t));
        }
        return pts;
    }

    /// <summary>Ломаная со скруглёнными углами (для труб и кабелей): радиус r в каждом внутреннем узле.</summary>
    public static List<V3> Rounded(IList<V3> pts, double r, int seg = 8)
    {
        var res = new List<V3> { pts[0] };
        for (int i = 1; i < pts.Count - 1; i++)
        {
            var a = pts[i - 1]; var b = pts[i]; var c = pts[i + 1];
            var d1 = (a - b); var d2 = (c - b);
            double l1 = d1.Length, l2 = d2.Length;
            double rr = Math.Min(r, Math.Min(l1, l2) * 0.5);
            var p1 = b + d1 / l1 * rr; var p2 = b + d2 / l2 * rr;
            res.AddRange(Bezier3(p1, V3.Lerp(p1, b, 0.55), V3.Lerp(p2, b, 0.55), p2, seg));
        }
        res.Add(pts[^1]);
        return res;
    }

    /// <summary>Штриховка плоского многоугольника линиями вдоль dir с шагом step.</summary>
    public void Hatch(IList<V3> poly, V3 dir, double step, Ink ink = Ink.Fine, double phase = 0.5)
    {
        var n = Newell(poly).Norm;
        var du = dir.Norm; var dv = n.Cross(du).Norm;
        var o = poly[0];
        var p2 = poly.Select(p => new Point((p - o).Dot(du), (p - o).Dot(dv))).ToList();
        double vmin = p2.Min(p => p.Y), vmax = p2.Max(p => p.Y);
        for (double c = vmin + step * phase; c < vmax; c += step)
        {
            var xs = new List<double>();
            for (int i = 0; i < p2.Count; i++)
            {
                var a = p2[i]; var b = p2[(i + 1) % p2.Count];
                if ((a.Y <= c && b.Y > c) || (b.Y <= c && a.Y > c))
                    xs.Add(a.X + (c - a.Y) / (b.Y - a.Y) * (b.X - a.X));
            }
            xs.Sort();
            for (int i = 0; i + 1 < xs.Count; i += 2)
                Line(o + du * xs[i] + dv * c, o + du * xs[i + 1] + dv * c, ink);
        }
    }
    public void HatchOn(Face f, IList<Point> uv, double angleDeg, double step, Ink ink = Ink.Fine)
    {
        if (!f.Visible) return;
        double a = angleDeg * Math.PI / 180;
        Hatch(uv.Select(p => Lw(f.At(p.X, p.Y))).ToList(), WLD(f.U * Math.Cos(a) + f.V * Math.Sin(a)), step, ink);
    }

    // ─── composite helpers ───

    /// <summary>
    /// Вентилятор в плоскости (center, u, v): лопасти серпом, ступица с кольцом.
    /// Лопасти рисуются отдельными элементами без перекрытия.
    /// </summary>
    public void Fan(V3 center, V3 u, V3 v, double rHub, double rOut, int blades, double sweep = 38, double rot = 0, double fill = 0.78, int engrave = 3, bool hubCap = true)
    {
        double step = 360.0 / blades;
        for (int k = 0; k < blades; k++)
        {
            double a0 = rot + k * step;
            const int N = 14;
            var lead = new List<Point>(); var trail = new List<Point>();
            for (int i = 0; i <= N; i++)
            {
                double t = (double)i / N;
                double r = rHub * 0.96 + (rOut - rHub * 0.96) * t;
                double w = step * fill * (0.42 + 0.58 * Math.Pow(t, 0.7));     // ширина лопасти по углу: к концу шире
                double ang = a0 + sweep * Math.Pow(t, 1.25);
                lead.Add(Pol(r, ang)); trail.Add(Pol(r, ang - w));
            }
            // кончик: дуга от передней кромки к задней по внешнему радиусу
            var tip = new List<Point>();
            double angT = Math.Atan2(trail[^1].Y, trail[^1].X) * 180 / Math.PI, angL = Math.Atan2(lead[^1].Y, lead[^1].X) * 180 / Math.PI;
            while (angT > angL) angT -= 360;
            for (int i = 1; i < 5; i++) tip.Add(Pol(rOut, angL + (angT - angL) * i / 5));
            var outline = new List<Point>(lead); outline.AddRange(tip);
            for (int i = trail.Count - 1; i >= 0; i--) outline.Add(trail[i]);
            var w3 = outline.Select(p => center + u * p.X + v * p.Y).ToList();
            Poly(w3, Shade.Paper, twoSided: true);
            for (int e = 1; e <= engrave; e++)
            {
                double f = (double)e / (engrave + 1);
                var line = new List<V3>();
                for (int i = 2; i <= N; i++)
                {
                    var l = lead[i]; var tr = trail[i];
                    double t = (double)i / N;
                    var p = new Point(l.X + (tr.X - l.X) * f * (0.4 + 0.6 * t), l.Y + (tr.Y - l.Y) * f * (0.4 + 0.6 * t));
                    line.Add(center + u * p.X + v * p.Y);
                }
                Path(line, Ink.Fine);
            }
        }
        if (hubCap)
        {
            var hub = Circle2(0, 0, rHub, 40).Select(p => center + u * p.X + v * p.Y).ToList();
            Poly(hub, Shade.Paper, twoSided: true);
            Path(Circle2(0, 0, rHub * 0.72, 36).Select(p => center + u * p.X + v * p.Y).ToList(), Ink.Fine, true);
        }

        static Point Pol(double r, double deg) { double a = deg * Math.PI / 180; return new Point(r * Math.Cos(a), r * Math.Sin(a)); }
    }

    public List<V3> Ring(V3 c, V3 u, V3 v, double r, int seg = 40) =>
        Circle2(0, 0, r, seg).Select(p => c + u * p.X + v * p.Y).ToList();
}
