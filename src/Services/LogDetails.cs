using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PulsePC.Services;

/// <summary>Одна запись журнала Windows простыми словами.</summary>
public sealed class LogEvent
{
    public DateTime At;
    public string Provider = "";
    public int Id;
    /// <summary>Что это значит — по номеру и источнику записи (простыми словами).</summary>
    public string Plain = "";
    /// <summary>Текст записи от Windows (на языке Windows), коротко.</summary>
    public string Message = "";
    /// <summary>О каком диске запись (модель), если его удалось узнать.</summary>
    public string Disk = "";
    /// <summary>Программа и модуль (для падений и зависаний).</summary>
    public string App = "", Module = "";
    /// <summary>Код синего экрана у аварийного выключения (0 — не было).</summary>
    public long Bugcheck;
    /// <summary>Уровень записи Windows: 1 критическая, 2 ошибка, 3 предупреждение.</summary>
    public int Level;
}

/// <summary>Разбор вида записей: что это значит, что делать и сами записи.</summary>
public sealed class LogDetailsReport
{
    public string Kind = "";
    public string Meaning = "";
    public List<string> Steps = new();
    public List<LogEvent> Events = new();
    /// <summary>Записей всего за период (в Events — не больше <see cref="LogDetails.MaxEvents"/> последних).</summary>
    public int Total;
    public DateTime Since;
    /// <summary>Диски с их состоянием — для вида «диски».</summary>
    public List<PhysDisk> Disks = new();
    /// <summary>Журнал не прочитался (тогда «записей нет» — неправда).</summary>
    public bool Failed;
}

/// <summary>
/// «Подробнее и что делать» для замечаний из журналов Windows (аварийные выключения, синие экраны, видеодрайвер, WHEA, диски,
/// падения и зависания программ, системный журнал): записи с объяснением, план действий и открытие этих же записей
/// в «Просмотре событий». Только чтение: журналы Windows не очищаются и не меняются; для «Просмотра событий» записи
/// копируются во временный файл .evtx.
/// </summary>
public static class LogDetails
{
    public const int MaxEvents = 60;

    /// <summary>Собрать разбор вида за последние days дней (не раньше отметки «просмотрено»).</summary>
    public static LogDetailsReport Build(string kind, IReadOnlyList<PhysDisk> disks, AppSettings settings = null, int days = Diagnostics.Days)
    {
        var since = LogMarks.Since(kind, days, settings);
        if (kind == LogMarks.Disk && (disks == null || disks.Count == 0)) { try { disks = Metrics.QueryDisks(); } catch { } }
        var r = new LogDetailsReport { Kind = kind, Since = since, Disks = disks?.ToList() ?? new() };
        r.Events = Read(kind, since, disks, out r.Total, out r.Failed, out var top);
        r.Meaning = Meaning(kind);
        r.Steps = Steps(kind, r.Events, r.Disks, top);
        bool badDisk = kind == LogMarks.Disk && r.Disks.Any(DiskBad);
        if (r.Failed) r.Steps = new List<string> { L.T("Журнал Windows не прочитался — откройте «Просмотр событий» и посмотрите записи там.") };
        else if (r.Total == 0 && !badDisk) r.Steps = new List<string> { L.T("Новых записей нет — делать ничего не нужно.") };
        return r;
    }

    /// <summary>Диск, о котором стоит беспокоиться: сам сообщает о неисправности, есть неисправленные ошибки чтения или износ от 80 %.</summary>
    public static bool DiskBad(PhysDisk d) =>
        d.ReadErrors > 0 || d.Wear >= 80 || (d.Health != "" && !d.Health.Equals("Healthy", StringComparison.OrdinalIgnoreCase));

    /// <summary>Самые частые программа (падения, зависания) или источник (системный журнал) по ВСЕМ записям периода.</summary>
    public sealed class Top
    {
        public List<(string Name, int Count)> Items = new();
    }

    /// <summary>Записи вида, новые сначала (не больше MaxEvents), и сколько их всего.</summary>
    public static List<LogEvent> Read(string kind, DateTime since, IReadOnlyList<PhysDisk> disks, out int total) =>
        Read(kind, since, disks, out total, out _, out _);

    public static List<LogEvent> Read(string kind, DateTime since, IReadOnlyList<PhysDisk> disks, out int total, out bool failed, out Top top)
    {
        var list = new List<LogEvent>();
        total = 0; failed = false; top = new Top();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!LogMarks.Kinds.TryGetValue(kind, out var k)) return list;
        try
        {
            var q = new EventLogQuery(k.Log, PathType.LogName, LogMarks.Query(kind, since)) { ReverseDirection = true };
            using var reader = new EventLogReader(q);
            for (EventRecord e = reader.ReadEvent(); e != null; e = reader.ReadEvent())
            {
                using (e)
                {
                    total++;
                    // «Чаще всего» — по всем записям, а не только по показанным последним.
                    string who = kind is LogMarks.Crash or LogMarks.Hang ? FirstProp(e) : kind == LogMarks.SystemErrors ? e.ProviderName ?? "" : "";
                    if (who != "") counts[who] = counts.TryGetValue(who, out int c) ? c + 1 : 1;
                    if (list.Count >= MaxEvents) continue;
                    var ev = new LogEvent { At = e.TimeCreated ?? DateTime.MinValue, Provider = e.ProviderName ?? "", Id = e.Id, Level = e.Level ?? 0 };
                    var props = new List<string>();
                    try { foreach (var p in e.Properties) props.Add(Convert.ToString(p.Value, CultureInfo.InvariantCulture) ?? ""); } catch { }
                    string msg = "";
                    try { msg = e.FormatDescription() ?? ""; } catch { }
                    ev.Message = Short(msg);
                    Fill(kind, ev, props, msg, disks);
                    list.Add(ev);
                }
            }
        }
        catch (Exception ex) { Log.Error("записи журнала " + kind, ex); failed = true; }
        top.Items = counts.OrderByDescending(x => x.Value).Take(5).Select(x => (x.Key, x.Value)).ToList();
        return list;
    }

    private static string FirstProp(EventRecord e)
    {
        try { return e.Properties.Count > 0 ? Convert.ToString(e.Properties[0].Value, CultureInfo.InvariantCulture) ?? "" : ""; } catch { return ""; }
    }

    private static string Short(string s)
    {
        s = Regex.Replace(s ?? "", @"\s+", " ").Trim();
        return s.Length > 420 ? s[..420] + "…" : s;
    }

    /// <summary>Номер диска из пути устройства («\Device\Harddisk2\DR2» → 2); −1 — нет.</summary>
    internal static int DiskNumber(string text)
    {
        var m = Regex.Match(text ?? "", @"Harddisk(\d+)", RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : -1;
    }

    /// <summary>Буква тома из текста записи («C:», «\??\D:», «том D:»); пусто — нет.</summary>
    internal static string Volume(string text)
    {
        var m = Regex.Match(text ?? "", @"(?<![\w])([A-Za-z]):(?=[\\\s.,;)»]|$)");
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() + ":" : "";
    }

    private static string DiskName(string text, IReadOnlyList<PhysDisk> disks)
    {
        int n = DiskNumber(text);
        if (n >= 0) return disks?.FirstOrDefault(d => d.DiskNumber == n)?.Name is { Length: > 0 } name ? name : L.F("диск {0}", n);
        string vol = Volume(text);
        if (vol != "")
        {
            var d = disks?.FirstOrDefault(x => x.Letters.Any(l => string.Equals(l + ":", vol, StringComparison.OrdinalIgnoreCase)));
            return d != null ? $"{d.Name} ({vol})" : vol;
        }
        return "";
    }

    /// <summary>Объяснение записи по источнику и номеру.</summary>
    internal static void Fill(string kind, LogEvent ev, List<string> props, string msg, IReadOnlyList<PhysDisk> disks)
    {
        string p = ev.Provider.ToLowerInvariant();
        string all = string.Join(" ", props) + " " + msg;
        switch (kind)
        {
            case LogMarks.Disk:
                ev.Disk = DiskName(all, disks);
                string on = ev.Disk;
                if (on == "")
                {
                    ev.Plain = (p, ev.Id) switch
                    {
                        ("disk", 7) => L.T("Плохой блок: часть поверхности диска не читается. Это повреждение самого диска."),
                        ("disk", 11) => L.T("Ошибка связи с диском: контроллер не смог обменяться с ним данными. Чаще всего — кабель, разъём или питание."),
                        ("disk", 15) => L.T("Диск не был готов к работе: пропадал или не отвечал."),
                        ("disk", 51) => L.T("Windows не смогла прочитать или записать данные на диск при обмене с файлом подкачки."),
                        ("disk", 52) => L.T("Диск сам предупреждает о скором отказе (самодиагностика SMART)."),
                        ("stornvme", _) => L.T("Драйвер NVMe сообщил о сбое обмена с диском."),
                        ("storahci", _) => L.T("Драйвер SATA сообщил о сбое обмена с диском. Чаще всего — кабель или разъём."),
                        ("ntfs", 55 or 98) => L.T("Повреждена файловая система: Windows нашла ошибки в служебных данных диска."),
                        ("ntfs", 137) => L.T("Сбой служебного журнала файловой системы (транзакции). Обычно не повреждение диска; если повторяется — проверьте файловую систему."),
                        ("ntfs", _) => L.T("Ошибка файловой системы."),
                        _ => L.T("Ошибка накопителя."),
                    };
                    break;
                }
                ev.Plain = (p, ev.Id) switch
                {
                    ("disk", 7) => L.F("Плохой блок: часть поверхности «{0}» не читается. Это повреждение самого диска.", on),
                    ("disk", 11) => L.F("Ошибка связи с «{0}»: контроллер не смог обменяться данными с диском. Чаще всего — кабель, разъём или питание.", on),
                    ("disk", 15) => L.F("«{0}» не был готов к работе: диск пропадал или не отвечал.", on),
                    ("disk", 51) => L.F("Windows не смогла прочитать или записать данные на «{0}» при обмене с файлом подкачки.", on),
                    ("disk", 52) => L.F("«{0}» сам предупреждает о скором отказе (самодиагностика SMART).", on),
                    ("disk", _) => L.F("Ошибка диска «{0}».", on),
                    ("stornvme", _) => L.F("Драйвер NVMe сообщил о сбое обмена с «{0}».", on),
                    ("storahci", _) => L.F("Драйвер SATA сообщил о сбое обмена с «{0}». Чаще всего — кабель или разъём.", on),
                    ("ntfs", 55 or 98) => L.F("Повреждена файловая система на «{0}»: Windows нашла ошибки в служебных данных диска.", on),
                    ("ntfs", 137) => L.F("Сбой служебного журнала файловой системы на «{0}» (транзакции). Обычно не повреждение диска; если повторяется — проверьте файловую систему.", on),
                    ("ntfs", 140 or 50) => L.F("Windows не смогла записать служебные данные файловой системы на «{0}».", on),
                    ("ntfs", _) => L.F("Ошибка файловой системы на «{0}».", on),
                    _ => L.T("Ошибка накопителя."),
                };
                break;
            case LogMarks.Shutdown:
                long code = 0;
                if (props.Count > 0) long.TryParse(props[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out code);
                ev.Bugcheck = code;
                ev.Plain = code != 0
                    ? L.F("Компьютер перезагрузился из-за синего экрана (код 0x{0:X}).", code)
                    : L.T("Компьютер выключился, не завершив работу: завис, пропало питание, сработала защита блока питания — или его выключили кнопкой.");
                break;
            case LogMarks.Bsod:
                var bc = Regex.Match(all, @"0x0*([0-9a-fA-F]{1,8})\b");
                ev.Plain = bc.Success ? L.F("Синий экран, код 0x{0}.", bc.Groups[1].Value.ToUpperInvariant()) : L.T("Синий экран.");
                break;
            case LogMarks.Gpu:
                ev.Plain = ev.Id == 4101 || p.Contains("display")
                    ? L.T("Драйвер видеокарты перестал отвечать, и Windows перезапустила его (экран на секунду гас).")
                    : L.T("Драйвер видеокарты сообщил о сбое.");
                break;
            case LogMarks.Whea:
                string src = Regex.IsMatch(all, "PCI", RegexOptions.IgnoreCase) ? L.T("шина PCI Express (видеокарта, NVMe, другие платы)")
                    : Regex.IsMatch(all, "Memory|памят", RegexOptions.IgnoreCase) ? L.T("память")
                    : Regex.IsMatch(all, "Processor|Cache|процессор", RegexOptions.IgnoreCase) ? L.T("процессор") : "";
                // Исправленные — уровень «предупреждение» (как в StabilityChecks: неисправленные — уровни 1 и 2).
                bool corrected = ev.Level == 3 || (ev.Level == 0 && ev.Id is 17 or 19 or 47);
                ev.Plain = (corrected ? L.T("Исправленная аппаратная ошибка") : L.T("Аппаратная ошибка")) + (src != "" ? " — " + src : "") + ".";
                break;
            case LogMarks.Crash:
                ev.App = props.Count > 0 ? props[0] : "";
                ev.Module = props.Count > 3 ? props[3] : "";
                ev.Plain = ev.Module != "" && !ev.Module.Equals(ev.App, StringComparison.OrdinalIgnoreCase)
                    ? L.F("«{0}» закрылась с ошибкой в модуле «{1}».", ev.App, ev.Module)
                    : L.F("«{0}» закрылась с ошибкой.", ev.App);
                break;
            case LogMarks.Hang:
                ev.App = props.Count > 0 ? props[0] : "";
                ev.Plain = L.F("«{0}» перестала отвечать и была закрыта.", ev.App);
                break;
            default:
                ev.Plain = L.F("Ошибка от «{0}» (запись {1}).", ev.Provider, ev.Id);
                break;
        }
    }

    /// <summary>Что значит этот вид записей.</summary>
    public static string Meaning(string kind) => kind switch
    {
        LogMarks.Disk => L.T("Windows записывает в журнал, когда не может прочитать или записать данные на накопитель, когда пропадает связь с ним или когда находит повреждения файловой системы. Одна-две записи после внезапного выключения бывают и на исправном диске; повторяющиеся ошибки одного диска — признак неисправности диска, кабеля или разъёма."),
        LogMarks.Shutdown => L.T("Компьютер выключился или перезагрузился, не завершив работу: завис, пропало питание, сработала защита блока питания, случился синий экран — или его выключили кнопкой. Windows записывает это после следующего включения."),
        LogMarks.Bsod => L.T("Синий экран — Windows остановилась из-за критической ошибки драйвера или оборудования. Код ошибки и виновный драйвер разобраны в строке «Синие экраны» на вкладке «Диагностика»."),
        LogMarks.Gpu => L.T("Драйвер видеокарты перестал отвечать, и Windows перезапустила его: экран гас на секунду, игра могла вылететь. Подробный разбор — в разделе «Драйвер» на вкладке «Система»."),
        LogMarks.Whea => L.T("WHEA — сообщения самого оборудования об ошибках: процессор, память, шина PCI Express. Исправленные ошибки оборудование исправило само; неисправленные могли вызвать сбой. Чаще всего их причина — разгон, андервольт или профиль памяти XMP/EXPO."),
        LogMarks.Crash => L.T("Программы, которые закрылись с ошибкой. Модуль — та часть, в которой случилась ошибка: если это файл драйвера видеокарты (nvwgf2umx.dll, atidxx64.dll и подобные), виноват драйвер, а не программа."),
        LogMarks.Hang => L.T("Программы, которые перестали отвечать и были закрыты — Windows или вами."),
        LogMarks.SystemErrors => L.T("Ошибки системного журнала Windows. Многие из них безобидны и бывают на любом компьютере; важно, повторяется ли одна и та же и совпадает ли она по времени с проблемой."),
        _ => "",
    };

    /// <summary>Что делать — по шагам, с учётом самих записей.</summary>
    public static List<string> Steps(string kind, List<LogEvent> events, List<PhysDisk> disks, Top top = null)
    {
        var s = new List<string>();
        top ??= new Top();
        switch (kind)
        {
            case LogMarks.Disk:
            {
                var bad = disks.Where(DiskBad).ToList();
                bool surface = events.Any(e => e.Provider.Equals("disk", StringComparison.OrdinalIgnoreCase) && e.Id is 7 or 51 or 52) || bad.Count > 0;
                bool link = events.Any(e => e.Provider.Equals("storahci", StringComparison.OrdinalIgnoreCase) || e.Provider.Equals("stornvme", StringComparison.OrdinalIgnoreCase)
                                            || (e.Provider.Equals("disk", StringComparison.OrdinalIgnoreCase) && e.Id is 11 or 15));
                bool fs = events.Any(e => e.Provider.Equals("Ntfs", StringComparison.OrdinalIgnoreCase) && e.Id != 137);
                bool hdd = bad.Any(d => d.Kind == "HDD") || events.Any(e => disks.Any(d => d.Kind == "HDD" && e.Disk.StartsWith(d.Name, StringComparison.OrdinalIgnoreCase)));
                if (surface || fs) s.Add(L.T("Сначала скопируйте важные файлы с этого диска на другой диск или в облако — начните с самого ценного, пока он читается."));
                if (hdd || surface) s.Add(L.T("Не запускайте дефрагментацию и не забивайте диск: дефрагментация часами перечитывает и переписывает всё подряд и может добить повреждённый жёсткий диск."));
                if (link) s.Add(L.T("Проверьте подключение: переподключите SATA-кабель или поставьте другой и подключите диск в другой порт на плате; NVMe — переустановите в слот. Сбои связи часто бывают от кабеля, а не от диска."));
                if (fs) s.Add(L.T("Проверьте файловую систему: «Этот компьютер» → правой кнопкой по диску → «Свойства» → «Сервис» → «Проверить». Сначала Windows только ищет ошибки и спрашивает, прежде чем исправлять."));
                var broken = bad.Where(d => !(d.Wear >= 80) || d.ReadErrors > 0 || (d.Health != "" && !d.Health.Equals("Healthy", StringComparison.OrdinalIgnoreCase))).ToList();
                var worn = bad.Except(broken).ToList();
                if (broken.Count > 0) s.Add(L.F("{0} сообщает об ошибках чтения или неисправности — ничего важного на нём не храните и готовьте замену (лучше на SSD).", string.Join(", ", broken.Select(d => d.Name))));
                if (worn.Count > 0) s.Add(L.F("{0}: ресурс записи почти исчерпан — сделайте копию важных данных и присмотрите замену.", string.Join(", ", worn.Select(d => d.Name))));
                else if (surface) s.Add(L.T("Если записи о плохих блоках повторяются, готовьте замену диска (лучше на SSD)."));
                s.Add(L.T("После этих шагов нажмите «Отметить как просмотренные» на вкладке «Диагностика»: если ошибки появятся снова, программа покажет только новые."));
                break;
            }
            case LogMarks.Shutdown:
                if (events.Any(e => e.Bugcheck != 0)) s.Add(L.T("Часть выключений — из-за синего экрана: разбор кода и виновного драйвера — в строке «Синие экраны» на вкладке «Диагностика»."));
                s.Add(L.T("Если вы сами выключали компьютер кнопкой или из розетки — всё в порядке, Windows записывает и это."));
                s.Add(L.T("Если выключается под нагрузкой (в игре) — проверьте блок питания (мощность, разъёмы видеокарты) и температуры: при перегреве или нехватке питания компьютер выключается сам."));
                s.Add(L.T("Если включены разгон, андервольт или профиль памяти XMP/EXPO — верните настройки BIOS по умолчанию и проверьте, повторится ли."));
                break;
            case LogMarks.Bsod:
                s.Add(L.T("Откройте строку «Синие экраны» на вкладке «Диагностика»: там код простыми словами, виновный драйвер и что сделать."));
                s.Add(L.T("Обновите драйвер, на который указывает разбор; если сбои начались после обновления — верните прежнюю версию."));
                s.Add(L.T("Уберите разгон и профиль памяти XMP/EXPO, если они включены, и проверьте, повторится ли."));
                break;
            case LogMarks.Gpu:
                s.Add(L.T("Откройте раздел «Драйвер» на вкладке «Система»: там видно, на какой версии драйвера были сбои."));
                s.Add(L.T("Поставьте другую версию драйвера видеокарты — свежую или, если сбои начались после обновления, прошлую."));
                s.Add(L.T("Уберите разгон видеокарты, если он есть, и проверьте, плотно ли вставлены разъёмы питания видеокарты."));
                break;
            case LogMarks.Whea:
                s.Add(L.T("Верните настройки процессора и памяти в BIOS по умолчанию: разгон, андервольт (Curve Optimizer, PBO) и профиль XMP/EXPO — и проверьте, повторятся ли ошибки."));
                s.Add(L.T("Обновите BIOS материнской платы: производители исправляют в нём нестабильность процессоров и памяти."));
                s.Add(L.T("Если источник — PCI Express: переустановите видеокарту или NVMe в слот и проверьте разъёмы питания."));
                break;
            case LogMarks.Crash:
            {
                var first = top.Items.Count > 0 ? top.Items[0]
                    : events.Where(e => e.App != "").GroupBy(e => e.App, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => (g.Key, g.Count())).FirstOrDefault();
                if (!string.IsNullOrEmpty(first.Item1)) s.Add(L.F("Чаще всего падала «{0}» ({1}). Обновите её или переустановите; у игр — проверьте целостность файлов в лаунчере.", first.Item1, Fmt.Plural(first.Item2, "раз", "раза", "раз")));
                if (events.Any(e => Regex.IsMatch(e.Module, @"^(nvwgf|nvd3d|nvoglv|nvlddmkm|atidxx|amdxx|aticfx|igd)", RegexOptions.IgnoreCase)))
                    s.Add(L.T("Ошибка — в модуле драйвера видеокарты: поставьте другую версию драйвера (раздел «Драйвер» на вкладке «Система»)."));
                s.Add(L.T("Если падают разные программы — проверьте память и стабильность: «Диагностика» → «Проверка памяти Windows», и уберите разгон."));
                break;
            }
            case LogMarks.Hang:
                s.Add(L.T("Зависания чаще всего — нехватка памяти или медленный диск: посмотрите «Почему тормозит прямо сейчас», когда это повторится."));
                s.Add(L.T("Обновите программу, которая зависает чаще всего."));
                break;
            case LogMarks.SystemErrors:
            {
                var three = top.Items.Count > 0 ? top.Items.Take(3).ToList()
                    : events.GroupBy(e => e.Provider, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(3).Select(g => (g.Key, g.Count())).ToList();
                if (three.Count > 0) s.Add(L.F("Чаще всего пишут: {0}. Посмотрите, совпадают ли эти записи по времени с проблемой.", string.Join(", ", three.Select(g => $"{g.Item1} ({g.Item2})"))));
                s.Add(L.T("Если ничего не беспокоит — эти записи можно не трогать: на исправном компьютере их тоже много."));
                break;
            }
        }
        return s;
    }

    // ───────────────────────── «Просмотр событий» ─────────────────────────

    /// <summary>
    /// Скопировать записи вида во временный файл .evtx и открыть его в «Просмотре событий».
    /// Журналы Windows не меняются. Возвращает путь к файлу или текст ошибки.
    /// </summary>
    public static (bool Ok, string Text) OpenInEventViewer(string kind, DateTime since)
    {
        try
        {
            var k = LogMarks.Kinds[kind];
            string dir = Path.Combine(Path.GetTempPath(), "PulsePC-logs");
            Directory.CreateDirectory(dir);
            foreach (var old in Directory.GetFiles(dir, "*.evtx")) { try { if (File.GetCreationTime(old) < DateTime.Now.AddDays(-1)) File.Delete(old); } catch { } }
            string file = Path.Combine(dir, $"{kind}-{DateTime.Now:yyyyMMdd-HHmmss}.evtx");
            // Только сами записи: тексты сообщений «Просмотр событий» на этом же компьютере берёт у Windows сам
            // (выгрузка «вместе с сообщениями» на части систем отказывает).
            EventLogSession.GlobalSession.ExportLog(k.Log, PathType.LogName, LogMarks.Query(kind, since), file, true);
            Process.Start(new ProcessStartInfo("eventvwr.exe", $"/l:\"{file}\"") { UseShellExecute = true });
            return (true, file);
        }
        catch (Exception ex)
        {
            Log.Error("открытие записей в «Просмотре событий»", ex);
            return (false, ex.Message);
        }
    }

    // ───────────────────────── самопроверка ─────────────────────────

    public static string SelfTest()
    {
        var fails = Checks().Where(c => !c.Ok).Select(c => c.Name).ToList();
        int n = Checks().Count();
        return (fails.Count == 0 ? $"OK ({n} проверок)" : "FAIL: " + string.Join("; ", fails));
    }

    internal static IEnumerable<(string Name, bool Ok)> Checks()
    {
        var disks = new List<PhysDisk>
        {
            new() { Name = "ST31000525SV", Media = "HDD", BusType = "SATA", DiskNumber = 2, Letters = new() { "E" }, Health = "Healthy", ReadErrors = 212 },
            new() { Name = "Samsung SSD 990 PRO 1TB", Media = "SSD", BusType = "NVMe", DiskNumber = 0, Letters = new() { "C" }, Health = "Healthy" },
        };
        yield return ("номер диска из пути устройства", DiskNumber(@"\Device\Harddisk2\DR2") == 2 && DiskNumber("нет") == -1);
        yield return ("буква тома из текста", Volume(@"\??\E: том повреждён") == "E:" && Volume("Volume C: is dirty") == "C:");
        var bad = new LogEvent { Provider = "disk", Id = 7 };
        Fill(LogMarks.Disk, bad, new List<string> { @"\Device\Harddisk2\DR2" }, "", disks);
        yield return ("плохой блок — про нужный диск", bad.Disk == "ST31000525SV" && bad.Plain.Contains("ST31000525SV"));
        var ntfs = new LogEvent { Provider = "Ntfs", Id = 55 };
        Fill(LogMarks.Disk, ntfs, new List<string> { "E:" }, "", disks);
        yield return ("файловая система — диск по букве тома", ntfs.Disk.StartsWith("ST31000525SV"));
        var steps = Steps(LogMarks.Disk, new List<LogEvent> { bad }, disks);
        yield return ("диск с ошибками чтения: копия, не дефрагментировать, замена", steps.Count >= 3 && (L.Code != "ru" || steps.Any(x => x.Contains("дефрагмент"))) && steps.Any(x => x.Contains("ST31000525SV")));
        var link = new LogEvent { Provider = "storahci", Id = 129 };
        yield return ("сбой SATA — совет про кабель", Steps(LogMarks.Disk, new List<LogEvent> { link }, new List<PhysDisk>()).Any(x => L.Code != "ru" || x.Contains("кабель")));
        var crash = new LogEvent { Provider = "Application Error", Id = 1000 };
        Fill(LogMarks.Crash, crash, new List<string> { "game.exe", "1.0", "0", "nvwgf2umx.dll" }, "", disks);
        yield return ("падение: программа и модуль", crash.App == "game.exe" && crash.Module == "nvwgf2umx.dll");
        yield return ("падение в модуле видеодрайвера — совет про драйвер", Steps(LogMarks.Crash, new List<LogEvent> { crash }, disks).Count >= 2);
        var off = new LogEvent { Provider = "Microsoft-Windows-Kernel-Power", Id = 41 };
        Fill(LogMarks.Shutdown, off, new List<string> { "278" }, "", disks);
        yield return ("выключение с кодом синего экрана", off.Bugcheck == 278 && off.Plain.Contains("0x116"));
        yield return ("у каждого вида есть объяснение и шаги", LogMarks.Kinds.Keys.All(k => Meaning(k) != "" && Steps(k, new List<LogEvent>(), new List<PhysDisk>()).Count > 0));
        yield return ("«ошибки дисков» без записей о дампе при загрузке (volmgr)", !LogMarks.Kinds[LogMarks.Disk].Filter.Contains("volmgr"));
        yield return ("буква тома: строчная — да, «1F:» и время «12:30» — нет", Volume(@"тома c:\data") == "C:" && Volume("ошибка 1F: x") == "" && Volume("в 12:30 ") == "");
        var unknown = new LogEvent { Provider = "storahci", Id = 129 };
        Fill(LogMarks.Disk, unknown, new List<string> { @"\Device\RaidPort0" }, "", disks);
        yield return ("диск не определён — фраза без ««диске»»", unknown.Disk == "" && !unknown.Plain.Contains("«"));
        var whea = new LogEvent { Provider = "Microsoft-Windows-WHEA-Logger", Id = 47, Level = 3 };
        Fill(LogMarks.Whea, whea, new List<string>(), "Memory", disks);
        yield return ("WHEA уровня «предупреждение» — исправленная", whea.Plain.StartsWith(L.T("Исправленная аппаратная ошибка")));
        var worn = new PhysDisk { Name = "SSD", Media = "SSD", Health = "Healthy", Wear = 85 };
        yield return ("изношенный диск — повод беспокоиться и шаг про замену", DiskBad(worn) && Steps(LogMarks.Disk, new List<LogEvent>(), new List<PhysDisk> { worn }).Any(x => x.Contains("SSD") && (L.Code != "ru" || x.Contains("ресурс"))));
    }
}
