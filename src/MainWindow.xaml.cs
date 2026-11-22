using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PulsePC.Services;

namespace PulsePC;

public partial class MainWindow : Window
{
    private readonly Metrics metrics = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly bool admin = Shell.IsAdmin();

    private readonly ObservableCollection<ProcVm> procs = new();
    private readonly ObservableCollection<DriveVm> drives = new();
    private readonly ObservableCollection<DiagItem> diagAttention = new(), diagInfo = new(), diagOk = new();
    private readonly List<CleanItem> cleanAll;
    private readonly ObservableCollection<CleanItem> cleanShown = new(), cleanEmpty = new();
    private readonly ObservableCollection<TweakItem> tweaks = new();
    private readonly ObservableCollection<GameAppItem> apps = new();

    private readonly ObservableCollection<StartupItem> startup = new();
    private readonly ObservableCollection<GameSession> sessions = new();

    private readonly AppSettings settings = AppSettings.Load();
    private readonly NetMonitor net = new();
    private readonly SessionRecorder recorder = new();
    private readonly HistoryLog history = new();
    private readonly List<string> closedApps = new();      // что открыть снова после игры
    private TrayIcon tray;
    private Alerts alerts;

    // Порядок совпадает с порядком вкладок в заголовке.
    private const int PgOverview = 0, PgSessions = 1, PgHistory = 2, PgNet = 3, PgDiag = 4, PgClean = 5, PgTweaks = 6, PgSystem = 7;
    /// <summary>Автозагрузка раньше была восьмой вкладкой; теперь это раздел «Системы», а старый номер приходит из осмотра.</summary>
    private const int PgStartupLegacy = 7;

    private FrameworkElement[] pages;
    private int page, tick, busy, histMinutes = 360;
    private bool scanned, disksShown, syncingTweaks, exiting, detecting, startupLoaded, watching, histOpened, recBlinking;
    private volatile DetectedGame detectedGame;

    public MainWindow()
    {
        InitializeComponent();
        WindowFit.AttachMain(this, settings);

        pages = new FrameworkElement[] { PageOverview, PageSessions, PageHistory, PageNet, PageDiag, PageClean, PageTweaks, PageSystem, PageHelp };
        foreach (var p in pages) p.RenderTransform = new TranslateTransform();

        Motion.Init(this);
        Motion.Changed += UpdateActivity;
        AdminText.Text = admin ? "" : L.T("только просмотр");
        AdminBadge.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
        AdminBadge.ToolTip = admin ? null : L.T("Программа запущена без прав администратора: показывает, но ничего не меняет.");
        LiveDot.Opacity = 0.35;

        for (int i = 0; i < 6; i++) procs.Add(new ProcVm());
        ProcList.ItemsSource = procs;
        DriveList.ItemsSource = drives;
        DiagAttention.ItemsSource = diagAttention;
        DiagInfo.ItemsSource = diagInfo;
        DiagOk.ItemsSource = diagOk;

        cleanAll = Cleaner.BuildItems();
        foreach (var item in cleanAll)
        {
            item.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(CleanItem.Selected) or nameof(CleanItem.Bytes)) OnUi(UpdateCleanSelection); };
            cleanShown.Add(item);
        }
        CleanList.ItemsSource = cleanShown;
        CleanEmptyList.ItemsSource = cleanEmpty;

        foreach (var item in Tweaks.BuildItems())
        {
            item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TweakItem.Desired)) OnUi(UpdatePending); };
            tweaks.Add(item);
        }
        TweakList.ItemsSource = tweaks;
        TweakList.IsEnabled = admin;
        AppList.ItemsSource = apps;

        StartupList.ItemsSource = startup;
        StartupList.IsEnabled = admin;
        SessionList.ItemsSource = sessions;
        foreach (var s in SessionStore.Load()) sessions.Add(s);

        // Значок у часов: программа продолжает работать в фоне и записывает игровые сессии.
        tray = new TrayIcon(settings);
        tray.ShowRequested += ShowFromTray;
        tray.ExitRequested += () => { exiting = true; Close(); };
        tray.SettingsChanged += ApplyFpsSetting;
        alerts = new Alerts(tray, settings);
        ApplyFpsSetting();
        history.Load();
        RestoreCheck.IsChecked = settings.RestoreApps;
        UpdateWatchText();
        recorder.Finished += session => OnUi(() => OnSessionFinished(session));     // событие приходит из фонового потока
        net.Sampled += () => Dispatcher.BeginInvoke(new Action(UpdateNetPage));

        StateChanged += (_, _) => OnStateChanged();
        // Возврат фокуса: начатые прорисовки не доигрываются, а сразу показываются готовыми.
        Activated += (_, _) => { Drawing.Finish(); RouterDrawing.Finish(); PadDrawing.Finish(); };
        SizeChanged += (_, _) => { if (busy > 0) UpdateActivity(); };
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            closed = true;
            FlushForShutdown(resume: false);
            PhoneServer.Stop();
            net.Dispose(); metrics.Dispose(); tray.Dispose();
        };

        // Сеансы трассировки «PulsePC…», оставшиеся от упавших прошлых запусков, — убрать (свои живые не трогаются).
        if (admin) Task.Run(() => { try { EtwSessions.CleanupStale(); } catch (Exception ex) { Log.Error("уборка сеансов трассировки", ex); } });
        metrics.Start();
        net.Start();
        timer.Tick += (_, _) => OnTick();
        timer.Start();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        MoveTabLine(false);
        UpdateSheetNo();
        InitOverview();
        InitUpkeep();
        InitCustomGames();
        InitTitleButtons();
        sessions.CollectionChanged += (_, _) => { UpdateRateBar(); UpdateCleanSessions(); ApplySessionsSide(); };
        UpdateCleanSessions();
        InitSystem();
        InitHealth();       // оценка здоровья ПК и «Почему тормозит» (MainWindow.Health.cs)
        // F1 — справка, откуда угодно в окне.
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.F1) { ShowHelp(); e.Handled = true; } };
        // Один раз после обновления: прежний перезапуск сетевой карты мог оставить её выключенной — включить обратно.
        if (admin && !settings.GetBool("net.ensured"))
        {
            settings.SetBool("net.ensured", true); settings.Save();
            _ = Task.Run(() => { try { NetPower.EnsureEnabled(); } catch { } });
        }
        UpdateOverview();
        UpdateCleanSelection();
        ShowSession(sessions.FirstOrDefault());
        Motion.Reveal(pages[0], 0, 12, Motion.Fast, Motion.Normal);
        await RefreshTweaksAsync();
        RefreshApps();
        LoadGames();
        LoadTunes(notify: true);
        // Первый запуск: осмотр всей системы, затем результаты со ссылками на разделы.
        if (!settings.FirstScanDone) ShowScan();
    }

    private void BtnFullScan_Click(object sender, RoutedEventArgs e) => ShowScan();

    private void ShowScan()
    {
        var w = new ScanWindow { Owner = this };
        w.NavigateRequested += p => { if (p == PgStartupLegacy) ShowSystemSection("Автозагрузка"); else SelectTab(p); };
        w.SectionRequested += ShowSystemSection;
        w.Completed += _ =>
        {
            bool first = !settings.FirstScanDone;
            settings.FirstScanDone = true; settings.Save();
            // После самого первого осмотра — одна подсказка, где искать справку.
            if (first && settings.AlertsEnabled)
                tray.Notify(L.T("Как пользоваться программой"), L.T("Справка — кнопка «?» вверху окна или клавиша F1. Нажмите, чтобы открыть."), false, () => { ShowFromTray(); ShowHelp(); });
        };
        w.Show();
    }

    /// <summary>Кадры записываются только с правами администратора: без них Windows не отдаёт события о выводе кадров.</summary>
    private void ApplyFpsSetting() => recorder.RecordFps = admin && settings.RecordFps;

    // ================================================================== трей
    /// <summary>Выход по просьбе установщика: окно закрывается по-настоящему, без «свернуть к часам» и подсказки.</summary>
    public void PrepareExit() => exiting = true;

    /// <summary>
    /// Выход из программы или из Windows: сначала сохранить идущую сессию, затем вернуть всё, что менялось на время игры
    /// (схема питания, службы обновлений, клавиша Win), отдать вентиляторы драйверу и сохранить историю.
    /// Повторный вызов безопасен. resume — продолжить работу после (выход из Windows могли отменить).
    /// </summary>
    public void FlushForShutdown(bool resume)
    {
        timer.Stop();
        try { recorder.Stop(net); } catch (Exception ex) { Log.Error("выход: сохранение сессии", ex); }
        try { hookChain.Wait(3000); } catch { }        // события, поставленные в очередь раньше, — до «игра закончилась»
        try { GameHooks.RaiseEnded(wait: true); } catch (Exception ex) { Log.Error("выход: возврат игровых настроек", ex); }
        try { FanControl.Shutdown(); } catch (Exception ex) { Log.Error("выход: вентиляторы", ex); }     // вентиляторы видеокарты — снова драйверу
        try { history.Save(); } catch (Exception ex) { Log.Error("выход: история", ex); }
        if (resume)
        {
            timer.Start();
            // Выход из Windows могли отменить (программа с несохранённым документом, пользователь нажал «Отмена»): если через
            // 30 секунд программа всё ещё работает, вентиляторы снова идут по выбранному режиму, а не остаются у драйвера.
            var again = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            again.Tick += (_, _) =>
            {
                again.Stop();
                if (!closed) { try { FanControl.Resume(); } catch (Exception ex) { Log.Error("вентиляторы после отменённого выхода", ex); } }
            };
            again.Start();
        }
    }

    /// <summary>Окно закрыто (программа выходит): отложенное продолжение работы после выхода из Windows уже не нужно.</summary>
    private bool closed;

    // События «игра началась / закончилась» уходят службам строго по очереди: при смене игры за один такт «закончилась» для
    // прежней должна отработать раньше, чем «началась» для новой (обработчики GameHooks идут в фоне).
    private Task hookChain = Task.CompletedTask;

    private void QueueHooks(Action raise) =>
        hookChain = hookChain.ContinueWith(_ => { try { raise(); } catch (Exception ex) { Log.Error("игровые события", ex); } }, TaskScheduler.Default);

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (exiting || !settings.CloseToTray) return;
        // Крестик прячет окно к часам: запись сессий и предупреждения продолжают работать.
        e.Cancel = true;
        Hide();
        if (!settings.TrayHintShown)
        {
            settings.TrayHintShown = true; settings.Save();
            tray.Notify(L.T("Пульс ПК продолжает работать"), L.T("Значок — у часов, игры записываются в фоне. Чтобы выйти, нажмите на значок правой кнопкой мыши."));
        }
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        UpdateOverview();
    }

    private void OnUi(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    // ================================================================== окно
    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void OnStateChanged()
    {
        // Развёрнутое окно с собственным заголовком выступает за край экрана на толщину рамки.
        Root.Padding = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        // Подсказка и имя для экранного диктора — по состоянию окна.
        MaxButton.ToolTip = WindowState == WindowState.Maximized ? L.T("Восстановить") : L.T("Развернуть");
        System.Windows.Automation.AutomationProperties.SetName(MaxButton, (string)MaxButton.ToolTip);
        if (WindowState != WindowState.Minimized) UpdateOverview();   // после сворачивания сразу показать свежие данные
    }

    // ================================================================== вкладки
    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (pages == null) return;   // окно ещё создаётся
        int index = Tabs.Children.IndexOf((UIElement)sender);
        if (index < 0 || index == page) return;

        int old = page;
        page = index;
        BtnHelp.ClearValue(ForegroundProperty);      // справка закрыта — кнопка «?» снова обычная
        for (int i = 0; i < pages.Length; i++) pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        // Новая страница проявляется и приезжает с той стороны, куда двинулись по вкладкам.
        Motion.Reveal(pages[index], 0, index > old ? 12 : -12, Motion.Fast, Motion.Normal);
        MoveTabLine(true);
        UpdateSheetNo();
        PlayPageHeader(index);

        switch (index)
        {
            case PgOverview: UpdateOverview(); ShowHealth(); Drawing.Play(); break;
            case PgSessions: UpdateSessionLive(); PadDrawing.Play(); break;
            case PgHistory: HistScroll.ScrollToTop(); OpenHistory(); break;
            case PgNet: net.Active = true; UpdateNetPage(); RouterDrawing.Play(); ShowNetChecks(); break;
            case PgDiag: LoadBoot(); LoadStability(); break;
            case PgTweaks: RefreshApps(); LoadTunes(notify: false); break;
            case PgSystem: OpenSystem(); break;
        }
    }

    /// <summary>Переключить вкладку из кода (выбор радиокнопки запустит обычный переход).</summary>
    private void SelectTab(int index)
    {
        if (Tabs.Children[index] is RadioButton tab) tab.IsChecked = true;
    }

    private static readonly string[] SheetNames = { "Обзор", "Сессии", "История", "Сеть", "Диагностика", "Очистка", "Оптимизация", "Система" };

    /// <summary>Штамп в подвале: номер листа, а в подробном виде детали — ещё и буква вида.</summary>
    private void UpdateSheetNo()
    {
        SheetNo.Text = Ink.Track(L.F("ЛИСТ {0}/{1}", page + 1, pages.Length), 2);
        bool view = page == PgOverview && detail;
        StampViewCell.Visibility = view ? Visibility.Visible : Visibility.Collapsed;
        if (view) StampView.Text = Ink.Track(L.F("ВИД {0}", ViewLetter(comp)), 2);
    }

    private void MoveTabLine(bool animate)
    {
        // На листе «Справка» вкладки не выбраны: черта под вкладками убирается.
        if (page >= Tabs.Children.Count) { TabLine.Width = 0; return; }
        if (Tabs.Children[page] is not RadioButton tab || tab.ActualWidth <= 0) return;
        double pad = tabIcons.Count > 0 ? 11 : 14;   // отступ текста вкладки (в узком окне вкладки плотнее)
        double x = tab.TranslatePoint(new Point(pad, 0), TabBar).X;
        TabLine.Width = Math.Max(0, tab.ActualWidth - 2 * pad);
        var shift = (TranslateTransform)TabLine.RenderTransform;
        if (animate) Motion.To(shift, TranslateTransform.XProperty, x, Motion.Normal);
        else Motion.Snap(shift, TranslateTransform.XProperty, x);
    }

    // ================================================================== линия занятости
    private void BeginBusy() { busy++; UpdateActivity(); }
    private void EndBusy()
    {
        busy = Math.Max(0, busy - 1);
        UpdateActivity();
        // Кнопки «Очистить», «Играть», «Только закрыть» выключены, пока идёт работа (busy > 0), а сами пересчитываются только
        // по своим событиям: закончилась работа в другом месте (замер, раздел «Системы») — включить их обратно здесь.
        if (busy == 0) { UpdateAppsButton(); UpdateCleanSelection(); }
    }

    private void UpdateActivity()
    {
        var shift = (TranslateTransform)ActivityBar.RenderTransform;
        double w = ActivityHost.ActualWidth;
        if (busy == 0 || w <= 0)
        {
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            ActivityBar.Opacity = 0;
            return;
        }
        if (!Motion.On)
        {
            // Без анимации: неподвижная линия на всю ширину, пока идёт работа.
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.X = 0; ActivityBar.Width = w; ActivityBar.Opacity = 0.6;
            return;
        }
        ActivityBar.Width = w * 0.25; ActivityBar.Opacity = 1;
        var run = new DoubleAnimation(-w * 0.25, w, TimeSpan.FromMilliseconds(1100)) { EasingFunction = Motion.InOut, RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(run, 30);
        shift.BeginAnimation(TranslateTransform.XProperty, run);
    }

    // ================================================================== обзор
    // Последние показания: снимаются по таймеру, а отрисовываются только когда окно видно.
    private double load = double.NaN, ghz = double.NaN, total, avail;
    private GpuSample gpu = new();
    private bool gpuOk, diskViewShown;

    private bool Hidden => !IsVisible || WindowState == WindowState.Minimized;

    /// <summary>Такт таймера (раз в 1,5 с): снять показания, обновить запись сессии и предупреждения, затем экран.</summary>
    private void OnTick()
    {
        tick++;
        (load, ghz) = metrics.ReadCpu();
        gpu = metrics.Gpu;
        gpuOk = (DateTime.Now - gpu.At).TotalSeconds < 10 && (!double.IsNaN(gpu.Temp) || !double.IsNaN(gpu.Load));     // у AMD/Intel температуры нет, но загрузка есть
        (total, avail) = Metrics.ReadRam();

        // Поиск запущенной игры - раз в 4,5 секунды в фоне: перебор процессов не должен задерживать интерфейс.
        if (tick % 3 == 1 && !detecting)
        {
            detecting = true;
            Task.Run(() => { try { detectedGame = GameDetector.Find(); } catch { } finally { detecting = false; } });
        }

        // Игра идёт: диски не опрашиваются, а на ноутбуке включается nvidia-smi.
        metrics.GameActive = recorder.IsRecording || detectedGame != null;

        bool wasRecording = recorder.IsRecording;
        string wasGame = recorder.CurrentGame;
        int wasPid = recorder.GamePid;
        // Запись идёт и тогда, когда часть показаний недоступна (NaN): раньше без загрузки процессора не писались
        // ни сессии, ни история — однажды так пропали 15 часов.
        {
            // Подробные счётчики (ядра, подкачка, диск) читаются только во время игры: вне её они не нужны.
            var extra = recorder.IsRecording || detectedGame != null ? metrics.ReadExtra() : new ExtraSample { MaxCore = double.NaN, PagesIn = double.NaN, DiskBusy = double.NaN };
            recorder.CpuBaseGhz = metrics.CpuBaseMhz / 1000.0;
            recorder.Update(detectedGame, new SessionSample
            {
                Cpu = load, Ghz = ghz, RamAvail = avail, MaxCore = extra.MaxCore, PagesIn = extra.PagesIn, DiskBusy = extra.DiskBusy,
            }, gpuOk ? gpu : null, net);
        }
        string nowGame = recorder.CurrentGame;
        int nowPid = recorder.GamePid;
        if (!wasRecording && recorder.IsRecording)
        {
            CheckMemoryAtGameStart();
            QueueHooks(() => GameHooks.RaiseStarted(nowGame, nowPid));
        }
        else if (wasRecording && recorder.IsRecording && (wasGame != nowGame || wasPid != nowPid))
        {
            // Одна игра сменила другую за один такт (или игру перезапустили): службам — «закончилась» для прежней
            // (с ожиданием её обработчиков), затем «началась» для новой, иначе они держали бы номер процесса прежней игры.
            CheckMemoryAtGameStart();
            QueueHooks(() => { GameHooks.RaiseEnded(wait: true); GameHooks.RaiseStarted(nowGame, nowPid); });
        }
        if (wasRecording && !recorder.IsRecording) QueueHooks(() => GameHooks.RaiseEnded());
        UpdateLive();
        if (recorder.IsRecording != wasRecording || page == PgSessions || recorder.IsRecording) UpdateSessionLive();
        // Игра закрыта: вернуть программы, которые закрывались перед ней.
        if (wasRecording && !recorder.IsRecording && settings.RestoreApps && closedApps.Count > 0) RestoreClosedApps();

        // Сеть измеряется только когда это нужно: открыта её страница или идёт запись сессии.
        net.Active = recorder.IsRecording || (page == PgNet && !Hidden);

        // История вне игр: поминутные точки копятся всегда, пока программа запущена (недоступные показания — пропуски в точке).
        history.Add(load, gpuOk ? gpu.Load : double.NaN, gpuOk ? gpu.Temp : double.NaN, total > 0 ? total - avail : double.NaN);
        if (page == PgHistory && tick % 20 == 0) UpdateHistoryPage();

        // Сторож: первая проверка через минуту после запуска, дальше - раз в час смотрим, не прошли ли сутки.
        if (tick % 2400 == 40 && !watching && Watchdog.IsDue(settings)) RunWatchdog();
        if (tick % 2400 == 80) CheckWeeklyNotice();
        if (tick % 2400 == 120) CheckWeeklyClean();

        alerts.Check(gpuOk ? gpu.Temp : double.NaN, avail);
        double liveFps = recorder.LiveFps;
        tray.SetTooltip(gpuOk
            ? L.F("Пульс ПК · видеокарта {0} · свободно {1:0.0} ГБ", double.IsNaN(gpu.Temp) ? gpu.Load.ToString("0") + L.Pick(" %", "%") : $"{gpu.Temp:0} °C", avail)
              + (recorder.IsRecording ? double.IsNaN(liveFps) ? L.T(" · запись сессии") : $" · {liveFps:0} FPS" : "")
            : L.F("Пульс ПК · свободно {0:0.0} ГБ", avail));

        // Графики копят показания всегда, а двигаются только когда их видно.
        bool onOverview = page == PgOverview && !Hidden;
        if (!double.IsNaN(load))
        {
            Chart.Push(load, gpuOk ? gpu.Load : double.NaN, onOverview && detail && comp == Comp.Cpu);
            SparkCpu.Push(load, double.NaN, onOverview);
        }
        SparkGpu.Push(gpuOk ? gpu.Temp : double.NaN, double.NaN, onOverview);

        if (tick % 2 == 1 && !Hidden) UpdateTopProcesses();
        if (tick % 10 == 1) UpdateDrives();
        // Диски опрашиваются раз в 30 минут; открыли чертёж дисков — свежие сведения по запросу (не чаще раза в 2 минуты).
        bool diskView = onOverview && detail && comp == Comp.Storage;
        if (diskView && !diskViewShown) metrics.RequestDiskRefresh();
        diskViewShown = diskView;
        UpdateOverview();
    }

    /// <summary>Показать последние показания на главной странице и в подвале.</summary>
    private void UpdateOverview()
    {
        if (Hidden) return;
        if (total <= 0) (total, avail) = Metrics.ReadRam();

        if (page == PgOverview) UpdatePanelValues();
        UpdateFooter();

        // Линия пульса в заголовке вздрагивает на каждом показании: видно, что прибор жив.
        if (Motion.On)
            LiveDot.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 1, TimeSpan.FromMilliseconds(700)) { EasingFunction = Motion.Out, FillBehavior = FillBehavior.Stop });

        if (!disksShown || tick % 20 == 0) UpdateDiskHealth();
    }

    // ================================================================== игровые сессии
    private void UpdateSessionLive()
    {
        if (recorder.IsRecording)
        {
            LivePanel.Visibility = Visibility.Visible;
            LiveGame.Text = recorder.CurrentGame;
            double fps = recorder.LiveFps;
            LiveTime.Text = L.F("записано {0}", Fmt.Duration(recorder.Recorded)) + (double.IsNaN(fps) ? "" : L.F(" · сейчас {0:0} FPS", fps));
            SessionsHint.Visibility = Visibility.Collapsed;
            var len = recorder.Recorded;     // записанное время: сон компьютера посреди игры не считается
            FooterRec.Text = L.F("●  запись · {0} · {1:00}:{2:00}:{3:00}", recorder.CurrentGame, (int)len.TotalHours, len.Minutes, len.Seconds);
            FooterRec.Visibility = Visibility.Visible;
            // Точка записи — единственный бесконечный цикл в программе; без фокуса окна стоит.
            if (Motion.On && !recBlinking)
            {
                recBlinking = true;
                RecDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(800))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Motion.InOut });
            }
            else if (!Motion.On && recBlinking) { recBlinking = false; RecDot.BeginAnimation(OpacityProperty, null); }
        }
        else
        {
            LivePanel.Visibility = Visibility.Collapsed;
            ApplySessionsSide();
            FooterRec.Visibility = Visibility.Collapsed;
            if (recBlinking) { recBlinking = false; RecDot.BeginAnimation(OpacityProperty, null); }
        }
    }

    /// <summary>
    /// Игра запустилась, а памяти мало: подсказка у часов, кто её занимает. Нажатие открывает «Оптимизацию»,
    /// где эти программы можно закрыть галочками. Сама программа ничего не закрывает.
    /// </summary>
    private void CheckMemoryAtGameStart()
    {
        if (!settings.AlertsEnabled || avail <= 0 || avail >= 2.0) return;
        string game = recorder.CurrentGame;
        int pid = recorder.GamePid;
        double free = avail;
        Task.Run(() =>
        {
            var top = ProcessMemory.Top(3, pid);
            OnUi(() =>
            {
                string who = top.Count > 0 ? L.F(" Больше всего занимают: {0}.", ProcessMemory.Describe(top)) : "";
                tray.Notify(L.F("Мало памяти для {0}", game), L.F("Свободно {0:0.0} ГБ.{1} Нажмите, чтобы выбрать, что закрыть.", free, who), true,
                    () => { ShowFromTray(); SelectTab(PgTweaks); });
            });
        });
    }

    private void OnSessionFinished(GameSession session)
    {
        sessions.Insert(0, session);
        SessionList.SelectedItem = session;
        UpdateSessionLive();
        // Новая игра или новые данные о том, что её ограничивает: пересчитать рекомендации настроек.
        LoadTunes(notify: true);
        if (session.SaveError != null)
            // Отчёт виден до выхода из программы, но на диск не лёг — говорим об этом сразу, а не «Сессия записана».
            tray.Notify(L.T("Сессия не сохранена"), L.F("{0}: отчёт есть на вкладке «Сессии», но не сохранился на диск ({1}) и пропадёт после перезапуска.",
                session.Game, session.SaveError), true, () => { ShowFromTray(); SelectTab(PgSessions); });
        else if (settings.AlertsEnabled)
            tray.Notify(L.T("Сессия записана"), L.F("{0}, {1}. Отчёт — на вкладке «Сессии».", session.Game, Fmt.Duration(session.Duration)));
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowSession(SessionList.SelectedItem as GameSession);

    /// <summary>Списку сессий — не меньше ~150 точек по высоте: всё, что выше него в колонке, при нехватке места прокручивается.</summary>
    /// <summary>Невысокое окно: верх колонки «Сессий» без рисунка и подсказки, чтобы список сессий занимал основное место.</summary>
    private bool sessionsCompact;

    private void SessionsBody_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Height < 600;
        if (compact == sessionsCompact && e.PreviousSize.Height > 0) return;
        sessionsCompact = compact;
        ApplySessionsSide();
        RenderCustomGames();
    }

    /// <summary>Рисунок геймпада и подсказка «оставьте программу запущенной»: в невысоком окне — только пока сессий нет.</summary>
    private void ApplySessionsSide()
    {
        bool roomy = !sessionsCompact || sessions.Count == 0;
        PadDrawing.Visibility = roomy ? Visibility.Visible : Visibility.Collapsed;
        SessionsHint.Visibility = roomy && !recorder.IsRecording ? Visibility.Visible : Visibility.Collapsed;
        SessionsSideLine.Margin = new Thickness(0, roomy ? 16 : 10, 0, 0);
    }

    private void ShowSession(GameSession s)
    {
        if (s == null)
        {
            SessionEmpty.Visibility = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SessionReport.Visibility = Visibility.Collapsed;
            return;
        }
        if (!ReferenceEquals(SessionList.SelectedItem, s)) { SessionList.SelectedItem = s; return; }   // выбор вызовет этот метод ещё раз

        SessionEmpty.Visibility = Visibility.Collapsed;
        WeekReport.Visibility = Visibility.Collapsed;
        SessionReport.Visibility = Visibility.Visible;
        SessGame.Text = s.Game;
        SessWhen.Text = $"{s.Start.ToString(L.Pick("d MMMM, HH:mm", "MMMM d, HH:mm"))} – {s.End:HH:mm} · {Fmt.Duration(s.Duration)}";
        ShowBehaviorMark(s);     // «найдена по поведению» и «Это не игра» (MainWindow.Games.cs)
        ShowCautiousMark(s);     // осторожный режим: античит и «Записывать полностью» (MainWindow.Cautious.cs)

        // Та же игра в прошлые разы: с предыдущей сессией сравниваются числа, остальные идут в таблицу внизу.
        var same = sessions.Where(x => x.Game == s.Game).OrderByDescending(x => x.Start).ToList();
        int at = same.IndexOf(s);
        var prev = at >= 0 && at + 1 < same.Count ? same[at + 1] : null;

        // Новый отчёт открывается целиком; масштаб общий для графиков кадров, нагрузки и температуры.
        if (SessChart.Zoom is ChartZoom sessZoom)
        {
            sessZoom.Reset(false);
            sessZoom.Full = s.Duration > TimeSpan.Zero ? s.Duration : TimeSpan.FromMinutes(1);
        }

        bool hasFps = s.AvgFps > 0;
        SessFpsBlock.Visibility = hasFps ? Visibility.Visible : Visibility.Collapsed;
        SessFpsChartBlock.Visibility = hasFps && s.FpsSeries?.Length >= 2 ? Visibility.Visible : Visibility.Collapsed;
        SessNoFps.Visibility = hasFps ? Visibility.Collapsed : Visibility.Visible;
        if (hasFps)
        {
            SessFps.Value = s.AvgFps ?? double.NaN;
            SessLow1.Value = s.Low1Fps ?? double.NaN;
            SessLow1.Foreground = s.Low1Fps < 0.5 * s.AvgFps ? Palette.Warn : Palette.Ink1;
            SessLow01.Value = s.Low01Fps ?? double.NaN;
            SessFpsCap.Text = L.T("в среднем") + Delta(s.AvgFps, prev?.AvgFps, 1, "0");
            SessLow1Cap.Text = L.T("в худшем 1 % кадров") + Delta(s.Low1Fps, prev?.Low1Fps, 1, "0");
            SessFpsChart.SetData(s.FpsSeries);
            // Плавность — у сессий с версии 1.5 (по времени кадров).
            SessSmooth.Visibility = SessSmoothCap.Visibility = s.Smoothness != null ? Visibility.Visible : Visibility.Collapsed;
            SessSmooth.Value = s.Smoothness ?? double.NaN;
            SessSmooth.Foreground = s.Smoothness < 60 ? Palette.Warn : Palette.Ink1;
            SessSmoothCap.Text = L.T("плавность") + Delta(s.Smoothness, prev?.Smoothness, 1, "0");
        }
        else
        {
            SessNoFps.Text = CautiousMode.ForSession(s)?.Cautious == true
                ? L.T("FPS не записывался: в этой игре работал осторожный режим — подробности под названием игры.")
                : L.T("FPS в этой сессии не записывался.");
        }

        SessMaxTemp.Value = s.MaxTemp;
        SessMaxTemp.Foreground = s.MaxTemp >= SessionAnalysis.HotMaxTempC(LaptopCheck.Applies) ? Palette.Warn : Palette.Ink1;
        SessAvgGpu.Value = s.AvgGpu;
        SessAvgCpu.Value = s.AvgCpu;
        SessMinRam.Value = s.MinRamGb;
        SessMinRam.Foreground = s.MinRamGb < 1.0 ? Palette.Warn : Palette.Ink1;
        SessMaxVram.Value = s.MaxVramGb;
        SessMaxTempCap.Text = L.T("макс. температура видеокарты") + Delta(s.MaxTemp, prev?.MaxTemp, 1, "0", "°");
        SessAvgGpuCap.Text = L.T("видеокарта в среднем") + Delta(s.AvgGpu, prev?.AvgGpu, 2, "0", L.Pick(" %", "%"));
        SessAvgCpuCap.Text = L.T("процессор в среднем") + Delta(s.AvgCpu, prev?.AvgCpu, 2, "0", L.Pick(" %", "%"));
        SessMinRamCap.Text = L.T("минимум свободной памяти") + Delta(s.MinRamGb, prev?.MinRamGb, 0.2, "0.0", " " + Fmt.GbUnit);

        SessCompareBlock.Visibility = same.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        // Вторая сессия для сравнения рядом: по умолчанию — предыдущая (или следующая, если эта — самая старая).
        // Своя для каждой открытой сессии: выбор строки в таблице действует, пока открыт этот отчёт.
        if (!ReferenceEquals(partnerFor, s) || comparePartner == null || ReferenceEquals(comparePartner, s) || !sessions.Contains(comparePartner))
            comparePartner = prev ?? (at > 0 ? same[at - 1] : null);
        partnerFor = s;
        FillCompareRows(s, same);

        bool hasNet = s.AvgPingMs != null;
        SessNetRow.Visibility = hasNet ? Visibility.Visible : Visibility.Collapsed;
        if (hasNet)
        {
            SessPing.Value = s.AvgPingMs ?? double.NaN;
            SessJitter.Value = s.JitterMs ?? double.NaN;
            SessLoss.Value = s.LossPct ?? double.NaN;
            SessLoss.Foreground = s.LossPct >= 1 ? Palette.Warn : Palette.Ink1;
        }

        SessChart.SetData(s.CpuSeries, s.GpuSeries);
        SessTempChart.SetData(s.TempSeries);
        SessNotes.ItemsSource = s.Notes?.Select(L.T).ToList();     // старые сессии хранят выводы по-русски
        ShowSessionAnalysis(s);
        SessionScroll.ScrollToTop();
        if (page == PgSessions) Motion.Reveal(SessionReport, 0, 8);
    }

    /// <summary>Вторая строка подписи: насколько показатель отличается от прошлой сессии той же игры.</summary>
    private static string Delta(double? now, double? before, double min, string format, string unit = "")
    {
        if (now is not > 0 || before is not > 0) return "";
        double d = now.Value - before.Value;
        if (Math.Abs(d) < min) return "";
        return "\n" + L.F("{0}{1}{2} к прошлой сессии", d > 0 ? "+" : "−", Math.Abs(d).ToString(format), unit);
    }

    // ================================================================== история
    private void HistRange_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out int minutes)) histMinutes = minutes;
        if (HdrHistory != null)
            HdrHistory.Line2 = histMinutes switch { 60 => L.T("ЗА ЧАС"), 360 => L.T("ЗА 6 ЧАСОВ"), 1440 => L.T("ЗА СУТКИ"), _ => L.T("ЗА ДВОЕ СУТОК") };
        // Масштаб работает внутри выбранного диапазона: новый диапазон открывается целиком.
        HistLoadChart?.Zoom?.Reset(false);
        if (pages != null) UpdateHistoryPage(reveal: true);
    }

    private DateTime histFrom;
    private bool histZoomHooked;

    /// <summary>Подписи общей оси времени под тремя графиками: начало, середина и конец видимого окна.</summary>
    private void UpdateHistAxis()
    {
        var z = HistLoadChart.Zoom;
        if (z == null || histFrom == default) return;
        static string At(DateTime t) => t.ToString(t.Date == DateTime.Today ? "HH:mm" : L.Pick("d MMM, HH:mm", "MMM d, HH:mm"));
        double minutes = histMinutes;
        DateTime from = histFrom.AddMinutes(minutes * z.Start), to = histFrom.AddMinutes(minutes * (z.Start + z.Span));
        HistFrom.Text = At(from);
        HistMid.Text = At(from.AddMinutes(minutes * z.Span / 2));
        HistTo.Text = z.Start + z.Span >= 0.999 ? L.T("сейчас") : At(to);
    }

    private void OpenHistory()
    {
        // При первом открытии: если записей пока меньше часа, шесть часов показывать незачем - линия вышла бы с ноготь.
        if (!histOpened)
        {
            histOpened = true;
            if (history.Window(360).Cpu.Count(v => !double.IsNaN(v)) < 45) { RangeHour.IsChecked = true; return; }   // переключение само обновит страницу
        }
        UpdateHistoryPage(reveal: true);
    }

    /// <summary>Обновить «Историю». reveal — линии открываются заново (смена диапазона, вход на страницу), иначе просто подменяются.</summary>
    private void UpdateHistoryPage(bool reveal = false)
    {
        if (page != PgHistory || Hidden) return;
        if (!histZoomHooked && HistLoadChart.Zoom != null) { histZoomHooked = true; HistLoadChart.Zoom.Changed += _ => UpdateHistAxis(); }

        var w = history.Window(histMinutes);
        var cpuOk = w.Cpu.Where(v => !double.IsNaN(v)).ToList();
        bool any = cpuOk.Count > 0;
        HistEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        HistBody.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (!any) return;

        var gpuOkList = w.Gpu.Where(v => !double.IsNaN(v)).ToList();
        var tempOk = w.Temp.Where(v => !double.IsNaN(v)).ToList();
        var ramOk = w.Ram.Where(v => !double.IsNaN(v)).ToList();

        double maxTemp = tempOk.Count > 0 ? tempOk.Max() : double.NaN;
        HistMaxTemp.Value = maxTemp;
        HistMaxTemp.Foreground = maxTemp >= 83 ? Palette.Warn : Palette.Ink1;
        HistAvgCpu.Value = cpuOk.Average();
        HistAvgGpu.Value = gpuOkList.Count > 0 ? gpuOkList.Average() : double.NaN;
        HistMaxRam.Value = ramOk.Count > 0 ? ramOk.Max() : double.NaN;
        HistMaxRamCap.Text = total > 0 ? L.F("пик занятой памяти из {0:0.0} ГБ", total) : L.T("пик занятой памяти");

        double ramTop = Math.Max(1, Math.Round(total));
        HistRamLabel.Source = L.F("ЗАНЯТАЯ ПАМЯТЬ, 0–{0:0} ГБ", ramTop);
        HistRamChart.Max = ramTop;
        if (HistLoadChart.Zoom != null) HistLoadChart.Zoom.Full = TimeSpan.FromMinutes(histMinutes);
        HistTempChart.SetData(w.Temp, null, reveal);
        HistLoadChart.SetData(w.Cpu, w.Gpu, reveal);
        HistRamChart.SetData(w.Ram, null, reveal);

        histFrom = w.From;
        UpdateHistAxis();
    }

    // ================================================================== сторож
    private void RunWatchdog()
    {
        watching = true;
        bool gaming = recorder.IsRecording;
        Task.Run(() =>
        {
            List<string> messages = null;
            try { messages = Watchdog.Run(settings, gaming); } catch { }
            OnUi(() =>
            {
                watching = false;
                UpdateWatchText();
                UpdateRevertedBar();
                if (settings.RevertedTweaks.Count == 0) _ = RefreshTweaksAsync();     // автовозврат мог изменить состояние переключателей
                if (messages is { Count: > 0 } && settings.AlertsEnabled)
                {
                    string text = string.Join(" ", messages);
                    tray.Notify(L.T("Сторож: с прошлой проверки есть изменения"), text, true,
                        settings.RevertedTweaks.Count > 0 ? () => { ShowFromTray(); SelectTab(PgTweaks); } : null);
                }
            });
        });
    }

    private void UpdateWatchText()
    {
        bool quiet = string.IsNullOrEmpty(settings.LastWatchSummary) || settings.LastWatchSummary == "изменений нет";
        WatchLast.Text = settings.LastWatch == DateTime.MinValue
            ? L.T("Первая проверка — через минуту после запуска.")
            : L.F("Последняя проверка {0}: {1}", settings.LastWatch.ToString(L.Pick("d MMMM, HH:mm", "MMMM d, HH:mm")), quiet ? L.T("изменений нет.") : L.T(settings.LastWatchSummary));
        WatchLast.Foreground = quiet ? Palette.Ink3 : Palette.Warn;
    }

    // ================================================================== сеть
    private bool netChecksAttached;

    /// <summary>Проверки сети по кнопке (Panels\NetDiagPanel): подключаются при первом открытии вкладки, Wi‑Fi перечитывается каждый раз.</summary>
    private void ShowNetChecks()
    {
        if (panelHost == null) return;
        if (!netChecksAttached) { NetChecks.Attach(panelHost); netChecksAttached = true; }
        NetChecks.Shown();
    }

    private void UpdateNetPage()
    {
        if (page != PgNet || Hidden) return;

        var i = net.Internet.Stats();
        var r = net.Router.Stats();
        NetInetPing.Value = i.Last; NetInetAvg.Value = i.Avg; NetInetJitter.Value = i.Jitter; NetInetLoss.Value = i.LossPct;
        NetRouterPing.Value = r.Last; NetRouterAvg.Value = r.Avg; NetRouterJitter.Value = r.Jitter; NetRouterLoss.Value = r.LossPct;
        NetInetLoss.Foreground = i.LossPct >= 1 ? Palette.Bad : Palette.Ink1;
        NetRouterLoss.Foreground = r.LossPct >= 1 ? Palette.Bad : Palette.Ink1;
        NetInetChart.SetLive(net.Internet.Snapshot());
        NetRouterChart.SetLive(net.Router.Snapshot());
        NetRouterLabel.Source = net.Router.Host != null ? L.T("РОУТЕР") + " · " + net.Router.Host : L.T("РОУТЕР");
        NetAdapter.Text = net.AdapterText;
        RouteRouterName.Text = net.Router.Host != null ? L.F("роутер {0}", net.Router.Host) : L.T("роутер");
        static string Ms(double v) => double.IsNaN(v) ? "" : (v < 10 ? v.ToString("0.0") : v.ToString("0")) + "\u202F" + L.Pick("мс", "ms");
        RouteRouter.Text = r.Count >= 3 ? (double.IsNaN(r.Avg) ? L.T("не отвечает") : Ms(r.Avg)) : "";
        RouteInet.Text = i.Count >= 3 ? (double.IsNaN(i.Avg) ? L.T("нет ответа") : Ms(i.Avg)) : "";

        if (i.Count < 5) return;   // для вывода нужно хотя бы десять секунд замеров

        // Кто виноват: если плохо уже до роутера - дело в домашней сети; если до роутера чисто, а дальше плохо - у провайдера.
        // Некоторые роутеры вообще не отвечают на пинг - это не неисправность, по ним вывод не делаем.
        bool routerSilent = r.Count >= 5 && double.IsNaN(r.Avg);
        bool routerBad = r.Count >= 5 && !routerSilent && (r.LossPct >= 2 || r.Avg >= 15 || r.Jitter >= 8);
        bool inetBad = i.LossPct >= 2 || i.Jitter >= 12 || double.IsNaN(i.Avg);
        string verdict, detail;
        if (double.IsNaN(i.Avg))
        {
            verdict = routerSilent || r.Count == 0 ? L.T("Сети нет") : L.T("Нет выхода в интернет");
            detail = routerSilent || r.Count == 0
                ? L.T("Не отвечают ни роутер, ни интернет. Проверьте кабель и питание роутера.")
                : L.T("Роутер отвечает, а интернет — нет. Скорее всего, проблема у провайдера.");
        }
        else if (routerBad)
        {
            verdict = L.T("Проблема в домашней сети");
            detail = L.F("Скачки пинга и потери начинаются уже на пути до роутера (потери {0:0.0} %, разброс {1:0.0} мс). Проверьте кабель, порт роутера и не перегружен ли он.", r.LossPct, r.Jitter);
        }
        else if (inetBad)
        {
            verdict = L.T("Проблема за пределами дома");
            detail = L.F("До роутера всё чисто, а до интернета — потери {0:0.0} % и разброс {1:0.0} мс. Проблема у провайдера или на линии до него.", i.LossPct, i.Jitter);
        }
        else
        {
            verdict = L.T("Сеть в порядке");
            detail = L.F("Пинг до интернета в среднем {0:0} мс, разброс {1:0.0} мс, потерь нет. Если игра всё равно лагает, дело не в вашем подключении.", i.Avg, i.Jitter)
                     + (routerSilent ? L.T(" Роутер на пинг не отвечает — у некоторых моделей это нормально.") : "");
        }
        // Виновный участок трассы выделяется: охра — разброс, кармин — потери.
        RouteRouter.Level = routerBad ? (r.LossPct >= 2 ? Severity.Bad : Severity.Warn) : Severity.Ok;
        RouteInet.Level = !routerBad && inetBad ? (i.LossPct >= 2 || double.IsNaN(i.Avg) ? Severity.Bad : Severity.Warn) : Severity.Ok;
        Motion.SetText(NetVerdict, verdict);
        NetVerdict.Foreground = routerBad || inetBad ? Palette.Warn : Palette.Ink1;
        if (NetDetail.Text != detail) NetDetail.Text = detail;
    }

    // ================================================================== автозагрузка
    private void LoadStartup()
    {
        startup.Clear();
        foreach (var item in StartupManager.Load())
        {
            item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(StartupItem.Enabled)) OnStartupToggled(item); };
            startup.Add(item);
        }
        startupLoaded = true;
        UpdateStartupCount();
        if (!admin) StartupNote.Text = L.T("Менять автозагрузку можно только с правами администратора — пока список только для просмотра.");
    }

    private void UpdateStartupCount()
    {
        StartupCount.Unit = $"/ {startup.Count}";
        StartupCount.Value = startup.Count(s => s.Enabled);
    }

    /// <summary>Переключатель действует сразу; если записать не удалось, он возвращается обратно.</summary>
    private void OnStartupToggled(StartupItem item)
    {
        try
        {
            item.Write?.Invoke(item.Enabled);
            Motion.SetText(StartupNote, item.Enabled
                ? L.F("«{0}» снова запускается вместе с Windows.", item.Title)
                : L.F("«{0}» больше не запускается вместе с Windows. Саму программу это не удаляет.", item.Title));
        }
        catch (Exception ex)
        {
            item.SetSilently(!item.Enabled);
            Motion.SetText(StartupNote, L.F("Не удалось изменить «{0}»: {1}", item.Title, ex.Message));
        }
        UpdateStartupCount();
    }

    private static string ShortCpu(string name)
    {
        string s = HardwareInfo.ShortCpu(name);
        return s == "" ? L.T("процессор") : s;
    }

    /// <summary>Подставной список «Что занимает память» — только для служебных снимков окна (Panels.WindowShots).</summary>
    internal static List<(string Name, int Count, long Bytes)> ShotProcs;

    private void UpdateTopProcesses()
    {
        try
        {
            var top = ShotProcs ?? Process.GetProcesses()
                .GroupBy(p => p.ProcessName)
                .Select(g => { long sum = 0; foreach (var p in g) { try { sum += p.WorkingSet64; } catch { } } return (Name: g.Key, Count: g.Count(), Bytes: sum); })
                .OrderByDescending(x => x.Bytes).Take(procs.Count).ToList();

            // Строки не пересоздаются, а обновляются на месте: список не мигает.
            for (int i = 0; i < procs.Count; i++)
            {
                if (i < top.Count) { procs[i].Name = top[i].Name; procs[i].Count = top[i].Count > 1 ? "×" + top[i].Count : ""; procs[i].Mem = Fmt.Size(top[i].Bytes); }
                else { procs[i].Name = ""; procs[i].Count = ""; procs[i].Mem = ""; }
            }
        }
        catch { }
    }

    private void UpdateDrives()
    {
        try
        {
            foreach (var d in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady))
            {
                string letter = d.Name.TrimEnd('\\');
                var vm = drives.FirstOrDefault(x => x.Letter == letter);
                if (vm == null) { vm = new DriveVm { Letter = letter }; drives.Add(vm); }
                double freePct = 100.0 * d.AvailableFreeSpace / d.TotalSize;
                vm.FreeText = L.F("{0} свободно", Fmt.Size(d.AvailableFreeSpace));
                vm.Fraction = 1 - freePct / 100.0;
                vm.BarBrush = freePct < 10 ? Palette.Bad : freePct < 15 ? Palette.Warn : Palette.Ink2;
            }
        }
        catch { }
    }

    private void UpdateDiskHealth()
    {
        if (!metrics.DisksLoaded) return;
        disksShown = true;
        var disks = metrics.Disks;
        if (disks.Count == 0)
        {
            DiskHealth.Text = L.T("Температуру и состояние дисков программа видит только с правами администратора.");
            DiskWarn.Visibility = Visibility.Collapsed;
            return;
        }

        static string Short(string n) => n.Replace("WDC ", "").Replace("Samsung SSD ", "").Split('-')[0].Trim();
        int healthy = disks.Count(d => d.Health == "Healthy");
        string head = healthy == disks.Count
            ? Fmt.Plural(disks.Count, "диск исправен", "диска исправны", "дисков исправны")
            : L.F("исправны {0} из {1} дисков", healthy, disks.Count);
        var temps = disks.Where(d => d.Temp != null).Select(d => $"{Short(d.Name)} {d.Temp:0} °C").ToList();
        DiskHealth.Text = temps.Count > 0 ? head + " · " + string.Join(" · ", temps) : head;

        var problems = disks.Where(d => d.Health != "Healthy" || d.ReadErrors > 0)
            .Select(d => d.Health != "Healthy"
                ? L.F("{0}: {1} — скопируйте важные данные", Short(d.Name), d.Health == "Unhealthy" ? L.T("Windows считает диск неисправным") : d.Health == "Warning" ? L.T("Windows предупреждает о проблемах с диском") : L.F("состояние «{0}»", d.Health))
                : L.F("{0}: ошибок чтения — {1:0}, наработка {2:N0} ч — не храните на нём важное", Short(d.Name), d.ReadErrors, d.Hours)).ToList();
        DiskWarn.Text = string.Join("\n", problems);
        DiskWarn.Visibility = problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================================================================== диагностика
    private async void BtnDiag_Click(object sender, RoutedEventArgs e) => await RunDiagAsync();

    private bool diagRunning;

    private async Task RunDiagAsync()
    {
        if (diagRunning) return;
        diagRunning = true;
        metrics.RequestDiskRefresh();       // сведения о дисках обновляются раз в 30 минут — к проверке освежить
        BtnDiag.IsEnabled = false;
        BtnDiag.Content = L.T("Проверяю…");
        BtnDiagSeen.IsEnabled = BtnDiagUnseen.IsEnabled = false;
        BeginBusy();
        try
        {
            await RefreshTweaksAsync();
            var snapshot = tweaks.ToList();
            var items = await Task.Run(() => Diagnostics.Run(metrics, snapshot, settings));
            RecordHealth(items);        // оценка здоровья на «Обзоре» — по тем же находкам (MainWindow.Health.cs)

            diagAttention.Clear(); diagInfo.Clear(); diagOk.Clear();
            foreach (var item in items)
            {
                if (item.Level <= Severity.Warn) diagAttention.Add(item);
                else if (item.Level == Severity.Info) diagInfo.Add(item);
                else diagOk.Add(item);
            }
            DiagHdrAttention.Visibility = diagAttention.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            DiagHdrInfo.Visibility = diagInfo.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            DiagHdrInfo.Margin = new Thickness(0, diagAttention.Count > 0 ? 26 : 0, 0, 2);
            DiagHdrOk.Visibility = diagOk.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            DiagPlaceholder.Visibility = Visibility.Collapsed;
            DiagScroll.ScrollToTop();

            int bad = items.Count(i => i.Level == Severity.Bad), warn = items.Count(i => i.Level == Severity.Warn);
            Motion.SetText(DiagVerdict, bad > 0 ? L.T("Есть серьёзные проблемы") : warn > 0 ? L.T("Серьёзных проблем нет, но есть замечания") : L.T("Всё в порядке"));
            DiagVerdict.Foreground = bad > 0 ? Palette.Bad : Palette.Ink1;
            bool marked = LogMarks.Any(settings);
            Motion.SetText(DiagNote, L.F("Проверены журналы Windows за {0} дней, диски, память, видеокарта и настройки.", Diagnostics.Days)
                                     + (marked ? L.T(" Просмотренные записи не учитываются — только новые.") : ""));
            UpdateDiagSeen(items);
            DiagTally.Visibility = Visibility.Visible;
            TallyAttention.Value = bad + warn;
            TallyAttention.Foreground = bad > 0 ? Palette.Bad : warn > 0 ? Palette.Warn : Palette.Ink1;
            TallyInfo.Value = diagInfo.Count;
            TallyOk.Value = diagOk.Count;
            DiagTime.Text = L.F("проверено в {0:HH:mm}", DateTime.Now);

            // Находки появляются по одной: читается как отдельные результаты, а не как стена текста.
            _ = Dispatcher.BeginInvoke(new Action(() => RevealRows(DiagAttention, DiagInfo, DiagOk)), DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            Motion.SetText(DiagNote, L.F("Проверка не удалась: {0}", ex.Message));
        }
        finally
        {
            EndBusy();
            BtnDiag.Content = L.T("Проверить снова");
            BtnDiag.IsEnabled = true;
            BtnDiagSeen.IsEnabled = BtnDiagUnseen.IsEnabled = true;
            diagRunning = false;
        }
    }

    /// <summary>Блок «Отметить как просмотренные»: есть что скрыть или есть отметки, которые можно снять.</summary>
    private void UpdateDiagSeen(IEnumerable<DiagItem> items)
    {
        bool canHide = items.OfType<DiagFinding>().Any(f => f.CanHide);
        var marks = LogMarks.Kinds.Keys.Select(k => LogMarks.Seen(k, settings)).Where(t => t != null).Select(t => t.Value).ToList();
        BtnDiagSeen.Visibility = canHide ? Visibility.Visible : Visibility.Collapsed;
        DiagUnseenRow.Visibility = marks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DiagSeenWhen.Text = marks.Count == 0 ? ""
            : L.F("Последняя отметка — {0}.", marks.Max().ToString(L.Pick("d MMMM, HH:mm", "MMMM d, HH:mm"), L.Culture));
        DiagSeenBlock.Visibility = canHide || marks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Отметить всё, что сейчас в списке из журналов, как просмотренное и проверить заново.</summary>
    private async void BtnDiagSeen_Click(object sender, RoutedEventArgs e)
    {
        var kinds = diagAttention.Concat(diagInfo).OfType<DiagFinding>().Where(f => f.CanHide).Select(f => f.Kind).Distinct().ToList();
        if (kinds.Count == 0) return;
        LogMarks.MarkSeen(kinds, settings);
        await RunDiagAsync();
    }

    /// <summary>«скрыть» у одной строки: отметить только её вид.</summary>
    private async void DiagHide_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DiagFinding f || !f.CanHide) return;
        LogMarks.MarkSeen(new[] { f.Kind }, settings);
        await RunDiagAsync();
    }

    private async void BtnDiagUnseen_Click(object sender, RoutedEventArgs e)
    {
        LogMarks.Reset(settings);
        await RunDiagAsync();
    }

    /// <summary>«Подробнее и что делать»: разбор записей журнала (только чтение).</summary>
    private void DiagDetails_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DiagFinding f || !f.HasDetails) return;
        new LogDetailsWindow(this, f.DetailsKind, f.Title, f.Level, metrics.Disks, settings).Show();
    }

    private void DiagLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DiagFinding f && f.HasLink) OpenTarget(f.Link);
    }

    /// <summary>Ссылка с адресом в Tag («tab:…», «sys:…», «clean:…»).</summary>
    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string target) OpenTarget(target);
    }

    private static void RevealRows(params ItemsControl[] lists)
    {
        int index = 0;
        foreach (var list in lists)
            foreach (var item in list.Items)
                if (list.ItemContainerGenerator.ContainerFromItem(item) is UIElement row)
                    Motion.Reveal(row, index++ * Motion.Stagger);
    }

    // ================================================================== очистка
    private void UpdateCleanSelection()
    {
        if (!scanned)
        {
            CleanHero.Value = double.NaN;
            CleanBar.SetSegments(Array.Empty<(double, bool)>());
            return;
        }

        var known = cleanAll.Where(i => !i.SizeUnknown && i.Bytes > 0).ToList();
        long sum = known.Where(i => i.Selected).Sum(i => i.Bytes);
        bool unknownSelected = cleanAll.Any(i => i.SizeUnknown && i.Selected);

        CleanLabel.Source = L.T("ВЫБРАНО К УДАЛЕНИЮ");
        CleanHero.Value = sum / Fmt.Gib;
        var ordered = known.OrderByDescending(i => i.Bytes).ToList();
        CleanBar.SetSegments(ordered.Select(i => ((double)i.Bytes, i.Selected)));
        // Легенда: у каждой строки образец той же штриховки, что у её участка полосы.
        for (int i = 0; i < ordered.Count; i++) ordered[i].Swatch = SegmentBar.Pattern(i, ordered[i].Selected);
        BtnClean.Content = sum > 0 ? L.F("Очистить {0}", Fmt.Size(sum)) : L.T("Очистить");
        BtnClean.IsEnabled = admin && busy == 0 && (sum > 0 || unknownSelected);
    }

    /// <summary>После подсчёта: непустые категории по убыванию размера, пустые — отдельной группой внизу.</summary>
    private void ArrangeCleanLists()
    {
        cleanShown.Clear(); cleanEmpty.Clear();
        foreach (var item in cleanAll.Where(i => !i.IsEmpty).OrderBy(i => i.SizeUnknown ? 1 : 0).ThenByDescending(i => i.Bytes))
            cleanShown.Add(item);
        foreach (var item in cleanAll.Where(i => i.IsEmpty)) { item.Selected = false; cleanEmpty.Add(item); }
        CleanEmptyHdr.Text = L.F("ПУСТО ({0})", cleanEmpty.Count);
        CleanEmptyHdr.Visibility = cleanEmpty.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ScanAllAsync()
    {
        foreach (var item in cleanAll) item.Pending = true;
        var items = cleanAll.ToList();
        // Сам список служит индикатором хода: строки «проявляются» по мере того, как приходят размеры.
        await Task.Run(() =>
        {
            foreach (var item in items)
            {
                long size; try { size = item.Scan(); } catch { size = 0; }
                item.Bytes = size;
                item.Pending = false;
            }
        });
    }

    private async void BtnScan_Click(object sender, RoutedEventArgs e)
    {
        BtnScan.IsEnabled = false; BtnClean.IsEnabled = false;
        BeginBusy();
        try
        {
            // На время подсчёта возвращаем все категории в один список.
            cleanShown.Clear(); cleanEmpty.Clear();
            foreach (var item in cleanAll) cleanShown.Add(item);
            CleanEmptyHdr.Visibility = Visibility.Collapsed;
            CleanScroll.ScrollToTop();
            Motion.SetText(CleanSummary, L.T("Считаю, сколько занимает каждая категория. Это может занять до минуты."));

            scanned = true;
            await ScanAllAsync();
            ArrangeCleanLists();

            long total = cleanAll.Where(i => !i.SizeUnknown).Sum(i => Math.Max(0, i.Bytes));
            Motion.SetText(CleanSummary, !admin
                ? L.F("Всего найдено {0}. Удаление доступно только с правами администратора.", Fmt.Size(total))
                : total > 0 ? L.F("Всего найдено {0}. Отметьте, что удалить; удаление необратимо.", Fmt.Size(total))
                : L.T("Мусора не найдено."));
            BtnScan.Style = (Style)FindResource("Outline");
            BtnScan.Content = L.T("Найти заново");
            BtnClean.Visibility = Visibility.Visible;
        }
        finally
        {
            EndBusy();
            BtnScan.IsEnabled = true;
            UpdateCleanSelection();
        }
    }

    private async void BtnClean_Click(object sender, RoutedEventArgs e)
    {
        var selected = cleanAll.Where(i => i.Selected && (i.SizeUnknown || i.Bytes > 0)).ToList();
        if (selected.Count == 0) return;

        long known = selected.Where(i => !i.SizeUnknown).Sum(i => i.Bytes);
        string list = string.Join("\n", selected.Select(i => "  • " + i.TitleText + (i.SizeUnknown ? "" : " — " + Fmt.Size(i.Bytes))));
        var answer = MessageBox.Show(this,
            L.F("Будет удалено:\n\n{0}\n\nВсего около {1}. Удаление необратимо.\n\nПродолжить?", list, Fmt.Size(known)),
            L.T("Очистка"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        BtnScan.IsEnabled = false; BtnClean.IsEnabled = false;
        BeginBusy();
        long before = Cleaner.SystemDriveFree();
        try
        {
            foreach (var item in selected)
            {
                Motion.SetText(CleanSummary, L.F("Очищаю: {0}…", item.TitleText));
                item.Pending = true;
                await Task.Run(() => { try { item.Clean(); } catch { } });
            }
            await ScanAllAsync();
            ArrangeCleanLists();
            long gained = Math.Max(0, Cleaner.SystemDriveFree() - before);
            Motion.SetText(CleanSummary, L.F("Готово. На системном диске прибавилось {0}.", Fmt.Size(gained)));
            UpdateDrives();
        }
        finally
        {
            EndBusy();
            BtnScan.IsEnabled = true;
            UpdateCleanSelection();
        }
    }

    // ================================================================== оптимизация
    private async Task RefreshTweaksAsync()
    {
        var items = tweaks.ToList();
        var states = await Task.Run(() => items.Select(t => { try { return (bool?)t.Check(); } catch { return null; } }).ToList());

        syncingTweaks = true;
        for (int i = 0; i < items.Count; i++) { items[i].Applied = states[i]; items[i].Desired = states[i] ?? false; }
        syncingTweaks = false;

        int applied = tweaks.Count(t => t.Applied == true);
        TweakCount.Unit = $"/ {tweaks.Count}";
        TweakCount.Value = applied;
        TweakCells.Target = applied;
        if (string.IsNullOrEmpty(TweakNote.Text))
            TweakNote.Text = !admin ? L.T("Менять настройки можно только с правами администратора.")
                : applied == tweaks.Count ? L.T("Все настройки применены.") : L.F("Осталось применить: {0}.", Fmt.Plural(tweaks.Count - applied, "настройку", "настройки", "настроек"));
        UpdatePending();
    }

    private void UpdatePending()
    {
        if (syncingTweaks) return;
        int changed = tweaks.Count(t => t.IsChanged);
        var shift = (TranslateTransform)PendingBar.RenderTransform;
        if (changed == 0) { PendingBar.Visibility = Visibility.Collapsed; return; }

        PendingText.Text = L.F("Изменений: {0}", changed);
        if (PendingBar.Visibility != Visibility.Visible)
        {
            PendingBar.Visibility = Visibility.Visible;
            Motion.Snap(shift, TranslateTransform.YProperty, 52);
            Motion.To(shift, TranslateTransform.YProperty, 0, Motion.Normal);
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        syncingTweaks = true;
        foreach (var t in tweaks) t.Desired = t.Applied ?? false;
        syncingTweaks = false;
        UpdatePending();
    }

    private async void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        var changed = tweaks.Where(t => t.IsChanged).ToList();
        if (changed.Count == 0) return;
        // Настройка, которая на секунды обрывает сеть, во время игры не применяется.
        if (recorder.IsRecording && changed.Any(t => t.DropsNetwork))
        {
            Motion.SetText(TweakNote, L.T("Сейчас идёт игра. Сетевая настройка применится после неё, чтобы интернет не пропал посреди матча."));
            TweakNote.Foreground = Palette.Warn;
            changed = changed.Where(t => !t.DropsNetwork).ToList();
            if (changed.Count == 0) return;
        }

        BtnApply.IsEnabled = false; BtnCancel.IsEnabled = false;
        BeginBusy();
        var errors = new List<string>();
        try
        {
            await Task.Run(() =>
            {
                foreach (var t in changed)
                {
                    try { if (t.Desired) t.Apply(); else t.Revert(); }
                    catch (Exception ex) { errors.Add(t.TitleText + ": " + ex.Message); }
                }
            });
            TweakNote.Text = "";
            await RefreshTweaksAsync();

            // Сторож сравнивает с этим списком: выключенное здесь вручную он «сбросом» не считает.
            // Настройки, перенесённые в разделы «Системы» (схема питания, ускорение мыши), ведутся там и здесь не теряются.
            settings.KnownApplied = (settings.KnownApplied ?? new()).Where(k => !tweaks.Any(t => t.Title == k))
                .Concat(tweaks.Where(t => t.Applied == true).Select(t => t.Title)).Distinct().ToList();
            // Решение принято вручную: вернул — больше не «сброшено»; выключил — тоже.
            settings.RevertedTweaks = settings.RevertedTweaks.Where(r => !changed.Any(t => t.Title == r)
                && (tweaks.Any(t => t.Title == r && t.Applied == false) || !tweaks.Any(t => t.Title == r))).ToList();
            settings.Save();
            UpdateRevertedBar();

            Motion.SetText(TweakNote, errors.Count > 0 ? L.F("Не всё удалось изменить: {0}", string.Join("; ", errors))
                : changed.Any(t => t.NeedsReboot) ? L.T("Готово. Часть изменений вступит в силу после перезагрузки.")
                : L.T("Готово."));
            TweakNote.Foreground = errors.Count > 0 ? Palette.Warn : Palette.Ink2;

            // Подсветить строки, которые изменились: после нажатия видно, что именно сработало.
            int i = 0;
            foreach (var t in changed)
                if (TweakList.ItemContainerGenerator.ContainerFromItem(t) is ContentPresenter row
                    && row.ContentTemplate?.FindName("Flash", row) is UIElement overlay)
                    Motion.Flash(overlay, i++ * 60);
        }
        finally
        {
            EndBusy();
            BtnApply.IsEnabled = true; BtnCancel.IsEnabled = true;
        }
    }

    // ================================================================== перед игрой
    private void BtnRefreshApps_Click(object sender, RoutedEventArgs e) => RefreshApps();

    private void RefreshApps()
    {
        try
        {
            apps.Clear();
            foreach (var app in GameApps.Scan())
            {
                app.PropertyChanged += (_, _) => UpdateAppsButton();
                apps.Add(app);
            }
            AppsEmpty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateAppsButton();
        }
        catch { }
    }

    private void UpdateAppsButton()
    {
        var selected = apps.Where(a => a.Selected).ToList();
        long sum = selected.Sum(a => a.Bytes);
        BtnCloseApps.Content = selected.Count > 0 ? L.F("Только закрыть · {0}", Fmt.Size(sum)) : L.T("Только закрыть");
        BtnCloseApps.IsEnabled = selected.Count > 0 && busy == 0;
        BtnPlay.IsEnabled = GameList.SelectedItem != null && busy == 0;
        BtnRestoreApps.Visibility = closedApps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Закрыть программы и запомнить, чем их открыть снова. Возвращает, сколько памяти освободилось (ГБ).</summary>
    private async Task<double> CloseAppsAsync(List<GameAppItem> selected)
    {
        var (_, before) = Metrics.ReadRam();
        foreach (var app in selected)
        {
            Motion.SetText(AppsSummary, L.F("Закрываю: {0}…", app.Title));
            string target = await Task.Run(() =>
            {
                string restore = null;
                try { restore = GameApps.RestoreTarget(app); } catch { }
                try { GameApps.Close(app); } catch { }
                return restore;
            });
            if (target != null && !closedApps.Contains(target)) closedApps.Add(target);
        }
        await Task.Delay(1500);
        var (_, after) = Metrics.ReadRam();
        return Math.Max(0, after - before);
    }

    private static string AppLines(List<GameAppItem> selected) =>
        string.Join("\n", selected.Select(a => "  • " + a.Title + " — " + a.Mem));

    private async void BtnCloseApps_Click(object sender, RoutedEventArgs e)
    {
        var selected = apps.Where(a => a.Selected).ToList();
        if (selected.Count == 0) return;

        if (MessageBox.Show(this, L.F("Закрыть программы?\n\n{0}\n\nНесохранённые данные в них могут быть потеряны.", AppLines(selected)), L.T("Перед игрой"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        BeginBusy();
        UpdateAppsButton();
        try
        {
            double freed = await CloseAppsAsync(selected);
            Motion.SetText(AppsSummary, L.F("Освобождено {0} памяти. Можно запускать игру.", Fmt.Gb(freed)));
        }
        finally
        {
            EndBusy();
            RefreshApps();
        }
    }

    // ================================================================== кнопка «Играть»
    private void LoadGames()
    {
        Task.Run(() =>
        {
            List<GameEntry> games;
            try { games = GameLibrary.Load(); } catch { games = new List<GameEntry>(); }
            OnUi(() =>
            {
                GameList.ItemsSource = games;
                GameList.SelectedItem = games.FirstOrDefault(g => g.Title == settings.LastGame) ?? games.FirstOrDefault();
                GameList.Visibility = games.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                GamesEmpty.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                UpdateAppsButton();
            });
        });
    }

    private void GameList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAppsButton();

    private void RestoreCheck_Click(object sender, RoutedEventArgs e)
    {
        settings.RestoreApps = RestoreCheck.IsChecked == true;
        settings.Save();
    }

    private async void BtnPlay_Click(object sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is not GameEntry game) return;
        var selected = apps.Where(a => a.Selected).ToList();

        if (selected.Count > 0 && MessageBox.Show(this,
                L.F("Закрыть программы и запустить «{0}»?\n\n{1}\n\nНесохранённые данные в них могут быть потеряны.", game.Title, AppLines(selected)), L.T("Играть"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        BeginBusy();
        UpdateAppsButton();
        try
        {
            double freed = selected.Count > 0 ? await CloseAppsAsync(selected) : 0;
            GameLibrary.Launch(game);
            settings.LastGame = game.Title;
            settings.Save();

            string text = selected.Count > 0 ? L.F("Освобождено {0} памяти. «{1}» запускается.", Fmt.Gb(freed), game.Title) : L.F("«{0}» запускается.", game.Title);
            if (closedApps.Count > 0 && settings.RestoreApps) text += L.T(" Закрытые программы откроются снова, когда выйдете из игры.");
            Motion.SetText(AppsSummary, text);
        }
        catch (Exception ex)
        {
            Motion.SetText(AppsSummary, L.F("Не удалось запустить «{0}»: {1}", game.Title, ex.Message));
        }
        finally
        {
            EndBusy();
            RefreshApps();
        }
    }

    private void BtnRestoreApps_Click(object sender, RoutedEventArgs e) => RestoreClosedApps();

    /// <summary>Открыть снова программы, закрытые перед игрой (с обычными правами пользователя).</summary>
    private void RestoreClosedApps()
    {
        var targets = closedApps.ToList();
        closedApps.Clear();
        int opened = 0;
        foreach (string target in targets)
        {
            try { GameLibrary.OpenAsUser(target); opened++; } catch { }
        }
        Motion.SetText(AppsSummary, opened > 0
            ? L.F("Открыто снова: {0}.", Fmt.Plural(opened, "программа", "программы", "программ"))
            : L.T("Не удалось снова открыть программы."));
        UpdateAppsButton();
    }
}

/// <summary>Строка таблицы сравнения сессий одной игры.</summary>
public class CompareRow
{
    /// <summary>Сессия строки — по щелчку она становится второй в сравнении рядом.</summary>
    public GameSession Session { get; set; }
    public string Smooth { get; set; }
    public string Hint { get; set; }
    public Visibility PartnerMark { get; set; } = Visibility.Collapsed;
    public string When { get; set; }
    public string Length { get; set; }
    public string Fps { get; set; }
    public string Low { get; set; }
    public string Temp { get; set; }
    public string Gpu { get; set; }
    public string Ram { get; set; }
    public Brush Ink { get; set; }
    public FontWeight Weight { get; set; }
}
