using System.Windows;

namespace PulsePC.Blueprint;

internal static partial class Scenes
{
    /// <summary>
    /// Роутер Wi-Fi, вид сзади-сверху. Задняя стенка (к зрителю, +Z) — порты, сверху — вентиляция и индикаторы,
    /// четыре антенны-лопатки на шарнирах у задней кромки. X — ширина 220, Z — глубина 150.
    /// </summary>
    private static Model Router()
    {
        var d = new Draft(30, 30);
        const double W = 220, Dp = 150;
        d.Group = 0;
        // ножки
        foreach (var (x, z) in new[] { (14.0, 12.0), (190.0, 12.0), (14.0, 128.0), (190.0, 128.0) }) d.Box(P(x, -4, z), P(x + 16, 0, z + 10), ink: Ink.Detail);
        // корпус: боковой профиль (z, y), вытянутый по ширине
        var prof = new List<V3>();
        foreach (var (z, y) in new[] { (0.0, 0.0), (Dp, 0.0), (Dp, 30.0), (Dp - 5, 35.0), (10.0, 27.0), (0.0, 20.0) }) prof.Add(P(0, y, z));
        d.Extrude(prof, P(W, 0, 0));
        // верхняя наклонная грань: от (y 27, z 10) до (y 35, z 145)
        var tdir = (P(0, 35, Dp - 5) - P(0, 27, 10)).Norm;
        var topF = d.MakeFace(P(0, 27, 10), V3.UX, tdir, tdir.Cross(V3.UX).Norm);
        double tl = (P(0, 35, Dp - 5) - P(0, 27, 10)).Length;
        d.Batch(() =>
        {
            // вентиляционные прорези
            for (int r = 0; r < 7; r++)
                for (int c = 0; c < 9; c++)
                    d.RoundRectOn(topF, 40 + c * 16, 40 + r * 11, 11, 4, 2, Shade.Recess, Ink.Fine);
            // индикаторы у передней кромки
            for (int i = 0; i < 6; i++) d.CircleOn(topF, 50 + i * 24, 12, 2.2, i == 0 ? Shade.Dark : Shade.Side, Ink.Detail, 14);
            d.LineOn(topF, 30, 22, 190, 22, Ink.Fine);
        });
        d.TextOn(topF, "AX3000", 110, 30, 5, bold: true, center: true, fine: true);
        d.Anchor("router.leds", P(98, 28.5, 22));

        // задняя стенка с портами
        d.NextGroup();
        var back = d.MakeFace(P(0, 0, Dp), V3.UX, V3.UY, V3.UZ);
        d.Batch(() =>
        {
            double[] xs = { 28, 58, 78, 98, 118 };   // WAN и четыре LAN
            foreach (var x in xs)
            {
                d.RectOn(back, x, 8, 16, 14, Shade.Side2, Ink.Detail);
                d.PolyOn(back, new[] { Q(x + 2, 10), Q(x + 14, 10), Q(x + 14, 18), Q(x + 10, 18), Q(x + 10, 20), Q(x + 6, 20), Q(x + 6, 18), Q(x + 2, 18) }, Shade.Dark, Ink.Fine);
            }
            d.RectOn(back, 144, 12, 14, 6, Shade.Side2, Ink.Detail);
            d.RectOn(back, 146, 14, 10, 2, Shade.Dark, Ink.Fine);
            d.CircleOn(back, 170, 15, 3, Shade.Side, Ink.Detail, 16);
            d.CircleOn(back, 182, 15, 1.4, Shade.Dark, Ink.Fine, 10);
            d.CircleOn(back, 200, 15, 5, Shade.Side2, Ink.Detail, 20);
            d.CircleOn(back, 200, 15, 1.6, Shade.Dark, Ink.Fine, 10);
            d.LineOn(back, 51, 4, 51, 26, Ink.Fine);
        });
        d.TextOn(back, "WAN", 36, 4.6, 2.4, center: true, fine: true);
        for (int i = 0; i < 4; i++) d.TextOn(back, (i + 1).ToString(), 66 + i * 20, 4.6, 2.4, center: true, fine: true);
        d.TextOn(back, "USB", 151, 4.6, 2.4, center: true, fine: true);
        d.TextOn(back, "DC", 200, 4.6, 2.4, center: true, fine: true);
        d.Anchor("router.wan", P(36, 22, Dp));
        d.Anchor("router.lan", P(88, 22, Dp));

        // антенны: шарнир у задней кромки, лопатка с наклоном
        d.NextGroup();
        double[] ax = { 22, 80, 140, 198 };
        double[] lean = { -9, -3, 3, 9 };
        for (int i = 0; i < 4; i++)
        {
            var hinge = P(ax[i], 40, Dp - 9);
            d.Box(P(ax[i] - 7, 33, Dp - 15), P(ax[i] + 7, 40, Dp - 3), ink: Ink.Detail);
            d.Cyl(hinge + P(-7, 0, 0), P(14, 0, 0), 5, 24);
            double a = 4 * Math.PI / 180, b = lean[i] * Math.PI / 180;
            var up = new V3(Math.Sin(b), Math.Cos(a) * Math.Cos(b), Math.Sin(a)).Norm;
            var side = up.Cross(V3.UZ).Norm;
            if (side.X < 0) side = -side;
            var depth = side.Cross(up).Norm;
            d.Push(hinge, side, up, depth);
            var paddle = new List<V3>();
            foreach (var (x, y) in new[] { (-6.5, 2.0), (6.5, 2.0), (6.5, 124.0), (3.5, 132.0), (-3.5, 132.0), (-6.5, 124.0) }) paddle.Add(P(x, y, -2.6));
            d.Extrude(paddle, P(0, 0, 5.2));
            var pf = d.MakeFace(P(0, 0, 2.6), V3.UX, V3.UY, V3.UZ);
            d.LineOn(pf, 0, 14, 0, 118, Ink.Fine);
            d.LineOn(pf, -4, 20, 4, 20, Ink.Fine);
            d.Pop();
        }
        d.Anchor("router.antenna", P(146, 110, Dp - 2));
        return d.M;
    }
}
