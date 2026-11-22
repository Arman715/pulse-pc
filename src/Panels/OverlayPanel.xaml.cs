using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PulsePC.Services;

namespace PulsePC.Panels;

/// <summary>
/// Раздел «Оверлей»: включатель, что показывать, угол, размер, раскладка, подложка, горячая клавиша и предпросмотр.
/// Это настройки самой программы (overlay.*): система не меняется, права администратора не нужны.
/// Настройки сохраняются только по действию пользователя, при открытии раздела ничего не пишется.
/// </summary>
public partial class OverlayPanel : UserControl, ISystemPanel
{
    private PanelHost host;
    private OverlayOptions options = new();
    private bool loading;

    public OverlayPanel()
    {
        InitializeComponent();
        KeyBox.ItemsSource = SoundHotkey.Keys.Select(k => k.Title).ToList();
        // «средний» в общем словаре переведён как «fair» (качество сигнала) — здесь это размер.
        SizeM.Content = L.Pick("средний", "medium");
        foreach (int op in OverlayOptions.OpacitySteps)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Range"), GroupName = "OvOpacity", Tag = op, Content = op + L.Pick(" %", "%") };
            rb.Click += Opacity_Click;
            OpacityChoices.Children.Add(rb);
        }
    }

    public void Attach(PanelHost panelHost)
    {
        host = panelHost;
        // Оверлей включили или выключили в меню значка у часов, пока раздел открыт, — переключатель следует за ним.
        Overlay.Changed += () => host.Ui(() =>
        {
            if (Overlay.Ready && options.Enabled != Overlay.Enabled)
            {
                options.Enabled = Overlay.Enabled;
                loading = true;
                try { OnToggle.IsChecked = options.Enabled; } finally { loading = false; }
            }
            ShowState();
        });
        // Опрос заряда закончился: «сам» — галочка следует за тем, нашлось ли устройство с зарядом.
        DeviceBattery.Changed += () => host.Ui(() =>
        {
            if (options.Battery == null)
            {
                loading = true;
                try { ItemBattery.IsChecked = DeviceBattery.AnyKnown; } finally { loading = false; }
                UpdatePreview();
            }
            ShowState();
        });
    }

    public void Shown()
    {
        if (host == null) return;
        // Служба знает текущие настройки (в том числе после переключения в меню у часов); без неё (снимок раздела) — из настроек.
        options = Overlay.Ready ? Overlay.Current : OverlayOptions.Load(host.Settings);
        loading = true;
        try
        {
            OnToggle.IsChecked = options.Enabled;
            foreach (var cb in ItemBoxes()) cb.IsChecked = Enum.TryParse(cb.Tag as string, out OverlayItem i) && options.Has(i);
            // Заряд: «сам» — отмечено, если программа уже нашла устройство с зарядом.
            ItemBattery.IsChecked = options.BatteryOn(DeviceBattery.AnyKnown);
            foreach (var rb in new[] { CornerTl, CornerTr, CornerBl, CornerBr }) rb.IsChecked = (string)rb.Tag == options.Corner;
            foreach (var rb in new[] { SizeS, SizeM, SizeL }) rb.IsChecked = (string)rb.Tag == options.Size;
            LayoutRow.IsChecked = !options.Column; LayoutCol.IsChecked = options.Column;
            foreach (RadioButton rb in OpacityChoices.Children) rb.IsChecked = (int)rb.Tag == options.Opacity;
            var hk = options.Hotkey;
            ModCtrl.IsChecked = hk.Ctrl; ModAlt.IsChecked = hk.Alt; ModShift.IsChecked = hk.Shift; ModWin.IsChecked = hk.Win;
            SelectKey(hk.Key);
        }
        finally { loading = false; }
        FpsNote.Text = host.Admin
            ? L.T("FPS, 1 % и график времени кадра есть, только когда программа записывает кадры: включено «Записывать FPS в играх» в меню значка у часов и игра не в осторожном режиме. Иначе в их ячейках будет «—».")
            : L.T("Программа запущена без прав администратора: кадры не записываются, поэтому FPS, 1 % и график времени кадра будут «—». Остальное оверлей покажет.");
        UpdatePreview();
        ShowState();
    }

    private IEnumerable<CheckBox> ItemBoxes() => new[] { ItemFps, ItemLow1, ItemGpu, ItemCpu, ItemGraph, ItemVram, ItemPing, ItemClock };

    private void ShowState()
    {
        var state = Overlay.State;
        // Служба не запущена (снимок раздела) — по настройке.
        if (state == OverlayState.Off && options.Enabled) state = OverlayState.NoGame;
        State.Text = Overlay.StateText(state, Overlay.Game, options.Hotkey, Overlay.Exclusive);
        var exclusiveGames = Overlay.ExclusiveGames;
        ExclusiveNote.Visibility = exclusiveGames.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (exclusiveGames.Count > 0)
            ExclusiveText.Text = L.F("Мог быть не виден в {0}: игра шла в исключительном полноэкранном режиме, а поверх него Windows может не показывать другие окна. Если оверлея не было видно, включите в настройках игры режим экрана «Без рамки» или «Полноэкранный в окне». Встраиваться в саму игру программа не будет: это риск для античитов.",
                string.Join(", ", exclusiveGames.Select(g => "«" + g + "»")));
        string problem = Overlay.HotkeyProblem;
        HotkeyNow.Text = problem ?? L.F("Сейчас: {0}. Показывает и прячет оверлей во время игры, настройку не выключает. Сочетание занято, только пока окно игры на переднем плане — в других программах оно свободно.", options.Hotkey.Text);
        HotkeyNow.Foreground = (System.Windows.Media.Brush)FindResource(problem != null ? "Warn" : "Ink2");
        NothingChosen.Visibility = Effective(true).Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var known = DeviceBattery.ForOverlay();
        BatteryNote.Text = known.Length > 0
            ? L.F("До трёх устройств, самые разряженные первыми. Сейчас найдено с зарядом: {0}.", string.Join(", ", known.Select(d => d.Name + " — " + DeviceBattery.PercentText(d))))
            : DeviceBattery.LastScan == DateTime.MinValue
                ? L.T("До трёх устройств, самые разряженные первыми. Показывается, только если заряд хоть одного устройства известен.")
                : L.T("Сейчас ни одно устройство не сообщает заряд — ячейки не будет. Подробности — в разделе «Заряд устройств».");
    }

    private void UpdatePreview()
    {
        PreviewBox.HorizontalAlignment = options.Corner is "tl" or "bl" ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        PreviewBox.VerticalAlignment = options.Corner is "tl" or "tr" ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        var shown = Effective(true);
        PreviewBox.Visibility = shown.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        Preview.Show(OverlayReading.Sample(3), shown);
    }

    /// <summary>Что будет в табличке; в предпросмотре заряд подставной, поэтому «есть что показать».</summary>
    private OverlayOptions Effective(bool haveCharges) => options.WithBattery(haveCharges && ItemBattery.IsChecked == true);

    /// <summary>Сохранить выбор (только по действию пользователя) и передать службе.</summary>
    private void Commit()
    {
        if (loading || host == null) return;
        try
        {
            options.Save(host.Settings);
            host.Settings.Save();
        }
        catch (Exception ex) { Log.Error("оверлей: сохранение настроек", ex); }
        Overlay.Apply(options);
        UpdatePreview();
        ShowState();
    }

    private void OnToggle_Click(object sender, RoutedEventArgs e)
    {
        options.Enabled = OnToggle.IsChecked == true;
        Commit();
        Say(options.Enabled
            ? L.T("Оверлей включён. Запустите игру — табличка появится в выбранном углу.")
            : L.T("Оверлей выключен."));
    }

    private void Item_Click(object sender, RoutedEventArgs e)
    {
        // Заряд — отдельная настройка: запоминается, только когда человек сам щёлкнул эту галочку.
        if (sender == ItemBattery) options.Battery = ItemBattery.IsChecked == true;
        options.Items = ItemBoxes().Where(cb => cb.IsChecked == true)
            .Select(cb => Enum.Parse<OverlayItem>((string)cb.Tag)).Distinct().OrderBy(x => x).ToList();
        Commit();
    }

    private void ItemName_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CheckBox cb && cb.IsEnabled)
        {
            cb.IsChecked = cb.IsChecked != true;
            Item_Click(cb, null);
        }
    }

    private void Corner_Click(object sender, RoutedEventArgs e) { options.Corner = (string)((FrameworkElement)sender).Tag; Commit(); }
    private void Size_Click(object sender, RoutedEventArgs e) { options.Size = (string)((FrameworkElement)sender).Tag; Commit(); }
    private void Layout_Click(object sender, RoutedEventArgs e) { options.Column = (string)((FrameworkElement)sender).Tag == "column"; Commit(); }
    private void Opacity_Click(object sender, RoutedEventArgs e) { options.Opacity = (int)((FrameworkElement)sender).Tag; Commit(); }

    // ───────────────────────── горячая клавиша ─────────────────────────

    private string SelectedKey
    {
        get
        {
            string title = KeyBox.SelectedItem as string;
            foreach (var k in SoundHotkey.Keys) if (k.Title == title) return k.Token;
            return null;
        }
    }

    private void SelectKey(string token)
    {
        foreach (var k in SoundHotkey.Keys)
            if (string.Equals(k.Token, token, StringComparison.OrdinalIgnoreCase)) { KeyBox.SelectedItem = k.Title; return; }
    }

    private void ModName_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CheckBox cb && cb.IsEnabled) cb.IsChecked = cb.IsChecked != true;
    }

    private void Hotkey_Click(object sender, RoutedEventArgs e)
    {
        if (host == null) return;
        var hotkey = new SoundHotkey(ModCtrl.IsChecked == true, ModAlt.IsChecked == true, ModShift.IsChecked == true, ModWin.IsChecked == true, SelectedKey);
        if (Overlay.TrySetHotkey(hotkey, host.Settings, out string error))
        {
            options.Hotkey = hotkey;
            SayHotkey(L.F("Сочетание {0} сохранено.", hotkey.Text));
        }
        else SayHotkey(error);
        ShowState();
    }

    private void SayHotkey(string text)
    {
        HotkeyResult.Text = text ?? "";
        HotkeyResult.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Say(string text)
    {
        Result.Text = text ?? "";
        Result.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
