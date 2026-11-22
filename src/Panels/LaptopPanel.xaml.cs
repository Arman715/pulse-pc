using System.Windows;
using System.Windows.Controls;
using PulsePC.Services;

namespace PulsePC.Panels;

/// <summary>
/// Раздел «Ноутбук» (показывается, только если LaptopCheck.Applies): питание, износ батареи, режим питания Windows, фирменная программа
/// производителя, советы для ноутбука и настройка уведомления «игра от батареи». Ничего не меняет в системе.
/// Для снимков на настольном ПК: PULSEPC_LAPTOP_DEMO=1 (подставной ноутбук ASUS) и PULSEPC_FAKE_BATTERY=«1|1|42».
/// </summary>
public partial class LaptopPanel : UserControl, ISystemPanel
{
    public sealed class NoteVm
    {
        public Severity Level { get; init; }
        public string Title { get; init; } = "";
        public string Text { get; init; } = "";
    }

    public sealed class FactVm
    {
        public string Name { get; init; } = "";
        public string Value { get; init; } = "";
        public string Note { get; init; } = "";
    }

    private PanelHost host;
    private bool loading = true;

    public LaptopPanel() => InitializeComponent();

    public void Attach(PanelHost h)
    {
        host = h;
        Alert.IsChecked = h.Settings.GetBool(LaptopCheck.AlertKey, true);
        Alert.IsEnabled = h.Admin;
        AdminNote.Visibility = h.Admin ? Visibility.Collapsed : Visibility.Visible;
        loading = false;
    }

    public void Shown()
    {
        if (host == null) return;
        Task.Run(() =>
        {
            LaptopCheck.State st = null;
            try { st = Environment.GetEnvironmentVariable("PULSEPC_LAPTOP_DEMO") == "1" ? Demo() : LaptopCheck.Read(host.Hardware()); } catch { }
            if (st != null && FakeBattery() is BatteryInfo fake) st.Battery = fake;
            host.Ui(() => Show(st));
        });
    }

    private void Show(LaptopCheck.State st)
    {
        if (st == null) { State.Text = L.T("Не удалось прочитать состояние ноутбука."); return; }
        var b = st.Battery;
        State.Text = !b.HasBattery ? L.T("Батарея не найдена")
            : b.OnBattery ? (b.Percent >= 0 ? L.F("Работает от батареи · заряд {0} %", b.Percent) : L.T("Работает от батареи"))
            : b.Percent >= 0 ? L.F("Работает от зарядки · заряд {0} %", b.Percent) : L.T("Работает от зарядки");
        State.Foreground = b.OnBattery ? (System.Windows.Media.Brush)FindResource("Warn") : (System.Windows.Media.Brush)FindResource("Ink1");
        Machine.Text = (st.Maker + " " + st.Model).Trim();
        Machine.Visibility = Machine.Text == "" ? Visibility.Collapsed : Visibility.Visible;

        Notes.ItemsSource = LaptopCheck.Advise(st).Select(a => new NoteVm { Level = a.Level, Title = a.Title, Text = a.Text }).ToList();

        // Батарея
        var h = st.Health;
        var facts = new List<FactVm>();
        if (h is { Read: true })
        {
            facts.Add(new FactVm { Name = L.T("Заводская ёмкость"), Value = Wh(h.DesignMwh), Note = h.Count > 1 ? L.F("батарей: {0}", h.Count) : "" });
            facts.Add(new FactVm { Name = L.T("Вмещает сейчас"), Value = Wh(h.FullMwh), Note = L.F("{0:0} % от заводской", 100 - h.WearPct) });
            facts.Add(new FactVm { Name = L.T("Износ"), Value = h.WearPct.ToString("0", L.Culture) + " %", Note = L.T("до 20 % — обычное дело для батареи пары лет") });
            facts.Add(new FactVm { Name = L.T("Циклов зарядки"), Value = h.Cycles > 0 ? h.Cycles.ToString(L.Culture) : "—", Note = h.Cycles > 0 ? "" : L.T("ноутбук не сообщает") });
        }
        if (b.Percent >= 0) facts.Add(new FactVm { Name = L.T("Заряд"), Value = b.Percent + " %", Note = b.OnBattery ? L.T("от батареи") : L.T("от зарядки") });
        BatteryFacts.ItemsSource = facts;
        var (level, title, text) = LaptopCheck.BatteryVerdict(h);
        BatteryMarker.Level = level;
        BatteryTitle.Text = title;
        BatteryText.Text = text;

        // Режим питания
        ModeFacts.ItemsSource = new List<FactVm>
        {
            new() { Name = L.T("Режим питания Windows"), Value = LaptopCheck.ModeName(st.Mode), Note = L.T("действующий сейчас, с учётом питания") },
            new() { Name = L.T("Экономия заряда"), Value = st.Saver ? L.T("включена") : L.T("выключена") },
        };
    }

    private static string Wh(long mwh) => (mwh / 1000.0).ToString(mwh >= 10000 ? "0" : "0.#", L.Culture) + " " + L.Pick("Вт·ч", "Wh");

    private void Alert_Click(object sender, RoutedEventArgs e)
    {
        if (loading || host?.Admin != true) return;
        host.Settings.SetBool(LaptopCheck.AlertKey, Alert.IsChecked == true);
        host.Settings.Save();
    }

    private void BtnPower_Click(object sender, RoutedEventArgs e) => host?.Open("sys:Питание");

    private static BatteryInfo? FakeBattery()
    {
        var p = (Environment.GetEnvironmentVariable("PULSEPC_FAKE_BATTERY") ?? "").Split('|');
        if (p.Length < 3) return null;
        return new BatteryInfo { HasBattery = p[0] == "1", OnBattery = p[1] == "1", Percent = int.TryParse(p[2], out int v) ? v : -1 };
    }

    /// <summary>Подставной игровой ноутбук ASUS — только для снимков вида.</summary>
    private static LaptopCheck.State Demo() => new()
    {
        Battery = new BatteryInfo { HasBattery = true, OnBattery = false, Percent = 87 },
        Mode = LaptopCheck.PowerMode.Balanced,
        Maker = "ASUSTeK COMPUTER INC.", Model = "ROG Strix G18 G814JI",
        Oem = LaptopCheck.FindOem("ASUSTeK COMPUTER INC.", new[] { "B9ECED6F.ArmouryCrate_5.9.0.0_x64__qmba6cd70vzyy" }),
        HasDiscrete = true, DiscreteName = "NVIDIA GeForce RTX 4070 Laptop GPU",
        Health = new LaptopCheck.BatteryHealth { Read = true, DesignMwh = 90000, FullMwh = 71500, Cycles = 214, Count = 1 },
    };
}
