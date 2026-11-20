using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PulsePC.Services;

namespace PulsePC.Panels;

/// <summary>Раздел «Замер до и после»: кнопка замера с меткой, список замеров, сравнение двух выбранных.</summary>
public partial class BenchPanel : UserControl, ISystemPanel
{
    /// <summary>Строка списка замеров.</summary>
    public sealed class Row : Notifier
    {
        public BenchResult Result { get; init; }
        public string Title { get; init; }
        public string Conditions { get; init; }
        private bool isBefore, isAfter;
        public bool IsBefore { get => isBefore; set => Set(ref isBefore, value); }
        public bool IsAfter { get => isAfter; set => Set(ref isAfter, value); }
    }

    /// <summary>Строка таблицы сравнения.</summary>
    public sealed class DeltaRow
    {
        public string Title { get; init; }
        public string Hint { get; init; }
        public string Before { get; init; }
        public string After { get; init; }
        public string Word { get; init; }
        public string Change { get; init; }
        public Brush Brush { get; init; }
        public Brush ChangeBrush { get; init; }
        public FontWeight Weight { get; init; }
        public Visibility Marker { get; init; }
    }

    private readonly ObservableCollection<Row> rows = new();
    private PanelHost host;
    private CancellationTokenSource cancel;
    private bool labelTouched, busy;
    private double shownFraction = -1;
    private int shownStage = -1;

    public BenchPanel()
    {
        InitializeComponent();
        Rows.ItemsSource = rows;
        LabelBox.TextChanged += (_, _) => { if (LabelBox.IsKeyboardFocused) labelTouched = true; };
    }

    public void Attach(PanelHost host)
    {
        this.host = host;
        // Началась или закончилась игра — кнопка замера выключается и включается сама.
        GameHooks.Started += (_, _) => host.Ui(UpdateRunState);
        GameHooks.Ended += () => host.Ui(UpdateRunState);
        UpdateRunState();
        Reload();
    }

    public void Shown()
    {
        UpdateRunState();
        if (!busy && !Bench.Running) Reload();
    }

    // ───────────── Список ─────────────

    private void Reload(string note = null)
    {
        Task.Run(() =>
        {
            var list = Bench.Load();
            host.Ui(() => Fill(list, note));
        });
    }

    private void Fill(List<BenchResult> list, string note)
    {
        DateTime? before = rows.FirstOrDefault(r => r.IsBefore)?.Result.At, after = rows.FirstOrDefault(r => r.IsAfter)?.Result.At;
        bool keep = note == null && before != null && after != null && list.Any(r => r.At == before) && list.Any(r => r.At == after);

        rows.Clear();
        foreach (var r in list)
            rows.Add(new Row
            {
                Result = r,
                Title = $"{Quote(r.Label)} · {When(r.At)}",
                Conditions = (r.PowerPlan.Length > 0 ? L.F("схема питания «{0}»", r.PowerPlan) + " · " : "") + L.F("открыто программ: {0}", r.OpenApps)
                             + (r.Disturbed ? " · " + L.F("замеру мешали другие программы: {0:0} % процессора", r.OtherLoad) : ""),
            });
        // По умолчанию сравниваются два последних: предпоследний — «было», последний — «стало».
        if (keep) { foreach (var r in rows) { r.IsBefore = r.Result.At == before; r.IsAfter = r.Result.At == after; } }
        else if (rows.Count >= 2) { rows[1].IsBefore = true; rows[0].IsAfter = true; }

        ListBlock.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        State.Text = rows.Count == 0 ? L.T("Замеров пока нет.")
            : rows.Count == 1 ? L.F("Есть один замер: {0}. Для сравнения нужен второй.", rows[0].Title)
            : L.F("Замеров: {0}. Последний — {1}.", rows.Count, rows[0].Title);
        // Метка — текст пользователя (сохраняется как есть); подставляется на языке интерфейса.
        if (!labelTouched && !busy) LabelBox.Text = rows.Count == 0 ? L.T("до") : L.T("после");
        if (note != null) ShowResult(note, false);
        ShowComparison();
    }

    private static string Quote(string label) =>
        string.IsNullOrWhiteSpace(label) ? L.T("без метки")
        // Метки по умолчанию «до» и «после» — на языке интерфейса; свои метки пользователя не переводятся.
        : L.Pick($"«{label.Trim()}»", $"“{(label.Trim() is "до" or "после" ? L.T(label.Trim()) : label.Trim())}”");

    private static string When(DateTime at) =>
        at.Date == DateTime.Today ? L.F("сегодня в {0:HH:mm}", at)
        : at.Date == DateTime.Today.AddDays(-1) ? L.F("вчера в {0:HH:mm}", at)
        : L.Pick($"{at:d MMM, HH:mm}", at.ToString("MMM d, h:mm tt", L.Culture));

    private void PickBefore_Click(object sender, RoutedEventArgs e) => Pick(sender, before: true);
    private void PickAfter_Click(object sender, RoutedEventArgs e) => Pick(sender, before: false);

    private void Pick(object sender, bool before)
    {
        if ((sender as FrameworkElement)?.DataContext is not Row picked) return;
        foreach (var r in rows)
        {
            if (before) r.IsBefore = r == picked; else r.IsAfter = r == picked;
        }
        ShowComparison();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Row row || busy || Bench.Running) return;
        var at = row.Result.At;
        string title = row.Title;
        Task.Run(() =>
        {
            Bench.Remove(at);
            var list = Bench.Load();
            host.Ui(() => Fill(list, L.F("Замер {0} удалён.", title)));
        });
    }

    // ───────────── Сравнение ─────────────

    private void ShowComparison()
    {
        var before = rows.FirstOrDefault(r => r.IsBefore);
        var after = rows.FirstOrDefault(r => r.IsAfter);
        if (before == null || after == null || rows.Count < 2)
        {
            CompareBlock.Visibility = Visibility.Collapsed;
            return;
        }
        CompareBlock.Visibility = Visibility.Visible;
        CompareTitle.Source = L.F("СРАВНЕНИЕ: {0} → {1}", Quote(before.Result.Label), Quote(after.Result.Label)).ToUpper(L.Culture);
        if (before == after)
        {
            Deltas.ItemsSource = null;
            Summary.Text = L.T("Для сравнения отметьте два разных замера: один в столбце «было», другой в столбце «стало».");
            return;
        }

        var deltas = Bench.Compare(before.Result, after.Result);
        Deltas.ItemsSource = deltas.Select(d => new DeltaRow
        {
            Title = d.Metric.TitleText, Hint = d.Metric.HintText, Before = d.BeforeText, After = d.AfterText, Change = d.ChangeText,
            Word = d.Verdict > 0 ? L.T("лучше") : d.Verdict < 0 ? L.T("хуже") : L.T("без изменений"),
            // Улучшение — тушью, ухудшение — цветом предупреждения с маркером; шум — приглушённо.
            Brush = d.Verdict > 0 ? Palette.Ink1 : d.Verdict < 0 ? Palette.Warn : Palette.Ink3,
            ChangeBrush = d.Verdict > 0 ? Palette.Ink1 : d.Verdict < 0 ? Palette.Warn : Palette.Ink4,
            Weight = d.Verdict == 0 ? FontWeights.Normal : FontWeights.SemiBold,
            Marker = d.Verdict < 0 ? Visibility.Visible : Visibility.Collapsed,
        }).ToList();
        string order = before.Result.At > after.Result.At ? " " + L.T("Замер «было» сделан позже, чем «стало»: если это случайно, поменяйте отметки местами.") : "";
        Summary.Text = Bench.Summary(before.Result, after.Result, deltas) + order;
    }

    // ───────────── Замер ─────────────

    private void UpdateRunState()
    {
        if (host == null) return;
        bool running = busy || Bench.Running, game = GameHooks.InGame;
        BtnRun.IsEnabled = !running && !game;
        BtnCancel.Visibility = running && cancel != null ? Visibility.Visible : Visibility.Collapsed;
        LabelBox.IsEnabled = !running;
        RunNote.Text = game
            ? L.T("Идёт игра. Замер отнял бы у неё процессор и диск, поэтому кнопка включится, когда игра закроется.")
            : running ? L.T("Идёт замер. Не трогайте компьютер: движение мыши и открытые окна искажают числа.")
            : L.T("Перед замером закройте игры и на полминуты оставьте компьютер в покое: движение мыши и работающие программы искажают числа.");
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (!LabelBox.IsEnabled) return;
        LabelBox.Text = L.T((string)((FrameworkElement)sender).Tag);       // Tag — «до» / «после»
        labelTouched = true;
    }

    private void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        if (host == null || busy || Bench.Running || GameHooks.InGame) { UpdateRunState(); return; }
        string label = LabelBox.Text.Trim();
        busy = true;
        cancel = new CancellationTokenSource();
        var token = cancel.Token;
        shownFraction = -1; shownStage = -1;
        ProgressBar.Value = 0; ProgressBar.Target = 0;
        ProgressPct.Text = L.Pick("0 %", "0%");
        ProgressStage.Text = L.T("Замер начинается…");
        ProgressBlock.Visibility = Visibility.Visible;
        Result.Visibility = Visibility.Collapsed;
        BtnRun.IsEnabled = false;
        host.BeginBusy();

        Task.Run(() =>
        {
            string note; bool warn = false;
            try
            {
                var result = Bench.Run(label, OnProgress, token);
                Bench.Add(result);
                note = L.F("Замер {0} сохранён.", Quote(label));
            }
            catch (OperationCanceledException)
            {
                note = token.IsCancellationRequested ? L.T("Замер отменён и не сохранён.") : L.T("Замер остановлен: началась игра. Повторите его, когда игра закроется.");
                warn = !token.IsCancellationRequested;
            }
            catch (InvalidOperationException ex) { note = ex.Message; warn = true; }
            catch (Exception ex) { note = L.F("Замер не удался: {0} Попробуйте ещё раз.", ex.Message); warn = true; }

            var list = Bench.Load();
            host.Ui(() =>
            {
                host.EndBusy();
                busy = false;
                cancel?.Dispose();
                cancel = null;
                labelTouched = false;
                ProgressBlock.Visibility = Visibility.Collapsed;
                Fill(list, note);
                if (warn) ShowResult(note, true);
                UpdateRunState();
            });
        });
        UpdateRunState();
    }

    // Вызывается из потока замера десятки раз в секунду: в интерфейс уходит только заметное изменение.
    private void OnProgress(BenchProgress p)
    {
        if (p.Stage == shownStage && p.Fraction - shownFraction < 0.01 && p.Fraction < 1) return;
        shownStage = p.Stage; shownFraction = p.Fraction;
        host.Ui(() =>
        {
            ProgressStage.Text = p.Fraction >= 1 ? L.T("Готово") : L.F("Этап {0} из {1} · {2}", p.Stage, p.Stages, p.Title);
            ProgressPct.Text = $"{p.Fraction * 100:0}" + L.Pick(" %", "%");
            ProgressBar.Target = p.Fraction;
        });
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        try { cancel?.Cancel(); } catch { }
        BtnCancel.IsEnabled = false;
        ProgressStage.Text = L.T("Останавливаю замер…");
        host.Ui(() => BtnCancel.IsEnabled = true);
    }

    private void ShowResult(string text, bool warn)
    {
        Result.Text = text;
        Result.Foreground = warn ? Palette.Warn : Palette.Ink3;
        Result.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
