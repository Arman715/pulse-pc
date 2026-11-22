using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PulsePC.Panels;

/// <summary>
/// Служебный режим: снимки окна программы для страницы проекта и справки — без «одного экземпляра» и без настоящих данных
/// (только вместе с PULSEPC_DATA — отдельной папкой с подставными данными):
///   PulsePC.exe --window-shots &lt;папка&gt; [--size 1500x980] цель>файл [цель@Элемент>файл] …
/// Цель — как в подсказках программы: tab:Обзор, tab:Сессии, sys:Оверлей. «@Элемент» — снять не окно, а элемент разметки
/// целиком (например, отчёт о сессии во всю длину, без прокрутки). Ничего не меняет в системе.
/// </summary>
public static class WindowShots
{
    public static bool TryRun(string[] args, Application app)
    {
        int i = Array.IndexOf(args, "--window-shots");
        if (i < 0 || i + 1 >= args.Length) return false;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PULSEPC_DATA"))) { app.Shutdown(2); return true; }
        string dir = args[i + 1];
        Directory.CreateDirectory(dir);
        int w = 1500, h = 980;
        int s = Array.IndexOf(args, "--size");
        if (s >= 0 && s + 1 < args.Length && args[s + 1].Split('x') is { Length: 2 } wh && int.TryParse(wh[0], out int ww) && int.TryParse(wh[1], out int hh)) { w = ww; h = hh; }
        var jobs = args.Skip(i + 2).Where(a => a.Contains('>') && !a.StartsWith("--")).ToList();
        _ = Run(app, dir, w, h, jobs);
        return true;
    }

    private static async Task Run(Application app, string dir, int w, int h, List<string> jobs)
    {
        var log = new List<string>();
        try
        {
            const long Mb = 1024 * 1024;
            MainWindow.ShotProcs = new() { ("Cyberpunk2077", 1, 6900 * Mb), ("chrome", 24, 1900 * Mb), ("Discord", 6, 640 * Mb), ("steamwebhelper", 9, 520 * Mb), ("explorer", 1, 180 * Mb), ("Spotify", 4, 170 * Mb) };
            var window = new MainWindow { Width = w, Height = h, Left = 0, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
            window.Show();
            // Обзор, сессии и «Здоровье ПК» загружаются в фоне; график нагрузки набирается за минуту.
            int warm = int.TryParse(Environment.GetEnvironmentVariable("PULSEPC_SHOTS_WARM"), out int sec) ? sec : 75;
            await Task.Delay(warm * 1000);
            foreach (var job in jobs)
            {
                var parts = job.Split('>', 2);
                string target = parts[0], element = null;
                int at = target.IndexOf('@');
                if (at >= 0) { element = target[(at + 1)..]; target = target[..at]; }
                // «log:<вид>» — окно «Подробнее и что делать» для записей журнала этого вида (настоящие журналы Windows, только чтение).
                Window extra = null;
                if (target.StartsWith("log:"))
                {
                    extra = new LogDetailsWindow(window, target[4..], target[4..], PulsePC.Severity.Warn, null, null) { Width = 760, Height = 1400, ShowActivated = false };
                    extra.Show();
                }
                else window.ShotTarget(target);
                await Task.Delay(extra != null ? 9000 : 3500);
                var visual = extra != null ? extra.Content as FrameworkElement : element != null ? window.FindName(element) as FrameworkElement : window.Content as FrameworkElement;
                if (visual == null) { log.Add($"{job}: нет элемента"); extra?.Close(); continue; }
                Save(visual, Path.Combine(dir, parts[1] + ".png"));
                log.Add($"{job}: {visual.ActualWidth:0}×{visual.ActualHeight:0}");
                extra?.Close();
            }
        }
        catch (Exception ex) { log.Add(ex.ToString()); }
        File.WriteAllLines(Path.Combine(dir, "shots.log"), log);
        app.Shutdown();
    }

    /// <summary>Элемент целиком в PNG (масштаб 1,5), с фоном окна; через VisualBrush — без сдвига и обрезки прокруткой.</summary>
    private static void Save(FrameworkElement visual, string file)
    {
        const double scale = 1.5;
        double width = Math.Max(1, visual.ActualWidth), height = Math.Max(1, visual.ActualHeight);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle((Brush)Application.Current.Resources["Base"], null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
        }
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        enc.Save(fs);
    }
}
