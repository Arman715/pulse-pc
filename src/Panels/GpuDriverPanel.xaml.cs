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
        State.Text = report == null && main != null ? (main.Basic ? "Драйвер видеокарты не установлен" : "Читаю журнал Windows…")
            : GpuDriver.Verdict(main, report, latest.Newer ? latest.Latest.Version : null, latest.Checked && latest.Diff == 0);
        ShowLatest(latest);
        if (main == null)
        {
            GpuName.Text = "Видеокарта не найдена";
            DriverLine.Text = "Windows не сообщила ни об одной видеокарте с установленным драйвером. Так бывает в виртуальной машине или при удалённом подключении.";
            AgeValue.Text = AgeLabel.Text = DriverAdvice.Text = "";
            BtnDownload.IsEnabled = false;
            return;
        }
        GpuName.Text = main.Name;
        DriverLine.Text = GpuDriver.DriverLine(main);
        AgeValue.Text = main.AgeText;
        AgeLabel.Text = main.AgeDays < 0 ? "" : "возраст драйвера";
        AgeValue.Foreground = main.AgeDays >= 0 && !main.Fresh ? Palette.Warn : Palette.Ink1;
        DriverAdvice.Text = latest.Newer
            ? "Обновлять не обязательно: стоит, если новую версию требует игра или в ней исправили вашу проблему. Новый драйвер ставится поверх нынешнего."
            : latest.Checked && latest.Diff == 0 ? "Установлена последняя версия с сайта NVIDIA — обновлять нечего."
            : GpuDriver.DriverAdvice(main);

        var others = adapters.Skip(1).Select(a => a.Name + (a.Version != "" ? " — версия " + a.Version : "")
                                                  + (a.AgeDays >= 0 ? ", драйверу " + a.AgeText : "")).ToList();
        OtherGpus.Text = others.Count > 0 ? "Вторая видеокарта: " + string.Join("; ", others) + "." : "";
        OtherGpus.Visibility = others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        bool hasPage = GpuDriver.DownloadUrl(main.Vendor) != "";
        BtnDownload.IsEnabled = hasPage;
        DownloadNote.Text = hasPage
            ? $"Откроется сайт {main.Vendor} в вашем браузере. Драйвер программа не скачивает и не ставит."
            : "Производитель видеокарты не определён — найдите драйвер на сайте производителя компьютера.";
    }

    /// <summary>Строка «последняя версия на сайте NVIDIA» по проверке раздела «Новые версии» (сама строка в сеть не ходит).</summary>
    private void ShowLatest(DriverStatus st)
    {
        LatestBlock.Visibility = st.Checkable ? Visibility.Visible : Visibility.Collapsed;
        if (!st.Checkable) return;
        var ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        string when = st.CheckedAt is DateTime at ? " Проверено " + VersionCheck.When(at) + "." : "";
        BtnWhatsNew.Visibility = st.Checked && st.Latest.DetailsUrl != "" ? Visibility.Visible : Visibility.Collapsed;
        if (st.Checked)
        {
            var l = st.Latest;
            string date = l.Date is DateTime d ? " от " + d.ToString("d MMMM yyyy", ru) : "";
            LatestMarker.Level = st.Newer ? Severity.Warn : Severity.Ok;
            LatestValue.Text = l.Version;
            LatestValue.Foreground = st.Newer ? Palette.Warn : Palette.Ink1;
            LatestLine.Text = st.Newer ? $"Вышла версия {l.Version}{date}{(l.Beta ? " (бета)" : "")}, у вас {main?.Version}." + when
                : st.Diff == 0 ? "У вас последняя версия." + when
                : $"У вас версия новее, чем NVIDIA предлагает для обычной установки ({l.Version})." + when;
            BtnWhatsNew.Content = "Что нового в " + l.Version;
        }
        else
        {
            LatestMarker.Level = Severity.Info;
            LatestValue.Text = "—";
            LatestValue.Foreground = Palette.Ink1;
            LatestLine.Text = st.Error != "" ? $"Сервис NVIDIA не ответил: {st.Error}." + when : "Последнюю версию на сайте NVIDIA программа ещё не проверяла.";
        }
    }

    private void BtnWhatsNew_Click(object sender, RoutedEventArgs e)
    {
        string url = VersionCheck.DriverStatusFor(main, host?.Settings).Latest?.DetailsUrl ?? "";
        if (url == "") return;
        try { GameLibrary.OpenAsUser(url); }
        catch { CacheResult.Text = "Браузер не открылся. Откройте страницу сами: " + url; }
    }

    private void BtnVersions_Click(object sender, RoutedEventArgs e) => host?.Open("sys:" + VersionCheck.SectionTitle);

    private void ShowCaches(List<ShaderCacheFolder> caches)
    {
        CacheRows.ItemsSource = caches.Select(c => new DriverRow { Title = c.Title, Extra = c.Path, Value = Fmt.Size(c.Bytes) }).ToList();
        long total = caches.Sum(c => c.Bytes);
        CacheSummary.Text = caches.Count == 0 ? "Папок кэша шейдеров на этом компьютере нет" : "Всего";
        CacheTotal.Text = caches.Count == 0 ? "" : Fmt.Size(total);
        BtnClear.IsEnabled = total > 0;
        if (CacheResult.Text == "")
        {
            string last = host.Settings.Get("gpudriver.lastClear");
            if (DateTime.TryParse(last, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var when))
                CacheResult.Text = "Прошлая очистка: " + when.ToString("d MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) + ".";
        }
    }

    private void ShowEvents(DriverEventReport r)
    {
        EventsLabel.Source = r?.SeenSince != null ? "СБОИ ДРАЙВЕРА " + r.Period.ToUpperInvariant() : "СБОИ ДРАЙВЕРА ЗА 30 ДНЕЙ";
        bool marked = LogMarks.Seen(LogMarks.Gpu, host.Settings) != null;
        SeenBlock.Visibility = r != null && r.Ok && (r.Total > 0 || marked) ? Visibility.Visible : Visibility.Collapsed;
        BtnSeen.Visibility = r != null && r.Total > 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnUnseen.Visibility = marked ? Visibility.Visible : Visibility.Collapsed;
        if (r == null || !r.Ok)
        {
            EventsHeadline.Text = "Журнал Windows прочитать не удалось";
            EventsFacts.ItemsSource = new[] { "Windows не дала прочитать журнал «Система»" + (string.IsNullOrEmpty(r?.Error) ? "." : ": " + r.Error) + " Откройте раздел ещё раз через минуту." };
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
        catch { CacheResult.Text = "Браузер не открылся. Откройте страницу сами: " + url; }
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
