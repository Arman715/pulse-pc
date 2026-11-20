using System.Windows;

namespace PulsePC.Blueprint;

internal static partial class Scenes
{
    /// <summary>
    /// Видеокарта (RTX 4060 Ti, два вентилятора), разнесённый вид: плата с кронштейном / радиатор с трубками / кожух с вентиляторами.
    /// Карта лежит: X — длина (кронштейн при x = 0), Z — высота карты (разъём PCIe у зрителя), Y — вверх.
    /// </summary>
    private static Model Gpu()
    {
        var d = new Draft(30, 35.26);   // изометрия: ось глубины под 45°
        // Разнесение симметрично: плата уходит вниз, кожух вверх — собранная карта стоит в центре рамки.
        int L0 = d.NewLayer(P(0, -88, 0));
        int L1 = 0;
        int L2 = d.NewLayer(P(0, 90, 0));

        // ═════════ слой 0: плата ═════════
        d.Layer = L0; d.Group = 0;

        // Лапка кронштейна (дальний конец) — раньше платы.
        var tab = d.Box(P(-1.2, -5, -9.2), P(9, 37, -8));
        d.LineOn(tab.Front, 3, 20, 7.5, 20, Ink.Detail);
        d.CircleOn(tab.Front, 5.2, 28, 1.6, Shade.Recess, Ink.Detail, 16);

        // Плата: контур с ножевым разъёмом PCIe x16 и ключом.
        var pcb = XZ(0, 0, 0, 195, 0, 195, 112, 111, 112, 111, 120, 35.1, 120, 35.1, 114, 33.5, 114, 33.5, 120, 22, 120, 22, 112, 0, 112);
        d.Extrude(pcb, P(0, 1.6, 0), capShade: Shade.Paper);
        var top = d.TopAt(1.6);
        d.Batch(() =>
        {
            // контакты разъёма
            for (double x = 23; x < 110.4; x += 1.0)
            {
                if (x > 32.8 && x < 35.6) continue;
                d.RectOn(top, x, 113.6, 0.62, 5.8, Shade.Side2, Ink.Fine);
            }
            // крепёжные отверстия кулера вокруг GPU
            foreach (var (hx, hz) in new[] { (64.0, 30.0), (118.0, 30.0), (64.0, 84.0), (118.0, 84.0) })
            {
                d.CircleOn(top, hx, hz, 3.2, Shade.Side2, Ink.Detail, 22);
                d.CircleOn(top, hx, hz, 1.6, Shade.Recess, Ink.Fine, 16);
            }
            // отверстия по углам платы
            d.CircleOn(top, 188, 6, 1.8, Shade.Recess, Ink.Detail, 16);
            d.CircleOn(top, 188, 104, 1.8, Shade.Recess, Ink.Detail, 16);
            // контур под GPU и шёлкография
            d.RectOn(top, 70, 36, 41.5, 41.5, Shade.None, Ink.Fine);
            d.RectOn(top, 122, 10, 66, 40, Shade.None, Ink.Fine);
        });
        // дорожки: пучки к памяти и к разъёму
        Traces(d, top);
        d.TextOn(top, "PG190", 150, 104, 3.2, fine: true);

        // ─── компоненты ───
        d.NextGroup();
        d.Sorted(() =>
        {
            // разъёмы портов у кронштейна
            foreach (double z in new[] { 8.0, 34.0, 60.0, 86.0 })
            {
                var pb = d.Box(P(0.4, 1.6, z), P(15, 12.6, z + 19));
                d.LinesOn(pb.Top, 2, 2, 11, 15, 2.6, false, Ink.Fine, 0.5);
            }
            // GPU: подложка, кристалл, мелкие конденсаторы
            var sub = d.Box(P(72.25, 1.6, 38.25), P(109.75, 2.8, 75.75));
            d.Batch(() =>
            {
                for (int i = 0; i < 6; i++)
                {
                    d.RectOn(sub.Top, 3 + i * 1.6, 3, 0.9, 1.6, Shade.Side2, Ink.Fine);
                    d.RectOn(sub.Top, 30 + i * 0.9 - 2, 33, 0.6, 1.6, Shade.Side2, Ink.Fine);
                }
            });
            var die = d.Box(P(81.5, 2.8, 50.5), P(100.5, 3.6, 63.5));
            d.TextOn(die.Top, "AD106", 9.5, 6.5, 2.0, center: true, fine: true);

            // видеопамять GDDR6 ×4
            Chip(d, 52, 36, 12, 14, 1.6, 1.2, "D8BZC", 1.6);
            Chip(d, 52, 64, 12, 14, 1.6, 1.2, "D8BZC", 1.6);
            Chip(d, 74, 19, 14, 12, 1.6, 1.2, "D8BZC", 1.6);
            Chip(d, 94, 19, 14, 12, 1.6, 1.2, "D8BZC", 1.6);

            // питание: силовые ключи, дроссели, конденсаторы
            for (int i = 0; i < 6; i++)
            {
                Chip(d, 129 + i * 9.5, 15.5, 5, 6, 1.6, 0.9, null, dot: false);
                Choke(d, 127.5 + i * 9.5, 25, 1.6, 8, 5.6, i % 2 == 0 ? "R22" : null);
            }
            for (int i = 0; i < 5; i++) Cap(d, 133 + i * 10, 41.5, 1.6, 3.1, 7.5);
            Cap(d, 186, 28, 1.6, 3.6, 8.5);
            Cap(d, 186, 40, 1.6, 3.6, 8.5);
            // контроллер питания, BIOS, ретаймер
            Chip(d, 140, 54, 6, 6, 1.6, 0.8, null);
            Chip(d, 28, 86, 6, 5, 1.6, 1.0, null);
            Chip(d, 20, 44, 7, 7, 1.6, 0.9, null);
            // разъём вентиляторов
            d.Box(P(150, 1.6, 98), P(158, 6, 103));
            // россыпь SMD
            Smd(d, 76, 80, 1.6, 9, 3.4, 0);
            Smd(d, 113, 40, 1.6, 9, 0, 3.8, 0.8, 1.6);
            Smd(d, 40, 30, 1.6, 6, 0, 4.2, 0.8, 1.6);
            Smd(d, 126, 62, 1.6, 8, 4.6, 0);
            Smd(d, 126, 70, 1.6, 8, 4.6, 0);
            Smd(d, 160, 88, 1.6, 5, 4, 0, 1.6, 0.8);
            // питание памяти и мелочь в правой части платы
            Choke(d, 34, 70, 1.6, 6.5, 4.5, null);
            Choke(d, 34, 80, 1.6, 6.5, 4.5, null);
            for (int i = 0; i < 3; i++) for (int j = 0; j < 2; j++) Cap(d, 168 + i * 8, 64 + j * 9, 1.6, 2.5, 5.5);
            Chip(d, 140, 80, 7, 7, 1.6, 0.9, null);
            Smd(d, 140, 96, 1.6, 6, 3.2, 0, 1.6, 0.8);
            Smd(d, 166, 100, 1.6, 6, 3.2, 0, 1.6, 0.8);
            Smd(d, 22, 12, 1.6, 5, 0, 3.6, 0.8, 1.6);
            for (int i = 0; i < 3; i++) Cap(d, 28, 16 + i * 8, 1.6, 2.4, 5);
            Chip(d, 118, 92, 5, 5, 1.6, 0.8, null);
        });
        // 8-контактный разъём питания — у дальнего края
        var pw = d.Box(P(160, 1.6, 1.5), P(181, 14, 12.5));
        d.Batch(() =>
        {
            for (int r = 0; r < 2; r++)
                for (int c = 0; c < 4; c++)
                    d.RoundRectOn(pw.Top, 1.6 + c * 4.8, 1.4 + r * 4.6, 3.6, 3.6, 0.6, Shade.Recess, Ink.Fine);
        });
        d.Box(P(168, 14, 12.5), P(173, 15.8, 14.5));   // защёлка

        d.Anchor("gpu.pcb", P(150, 1.6, 96));
        d.Anchor("gpu.die", P(91, 3.6, 57));
        d.Anchor("gpu.vram", P(58, 2.8, 43));
        d.Anchor("gpu.power", P(170.5, 14, 7));

        // ═════════ слой 1: радиатор ═════════
        d.Layer = L1;
        d.NextGroup();
        d.Proj(P(18, 1.6, 110), L0, P(18, 7.5, 110), L1);
        d.Proj(P(232, 1.6, 110), L0, P(232, 7.5, 110), L1);
        d.Proj(P(18, 1.6, 4), L0, P(18, 7.5, 4), L1);

        d.Box(P(64, 3.6, 26), P(118, 7.5, 88), shade: null);  // медное основание
        double[] pz = { 34, 46, 58, 70 };
        // трубки вдоль радиатора (видны в просвете между блоками)
        foreach (var z in pz) d.Tube(new[] { P(18, 9.8, z), P(232, 9.8, z) }, 3.0);
        // загибы трубок за дальним (правым) торцом — до блока B
        foreach (var z in pz) d.Tube(Draft.Rounded(new[] { P(226, 9.8, z), P(240, 9.8, z), P(240, 23, z), P(226, 23, z) }, 6), 3.0);

        FinBlock(d, 18, 108, 4, 110, 7.5, 30);
        // загибы у ближнего торца — после блока A (выходят из видимой грани)
        foreach (var z in pz) d.Tube(Draft.Rounded(new[] { P(18.2, 9.8, z), P(9, 9.8, z), P(9, 21, z), P(18.2, 21, z) }, 5.5), 3.0);
        FinBlock(d, 116, 232, 4, 110, 7.5, 30);
        d.Anchor("gpu.heatsink", P(172, 30, 58));

        // ═════════ слой 2: кожух и вентиляторы ═════════
        d.Layer = L2;
        d.NextGroup();
        d.Proj(P(16, 30, 120), L1, P(16, 30, 120), L2);
        d.Proj(P(230, 30, 120), L1, P(230, 30, 120), L2);
        d.Proj(P(244, 30, 10), L1, P(244, 30, 10), L2);

        const double sy0 = 30, sy1 = 46;   // кожух: низ и верх
        var outline = XZ(sy0, 16, -4, 234, -4, 244, 6, 244, 112, 236, 120, 16, 120);
        d.Extrude(outline, P(0, sy1 - sy0, 0), caps: false);
        var front = d.MakeFace(P(16, sy0, 120), V3.UX, V3.UY, V3.UZ);
        d.Batch(() =>
        {
            // грани-панели на боковой стенке: ломаная канавка
            d.PathOn(front, new[] { Q(0, 5), Q(70, 5), Q(80, 11), Q(150, 11), Q(158, 5), Q(220, 5) }, Ink.Detail);
            d.PathOn(front, new[] { Q(80, 11), Q(80, 16) }, Ink.Fine);
            d.PathOn(front, new[] { Q(150, 11), Q(150, 16) }, Ink.Fine);
            for (int i = 0; i < 9; i++) d.LineOn(front, 86 + i * 7, 2.2, 90 + i * 7, 9, Ink.Fine);
        });
        d.TextOn(front, BlueprintLabels.GpuBrand, 172, 8.4, 3.2, fine: true);
        var fans = new[] { (70.0, 58.0), (184.0, 58.0) };
        const double rOpen = 47;
        var holes = fans.Select(f => (IList<V3>)d.Ring(P(f.Item1, sy1, f.Item2), V3.UX, V3.UZ, rOpen, 72)).ToList();
        var top2 = outline.Select(p => p + P(0, sy1 - sy0, 0)).ToList();
        d.PolyHoles(top2, holes, Shade.Paper);
        var st = d.TopAt(sy1);
        d.Batch(() =>
        {
            // фаска вдоль ближней кромки
            d.PathOn(st, new[] { Q(16, 115), Q(232, 115), Q(239, 108) }, Ink.Fine);
            d.PathOn(st, new[] { Q(16, 1), Q(232, 1), Q(239, 8) }, Ink.Fine);
            // кольца вокруг вентиляторов
            foreach (var (fx, fz) in fans) d.PathOn(st, Draft.Circle2(fx, fz, 51.5, 72), Ink.Detail, true);
            // решётка между вентиляторами
            for (int i = 0; i < 7; i++)
            {
                double z0 = 24 + i * 11;
                d.PolyOn(st, new[] { Q(121, z0 + 2.5), Q(134, z0 - 3.5), Q(134, z0 - 0.5), Q(121, z0 + 5.5) }, Shade.Recess, Ink.Fine);
            }
            d.PathOn(st, new[] { Q(118, 8), Q(118, 108) }, Ink.Fine);
            d.PathOn(st, new[] { Q(137, 8), Q(137, 108) }, Ink.Fine);
            // винты кожуха
            foreach (var (sx, sz) in new[] { (238.0, 14.0), (238.0, 102.0), (120.0, 112.0), (120.0, 4.0) })
            {
                d.CircleOn(st, sx, sz, 2.2, Shade.Side, Ink.Detail, 16);
                d.LineOn(st, sx - 1.2, sz, sx + 1.2, sz, Ink.Fine);
                d.LineOn(st, sx, sz - 1.2, sx, sz + 1.2, Ink.Fine);
            }
        });

        // Торцевая «лапа» кожуха у кронштейна: массивный угловой торец, свисающий ниже кожуха.
        var cap = new List<V3> { P(0, sy1, -6), P(16, sy1, -6), P(16, sy0, -6), P(13, 25, -6), P(0, 25, -6) };
        d.Extrude(cap, P(0, 0, 126));
        var capEnd = d.MakeFace(P(0, 25, -6), V3.UZ, V3.UY, -V3.UX);   // u = z, v = y
        d.Batch(() =>
        {
            d.PathOn(capEnd, new[] { Q(8, 3), Q(30, 3), Q(38, 10), Q(88, 10), Q(96, 3), Q(118, 3) }, Ink.Detail);
            for (int i = 0; i < 5; i++) d.LineOn(capEnd, 44 + i * 9, 12.5, 50 + i * 9, 19, Ink.Fine);
        });
        var capTop = d.MakeFace(P(0, sy1, -6), V3.UX, V3.UZ, V3.UY);
        d.ScrewOn(capTop, 8, 10, 2.2); d.ScrewOn(capTop, 8, 116, 2.2);

        d.NextGroup();
        int k = 0;
        foreach (var (fx, fz) in fans)
        {
            var c = P(fx, sy1, fz);
            // проём: тёмная глубина и нижняя кромка
            d.Poly(d.Ring(c, V3.UX, V3.UZ, rOpen, 72), Shade.Recess, twoSided: true, ink: Ink.Detail);
            d.Path(d.Ring(c + P(0, -8, 0), V3.UX, V3.UZ, rOpen, 72), Ink.Fine, true);
            d.Fan(P(fx, sy1 - 7, fz), V3.UX, V3.UZ, 13.5, 45, 9, 44, k == 0 ? 8 : 26, 0.8, 3, hubCap: false);
            d.Cyl(P(fx, sy1 - 8, fz), P(0, 5, 0), 13.5, 48, capShade: Shade.Paper);
            var hubTop = d.TopAt(sy1 - 3);
            d.CircleOn(hubTop, fx, fz, 9.5, Shade.None, Ink.Fine, 40);
            k++;
        }
        d.Anchor("gpu.fan", P(70, sy1 - 3, 58));

        // ═════════ кронштейн (слой 0, ближний к зрителю — рисуется последним) ═════════
        d.Layer = L0; d.Group = 0;
        var br = new List<V3>();
        foreach (var (z, y) in new[] { (-9.2, 37.0), (-9.2, -5.0), (112.0, -5.0), (118.0, -2.0), (126.0, -2.0), (126.0, 1.0), (112.0, 4.0), (112.0, 37.0) })
            br.Add(P(-1.2, y, z));
        d.Extrude(br, P(1.2, 0, 0));
        var bf = d.MakeFace(P(-1.2, 0, 0), V3.UZ, V3.UY, -V3.UX);   // u = z, v = y
        d.Batch(() =>
        {
            // вентиляционные отверстия
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 15; col++)
                {
                    double u = 2.5 + col * 7.2 + (row % 2) * 3.6, v = 19 + row * 6;
                    if (u > 108) continue;
                    d.CircleOn(bf, u, v, 2.1, Shade.Recess, Ink.Detail, 14);
                }
            // порты: DP, DP, HDMI, DP
            string[] kinds = { "dp", "dp", "hdmi", "dp" };
            for (int i = 0; i < 4; i++)
            {
                double u0 = 8 + i * 26, v0 = 2.6;
                d.RoundRectOn(bf, u0, v0, 19, 9.4, 0.8, Shade.Side, Ink.Detail);
                if (kinds[i] == "dp")
                    d.PolyOn(bf, new[] { Q(u0 + 2, v0 + 2), Q(u0 + 17, v0 + 2), Q(u0 + 17, v0 + 5.6), Q(u0 + 15.2, v0 + 7.4), Q(u0 + 2, v0 + 7.4) }, Shade.Dark, Ink.Detail);
                else
                    d.PolyOn(bf, new[] { Q(u0 + 3.2, v0 + 2), Q(u0 + 15.8, v0 + 2), Q(u0 + 17.2, v0 + 4.6), Q(u0 + 17.2, v0 + 7.4), Q(u0 + 1.8, v0 + 7.4), Q(u0 + 1.8, v0 + 4.6) }, Shade.Dark, Ink.Detail);
                d.RectOn(bf, u0 + 4, v0 + 4.2, 11, 1.4, Shade.Paper, Ink.Fine);
            }
        });

        return d.M;
    }

    /// <summary>Блок рёбер радиатора: рёбра — пластины поперёк длины, видны как частые линии на верхней и передней гранях.</summary>
    private static void FinBlock(Draft d, double x0, double x1, double z0, double z1, double y0, double y1)
    {
        var b = d.Box(P(x0, y0, z0), P(x1, y1, z1));
        double w = x1 - x0, depth = z1 - z0, h = y1 - y0;
        const double pitch = 2.4;
        d.Batch(() =>
        {
            for (double u = pitch; u < w - 0.5; u += pitch)
            {
                d.LineOn(b.Top, u, 0.4, u, depth - 0.4, Ink.Fine);
                d.LineOn(b.Front, u, 0.6, u, h - 0.2, Ink.Fine);
            }
            // зубчатая кромка рёбер сверху спереди
            d.LineOn(b.Front, 0, h - 1.4, w, h - 1.4, Ink.Fine);
            d.LineOn(b.Top, 0, depth - 1.2, w, depth - 1.2, Ink.Fine);
        });
    }

    /// <summary>Пучки дорожек на плате: от кристалла к памяти и к разъёму PCIe (тонкие линии с изломом 45°).</summary>
    private static void Traces(Draft d, Face top)
    {
        void Bundle(Point a, Point mid, Point b, int n, Vector step)
        {
            for (int i = 0; i < n; i++)
            {
                var o = step * i;
                d.PathOn(top, new[] { a + o, mid + o, b + o }, Ink.Fine);
            }
        }
        Bundle(Q(72, 44), Q(68, 44), Q(64.5, 40.5), 4, new Vector(0, 2.2));
        Bundle(Q(72, 64), Q(68, 64), Q(64.5, 67.5), 4, new Vector(0, 2.2));
        Bundle(Q(78, 38), Q(78, 34), Q(80, 31.5), 4, new Vector(2.4, 0));
        Bundle(Q(98, 38), Q(98, 34), Q(100, 31.5), 4, new Vector(2.4, 0));
        Bundle(Q(80, 76), Q(80, 96), Q(70, 106), 6, new Vector(2.2, 0));
        Bundle(Q(110, 50), Q(120, 50), Q(126, 44), 3, new Vector(0, 2.4));
        Bundle(Q(30, 60), Q(40, 60), Q(46, 54), 4, new Vector(0, 2.4));
    }
}
