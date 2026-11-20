using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PulsePC.Services;

namespace PulsePC.Panels;

/// <summary>
/// Раздел «Питание»: текущая схема, когда включать максимальную (только в игре / всегда / не менять), список схем на ПК.
/// Единственное место программы, где меняется схема питания (прежний переключатель «Оптимизации» перенесён сюда).
/// </summary>
public partial class PowerPanel : UserControl, ISystemPanel
{
    private PanelHost host;
    private PowerState state;
    private bool switching;

    public PowerPanel() => InitializeComponent();

    public void Attach(PanelHost h)
    {
        host = h;
        SetModesEnabled(false);      // до первого чтения состояния
        AdminNote.Visibility = h.Admin ? Visibility.Collapsed : Visibility.Visible;
        // Схему сменила сама программа (началась или закончилась игра) — показать это сразу.
        PowerAuto.Changed += Refresh;
    }

    public void Shown() => Refresh();

    /// <summary>Чтение состояния (в фоновом потоке). Вынесено, чтобы вид раздела можно было проверить на разных состояниях.</summary>
    protected virtual PowerState Load(AppSettings settings) => PowerAuto.Read(settings);

    private void Refresh()
    {
        if (host == null) return;
        var settings = host.Settings;
        Task.Run(() =>
        {
            PowerState st = null;
            try { st = Load(settings); } catch { }
            host.Ui(() => Show(st));
        });
    }

    private void Show(PowerState st)
    {
        state = st;
        if (st == null || st.Active == null)
        {
            State.Text = L.T("Не удалось прочитать схему питания.");
            OnGame.Text = "—";
            SchemeList.ItemsSource = null;
            SchemesEmpty.Visibility = Visibility.Visible;
            Offer.Visibility = Visibility.Collapsed;
            SetModesEnabled(false);
            return;
        }

        string active = st.Active.Name;
        bool always = st.Mode == PowerAuto.ModeAlways;
        bool held = st.InGame && st.BeforeName != "";       // идёт игра, схему сменила программа
        State.Text = held ? L.F("Идёт игра: включена «{0}».", active)
            : st.Enabled && !st.Active.IsMax ? L.F("Сейчас «{0}». В игре включится «{1}».", active, st.MaxName)
            : always && !st.Active.IsMax ? L.F("Сейчас «{0}», хотя выбрана «всегда максимальная».", active)
            : L.F("Сейчас включена схема «{0}».", active);

        ModeGame.IsChecked = st.Mode == PowerAuto.ModeGame;
        ModeAlways.IsChecked = always;
        ModeOff.IsChecked = st.Mode == PowerAuto.ModeOff;
        SetModesEnabled(host.Admin && !switching);

        // Названия схем даёт Windows на своём языке, поэтому в текстах они стоят только в именительном падеже.
        OnGame.Text = always ? (st.Active.IsMax ? L.F("Ничего не изменится: «{0}» включена всегда.", active) : L.F("Схема сменится на «{0}» и останется после игры.", st.MaxName))
            : !st.Enabled ? L.F("Ничего не изменится: останется «{0}».", active)
            : held ? L.F("Игра уже идёт. Когда вы её закроете, вернётся схема «{0}».", st.BeforeName)
            : st.Active.IsMax ? L.F("Схема не изменится: «{0}» уже включена. После игры она и останется.", active)
            : L.F("Схема сменится: «{0}» → «{1}». Когда игра закроется, вернётся «{0}».", active, st.MaxName);

        if (st.Last == null) { LastText.Text = L.T("Ещё не было."); LastWhen.Text = ""; }
        else { LastText.Text = st.LastText; LastWhen.Text = When(st.Last.Value); }

        // Режим включён, игра не идёт, а стоит быстрая схема: вне игр от неё только нагрев и шум.
        bool offer = st.Enabled && !st.InGame && st.ActiveIsFast && st.HasBalanced;
        Offer.Visibility = offer ? Visibility.Visible : Visibility.Collapsed;
        if (offer)
        {
            OfferText.Text = L.F("Сейчас игра не идёт, а включена схема «{0}»: процессор держит полную частоту без нагрузки, поэтому сильнее греется и шумит. Со схемой «{1}» вне игр станет тише и прохладнее, а в игре программа сама включит полную скорость.", active, st.BalancedName);
            BtnBalanced.Content = L.F("Включить схему «{0}»", st.BalancedName);
        }
        BtnBalanced.IsEnabled = host.Admin;

        SchemeList.ItemsSource = st.Schemes;
        SchemesEmpty.Visibility = st.Schemes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Ноутбук: от батареи полная скорость быстро сажает заряд.
        var bat = FakeBattery() ?? st.Battery;
        string battery = BatteryInfo.Advice(bat, st.ActiveIsFast || st.Mode != PowerAuto.ModeOff);
        BatteryNote.Visibility = battery == "" ? Visibility.Collapsed : Visibility.Visible;
        BatteryText.Text = battery;
        BatteryMarker.Level = bat.OnBattery ? Severity.Warn : Severity.Info;
    }

    /// <summary>Для снимков раздела при проверке на настольном ПК: PULSEPC_FAKE_BATTERY=«1|1|42» (батарея есть|от батареи|заряд).</summary>
    private static BatteryInfo? FakeBattery()
    {
        var p = (Environment.GetEnvironmentVariable("PULSEPC_FAKE_BATTERY") ?? "").Split('|');
        if (p.Length < 3) return null;
        return new BatteryInfo { HasBattery = p[0] == "1", OnBattery = p[1] == "1", Percent = int.TryParse(p[2], out int v) ? v : -1 };
    }

    private void SetModesEnabled(bool on) => ModeGame.IsEnabled = ModeAlways.IsEnabled = ModeOff.IsEnabled = on;

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (host == null || !host.Admin || switching) return;
        string mode = sender == ModeAlways ? PowerAuto.ModeAlways : sender == ModeGame ? PowerAuto.ModeGame : PowerAuto.ModeOff;
        if (state != null && state.Mode == mode) return;
        switching = true;
        SetModesEnabled(false);
        host.BeginBusy();
        Task.Run(() =>
        {
            string message;
            try { message = PowerAuto.SetMode(host, mode); }
            catch (Exception ex) { message = L.F("Не удалось сменить режим: {0}", ex.Message); }
            host.Ui(() =>
            {
                host.EndBusy();
                switching = false;
                Say(message);
                Refresh();
            });
        });
    }

    private void BtnBalanced_Click(object sender, RoutedEventArgs e)
    {
        if (host == null || !host.Admin) return;
        BtnBalanced.IsEnabled = false;
        host.BeginBusy();
        Task.Run(() =>
        {
            (bool Ok, string Message) r;
            try { r = PowerAuto.SwitchToBalanced(); }
            catch (Exception ex) { r = (false, L.F("Не удалось сменить схему: {0}", ex.Message)); }
            host.Ui(() =>
            {
                host.EndBusy();
                Say(r.Message);
                BtnBalanced.IsEnabled = true;
                Refresh();
            });
        });
    }

    /// <summary>Итог действия под переключателем; пока сказать нечего, строка места не занимает.</summary>
    private void Say(string text)
    {
        Result.Text = text;
        Result.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>«сегодня, 21:40», «вчера, 09:05», «18 ноября, 21:40».</summary>
    private static string When(DateTime t)
    {
        int days = (DateTime.Today - t.Date).Days;
        return days == 0 ? L.F("сегодня, {0:HH:mm}", t) : days == 1 ? L.F("вчера, {0:HH:mm}", t)
            : t.ToString(L.Pick("d MMMM, HH:mm", "MMMM d, HH:mm"), L.Culture);
    }
}
