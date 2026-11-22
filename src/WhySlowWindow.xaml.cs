using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PulsePC.Panels;
using PulsePC.Services;

namespace PulsePC;

/// <summary>
/// «Почему тормозит прямо сейчас»: пять секунд наблюдения (Services\WhySlow), вывод простыми словами и действия.
/// Закрыть программу — только по кнопке и после подтверждения; принудительно — отдельной кнопкой и ещё одним подтверждением.
/// </summary>
public partial class WhySlowWindow : Window
{
    private readonly PanelHost host;
    private CancellationTokenSource cancel;
    private bool busy;

    public WhySlowWindow(Window owner, PanelHost host)
    {
        InitializeComponent();
        WindowFit.Attach(this);
        this.host = host;
        Owner = owner;
        Title = L.T("Пульс ПК") + " — " + L.T("Почему тормозит");
        Motion.Init(this);
        Closed += (_, _) => { try { cancel?.Cancel(); } catch { } };
        Loaded += (_, _) => Start();
    }

    /// <summary>Снимок окна для проверки: готовый отчёт без наблюдения (PULSEPC_WHYSLOW_DEMO=1).</summary>
    private static bool Demo => Environment.GetEnvironmentVariable("PULSEPC_WHYSLOW_DEMO") == "1";

    // ================================================================== наблюдение

    private void Start()
    {
        if (busy) return;
        busy = true;
        BtnAgain.IsEnabled = false;
        Mark.Visibility = Visibility.Collapsed;
        Heading.Text = L.T("Смотрю, что нагружает компьютер…");
        Heading.Foreground = (Brush)FindResource("Ink1");
        Intro.Text = L.T("Пять секунд: процессор, видеокарта, память, диски, сеть и самые активные программы. Ничего не меняется и не закрывается.");
        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;
        CausesBlock.Visibility = ReadingsBlock.Visibility = ProcsBlock.Visibility = ActionResult.Visibility = Visibility.Collapsed;
        Footer.Text = "";
        cancel?.Cancel();
        cancel = new CancellationTokenSource();
        var token = cancel.Token;
        bool admin = host.Admin;
        Task.Run(() =>
        {
            try
            {
                var sample = Demo ? DemoSample() : WhySlow.Observe(host.Live, WhySlow.ObserveSeconds, p => Dispatcher.BeginInvoke(new Action(() => Progress.Value = p)), token);
                var report = WhySlow.Analyze(sample, admin);
                Dispatcher.BeginInvoke(new Action(() => Show(report)));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Error("почему тормозит", ex);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    busy = false;
                    BtnAgain.IsEnabled = true;
                    Progress.Visibility = Visibility.Collapsed;
                    Heading.Text = L.T("Замер не удался");
                    Intro.Text = L.F("Что случилось: {0}", ex.Message);
                }));
            }
        }, token);
    }

    private static SlowSample DemoSample() => new()
    {
        Cpu = 96, MaxCore = 100, Gpu = 4, GpuTemp = 47, RamTotalGb = 15.8, RamAvailGb = 1.3, PagesIn = 420, DiskBusy = 71, DiskName = "C:", DiskMBs = 38,
        NetDownMBs = 0.2, NetUpMBs = 0.05, PingMs = 21, UptimeDays = 2,
        Procs = new List<SlowProc>
        {
            new() { Name = "chrome", Cpu = 58, DiskMBs = 2.1, MemBytes = 4_300_000_000, Count = 31, HasWindow = true },
            new() { Name = "MsMpEng", Cpu = 17, DiskMBs = 31, MemBytes = 410_000_000 },
            new() { Name = "Discord", Cpu = 4, MemBytes = 820_000_000, Count = 6, HasWindow = true },
            new() { Name = "OneDrive", Cpu = 2, DiskMBs = 4.5, MemBytes = 160_000_000, HasWindow = false },
            new() { Name = "explorer", Cpu = 1, MemBytes = 230_000_000, HasWindow = true },
        },
    };

    // ================================================================== показ

    private void Show(SlowReport r)
    {
        busy = false;
        BtnAgain.IsEnabled = true;
        Progress.Visibility = Visibility.Collapsed;
        Mark.Visibility = Visibility.Visible;
        Mark.Level = r.Level;
        Heading.Text = r.Title;
        Heading.Foreground = (Brush)FindResource(r.Level == Severity.Bad ? "Bad" : "Ink1");
        Intro.Text = r.Text;
        Footer.Text = L.F("Замер {0:HH:mm:ss}. Числа — средние за пять секунд.", DateTime.Now);

        Causes.Children.Clear();
        foreach (var c in r.Causes) Causes.Children.Add(CauseRow(c));
        CausesBlock.Visibility = r.Causes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        Readings.Children.Clear();
        foreach (var (label, value, warn) in r.Readings) Readings.Children.Add(ReadingRow(label, value, warn));
        ReadingsBlock.Visibility = Visibility.Visible;

        Procs.Children.Clear();
        foreach (var p in r.Top) Procs.Children.Add(ProcRow(p));
        ProcsBlock.Visibility = r.Top.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        Motion.Reveal(Heading, 0, 6);
        Motion.Reveal(CausesBlock, 60, 6);
        Motion.Reveal(ReadingsBlock, 120, 6);
        Motion.Reveal(ProcsBlock, 180, 6);
        Scroll.ScrollToTop();
    }

    private FrameworkElement CauseRow(SlowCause c)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 12, 0, 12) };
        var mark = new StateMarker { Level = c.Level, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 12, 0) };
        DockPanel.SetDock(mark, Dock.Left);
        dock.Children.Add(mark);
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = c.Title, Style = (Style)FindResource("RowTitle") });
        body.Children.Add(new TextBlock { Text = c.Text, Style = (Style)FindResource("Body"), Margin = new Thickness(0, 3, 0, 0) });
        if (c.Actions.Count > 0)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var a in c.Actions) row.Children.Add(ActionButton(a));
            body.Children.Add(row);
        }
        dock.Children.Add(body);
        var border = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = dock };
        border.SetResourceReference(Border.BorderBrushProperty, "Line");
        return border;
    }

    private Button ActionButton(SlowAction a)
    {
        var b = new Button { Content = a.Label, Margin = new Thickness(0, 0, 10, 6) };
        if (a.Kind == SlowAction.Close) b.Style = (Style)FindResource("Destructive");
        b.Click += (_, _) => Act(a, b);
        return b;
    }

    private FrameworkElement ReadingRow(string label, string value, bool warn)
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = new TextBlock { Text = label, Style = (Style)FindResource("Caption"), VerticalAlignment = VerticalAlignment.Center };
        var v = new TextBlock { Text = value, Style = (Style)FindResource("MonoNum"), TextAlignment = TextAlignment.Left, TextWrapping = TextWrapping.Wrap };
        v.SetResourceReference(TextBlock.ForegroundProperty, warn ? "Warn" : "Ink1");
        Grid.SetColumn(v, 1);
        grid.Children.Add(l); grid.Children.Add(v);
        var border = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
        border.SetResourceReference(Border.BorderBrushProperty, "Line");
        return border;
    }

    private FrameworkElement ProcRow(SlowProc p)
    {
        var grid = new Grid { Margin = new Thickness(0, 7, 0, 7) };
        foreach (var w in new[] { new GridLength(1, GridUnitType.Star), new GridLength(86), new GridLength(86), new GridLength(86), new GridLength(96) })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        string name = p.Pretty + (p.Count > 1 ? " ×" + p.Count : "") + (p.IsGame ? " · " + L.T("игра") : "");
        var n = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = p.Name + ".exe" };
        n.SetResourceReference(TextBlock.ForegroundProperty, "Ink1");
        grid.Children.Add(n);
        string Pct(double v) => v < 0.5 ? "—" : v.ToString("0", L.Culture) + (L.En ? "%" : " %");
        string MBs(double v) => v < 0.1 ? "—" : v.ToString(v >= 10 ? "0" : "0.0", L.Culture) + " " + L.Pick("МБ/с", "MB/s");
        string[] cells = { Pct(p.Cpu), MBs(p.DiskMBs), p.MemBytes > 0 ? Fmt.Size(p.MemBytes) : "—" };
        for (int i = 0; i < 3; i++)
        {
            var t = new TextBlock { Text = cells[i], Style = (Style)FindResource("MonoNum") };
            Grid.SetColumn(t, i + 1);
            grid.Children.Add(t);
        }
        if (p.CanClose)
        {
            var b = new Button { Content = L.T("Закрыть"), Style = (Style)FindResource("Link"), HorizontalAlignment = HorizontalAlignment.Right };
            b.Click += (_, _) => Act(new SlowAction { Kind = SlowAction.Close, Target = p.Name, Label = p.Pretty }, b);
            Grid.SetColumn(b, 4);
            grid.Children.Add(b);
        }
        var border = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
        border.SetResourceReference(Border.BorderBrushProperty, "Line");
        return border;
    }

    // ================================================================== действия

    private void Act(SlowAction a, Button button)
    {
        switch (a.Kind)
        {
            case SlowAction.Open:
                host.Open(a.Target);
                Owner?.Activate();
                break;
            case SlowAction.Free:
                FreeMemory(button);
                break;
            case SlowAction.Close:
                CloseProgram(a.Target, button);
                break;
        }
    }

    private void FreeMemory(Button button)
    {
        if (!host.Admin) { Say(L.T("Освобождать память можно только с правами администратора.")); return; }
        button.IsEnabled = false;
        Say(L.T("Освобождаю память…"));
        bool trim = host.Settings.GetBool(MemoryBoost.KTrim);
        Task.Run(() =>
        {
            var r = MemoryBoost.Boost(trim, 0, "", false);
            try { MemoryBoost.Remember(host.Settings, r); } catch { }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                button.IsEnabled = true;
                Say(MemoryBoost.Describe(r) + (trim ? "" : " " + L.T("Программы при этом не сжимались — это включается в разделе «Память в игре».")));
            }));
        });
    }

    private void CloseProgram(string name, Button button)
    {
        string pretty = WhySlow.Pretty(name);
        if (MessageBox.Show(this, L.F("Закрыть «{0}»?\n\nПрограмма закроется так же, как по крестику в её окне. Если в ней есть несохранённые данные, она может спросить, что с ними сделать.", pretty),
                L.T("Почему тормозит"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        button.IsEnabled = false;
        Say(L.F("Закрываю «{0}»…", pretty));
        Task.Run(() =>
        {
            int left = WhySlow.Close(name);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // −1: закрыть нельзя — это идущая игра (её могли запустить после замера), системная или защитная программа.
                if (left < 0) { button.IsEnabled = true; Say(L.F("«{0}» программа не закрывает: это игра, часть Windows или защитная программа.", pretty)); return; }
                if (left == 0) { Say(L.F("«{0}» закрыта. Нажмите «Проверить ещё раз», чтобы увидеть, стало ли легче.", pretty)); return; }
                // Программа не закрылась сама (спросила о сохранении, свернулась к часам или зависла) — решает человек.
                if (MessageBox.Show(this, L.F("«{0}» не закрылась сама: осталось процессов — {1}. Возможно, она ждёт ответа в своём окне или свернулась к часам.\n\nЗавершить её принудительно? Несохранённые данные в ней пропадут.", pretty, left),
                        L.T("Почему тормозит"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                {
                    button.IsEnabled = true;
                    Say(L.F("«{0}» оставлена как есть.", pretty));
                    return;
                }
                Say(L.F("Завершаю «{0}»…", pretty));
                Task.Run(() =>
                {
                    int still = WhySlow.Kill(name);
                    Dispatcher.BeginInvoke(new Action(() =>
                        Say(still == 0 ? L.F("«{0}» завершена.", pretty)
                            : still < 0 ? L.F("«{0}» программа не закрывает: это игра, часть Windows или защитная программа.", pretty) : L.F("«{0}» завершить не удалось: Windows не дала (нужны права администратора или программа защищена).", pretty))));
                });
            }));
        });
    }

    private void Say(string text)
    {
        ActionResult.Text = text;
        ActionResult.Visibility = Visibility.Visible;
        Motion.Reveal(ActionResult, 0, 4);
    }

    private void Again_Click(object sender, RoutedEventArgs e) => Start();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
