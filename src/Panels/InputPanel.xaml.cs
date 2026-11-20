using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using PulsePC.Services;

namespace PulsePC.Panels;

/// <summary>
/// Раздел «Клавиатура и мышь»: сочетания специальных возможностей, блокировка клавиши Win в игре,
/// замер частоты опроса мыши и ускорение указателя (переключатель перенесён сюда с вкладки «Оптимизация»).
/// </summary>
public partial class InputPanel : UserControl, ISystemPanel
{
    private PanelHost host;
    private bool busy;
    private HotkeyState hotkeys;

    // Замер мыши: живёт только между нажатием «Измерить» и итогом.
    private HwndSource measureSource;
    private DispatcherTimer measureTimer;
    private long measureStart;
    private readonly List<(long Ticks, IntPtr Device)> reports = new(32768);

    public InputPanel()
    {
        InitializeComponent();
        Unloaded += (_, _) => StopMeasure();
    }

    public void Attach(PanelHost panelHost)
    {
        host = panelHost;
        AdminNote.Visibility = host.Admin ? Visibility.Collapsed : Visibility.Visible;
        WinLock.IsChecked = host.Settings.GetBool(InputTuning.WinLockKey);
        WinLock.IsEnabled = host.Admin;
    }

    public void Shown()
    {
        if (host == null || busy) return;
        Task.Run(() =>
        {
            HotkeyState hot = null;
            (bool Ok, int Speed, bool Acceleration) pointer = default;
            try { hot = InputTuning.ReadHotkeys(); pointer = InputTuning.ReadPointer(); } catch { }
            bool backup = InputTuning.HasHotkeyBackup;
            List<InputTuning.MouseDevice> found = new();
            try { found = InputTuning.ListMice(); } catch { }
            host.Ui(() => { Show(hot, pointer, backup); mice = found; ShowMouse(); });
        });
    }

    private void Show(HotkeyState hot, (bool Ok, int Speed, bool Acceleration) pointer, bool backup)
    {
        hotkeys = hot;
        bool readable = hot != null && hot.Readable;
        SetMark(StickyMark, StickyWord, readable, hot?.Sticky == true);
        SetMark(FilterMark, FilterWord, readable, hot?.Filter == true);
        SetMark(ToggleMark, ToggleWord, readable, hot?.Toggle == true);
        BtnHotOff.IsEnabled = host.Admin && !busy && readable && hot.Count > 0;
        BtnHotRestore.IsEnabled = host.Admin && !busy && backup;
        if (!readable) HotResult.Text = "Windows не отдала настройки специальных возможностей. Откройте раздел ещё раз.";

        SpeedValue.Text = pointer.Ok ? $"{pointer.Speed} из 20" : "—";
        if (!accelBusy)
        {
            AccelOff.IsChecked = pointer.Ok && !pointer.Acceleration;
            AccelOff.IsEnabled = host.Admin && pointer.Ok;
        }

        WinLock.IsChecked = host.Settings.GetBool(InputTuning.WinLockKey);
        ShowState();
    }

    private static void SetMark(StateMarker mark, TextBlock word, bool readable, bool active)
    {
        mark.Visibility = readable ? Visibility.Visible : Visibility.Collapsed;
        mark.Level = active ? Severity.Info : Severity.Ok;
        word.Text = !readable ? "—" : active ? "срабатывает" : "отключено";
    }

    /// <summary>Фраза вверху раздела и подпись под переключателем клавиши Win.</summary>
    private void ShowState()
    {
        bool winLock = WinLock.IsChecked == true;
        if (hotkeys == null || !hotkeys.Readable) State.Text = "Сочетания прочитать не удалось";
        else if (hotkeys.Count > 0)
            State.Text = $"В игре {(hotkeys.Count == 1 ? "может" : "могут")} помешать {Fmt.Plural(hotkeys.Count, "сочетание", "сочетания", "сочетаний")}"
                         + (winLock ? "" : " и клавиша Win");
        else State.Text = winLock ? "Сочетания отключены, клавиша Win в игре блокируется" : "Сочетания отключены, клавиша Win в игре работает";

        WinNote.Text = !winLock ? "Сейчас блокировка выключена: клавиша Win работает везде."
            : InputTuning.WinLockActive ? $"Клавиша Win заблокирована: идёт игра{(GameHooks.Game.Length > 0 ? " «" + GameHooks.Game + "»" : "")}."
            : GameHooks.InGame ? "Блокировка включена, но заблокировать клавишу не удалось. Выключите и снова включите переключатель."
            : "Блокировка включена. Клавиша Win заблокируется, когда программа увидит запущенную игру, и снова заработает, когда игра закроется.";
    }

    // ---------- Сочетания ----------

    private void HotOff_Click(object sender, RoutedEventArgs e) => RunHotkeys("Отключаю…", InputTuning.DisableHotkeys);
    private void HotRestore_Click(object sender, RoutedEventArgs e) => RunHotkeys("Возвращаю…", InputTuning.RestoreHotkeys);

    private void RunHotkeys(string progress, Func<(bool Ok, string Message)> action)
    {
        if (busy || host?.Admin != true) return;
        busy = true;
        HotResult.Text = progress;
        BtnHotOff.IsEnabled = BtnHotRestore.IsEnabled = false;
        host.BeginBusy();
        Task.Run(() =>
        {
            string message;
            HotkeyState hot = null;
            (bool Ok, int Speed, bool Acceleration) pointer = default;
            try { message = action().Message; }
            catch (Exception ex) { message = "Не удалось: " + ex.Message; }
            try { hot = InputTuning.ReadHotkeys(); pointer = InputTuning.ReadPointer(); } catch { }
            bool backup = InputTuning.HasHotkeyBackup;
            host.Ui(() =>
            {
                host.EndBusy();
                busy = false;
                Show(hot, pointer, backup);
                HotResult.Text = message;
            });
        });
    }

    // ---------- Ускорение мыши ----------

    private bool accelBusy;

    /// <summary>
    /// Та же настройка, что раньше была на вкладке «Оптимизация» («Ускорение мыши выключено»): то же название для сторожа,
    /// поэтому выключенное раньше ускорение он и дальше возвращает, если его включит обновление Windows.
    /// </summary>
    private void AccelOff_Click(object sender, RoutedEventArgs e)
    {
        if (host?.Admin != true || accelBusy) return;
        bool off = AccelOff.IsChecked == true;
        accelBusy = true;
        AccelOff.IsEnabled = false;
        Task.Run(() =>
        {
            var item = Tweaks.MouseAccel();
            string message;
            bool applied = false;
            try
            {
                if (off) item.Apply(); else item.Revert();
                applied = item.Check();
                message = off == applied ? (off ? "Ускорение выключено." : "Ускорение включено, как в Windows по умолчанию.")
                                         : "Windows не приняла изменение. Попробуйте ещё раз.";
            }
            catch (Exception ex) { message = "Не удалось: " + ex.Message; }
            // Сторож следит только за тем, что выключено по выбору пользователя.
            Tweaks.Remember(host.Settings, Tweaks.MouseTitle, off && applied);
            var values = host.Settings.Values;
            if (values != null) lock (values) host.Settings.Save(); else host.Settings.Save();
            (bool Ok, int Speed, bool Acceleration) pointer = default;
            try { pointer = InputTuning.ReadPointer(); } catch { }
            host.Ui(() =>
            {
                accelBusy = false;
                AccelOff.IsChecked = pointer.Ok ? !pointer.Acceleration : applied;
                AccelOff.IsEnabled = host.Admin;
                PointerNote.Text = message + " Если обновление Windows снова включит ускорение, программа выключит его сама.";
            });
        });
    }

    // ---------- Клавиша Win ----------

    private void WinLock_Click(object sender, RoutedEventArgs e)
    {
        if (host?.Admin != true) return;
        bool on = WinLock.IsChecked == true;
        WinLock.IsEnabled = false;
        // Постановка перехвата ждёт свой поток, запись настроек — диск: не в потоке интерфейса.
        Task.Run(() =>
        {
            try { InputTuning.SetWinLock(host.Settings, on); } catch { }
            host.Ui(() => { WinLock.IsEnabled = true; ShowState(); });
        });
    }

    // ---------- Частота опроса мыши ----------

    private void Measure_Click(object sender, RoutedEventArgs e)
    {
        if (measureSource != null) return;
        if (GameHooks.InGame)
        {
            RateNote.Text = "Идёт игра. Замер сделайте после неё: он на три секунды забирает мышь на себя.";
            return;
        }
        var source = PresentationSource.FromVisual(this) as HwndSource;
        if (source == null || source.Handle == IntPtr.Zero)
        {
            RateNote.Text = "Замер недоступен: раздел не подключён к окну программы.";
            return;
        }
        if (!InputTuning.BeginMouseCapture(source.Handle))
        {
            RateNote.Text = "Windows не разрешила читать отчёты мыши. Попробуйте ещё раз.";
            return;
        }

        reports.Clear();
        measureSource = source;
        measureSource.AddHook(MouseHook);
        measureStart = Stopwatch.GetTimestamp();
        BtnMeasure.IsEnabled = false;
        ShowRate(null);
        RateWhen.Visibility = RateCompare.Visibility = Visibility.Collapsed;
        RateEmpty.Text = "идёт замер";
        RateNote.Text = "Водите мышью кругами, быстро и без остановок… 3 с";
        measureTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(100) };
        measureTimer.Tick += (_, _) =>
        {
            double left = InputTuning.MeasureSeconds - Stopwatch.GetElapsedTime(measureStart).TotalSeconds;
            if (left > 0) RateNote.Text = $"Водите мышью кругами, быстро и без остановок… {Math.Ceiling(left):0} с";
            else FinishMeasure();
        };
        measureTimer.Start();
    }

    /// <summary>Отчёт мыши: только записать метку времени. Вся обработка — после замера.</summary>
    private IntPtr MouseHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x00FF /* WM_INPUT */)
        {
            long now = Stopwatch.GetTimestamp();
            if (InputTuning.IsMouseReport(msg, lParam, out IntPtr device)) reports.Add((now, device));
        }
        return IntPtr.Zero;
    }

    /// <summary>Снять регистрацию отчётов и перехват сообщений окна.</summary>
    private void StopMeasure()
    {
        measureTimer?.Stop();
        measureTimer = null;
        if (measureSource == null) return;
        try { InputTuning.EndMouseCapture(); } catch { }
        try { measureSource.RemoveHook(MouseHook); } catch { }
        measureSource = null;
        BtnMeasure.IsEnabled = true;
    }

    private void FinishMeasure()
    {
        StopMeasure();
        // Отчёты, пришедшие после трёх секунд (таймер мог опоздать), в расчёт не идут.
        long end = measureStart + (long)(InputTuning.MeasureSeconds * Stopwatch.Frequency);
        var rate = InputTuning.Estimate(reports.Where(r => r.Ticks <= end).ToList());
        IntPtr device = InputTuning.MainDevice(reports.Where(r => r.Ticks <= end).ToList());
        reports.Clear();
        if (!rate.Enough)
        {
            ShowMouse();                      // прежний замер остаётся на месте
            RateNote.Text = rate.Reports == 0
                ? "Мышь не прислала ни одного отчёта. Окно программы должно быть активным; нажмите «Измерить» и сразу водите мышью."
                : "Мало данных: мышь двигалась слишком мало. Нажмите «Измерить» и все три секунды водите мышью без остановок.";
            return;
        }
        // Какая мышь прислала отчёты: по ней — марка, совет и сравнение с прошлым замером.
        InputTuning.MouseDevice measured = null;
        try { measured = InputTuning.Describe(device); } catch { }
        if (measured != null && measured.Vid >= 0 && !mice.Any(m => m.Id == measured.Id)) mice.Insert(0, measured);
        string id = measured?.Id ?? "";
        var before = InputTuning.LastMeasure(host?.Settings);
        string compare = InputTuning.CompareText(before.Hz, before.At, before.Id, rate.Rounded, id, DateTime.Now);
        if (host != null)
        {
            InputTuning.RememberMeasure(host.Settings, rate.Rounded, DateTime.Now, id);
            var settings = host.Settings;
            Task.Run(() => { var values = settings.Values; if (values != null) lock (values) settings.Save(); else settings.Save(); });
        }
        ShowMouse();
        RateCompare.Text = compare;
        RateCompare.Visibility = compare.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RateNote.Text = InputTuning.RateVerdict(rate.Rounded) + $" Получено отчётов: {rate.Reports}. Если водить мышью медленно, число выйдет заниженным.";
    }

    // ---------- Модель мыши, последний замер, совет ----------

    private List<InputTuning.MouseDevice> mice = new();

    private sealed record MouseRow(string Title, string Caption, string Mark);

    /// <summary>Список мышей, последний сохранённый замер и совет, если частота низкая.</summary>
    private void ShowMouse()
    {
        if (host == null) return;
        var last = InputTuning.LastMeasure(host.Settings);
        bool many = mice.Count(m => m.Vid >= 0) > 1;
        MiceList.ItemsSource = mice.Select(m => new MouseRow(m.Title, m.Caption.Length > 0 ? m.Caption : "кодов производителя нет",
            many && last.Id.Length > 0 && m.Id == last.Id ? "замер по ней" : "")).ToList();
        MiceNote.Visibility = many ? Visibility.Visible : Visibility.Collapsed;

        if (measureSource != null) return;          // идёт замер: число и совет покажет его итог
        if (last.Hz <= 0)
        {
            ShowRate(null);
            RateWhen.Visibility = Visibility.Collapsed;
            RaiseBlock.Visibility = Visibility.Collapsed;
            BtnMeasure.Content = "Измерить";
            return;
        }
        ShowRate($"около {last.Hz}{Nb}Гц");
        RateWhen.Text = last.At == default ? "" : last.At.Date == DateTime.Today ? $"сегодня, {last.At:HH:mm}"
            : last.At.ToString("d MMMM, HH:mm", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
        RateWhen.Visibility = RateWhen.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnMeasure.Content = "Измерить ещё раз";

        if (last.Hz > InputTuning.LowRate) { RaiseBlock.Visibility = Visibility.Collapsed; return; }
        // Совет — для той мыши, по которой был замер; если она неизвестна, а мышь одна — для неё.
        var known = mice.Where(m => m.Vid >= 0).ToList();
        var mouse = mice.FirstOrDefault(m => last.Id.Length > 0 && m.Id == last.Id) ?? (known.Count == 1 ? known[0] : null);
        string brand = mouse?.Brand ?? "";
        RaiseTitle.Text = $"{last.Hz}{Nb}Гц — мало для игр. Как поднять частоту" + (brand.Length > 0 ? $" у мыши {brand}" : "");
        RaiseText.Text = InputTuning.RaiseAdvice(mouse?.Vid ?? -1, mouse?.Product ?? "", mouse?.Bluetooth == true);
        RaiseBlock.Visibility = Visibility.Visible;
    }

    private const string Nb = " ";

    /// <summary>Число справа в строке замера; null — замера ещё нет.</summary>
    private void ShowRate(string text)
    {
        RateValue.Text = text ?? "";
        RateValue.Visibility = text != null ? Visibility.Visible : Visibility.Collapsed;
        RateEmpty.Text = "не измерено";
        RateEmpty.Visibility = text != null ? Visibility.Collapsed : Visibility.Visible;
    }
}
