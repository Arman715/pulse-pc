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
    public static string SeenText(DateTime seen) => L.F("после отметки {0:d MMMM}", seen);
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
        string Period(string kind) => Mark(kind) is DateTime seen ? L.F("{0} — новых нет", LogMarks.SeenText(seen)) : L.F("за {0} дней", Days);
        int Count(string kind, out DateTime last, out List<string> names) =>
            Events(LogMarks.Kinds[kind].Log, LogMarks.Query(kind, LogMarks.Since(kind, Days, settings)), out last, out names);
        // О некоторых видах обычно молчат; после отметки строка появляется в «В порядке», чтобы было видно, куда она делась.
        void QuietOk(string kind, string title) { if (Mark(kind) != null) Add(Severity.Ok, title, Period(kind)); }

        int kp = Count(LogMarks.Shutdown, out var kpLast, out _);
        if (kp == 0) Add(Severity.Ok, L.T("Аварийных выключений нет"), Period(LogMarks.Shutdown));
        else Add(Severity.Warn, L.T("Аварийные выключения"), L.F("Последнее — {0:dd.MM в HH:mm}. Компьютер выключился, не завершив работу: завис, сбой или пропало питание.", kpLast), kp.ToString(), LogMarks.Shutdown);

        int bsod = Count(LogMarks.Bsod, out var bsodLast, out _);
        if (bsod > 0) Add(Severity.Bad, L.T("Синие экраны"), L.F("Последний — {0:dd.MM в HH:mm}. Обычно виноват драйвер или неисправное железо.", bsodLast), bsod.ToString(), LogMarks.Bsod);
        else Add(Severity.Ok, L.T("Синих экранов нет"), Period(LogMarks.Bsod));

        // Видеодрайвер: здесь одна строка, подробный разбор — в разделе «Драйвер».
        int gpuErr = Count(LogMarks.Gpu, out var gpuLast, out _);
        if (gpuErr == 0) Add(Severity.Ok, L.T("Драйвер видеокарты без сбоев"), Period(LogMarks.Gpu));
        else
        {
            DateTime? installed = null;
            try { var main = GpuDriver.ReadAdapters().FirstOrDefault(); if (main != null) installed = GpuDriver.InstalledAt(main.Service, 4000); } catch { }
            const string link = "sys:Драйвер";
            string linkText = L.T("Что это за записи и что делать — в разделе «Драйвер»");
            if (installed != null && gpuLast < installed.Value)
                Add(Severity.Info, L.T("Сбои драйвера видеокарты — только на прежней версии"),
                    L.F("Все записи — до {0:d MMMM}, когда поставили нынешний драйвер. На нём сбоев нет.", installed.Value),
                    gpuErr.ToString(), LogMarks.Gpu, link, linkText);
            else
                Add(Severity.Warn, L.T("Сбои драйвера видеокарты"), L.F("Последний — {0:dd.MM в HH:mm}.", gpuLast), gpuErr.ToString(), LogMarks.Gpu, link, linkText);
        }

        int whea = Count(LogMarks.Whea, out var wheaLast, out _);
        if (whea > 0) Add(Severity.Bad, L.T("Аппаратные ошибки"), L.F("Последняя — {0:dd.MM в HH:mm}. Процессор, память или шина сообщили об ошибке.", wheaLast), whea.ToString(), LogMarks.Whea);
        else QuietOk(LogMarks.Whea, L.T("Аппаратных ошибок нет"));

        int dsk = Count(LogMarks.Disk, out var dskLast, out _);
        if (dsk > 0) Add(Severity.Bad, L.T("Ошибки дисков в журнале"), L.F("Последняя — {0:dd.MM в HH:mm}. Сделайте копию важных данных.", dskLast), dsk.ToString(), LogMarks.Disk);
        else QuietOk(LogMarks.Disk, L.T("Ошибок дисков в журнале нет"));

        int crashes = Count(LogMarks.Crash, out _, out var crashNames);
        if (crashes > 0) Add(Severity.Info, L.T("Падения программ"), L.F("Чаще всего: {0}.", TopNames(crashNames)), crashes.ToString(), LogMarks.Crash);
        else Add(Severity.Ok, L.T("Программы не падали"), Period(LogMarks.Crash));

        int hangs = Count(LogMarks.Hang, out _, out var hangNames);
        if (hangs > 0) Add(Severity.Info, L.T("Зависания программ"), L.F("Чаще всего: {0}.", TopNames(hangNames)), hangs.ToString(), LogMarks.Hang);
        else QuietOk(LogMarks.Hang, L.T("Программы не зависали"));

        int sysErr = Count(LogMarks.SystemErrors, out _, out _);
        var sysMark = Mark(LogMarks.SystemErrors);
        if (sysErr > 100)
            Add(Severity.Warn, L.T("Много ошибок в системном журнале"),
                (sysMark != null ? L.F("Новых {0}", LogMarks.SeenText(sysMark.Value)) : L.F("За {0} дней", Days)) + ". " + L.T("Какая ошибка повторяется, видно в «Просмотре событий», журнал «Система»."), sysErr.ToString(), LogMarks.SystemErrors);
        else Add(Severity.Ok, L.T("Системный журнал в норме"), sysMark != null ? $"{LogMarks.SeenText(sysMark.Value)}: {sysErr}" : L.F("ошибок за {0} дней: {1}", Days, sysErr));

        // --- Диски ---
        var disks = metrics.Disks;
        foreach (var d in disks)
        {
            if (!string.Equals(d.Health, "Healthy", StringComparison.OrdinalIgnoreCase) && d.Health != "")
                Add(Severity.Bad, L.F("Диск {0} сообщает о неисправности", d.Name), L.F("Состояние «{0}». Срочно скопируйте с него важные данные.", d.Health));
            else if (d.ReadErrors > 0)
                Add(Severity.Warn, L.F("Диск {0}: ошибки чтения", d.Name), L.F("Наработка {0:N0} ч. Не храните на нём ничего важного.", d.Hours), $"{d.ReadErrors:0}");
            else if (d.Wear >= 80)
                Add(Severity.Warn, L.F("Диск {0} изношен", d.Name), L.T("Ресурс диска почти исчерпан. Сделайте копию важных данных."), Pct(d.Wear ?? 0));
            else if (d.Temp >= 60)
                Add(Severity.Warn, L.F("Диск {0} горячий", d.Name), L.T("Проверьте обдув корпуса."), $"{d.Temp:0} °C");
        }
        if (disks.Count > 0 && disks.All(d => d.Health == "Healthy" && !(d.ReadErrors > 0)))
            Add(Severity.Ok, L.T("Диски исправны"), Fmt.Plural(disks.Count, "диск", "диска", "дисков"));
        if (disks.Count == 0)
            Add(Severity.Info, L.T("Состояние дисков недоступно"), L.T("Температуру и износ дисков видно только с правами администратора."));

        const string cleanLink = "tab:Очистка";
        string cleanText = L.T("Освободить место — на вкладке «Очистка»");
        foreach (var drive in DriveInfo.GetDrives())
        {
            // Диск, который пропал или заблокирован (BitLocker, отключённый USB) между перечислением и запросом, бросает
            // исключение — без этой защиты из-за одного диска не показывалась вся «Диагностика».
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady || drive.TotalSize <= 0) continue;
                double freePct = 100.0 * drive.AvailableFreeSpace / drive.TotalSize;
                string name = drive.Name.TrimEnd('\\');
                string text = L.F("Свободно {0} из {1}.", Fmt.Size(drive.AvailableFreeSpace), Fmt.Size(drive.TotalSize));
                if (freePct < 10) Add(Severity.Bad, L.F("На диске {0} почти нет места", name), text, Pct(freePct), link: cleanLink, linkText: cleanText);
                else if (freePct < 20) Add(Severity.Warn, L.F("На диске {0} мало места", name), text, Pct(freePct), link: cleanLink, linkText: cleanText);
            }
            catch { }
        }

        // --- Память ---
        var (totalGb, availGb) = Metrics.ReadRam();
        if (availGb < 2) Add(Severity.Warn, L.T("Мало свободной памяти"), L.F("Доступно {0} из {1}. Закройте тяжёлые программы перед игрой.", Fmt.Gb(availGb), Fmt.Gb(totalGb)), Fmt.Gb(availGb));
        else Add(Severity.Ok, L.T("Памяти достаточно"), L.F("доступно {0} из {1}", Fmt.Gb(availGb), Fmt.Gb(totalGb)));
        int ramGb = (int)Math.Ceiling(totalGb / 2) * 2;     // часть памяти забирает встроенная графика: 15,3 ГБ — это планки на 16
        if (totalGb < 20) Add(Severity.Info, L.F("{0} ГБ памяти — впритык для современных игр", ramGb), L.F("В играх свободной памяти почти не остаётся. Расширение до {0} ГБ — самое полезное улучшение для этого компьютера.", ramGb <= 8 ? 16 : 32));

        // --- Видеокарта ---
        var gpu = metrics.Gpu;
        bool gpuSilent = gpu.At == DateTime.MinValue;
        if (double.IsNaN(gpu.Temp)) Add(Severity.Info, gpuSilent ? L.T("Нет данных о видеокарте") : L.T("Температура видеокарты недоступна"),
            gpuSilent ? (gpu.Source == "" && GameTuning.IsNvidia ? L.T("Утилита nvidia-smi не ответила.") : L.T("Windows пока не отдала показания видеокарты."))
                : L.T("Драйвер этой видеокарты не сообщает Windows её температуру — так бывает у встроенной графики и старых драйверов. Загрузку и видеопамять программа всё равно показывает."));
        else
        {
            if (gpu.Temp >= 86) Add(Severity.Bad, L.T("Видеокарта перегревается"), L.T("Проверьте вентиляторы и обдув корпуса."), $"{gpu.Temp:0} °C");
            else if (gpu.Temp >= 80) Add(Severity.Warn, L.T("Видеокарта горячая"), L.T("Под нагрузкой это допустимо, в простое — нет."), $"{gpu.Temp:0} °C");
            else Add(Severity.Ok, L.T("Температура видеокарты в норме"), L.F("{0:0} °C, драйвер {1}", gpu.Temp, gpu.Driver));

            if (gpu.Load < 5 && gpu.Power > 30)
            {
                if (GameTuning.IsNvidia)
                    Add(Severity.Warn, L.T("Видеокарта греется в простое"),
                        L.T("Обычно это общий режим электропитания «максимальная производительность» в драйвере NVIDIA. Поставьте «Обычный», а максимальный оставьте только для игр."),
                        Watts(gpu.Power), link: "sys:" + GpuVendors.SectionTitle("NVIDIA"), linkText: L.F("Поменять — в разделе «{0}»", L.T(GpuVendors.SectionTitle("NVIDIA"))));
                else
                    Add(Severity.Warn, L.T("Видеокарта греется в простое"),
                        L.T("Обычно это режим максимальной производительности в настройках драйвера видеокарты. Для всех программ поставьте обычный режим, а максимальный оставьте только для игр."), Watts(gpu.Power));
            }
        }

        // --- Настройки ---
        var notApplied = tweaks.Where(t => t.Applied == false).Select(t => L.T(t.Title)).ToList();
        if (notApplied.Count == 0) Add(Severity.Ok, L.T("Игровые настройки на месте"), L.T("все применены"));
        else Add(Severity.Warn, L.T("Не включены игровые настройки"), string.Join("; ", notApplied) + ".", notApplied.Count.ToString(),
            link: "tab:Оптимизация", linkText: L.T("Включить — на вкладке «Оптимизация»"));

        // --- Прочее ---
        if (PendingReboot()) Add(Severity.Warn, L.T("Windows ждёт перезагрузки"), L.T("Обновление или установка закончится только после перезагрузки."));
        double uptimeDays = Environment.TickCount64 / 86400000.0;
        if (uptimeDays >= 7) Add(Severity.Info, L.T("Давно не было перезагрузки"), L.T("Перезагрузка раз в несколько дней освобождает память и применяет обновления."), Fmt.Plural((int)Math.Round(uptimeDays), "день", "дня", "дней"));
        if (!Shell.IsAdmin()) Add(Severity.Info, L.T("Программа запущена без прав администратора"), L.T("Часть проверок и действий недоступна."));

        return found.OrderBy(x => x.Level).ToList();
    }

    /// <summary>«93 %» по-русски, «93%» по-английски.</summary>
    private static string Pct(double v) => v.ToString("0", L.Culture) + (L.En ? "%" : " %");

    /// <summary>«45 Вт» / «45 W».</summary>
    private static string Watts(double v) => v.ToString("0", L.Culture) + " " + (L.En ? "W" : "Вт");

    private static string TopNames(List<string> names) =>
        names.Count == 0 ? L.T("нет данных")
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
