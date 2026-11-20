using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PulsePC.Services;

namespace PulsePC.Panels;

/// <summary>Раздел «Настройки NVIDIA по играм»: общие настройки драйвера и профили установленных игр.</summary>
public partial class NvidiaPanel : UserControl, ISystemPanel
{
    private PanelHost host;
    private NvTarget current;
    private int generation;     // номер загрузки: результат устаревшей загрузки отбрасывается
    private bool busy;

    public NvidiaPanel() => InitializeComponent();

    public void Attach(PanelHost h)
    {
        host = h;
        AdminNote.Visibility = host.Admin ? Visibility.Collapsed : Visibility.Visible;
        Rows.IsEnabled = host.Admin;        // без прав администратора — только просмотр
    }

    public void Shown() => Reload(current?.Title, null);

    /// <summary>Перечитать профили в фоне; keep — какой профиль оставить выбранным, result — итог только что сделанного действия.</summary>
    private void Reload(string keep, string result)
    {
        if (host == null) return;
        int gen = ++generation;
        var sessions = host.Sessions();
        Task.Run(() =>
        {
            NvState state;
            try { state = Fake() ?? NvidiaProfiles.Scan(sessions); }
            catch (Exception ex) { state = new NvState { Reason = "Не удалось прочитать профили драйвера: " + ex.Message }; }
            host.Ui(() =>
            {
                if (gen != generation) return;
                Show(state, keep);
                if (result != null) Result.Text = result;
            });
        });
    }

    /// <summary>
    /// Для снимков раздела при проверке на чужом железе: PULSEPC_FAKE_GPU=«AMD|AMD Radeon RX 7800 XT|0» (производитель|название|встроенная 0/1|батарея 0/1).
    /// Без переменной — null, раздел читает настоящий драйвер.
    /// </summary>
    private static NvState Fake()
    {
        var parts = (Environment.GetEnvironmentVariable("PULSEPC_FAKE_GPU") ?? "").Split('|');
        if (parts.Length < 2) return null;
        var st = new NvState();
        string vendor = parts[0];
        NvidiaProfiles.Advise(st, vendor, parts[1], parts.Length > 2 && parts[2] == "1", false, "", GameTuning.FrameCap(), GameTuning.Refresh(),
            GpuVendors.FindControlApp(vendor) is string app && app != "" ? app : vendor is "AMD" or "Intel" ? "demo" : "", parts.Length > 3 && parts[3] == "1");
        return st;
    }

    private void Show(NvState state, string keep)
    {
        Intro.Text = state.Vendor is "" or "NVIDIA"
            ? "Это те же «Параметры 3D», что в панели управления NVIDIA: общие настройки драйвера и отдельные — для каждой игры. Они хранятся в самом драйвере: файлы игр и сами игры программа здесь не трогает."
            : GpuVendors.ControlApp(state.Vendor) is string app && app != ""
                ? $"Задержку и лимит FPS для видеокарт {state.Vendor} меняют в приложении {app}. Программа в драйвер не пишет — только подсказывает, что и где поставить."
                : "Программа в драйвер этой видеокарты не пишет — только подсказывает, что поставить в самих играх.";
        if (!state.Available)
        {
            State.Text = state.Reason;
            AdviceRows.ItemsSource = state.Advice;
            Fallback.Visibility = state.Advice.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            Work.Visibility = Visibility.Collapsed;
            return;
        }
        Fallback.Visibility = Visibility.Collapsed;
        Work.Visibility = Visibility.Visible;

        bool common = state.Targets.Any(t => t.Global && t.Changes > 0);
        int games = state.Targets.Count(t => !t.Global && t.Changes > 0);
        string forGames = games + " " + (games % 10 == 1 && games % 100 != 11 ? "игры" : "игр");
        State.Text = !common && games == 0 ? "Профили драйвера в порядке — менять нечего"
            : games == 0 ? "Есть рекомендации для общих настроек драйвера"
            : common ? "Есть рекомендации для общих настроек и " + forGames
            : "Есть рекомендации для " + forGames;

        TargetList.ItemsSource = state.Targets;
        var pick = state.Targets.FirstOrDefault(t => t.Title == keep);
        // Для снимков раздела при проверке: номер профиля в списке можно задать снаружи.
        if (pick == null && int.TryParse(Environment.GetEnvironmentVariable("PULSEPC_PANEL_SELECT"), out int index) && index >= 0 && index < state.Targets.Count)
            pick = state.Targets[index];
        TargetList.SelectedItem = pick ?? state.Targets.FirstOrDefault();
    }

    private void TargetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (current != null) foreach (var r in current.Rows) r.PropertyChanged -= Row_Changed;
        current = TargetList.SelectedItem as NvTarget;
        Result.Text = "";
        if (current == null) return;
        foreach (var r in current.Rows) r.PropertyChanged += Row_Changed;

        TargetTitle.Text = current.Title;
        TargetCaption.Text = current.Global ? "Действуют на все игры и программы, у которых нет своего значения в профиле."
            : current.Exe == "" ? "Запускаемый файл игры не найден."
            : current.HasProfile ? $"Профиль драйвера «{current.ProfileName}» · файл {current.Exe}"
            : $"Файл {current.Exe} · своего профиля нет, действуют общие настройки";
        Rows.ItemsSource = current.Rows;
        TargetNote.Text = current.Note;
        TargetNote.Visibility = current.Note == "" ? Visibility.Collapsed : Visibility.Visible;
        UpdateButtons();
    }

    private void Row_Changed(object sender, PropertyChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        bool can = host != null && host.Admin && !busy && current != null;
        BtnApply.IsEnabled = can && current.Rows.Any(r => r.CanChange && r.Selected);
        BtnRestore.IsEnabled = can && current.HasBackup;
    }

    // Список без своей прокрутки: колесо мыши над ним должно прокручивать страницу.
    private void TargetList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        (Parent as UIElement ?? this).RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = this });
    }

    /// <summary>Открыть приложение производителя видеокарты (AMD Software, Intel Graphics Software). В драйвер программа не пишет.</summary>
    private void BtnLaunch_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string target || target == "" || target == "demo") return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Result.Text = "Не удалось открыть приложение: " + ex.Message; }
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        var target = current;
        if (target == null) return;
        var rows = target.Rows.Where(r => r.CanChange && r.Selected).ToList();
        Run(() => NvidiaProfiles.Apply(target, rows), "Записываю в профиль драйвера…");
    }

    private void BtnRestore_Click(object sender, RoutedEventArgs e)
    {
        var target = current;
        if (target == null) return;
        Run(() => NvidiaProfiles.Restore(target), "Возвращаю прежние значения…");
    }

    private void Run(Func<string> action, string progress)
    {
        if (host == null || !host.Admin || busy) return;
        busy = true;
        UpdateButtons();
        Result.Text = progress;
        string keep = current?.Title;
        host.BeginBusy();
        Task.Run(() =>
        {
            string message;
            try { message = action(); }
            catch (Exception ex) { message = "Не удалось: " + ex.Message; }
            host.Ui(() =>
            {
                host.EndBusy();
                busy = false;
                Reload(keep, message);      // состояние после записи читается из драйвера заново
            });
        });
    }
}
