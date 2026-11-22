using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PulsePC.Blueprint;
using PulsePC.Services;

namespace PulsePC;

/// <summary>
/// Первичный осмотр: при первом запуске проходит по системе (железо, Windows, игры, сеть, журналы) и показывает
/// паспорт ПК и замечания со ссылками на разделы программы. Ничего не меняет.
/// Использование: <c>var w = new ScanWindow { Owner = this }; w.NavigateRequested += SelectTab; w.Show();</c>
/// Осмотр начинается сам, когда окно загрузится.
/// </summary>
public partial class ScanWindow : Window
{
    /// <summary>Нажата ссылка «Открыть раздел»: номер страницы главного окна. Окно после этого закрывается.</summary>
    public event Action<int> NavigateRequested;

    /// <summary>Нажата ссылка на раздел вкладки «Система» («Драйвер», «Питание»…). Окно после этого закрывается.</summary>
    public event Action<string> SectionRequested;

    /// <summary>Осмотр закончен и результаты показаны (не вызывается, если окно закрыли раньше).</summary>
    public event Action<ScanReport> Completed;

    /// <summary>Результат осмотра; null, пока осмотр идёт.</summary>
    public ScanReport Report { get; private set; }

    // Минимальная длительность шага на экране: чтобы список успевали прочитать, а не видели мелькание.
    private const int StepMs = 320;

    private static readonly string[] StepHints =
    {
        L.T("модель, ядра и частота"), L.T("модель, драйвер и видеопамять"), L.T("планки, частота и каналы"), L.T("модель и версия BIOS"),
        L.T("диски, свободное место и состояние"), L.T("разрешение и частота обновления"), L.T("версия, схема питания и игровые настройки"),
        L.T("Steam, Epic Games, Riot и настройки графики"), L.T("установленные программы и автозагрузка"), L.T("пинг до роутера и до интернета"),
        L.T("временные файлы и кэш"), L.T("сбои и ошибки за 7 дней"),
    };

    private readonly CancellationTokenSource cancel = new();
    private readonly List<StepRow> rows = new();
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Stopwatch watch = new();
    private bool started;

    public ScanWindow()
    {
        InitializeComponent();
        // На ноутбуке осмотр рисует ноутбук, а не системный блок (точки выносок у сцен общие).
        try { if (PulsePC.Services.LaptopCheck.Applies) Drawing.Scene = PulsePC.Blueprint.Scene.Laptop; } catch { }
        WindowFit.Attach(this);
        Motion.Init(this);
        Header.Line2 = L.F("ПЕРВЫЙ ЗАПУСК · {0:d}", DateTime.Now);
        for (int i = 0; i < SystemScan.StepTitles.Length; i++)
        {
            var row = new StepRow(i, L.T(SystemScan.StepTitles[i]));
            rows.Add(row);
            StepList.Children.Add(row.Root);
        }
        Progress.Steps = SystemScan.StepTitles.Length;
        StepCounter.Text = $"00 / {SystemScan.StepTitles.Length:00}";
        Elapsed.Text = "0:00";
        clock.Tick += (_, _) => Elapsed.Text = $"{(int)watch.Elapsed.TotalMinutes}:{watch.Elapsed.Seconds:00}";
        Loaded += OnLoaded;
        Closed += (_, _) => { cancel.Cancel(); clock.Stop(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && Report != null) Close(); };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (started) return;
        started = true;
        Header.Play();
        Drawing.Play();
        watch.Start();
        clock.Start();

        ScanReport report = null;
        try
        {
            report = await SystemScan.RunAsync(p => Dispatcher.BeginInvoke(new Action(() => OnProgress(p))), cancel.Token, StepMs);
        }
        catch { }
        if (cancel.IsCancellationRequested) return;
        watch.Stop();
        clock.Stop();
        report ??= new ScanReport();
        Report = report;

        // Последний шаг и выноски должны успеть показаться, прежде чем лист сменится.
        Drawing.Highlight = Part.None;
        NowText.Text = L.T("Осмотр закончен");
        await Task.Delay(Motion.On ? 700 : 150);
        if (cancel.IsCancellationRequested) return;
        ShowResults(report);
        try { Completed?.Invoke(report); } catch { }
    }

    // ───────────────────────────── фаза 1: ход осмотра ─────────────────────────────

    private void OnProgress(ScanProgress p)
    {
        if (p.Index < 0 || p.Index >= rows.Count || cancel.IsCancellationRequested) return;
        var row = rows[p.Index];
        if (!p.Done)
        {
            row.SetRunning();
            StepCounter.Text = $"{p.Index + 1:00} / {p.Count:00}";
            NowText.Text = L.T(p.Title) + ": " + (p.Index < StepHints.Length ? StepHints[p.Index] : "");
            Drawing.Highlight = PartOf(p.Index);
            Progress.Running = p.Index;
            return;
        }

        row.SetDone(p.Summary, p.Worst, p.Failed);
        Progress.Done = p.Index + 1;
        Motion.To(Progress, ScanRuler.ValueProperty, (p.Index + 1) / (double)p.Count, Motion.Normal);
        var (anchor, label) = CalloutOf(p.Index);
        if (anchor != null && !p.Failed)
            Drawing.SetCallout(anchor, label, CalloutValue(p.Index, p.Summary), p.Worst <= Severity.Warn, compact: true);
    }

    private static Part PartOf(int step) => step switch
    {
        0 => Part.Cpu, 1 => Part.Gpu, 2 => Part.Ram, 3 => Part.Motherboard, 4 => Part.Storage, _ => Part.None,
    };

    private static (string Anchor, string Label) CalloutOf(int step) => step switch
    {
        0 => ("pc.cpu", L.T("А  Процессор")),
        1 => ("pc.gpu", L.T("Б  Видеокарта")),
        2 => ("pc.ram", L.T("В  Память")),
        3 => ("pc.board", L.T("Д  Материнская плата")),
        4 => ("pc.storage", L.T("Г  Диски")),
        _ => (null, null),
    };

    /// <summary>Короткая подпись к выноске: слева от рисунка места мало (~110 px), справа — ~160 px.</summary>
    private static string CalloutValue(int step, string summary)
    {
        summary ??= "";
        string s = step switch
        {
            0 => System.Text.RegularExpressions.Regex.Replace(summary.Split(" · ")[0], @"^(Intel|AMD)\s+", ""),
            1 => System.Text.RegularExpressions.Regex.Replace(summary, @"^(GeForce|Radeon)\s+", ""),
            3 => summary.Split(" · BIOS")[0],
            4 => L.En ? summary.Replace(" free", "") : summary.Replace(" свободно ", " "),
            _ => summary,
        };
        int max = step is 2 or 3 ? 24 : 20;
        return s.Length > max ? s[..max].TrimEnd() + "…" : s;
    }

    // ───────────────────────────── фаза 2: паспорт и замечания ─────────────────────────────

    private void ShowResults(ScanReport r)
    {
        Header.Play(L.T("ОСМОТР"), L.T("ПЕРВИЧНЫЙ · ИТОГ"), L.F("{0:d} · {1} С", r.At, Math.Max(1, (int)Math.Round(r.Duration.TotalSeconds))));
        ScanPhase.Visibility = Visibility.Collapsed;
        ResultPhase.Visibility = Visibility.Visible;

        // Паспорт
        PassportGrid.Children.Clear();
        PassportGrid.RowDefinitions.Clear();
        int n = 0;
        foreach (var line in r.Passport)
        {
            PassportGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TrackedText { Source = line.Label.ToUpper(L.Culture), Margin = new Thickness(0, 11, 12, 8), VerticalAlignment = VerticalAlignment.Top };
            label.SetResourceReference(StyleProperty, "Label");
            // Первая строка — главное (модель), остальные — подробности мельче и светлее.
            var value = new TextBlock { Margin = new Thickness(0, 7, 0, 8), LineHeight = 19, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
            value.SetResourceReference(StyleProperty, "Body");
            value.SetResourceReference(TextBlock.ForegroundProperty, "Ink2");
            var parts = line.Value.Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) value.Inlines.Add(new LineBreak());
                var run = new Run(parts[i]);
                if (i == 0) run.SetResourceReference(TextElement.ForegroundProperty, "Ink1");
                else run.FontSize = 12.5;
                value.Inlines.Add(run);
            }
            var rule = new Rectangle { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, SnapsToDevicePixels = true };
            rule.SetResourceReference(Shape.FillProperty, "Line");
            Grid.SetRow(label, n); Grid.SetRow(value, n); Grid.SetRow(rule, n);
            Grid.SetColumn(value, 1); Grid.SetColumnSpan(rule, 2);
            PassportGrid.Children.Add(rule);
            PassportGrid.Children.Add(label);
            PassportGrid.Children.Add(value);
            Motion.Reveal(label, 80 + n * Motion.Stagger, 6);
            Motion.Reveal(value, 80 + n * Motion.Stagger, 6);
            n++;
        }

        // Счёт замечаний
        Tally.Children.Clear();
        void Count(Severity s, string one, string few, string many)
        {
            int c = r.Count(s);
            if (c == 0) return;
            var marker = new StateMarker { Level = s, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Tally.Children.Count == 0 ? 0 : 16, 0, 7, 0) };
            var text = new TextBlock { Text = Fmt.Plural(c, one, few, many), VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(StyleProperty, "Caption");
            Tally.Children.Add(marker);
            Tally.Children.Add(text);
        }
        Count(Severity.Bad, "серьёзное", "серьёзных", "серьёзных");
        Count(Severity.Warn, "важное", "важных", "важных");
        Count(Severity.Info, "к сведению", "к сведению", "к сведению");

        // Шаги, которые не удалось выполнить: о них — первым делом, иначе пустой список выглядел бы как «всё в порядке».
        var failedSteps = r.Steps.Where(s => s.Error != null).ToList();
        if (failedSteps.Count > 0)
        {
            var marker = new StateMarker { Level = Severity.Warn, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Tally.Children.Count == 0 ? 0 : 16, 0, 7, 0) };
            var text = new TextBlock { Text = L.F("не проверено: {0}", failedSteps.Count), VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(StyleProperty, "Caption");
            Tally.Children.Add(marker);
            Tally.Children.Add(text);
        }

        // Замечания
        FindingList.Children.Clear();
        if (failedSteps.Count > 0)
        {
            var row = FindingRow(new ScanFinding
            {
                Severity = Severity.Warn,
                Title = Fmt.Plural(failedSteps.Count, "проверка не выполнена", "проверки не выполнены", "проверок не выполнено"),
                Detail = L.F("Не удалось проверить: {0}. По этим пунктам итог неизвестен — повторите осмотр позже или после перезапуска программы.",
                    string.Join(", ", failedSteps.Select(s => "«" + L.T(s.Title) + "»"))),
            });
            FindingList.Children.Add(row);
            Motion.Reveal(row, 120, 6);
        }
        if (r.Findings.Count == 0)
        {
            var empty = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
            var t = new TextBlock { Text = failedSteps.Count > 0 ? L.T("В выполненных проверках замечаний нет") : L.T("Замечаний нет") };
            t.SetResourceReference(StyleProperty, "RowTitle");
            var d = new TextBlock
            {
                Text = failedSteps.Count > 0
                    ? L.T("Остальное, что можно проверить без нагрузки, в порядке. Как ведёт себя компьютер в играх, покажут записанные сессии.")
                    : L.T("Всё, что можно проверить без нагрузки, в порядке. Как ведёт себя компьютер в играх, покажут записанные сессии."),
                Margin = new Thickness(0, 4, 0, 0),
            };
            d.SetResourceReference(StyleProperty, "Body");
            empty.Children.Add(t);
            empty.Children.Add(d);
            FindingList.Children.Add(empty);
            Motion.Reveal(empty, 120, 6);
        }
        int k = 0;
        foreach (var f in r.Findings)
        {
            var row = FindingRow(f);
            FindingList.Children.Add(row);
            Motion.Reveal(row, 140 + Math.Min(k, 10) * Motion.Stagger, 8);
            k++;
        }

        FooterText.Text = L.F("Осмотр занял {0} с и ничего не изменил: исправления — только в программе и по вашей команде.", Math.Max(1, (int)Math.Round(r.Duration.TotalSeconds)));
        DoneButton.Visibility = Visibility.Visible;
        Motion.Reveal(DoneButton, 200, 4);
        DoneButton.Focus();
    }

    private FrameworkElement FindingRow(ScanFinding f)
    {
        var border = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 11, 2, 12) };
        border.SetResourceReference(Border.BorderBrushProperty, "Line");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var marker = new StateMarker { Level = f.Severity, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 14, 0) };
        grid.Children.Add(marker);

        var stack = new StackPanel();
        Grid.SetColumn(stack, 1);
        var title = new TextBlock { Text = f.Title };
        title.SetResourceReference(StyleProperty, "RowTitle");
        var detail = new TextBlock { Text = f.Detail, Margin = new Thickness(0, 3, 0, 0) };
        detail.SetResourceReference(StyleProperty, "Body");
        stack.Children.Add(title);
        stack.Children.Add(detail);
        // Замечание из журналов Windows — «Подробнее и что делать»: записи простыми словами, план и открытие в «Просмотре событий».
        if (f.Kind != "" && LogMarks.Kinds.ContainsKey(f.Kind))
        {
            string kind = f.Kind, title0 = f.Title;
            var level = f.Severity;
            var more = new Button { Content = L.T("Подробнее и что делать"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 5, 0, 0) };
            more.SetResourceReference(StyleProperty, "Link");
            more.SetResourceReference(Control.ForegroundProperty, "Ink1");
            more.Click += (_, _) => new LogDetailsWindow(this, kind, title0, level, null, LogMarks.Settings).Show();
            stack.Children.Add(more);
        }
        if (f.TargetPage >= 0 && f.PageName != "")
        {
            int page = f.TargetPage;
            string section = f.TargetSection;
            var link = new Button { Content = f.TargetSection != "" ? L.F("Открыть раздел «{0}»", L.T(f.PageName)) : L.F("Открыть вкладку «{0}»", L.T(f.PageName)), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 5, 0, 0) };
            link.SetResourceReference(StyleProperty, "Link");
            link.SetResourceReference(Control.ForegroundProperty, "Ink1");
            link.Click += (_, _) =>
            {
                if (section != "" && SectionRequested != null)
                {
                    try { SectionRequested(section); } catch { }
                    Close();
                }
                else Navigate(page);
            };
            stack.Children.Add(link);
        }
        grid.Children.Add(stack);
        border.Child = grid;
        return border;
    }

    private void Navigate(int page)
    {
        try { NavigateRequested?.Invoke(page); } catch { }
        Close();
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ───────────────────────────── отдельный запуск ─────────────────────────────

    /// <summary>
    /// Показать окно осмотра без главного окна (для проверки): создаёт ресурсы программы, если их ещё нет,
    /// и ждёт закрытия окна. Вызывать из потока STA.
    /// </summary>
    public static void RunStandalone()
    {
        if (Application.Current == null)
        {
            // Нужны только ресурсы App.xaml. Конструктор Application ставит в очередь диспетчера вызов OnStartup,
            // который открыл бы главное окно, — эту операцию снимаем с очереди, пока она не выполнилась.
            var dispatcher = Dispatcher.CurrentDispatcher;
            var posted = new List<DispatcherOperation>();
            DispatcherHookEventHandler hook = (_, e) => posted.Add(e.Operation);
            dispatcher.Hooks.OperationPosted += hook;
            App app;
            try { app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; }
            finally { dispatcher.Hooks.OperationPosted -= hook; }
            foreach (var op in posted) op.Abort();
            app.InitializeComponent();
        }
        var window = new ScanWindow { WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = true };
        var frame = new DispatcherFrame();
        window.Closed += (_, _) => frame.Continue = false;
        window.Show();
        Dispatcher.PushFrame(frame);
    }

    // ───────────────────────────── строка шага ─────────────────────────────

    private sealed class StepRow
    {
        public readonly Border Root;
        private readonly StepBox box = new() { VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock number, title, result;
        private readonly StateMarker mark = new() { VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, Margin = new Thickness(10, 0, 0, 0) };

        public StepRow(int index, string text)
        {
            Root = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Height = 35 };
            Root.SetResourceReference(Border.BorderBrushProperty, "Line");
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(138) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            g.Children.Add(box);
            number = new TextBlock { Text = (index + 1).ToString("00"), VerticalAlignment = VerticalAlignment.Center };
            number.SetResourceReference(StyleProperty, "Stamp");
            number.SetResourceReference(TextBlock.ForegroundProperty, "Ink4");
            Grid.SetColumn(number, 1);
            g.Children.Add(number);

            title = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Ink3");
            Grid.SetColumn(title, 2);
            g.Children.Add(title);

            result = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
            result.SetResourceReference(StyleProperty, "Caption");
            Grid.SetColumn(result, 3);
            g.Children.Add(result);

            Grid.SetColumn(mark, 4);
            g.Children.Add(mark);
            Root.Child = g;
        }

        public void SetRunning()
        {
            box.State = StepState.Running;
            title.SetResourceReference(TextBlock.ForegroundProperty, "Ink1");
            title.FontWeight = FontWeights.SemiBold;
            number.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            result.Text = L.T("проверяю…");
            result.SetResourceReference(TextBlock.ForegroundProperty, "Ink3");
        }

        public void SetDone(string summary, Severity worst, bool failed)
        {
            box.State = failed ? StepState.Failed : StepState.Done;
            title.FontWeight = FontWeights.Normal;
            title.SetResourceReference(TextBlock.ForegroundProperty, "Ink1");
            number.SetResourceReference(TextBlock.ForegroundProperty, "Ink3");
            result.SetResourceReference(TextBlock.ForegroundProperty, failed ? "Ink3" : "Ink2");
            result.ToolTip = string.IsNullOrEmpty(summary) ? null : summary;
            Motion.SetText(result, summary);
            if (worst <= Severity.Warn)
            {
                mark.Level = worst;
                mark.Visibility = Visibility.Visible;
            }
        }
    }
}

public enum StepState { Pending, Running, Done, Failed }

/// <summary>
/// Квадрат шага осмотра 10×10: ждёт — пунктирный контур, идёт — красная штриховка (живой сигнал),
/// готово — залит тушью, не удалось — контур с чертой.
/// </summary>
public class StepBox : FrameworkElement
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(StepState), typeof(StepBox), new FrameworkPropertyMetadata(StepState.Pending, FrameworkPropertyMetadataOptions.AffectsRender));
    public StepState State { get => (StepState)GetValue(StateProperty); set => SetValue(StateProperty, value); }

    protected override Size MeasureOverride(Size s) => new(10, 10);

    protected override void OnRender(DrawingContext dc)
    {
        var r = new Rect(0.5, 0.5, 9, 9);
        switch (State)
        {
            case StepState.Pending:
                dc.DrawRectangle(null, Dashed(Palette.Ink4), r);
                break;
            case StepState.Running:
                var red = Ink.Pen(Palette.Accent, 1);
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, 10, 10)));
                for (double x = -10; x < 10; x += 3) dc.DrawLine(red, new Point(x, 10), new Point(x + 10, 0));
                dc.Pop();
                dc.DrawRectangle(null, red, r);
                break;
            case StepState.Done:
                dc.DrawRectangle(Palette.Ink1, null, new Rect(0, 0, 10, 10));
                break;
            default:
                var pen = Ink.Pen(Palette.Ink3, 1);
                dc.DrawRectangle(null, pen, r);
                dc.DrawLine(pen, new Point(2, 5), new Point(8, 5));
                break;
        }
    }

    private static Pen Dashed(Brush b)
    {
        var p = new Pen(b, 1) { DashStyle = new DashStyle(new[] { 2.0, 1.0 }, 0) };
        p.Freeze();
        return p;
    }
}

/// <summary>
/// Линейка хода осмотра: деления по числу шагов, пройденная часть — красная черта над основанием,
/// текущий шаг отмечен короткой красной засечкой.
/// </summary>
public class ScanRuler : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ScanRuler), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    private int steps = 12, running = -1, done;
    public int Steps { get => steps; set { steps = Math.Max(1, value); InvalidateVisual(); } }
    public int Running { get => running; set { running = value; InvalidateVisual(); } }
    public int Done { get => done; set { done = value; InvalidateVisual(); } }

    protected override Size MeasureOverride(Size s) => new(0, 22);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 20) return;
        double baseY = Math.Round(h - 4) + 0.5;
        var ink = Ink.Pen(Palette.Ink3, 1);
        var fine = Ink.Pen(Palette.Line, 1);
        dc.DrawLine(Ink.Pen(Palette.LineStrong, 1), new Point(0, baseY), new Point(w, baseY));

        // мелкие деления — по четыре на шаг, на границах шагов — длинные
        int sub = 4;
        for (int i = 0; i <= steps * sub; i++)
        {
            double x = Math.Round(i * (w - 1) / (steps * sub)) + 0.5;
            bool major = i % sub == 0;
            dc.DrawLine(major ? ink : fine, new Point(x, baseY - (major ? 8 : 4)), new Point(x, baseY));
        }

        // текущий шаг: красная засечка над его отрезком
        if (running >= 0 && running >= done && running < steps)
        {
            double x0 = running * (w - 1) / steps, x1 = (running + 1) * (w - 1) / steps;
            dc.DrawRectangle(Palette.RedTint, null, new Rect(x0 + 1, baseY - 8, Math.Max(0, x1 - x0 - 1), 8));
        }

        // пройденная часть
        double fx = Math.Clamp(Value, 0, 1) * (w - 1);
        if (fx > 0.5) dc.DrawRectangle(Palette.Accent, null, new Rect(0, baseY - 1.5, fx, 2.5));
    }
}
