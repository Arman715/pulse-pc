using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using Microsoft.Win32;

namespace PulsePC.Services;

/// <summary>
/// Вид записей журнала Windows, которые можно «отметить как просмотренные»: одно правило отбора
/// для «Диагностики», первичного осмотра, раздела «Драйвер» и сторожа.
/// </summary>
public sealed class LogKind
{
    public string Key { get; init; } = "";
    public string Log { get; init; } = "System";
    /// <summary>Условие XPath внутри System[…], без времени.</summary>
    public string Filter { get; init; } = "";
}

/// <summary>
/// Отметки «просмотрено»: после отметки считаются только записи новее неё. Журналы Windows не очищаются и не меняются —
/// программа лишь перестаёт показывать то, что пользователь уже видел. Хранятся в AppSettings: diag.seen.&lt;вид&gt; = время ISO 8601.
/// </summary>
public static class LogMarks
{
    public const string Shutdown = "shutdown", Bsod = "bsod", Gpu = "gpu", Whea = "whea", Disk = "disk", Crash = "crash", Hang = "hang", SystemErrors = "system";
    private const string Prefix = "diag.seen.";
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Общие настройки программы (ставит главное окно). Без окна — читаются из файла.</summary>
    public static AppSettings Settings { get; set; }
    private static AppSettings S(AppSettings s) => s ?? (Settings ??= AppSettings.Load());

    private static Dictionary<string, LogKind> kinds;
    /// <summary>Все виды. Строится при первом обращении: фильтр видеодрайвера берётся из GpuDriver.</summary>
    public static IReadOnlyDictionary<string, LogKind> Kinds => kinds ??= new Dictionary<string, LogKind>
    {
        [Shutdown] = new() { Key = Shutdown, Filter = "Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41)" },
        [Bsod] = new() { Key = Bsod, Filter = "Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and (EventID=1001)" },
        [Gpu] = new() { Key = Gpu, Filter = GpuDriver.EventFilter },
        [Whea] = new() { Key = Whea, Filter = "Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (Level=1 or Level=2 or Level=3)" },
        [Disk] = new() { Key = Disk, Filter = "(Provider[@Name='disk'] or Provider[@Name='stornvme'] or Provider[@Name='storahci'] or Provider[@Name='Ntfs'] or Provider[@Name='volmgr']) and (Level=1 or Level=2)" },
        [Crash] = new() { Key = Crash, Log = "Application", Filter = "Provider[@Name='Application Error'] and (EventID=1000)" },
        [Hang] = new() { Key = Hang, Log = "Application", Filter = "Provider[@Name='Application Hang']" },
        [SystemErrors] = new() { Key = SystemErrors, Filter = "(Level=1 or Level=2)" },
    };

    /// <summary>Когда вид отмечен как просмотренный; null — отметки нет.</summary>
    public static DateTime? Seen(string kind, AppSettings settings = null)
    {
        string raw = S(settings).Get(Prefix + kind);
        return raw != "" && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t.ToLocalTime() : null;
    }

    /// <summary>Отметка, которая действует в окне последних days дней (более старая ничего не скрывает).</summary>
    public static DateTime? SeenWithin(string kind, int days, AppSettings settings = null)
    {
        var seen = Seen(kind, settings);
        return seen != null && seen.Value > DateTime.Now.AddDays(-days) ? seen : null;
    }

    public static bool Any(AppSettings settings = null) => Kinds.Keys.Any(k => Seen(k, settings) != null);

    /// <summary>Отметить виды как просмотренные сейчас. Журналы Windows не трогаются.</summary>
    public static void MarkSeen(IEnumerable<string> kindKeys, AppSettings settings = null)
    {
        var s = S(settings);
        string now = DateTime.Now.ToString("o", CultureInfo.InvariantCulture);
        foreach (var k in kindKeys) s.Set(Prefix + k, now);
        Save(s);
    }

    /// <summary>Снять отметку одного вида.</summary>
    public static void Unmark(string kind, AppSettings settings = null)
    {
        var s = S(settings);
        s.Set(Prefix + kind, null);
        Save(s);
    }

    /// <summary>«Показать всё снова»: убрать все отметки.</summary>
    public static void Reset(AppSettings settings = null)
    {
        var s = S(settings);
        foreach (var k in Kinds.Keys) s.Set(Prefix + k, null);
        Save(s);
    }

    private static void Save(AppSettings s)
    {
        var values = s.Values;
        if (values == null) { s.Save(); return; }
        lock (values) s.Save();
    }

    /// <summary>Условие XPath «записи не раньше этого времени».</summary>
    public static string TimeFrom(DateTime since) =>
        "TimeCreated[@SystemTime >= '" + since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z']";

    /// <summary>Начало счёта для вида: последние days дней, но не раньше отметки «просмотрено».</summary>
    public static DateTime Since(string kind, int days, AppSettings settings = null)
    {
        var from = DateTime.Now.AddDays(-days);
        var seen = Seen(kind, settings);
        return seen != null && seen.Value > from ? seen.Value : from;
    }

    /// <summary>Запрос записей вида начиная с since; extra — дополнительное условие (например, уровень).</summary>
    public static string Query(string kind, DateTime since, string extra = null) =>
        $"*[System[({Kinds[kind].Filter}){(extra == null ? "" : " and " + extra)} and {TimeFrom(since)}]]";

    /// <summary>«после отметки 19 ноября».</summary>
    public static string SeenText(DateTime seen) => "после отметки " + seen.ToString("d MMMM", Ru);
}

/// <summary>Находка «Диагностики»: к строке можно приложить вид журнала (для «скрыть») и ссылку на раздел.</summary>
public sealed class DiagFinding : DiagItem
{
    /// <summary>Вид записей журнала (LogMarks); пусто — строку скрыть нельзя.</summary>
    public string Kind { get; set; } = "";
    /// <summary>Куда ведёт ссылка: «tab:Очистка», «tab:Оптимизация» или «sys:&lt;раздел вкладки «Система»&gt;».</summary>
    public string Link { get; set; } = "";
    public string LinkText { get; set; } = "";
    public bool CanHide => Kind != "" && Level != Severity.Ok;
    public bool HasLink => Link != "";
    public bool HasActions => CanHide || HasLink;
}

/// <summary>Проверка состояния системы: журналы, диски, память, видеокарта, настройки.</summary>
public static class Diagnostics
{
    public const int Days = 7;

    public static List<DiagItem> Run(Metrics metrics, IEnumerable<TweakItem> tweaks, AppSettings settings = null)
    {
        var found = new List<DiagItem>();
        void Add(Severity level, string title, string detail, string count = "", string kind = "", string link = "", string linkText = "") =>
            found.Add(new DiagFinding { Level = level, Title = title, Detail = detail, Count = count, Kind = kind, Link = link, LinkText = linkText });

        // --- Журналы Windows: за 7 дней, но не раньше отметки «просмотрено» ---
        DateTime? Mark(string kind) => LogMarks.SeenWithin(kind, Days, settings);
        string Period(string kind) => Mark(kind) is DateTime seen ? LogMarks.SeenText(seen) + " — новых нет" : $"за {Days} дней";
        int Count(string kind, out DateTime last, out List<string> names) =>
            Events(LogMarks.Kinds[kind].Log, LogMarks.Query(kind, LogMarks.Since(kind, Days, settings)), out last, out names);
        // О некоторых видах обычно молчат; после отметки строка появляется в «В порядке», чтобы было видно, куда она делась.
        void QuietOk(string kind, string title) { if (Mark(kind) != null) Add(Severity.Ok, title, Period(kind)); }

        int kp = Count(LogMarks.Shutdown, out var kpLast, out _);
        if (kp == 0) Add(Severity.Ok, "Аварийных выключений нет", Period(LogMarks.Shutdown));
        else Add(Severity.Warn, "Аварийные выключения", $"Последнее — {kpLast:dd.MM в HH:mm}. Компьютер выключился, не завершив работу: завис, сбой или пропало питание.", kp.ToString(), LogMarks.Shutdown);

        int bsod = Count(LogMarks.Bsod, out var bsodLast, out _);
        if (bsod > 0) Add(Severity.Bad, "Синие экраны", $"Последний — {bsodLast:dd.MM в HH:mm}. Обычно виноват драйвер или неисправное железо.", bsod.ToString(), LogMarks.Bsod);
        else Add(Severity.Ok, "Синих экранов нет", Period(LogMarks.Bsod));

        // Видеодрайвер: здесь одна строка, подробный разбор — в разделе «Драйвер».
        int gpuErr = Count(LogMarks.Gpu, out var gpuLast, out _);
        if (gpuErr == 0) Add(Severity.Ok, "Драйвер видеокарты без сбоев", Period(LogMarks.Gpu));
        else
        {
            DateTime? installed = null;
            try { var main = GpuDriver.ReadAdapters().FirstOrDefault(); if (main != null) installed = GpuDriver.InstalledAt(main.Service, 4000); } catch { }
            const string link = "sys:Драйвер", linkText = "Что это за записи и что делать — в разделе «Драйвер»";
            if (installed != null && gpuLast < installed.Value)
                Add(Severity.Info, "Сбои драйвера видеокарты — только на прежней версии",
                    $"Все записи — до {installed.Value.ToString("d MMMM", CultureInfo.GetCultureInfo("ru-RU"))}, когда поставили нынешний драйвер. На нём сбоев нет.",
                    gpuErr.ToString(), LogMarks.Gpu, link, linkText);
            else
                Add(Severity.Warn, "Сбои драйвера видеокарты", $"Последний — {gpuLast:dd.MM в HH:mm}.", gpuErr.ToString(), LogMarks.Gpu, link, linkText);
        }

        int whea = Count(LogMarks.Whea, out var wheaLast, out _);
        if (whea > 0) Add(Severity.Bad, "Аппаратные ошибки", $"Последняя — {wheaLast:dd.MM в HH:mm}. Процессор, память или шина сообщили об ошибке.", whea.ToString(), LogMarks.Whea);
        else QuietOk(LogMarks.Whea, "Аппаратных ошибок нет");

        int dsk = Count(LogMarks.Disk, out var dskLast, out _);
        if (dsk > 0) Add(Severity.Bad, "Ошибки дисков в журнале", $"Последняя — {dskLast:dd.MM в HH:mm}. Сделайте копию важных данных.", dsk.ToString(), LogMarks.Disk);
        else QuietOk(LogMarks.Disk, "Ошибок дисков в журнале нет");

        int crashes = Count(LogMarks.Crash, out _, out var crashNames);
        if (crashes > 0) Add(Severity.Info, "Падения программ", "Чаще всего: " + TopNames(crashNames) + ".", crashes.ToString(), LogMarks.Crash);
        else Add(Severity.Ok, "Программы не падали", Period(LogMarks.Crash));

        int hangs = Count(LogMarks.Hang, out _, out var hangNames);
        if (hangs > 0) Add(Severity.Info, "Зависания программ", "Чаще всего: " + TopNames(hangNames) + ".", hangs.ToString(), LogMarks.Hang);
        else QuietOk(LogMarks.Hang, "Программы не зависали");

        int sysErr = Count(LogMarks.SystemErrors, out _, out _);
        var sysMark = Mark(LogMarks.SystemErrors);
        if (sysErr > 100)
            Add(Severity.Warn, "Много ошибок в системном журнале",
                (sysMark != null ? "Новых " + LogMarks.SeenText(sysMark.Value) : $"За {Days} дней") + ". Какая ошибка повторяется, видно в «Просмотре событий», журнал «Система».", sysErr.ToString(), LogMarks.SystemErrors);
        else Add(Severity.Ok, "Системный журнал в норме", sysMark != null ? $"{LogMarks.SeenText(sysMark.Value)}: {sysErr}" : $"ошибок за {Days} дней: {sysErr}");

        // --- Диски ---
        var disks = metrics.Disks;
        foreach (var d in disks)
        {
            if (!string.Equals(d.Health, "Healthy", StringComparison.OrdinalIgnoreCase) && d.Health != "")
                Add(Severity.Bad, $"Диск {d.Name} сообщает о неисправности", $"Состояние «{d.Health}». Срочно скопируйте с него важные данные.");
            else if (d.ReadErrors > 0)
                Add(Severity.Warn, $"Диск {d.Name}: ошибки чтения", $"Наработка {d.Hours:N0} ч. Не храните на нём ничего важного.", $"{d.ReadErrors:0}");
            else if (d.Wear >= 80)
                Add(Severity.Warn, $"Диск {d.Name} изношен", "Ресурс диска почти исчерпан. Сделайте копию важных данных.", $"{d.Wear:0} %");
            else if (d.Temp >= 60)
                Add(Severity.Warn, $"Диск {d.Name} горячий", "Проверьте обдув корпуса.", $"{d.Temp:0} °C");
        }
        if (disks.Count > 0 && disks.All(d => d.Health == "Healthy" && !(d.ReadErrors > 0)))
            Add(Severity.Ok, "Диски исправны", Fmt.Plural(disks.Count, "диск", "диска", "дисков"));
        if (disks.Count == 0)
            Add(Severity.Info, "Состояние дисков недоступно", "Температуру и износ дисков видно только с правами администратора.");

        const string cleanLink = "tab:Очистка", cleanText = "Освободить место — на вкладке «Очистка»";
        foreach (var drive in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady))
        {
            double freePct = 100.0 * drive.AvailableFreeSpace / drive.TotalSize;
            string name = drive.Name.TrimEnd('\\');
            string text = $"Свободно {Fmt.Size(drive.AvailableFreeSpace)} из {Fmt.Size(drive.TotalSize)}.";
            if (freePct < 10) Add(Severity.Bad, $"На диске {name} почти нет места", text, $"{freePct:0} %", link: cleanLink, linkText: cleanText);
            else if (freePct < 20) Add(Severity.Warn, $"На диске {name} мало места", text, $"{freePct:0} %", link: cleanLink, linkText: cleanText);
        }

        // --- Память ---
        var (totalGb, availGb) = Metrics.ReadRam();
        if (availGb < 2) Add(Severity.Warn, "Мало свободной памяти", $"Доступно {Fmt.Gb(availGb)} из {Fmt.Gb(totalGb)}. Закройте тяжёлые программы перед игрой.", Fmt.Gb(availGb));
        else Add(Severity.Ok, "Памяти достаточно", $"доступно {Fmt.Gb(availGb)} из {Fmt.Gb(totalGb)}");
        int ramGb = (int)Math.Ceiling(totalGb / 2) * 2;     // часть памяти забирает встроенная графика: 15,3 ГБ — это планки на 16
        if (totalGb < 20) Add(Severity.Info, $"{ramGb} ГБ памяти — впритык для современных игр", $"В играх свободной памяти почти не остаётся. Расширение до {(ramGb <= 8 ? 16 : 32)} ГБ — самое полезное улучшение для этого компьютера.");

        // --- Видеокарта ---
        var gpu = metrics.Gpu;
        bool gpuSilent = gpu.At == DateTime.MinValue;
        if (double.IsNaN(gpu.Temp)) Add(Severity.Info, gpuSilent ? "Нет данных о видеокарте" : "Температура видеокарты недоступна",
            gpuSilent ? (gpu.Source == "" && GameTuning.IsNvidia ? "Утилита nvidia-smi не ответила." : "Windows пока не отдала показания видеокарты.")
                : "Драйвер этой видеокарты не сообщает Windows её температуру — так бывает у встроенной графики и старых драйверов. Загрузку и видеопамять программа всё равно показывает.");
        else
        {
            if (gpu.Temp >= 86) Add(Severity.Bad, "Видеокарта перегревается", "Проверьте вентиляторы и обдув корпуса.", $"{gpu.Temp:0} °C");
            else if (gpu.Temp >= 80) Add(Severity.Warn, "Видеокарта горячая", "Под нагрузкой это допустимо, в простое — нет.", $"{gpu.Temp:0} °C");
            else Add(Severity.Ok, "Температура видеокарты в норме", $"{gpu.Temp:0} °C, драйвер {gpu.Driver}");

            if (gpu.Load < 5 && gpu.Power > 30)
            {
                if (GameTuning.IsNvidia)
                    Add(Severity.Warn, "Видеокарта греется в простое",
                        "Обычно это общий режим электропитания «максимальная производительность» в драйвере NVIDIA. Поставьте «Обычный», а максимальный оставьте только для игр.",
                        $"{gpu.Power:0} Вт", link: "sys:" + GpuVendors.SectionTitle("NVIDIA"), linkText: $"Поменять — в разделе «{GpuVendors.SectionTitle("NVIDIA")}»");
                else
                    Add(Severity.Warn, "Видеокарта греется в простое",
                        "Обычно это режим максимальной производительности в настройках драйвера видеокарты. Для всех программ поставьте обычный режим, а максимальный оставьте только для игр.", $"{gpu.Power:0} Вт");
            }
        }

        // --- Настройки ---
        var notApplied = tweaks.Where(t => t.Applied == false).Select(t => t.Title).ToList();
        if (notApplied.Count == 0) Add(Severity.Ok, "Игровые настройки на месте", "все применены");
        else Add(Severity.Warn, "Не включены игровые настройки", string.Join("; ", notApplied) + ".", notApplied.Count.ToString(),
            link: "tab:Оптимизация", linkText: "Включить — на вкладке «Оптимизация»");

        // --- Прочее ---
        if (PendingReboot()) Add(Severity.Warn, "Windows ждёт перезагрузки", "Обновление или установка закончится только после перезагрузки.");
        double uptimeDays = Environment.TickCount64 / 86400000.0;
        if (uptimeDays >= 7) Add(Severity.Info, "Давно не было перезагрузки", "Перезагрузка раз в несколько дней освобождает память и применяет обновления.", Fmt.Plural((int)Math.Round(uptimeDays), "день", "дня", "дней"));
        if (!Shell.IsAdmin()) Add(Severity.Info, "Программа запущена без прав администратора", "Часть проверок и действий недоступна.");

        return found.OrderBy(x => x.Level).ToList();
    }

    private static string TopNames(List<string> names) =>
        names.Count == 0 ? "нет данных"
        : string.Join(", ", names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Key} ({g.Count()})"));

    /// <summary>Считает события по запросу; возвращает число, время последнего и первое свойство каждого (имя программы для сбоев).</summary>
    private static int Events(string log, string xpath, out DateTime last, out List<string> firstProps)
    {
        int count = 0; last = DateTime.MinValue; firstProps = new List<string>();
        try
        {
            var query = new EventLogQuery(log, PathType.LogName, xpath);
            using var reader = new EventLogReader(query);
            for (EventRecord e = reader.ReadEvent(); e != null; e = reader.ReadEvent())
            {
                using (e)
                {
                    count++;
                    if (e.TimeCreated.HasValue && e.TimeCreated.Value > last) last = e.TimeCreated.Value;
                    try { if (e.Properties.Count > 0 && firstProps.Count < 500) firstProps.Add(Convert.ToString(e.Properties[0].Value)); } catch { }
                }
            }
        }
        catch { }
        return count;
    }

    /// <summary>Число записей вида начиная с since (не больше limit). Для осмотра и сторожа.</summary>
    public static int CountSince(string kind, DateTime since, string extra = null, int limit = 1000)
    {
        int n = 0;
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(LogMarks.Kinds[kind].Log, PathType.LogName, LogMarks.Query(kind, since, extra)));
            for (EventRecord e = reader.ReadEvent(); e != null && n < limit; e = reader.ReadEvent()) { e.Dispose(); n++; }
        }
        catch { }
        return n;
    }

    private static bool PendingReboot()
    {
        try
        {
            using var a = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            using var b = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            return a != null || b != null;
        }
        catch { return false; }
    }
}
