using System.Windows;
using System.Windows.Controls;
using PulsePC.Services;

namespace PulsePC.Panels;

/// <summary>Раздел «Файл подкачки»: что настроено сейчас, хватало ли памяти в играх, вердикт и обратимая рекомендация.</summary>
public partial class PagefilePanel : UserControl, ISystemPanel
{
    /// <summary>Строка показаний: название, пояснение и число справа.</summary>
    public sealed class Fact
    {
        public string Title { get; set; } = "";
        public string Hint { get; set; } = "";
        public string Value { get; set; } = "";
    }

    private PanelHost host;
    private PagefileState state;
    private bool busy;

    public PagefilePanel() => InitializeComponent();

    public void Attach(PanelHost h)
    {
        host = h;
        AdminNote.Visibility = h.Admin ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Shown() => Refresh();

    /// <summary>Чтение состояния (в фоновом потоке). Вынесено, чтобы вид раздела можно было проверить на разных состояниях.</summary>
    protected virtual PagefileState Load(IReadOnlyList<GameSession> sessions) => Pagefile.Read(sessions);

    private void Refresh()
    {
        if (host == null) return;
        var sessions = host.Sessions;
        Task.Run(() =>
        {
            PagefileState st;
            try { st = Load(sessions()); }
            catch (Exception ex) { st = new PagefileState { Error = L.F("Не удалось прочитать настройку файла подкачки: {0}", ex.Message) }; }
            host.Ui(() => Show(st));
        });
    }

    private void Show(PagefileState st)
    {
        state = st;
        if (!st.Loaded)
        {
            State.Text = L.T("Не удалось прочитать настройку файла подкачки.");
            NowList.ItemsSource = null; GamesList.ItemsSource = null;
            GamesEmpty.Visibility = Visibility.Collapsed;
            AdviceMark.Level = Severity.Info;
            AdviceTitle.Text = L.T("Рекомендации нет");
            AdviceWhy.Text = st.Error + " " + L.T("Откройте раздел ещё раз чуть позже.");
            MemoryBlock.Visibility = Visibility.Collapsed;
            BtnApply.IsEnabled = false; BtnRestore.IsEnabled = false;
            return;
        }

        State.Text = st.Advice.Verdict;

        var now = new List<Fact>
        {
            new()
            {
                Title = L.T("Кто задаёт размер"),
                Hint = st.Auto ? L.T("Windows сама увеличивает файл, когда памяти не хватает, и уменьшает, когда он не нужен.")
                    : st.Files.Any(f => f.HasSetting) ? L.T("Размер выбран вручную в настройках Windows.") : L.T("Файл подкачки выключен в настройках Windows."),
                Value = st.Auto ? "Windows" : st.Files.Any(f => f.HasSetting) ? L.T("вручную") : L.T("выключен"),
            },
        };
        foreach (var f in st.Files)
        {
            var d = st.DriveOf(f);
            string disk = d == null ? L.T("Диск не найден.") : d.DiskName == "" ? Cap(d.KindText) + "." : $"{Cap(d.KindText)}, {d.DiskName}.";
            string set = !f.HasSetting || st.Auto ? "" : " " + (f.MaximumMb == 0 ? L.T("Размер на этом диске выбирает Windows.") : L.F("Задано от {0} до {1}.", Pagefile.Mb(f.InitialMb), Pagefile.Mb(f.MaximumMb)));
            string use = " " + (f.InUse ? L.F("Занято {0}, с включения компьютера — до {1}.", Pagefile.Mb(f.UsedMb), Pagefile.Mb(f.PeakMb)) : L.T("Появится после перезагрузки."));
            now.Add(new Fact { Title = f.Path, Hint = disk + set + use, Value = f.InUse ? Pagefile.Mb(f.AllocatedMb) : "—" });
        }
        now.Add(new Fact
        {
            Title = L.T("Память компьютера"),
            Hint = st.CommitLimitMb > 0 ? L.F("Вместе с файлом подкачки программам доступно до {0}, сейчас свободно {1}.", Pagefile.Mb(st.CommitLimitMb), Pagefile.Mb(st.CommitFreeMb)) : "",
            Value = Pagefile.Ram(st.RamGb),
        });
        NowList.ItemsSource = now;

        var g = st.Games;
        if (g.Sessions == 0)
        {
            GamesList.ItemsSource = null;
            GamesEmpty.Visibility = Visibility.Visible;
        }
        else
        {
            var games = new List<Fact>
            {
                new()
                {
                    Title = L.T("Меньше всего свободной памяти"),
                    Hint = g.Tight == 0 ? L.F("За {0} свободной памяти ни разу не оставалось меньше 2 ГБ.", Fmt.Plural(g.Sessions, "последнюю игру", "последние игры", "последних игр"))
                        : L.F("Меньше 2 ГБ оставалось в {0} из {1}. В такие минуты Windows убирает данные в файл подкачки.", g.Tight, Fmt.Plural(g.Sessions, "последней игры", "последних игр", "последних игр")),
                    Value = Fmt.Gb(g.MinFreeGb),
                },
            };
            if (g.Analysed > 0)
            {
                games.Add(new Fact
                {
                    Title = L.T("Игры, где шла подкачка"),
                    Hint = L.T("Во время игры Windows много читала с диска то, что не поместилось в память. В игре это рывки и подгрузки."),
                    Value = g.Paged + L.Pick(" из ", " of ") + g.Analysed,
                });
                games.Add(new Fact
                {
                    Title = L.T("Просадки FPS из-за памяти"),
                    Hint = L.T("Резкие падения FPS в моменты, когда память была на исходе."),
                    Value = g.RamDips.ToString(),
                });
            }
            GamesList.ItemsSource = games;
            GamesEmpty.Visibility = Visibility.Collapsed;
        }

        AdviceMark.Level = st.Advice.Level;
        AdviceTitle.Text = st.Advice.Title;
        AdviceWhy.Text = st.Advice.Why;
        MemoryBlock.Visibility = st.MemoryNote == "" ? Visibility.Collapsed : Visibility.Visible;
        MemoryNote.Text = st.MemoryNote;
        Buttons();
    }

    private void Buttons()
    {
        bool can = host != null && host.Admin && !busy && state != null && state.Loaded;
        BtnApply.IsEnabled = can && state.Advice.Plan != null;
        BtnRestore.IsEnabled = can && state.Backup != null;
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        var plan = state?.Advice.Plan;
        if (plan == null) return;
        Run(() => Pagefile.Apply(plan), L.T("Записываю настройку…"));
    }

    private void BtnRestore_Click(object sender, RoutedEventArgs e) => Run(Pagefile.Restore, L.T("Возвращаю прежнюю настройку…"));

    private void Run(Func<(bool Ok, string Message)> action, string progress)
    {
        if (host == null || !host.Admin || busy) return;
        busy = true;
        Buttons();
        Result.Text = progress;
        host.BeginBusy();
        Task.Run(() =>
        {
            (bool Ok, string Message) r;
            try { r = action(); }
            catch (Exception ex) { r = (false, L.F("Не удалось: {0}", ex.Message)); }
            host.Ui(() =>
            {
                host.EndBusy();
                busy = false;
                Result.Text = r.Message;
                Refresh();
            });
        });
    }

    private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];
}
