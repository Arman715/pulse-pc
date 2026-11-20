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
            State.Text = "Не удалось прочитать состояние обновлений.";
            NowList.ItemsSource = null;
            GuardToggle.IsEnabled = false; NoRebootToggle.IsEnabled = false;
            return;
        }

        bool holding = st.InGame && (st.Services.Any(x => x.Held) || st.HoursShifted);
        State.Text = holding ? "Идёт игра: обновления приостановлены."
            : st.Guard && st.NoReboot ? "В игре обновления и перезагрузки ставятся на паузу."
            : st.Guard ? "В игре обновления ставятся на паузу."
            : st.NoReboot ? "В игре Windows не перезагрузит компьютер."
            : "Защита от обновлений во время игры выключена.";

        GuardToggle.IsChecked = st.Guard;
        NoRebootToggle.IsChecked = st.NoReboot;
        GuardToggle.IsEnabled = host.Admin;
        // Период задан политикой организации: значения пользователя не действуют, переключатель был бы обманом.
        NoRebootToggle.IsEnabled = host.Admin && !st.HoursPolicy;

        if (st.Last == null) { LastText.Text = "Ещё не было."; LastWhen.Text = ""; }
        else { LastText.Text = st.LastText; LastWhen.Text = When(st.Last.Value); }

        var now = new List<Fact>();
        foreach (var x in st.Services) now.Add(new Fact { Title = x.Title, Hint = x.Hint, Value = x.Value });

        bool loaderOff = st.Services.Any(x => x.Name == "DoSvc" && x.State is "stopped" or "missing");
        now.Add(new Fact
        {
            Title = "Загрузка обновлений",
            Hint = st.Downloading < 0 ? "Windows не ответила, качает ли она что-нибудь."
                : st.Downloading == 0 ? (loaderOff ? "Служба загрузки не запущена, Windows сейчас ничего не качает." : "Windows сейчас ничего не качает.")
                : $"Windows качает {Fmt.Plural(st.Downloading, "файл", "файла", "файлов")}, осталось {Fmt.Size(st.DownloadLeftBytes)}. В игре это отнимает скорость интернета и нагружает диск.",
            Value = st.Downloading < 0 ? "не удалось узнать" : st.Downloading == 0 ? "не идёт" : "идёт",
        });
        now.Add(new Fact
        {
            Title = "Перезагрузка для установки",
            Hint = st.RebootPending ? "Обновления скачаны и ждут перезагрузки. Перезагрузите компьютер сами, когда будет удобно, — тогда Windows не выберет время за вас."
                : "Для установки обновлений перезагрузка не нужна.",
            Value = st.RebootPending ? "нужна" : "не нужна",
        });
        now.Add(new Fact
        {
            Title = "Период активности",
            Hint = "В эти часы Windows не перезагружает компьютер ради обновлений."
                   + (st.HoursShifted ? $" Сейчас сдвинут на время игры; был {st.HoursWas}." : "")
                   + (st.HoursPolicy ? " Задан политикой организации — программа его не меняет." : ""),
            Value = st.HoursStart == null || st.HoursEnd == null ? "не задан" : $"{st.HoursStart:00}:00–{st.HoursEnd:00}:00",
        });
        NowList.ItemsSource = now;
    }

    private void GuardToggle_Click(object sender, RoutedEventArgs e)
    {
        if (host == null || !host.Admin) return;
        bool on = GuardToggle.IsChecked == true;
        UpdateGuard.SetGuard(host, on);
        Say(!on ? "Службы обновлений во время игры больше не останавливаются." + Tail()
            : "Включено. Службы обновлений будут останавливаться " + (GameHooks.InGame ? "со следующего запуска игры." : "при запуске игры и запускаться после неё."));
    }

    private void NoRebootToggle_Click(object sender, RoutedEventArgs e)
    {
        if (host == null || !host.Admin) return;
        bool on = NoRebootToggle.IsChecked == true;
        UpdateGuard.SetNoReboot(host, on);
        Say(!on ? "Период активности на время игры больше не сдвигается." + Tail()
            : "Включено. Период активности будет сдвигаться " + (GameHooks.InGame ? "со следующего запуска игры." : "при запуске игры и возвращаться после неё."));
    }

    /// <summary>Итог действия под переключателями; пока сказать нечего, строка места не занимает.</summary>
    private void Say(string text)
    {
        Result.Text = text;
        Result.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    // Выключили посреди игры: то, что уже остановлено и сдвинуто, вернётся, когда игра закроется.
    private static string Tail() => GameHooks.InGame ? " То, что уже приостановлено, вернётся, когда вы закроете игру." : "";

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>«сегодня, 21:40», «вчера, 09:05», «18 ноября, 21:40».</summary>
    private static string When(DateTime t)
    {
        int days = (DateTime.Today - t.Date).Days;
        return days == 0 ? "сегодня, " + t.ToString("HH:mm") : days == 1 ? "вчера, " + t.ToString("HH:mm") : t.ToString("d MMMM, HH:mm", Ru);
    }
}
