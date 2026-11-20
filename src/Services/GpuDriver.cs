using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text;
using Microsoft.Win32;
using PulsePC.Panels;

namespace PulsePC.Services;

/// <summary>Видеокарта и её драйвер, как они записаны в Windows.</summary>
public sealed class GpuDriverInfo
{
    public string Name = "", Vendor = "", RawVersion = "", Version = "", Service = "";
    /// <summary>Код устройства из Windows: «VEN_10DE&amp;DEV_2803&amp;SUBSYS_…» (по нему спрашивается последняя версия драйвера).</summary>
    public string Pnp = "";
    public DateTime? Date;          // дата выпуска драйвера
    public DateTime? Installed;     // когда драйвер последний раз установлен или переустановлен (из журнала Windows)
    public bool Integrated;
    /// <summary>Работает базовый драйвер Windows: драйвер производителя не установлен.</summary>
    public bool Basic;
    public double VramGb;

    public int AgeDays => Date == null ? -1 : Math.Max(0, (DateTime.Today - Date.Value.Date).Days);
    /// <summary>До четырёх месяцев драйвер считается свежим.</summary>
    public bool Fresh => AgeDays >= 0 && AgeDays <= GpuDriver.FreshDays;
    public string AgeText => AgeDays < 0 ? "" : GpuDriver.Age(AgeDays);
}

public sealed class ShaderCacheFolder
{
    public string Title = "", Path = "";
    public long Bytes;
}

public sealed class DriverEvent
{
    public DateTime Time;
    public string Provider = "", Text = "", Kind = "", Game;
    public int Id;
}

/// <summary>Строка отчёта: заголовок, пояснение, мелкая приписка и число справа.</summary>
public sealed class DriverRow
{
    public string Title { get; set; } = "";
    public string Hint { get; set; } = "";
    public string Extra { get; set; } = "";
    public string Value { get; set; } = "";
    /// <summary>Доля для полосы (0..1).</summary>
    public double Fraction { get; set; }
}

/// <summary>Разбор событий драйвера видеокарты за месяц.</summary>
public sealed class DriverEventReport
{
    public bool Ok;                 // журнал удалось прочитать
    public string Error = "";
    public bool Truncated;          // чтение остановлено по времени или по числу записей
    public double Seconds;
    public int Days = 30;
    /// <summary>Записи до этого времени отмечены как просмотренные и не учитываются; null — отметки нет (или она старше окна).</summary>
    public DateTime? SeenSince;
    /// <summary>«за 30 дней» или «после отметки 19 ноября».</summary>
    public string Period => SeenSince != null ? LogMarks.SeenText(SeenSince.Value)
        : L.Pick("за " + Fmt.Plural(Days, "день", "дня", "дней"), "in the last " + Fmt.Plural(Days, "день", "дня", "дней"));     // короткий шаблон «за {0}» в словаре поймал бы чужие строки
    public List<DriverEvent> Events = new();
    public int Total => Events.Count;
    public int Resets;              // перезапуски драйвера (Display 4101)
    public int OnCurrentDriver = -1;    // сколько событий уже на нынешней установке драйвера; −1 — неизвестно
    public bool Calm;               // делать сейчас ничего не нужно
    public string Headline = "";
    public List<string> Facts = new();
    public List<DriverRow> Kinds = new(), ByDay = new(), Recent = new(), Steps = new();
    public string StepsLead = "";
}

/// <summary>
/// Драйвер видеокарты: версия и возраст, кэш шейдеров, ошибки драйвера в журнале Windows.
/// Всё, кроме очистки кэша, — только чтение. В интернет служба не обращается: страница загрузки открывается в браузере пользователя.
/// </summary>
public static class GpuDriver
{
    public const int FreshDays = 125;       // около четырёх месяцев
    private const string DisplayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    private static PanelHost host;

    public static void Init(PanelHost h) => host = h;      // временных изменений служба не делает, убирать после сбоя нечего

    // ------------------------------------------------------------------ видеокарты и драйвер
    /// <summary>Установленные видеокарты. Главная — первая: дискретная с наибольшей видеопамятью.</summary>
    public static List<GpuDriverInfo> ReadAdapters()
    {
        var list = new List<GpuDriverInfo>();
        try
        {
            using var pci = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI");
            foreach (string dev in pci?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                try
                {
                    using var devKey = pci.OpenSubKey(dev);
                    foreach (string inst in devKey?.GetSubKeyNames() ?? Array.Empty<string>())
                    {
                        try
                        {
                            using var k = devKey.OpenSubKey(inst);
                            if (!string.Equals(k?.GetValue("ClassGUID") as string, DisplayClass, StringComparison.OrdinalIgnoreCase)) continue;
                            string service = k.GetValue("Service") as string ?? "";
                            if (!Present(service, $@"PCI\{dev}\{inst}")) continue;      // в реестре остаются и давно вынутые видеокарты
                            var a = FromClassKey(k.GetValue("Driver") as string, dev, service);
                            if (a != null) list.Add(a);
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        catch { }
        return list.OrderBy(a => a.Basic).ThenBy(a => a.Integrated).ThenByDescending(a => a.VramGb).ToList();
    }

    // Устройства, которые служба драйвера обслуживает прямо сейчас, перечислены в её ключе Enum.
    private static bool Present(string service, string instance)
    {
        if (service == "") return false;
        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}\Enum");
        if (k == null) return false;
        return k.GetValueNames().Any(n => int.TryParse(n, out _) && string.Equals(k.GetValue(n) as string, instance, StringComparison.OrdinalIgnoreCase));
    }

    private static GpuDriverInfo FromClassKey(string driver, string device, string service)
    {
        if (string.IsNullOrEmpty(driver)) return null;
        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{driver}");
        string name = (k?.GetValue("DriverDesc") as string ?? "").Trim();
        if (name == "") return null;
        var a = new GpuDriverInfo
        {
            Name = name, Service = service, RawVersion = k.GetValue("DriverVersion") as string ?? "", Pnp = device ?? "",
            Vendor = GpuVendors.FromPnp(device) is string ven && ven != "" ? ven : HardwareInfo.VendorOf(name),
            Basic = service.Equals("BasicDisplay", StringComparison.OrdinalIgnoreCase),
        };
        a.Integrated = GpuVendors.IsIntegrated(a.Vendor, name);

        object mem = k.GetValue("HardwareInformation.qwMemorySize") ?? k.GetValue("HardwareInformation.MemorySize");
        long bytes = mem switch { long l => l, int i => (uint)i, byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0), byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0), _ => 0 };
        a.VramGb = bytes / Fmt.Gib;

        // Дата выпуска: точное значение лежит рядом в двоичном виде, строка «месяц-день-год» — запасной вариант.
        if (k.GetValue("DriverDateData") is byte[] ft && ft.Length >= 8)
        {
            try { a.Date = DateTime.FromFileTimeUtc(BitConverter.ToInt64(ft, 0)).Date; } catch { }
        }
        if (a.Date == null && DateTime.TryParseExact(k.GetValue("DriverDate") as string, "M-d-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) a.Date = d;
        if (a.Date != null && (a.Date.Value.Year < 2010 || a.Date > DateTime.Today.AddDays(2))) a.Date = null;    // у встроенных драйверов Windows дата условная (2006 год)

        // AMD записывает рядом версию своего пакета («24.3.1»); у старых драйверов — Catalyst_Version («15.7»).
        string radeon = k.GetValue("RadeonSoftwareVersion") as string;
        if (string.IsNullOrWhiteSpace(radeon)) radeon = k.GetValue("Catalyst_Version") as string;
        a.Version = a.Basic ? "" : FriendlyVersion(a.Vendor, a.RawVersion, radeon);
        return a;
    }

    /// <summary>
    /// Версия так, как её называет производитель. NVIDIA «32.0.15.6636» → «566.36» (последние пять цифр);
    /// AMD — версия пакета Adrenalin из реестра («24.3.1»), без неё — как в Windows; Intel — полностью, как на сайте Intel («32.0.101.6913»).
    /// </summary>
    public static string FriendlyVersion(string vendor, string raw, string radeon = null)
    {
        raw = (raw ?? "").Trim();
        if (vendor == "NVIDIA")
        {
            // Windows не дописывает нули в последней части: «32.0.16.3» — это 600.03.
            var p = raw.Split('.');
            if (p.Length == 4 && p.All(x => x.Length > 0 && x.All(char.IsDigit)) && p[3].Length <= 4)
            {
                string digits = p[2] + p[3].PadLeft(4, '0');
                if (digits.Length >= 5)
                {
                    string tail = digits[^5..];
                    return tail[..3].TrimStart('0') + "." + tail[3..];
                }
            }
        }
        if (vendor == "AMD" && !string.IsNullOrWhiteSpace(radeon))
        {
            var m = System.Text.RegularExpressions.Regex.Match(radeon, @"\d+\.\d+(\.\d+)?");
            if (m.Success) return "Adrenalin " + m.Value;
        }
        return raw;
    }

    /// <summary>«11 дней», «7 месяцев», «2 года».</summary>
    public static string Age(int days)
    {
        if (days <= 0) return L.T("меньше дня");
        if (days < 45) return Fmt.Plural(days, "день", "дня", "дней");
        int months = (int)Math.Round(days / 30.44);
        if (months < 24) return Fmt.Plural(months, "месяц", "месяца", "месяцев");
        return Fmt.Plural(months / 12, "год", "года", "лет");
    }

    /// <summary>Официальная страница загрузки драйверов производителя.</summary>
    public static string DownloadUrl(string vendor) => vendor switch
    {
        "NVIDIA" => "https://www.nvidia.com/drivers/",
        "AMD" => "https://www.amd.com/en/support/download/drivers.html",
        // Помощник Intel сам определит видеокарту и предложит нужный драйвер (и для встроенной графики, и для Arc).
        "Intel" => "https://www.intel.com/content/www/us/en/support/detect.html",
        _ => "",
    };

    public static string DriverLine(GpuDriverInfo d)
    {
        if (d == null) return "";
        if (d.Basic) return L.T("Работает базовый драйвер Windows.");
        string version = d.Version == "" ? L.T("неизвестна") : d.Version;
        var sb = new StringBuilder(d.Date != null ? L.F("Версия {0} от {1}", version, LongDate(d.Date.Value)) : L.F("Версия {0}", version));
        if (d.Installed != null) sb.Append(" · " + L.F("установлен {0}", Day(d.Installed.Value)));
        return sb.ToString();
    }

    public static string DriverAdvice(GpuDriverInfo d)
    {
        if (d == null) return L.T("Windows не сообщила ни об одной видеокарте с драйвером.");
        string site = d.Vendor == "" ? L.T("производителя видеокарты") : d.Vendor;
        if (d.Basic) return L.F("Драйвер производителя не установлен, поэтому игры работают медленно или не запускаются. Скачайте драйвер с сайта {0} и установите его.", site);
        if (d.Date == null) return L.T("Дату выпуска драйвера Windows не сообщила, поэтому оценить его возраст нельзя. Сверьте версию с той, что на сайте производителя.");
        string laptop = d.Integrated ? " " + L.T("Для встроенной графики ноутбука надёжнее брать драйвер с сайта производителя ноутбука.") : "";
        if (d.Fresh) return L.F("Драйверу {0} — он свежий. Обновлять стоит, только если новую версию требует игра или в ней исправили вашу проблему.", d.AgeText) + laptop;
        return L.F("Драйверу {0} — стоит обновить: в новых версиях исправляют ошибки и добавляют поддержку свежих игр. Скачайте драйвер с сайта {1} и установите поверх нынешнего.", d.AgeText, site) + laptop;
    }

    /// <summary>
    /// Одна фраза о состоянии: возраст драйвера и ошибки. Если известна последняя версия на сайте производителя
    /// (раздел «Новые версии»): newer — вышла новее установленной, latest — установлена последняя.
    /// </summary>
    public static string Verdict(GpuDriverInfo d, DriverEventReport r, string newer = null, bool latest = false)
    {
        if (d == null) return L.T("Видеокарта с драйвером не найдена");
        if (d.Basic) return L.T("Драйвер видеокарты не установлен");
        bool known = r != null && r.Ok;
        string errors = !known ? "" : r.Total == 0 ? L.T("сбоев за месяц нет")
            : r.OnCurrentDriver == 0 ? L.T("на нём сбоев нет")
            : r.Resets > 0 ? L.F("за месяц он перезапускался {0}", Times(r.Resets))
            : L.F("в журнале {0} о сбоях за месяц", Fmt.Plural(r.Total, "запись", "записи", "записей"));
        bool trouble = known && r.Total > 0 && r.OnCurrentDriver != 0;
        if (!string.IsNullOrEmpty(newer))
        {
            // «на нём» здесь читалось бы как о новом драйвере — говорим о нынешнем прямо.
            string now = known && r.Total > 0 && r.OnCurrentDriver == 0 ? L.T("на нынешнем сбоев нет") : errors;
            return L.F("Вышел драйвер {0}", newer) + (now == "" ? "" : trouble ? "; " + now : ", " + now);
        }
        if (latest) return errors == "" ? L.T("Драйвер последней версии") : trouble ? L.F("Драйвер последней версии, но {0}", errors) : L.F("Драйвер последней версии, {0}", errors);
        if (d.Date == null) return errors == "" ? L.T("Возраст драйвера определить не удалось") : L.F("Возраст драйвера неизвестен, {0}", errors);
        if (d.Fresh) return errors == "" ? L.T("Драйвер свежий") : trouble ? L.F("Драйвер свежий, но {0}", errors) : L.F("Драйвер свежий, {0}", errors);
        return L.F("Драйверу {0} — стоит обновить", d.AgeText) + (errors == "" ? "" : trouble ? "; " + errors : ", " + errors);
    }

    // ------------------------------------------------------------------ кэш шейдеров
    private static (string Title, string Path)[] CacheRoots()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return new[]
        {
            ("NVIDIA · DirectX", Path.Combine(local, "NVIDIA", "DXCache")),
            ("NVIDIA · OpenGL", Path.Combine(local, "NVIDIA", "GLCache")),
            (L.T("NVIDIA · прежнее расположение"), Path.Combine(local, "NVIDIA Corporation", "NV_Cache")),
            (L.T("NVIDIA · общий для всех пользователей"), Path.Combine(common, "NVIDIA Corporation", "NV_Cache")),
            (L.T("DirectX (кэш Windows)"), Path.Combine(local, "D3DSCache")),
            ("AMD · DirectX", Path.Combine(local, "AMD", "DxCache")),
            ("AMD · DirectX 12", Path.Combine(local, "AMD", "DxcCache")),
            ("AMD · Vulkan", Path.Combine(local, "AMD", "VkCache")),
            ("AMD · OpenGL", Path.Combine(local, "AMD", "GLCache")),
            ("Intel", Path.Combine(local, "Intel", "ShaderCache")),
        };
    }

    /// <summary>Найденные папки кэша шейдеров с размерами.</summary>
    public static List<ShaderCacheFolder> CacheFolders()
    {
        var list = new List<ShaderCacheFolder>();
        foreach (var (title, path) in CacheRoots())
        {
            try
            {
                if (!Directory.Exists(path)) continue;
                list.Add(new ShaderCacheFolder { Title = title, Path = path, Bytes = SafeFs.Size(path).Bytes });
            }
            catch { }
        }
        return list;
    }

    /// <summary>Размер кэша шейдеров всех видеокарт. Им же пользуется пункт «Кэш шейдеров видеокарты» в «Очистке».</summary>
    public static long CacheBytes() => CacheFolders().Sum(c => c.Bytes);

    /// <summary>Очистить кэш шейдеров. Во время игры не выполняется; занятые файлы пропускаются.</summary>
    public static (bool Done, string Message) ClearShaderCache()
    {
        var r = ClearShaderCacheEx();
        return (r.Done, r.Message);
    }

    /// <summary>
    /// Единственное место, где чистится кэш шейдеров: и кнопкой «Очистки», и по ссылке из этого раздела.
    /// Freed — сколько освобождено; при идущей игре ничего не удаляется.
    /// </summary>
    public static (bool Done, string Message, long Freed) ClearShaderCacheEx()
    {
        string game = GameHooks.InGame ? GameHooks.Game : GameDetector.Find()?.Name;
        if (game != null)
            return (false, L.F("Сейчас идёт игра{0}: она пользуется этим кэшем. Очистите его после выхода из игры.", game == "" ? "" : " (" + game + ")"), 0);
        var (freed, left) = ClearFolders(CacheRoots().Select(r => r.Path));
        var settings = host?.Settings;
        if (settings != null)
        {
            settings.Set("gpudriver.lastClear", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            var values = settings.Values;
            if (values != null) lock (values) settings.Save(); else settings.Save();
        }
        if (freed == 0 && left == 0) return (true, L.T("Кэш уже пуст."), 0);
        string busy = left > 0 ? " " + L.F("{0} занято работающими программами — эти файлы пропущены.", Fmt.Size(left)) : "";
        return (true, L.F("Освобождено {0}.", Fmt.Size(freed)) + busy + " " + L.T("Первые запуски игр будут дольше: кэш строится заново."), freed);
    }

    /// <summary>Удалить содержимое папок (сами папки остаются). Возвращает, сколько освобождено и сколько осталось из-за занятых файлов.</summary>
    public static (long Freed, long Left) ClearFolders(IEnumerable<string> roots)
    {
        long freed = 0, left = 0;
        foreach (string root in roots)
        {
            try
            {
                if (!SafeFs.IsSafe(root) || !Directory.Exists(root)) continue;
                if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) continue;     // по ссылкам не ходим
                foreach (string entry in Directory.GetFileSystemEntries(root)) freed += SafeFs.Delete(entry);
                left += SafeFs.Size(root).Bytes;
            }
            catch { }
        }
        return (freed, left);
    }

    // ------------------------------------------------------------------ журнал Windows
    private static readonly string[] Providers = { "nvlddmkm", "amdkmdag", "amdwddmg", "amdkmdap", "atikmdag", "atikmpag", "igfx", "igfxn", "igfxnd" };

    /// <summary>
    /// Условие XPath (внутри System[…], без времени) для ошибок драйвера видеокарты любого производителя и перезапусков драйвера.
    /// Одно на всю программу: по нему считают «Диагностика», первичный осмотр, сторож и этот раздел.
    /// </summary>
    public static string EventFilter =>
        "(" + string.Join(" or ", Providers.Select(p => $"Provider[@Name='{p}']")) + " or (Provider[@Name='Display'] and EventID=4101)) and (Level=1 or Level=2 or Level=3)";

    /// <summary>
    /// События драйвера видеокарты в журнале «Система» за последние дни, новые первыми. Читается в фоне, не дольше timeoutMs.
    /// installed — когда установлен нынешний драйвер (если известно); sessions — записанные игры для сопоставления по времени.
    /// seen — отметка «просмотрено» из «Диагностики»: записи до неё не учитываются (в журнале Windows они остаются).
    /// </summary>
    public static DriverEventReport ReadEvents(IReadOnlyList<GameSession> sessions, DateTime? installed, int days = 30, int timeoutMs = 12000, DateTime? seen = null)
    {
        var report = new DriverEventReport { Days = days };
        if (seen != null && seen.Value > DateTime.Now.AddDays(-days)) report.SeenSince = seen;
        var watch = Stopwatch.StartNew();
        var found = new List<DriverEvent>();
        bool truncated = false;
        var task = Task.Run(() =>
        {
            string time = report.SeenSince != null ? LogMarks.TimeFrom(report.SeenSince.Value) : $"TimeCreated[timediff(@SystemTime) <= {days * 86400000L}]";
            string xpath = $"*[System[{EventFilter} and {time}]]";
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, xpath) { ReverseDirection = true });
            for (EventRecord e = reader.ReadEvent(); e != null; e = reader.ReadEvent())
            {
                using (e)
                {
                    try
                    {
                        if (e.TimeCreated == null) continue;
                        lock (found) found.Add(new DriverEvent { Time = e.TimeCreated.Value, Provider = e.ProviderName ?? "", Id = e.Id, Text = TextOf(e) });
                    }
                    catch { }
                }
                if (found.Count >= 5000 || watch.ElapsedMilliseconds > timeoutMs) { truncated = true; break; }
            }
        });
        try
        {
            if (!task.Wait(timeoutMs + 3000)) truncated = true;
            report.Ok = true;
        }
        catch (Exception ex)
        {
            report.Error = (ex is AggregateException a && a.InnerException != null ? a.InnerException : ex).Message;
        }
        lock (found) report.Events = found.OrderByDescending(e => e.Time).ToList();
        report.Truncated = truncated;
        report.Seconds = watch.Elapsed.TotalSeconds;
        if (report.Ok) Analyze(report, sessions ?? Array.Empty<GameSession>(), installed);
        else report.Headline = L.T("Журнал Windows прочитать не удалось");
        return report;
    }

    /// <summary>Когда драйвер последний раз установлен или переустановлен: Windows отмечает это в журнале «Система».</summary>
    public static DateTime? InstalledAt(string service, int timeoutMs = 5000)
    {
        if (string.IsNullOrEmpty(service)) return null;
        DateTime? result = null;
        var task = Task.Run(() =>
        {
            var watch = Stopwatch.StartNew();
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName,
                "*[System[Provider[@Name='Microsoft-Windows-UserPnp'] and (EventID=20003)]]") { ReverseDirection = true });
            for (EventRecord e = reader.ReadEvent(); e != null && watch.ElapsedMilliseconds < timeoutMs; e = reader.ReadEvent())
            {
                using (e)
                {
                    try
                    {
                        if (e.Properties.Count > 0 && string.Equals(e.Properties[0].Value as string, service, StringComparison.OrdinalIgnoreCase))
                        {
                            result = e.TimeCreated;
                            return;
                        }
                    }
                    catch { }
                }
            }
        });
        try { task.Wait(timeoutMs + 2000); } catch { }
        return result;
    }

    // У событий драйвера нет готового описания в Windows: смысл — в строке данных («Graphics Exception: …»).
    private static string TextOf(EventRecord e)
    {
        try
        {
            if (e.ProviderName == "Display") return L.F("драйвер {0}", e.Properties.Count > 0 ? e.Properties[0].Value as string : "");
            foreach (var p in e.Properties)
                if (p.Value is string s && s.Trim().Length > 0 && !s.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
                    return s.Trim();
        }
        catch { }
        return "";
    }

    private static (string Key, string Title, string Meaning, int Severity) Classify(string provider, int id)
    {
        if (provider == "Display")
            return ("reset", L.T("Драйвер перестал отвечать и был перезапущен"),
                L.T("Экран на секунду гас или игра замирала: Windows перезапустила драйвер видеокарты, чтобы компьютер не завис целиком. Это заметный сбой, а не служебная запись."), 3);
        if (provider == "nvlddmkm")
            return id switch
            {
                13 => ("nv13", L.T("Сбой при обработке графики (событие 13)"),
                    L.T("Драйвер NVIDIA отметил, что одна из команд графики не выполнилась. Чаще всего причина в самой игре или программе, и драйвер после этого работает дальше.") + " " +
                    L.T("Windows называет запись «ошибкой», но сама по себе она не означает ни падения игры, ни неисправности видеокарты."), 1),
                14 => ("nv14", L.T("Ошибка в работе видеокарты (событие 14)"),
                    L.T("Драйвер NVIDIA записал ошибку в работе самой видеокарты. Обычно рядом с ней — падение игры или искажения картинки."), 2),
                153 => ("nv153", L.T("Видеокарта восстановилась после ошибки (событие 153)"),
                    L.T("Драйвер NVIDIA сообщил об ошибке видеокарты и восстановил работу. Часто идёт вместе с перезапуском драйвера."), 2),
                _ => ("nv" + id, L.F("Запись драйвера NVIDIA (событие {0})", id), L.T("Драйвер NVIDIA записал неполадку в своей работе."), 1),
            };
        string maker = provider.StartsWith("ig", StringComparison.OrdinalIgnoreCase) ? "Intel" : "AMD";
        return (provider + id, L.F("Запись драйвера {0} (событие {1})", maker, id), L.F("Драйвер {0} записал ошибку или предупреждение при работе видеокарты.", maker), 2);
    }

    // «из 181 события», «из 184 событий»: родительный падеж — слова «события»/«событий» в словаре могут быть заняты под именительный.
    private static string Events(int n) => L.Pick(n + " " + (n % 10 == 1 && n % 100 != 11 ? "события" : "событий"), n == 1 ? "1 event" : n + " events");
    // «3 раза», по-английски «once» / «3 times»: «раз» — и единственное, и множественное число, словарём не различить.
    private static string Times(int n) => L.Pick(Fmt.Plural(n, "раз", "раза", "раз"), n == 1 ? "once" : n + " times");
    private static string Day(DateTime t) => t.ToString(L.En ? "MMMM d" : "d MMMM", L.Culture);
    private static string LongDate(DateTime t) => t.ToString(L.En ? "MMMM d, yyyy" : "d MMMM yyyy", L.Culture);

    private static void Analyze(DriverEventReport r, IReadOnlyList<GameSession> sessions, DateTime? installed)
    {
        var ev = r.Events;
        foreach (var e in ev)
        {
            e.Kind = Classify(e.Provider, e.Id).Key;
            e.Game = sessions.FirstOrDefault(s => e.Time >= s.Start.AddMinutes(-1) && e.Time <= s.End.AddMinutes(1))?.Game;
        }
        r.Resets = ev.Count(e => e.Kind == "reset");
        r.Steps = Steps(sessions);
        if (ev.Count == 0)
        {
            r.Calm = true;
            r.Headline = r.SeenSince != null ? L.F("После отметки {0} новых сбоев драйвера нет", Day(r.SeenSince.Value)) : L.F("Сбоев драйвера {0} нет", r.Period);
            r.Facts.Add(r.SeenSince != null
                ? L.T("Сбоев и перезапусков драйвера с тех пор не было. Записи до отметки остались в журнале Windows — программа их просто не показывает.")
                : L.T("В журнале Windows нет ни сбоев, ни перезапусков драйвера. Делать ничего не нужно."));
            return;
        }

        // --- виды событий
        foreach (var g in ev.GroupBy(e => e.Kind).OrderByDescending(g => Classify(g.First().Provider, g.First().Id).Severity).ThenByDescending(g => g.Count()))
        {
            var first = g.First();
            var c = Classify(first.Provider, first.Id);
            var texts = g.Where(e => e.Text != "").GroupBy(e => e.Text).OrderByDescending(t => t.Count()).Take(2).Select(t => t.Key).ToList();
            string extra = texts.Count == 0 ? "" : L.F("В событии записано: {0}", string.Join("; ", texts));
            string hint = c.Meaning;
            if (texts.Any(t => t.Contains("FECS", StringComparison.OrdinalIgnoreCase)))
                hint += " " + L.T("FECS в тексте события — узел видеокарты, который переключает её между задачами: сбой отмечен в этот момент.");
            r.Kinds.Add(new DriverRow { Title = c.Title, Hint = hint, Extra = extra, Value = g.Count().ToString() });
        }

        // --- по дням
        var days = ev.GroupBy(e => e.Time.Date).OrderByDescending(g => g.Key).ToList();
        int maxDay = days.Max(g => g.Count());
        foreach (var g in days.Take(10))
        {
            DateTime from = g.Min(e => e.Time), to = g.Max(e => e.Time);
            string span = from.ToString("HH:mm") == to.ToString("HH:mm") ? from.ToString("HH:mm") : $"{from:HH:mm}–{to:HH:mm}";
            var games = g.Where(e => e.Game != null).GroupBy(e => e.Game).OrderByDescending(x => x.Count()).Select(x => x.Key).ToList();
            r.ByDay.Add(new DriverRow
            {
                Title = Day(g.Key), Hint = span + (games.Count > 0 ? " · " + string.Join(", ", games) : ""),
                Value = g.Count().ToString(), Fraction = g.Count() / (double)maxDay,
            });
        }
        if (days.Count > 10) r.ByDay.Add(new DriverRow { Title = L.F("ещё {0}", Fmt.Plural(days.Count - 10, "день", "дня", "дней")), Value = days.Skip(10).Sum(g => g.Count()).ToString() });

        // --- серии: много записей подряд — это одна неполадка, а не столько же отдельных сбоев
        var series = new List<List<DriverEvent>>();
        foreach (var e in ev.OrderBy(e => e.Time))
        {
            if (series.Count > 0 && (e.Time - series[^1][^1].Time).TotalMinutes <= 10) series[^1].Add(e);
            else series.Add(new List<DriverEvent> { e });
        }

        // --- последние случаи: по одной строке на серию, новые первыми
        foreach (var s in Enumerable.Reverse(series).Take(5))
        {
            var top = s.GroupBy(e => e.Kind).OrderByDescending(g => g.Count()).First().First();
            string text = s.Where(e => e.Text != "").GroupBy(e => e.Text).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";
            string game = s.Where(e => e.Game != null).GroupBy(e => e.Game).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            string time = s[0].Time.ToString("HH:mm") == s[^1].Time.ToString("HH:mm") ? s[0].Time.ToString("HH:mm") : $"{s[0].Time:HH:mm}–{s[^1].Time:HH:mm}";
            r.Recent.Add(new DriverRow
            {
                Value = s[0].Time.ToString(L.En ? "MMM d" : "d MMM", L.Culture) + ", " + time,
                Title = Classify(top.Provider, top.Id).Title + (s.Count > 1 ? " — " + L.F("{0} подряд", Fmt.Plural(s.Count, "запись", "записи", "записей")) : ""),
                Extra = text, Hint = game != null ? L.F("во время {0}", game) : "",
            });
        }
        var longest = series.OrderByDescending(s => s.Count).First();
        if (ev.Count >= 6 && series.Count * 3 <= ev.Count)
        {
            var span = longest[^1].Time - longest[0].Time;
            string entries = Fmt.Plural(longest.Count, "запись", "записи", "записей");
            r.Facts.Add((span.TotalMinutes < 1
                            ? L.F("Записи идут сериями: их {0}, самая длинная — {1} меньше чем за минуту {2}.", series.Count, entries, Day(longest[0].Time))
                            : L.F("Записи идут сериями: их {0}, самая длинная — {1} за {3} {2}.", series.Count, entries, Day(longest[0].Time), Fmt.Duration(span))) + " " +
                        L.T("Серия — это одна неполадка, записанная много раз подряд, а не столько же отдельных сбоев."));
        }

        // --- нынешний драйвер или прежний
        if (installed != null)
        {
            r.OnCurrentDriver = ev.Count(e => e.Time >= installed.Value);
            DateTime windowStart = r.SeenSince ?? DateTime.Now.AddDays(-r.Days);
            if (r.OnCurrentDriver == 0)
                r.Facts.Add(L.F("Все записи сделаны до {0} — дня, когда установлен нынешний драйвер. На нём сбоев не было.", Day(installed.Value)));
            else if (installed.Value > windowStart && r.OnCurrentDriver < ev.Count)
                r.Facts.Add(L.F("Нынешний драйвер установлен {0}: уже на нём — {1} из {2}, остальные — на прежнем.", Day(installed.Value), r.OnCurrentDriver, Events(ev.Count)));
            else if (installed.Value > windowStart)
                r.Facts.Add(L.F("Все записи появились уже на нынешнем драйвере (установлен {0}).", Day(installed.Value)));
        }

        // --- игры
        if (sessions.Count == 0)
            r.Facts.Add(L.T("Программа ещё не записала ни одной игры, поэтому связать записи с играми пока нельзя."));
        else
        {
            DateTime earliest = sessions.Min(s => s.Start);
            var since = ev.Where(e => e.Time >= earliest).ToList();
            var inGame = ev.Where(e => e.Game != null).ToList();
            if (since.Count == 0)
                r.Facts.Add(L.F("Программа записывает игры с {0}, а все эти события — раньше. Шли ли они во время игры, узнать уже нельзя.", Day(earliest)));
            else if (inGame.Count == 0)
                r.Facts.Add(L.F("Из {0}, появившихся с начала записи игр, ни одно не пришлось на игровую сессию: в играх драйвер работал без сбоев.", Events(since.Count)));
            else
            {
                var byGame = inGame.GroupBy(e => e.Game).OrderByDescending(g => g.Count()).ToList();
                string where = byGame.Count == 1 ? L.F("{0} — во время {1}", inGame.Count, byGame[0].Key)
                    : L.F("{0} — во время игр: {1}", inGame.Count, string.Join(", ", byGame.Take(4).Select(g => $"{g.Key} — {g.Count()}")));
                string rest = since.Count < ev.Count ? " " + L.F("Ещё {0} — до того, как программа начала записывать игры.", ev.Count - since.Count) : "";
                r.Facts.Add(L.F("Из {0} {1}.", Events(ev.Count), where) + rest);
            }
        }
        if (r.Truncated) r.Facts.Add(L.T("Журнал большой, и программа прочитала его не целиком: показаны самые свежие записи."));

        // --- вывод
        bool onlyMild = ev.All(e => Classify(e.Provider, e.Id).Severity <= 1);
        r.Calm = r.OnCurrentDriver == 0 || onlyMild && r.Resets == 0 && series.Count <= 3;
        string records = Fmt.Plural(ev.Count, "запись", "записи", "записей");
        r.Headline = r.OnCurrentDriver == 0 ? L.F("На нынешнем драйвере сбоев нет, а {0} — от прежнего", records)
            : r.Resets > 0 ? L.F("Драйвер перезапускался {0} {1}", Times(r.Resets), r.Period)
            : onlyMild ? L.F("{0} драйвера {1}, перезапусков не было", records, r.Period)
            : L.F("{0} о сбоях драйвера {1}", records, r.Period);
        if (r.SeenSince != null)
            r.Facts.Insert(0, L.F("Учтены только записи после {0}: более ранние вы отметили как просмотренные. В журнале Windows они остались.", Day(r.SeenSince.Value)));
        r.StepsLead = r.Calm
            ? L.T("Сейчас ничего делать не нужно. Если записи появятся снова или игры начнут падать, пройдите шаги по порядку и после каждого проверяйте, прекратились ли записи:")
            : L.T("Пройдите шаги по порядку. После каждого поиграйте день-два и проверьте здесь, появляются ли новые записи:");
    }

    private static List<DriverRow> Steps(IReadOnlyList<GameSession> sessions)
    {
        double maxTemp = sessions.Take(10).Select(s => s.MaxTemp).DefaultIfEmpty(0).Max();
        string temp = maxTemp <= 0 ? L.T("Температуру видеокарты в игре программа показывает в отчёте о сессии: выше 85 °C — пора чистить от пыли.")
            : maxTemp >= 85 ? L.F("В последних играх видеокарта нагревалась до {0:0} °C — это много: почистите её от пыли и проверьте вентиляторы.", maxTemp)
            : L.F("В последних играх видеокарта нагревалась не выше {0:0} °C — перегрева нет.", maxTemp);
        return new List<DriverRow>
        {
            new() { Value = "1", Title = L.T("Обновить драйвер или переустановить его начисто"),
                Hint = L.T("При установке выберите выборочный режим и отметьте «чистую установку» (у AMD — «сброс до заводских настроек»): старые файлы драйвера заменятся целиком. Затем очистите кэш шейдеров.") },
            new() { Value = "2", Title = L.T("Снять разгон"),
                Hint = L.T("Верните видеокарту, процессор и память к штатным частотам и напряжениям — в программах разгона и в BIOS. Нестабильный разгон — частая причина таких записей.") },
            new() { Value = "3", Title = L.T("Проверить питание и температуру видеокарты"),
                Hint = L.T("Кабели питания видеокарты должны быть вставлены до щелчка, а каждый разъём — подключён отдельным кабелем от блока питания.") + " " + temp },
            new() { Value = "4", Title = L.T("Отключить оверлеи"),
                Hint = L.T("Оверлеи Discord, Steam, приложения производителя видеокарты и программы записи экрана встраиваются в игру и нередко вызывают такие сбои. Выключите их на время проверки.") },
        };
    }

    // ------------------------------------------------------------------ самопроверка (только чтение)
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        string fail = null;
        try
        {
            var adapters = ReadAdapters();
            sb.AppendLine($"Видеокарт с драйвером: {adapters.Count}");
            var main = adapters.FirstOrDefault();
            if (main != null) main.Installed = InstalledAt(main.Service);
            foreach (var a in adapters)
                sb.AppendLine($"  {a.Name} [{a.Vendor}{(a.Integrated ? ", встроенная" : "")}{(a.Basic ? ", базовый драйвер" : "")}] служба {a.Service}, {a.VramGb:0.#} ГБ; " +
                              $"версия Windows {a.RawVersion} → {a.Version}; дата {(a.Date == null ? "неизвестна" : a.Date.Value.ToString("dd.MM.yyyy"))}; " +
                              $"возраст {(a.AgeDays < 0 ? "неизвестен" : a.AgeText + (a.Fresh ? " (свежий)" : " (стоит обновить)"))}");
            if (main != null)
            {
                sb.AppendLine("  строка раздела: " + DriverLine(main));
                sb.AppendLine("  совет: " + DriverAdvice(main));
                sb.AppendLine("  страница загрузки: " + DownloadUrl(main.Vendor));
            }
            sb.AppendLine("Формат версии: NVIDIA 32.0.15.6636 → " + FriendlyVersion("NVIDIA", "32.0.15.6636") + "; Intel 31.0.101.5186 → " + FriendlyVersion("Intel", "31.0.101.5186")
                          + "; AMD 31.0.21921.1000 → " + FriendlyVersion("AMD", "31.0.21921.1000", "24.3.1"));
            if (FriendlyVersion("NVIDIA", "32.0.15.6636") != "566.36") fail = "версия NVIDIA переведена неверно";

            var caches = CacheFolders();
            sb.AppendLine($"Папок кэша шейдеров: {caches.Count}, всего {Fmt.Size(caches.Sum(c => c.Bytes))}");
            foreach (var c in caches) sb.AppendLine($"  {c.Title}: {Fmt.Size(c.Bytes)} — {c.Path}");

            var sessions = host?.Sessions() ?? SessionStore.Load();
            var r = ReadEvents(sessions, main?.Installed);
            sb.AppendLine($"Журнал «Система» за {r.Days} дней: {(r.Ok ? "прочитан" : "не прочитан — " + r.Error)} за {r.Seconds:0.0} с, событий {r.Total}{(r.Truncated ? " (чтение остановлено по времени)" : "")}");
            if (!r.Ok) fail ??= "журнал не прочитан: " + r.Error;
            sb.AppendLine("  итог: " + r.Headline);
            foreach (var k in r.Kinds) sb.AppendLine($"  вид: {k.Title} — {k.Value}. {k.Extra}");
            foreach (var d in r.ByDay) sb.AppendLine($"  день: {d.Title} {d.Hint} — {d.Value}");
            foreach (var f in r.Facts) sb.AppendLine("  факт: " + f);
            sb.AppendLine($"  игровых сессий для сопоставления: {sessions.Count}; на нынешнем драйвере событий: {(r.OnCurrentDriver < 0 ? "неизвестно" : r.OnCurrentDriver.ToString())}; спокойный вывод: {(r.Calm ? "да" : "нет")}");
            sb.AppendLine("Фраза раздела: " + Verdict(main, r));
        }
        catch (Exception ex) { fail = ex.GetType().Name + ": " + ex.Message; }
        return (fail == null ? "OK" : "FAIL: " + fail) + "\n" + sb;
    }
}
