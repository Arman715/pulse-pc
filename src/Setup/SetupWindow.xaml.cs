using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PulsePC.Blueprint;
using PulsePC.Services;

namespace PulsePC.Setup;

/// <summary>
/// Лист «МОНТАЖ» / «ДЕМОНТАЖ»: установка и удаление в том же чертёжном языке, что и программа.
/// Пока идёт работа, рисунок слева прочерчивается по шагам (при удалении — стирается до пунктира проекта).
/// </summary>
public partial class SetupWindow : Window
{
    private readonly SetupRequest request;
    private readonly bool uninstall;
    private SetupTarget target;
    private bool upgrade, busy;
    private StepRow[] rows;

    /// <summary>Точка входа из App: окно или работа без окна (--quiet).</summary>
    public static void Run(Application app, SetupRequest r)
    {
        if (r.Quiet) { Installer.SignalReady(r); app.Shutdown(RunQuiet(r)); return; }
        var window = new SetupWindow(r);
        app.MainWindow = window;
        // Программа, которая запустила обновление, ждёт этого знака и только тогда выходит.
        window.ContentRendered += (_, _) => Installer.SignalReady(r);
        window.Show();
    }

    private static int RunQuiet(SetupRequest r)
    {
        SetupTarget target = null;
        try
        {
            if (r.Mode == SetupMode.Install) target = InstallTarget(r);
            var warnings = r.Mode == SetupMode.Install ? Installer.Install(target, !r.NoDesktop, r.Autostart, null) : null;
            if (r.Mode != SetupMode.Install) Installer.Uninstall(Installer.UninstallTarget(r), r.PurgeData, null);
            Log(r, warnings is { Count: > 0 } ? "OK\nWARN " + string.Join("\nWARN ", warnings) : "OK");
            return 0;
        }
        catch (Exception ex)
        {
            Log(r, "ERROR " + ex);
            return 1;
        }
        finally
        {
            // Обновление без нажатия: программа закрылась ради установки — запустить её снова (и при сбое: прежнюю версию,
            // она прочитает журнал установки и скажет, что не вышло).
            if (r.Relaunch && target != null) Relaunch(target, r.RelaunchMinimized);
        }
    }

    private static void Relaunch(SetupTarget t, bool minimized)
    {
        try
        {
            if (!File.Exists(t.Exe)) return;
            var psi = new ProcessStartInfo(t.Exe) { UseShellExecute = false, WorkingDirectory = t.Dir };
            if (minimized) psi.ArgumentList.Add("--minimized");
            // Папка распаковки установщика программе не нужна.
            psi.Environment.Remove("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) { Services.Log.Error("перезапуск после обновления", ex); }
    }

    private static void Log(SetupRequest r, string text)
    {
        if (string.IsNullOrEmpty(r.Log)) return;
        try { File.WriteAllText(r.Log, text + Environment.NewLine); } catch { }
    }

    private static SetupTarget InstallTarget(SetupRequest r)
    {
        if (r.TestDir != null) return SetupTarget.ForTest(r.TestDir);
        string existing = Installer.InstalledDir(SetupTarget.Real(Installer.DefaultDir));
        return SetupTarget.Real(existing ?? Installer.DefaultDir);
    }

    public SetupWindow(SetupRequest r)
    {
        InitializeComponent();
        WindowFit.Attach(this);
        request = r;
        uninstall = r.Mode == SetupMode.Uninstall;
        Motion.Init(this);

        target = uninstall ? Installer.UninstallTarget(r) : InstallTarget(r);
        upgrade = !uninstall && Installer.InstalledDir(target) != null;

        string title = uninstall ? L.T("Пульс ПК — удаление") : L.T("Пульс ПК — установка");
        Title = title;
        CaptionText.Text = title;
        Header.Title = uninstall ? L.T("ДЕМОНТАЖ") : L.T("МОНТАЖ");
        Header.Line1 = L.F("ПУЛЬС ПК · ВЕРСИЯ {0}", Installer.Version);
        Header.Line2 = L.T("ЛИСТ ОБЩИХ ДАННЫХ");
        SheetNo.Text = Ink.Track("ЛИСТ 1/1", 2);
        WorkLabel.Source = uninstall ? L.T("ХОД ДЕМОНТАЖА") : L.T("ХОД МОНТАЖА");

        if (uninstall) SetupUninstallForm(); else SetupInstallForm();
        UpdateFolder();
        if (r.TestDir != null) FooterText.Text = L.T("Проверка: запись в HKCU, ярлыки и данные — в папке «…-env» рядом.");

        Loaded += (_, _) =>
        {
            Header.Play();
            Drawing.Play();
            int delay = 80;
            foreach (UIElement child in FormContent.Children) { Motion.Reveal(child, delay, 6); delay += Motion.Stagger; }
        };
        Closing += (_, e) => { if (busy) e.Cancel = true; };
    }

    // ================================================================== форма

    private void SetupInstallForm()
    {
        NotesLabel.Source = L.T("НАЗНАЧЕНИЕ");
        AddNotes(
            L.T("Загрузка процессора, видеокарты и памяти."),
            L.T("Запись игр: FPS, просадки, пинг."),
            L.T("Поиск сбоев в журналах Windows и на дисках."),
            L.T("Очистка временных файлов по выбору."),
            L.T("Игровые настройки Windows — любую можно вернуть."));
        FolderLabel.Source = L.T("ПАПКА УСТАНОВКИ");
        // Не L.T("Обновить"): этот общий ключ переводится как «Refresh» (обновить список), а здесь — установка новой версии.
        BtnMain.Content = upgrade ? L.Pick("Обновить", "Update") : L.T("Установить");
        if (upgrade)
        {
            // Обновление: папку не меняем, автозапуск — как был.
            BtnFolder.Visibility = Visibility.Collapsed;
            ChkAutostart.IsChecked = Installer.AutostartExists(target);
        }
        FooterText.Text = upgrade
            ? L.T("Новая версия заменит старую, история и настройки сохранятся.")
            : L.T("Программа ставится для всех пользователей этого компьютера.");
        BuildTitleBlock((L.T("ВЕРСИЯ"), Installer.Version), (L.T("ОБЪЁМ"), FormatSize(Installer.PayloadBytes())), (L.T("СИСТЕМА"), "Windows 10/11 x64"));
    }

    private void SetupUninstallForm()
    {
        NotesLabel.Source = L.T("БУДЕТ УДАЛЕНО");
        AddNotes(
            L.T("Программа и PresentMon из папки ниже."),
            L.T("Ярлыки в меню «Пуск» и на рабочем столе."),
            L.T("Автозапуск и запись в списке установленных программ."),
            L.T("Разрешения брандмауэра Windows для программы."),
            L.T("Скачанные обновления."));
        WarnNote.Visibility = Visibility.Visible;
        WarnText.Text = L.T("Удаление не возвращает настройки, которые меняла программа. Если нужно, верните их до удаления: настройки Windows и игр — на вкладке «Оптимизация», схему питания и ускорение мыши — в разделах «Питание» и «Клавиатура и мышь» вкладки «Система».");
        FolderLabel.Source = L.T("ПАПКА ПРОГРАММЫ");
        BtnFolder.Visibility = Visibility.Collapsed;
        OptDesktop.Visibility = OptLaunch.Visibility = OptAutostart.Visibility = Visibility.Collapsed;
        OptPurge.Visibility = Visibility.Visible;
        long data = Installer.UserDataBytes(target);
        if (data > 0) PurgeText.Text = target.UserDataDir + " " + L.T("и") + " " + target.SecureDir + " · " + FormatSize(data);
        else { PurgeText.Text = L.T("Папок с историей и копиями для отката нет — удалять нечего."); ChkPurge.IsEnabled = false; }
        PurgeText.ToolTip = L.F("История и настройки: {0}\nКопии для отката настроек: {1}", target.UserDataDir, target.SecureDir);
        BtnMain.Content = L.T("Удалить");
        BtnMain.Style = (Style)FindResource("Destructive");
        BtnMain.Margin = new Thickness(0);
        FooterText.Text = L.T("Удаляется только то, что поставил установщик.");
        BuildTitleBlock((L.T("ВЕРСИЯ"), Installer.Version), (L.T("ПРОГРАММА"), FormatSize(Installer.InstalledBytes(target))),
            (L.T("ИСТОРИЯ"), data > 0 ? FormatSize(data) : L.T("нет")));
    }

    /// <summary>Пояснения пронумерованы, как технические требования на чертеже.</summary>
    private void AddNotes(params string[] notes)
    {
        Notes.Children.Clear();
        for (int i = 0; i < notes.Length; i++)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
            var num = new TextBlock
            {
                Text = (i + 1) + ".", Width = 22, FontFamily = (FontFamily)FindResource("Mono"), FontSize = 12,
                Foreground = (Brush)FindResource("Ink3"), Margin = new Thickness(0, 2, 0, 0),
            };
            DockPanel.SetDock(num, Dock.Left);
            row.Children.Add(num);
            var text = new TextBlock { Text = notes[i], Style = (Style)FindResource("Body"), Foreground = (Brush)FindResource("Ink1") };
            row.Children.Add(text);
            Notes.Children.Add(row);
        }
    }

    /// <summary>Основная надпись в углу поля: строки «название — значение» в рамке тушью.</summary>
    private void BuildTitleBlock(params (string Key, string Value)[] items)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var ink2 = (Brush)FindResource("Ink2");
        for (int i = 0; i < items.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
            var top = new Thickness(0, i == 0 ? 0 : 1, 0, 0);
            var key = new Border
            {
                BorderBrush = ink2, BorderThickness = new Thickness(0, top.Top, 1, 0), Padding = new Thickness(8, 0, 10, 0),
                Child = new TextBlock { Text = Ink.Track(items[i].Key, 1), Style = (Style)FindResource("Stamp"), Foreground = (Brush)FindResource("Ink3"), MinWidth = 70 },
            };
            var value = new Border
            {
                BorderBrush = ink2, BorderThickness = top, Padding = new Thickness(10, 0, 10, 0),
                Child = new TextBlock { Text = items[i].Value, Style = (Style)FindResource("Stamp"), Foreground = (Brush)FindResource("Ink1"), MinWidth = 120 },
            };
            Grid.SetRow(key, i); Grid.SetRow(value, i); Grid.SetColumn(value, 1);
            grid.Children.Add(key); grid.Children.Add(value);
        }
        TitleBlock.Child = grid;
    }

    private void UpdateFolder()
    {
        FolderText.Text = target.Dir;
        FolderText.ToolTip = target.Dir;
    }

    private void Option_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CheckBox box } && box.IsEnabled) box.IsChecked = box.IsChecked != true;
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = L.T("Папка для программы «Пульс ПК»"),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Path.GetDirectoryName(target.Dir) ?? target.Dir,
        };
        if (dialog.ShowDialog(new DialogOwner(new WindowInteropHelper(this).Handle)) != System.Windows.Forms.DialogResult.OK) return;
        string dir = dialog.SelectedPath;
        if (string.IsNullOrWhiteSpace(dir)) return;
        // Выбирают обычно родительскую папку: программа ставится в свою подпапку, чтобы при удалении не задеть соседей.
        if (!Path.GetFileName(dir.TrimEnd('\\')).Equals(Installer.AppName, StringComparison.OrdinalIgnoreCase))
            dir = Path.Combine(dir, Installer.AppName);
        // Сетевые папки, диски без прав доступа (FAT, exFAT), ссылки и папки с чужими программами не подходят:
        // папку установки программа закрывает от записи для всех, кроме администраторов, — там это невозможно или опасно.
        string problem = Installer.CheckInstallDir(dir, request.TestDir != null);
        if (problem != null)
        {
            FooterText.Text = problem;
            Motion.Reveal(FooterText, 0, 4);
            return;
        }
        target = request.TestDir != null ? SetupTarget.ForTest(dir) : SetupTarget.Real(dir);
        UpdateFolder();
        FooterText.Text = L.T("Программа ставится для всех пользователей этого компьютера. Менять её файлы смогут только администраторы.");
        Motion.Reveal(FolderText, 0, 4);
    }

    private sealed class DialogOwner : System.Windows.Forms.IWin32Window
    {
        public DialogOwner(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
    }

    // ================================================================== работа

    private async void Main_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        bool desktop = ChkDesktop.IsChecked == true, autostart = ChkAutostart.IsChecked == true, purge = ChkPurge.IsChecked == true;
        string[] steps = uninstall ? Installer.UninstallSteps(purge) : Installer.InstallSteps(autostart);
        BeginWork(steps);

        var t = target;
        var clock = Stopwatch.StartNew();
        double stepStart = 0;
        int current = -1;
        // Шаг на экране держится не меньше этого времени, иначе список мелькает (PULSEPC_SETUP_PACE — для снимков).
        int pace = int.TryParse(Environment.GetEnvironmentVariable("PULSEPC_SETUP_PACE"), out int p) ? p : 220;

        void Step(int index)
        {
            double took = clock.Elapsed.TotalSeconds - stepStart;
            if (current >= 0 && took * 1000 < pace) Thread.Sleep(pace - (int)(took * 1000));
            int done = current;
            current = index;
            stepStart = clock.Elapsed.TotalSeconds;
            Dispatcher.Invoke(() => ShowStep(index, done, took));
        }

        Exception error = null;
        List<SetupWarning> warnings = null;
        try
        {
            await RunSta(() =>
            {
                if (uninstall) Installer.Uninstall(t, purge, Step);
                else warnings = Installer.Install(t, desktop, autostart, Step);
                Step(steps.Length);
            });
        }
        catch (Exception ex) { error = ex; }

        busy = false;
        BtnX.IsEnabled = true;
        if (error != null) ShowError(current, error);
        else ShowDone(desktop, warnings);
    }

    private static Task RunSta(Action action)
    {
        var done = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try { action(); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);     // COM-объект ярлыков (IShellLink)
        thread.Start();
        return done.Task;
    }

    private void BeginWork(string[] steps)
    {
        busy = true;
        BtnX.IsEnabled = false;
        Drawing.Finish();
        Steps.Children.Clear();
        rows = new StepRow[steps.Length];
        Steps.Children.Add(new System.Windows.Shapes.Rectangle { Style = (Style)FindResource("Hairline") });
        for (int i = 0; i < steps.Length; i++)
        {
            rows[i] = new StepRow(steps[i], this);
            Steps.Children.Add(rows[i].Root);
        }
        Verdict.Visibility = Visibility.Collapsed;
        DoneButtons.Visibility = Visibility.Collapsed;
        BtnRetry.Visibility = Visibility.Collapsed;
        ProgressBlock.Visibility = Visibility.Visible;
        Motion.Snap(BarScale, ScaleTransform.ScaleXProperty, 0);
        SetCounter(0, steps.Length);

        if (FormPanel.Visibility == Visibility.Visible)
        {
            FormPanel.Visibility = Visibility.Collapsed;
            WorkPanel.Visibility = Visibility.Visible;
            Motion.Reveal(WorkLabel, 0, 6);
            for (int i = 0; i < rows.Length; i++) Motion.Reveal(rows[i].Root, 40 + i * Motion.Stagger, 6);

            // Проект пунктиром; сплошной рисунок — то, что уже сделано.
            Ghost.Visibility = Visibility.Visible;
            Motion.Reveal(Ghost, 0, 0, Motion.Normal);
            Motion.Snap(Drawing, BlueprintView.ProgressProperty, uninstall ? 1 : 0);
        }
    }

    private void ShowStep(int index, int finished, double seconds)
    {
        int n = rows.Length;
        if (finished >= 0 && finished < n) rows[finished].SetState(StepState.Done, seconds);
        if (index < n) rows[index].SetState(StepState.Active, 0);
        double fraction = Math.Clamp((double)index / n, 0, 1);
        SetCounter(Math.Min(index + 1, n), n, fraction);
        Motion.To(BarScale, ScaleTransform.ScaleXProperty, fraction, Motion.Data);
        Motion.To(Drawing, BlueprintView.ProgressProperty, uninstall ? 1 - fraction : fraction, Motion.Data + 200, Motion.InOut);
    }

    private void SetCounter(int step, int n, double fraction = 0)
    {
        StepCounter.Text = L.F("ШАГ {0} ИЗ {1}", step, n);
        PercentText.Text = Math.Round(fraction * 100).ToString(L.Culture) + L.Pick(" %", "%");
    }

    private void ShowDone(bool desktop, List<SetupWarning> warnings = null)
    {
        ProgressBlock.Visibility = Visibility.Collapsed;
        Verdict.Visibility = Visibility.Visible;
        DoneButtons.Visibility = Visibility.Visible;
        VerdictMark.Level = Severity.Ok;
        VerdictTitle.Text = L.T("Готово");
        VerdictTitle.Foreground = (Brush)FindResource("Ink1");

        if (uninstall)
        {
            VerdictText.Text = ChkPurge.IsChecked == true
                ? L.T("Пульс ПК удалён вместе с историей, настройками и копиями для отката.")
                : L.F("Пульс ПК удалён. История сессий и настройки остались в {0}, копии для отката — в {1}.", target.UserDataDir, target.SecureDir);
            BtnLaunch.Visibility = Visibility.Collapsed;
            BtnClose.Style = (Style)FindResource("Primary");
            BtnClose.Margin = new Thickness(0);
            Drawing.Visibility = Visibility.Hidden;       // остался только пунктир: «демонтировано»
            FooterText.Text = L.T("Остаток папки программы удалится, когда вы закроете это окно.");
        }
        else
        {
            // По виду предупреждения, а не по тексту: текст — на языке интерфейса.
            bool startOk = warnings == null || !warnings.Any(w => w.Kind == SetupWarningKind.StartMenu);
            bool deskOk = warnings == null || !warnings.Any(w => w.Kind == SetupWarningKind.Desktop);
            string where = startOk ? (desktop && deskOk ? L.T("Ярлык — в меню «Пуск» и на рабочем столе.") : L.T("Ярлык — в меню «Пуск»."))
                         : desktop && deskOk ? L.T("Ярлык — на рабочем столе.") : L.F("Программа — в папке {0}.", target.Dir);
            VerdictText.Text = (upgrade ? L.F("Пульс ПК обновлён до версии {0}.", Installer.Version) : L.T("Пульс ПК установлен.")) + " " + where;
            if (warnings is { Count: > 0 })
            {
                // Установка прошла; что-то второстепенное не вышло — говорим, что именно и почему, но не пугаем «ошибкой».
                VerdictMark.Level = Severity.Warn;
                VerdictTitle.Text = L.T("Готово, с замечанием");
                VerdictText.Text += "\n\n" + string.Join("\n", warnings.Select(w => char.ToUpper(w.Text[0]) + w.Text[1..]));
            }
            Motion.To(Ghost, OpacityProperty, 0, Motion.Normal);
            if (ChkLaunch.IsChecked == true && Launch())
            {
                VerdictText.Text += " " + L.T("Программа запущена.");
                BtnLaunch.Visibility = Visibility.Collapsed;
                BtnClose.Style = (Style)FindResource("Primary");
                BtnClose.Margin = new Thickness(0);
            }
            else BtnLaunch.Visibility = Visibility.Visible;
        }
        Motion.Reveal(Verdict, 0, 6);
        Dispatcher.BeginInvoke(new Action(() => WorkScroll.ScrollToEnd()), System.Windows.Threading.DispatcherPriority.Loaded);
        Motion.Reveal(DoneButtons, 60, 4);
        (BtnLaunch.Visibility == Visibility.Visible ? BtnLaunch : BtnClose).Focus();
    }

    private void ShowError(int failedStep, Exception ex)
    {
        if (failedStep >= 0 && failedStep < rows.Length) rows[failedStep].SetState(StepState.Failed, 0);
        ProgressBlock.Visibility = Visibility.Collapsed;
        Verdict.Visibility = Visibility.Visible;
        DoneButtons.Visibility = Visibility.Visible;
        BtnLaunch.Visibility = Visibility.Collapsed;
        BtnRetry.Visibility = Visibility.Visible;
        VerdictMark.Level = Severity.Bad;
        VerdictTitle.Text = L.T("Не удалось");
        VerdictTitle.Foreground = (Brush)FindResource("Bad");
        string reason = ex is UnauthorizedAccessException
            ? L.T("Нет доступа к папке или реестру. Запустите установщик от имени администратора.")
            : Installer.Reason(ex);
        VerdictText.Text = (failedStep >= 0 && failedStep < rows.Length ? L.F("Шаг «{0}»: ", rows[failedStep].Title) : "") + reason;
        Motion.Reveal(Verdict, 0, 6);
        Dispatcher.BeginInvoke(new Action(() => WorkScroll.ScrollToEnd()), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private bool Launch()
    {
        try
        {
            Process.Start(new ProcessStartInfo(target.Exe) { UseShellExecute = true, WorkingDirectory = target.Dir })?.Dispose();
            return true;
        }
        catch { return false; }
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (Launch()) Close();
        else
        {
            VerdictText.Text = L.F("Не удалось запустить {0}. Откройте программу ярлыком в меню «Пуск».", target.Exe);
            Motion.Reveal(VerdictText, 0, 4);
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string FormatSize(long bytes)
    {
        double mb = bytes / 1048576.0;
        if (mb >= 10) return mb.ToString("0", L.Culture) + " " + Fmt.MbUnit;
        if (mb >= 0.1) return mb.ToString("0.0", L.Culture) + " " + Fmt.MbUnit;
        return Math.Max(1, bytes / 1024).ToString(L.Culture) + " " + Fmt.KbUnit;
    }

    // ================================================================== строка шага

    private enum StepState { Pending, Active, Done, Failed }

    /// <summary>Строка ведомости: квадрат-маркер, название шага, справа — сколько он занял.</summary>
    private sealed class StepRow
    {
        public readonly Border Root;
        public readonly string Title;
        private readonly StepMark mark = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        private readonly TextBlock text, time;
        private readonly FrameworkElement owner;

        public StepRow(string title, FrameworkElement owner)
        {
            Title = title;
            this.owner = owner;
            text = new TextBlock { Text = title, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            time = new TextBlock { Style = (Style)owner.FindResource("MonoNum"), Foreground = (Brush)owner.FindResource("Ink3"), FontSize = 11.5, Margin = new Thickness(12, 0, 0, 0) };
            var dock = new DockPanel();
            DockPanel.SetDock(mark, Dock.Left);
            DockPanel.SetDock(time, Dock.Right);
            dock.Children.Add(mark);
            dock.Children.Add(time);
            dock.Children.Add(text);
            Root = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 9, 2, 9), Child = dock };
            Root.SetResourceReference(Border.BorderBrushProperty, "Line");
            SetState(StepState.Pending, 0);
        }

        public void SetState(StepState state, double seconds)
        {
            mark.State = state;
            text.SetResourceReference(TextBlock.ForegroundProperty, state switch
            {
                StepState.Pending => "Ink3",
                StepState.Failed => "Bad",
                StepState.Active => "Ink1",
                _ => "Ink2",
            });
            text.FontWeight = state == StepState.Active ? FontWeights.SemiBold : FontWeights.Normal;
            time.Text = state == StepState.Done ? seconds.ToString(seconds < 10 ? "0.00" : "0.0", L.Culture) + L.Pick(" с", " s") : "";
            if (state is StepState.Active or StepState.Done) Motion.Reveal(state == StepState.Done ? time : text, 0, state == StepState.Done ? 3 : 0, Motion.Fast);
        }
    }

    /// <summary>
    /// Квадрат 10×10 в духе StateMarker: ждёт — пустой контур, идёт — красный контур со штриховкой,
    /// сделан — залит тушью, ошибка — сплошной кармин.
    /// </summary>
    private sealed class StepMark : FrameworkElement
    {
        private StepState state;
        public StepState State { get => state; set { state = value; InvalidateVisual(); } }

        protected override Size MeasureOverride(Size s) => new(10, 10);

        protected override void OnRender(DrawingContext dc)
        {
            var box = new Rect(0.5, 0.5, 9, 9);
            switch (state)
            {
                case StepState.Pending:
                    dc.DrawRectangle(null, Ink.Pen(Palette.Ink4, 1), box);
                    break;
                case StepState.Active:
                    var red = Ink.Pen(Palette.Accent, 1);
                    dc.PushClip(new RectangleGeometry(new Rect(0, 0, 10, 10)));
                    for (double x = -10; x < 10; x += 3) dc.DrawLine(red, new Point(x, 10), new Point(x + 10, 0));
                    dc.Pop();
                    dc.DrawRectangle(null, red, box);
                    break;
                case StepState.Done:
                    dc.DrawRectangle(Palette.Ink1, null, new Rect(0, 0, 10, 10));
                    break;
                case StepState.Failed:
                    dc.DrawRectangle(Palette.Bad, null, new Rect(0, 0, 10, 10));
                    break;
            }
        }
    }
}
