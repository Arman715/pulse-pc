using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using PulsePC.Services;

namespace PulsePC;

/// <summary>
/// Окно оверлея: без рамки, прозрачное, поверх всех, не в панели задач и не в Alt+Tab (WS_EX_TOOLWINDOW), не забирает фокус
/// (WS_EX_NOACTIVATE) и пропускает мышь насквозь (WS_EX_TRANSPARENT | WS_EX_LAYERED). Это обычное окно программы
/// «Пульс ПК» — в процесс игры ничего не встраивается. Где стоять и когда быть видимым, решает служба Overlay.
/// </summary>
public sealed class OverlayWindow : Window
{
    public OverlayView View { get; } = new();
    public IntPtr Handle { get; private set; }

    public OverlayWindow()
    {
        Title = L.T("Пульс ПК — оверлей");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000; Top = -32000;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        // Прозрачное окно Windows рисует без ClearType: сглаживание оттенками серого чище на любой картинке под ним.
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        Content = View;
        SourceInitialized += (_, _) => MakeToolWindow();
    }

    public IntPtr EnsureHandle()
    {
        Handle = new WindowInteropHelper(this).EnsureHandle();
        return Handle;
    }

    private void MakeToolWindow()
    {
        var h = new WindowInteropHelper(this).Handle;
        Handle = h;
        long ex = GetWindowLongPtr(h, GwlExStyle).ToInt64();
        ex |= WsExToolWindow | WsExNoActivate | WsExTransparent | WsExLayered | WsExTopmost;
        ex &= ~WsExAppWindow;
        SetWindowLongPtr(h, GwlExStyle, new IntPtr(ex));
        HwndSource.FromHwnd(h)?.AddHook(Hook);
    }

    // Щелчок не активирует окно, мышь проходит насквозь — даже если Windows всё же спросит.
    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmMouseActivate = 0x21, MaNoActivate = 3, WmNcHitTest = 0x84, HtTransparent = -1;
        if (msg == WmMouseActivate) { handled = true; return new IntPtr(MaNoActivate); }
        if (msg == WmNcHitTest) { handled = true; return new IntPtr(HtTransparent); }
        return IntPtr.Zero;
    }

    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x8, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExAppWindow = 0x40000, WsExLayered = 0x80000, WsExNoActivate = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}

/// <summary>
/// Табличка оверлея: подписи мелко, светлые цифры Bahnschrift на тёмной полупрозрачной подложке; строкой или столбиком,
/// три размера. Не зависит от темы программы (поверх игры всегда тёмная). Красный — только тревога. Без анимаций.
/// Тот же элемент — в предпросмотре раздела «Оверлей».
/// </summary>
public sealed class OverlayView : Border
{
    private static readonly FontFamily Din = new("Bahnschrift");
    private static readonly Brush ValueBrush = Frozen(Theme.DarkColorOf("Ink1"));
    private static readonly Brush UnitBrush = Frozen(Theme.DarkColorOf("Ink2"));
    private static readonly Brush LabelBrush = Frozen(Theme.DarkColorOf("Ink2"));
    private static readonly Brush AlarmBrush = Frozen(Theme.DarkColorOf("Accent"));

    /// <summary>Размер: число, подпись, поля, промежуток между показаниями, график.</summary>
    private readonly record struct Metrics(double Value, double Label, double PadX, double PadY, double Gap, double GraphW, double GraphH);

    private static Metrics For(string size) => size switch
    {
        "s" => new(14, 9, 8, 5, 12, 72, 15),
        "l" => new(26, 12, 14, 10, 22, 132, 28),
        _ => new(18, 10, 10, 7, 16, 96, 20),
    };

    private string built;
    private Metrics m;
    private readonly List<(OverlayItem Item, TextBlock Value)> values = new();
    private FrameGraph graph;
    private BatteryStrip battery;

    private static Brush Frozen(Color c, double opacity = 1)
    {
        var b = new SolidColorBrush(c) { Opacity = opacity };
        b.Freeze();
        return b;
    }

    /// <summary>Показать числа. Табличка пересобирается, только если поменялись настройки или язык.</summary>
    public void Show(OverlayReading r, OverlayOptions o)
    {
        string key = string.Join(",", o.Items.OrderBy(x => x)) + "|" + o.Size + "|" + o.Column + "|" + o.Opacity + "|" + L.Code;
        if (key != built) Build(o, key);
        foreach (var (item, tb) in values) Fill(tb, Overlay.Segments(item, r), m.Value);
        graph?.SetData(r.FrameMs);
        battery?.SetData(r.Charges);
    }

    private void Build(OverlayOptions o, string key)
    {
        built = key;
        m = For(o.Size);
        values.Clear();
        graph = null;
        battery = null;
        double alpha = Math.Clamp(o.Opacity, 30, 100) / 100.0;
        Background = Frozen(Theme.DarkColorOf("Base"), alpha);
        BorderBrush = Frozen(Theme.DarkColorOf("LineStrong"), alpha);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(m.PadX, m.PadY, m.PadX, m.PadY);
        SnapsToDevicePixels = true;

        var items = o.Items.Distinct().OrderBy(x => x).ToList();
        if (o.Column)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int i = 0; i < items.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = MakeLabel(items[i]);
                label.VerticalAlignment = VerticalAlignment.Center;
                label.Margin = new Thickness(0, 0, m.Gap, 0);
                Grid.SetRow(label, i);
                grid.Children.Add(label);
                var value = MakeValue(items[i], HorizontalAlignment.Right);
                value.Margin = new Thickness(0, i == 0 ? 0 : m.Value * 0.12, 0, 0);
                Grid.SetRow(value, i); Grid.SetColumn(value, 1);
                grid.Children.Add(value);
            }
            Child = grid;
        }
        else
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < items.Count; i++)
            {
                var cell = new StackPanel { Margin = new Thickness(i == 0 ? 0 : m.Gap, 0, 0, 0) };
                cell.Children.Add(MakeLabel(items[i]));
                cell.Children.Add(MakeValue(items[i], HorizontalAlignment.Left));
                row.Children.Add(cell);
            }
            Child = row;
        }
    }

    private TextBlock MakeLabel(OverlayItem item) => new()
    {
        Text = Overlay.Label(item),
        FontFamily = Din, FontWeight = FontWeights.SemiBold, FontSize = m.Label,
        Foreground = LabelBrush,
        Margin = new Thickness(0, 0, 0, m.Label * 0.1),
    };

    private FrameworkElement MakeValue(OverlayItem item, HorizontalAlignment align)
    {
        if (item == OverlayItem.FrameGraph)
        {
            graph = new FrameGraph { Width = m.GraphW, Height = m.Value * 1.05, HorizontalAlignment = align, VerticalAlignment = VerticalAlignment.Center };
            return graph;
        }
        if (item == OverlayItem.Battery)
        {
            battery = new BatteryStrip(m.Value, ValueBrush, UnitBrush, AlarmBrush) { HorizontalAlignment = align };
            return battery;
        }
        var tb = new TextBlock
        {
            FontFamily = Din, FontWeight = FontWeights.SemiBold, FontSize = m.Value, Foreground = ValueBrush,
            HorizontalAlignment = align, TextAlignment = align == HorizontalAlignment.Right ? TextAlignment.Right : TextAlignment.Left,
        };
        Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
        // Ширина — по самому широкому значению: цифры меняются, а табличка не «прыгает».
        Fill(tb, Overlay.Widest(item), m.Value);
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        tb.MinWidth = Math.Ceiling(tb.DesiredSize.Width);
        values.Add((item, tb));
        return tb;
    }

    private static void Fill(TextBlock tb, Overlay.Seg[] segs, double size)
    {
        tb.Inlines.Clear();
        for (int i = 0; i < segs.Length; i++)
        {
            var s = segs[i];
            if (i > 0) tb.Inlines.Add(new Run(" "));
            tb.Inlines.Add(new Run(s.Text) { Foreground = s.Alarm ? AlarmBrush : ValueBrush });
            if (s.Unit.Length > 0)
                tb.Inlines.Add(new Run(" " + s.Unit) { FontSize = size * 0.62, Foreground = s.Alarm ? AlarmBrush : UnitBrush });
        }
    }
}

/// <summary>
/// Заряд устройств в табличке: значок типа (гарнитура, мышь, клавиатура, геймпад), проценты и молния «заряжается».
/// Красным — 15 % и ниже, если устройство не на зарядке. Перестраивается, только когда меняются сами числа.
/// </summary>
public sealed class BatteryStrip : StackPanel
{
    private readonly double size;
    private readonly Brush value, unit, alarm;
    private string shown;

    public BatteryStrip(double size, Brush value, Brush unit, Brush alarm)
    {
        this.size = size; this.value = value; this.unit = unit; this.alarm = alarm;
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        SnapsToDevicePixels = true;
    }

    public void SetData(DeviceCharge[] charges)
    {
        charges ??= Array.Empty<DeviceCharge>();
        string sig = string.Join(";", charges.Select(c => $"{c.Kind}:{c.Percent}:{c.Charging}:{c.Approx}"));
        if (sig == shown) return;
        shown = sig;
        Children.Clear();
        if (charges.Length == 0)
        {
            Children.Add(Text(Overlay.Dash, value));
            return;
        }
        for (int i = 0; i < charges.Length; i++)
        {
            var c = charges[i];
            var brush = DeviceBattery.IsLow(c) ? alarm : value;
            Children.Add(new System.Windows.Shapes.Path
            {
                Data = DeviceBattery.Icon(c.Kind), Stroke = brush, StrokeThickness = Math.Max(1.1, size / 13),
                StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Stretch = Stretch.Uniform, Width = size * 0.92, Height = size * 0.92, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(i == 0 ? 0 : size * 0.6, 0, size * 0.22, 0),
            });
            var tb = Text((c.Approx ? "≈" : "") + Overlay.Whole(c.Percent ?? double.NaN), brush);
            tb.Inlines.Add(new Run(" %") { FontSize = size * 0.62, Foreground = DeviceBattery.IsLow(c) ? alarm : unit });
            Children.Add(tb);
            if (c.Charging == true)
                Children.Add(new System.Windows.Shapes.Path
                {
                    Data = DeviceBattery.BoltIcon, Fill = unit, Stretch = Stretch.Uniform, Width = size * 0.5, Height = size * 0.62,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(size * 0.12, 0, 0, 0),
                });
        }
    }

    private TextBlock Text(string text, Brush brush)
    {
        var tb = new TextBlock
        {
            FontFamily = new FontFamily("Bahnschrift"), FontWeight = FontWeights.SemiBold, FontSize = size, Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
        tb.Inlines.Add(new Run(text));
        return tb;
    }
}

/// <summary>
/// Мини-график времени кадра за ~10 с: в каждом отрезке — самый долгий кадр. Пунктир — обычное время кадра,
/// красные отметки — рывки (кадр вдвое дольше обычного и дольше хотя бы на 8 мс, как в отчёте о сессии).
/// </summary>
public sealed class FrameGraph : FrameworkElement
{
    private static readonly Pen LinePen = MakePen(Theme.DarkColorOf("Ink1"), 1.2);
    private static readonly Pen NormalPen = MakePen(Theme.DarkColorOf("Ink4"), 1, dashed: true);
    private static readonly Pen SpikePen = MakePen(Theme.DarkColorOf("Accent"), 1.6);
    private float[] data = Array.Empty<float>();

    private static Pen MakePen(Color c, double w, bool dashed = false)
    {
        var p = new Pen(new SolidColorBrush(c), w) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat, LineJoin = PenLineJoin.Round };
        if (dashed) p.DashStyle = new DashStyle(new double[] { 2, 2 }, 0);
        p.Freeze();
        return p;
    }

    public void SetData(float[] frameMs)
    {
        data = frameMs ?? Array.Empty<float>();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var valid = data.Where(v => !float.IsNaN(v) && v > 0).ToArray();
        if (valid.Length < 2)
        {
            dc.DrawLine(NormalPen, new Point(0, h / 2), new Point(w, h / 2));
            return;
        }
        var sorted = valid.OrderBy(v => v).ToArray();
        double normal = sorted[sorted.Length / 2];
        double top = Math.Max(sorted[^1], normal * 2.5) * 1.05;
        double Y(double v) => h - Math.Min(v, top) / top * (h - 1) - 0.5;
        double step = data.Length > 1 ? w / (data.Length - 1) : w;
        dc.DrawLine(NormalPen, new Point(0, Y(normal)), new Point(w, Y(normal)));

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            bool open = false;
            for (int i = 0; i < data.Length; i++)
            {
                float v = data[i];
                if (float.IsNaN(v) || v <= 0) { open = false; continue; }
                var p = new Point(i * step, Y(v));
                if (!open) { g.BeginFigure(p, false, false); open = true; }
                else g.LineTo(p, true, true);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, LinePen, geo);
        for (int i = 0; i < data.Length; i++)
        {
            float v = data[i];
            if (!float.IsNaN(v) && FpsCapture.IsStutter(v, normal))
                dc.DrawLine(SpikePen, new Point(i * step, Y(normal)), new Point(i * step, Y(v)));
        }
    }
}
