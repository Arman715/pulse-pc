using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PulsePC.Services;

namespace PulsePC.Panels;

/// <summary>
/// Раздел «Монитор»: на какой частоте работает каждый экран и сколько он умеет, разрешение, цвет и HDR,
/// переменная частота (G-SYNC / FreeSync) и слежение за частотой. Меняется только частота — по кнопке,
/// с правами администратора, с подтверждением за 15 секунд и кнопкой «Вернуть как было».
/// </summary>
public partial class DisplayPanel : UserControl, ISystemPanel
{
    private const string Nb = " ";
    private PanelHost host;
    private bool busy, loading;
    private readonly DispatcherTimer countdown = new() { Interval = TimeSpan.FromSeconds(1) };

    public DisplayPanel()
    {
        InitializeComponent();
        countdown.Tick += (_, _) => ShowPending();
        // Смена частоты, подтверждение или откат — перечитать экраны.
        DisplayCheck.Changed += () => host?.Ui(() => { if (IsVisible || !IsLoaded) Reload(); });
        IsVisibleChanged += (_, _) => { if (!IsVisible) countdown.Stop(); else ShowPending(); };
    }

    public void Attach(PanelHost h)
    {
        host = h;
        DisplayCheck.Init(h);           // обычно уже вызван при запуске; повторный вызов ничего не делает
        loading = true;
        Watch.IsChecked = h.Settings.GetBool(DisplayCheck.WatchKey, true);
        Watch.IsEnabled = h.Admin;
        loading = false;
        AdminNote.Visibility = h.Admin ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Shown() => Reload();

    private void Reload()
    {
        if (host == null) return;
        Task.Run(() =>
        {
            List<ScreenInfo> screens = new();
            VrrInfo vrr = null;
            string vendor = "";
            try { screens = DisplayCheck.ReadScreens(); } catch { }
            try { vrr = DisplayCheck.ReadVrr(); } catch { }
            try { vendor = GpuVendors.MainVendor ?? ""; } catch { }
            var backups = screens.ToDictionary(s => s.Key, s => DisplayCheck.HasBackup(s.Key));
            host.Ui(() => Show(screens, vrr, vendor, backups));
        });
    }

    // ================================================================== экраны
    private void Show(List<ScreenInfo> screens, VrrInfo vrr, string vendor, Dictionary<string, bool> backups)
    {
        bool many = screens.Count > 1;
        var rows = screens.Select(s => Row(s, many, backups.TryGetValue(s.Key, out bool b) && b)).ToList();
        Screens.ItemsSource = rows;
        NoScreens.Visibility = screens.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var low = screens.Select(s => (s, j: DisplayCheck.Judge(s.Hz, s.Rates))).Where(x => x.j.Low).ToList();
        if (screens.Count == 0) State.Text = "Экраны не прочитаны";
        else if (low.Count == 1)
            State.Text = $"{(many ? $"Экран {low[0].s.Number} работает" : "Монитор работает")} на {DisplayCheck.HzText(low[0].s.Hz)}{Nb}Гц, а умеет {low[0].j.Target}{Nb}Гц";
        else if (low.Count > 1) State.Text = $"{Fmt.Plural(low.Count, "экран работает", "экрана работают", "экранов работают")} ниже своей частоты";
        else if (!many)
        {
            var s0 = screens[0];
            // 164,84 Гц при максимуме 165 — это и есть 165: в заголовке круглое число, точное — ниже.
            string hz = s0.MaxHz > 0 && Math.Abs(s0.Hz - s0.MaxHz) <= 1.5 ? s0.MaxHz.ToString() : DisplayCheck.HzText(s0.Hz);
            State.Text = $"Монитор работает на {hz}{Nb}Гц — " + (s0.AtNative ? "это его максимум" : $"это максимум при {s0.Width}×{s0.Height}");
        }
        else State.Text = "Все экраны работают на своей наибольшей частоте";

        ShowVrr(vrr, vendor);
        ShowPending();
        var (text, at) = DisplayCheck.LastAction;
        if (text.Length > 0 && (DateTime.Now - at).TotalMinutes < 30)
        {
            Result.Text = text + $" ({at:HH:mm})";
            Result.Visibility = Visibility.Visible;
        }
    }

    private ScreenRow Row(ScreenInfo s, bool many, bool backup)
    {
        var (low, target) = DisplayCheck.Judge(s.Hz, s.Rates);
        string res = s.Width > 0 ? $"{s.Width}×{s.Height}" : "—";
        var facts = new List<Fact>
        {
            new("Частоты при " + res, s.Rates.Length > 0 ? string.Join(" · ", s.Rates) + Nb + "Гц" : "не прочитаны",
                "Столько предлагает драйвер видеокарты для этого монитора и разрешения."),
            new("Родное разрешение", !s.NativeKnown ? "не прочитано" : $"{s.NativeWidth}×{s.NativeHeight}" + (s.AtNative ? " — включено" : ""),
                s.NativeKnown && !s.AtNative ? $"Сейчас {res}: картинка растянута и менее чёткая{(s.NativeMaxHz > 0 ? $". При родном разрешении монитор умеет до {s.NativeMaxHz}{Nb}Гц" : "")}." : ""),
            new("HDR", !s.HdrKnown ? "не читается" : !s.HdrSupported ? "не поддерживается" : s.HdrOn ? "включён" : "поддерживается, выключен",
                s.HdrKnown && s.HdrSupported && !s.HdrOn ? "Включается в «Параметрах» → «Система» → «Дисплей» → «Использовать HDR». В играх без поддержки HDR картинка от него не улучшится." : "", Mono: false),
            new("Подключение", s.Connection.Length > 0 ? s.Connection : "Windows не сообщает",
                s.Encoding.Length > 0 && s.Encoding != "RGB" ? $"Цвет передаётся как {s.Encoding}: обычно так бывает, когда кабелю не хватает пропускной способности." : "", Mono: false),
        };

        string verdict, advice = "";
        Severity level;
        if (double.IsNaN(s.Hz) || s.Rates.Length == 0)
        {
            verdict = "Частоту прочитать не удалось.";
            level = Severity.Info;
        }
        else if (low)
        {
            verdict = $"Частота ниже возможной: {DisplayCheck.HzText(s.Hz)}{Nb}Гц вместо {target}{Nb}Гц.";
            advice = "Так бывает после переустановки драйвера, смены кабеля или разъёма: Windows включает 60 Гц, пока частоту не поменять вручную. "
                     + "Видеокарта при этом может выдавать больше кадров, но на экране их не видно.";
            level = Severity.Warn;
        }
        else
        {
            verdict = s.Rates.Length > 1 ? $"Частота максимальная для {res}." : $"Монитор при {res} работает только на {target}{Nb}Гц.";
            if (!s.AtNative && s.NativeMaxHz > target)
                advice = $"При родном разрешении {s.NativeWidth}×{s.NativeHeight} монитор умеет до {s.NativeMaxHz}{Nb}Гц.";
            else if (target <= 60 && s.Connection == "HDMI")
                advice = "Если монитор по паспорту умеет больше 60 Гц, подключите его по DisplayPort или кабелем HDMI 2.0 и новее: старый кабель не пропускает высокую частоту.";
            level = Severity.Ok;
        }

        return new ScreenRow
        {
            Key = s.Key, Target = target,
            Label = $"ЭКРАН {s.Number}" + (s.Primary && many ? " · ОСНОВНОЙ" : ""),
            Title = s.Title,
            NowText = DisplayCheck.HzText(s.Hz),
            NowInk = low ? Palette.Warn : Palette.Ink1,
            MaxText = s.MaxHz > 0 ? s.MaxHz.ToString() : "—",
            MaxCaption = "наибольшая при этом разрешении",
            ResText = res,
            ResCaption = !s.NativeKnown ? "разрешение" : s.AtNative ? "разрешение, родное" : "разрешение, не родное",
            BitsText = s.Bits > 0 ? s.Bits.ToString() : "—",
            BitsUnit = s.Bits > 0 ? " бит" : "",
            Facts = facts,
            Level = level,
            Verdict = verdict,
            Advice = advice,
            EnableText = $"Включить {target}{Nb}Гц",
            EnableVisible = low ? Visibility.Visible : Visibility.Collapsed,
            RestoreVisible = backup ? Visibility.Visible : Visibility.Collapsed,
            ButtonsVisible = low || backup ? Visibility.Visible : Visibility.Collapsed,
            CanChange = host.Admin && !busy && DisplayCheck.Pending == null,
        };
    }

    // ================================================================== переменная частота
    private void ShowVrr(VrrInfo v, string vendor)
    {
        static (Severity, string) Flag(bool? on, string yes, string no) =>
            on switch { true => (Severity.Ok, yes), false => (Severity.Info, no), _ => (Severity.Info, "не задано") };

        (VrrMark.Level, VrrWord.Text) = v == null ? (Severity.Info, "—") : Flag(v.WindowsVrr, "включена", "выключена");
        (WinOptMark.Level, WinOptWord.Text) = v == null ? (Severity.Info, "—") : Flag(v.WindowedOptimizations, "включены", "выключены");

        string windows = "Переключатели Windows — в «Параметрах» → «Система» → «Дисплей» → «Графика», в параметрах графики по умолчанию.";
        if (v != null && v.Nvidia)
        {
            SyncTitle.Text = "G-SYNC в драйвере NVIDIA";
            (SyncMark.Level, SyncWord.Text) = v.GsyncMode switch
            {
                2 => (Severity.Ok, "окно и полный экран"),
                1 => (Severity.Ok, "только полный экран"),
                0 => (Severity.Info, "выключен"),
                -1 => (Severity.Info, "не задан"),
                _ => (Severity.Info, "не прочитан"),
            };
            SyncText.Text = v.GsyncMode switch
            {
                2 => "Общая настройка драйвера: G-SYNC работает и в полноэкранных играх, и в оконных.",
                1 => "Общая настройка драйвера: G-SYNC работает только в полноэкранных играх. Для игр в окне без рамки выберите «полноэкранный и оконный режим».",
                0 => "Общая настройка драйвера: G-SYNC выключен.",
                -1 => "В общем профиле драйвера настройка не записана — действует заводское значение драйвера.",
                _ => "Драйвер не отдал эту настройку.",
            };
            SyncWhere.Text = "Включён ли G-SYNC для самого монитора, видно в «Панели управления NVIDIA» → «Дисплей» → «Настройка G-SYNC». " + windows + " Программа эти настройки только читает.";
        }
        else
        {
            SyncTitle.Text = vendor switch { "AMD" => "FreeSync в драйвере AMD", "Intel" => "Adaptive Sync в драйвере Intel", _ => "Переменная частота в драйвере видеокарты" };
            SyncMark.Level = Severity.Info;
            SyncWord.Text = "не читается";
            SyncText.Text = vendor switch
            {
                "AMD" => "Программа не может прочитать эту настройку. Проверьте в AMD Software: Adrenalin Edition → «Игры» → «Дисплей» → «AMD FreeSync».",
                "Intel" => "Программа не может прочитать эту настройку. Проверьте в Intel Graphics Software → «Дисплей» → «Adaptive Sync».",
                _ => "Программа не может прочитать эту настройку. Она есть в фирменной программе драйвера видеокарты, в разделе о дисплее.",
            };
            SyncWhere.Text = windows + " Программа эти настройки только читает.";
        }
    }

    // ================================================================== смена частоты
    private void Enable_Click(object sender, RoutedEventArgs e)
    {
        if (busy || host?.Admin != true || (sender as FrameworkElement)?.Tag is not ScreenRow row) return;
        Run($"Включаю {row.Target}{Nb}Гц…", () => DisplayCheck.Apply(row.Key, row.Target));
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (busy || host?.Admin != true || (sender as FrameworkElement)?.Tag is not ScreenRow row) return;
        Run("Возвращаю…", () => DisplayCheck.Restore(row.Key));
    }

    private void Run(string progress, Func<(bool Ok, string Message)> action)
    {
        busy = true;
        Result.Text = progress;
        Result.Visibility = Visibility.Visible;
        host.BeginBusy();
        Reload();
        Task.Run(() =>
        {
            string message;
            try { message = action().Message; }
            catch (Exception ex) { message = "Не удалось: " + ex.Message; }
            host.Ui(() =>
            {
                host.EndBusy();
                busy = false;
                Reload();
                Result.Text = message;
                Result.Visibility = Visibility.Visible;
            });
        });
    }

    private void ShowPending()
    {
        var p = DisplayCheck.Pending;
        PendingBox.Visibility = p == null ? Visibility.Collapsed : Visibility.Visible;
        if (p == null) { countdown.Stop(); return; }
        int left = Math.Max(0, (int)Math.Ceiling((p.Deadline - DateTime.Now).TotalSeconds));
        PendingTitle.Text = $"«{p.Title}» переключён на {p.ToHz}{Nb}Гц";
        PendingText.Text = $"Если картинка в порядке, нажмите «Оставить». Без ответа через {left}{Nb}с вернётся {p.FromHz}{Nb}Гц.";
        BtnKeep.IsEnabled = BtnReject.IsEnabled = host?.Admin == true;
        if (IsVisible && !countdown.IsEnabled) countdown.Start();
    }

    private void Keep_Click(object sender, RoutedEventArgs e)
    {
        if (host?.Admin != true) return;
        Task.Run(DisplayCheck.Confirm);
    }

    private void Reject_Click(object sender, RoutedEventArgs e)
    {
        if (host?.Admin != true) return;
        Task.Run(DisplayCheck.Reject);
    }

    // ================================================================== слежение
    private void Watch_Click(object sender, RoutedEventArgs e)
    {
        if (loading || host?.Admin != true) return;
        bool on = Watch.IsChecked == true;
        Task.Run(() =>
        {
            host.Settings.SetBool(DisplayCheck.WatchKey, on);
            var values = host.Settings.Values;
            if (values != null) lock (values) host.Settings.Save(); else host.Settings.Save();
        });
    }

    // ================================================================== строки для разметки
    public sealed record Fact(string Name, string Value, string Note, bool Mono = true)
    {
        public Visibility MonoVisible => Mono ? Visibility.Visible : Visibility.Collapsed;
        public Visibility TextVisible => Mono ? Visibility.Collapsed : Visibility.Visible;
        public Visibility NoteVisible => string.IsNullOrEmpty(Note) ? Visibility.Collapsed : Visibility.Visible;
    }

    public sealed class ScreenRow
    {
        public string Key { get; init; }
        public int Target { get; init; }
        public string Label { get; init; }
        public string Title { get; init; }
        public string NowText { get; init; }
        public Brush NowInk { get; init; }
        public string MaxText { get; init; }
        public string MaxCaption { get; init; }
        public string ResText { get; init; }
        public string ResCaption { get; init; }
        public string BitsText { get; init; }
        public string BitsUnit { get; init; }
        public List<Fact> Facts { get; init; }
        public Severity Level { get; init; }
        public string Verdict { get; init; }
        public string Advice { get; init; }
        public Visibility AdviceVisible => string.IsNullOrEmpty(Advice) ? Visibility.Collapsed : Visibility.Visible;
        public string EnableText { get; init; }
        public Visibility EnableVisible { get; init; }
        public Visibility RestoreVisible { get; init; }
        public Visibility ButtonsVisible { get; init; }
        public bool CanChange { get; init; }
    }
}
