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
        if (!readable) HotResult.Text = L.T("Windows не отдала настройки специальных возможностей. Откройте раздел ещё раз.");

        SpeedValue.Text = pointer.Ok ? L.F("{0} из 20", pointer.Speed) : "—";
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
        word.Text = !readable ? "—" : active ? L.T("срабатывает") : L.T("отключено");
    }

    /// <summary>Фраза вверху раздела и подпись под переключателем клавиши Win.</summary>
    private void ShowState()
    {
        bool winLock = WinLock.IsChecked == true;
        if (hotkeys == null || !hotkeys.Readable) State.Text = L.T("Сочетания прочитать не удалось");
        else if (hotkeys.Count > 0)
        {
            string count = Fmt.Plural(hotkeys.Count, "сочетание", "сочетания", "сочетаний");
            State.Text = hotkeys.Count == 1
                ? (winLock ? L.F("В игре может помешать {0}", count) : L.F("В игре может помешать {0} и клавиша Win", count))
                : (winLock ? L.F("В игре могут помешать {0}", count) : L.F("В игре могут помешать {0} и клавиша Win", count));
        }
        else State.Text = winLock ? L.T("Сочетания отключены, клавиша Win в игре блокируется") : L.T("Сочетания отключены, клавиша Win в игре работает");

        WinNote.Text = !winLock ? L.T("Сейчас блокировка выключена: клавиша Win работает везде.")
            : InputTuning.WinLockActive ? (GameHooks.Game.Length > 0 ? L.F("Клавиша Win заблокирована: идёт игра «{0}».", GameHooks.Game) : L.T("Клавиша Win заблокирована: идёт игра."))
            : GameHooks.InGame ? L.T("Блокировка включена, но заблокировать клавишу не удалось. Выключите и снова включите переключатель.")
            : L.T("Блокировка включена. Клавиша Win заблокируется, когда программа увидит запущенную игру, и снова заработает, когда игра закроется.");
    }

    // ---------- Сочетания ----------

    private void HotOff_Click(object sender, RoutedEventArgs e) => RunHotkeys(L.T("Отключаю…"), InputTuning.DisableHotkeys);
    private void HotRestore_Click(object sender, RoutedEventArgs e) => RunHotkeys(L.T("Возвращаю…"), InputTuning.RestoreHotkeys);

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
            catch (Exception ex) { message = L.F("Не удалось: {0}", ex.Message); }
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
                message = off == applied ? (off ? L.T("Ускорение выключено.") : L.T("Ускорение включено, как в Windows по умолчанию."))
                                         : L.T("Windows не приняла изменение. Попробуйте ещё раз.");
            }
            catch (Exception ex) { message = L.F("Не удалось: {0}", ex.Message); }
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
                PointerNote.Text = message + L.T(" Если обновление Windows снова включит ускорение, программа выключит его сама.");
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
            RateNote.Text = L.T("Идёт игра. Замер сделайте после неё: он на три секунды забирает мышь на себя.");
            return;
        }
        var source = PresentationSource.FromVisual(this) as HwndSource;
        if (source == null || source.Handle == IntPtr.Zero)
        {
            RateNote.Text = L.T("Замер недоступен: раздел не подключён к окну программы.");
            return;
        }
        if (!InputTuning.BeginMouseCapture(source.Handle))
        {
            RateNote.Text = L.T("Windows не разрешила читать отчёты мыши. Попробуйте ещё раз.");
            return;
        }

        reports.Clear();
        measureSource = source;
        measureSource.AddHook(MouseHook);
        measureStart = Stopwatch.GetTimestamp();
        BtnMeasure.IsEnabled = false;
        ShowRate(null);
        RateWhen.Visibility = RateCompare.Visibility = Visibility.Collapsed;
        RateEmpty.Text = L.T("идёт замер");
        RateNote.Text = L.F("Водите мышью кругами, быстро и без остановок… {0:0}\u00A0с", InputTuning.MeasureSeconds);
        measureTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(100) };
        measureTimer.Tick += (_, _) =>
        {
            double left = InputTuning.MeasureSeconds - Stopwatch.GetElapsedTime(measureStart).TotalSeconds;
            if (left > 0) RateNote.Text = L.F("Водите мышью кругами, быстро и без остановок… {0:0}\u00A0с", Math.Ceiling(left));
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
                ? L.T("Мышь не прислала ни одного отчёта. Окно программы должно быть активным; нажмите «Измерить» и сразу водите мышью.")
                : L.T("Мало данных: мышь двигалась слишком мало. Нажмите «Измерить» и все три секунды водите мышью без остановок.");
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
        RateNote.Text = InputTuning.RateVerdict(rate.Rounded) + L.F(" Получено отчётов: {0}. Если водить мышью медленно, число выйдет заниженным.", rate.Reports);
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
        MiceList.ItemsSource = mice.Select(m => new MouseRow(m.Title, m.Caption.Length > 0 ? m.Caption : L.T("кодов производителя нет"),
            many && last.Id.Length > 0 && m.Id == last.Id ? L.T("замер по ней") : "")).ToList();
        MiceNote.Visibility = many ? Visibility.Visible : Visibility.Collapsed;

        if (measureSource != null) return;          // идёт замер: число и совет покажет его итог
        if (last.Hz <= 0)
        {
            ShowRate(null);
            RateWhen.Visibility = Visibility.Collapsed;
            RaiseBlock.Visibility = Visibility.Collapsed;
            BtnMeasure.Content = L.T("Измерить");
            return;
        }
        ShowRate(L.F("около {0}\u00A0Гц", last.Hz));
        RateWhen.Text = last.At == default ? "" : last.At.Date == DateTime.Today ? L.F("сегодня, {0:HH:mm}", last.At)
            : last.At.ToString(L.Pick("d MMMM, HH:mm", "MMMM d, HH:mm"), L.Culture);
        RateWhen.Visibility = RateWhen.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnMeasure.Content = L.T("Измерить ещё раз");

        if (last.Hz > InputTuning.LowRate) { RaiseBlock.Visibility = Visibility.Collapsed; return; }
        // Совет — для той мыши, по которой был замер; если она неизвестна, а мышь одна — для неё.
        var known = mice.Where(m => m.Vid >= 0).ToList();
        var mouse = mice.FirstOrDefault(m => last.Id.Length > 0 && m.Id == last.Id) ?? (known.Count == 1 ? known[0] : null);
        string brand = mouse?.Brand ?? "";
        RaiseTitle.Text = brand.Length > 0 ? L.F("{0}\u00A0Гц — мало для игр. Как поднять частоту у мыши {1}", last.Hz, brand)
                                    : L.F("{0}\u00A0Гц — мало для игр. Как поднять частоту", last.Hz);
        RaiseText.Text = InputTuning.RaiseAdvice(mouse?.Vid ?? -1, mouse?.Product ?? "", mouse?.Bluetooth == true);
        RaiseBlock.Visibility = Visibility.Visible;
    }

    /// <summary>Число справа в строке замера; null — замера ещё нет.</summary>
    private void ShowRate(string text)
    {
        RateValue.Text = text ?? "";
        RateValue.Visibility = text != null ? Visibility.Visible : Visibility.Collapsed;
        RateEmpty.Text = L.T("не измерено");
        RateEmpty.Visibility = text != null ? Visibility.Collapsed : Visibility.Visible;
    }
}
