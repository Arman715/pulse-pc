using System.Windows;
using System.Windows.Controls;
using PulsePC.Services;

namespace PulsePC.Panels;

/// <summary>
/// Раздел «Драйвер видеокарты»: версия и возраст драйвера, кэш шейдеров (чистится в «Очистке», сюда — ссылка),
/// ошибки драйвера за месяц с той же отметкой «просмотрено», что в «Диагностике».
/// </summary>
public partial class GpuDriverPanel : UserControl, ISystemPanel
{
    private PanelHost host;
    private GpuDriverInfo main;
    private int generation;     // номер загрузки: результат устаревшей загрузки отбрасывается
    private List<GpuDriverInfo> lastAdapters;
    private DriverEventReport lastReport;

    public GpuDriverPanel()
    {
        InitializeComponent();
        // Раздел «Новые версии» узнал последнюю версию драйвера — обновить строку здесь.
        VersionCheck.Changed += () => host?.Ui(() => { if (lastAdapters != null) ShowDriver(lastAdapters, lastReport); });
    }

    public void Attach(PanelHost h)
    {
        host = h;
        AdminNote.Visibility = host.Admin ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Shown()
    {
        if (host == null) return;
        int gen = ++generation;
        var sessions = host.Sessions();
        Task.Run(() =>
        {
            // Три шага по возрастанию длительности: быстрое появляется на экране сразу.
            var adapters = Safe(GpuDriver.ReadAdapters) ?? new List<GpuDriverInfo>();
            var first = adapters.FirstOrDefault();
            host.Ui(() => { if (gen == generation) ShowDriver(adapters, null); });

            var caches = Safe(GpuDriver.CacheFolders) ?? new List<ShaderCacheFolder>();
            host.Ui(() => { if (gen == generation) ShowCaches(caches); });

            if (first != null) first.Installed = Safe(() => GpuDriver.InstalledAt(first.Service));
            var seen = LogMarks.Seen(LogMarks.Gpu, host.Settings);
            var report = Safe(() => GpuDriver.ReadEvents(sessions, first?.Installed, seen: seen));
            host.Ui(() =>
            {
                if (gen != generation) return;
                ShowDriver(adapters, report);
                ShowEvents(report);
            });
        });
    }

    private static T Safe<T>(Func<T> read)
    {
        try { return read(); } catch { return default; }
    }

    private void ShowDriver(List<GpuDriverInfo> adapters, DriverEventReport report)
    {
        lastAdapters = adapters;
        lastReport = report;
        main = adapters.FirstOrDefault();
        var latest = VersionCheck.DriverStatusFor(main, host.Settings);
        State.Text = report == null && main != null ? (main.Basic ? L.T("Драйвер видеокарты не установлен") : L.T("Читаю журнал Windows…"))
            : GpuDriver.Verdict(main, report, latest.Newer ? latest.Latest.Version : null, latest.Checked && latest.Diff == 0);
        ShowLatest(latest);
        if (main == null)
        {
            GpuName.Text = L.T("Видеокарта не найдена");
            DriverLine.Text = L.T("Windows не сообщила ни об одной видеокарте с установленным драйвером. Так бывает в виртуальной машине или при удалённом подключении.");
            AgeValue.Text = AgeLabel.Text = DriverAdvice.Text = "";
            BtnDownload.IsEnabled = false;
            return;
        }
        GpuName.Text = main.Name;
        DriverLine.Text = GpuDriver.DriverLine(main);
        AgeValue.Text = main.AgeText;
        AgeLabel.Text = main.AgeDays < 0 ? "" : L.T("возраст драйвера");
        AgeValue.Foreground = main.AgeDays >= 0 && !main.Fresh ? Palette.Warn : Palette.Ink1;
        DriverAdvice.Text = latest.Newer
            ? L.T("Обновлять не обязательно: стоит, если новую версию требует игра или в ней исправили вашу проблему. Новый драйвер ставится поверх нынешнего.")
            : latest.Checked && latest.Diff == 0 ? L.T("Установлена последняя версия с сайта NVIDIA — обновлять нечего.")
            : GpuDriver.DriverAdvice(main);

        var others = adapters.Skip(1).Select(a => a.Name + (a.Version != "" ? " — " + L.F("версия {0}", a.Version) : "")
                                                  + (a.AgeDays >= 0 ? ", " + L.F("драйверу {0}", a.AgeText) : "")).ToList();
        OtherGpus.Text = others.Count > 0 ? L.F("Вторая видеокарта: {0}.", string.Join("; ", others)) : "";
        OtherGpus.Visibility = others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        bool hasPage = GpuDriver.DownloadUrl(main.Vendor) != "";
        BtnDownload.IsEnabled = hasPage;
        DownloadNote.Text = hasPage
            ? L.F("Откроется сайт {0} в вашем браузере. Драйвер программа не скачивает и не ставит.", main.Vendor)
            : L.T("Производитель видеокарты не определён — найдите драйвер на сайте производителя компьютера.");
    }

    /// <summary>Строка «последняя версия на сайте NVIDIA» по проверке раздела «Новые версии» (сама строка в сеть не ходит).</summary>
    private void ShowLatest(DriverStatus st)
    {
        LatestBlock.Visibility = st.Checkable ? Visibility.Visible : Visibility.Collapsed;
        if (!st.Checkable) return;
        string when = st.CheckedAt is DateTime at ? " " + L.F("Проверено {0}.", VersionCheck.When(at)) : "";
        BtnWhatsNew.Visibility = st.Checked && st.Latest.DetailsUrl != "" ? Visibility.Visible : Visibility.Collapsed;
        if (st.Checked)
        {
            var l = st.Latest;
            string beta = l.Beta ? " " + L.T("(бета)") : "";
            LatestMarker.Level = st.Newer ? Severity.Warn : Severity.Ok;
            LatestValue.Text = l.Version;
            LatestValue.Foreground = st.Newer ? Palette.Warn : Palette.Ink1;
            LatestLine.Text = st.Newer
                    ? (l.Date is DateTime d
                        ? L.F("Вышла версия {0} от {1}{2}, у вас {3}.", l.Version, d.ToString(L.En ? "MMMM d, yyyy" : "d MMMM yyyy", L.Culture), beta, main?.Version)
                        : L.F("Вышла версия {0}{1}, у вас {2}.", l.Version, beta, main?.Version)) + when
                : st.Diff == 0 ? L.T("У вас последняя версия.") + when
                : L.F("У вас версия новее, чем NVIDIA предлагает для обычной установки ({0}).", l.Version) + when;
            BtnWhatsNew.Content = L.F("Что нового в {0}", l.Version);
        }
        else
        {
            LatestMarker.Level = Severity.Info;
            LatestValue.Text = "—";
            LatestValue.Foreground = Palette.Ink1;
            LatestLine.Text = st.Error != "" ? L.F("Сервис NVIDIA не ответил: {0}.", L.T(st.Error)) + when : L.T("Последнюю версию на сайте NVIDIA программа ещё не проверяла.");
        }
    }

    private void BtnWhatsNew_Click(object sender, RoutedEventArgs e)
    {
        string url = VersionCheck.DriverStatusFor(main, host?.Settings).Latest?.DetailsUrl ?? "";
        if (url == "") return;
        try { GameLibrary.OpenAsUser(url); }
        catch { CacheResult.Text = L.F("Браузер не открылся. Откройте страницу сами: {0}", url); }
    }

    private void BtnVersions_Click(object sender, RoutedEventArgs e) => host?.Open("sys:" + VersionCheck.SectionTitle);

    private void ShowCaches(List<ShaderCacheFolder> caches)
    {
        CacheRows.ItemsSource = caches.Select(c => new DriverRow { Title = c.Title, Extra = c.Path, Value = Fmt.Size(c.Bytes) }).ToList();
        long total = caches.Sum(c => c.Bytes);
        CacheSummary.Text = caches.Count == 0 ? L.T("Папок кэша шейдеров на этом компьютере нет") : L.T("Всего");
        CacheTotal.Text = caches.Count == 0 ? "" : Fmt.Size(total);
        BtnClear.IsEnabled = total > 0;
        if (CacheResult.Text == "")
        {
            string last = host.Settings.Get("gpudriver.lastClear");
            if (DateTime.TryParse(last, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var when))
                CacheResult.Text = L.F("Прошлая очистка: {0}.", when.ToString(L.En ? "MMMM d, yyyy" : "d MMMM yyyy", L.Culture));
        }
    }

    private void ShowEvents(DriverEventReport r)
    {
        EventsLabel.Source = r?.SeenSince != null ? L.F("СБОИ ДРАЙВЕРА {0}", r.Period).ToUpper(L.Culture) : "СБОИ ДРАЙВЕРА ЗА 30 ДНЕЙ";
        bool marked = LogMarks.Seen(LogMarks.Gpu, host.Settings) != null;
        SeenBlock.Visibility = r != null && r.Ok && (r.Total > 0 || marked) ? Visibility.Visible : Visibility.Collapsed;
        BtnSeen.Visibility = r != null && r.Total > 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnUnseen.Visibility = marked ? Visibility.Visible : Visibility.Collapsed;
        if (r == null || !r.Ok)
        {
            EventsHeadline.Text = L.T("Журнал Windows прочитать не удалось");
            EventsFacts.ItemsSource = new[]
            {
                (string.IsNullOrEmpty(r?.Error) ? L.T("Windows не дала прочитать журнал «Система».") : L.F("Windows не дала прочитать журнал «Система»: {0}.", r.Error))
                + " " + L.T("Откройте раздел ещё раз через минуту."),
            };
            EventsDetail.Visibility = Visibility.Collapsed;
            return;
        }
        EventsHeadline.Text = r.Headline;
        EventsHeadline.Foreground = r.Total > 0 && !r.Calm ? Palette.Warn : Palette.Ink1;
        EventsFacts.ItemsSource = r.Facts;
        KindRows.ItemsSource = r.Kinds;
        DayRows.ItemsSource = r.ByDay;
        RecentRows.ItemsSource = r.Recent;
        StepsLead.Text = r.StepsLead;
        StepRows.ItemsSource = r.Steps;
        EventsDetail.Visibility = r.Total > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnDownload_Click(object sender, RoutedEventArgs e)
    {
        string url = GpuDriver.DownloadUrl(main?.Vendor ?? "");
        if (url == "") return;
        try { GameLibrary.OpenAsUser(url); }
        catch { CacheResult.Text = L.F("Браузер не открылся. Откройте страницу сами: {0}", url); }
    }

    /// <summary>Кэш шейдеров чистится в одном месте — в «Очистке»: открыть её с отмеченным только этим пунктом.</summary>
    private void BtnClear_Click(object sender, RoutedEventArgs e) => host?.Open("clean:" + Cleaner.ShaderCacheTitle);

    // Отметка «просмотрено» для ошибок драйвера — общая с «Диагностикой», первичным осмотром и сторожем.
    private void BtnSeen_Click(object sender, RoutedEventArgs e)
    {
        if (host == null) return;
        LogMarks.MarkSeen(new[] { LogMarks.Gpu }, host.Settings);
        Shown();
    }

    private void BtnUnseen_Click(object sender, RoutedEventArgs e)
    {
        if (host == null) return;
        // Здесь снимается только отметка ошибок драйвера; остальные отметки «Диагностики» остаются.
        LogMarks.Unmark(LogMarks.Gpu, host.Settings);
        Shown();
    }
}
