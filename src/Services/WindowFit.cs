using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PulsePC;

/// <summary>
/// Окно не больше рабочей области своего монитора (экран без панели задач). На ноутбуке 1366×768 с масштабом 125 %
/// по высоте доступно около 614 точек, а главное окно просило минимум 640, окно осмотра — ровно 680: низ уходил за экран.
/// Размер, минимальный размер и положение ужимаются под рабочую область; содержимое этих окон прокручивается.
/// </summary>
public static class WindowFit
{
    public static void Attach(Window w)
    {
        w.SourceInitialized += (_, _) => Fit(w, keepPosition: false);
        w.Loaded += (_, _) => Fit(w, keepPosition: true);
    }

    private const string BoundsKey = "window.bounds";

    /// <summary>
    /// Главное окно: при первом запуске — размер под экран (на большом мониторе крупнее, на ноутбуке — по рабочей области),
    /// дальше — там и такого размера, как его оставили (если тот монитор ещё подключён). Плюс масштаб по настройке Windows
    /// «Увеличить текст» («Специальные возможности» → «Размер текста»): программа на WPF сама её не учитывает.
    /// </summary>
    public static void AttachMain(Window w, Services.AppSettings settings)
    {
        double scale = TextScale();
        w.SourceInitialized += (_, _) =>
        {
            var area = WorkArea(w);
            if (scale > 1.01 && w.Content is FrameworkElement root)
            {
                // Масштаб не больше, чем позволяет экран: минимальное окно должно помещаться.
                scale = Math.Min(scale, Math.Min(area.Width / w.MinWidth, area.Height / w.MinHeight));
                if (scale > 1.01)
                {
                    root.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);
                    w.MinWidth *= scale; w.MinHeight *= scale;
                    w.Width *= scale; w.Height *= scale;
                }
            }
            if (!RestoreBounds(w, settings, area))
            {
                // Первый запуск: 72 % ширины и 82 % высоты рабочей области, но не меньше задуманного и не больше 1600×1100.
                w.Width = Math.Clamp(area.Width * 0.72, w.Width, Math.Max(w.Width, 1600 * Math.Max(1, scale)));
                w.Height = Math.Clamp(area.Height * 0.82, w.Height, Math.Max(w.Height, 1100 * Math.Max(1, scale)));
            }
            Fit(w, keepPosition: false);
        };
        w.Loaded += (_, _) => Fit(w, keepPosition: true);
        w.Closing += (_, _) => SaveBounds(w, settings);
    }

    /// <summary>Множитель «Размер текста» Windows (1 — обычный).</summary>
    public static double TextScale()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
            if (key?.GetValue("TextScaleFactor") is int f && f > 100 && f <= 225) return f / 100.0;
        }
        catch { }
        return 1;
    }

    private static bool RestoreBounds(Window w, Services.AppSettings settings, Rect area)
    {
        string saved = settings?.Get(BoundsKey);
        if (string.IsNullOrEmpty(saved)) return false;
        var p = saved.Split(';');
        if (p.Length < 5 || !double.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double l)
            || !double.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double t)
            || !double.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double width)
            || !double.TryParse(p[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double height)) return false;
        // Сохранённое место должно хоть немного пересекаться с каким-нибудь экраном — иначе окно оказалось бы за пределами (монитор отключили).
        var virt = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!virt.IntersectsWith(new Rect(l + 40, t + 10, Math.Max(1, width - 80), 30))) return false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = l; w.Top = t;
        w.Width = Math.Max(w.MinWidth, width); w.Height = Math.Max(w.MinHeight, height);
        if (p[4] == "1") w.WindowState = WindowState.Maximized;
        return true;
    }

    private static void SaveBounds(Window w, Services.AppSettings settings)
    {
        if (settings == null) return;
        try
        {
            var b = w.WindowState == WindowState.Normal ? new Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight) : w.RestoreBounds;
            if (b.IsEmpty || double.IsNaN(b.Left) || b.Width < 100) return;
            settings.Set(BoundsKey, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{b.Left:0};{b.Top:0};{b.Width:0};{b.Height:0};{(w.WindowState == WindowState.Maximized ? 1 : 0)}"));
            settings.Save();
        }
        catch { }
    }

    /// <summary>Рабочая область монитора, на котором окно, в единицах WPF (с учётом масштаба Windows).</summary>
    public static Rect WorkArea(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            var source = PresentationSource.FromVisual(w);
            if (hwnd != IntPtr.Zero && source?.CompositionTarget != null)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info))
                {
                    var m = source.CompositionTarget.TransformFromDevice;
                    var tl = m.Transform(new Point(info.rcWork.Left, info.rcWork.Top));
                    var br = m.Transform(new Point(info.rcWork.Right, info.rcWork.Bottom));
                    return new Rect(tl, br);
                }
            }
        }
        catch { }
        return SystemParameters.WorkArea;
    }

    private static void Fit(Window w, bool keepPosition)
    {
        if (w.WindowState != WindowState.Normal) return;
        var area = WorkArea(w);
        if (area.Width < 200 || area.Height < 200) return;
        if (w.MinWidth > area.Width) w.MinWidth = area.Width;
        if (w.MinHeight > area.Height) w.MinHeight = area.Height;
        if (!double.IsNaN(w.Width) && w.Width > area.Width) w.Width = area.Width;
        if (!double.IsNaN(w.Height) && w.Height > area.Height) w.Height = area.Height;
        if (!keepPosition || double.IsNaN(w.Left) || double.IsNaN(w.Top)) return;
        double width = double.IsNaN(w.Width) ? w.ActualWidth : w.Width, height = double.IsNaN(w.Height) ? w.ActualHeight : w.Height;
        if (w.Left < area.Left || w.Left + width > area.Right) w.Left = area.Left + Math.Max(0, (area.Width - width) / 2);
        if (w.Top < area.Top || w.Top + height > area.Bottom) w.Top = area.Top + Math.Max(0, (area.Height - height) / 2);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
