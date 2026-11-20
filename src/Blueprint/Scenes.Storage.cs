using System.Windows;

namespace PulsePC.Blueprint;

internal static partial class Scenes
{
    /// <summary>
    /// Накопители: если известен только один NVMe (или список ещё не прочитан) — его подробный разнесённый вид;
    /// иначе все физические накопители стопкой, каждый своим чертежом (NVMe, 2,5″ SATA, 3,5″ HDD).
    /// </summary>
    private static Model Storage()
    {
        var drives = BlueprintLabels.Drives;
        return drives.Count == 0 || drives.Count == 1 && drives[0].Kind == DriveKind.Nvme ? NvmeExploded() : DriveStack(drives);
    }

    /// <summary>Габарит накопителя, мм: длина (X), толщина (Y), ширина (Z).</summary>
    private static (double W, double T, double D) DriveSize(DriveKind k) => k switch
    {
        DriveKind.Hdd35 => (147, 26.1, 101.6),
        DriveKind.Sata25 => (100, 7, 69.85),
        _ => (80, 2.2, 22),
    };

    /// <summary>
    /// Все накопители стопкой: в собранном виде лежат друг на друге, при разнесении расходятся по вертикали так,
    /// что верх каждого открыт. Первый в списке — сверху. Выноски disk.0 … disk.N−1 попеременно справа и слева.
    /// </summary>
    private static Model DriveStack(IReadOnlyList<DriveSpec> drives)
    {
        var d = new Draft(30, 30);
        int n = Math.Min(drives.Count, 8);
        // Вертикальная «отдача» корпуса на экране: сколько его видно выше и ниже середины (по проекции камеры).
        static double Reach(double w, double dp) => 0.125 * w + 0.217 * dp;
        var y0 = new double[n]; var y1 = new double[n];        // низ в собранном и в разнесённом виде
        double ya = 0, yb = 0;
        for (int i = n - 1; i >= 0; i--)
        {
            var s = DriveSize(drives[i].Kind);
            y0[i] = ya; y1[i] = yb;
            if (i == 0) break;
            var up = DriveSize(drives[i - 1].Kind);
            ya += s.T + 3;
            // верхний может немного заходить на заднюю часть крышки нижнего: надписи и выноски — ближе к переднему краю
            yb += s.T + ((Reach(s.W, s.D) + Reach(up.W, up.D)) * 0.8 + 6) / 0.866;
        }
        var layers = new int[n];
        for (int i = 0; i < n; i++) layers[i] = y1[i] - y0[i] > 1e-6 ? d.NewLayer(P(0, y1[i] - y0[i], 0)) : 0;

        // снизу вверх: верхний рисуется последним и закрывает нижний
        d.Group = 0;
        for (int i = n - 1; i >= 0; i--)
        {
            var spec = drives[i];
            var (w, t, dp) = DriveSize(spec.Kind);
            d.Layer = layers[i];
            if (i < n - 1)
            {
                // ось сборки: от середины крышки нижнего к середине днища этого
                var below = DriveSize(drives[i + 1].Kind);
                d.Proj(P(0, y0[i + 1] + below.T, 0), layers[i + 1], P(0, y0[i], 0), layers[i]);
            }
            d.Push(P(-w / 2, y0[i], -dp / 2));
            switch (spec.Kind)
            {
                case DriveKind.Hdd35: Hdd(d, spec.Label); break;
                case DriveKind.Sata25: Ssd25(d, spec.Label); break;
                default: NvmeStick(d, spec.Label); break;
            }
            bool right = i % 2 == 0;
            d.Anchor($"disk.{i}", P(right ? w * 0.8 : w * 0.18, t, dp * 0.55), right ? 1 : -1);
            d.Pop();
            d.NextGroup();
        }
        return d.M;
    }

    /// <summary>Жёсткий диск 3,5″: литой корпус, крышка с выпуклостью пластин, наклейка, разъёмы SATA на торце.</summary>
    private static void Hdd(Draft d, string label)
    {
        const double W = 147, T = 26.1, D = 101.6;
        var b = d.Box(P(0, 0, 0), P(W, T, D));
        d.Batch(() =>
        {
            d.LineOn(b.Front, 0, T - 3.2, W, T - 3.2, Ink.Fine);             // шов крышки
            foreach (double u in new[] { 28.5, 70.0, 130.1 }) d.CircleOn(b.Front, u, 6.35, 1.7, Shade.Recess, Ink.Fine, 14);
            d.LineOn(b.Left, 0, T - 3.2, D, T - 3.2, Ink.Fine);
            d.RectOn(b.Left, 12, 3, 13, 5, Shade.Dark, Ink.Fine);               // данные, 7 контактов
            d.RectOn(b.Left, 28, 3, 23, 5, Shade.Dark, Ink.Fine);               // питание, 15 контактов
            for (int i = 0; i < 4; i++) d.RectOn(b.Left, 58 + i * 2.6, 4, 1.2, 2.4, Shade.Side2, Ink.Fine);   // перемычки
        });
        var top = b.Top;
        d.Batch(() =>
        {
            d.PathOn(top, new[] { Q(3, 3), Q(W - 3, 3), Q(W - 3, D - 3), Q(3, D - 3) }, Ink.Fine, true);
            d.CircleOn(top, 98, D / 2, 45, Shade.None, Ink.Fine, 56);           // пластины под крышкой
            d.CircleOn(top, 98, D / 2, 9, Shade.Side, Ink.Detail, 24);          // шпиндель
            for (int i = 0; i < 6; i++)
            {
                double a = i * Math.PI / 3;
                d.CircleOn(top, 98 + 5.6 * Math.Cos(a), D / 2 + 5.6 * Math.Sin(a), 0.9, Shade.None, Ink.Fine, 8);
            }
            d.CircleOn(top, 128, 86, 6, Shade.Side, Ink.Detail, 20);            // ось блока головок
            foreach (var (u, v) in new[] { (6.0, 6.0), (W - 6, 6.0), (6.0, D - 6), (W - 6, D - 6), (W / 2, 6.0), (W / 2, D - 6) })
                d.CircleOn(top, u, v, 1.5, Shade.Recess, Ink.Fine, 10);
        });
        // наклейка поверх части крышки
        d.RectOn(top, 8, 12, 62, 78, Shade.Paper, Ink.Detail);
        d.Batch(() => { for (int i = 0; i < 22; i++) d.LineOn(top, 12 + i * 1.3 + (i % 3) * 0.3, 18, 12 + i * 1.3 + (i % 3) * 0.3, 30, Ink.Fine); });
        d.TextOn(top, label, 12, 76, 5, bold: true);
        d.TextOn(top, "3.5\" HDD  SATA", 12, 84, 2.4, fine: true);
    }

    /// <summary>Твердотельный 2,5″ SATA: плоский корпус 100×70×7, наклейка, разъёмы на торце.</summary>
    private static void Ssd25(Draft d, string label)
    {
        const double W = 100, T = 7, D = 69.85;
        var b = d.Box(P(0, 0, 0), P(W, T, D));
        d.Batch(() =>
        {
            d.LineOn(b.Front, 0, T / 2, W, T / 2, Ink.Fine);                    // шов двух половин корпуса
            foreach (double u in new[] { 14.0, 90.6 }) d.CircleOn(b.Front, u, T / 2, 1.2, Shade.Recess, Ink.Fine, 12);
            d.LineOn(b.Left, 0, T / 2, D, T / 2, Ink.Fine);
            d.RectOn(b.Left, 10, 1.4, 12, 3.6, Shade.Dark, Ink.Fine);
            d.RectOn(b.Left, 25, 1.4, 21, 3.6, Shade.Dark, Ink.Fine);
        });
        var top = b.Top;
        d.Batch(() => d.PathOn(top, new[] { Q(2, 2), Q(W - 2, 2), Q(W - 2, D - 2), Q(2, D - 2) }, Ink.Fine, true));
        d.RectOn(top, 8, 8, 84, D - 16, Shade.Paper, Ink.Detail);
        d.Batch(() => { for (int i = 0; i < 18; i++) d.LineOn(top, 13 + i * 1.2 + (i % 3) * 0.3, 14, 13 + i * 1.2 + (i % 3) * 0.3, 22, Ink.Fine); });
        d.TextOn(top, label, 13, 47, 5, bold: true);
        d.TextOn(top, "2.5\" SSD  SATA", 13, 54, 2.2, fine: true);
    }

    /// <summary>NVMe M.2 2280: плата с ключом и контактами, контроллер, память и наклейка на чипах NAND.</summary>
    private static void NvmeStick(Draft d, string label)
    {
        var outline = new List<V3>();
        outline.AddRange(XZ(0, 0, 0, 77.5, 0, 80, 2.5, 80, 9.25));
        foreach (var p in Draft.Circle2(80, 11, 1.75, 10, 270, 90)) outline.Add(P(p.X, 0, p.Y));
        outline.AddRange(XZ(0, 80, 12.75, 80, 19.5, 77.5, 22, 0, 22, 0, 17.4, 4, 17.4, 4, 16.2, 0, 16.2));
        d.Extrude(outline, P(0, 0.8, 0), capShade: Shade.Paper);
        var top = d.TopAt(0.8);
        d.Batch(() =>
        {
            for (double z = 0.6; z < 21.6; z += 0.75)
            {
                if (z > 15.6 && z < 17.8) continue;
                d.RectOn(top, 0.3, z, 3.2, 0.4, Shade.Side2, Ink.Fine);
            }
        });
        d.Sorted(() =>
        {
            Chip(d, 8, 5, 12, 12, 0.8, 1.1, null);              // контроллер
            Chip(d, 23, 6, 8, 10, 0.8, 1.0, "DRAM", 1.2);
            Chip(d, 4.6, 1.5, 3, 3, 0.8, 0.7, null);
        });
        // наклейка поверх двух чипов NAND
        var st = d.Box(P(34, 0.8, 1.5), P(76, 2.2, 20.5), shade: Shade.Paper);
        d.TextOn(st.Top, label, 3, 8.5, 2.6, bold: true);
        d.TextOn(st.Top, "M.2 2280 NVMe", 3, 13.5, 1.5, fine: true);
    }

    /// <summary>
    /// NVMe M.2 2280, разнесённый вид: винт / радиатор / термопрокладка / плата накопителя / кусок материнской платы со слотом и стойкой.
    /// X — длина (разъём при x = 0), Z — ширина 22 мм.
    /// </summary>
    private static Model NvmeExploded()
    {
        var d = new Draft(30, 35.26);
        int Lb = d.NewLayer(P(0, -40, 0));     // слот на плате
        int L0 = 0;                            // накопитель
        int Lp = d.NewLayer(P(0, 24, 0));      // термопрокладка
        int Lh = d.NewLayer(P(0, 46, 0));      // радиатор
        int Ls = d.NewLayer(P(0, 76, 0));      // винт

        // ═════════ кусок платы со слотом M.2 и стойкой ═════════
        d.Layer = Lb; d.Group = 0;
        var mb = d.Box(P(-14, -4.6, -6), P(90, -3, 28));
        d.Batch(() =>
        {
            for (int i = 0; i < 6; i++) d.PathOn(mb.Top, new[] { Q(2, 1.5 + i * 1.4), Q(40, 1.5 + i * 1.4), Q(43, -1 + i * 1.4) }, Ink.Fine);
            for (int i = 0; i < 4; i++) d.PathOn(mb.Top, new[] { Q(3, 32 - i * 1.4), Q(60, 32 - i * 1.4), Q(62, 34 - i * 1.4) }, Ink.Fine);
            d.CircleOn(mb.Top, 94, 17, 4.5, Shade.None, Ink.Fine, 20);   // метка 2280
            d.CircleOn(mb.Top, 56, 17, 3.2, Shade.None, Ink.Fine, 18);   // метка 2242
        });
        d.TextOn(mb.Top, "M2_1", 30, 31, 3, fine: true);
        d.TextOn(mb.Top, "2242", 50, 13, 1.6, fine: true);
        d.TextOn(mb.Top, "2280", 88, 13, 1.6, fine: true);
        // слот
        var sock = d.Box(P(-9, -3, -1.5), P(1.5, 2.2, 23.5));
        d.Batch(() =>
        {
            d.RectOn(sock.Right, 1.5, 2.6, 22, 1.4, Shade.Dark, Ink.Fine);
            for (double u = 2; u < 23; u += 1.0) d.LineOn(sock.Top, 2, u, 9, u, Ink.Fine);
        });
        // стойка с резьбой
        d.Cyl(P(80, -3, 11), P(0, 3, 0), 2.6, 6 * 4, capShade: Shade.Side);
        var st = d.TopAt(0);
        d.CircleOn(st, 80, 11, 1.2, Shade.Recess, Ink.Fine, 14);
        d.Anchor("ssd.slot", P(-4, 2.2, 11));

        // ═════════ плата накопителя ═════════
        d.Layer = L0;
        d.NextGroup();
        d.Proj(P(0, -3, 0), Lb, P(0, 0, 0), L0);
        d.Proj(P(80, -3, 22), Lb, P(80, 0, 22), L0);
        d.Proj(P(80, 0, 11), L0, P(80, 1.0, 11), Ls);
        var outline = new List<V3>();
        outline.AddRange(XZ(0, 0, 0, 77.5, 0, 80, 2.5, 80, 9.25));
        foreach (var p in Draft.Circle2(80, 11, 1.75, 10, 270, 90)) outline.Add(P(p.X, 0, p.Y));
        outline.AddRange(XZ(0, 80, 12.75, 80, 19.5, 77.5, 22, 0, 22, 0, 17.4, 4, 17.4, 4, 16.2, 0, 16.2));
        d.Extrude(outline, P(0, 0.8, 0), capShade: Shade.Paper);
        var top = d.TopAt(0.8);
        d.Batch(() =>
        {
            for (double z = 0.6; z < 21.6; z += 0.75)
            {
                if (z > 15.6 && z < 17.8) continue;
                d.RectOn(top, 0.3, z, 3.2, 0.4, Shade.Side2, Ink.Fine);
            }
            for (int i = 0; i < 4; i++) d.PathOn(top, new[] { Q(4.5, 7 + i * 1.2), Q(7, 7 + i * 1.2) }, Ink.Fine);
        });
        d.TextOn(top, "M.2 2280 NVMe", 52, 21, 1.4, fine: true);
        d.NextGroup();
        d.Sorted(() =>
        {
            Chip(d, 8, 5, 12, 12, 0.8, 1.1, "E21T", 1.6);           // контроллер
            Chip(d, 23, 6, 8, 10, 0.8, 1.0, "DRAM", 1.2);           // DRAM
            Chip(d, 35, 2, 16, 18, 0.8, 1.3, "NAND", 2.0);          // NAND ×2
            Chip(d, 56, 2, 16, 18, 0.8, 1.3, "NAND", 2.0);
            Chip(d, 4.6, 1.5, 3, 3, 0.8, 0.7, null);                // PMIC
            Smd(d, 21.5, 18, 0.8, 4, 3.0, 0, 1.4, 0.7, 0.4);
            Smd(d, 32.5, 2.5, 0.8, 8, 0, 2.1, 0.7, 1.2, 0.4);
            Smd(d, 74, 4, 0.8, 6, 0, 2.6, 0.7, 1.2, 0.4);
        });
        d.Anchor("ssd.controller", P(14, 1.9, 11));
        d.Anchor("ssd.dram", P(27, 1.8, 11));
        d.Anchor("ssd.nand", P(64, 2.1, 11));

        // ═════════ термопрокладка ═════════
        d.Layer = Lp;
        d.NextGroup();
        var pad = d.Box(P(7, 2.1, 1.5), P(73, 3.1, 20.5), shade: Shade.Side);
        d.HatchOn(pad.Top, new[] { Q(0.5, 0.5), Q(65.5, 0.5), Q(65.5, 18.5), Q(0.5, 18.5) }, 45, 2.2, Ink.Fine);
        d.Anchor("ssd.pad", P(40, 3.1, 11));

        // ═════════ радиатор ═════════
        d.Layer = Lh;
        d.NextGroup();
        d.Proj(P(4, 0.8, 22), L0, P(4, 3.1, 22), Lh);
        d.Proj(P(76, 0.8, 22), L0, P(76, 3.1, 22), Lh);
        d.Proj(P(76, 0.8, 0), L0, P(76, 3.1, 0), Lh);
        var hb = d.Box(P(4, 3.1, 0), P(76, 5.1, 22));
        d.LineOn(hb.Front, 0, 1.0, 72, 1.0, Ink.Fine);
        d.Sorted(() =>
        {
            for (int i = 0; i < 6; i++)
            {
                double z = 1.2 + i * 3.8;
                var r = d.Box(P(6, 5.1, z), P(74, 9.4, z + 1.8), ink: Ink.Detail);
                d.LineOn(r.Top, 0, 0.9, 68, 0.9, Ink.Fine);
            }
        });
        d.Anchor("ssd.heatsink", P(48, 9.4, 13.5));

        // ═════════ винт ═════════
        d.Layer = Ls;
        d.NextGroup();
        d.Cyl(P(80, -2.8, 11), P(0, 3.8, 0), 1.3, 18, ink: Ink.Detail);
        d.Cyl(P(80, 1.0, 11), P(0, 1.7, 0), 2.9, 32, capShade: Shade.Paper);
        var sh = d.TopAt(2.7);
        d.LineOn(sh, 78.6, 11, 81.4, 11, Ink.Detail);
        d.LineOn(sh, 80, 9.6, 80, 12.4, Ink.Detail);
        d.Anchor("ssd.screw", P(80, 2.7, 11));
        return d.M;
    }
}
