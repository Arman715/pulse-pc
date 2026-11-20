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

        State.Text = !s.Enabled ? L.T("Панель выключена.")
            : !s.Running ? (s.Problem.Length > 0 ? L.T("Панель включена, но не запустилась.") : L.T("Панель запускается…"))
            : s.Devices > 0 ? L.F("Панель работает, подключено устройств: {0}.", s.Devices)
            : L.T("Панель работает, телефон пока не подключён.");

        string result = s.Enabled && !s.Running && s.Problem.Length > 0 ? s.Problem
            : note.Length == 0 && s.Running && s.KeyRenewed
                ? L.T("Ключ в адресе стал длиннее (12 знаков вместо 6) — так его не подобрать перебором. Старый QR-код и значок на телефоне больше не открываются: наведите камеру на новый код ниже. Профиль для iPhone тоже нужно сохранить и поставить заново.")
                : note;
        Result.Text = result;
        Result.Foreground = result == note ? Palette.Ink3 : Palette.Warn;
        Result.Visibility = result.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        AddressBlock.Visibility = s.Running ? Visibility.Visible : Visibility.Collapsed;
        ShowTrouble(s);
        if (!s.Running) { shownUrl = ""; return; }

        if (s.Url != shownUrl)
        {
            shownUrl = s.Url;
            Url.Text = s.Url;
            var code = QrCode.Encode(s.Url);
            Qr.Matrix = code;
            QrFrame.Visibility = code != null ? Visibility.Visible : Visibility.Collapsed;
            HowTo.Text = code != null
                ? L.T("Наведите камеру телефона на код или наберите адрес в браузере телефона целиком:")
                : L.T("Наберите адрес в браузере телефона целиком:");
        }
        Devices.Text = s.Devices == 0 ? L.T("пока никто") : Fmt.Plural(s.Devices, "устройство", "устройства", "устройств");
        Last.Text = s.LastRequest == default ? L.T("ещё не было") : Ago(s.LastRequest);
        Adapter.Text = s.Adapter;
    }

    /// <summary>
    /// «Не открывается на телефоне?»: то, что программа видит сама (брандмауэр, отклонённое устройство), — первым и цветом;
    /// ниже — что проверить на телефоне и в роутере.
    /// </summary>
    private void ShowTrouble(PhoneServer.State s)
    {
        if (s.Running && s.Devices == 0) PhoneServer.CheckFirewall();
        var found = new List<string>();
        if (s.Running && s.Firewall.Length > 0) found.Add(s.Firewall);
        if (s.Running && s.Rejected.Length > 0 && (DateTime.Now - s.RejectedAt).TotalMinutes < 30)
            found.Add(L.F("Устройство {0} пыталось открыть страницу {1}, но программа его не пустила: его адрес не из домашней сети. Так бывает, если на телефоне включён VPN или телефон подключён к гостевой сети Wi-Fi.", s.Rejected, Ago(s.RejectedAt)));
        if (s.Running && s.WiredOnly && s.Devices == 0 && (s.LastRequest == default || (DateTime.Now - s.LastRequest).TotalMinutes > 10))
            found.Add(L.T("Компьютер подключён к роутеру только кабелем. Если телефон не открывает страницу, а когда компьютер подключён по Wi-Fi — открывает, значит роутер не пускает устройства из Wi-Fi к компьютерам на кабеле. "
                          + "Подключите компьютер ещё и к Wi-Fi — программа сама покажет адрес Wi-Fi. Или разрешите это в настройках роутера: «изоляция клиентов» (AP isolation) должна быть выключена."));
        TroubleFound.Text = string.Join("\n\n", found);
        TroubleBlock.Visibility = found.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Ago(DateTime at)
    {
        var passed = DateTime.Now - at;
        if (passed.TotalSeconds < 5) return L.T("только что");
        if (passed.TotalSeconds < 60) return L.F("{0} с назад", (int)passed.TotalSeconds);
        if (passed.TotalMinutes < 60) return L.F("{0} мин назад", (int)passed.TotalMinutes);
        return at.Date == DateTime.Today ? L.F("сегодня в {0:HH:mm}", at) : L.F("{0:d MMM} в {0:HH:mm}", at);
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
        try { Clipboard.SetText(shownUrl); note = L.T("Адрес скопирован."); }
        catch { note = L.T("Скопировать не получилось: буфер обмена занят другой программой. Попробуйте ещё раз."); }
        Refresh();
    }

    /// <summary>Профиль для iPhone на рабочий стол: его ставят через iMazing, Apple Configurator или открывают на телефоне.</summary>
    private void BtnIphone_Click(object sender, RoutedEventArgs e)
    {
        var bytes = PhoneServer.CurrentProfile();
        if (bytes == null) { note = L.T("Сначала включите панель."); Refresh(); return; }
        try
        {
            string name = L.T("Пульс ПК для iPhone.mobileconfig");
            string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name);
            File.WriteAllBytes(file, bytes);
            note = L.F("Профиль сохранён на рабочем столе: «{0}». Установите его через iMazing («Профили» → «Установить») или откройте на iPhone адрес панели с «pulse.mobileconfig» на конце. На телефоне: «Настройки» → «Профиль загружен» → «Установить».", name);
        }
        catch (Exception ex) { note = L.F("Не удалось сохранить профиль: {0}", ex.Message); }
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
            note = L.T("Ключ сменён: прежний адрес больше не открывается. На телефоне откройте новый.");
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
