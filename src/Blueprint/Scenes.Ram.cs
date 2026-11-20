using System.Windows;

namespace PulsePC.Blueprint;

internal static partial class Scenes
{
    /// <summary>
    /// Память: столько планок, сколько стоит в слотах (BlueprintLabels.RamSticks; неизвестно — две).
    /// Планки стоят рядом, как в слотах. При разнесении дальние расходятся веером назад и вверх — у каждой
    /// видна верхняя кромка с местом под выноску; с ближней снимается передний радиатор — видны микросхемы и контакты.
    /// Плоскость планки — XY, толщина по Z. Выноски: ram.stick1 — самая дальняя … ram.stickN — ближняя (на снятом радиаторе).
    /// </summary>
    private static Model Ram()
    {
        var specs = BlueprintLabels.RamSticks;
        int n = specs.Count > 0 ? Math.Min(specs.Count, 8) : 2;
        string Spec(int i) => specs.Count > 0 ? specs[Math.Min(i, specs.Count - 1)] ?? "" : BlueprintLabels.RamSpec;

        var d = new Draft(28, 22);
        // Шаг веера на одну планку: чем их больше, тем плотнее, чтобы рисунок оставался крупным.
        V3 step = n switch
        {
            <= 2 => P(-30, 30, -50),
            3 => P(-22, 25, -38),
            4 => P(-17, 21, -30),
            _ => P(-10, 15, -18),
        };
        const double slot = 12;                    // шаг слотов в собранном виде
        var farLayers = new int[n];
        for (int k = n - 1; k >= 1; k--) farLayers[k] = d.NewLayer(step * k);
        int Lb = 0;                                // плата ближней
        int Lh = d.NewLayer(P(36, 58, 60));        // снятый передний радиатор ближней

        // ═════════ дальние планки (собранные), от самой дальней к ближней ═════════
        d.Group = 0;
        for (int k = n - 1; k >= 1; k--)
        {
            int i = n - 1 - k;                     // номер по порядку слотов: 0 — самая дальняя
            double z = -slot * k;
            d.Layer = farLayers[k];
            Spreader(d, z - 0.1, back: true, Spec(i));
            DimmPcb(d, z, chips: false, Spec(i));
            Spreader(d, z + 2.4, back: false, Spec(i));
            // выноска — на левый верхний угол радиатора: его не закрывает планка впереди
            d.Anchor($"ram.stick{i + 1}", P(26, 35, z + 3.4), side: -1);
            if (n > 4) d.NextGroup();
        }

        // ═════════ ближняя планка: плата с микросхемами ═════════
        d.Layer = Lb;
        d.NextGroup();
        Spreader(d, -0.1, back: true, Spec(n - 1));
        DimmPcb(d, 0, chips: true, Spec(n - 1));
        d.Anchor("ram.chips", P(84, 15.5, 2.3));
        d.Anchor("ram.contacts", P(30, 2.2, 1.2));

        // ═════════ снятый передний радиатор ═════════
        d.Layer = Lh;
        d.NextGroup();
        d.Proj(P(1, 6, 2.4), Lb, P(1, 6, 2.4), Lh);
        d.Proj(P(132.35, 6, 2.4), Lb, P(132.35, 6, 2.4), Lh);
        d.Proj(P(132.35, 36, 2.4), Lb, P(132.35, 36, 2.4), Lh);
        Spreader(d, 2.4, back: false, Spec(n - 1));
        d.Anchor($"ram.stick{n}", P(96, 26, 2.6), side: 1);
        // габарит «через все модули»: от левого края самой дальней планки до правого края ближней
        if (n > 1) d.Dimension(P(0, 42, -slot * (n - 1)), farLayers[n - 1], P(133.35, 42, 3.4), Lh);
        else d.Dimension(P(0, 42, 0), Lb, P(133.35, 42, 3.4), Lh);
        return d.M;
    }

    /// <summary>Плата DDR4 DIMM 133,35×31,25 с ключом, боковыми вырезами и контактами; z — задняя плоскость.</summary>
    private static void DimmPcb(Draft d, double z, bool chips, string spec)
    {
        var outline = new List<V3>();
        foreach (var (x, y) in new[] { (0.0, 1.0), (1.0, 0.0), (71.1, 0.0), (71.1, 4.0), (72.6, 4.0), (72.6, 0.0), (132.35, 0.0), (133.35, 1.0),
                                       (133.35, 14.0), (130.9, 14.0), (130.9, 19.0), (133.35, 19.0), (133.35, 31.25), (0.0, 31.25), (0.0, 19.0), (2.45, 19.0), (2.45, 14.0), (0.0, 14.0) })
            outline.Add(P(x, y, z));
        d.Extrude(outline, P(0, 0, 1.2), capShade: Shade.Paper);
        var f = d.MakeFace(P(0, 0, z + 1.2), V3.UX, V3.UY, V3.UZ);
        d.Batch(() =>
        {
            for (double x = 2.2; x < 131.5; x += 1.7)
            {
                if (x > 69.8 && x < 73.6) continue;
                d.RectOn(f, x, 0.4, 0.95, 3.6, Shade.Side2, Ink.Fine);
            }
            d.LineOn(f, 1, 5.2, 132.3, 5.2, Ink.Fine);
        });
        if (!chips) return;
        double zc = z + 1.2;
        d.Sorted(() =>
        {
            for (int i = 0; i < 8; i++)
            {
                double x = i < 4 ? 3 + i * 15 : 75 + (i - 4) * 15;
                var c = d.Box(P(x, 9.5, zc), P(x + 11, 21.5, zc + 1.1), ink: Ink.Detail);
                d.CircleOn(c.Front, 1.2, 10.8, 0.45, Shade.None, Ink.Fine, 8);
                d.TextOn(c.Front, "D9ZFP", 5.5, 6, 1.4, center: true, fine: true);
            }
            // SPD и резисторные сборки
            d.Box(P(64, 22, zc), P(69, 26, zc + 0.8), ink: Ink.Detail);
            d.Merge(() =>
            {
                for (int i = 0; i < 16; i++)
                {
                    double x = 6 + i * 7.7;
                    if (x > 63 && x < 70) continue;
                    d.Box(P(x, 6.4, zc), P(x + 1.6, 7.3, zc + 0.5), ink: Ink.Fine);
                    d.Box(P(x + 2.6, 24, zc), P(x + 4.2, 24.9, zc + 0.5), ink: Ink.Fine);
                }
            });
        });
        d.TextOn(f, spec.Replace("  ", " "), 8, 27.5, 1.6, fine: true);
    }

    /// <summary>Половина радиатора DIMM: фигурный верх, рельефные грани, наклейка. z — плоскость, к которой прилегает.</summary>
    private static void Spreader(Draft d, double z, bool back, string spec)
    {
        double t = 1.0;
        double z0 = back ? z - t : z, z1 = back ? z : z + t;
        var outline = new List<V3>();
        foreach (var (x, y) in new[] { (1.0, 6.0), (132.35, 6.0), (132.35, 36.0), (126.0, 42.0), (84.0, 42.0), (79.0, 38.0), (54.0, 38.0), (49.0, 42.0), (7.0, 42.0), (1.0, 36.0) })
            outline.Add(P(x, y, z0));
        d.Extrude(outline, P(0, 0, z1 - z0));
        if (back) return;
        var f = d.MakeFace(P(0, 0, z1), V3.UX, V3.UY, V3.UZ);
        d.Batch(() =>
        {
            // рельеф: скошенная рамка и диагональные рёбра
            d.PathOn(f, new[] { Q(4, 9), Q(129, 9), Q(129, 34.5), Q(124.5, 39), Q(85, 39), Q(80, 35), Q(53, 35), Q(48, 39), Q(8.5, 39), Q(4, 34.5) }, Ink.Detail, true);
            for (int i = 0; i < 7; i++) d.LineOn(f, 8 + i * 5, 12, 14 + i * 5, 32, Ink.Fine);
            for (int i = 0; i < 7; i++) d.LineOn(f, 120 - i * 5, 12, 126 - i * 5, 32, Ink.Fine);
            d.RectOn(f, 46, 13, 42, 18, Shade.Paper, Ink.Detail);
        });
        d.TextOn(f, BlueprintLabels.RamType, 50, 24, 4.2, bold: true);
        d.TextOn(f, spec, 50, 16.5, 2.2, fine: true);
    }
}
