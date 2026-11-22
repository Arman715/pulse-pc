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
    private PanelHost host;
    private int generation;
    private bool checkingHere, updating, feedLoaded;

    public VersionsPanel()
    {
        InitializeComponent();
        // Ключ «Обновить» в словаре — «Refresh» (обновить список); здесь это установка новой версии — «Update».
        BtnUpdate.Content = L.Pick("Обновить", "Update");
        VersionCheck.Changed += () => host?.Ui(() => { if (!checkingHere) Shown(); });
    }

    public void Attach(PanelHost h)
    {
        host = h;
        bool admin = h.Admin;
        AutoToggle.IsEnabled = FeedBox.IsEnabled = BtnUpdate.IsEnabled = admin;
        AdminNote.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
        if (Portable.Enabled) AdminNote.Text = L.T("Это переносная версия: история и настройки — в папке Data рядом с программой, новые версии ставятся вручную.");
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
        CheckedText.Text = busy ? L.T("проверяю…") : last is DateTime t ? L.F("проверено {0}", VersionCheck.When(t)) : L.T("ещё не проверялось");
        BtnCheck.IsEnabled = !busy;

        bool newDriver = driver.Newer, newApp = app.Release != null && app.Diff > 0;
        bool driverKnown = driver.Checked, appKnown = app.Release != null;
        bool anyError = driver.Checkable && !driver.Checked && driver.Error != "" || app.Kind is FeedKind.Manifest or FeedKind.GitHub && app.Release == null && app.Error != "";
        State.Text =
            newDriver && newApp ? L.T("Вышли новый драйвер видеокарты и новая версия программы")
            : newDriver ? L.F("Вышел драйвер видеокарты {0}", driver.Latest.Version)
            : newApp ? L.F("Вышла новая версия «Пульс ПК» — {0}", app.Release.Version)
            : busy && !driverKnown && !appKnown ? L.T("Проверяю версии…")
            : driverKnown && appKnown ? L.T("У драйвера видеокарты и программы последние версии")
            : driverKnown ? L.T("Драйвер видеокарты последней версии")
            : appKnown ? L.T("Программа последней версии")
            : anyError ? L.T("Проверить новые версии не удалось")
            : !driver.Checkable && app.Kind == FeedKind.None ? L.T("Новые версии — на сайтах производителей")
            : L.T("Новые версии ещё не проверялись");

        // Обновление программы
        var auto = VersionCheck.AutoState(host.Admin);
        UpdateBlock.Visibility = newApp ? Visibility.Visible : Visibility.Collapsed;
        if (newApp)
        {
            UpdateLabel.Source = L.F("ПУЛЬС ПК {0}: ЧТО НОВОГО", app.Release.Version);
            NotesList.ItemsSource = app.Release.Notes.Count > 0 ? app.Release.Notes : new List<string> { L.T("Описание изменений источник не прислал.") };
            BtnUpdate.IsEnabled = host.Admin && !updating;
            UpdateButtons.Visibility = Portable.Enabled ? Visibility.Collapsed : Visibility.Visible;
            PortableButtons.Visibility = Portable.Enabled ? Visibility.Visible : Visibility.Collapsed;
        }
        // Один вопрос: при первой найденной новой версии, если ставить самой здесь вообще можно.
        AskBlock.Visibility = newApp && auto.Ok && !VersionCheck.AutoAsked(s) ? Visibility.Visible : Visibility.Collapsed;
        ShowAutoInstall(s, auto, newApp ? app.Release.Version : null);
        ShowWhatsNew(s);

        AutoToggle.IsChecked = VersionCheck.AutoCheck(s);
        FeedHint.Text = L.T("Откуда программа узнаёт о своих новых версиях: адрес файла update.json (https://…/update.json) или репозиторий GitHub в виде «владелец/репозиторий».")
                        + " " + L.F("Сам установщик скачивается всегда из официальных выпусков {0} на GitHub и ставится, только если совпали контрольная сумма и подпись автора.", VersionCheck.DefaultFeed)
                        + " " + (app.Kind == FeedKind.None ? L.T("Пока источник не задан, новые версии нужно скачивать самому.") : L.T("Чтобы убрать источник, очистите поле и сохраните."));
        if (!feedLoaded || !FeedBox.IsKeyboardFocused && !BtnFeed.IsEnabled)
        {
            FeedBox.Text = VersionCheck.Feed(s);
            feedLoaded = true;
            BtnFeed.IsEnabled = false;
        }
    }

    private static string Day(DateTime? d) => d is DateTime x ? L.F("от {0}", Date(x)) : "";

    /// <summary>Дата цифрами в культуре языка: 17.11.2026 / 11/17/2026.</summary>
    private static string Date(DateTime d) => d.ToString("d", L.Culture);

    private VersionRowView DriverRow(GpuDriverInfo gpu, DriverStatus st, string nvApp)
    {
        var row = new VersionRowView { Title = L.T("Драйвер видеокарты") };
        if (gpu == null)
        {
            row.Subtitle = L.T("видеокарта не найдена");
            row.Verdict = L.T("Windows не сообщила о видеокарте с драйвером производителя. Подробности — в разделе «Драйвер».");
            return row;
        }
        row.Subtitle = gpu.Name;
        row.Installed = gpu.Version == "" ? "—" : gpu.Version;
        // Дата в Windows — дата сборки драйвера; NVIDIA публикует его на несколько дней позже.
        row.InstalledNote = gpu.Date is DateTime built ? L.F("сборка {0}", Date(built)) : "";
        string page = GpuDriver.DownloadUrl(gpu.Vendor);

        if (!st.Checkable)
        {
            row.LatestNote = L.T("на сайте");
            row.Verdict = (gpu.AgeDays >= 0 ? L.F("Драйверу {0}.", gpu.AgeText) + " " : "")
                          + (gpu.Vendor == ""
                              ? L.T("Новые версии программа узнаёт только у NVIDIA, у производителя видеокарты открытого способа для этого нет. Сверьте номер версии на сайте производителя.")
                              : L.F("Новые версии программа узнаёт только у NVIDIA, у {0} открытого способа для этого нет. Сверьте номер версии на сайте производителя.", gpu.Vendor));
            if (page != "") row.Links.Add(new VersionLink { Label = L.F("Страница драйверов {0}", gpu.Vendor), Url = page });
            return row;
        }

        if (st.Checked)
        {
            var l = st.Latest;
            row.Latest = l.Version;
            row.LatestNote = (l.Date is DateTime out_ ? L.F("вышел {0}", Date(out_)) : "") + (l.Beta ? L.T(", бета") : "");
            // Причина сбоя хранится в настройках по-русски (её же проверяет самопроверка) — переводится при показе.
            string stale = st.Error != "" && st.CheckedAt is DateTime at ? " " + L.F("Последняя проверка {0} не удалась ({1}), здесь — прежний ответ NVIDIA.", VersionCheck.When(at), L.T(st.Error)) : "";
            if (st.Newer)
            {
                row.Level = Severity.Warn;
                row.LatestInk = Palette.Warn;
                row.Verdict = L.T("Вышла новая версия. Обновлять не обязательно: стоит, если её требует игра или в ней исправили вашу проблему — это написано в «Что нового». Программа драйвер не скачивает и не ставит.") + stale;
                if (l.DetailsUrl != "") row.Links.Add(new VersionLink { Label = L.F("Что нового в {0}", l.Version), Url = l.DetailsUrl });
                if (nvApp != null) row.Links.Add(new VersionLink { Label = L.T("Открыть NVIDIA App"), Url = nvApp });
                row.Links.Add(new VersionLink { Label = L.T("Скачать на сайте NVIDIA"), Url = l.DownloadUrl != "" ? l.DownloadUrl : page });
            }
            else
            {
                row.Level = Severity.Ok;
                row.Verdict = (st.Diff == 0
                    ? L.T("Установлена последняя версия — обновлять нечего.")
                    : L.T("Установлена версия новее той, что NVIDIA предлагает для обычной установки (например, бета). Ничего делать не нужно.")) + stale;
                if (l.DetailsUrl != "") row.Links.Add(new VersionLink { Label = L.F("Что нового в {0}", l.Version), Url = l.DetailsUrl });
            }
            return row;
        }

        if (st.Error != "")
        {
            row.Verdict = L.F("Сервис NVIDIA не ответил: {0}. Программа попробует снова через сутки, а можно нажать «Проверить сейчас».", L.T(st.Error));
        }
        else row.Verdict = gpu.Version == "" ? L.T("Номер установленной версии Windows не сообщила.") : L.T("Ещё не проверялось: нажмите «Проверить сейчас».");
        row.Links.Add(new VersionLink { Label = L.T("Страница драйверов NVIDIA"), Url = page });
        return row;
    }

    private static VersionRowView BiosRow(BiosInfo b)
    {
        string board = b.Laptop && b.SystemModel != "" ? (b.Maker + " " + b.SystemModel).Trim() : (b.Maker + " " + b.Model).Trim();
        var row = new VersionRowView
        {
            Title = "BIOS",
            Subtitle = board == "" ? L.T("модель платы не определена") : b.Laptop ? L.F("ноутбук {0}", board) : L.F("плата {0}", board),
            Installed = b.Version == "" ? "—" : b.Version,
            InstalledNote = Day(b.Date),
            LatestNote = L.T("на сайте"),
        };
        string age = "";
        if (b.Date is DateTime d)
        {
            string date = d.ToString(L.En ? "MMMM d, yyyy" : "d MMMM yyyy", L.Culture);
            age = (b.AgeDays > 0 ? L.F("Выпущен {0} года, {1} назад.", date, VersionCheck.BiosAge(b)) : L.F("Выпущен {0} года.", date)) + " ";
        }
        row.Verdict = age + L.T("Последнюю версию программа не проверяет: сайты производителей плат не отвечают программам.") + " "
                      + (b.Version != "" ? L.F("Сравните «{0}» с самой новой версией на странице платы.", b.Version) : L.T("Версию BIOS Windows не сообщила."));
        if (b.PageUrl != "") row.Links.Add(new VersionLink { Label = b.PageTitle, Url = b.PageUrl });
        if (b.SearchUrl != "") row.Links.Add(new VersionLink { Label = b.PageUrl != "" ? L.T("Найти в интернете") : L.T("Найти страницу платы в интернете"), Url = b.SearchUrl });
        return row;
    }

    private static VersionRowView AppRow((FeedKind Kind, AppRelease Release, int Diff, string Error, DateTime? CheckedAt) a)
    {
        var row = new VersionRowView { Title = L.T("Пульс ПК"), Subtitle = Portable.Enabled ? L.T("эта программа, переносная версия") : L.T("эта программа"), Installed = Installer.Version };
        switch (a.Kind)
        {
            case FeedKind.None:
                row.LatestNote = L.T("источник не задан");
                row.Verdict = L.T("Источник обновлений не настроен. Пока новые версии программы нужно скачивать самому.");
                return row;
            case FeedKind.Invalid:
                row.Level = Severity.Warn;
                row.VerdictInk = Palette.Warn;
                row.Verdict = L.T("Источник обновлений записан непонятно — исправьте его ниже, в «Проверке».");
                return row;
        }
        if (a.Release == null)
        {
            row.Verdict = a.Error != "" ? L.F("Источник обновлений не ответил: {0}.", L.T(a.Error)) : L.T("Ещё не проверялось: нажмите «Проверить сейчас».");
            return row;
        }
        row.Latest = a.Release.Version;
        row.LatestNote = DateTime.TryParseExact(a.Release.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? Day(d) : "";
        if (a.Diff > 0)
        {
            row.Level = Severity.Warn;
            row.LatestInk = Palette.Warn;
            row.Verdict = Portable.Enabled
                ? L.T("Вышла новая версия. Переносная версия обновляется вручную: что изменилось и ссылка на страницу выпусков — ниже.")
                : L.T("Вышла новая версия. Что в ней изменилось и кнопка «Обновить» — ниже.");
        }
        else
        {
            row.Level = Severity.Ok;
            row.Verdict = L.T("Установлена последняя версия.");
        }
        return row;
    }

    // ================================================================== проверка

    private void StartCheck()
    {
        if (checkingHere) return;
        checkingHere = true;
        BtnCheck.IsEnabled = false;
        CheckedText.Text = L.T("проверяю…");
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
        Result.Text = AutoToggle.IsChecked == true ? L.T("Программа будет проверять новые версии сама раз в сутки.") : L.T("Сама программа больше не проверяет новые версии — только по кнопке «Проверить сейчас».");
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
            FeedResult.Text = L.T("Не понимаю такой источник. Нужен адрес https://…/update.json или «владелец/репозиторий» на GitHub. Файлы на диске и адреса http не принимаются.");
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
            FeedResult.Text = L.T("Источник убран: программа не будет искать свои новые версии.");
            Shown();
        }
        else
        {
            FeedResult.Text = L.T("Сохранено. Проверяю источник…");
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
            Result.Text = L.T("Сейчас идёт игра, и программа её записывает. Обновите после игры: при обновлении программа закроется.");
            return;
        }
        updating = true;
        BtnUpdate.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.Target = 0;
        Result.Foreground = Palette.Ink3;
        Result.Text = L.T("Сверяю с официальным выпуском на GitHub…");
        host.BeginBusy();
        string folder = VersionCheck.UpdateFolder;
        Task.Run(() =>
        {
            var lastUi = DateTime.MinValue;
            try
            {
                if (!VersionCheck.UpdateFolderProtected)
                {
                    host.Ui(() => Finish(L.T("Не удалось создать защищённую папку %ProgramData%\\PulsePC — скачивать установщик в папку пользователя небезопасно. Обновите программу вручную, скачав установщик с GitHub."), Palette.Warn));
                    return;
                }
                // Адрес и контрольную сумму берём не из settings.json (его может переписать любая программа), а заново из выпуска.
                var official = VersionCheck.QueryOfficial(out string error);
                if (official == null)
                {
                    host.Ui(() => Finish(L.F("Официальный выпуск на GitHub не ответил: {0}. Ничего не скачано.", L.T(error)), Palette.Warn));
                    return;
                }
                if (VersionCheck.CompareVersions(official.Version, Installer.Version) <= 0)
                {
                    host.Ui(() => Finish(L.F("В официальном выпуске на GitHub нет версии новее установленной {0} (там {1}). Ничего не скачано.", Installer.Version, official.Version), Palette.Ink3));
                    return;
                }
                host.Ui(() => Result.Text = L.F("Скачиваю установщик {0}…", official.Version));
                var d = VersionCheck.Download(official, folder, (done, total) =>
                {
                    if ((DateTime.UtcNow - lastUi).TotalMilliseconds < 200) return;
                    lastUi = DateTime.UtcNow;
                    host.Ui(() =>
                    {
                        Result.Text = total > 0 ? L.F("Скачано {0} из {1}…", Fmt.Size(done), Fmt.Size(total)) : L.F("Скачано {0}…", Fmt.Size(done));
                        Progress.Target = total > 0 ? (double)done / total : 0;
                    });
                });
                if (!d.Ok)
                {
                    host.Ui(() => Finish(L.T(d.Message), Palette.Warn));
                    return;
                }
                using var run = VersionCheck.Launch(d.Path, official);
                if (!run.Started)
                {
                    host.Ui(() => Finish(L.T(d.Message) + " " + L.T(run.Message), Palette.Warn));
                    return;
                }
                host.Ui(() => Result.Text = L.T(d.Message) + " " + L.T(run.Message));
                // Выходим, только когда окно установщика открылось (он сам подаёт знак), а не через фиксированное время.
                var wait = VersionCheck.WaitForInstaller(run);
                host.Ui(() => Finish(L.T(d.Message) + " " + L.T(wait.Message), wait.Ready ? Palette.Ink1 : Palette.Warn));
                if (wait.Ready) VersionCheck.RequestExit();
            }
            catch (Exception ex) { host.Ui(() => Finish(L.F("Обновление не удалось: {0}", ex.Message), Palette.Warn)); }
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

    // ================================================================== обновление без нажатия

    private void ShowAutoInstall(AppSettings s, (bool Ok, string Why) auto, string newVersion)
    {
        bool on = VersionCheck.AutoInstall(s);
        AutoInstallToggle.IsChecked = on && auto.Ok;
        AutoInstallToggle.IsEnabled = host.Admin && auto.Ok;
        if (!auto.Ok)
        {
            AutoInstallState.Text = auto.Why;
            return;
        }
        string error = s.Get(VersionCheck.KAutoError), failed = s.Get(VersionCheck.KAutoFailed);
        AutoInstallState.Text =
            !on ? (VersionCheck.AutoAsked(s) ? L.T("Выключено: новые версии ставятся только кнопкой «Обновить».") : "")
            : newVersion != null && failed == newVersion ? L.F("Версия {0} не встала сама ({1}). Обновите её кнопкой «Обновить» выше.", newVersion, L.T(error))
            : newVersion != null && error != "" ? L.F("Версия {0} пока не скачана: {1}. Программа попробует снова через час.", newVersion, L.T(error))
            : newVersion != null ? L.F("Версия {0} поставится сама: в первые минуты после запуска программы или когда вы отойдёте от компьютера — не во время игры.", newVersion)
            : L.T("Включено. Когда выйдет новая версия, она поставится сама — не во время игры.");
    }

    private void ShowWhatsNew(AppSettings s)
    {
        var recent = VersionCheck.RecentUpdate(s);
        WhatsNewBlock.Visibility = recent.Recent ? Visibility.Visible : Visibility.Collapsed;
        if (!recent.Recent) return;
        WhatsNewLabel.Source = L.F("ЧТО НОВОГО В {0}", Installer.Version);
        string when = recent.At is DateTime at ? VersionCheck.When(at) : "";
        WhatsNewText.Text = recent.From != "" ? L.F("Программа обновлена {0}: была версия {1}, теперь {2}.", when, recent.From, Installer.Version)
                                              : L.F("Программа обновлена {0} до версии {1}.", when, Installer.Version);
        var notes = VersionCheck.WhatsNew(s);
        WhatsNewList.ItemsSource = notes.Count > 0 ? notes : new List<string> { L.T("Описания изменений нет — оно на странице выпусков на GitHub.") };
    }

    private void SetAuto(bool on)
    {
        if (host == null || !host.Admin) return;
        VersionCheck.SetAutoInstall(host.Settings, on);
        Result.Foreground = Palette.Ink3;
        Result.Text = on ? L.T("Новые версии будут ставиться сами — не во время игры. После обновления придёт короткое уведомление.")
                         : L.T("Новые версии ставятся только кнопкой «Обновить».");
        Shown();
    }

    private void AutoYes_Click(object sender, RoutedEventArgs e) => SetAuto(true);
    private void AutoNo_Click(object sender, RoutedEventArgs e) => SetAuto(false);
    private void AutoInstallToggle_Click(object sender, RoutedEventArgs e) => SetAuto(AutoInstallToggle.IsChecked == true);

    private void Releases_Click(object sender, RoutedEventArgs e)
    {
        try { GameLibrary.OpenAsUser(Portable.ReleasesUrl); }
        catch { Result.Text = L.F("Не открылось. Адрес: {0}", Portable.ReleasesUrl); }
    }

    // ================================================================== ссылки

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VersionLink link || link.Url == "") return;
        try { GameLibrary.OpenAsUser(link.Url); }
        catch { Result.Text = L.F("Не открылось. Адрес: {0}", link.Url); }
    }
}
