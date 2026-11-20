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
                Conditions = (r.PowerPlan.Length > 0 ? $"схема питания «{r.PowerPlan}» · " : "") + $"открыто программ: {r.OpenApps}"
                             + (r.Disturbed ? $" · замеру мешали другие программы: {r.OtherLoad:0} % процессора" : ""),
            });
        // По умолчанию сравниваются два последних: предпоследний — «было», последний — «стало».
        if (keep) { foreach (var r in rows) { r.IsBefore = r.Result.At == before; r.IsAfter = r.Result.At == after; } }
        else if (rows.Count >= 2) { rows[1].IsBefore = true; rows[0].IsAfter = true; }

        ListBlock.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        State.Text = rows.Count == 0 ? "Замеров пока нет."
            : rows.Count == 1 ? $"Есть один замер: {rows[0].Title}. Для сравнения нужен второй."
            : $"Замеров: {rows.Count}. Последний — {rows[0].Title}.";
        if (!labelTouched && !busy) LabelBox.Text = rows.Count == 0 ? "до" : "после";
        if (note != null) ShowResult(note, false);
        ShowComparison();
    }

    private static string Quote(string label) => string.IsNullOrWhiteSpace(label) ? "без метки" : $"«{label.Trim()}»";

    private static string When(DateTime at) =>
        at.Date == DateTime.Today ? $"сегодня в {at:HH:mm}" : at.Date == DateTime.Today.AddDays(-1) ? $"вчера в {at:HH:mm}" : $"{at:d MMM, HH:mm}";

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
            host.Ui(() => Fill(list, $"Замер {title} удалён."));
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
        CompareTitle.Source = $"СРАВНЕНИЕ: {Quote(before.Result.Label)} → {Quote(after.Result.Label)}".ToUpperInvariant();
        if (before == after)
        {
            Deltas.ItemsSource = null;
            Summary.Text = "Для сравнения отметьте два разных замера: один в столбце «было», другой в столбце «стало».";
            return;
        }

        var deltas = Bench.Compare(before.Result, after.Result);
        Deltas.ItemsSource = deltas.Select(d => new DeltaRow
        {
            Title = d.Metric.Title, Hint = d.Metric.Hint, Before = d.BeforeText, After = d.AfterText, Change = d.ChangeText,
            Word = d.Verdict > 0 ? "лучше" : d.Verdict < 0 ? "хуже" : "без изменений",
            // Улучшение — тушью, ухудшение — цветом предупреждения с маркером; шум — приглушённо.
            Brush = d.Verdict > 0 ? Palette.Ink1 : d.Verdict < 0 ? Palette.Warn : Palette.Ink3,
            ChangeBrush = d.Verdict > 0 ? Palette.Ink1 : d.Verdict < 0 ? Palette.Warn : Palette.Ink4,
            Weight = d.Verdict == 0 ? FontWeights.Normal : FontWeights.SemiBold,
            Marker = d.Verdict < 0 ? Visibility.Visible : Visibility.Collapsed,
        }).ToList();
        string order = before.Result.At > after.Result.At ? " Замер «было» сделан позже, чем «стало»: если это случайно, поменяйте отметки местами." : "";
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
            ? "Идёт игра. Замер отнял бы у неё процессор и диск, поэтому кнопка включится, когда игра закроется."
            : running ? "Идёт замер. Не трогайте компьютер: движение мыши и открытые окна искажают числа."
            : "Перед замером закройте игры и на полминуты оставьте компьютер в покое: движение мыши и работающие программы искажают числа.";
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (!LabelBox.IsEnabled) return;
        LabelBox.Text = (string)((FrameworkElement)sender).Tag;
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
        ProgressPct.Text = "0 %";
        ProgressStage.Text = "Замер начинается…";
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
                note = $"Замер {Quote(label)} сохранён.";
            }
            catch (OperationCanceledException)
            {
                note = token.IsCancellationRequested ? "Замер отменён и не сохранён." : "Замер остановлен: началась игра. Повторите его, когда игра закроется.";
                warn = !token.IsCancellationRequested;
            }
            catch (InvalidOperationException ex) { note = ex.Message; warn = true; }
            catch (Exception ex) { note = "Замер не удался: " + ex.Message + " Попробуйте ещё раз."; warn = true; }

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
            ProgressStage.Text = p.Fraction >= 1 ? "Готово" : $"Этап {p.Stage} из {p.Stages} · {p.Title}";
            ProgressPct.Text = $"{p.Fraction * 100:0} %";
            ProgressBar.Target = p.Fraction;
        });
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        try { cancel?.Cancel(); } catch { }
        BtnCancel.IsEnabled = false;
        ProgressStage.Text = "Останавливаю замер…";
        host.Ui(() => BtnCancel.IsEnabled = true);
    }

    private void ShowResult(string text, bool warn)
    {
        Result.Text = text;
        Result.Foreground = warn ? Palette.Warn : Palette.Ink3;
        Result.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
