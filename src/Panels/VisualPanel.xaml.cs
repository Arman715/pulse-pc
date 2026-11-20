using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using PulsePC.Services;

namespace PulsePC.Panels;

/// <summary>Строка эффекта оформления: чего хочет пользователь и что сейчас в системе.</summary>
public sealed class FxRow : Notifier
{
    public string Title { get; init; }
    public string Hint { get; init; }

    private bool desired;
    public bool Desired { get => desired; set => Set(ref desired, value); }

    /// <summary>Фактическое состояние в системе.</summary>
    public bool Current { get; set; }
}

/// <summary>Раздел «Оформление Windows»: три набора и двенадцать переключателей эффектов.</summary>
public partial class VisualPanel : UserControl, ISystemPanel
{
    private PanelHost host;
    private readonly ObservableCollection<FxRow> rows = new();
    private bool loaded, busy, filling;
    private string changedNote;     // подпись «Изменено: …», которую Refresh снимает сам

    public VisualPanel()
    {
        InitializeComponent();
        foreach (var e in VisualFx.Effects)
        {
            var row = new FxRow { Title = e.Title, Hint = e.Hint };
            row.PropertyChanged += (_, _) => { if (!filling) Refresh(); };
            rows.Add(row);
        }
        Rows.ItemsSource = rows;
        Rows.IsEnabled = false;
        Profiles.IsEnabled = false;
    }

    public void Attach(PanelHost panelHost)
    {
        host = panelHost;
        AdminNote.Visibility = host.Admin ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Shown()
    {
        if (host == null || busy) return;
        Task.Run(() =>
        {
            bool[] state = null;
            try { state = VisualFx.Read(); } catch { }
            bool backup = VisualFx.HasBackup;
            host.Ui(() => Show(state, backup));
        });
    }

    /// <summary>Показать прочитанное состояние; переключатели встают так, как сейчас в системе.</summary>
    private void Show(bool[] state, bool backup)
    {
        if (state == null)
        {
            State.Text = L.T("Оформление прочитать не удалось");
            Result.Text = L.T("Windows не отдала настройки эффектов. Откройте раздел ещё раз.");
            return;
        }
        filling = true;
        for (int i = 0; i < rows.Count; i++) { rows[i].Current = state[i]; rows[i].Desired = state[i]; }
        filling = false;
        loaded = true;
        State.Text = L.F("Сейчас: {0}", VisualFx.Describe(state));
        BtnRestore.IsEnabled = host.Admin && backup;
        Refresh();
    }

    /// <summary>Набор, кнопки и подпись под положения переключателей.</summary>
    private void Refresh()
    {
        bool can = loaded && !busy && host?.Admin == true;
        Rows.IsEnabled = can;
        Profiles.IsEnabled = can;
        Profiles.Opacity = can ? 1 : 0.55;

        var desired = rows.Select(r => r.Desired).ToArray();
        var profile = VisualFx.Match(desired);
        ProfilePretty.IsChecked = profile == FxProfile.Pretty;
        ProfileBalance.IsChecked = profile == FxProfile.Balance;
        ProfileSpeed.IsChecked = profile == FxProfile.Speed;
        ProfileNote.Text = profile switch
        {
            FxProfile.Pretty => L.T("«Красиво»: включены все эффекты."),
            FxProfile.Balance => L.T("«Баланс»: остаётся то, что помогает читать и не путать окна, — сглаживание шрифтов, эскизы, содержимое окна при перетаскивании, тени окон и просмотр рабочего стола. Прозрачность и анимации выключены."),
            FxProfile.Speed => L.T("«Скорость»: выключено всё, кроме сглаживания шрифтов, — без него текст трудно читать."),
            _ => L.T("Свой набор: переключатели не совпадают ни с одним профилем."),
        };

        int changed = rows.Count(r => r.Desired != r.Current);
        BtnApply.IsEnabled = can && changed > 0;
        if (loaded && !busy && changed > 0)
            Result.Text = changedNote = L.F("Изменено: {0} из {1}. Вступит в силу после «Применить».", changed, rows.Count);
        else if (loaded && !busy && changedNote != null && Result.Text == changedNote) Result.Text = "";
    }

    private void Profile_Click(object sender, RoutedEventArgs e)
    {
        var profile = sender == ProfilePretty ? FxProfile.Pretty : sender == ProfileBalance ? FxProfile.Balance : FxProfile.Speed;
        var states = VisualFx.ProfileStates(profile);
        filling = true;
        for (int i = 0; i < rows.Count; i++) rows[i].Desired = states[i];
        filling = false;
        Refresh();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (busy || host?.Admin != true) return;
        if (GameHooks.InGame)
        {
            Result.Text = L.T("Идёт игра. При смене оформления Windows перерисовывает все окна, и игра может дёрнуться. Сделайте это после игры.");
            return;
        }
        var desired = rows.Select(r => r.Desired).ToArray();
        Run(L.T("Применяю…"), () =>
        {
            var (changed, failed) = VisualFx.Apply(desired);
            if (failed.Count > 0)
                return L.F("Не удалось изменить: {0}. Попробуйте ещё раз; если не выйдет — выйдите из Windows и войдите снова.", string.Join("; ", failed));
            return changed == 0
                ? L.T("Менять нечего: эффекты уже так и настроены.")
                : L.F("Готово, изменено эффектов: {0}. Эскизы, панель задач и просмотр рабочего стола могут обновиться только после повторного открытия окон или нового входа в Windows.", changed);
        });
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (busy || host?.Admin != true) return;
        if (GameHooks.InGame)
        {
            Result.Text = L.T("Идёт игра. Верните оформление после неё.");
            return;
        }
        Run(L.T("Возвращаю…"), () => VisualFx.Restore().Message);
    }

    /// <summary>Действие в фоне с линией занятости; после него состояние перечитывается.</summary>
    private void Run(string progress, Func<string> action)
    {
        busy = true;
        Result.Text = progress;
        BtnApply.IsEnabled = BtnRestore.IsEnabled = false;
        Rows.IsEnabled = Profiles.IsEnabled = false;
        host.BeginBusy();
        Task.Run(() =>
        {
            string message;
            bool[] state = null;
            bool backup = false;
            try { message = action(); }
            catch (Exception ex) { message = L.F("Не удалось: {0}", ex.Message); }
            try { state = VisualFx.Read(); backup = VisualFx.HasBackup; } catch { }
            host.Ui(() =>
            {
                host.EndBusy();
                busy = false;
                Show(state, backup);
                Result.Text = message;
            });
        });
    }
}
