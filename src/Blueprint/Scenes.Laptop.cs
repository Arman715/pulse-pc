using System.Windows;

namespace PulsePC.Blueprint;

internal static partial class Scenes
{
    // Ноутбук: X — ширина (0 — левый край, LW — правый), Y — вверх, Z — от петель (0) к переднему краю (LD).
    // Нижняя половина корпуса без верхней панели с клавиатурой: внутри видны плата, охлаждение, память, накопитель и батарея.
    private const double LW = 356, LD = 246, LWall = 12;
    private const double LBoard = 4, LBoardTop = 5.4;    // плата на стойках: низ и лицевая сторона

    /// <summary>
    /// Ноутбук без верхней панели с клавиатурой, вид спереди-справа-сверху; крышка с экраном открыта.
    /// Те же горячие зоны и точки выносок, что у системного блока («pc.cpu», «pc.gpu»…), — общий вид «Обзора» и осмотр
    /// подписываются одинаково. Блока питания нет: на ноутбуке его роль играет батарея и внешний адаптер.
    /// </summary>
    private static Model Laptop()
    {
        var d = new Draft(-24, 44);

        // ═════════ крышка с экраном (дальняя — рисуется первой) ═════════
        d.Group = 0; d.Part = Part.None;
        double tilt = 16 * Math.PI / 180;                       // открыта на 106°
        var up = new V3(0, Math.Cos(tilt), -Math.Sin(tilt));     // вдоль крышки вверх
        var face = new V3(0, Math.Sin(tilt), Math.Cos(tilt));    // нормаль экрана — к зрителю
        d.Push(P(0, LWall + 1, 9), V3.UX, up, face);
        var lid = d.Box(P(3, 0, -6), P(LW - 3, 228, 0));
        d.Batch(() =>
        {
            d.RectOn(lid.Front, 10, 16, LW - 26, 200, Shade.Recess, Ink.Detail);           // матрица
            // блики на стекле — две косые черты, как на чертежах мониторов
            d.LineOn(lid.Front, 40, 150, 92, 202, Ink.Fine);
            d.LineOn(lid.Front, 58, 150, 110, 202, Ink.Fine);
            d.CircleOn(lid.Front, (LW - 6) / 2, 221, 1.8, Shade.Dark, Ink.Fine, 14);         // камера
            d.RoundRectOn(lid.Front, (LW - 6) / 2 - 22, 4, 44, 6, 3, Shade.None, Ink.Fine);  // резиновый упор
        });
        d.Pop();

        // ═════════ корпус: дно и дальние стенки ═════════
        d.Group = 0;
        var floor = d.Box(P(0, 0, 0), P(LW, 2, LD), ink: Ink.Detail);
        d.Batch(() =>
        {
            // рёбра жёсткости и решётка забора воздуха под вентиляторами
            foreach (double x in new[] { 120.0, 236.0 }) d.LineOn(floor.Top, x, 140, x, LD - 10, Ink.Fine);
        });
        var back = d.Box(P(0, 2, 0), P(LW, LWall, 3));
        var left = d.Box(P(0, 2, 3), P(3, LWall, LD - 3));
        d.Batch(() =>
        {
            // разъёмы на левой стенке (видна изнутри — прорези)
            d.RectOn(left.Right, 40, 3, 12, 5, Shade.Recess, Ink.Fine);
            d.RectOn(left.Right, 58, 3, 9, 4, Shade.Recess, Ink.Fine);
            d.RectOn(left.Right, 72, 3, 9, 4, Shade.Recess, Ink.Fine);
        });
        // петли
        foreach (double x in new[] { 34.0, LW - 74 })
        {
            d.Box(P(x, 2, 3), P(x + 40, LWall + 1, 18), ink: Ink.Detail);
            d.Cyl(P(x - 2, LWall + 1, 9), P(44, 0, 0), 4.2, 20);
        }

        // ═════════ материнская плата ═════════
        d.Group = 1;
        d.Part = Part.Motherboard;
        var board = d.Box(P(16, LBoard, 20), P(LW - 16, LBoardTop, 134), ink: Ink.Outline);
        d.Batch(() =>
        {
            // крепёжные отверстия, дорожки и шелкография
            // координаты на грани — от угла платы (X 16, Z 20)
            foreach (var (u, v) in new[] { (6.0, 6.0), (6.0, 108.0), (318.0, 6.0), (318.0, 108.0), (104.0, 104.0), (204.0, 8.0) })
                d.CircleOn(board.Top, u, v, 2.2, Shade.Side2, Ink.Detail, 12);
            for (int i = 0; i < 5; i++) d.PathOn(board.Top, new[] { Q(160, 80 + i * 3), Q(174, 80 + i * 3), Q(180, 86 + i * 3) }, Ink.Fine);
            d.RectOn(board.Top, 254, 10, 34, 24, Shade.None, Ink.Fine);
        });
        d.TextOn(board.Top, BlueprintLabels.Board, 108, 26, 3.4, bold: true, fine: true);     // между левым вентилятором и процессором
        d.Sorted(() =>
        {
            // чипсет, микросхемы питания, разъём батареи
            Chip(d, 274, 34, 26, 16, LBoardTop, 1.2, "PCH", 3.4);
            foreach (double x in new[] { 110.0, 118.0, 126.0 }) Choke(d, x, 36, LBoardTop, 6, 3.4, null);
            foreach (double x in new[] { 250.0, 258.0 }) Choke(d, x, 36, LBoardTop, 6, 3.4, null);
            var conn = d.Box(P(150, LBoardTop, 124), P(196, LBoardTop + 3.2, 132), ink: Ink.Detail);
            d.Batch(() => { for (int i = 0; i < 9; i++) d.RectOn(conn.Top, 2 + i * 5, 2, 3, 4, Shade.Recess, Ink.Fine); });
            Smd(d, 96, 104, LBoardTop, 8, 3, 0);
            Smd(d, 250, 70, LBoardTop, 6, 0, 3);
        });
        d.Anchor("pc.board", P(LW - 40, LBoardTop, 112), +1);

        // ═════════ батарея ═════════
        d.Group = 1;
        d.Part = Part.None;
        var bat = d.Box(P(36, 2, 142), P(LW - 36, 10, LD - 12), ink: Ink.Outline);
        d.Batch(() =>
        {
            double w = LW - 72;
            for (int i = 1; i < 4; i++) d.LineOn(bat.Top, i * w / 4, 4, i * w / 4, LD - 154 - 4, Ink.Fine);   // банки
            d.RectOn(bat.Top, 18, 18, 96, 46, Shade.Paper, Ink.Detail);
            d.LinesOn(bat.Top, 24, 34, 70, 26, 5, true, Ink.Fine);
        });
        d.TextOn(bat.Top, "Li-ion", 24, 28, 6, bold: true, fine: true);
        // шлейф батареи к плате
        d.Tube(new[] { P(176, 10.4, 142), P(176, 10.4, 136), P(173, LBoardTop + 3.4, 130) }, 1.6, center: false);

        // ═════════ накопитель M.2 ═════════
        d.Group = 2;
        d.Part = Part.Storage;
        d.Box(P(234, LBoardTop, 106), P(240, LBoardTop + 3.4, 128), ink: Ink.Detail);                 // разъём
        var ssd = d.Box(P(240, LBoardTop + 1.2, 106), P(320, LBoardTop + 2.0, 128), ink: Ink.Detail);
        d.Batch(() => { for (double u = 1; u < 6; u += 0.9) d.LineOn(ssd.Top, u, 3, u, 19, Ink.Fine); });
        Chip(d, 250, 110, 14, 14, LBoardTop + 2.0, 1.0, null, 0);
        Chip(d, 270, 109, 20, 16, LBoardTop + 2.0, 1.2, "NAND", 2.6);
        Chip(d, 294, 109, 20, 16, LBoardTop + 2.0, 1.2, "NAND", 2.6);
        d.Cyl(P(322, LBoardTop, 117), P(0, 2.6, 0), 2.4, 14);                                        // винт
        d.Anchor("pc.storage", P(292, LBoardTop + 3.2, 117), +1);

        // ═════════ память SO-DIMM ═════════
        d.Group = 2;
        d.Part = Part.Ram;
        int sticks = Math.Clamp(BlueprintLabels.RamSticks.Count, 1, 2);
        if (BlueprintLabels.RamSticks.Count == 0) sticks = 2;
        d.Sorted(() =>
        {
            for (int i = 0; i < sticks; i++)
            {
                double y = LBoardTop + 1 + i * 4.2, z = 92 + i * 4;
                d.Box(P(100, LBoardTop, z + 26), P(176, y + 0.4, z + 31), ink: Ink.Detail);               // гнездо
                var stick = d.Box(P(104, y, z), P(173.6, y + 1.0, z + 30), ink: Ink.Outline);
                d.Batch(() =>
                {
                    for (int c = 0; c < 4; c++) d.RectOn(stick.Top, 4 + c * 16, 6, 12, 10, Shade.Dark, Ink.Fine);
                    d.LinesOn(stick.Top, 2, 26, 66, 3.4, 1.4, false, Ink.Fine);                         // контакты
                });
                string label = i < BlueprintLabels.RamSticks.Count ? BlueprintLabels.RamSticks[i] : BlueprintLabels.RamSpec;
                d.TextOn(stick.Top, label, 4, 22, 2.2, fine: true);
            }
        });
        d.Anchor("pc.ram", P(120, LBoardTop + 1 + (sticks - 1) * 4.2 + 1, 104), -1);

        // ═════════ охлаждение: вентиляторы, радиаторы, тепловые трубки ═════════
        d.Group = 3;
        d.Part = Part.None;
        foreach (double fx in new[] { 58.0, LW - 58 })
        {
            // радиатор у задней стенки — воздух выдувается назад
            var fins = d.Box(P(fx - 30, LBoardTop, 4), P(fx + 30, 11, 14), ink: Ink.Detail);
            d.Batch(() => d.LinesOn(fins.Top, 0, 0, 60, 10, 2.2, false, Ink.Fine));
            // корпус турбины и крыльчатка
            d.Cyl(P(fx, LBoardTop, 52), P(0, 5.6, 0), 32, 48, bodyShade: Shade.Side2);
            var top = d.TopAt(11);
            d.Poly(d.Ring(P(fx, 11.05, 52), V3.UX, V3.UZ, 21, 40), Shade.Recess, twoSided: true, ink: Ink.Detail);
            d.Fan(P(fx, 11.1, 52), V3.UX, V3.UZ, 8, 20, 11, 30, fx < LW / 2 ? 0 : 14, 0.7, 1);
            foreach (var (u, v) in new[] { (fx - 26.0, 30.0), (fx + 26.0, 30.0), (fx, 82.0) }) d.ScrewOn(top, u, v, 1.6);
        }

        // тепловые трубки: от левого радиатора через процессор и видеокарту к правому
        for (int k = 0; k < 2; k++)
        {
            double dz = k * 6;
            d.Tube(Draft.Rounded(new[] { P(64, 12.6, 10 + dz), P(108, 12.6, 10 + dz), P(128, 12.6, 54 + dz), P(222, 12.6, 54 + dz), P(254, 12.6, 16 + dz), P(LW - 64, 12.6, 10 + dz) }, 12), 2.5);
        }

        // ═════════ процессор ═════════
        d.Group = 4;
        d.Part = Part.Cpu;
        var cpuPlate = d.Box(P(132, LBoardTop, 50), P(166, 10, 84), ink: Ink.Outline);
        d.Batch(() =>
        {
            foreach (var (u, v) in new[] { (3.0, 3.0), (31.0, 3.0), (3.0, 31.0), (31.0, 31.0) }) d.CircleOn(cpuPlate.Top, u, v, 1.6, Shade.Side2, Ink.Fine, 10);
        });
        d.TextOn(cpuPlate.Top, "CPU", 17, 27, 4, bold: true, center: true, fine: true);
        d.Anchor("pc.cpu", P(136, 10, 82), -1);          // у края пластины: надпись «CPU» линия не перечёркивает

        // ═════════ видеокарта (на плате ноутбука — чип и видеопамять) ═════════
        d.Part = Part.Gpu;
        d.Sorted(() =>
        {
            foreach (var (x, z) in new[] { (186.0, 52.0), (226.0, 52.0), (186.0, 92.0), (226.0, 92.0) })
                Chip(d, x, z, 12, 10, LBoardTop, 1.0, null, 0, dot: false);
            var gpuPlate = d.Box(P(196, LBoardTop, 58), P(236, 10, 94), ink: Ink.Outline);
            d.Batch(() =>
            {
                foreach (var (u, v) in new[] { (3.0, 3.0), (37.0, 3.0), (3.0, 33.0), (37.0, 33.0) }) d.CircleOn(gpuPlate.Top, u, v, 1.6, Shade.Side2, Ink.Fine, 10);
            });
            d.TextOn(gpuPlate.Top, "GPU", 20, 26, 4, bold: true, center: true, fine: true);
        });
        d.Anchor("pc.gpu", P(232, 10, 64), +1);

        // ═════════ корпус: ближние стенки ═════════
        d.Group = 0;
        d.Part = Part.None;
        var right = d.Box(P(LW - 3, 2, 3), P(LW, LWall, LD - 3));
        d.Batch(() =>
        {
            // разъёмы правой стенки снаружи: USB-C, USB-A ×2, HDMI, аудио
            d.RoundRectOn(right.Right, 30, 4, 9, 3.4, 1.6, Shade.Recess, Ink.Detail);
            d.RectOn(right.Right, 48, 3.5, 13, 5, Shade.Recess, Ink.Detail);
            d.RectOn(right.Right, 68, 3.5, 13, 5, Shade.Recess, Ink.Detail);
            d.RectOn(right.Right, 90, 3.2, 15, 5.6, Shade.Recess, Ink.Detail);
            d.CircleOn(right.Right, 120, 6, 1.8, Shade.Recess, Ink.Detail, 12);
            d.LinesOn(right.Right, 150, 2.6, 70, 5, 3.2, false, Ink.Fine);                 // вентиляционная решётка
        });
        var front = d.Box(P(0, 2, LD - 3), P(LW, LWall, LD));
        d.Batch(() =>
        {
            d.RoundRectOn(front.Front, LW / 2 - 30, 7, 60, 2.4, 1.2, Shade.Recess, Ink.Fine);      // выемка под палец
            d.CircleOn(front.Front, 24, 6, 1.0, Shade.Dark, Ink.Fine, 10);                          // индикатор заряда
        });
        d.TextOn(front.Front, "PULSE", LW - 60, 4, 3.2, bold: true, fine: true);
        return d.M;
    }
}
