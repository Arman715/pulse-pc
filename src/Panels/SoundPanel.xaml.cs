using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PulsePC.Services;

namespace PulsePC.Panels;

/// <summary>
/// Раздел «Звук»: устройства вывода и основное из них, переключение горячей клавишей, «улучшения», микрофон.
/// При открытии только читает; всё, что меняет систему, делается по кнопке.
/// </summary>
public partial class SoundPanel : UserControl, ISystemPanel
{
    /// <summary>Строка устройства вывода: одна и та же для списка устройств и для списка улучшений.</summary>
    public sealed class OutputRow
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Caption { get; init; } = "";
        public string Mark { get; init; } = "";
        public Brush MarkBrush { get; init; } = Palette.Ink3;
        public Visibility ButtonVisibility { get; init; }
        public string ButtonText { get; init; } = L.T("Сделать основным");
        public bool CanChange { get; init; }
        public string FxText { get; init; } = "";
        /// <summary>Улучшения выключены сейчас.</summary>
        public bool FxWas { get; init; }
        /// <summary>Положение переключателя «Отключить».</summary>
        public bool FxOff { get; set; }
        public bool FxCanToggle { get; init; }
        public Visibility FxToggleVisibility { get; init; }
    }

    /// <summary>Строка в круге переключения: устройство может быть сейчас отключено.</summary>
    public sealed class CycleRow
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Note { get; init; } = "";
        public bool InCycle { get; set; }
        public bool CanChange { get; init; }
    }

    private PanelHost host;
    private int ticket;
    private bool hotkeyShown;
    private List<OutputRow> outputs = new();
    private List<CycleRow> cycle = new();

    public SoundPanel()
    {
        InitializeComponent();
        KeyBox.ItemsSource = SoundHotkey.Keys.Select(k => k.Title).ToList();
        SelectKey("F10");
    }

    /// <summary>Выбранная в списке клавиша в виде для настроек («F10», «Num7»).</summary>
    internal string SelectedKey
    {
        get
        {
            string title = KeyBox.SelectedItem as string;
            foreach (var k in SoundHotkey.Keys) if (k.Title == title) return k.Token;
            return null;
        }
    }

    internal void SelectKey(string token)
    {
        foreach (var k in SoundHotkey.Keys)
            if (string.Equals(k.Token, token, StringComparison.OrdinalIgnoreCase)) { KeyBox.SelectedItem = k.Title; return; }
    }

    public void Attach(PanelHost panelHost)
    {
        host = panelHost;
        AudioDevices.Bind(host);
        AudioDevices.Changed += () => host.Ui(Reload);

        bool admin = host.Admin;
        NoAdmin.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
        HotkeyControls.IsEnabled = admin;
        BtnHotkeyOn.IsEnabled = admin;
        BtnFxApply.IsEnabled = admin;
    }

    public void Shown() => Reload();

    // ───────────────────────── чтение ─────────────────────────

    private void Reload()
    {
        var h = host;
        if (h == null) return;
        int mine = ++ticket;
        var saved = AudioDevices.SavedCycle(h.Settings);
        Task.Run(() =>
        {
            var devices = AudioDevices.Outputs();
            var mic = AudioDevices.DefaultInput();
            var writable = devices.Where(d => AudioDevices.Enhancements.CanWrite(d.Id)).Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Отмеченные раньше, но сейчас отключённые устройства остаются в круге: показываем их по имени.
            var away = saved.Where(id => !devices.Any(d => AudioDevices.SameId(d.Id, id)))
                .Select(id => (Id: id, Name: AudioDevices.NameOf(id))).ToList();
            bool backup = AudioDevices.Enhancements.HasBackup;
            h.Ui(() => { if (mine == ticket) Render(devices, mic, writable, away, saved, backup); });
        });
    }

    internal void Render(List<AudioDevice> devices, AudioDevice mic, HashSet<string> writable,
        List<(string Id, string Name)> away, List<string> saved, bool backup)
    {
        if (devices.Count == 0)
        {
            State.Text = L.T("Звуковых устройств вывода не найдено. Подключите наушники или колонки — они появятся здесь.");
            Intro.Visibility = Visibility.Collapsed;
            NoAdmin.Visibility = Visibility.Collapsed;
            Sections.Visibility = Visibility.Collapsed;
            return;
        }

        bool admin = host.Admin;
        var main = devices.FirstOrDefault(d => d.IsDefault);
        State.Text = main != null ? L.F("Звук идёт на «{0}».", main.Name) : L.T("Основное устройство вывода не выбрано.");
        Intro.Visibility = Visibility.Visible;
        NoAdmin.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
        Sections.Visibility = Visibility.Visible;

        // Несохранённые отметки круга переживают перечитывание списка.
        var marked = cycle.Count > 0
            ? cycle.Where(c => c.InCycle).Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : saved.ToHashSet(StringComparer.OrdinalIgnoreCase);

        outputs = devices.Select(d =>
        {
            bool known = d.Enhancements != SysFxState.Unknown;
            bool canWrite = known && writable.Contains(d.Id);
            return new OutputRow
            {
                Id = d.Id,
                Name = d.Name,
                Caption = d.Format.Length > 0 ? d.Format : L.T("Формат прочитать не удалось"),
                Mark = d.IsDefault ? L.T("ОСНОВНОЕ") : d.IsDefaultComm ? L.T("ДЛЯ СВЯЗИ") : "",
                MarkBrush = d.IsDefault ? Palette.Accent : Palette.Ink3,
                ButtonVisibility = d.IsDefault && (d.IsDefaultComm || !devices.Any(x => x.IsDefaultComm)) ? Visibility.Collapsed : Visibility.Visible,
                // Основное для программ, но звонки идут на другое устройство: кнопка переводит сюда и связь.
                ButtonText = d.IsDefault ? L.T("Сделать и для связи") : L.T("Сделать основным"),
                CanChange = admin,
                FxText = d.Enhancements switch
                {
                    SysFxState.On => L.T("Улучшения включены"),
                    SysFxState.Off => L.T("Улучшения выключены"),
                    _ => L.T("Состояние улучшений прочитать не удалось"),
                },
                FxWas = d.Enhancements == SysFxState.Off,
                FxOff = d.Enhancements == SysFxState.Off,
                // С правами администратора переключатель есть только там, где Windows разрешает запись.
                FxCanToggle = admin && canWrite,
                FxToggleVisibility = known && (!admin || canWrite) ? Visibility.Visible : Visibility.Collapsed,
            };
        }).ToList();
        OutputList.ItemsSource = outputs;
        FxList.ItemsSource = outputs;

        cycle = devices.Select(d => new CycleRow { Id = d.Id, Name = d.Name, InCycle = marked.Contains(d.Id), CanChange = admin })
            .Concat(away.Select(a => new CycleRow
            {
                Id = a.Id,
                Name = a.Name ?? L.T("Устройство, которого Windows больше не видит"),
                Note = L.T(" — сейчас не подключено"),
                InCycle = marked.Contains(a.Id),
                CanChange = admin,
            })).ToList();
        CycleList.ItemsSource = cycle;

        bool anyToggle = outputs.Any(o => o.FxToggleVisibility == Visibility.Visible);
        bool anyWritable = outputs.Any(o => o.FxCanToggle);
        BtnFxApply.Visibility = anyToggle ? Visibility.Visible : Visibility.Collapsed;
        BtnFxApply.IsEnabled = anyWritable;
        BtnFxRestore.Visibility = anyToggle || backup ? Visibility.Visible : Visibility.Collapsed;
        BtnFxRestore.IsEnabled = admin && backup;
        FxNote.Text = anyToggle
            ? L.T("Изменение подействует, когда Windows заново запустит звук на устройстве: точно — после перезагрузки или переподключения устройства, иногда раньше — когда все программы замолчат и заиграют снова. То, что уже играет, не прерывается.")
            : L.T("На этом компьютере Windows не даёт программам менять улучшения. Их можно выключить в свойствах устройства: вкладка «Улучшения» или «Дополнительно».");

        if (mic == null)
        {
            MicName.Text = L.T("Микрофон не найден");
            MicNote.Text = L.T("Основного устройства записи в Windows сейчас нет.");
        }
        else
        {
            MicName.Text = mic.Name;
            MicNote.Text = (mic.Format.Length > 0 ? L.F("Основное устройство записи, {0}.", mic.Format) : L.T("Основное устройство записи.")) + " "
                + (mic.Listening
                    ? L.T("Включено прослушивание: звук микрофона идёт в ваши наушники или колонки с задержкой. Выключается в свойствах микрофона, вкладка «Прослушать».")
                    : L.T("Прослушивание выключено: вы не слышите сами себя."));
        }

        ShowHotkey();
    }

    private void ShowHotkey()
    {
        var active = AudioDevices.ActiveHotkey;
        var known = active.IsEmpty ? AudioDevices.SavedHotkey(host.Settings) : active;
        if (!hotkeyShown && !known.IsEmpty)
        {
            ModCtrl.IsChecked = known.Ctrl; ModAlt.IsChecked = known.Alt; ModShift.IsChecked = known.Shift; ModWin.IsChecked = known.Win;
            SelectKey(known.Key);
        }
        hotkeyShown = true;

        HotkeyNow.Text = !active.IsEmpty ? L.F("Сейчас работает: {0}.", active.Text)
            : AudioDevices.HotkeyProblem ?? L.T("Клавиша не включена.");
        BtnHotkeyOn.Content = active.IsEmpty ? L.T("Включить клавишу") : L.T("Сохранить");
        BtnHotkeyOff.IsEnabled = host.Admin && !active.IsEmpty;
    }

    /// <summary>Имя устройства в кавычках языка интерфейса.</summary>
    private static string Quote(string name) => L.Pick("«" + name + "»", "“" + name + "”");

    private static void Say(TextBlock target, string text)
    {
        target.Text = text ?? "";
        target.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    // ───────────────────────── основное устройство ─────────────────────────

    private void MakeDefault_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id || host == null || !host.Admin) return;
        string name = outputs.FirstOrDefault(o => o.Id == id)?.Name ?? "";
        var h = host;
        h.BeginBusy();
        Task.Run(() =>
        {
            bool ok = AudioDevices.SetDefault(id, out string error);
            h.Ui(() =>
            {
                h.EndBusy();
                Say(SwitchResult, ok ? L.F("Основное устройство теперь «{0}».", name) : L.F("Не удалось сделать «{0}» основным. {1}", name, error));
                Reload();
            });
        });
    }

    // ───────────────────────── горячая клавиша ─────────────────────────

    private void CycleName_Click(object sender, MouseButtonEventArgs e) => Flip(sender);
    private void ModName_Click(object sender, MouseButtonEventArgs e) => Flip(sender);

    /// <summary>Нажатие на подпись ставит и снимает флажок рядом с ней.</summary>
    private static void Flip(object label)
    {
        if ((label as FrameworkElement)?.Tag is CheckBox box && box.IsEnabled) box.IsChecked = box.IsChecked != true;
    }

    private void HotkeyOn_Click(object sender, RoutedEventArgs e)
    {
        if (host == null || !host.Admin) return;
        var hotkey = new SoundHotkey(ModCtrl.IsChecked == true, ModAlt.IsChecked == true, ModShift.IsChecked == true, ModWin.IsChecked == true,
            SelectedKey);
        // Порядок круга не должен зависеть от того, какое устройство сейчас основное: сначала как было сохранено, новые — в конец.
        var order = AudioDevices.SavedCycle(host.Settings);
        int Place(string id) { int i = order.FindIndex(o => AudioDevices.SameId(o, id)); return i < 0 ? int.MaxValue : i; }
        var chosen = cycle.Where(c => c.InCycle).OrderBy(c => Place(c.Id)).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (AudioDevices.SetHotkey(hotkey, chosen.Select(c => c.Id).ToList(), out string error))
            Say(HotkeyResult, L.F("Клавиша включена: {0}. Переключает между: {1}.", hotkey.Text, string.Join(", ", chosen.Select(c => Quote(c.Name)))));
        else
            Say(HotkeyResult, error);
        ShowHotkey();
    }

    private void HotkeyOff_Click(object sender, RoutedEventArgs e)
    {
        if (host == null || !host.Admin) return;
        AudioDevices.ClearHotkey();
        Say(HotkeyResult, L.T("Клавиша отключена. Отмеченные устройства запомнены."));
        ShowHotkey();
    }

    // ───────────────────────── улучшения ─────────────────────────

    private void FxApply_Click(object sender, RoutedEventArgs e)
    {
        if (host == null || !host.Admin) return;
        var todo = outputs.Where(o => o.FxCanToggle && o.FxOff != o.FxWas).ToList();
        if (todo.Count == 0) { Say(FxResult, L.T("Менять нечего: переключатели стоят так же, как настройки устройств.")); return; }
        var h = host;
        h.BeginBusy();
        Task.Run(() =>
        {
            var done = new List<string>();
            var failed = new List<string>();
            foreach (var row in todo)
            {
                if (AudioDevices.Enhancements.Set(row.Id, row.FxOff, out string error))
                    done.Add(row.FxOff ? L.F("«{0}» — выключены", row.Name) : L.F("«{0}» — включены", row.Name));
                else failed.Add(Quote(row.Name) + ": " + error);
            }
            h.Ui(() =>
            {
                h.EndBusy();
                string text = done.Count > 0 ? L.F("Улучшения: {0}. Подействует после перезагрузки или переподключения устройства.", string.Join("; ", done)) : "";
                if (failed.Count > 0) text += (text.Length > 0 ? " " : "") + L.F("Не удалось: {0} Откройте свойства устройства и выключите улучшения там.", string.Join("; ", failed));
                Say(FxResult, text);
                Reload();
            });
        });
    }

    private void FxRestore_Click(object sender, RoutedEventArgs e)
    {
        if (host == null || !host.Admin) return;
        var h = host;
        h.BeginBusy();
        Task.Run(() =>
        {
            int count = AudioDevices.Enhancements.Restore(out string error);
            h.Ui(() =>
            {
                h.EndBusy();
                string text = count > 0 ? L.F("Улучшения возвращены как были: {0}.", Fmt.Plural(count, "устройство", "устройства", "устройств")) : L.T("Возвращать нечего.");
                Say(FxResult, error != null ? text + " " + error : text);
                Reload();
            });
        });
    }

    private void OpenProperties_Click(object sender, RoutedEventArgs e)
    {
        if (!AudioDevices.OpenSoundWindow()) Say(FxResult, L.T("Не удалось открыть окно «Звук». Откройте его через «Параметры» → «Система» → «Звук»."));
    }
}
