using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PulsePC.Services;

namespace PulsePC.Panels;

/// <summary>Раздел «Обновления Windows»: пауза обновлений и перезагрузок на время игры, состояние служб сейчас.</summary>
public partial class UpdatesPanel : UserControl, ISystemPanel
{
    /// <summary>Строка показаний: название, пояснение и состояние справа.</summary>
    public sealed class Fact
    {
        public string Title { get; set; } = "";
        public string Hint { get; set; } = "";
        public string Value { get; set; } = "";
    }

    private PanelHost host;

    public UpdatesPanel() => InitializeComponent();

    public void Attach(PanelHost h)
    {
        host = h;
        AdminNote.Visibility = h.Admin ? Visibility.Collapsed : Visibility.Visible;
        // Началась или закончилась игра — показать, что остановлено и что возвращено.
        UpdateGuard.Changed += Refresh;
    }

    public void Shown() => Refresh();

    /// <summary>Чтение состояния (в фоновом потоке). Вынесено, чтобы вид раздела можно было проверить на разных состояниях.</summary>
    protected virtual UpdateState Load(AppSettings settings) => UpdateGuard.Read(settings);

    private void Refresh()
    {
        if (host == null) return;
        var settings = host.Settings;
        Task.Run(() =>
        {
            UpdateState st = null;
            try { st = Load(settings); } catch { }
            host.Ui(() => Show(st));
        });
    }

    private void Show(UpdateState st)
    {
        if (st == null)
        {
            State.Text = L.T("Не удалось прочитать состояние обновлений.");
            NowList.ItemsSource = null;
            GuardToggle.IsEnabled = false; NoRebootToggle.IsEnabled = false;
            return;
        }

        bool holding = st.InGame && (st.Services.Any(x => x.Held) || st.HoursShifted);
        State.Text = holding ? L.T("Идёт игра: обновления приостановлены.")
            : st.Guard && st.NoReboot ? L.T("В игре обновления и перезагрузки ставятся на паузу.")
            : st.Guard ? L.T("В игре обновления ставятся на паузу.")
            : st.NoReboot ? L.T("В игре Windows не перезагрузит компьютер.")
            : L.T("Защита от обновлений во время игры выключена.");

        GuardToggle.IsChecked = st.Guard;
        NoRebootToggle.IsChecked = st.NoReboot;
        GuardToggle.IsEnabled = host.Admin;
        // Период задан политикой организации: значения пользователя не действуют, переключатель был бы обманом.
        NoRebootToggle.IsEnabled = host.Admin && !st.HoursPolicy;

        if (st.Last == null) { LastText.Text = L.T("Ещё не было."); LastWhen.Text = ""; }
        else { LastText.Text = st.LastText; LastWhen.Text = When(st.Last.Value); }

        var now = new List<Fact>();
        foreach (var x in st.Services) now.Add(new Fact { Title = x.Title, Hint = x.Hint, Value = x.Value });

        bool loaderOff = st.Services.Any(x => x.Name == "DoSvc" && x.State is "stopped" or "missing");
        now.Add(new Fact
        {
            Title = L.T("Загрузка обновлений"),
            Hint = st.Downloading < 0 ? L.T("Windows не ответила, качает ли она что-нибудь.")
                : st.Downloading == 0 ? (loaderOff ? L.T("Служба загрузки не запущена, Windows сейчас ничего не качает.") : L.T("Windows сейчас ничего не качает."))
                : L.F("Windows качает {0}, осталось {1}. В игре это отнимает скорость интернета и нагружает диск.", Fmt.Plural(st.Downloading, "файл", "файла", "файлов"), Fmt.Size(st.DownloadLeftBytes)),
            Value = st.Downloading < 0 ? L.T("не удалось узнать") : st.Downloading == 0 ? L.T("не идёт") : L.T("идёт"),
        });
        now.Add(new Fact
        {
            Title = L.T("Перезагрузка для установки"),
            Hint = st.RebootPending ? L.T("Обновления скачаны и ждут перезагрузки. Перезагрузите компьютер сами, когда будет удобно, — тогда Windows не выберет время за вас.")
                : L.T("Для установки обновлений перезагрузка не нужна."),
            Value = st.RebootPending ? L.T("нужна") : L.T("не нужна"),
        });
        now.Add(new Fact
        {
            Title = L.T("Период активности"),
            Hint = L.T("В эти часы Windows не перезагружает компьютер ради обновлений.")
                   + (st.HoursShifted ? " " + L.F("Сейчас сдвинут на время игры; был {0}.", st.HoursWas) : "")
                   + (st.HoursPolicy ? " " + L.T("Задан политикой организации — программа его не меняет.") : ""),
            Value = st.HoursStart == null || st.HoursEnd == null ? L.T("не задан") : $"{st.HoursStart:00}:00–{st.HoursEnd:00}:00",
        });
        NowList.ItemsSource = now;
    }

    private void GuardToggle_Click(object sender, RoutedEventArgs e)
    {
        if (host == null || !host.Admin) return;
        bool on = GuardToggle.IsChecked == true;
        UpdateGuard.SetGuard(host, on);
        Say(!on ? L.T("Службы обновлений во время игры больше не останавливаются.") + Tail()
            : GameHooks.InGame ? L.T("Включено. Службы обновлений будут останавливаться со следующего запуска игры.")
            : L.T("Включено. Службы обновлений будут останавливаться при запуске игры и запускаться после неё."));
    }

    private void NoRebootToggle_Click(object sender, RoutedEventArgs e)
    {
        if (host == null || !host.Admin) return;
        bool on = NoRebootToggle.IsChecked == true;
        UpdateGuard.SetNoReboot(host, on);
        Say(!on ? L.T("Период активности на время игры больше не сдвигается.") + Tail()
            : GameHooks.InGame ? L.T("Включено. Период активности будет сдвигаться со следующего запуска игры.")
            : L.T("Включено. Период активности будет сдвигаться при запуске игры и возвращаться после неё."));
    }

    /// <summary>Итог действия под переключателями; пока сказать нечего, строка места не занимает.</summary>
    private void Say(string text)
    {
        Result.Text = text;
        Result.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    // Выключили посреди игры: то, что уже остановлено и сдвинуто, вернётся, когда игра закроется.
    private static string Tail() => GameHooks.InGame ? " " + L.T("То, что уже приостановлено, вернётся, когда вы закроете игру.") : "";

    /// <summary>«сегодня, 21:40», «вчера, 09:05», «18 ноября, 21:40».</summary>
    private static string When(DateTime t)
    {
        int days = (DateTime.Today - t.Date).Days;
        return days == 0 ? L.F("сегодня, {0:HH:mm}", t) : days == 1 ? L.F("вчера, {0:HH:mm}", t)
            : t.ToString(L.En ? "MMMM d, HH:mm" : "d MMMM, HH:mm", L.Culture);
    }
}
