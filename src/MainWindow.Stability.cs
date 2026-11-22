using System.Windows;
using System.Windows.Media;
using PulsePC.Services;

namespace PulsePC;

/// <summary>
/// Вкладка «Диагностика», блок «Проверки Windows» (версия 1.5): целостность Windows (DISM /CheckHealth — только чтение),
/// задержки драйверов (30 секунд трассировки ядра) и проверка памяти средством Windows (после подтверждения; перезагрузку
/// человек выбирает в окне Windows). Без прав администратора кнопки выключены.
/// </summary>
public partial class MainWindow
{
    private bool stabLoaded, stabBusy;
    private CancellationTokenSource dpcCancel;

    private sealed class DpcRowVm
    {
        public string Name { get; init; } = "";
        public string Role { get; init; } = "";
        public string Value { get; init; } = "";
        public Brush ValueBrush { get; init; }
    }

    /// <summary>При первом открытии вкладки: прошлые итоги проверок и итог проверки памяти из журнала (в фоне).</summary>
    private void LoadStability()
    {
        StabAdminNote.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
        BtnStabDism.IsEnabled = BtnStabDpc.IsEnabled = BtnStabMem.IsEnabled = admin && !stabBusy;
        if (stabLoaded) return;
        stabLoaded = true;
        bool demo = StabilityChecks.Demo;     // PULSEPC_TEST_STABILITY=1 — подставные итоги для снимков
        var dism = demo ? StabilityChecks.DemoDism() : StabilityChecks.LastDism(settings);
        if (dism != null) ShowDism(dism);
        var dpc = demo ? StabilityChecks.DemoDpc() : DpcLatency.Last(settings);
        if (dpc != null) ShowDpc(dpc);
        Task.Run(() =>
        {
            MemTestResult r = null;
            try { r = demo ? StabilityChecks.DemoMemTest() : StabilityChecks.ReadMemTest(); } catch { }
            OnUi(() => ShowMemTest(r));
        });
    }

    private void ShowMemTest(MemTestResult r)
    {
        string asked = settings.Get(StabilityChecks.MemAskedKey);
        bool pending = DateTime.TryParse(asked, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
                       && (r == null || r.Time < at) && (DateTime.Now - at).TotalDays < 7;
        string text = r == null
            ? (pending ? L.T("Проверка назначена — итог появится здесь после перезагрузки.") : "")
            : (r.Errors ? L.F("Последняя проверка ({0:d MMMM}) нашла ошибки памяти — подробности в списке справа.", r.Time)
                        : L.F("Последняя проверка ({0:d MMMM}): ошибок нет.", r.Time)) + (pending ? " " + L.T("Новая проверка назначена — итог появится после перезагрузки.") : "");
        StabMemResult.Text = text;
        StabMemResult.Foreground = r?.Errors == true ? Palette.Bad : (Brush)FindResource("Ink2");
        StabMemResult.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ целостность
    private async void BtnStabDism_Click(object sender, RoutedEventArgs e)
    {
        if (stabBusy || !admin) return;
        stabBusy = true;
        SetStabButtons(false);
        BtnStabDism.Content = L.T("Проверяю…");
        StabDismResult.Visibility = Visibility.Visible;
        StabDismResult.Text = L.T("DISM проверяет хранилище компонентов Windows. Обычно это меньше минуты.");
        StabDismFix.Visibility = Visibility.Collapsed;
        BeginBusy();
        try
        {
            var r = await Task.Run(StabilityChecks.RunDism);
            StabilityChecks.SaveDism(settings, r);
            ShowDism(r);
            UpsertDiag(StabilityChecks.DismFinding(r), x => x.Title == L.T("Целостность Windows в порядке") || x.Title == L.T("Файлы Windows повреждены"));
        }
        finally
        {
            EndBusy();
            BtnStabDism.Content = L.T("Проверить целостность");
            stabBusy = false;
            SetStabButtons(true);
        }
    }

    private void ShowDism(DismResult r)
    {
        StabDismResult.Visibility = Visibility.Visible;
        StabDismResult.Text = StabilityChecks.DismText(r);
        bool broken = r.State is DismState.Repairable or DismState.NotRepairable;
        StabDismResult.Foreground = (Brush)FindResource(broken ? "Ink1" : "Ink2");
        StabDismCommands.Text = StabilityChecks.RepairCommands;
        StabDismFix.Visibility = broken ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnStabCopy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(StabilityChecks.RepairCommands); StabDismResult.Text = StabilityChecks.DismText(StabilityChecks.LastDism(settings) ?? new DismResult()) + " " + L.T("Команды скопированы."); }
        catch { }
    }

    // ------------------------------------------------------------------ задержки драйверов
    private async void BtnStabDpc_Click(object sender, RoutedEventArgs e)
    {
        if (stabBusy || !admin) return;
        if (GameHooks.InGame)
        {
            StabDpcResult.Visibility = Visibility.Visible;
            StabDpcResult.Text = L.T("Идёт игра — проверку лучше сделать, когда игра закрыта.");
            return;
        }
        stabBusy = true;
        SetStabButtons(false);
        dpcCancel = new CancellationTokenSource();
        int seconds = DpcLatency.DefaultSeconds;
        StabDpcResult.Visibility = Visibility.Visible;
        StabDpcResult.Foreground = (Brush)FindResource("Ink2");
        StabDpcResult.Text = L.F("Слежу за драйверами: осталось {0} с. Пока идёт проверка, пользуйтесь компьютером как обычно — так задержки виднее.", seconds);
        StabDpcList.ItemsSource = null;
        BeginBusy();
        try
        {
            var token = dpcCancel.Token;
            var r = await Task.Run(() => DpcLatency.Run(seconds, token, s => OnUi(() =>
            {
                if (stabBusy) StabDpcResult.Text = L.F("Слежу за драйверами: осталось {0} с. Пока идёт проверка, пользуйтесь компьютером как обычно — так задержки виднее.", Math.Max(0, seconds - s));
            })));
            DpcLatency.Save(settings, r);
            ShowDpc(r);
            if (r.Error == null) UpsertDiag(DpcLatency.Finding(r), x => x.Title.StartsWith(L.T("Задержки драйверов"), StringComparison.Ordinal));
        }
        finally
        {
            EndBusy();
            stabBusy = false;
            SetStabButtons(true);
        }
    }

    private void ShowDpc(DpcResult r)
    {
        StabDpcResult.Visibility = Visibility.Visible;
        if (r.Error != null)
        {
            StabDpcResult.Text = r.Error;
            StabDpcResult.Foreground = (Brush)FindResource("Ink2");
            StabDpcList.ItemsSource = null;
            return;
        }
        double worst = r.WorstUs;
        // Здесь — коротко; что это за драйвер и что делать — строкой в списке справа (тот же вывод, что в «Диагностике»).
        StabDpcResult.Text = r.Drivers.Count == 0 ? L.T("Задержек не замечено.")
            : L.F("Худшая задержка — {0} ({1}), проверка {2:d MMMM, HH:mm}.", DpcLatency.Us(worst), r.Drivers[0].Name, r.At) + " " +
              (worst < DpcLatency.WarnUs ? L.T("Это норма.") : L.T("Что делать — в списке справа."));
        StabDpcResult.Foreground = (Brush)FindResource("Ink2");
        StabDpcList.ItemsSource = r.Drivers.Take(5).Select(d => new DpcRowVm
        {
            Name = d.Name,
            Role = DriverNames.Describe(d.Name).Role + (d.MaxIsIsr ? " · " + L.T("прерывание") : ""),
            Value = DpcLatency.Us(d.MaxUs),
            ValueBrush = d.MaxUs >= DpcLatency.BadUs ? Palette.Warn : (Brush)FindResource("Ink1"),
        }).ToList();
    }

    // ------------------------------------------------------------------ проверка памяти
    private void BtnStabMem_Click(object sender, RoutedEventArgs e)
    {
        if (stabBusy || !admin) return;
        if (GameHooks.InGame)
        {
            StabMemResult.Visibility = Visibility.Visible;
            StabMemResult.Text = L.T("Идёт игра — проверку памяти назначайте, когда закончите играть.");
            return;
        }
        var answer = MessageBox.Show(this,
            L.T("Откроется «Средство проверки памяти Windows».\n\nВ нём выберите:\n• «Выполнить перезагрузку и проверку» — компьютер сразу перезагрузится, проверка займёт 10–20 минут, потом Windows запустится сама;\n• или «Выполнить проверку при следующем включении».\n\nПеред перезагрузкой сохраните работу и закройте игры. Итог проверки появится здесь и в уведомлении."),
            L.T("Проверка памяти"), MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;
        string err = StabilityChecks.StartMemTest(settings);
        StabMemResult.Visibility = Visibility.Visible;
        StabMemResult.Foreground = (Brush)FindResource("Ink2");
        StabMemResult.Text = err == null ? L.T("Проверка назначена — итог появится здесь после перезагрузки.") : L.F("Не удалось открыть средство проверки памяти: {0}", err);
    }

    /// <summary>
    /// Свежий итог проверки — сразу в список находок справа (если «Диагностика» уже запускалась): старая строка того же вида
    /// заменяется. Иначе он появится при следующей проверке — Diagnostics.Run читает сохранённый итог.
    /// </summary>
    private void UpsertDiag(DiagFinding f, Func<DiagItem, bool> same)
    {
        if (DiagTally.Visibility != Visibility.Visible) return;
        foreach (var list in new[] { diagAttention, diagInfo, diagOk })
            foreach (var old in list.Where(same).ToList()) list.Remove(old);
        if (f != null)
            (f.Level <= Severity.Warn ? diagAttention : f.Level == Severity.Info ? diagInfo : diagOk).Insert(0, f);
        DiagHdrAttention.Visibility = diagAttention.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DiagHdrInfo.Visibility = diagInfo.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DiagHdrInfo.Margin = new Thickness(0, diagAttention.Count > 0 ? 26 : 0, 0, 2);
        DiagHdrOk.Visibility = diagOk.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TallyAttention.Value = diagAttention.Count;
        TallyInfo.Value = diagInfo.Count;
        TallyOk.Value = diagOk.Count;
    }

    private void SetStabButtons(bool on)
    {
        bool can = on && admin;
        BtnStabDism.IsEnabled = BtnStabDpc.IsEnabled = BtnStabMem.IsEnabled = can;
    }
}
