using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PulsePC.Services;

namespace PulsePC.Panels;

/// <summary>Ссылка под строкой версии: сайт производителя или программа (NVIDIA App).</summary>
public sealed class VersionLink
{
    public string Label { get; set; } = "";
    public string Url { get; set; } = "";
}

/// <summary>Строка таблицы «Новые версии».</summary>
public sealed class VersionRowView
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Installed { get; set; } = "—";
    public string InstalledNote { get; set; } = "";
    public string Latest { get; set; } = "—";
    public string LatestNote { get; set; } = "";
    public string Verdict { get; set; } = "";
    public Severity Level { get; set; } = Severity.Info;
    public Brush LatestInk { get; set; } = Palette.Ink1;
    public Brush VerdictInk { get; set; } = Palette.Ink2;
    public List<VersionLink> Links { get; set; } = new();
    public bool HasLinks => Links.Count > 0;
}

/// <summary>
/// Раздел «Новые версии»: драйвер видеокарты, BIOS и «Пульс ПК» — что установлено, что последнее, что делать.
/// Проверка по сети — в фоне (раз в сутки или кнопкой); открытие раздела ничего не меняет в системе.
/// Обновление программы — по кнопке: скачать, сверить SHA-256, запустить установщик и выйти.
/// </summary>
public partial class VersionsPanel : UserControl, ISystemPanel
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private PanelHost host;
    private int generation;
    private bool checkingHere, updating, feedLoaded;

    public VersionsPanel()
    {
        InitializeComponent();
        VersionCheck.Changed += () => host?.Ui(() => { if (!checkingHere) Shown(); });
    }

    public void Attach(PanelHost h)
    {
        host = h;
        bool admin = h.Admin;
        AutoToggle.IsEnabled = FeedBox.IsEnabled = BtnUpdate.IsEnabled = admin;
        AdminNote.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Shown()
    {
        if (host == null) return;
        int gen = ++generation;
        var hw = host.Hardware();
        Task.Run(() =>
        {
            var gpu = VersionCheck.SafeMainGpu();
            var bios = Safe(() => VersionCheck.ReadBios(hw)) ?? new BiosInfo();
            string nvApp = Safe(VersionCheck.NvidiaAppPath);
            host.Ui(() => { if (gen == generation) Show(gpu, bios, nvApp); });
        });
        // Раз в сутки — и при открытии раздела, если плановая проверка ещё не успела.
        if (VersionCheck.AutoCheck(host.Settings) && VersionCheck.Due(host.Settings) && !VersionCheck.Checking && !checkingHere) StartCheck();
    }

    private static T Safe<T>(Func<T> read)
    {
        try { return read(); } catch { return default; }
    }

    // ================================================================== показ

    private void Show(GpuDriverInfo gpu, BiosInfo bios, string nvApp)
    {
        var s = host.Settings;
        var driver = VersionCheck.DriverStatusFor(gpu, s);
        var app = VersionCheck.AppStatus(s);
        Rows.ItemsSource = new[] { DriverRow(gpu, driver, nvApp), BiosRow(bios), AppRow(app) };

        bool busy = checkingHere || VersionCheck.Checking;
        var last = VersionCheck.LastChecked(s);
        CheckedText.Text = busy ? "проверяю…" : last is DateTime t ? "проверено " + VersionCheck.When(t) : "ещё не проверялось";
        BtnCheck.IsEnabled = !busy;

        bool newDriver = driver.Newer, newApp = app.Release != null && app.Diff > 0;
        bool driverKnown = driver.Checked, appKnown = app.Release != null;
        bool anyError = driver.Checkable && !driver.Checked && driver.Error != "" || app.Kind is FeedKind.Manifest or FeedKind.GitHub && app.Release == null && app.Error != "";
        State.Text =
            newDriver && newApp ? "Вышли новый драйвер видеокарты и новая версия программы"
            : newDriver ? $"Вышел драйвер видеокарты {driver.Latest.Version}"
            : newApp ? $"Вышла новая версия «Пульс ПК» — {app.Release.Version}"
            : busy && !driverKnown && !appKnown ? "Проверяю версии…"
            : driverKnown && appKnown ? "Драйвер видеокарты и программа — последних версий"
            : driverKnown ? "Драйвер видеокарты последней версии"
            : appKnown ? "Программа последней версии"
            : anyError ? "Проверить новые версии не удалось"
            : !driver.Checkable && app.Kind == FeedKind.None ? "Новые версии — на сайтах производителей"
            : "Новые версии ещё не проверялись";

        // Обновление программы
        UpdateBlock.Visibility = newApp ? Visibility.Visible : Visibility.Collapsed;
        if (newApp)
        {
            UpdateLabel.Source = $"ПУЛЬС ПК {app.Release.Version}: ЧТО НОВОГО";
            NotesList.ItemsSource = app.Release.Notes.Count > 0 ? app.Release.Notes : new List<string> { "Описание изменений источник не прислал." };
            BtnUpdate.IsEnabled = host.Admin && !updating;
        }

        AutoToggle.IsChecked = VersionCheck.AutoCheck(s);
        FeedHint.Text = "Откуда программа узнаёт о своих новых версиях: адрес файла update.json (https://…/update.json) или репозиторий GitHub в виде «владелец/репозиторий»."
                        + $" Сам установщик скачивается всегда из официальных выпусков {VersionCheck.DefaultFeed} на GitHub и ставится, только если совпали контрольная сумма и подпись автора."
                        + (app.Kind == FeedKind.None ? " Пока источник не задан, новые версии нужно скачивать самому." : " Чтобы убрать источник, очистите поле и сохраните.");
        if (!feedLoaded || !FeedBox.IsKeyboardFocused && !BtnFeed.IsEnabled)
        {
            FeedBox.Text = VersionCheck.Feed(s);
            feedLoaded = true;
            BtnFeed.IsEnabled = false;
        }
    }

    private static string Day(DateTime? d) => d is DateTime x ? "от " + x.ToString("dd.MM.yyyy", Ru) : "";

    private VersionRowView DriverRow(GpuDriverInfo gpu, DriverStatus st, string nvApp)
    {
        var row = new VersionRowView { Title = "Драйвер видеокарты" };
        if (gpu == null)
        {
            row.Subtitle = "видеокарта не найдена";
            row.Verdict = "Windows не сообщила о видеокарте с драйвером производителя. Подробности — в разделе «Драйвер».";
            return row;
        }
        row.Subtitle = gpu.Name;
        row.Installed = gpu.Version == "" ? "—" : gpu.Version;
        // Дата в Windows — дата сборки драйвера; NVIDIA публикует его на несколько дней позже.
        row.InstalledNote = gpu.Date is DateTime built ? "сборка " + built.ToString("dd.MM.yyyy", Ru) : "";
        string page = GpuDriver.DownloadUrl(gpu.Vendor);

        if (!st.Checkable)
        {
            string who = gpu.Vendor == "" ? "производителя видеокарты" : gpu.Vendor;
            row.LatestNote = "на сайте";
            row.Verdict = (gpu.AgeDays >= 0 ? $"Драйверу {gpu.AgeText}. " : "")
                          + $"Новые версии программа узнаёт только у NVIDIA, у {who} открытого способа для этого нет. Сверьте номер версии на сайте производителя.";
            if (page != "") row.Links.Add(new VersionLink { Label = "Страница драйверов " + gpu.Vendor, Url = page });
            return row;
        }

        if (st.Checked)
        {
            var l = st.Latest;
            row.Latest = l.Version;
            row.LatestNote = (l.Date is DateTime out_ ? "вышел " + out_.ToString("dd.MM.yyyy", Ru) : "") + (l.Beta ? ", бета" : "");
            string stale = st.Error != "" && st.CheckedAt is DateTime at ? $" Последняя проверка {VersionCheck.When(at)} не удалась ({st.Error}), здесь — прежний ответ NVIDIA." : "";
            if (st.Newer)
            {
                row.Level = Severity.Warn;
                row.LatestInk = Palette.Warn;
                row.Verdict = "Вышла новая версия. Обновлять не обязательно: стоит, если её требует игра или в ней исправили вашу проблему — это написано в «Что нового». Программа драйвер не скачивает и не ставит." + stale;
                if (l.DetailsUrl != "") row.Links.Add(new VersionLink { Label = $"Что нового в {l.Version}", Url = l.DetailsUrl });
                if (nvApp != null) row.Links.Add(new VersionLink { Label = "Открыть NVIDIA App", Url = nvApp });
                row.Links.Add(new VersionLink { Label = "Скачать на сайте NVIDIA", Url = l.DownloadUrl != "" ? l.DownloadUrl : page });
            }
            else
            {
                row.Level = Severity.Ok;
                row.Verdict = (st.Diff == 0
                    ? "Установлена последняя версия — обновлять нечего."
                    : "Установлена версия новее той, что NVIDIA предлагает для обычной установки (например, бета). Ничего делать не нужно.") + stale;
                if (l.DetailsUrl != "") row.Links.Add(new VersionLink { Label = $"Что нового в {l.Version}", Url = l.DetailsUrl });
            }
            return row;
        }

        if (st.Error != "")
        {
            row.Verdict = $"Сервис NVIDIA не ответил: {st.Error}. Программа попробует снова через сутки, а можно нажать «Проверить сейчас».";
        }
        else row.Verdict = gpu.Version == "" ? "Номер установленной версии Windows не сообщила." : "Ещё не проверялось: нажмите «Проверить сейчас».";
        row.Links.Add(new VersionLink { Label = "Страница драйверов NVIDIA", Url = page });
        return row;
    }

    private static VersionRowView BiosRow(BiosInfo b)
    {
        string board = b.Laptop && b.SystemModel != "" ? (b.Maker + " " + b.SystemModel).Trim() : (b.Maker + " " + b.Model).Trim();
        var row = new VersionRowView
        {
            Title = "BIOS",
            Subtitle = board == "" ? "модель платы не определена" : (b.Laptop ? "ноутбук " : "плата ") + board,
            Installed = b.Version == "" ? "—" : b.Version,
            InstalledNote = Day(b.Date),
            LatestNote = "на сайте",
        };
        string age = b.Date is DateTime d ? $"Выпущен {d.ToString("d MMMM yyyy", Ru)} года" + (b.AgeDays > 0 ? $", {VersionCheck.BiosAge(b)} назад. " : ". ") : "";
        row.Verdict = age + "Последнюю версию программа не проверяет: сайты производителей плат не отвечают программам. "
                      + (b.Version != "" ? $"Сравните «{b.Version}» с самой новой версией на странице платы." : "Версию BIOS Windows не сообщила.");
        if (b.PageUrl != "") row.Links.Add(new VersionLink { Label = b.PageTitle, Url = b.PageUrl });
        if (b.SearchUrl != "") row.Links.Add(new VersionLink { Label = b.PageUrl != "" ? "Найти в интернете" : "Найти страницу платы в интернете", Url = b.SearchUrl });
        return row;
    }

    private static VersionRowView AppRow((FeedKind Kind, AppRelease Release, int Diff, string Error, DateTime? CheckedAt) a)
    {
        var row = new VersionRowView { Title = "Пульс ПК", Subtitle = "эта программа", Installed = Installer.Version };
        switch (a.Kind)
        {
            case FeedKind.None:
                row.LatestNote = "источник не задан";
                row.Verdict = "Источник обновлений не настроен. Пока новые версии программы нужно скачивать самому.";
                return row;
            case FeedKind.Invalid:
                row.Level = Severity.Warn;
                row.VerdictInk = Palette.Warn;
                row.Verdict = "Источник обновлений записан непонятно — исправьте его ниже, в «Проверке».";
                return row;
        }
        if (a.Release == null)
        {
            row.Verdict = a.Error != "" ? $"Источник обновлений не ответил: {a.Error}." : "Ещё не проверялось: нажмите «Проверить сейчас».";
            return row;
        }
        row.Latest = a.Release.Version;
        row.LatestNote = DateTime.TryParseExact(a.Release.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? Day(d) : "";
        if (a.Diff > 0)
        {
            row.Level = Severity.Warn;
            row.LatestInk = Palette.Warn;
            row.Verdict = "Вышла новая версия. Что в ней изменилось и кнопка «Обновить» — ниже.";
        }
        else
        {
            row.Level = Severity.Ok;
            row.Verdict = "Установлена последняя версия.";
        }
        return row;
    }

    // ================================================================== проверка

    private void StartCheck()
    {
        if (checkingHere) return;
        checkingHere = true;
        BtnCheck.IsEnabled = false;
        CheckedText.Text = "проверяю…";
        host.BeginBusy();
        var s = host.Settings;
        Task.Run(() =>
        {
            try { VersionCheck.Check(s); }
            catch { }
            finally
            {
                host.EndBusy();
                host.Ui(() => { checkingHere = false; Shown(); });
            }
        });
    }

    private void BtnCheck_Click(object sender, RoutedEventArgs e)
    {
        if (host == null) return;
        Result.Text = "";
        StartCheck();
    }

    // ================================================================== настройки

    private void AutoToggle_Click(object sender, RoutedEventArgs e)
    {
        if (host == null || !host.Admin) return;
        host.Settings.SetBool("versions.auto", AutoToggle.IsChecked == true);
        VersionCheck.SaveSettings(host.Settings);
        Result.Text = AutoToggle.IsChecked == true ? "Программа будет проверять новые версии сама раз в сутки." : "Сама программа больше не проверяет новые версии — только по кнопке «Проверить сейчас».";
    }

    private void FeedBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (host == null || !feedLoaded) return;
        BtnFeed.IsEnabled = host.Admin && FeedBox.Text.Trim() != VersionCheck.Feed(host.Settings);
        FeedResult.Text = "";
    }

    private void FeedBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && BtnFeed.IsEnabled) { BtnFeed_Click(sender, e); e.Handled = true; }
    }

    private void BtnFeed_Click(object sender, RoutedEventArgs e)
    {
        if (host == null || !host.Admin) return;
        string value = FeedBox.Text.Trim();
        var kind = VersionCheck.Classify(value, out string norm);
        if (kind == FeedKind.Invalid)
        {
            FeedResult.Text = "Не понимаю такой источник. Нужен адрес https://…/update.json или «владелец/репозиторий» на GitHub. Файлы на диске и адреса http не принимаются.";
            FeedResult.Foreground = Palette.Warn;
            return;
        }
        if (kind == FeedKind.GitHub) value = norm;
        host.Settings.Set("update.feed", value == VersionCheck.DefaultFeed ? null : value);
        VersionCheck.SaveSettings(host.Settings);
        FeedBox.Text = value;
        BtnFeed.IsEnabled = false;
        FeedResult.Foreground = Palette.Ink3;
        if (kind == FeedKind.None)
        {
            FeedResult.Text = "Источник убран: программа не будет искать свои новые версии.";
            Shown();
        }
        else
        {
            FeedResult.Text = "Сохранено. Проверяю источник…";
            StartCheck();
        }
    }

    // ================================================================== обновление

    private void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (host == null || !host.Admin || updating) return;
        var rel = VersionCheck.AppStatus(host.Settings).Release;
        if (rel == null) return;
        if (GameHooks.InGame)
        {
            Result.Text = "Сейчас идёт игра, и программа её записывает. Обновите после игры: при обновлении программа закроется.";
            return;
        }
        updating = true;
        BtnUpdate.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.Target = 0;
        Result.Foreground = Palette.Ink3;
        Result.Text = "Сверяю с официальным выпуском на GitHub…";
        host.BeginBusy();
        string folder = VersionCheck.UpdateFolder;
        Task.Run(() =>
        {
            var lastUi = DateTime.MinValue;
            try
            {
                if (!VersionCheck.UpdateFolderProtected)
                {
                    host.Ui(() => Finish("Не удалось создать защищённую папку %ProgramData%\\PulsePC — скачивать установщик в папку пользователя небезопасно. Обновите программу вручную, скачав установщик с GitHub.", Palette.Warn));
                    return;
                }
                // Адрес и контрольную сумму берём не из settings.json (его может переписать любая программа), а заново из выпуска.
                var official = VersionCheck.QueryOfficial(out string error);
                if (official == null)
                {
                    host.Ui(() => Finish("Официальный выпуск на GitHub не ответил: " + error + ". Ничего не скачано.", Palette.Warn));
                    return;
                }
                if (VersionCheck.CompareVersions(official.Version, Installer.Version) <= 0)
                {
                    host.Ui(() => Finish($"В официальном выпуске на GitHub нет версии новее установленной {Installer.Version} (там {official.Version}). Ничего не скачано.", Palette.Ink3));
                    return;
                }
                host.Ui(() => Result.Text = $"Скачиваю установщик {official.Version}…");
                var d = VersionCheck.Download(official, folder, (done, total) =>
                {
                    if ((DateTime.UtcNow - lastUi).TotalMilliseconds < 200) return;
                    lastUi = DateTime.UtcNow;
                    host.Ui(() =>
                    {
                        Result.Text = total > 0 ? $"Скачано {Fmt.Size(done)} из {Fmt.Size(total)}…" : $"Скачано {Fmt.Size(done)}…";
                        Progress.Target = total > 0 ? (double)done / total : 0;
                    });
                });
                if (!d.Ok)
                {
                    host.Ui(() => Finish(d.Message, Palette.Warn));
                    return;
                }
                using var run = VersionCheck.Launch(d.Path, official);
                if (!run.Started)
                {
                    host.Ui(() => Finish(d.Message + " " + run.Message, Palette.Warn));
                    return;
                }
                host.Ui(() => Result.Text = d.Message + " " + run.Message);
                // Выходим, только когда окно установщика открылось (он сам подаёт знак), а не через фиксированное время.
                var wait = VersionCheck.WaitForInstaller(run);
                host.Ui(() => Finish(d.Message + " " + wait.Message, wait.Ready ? Palette.Ink1 : Palette.Warn));
                if (wait.Ready) VersionCheck.RequestExit();
            }
            catch (Exception ex) { host.Ui(() => Finish("Обновление не удалось: " + ex.Message, Palette.Warn)); }
            finally { host.EndBusy(); }
        });
    }

    private void Finish(string text, Brush ink)
    {
        updating = false;
        Progress.Visibility = Visibility.Collapsed;
        BtnUpdate.IsEnabled = host.Admin;
        Result.Text = text;
        Result.Foreground = ink;
    }

    // ================================================================== ссылки

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VersionLink link || link.Url == "") return;
        try { GameLibrary.OpenAsUser(link.Url); }
        catch { Result.Text = "Не открылось. Адрес: " + link.Url; }
    }
}
