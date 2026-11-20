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
            catch (Exception ex) { st = new PagefileState { Error = "Не удалось прочитать настройку файла подкачки: " + ex.Message }; }
            host.Ui(() => Show(st));
        });
    }

    private void Show(PagefileState st)
    {
        state = st;
        if (!st.Loaded)
        {
            State.Text = "Не удалось прочитать настройку файла подкачки.";
            NowList.ItemsSource = null; GamesList.ItemsSource = null;
            GamesEmpty.Visibility = Visibility.Collapsed;
            AdviceMark.Level = Severity.Info;
            AdviceTitle.Text = "Рекомендации нет";
            AdviceWhy.Text = st.Error + " Откройте раздел ещё раз чуть позже.";
            MemoryBlock.Visibility = Visibility.Collapsed;
            BtnApply.IsEnabled = false; BtnRestore.IsEnabled = false;
            return;
        }

        State.Text = st.Advice.Verdict;

        var now = new List<Fact>
        {
            new()
            {
                Title = "Кто задаёт размер",
                Hint = st.Auto ? "Windows сама увеличивает файл, когда памяти не хватает, и уменьшает, когда он не нужен."
                    : st.Files.Any(f => f.HasSetting) ? "Размер выбран вручную в настройках Windows." : "Файл подкачки выключен в настройках Windows.",
                Value = st.Auto ? "Windows" : st.Files.Any(f => f.HasSetting) ? "вручную" : "выключен",
            },
        };
        foreach (var f in st.Files)
        {
            var d = st.DriveOf(f);
            string disk = d == null ? "Диск не найден." : d.DiskName == "" ? Cap(d.KindText) + "." : $"{Cap(d.KindText)}, {d.DiskName}.";
            string set = !f.HasSetting || st.Auto ? "" : f.MaximumMb == 0 ? " Размер на этом диске выбирает Windows." : $" Задано от {Pagefile.Mb(f.InitialMb)} до {Pagefile.Mb(f.MaximumMb)}.";
            string use = f.InUse ? $" Занято {Pagefile.Mb(f.UsedMb)}, с включения компьютера — до {Pagefile.Mb(f.PeakMb)}." : " Появится после перезагрузки.";
            now.Add(new Fact { Title = f.Path, Hint = disk + set + use, Value = f.InUse ? Pagefile.Mb(f.AllocatedMb) : "—" });
        }
        now.Add(new Fact
        {
            Title = "Память компьютера",
            Hint = st.CommitLimitMb > 0 ? $"Вместе с файлом подкачки программам доступно до {Pagefile.Mb(st.CommitLimitMb)}, сейчас свободно {Pagefile.Mb(st.CommitFreeMb)}." : "",
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
                    Title = "Меньше всего свободной памяти",
                    Hint = g.Tight == 0 ? $"За {Fmt.Plural(g.Sessions, "последнюю игру", "последние игры", "последних игр")} свободной памяти ни разу не оставалось меньше 2 ГБ."
                        : $"Меньше 2 ГБ оставалось в {g.Tight} из {Fmt.Plural(g.Sessions, "последней игры", "последних игр", "последних игр")}. В такие минуты Windows убирает данные в файл подкачки.",
                    Value = Fmt.Gb(g.MinFreeGb),
                },
            };
            if (g.Analysed > 0)
            {
                games.Add(new Fact
                {
                    Title = "Игры, где шла подкачка",
                    Hint = "Во время игры Windows много читала с диска то, что не поместилось в память. В игре это рывки и подгрузки.",
                    Value = $"{g.Paged} из {g.Analysed}",
                });
                games.Add(new Fact
                {
                    Title = "Просадки FPS из-за памяти",
                    Hint = "Резкие падения FPS в моменты, когда память была на исходе.",
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
        Run(() => Pagefile.Apply(plan), "Записываю настройку…");
    }

    private void BtnRestore_Click(object sender, RoutedEventArgs e) => Run(Pagefile.Restore, "Возвращаю прежнюю настройку…");

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
            catch (Exception ex) { r = (false, "Не удалось: " + ex.Message); }
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
