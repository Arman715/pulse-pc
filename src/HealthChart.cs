using System.Windows;
using System.Windows.Media;

namespace PulsePC;

/// <summary>
/// Оценка здоровья ПК по дням: столбик на день (тушью; ниже 75 — охрой, ниже 50 — кармином), день без оценки — короткая риска
/// на базовой линии. Пунктир — уровень 75 («в целом в порядке»). Цвета — из палитры темы.
/// </summary>
public sealed class HealthChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(int?[]), typeof(HealthChart), new FrameworkPropertyMetadata(Array.Empty<int?>(), FrameworkPropertyMetadataOptions.AffectsRender));
    public int?[] Series { get => (int?[])GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }

    protected override Size MeasureOverride(Size available) => new(0, double.IsNaN(Height) ? 44 : Height);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var s = Series ?? Array.Empty<int?>();
        if (w <= 0 || h <= 0 || s.Length == 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        double bottom = Math.Round(h) - 0.5;
        double step = w / s.Length, bar = Math.Max(2, Math.Floor(step * 0.62));
        double y75 = Math.Round(bottom - (h - 2) * 0.75) + 0.5;
        dc.DrawLine(Ink.Pen(Palette.Ink4, 1, dashed: true), new Point(0, y75), new Point(w, y75));
        dc.DrawLine(Ink.Pen(Palette.LineStrong, 1), new Point(0, bottom), new Point(w, bottom));
        for (int i = 0; i < s.Length; i++)
        {
            double x = Math.Round(i * step + (step - bar) / 2);
            if (s[i] is not int v)
            {
                dc.DrawRectangle(Palette.Line, null, new Rect(x, bottom - 2.5, bar, 2));
                continue;
            }
            double top = bottom - Math.Max(1.5, (h - 2) * Math.Clamp(v, 0, 100) / 100.0);
            var brush = v < 50 ? Palette.Bad : v < 75 ? Palette.Warn : Palette.Ink2;
            dc.DrawRectangle(brush, null, new Rect(x, top, bar, bottom - top));
        }
    }
}
