using System.Windows;
using System.Windows.Media;

namespace PulsePC.Blueprint;

/// <summary>
/// Цвета и толщины линий чертежей (по blueprint-spec.md §2). Хост может поменять любые поля и вызвать
/// <see cref="Apply"/>: каждый BlueprintView перестроит кэш при следующей отрисовке (вызовите InvalidateVisual у видимых).
/// </summary>
public static class BlueprintPalette
{
    // Бумага и тона граней: верх — бумага, боковые грани чуть темнее (плоские тона, без градиентов).
    public static Color Paper = Color.FromRgb(0xF2, 0xEE, 0xE5);
    public static Color Side = Color.FromRgb(0xEC, 0xE7, 0xDC);
    public static Color Side2 = Color.FromRgb(0xE4, 0xDE, 0xD2);
    public static Color Recess = Color.FromRgb(0xD8, 0xD1, 0xC2);   // глубина: отверстия, проёмы
    public static Color Dark = Color.FromRgb(0x45, 0x42, 0x3C);     // прорези портов
    public static Color Ink1 = Color.FromRgb(0x1A, 0x19, 0x17);     // контуры, мелкие детали, текст
    public static Color Ink2 = Color.FromRgb(0x45, 0x42, 0x3C);     // гравировка, штриховка, выноски общего вида
    public static Color Ink3 = Color.FromRgb(0x6F, 0x6A, 0x60);     // линии проекции, подписи
    public static Color Ink4 = Color.FromRgb(0xA8, 0xA1, 0x94);     // невидимые и «призрачные» линии
    public static Color Red = Color.FromRgb(0xC2, 0x3B, 0x22);
    public static Color RedTint = Color.FromRgb(0xF3, 0xDF, 0xD6);
    public static Color Warn = Color.FromRgb(0x95, 0x58, 0x0F);

    public static double OutlineWidth = 1.5;     // видимый контур
    public static double DetailWidth = 0.8;      // рёбра, дорожки, лопасти
    public static double FineWidth = 0.5;        // гравировка и штриховка
    public static double FineOpacity = 0.6;      // гравировка — Ink2 с этой прозрачностью
    public static double GhostWidth = 0.8;       // «призраки» и невидимые линии, штрих 4 3
    public static double ProjectionWidth = 0.8;  // линии проекции, штрих 5 4, кружки d = 5
    public static double HighlightWidth = 2.0;
    public static double HighlightTintOpacity = 0.7;

    public static double LeaderWidth = 1.25, LeaderDot = 6;           // выноски детализации
    public static double CompactLeaderWidth = 1.0, CompactDot = 5;    // выноски общего вида

    public static string LabelFont = "Segoe UI Variable Text, Segoe UI";
    public static string ValueFont = "Bahnschrift";
    public static string CaptionFont = "Segoe UI Variable Small, Segoe UI";
    public static double LabelSize = 15, ValueSize = 30, CaptionSize = 12;

    /// <summary>Растёт при каждом <see cref="Apply"/>; виды сравнивают его со своим и перестраивают кэш.</summary>
    public static int Version { get; private set; } = 1;

    internal static Brush[] ShadeBrushes;
    internal static Pen OutlinePen, DetailPen, FinePen, GhostPen, ProjPen, CirclePen;
    internal static Brush InkBrush, Ink2Brush, Ink3Brush, RedBrush, TintBrush, WarnBrush, PaperBrush;

    static BlueprintPalette() => Build();

    /// <summary>Применить изменённые поля: пересоздать замороженные кисти и перья.</summary>
    public static void Apply() { Build(); Version++; }

    private static void Build()
    {
        ShadeBrushes = new[] { B(Paper), B(Side), B(Side2), B(Recess), B(Dark), null };
        InkBrush = B(Ink1); Ink2Brush = B(Ink2); Ink3Brush = B(Ink3); RedBrush = B(Red); WarnBrush = B(Warn); PaperBrush = B(Paper);
        TintBrush = B(RedTint);
        OutlinePen = P(InkBrush, OutlineWidth);
        DetailPen = P(InkBrush, DetailWidth);
        var fine = new SolidColorBrush(Ink2) { Opacity = FineOpacity }; fine.Freeze();
        FinePen = P(fine, FineWidth);
        GhostPen = P(B(Ink4), GhostWidth, 4, 3);
        ProjPen = P(Ink3Brush, ProjectionWidth, 5, 4);
        CirclePen = P(Ink3Brush, ProjectionWidth);
    }

    internal static Brush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>Перо; dash/gap — в пикселях (WPF меряет штрих в толщинах пера).</summary>
    internal static Pen P(Brush b, double w, double dash = 0, double gap = 0)
    {
        var p = new Pen(b, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (dash > 0)
        {
            p.DashStyle = new DashStyle(new[] { dash / w, gap / w }, 0);
            p.DashCap = PenLineCap.Flat; p.StartLineCap = PenLineCap.Flat; p.EndLineCap = PenLineCap.Flat;
        }
        p.Freeze();
        return p;
    }
}
