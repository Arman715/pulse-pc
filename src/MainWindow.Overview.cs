using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PulsePC.Blueprint;
using PulsePC.Services;

namespace PulsePC;

/// <summary>Комплектующие, которые можно открыть на главной странице.</summary>
public enum Comp { Cpu, Gpu, Ram, Storage, Board }

/// <summary>Строка показаний в правой панели: пиктограмма, подпись, значение.</summary>
public class ReadingVm : Notifier
{
    public Geometry Icon { get; set; }
    public string Label { get; set; }
    private string value = "—";
    public string Value { get => value; set => Set(ref this.value, value); }
    private Brush ink = Palette.Ink1;
    public Brush Ink { get => ink; set => Set(ref ink, value); }
}

/// <summary>Пункт списка «Компоненты».</summary>
public class CompVm
{
    public Comp Comp { get; set; }
    public Geometry Icon { get; set; }
    public string Title { get; set; }
}

public partial class MainWindow
{
    private Comp comp = Comp.Gpu;          // открытая (или выбранная на общем виде) деталь
    private Comp? hoverComp;               // деталь под курсором на общем виде
    private bool detail;                   // открыт подробный вид детали
    private HardwareInfo hw = new();
    private readonly ObservableCollection<ReadingVm> readings = new();
    private readonly List<CompVm> compItems = new();
    private bool syncingComp;

    private static Geometry Icon(string key) => (Geometry)Application.Current.FindResource(key);

    private static string CompTitle(Comp c) => c switch
    {
        Comp.Cpu => "Процессор", Comp.Gpu => "Видеокарта", Comp.Ram => "Память", Comp.Storage => "Диски", _ => "Материнская плата",
    };

    private static string CompIcon(Comp c) => c switch
    {
        Comp.Cpu => "IcoCpu", Comp.Gpu => "IcoGpu", Comp.Ram => "IcoRam", Comp.Storage => "IcoDrive", _ => "IcoBoard",
    };

    private static Part PartOf(Comp c) => c switch
    {
        Comp.Cpu => Part.Cpu, Comp.Gpu => Part.Gpu, Comp.Ram => Part.Ram, Comp.Storage => Part.Storage, _ => Part.Motherboard,
    };

    private static Comp? CompOf(Part p) => p switch
    {
        Part.Cpu => Comp.Cpu, Part.Gpu => Comp.Gpu, Part.Ram => Comp.Ram, Part.Storage => Comp.Storage, Part.Motherboard => Comp.Board, _ => null,
    };

    private static Scene SceneOf(Comp c) => c switch
    {
        Comp.Cpu => Scene.Cpu, Comp.Gpu => Scene.Gpu, Comp.Ram => Scene.Ram, Comp.Storage => Scene.Storage, _ => Scene.Pc,
    };

    private void InitOverview()
    {
        foreach (Comp c in new[] { Comp.Cpu, Comp.Gpu, Comp.Ram, Comp.Storage, Comp.Board })
            compItems.Add(new CompVm { Comp = c, Icon = Icon(CompIcon(c)), Title = CompTitle(c) });
        CompList.ItemsSource = compItems;
        CompRows.ItemsSource = readings;

        // Наведение связывает деталь на рисунке, её выноску и ячейку нижней полосы.
        Drawing.PartHovered += part =>
        {
            if (detail) return;
            hoverComp = CompOf(part);
            Drawing.Highlight = hoverComp != null ? part : Part.None;
            MarkStrip(hoverComp);
        };
        Drawing.PartClicked += part => { if (!detail && CompOf(part) is Comp c) OpenComp(c); };

        // Паспорт железа читается один раз в фоне.
        Task.Run(() => { var info = HardwareInfo.Query(); OnUi(() => { hw = info; metrics.FallbackGpuName = info.MainGpu?.Name ?? ""; metrics.FallbackGpuDriver = info.MainGpu?.Driver ?? ""; metrics.FallbackVramGb = info.MainGpu?.VramGb ?? 0; ApplyBlueprintLabels(); UpdateFooter(); if (!Hidden) UpdatePanelValues(); }); });

        ShowGeneral(animate: false);
    }

    /// <summary>Штамп листа на открытой странице прочерчивается заново.</summary>
    private void PlayPageHeader(int index)
    {
        static SheetHeader Find(DependencyObject node)
        {
            if (node is SheetHeader h) return h;
            int n = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < n; i++) if (Find(VisualTreeHelper.GetChild(node, i)) is SheetHeader found) return found;
            return null;
        }
        // Страница только что стала видимой: дерево элементов достраивается после разметки.
        Dispatcher.BeginInvoke(new Action(() => Find(pages[index])?.Play()), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ------------------------------------------------------------------ переходы
    /// <summary>Общий вид системного блока.</summary>
    private void ShowGeneral(bool animate)
    {
        detail = false;
        hoverComp = null;
        Drawing.ClearCallouts();
        Drawing.Scene = Scene.Pc;
        Drawing.Interactive = true;
        Drawing.GhostOthers = false;
        Drawing.Highlight = Part.None;
        Crumbs(null);
        OvHeader.Play("ЧЕРТЁЖ", "СИСТЕМНЫЙ БЛОК", "ОБЩИЙ ВИД, БОКОВАЯ КРЫШКА СНЯТА");
        AngleMarkEl.Visibility = Visibility.Collapsed;
        RulerEl.Visibility = Visibility.Collapsed;
        ShowStrip(true, animate);
        SetCallouts();
        ShowPanel(null, animate);
        SelectCompInList(null);
        MarkStrip(null);
        UpdateSheetNo();
        Drawing.Play();
    }

    /// <summary>Подробный вид детали: разнесённый чертёж и показания.</summary>
    private void OpenComp(Comp c)
    {
        if (page != PgOverview) SelectTab(PgOverview);
        bool fromGeneral = !detail;
        comp = c;
        hoverComp = null;
        int my = ++navGen;

        void Swap()
        {
            if (my != navGen) return;      // за время анимации выбрали другую деталь
            detail = true;
            Drawing.ClearCallouts();
            Drawing.Interactive = false;
            Drawing.Scene = SceneOf(c);
            Drawing.GhostOthers = c == Comp.Board;
            Drawing.Highlight = c == Comp.Board ? Part.Motherboard : Part.None;
            // Значок угла проекции (35°/45°) пользователю ничего не говорит — не показываем.
            RulerEl.Visibility = Visibility.Visible;
            Motion.Reveal(RulerEl, 180, 0, 300, 300);
            SetCallouts();
            CameraIn(fromGeneral && c != Comp.Board ? 0.82 : 0.94, fromGeneral ? 560 : 420);
            Drawing.Play();
        }

        detail = true;
        Crumbs(c);
        OvHeader.Play("ЧЕРТЁЖ", $"ВИД {ViewLetter(c)} · {CompTitle(c).ToUpperInvariant()}",
            c == Comp.Board ? "РАСПОЛОЖЕНИЕ НА ОБЩЕМ ВИДЕ" : c == Comp.Storage ? StorageSubtitle() : "КОНСТРУКТИВНАЯ СХЕМА, РАЗНЕСЁННЫЙ ВИД");
        ShowStrip(false, true);
        ShowPanel(c, animate: true);
        SelectCompInList(c);
        MarkStrip(null);
        UpdateSheetNo();

        // С общего вида — камера «влетает» в деталь, и из глубины проявляется её разнесённый вид;
        // с другой детали — текущая собирается, отъезжает и сменяется новой.
        if (fromGeneral) CameraOut(PartCenter(PartOf(c)), c == Comp.Board ? 1.22 : 2.1, c == Comp.Board ? 340 : 440, Swap);
        else Drawing.Collapse(() => { if (my == navGen) CameraOut(ViewCenter, 0.9, 220, Swap); });
    }

    private void BtnBack_Click(object sender, RoutedEventArgs e) => GoBack();

    /// <summary>Назад к общему виду: деталь собирается и отдаляется, системный блок наплывает навстречу.</summary>
    private void GoBack()
    {
        if (!detail) return;
        int my = ++navGen;
        Drawing.Collapse(() =>
        {
            if (my != navGen) return;
            CameraOut(ViewCenter, 0.78, 260, () => { ShowGeneral(animate: true); CameraIn(1.3, 600); });
        });
    }

    private static string ViewLetter(Comp c) => c switch { Comp.Cpu => "А", Comp.Gpu => "Б", Comp.Ram => "В", Comp.Storage => "Г", _ => "Д" };

    /// <summary>Хлебные крошки: на общем виде — просто «Общий вид», в детали — ссылка назад и название.</summary>
    private void Crumbs(Comp? c)
    {
        BtnBack.Visibility = c == null ? Visibility.Collapsed : Visibility.Visible;
        CrumbHome.Visibility = c == null ? Visibility.Visible : Visibility.Collapsed;
        CrumbSep.Visibility = CrumbCurrent.Visibility = c == null ? Visibility.Collapsed : Visibility.Visible;
        CrumbCurrent.Text = c == null ? "" : CompTitle(c.Value);
        OvHint.Visibility = c == null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Нижняя полоса: на общем виде видна, в детали уезжает вниз (её роль играет список «Компоненты»).</summary>
    private void ShowStrip(bool show, bool animate)
    {
        if (show)
        {
            Strip.Visibility = Visibility.Visible;
            if (animate) Motion.Reveal(Strip, 120, 24, Motion.Normal, Motion.Normal);
        }
        else Strip.Visibility = Visibility.Collapsed;
    }

    private void MarkStrip(Comp? c)
    {
        foreach (var cell in new[] { StripCpu, StripGpu, StripRam, StripDisk })
            StripState.SetSelected(cell, c != null && (string)cell.Tag == c.ToString());
    }

    // ------------------------------------------------------------------ «камера» чертежа
    private int navGen;   // номер перехода: шаги устаревшей анимации отбрасываются

    private Point ViewCenter => new(Drawing.ActualWidth / 2, Drawing.ActualHeight / 2);

    private Point PartCenter(Part part) =>
        Drawing.PartBounds(part) is Rect b && !b.IsEmpty ? new Point(b.X + b.Width / 2, b.Y + b.Height / 2) : ViewCenter;

    private static DoubleAnimation Tween(double from, double to, int ms, IEasingFunction ease, int delay = 0)
    {
        var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease, BeginTime = TimeSpan.FromMilliseconds(delay) };
        Timeline.SetDesiredFrameRate(a, 60);
        return a;
    }

    /// <summary>Рисунок без масштаба и полностью виден.</summary>
    private void ResetCamera()
    {
        Drawing.BeginAnimation(OpacityProperty, null);
        Drawing.Opacity = 1;
        Drawing.RenderTransform = Transform.Identity;
    }

    /// <summary>
    /// Уход кадра: рисунок масштабируется до zoom вокруг точки center (больше 1 — камера влетает, меньше — отъезжает),
    /// точка смещается к середине поля, во второй половине рисунок гаснет; затем then.
    /// </summary>
    private void CameraOut(Point center, double zoom, int ms, Action then)
    {
        int my = navGen;
        if (!Motion.On || Drawing.ActualWidth < 4) { ResetCamera(); then(); return; }
        var scale = new ScaleTransform(1, 1, center.X, center.Y);
        var move = new TranslateTransform();
        Drawing.RenderTransform = new TransformGroup { Children = { scale, move } };
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var z = Tween(1, zoom, ms, ease);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, z);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, z);
        move.BeginAnimation(TranslateTransform.XProperty, Tween(0, (ViewCenter.X - center.X) * 0.5, ms, ease));
        move.BeginAnimation(TranslateTransform.YProperty, Tween(0, (ViewCenter.Y - center.Y) * 0.5, ms, ease));
        var fade = Tween(1, 0, ms / 2, new QuadraticEase { EasingMode = EasingMode.EaseIn }, ms / 2);
        fade.Completed += (_, _) => { if (my != navGen) return; ResetCamera(); then(); };
        Drawing.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Приход кадра: новый рисунок проявляется, масштаб fromScale → 1 с замедлением к концу.</summary>
    private void CameraIn(double fromScale, int ms)
    {
        if (!Motion.On || Drawing.ActualWidth < 4) { ResetCamera(); return; }
        int my = navGen;
        var scale = new ScaleTransform(fromScale, fromScale, ViewCenter.X, ViewCenter.Y);
        Drawing.RenderTransform = scale;
        var z = Tween(fromScale, 1, ms, new QuinticEase { EasingMode = EasingMode.EaseOut });
        z.Completed += (_, _) => { if (my == navGen) ResetCamera(); };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, z);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, z);
        Drawing.BeginAnimation(OpacityProperty, Tween(0, 1, ms * 6 / 10, Motion.Out));
    }

    private void Strip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out Comp c)) OpenComp(c);
    }

    // Выбор из списка мышью — по отпусканию кнопки. Если открывать деталь сразу при нажатии, панель перестраивается
    // и прокручивается наверх под зажатой кнопкой, а ListBox принимает это за «протягивание» и выбирает первую строку (процессор).
    private CompVm pressedComp;

    private static CompVm CompAt(object source)
    {
        for (var d = source as DependencyObject; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ListBoxItem { DataContext: CompVm vm }) return vm;
        return null;
    }

    private void CompList_MouseDown(object sender, MouseButtonEventArgs e)
    {
        pressedComp = CompAt(e.OriginalSource);
        if (pressedComp != null) e.Handled = true;   // ListBox не захватывает мышь и не выбирает строку сам
    }

    private void CompList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var vm = CompAt(e.OriginalSource);
        bool same = vm != null && vm == pressedComp;
        pressedComp = null;
        if (!same) return;
        e.Handled = true;
        if (!detail || vm.Comp != comp) OpenComp(vm.Comp);
    }

    // С клавиатуры (стрелки) выбор по-прежнему открывает деталь.
    private void CompList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingComp || CompList.SelectedItem is not CompVm vm) return;
        if (!detail || vm.Comp != comp) OpenComp(vm.Comp);
    }

    private void SelectCompInList(Comp? c)
    {
        syncingComp = true;
        CompList.SelectedItem = c == null ? null : compItems.First(x => x.Comp == c);
        syncingComp = false;
    }

    // Esc, Backspace, Alt+← и кнопка мыши «назад» возвращают к общему виду.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        bool back = e.Key is Key.Escape or Key.Back || (e.Key == Key.System && e.SystemKey == Key.Left);
        if (back && page == PgOverview && detail && e.OriginalSource is not TextBox) { GoBack(); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1 && page == PgOverview && detail) { GoBack(); e.Handled = true; }
        base.OnPreviewMouseDown(e);
    }

    // ------------------------------------------------------------------ правая панель
    private Comp? shownComp = (Comp)(-1);

    /// <summary>
    /// Перестроить правую панель: на общем виде — «Нагрузка» (график за минуту и кто занимает память),
    /// в детали — её показания. Затем заполнить значениями.
    /// </summary>
    private void ShowPanel(Comp? sel, bool animate)
    {
        bool changed = sel != shownComp;
        shownComp = sel;
        if (changed && sel == null)
        {
            CompGlyph.Data = Icon("IcoDiag");
            CompName.Text = "Нагрузка";
            readings.Clear();
            CompHero.Visibility = CompScale.Visibility = CompCells.Visibility = BlockDisks.Visibility = Visibility.Collapsed;
            BlockChart.Visibility = BlockProcs.Visibility = Visibility.Visible;
            CompModel.Visibility = Visibility.Visible;
        }
        else if (changed)
        {
            var c = sel.Value;
            CompModel.Visibility = Visibility.Visible;
            CompGlyph.Data = Icon(CompIcon(c));
            CompName.Text = CompTitle(c);
            readings.Clear();
            void Row(string icon, string label) => readings.Add(new ReadingVm { Icon = Icon(icon), Label = label });
            switch (c)
            {
                case Comp.Gpu:
                    Row("IcoCores", "Загрузка"); Row("IcoBolt", "Потребление"); Row("IcoWave", "Частота"); Row("IcoFan", "Вентилятор"); Row("IcoRam", "Видеопамять"); Row("IcoInfo", "Драйвер");
                    break;
                case Comp.Cpu:
                    Row("IcoWave", "Частота сейчас"); Row("IcoCores", "Ядра / потоки"); Row("IcoClock", "Базовая частота"); Row("IcoCpu", "Сокет");
                    break;
                case Comp.Ram:
                    Row("IcoRam", "Доступно программам"); Row("IcoCores", "Планки"); Row("IcoWave", "Частота");
                    break;
                case Comp.Storage:
                    Row("IcoDrive", "Свободно на системном"); Row("IcoShield", "Состояние"); Row("IcoTemp", "Температура");
                    break;
                case Comp.Board:
                    Row("IcoBoard", "Производитель"); Row("IcoCpu", "Чипсет"); Row("IcoInfo", "BIOS"); Row("IcoClock", "Без перезагрузки");
                    break;
            }
            CompScale.Visibility = c == Comp.Gpu ? Visibility.Visible : Visibility.Collapsed;
            CompCells.Visibility = c == Comp.Ram ? Visibility.Visible : Visibility.Collapsed;
            CompHero.Visibility = c == Comp.Board ? Visibility.Collapsed : Visibility.Visible;
            BlockChart.Visibility = c == Comp.Cpu ? Visibility.Visible : Visibility.Collapsed;
            BlockProcs.Visibility = c == Comp.Ram ? Visibility.Visible : Visibility.Collapsed;
            BlockDisks.Visibility = c == Comp.Storage ? Visibility.Visible : Visibility.Collapsed;
            CompHero.Value = double.NaN;
        }
        UpdatePanelValues();
        if (changed) PanelScroll.ScrollToTop();
        if (changed && animate) Motion.Reveal(PanelBody, 0, 6, Motion.Fast, Motion.Normal);
    }

    private static string Num(double v, string format, string unit) => double.IsNaN(v) ? "—" : v.ToString(format) + (unit.Length > 0 ? " " + unit : "");

    /// <summary>Значения правой панели, выносок на чертеже и нижней полосы (раз в 1,5 с).</summary>
    private void UpdatePanelValues()
    {
        SyncDriveDrawing();
        UpdateCalloutValues();
        UpdateStrip();
        if (shownComp == null)
        {
            CompModel.Text = double.IsNaN(ghz) ? "Процессор и видеокарта" : $"процессор {load:0} % · {ghz:0.00} ГГц";
            CompNote.Visibility = Visibility.Collapsed;
            return;
        }
        var c = shownComp.Value;
        double used = total - avail;
        switch (c)
        {
            case Comp.Gpu:
                CompModel.Text = GpuFullName() is string gn && gn != "" ? gn : "Видеокарта не определена";
                SetRow(5, gpuOk && gpu.Driver.Length > 0 ? gpu.Driver : hw.MainGpu?.Driver is string dv && dv != "" ? dv : "—");
                // Температуру отдают драйвер NVIDIA и Windows (у отдельных видеокарт AMD и Intel); без неё главное число — загрузка.
                bool hasTemp = gpuOk && !double.IsNaN(gpu.Temp);
                CompHero.Unit = hasTemp ? "°C" : "%"; CompHero.Format = "0"; CompHero.UnitSize = 40;
                CompHero.Value = hasTemp ? gpu.Temp : gpuOk ? gpu.Load : double.NaN;
                CompHero.Foreground = hasTemp && gpu.Temp >= 83 ? Palette.Warn : Palette.Ink1;
                CompScale.Visibility = hasTemp ? Visibility.Visible : Visibility.Collapsed;
                CompScale.Target = hasTemp ? gpu.Temp : double.NaN;
                SetRow(0, Num(gpuOk ? gpu.Load : double.NaN, "0", "%"));
                SetRow(1, gpuOk ? gpu.PowerText : "—");
                SetRow(2, Num(gpuOk ? gpu.Clock : double.NaN, "0", "МГц"));
                SetRow(3, gpuOk ? gpu.FanText : "—");
                SetRow(4, gpuOk && !double.IsNaN(gpu.MemUsed) ? $"{gpu.MemUsed / 1024:0.0} / {(double.IsNaN(gpu.MemTotal) ? hw.MainGpu?.VramGb ?? 0 : gpu.MemTotal / 1024):0.0} ГБ" : hw.MainGpu?.VramGb > 0 ? $"{hw.MainGpu.VramGb:0.#} ГБ" : "—");
                break;
            case Comp.Cpu:
                CompModel.Text = string.IsNullOrEmpty(metrics.CpuName) ? "Процессор" : HardwareInfo.ShortCpu(metrics.CpuName);
                CompHero.Unit = "%"; CompHero.Format = "0"; CompHero.UnitSize = 40;
                CompHero.Value = load;
                CompHero.Foreground = Palette.Ink1;
                SetRow(0, Num(ghz, "0.00", "ГГц"));
                SetRow(1, hw.Cores > 0 ? $"{hw.Cores} / {hw.Threads}" : "—");
                SetRow(2, hw.MaxMhz > 0 ? $"{hw.MaxMhz / 1000.0:0.0} ГГц" : "—");
                SetRow(3, CpuSocket());
                CompNote.Text = "Температуру этого процессора Windows не сообщает без стороннего драйвера, поэтому её здесь нет.";
                break;
            case Comp.Ram:
                CompModel.Text = hw.Ram.Count > 0 ? $"{hw.Ram.Count} × {hw.Ram[0].SizeGb:0} ГБ  ·  {hw.Ram[0].Maker} {hw.RamType}".TrimEnd() : $"{Math.Round(total):0} ГБ";
                CompHero.Unit = $"/ {total:0.0} ГБ"; CompHero.Format = "0.0"; CompHero.UnitSize = 24;
                CompHero.Value = used;
                CompHero.Foreground = avail < 2 ? Palette.Warn : Palette.Ink1;
                CompCells.Target = used;
                SetRow(0, $"{avail:0.0} ГБ", avail < 2 ? Palette.Warn : null);
                SetRow(1, hw.Ram.Count > 0 ? string.Join(" + ", hw.Ram.Select(m => $"{m.SizeGb:0}")) + " ГБ" : "—");
                SetRow(2, hw.Ram.Count > 0 && hw.Ram[0].Mhz > 0 ? $"{hw.Ram[0].Mhz} МТ/с" : "—");
                break;
            case Comp.Storage:
            {
                var sys = SystemDrive();
                var sysDisk = SystemPhysDisk();
                CompModel.Text = metrics.Disks.Count > 0
                    ? $"{Fmt.Plural(metrics.Disks.Count, "диск", "диска", "дисков")}  ·  системный {ShortDisk(sysDisk.Name)}"
                    : Fmt.Plural(drives.Count, "раздел", "раздела", "разделов");
                CompHero.Unit = "ГБ своб."; CompHero.Format = "0"; CompHero.UnitSize = 24;
                CompHero.Value = sys == null ? double.NaN : sys.AvailableFreeSpace / Fmt.Gib;
                CompHero.Foreground = sys != null && sys.AvailableFreeSpace < 0.1 * sys.TotalSize ? Palette.Warn : Palette.Ink1;
                SetRow(0, sys == null ? "—" : $"{100.0 * sys.AvailableFreeSpace / sys.TotalSize:0} %  из {Fmt.Size(sys.TotalSize)}");
                var disks = metrics.Disks;
                int healthy = disks.Count(d => d.Health == "Healthy");
                SetRow(1, disks.Count == 0 ? (metrics.DisksLoaded ? "нужны права администратора" : "читаю…")
                    : healthy == disks.Count ? "все исправны" : $"исправны {healthy} из {disks.Count}", disks.Count > 0 && healthy < disks.Count ? Palette.Bad : null);
                var temps = disks.Where(d => d.Temp != null).Select(d => d.Temp.Value).ToList();
                SetRow(2, temps.Count > 0 ? $"до {temps.Max():0} °C" : "—");
                break;
            }
            case Comp.Board:
                CompModel.Text = hw.Loaded ? hw.Board : "Материнская плата";
                SetRow(0, hw.Loaded && hw.MakerShort != "" ? hw.MakerShort : "—");
                SetRow(1, hw.Chipset != "" ? hw.Chipset : "—");
                SetRow(2, hw.Loaded ? hw.Bios + (hw.BiosDate.Length > 0 ? " от " + hw.BiosDate : "") : "—");
                var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
                SetRow(3, $"{(int)up.TotalHours} ч {up.Minutes} мин");
                break;
        }
        CompNote.Visibility = c == Comp.Cpu ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetRow(int i, string value, Brush ink = null)
    {
        if (i >= readings.Count) return;
        readings[i].Value = value;
        readings[i].Ink = ink ?? Palette.Ink1;
    }

    private string CpuSocket() => hw.SocketName != "" ? hw.SocketName : "—";

    /// <summary>Полное имя главной видеокарты: от драйвера NVIDIA, иначе из паспорта (AMD, Intel).</summary>
    private string GpuFullName() => gpuOk && !string.IsNullOrEmpty(gpu.Name) ? gpu.Name : hw.MainGpu?.Name ?? "";

    private string GpuShortName() => HardwareInfo.ShortGpu(GpuFullName());

    /// <summary>Физический диск, на котором стоит Windows (по паспорту), иначе первый.</summary>
    private PhysDisk SystemPhysDisk() =>
        metrics.Disks.FirstOrDefault(d => hw.SystemDisk != "" && d.Name.Equals(hw.SystemDisk, StringComparison.OrdinalIgnoreCase)) ?? metrics.Disks.FirstOrDefault();

    /// <summary>Подписать детали чертежа железом этого компьютера.</summary>
    private void ApplyBlueprintLabels()
    {
        string gpuName = GpuFullName();
        Blueprint.BlueprintLabels.Cpu = HardwareInfo.ShortCpu(hw.CpuName != "" ? hw.CpuName : metrics.CpuName).Replace("Core ", "");
        Blueprint.BlueprintLabels.Gpu = HardwareInfo.ShortGpu(gpuName).Replace("GeForce ", "");
        Blueprint.BlueprintLabels.GpuBrand = HardwareInfo.GpuBrand(gpuName);
        Blueprint.BlueprintLabels.Board = (hw.Board ?? "").Split('(')[0].Trim();
        Blueprint.BlueprintLabels.RamType = hw.RamType != "" ? hw.RamType : "DDR";
        static string Spec(RamModule r) => $"{(r.Mhz > 0 ? r.Mhz : r.RatedMhz)} MHz  {r.SizeGb:0} GB";
        var m = hw.Ram.FirstOrDefault();
        Blueprint.BlueprintLabels.RamSpec = m == null ? "" : Spec(m);
        // На чертеже столько планок, сколько стоит; состав неизвестен — две, как раньше.
        Blueprint.BlueprintLabels.RamSticks = hw.Ram.Select(Spec).ToArray();
        diskSignature = null;      // системный диск теперь известен — порядок накопителей мог измениться
        SyncDriveDrawing(apply: false);
        Blueprint.BlueprintLabels.Apply();
        GameTuning.IsNvidia = hw.MainGpu == null || hw.MainGpu.Vendor == "NVIDIA";
    }

    private static DriveInfo SystemDrive()
    {
        try { var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)); return d.IsReady ? d : null; } catch { return null; }
    }

    private static string ShortDisk(string n) => n.Replace("WDC ", "").Replace("Samsung SSD ", "Samsung ").Split('-')[0].Trim();

    // ------------------------------------------------------------------ накопители на чертеже
    /// <summary>Накопитель на чертеже: физический диск, его вид и буквы разделов, найденные по размеру.</summary>
    private sealed record DrawnDisk(PhysDisk Disk, DriveKind Kind, string Name, string[] Letters);

    private List<DrawnDisk> drawnDisks = new();
    private string diskSignature;

    /// <summary>Чертёж рисует все накопители стопкой (а не подробный вид одного NVMe).</summary>
    private bool DriveStackShown => drawnDisks.Count > 1 || drawnDisks.Count == 1 && drawnDisks[0].Kind != DriveKind.Nvme;

    /// <summary>
    /// Вид накопителя: по шине и типу носителя от Windows (BusType, MediaType); если Windows их не сообщила — NVMe узнаём по имени модели.
    /// Неизвестный твердотельный рисуется как 2,5″ SATA, всё прочее — как 3,5″ HDD.
    /// </summary>
    private static DriveKind KindOf(PhysDisk d)
    {
        if (d.BusType.Equals("NVMe", StringComparison.OrdinalIgnoreCase)) return DriveKind.Nvme;
        if (d.BusType.Equals("SATA", StringComparison.OrdinalIgnoreCase) && d.Media.Equals("SSD", StringComparison.OrdinalIgnoreCase)) return DriveKind.Sata25;
        if (d.BusType.Equals("SATA", StringComparison.OrdinalIgnoreCase) && d.Media.Equals("HDD", StringComparison.OrdinalIgnoreCase)) return DriveKind.Hdd35;
        string n = d.Name ?? "";
        bool ssd = d.Media.Equals("SSD", StringComparison.OrdinalIgnoreCase)
                   || System.Text.RegularExpressions.Regex.IsMatch(n, @"SSD|NVMe|Solid", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!ssd) return DriveKind.Hdd35;
        bool nvme = System.Text.RegularExpressions.Regex.IsMatch(n,
            @"NVMe|PCIe|M\.2|\b9[789]0\b|\bPM9\d\d|\bSN\d{3}|WDS\d+\w\dX|\bSNV\d|\bNV[1-3]\b|KC3000|Renegade|CT\d+[PT]\d|\bMP\d{3}|\bP[345]1\b|\bSHGP|\bSKC3",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return nvme ? DriveKind.Nvme : DriveKind.Sata25;
    }

    /// <summary>Имя без производителя-префикса WDC/Samsung SSD и без объёма в конце: «Samsung 990 PRO», «WD20EZBX».</summary>
    private static string DiskName(string n) =>
        System.Text.RegularExpressions.Regex.Replace(ShortDisk(n ?? ""), @"\s+\d+(\.\d+)?\s?(TB|GB)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

    private static string DiskSize(double gb) => gb >= 1000 ? $"{gb / 1024:0.#} ТБ" : $"{gb:0} ГБ";

    /// <summary>
    /// Список накопителей для чертежа: системный сверху, затем NVMe, SATA, HDD. Буквы разделов приписываются диску,
    /// только если раздел занимает почти весь диск и такой диск один — иначе свободное место не показывается.
    /// </summary>
    private void SyncDriveDrawing(bool apply = true)
    {
        var disks = metrics.Disks;
        string sig = string.Join("|", disks.Select(d => d.Name + "/" + d.Media + "/" + d.BusType + "/" + d.SizeGb + "/" + string.Join(",", d.Letters))) + "#" + hw.SystemDisk;
        if (sig == diskSignature) return;
        diskSignature = sig;

        var sys = SystemPhysDisk();
        var list = disks.Select(d => new DrawnDisk(d, KindOf(d), DiskName(d.Name), Array.Empty<string>()))
            .OrderByDescending(x => ReferenceEquals(x.Disk, sys) && hw.SystemDisk != "")
            .ThenBy(x => x.Kind).ThenByDescending(x => x.Disk.SizeGb).ToList();

        // разделы → диски: буквы от Windows (Get-Partition). Оставшиеся буквы (тома дисковых пространств, RAID) — по размеру,
        // только дискам без своих букв.
        var letters = list.ToDictionary(x => x, x => x.Disk.Letters.Select(l => l + ":").ToList());
        try
        {
            string sysRoot = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
            // системный диск известен по паспорту — его раздел Windows приписывается ему без сравнения размеров
            var sysItem = hw.SystemDisk != "" ? list.FirstOrDefault(x => ReferenceEquals(x.Disk, sys)) : null;
            foreach (var v in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady))
            {
                string letter = v.Name.TrimEnd('\\');
                if (letters.Values.Any(l => l.Contains(letter, StringComparer.OrdinalIgnoreCase))) continue;
                if (sysItem != null && letter.Equals(sysRoot, StringComparison.OrdinalIgnoreCase)) { letters[sysItem].Add(letter); continue; }
                double gb = v.TotalSize / Fmt.Gib;
                var fit = list.Where(x => x.Disk.SizeGb > 0 && gb <= x.Disk.SizeGb * 1.005 && gb >= x.Disk.SizeGb * 0.9 && !ReferenceEquals(x, sysItem) && letters[x].Count == 0).ToList();
                if (fit.Count == 1 && letters[fit[0]].Count == 0) letters[fit[0]].Add(letter);
            }
        }
        catch { }
        drawnDisks = list.Select(x => x with { Letters = letters[x].ToArray() }).ToList();

        // На корпусе — модель без производителя: «990 PRO», «S270», «WD20EZBX».
        static string Engraving(string name) { var parts = name.Split(' ', 2); return parts.Length == 2 && parts[1].Length >= 3 ? parts[1] : name; }
        BlueprintLabels.Drives = drawnDisks.Select(x => new DriveSpec(x.Kind, Engraving(x.Name))).ToArray();
        if (!apply) return;
        BlueprintLabels.Apply();
        // Открыт вид накопителей — сменился сам рисунок: выноски и подзаголовок заново.
        if (detail && comp == Comp.Storage)
        {
            Drawing.ClearCallouts();
            OvHeader.Play("ЧЕРТЁЖ", $"ВИД {ViewLetter(Comp.Storage)} · {CompTitle(Comp.Storage).ToUpperInvariant()}", StorageSubtitle());
        }
    }

    private string StorageSubtitle() => DriveStackShown ? "ВСЕ ДИСКИ, РАЗНЕСЁННЫЙ ВИД" : "СИСТЕМНЫЙ ДИСК C:, РАЗНЕСЁННЫЙ ВИД";

    /// <summary>Выноски стопки накопителей: имя, объём и буквы; число — свободно, иначе температура или состояние.</summary>
    private void SetDiskCallouts()
    {
        for (int i = 0; i < drawnDisks.Count; i++)
        {
            var x = drawnDisks[i];
            var pd = x.Disk;
            string label = $"{x.Name} · {DiskSize(pd.SizeGb)}" + (x.Letters.Length > 0 ? " · " + string.Join(" ", x.Letters) : "");
            double free = double.NaN;
            foreach (var l in x.Letters)
                try { var v = new DriveInfo(l); if (v.IsReady) free = (double.IsNaN(free) ? 0 : free) + v.AvailableFreeSpace / Fmt.Gib; } catch { }
            bool bad = pd.Health != "" && pd.Health != "Healthy" || pd.ReadErrors > 0;
            bool hot = pd.Temp is double tt && tt >= (x.Kind == DriveKind.Hdd35 ? 55 : 70);
            string value; bool warn = false;
            if (bad)
            {
                value = pd.Health == "Healthy" ? "ошибки чтения" : pd.Health == "Warning" ? "есть сбои" : pd.Health == "Unhealthy" ? "неисправен" : pd.Health;
                warn = true;
            }
            else if (hot) { value = $"{pd.Temp:0} °C"; warn = true; if (!double.IsNaN(free)) label += $" · своб. {free:0} ГБ"; }
            else if (!double.IsNaN(free))
            {
                value = $"{free:0} ГБ своб.";
                warn = pd.SizeGb > 0 && free < 0.1 * pd.SizeGb;
                if (pd.Temp is double t) label += $" · {t:0} °C";
            }
            else if (pd.Temp is double t2) value = $"{t2:0} °C";
            else value = pd.Health == "Healthy" ? "исправен" : "—";
            Drawing.SetCallout($"disk.{i}", label, value, warn);
        }
    }

    // ------------------------------------------------------------------ выноски на чертеже
    /// <summary>Набор выносок для текущей сцены (подписи), значения подставляет UpdateCalloutValues.</summary>
    private void SetCallouts()
    {
        Drawing.ClearCallouts();
        UpdateCalloutValues();
    }

    private void UpdateCalloutValues()
    {
        double used = total - avail;
        var sys = SystemDrive();
        string free = sys == null ? "—" : $"{sys.AvailableFreeSpace / Fmt.Gib:0} ГБ";
        if (!detail)
        {
            string cpuModel = HardwareInfo.ShortCpu(metrics.CpuName);
            string gpuModel = GpuShortName();
            int disks = metrics.Disks.Count > 0 ? metrics.Disks.Count : drives.Count;
            bool diskTrouble = metrics.Disks.Any(d => d.Health != "Healthy" || d.ReadErrors > 0);
            Drawing.SetCallout("pc.cpu", "А  Процессор", cpuModel, compact: true);
            Drawing.SetCallout("pc.gpu", "Б  Видеокарта", gpuModel, gpuOk && gpu.Temp >= 83, compact: true);
            Drawing.SetCallout("pc.ram", "В  Память", $"{Math.Round(total):0} ГБ", compact: true);
            Drawing.SetCallout("pc.storage", "Г  Диски", disks > 0 ? Fmt.Plural(disks, "диск", "диска", "дисков") : "", diskTrouble, compact: true);
            Drawing.SetCallout("pc.board", "Д  Материнская плата", hw.Loaded ? hw.Board.Split('(')[0].Trim() : "", compact: true);
            return;
        }
        switch (comp)
        {
            case Comp.Gpu:
                Drawing.SetCallout("gpu.fan", "Вентилятор", gpuOk ? gpu.FanText : "—");
                Drawing.SetCallout("gpu.power", "Потребление", gpuOk ? gpu.PowerText : "—");
                Drawing.SetCallout("gpu.die", "Частота", Num(gpuOk ? gpu.Clock : double.NaN, "0", "МГц"));
                break;
            case Comp.Cpu:
                // Обе выноски с числами — на корпус процессора; кулер и сокет только подписаны.
                Drawing.SetCallout("cpu.ihs", "Загрузка", Num(load, "0", "%"));
                Drawing.SetCallout("cpu.package", "Частота", Num(ghz, "0.00", "ГГц"));
                Drawing.SetCallout("cpu.cooler", "Кулер", "");
                Drawing.SetCallout("cpu.socket", "Сокет " + CpuSocket(), "");
                break;
            case Comp.Ram:
            {
                // У каждой планки своя выноска: производитель · слот, объём. Больше четырёх — компактные, чтобы влезли.
                int sticks = Math.Min(hw.Ram.Count, 8);
                for (int i = 0; i < sticks; i++)
                {
                    var r = hw.Ram[i];
                    string slot = SlotName(r.Slot);
                    string who = r.Maker != "" && slot != "" ? r.Maker + " · " + slot : r.Maker != "" ? r.Maker : slot != "" ? "Слот " + slot : $"Планка {i + 1}";
                    Drawing.SetCallout($"ram.stick{i + 1}", who, $"{r.SizeGb:0} ГБ", compact: sticks > 4);
                }
                Drawing.SetCallout("ram.chips", "Занято", $"{used:0.0} ГБ", avail < 2);
                Drawing.SetDimension($"{total:0.0} ГБ");
                break;
            }
            case Comp.Storage when DriveStackShown:
                SetDiskCallouts();
                break;
            case Comp.Storage:
            {
                var nvme = SystemPhysDisk();
                Drawing.SetCallout("ssd.nand", "Свободно на C:", free);
                if (nvme?.Temp is double t) Drawing.SetCallout("ssd.controller", "Температура", $"{t:0} °C", t >= 70);
                else Drawing.SetCallout("ssd.controller", "Контроллер", "");
                Drawing.SetCallout("ssd.heatsink", "Состояние", nvme == null ? "нужны права" : nvme.Health == "Healthy" ? "исправен" : nvme.Health, nvme != null && nvme.Health != "Healthy");
                break;
            }
            case Comp.Board:
                Drawing.SetCallout("pc.board", "Материнская плата", hw.Loaded ? hw.Board.Split('(')[0].Trim() : "—");
                break;
        }
    }

    private static string SlotName(string slot) => slot.Contains('-') ? slot[(slot.LastIndexOf('-') + 1)..] : slot;

    // ------------------------------------------------------------------ нижняя полоса
    private void UpdateStrip()
    {
        StripCpuVal.Value = load;
        StripGpuVal.Value = gpuOk ? gpu.Temp : double.NaN;     // у видеокарт без датчика в Windows — прочерк
        StripGpuVal.Foreground = gpuOk && gpu.Temp >= 83 ? Palette.Warn : Palette.Ink1;
        StripRamVal.Unit = $"/ {total:0.0} ГБ";
        StripRamVal.Value = total - avail;
        StripRamBar.Target = total > 0 ? (total - avail) / total : double.NaN;
        var sys = SystemDrive();
        StripDiskVal.Value = sys == null ? double.NaN : sys.AvailableFreeSpace / Fmt.Gib;
        StripDiskBar.Target = sys == null ? double.NaN : 1 - (double)sys.AvailableFreeSpace / sys.TotalSize;
    }

    private void UpdateFooter()
    {
        string cpu = string.IsNullOrEmpty(metrics.CpuName) ? "процессор" : HardwareInfo.ShortCpu(metrics.CpuName);
        string gpuName = GpuShortName() is string gs && gs != "" ? gs : "видеокарта";
        string board = hw.Loaded ? "  •  " + hw.Board.Split('(')[0].Trim() : "";
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        FooterText.Text = $"{cpu}  •  {gpuName}  •  {Math.Round(total):0} ГБ{board}  •  Windows {Environment.OSVersion.Version.Build}  •  без перезагрузки {(int)up.TotalHours} ч {up.Minutes} мин";
    }
}

/// <summary>Отметка «выбрано» у ячейки нижней полосы (на неё реагирует стиль ячейки).</summary>
public static class StripState
{
    public static readonly DependencyProperty SelectedProperty = DependencyProperty.RegisterAttached(
        "Selected", typeof(bool), typeof(StripState), new FrameworkPropertyMetadata(false));
    public static bool GetSelected(DependencyObject d) => (bool)d.GetValue(SelectedProperty);
    public static void SetSelected(DependencyObject d, bool v) => d.SetValue(SelectedProperty, v);
}
