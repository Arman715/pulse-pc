using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PulsePC.Services;

namespace PulsePC.Panels;

/// <summary>
/// Раздел «Заряд устройств» (группа «ИГРА»): заряд беспроводных гарнитур, мышей, клавиатур и геймпадов с источником, кнопка «Обновить»,
/// уведомления перед игрой и во время игры, и честно — у каких устройств заряд не читается и где его смотреть.
/// Ничего не меняет ни в системе, ни в устройствах: только читает. Для снимков: PULSEPC_BATTERY_DEMO=1 — подставные устройства.
/// </summary>
public partial class DeviceBatteryPanel : UserControl, ISystemPanel
{
    public sealed class RowVm
    {
        public Geometry Icon { get; init; }
        public Geometry Bolt { get; init; } = DeviceBattery.BoltIcon;
        public Visibility BoltVisibility { get; init; } = Visibility.Collapsed;
        public string Name { get; init; } = "";
        public string Detail { get; init; } = "";
        public string Value { get; init; } = "";
        public string State { get; init; } = "";
        public double Fill { get; init; }
        public Brush Brush { get; init; }
    }

    public sealed class SourceVm
    {
        public string Name { get; init; } = "";
        public string Text { get; init; } = "";
    }

    private PanelHost host;
    private bool loading = true, busy;

    public DeviceBatteryPanel() => InitializeComponent();

    public void Attach(PanelHost h)
    {
        host = h;
        Remind.IsChecked = h.Settings.GetBool(DeviceBattery.KRemind, true);
        InGame.IsChecked = h.Settings.GetBool(DeviceBattery.KInGame, true);
        Remind.IsEnabled = InGame.IsEnabled = h.Admin;
        AdminNote.Visibility = h.Admin ? Visibility.Collapsed : Visibility.Visible;
        RemindText.Text = L.F("При запуске игры — уведомление у часов с зарядом устройств, один раз за игру. Если у какого-то {0} % и меньше — как предупреждение: «подзарядите мышь».", DeviceBattery.WarnPct);
        InGameText.Text = L.F("Если во время игры заряд упал до {0} % — одно уведомление «скоро выключится», не чаще раза на устройство за игру.", DeviceBattery.CriticalPct);
        Sources.ItemsSource = new List<SourceVm>
        {
            new() { Name = L.T("Windows"), Text = L.T("заряд Bluetooth-устройств, который показывают «Параметры» Windows (наушники, мыши, клавиатуры, геймпады)") },
            new() { Name = "HID", Text = L.T("устройства, которые сами сообщают заряд стандартным способом (Battery Strength) — по USB-приёмнику или Bluetooth") },
            new() { Name = "Logitech", Text = L.T("мыши, клавиатуры и гарнитуры на приёмниках Lightspeed, Unifying, Bolt и по Bluetooth — открытый протокол HID++, только запросы чтения, те же, что делает G HUB") },
            new() { Name = L.T("Марки"), Text = L.F("мыши, гарнитуры и геймпады со своим протоколом ({0}) — программа задаёт им тот же вопрос о заряде, что и фирменная программа (геймпады PlayStation только слушает), и настроек в них не меняет", VendorBattery.BrandsText) },
            new() { Name = "Xbox", Text = L.T("геймпады Xbox — только уровень (пусто, мало, средне, полный), поэтому проценты примерные") },
        };
        HowNote.Text = L.F("Программа спрашивает заряд раз в {0} минуты — и вне игры, и в игре: заряд меняется медленно, а частые запросы будили бы мышь и гарнитуру. Ничего не ставится и в настройках устройств ничего не меняется. Спящая мышь или выключенная гарнитура не отвечает — тогда полчаса показывается последний известный заряд. Гарнитура Logitech G435 читается реже — не чаще раза в 20 минут и только в тишине или при запуске игры: при чтении её звук прерывается на долю секунды. Между чтениями у неё показывается последний прочитанный заряд.", DeviceBattery.PollMinutes);
        loading = false;
        DeviceBattery.Changed += () => host.Ui(() => Show(DeviceBattery.Current));
    }

    public void Shown()
    {
        if (host == null) return;
        // Опрос уже был (служба спрашивает раз в несколько минут) — показываем его; нет — спрашиваем в фоне.
        if (DeviceBattery.LastScan != DateTime.MinValue && !DeviceBattery.Demo) Show(DeviceBattery.Current);
        else Refresh();
    }

    private void Refresh()
    {
        if (busy) return;
        busy = true;
        BtnRefresh.IsEnabled = false;
        host.BeginBusy();
        Task.Run(() =>
        {
            IReadOnlyList<DeviceCharge> list = null;
            try { list = DeviceBattery.ScanNow(); } catch (Exception ex) { Log.Error("заряд устройств: раздел", ex); }
            host.Ui(() =>
            {
                busy = false;
                BtnRefresh.IsEnabled = true;
                host.EndBusy();
                Show(list ?? DeviceBattery.Current);
            });
        });
    }

    private void Show(IReadOnlyList<DeviceCharge> list)
    {
        list ??= Array.Empty<DeviceCharge>();
        bool scanned = DeviceBattery.LastScan != DateTime.MinValue;
        State.Text = DeviceBattery.Verdict(list, scanned);
        var known = list.Where(d => d.Known).OrderBy(d => d.Percent).ToList();
        State.Foreground = known.Count > 0 && DeviceBattery.IsLow(known[0]) ? (Brush)FindResource("Accent") : (Brush)FindResource("Ink1");
        var ink = (Brush)FindResource("Ink1");
        var accent = (Brush)FindResource("Accent");
        Known.ItemsSource = known.Select(d => new RowVm
        {
            Icon = DeviceBattery.Icon(d.Kind),
            Name = d.Name,
            Detail = string.Join(" · ", new[] { d.Source, d.Note }.Where(x => !string.IsNullOrEmpty(x))),
            Value = DeviceBattery.PercentText(d),
            Fill = Math.Clamp((d.Percent ?? 0) / 100.0, 0, 1),
            Brush = DeviceBattery.IsLow(d) ? accent : ink,
            BoltVisibility = d.Charging == true ? Visibility.Visible : Visibility.Collapsed,
            State = d.Charging == true ? L.T("заряжается") : DeviceBattery.IsLow(d) ? L.T("пора зарядить") : d.Approx ? L.T("примерно") : "",
        }).ToList();
        NoneKnown.Visibility = scanned && known.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var unknown = list.Where(d => !d.Known).ToList();
        Unknown.ItemsSource = unknown.Select(d => new RowVm
        {
            Icon = DeviceBattery.Icon(d.Kind), Name = d.Name,
            Detail = d.Note.Length > 0 ? d.Note : d.Source,
        }).ToList();
        UnknownBlock.Visibility = unknown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Checked.Text = scanned
            ? L.F("Проверено в {0}. Сама программа спрашивает раз в {1} минуты.", DeviceBattery.LastScan.ToString("HH:mm", L.Culture), DeviceBattery.PollMinutes)
            : "";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Remind_Click(object sender, RoutedEventArgs e)
    {
        if (loading || host?.Admin != true) return;
        host.Settings.SetBool(DeviceBattery.KRemind, Remind.IsChecked == true);
        host.Settings.Save();
    }

    private void InGame_Click(object sender, RoutedEventArgs e)
    {
        if (loading || host?.Admin != true) return;
        host.Settings.SetBool(DeviceBattery.KInGame, InGame.IsChecked == true);
        host.Settings.Save();
    }

    private void Overlay_Click(object sender, RoutedEventArgs e) => host?.Open("sys:Оверлей");
}
