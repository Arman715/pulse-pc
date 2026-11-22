using System.Windows;

namespace PulsePC.Blueprint;

/// <summary>
/// Генераторы сцен. Все размеры — в миллиметрах, оси: X — длина, Y — вверх, Z — к зрителю.
/// Модели строятся один раз на процесс и только читаются видом.
/// </summary>
internal static partial class Scenes
{
    private static readonly Dictionary<Scene, Model> cache = new();

    public static Model Get(Scene s)
    {
        if (cache.TryGetValue(s, out var m)) return m;
        m = s switch
        {
            Scene.Pc => Pc(),
            Scene.Gpu => Gpu(),
            Scene.Cpu => Cpu(),
            Scene.Ram => Ram(),
            Scene.Storage => Storage(),
            Scene.Router => Router(),
            Scene.Gamepad => Gamepad(),
            Scene.Laptop => Laptop(),
            _ => new Model(),
        };
        cache[s] = m;
        return m;
    }

    internal static void Reset() => cache.Clear();

    // ─── общие мелочи ───

    private static V3 P(double x, double y, double z) => new(x, y, z);
    private static Point Q(double u, double v) => new(u, v);

    /// <summary>Контур в плоскости XZ на высоте y (для Extrude вверх).</summary>
    private static List<V3> XZ(double y, params double[] xz)
    {
        var r = new List<V3>();
        for (int i = 0; i + 1 < xz.Length; i += 2) r.Add(new V3(xz[i], y, xz[i + 1]));
        return r;
    }

    /// <summary>Плоская микросхема: корпус и (по желанию) маркировка на верхней грани.</summary>
    private static BoxH Chip(Draft d, double x, double z, double w, double depth, double y, double h, string mark = null, double markSize = 0, bool dot = true)
    {
        var b = d.Box(P(x, y, z), P(x + w, y + h, z + depth));
        if (dot) d.CircleOn(b.Top, Math.Min(1.4, w * 0.12), depth - Math.Min(1.4, depth * 0.12), Math.Min(0.55, w * 0.05), Shade.None, Ink.Fine, 10);
        if (mark != null) d.TextOn(b.Top, mark, w / 2, depth / 2, markSize > 0 ? markSize : Math.Min(w, depth) * 0.16, center: true, fine: true);
        return b;
    }

    /// <summary>Ряд мелких SMD-компонентов (одним элементом).</summary>
    private static void Smd(Draft d, double x, double z, double y, int n, double stepX, double stepZ, double w = 1.6, double dpt = 0.8, double h = 0.5)
    {
        d.Merge(() =>
        {
            for (int i = 0; i < n; i++)
                d.Box(P(x + i * stepX, y, z + i * stepZ), P(x + i * stepX + w, y + h, z + i * stepZ + dpt), ink: Ink.Detail);
        });
    }

    /// <summary>Конденсатор: цилиндр с крестовой насечкой и кольцом на торце.</summary>
    private static void Cap(Draft d, double x, double z, double y, double r, double h)
    {
        d.Cyl(P(x, y, z), P(0, h, 0), r, 28);
        var top = d.TopAt(y + h);
        d.CircleOn(top, x, z, r * 0.78, Shade.None, Ink.Fine, 22);
        d.LineOn(top, x - r * 0.5, z, x + r * 0.5, z, Ink.Fine);
        d.LineOn(top, x, z - r * 0.5, x, z + r * 0.5, Ink.Fine);
    }

    /// <summary>Дроссель: квадратный брусок со скруглённой маркировкой.</summary>
    private static void Choke(Draft d, double x, double z, double y, double s, double h, string mark = "R22")
    {
        var b = d.Box(P(x, y, z), P(x + s, y + h, z + s));
        d.RectOn(b.Top, s * 0.12, s * 0.12, s * 0.76, s * 0.76, Shade.None, Ink.Fine);
        if (mark != null) d.TextOn(b.Top, mark, s / 2, s / 2, s * 0.2, center: true, fine: true);
    }
}
