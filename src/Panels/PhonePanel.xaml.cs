using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PulsePC.Services;

namespace PulsePC.Panels;

/// <summary>Раздел «Панель на телефоне»: переключатель, адрес текстом и QR-кодом, кто подключён, советы.</summary>
public partial class PhonePanel : UserControl, ISystemPanel
{
    private readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(2) };
    private PanelHost host;
    private string shownUrl = "", note = "";
    private bool switching;

    public PhonePanel()
    {
        InitializeComponent();
        tick.Tick += (_, _) => Refresh();
        // Счётчик устройств и «последнее обращение» обновляются, только пока раздел на экране.
        IsVisibleChanged += (_, _) => { if (IsVisible && host != null) { tick.Start(); Refresh(); } else tick.Stop(); };
    }

    public void Attach(PanelHost host)
    {
        this.host = host;
        PhoneServer.Init(host);                         // обычно уже вызван при запуске; повторный вызов ничего не делает
        PhoneServer.Changed += () => host.Ui(Refresh);
        Switch.IsEnabled = host.Admin;
        BtnKey.IsEnabled = host.Admin;
        AdminNote.Visibility = host.Admin ? Visibility.Collapsed : Visibility.Visible;
        if (IsVisible) tick.Start();
        Refresh();
    }

    public void Shown()
    {
        note = "";
        if (IsVisible) tick.Start();
        Refresh();
    }

    private void Refresh()
    {
        if (host == null) return;
        var s = PhoneServer.Snapshot();
        if (!switching) Switch.IsChecked = s.Enabled;

        State.Text = !s.Enabled ? "Панель выключена."
            : !s.Running ? (s.Problem.Length > 0 ? "Панель включена, но не запустилась." : "Панель запускается…")
            : s.Devices > 0 ? $"Панель работает, подключено устройств: {s.Devices}."
            : "Панель работает, телефон пока не подключён.";

        string result = s.Enabled && !s.Running && s.Problem.Length > 0 ? s.Problem
            : note.Length == 0 && s.Running && s.KeyRenewed
                ? "Ключ в адресе стал длиннее (12 знаков вместо 6) — так его не подобрать перебором. Старый QR-код и значок на телефоне больше не открываются: наведите камеру на новый код ниже."
                    + " Профиль для iPhone тоже нужно сохранить и поставить заново."
                : note;
        Result.Text = result;
        Result.Foreground = result == note ? Palette.Ink3 : Palette.Warn;
        Result.Visibility = result.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        AddressBlock.Visibility = s.Running ? Visibility.Visible : Visibility.Collapsed;
        if (!s.Running) { shownUrl = ""; return; }

        if (s.Url != shownUrl)
        {
            shownUrl = s.Url;
            Url.Text = s.Url;
            var code = QrCode.Encode(s.Url);
            Qr.Matrix = code;
            QrFrame.Visibility = code != null ? Visibility.Visible : Visibility.Collapsed;
            HowTo.Text = code != null
                ? "Наведите камеру телефона на код или наберите адрес в браузере телефона целиком:"
                : "Наберите адрес в браузере телефона целиком:";
        }
        Devices.Text = s.Devices == 0 ? "пока никто" : Fmt.Plural(s.Devices, "устройство", "устройства", "устройств");
        Last.Text = s.LastRequest == default ? "ещё не было" : Ago(s.LastRequest);
        Adapter.Text = s.Adapter;
    }

    private static string Ago(DateTime at)
    {
        var passed = DateTime.Now - at;
        if (passed.TotalSeconds < 5) return "только что";
        if (passed.TotalSeconds < 60) return $"{(int)passed.TotalSeconds} с назад";
        if (passed.TotalMinutes < 60) return $"{(int)passed.TotalMinutes} мин назад";
        return at.Date == DateTime.Today ? $"сегодня в {at:HH:mm}" : $"{at:d MMM} в {at:HH:mm}";
    }

    private void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (host == null || !host.Admin) return;
        bool on = Switch.IsChecked == true;
        switching = true;
        Switch.IsEnabled = false;
        note = "";
        host.BeginBusy();
        PhoneServer.SetEnabled(on, () => host.Ui(() =>
        {
            host.EndBusy();
            switching = false;
            Switch.IsEnabled = true;
            Refresh();
        }));
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (shownUrl.Length == 0) return;
        try { Clipboard.SetText(shownUrl); note = "Адрес скопирован."; }
        catch { note = "Скопировать не получилось: буфер обмена занят другой программой. Попробуйте ещё раз."; }
        Refresh();
    }

    /// <summary>Профиль для iPhone на рабочий стол: его ставят через iMazing, Apple Configurator или открывают на телефоне.</summary>
    private void BtnIphone_Click(object sender, RoutedEventArgs e)
    {
        var bytes = PhoneServer.CurrentProfile();
        if (bytes == null) { note = "Сначала включите панель."; Refresh(); return; }
        try
        {
            string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Пульс ПК для iPhone.mobileconfig");
            File.WriteAllBytes(file, bytes);
            note = "Профиль сохранён на рабочем столе: «Пульс ПК для iPhone.mobileconfig». Установите его через iMazing («Профили» → «Установить») "
                 + "или откройте на iPhone адрес панели с «pulse.mobileconfig» на конце. На телефоне: «Настройки» → «Профиль загружен» → «Установить».";
        }
        catch (Exception ex) { note = "Не удалось сохранить профиль: " + ex.Message; }
        Refresh();
    }

    private void BtnKey_Click(object sender, RoutedEventArgs e)
    {
        if (host == null || !host.Admin) return;
        BtnKey.IsEnabled = false;
        host.BeginBusy();
        PhoneServer.ChangeKey(() => host.Ui(() =>
        {
            host.EndBusy();
            BtnKey.IsEnabled = true;
            note = "Ключ сменён: прежний адрес больше не открывается. На телефоне откройте новый.";
            Refresh();
        }));
    }
}

/// <summary>
/// Рисунок QR-кода: тушь на светлой бумаге, поле в четыре модуля вокруг (без него камеры код не находят).
/// Модуль — целое число точек экрана при любом обычном масштабе Windows, поэтому края не размываются.
/// </summary>
public sealed class QrView : FrameworkElement
{
    private const double Module = 4;
    private const int Quiet = 4;
    private bool[,] matrix;

    public QrView()
    {
        SnapsToDevicePixels = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    /// <summary>Модули [строка, столбец]; null — кода нет, элемент занимает ноль места.</summary>
    public bool[,] Matrix
    {
        get => matrix;
        set { matrix = value; InvalidateMeasure(); InvalidateVisual(); }
    }

    private double Side => matrix == null ? 0 : (matrix.GetLength(0) + 2 * Quiet) * Module;

    protected override Size MeasureOverride(Size availableSize) => new(Side, Side);

    protected override void OnRender(DrawingContext dc)
    {
        if (matrix == null) return;
        int n = matrix.GetLength(0);
        dc.DrawRectangle(Palette.Raised, null, new Rect(0, 0, Side, Side));
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                if (!matrix[y, x]) continue;
                int from = x;
                while (x + 1 < n && matrix[y, x + 1]) x++;          // соседние тёмные модули — одним прямоугольником, без швов
                dc.DrawRectangle(Palette.Ink1, null, new Rect((from + Quiet) * Module, (y + Quiet) * Module, (x - from + 1) * Module, Module));
            }
    }
}
