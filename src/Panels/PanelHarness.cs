using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PulsePC.Services;

namespace PulsePC.Panels;

/// <summary>
/// Служебные режимы для проверки разделов вкладки «Система» без окна программы:
///   PulsePC.exe --render-panel &lt;Класс&gt; &lt;файл.png&gt; [--admin] [--width 780] [--wait 4]
///       создаёт PulsePC.Panels.&lt;Класс&gt;, вызывает Attach и Shown, ждёт фоновую загрузку и сохраняет снимок;
///   PulsePC.exe --feature-test &lt;Класс&gt; &lt;файл.txt&gt;
///       вызывает статический метод string SelfTest() класса PulsePC.Services.&lt;Класс&gt; и записывает результат.
/// Ни один из режимов ничего не меняет в системе.
/// </summary>
public static class PanelHarness
{
    public static bool TryRun(string[] args)
    {
        int t = Array.IndexOf(args, "--feature-test");
        if (t >= 0 && t + 2 < args.Length) { FeatureTest(args[t + 1], args[t + 2]); return true; }

        int r = Array.IndexOf(args, "--render-panel");
        if (r < 0 || r + 2 >= args.Length) return false;
        try { Render(args[r + 1], args[r + 2], args.Contains("--admin"), Number(args, "--width", 780), Number(args, "--wait", 4)); }
        catch (Exception ex) { File.WriteAllText(args[r + 2] + ".error.txt", ex.ToString()); }
        return true;
    }

    private static int Number(string[] args, string name, int fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int v) ? v : fallback;
    }

    private static void FeatureTest(string name, string file)
    {
        string text;
        try
        {
            var type = typeof(PanelHarness).Assembly.GetType("PulsePC.Services." + name, true);
            var method = type.GetMethod("SelfTest", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes)
                         ?? throw new MissingMethodException(name, "SelfTest");
            text = method.Invoke(null, null) as string ?? "";
        }
        catch (Exception ex) { text = "ОШИБКА\n" + (ex is TargetInvocationException t && t.InnerException != null ? t.InnerException : ex); }
        File.WriteAllText(file, text);
    }

    /// <summary>Контекст-заглушка: настройки по умолчанию, показания как во время игры.</summary>
    public static PanelHost FakeHost(bool admin) => new()
    {
        Admin = admin,
        Settings = new AppSettings(),
        Live = () => new LiveData
        {
            Recording = true, Game = "Deadlock", SessionSeconds = 2520, Fps = 148, CpuLoad = 61, CpuGhz = 4.1,
            GpuLoad = 93, GpuTemp = 67, VramUsedGb = 5.4, RamFreeGb = 3.1, RamTotalGb = 15.8, PingMs = 14, GpuName = "Видеокарта",
        },
        Ui = a => Application.Current.Dispatcher.BeginInvoke(a),
        // Сессии — только из отдельной папки проверки (PULSEPC_DATA), настоящие в снимок не попадают.
        Sessions = () => Environment.GetEnvironmentVariable("PULSEPC_DATA") is { Length: > 0 } ? SessionStore.Load() : Array.Empty<GameSession>(),
    };

    private static void Render(string name, string file, bool admin, int width, int waitSeconds)
    {
        var type = typeof(PanelHarness).Assembly.GetType("PulsePC.Panels." + name, true);
        var panel = (FrameworkElement)Activator.CreateInstance(type);
        var page = new Border
        {
            Background = (Brush)Application.Current.Resources["Base"],
            Padding = new Thickness(32, 24, 32, 24),
            Width = width + 64,
            Child = panel,
        };
        TextElement(page);
        var host = FakeHost(admin);
        // --set ключ=значение (можно несколько): настройки раздела для снимка, например --set fan.mode=curve
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--set" && args[i + 1].Split('=', 2) is { Length: 2 } kv) host.Settings.Set(kv[0], kv[1]);
        if (panel is ISystemPanel p) { p.Attach(host); p.Shown(); }

        // Разделы читают состояние в фоне: даём им время и прокачиваем очередь интерфейса.
        Layout(page);
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(1, waitSeconds)) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        Layout(page);

        const double scale = 1.25;
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(page.ActualWidth * scale), (int)Math.Ceiling(Math.Max(1, page.ActualHeight) * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(page);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    /// <summary>Шрифт и цвет текста как у окна программы.</summary>
    private static void TextElement(Border page)
    {
        System.Windows.Documents.TextElement.SetFontFamily(page, (FontFamily)Application.Current.Resources["TextFont"]);
        System.Windows.Documents.TextElement.SetFontSize(page, 13.5);
        System.Windows.Documents.TextElement.SetForeground(page, (Brush)Application.Current.Resources["Ink1"]);
        page.UseLayoutRounding = true;
        page.SnapsToDevicePixels = true;
    }

    private static void Layout(FrameworkElement page)
    {
        page.Measure(new Size(page.Width, double.PositiveInfinity));
        page.Arrange(new Rect(page.DesiredSize));
        page.UpdateLayout();
    }
}
