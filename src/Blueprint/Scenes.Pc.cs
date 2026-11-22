using System.Windows;

namespace PulsePC.Blueprint;

internal static partial class Scenes
{
    // Корпус: X — от задней стенки (0) к передней (450), Y — вверх, Z — от правой стенки (0) к снятой левой (215).
    private const double CW = 450, CH = 470, CD = 215;
    // Материнская плата ATX 244×305: u — от края с разъёмами, v — от верхнего края вниз.
    private static double BX(double u) => 22 + u;
    private static double BY(double v) => 455 - v;
    private const double BZ = 10, BS = 11.6;   // плата: тыльная сторона и лицевая поверхность

    /// <summary>Системный блок без боковой крышки, вид спереди-слева-сверху. Детали — горячие зоны.</summary>
    private static Model Pc()
    {
        var d = new Draft(-24.15, 15);   // ось глубины под 30°

        // ═════════ корпус: дальние стенки ═════════
        d.Part = Part.None; d.Group = 0;
        // правая стенка изнутри (основание платы)
        d.Poly(new[] { P(0, 0, 0), P(CW, 0, 0), P(CW, CH, 0), P(0, CH, 0) }, Shade.Side);
        var tray = d.MakeFace(P(0, 0, 0), V3.UX, V3.UY, V3.UZ);
        d.Batch(() =>
        {
            // кабельные проходы с резиновыми шторками
            foreach (double y in new[] { 395.0, 300.0, 205.0 })
            {
                d.RoundRectOn(tray, 282, y, 20, 62, 6, Shade.Recess, Ink.Detail);
                d.LineOn(tray, 292, y + 6, 292, y + 56, Ink.Fine);
            }
            d.RoundRectOn(tray, 30, 112, 70, 18, 6, Shade.Recess, Ink.Detail);
            d.RoundRectOn(tray, 30, 458, 60, 9, 4, Shade.Recess, Ink.Detail);
            // точки для стяжек
            foreach (var (x, y) in new[] { (320.0, 440.0), (320.0, 160.0), (395.0, 300.0), (395.0, 180.0), (395.0, 420.0) })
                d.RectOn(tray, x, y, 6, 3, Shade.None, Ink.Fine);
        });
        // дальние ножки (почти скрыты) и дно
        foreach (double x in new[] { 20.0, 380.0 }) d.Box(P(x, -14, 12), P(x + 50, -3, 48));
        d.Box(P(0, -3, 0), P(CW, 0, CD), ink: Ink.Detail);
        // задняя стенка (видна внутренняя сторона)
        var rear = d.Box(P(0, 0, 0), P(3, CH, CD));
        d.Batch(() =>
        {
            // окно под заглушку разъёмов платы
            d.RectOn(rear.Right, 14, BY(128), 40, 122, Shade.Recess, Ink.Detail);
            // прорези слотов расширения
            for (int i = 0; i < 7; i++)
            {
                double y = BY(158 + i * 20.32) - 13;
                d.RectOn(rear.Right, 22, y, 104, 14, Shade.Side2, Ink.Detail);
                d.CircleOn(rear.Right, 132, y + 7, 2.0, Shade.Side, Ink.Fine, 12);
            }
            // решётка под задним вентилятором
            d.PathOn(rear.Right, Draft.Circle2(100, 395, 58, 48), Ink.Fine, true);
            // отверстия блока питания
            d.RectOn(rear.Right, 30, 6, 150, 84, Shade.Recess, Ink.Fine);
        });

        // ═════════ блок питания в отсеке, кожух ═════════
        d.Group = 0;
        d.Part = Part.Psu;
        var psu = d.Box(P(6, 3, 34), P(166, 89, 184));
        d.Batch(() =>
        {
            // наклейка на боковой стенке
            d.RectOn(psu.Front, 18, 14, 96, 58, Shade.Paper, Ink.Detail);
            d.LinesOn(psu.Front, 24, 20, 54, 30, 5, true, Ink.Fine);
            d.RectOn(psu.Front, 84, 22, 24, 24, Shade.None, Ink.Fine);
            // модульные разъёмы на торце
            for (int r = 0; r < 2; r++)
                for (int c = 0; c < 4; c++)
                    d.RoundRectOn(psu.Right, 18 + c * 30, 16 + r * 24, 22, 12, 2, Shade.Recess, Ink.Fine);
        });
        // Мощность — только указанная человеком: Windows не знает, какой блок питания стоит.
        if (!string.IsNullOrEmpty(BlueprintLabels.Psu)) d.TextOn(psu.Front, BlueprintLabels.Psu, 30, 58, 9, bold: true, fine: true);
        d.Anchor("pc.psu", P(70, 60, 184), -1);

        d.Part = Part.None;
        // кожух БП: верхняя пластина и боковая крышка над отсеком дисков
        var shroudTop = d.Box(P(0, 96, 0), P(440, 100, 213));
        d.Batch(() =>
        {
            d.RoundRectOn(shroudTop.Top, 214, 152, 40, 28, 6, Shade.Recess, Ink.Detail);    // проход кабелей видеокарты
            d.RoundRectOn(shroudTop.Top, 255, 24, 22, 60, 6, Shade.Recess, Ink.Detail);     // проход у платы
            for (int i = 0; i < 12; i++) d.RoundRectOn(shroudTop.Top, 30 + i * 13, 150, 7, 46, 3.5, Shade.Recess, Ink.Fine);
        });
        var cover = d.Box(P(180, 0, 209), P(440, 96, 213));
        d.Batch(() =>
        {
            d.LineOn(cover.Front, 0, 88, 260, 88, Ink.Fine);
            for (int i = 0; i < 16; i++) d.LineOn(cover.Front, 120 + i * 8, 22, 120 + i * 8, 70, Ink.Fine);
        });
        d.TextOn(cover.Front, "PULSE", 30, 40, 8, bold: true, fine: true);

        // ═════════ материнская плата ═════════
        d.Group = 1;
        d.Part = Part.Motherboard;
        var board = d.Box(P(BX(0), BY(305), BZ), P(BX(244), BY(0), BS), ink: Ink.Outline);
        d.Batch(() =>
        {
            // крепёжные отверстия
            foreach (var (u, v) in new[] { (6.0, 10.0), (6.0, 160.0), (6.0, 295.0), (130.0, 10.0), (130.0, 160.0), (238.0, 10.0), (238.0, 160.0), (238.0, 295.0) })
                d.CircleOn(board.Front, u, 305 - v, 2.6, Shade.Side2, Ink.Detail, 14);
            // дорожки и шелкография
            for (int i = 0; i < 5; i++) d.PathOn(board.Front, new[] { Q(150, 305 - 36 - i * 3), Q(160, 305 - 36 - i * 3), Q(166, 305 - 30 - i * 3) }, Ink.Fine);
            d.RectOn(board.Front, 60, 305 - 104, 92, 30, Shade.None, Ink.Fine);
        });
        d.Sorted(() =>
        {
            // кожух разъёмов задней панели
            var io = BB(d, 0, 8, 32, 128, 30, Ink.Outline);
            d.LinesOn(io.Front, 4, 10, 24, 100, 6, true, Ink.Fine);
            // радиаторы питания (VRM): у разъёмов и над сокетом
            var vrm1 = BB(d, 36, 22, 54, 122, 24, Ink.Outline);
            d.LinesOn(vrm1.Front, 0, 0, 18, 100, 4, true, Ink.Fine, 1.5);
            var vrm2 = BB(d, 58, 6, 150, 24, 22, Ink.Outline);
            d.LinesOn(vrm2.Front, 0, 0, 92, 18, 4, false, Ink.Fine, 1.5);
            // сокет LGA1700 с прижимной рамкой
            var sock = BB(d, 80, 47, 140, 97, 5, Ink.Detail);
            d.RectOn(sock.Front, 6, 6, 48, 38, Shade.Side2, Ink.Fine);
            d.Tube(new[] { P(BX(143), BY(50), BS + 2), P(BX(143), BY(98), BS + 2), P(BX(132), BY(100), BS + 2) }, 1.1, center: false);
            // слоты памяти ×4 с защёлками
            foreach (double u in new[] { 165.0, 174.3, 183.6, 192.9 })
            {
                var s = BB(d, u - 3.2, 14, u + 3.2, 155, 8, Ink.Detail);
                d.LineOn(s.Front, 3.2, 4, 3.2, 137, Ink.Fine);
                BB(d, u - 3.6, 8, u + 3.6, 14, 12, Ink.Detail);
            }
            // 24-контактный разъём питания
            var atx = BB(d, 226, 52, 238, 112, 12, Ink.Detail);
            d.Batch(() => { for (int i = 0; i < 12; i++) d.RectOn(atx.Front, 2, 4 + i * 4.6, 8, 3, Shade.Recess, Ink.Fine); });
            // 8-контактный питания процессора
            var eps = BB(d, 8, 2, 30, 8, 12, Ink.Detail);
            // слоты PCIe: x16 (под видеокартой), x1, x16, x1
            Slot(d, 58, 165, 89, true);
            Slot(d, 58, 186, 25, false);
            Slot(d, 58, 226, 89, false);
            Slot(d, 58, 246, 25, false);
            // радиатор чипсета
            var chip = BB(d, 172, 212, 214, 250, 9, Ink.Outline);
            d.LinesOn(chip.Front, 4, 4, 34, 30, 4, true, Ink.Fine);
            d.RectOn(chip.Front, 10, 10, 22, 18, Shade.None, Ink.Detail);
            // SATA и разъёмы нижнего края
            for (int i = 0; i < 4; i++) BB(d, 230, 240 + i * 11, 240, 248 + i * 11, 7, Ink.Detail);
            for (int i = 0; i < 6; i++) BB(d, 30 + i * 30, 294, 48 + i * 30, 302, 7, Ink.Detail);
            // аудио: конденсаторы и кодек
            for (int i = 0; i < 4; i++) d.Cyl(P(BX(12 + (i % 2) * 9), BY(250 + (i / 2) * 12), BS), P(0, 0, 9), 3.2, 20, ink: Ink.Detail);
            BB(d, 20, 270, 32, 282, 2, Ink.Detail);
            // батарейка
            d.Cyl(P(BX(150), BY(282), BS), P(0, 0, 3.2), 10, 32, ink: Ink.Detail);
            // M.2 слот (второй) пустой
            BB(d, 150, 268, 156, 290, 4, Ink.Detail);
            // разъёмы вентиляторов у верхнего края, USB-колодка, мелочь у чипсета
            BB(d, 160, 2, 170, 7, 6, Ink.Detail);
            BB(d, 200, 2, 210, 7, 6, Ink.Detail);
            BB(d, 196, 258, 220, 266, 8, Ink.Detail);
            d.Merge(() =>
            {
                for (int i = 0; i < 6; i++) d.Box(P(BX(150 + i * 4), BY(258), BS), P(BX(152 + i * 4), BY(255), BS + 1), ink: Ink.Fine);
                for (int i = 0; i < 6; i++) d.Box(P(BX(64 + i * 5), BY(38), BS), P(BX(66.5 + i * 5), BY(35), BS + 1), ink: Ink.Fine);
                for (int i = 0; i < 5; i++) d.Box(P(BX(150 + i * 4), BY(110), BS), P(BX(152 + i * 4), BY(107), BS + 1), ink: Ink.Fine);
            });
            for (int i = 0; i < 3; i++) d.Cyl(P(BX(152 + i * 7), BY(122), BS), P(0, 0, 8), 2.6, 18, ink: Ink.Detail);
        });
        d.TextOn(board.Front, BlueprintLabels.Board, 168, 305 - 278, 4.2, bold: true, fine: true);
        // жгут 24-pin к проходу в основании
        d.Part = Part.None;
        var rib = d.Box(P(BX(238), BY(110), BS + 2), P(282, BY(54), BS + 11));
        d.Batch(() => { for (double v = 4.6; v < 56; v += 4.6) d.LineOn(rib.Front, 0, v, 282 - BX(238), v, Ink.Fine); });
        d.Box(P(282, BY(112), 2), P(286, BY(52), BS + 13), ink: Ink.Detail);   // изгиб жгута в проход
        // кабель питания процессора — в верхний вырез
        for (int i = 0; i < 3; i++)
            d.Tube(Draft.Rounded(new[] { P(BX(12 + i * 6), BY(5), BS + 12), P(BX(12 + i * 6), BY(-2), BS + 16), P(48 + i * 6, 463, 4) }, 8), 2.6);
        d.Part = Part.Motherboard;
        d.Anchor("pc.board", P(BX(193), BY(232), BS + 9), +1);

        // ═════════ накопители ═════════
        d.Group = 2;
        d.Part = Part.Storage;
        // Рисуются только те виды накопителей, что стоят в компьютере (список ещё не прочитан — оба, как обычно бывает).
        var kinds = BlueprintLabels.Drives;
        bool hasNvme = kinds.Count == 0 || kinds.Any(k => k.Kind == DriveKind.Nvme);
        bool hasSata = kinds.Count == 0 || kinds.Any(k => k.Kind == DriveKind.Sata25);
        // Только жёсткие диски: на кожухе — 3,5" диск с надписью HDD (иначе на общем виде не было бы ни одного накопителя).
        bool onlyHdd = !hasNvme && !hasSata && kinds.Any(k => k.Kind == DriveKind.Hdd35);
        // NVMe 2280 в нижнем слоте M.2
        BB(d, 56, 266, 62, 288, 5, Ink.Detail);                     // разъём
        if (hasNvme)
        {
            var nvme = d.Box(P(BX(62), BY(288), BS + 3), P(BX(142), BY(266), BS + 3.8), ink: Ink.Detail);
            d.Batch(() => { for (double u = 1; u < 6; u += 0.9) d.LineOn(nvme.Front, u, 3, u, 19, Ink.Fine); });
            NvmeChips(d, BX(62), BY(288), BS + 3.8);
        }
        d.Cyl(P(BX(144), BY(277), BS), P(0, 0, 4.6), 2.6, 16, ink: Ink.Detail);  // стойка с винтом
        // 2,5" SSD на кожухе БП
        if (hasSata || onlyHdd)
        {
            var ssd = d.Box(P(300, 100, 112), P(400, onlyHdd ? 118 : 107, 182), ink: Ink.Outline);
            d.RectOn(ssd.Top, 10, 10, 64, 50, Shade.None, Ink.Detail);
            d.TextOn(ssd.Top, onlyHdd ? "HDD" : "SSD", 42, 35, 9, bold: true, center: true, fine: true);
        }
        // Выноска «Диски» — к тому накопителю, что нарисован: NVMe в слоте или диск на кожухе.
        d.Anchor("pc.storage", hasNvme ? P(BX(96), BY(277), BS + 3.8) : P(350, onlyHdd ? 118 : 107, 147), -1);

        // ═════════ память ═════════
        d.Group = 2;
        d.Part = Part.Ram;
        foreach (double u in new[] { 174.3, 192.9 }) RamStick(d, BX(u), BY(151), BY(18));
        d.Anchor("pc.ram", P(BX(196), BY(40), BS + 38), +1);

        // ═════════ видеокарта ═════════
        d.Group = 3;
        d.Part = Part.Gpu;
        PcGpu(d);
        d.Anchor("pc.gpu", P(110, 262, 129), -1);

        // кабель питания видеокарты — к проходу в кожухе
        d.Part = Part.None;
        d.Tube(Draft.Rounded(new[] { P(176, 282, 129), P(176, 282, 148), P(214, 230, 168), P(228, 140, 168), P(228, 100, 168) }, 22), 3.4);
        d.Tube(Draft.Rounded(new[] { P(184, 282, 129), P(184, 282, 144), P(222, 230, 162), P(236, 140, 162), P(236, 100, 162) }, 22), 3.4);

        // ═════════ кулер процессора ═════════
        d.Group = 4;
        d.Part = Part.Cpu;
        PcCooler(d);
        d.Anchor("pc.cpu", P(135, 410, 163), -1);

        // ═════════ вентиляторы корпуса ═════════
        d.Group = 0;
        d.Part = Part.None;
        // задний (выдув)
        var rf = d.Box(P(3, 337, 40), P(28, 457, 160));
        d.ScrewOn(rf.Front, 5, 5, 2.4, false); d.ScrewOn(rf.Front, 5, 115, 2.4, false);
        d.ScrewOn(rf.Front, 20, 5, 2.4, false); d.ScrewOn(rf.Front, 20, 115, 2.4, false);
        d.Poly(d.Ring(P(28.2, 397, 100), V3.UZ, V3.UY, 57, 48), Shade.Recess, twoSided: true, ink: Ink.Detail);
        d.Fan(P(28.3, 397, 100), V3.UZ, V3.UY, 18, 55, 7, 40, 10, 0.78, 2);
        d.Cyl(P(28.3, 397, 100), P(0.8, 0, 0), 18, 32);
        // передние (вдув): видны рамки сбоку
        foreach (double y0 in new[] { 106.0, 228.0, 350.0 })
        {
            var f = d.Box(P(410, y0, 45), P(435, y0 + 118, 165));
            d.ScrewOn(f.Front, 5, 5, 2.4, false); d.ScrewOn(f.Front, 5, 113, 2.4, false);
            d.ScrewOn(f.Front, 20, 5, 2.4, false); d.ScrewOn(f.Front, 20, 113, 2.4, false);
            d.LineOn(f.Front, 12.5, 8, 12.5, 110, Ink.Fine);
        }

        // ═════════ корпус: ближние части ═════════
        d.Group = 0;
        // передняя панель с сеткой
        var fr = d.Box(P(440, -3, 0), P(CW, CH, CD + 4));
        d.Batch(() =>
        {
            d.HatchOn(fr.Right, new[] { Q(8, 10), Q(CD - 4, 10), Q(CD - 4, CH - 16), Q(8, CH - 16) }, 45, 7, Ink.Fine);
            d.HatchOn(fr.Right, new[] { Q(8, 10), Q(CD - 4, 10), Q(CD - 4, CH - 16), Q(8, CH - 16) }, -45, 7, Ink.Fine);
            d.RectOn(fr.Right, 8, 10, CD - 12, CH - 26, Shade.None, Ink.Detail);
            // вентиляторы за сеткой
            foreach (double yc in new[] { 165.0, 287.0, 409.0 })
            {
                d.PathOn(fr.Right, Draft.Circle2(105, yc, 56, 48), Ink.Detail, true);
                d.PathOn(fr.Right, Draft.Circle2(105, yc, 18, 32), Ink.Detail, true);
            }
        });
        // верхняя панель с сеткой и кнопками
        var top = d.Box(P(0, CH, 0), P(CW, CH + 4, CD + 4));
        d.Batch(() =>
        {
            d.RoundRectOn(top.Top, 40, 30, 330, 160, 4, Shade.Side, Ink.Detail);
            d.HatchOn(top.Top, new[] { Q(44, 34), Q(366, 34), Q(366, 186), Q(44, 186) }, 0, 6, Ink.Fine);
            d.CircleOn(top.Top, 410, 70, 8, Shade.Side, Ink.Detail, 24);
            d.CircleOn(top.Top, 410, 70, 4.5, Shade.None, Ink.Fine, 18);
            d.RoundRectOn(top.Top, 402, 96, 16, 6, 1, Shade.Dark, Ink.Fine);
            d.RoundRectOn(top.Top, 402, 108, 16, 6, 1, Shade.Dark, Ink.Fine);
            d.CircleOn(top.Top, 410, 128, 2.6, Shade.Dark, Ink.Fine, 12);
        });
        // рамка проёма: передняя стойка
        d.Box(P(430, -3, CD - 6), P(440, CH, CD + 4));
        // ножки
        foreach (var (x, z) in new[] { (20.0, 168.0), (380.0, 168.0) })
            d.Box(P(x, -14, z), P(x + 50, -3, z + 36));

        return d.M;
    }

    /// <summary>Брусок на плате в её координатах: u0..u1 по ширине, v0..v1 от верхнего края вниз, высота h над платой.</summary>
    private static BoxH BB(Draft d, double u0, double v0, double u1, double v1, double h, Ink ink = Ink.Detail) =>
        d.Box(P(BX(u0), BY(v1), BS), P(BX(u1), BY(v0), BS + h), ink: ink);

    private static void Slot(Draft d, double u, double v, double len, bool armored)
    {
        var s = BB(d, u, v, u + len, v + 7, armored ? 11 : 9, armored ? Ink.Outline : Ink.Detail);
        d.LineOn(s.Front, 2, 3.5, len - 2, 3.5, Ink.Fine);
        if (len > 30) d.LineOn(s.Front, 11.5, 0, 11.5, 7, Ink.Fine);
        BB(d, u + len, v - 1, u + len + 6, v + 8, 11, Ink.Detail);   // защёлка
    }

    private static void NvmeChips(Draft d, double x0, double y0, double z)
    {
        // контроллер, DRAM, две NAND — на лицевой стороне (смотрит на зрителя: +Z)
        d.Sorted(() =>
        {
            d.Box(P(x0 + 6, y0 + 5, z), P(x0 + 18, y0 + 17, z + 1.2), ink: Ink.Detail);
            d.Box(P(x0 + 22, y0 + 6, z), P(x0 + 30, y0 + 16, z + 1.0), ink: Ink.Detail);
            d.Box(P(x0 + 34, y0 + 3, z), P(x0 + 52, y0 + 19, z + 1.4), ink: Ink.Detail);
            d.Box(P(x0 + 56, y0 + 3, z), P(x0 + 74, y0 + 19, z + 1.4), ink: Ink.Detail);
        });
    }

    /// <summary>Планка памяти в слоте (плоскость YZ), с радиатором и гребнем сверху.</summary>
    private static void RamStick(Draft d, double x, double yBottom, double yTop)
    {
        double z0 = BS + 7, z1 = BS + 40;
        var pcb = d.Box(P(x - 0.7, yBottom + 2, z0), P(x + 0.7, yTop - 2, z1 - 4), ink: Ink.Detail);
        var sp = d.Box(P(x - 3.2, yBottom, z0 + 3), P(x + 3.2, yTop, z1), ink: Ink.Outline);
        d.Batch(() =>
        {
            // рисунок радиатора на видимой стороне (+X): угловые грани и полосы
            double L = yTop - yBottom, Hh = z1 - z0 - 3;
            d.PathOn(sp.Right, new[] { Q(4, 4), Q(Hh - 6, 4), Q(Hh - 2, 18), Q(Hh - 2, L - 30), Q(Hh - 8, L - 6), Q(4, L - 6) }, Ink.Fine, true);
            for (int i = 0; i < 6; i++) d.LineOn(sp.Right, 8, 30 + i * 12, Hh - 8, 36 + i * 12, Ink.Fine);
            // гребень сверху
            for (double v = 6; v < L - 6; v += 6) d.LineOn(sp.Front, 0.6, v, 5.8, v, Ink.Fine);
        });
    }

    /// <summary>Видеокарта в первом слоте x16: снизу кожух, над ним рёбра радиатора, сверху плата и бэкплейт.</summary>
    private static void PcGpu(Draft d)
    {
        double yTop = BY(165) + 1.5;     // верх бэкплейта у слота
        double x0 = 4, x1 = 252;
        // кронштейн на задней стенке
        var br = d.Box(P(3, yTop - 44, 16), P(4.4, yTop + 2, 136));
        d.Batch(() => { for (int i = 0; i < 9; i++) d.CircleOn(br.Right, 18 + i * 11, 30, 2.2, Shade.Recess, Ink.Fine, 10); });
        // кожух (вентиляторы вниз)
        var sh = d.Box(P(x0 + 1, yTop - 44, 13), P(x1, yTop - 30, 131));
        d.Batch(() =>
        {
            d.PathOn(sh.Front, new[] { Q(0, 9), Q(70, 9), Q(80, 3), Q(160, 3), Q(170, 9), Q(x1 - x0 - 1, 9) }, Ink.Detail);
            for (int i = 0; i < 8; i++) d.LineOn(sh.Front, 86 + i * 9, 5, 92 + i * 9, 12, Ink.Fine);
        });
        d.TextOn(sh.Front, BlueprintLabels.GpuBrand, 205, 7, 5, center: true, fine: true);
        // рёбра радиатора между кожухом и платой
        var hs = d.Box(P(x0 + 8, yTop - 30, 16), P(x1 - 4, yTop - 6, 126), ink: Ink.Detail);
        d.Batch(() =>
        {
            for (double u = 3; u < x1 - x0 - 14; u += 3) d.LineOn(hs.Front, u, 0.5, u, 23.5, Ink.Fine);
        });
        // разъём питания 8-pin в вырезе радиатора
        var pw = d.Box(P(168, yTop - 18, 116), P(192, yTop - 6, 129), ink: Ink.Detail);
        d.Batch(() => { for (int r = 0; r < 2; r++) for (int c = 0; c < 4; c++) d.RectOn(pw.Front, 2 + c * 5.6, 2 + r * 5, 3.6, 3.4, Shade.Recess, Ink.Fine); });
        // плата и бэкплейт
        d.Box(P(x0 + 1, yTop - 6, 14), P(206, yTop - 4.4, 126), ink: Ink.Detail);
        var bp = d.Box(P(x0 + 1, yTop - 4.4, 14), P(x1, yTop, 130));
        d.Batch(() =>
        {
            for (int i = 0; i < 14; i++) d.RoundRectOn(bp.Top, 120 + i * 8, 20, 4, 80, 2, Shade.Recess, Ink.Fine);
            d.RectOn(bp.Top, 8, 10, 100, 96, Shade.None, Ink.Fine);
        });
        d.TextOn(bp.Top, BlueprintLabels.Gpu, 58, 58, 8, bold: true, center: true, fine: true);
    }

    /// <summary>Башенный кулер: основание на сокете, трубки, пачка рёбер с крышкой, вентилятор 120 мм спереди.</summary>
    private static void PcCooler(Draft d)
    {
        double cx = BX(110), cy = BY(72);
        // прижимная пластина и основание
        d.Box(P(cx - 26, cy - 8, BS + 5), P(cx + 26, cy + 8, BS + 9), ink: Ink.Detail);
        d.Box(P(cx - 20, cy - 20, BS + 5), P(cx + 20, cy + 20, BS + 14));
        // тепловые трубки от основания к рёбрам
        double[] px = { cx - 15, cx - 5, cx + 5, cx + 15 };
        foreach (var x in px) d.Tube(new[] { P(x, cy, BS + 12), P(x, cy, 52) }, 3.0);
        // пачка рёбер
        double fx0 = cx - 25, fx1 = cx + 25, fy0 = cy - 60, fy1 = cy + 60, fz0 = 50, fz1 = 158;
        var fins = d.Box(P(fx0, fy0, fz0), P(fx1, fy1, fz1));
        d.Batch(() =>
        {
            for (double v = 2.6; v < fz1 - fz0; v += 2.6) d.LineOn(fins.Top, 0.5, v, fx1 - fx0 - 0.5, v, Ink.Fine);
            for (double v = 2.6; v < fz1 - fz0; v += 2.6) d.LineOn(fins.Right, v, 1, v, fy1 - fy0 - 1, Ink.Fine);
        });
        // крышка с торцами трубок
        var cap = d.Box(P(fx0 - 1, fy0 - 1, fz1), P(fx1 + 1, fy1 + 1, fz1 + 5));
        d.Batch(() =>
        {
            foreach (var x in px)
            {
                d.CircleOn(cap.Front, x - fx0 + 1, 22, 3.4, Shade.Side, Ink.Detail, 16);
                d.CircleOn(cap.Front, x - fx0 + 1, 100, 3.4, Shade.Side, Ink.Detail, 16);
            }
            d.RectOn(cap.Front, 8, 40, 36, 42, Shade.None, Ink.Fine);
            d.LinesOn(cap.Front, 12, 46, 28, 30, 5, true, Ink.Fine);
        });
        // вентилятор на передней стороне
        var fan = d.Box(P(fx1, fy0, fz0 - 4), P(fx1 + 26, fy1, fz1 + 2));
        d.Poly(d.Ring(P(fx1 + 26.1, cy, (fz0 + fz1) / 2), V3.UZ, V3.UY, 57, 48), Shade.Recess, twoSided: true, ink: Ink.Detail);
        d.Fan(P(fx1 + 26.2, cy, (fz0 + fz1) / 2), V3.UZ, V3.UY, 17, 55, 7, 40, 20, 0.78, 2);
        d.Cyl(P(fx1 + 26.2, cy, (fz0 + fz1) / 2), P(0.8, 0, 0), 17, 32);
        d.ScrewOn(fan.Front, 6, 6, 2.4, false); d.ScrewOn(fan.Front, 6, 114, 2.4, false);
        d.ScrewOn(fan.Front, 20, 6, 2.4, false); d.ScrewOn(fan.Front, 20, 114, 2.4, false);
    }
}
