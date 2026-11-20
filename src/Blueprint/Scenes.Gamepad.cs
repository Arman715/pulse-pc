using System.Windows;

namespace PulsePC.Blueprint;

internal static partial class Scenes
{
    /// <summary>Сглаженный замкнутый контур через точки (Catmull-Rom).</summary>
    private static List<Point> Smooth(IList<Point> p, int seg = 6, bool closed = true)
    {
        var r = new List<Point>();
        int n = p.Count;
        int last = closed ? n : n - 1;
        for (int i = 0; i < last; i++)
        {
            Point p0 = p[closed ? (i - 1 + n) % n : Math.Max(0, i - 1)], p1 = p[i], p2 = p[(i + 1) % n], p3 = p[closed ? (i + 2) % n : Math.Min(n - 1, i + 2)];
            for (int k = 0; k < seg; k++)
            {
                double t = (double)k / seg, t2 = t * t, t3 = t2 * t;
                r.Add(new Point(
                    0.5 * (2 * p1.X + (-p0.X + p2.X) * t + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3),
                    0.5 * (2 * p1.Y + (-p0.Y + p2.Y) * t + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3)));
            }
        }
        if (!closed) r.Add(p[n - 1]);
        return r;
    }

    /// <summary>
    /// Геймпад лежит на столе: сверху (+Y) стики, крестовина, ABXY; на дальней кромке (−Z) бамперы и курки, рукоятки к зрителю.
    /// </summary>
    private static Model Gamepad()
    {
        var d = new Draft(16, 56);
        d.Group = 0;
        // контур корпуса (x, z): правая половина и зеркально левая
        var half = new[] { Q(0, -36), Q(28, -40), Q(52, -42), Q(68, -36), Q(76, -22), Q(78, -4), Q(76, 14), Q(72, 32), Q(68, 48), Q(62, 60), Q(52, 67), Q(42, 65), Q(35, 54), Q(29, 38), Q(18, 27) };
        var ctrl = new List<Point>(half) { Q(0, 23) };
        for (int i = half.Length - 1; i >= 1; i--) ctrl.Add(Q(-half[i].X, half[i].Y));
        var outline = Smooth(ctrl, 6);

        // курки и бамперы за корпусом — раньше корпуса
        foreach (int s in new[] { -1, 1 })
        {
            var trig = Smooth(new[] { Q(s * 30, -40), Q(s * 50, -48), Q(s * 62, -42), Q(s * 56, -30), Q(s * 36, -32) }, 5).Select(p => P(p.X, 16, p.Y)).ToList();
            d.SmoothExtrude(trig, P(0, 12, 0), Shade.Side2);
            var bump = Smooth(new[] { Q(s * 22, -42), Q(s * 44, -49), Q(s * 66, -42), Q(s * 64, -36), Q(s * 44, -42), Q(s * 24, -38) }, 5).Select(p => P(p.X, 10, p.Y)).ToList();
            d.SmoothExtrude(bump, P(0, 9, 0), Shade.Side);
        }
        d.Anchor("pad.trigger", P(50, 28, -47));
        // корпус: скруглённое «брюшко» уже верхней половины, затем верхняя половина
        d.SmoothExtrude(outline.Select(p => P(p.X * 0.93, 0, p.Y * 0.93 + 1.5)).ToList(), P(0, 9, 0), Shade.Side2, cap: false);
        d.SmoothExtrude(outline.Select(p => P(p.X, 8, p.Y)).ToList(), P(0, 14, 0), Shade.Side);
        var top = d.TopAt(22);
        d.Batch(() =>
        {
            var inner = Smooth(ctrl.Select(p => new Point(p.X * 0.86, p.Y * 0.86 - 2)).ToList(), 6);
            d.PathOn(top, inner, Ink.Fine, true);
        });

        d.NextGroup();
        // гнёзда стиков
        foreach (var (x, z) in new[] { (-44.0, -10.0), (22.0, 18.0) })
        {
            d.Poly(d.Ring(P(x, 22, z), V3.UX, V3.UZ, 15, 40), Shade.Recess, twoSided: true, ink: Ink.Detail);
            d.Path(d.Ring(P(x, 18.5, z), V3.UX, V3.UZ, 15, 40), Ink.Fine, true);
        }
        // крестовина
        d.Poly(d.Ring(P(-24, 22, 18), V3.UX, V3.UZ, 16, 40), Shade.Side2, twoSided: true, ink: Ink.Detail);
        var dp = new List<V3>();
        foreach (var (x, z) in new[] { (-4.0, -12.0), (4.0, -12.0), (4.0, -4.0), (12.0, -4.0), (12.0, 4.0), (4.0, 4.0), (4.0, 12.0), (-4.0, 12.0), (-4.0, 4.0), (-12.0, 4.0), (-12.0, -4.0), (-4.0, -4.0) })
            dp.Add(P(-24 + x, 22, 18 + z));
        d.Extrude(dp, P(0, 3, 0));
        var dpt = d.TopAt(25);
        d.Batch(() =>
        {
            d.PolyOn(dpt, new[] { Q(-24, 8), Q(-22, 11), Q(-26, 11) }, Shade.Dark, Ink.Fine);
            d.PolyOn(dpt, new[] { Q(-24, 28), Q(-22, 25), Q(-26, 25) }, Shade.Dark, Ink.Fine);
            d.PolyOn(dpt, new[] { Q(-34, 18), Q(-31, 16), Q(-31, 20) }, Shade.Dark, Ink.Fine);
            d.PolyOn(dpt, new[] { Q(-14, 18), Q(-17, 16), Q(-17, 20) }, Shade.Dark, Ink.Fine);
            d.CircleOn(dpt, -24, 18, 2.4, Shade.None, Ink.Fine, 12);
        });
        d.Anchor("pad.dpad", P(-24, 25, 18));
        // центральные кнопки
        d.Cyl(P(0, 22, -24), P(0, 2, 0), 8, 36);
        var gt = d.TopAt(24);
        d.CircleOn(gt, 0, -24, 5, Shade.None, Ink.Fine, 24);
        d.LineOn(gt, -3.2, -27.2, 3.2, -20.8, Ink.Fine); d.LineOn(gt, 3.2, -27.2, -3.2, -20.8, Ink.Fine);
        foreach (double x in new[] { -13.0, 13.0 }) d.Cyl(P(x, 22, -6), P(0, 1.6, 0), 3.4, 20);
        d.Cyl(P(0, 22, 4), P(0, 1.4, 0), 2.6, 16);
        // ABXY
        d.Sorted(() =>
        {
            foreach (var (x, z, l) in new[] { (44.0, -21.0, "Y"), (55.0, -10.0, "B"), (33.0, -10.0, "X"), (44.0, 1.0, "A") })
            {
                d.Cyl(P(x, 22, z), P(0, 3.4, 0), 5.6, 28);
                d.TextOn(d.TopAt(25.4), l, x, z, 4.2, bold: true, center: true);
            }
        });
        d.Anchor("pad.buttons", P(44, 25.4, -10));

        // стики
        d.NextGroup();
        foreach (var (x, z) in new[] { (-44.0, -10.0), (22.0, 18.0) })
        {
            d.Cyl(P(x, 18.5, z), P(0, 8.5, 0), 6, 28, bodyShade: Shade.Side2);
            d.Cyl(P(x, 27, z), P(0, 3.5, 0), 10.5, 40);
            var st = d.TopAt(30.5);
            d.CircleOn(st, x, z, 8.2, Shade.Side, Ink.Fine, 36);
            d.Batch(() =>
            {
                for (int k = 0; k < 16; k++)
                {
                    double a = k * Math.PI / 8;
                    d.LineOn(st, x + Math.Cos(a) * 8.6, z + Math.Sin(a) * 8.6, x + Math.Cos(a) * 10, z + Math.Sin(a) * 10, Ink.Fine);
                }
            });
        }
        d.Anchor("pad.stick", P(-44, 30.5, -10));
        return d.M;
    }
}
