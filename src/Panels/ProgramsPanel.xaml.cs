using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PulsePC.Services;

namespace PulsePC.Panels;

/// <summary>
/// Раздел «Установленные программы»: программы из «Установки и удаления программ» (поиск, порядок, «Удалить» — деинсталлятор самой
/// программы после подтверждения), приложения Microsoft Store (только просмотр) и все игры, которые видит программа (откуда известна,
/// «это не игра» для найденных поиском и по поведению, настройки поиска). Открытие раздела ничего не меняет в системе.
/// </summary>
public partial class ProgramsPanel : UserControl, ISystemPanel
{
    private enum View { Programs, Store, Games }

    private PanelHost host;
    private View view = View.Programs;
    private List<InstalledProgram> programs = new();
    private List<StoreApp> store = new();
    private List<GameEntry> games = new();
    private bool loading, loaded;
    private readonly HashSet<string> pending = new(StringComparer.OrdinalIgnoreCase);    // удаляются сейчас (ключ записи)
    private FrameworkElement confirm;                                                     // открытое подтверждение под строкой

    public ProgramsPanel() => InitializeComponent();

    public void Attach(PanelHost h)
    {
        host = h;
        GameFinder.UseSettings(h.Settings);
        GameFinder.Changed += () => host.Ui(OnFinderChanged);
        // Осторожный режим: выбор поменяли здесь или в отчёте сессии — список игр перерисовывается. Настройки режиму передаёт
        // окно программы (MainWindow.Games.cs); в проверочном --render-panel их нет, и найденное остаётся только в памяти —
        // иначе пустые настройки снимка записались бы поверх настоящих.
        CautiousMode.Changed += () => host.Ui(() => { if (loaded && view == View.Games) Render(); });
        BehaviorToggle.IsEnabled = DiskToggle.IsEnabled = h.Admin;
        ToggleNote.Visibility = h.Admin ? Visibility.Collapsed : Visibility.Visible;
        // Какой список открыт — запоминается на время работы программы (programs.view: programs / store / games).
        switch (h.Settings.Get("programs.view"))
        {
            case "store": ViewStore.IsChecked = true; break;
            case "games": ViewGames.IsChecked = true; break;
        }
    }

    public void Shown() => Reload();

    private void Reload()
    {
        if (host == null || loading) return;
        loading = true;
        host.BeginBusy();
        Task.Run(() =>
        {
            List<InstalledProgram> p = null; List<StoreApp> s = null; List<GameEntry> g = null;
            try { p = ProgramsList.Load(); } catch (Exception ex) { Log.Error("список программ", ex); }
            try { s = ProgramsList.LoadStore(); } catch (Exception ex) { Log.Error("приложения Store", ex); }
            try { g = GameLibrary.Load(); } catch (Exception ex) { Log.Error("список игр", ex); }
            host.Ui(() =>
            {
                loading = false;
                host.EndBusy();
                programs = p ?? new(); store = s ?? new(); games = g ?? new();
                loaded = true;
                Render();
                ScanAntiCheats(forceAntiCheat);
                forceAntiCheat = false;
            });
        });
    }

    // ================================================================== античиты игр (осторожный режим)
    private bool acScanning, forceAntiCheat;

    /// <summary>
    /// Античит каждой игры из списка: по папке установки (только имена файлов, не глубже 5 уровней) и встроенному списку.
    /// В фоне, один раз на игру (результат запоминается в настройках); во время игры папки не обходятся.
    /// </summary>
    private void ScanAntiCheats(bool force)
    {
        if (acScanning) return;
        var list = games.ToList();
        acScanning = true;
        Task.Run(() =>
        {
            bool any = false;
            try
            {
                foreach (var g in list)
                {
                    if (!force && AntiCheat.Seen(g.Title)) continue;
                    if (GameHooks.InGame) break;
                    AntiCheatInfo info = null;
                    try
                    {
                        bool exe = !string.IsNullOrEmpty(g.Target) && g.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(g.Target);
                        if (exe) info = AntiCheat.FromFolder(g.Target);
                        string dir = g.InstallDir;
                        if (string.IsNullOrEmpty(dir) && exe) dir = Path.GetDirectoryName(g.Target);
                        if (info?.Kernel != true && !string.IsNullOrEmpty(dir)) info = AntiCheat.FromTree(dir) ?? info;
                        if (info?.Kernel != true) info = AntiCheat.FromList(g.Title, exe ? g.Target : null) ?? info;
                    }
                    catch (Exception ex) { Log.Error("античит игры " + g.Title, ex); }
                    // «Обновить» не должен забывать античит уровня ядра, найденный раньше по службе, запущенной вместе с игрой:
                    // в папке его может не быть (Vanguard, FACEIT), и следующая игра записалась бы полностью.
                    if (force && info?.Kernel != true && AntiCheat.ForGame(g.Title) is { Kernel: true }) continue;
                    AntiCheat.Remember(g.Title, info, save: false);
                    any = true;
                }
                if (any) AntiCheat.SaveSeen();
            }
            catch (Exception ex) { Log.Error("античиты игр", ex); }
            host.Ui(() =>
            {
                acScanning = false;
                if (any && view == View.Games) Render();
            });
        });
    }

    /// <summary>Строка под игрой: переключатель осторожного режима и что выбрано (античит, откуда известно, чьё решение).</summary>
    private FrameworkElement CautiousLine(GameEntry g)
    {
        var d = CautiousMode.For(g.Title);
        var line = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var toggle = new CheckBox
        {
            Style = (Style)FindResource("Toggle"), IsChecked = d.Cautious, IsEnabled = host.Admin,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 10, 0), ToolTip = CautiousMode.Explain,
        };
        System.Windows.Automation.AutomationProperties.SetName(toggle, L.F("Осторожный режим: {0}", g.Title));
        DockPanel.SetDock(toggle, Dock.Left);
        toggle.Click += (_, _) =>
        {
            if (!host.Admin) { toggle.IsChecked = d.Cautious; return; }
            if (toggle.IsChecked == true)
            {
                CautiousMode.Choose(g.Title, true);
                Result.SetResourceReference(TextBlock.ForegroundProperty, "Ink3");
                Result.Text = L.F("«{0}»: включён осторожный режим — FPS и сервер игры не записываются (если игра уже идёт — через несколько секунд).", g.Title);
                return;
            }
            toggle.IsChecked = true;      // выключится только после подтверждения
            ShowConfirm(toggle, CautiousMode.ConfirmFullText(g.Title), L.T("Записывать полностью"), destructive: false, () =>
            {
                CautiousMode.Choose(g.Title, false);
                Result.SetResourceReference(TextBlock.ForegroundProperty, "Ink3");
                Result.Text = L.F("«{0}»: полная запись — с FPS и сервером игры.", g.Title);
            });
        };
        line.Children.Add(toggle);
        var text = new TextBlock { Text = CautiousMode.StateLine(d), Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        if (d.Cautious) text.SetResourceReference(TextBlock.ForegroundProperty, "Ink2");
        line.Children.Add(text);
        return line;
    }

    private void OnFinderChanged()
    {
        UpdateScanState();
        if (GameFinder.Scanning || !loaded) return;
        Task.Run(() =>
        {
            List<GameEntry> g;
            try { g = GameLibrary.Load(); } catch { return; }
            host.Ui(() => { games = g; Render(); });
        });
    }

    // ================================================================== вывод
    private void View_Checked(object sender, RoutedEventArgs e)
    {
        view = sender == ViewStore ? View.Store : sender == ViewGames ? View.Games : View.Programs;
        host?.Settings.Set("programs.view", view.ToString().ToLowerInvariant());
        if (IsInitialized && loaded) Render();
    }

    private void Sort_Checked(object sender, RoutedEventArgs e) { if (IsInitialized && loaded) Render(); }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) { if (loaded) Render(); }

    private bool Matches(params string[] fields)
    {
        string q = SearchBox.Text.Trim();
        return q.Length == 0 || fields.Any(f => f?.Contains(q, StringComparison.CurrentCultureIgnoreCase) == true);
    }

    private void Render()
    {
        confirm = null;
        Rows.Children.Clear();
        ViewPrograms.Content = L.F("Программы · {0}", programs.Count);
        ViewStore.Content = L.F("Microsoft Store · {0}", store.Count);
        ViewGames.Content = L.F("Игры · {0}", games.Count);
        SortBar.Visibility = view == View.Games ? Visibility.Collapsed : Visibility.Visible;
        SortSize.Visibility = view == View.Programs ? Visibility.Visible : Visibility.Collapsed;
        if (view == View.Store && SortSize.IsChecked == true) SortName.IsChecked = true;
        AdminNote.Visibility = view == View.Programs && !host.Admin && programs.Any(p => p.Machine) ? Visibility.Visible : Visibility.Collapsed;
        StoreNote.Visibility = view == View.Store ? Visibility.Visible : Visibility.Collapsed;
        GamesBlock.Visibility = view == View.Games ? Visibility.Visible : Visibility.Collapsed;
        Intro.Visibility = view == View.Programs ? Visibility.Visible : Visibility.Collapsed;
        BtnSettings.Visibility = view == View.Games ? Visibility.Collapsed : Visibility.Visible;
        SearchBox.Tag = view == View.Games ? L.T("Найти игру") : L.T("Найти по названию или издателю");
        int shown = view switch { View.Store => RenderStore(), View.Games => RenderGames(), _ => RenderPrograms() };
        Empty.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Text = SearchBox.Text.Trim().Length > 0 ? L.T("Ничего не найдено.")
            : view == View.Games ? L.T("Игр не найдено. Поиск на дисках идёт в фоне; игру можно добавить вручную на вкладке «Сессии».")
            : L.T("Список пуст.");
        UpdateScanState();
        if (view == View.Games)
        {
            BehaviorToggle.IsChecked = GameFinder.BehaviorOn;
            DiskToggle.IsChecked = GameFinder.DiskSearchOn;
        }
    }

    private int RenderPrograms()
    {
        long total = programs.Sum(p => p.SizeBytes);
        State.Text = programs.Count == 0 ? L.T("Список программ пуст или не читается.")
            : L.F("Установлено {0}; вместе — около {1} (размер указан не у всех).", Fmt.Plural(programs.Count, "программа", "программы", "программ"), Fmt.Size(total));
        IEnumerable<InstalledProgram> list = programs.Where(p => Matches(p.Name, p.Publisher));
        list = SortSize.IsChecked == true ? list.OrderByDescending(p => p.SizeBytes).ThenBy(p => p.Name, NameOrder)
             : SortDate.IsChecked == true ? list.OrderByDescending(p => p.Installed ?? DateTime.MinValue).ThenBy(p => p.Name, NameOrder)
             : list.OrderBy(p => p.Name, NameOrder);
        int n = 0;
        foreach (var p in list) { Rows.Children.Add(ProgramRow(p)); n++; }
        return n;
    }

    private int RenderStore()
    {
        State.Text = L.F("Приложений Microsoft Store: {0}.", store.Count);
        IEnumerable<StoreApp> list = store.Where(a => Matches(a.Name, a.Publisher));
        list = SortDate.IsChecked == true ? list.OrderByDescending(a => a.Installed ?? DateTime.MinValue) : list.OrderBy(a => a.Name, NameOrder);
        int n = 0;
        foreach (var a in list)
        {
            Rows.Children.Add(Row(a.Name, a.Version, null, Date(a.Installed), null, a.FullName));
            n++;
        }
        return n;
    }

    private int RenderGames()
    {
        int found = games.Count(g => g.Source is "disk" or "store" or "behavior");
        State.Text = games.Count == 0 ? L.T("Программа пока не видит ни одной игры.")
            : L.Pick(string.Format(L.Culture, "Программа видит {0}; из них найдено поиском и по поведению — {1}.", Fmt.Plural(games.Count, "игру", "игры", "игр"), found),
                     string.Format(L.Culture, "The app sees {0}; found by search or by behavior: {1}.", Fmt.Plural(games.Count, "игру", "игры", "игр"), found));
        int n = 0;
        foreach (var g in games.Where(x => Matches(x.Title, x.Caption)).OrderBy(x => x.Title, NameOrder))
        {
            FrameworkElement action = null;
            if (g.CanRejectAsNotGame)
            {
                var b = LinkButton(L.T("это не игра"));
                b.Click += (_, _) => ConfirmNotGame(g, b);
                action = b;
            }
            string where = string.IsNullOrEmpty(g.InstallDir) ? g.Caption : g.Caption + " · " + g.InstallDir;
            Rows.Children.Add(Row(g.Title, where, null, null, action, g.Target, CautiousLine(g)));
            n++;
        }
        return n;
    }

    // Порядок названий как в «Параметрах»: латиница, затем кириллица, без учёта регистра.
    private static readonly StringComparer NameOrder = StringComparer.Create(CultureInfo.InvariantCulture, true);

    private static string Date(DateTime? d) => d is DateTime x ? x.ToString(L.Pick("d MMM yyyy", "MMM d, yyyy"), L.Culture) : "—";

    private FrameworkElement ProgramRow(InstalledProgram p)
    {
        FrameworkElement action;
        string key = Key(p);
        if (pending.Contains(key)) action = Note(L.T("удаляется…"), null);
        else switch (p.Kind)
        {
            case ProgramKind.Normal:
            case ProgramKind.Runtime:
                var b = LinkButton(UninstallWord);
                b.IsEnabled = !p.Machine || host.Admin;
                if (!b.IsEnabled) b.ToolTip = L.T("Только с правами администратора");
                b.Click += (_, _) => ConfirmUninstall(p, b);
                action = b;
                break;
            case ProgramKind.Self:
                var s = LinkButton(L.T("в «Параметрах»"));
                s.ToolTip = ProgramsList.NoButtonReason(p);
                s.Click += (_, _) => OpenSettings();
                action = s;
                break;
            case ProgramKind.Security: action = Note(L.T("защитная"), ProgramsList.NoButtonReason(p)); break;
            case ProgramKind.Driver: action = Note(L.T("драйвер"), ProgramsList.NoButtonReason(p)); break;
            default: action = Note(L.T("нет деинсталлятора"), ProgramsList.NoButtonReason(p)); break;
        }
        string caption = string.Join(" · ", new[] { p.Publisher, p.Version }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (!p.Machine) caption = caption.Length > 0 ? caption + " · " + L.T("только для вас") : L.T("только для вас");
        return Row(p.Name, caption, p.SizeBytes > 0 ? Fmt.Size(p.SizeBytes) : "—", Date(p.Installed), action, p.UninstallString);
    }

    // Общий ключ «Удалить» переводится как «Delete» (сессии, файлы); программу же — «Uninstall».
    private static string UninstallWord => L.Pick("Удалить", "Uninstall");

    private static string Key(InstalledProgram p) => p.Hive + "|" + p.KeyName;

    /// <summary>Строка таблицы: название и подпись слева, размер и дата моноширинным справа, действие в конце.</summary>
    private Border Row(string title, string caption, string size, string date, FrameworkElement action, string tip, FrameworkElement extra = null)
    {
        var border = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
        border.SetResourceReference(Border.BorderBrushProperty, "Line");
        var grid = new Grid { Margin = new Thickness(0, 9, 0, 9) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (size != null) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        if (date != null) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });

        var left = new StackPanel { ToolTip = string.IsNullOrEmpty(tip) ? null : tip };
        left.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("RowTitle"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrEmpty(caption))
            left.Children.Add(new TextBlock { Text = caption, Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
        if (extra != null) left.Children.Add(extra);
        grid.Children.Add(left);
        int col = 1;
        if (size != null)
        {
            var t = new TextBlock { Text = size, Style = (Style)FindResource("MonoNum"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 2, 0, 0) };
            Grid.SetColumn(t, col++);
            grid.Children.Add(t);
        }
        if (date != null)
        {
            var t = new TextBlock { Text = date, Style = (Style)FindResource("MonoNum"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 2, 0, 0) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Ink2");
            Grid.SetColumn(t, col++);
            grid.Children.Add(t);
        }
        if (action != null)
        {
            action.HorizontalAlignment = HorizontalAlignment.Right;
            action.VerticalAlignment = VerticalAlignment.Top;
            action.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(action, col);
            grid.Children.Add(action);
        }
        border.Child = grid;
        return border;
    }

    private Button LinkButton(string text) => new() { Style = (Style)FindResource("Link"), Content = text };

    private TextBlock Note(string text, string tip) =>
        new() { Text = text, Style = (Style)FindResource("Caption"), ToolTip = tip, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 3, 0, 0) };

    // ================================================================== подтверждение под строкой
    private void ShowConfirm(FrameworkElement anchor, string text, string yes, bool destructive, Action act)
    {
        if (confirm != null) Rows.Children.Remove(confirm);
        var row = FindRow(anchor);
        var box = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 6, 0, 8) };
        box.SetResourceReference(Border.BackgroundProperty, "Raised");
        box.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
        var panel = new StackPanel();
        var t = new TextBlock { Text = text, Style = (Style)FindResource("Body"), TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Ink1");
        panel.Children.Add(t);
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = yes, Margin = new Thickness(0, 0, 10, 0) };
        if (destructive) ok.Style = (Style)FindResource("Destructive");
        ok.Click += (_, _) => { Rows.Children.Remove(box); confirm = null; act(); };
        var cancel = new Button { Content = L.T("Отмена") };
        cancel.Click += (_, _) => { Rows.Children.Remove(box); confirm = null; };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        box.Child = panel;
        int at = row == null ? Rows.Children.Count : Rows.Children.IndexOf(row) + 1;
        Rows.Children.Insert(Math.Min(at, Rows.Children.Count), box);
        confirm = box;
        ok.Focus();
    }

    private UIElement FindRow(DependencyObject d)
    {
        while (d != null && !(d is UIElement u && Rows.Children.Contains(u))) d = System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
        return d as UIElement;
    }

    private void ConfirmUninstall(InstalledProgram p, FrameworkElement anchor)
    {
        string text = L.F("Удалить «{0}»? Это делает деинсталлятор самой программы; вернуть как было нельзя — только установить заново.", p.Name);
        if (p.Kind == ProgramKind.Runtime)
            text += "\n\n" + L.T("Это компонент, который нужен другим программам и играм: после его удаления они могут перестать запускаться.");
        ShowConfirm(anchor, text, UninstallWord, destructive: true, () => Uninstall(p));
    }

    private void Uninstall(InstalledProgram p)
    {
        string err = ProgramsList.Uninstall(p, host.Admin, out string started);
        Result.SetResourceReference(TextBlock.ForegroundProperty, err != null ? "Warn" : "Ink3");
        if (err != null) { Result.Text = err; return; }
        if (ProgramsList.DryRun) { Result.Text = L.F("Проверка: деинсталлятор не запускался. Команда была бы такой: {0}", started); return; }
        string key = Key(p);
        pending.Add(key);
        Result.Text = L.F("Деинсталлятор «{0}» запущен — дальше следуйте его окну. Когда он закончит, программа пропадёт из списка.", p.Name);
        Render();
        // Многие деинсталляторы сразу перезапускают себя из временной папки, поэтому ждём не процесс, а исчезновения записи.
        Task.Run(async () =>
        {
            var until = DateTime.Now.AddMinutes(20);
            while (DateTime.Now < until)
            {
                await Task.Delay(4000);
                if (ProgramsList.StillInstalled(p)) continue;
                host.Ui(() =>
                {
                    pending.Remove(key);
                    programs.Remove(p);
                    Result.SetResourceReference(TextBlock.ForegroundProperty, "Ink3");
                    Result.Text = L.F("«{0}» удалена.", p.Name);
                    Render();
                });
                return;
            }
            host.Ui(() =>
            {
                pending.Remove(key);
                Result.Text = L.F("«{0}» всё ещё в списке: похоже, удаление отменили или деинсталлятор ждёт перезагрузки.", p.Name);
                Render();
            });
        });
    }

    private void ConfirmNotGame(GameEntry g, FrameworkElement anchor)
    {
        string text = L.F("«{0}» больше не будет считаться игрой: запись для неё не начнётся, и поиск её больше не найдёт. Записанные сессии останутся — удалить их можно на вкладке «Сессии».", g.Title);
        ShowConfirm(anchor, text, L.T("Это не игра"), destructive: false, () =>
        {
            GameFinder.MarkNotGame(GameFinder.RejectPath(g));
            games.Remove(g);
            Result.SetResourceReference(TextBlock.ForegroundProperty, "Ink3");
            Result.Text = L.F("«{0}» убрана из игр.", g.Title);
            Render();
        });
    }

    // ================================================================== игры: поиск и настройки
    private void UpdateScanState()
    {
        BtnScan.IsEnabled = !GameFinder.Scanning;
        if (GameFinder.Scanning) { ScanState.Text = L.T("Идёт поиск на дисках…"); return; }
        if (GameFinder.LastScan == DateTime.MinValue) { ScanState.Text = L.T("Поиск на дисках ещё не выполнялся: первый начнётся через пару минут после запуска программы."); return; }
        int onDisk = GameFinder.Found().Count(g => g.Source != "behavior");
        ScanState.Text = L.F("Последний поиск — {0}: просмотрено {1} за {2} с, найдено {3}.",
            GameFinder.LastScan.ToString(L.Pick("d MMMM, HH:mm", "MMMM d, HH:mm"), L.Culture),
            Fmt.Plural(GameFinder.LastScanDirs, "папка", "папки", "папок"),
            GameFinder.LastScanSeconds.ToString("0", L.Culture),
            Fmt.Plural(onDisk, "игра", "игры", "игр"));
    }

    private void Scan_Click(object sender, RoutedEventArgs e)
    {
        Result.SetResourceReference(TextBlock.ForegroundProperty, "Ink3");
        if (GameFinder.StartScan()) Result.Text = L.T("Поиск начат: он идёт в фоне и почти не нагружает диск. Список обновится сам.");
        else if (GameHooks.InGame) Result.Text = L.T("Во время игры диски не обходятся — поиск начнётся после неё.");
        UpdateScanState();
    }

    private void BehaviorToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!host.Admin) { BehaviorToggle.IsChecked = GameFinder.BehaviorOn; return; }
        GameFinder.SetBehaviorOn(BehaviorToggle.IsChecked == true);
    }

    private void DiskToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!host.Admin) { DiskToggle.IsChecked = GameFinder.DiskSearchOn; return; }
        GameFinder.SetDiskSearchOn(DiskToggle.IsChecked == true);
    }

    // ================================================================== кнопки внизу
    private void OpenSettings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        try { GameLibrary.OpenAsUser("ms-settings:appsfeatures"); }
        catch (Exception ex) { Result.Text = L.F("Не удалось открыть «Параметры»: {0}", ex.Message); }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        Result.Text = "";
        forceAntiCheat = true;      // и античиты игр — заново по папкам
        Reload();
    }
}
