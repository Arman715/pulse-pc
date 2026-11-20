using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PulsePC.Blueprint;

/// <summary>
/// Служебный режим: PulsePC.exe --render-blueprints &lt;папка&gt; [сцены…]
/// Рисует все сцены в PNG 1200×820 на бумаге: конечное состояние с примерами выносок,
/// Pc с подсветкой, а также промежуточные состояния прорисовки и разнесения видеокарты.
/// </summary>
public static class RenderHarness
{
    public static bool TryRun(string[] args)
    {
        int a = Array.IndexOf(args, "--blueprint-anim");
        if (a >= 0) { AnimTest(a + 1 < args.Length ? args[a + 1] : Path.GetTempPath()); return true; }
        int c = Array.IndexOf(args, "--render-charts");
        if (c >= 0) { ChartCases(c + 1 < args.Length ? args[c + 1] : Path.Combine(Path.GetTempPath(), "charts")); return true; }
        int i = Array.IndexOf(args, "--render-blueprints");
        if (i < 0) return false;
        string dir = i + 1 < args.Length ? args[i + 1] : Path.Combine(Path.GetTempPath(), "blueprints");
        var only = args.Skip(i + 2).Select(a => a.ToLowerInvariant()).ToHashSet();
        bool Want(string n) => only.Count == 0 || only.Contains(n.ToLowerInvariant());
        Directory.CreateDirectory(dir);
        var log = new StringBuilder();
        try
        {
            if (Want("pc"))
            {
                Render(dir, "pc", v => { v.Scene = Scene.Pc; PcCallouts(v); });
                Render(dir, "pc-hl-gpu", v => { v.Scene = Scene.Pc; v.Highlight = Part.Gpu; PcCallouts(v); });
                Render(dir, "pc-hl-ram", v => { v.Scene = Scene.Pc; v.Highlight = Part.Ram; PcCallouts(v); });
                Render(dir, "pc-ghost-board", v => { v.Scene = Scene.Pc; v.Highlight = Part.Motherboard; v.GhostOthers = true; v.SetCallout("pc.board", "Д  Материнская плата", "MSI PRO B760-P WIFI", compact: true); });
                Render(dir, "pc-draw-50", v => { v.Scene = Scene.Pc; v.Progress = 0.5; });
                Render(dir, "pc-720x500", v => { v.Scene = Scene.Pc; v.Highlight = Part.Cpu; PcCallouts(v); }, 720, 500);
                log.AppendLine(Perf(Scene.Pc));
            }
            if (Want("gpu"))
            {
                Render(dir, "gpu", v => { v.Scene = Scene.Gpu; GpuCallouts(v); });
                Render(dir, "gpu-draw-35", v => { v.Scene = Scene.Gpu; v.Progress = 0.35; v.Explode = 0; });
                Render(dir, "gpu-draw-70", v => { v.Scene = Scene.Gpu; v.Progress = 0.7; v.Explode = 0; });
                Render(dir, "gpu-explode-0", v => { v.Scene = Scene.Gpu; v.Explode = 0; });
                Render(dir, "gpu-explode-50", v => { v.Scene = Scene.Gpu; v.Explode = 0.5; GpuCallouts(v); });
                Render(dir, "gpu-small", v => { v.Scene = Scene.Gpu; GpuCallouts(v); }, 800, 650);
                log.AppendLine(Perf(Scene.Gpu));
            }
            if (Want("host"))
            {
                Render(dir, "host-gpu4", v =>
                {
                    v.Scene = Scene.Gpu;
                    v.SetCallout("gpu.fan", "Вентилятор", "0 %"); v.SetCallout("gpu.power", "Потребление", "13 Вт");
                    v.SetCallout("gpu.die", "Частота", "210 МГц"); v.SetCallout("gpu.vram", "Видеопамять", "0,9 ГБ");
                }, 760, 600);
                Render(dir, "host-ram4", v =>
                {
                    v.Scene = Scene.Ram;
                    v.SetCallout("ram.stick1", "Kingston · A2", "8 ГБ"); v.SetCallout("ram.stick2", "Kingston · B2", "8 ГБ");
                    v.SetCallout("ram.chips", "Частота", "3200 МТ/с"); v.SetCallout("ram.contacts", "Занято", "8,7 ГБ");
                    v.SetDimension("15,8 ГБ");
                }, 760, 600);
                Render(dir, "host-cpu", v =>
                {
                    v.Scene = Scene.Cpu;
                    v.SetCallout("cpu.cooler", "Загрузка", "4 %"); v.SetCallout("cpu.ihs", "Частота", "4,10 ГГц"); v.SetCallout("cpu.socket", "Сокет", "LGA1700");
                }, 760, 600);
                Render(dir, "host-board", v => { v.Scene = Scene.Pc; v.Highlight = Part.Motherboard; v.GhostOthers = true; v.SetCallout("pc.board", "Материнская плата", "MSI PRO B760-P WIFI"); }, 760, 600);
                Render(dir, "host-gamepad", v => v.Scene = Scene.Gamepad, 360, 110);
                Render(dir, "host-router", v => v.Scene = Scene.Router, 560, 300);
            }
            if (Want("cpu"))
                Render(dir, "cpu", v =>
                {
                    v.Scene = Scene.Cpu;
                    v.SetCallout("cpu.ihs", "Загрузка", "4 %");
                    v.SetCallout("cpu.package", "Частота", "4,10 ГГц");
                    v.SetCallout("cpu.cooler", "Кулер", "");
                    v.SetCallout("cpu.socket", "Сокет LGA1700", "");
                });
            if (Want("ram"))
            {
                Render(dir, "ram", v =>
                {
                    v.Scene = Scene.Ram;
                    v.SetCallout("ram.stick1", "Занято", "8,7 ГБ");
                    v.SetCallout("ram.stick2", "Доступно", "7,1 ГБ");
                    v.SetCallout("ram.chips", "Микросхемы памяти", "");
                    v.SetDimension("15,8 ГБ");
                });
                // столько планок, сколько стоит: 1, 2, 4 и 8 (HEDT); выноски — как их ставит главное окно
                string[] slots = { "A1", "A2", "B1", "B2", "C1", "C2", "D1", "D2" };
                foreach (int n in new[] { 1, 2, 3, 4, 8 })
                {
                    double size = n >= 8 ? 32 : n == 1 ? 16 : 8;
                    BlueprintLabels.RamSticks = Enumerable.Range(0, n).Select(_ => $"3200 MHz {size:0} GB").ToArray();
                    BlueprintLabels.RamSpec = BlueprintLabels.RamSticks[0];
                    Scenes.Reset();
                    foreach (var (w, h, tag) in new[] { (1200, 820, ""), (760, 600, "-host") })
                        Render(dir, $"ram-{n}{tag}", v =>
                        {
                            v.Scene = Scene.Ram;
                            for (int i = 0; i < n; i++)
                                v.SetCallout($"ram.stick{i + 1}", $"Kingston · {slots[i]}", $"{size:0} ГБ", compact: n > 4);
                            v.SetCallout("ram.chips", "Занято", $"{n * size * 0.55:0.0} ГБ");
                            v.SetDimension($"{n * size - 0.2:0.0} ГБ");
                        }, w, h);
                }
                BlueprintLabels.RamSticks = Array.Empty<string>(); BlueprintLabels.RamSpec = ""; Scenes.Reset();
            }
            if (Want("storage"))
            {
                Render(dir, "storage", v =>
                {
                    v.Scene = Scene.Storage;
                    v.SetCallout("ssd.nand", "Свободно", "335,6 ГБ");
                    v.SetCallout("ssd.controller", "Температура", "41 °C", warn: false);
                    v.SetCallout("ssd.heatsink", "Радиатор", "");
                });
                // все накопители: от одного до пяти разных типов; первый — системный
                var nvme = (new DriveSpec(DriveKind.Nvme, "990 PRO"), "Samsung 990 PRO · 1 ТБ · C:", "330 ГБ своб.");
                var hdd2 = (new DriveSpec(DriveKind.Hdd35, "WD20EZBX"), "WD20EZBX · 2 ТБ · E:", "1737 ГБ своб.");
                var hdd5 = (new DriveSpec(DriveKind.Hdd35, "WD5000AZRX"), "WD5000AZRX · 500 ГБ · D:", "436 ГБ своб.");
                var sata = (new DriveSpec(DriveKind.Sata25, "S270 120GB"), "MSI S270 · 120 ГБ · F:", "57 ГБ своб.");
                var sata2 = (new DriveSpec(DriveKind.Sata25, "870 EVO"), "Samsung 870 EVO · 500 ГБ", "исправен");
                var cases = new (string name, (DriveSpec spec, string label, string value)[] list)[]
                {
                    ("storage-1hdd", new[] { hdd2 }),
                    ("storage-2", new[] { nvme, hdd2 }),
                    ("storage-3", new[] { sata, nvme, hdd5 }),
                    ("storage-4", new[] { nvme, hdd2, hdd5, sata }),
                    ("storage-5", new[] { nvme, sata2, hdd2, hdd5, sata }),
                };
                foreach (var (name, list) in cases)
                {
                    BlueprintLabels.Drives = list.Select(x => x.spec).ToArray();
                    Scenes.Reset();
                    foreach (var (w, h, tag) in new[] { (1200, 820, ""), (760, 600, "-host") })
                        Render(dir, name + tag, v =>
                        {
                            v.Scene = Scene.Storage;
                            for (int i = 0; i < list.Length; i++) v.SetCallout($"disk.{i}", list[i].label, list[i].value);
                        }, w, h);
                }
                BlueprintLabels.Drives = Array.Empty<DriveSpec>(); Scenes.Reset();
            }
            if (Want("router"))
                Render(dir, "router", v =>
                {
                    v.Scene = Scene.Router;
                    v.SetCallout("router.wan", "Пинг", "12 мс");
                    v.SetCallout("router.antenna", "Потери", "0 %");
                });
            if (Want("gamepad"))
                Render(dir, "gamepad", v =>
                {
                    v.Scene = Scene.Gamepad;
                    v.SetCallout("pad.stick", "Сессия", "1 ч 12 мин");
                    v.SetCallout("pad.buttons", "Средний FPS", "144");
                });
        }
        catch (Exception ex) { log.AppendLine(ex.ToString()); }
        File.WriteAllText(Path.Combine(dir, "harness.txt"), log.ToString());
        return true;
    }

    /// <summary>
    /// PulsePC.exe --render-charts &lt;папка&gt;: графики с масштабом по времени на синтетических данных
    /// (живой 60 с, история 48 ч с разрывами, FPS сессии с просадками) — целиком и увеличенные, плюс замер кадра.
    /// </summary>
    private static void ChartCases(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new StringBuilder();
        try
        {
            var rnd = new Random(5);
            double[] Series(int n, double baseV, double spread, Func<int, double> shape = null)
            {
                var a = new double[n];
                for (int i = 0; i < n; i++) a[i] = Math.Max(0, baseV + (rnd.NextDouble() - 0.5) * spread + 6 * Math.Sin(i / 11.0) + (shape?.Invoke(i) ?? 0));
                return a;
            }
            // живой график: 41 показание, 60 с
            var liveCpu = Series(42, 18, 14); var liveGpu = Series(42, 35, 20);
            void Live(LoadChart c) { for (int i = 0; i < liveCpu.Length; i++) c.Push(liveCpu[i], liveGpu[i], false); }
            ChartShot(dir, "live-60s", "НАГРУЗКА", new ChartZoom { Live = true, Full = TimeSpan.FromSeconds(60) }, z => { }, Live, 90);
            ChartShot(dir, "live-15s", "НАГРУЗКА", new ChartZoom { Live = true, Full = TimeSpan.FromSeconds(60) }, z => z.ZoomBy(4, 0.3), Live, 90);
            // история 48 ч: 720 точек по 4 минуты, два разрыва (программа не была запущена)
            var hc = Series(720, 20, 16, i => i > 430 && i < 520 ? 45 : 0); var hg = Series(720, 25, 20, i => i > 430 && i < 520 ? 55 : 0);
            for (int i = 120; i < 260; i++) { hc[i] = double.NaN; hg[i] = double.NaN; }
            for (int i = 600; i < 612; i++) { hc[i] = double.NaN; hg[i] = double.NaN; }
            void Hist(LoadChart c) { c.HatchGaps = true; c.SetData(hc, hg, false); }
            ChartShot(dir, "hist-48h", "НАГРУЗКА, %", new ChartZoom { Full = TimeSpan.FromHours(48) }, z => { }, Hist, 110);
            ChartShot(dir, "hist-2h30", "НАГРУЗКА, %", new ChartZoom { Full = TimeSpan.FromHours(48) }, z => z.Set(0.585, 2.5 / 48, false), Hist, 110);
            ChartShot(dir, "hist-gap-edge", "НАГРУЗКА, %", new ChartZoom { Full = TimeSpan.FromHours(48) }, z => z.Set(0.12, 0.3, false), Hist, 110);
            // FPS сессии 1 ч 34 мин: 240 точек, просадки с отметками; шкала по видимому окну
            var fps = Series(240, 150, 16, i => i is 40 or 41 ? -95 : i is 150 ? -70 : 0);
            void Fps(LoadChart c) { c.AutoScale = true; c.Unit = "FPS"; c.SetData(fps, null, false); c.Marks = new[] { 40.5 / 239, 150.0 / 239 }; }
            ChartShot(dir, "fps-full", "КАДРЫ В СЕКУНДУ", new ChartZoom { Full = TimeSpan.FromMinutes(94) }, z => { }, Fps, 96);
            ChartShot(dir, "fps-dip", "КАДРЫ В СЕКУНДУ", new ChartZoom { Full = TimeSpan.FromMinutes(94) }, z => z.Set(0.12, 0.1, false), Fps, 96);
            ChartShot(dir, "fps-calm", "КАДРЫ В СЕКУНДУ", new ChartZoom { Full = TimeSpan.FromMinutes(94) }, z => z.Set(0.7, 0.08, false), Fps, 96);
            foreach (var t in new[] { 1, 15, 61, 150, 330, 600, 3540, 5640, 9000, 21600, 86400, 172800 })
                log.AppendLine($"{t,7} с → «{ChartZoom.FormatSpan(TimeSpan.FromSeconds(t))}»");
            log.AppendLine(ChartPerf(hc, hg));
            // колесо: обработчик меняет общий масштаб и забирает событие у страницы; на краю — отдаёт
            var wz = new ChartZoom { Full = TimeSpan.FromHours(48) };
            var wc = new LoadChart { Width = 600, Height = 100 };
            wc.SetData(hc, hg, false); wc.Zoom = wz;
            wc.Measure(new Size(600, 100)); wc.Arrange(new Rect(0, 0, 600, 100));
            bool Wheel(int delta)
            {
                var e = new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.MouseWheelEvent };
                wc.RaiseEvent(e);
                return e.Handled;
            }
            bool h1 = Wheel(120); double s1 = wz.Span;
            bool h2 = Wheel(-120); bool h3 = Wheel(-120);
            log.AppendLine($"колесо: вверх → окно {s1:0.000} ({wz.Caption}), handled={h1}; вниз → handled={h2}, span={wz.Span:0.000}; ещё вниз на полном виде → handled={h3} (страница прокручивается)");
        }
        catch (Exception ex) { log.AppendLine(ex.ToString()); }
        File.WriteAllText(Path.Combine(dir, "charts.txt"), log.ToString());
    }

    private static void ChartShot(string dir, string name, string title, ChartZoom zoom, Action<ChartZoom> act, Action<LoadChart> fill, double height)
    {
        const int w = 640;
        var chart = new LoadChart { Height = height, Margin = new Thickness(0, 8, 0, 0) };
        var bar = new PulsePC.ChartZoomBar { Title = title, TitleStyle = Application.Current.TryFindResource("Label") as Style, Zoom = zoom };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(bar); panel.Children.Add(chart);
        var root = new Border { Background = Palette.Base, Child = panel, Width = w };
        fill(chart);
        chart.Zoom = zoom;
        act(zoom);
        root.Measure(new Size(w, double.PositiveInfinity)); root.Arrange(new Rect(root.DesiredSize)); root.UpdateLayout();
        int h = (int)Math.Ceiling(root.ActualHeight);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(Path.Combine(dir, "chart-" + name + ".png"));
        enc.Save(fs);
    }

    /// <summary>Сколько стоит кадр перехода масштаба: время OnRender и выделенная память на кадр.</summary>
    private static string ChartPerf(double[] cpu, double[] gpu)
    {
        var zoom = new ChartZoom { Full = TimeSpan.FromHours(48) };
        var chart = new LoadChart { Width = 900, Height = 110, HatchGaps = true };
        chart.SetData(cpu, gpu, false);
        chart.Zoom = zoom;
        chart.Measure(new Size(900, 110)); chart.Arrange(new Rect(0, 0, 900, 110));
        var render = typeof(LoadChart).GetMethod("OnRender", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        void Frame() { var dv = new DrawingVisual(); using var dc = dv.RenderOpen(); render.Invoke(chart, new object[] { dc }); }
        Frame();   // первый кадр строит геометрию
        const int N = 200;
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int k = 0; k < N; k++)
        {
            zoom.Set(0.2 + 0.3 * k / N, 0.6 - 0.5 * k / N, false);   // каждый кадр — новое окно, как при анимации
            Frame();
        }
        double ms = sw.Elapsed.TotalMilliseconds / N;
        long bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / N;
        return $"кадр масштаба (720 точек ×2, штриховка разрывов): {ms:0.00} мс, ~{bytes / 1024.0:0.0} КБ на кадр вместе с записью команд отрисовки (DrawingVisual)";
    }

    private static void PcCallouts(BlueprintView v)
    {
        v.SetCallout("pc.cpu", "А  Процессор", "i5-12400F", compact: true);
        v.SetCallout("pc.gpu", "Б  Видеокарта", "RTX 4060 Ti", compact: true);
        v.SetCallout("pc.ram", "В  Память", "16 ГБ", compact: true);
        v.SetCallout("pc.storage", "Г  Накопители", "4 накопителя", compact: true);
        v.SetCallout("pc.board", "Д  Материнская плата", "MSI PRO B760-P WIFI", compact: true);
    }

    private static void GpuCallouts(BlueprintView v)
    {
        v.SetCallout("gpu.fan", "Вентилятор", "0 %");
        v.SetCallout("gpu.heatsink", "Потребление", "13 Вт");
        v.SetCallout("gpu.die", "Частота", "210 МГц");
    }

    private static (Border root, BlueprintView view) Make(Action<BlueprintView> setup, int w, int h)
    {
        var view = new BlueprintView();
        var root = new Border { Background = new SolidColorBrush(BlueprintPalette.Paper), Child = view, Width = w, Height = h };
        setup(view);
        root.Measure(new Size(w, h)); root.Arrange(new Rect(0, 0, w, h)); root.UpdateLayout();
        return (root, view);
    }

    private static void Render(string dir, string name, Action<BlueprintView> setup, int w = 1200, int h = 820)
    {
        var (root, view) = Make(v =>
        {
            setup(v);
            // конечное состояние выносок, но без сброса заданных Progress/Explode
            double p = v.Progress, e = v.Explode;
            v.Finish();
            v.Progress = p; v.Explode = e;
        }, w, h);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(Path.Combine(dir, name + ".png"));
        enc.Save(fs);
    }

    /// <summary>Проверка цепочки анимаций в живом окне: Play → разнесение → выноски → Collapse → повторный Play → подсветка.</summary>
    private static void AnimTest(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new StringBuilder();
        var view = new BlueprintView();
        var win = new Window
        {
            Width = 900, Height = 700, Left = -3000, Top = -3000, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            Background = new SolidColorBrush(BlueprintPalette.Paper), Content = view, ShowActivated = false,
        };
        win.Show();
        var frame = new System.Windows.Threading.DispatcherFrame();
        var sw = Stopwatch.StartNew();
        int frames = 0;
        EventHandler counter = (_, _) => frames++;
        CompositionTarget.Rendering += counter;
        void Snap(string name)
        {
            var rtb = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(BlueprintPalette.Paper), null, new Rect(0, 0, view.ActualWidth, view.ActualHeight));
                dc.DrawRectangle(new VisualBrush(view) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, view.ActualWidth, view.ActualHeight) }, null, new Rect(0, 0, view.ActualWidth, view.ActualHeight));
            }
            rtb.Render(dv);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(dir, $"anim-{name}.png")); enc.Save(fs);
            log.AppendLine($"{sw.ElapsedMilliseconds,5} ms  {name}: progress={view.Progress:0.00} explode={view.Explode:0.00} frames={frames}");
        }
        var steps = new List<(int at, Action act)>
        {
            (100, () => { view.Scene = Scene.Gpu; GpuCallouts(view); view.PlayFull(); log.AppendLine("PlayFull, angle=" + view.CameraAngle); }),
            (500, () => Snap("1-draw")),
            (1500, () => Snap("2-explode")),
            (2000, () => Snap("3-callouts")),
            (2900, () => Snap("4-final")),
            (3000, () => { view.SetCallout("gpu.fan", "Вентилятор", "37 %"); view.Collapse(() => log.AppendLine($"{sw.ElapsedMilliseconds,5} ms  collapse done")); }),
            (3150, () => Snap("5-collapsing")),
            (3600, () => Snap("6-collapsed")),
            (3700, () => { view.Play(); log.AppendLine("Play (second time → fade)"); }),
            (3800, () => Snap("7-fade")),
            (5200, () => Snap("8-replayed")),
            (5300, () => { view.Scene = Scene.Pc; view.Interactive = true; PcCallouts(view); view.PlayFull(); }),
            (7200, () => { view.Highlight = Part.Gpu; }),
            (7280, () => Snap("9-highlight-mid")),
            (7700, () =>
            {
                Snap("10-highlight");
                foreach (Part p in Enum.GetValues(typeof(Part)))
                {
                    var r = view.PartBounds(p);
                    if (r == null) continue;
                    var c = new Point(r.Value.X + r.Value.Width / 2, r.Value.Y + r.Value.Height / 2);
                    log.AppendLine($"  {p}: bounds={r.Value.X:0},{r.Value.Y:0},{r.Value.Width:0}x{r.Value.Height:0}  PartAt(center)={view.PartAt(c)}");
                }
            }),
            (7800, () => { frame.Continue = false; }),
        };
        int idx = 0;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            while (idx < steps.Count && sw.ElapsedMilliseconds >= steps[idx].at)
            {
                try { steps[idx].act(); } catch (Exception ex) { log.AppendLine(ex.ToString()); frame.Continue = false; }
                idx++;
            }
        };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        timer.Stop();
        CompositionTarget.Rendering -= counter;
        log.AppendLine($"render frames total: {frames} over {sw.ElapsedMilliseconds} ms");
        win.Close();
        File.WriteAllText(Path.Combine(dir, "anim.txt"), log.ToString());
    }

    /// <summary>Сколько стоит кадр анимации на стороне ЦП (построение команд отрисовки) при 800×650.</summary>
    private static string Perf(Scene s)
    {
        var (root, view) = Make(v => v.Scene = s, 800, 650);
        var sw = Stopwatch.StartNew();
        view.DebugRender(1, 1);
        long first = sw.ElapsedMilliseconds;
        sw.Restart();
        const int N = 30;
        for (int k = 0; k < N; k++) view.DebugRender((k + 1) / (double)N, 1);
        double draw = sw.Elapsed.TotalMilliseconds / N;
        sw.Restart();
        for (int k = 0; k < N; k++) view.DebugRender(1, k / (double)N);
        double expl = sw.Elapsed.TotalMilliseconds / N;
        return $"{s}: items={view.DebugItemCount}, first layout+render {first} ms, draw-on frame {draw:0.0} ms, explode frame {expl:0.0} ms";
    }
}
