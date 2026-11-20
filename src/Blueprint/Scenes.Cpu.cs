using System.Windows;

namespace PulsePC.Blueprint;

internal static partial class Scenes
{
    /// <summary>
    /// Процессор, разнесённый вид: башенный кулер (вентилятор выдвинут вперёд) / процессор LGA1700 / сокет с рамкой на куске платы.
    /// Центр сокета в начале координат, Y — вверх.
    /// </summary>
    private static Model Cpu()
    {
        var d = new Draft(30, 35.26);
        int Ls = d.NewLayer(P(0, -78, 0));        // сокет с платой
        int Lc = 0;                               // процессор
        int Lk = d.NewLayer(P(0, 62, 0));         // кулер
        int Lf = d.NewLayer(P(34, 108, 74));        // вентилятор кулера

        // ═════════ плата и сокет ═════════
        d.Layer = Ls; d.Group = 0;
        var pcb = d.Box(P(-66, -1.6, -62), P(66, 0, 62));
        d.Batch(() =>
        {
            foreach (var (x, z) in new[] { (-39.0, -39.0), (39.0, -39.0), (-39.0, 39.0), (39.0, 39.0) })
            {
                d.CircleOn(pcb.Top, x + 66, z + 62, 3.6, Shade.Side2, Ink.Detail, 20);
                d.CircleOn(pcb.Top, x + 66, z + 62, 1.8, Shade.Recess, Ink.Fine, 14);
            }
            for (int i = 0; i < 6; i++) d.PathOn(pcb.Top, new[] { Q(100, 30 + i * 3), Q(112, 30 + i * 3), Q(118, 24 + i * 3) }, Ink.Fine);
            for (int i = 0; i < 5; i++) d.PathOn(pcb.Top, new[] { Q(40 + i * 3, 110), Q(40 + i * 3, 118), Q(34 + i * 3, 124) }, Ink.Fine);
        });
        d.NextGroup();
        d.Sorted(() =>
        {
            // дроссели питания слева и конденсаторы
            for (int i = 0; i < 6; i++) Choke(d, -62, -48 + i * 16, 0, 10, 6, i % 2 == 0 ? "R22" : null);
            for (int i = 0; i < 4; i++) Cap(d, -46, -40 + i * 26, 0, 3.2, 8);
            for (int i = 0; i < 5; i++) Chip(d, 44 + (i % 2) * 8, -54 + i * 10, 5, 5, 0, 0.9, null);
            for (int i = 0; i < 3; i++) Cap(d, 50, 14 + i * 12, 0, 3, 7);
            Chip(d, 38, 40, 9, 9, 0, 1.0, null);
            Smd(d, -30, 42, 0, 10, 5.5, 0, 1.6, 0.8);
            Smd(d, -30, -46, 0, 10, 5.5, 0, 1.6, 0.8);
        });
        // корпус сокета с полем контактов
        var sb = d.Box(P(-31, 0, -27), P(31, 3.2, 27));
        d.Batch(() =>
        {
            d.RectOn(sb.Top, 8, 8.2, 46, 37.6, Shade.Side2, Ink.Detail);
            for (double u = 10; u < 53; u += 1.6) d.LineOn(sb.Top, u, 9.5, u, 44.5, Ink.Fine);
            d.PolyOn(sb.Top, new[] { Q(9, 44.8), Q(13, 44.8), Q(9, 40.8) }, Shade.Dark, Ink.Fine);
        });
        // прижимная рамка с окном
        var frameOuter = XZ(4.2, -29, -25, 29, -25, 29, 25, -29, 25);
        d.Extrude(XZ(3.2, -29, -25, 29, -25, 29, 25, -29, 25), P(0, 1.0, 0), caps: false, ink: Ink.Detail);
        d.PolyHoles(frameOuter, new List<IList<V3>> { XZ(4.2, -23.5, -19.5, 23.5, -19.5, 23.5, 19.5, -23.5, 19.5) }, Shade.Paper);
        var ft = d.TopAt(4.2);
        d.Batch(() =>
        {
            d.CircleOn(ft, -26, -22, 1.6, Shade.Side, Ink.Fine, 10);
            d.CircleOn(ft, 26, -22, 1.6, Shade.Side, Ink.Fine, 10);
            d.CircleOn(ft, 26, 22, 1.6, Shade.Side, Ink.Fine, 10);
        });
        // рычаг
        d.Tube(Draft.Rounded(new[] { P(34, 2.4, -30), P(34, 2.4, 30), P(34, 2.4, 34), P(40, 2.4, 36) }, 3), 1.3);
        d.Box(P(32, 0, -32), P(36, 3.8, -26), ink: Ink.Detail);
        d.Anchor("cpu.socket", P(-20, 4.2, 24));

        // ═════════ процессор ═════════
        d.Layer = Lc;
        d.NextGroup();
        d.Proj(P(-22.5, 3.2, 18.75), Ls, P(-22.5, 4.2, 18.75), Lc);
        d.Proj(P(22.5, 3.2, 18.75), Ls, P(22.5, 4.2, 18.75), Lc);
        d.Proj(P(-22.5, 3.2, -18.75), Ls, P(-22.5, 4.2, -18.75), Lc);
        var sub = d.Box(P(-22.5, 4.2, -18.75), P(22.5, 5.4, 18.75));
        d.Batch(() =>
        {
            // ключи по краям подложки, треугольник первого вывода
            d.RectOn(sub.Front, 8, 0, 2, 1.2, Shade.Recess, Ink.Fine);
            d.RectOn(sub.Front, 35, 0, 2, 1.2, Shade.Recess, Ink.Fine);
            d.PolyOn(sub.Top, new[] { Q(1.4, 36), Q(5, 36), Q(1.4, 32.4) }, Shade.Dark, Ink.Fine);
            for (int i = 0; i < 10; i++) d.RectOn(sub.Top, 9 + i * 2.8, 34.6, 1.3, 1.8, Shade.Side2, Ink.Fine);
            for (int i = 0; i < 6; i++) d.RectOn(sub.Top, 1.2, 8 + i * 3.6, 1.8, 1.3, Shade.Side2, Ink.Fine);
        });
        // крышка (IHS) с юбкой
        d.Box(P(-18, 5.4, -15.4), P(18, 6.2, 15.4), ink: Ink.Detail);
        var ihs = d.Box(P(-16.6, 6.2, -14), P(16.6, 8.4, 14));
        d.TextOn(ihs.Top, "intel", 4, 7.5, 3.4, bold: true);
        d.TextOn(ihs.Top, "CORE i5", 4, 12.8, 2.2, fine: true);
        d.TextOn(ihs.Top, BlueprintLabels.Cpu, 4, 17.2, 2.0, fine: true);
        d.TextOn(ihs.Top, "SRL5Y  3.30GHZ", 4, 21.4, 1.7, fine: true);
        d.TextOn(ihs.Top, "X141J419", 4, 25.2, 1.7, fine: true);
        d.Anchor("cpu.ihs", P(6, 8.4, -2));
        d.Anchor("cpu.package", P(-19, 5.4, 15));

        // ═════════ кулер ═════════
        d.Layer = Lk;
        d.NextGroup();
        d.Proj(P(-20, 8.4, 20), Lc, P(-20, 9, 20), Lk);
        d.Proj(P(20, 8.4, 20), Lc, P(20, 9, 20), Lk);
        d.Proj(P(-20, 8.4, -20), Lc, P(-20, 9, -20), Lk);
        // трубки: U-образные, через основание, вверх в рёбра
        double[] tz = { -11, 0, 11 };
        for (int i = 0; i < 3; i++)
        {
            double xo = 28 + i * 8;
            d.Tube(Draft.Rounded(new[] { P(-xo, 200, tz[i]), P(-xo, 13, tz[i]), P(xo, 13, tz[i]), P(xo, 200, tz[i]) }, 14), 3.0);
        }
        // основание с медной подошвой и крепёжной планкой
        var base0 = d.Box(P(-20, 9, -20), P(20, 13.5, 20));
        d.LinesOn(base0.Front, 0, 0, 40, 4.5, 4, false, Ink.Fine);
        var bar = d.Box(P(-46, 13.5, -6), P(46, 16.5, 6), ink: Ink.Detail);
        d.ScrewOn(bar.Top, 4.5, 6, 3); d.ScrewOn(bar.Top, 87.5, 6, 3);
        d.Box(P(-18, 13.5, -18), P(18, 20, 18));
        // пачка рёбер
        const double fy0 = 42, fy1 = 196;
        var fins = d.Box(P(-62, fy0, -26), P(62, fy1, 26));
        d.Batch(() =>
        {
            for (double v = 2.4; v < fy1 - fy0; v += 2.4)
            {
                d.LineOn(fins.Left, 0.4, v, 51.6, v, Ink.Fine);
                d.LineOn(fins.Front, 0.4, v, 123.6, v, Ink.Fine);
            }
        });
        // крышка
        var cap = d.Box(P(-63, fy1, -27), P(63, fy1 + 5, 27));
        d.Batch(() =>
        {
            d.RectOn(cap.Top, 32, 12, 62, 30, Shade.None, Ink.Detail);
            d.LinesOn(cap.Top, 36, 16, 54, 22, 4.4, true, Ink.Fine);
        });
        for (int i = 0; i < 3; i++)
        {
            double xo = 28 + i * 8;
            foreach (var x in new[] { -xo, xo }) d.Cyl(P(x, fy1 + 5, tz[i]), P(0, 3, 0), 3.0, 20);
        }
        d.Anchor("cpu.cooler", P(-45, fy1 + 5, 0));

        // ═════════ вентилятор (выдвинут вперёд) ═════════
        d.Layer = Lf;
        d.NextGroup();
        d.Proj(P(-60, 60, 26), Lk, P(-60, 60, 28), Lf);
        d.Proj(P(60, 60, 26), Lk, P(60, 60, 28), Lf);
        d.Proj(P(-60, 178, 26), Lk, P(-60, 178, 28), Lf);
        var frame = d.Box(P(-60, 59, 28), P(60, 179, 53));
        var fc = P(0, 119, 53.1);
        d.Batch(() =>
        {
            foreach (var (u, v) in new[] { (7.0, 7.0), (113.0, 7.0), (7.0, 113.0), (113.0, 113.0) })
            {
                d.CircleOn(frame.Front, u, v, 2.6, Shade.Recess, Ink.Detail, 14);
            }
            d.LinesOn(frame.Left, 2, 2, 21, 116, 4, true, Ink.Fine);
        });
        d.Poly(d.Ring(fc, V3.UX, V3.UY, 58, 64), Shade.Recess, twoSided: true, ink: Ink.Detail);
        d.Path(d.Ring(fc + P(0, 0, -8), V3.UX, V3.UY, 58, 64), Ink.Fine, true);
        d.Fan(fc + P(0, 0, -2), V3.UX, V3.UY, 20, 56, 7, 44, 12, 0.8, 3, hubCap: false);
        d.Cyl(fc + P(0, 0, -4), P(0, 0, 4.5), 20, 48, capShade: Shade.Paper);
        var hub = d.MakeFace(fc + P(0, 0, 0.5), V3.UX, V3.UY, V3.UZ);
        d.CircleOn(hub, 0, 0, 13, Shade.None, Ink.Fine, 36);
        d.Anchor("cpu.fan", fc + P(0, 0, 0.5));
        return d.M;
    }
}
