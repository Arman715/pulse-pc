using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PulsePC.Panels;

namespace PulsePC.Services;

/// <summary>Последний драйвер NVIDIA для этой видеокарты — ответ официального сервиса NVIDIA.</summary>
public sealed class NvidiaRelease
{
    public string Version = "", Name = "", DetailsUrl = "", DownloadUrl = "";
    public DateTime? Date;
    public bool Beta, Whql;
}

/// <summary>Новая версия «Пульс ПК» из источника обновлений.</summary>
public sealed class AppRelease
{
    public string Version = "", Url = "", Sha256 = "", Date = "";
    /// <summary>Подпись ECDSA P-256 (Base64) из файла «&lt;установщик&gt;.sig» того же выпуска; пусто — подписи нет.</summary>
    public string Signature = "";
    public List<string> Notes = new();
}

public enum FeedKind { None, Manifest, GitHub, Invalid }

/// <summary>BIOS и плата: версия, дата, где смотреть новую версию.</summary>
public sealed class BiosInfo
{
    public string Version = "", Vendor = "", BoardMaker = "", Maker = "", Board = "", Model = "", BoardVersion = "", SystemMaker = "", SystemModel = "";
    public DateTime? Date;
    public bool Laptop;
    /// <summary>Страница поддержки платы у производителя (пусто — шаблона нет) и подпись ссылки.</summary>
    public string PageUrl = "", PageTitle = "", SearchUrl = "";
    public int AgeDays => Date == null ? -1 : Math.Max(0, (DateTime.Today - Date.Value.Date).Days);
}

/// <summary>Что известно о драйвере видеокарты по последней проверке (для разделов «Драйвер» и «Новые версии»).</summary>
public sealed class DriverStatus
{
    /// <summary>Производитель проверяется программой (сейчас — только NVIDIA).</summary>
    public bool Checkable;
    public bool Checked;            // проверка была и ответ есть
    public string Error = "";       // последняя проверка не удалась
    public NvidiaRelease Latest;
    /// <summary>&gt;0 — на сайте новее установленного, 0 — установлена последняя, &lt;0 — установлена новее (бета или для разработчиков).</summary>
    public int Diff;
    public DateTime? CheckedAt;
    public bool Newer => Checked && Diff > 0;
    public bool UpToDate => Checked && Diff <= 0;
}

/// <summary>
/// «Новые версии»: драйвер видеокарты (NVIDIA — по официальному сервису NVIDIA), BIOS (только сведения и ссылка:
/// сайты производителей плат программам не отвечают), обновление самой программы из настраиваемого источника.
/// Драйвер и BIOS программа не скачивает и не ставит. Себя обновляет по кнопке или (если включено) сама вне игры — с проверкой SHA-256 и подписи.
/// Запросы в интернет — раз в сутки (если не выключено) и кнопкой; в них нет ничего о пользователе:
/// код видеокарты и версия Windows для NVIDIA, адрес источника — для обновления программы.
/// </summary>
public static class VersionCheck
{
    /// <summary>Источник обновлений по умолчанию: выпуски публичного репозитория на GitHub (там только установщики).</summary>
    public const string DefaultFeed = "Arman715/pulse-pc";
    public const int TimeoutMs = 15000;
    public const string SectionTitle = "Новые версии";
    // Язык: причины сбоев (error) и сообщения Download/Launch/WaitForInstaller остаются здесь по-русски — их сохраняют
    // в настройках и проверяет самопроверка (Contains("подпись") и т. п.). Переводит их тот, кто показывает: L.T(текст),
    // английские строки и шаблоны с {0} для них — в Lang\en\g4.json.
    private const string Nb = "\u00A0";

    private const string NvidiaService =
        "https://gfwsl.geforce.com/nvidia_web_services/controller.gfeclientcontent.NG.php/com.nvidia.services.GFEClientContent_NG.getDispDrvrByDevid/";
    /// <summary>Адрес API GitHub (самопроверка подменяет его на локальный сервер).</summary>
    internal static string GitHubApi = "https://api.github.com";

    private static PanelHost host;
    private static Timer timer;
    private static readonly SemaphoreSlim gate = new(1, 1);
    /// <summary>Проверка закончилась (из фонового потока): открытый раздел перечитывает состояние.</summary>
    public static event Action Changed;
    public static bool Checking { get; private set; }

    private static readonly HttpClient http = CreateClient(true);
    /// <summary>Для обновления программы: перенаправления не выполняются сами — каждый адрес проверяется (только GitHub, только https).</summary>
    private static readonly HttpClient strict = CreateClient(false);

    /// <summary>Официальный репозиторий, из которого программа ставит свои обновления (самопроверка подменяет его).</summary>
    internal static string OfficialRepo = DefaultFeed;
    /// <summary>Самопроверка: разрешить http://127.0.0.1 вместо GitHub. В обычной работе всегда false.</summary>
    internal static bool TestLoopback;

    private static HttpClient CreateClient(bool redirects)
    {
        var c = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromMilliseconds(TimeoutMs),
            AllowAutoRedirect = redirects,
        }) { Timeout = Timeout.InfiniteTimeSpan };
        // Честное имя программы: GitHub без него не отвечает. Больше о компьютере ничего не передаётся.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PulsePC/" + Installer.Version);
        return c;
    }

    // ================================================================== запуск программы

    public static void Init(PanelHost h)
    {
        if (host != null || h == null) return;
        host = h;
        started = DateTime.Now;
        Task.Run(CleanupOld);
        // Первая проверка — через минуту после запуска (не мешать старту), дальше раз в 3 часа смотрим, не прошли ли сутки.
        timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(60), TimeSpan.FromHours(3));
        // Обновлено ли с прошлого запуска (уведомление «Обновлено до …») и обновление без нажатия (раз в минуту — дешёвые проверки).
        Task.Run(() => { Thread.Sleep(6000); try { AfterUpdate(h.Settings); } catch (Exception ex) { Log.Error("после обновления", ex); } });
        autoTimer = new Timer(_ => AutoTick(), null, TimeSpan.FromSeconds(75), TimeSpan.FromMinutes(1));
    }

    private static void Tick()
    {
        var s = host?.Settings;
        if (s == null) return;
        try
        {
            if (AutoCheck(s) && Due(s)) Check(s);
            NotifyNew(s);
        }
        catch { }
    }

    /// <summary>Проверять самим раз в сутки (по умолчанию да).</summary>
    public static bool AutoCheck(AppSettings s) => s.GetBool("versions.auto", true);

    /// <summary>Пора проверить: не проверялось или прошло почти сутки.</summary>
    public static bool Due(AppSettings s) => LastChecked(s) is not DateTime t || (DateTime.Now - t).TotalHours >= 23;

    public static DateTime? LastChecked(AppSettings s) => ReadTime(s.Get("versions.checked"));

    /// <summary>Уведомление у часов — один раз на каждую новую версию и не посреди игры.</summary>
    private static void NotifyNew(AppSettings s)
    {
        if (host == null || GameHooks.InGame) return;
        bool changed = false;
        var app = AppStatus(s);
        if (app.Release != null && app.Diff > 0 && s.Get("update.notified") != app.Release.Version)
        {
            s.Set("update.notified", app.Release.Version);
            changed = true;
            // Название раздела вписано в текст: SectionTitle — ключ раздела, он не переводится.
            var auto = AutoState();
            string how = Portable.Enabled ? L.T("Переносная версия обновляется вручную: ссылка на новую версию — в разделе «Новые версии» вкладки «Система».")
                : AutoInstall(s) && auto.Ok ? L.T("Она установится сама, когда вы не будете играть. Можно и сразу — кнопкой в разделе «Новые версии» вкладки «Система».")
                : !AutoAsked(s) && auto.Ok ? L.T("Обновить можно в разделе «Новые версии» вкладки «Система». Там же можно разрешить ставить обновления без нажатия.")
                : L.T("Обновить можно в разделе «Новые версии» вкладки «Система».");
            host.Notify(L.T("Вышла новая версия «Пульс ПК»"), L.F("Версия {0}.", app.Release.Version) + " " + how,
                false, () => host.Open("sys:" + SectionTitle));
        }
        var gpu = SafeMainGpu();
        var d = DriverStatusFor(gpu, s);
        if (d.Newer && s.Get("versions.nv.notified") != d.Latest.Version)
        {
            s.Set("versions.nv.notified", d.Latest.Version);
            changed = true;
            host.Notify(L.T("Вышел новый драйвер видеокарты"), d.Latest.Date is DateTime dt
                    ? L.F("NVIDIA {0} от {1}. Сейчас установлен {2}.", d.Latest.Version, DayMonth(dt), gpu.Version)
                    : L.F("NVIDIA {0}. Сейчас установлен {1}.", d.Latest.Version, gpu.Version),
                false, () => host.Open("sys:" + SectionTitle));
        }
        if (changed) Save(s);
    }

    /// <summary>
    /// Убрать скачанные установщики версий не новее установленной, недокачанные файлы и распакованное установщиком.
    /// Только в защищённой папке (%ProgramData%\PulsePC\update): в папке пользователя программа с правами администратора
    /// ничего не удаляет — туда может подложить ссылку любая программа.
    /// </summary>
    private static void CleanupOld()
    {
        try
        {
            if (!Shell.IsAdmin() || !UpdateFolderProtected) return;
            string dir = UpdateFolder;
            if (!Directory.Exists(dir) || IsReparse(dir)) return;
            foreach (string f in Directory.GetFiles(dir))
            {
                string name = Path.GetFileName(f);
                var m = Regex.Match(name, @"^PulsePC-Setup-(\d+(?:\.\d+){1,3})\.exe(\.part)?$", RegexOptions.IgnoreCase);
                if (!m.Success) continue;       // чужие файлы не трогаем
                if (m.Groups[2].Success || CompareVersions(m.Groups[1].Value, Installer.Version) <= 0)
                    try { File.Delete(f); } catch { }
            }
            // Сюда распаковывается запущенный установщик; после обновления он уже не нужен (занятые файлы останутся до следующего раза).
            string run = Path.Combine(dir, RunFolderName);
            if (Directory.Exists(run) && !SetupRunning())
            {
                try { SecureStore.DeleteTree(run); } catch (Exception ex) { Log.Error("очистка папки распаковки установщика", ex); }
            }
        }
        catch { }
    }

    /// <summary>Запущен ли установщик программы (процесс «PulsePC-Setup…»): его распакованные библиотеки трогать нельзя.</summary>
    private static bool SetupRunning()
    {
        try
        {
            return Process.GetProcesses().Count(p =>
            {
                using (p)
                {
                    try { return p.ProcessName.StartsWith("PulsePC-Setup", StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                }
            }) > 0;
        }
        catch { return false; }
    }

    private static bool IsReparse(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch { return false; }
    }

    /// <summary>Сохранить настройки раздела (из окна программы).</summary>
    public static void SaveSettings(AppSettings s) => Save(s);

    /// <summary>Сохранять настройки может только запущенная программа (Init вызван): снимок раздела не трогает настоящий settings.json.</summary>
    private static void Save(AppSettings s)
    {
        if (host == null) return;
        var values = s.Values;
        if (values != null) lock (values) s.Save(); else s.Save();
    }

    // ================================================================== проверка

    /// <summary>
    /// Проверить всё, что проверяется по сети: драйвер NVIDIA и источник обновлений программы. Итог — в настройках
    /// (ключи versions.* и update.*). Если проверка уже идёт — дождаться её. Вызывать в фоне.
    /// </summary>
    public static void Check(AppSettings s)
    {
        if (!gate.Wait(0))
        {
            // Уже проверяем (кнопка во время плановой проверки): дождаться той же.
            if (gate.Wait(TimeoutMs * 3)) gate.Release();
            return;
        }
        Checking = true;
        try
        {
            var gpu = SafeMainGpu();
            string dev = DeviceId(gpu);
            if (gpu != null && gpu.Vendor == "NVIDIA" && dev != "")
            {
                var rel = QueryNvidia(dev, out string error);
                s.Set("versions.nv.dev", dev);
                s.Set("versions.nv.checked", Stamp(DateTime.Now));
                s.Set("versions.nv.error", rel == null ? error : null);
                if (rel != null)
                {
                    s.Set("versions.nv.version", rel.Version);
                    s.Set("versions.nv.date", rel.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    s.Set("versions.nv.details", rel.DetailsUrl);
                    s.Set("versions.nv.download", rel.DownloadUrl);
                    s.Set("versions.nv.beta", rel.Beta ? "1" : "0");
                }
            }

            string feed = Feed(s);
            s.Set("update.source", feed);
            if (Classify(feed, out _) is FeedKind.Manifest or FeedKind.GitHub)
            {
                var app = QueryFeed(feed, out string error);
                s.Set("update.checked", Stamp(DateTime.Now));
                s.Set("update.error", app == null ? error : null);
                s.Set("update.version", app?.Version);
                s.Set("update.url", app?.Url);
                s.Set("update.sha256", app?.Sha256);
                s.Set("update.date", app?.Date);
                s.Set("update.notes", app == null ? null : string.Join("\n", app.Notes));
            }
            else
            {
                foreach (string k in new[] { "update.checked", "update.error", "update.version", "update.url", "update.sha256", "update.date", "update.notes" }) s.Set(k, null);
            }
            s.Set("versions.checked", Stamp(DateTime.Now));
            Save(s);
        }
        finally
        {
            Checking = false;
            gate.Release();
            try { Changed?.Invoke(); } catch { }
        }
    }

    private static string Stamp(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static DateTime? ReadTime(string v) =>
        DateTime.TryParseExact(v ?? "", "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    /// <summary>«сегодня в 11:54», «вчера в 20:10», «17 ноября в 09:03» (по-английски «today at 11:54», «November 17 at 09:03»).</summary>
    public static string When(DateTime t)
    {
        string time = t.ToString("HH:mm", L.Culture);
        if (t.Date == DateTime.Today) return L.F("сегодня в {0}", time);
        if (t.Date == DateTime.Today.AddDays(-1)) return L.F("вчера в {0}", time);
        return DayMonth(t) + L.Pick(" в ", " at ") + time;
    }

    /// <summary>«17 ноября» / «November 17» — день и месяц на языке интерфейса.</summary>
    public static string DayMonth(DateTime t) => t.ToString(L.En ? "MMMM d" : "d MMMM", L.Culture);

    // ================================================================== драйвер NVIDIA

    public static GpuDriverInfo SafeMainGpu()
    {
        try { return GpuDriver.ReadAdapters().FirstOrDefault(a => !a.Basic); } catch { return null; }
    }

    /// <summary>«VEN_10DE&amp;DEV_2803&amp;SUBSYS_…» → «2803».</summary>
    public static string DeviceId(GpuDriverInfo g)
    {
        var m = Regex.Match(g?.Pnp ?? "", @"DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : "";
    }

    /// <summary>Адрес запроса к сервису NVIDIA: код видеокарты, Windows 10/11 64-бит, английский язык ответа, драйверы DCH.</summary>
    public static string NvidiaUrl(string dev)
    {
        string json = "{\"dIDa\":[\"" + dev + "_10DE\"],\"osC\":\"10.0\",\"osB\":\"64\",\"is6\":\"1\",\"lg\":\"1033\",\"iLp\":\"1\",\"prvMd\":\"0\"," +
                      "\"gcV\":\"3.25.1.27\",\"gIsB\":\"0\",\"dch\":\"1\",\"upCRD\":\"0\",\"isCRD\":\"0\"}";
        return NvidiaService + Uri.EscapeDataString(json);
    }

    public static NvidiaRelease QueryNvidia(string dev, out string error, int timeoutMs = TimeoutMs)
    {
        error = "";
        if (!Regex.IsMatch(dev ?? "", "^[0-9A-F]{4}$")) { error = "код видеокарты не прочитан"; return null; }
        try { return ParseNvidia(FetchText(NvidiaUrl(dev), timeoutMs), out error); }
        catch (Exception ex) { error = NetError(ex, "NVIDIA"); return null; }
    }

    /// <summary>Разбор ответа сервиса NVIDIA (DriverAttributes).</summary>
    public static NvidiaRelease ParseNvidia(string json, out string error)
    {
        error = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("criteria", out var cr) && cr.TryGetProperty("IsSupported", out var sup) && Str(sup, "state") == "false")
            {
                error = "NVIDIA не нашла драйвер для этой видеокарты";
                return null;
            }
            if (!root.TryGetProperty("DriverAttributes", out var a) || a.ValueKind != JsonValueKind.Object)
            {
                error = "в ответе NVIDIA нет сведений о драйвере";
                return null;
            }
            var r = new NvidiaRelease
            {
                Version = Str(a, "Version"),
                Name = Str(a, "NameLocalized") is { Length: > 0 } n ? n : Str(a, "Name"),
                DetailsUrl = Str(a, "DetailsURL"),
                DownloadUrl = Str(a, "DownloadURLAdmin") is { Length: > 0 } u ? u : Str(a, "DownloadURL"),
                Beta = Num(a, "IsBeta") == 1,
                Whql = Num(a, "IsWHQL") == 1,
            };
            string date = Str(a, "ReleaseDateTime");
            if (date.Length >= 10 && DateTime.TryParseExact(date[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) r.Date = d;
            if (!Regex.IsMatch(r.Version, @"^\d{3}\.\d{2}$")) { error = "NVIDIA прислала непонятный номер версии «" + r.Version + "»"; return null; }
            // Ссылки — только на сайты NVIDIA.
            if (!NvidiaHost(r.DetailsUrl)) r.DetailsUrl = "";
            if (!NvidiaHost(r.DownloadUrl)) r.DownloadUrl = "";
            return r;
        }
        catch (JsonException) { error = "NVIDIA ответила не так, как ожидалось"; return null; }
    }

    private static bool NvidiaHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && (u.Host.Equals("nvidia.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".nvidia.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Что известно о драйвере по последней проверке (без запросов в сеть).</summary>
    public static DriverStatus DriverStatusFor(GpuDriverInfo gpu, AppSettings s)
    {
        var st = new DriverStatus { Checkable = gpu != null && gpu.Vendor == "NVIDIA" && !gpu.Basic };
        if (!st.Checkable || s == null) return st;
        // Ответ был для этой же видеокарты? После замены видеокарты старый ответ не годится.
        if (s.Get("versions.nv.dev") != DeviceId(gpu)) return st;
        st.CheckedAt = ReadTime(s.Get("versions.nv.checked"));
        st.Error = s.Get("versions.nv.error");
        string v = s.Get("versions.nv.version");
        if (v == "") return st;
        st.Latest = new NvidiaRelease
        {
            Version = v, DetailsUrl = s.Get("versions.nv.details"), DownloadUrl = s.Get("versions.nv.download"), Beta = s.GetBool("versions.nv.beta"),
            Date = DateTime.TryParseExact(s.Get("versions.nv.date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
        };
        st.Checked = gpu.Version != "";
        st.Diff = st.Checked ? CompareVersions(v, gpu.Version) : 0;
        return st;
    }

    /// <summary>NVIDIA App установлена (через неё драйвер обновляется в два нажатия).</summary>
    public static string NvidiaAppPath()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string exe = Path.Combine(pf, "NVIDIA Corporation", "NVIDIA App", "CEF", "NVIDIA App.exe");
        return File.Exists(exe) ? exe : null;
    }

    // ================================================================== версии

    /// <summary>Сравнение версий по частям: «621.24» &lt; «625.10», «1.10.0» &gt; «1.9.3»; «v1.4.1» = «1.4.1». Нечисловые части — как 0.</summary>
    public static int CompareVersions(string a, string b)
    {
        static int[] Parts(string v) => Regex.Replace((v ?? "").Trim(), @"^[vV]", "").Split('.', '-', ' ')
            .Select(p => int.TryParse(Regex.Match(p, @"^\d+").Value, out int n) ? n : 0).ToArray();
        var x = Parts(a);
        var y = Parts(b);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0;
            if (p != q) return p.CompareTo(q);
        }
        return 0;
    }

    private static string CleanVersion(string v)
    {
        var m = Regex.Match((v ?? "").Trim(), @"^[vV]?(\d+(?:\.\d+){1,3})$");
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>Версия ровно из четырёх частей, недостающие — 0: «1.4.2» → «1.4.2.0». Так версия входит в подписанный текст.</summary>
    public static string Version4(string v)
    {
        var p = Regex.Replace((v ?? "").Trim(), @"^[vV]", "").Split('.')
            .Select(x => int.TryParse(Regex.Match(x, @"^\d+").Value, out int n) ? n : 0).ToList();
        while (p.Count < 4) p.Add(0);
        return string.Join(".", p.Take(4));
    }

    // ================================================================== обновление программы: источник

    public static string Feed(AppSettings s) => (s?.Get("update.feed", DefaultFeed) ?? DefaultFeed).Trim();

    /// <summary>
    /// Вид источника: пусто — не настроен; «владелец/репозиторий», «github:владелец/репозиторий» или адрес github.com — GitHub;
    /// адрес https://… — манифест update.json. Локальные файлы и file:/// больше не принимаются: настройки лежат в папке
    /// пользователя, и подставить туда путь может любая программа.
    /// Источник только сообщает о новой версии; сам установщик берётся всегда из официального выпуска на GitHub (OfficialRepo).
    /// </summary>
    public static FeedKind Classify(string feed, out string normalized)
    {
        normalized = (feed ?? "").Trim();
        if (normalized == "") return FeedKind.None;
        const string repo = @"([A-Za-z0-9][A-Za-z0-9-]{0,38})/([A-Za-z0-9._-]{1,100}?)(?:\.git)?/?$";
        var gh = Regex.Match(normalized, @"^(?:github:|https://(?:www\.)?github\.com/)?" + repo, RegexOptions.IgnoreCase);
        if (gh.Success)
        {
            normalized = gh.Groups[1].Value + "/" + gh.Groups[2].Value;
            return FeedKind.GitHub;
        }
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var u) && !u.IsFile && !u.IsUnc && Allowed(u)) return FeedKind.Manifest;
        return FeedKind.Invalid;
    }

    /// <summary>Откуда можно читать сведения о версии: только https (http://127.0.0.1 — лишь в самопроверке).</summary>
    public static bool Allowed(Uri u) =>
        u.Scheme == Uri.UriSchemeHttps && u.UserInfo == ""
        || TestLoopback && u.Scheme == Uri.UriSchemeHttp && u.IsLoopback;

    /// <summary>
    /// Адрес из цепочки скачивания обновления: только https на стандартном порту и только GitHub.
    /// Первый адрес — API (api.github.com/repos/&lt;OfficialRepo&gt;/…) или файл выпуска (github.com/&lt;OfficialRepo&gt;/releases/download/…);
    /// после перенаправления — ещё хранилища файлов GitHub (objects/release-assets.githubusercontent.com).
    /// </summary>
    public static bool OfficialHop(Uri u, bool first, bool api)
    {
        if (u == null || !u.IsAbsoluteUri) return false;
        if (TestLoopback && u.Scheme == Uri.UriSchemeHttp && u.IsLoopback) return true;
        if (u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort || u.UserInfo != "") return false;
        string host = u.IdnHost.ToLowerInvariant(), path = u.AbsolutePath;
        string repo = "/" + OfficialRepo.Trim('/');
        if (api)
            return host == "api.github.com" && (path.StartsWith("/repos" + repo + "/", StringComparison.OrdinalIgnoreCase) || !first && path.StartsWith("/repositories/", StringComparison.Ordinal));
        if (host == "github.com") return path.StartsWith(repo + "/releases/download/", StringComparison.OrdinalIgnoreCase);
        return !first && host is "objects.githubusercontent.com" or "release-assets.githubusercontent.com";
    }

    /// <summary>Узнать последнюю версию в источнике. null — не удалось, причина в error.</summary>
    public static AppRelease QueryFeed(string feed, out string error, int timeoutMs = TimeoutMs)
    {
        error = "";
        try
        {
            switch (Classify(feed, out string norm))
            {
                case FeedKind.Manifest:
                {
                    var baseUri = new Uri(norm);     // и адрес, и полный путь к файлу (станет file:///…)
                    return ParseManifest(FetchText(baseUri.AbsoluteUri, timeoutMs), baseUri, out error);
                }
                case FeedKind.GitHub:
                {
                    string api = $"{GitHubApi.TrimEnd('/')}/repos/{norm}/releases/latest";
                    return ParseGitHub(FetchText(api, timeoutMs, github: true), url => FetchText(url, timeoutMs), out error);
                }
                case FeedKind.None: error = "источник обновлений не настроен"; return null;
                default: error = "источник записан непонятно: нужен адрес https://…/update.json или «владелец/репозиторий» на GitHub"; return null;
            }
        }
        catch (Exception ex) { error = NetError(ex, "Источник обновлений"); return null; }
    }

    /// <summary>Манифест: {"version","url","sha256","date","notes":[…]}; url может быть относительным — от адреса манифеста.</summary>
    public static AppRelease ParseManifest(string json, Uri baseUri, out string error)
    {
        error = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var rel = new AppRelease
            {
                Version = CleanVersion(Str(r, "version")),
                Sha256 = Str(r, "sha256").Trim().ToLowerInvariant(),
                Date = Str(r, "date"),
                Notes = Notes(r),
            };
            if (rel.Version == "") { error = "в манифесте нет номера версии"; return null; }
            string url = Str(r, "url");
            if (url == "" || !Uri.TryCreate(baseUri, url, out var abs) || !Allowed(abs)) { error = "в манифесте нет подходящего адреса установщика (нужен https)"; return null; }
            rel.Url = abs.AbsoluteUri;
            if (!IsSha(rel.Sha256)) { error = "в манифесте нет контрольной суммы SHA-256"; return null; }
            return rel;
        }
        catch (JsonException) { error = "манифест обновления испорчен (это не JSON)"; return null; }
    }

    private static List<string> Notes(JsonElement r)
    {
        var list = new List<string>();
        if (r.TryGetProperty("notes", out var n))
        {
            if (n.ValueKind == JsonValueKind.Array)
                foreach (var x in n.EnumerateArray()) { if (x.ValueKind == JsonValueKind.String && x.GetString().Trim() is { Length: > 0 } t) list.Add(t); }
            else if (n.ValueKind == JsonValueKind.String) list.AddRange(SplitNotes(n.GetString()));
        }
        return list.Take(12).ToList();
    }

    /// <summary>Описание выпуска строками: без пустых, маркеров списка и заголовков Markdown.</summary>
    private static IEnumerable<string> SplitNotes(string text) =>
        (text ?? "").Replace("\r", "").Split('\n')
            .Select(l => Regex.Replace(l.Trim(), @"^([-*•]|#+|\d+[.)])\s*", "").Trim())
            .Where(l => l.Length > 0);

    /// <summary>
    /// Последний выпуск официального репозитория — то, что действительно скачивается и ставится. Запрос всегда заново
    /// (сохранённому в settings.json не верим: файл в папке пользователя), только https и только GitHub, с подписью «.sig».
    /// </summary>
    public static AppRelease QueryOfficial(out string error, int timeoutMs = TimeoutMs)
    {
        error = "";
        try
        {
            string api = $"{GitHubApi.TrimEnd('/')}/repos/{OfficialRepo}/releases/latest";
            if (!OfficialHop(new Uri(api), true, true)) { error = "адрес API GitHub не подходит"; return null; }
            return ParseGitHub(FetchOfficialText(api, timeoutMs, true), url => FetchOfficialText(url, timeoutMs, false), out error, official: true);
        }
        catch (Exception ex) { error = NetError(ex, "GitHub"); return null; }
    }

    /// <summary>
    /// Ответ GitHub releases/latest: версия из tag_name, установщик — PulsePC-Setup-*.exe среди файлов выпуска,
    /// SHA-256 — из файла «&lt;установщик&gt;.sha256» или из update.json того же выпуска.
    /// official — разбор для установки: адреса только из официального репозитория, имя установщика совпадает с версией,
    /// подпись «&lt;установщик&gt;.sig» скачивается (обязательна ли она, решает Download).
    /// </summary>
    public static AppRelease ParseGitHub(string json, Func<string, string> fetch, out string error, bool official = false)
    {
        error = "";
        Func<Uri, bool> ok = official ? u => OfficialHop(u, true, false) : Allowed;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var rel = new AppRelease { Version = CleanVersion(Str(r, "tag_name")) };
            if (rel.Version == "") rel.Version = CleanVersion(Regex.Match(Str(r, "name"), @"\d+(\.\d+){1,3}").Value);
            string published = Str(r, "published_at");
            if (published.Length >= 10) rel.Date = published[..10];
            rel.Notes = SplitNotes(Str(r, "body")).Take(12).ToList();

            var assets = new List<(string Name, string Url)>();
            if (r.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array)
                foreach (var x in a.EnumerateArray()) assets.Add((Str(x, "name"), Str(x, "browser_download_url")));
            var exe = assets.FirstOrDefault(x => Regex.IsMatch(x.Name, @"^PulsePC-Setup-[\d.]+\.exe$", RegexOptions.IgnoreCase));
            if (exe.Name == null) { error = "в последнем выпуске нет файла PulsePC-Setup-….exe"; return null; }
            if (!Uri.TryCreate(exe.Url, UriKind.Absolute, out var exeUri) || !ok(exeUri))
            {
                error = official ? "адрес установщика в выпуске не из официального репозитория GitHub" : "адрес установщика в выпуске не https";
                return null;
            }
            rel.Url = exeUri.AbsoluteUri;
            string nameVersion = CleanVersion(Regex.Match(exe.Name, @"\d+(\.\d+){1,3}").Value);
            if (rel.Version == "") rel.Version = nameVersion;
            if (rel.Version == "") { error = "у последнего выпуска нет номера версии"; return null; }
            if (official && Version4(nameVersion) != Version4(rel.Version)) { error = $"имя установщика ({exe.Name}) не совпадает с версией выпуска {rel.Version}"; return null; }

            var shaFile = assets.FirstOrDefault(x => x.Name.Equals(exe.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
            if (shaFile.Name != null && Uri.TryCreate(shaFile.Url, UriKind.Absolute, out var su) && ok(su))
                rel.Sha256 = Regex.Match(fetch(su.AbsoluteUri) ?? "", @"\b[0-9a-fA-F]{64}\b").Value.ToLowerInvariant();
            if (official)
            {
                var sigFile = assets.FirstOrDefault(x => x.Name.Equals(exe.Name + ".sig", StringComparison.OrdinalIgnoreCase));
                if (sigFile.Name != null && Uri.TryCreate(sigFile.Url, UriKind.Absolute, out var gu) && ok(gu))
                    rel.Signature = (fetch(gu.AbsoluteUri) ?? "").Trim();
            }
            if (!IsSha(rel.Sha256))
            {
                var man = assets.FirstOrDefault(x => x.Name.Equals("update.json", StringComparison.OrdinalIgnoreCase));
                if (man.Name != null && Uri.TryCreate(man.Url, UriKind.Absolute, out var mu) && ok(mu))
                {
                    var m = ParseManifest(fetch(mu.AbsoluteUri), mu, out _);
                    if (m != null && CompareVersions(m.Version, rel.Version) == 0) { rel.Sha256 = m.Sha256; if (rel.Notes.Count == 0) rel.Notes = m.Notes; }
                }
            }
            if (!IsSha(rel.Sha256)) { error = "к выпуску не приложена контрольная сумма SHA-256 (файл .sha256 или update.json)"; return null; }
            return rel;
        }
        catch (JsonException) { error = "GitHub ответил не так, как ожидалось"; return null; }
    }

    public static bool IsSha(string s) => Regex.IsMatch(s ?? "", "^[0-9a-f]{64}$");

    /// <summary>Что известно об обновлении программы по последней проверке (без запросов в сеть).</summary>
    public static (FeedKind Kind, AppRelease Release, int Diff, string Error, DateTime? CheckedAt) AppStatus(AppSettings s)
    {
        string feed = Feed(s);
        var kind = Classify(feed, out _);
        if (kind is FeedKind.None or FeedKind.Invalid) return (kind, null, 0, "", null);
        if (s.Get("update.source") != feed) return (kind, null, 0, "", null);     // источник поменяли — старый ответ не годится
        var at = ReadTime(s.Get("update.checked"));
        string v = s.Get("update.version");
        if (v == "") return (kind, null, 0, s.Get("update.error"), at);
        // Только для показа: адрес и контрольная сумма отсюда не используются — перед обновлением выпуск запрашивается заново (QueryOfficial).
        var rel = new AppRelease
        {
            Version = v, Date = s.Get("update.date"),
            Notes = s.Get("update.notes").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList(),
        };
        return (kind, rel, CompareVersions(v, Installer.Version), "", at);
    }

    // ================================================================== подпись выпуска

    /// <summary>
    /// Открытый ключ ECDSA P-256 (координаты X и Y, Base64), которым проверяется подпись установщика — файл «PulsePC-Setup-x.exe.sig»
    /// в том же выпуске. Закрытый ключ есть только у автора, вне репозитория (%USERPROFILE%\.pulsepc\release-signing.key):
    /// «build-release.ps1 -NewSigningKey» создаёт его один раз и вписывает сюда открытую часть. Пустой ключ — ни одна подпись не верна.
    /// </summary>
    private static readonly string SigningKeyX = "LMLymFnr3vQaQ6Qqss4NlsfXZjfEREWTAryQYopAVeQ=";
    private static readonly string SigningKeyY = "DK+mr2wXD/nt+KfBxOzkdDMzB/6Ozphryj+veaO00s8=";
    /// <summary>Первая подписанная версия. Всё новее неё ставится только с верной подписью; нет файла .sig — обновление отклоняется.</summary>
    public const string FirstSignedVersion = "1.4.2";
    /// <summary>Самопроверка подменяет ключ своим, временным.</summary>
    internal static ECParameters? TestKey;

    /// <summary>Что подписывается: имя, версия из четырёх частей и SHA-256 установщика. Версия внутри — старый подписанный файл не выдать за новый.</summary>
    public static string SignedText(string version, string sha256) => "PulsePC-Setup\n" + Version4(version) + "\n" + (sha256 ?? "").ToLowerInvariant();

    public static bool SignatureRequired(string version) => CompareVersions(version, FirstSignedVersion) > 0;

    public static bool SigningKeyEmbedded => TestKey != null || SigningKeyX != "" && SigningKeyY != "";

    /// <summary>Подпись (Base64, 64 байта r||s) верна для этой версии и этого SHA-256.</summary>
    public static bool VerifySignature(string version, string sha256, string signature, out string error)
    {
        error = "";
        try
        {
            ECParameters key;
            if (TestKey is ECParameters t) key = t;
            else
            {
                if (SigningKeyX == "" || SigningKeyY == "") { error = "в эту сборку не встроен ключ проверки подписи"; return false; }
                key = new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = Convert.FromBase64String(SigningKeyX), Y = Convert.FromBase64String(SigningKeyY) } };
            }
            byte[] sig;
            try { sig = Convert.FromBase64String((signature ?? "").Trim()); } catch (FormatException) { error = "файл подписи испорчен"; return false; }
            if (sig.Length != 64) { error = "файл подписи испорчен"; return false; }
            using var ec = ECDsa.Create(key);
            if (ec.VerifyData(Encoding.UTF8.GetBytes(SignedText(version, sha256)), sig, HashAlgorithmName.SHA256)) return true;
            error = "подпись не совпала: установщик подписан не автором программы или для другой версии";
            return false;
        }
        catch (Exception ex) { error = "подпись не проверилась: " + ex.Message; return false; }
    }

    /// <summary>Проверка подписи по правилам перехода: новее 1.4.2 — подпись обязательна; если подпись есть — она должна быть верной.</summary>
    private static bool SignatureOk(AppRelease r, string sha256, out string message)
    {
        message = "";
        bool has = !string.IsNullOrWhiteSpace(r.Signature);
        if (!has && !SignatureRequired(r.Version)) return true;
        if (!has) { message = $"К выпуску {r.Version} не приложена подпись (файл .sig) — без неё установщик не запускается."; return false; }
        if (VerifySignature(r.Version, sha256, r.Signature, out string err)) return true;
        message = "Подпись установщика не прошла проверку: " + err + ".";
        return false;
    }

    // ================================================================== обновление программы: скачать и запустить

    /// <summary>
    /// Папка для скачанного установщика: %ProgramData%\PulsePC\update — писать в неё могут только администраторы и система
    /// (раньше была %LOCALAPPDATA%\PulsePC\update: туда любая программа могла подложить DLL рядом с установщиком или ссылку).
    /// </summary>
    public static string UpdateFolder => SecureStore.PathOf("update");

    /// <summary>
    /// Папка обновлений действительно защищённая (%ProgramData%\PulsePC). Если её создать не удалось, SecureStore отдаёт папку
    /// пользователя — туда установщик не скачиваем (в самопроверке с PULSEPC_DATA — можно).
    /// </summary>
    public static bool UpdateFolderProtected =>
        // Папка проверки (PULSEPC_DATA) засчитывается только программе без прав: у программы с правами установщик из неё
        // запускался бы от администратора, а писать в папку проверки может кто угодно.
        !Portable.Enabled && (Environment.GetEnvironmentVariable("PULSEPC_DATA") is { Length: > 0 } && !Shell.IsAdmin()
        || !SecureStore.FellBack && UpdateFolder.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PulsePC") + "\\", StringComparison.OrdinalIgnoreCase));

    /// <summary>Подпапка UpdateFolder, куда однофайловый установщик распаковывает свои библиотеки (вместо %TEMP%\.net).</summary>
    private const string RunFolderName = "run";

    /// <summary>Создать папку для скачивания; ссылку (точку соединения) на её месте убрать, а не идти по ней.</summary>
    private static void PrepareFolder(string folder)
    {
        var di = new DirectoryInfo(folder);
        if (di.Exists && di.Attributes.HasFlag(FileAttributes.ReparsePoint)) di.Delete();
        di.Refresh();
        if (!di.Exists) di.Create();
        di.Refresh();
        if (di.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("папка для обновления подменена ссылкой");
    }

    /// <summary>
    /// Скачать установщик в папку и проверить SHA-256 и подпись. Не совпало — файл удаляется. Имя файла всегда «PulsePC-Setup-&lt;версия&gt;.exe»:
    /// по нему программа запускается как установщик. Скачивается только по https и только с GitHub (официальный репозиторий,
    /// перенаправления проверяются по одному). progress — (скачано, всего или −1).
    /// </summary>
    public static (bool Ok, string Path, string Message) Download(AppRelease r, string folder, Action<long, long> progress = null, CancellationToken cancel = default)
    {
        if (r == null || CleanVersion(r.Version) == "") return (false, null, "Неизвестно, какую версию скачивать.");
        if (!IsSha(r.Sha256)) return (false, null, "В источнике нет контрольной суммы SHA-256 — без неё обновлять небезопасно.");
        if (!Uri.TryCreate(r.Url, UriKind.Absolute, out var uri) || !OfficialHop(uri, true, false))
            return (false, null, "Адрес установщика не подходит: обновление скачивается только по https из выпусков " + OfficialRepo + " на GitHub.");
        if (SignatureRequired(r.Version) && string.IsNullOrWhiteSpace(r.Signature))
            return (false, null, $"К выпуску {r.Version} не приложена подпись (файл .sig) — без неё обновлять небезопасно. Ничего не скачано.");

        string final = Path.Combine(folder, $"PulsePC-Setup-{CleanVersion(r.Version)}.exe");
        string part = final + ".part";
        HttpResponseMessage resp = null;
        Stream src = null;
        try
        {
            PrepareFolder(folder);
            long total = -1, done = 0;
            using (var head = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                head.CancelAfter(TimeoutMs);
                resp = SendOfficial(uri, false, head.Token);
            }
            if (!resp.IsSuccessStatusCode) return (false, null, "Установщик не скачался: " + StatusText((int)resp.StatusCode) + ".");
            total = resp.Content.Headers.ContentLength ?? -1;
            src = resp.Content.ReadAsStream(cancel);
            if (total > MaxInstaller) return (false, null, "Файл в источнике слишком большой для установщика программы — скачивание отменено.");

            string got;
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                TryDelete(part);
                using (var fs = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buf = new byte[81920];
                    while (true)
                    {
                        // Нет новых байтов 30 секунд — связь оборвалась.
                        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                        stall.CancelAfter(30000);
                        int n = src.ReadAsync(buf, 0, buf.Length, stall.Token).GetAwaiter().GetResult();
                        if (n <= 0) break;
                        sha.AppendData(buf, 0, n);
                        fs.Write(buf, 0, n);
                        done += n;
                        if (done > MaxInstaller) throw new InvalidDataException("слишком большой файл");
                        progress?.Invoke(done, total);
                    }
                }
                got = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            }
            if (got != r.Sha256)
            {
                TryDelete(part);
                return (false, null, "Контрольная сумма SHA-256 не совпала: файл повреждён при скачивании или подменён. Он удалён, ничего не установлено.");
            }
            if (!SignatureOk(r, got, out string sigProblem))
            {
                TryDelete(part);
                return (false, null, sigProblem + " Файл удалён, ничего не установлено.");
            }
            if (!LooksLikeExe(part))
            {
                TryDelete(part);
                return (false, null, "Скачанный файл — не программа Windows. Он удалён, ничего не установлено.");
            }
            TryDelete(final);
            File.Move(part, final);
            bool signed = !string.IsNullOrWhiteSpace(r.Signature);
            return (true, final, "Скачано " + Fmt.Size(new FileInfo(final).Length) + ", контрольная сумма SHA-256 совпала" + (signed ? ", подпись автора верна." : "."));
        }
        catch (Exception ex)
        {
            TryDelete(part);
            if (ex is OperationCanceledException && cancel.IsCancellationRequested) return (false, null, "Скачивание отменено.");
            return (false, null, "Установщик не скачался: " + NetError(ex, "Источник") + ".");
        }
        finally
        {
            src?.Dispose();
            resp?.Dispose();
        }
    }

    private const long MaxInstaller = 400L * 1024 * 1024;

    private static bool LooksLikeExe(string file)
    {
        try
        {
            using var f = File.OpenRead(file);
            return f.ReadByte() == 'M' && f.ReadByte() == 'Z';
        }
        catch { return false; }
    }

    private static void TryDelete(string f)
    {
        try { if (File.Exists(f)) File.Delete(f); } catch { }
    }

    /// <summary>Запущенный установщик: процесс и событие, которым он сообщает, что его окно открылось.</summary>
    public sealed class LaunchResult : IDisposable
    {
        public bool Started;
        public string Message = "";
        public Process Installer;
        public EventWaitHandle Ready;

        public void Dispose()
        {
            try { Installer?.Dispose(); } catch { }
            try { Ready?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Запустить скачанный установщик. Файл открывается и держится открытым (запись и удаление запрещены) от проверки
    /// SHA-256 и подписи до запуска процесса — подменить его между проверкой и запуском нельзя. Текущая папка установщика —
    /// защищённая папка обновлений, распаковка его библиотек — туда же (DOTNET_BUNDLE_EXTRACT_BASE_DIR), а не в %TEMP%.
    /// dryRun (или переменная PULSEPC_UPDATE_DRYRUN=1) — ничего не запускать, только сказать, что было бы запущено.
    /// </summary>
    public static LaunchResult Launch(string file, AppRelease r, bool dryRun = false, IReadOnlyList<string> extraArgs = null)
    {
        dryRun |= Environment.GetEnvironmentVariable("PULSEPC_UPDATE_DRYRUN") == "1";
        var res = new LaunchResult();
        if (r == null) { res.Message = "Неизвестно, какую версию запускать."; return res; }
        if (!File.Exists(file)) { res.Message = "Скачанный установщик пропал. Нажмите «Обновить» ещё раз."; return res; }
        FileStream hold = null;
        try
        {
            if (IsReparse(file) || IsReparse(Path.GetDirectoryName(file)))
            {
                res.Message = "Скачанный установщик подменён ссылкой. Ничего не запущено.";
                return res;
            }
            try { hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read); }
            catch (Exception ex) { res.Message = "Не удалось прочитать скачанный установщик: " + ex.Message; return res; }

            string got = Convert.ToHexString(SHA256.HashData(hold)).ToLowerInvariant();
            string problem = got != (r.Sha256 ?? "").ToLowerInvariant()
                ? "Установщик изменился после скачивания: контрольная сумма не совпала."
                : !SignatureOk(r, got, out string sigProblem) ? sigProblem : null;
            if (problem == null)
            {
                hold.Position = 0;
                if (hold.ReadByte() != 'M' || hold.ReadByte() != 'Z') problem = "Скачанный файл — не программа Windows.";
            }
            if (problem != null)
            {
                hold.Dispose(); hold = null;
                TryDelete(file);
                res.Message = problem + " Файл удалён, ничего не запущено.";
                return res;
            }
            if (dryRun)
            {
                res.Message = $"Проверка: был бы запущен «{file}». Установщик не запускался, программа не закрывалась.";
                LastDryRunArgs = string.Join(" ", new[] { "--setup", "--ready-event", "<имя>" }.Concat(extraArgs ?? Array.Empty<string>()));
                return res;
            }

            string dir = Path.GetDirectoryName(file);
            if (!UpdateFolderProtected)
            {
                res.Message = "Защищённая папка %ProgramData%\\PulsePC недоступна — запускать установщик из папки пользователя небезопасно. Ничего не запущено.";
                return res;
            }
            if (SetupRunning())
            {
                res.Message = "Установщик программы уже запущен. Закройте его окно и нажмите «Обновить» ещё раз.";
                return res;
            }
            // Распаковка библиотек установщика — в заново созданную пустую папку: прежнюю (с остатками прошлых обновлений
            // или чем угодно, что в ней оказалось) удаляем целиком, не раскрывая ссылок.
            string extract = Path.Combine(dir, RunFolderName);
            try { SecureStore.Recreate(extract); }
            catch (Exception ex)
            {
                // Что-то в прежней папке занято — распаковка в новую пустую подпапку рядом.
                Log.Error("очистка папки распаковки установщика", ex);
                if (IsReparse(extract)) throw new IOException("папка распаковки подменена ссылкой");
                extract = Path.Combine(extract, Guid.NewGuid().ToString("N"));
                SecureStore.Recreate(extract);
            }
            string readyName = "PulsePC.Setup.Ready." + Guid.NewGuid().ToString("N");
            res.Ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,         // программа уже с правами администратора: установщик получает их без окна UAC
                WorkingDirectory = dir,
            };
            psi.ArgumentList.Add("--setup");
            psi.ArgumentList.Add("--ready-event");
            psi.ArgumentList.Add(readyName);
            foreach (string a in extraArgs ?? Array.Empty<string>()) psi.ArgumentList.Add(a);
            psi.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = extract;
            res.Installer = Process.Start(psi);
            if (res.Installer == null) { res.Message = "Установщик не запустился."; return res; }
            res.Started = true;
            res.Message = "Установщик запускается…";
            return res;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 1223 or 740)
        {
            res.Message = "Установщику нужны права администратора, а Windows их не дала. Программа не обновлена.";
            return res;
        }
        catch (Exception ex) { res.Message = "Установщик не запустился: " + ex.Message; return res; }
        finally { hold?.Dispose(); }     // процесс уже запущен: его файл занят системой до выхода
    }

    /// <summary>
    /// Дождаться, пока установщик откроет окно (событие Ready). true — можно выходить; false — установщик закрылся
    /// или не ответил: программа остаётся работать. Старые установщики события не знают — тогда они сами закроют программу.
    /// </summary>
    public static (bool Ready, string Message) WaitForInstaller(LaunchResult run, int timeoutMs = 120000)
    {
        if (run?.Installer == null || run.Ready == null) return (false, "Установщик не запущен.");
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (run.Ready.WaitOne(250)) return (true, "Установщик открылся. Программа сейчас закроется, чтобы он смог заменить её файлы.");
            try
            {
                if (run.Installer.HasExited)
                    return run.Ready.WaitOne(0) ? (true, "Установщик открылся.")
                        : (false, $"Установщик закрылся сам (код {run.Installer.ExitCode}). Программа не обновлена и продолжает работать.");
            }
            catch { }
        }
        return (false, "Установщик не открылся за две минуты. Программа продолжает работать; если окно установщика появится, нажмите в нём «Обновить».");
    }

    /// <summary>Попросить работающую программу выйти тем же путём, что и установщик (событие PulsePC.Exit.*).</summary>
    public static void RequestExit()
    {
        try
        {
            string suffix = Shell.IsAdmin() ? "Admin" : "User";
            if (EventWaitHandle.TryOpenExisting("PulsePC.Exit." + suffix, out var ev)) { ev.Set(); ev.Dispose(); }
        }
        catch { }
    }

    // ================================================================== обновление без нажатия
    // Настройка «Устанавливать обновления сами» (update.autoInstall): по умолчанию выключена, один раз спрашивается в разделе
    // при найденной новой версии. Проверки те же, что у кнопки «Обновить»: выпуск запрашивается заново у официального
    // репозитория (QueryOfficial), скачанный файл сверяется по SHA-256 и подписи (Download), перед запуском — ещё раз (Launch).
    // Скачивание — в фоне и не во время игры. Установка — в первые минуты после запуска программы или когда человек не за
    // компьютером (10 минут без мыши и клавиатуры), только если не идёт игра. Установщик работает без окна (--quiet) и сам
    // запускает программу снова (--relaunch); при следующем запуске — уведомление «Обновлено до …» или о сбое.

    public const string KAutoInstall = "update.autoInstall", KAutoPending = "update.auto.pending", KAutoFrom = "update.auto.from",
        KAutoAt = "update.auto.at", KAutoFailed = "update.auto.failed", KAutoError = "update.auto.error", KLastRun = "update.lastRun",
        KUpdatedTo = "update.updatedTo", KUpdatedFrom = "update.updatedFrom", KUpdatedAt = "update.updatedAt";
    /// <summary>Сколько минут после запуска программы считается «запуском» (можно ставить, человек ещё не занят игрой).</summary>
    public const int StartupMinutes = 5;
    /// <summary>Сколько минут без мыши и клавиатуры — «человек не за компьютером».</summary>
    public const int IdleMinutes = 10;
    /// <summary>Сколько ждать между предупреждением «скоро обновлюсь» и установкой.</summary>
    public const int NoticeSeconds = 60;

    private static Timer autoTimer;
    private static DateTime started = DateTime.Now, lastAttempt = DateTime.MinValue;
    private static DateTime? armed;
    private static int autoBusy;
    private static (AppRelease Release, string Path)? ready;
    /// <summary>Самопроверка: что получил бы установщик в режиме проверки (PULSEPC_UPDATE_DRYRUN).</summary>
    internal static string LastDryRunArgs = "";

    public static bool AutoInstall(AppSettings s) => s?.Get(KAutoInstall) == "1";
    /// <summary>Человек уже ответил на вопрос «Устанавливать обновления сами?».</summary>
    public static bool AutoAsked(AppSettings s) => s?.Get(KAutoInstall) is "1" or "0";

    public static void SetAutoInstall(AppSettings s, bool on)
    {
        s.Set(KAutoInstall, on ? "1" : "0");
        if (on) s.Set(KAutoFailed, null);       // включили снова — можно пробовать и версию, которая не встала
        armed = null;
        Save(s);
    }

    /// <summary>Можно ли на этом компьютере ставить обновления самой (и если нет — почему, на языке интерфейса).</summary>
    public static (bool Ok, string Why) AutoState(bool? admin = null)
    {
        if (Portable.Enabled) return (false, L.T("Это переносная версия: она обновляется вручную, архивом со страницы выпусков."));
        if (!(admin ?? Shell.IsAdmin())) return (false, L.T("Ставить обновления может только программа, запущенная с правами администратора."));
        if (!UpdateFolderProtected) return (false, L.T("Защищённая папка %ProgramData%\\PulsePC недоступна — скачивать установщик в папку пользователя небезопасно."));
        if (!RunningInstalled()) return (false, L.T("Программа запущена не из папки установки — обновлять её саму нечем. Обновления без нажатия работают у установленной программы."));
        return (true, "");
    }

    /// <summary>Запущена установленная копия (из папки, записанной установщиком), а не копия для разработки или проверки.</summary>
    private static bool RunningInstalled()
    {
        // Режим проверки: установщик всё равно не запускается (Launch только описывает запуск) — показ раздела как у установленной программы.
        if (Environment.GetEnvironmentVariable("PULSEPC_UPDATE_DRYRUN") == "1") return true;
        try
        {
            string dir = Installer.InstalledDir(SetupTarget.Real(Installer.DefaultDir));
            if (string.IsNullOrEmpty(dir)) return false;
            return string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Идёт ли игра: запись сессии или найденный процесс игры.</summary>
    private static bool GameNow()
    {
        if (GameHooks.InGame) return true;
        try { return GameDetector.Find() != null; } catch { return true; }
    }

    /// <summary>Сколько минут не трогали мышь и клавиатуру.</summary>
    public static double IdleFor()
    {
        try
        {
            var info = new LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info)) return 0;
            uint ms = unchecked((uint)Environment.TickCount - info.dwTime);
            return ms / 60000.0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Когда ставить. Чистая функция (самопроверка): включено, есть версия новее и не та, что уже не встала; не идёт игра;
    /// и идут первые минуты после запуска программы либо человек давно не трогал мышь и клавиатуру.
    /// </summary>
    public static bool InstallMoment(bool enabled, int diff, bool failedBefore, bool inGame, double minutesSinceStart, double idleMinutes) =>
        enabled && diff > 0 && !failedBefore && !inGame && (minutesSinceStart < StartupMinutes || idleMinutes >= IdleMinutes);

    private static void AutoTick()
    {
        var s = host?.Settings;
        if (s == null || Interlocked.Exchange(ref autoBusy, 1) == 1) return;
        try { AutoStep(s, GameNow, DateTime.Now, IdleFor(), UpdateFolder, dryRun: false); }
        catch (Exception ex) { Log.Error("обновление без нажатия", ex); }
        finally { Interlocked.Exchange(ref autoBusy, 0); }
    }

    /// <summary>
    /// Один шаг обновления без нажатия (раз в минуту). Возвращает, что сделано, — для самопроверки, которая гоняет те же шаги
    /// на локальном сервере 127.0.0.1 (dryRun: установщик не запускается, права и папка установки не проверяются).
    /// </summary>
    internal static string AutoStep(AppSettings s, Func<bool> gameNow, DateTime now, double idle, string folder, bool dryRun)
    {
        if (!AutoInstall(s) || Checking) { armed = null; return "выключено"; }
        if (!dryRun && !AutoState().Ok) return "здесь нельзя";
        var app = AppStatus(s);
        if (app.Release == null || app.Diff <= 0) { ready = null; armed = null; return "новой версии нет"; }
        if (s.Get(KAutoFailed) == app.Release.Version) return "эта версия уже не встала";      // дальше только кнопкой
        if (gameNow()) { armed = null; return "игра"; }

        // 1. Скачать заранее, в фоне (не во время игры). Сведения о выпуске — заново у официального репозитория.
        if (ready is not { } r || CompareVersions(r.Release.Version, app.Release.Version) < 0 || !File.Exists(r.Path))
        {
            if ((now - lastAttempt).TotalMinutes < 60) return "ждёт следующей попытки";
            lastAttempt = now;
            var official = QueryOfficial(out string error);
            if (official == null) { Remember(s, L.T("Официальный выпуск на GitHub не ответил") + ": " + L.T(error)); return "выпуск не ответил"; }
            if (CompareVersions(official.Version, Installer.Version) <= 0) return "в выпуске нет новее";
            string path = AlreadyDownloadedIn(folder, official);
            if (path == null)
            {
                var d = Download(official, folder);
                if (!d.Ok)
                {
                    Remember(s, L.T(d.Message));
                    // Выпуск не прошёл проверку (сумма, подпись, не программа, нет суммы или подписи): сама эта версия больше
                    // не качается — иначе до 400 МБ каждый час без конца. Сбой связи — следующая попытка через час.
                    if (!d.Message.StartsWith("Установщик не скачался", StringComparison.Ordinal) && !d.Message.StartsWith("Скачивание отменено", StringComparison.Ordinal))
                    {
                        s.Set(KAutoFailed, official.Version);
                        Save(s);
                    }
                    return "не скачано: " + d.Message;
                }
                path = d.Path;
            }
            ready = (official, path);
            Remember(s, null);
            return "скачано";        // ставить — на следующем шаге, когда снова проверим, что игры нет
        }

        // 2. Когда ставить: первые минуты после запуска программы или человек не за компьютером.
        if (!InstallMoment(true, CompareVersions(r.Release.Version, Installer.Version), false, false, (now - started).TotalMinutes, idle)) { armed = null; return "не время"; }
        if (armed == null)
        {
            armed = now;
            // За компьютером (первые минуты после запуска) — предупредить заранее; если человек отошёл, предупреждать некого.
            if (idle < 1) host?.Notify(L.T("«Пульс ПК» сейчас обновится"),
                L.F("Через минуту программа установит версию {0} и перезапустится сама. Если вы собираетесь играть — запустите игру, и обновление подождёт.", r.Release.Version),
                false, () => host.Open("sys:" + SectionTitle));
            return "предупреждение";
        }
        if ((now - armed.Value).TotalSeconds < NoticeSeconds) return "ждёт после предупреждения";
        if (gameNow()) { armed = null; return "игра"; }
        return InstallNow(s, r.Release, r.Path, dryRun);
    }

    private static void Remember(AppSettings s, string error)
    {
        if (s.Get(KAutoError) == (error ?? "")) return;
        s.Set(KAutoError, error);
        Save(s);
    }

    /// <summary>Установщик этой версии уже скачан в защищённую папку и совпадает по SHA-256 — второй раз не качаем (подпись проверит Launch).</summary>
    private static string AlreadyDownloaded(AppRelease r) => AlreadyDownloadedIn(UpdateFolder, r);

    private static string AlreadyDownloadedIn(string folder, AppRelease r)
    {
        try
        {
            string file = Path.Combine(folder, $"PulsePC-Setup-{CleanVersion(r.Version)}.exe");
            if (!IsSha(r.Sha256) || !File.Exists(file) || IsReparse(file) || IsReparse(folder)) return null;
            using var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant() == r.Sha256 ? file : null;
        }
        catch { return null; }
    }

    /// <summary>Журнал установщика без окна (в защищённой папке): «OK» или «ERROR …».</summary>
    public static string AutoLogPath => Path.Combine(UpdateFolder, "auto-install.log");

    /// <summary>Ключи установщика для обновления без нажатия: без окна, журнал, перезапуск; ярлык и автозапуск — как сейчас.</summary>
    public static List<string> AutoArgs(string log, bool windowVisible, bool desktopShortcut, bool autostart)
    {
        var a = new List<string> { "--quiet", "--log", log, windowVisible ? "--relaunch" : "--relaunch-minimized" };
        if (!desktopShortcut) a.Add("--no-desktop");
        if (autostart) a.Add("--autostart");
        return a;
    }

    private static string InstallNow(AppSettings s, AppRelease r, string file, bool dryRun = false)
    {
        bool visible = false;
        try { visible = System.Windows.Application.Current?.Dispatcher.Invoke(() => System.Windows.Application.Current.MainWindow?.IsVisible == true) == true; } catch { }
        bool desktop = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Installer.ShortcutName));
        bool autostart = false;
        try { autostart = Installer.AutostartExists(SetupTarget.Real(Installer.InstalledDir(SetupTarget.Real(Installer.DefaultDir)) ?? Installer.DefaultDir)); } catch { }
        string log = Path.Combine(Path.GetDirectoryName(file) ?? UpdateFolder, "auto-install.log");
        TryDelete(log);

        s.Set(KAutoPending, r.Version);
        s.Set(KAutoFrom, Installer.Version);
        s.Set(KAutoAt, Stamp(DateTime.Now));
        Save(s);
        if (!dryRun) Log.Info($"обновление без нажатия: ставлю {r.Version}");
        using var run = Launch(file, r, dryRun, AutoArgs(log, visible, desktop, autostart));
        if (!run.Started)
        {
            s.Set(KAutoPending, null);
            if (!run.Message.StartsWith("Проверка:", StringComparison.Ordinal)) s.Set(KAutoFailed, r.Version);
            s.Set(KAutoError, run.Message);
            Save(s);
            if (!dryRun) Log.Info("обновление без нажатия не запустилось: " + run.Message);
            ready = null; armed = null;
            return run.Message;
        }
        var wait = WaitForInstaller(run, 60000);
        if (wait.Ready) { RequestExit(); return "установщик запущен"; }
        s.Set(KAutoPending, null);
        s.Set(KAutoFailed, r.Version);
        s.Set(KAutoError, wait.Message);
        Save(s);
        ready = null; armed = null;
        return wait.Message;
    }

    /// <summary>
    /// Первый запуск после обновления: уведомление «Обновлено до …» (что нового — из описания выпуска); если обновление без
    /// нажатия не встало — уведомление о сбое и запрет повторять эту версию сами (только кнопкой).
    /// </summary>
    public static void AfterUpdate(AppSettings s)
    {
        if (s == null) return;
        string current = Installer.Version, last = s.Get(KLastRun), pending = s.Get(KAutoPending);
        bool changed = false;
        if (pending != "")
        {
            if (CompareVersions(current, pending) < 0)
            {
                string why = "";
                try { if (File.Exists(AutoLogPath)) why = File.ReadAllText(AutoLogPath).Trim(); } catch { }
                why = why.StartsWith("ERROR ", StringComparison.Ordinal) ? why[6..].Split('\n')[0].Trim() : why == "" ? L.T("установщик не оставил отчёта") : why.Split('\n')[0];
                s.Set(KAutoFailed, pending);
                s.Set(KAutoError, why);
                host?.Notify(L.T("Обновление не установилось"),
                    L.F("Версия {0} не встала сама: {1}. Программа работает в прежней версии; обновить можно кнопкой в разделе «Новые версии».", pending, why),
                    true, () => host.Open("sys:" + SectionTitle));
            }
            s.Set(KAutoPending, null);
            changed = true;
        }
        if (last != current)
        {
            // Прежние версии (до 1.5) номер не запоминали: если программа уже проверяла версии — это обновление, а не первая установка.
            if (last != "" ? CompareVersions(current, last) > 0 : s.Get("versions.checked") != "")
            {
                s.Set(KUpdatedTo, current);
                s.Set(KUpdatedFrom, last);
                s.Set(KUpdatedAt, Stamp(DateTime.Now));
                var notes = WhatsNew(s);
                string text = notes.Count > 0 ? string.Join(" ", notes.Take(2).Select(n => n.TrimEnd('.') + ".")) + (notes.Count > 2 ? " " + L.T("Остальное — в разделе «Новые версии».") : "")
                    : L.T("Что нового — в разделе «Новые версии».");
                host?.Notify(L.F("Обновлено до {0} — что нового", current), text, false, () => host.Open("sys:" + SectionTitle));
            }
            s.Set(KLastRun, current);
            changed = true;
        }
        if (changed) Save(s);
    }

    /// <summary>Что нового в установленной версии (если о ней есть описание из последней проверки).</summary>
    public static List<string> WhatsNew(AppSettings s) =>
        CompareVersions(s.Get("update.version"), Installer.Version) == 0
            ? s.Get("update.notes").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x != "").ToList()
            : new List<string>();

    /// <summary>Недавно обновлено (14 дней): раздел показывает «Что нового в …».</summary>
    public static (bool Recent, string From, DateTime? At) RecentUpdate(AppSettings s)
    {
        if (s.Get(KUpdatedTo) != Installer.Version) return (false, "", null);
        var at = ReadTime(s.Get(KUpdatedAt));
        return (at is DateTime t && (DateTime.Now - t).TotalDays <= 14, s.Get(KUpdatedFrom), at);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    // ================================================================== BIOS

    private static readonly string[] Placeholders =
    {
        "Default string", "To be filled by O.E.M.", "To Be Filled By O.E.M.", "System Product Name", "System manufacturer", "System Manufacturer",
        "Base Board Product Name", "Not Applicable", "O.E.M.", "OEM", "None", "Not Specified", "x.x",
    };

    private static string Clean(object v)
    {
        string s = (v as string ?? "").Trim();
        return Placeholders.Any(p => p.Equals(s, StringComparison.OrdinalIgnoreCase)) ? "" : s;
    }

    /// <summary>BIOS и плата из SMBIOS (то же, что Win32_BIOS и Win32_BaseBoard), запасной вариант — паспорт железа.</summary>
    public static BiosInfo ReadBios(HardwareInfo hw = null)
    {
        var b = new BiosInfo();
        string cpu = "";
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            if (k != null)
            {
                b.Version = Clean(k.GetValue("BIOSVersion"));
                b.Vendor = Clean(k.GetValue("BIOSVendor"));
                b.BoardMaker = Clean(k.GetValue("BaseBoardManufacturer"));
                b.Board = Clean(k.GetValue("BaseBoardProduct"));
                b.BoardVersion = Clean(k.GetValue("BaseBoardVersion"));
                b.SystemMaker = Clean(k.GetValue("SystemManufacturer"));
                b.SystemModel = Clean(k.GetValue("SystemProductName"));
                if (DateTime.TryParseExact(Clean(k.GetValue("BIOSReleaseDate")), new[] { "MM/dd/yyyy", "M/d/yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) b.Date = d;
                // Признак ноутбука — общий для всей программы (тип корпуса, мобильная платформа или батарея; ИБП не в счёт).
                b.Laptop = LaptopCheck.Applies;
            }
            using var c = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            cpu = c?.GetValue("ProcessorNameString") as string ?? "";
        }
        catch { }
        if (hw != null)
        {
            if (b.Version == "") b.Version = Clean(hw.Bios);
            if (b.BoardMaker == "") b.BoardMaker = Clean(hw.BoardMaker);
            if (b.Board == "") b.Board = Clean(hw.Board);
            if (b.Date == null && DateTime.TryParseExact(hw.BiosDate ?? "", "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) b.Date = d;
            if (cpu == "") cpu = hw.CpuName;
        }
        if (b.Date != null && (b.Date.Value.Year < 1995 || b.Date > DateTime.Today.AddDays(2))) b.Date = null;
        b.Maker = new HardwareInfo { BoardMaker = b.BoardMaker != "" ? b.BoardMaker : b.SystemMaker }.MakerShort;
        (b.Model, b.PageUrl, b.PageTitle) = BoardPage(b.Maker, b.Board, b.BoardVersion, HardwareInfo.CpuVendor(cpu), b.Laptop, b.SystemModel);
        string what = (b.Maker + " " + (b.Laptop && b.SystemModel != "" ? b.SystemModel : b.Model)).Trim();
        b.SearchUrl = what == "" ? "" : "https://www.google.com/search?q=" + Uri.EscapeDataString(what + " BIOS");
        return b;
    }

    /// <summary>
    /// Страница поддержки платы по шаблону производителя: (модель для показа, адрес, подпись). Адрес пустой — шаблона нет, остаётся поиск.
    /// Сами страницы программа не открывает и не запрашивает: адрес только показывается ссылкой.
    /// </summary>
    public static (string Model, string Url, string Title) BoardPage(string maker, string board, string boardVersion, string cpuVendor, bool laptop = false, string systemModel = "")
    {
        string model = (board ?? "").Trim();
        // MSI дописывает в скобках код платы: «PRO B760-P WIFI DDR4 (MS-7D98)».
        if (maker == "MSI") model = Regex.Replace(model, @"\s*\((MS-[0-9A-Z]+)\)\s*$", "", RegexOptions.IgnoreCase).Trim();
        string slug = Regex.Replace(Regex.Replace(model, @"[\s/]+", "-"), "-{2,}", "-").Trim('-');
        string Enc(string s) => Uri.EscapeDataString(s);
        if (laptop)
        {
            // У ноутбуков BIOS ищут по модели ноутбука на сайте его производителя.
            return maker switch
            {
                "ASUS" when systemModel != "" => (systemModel, $"https://www.asus.com/supportonly/{Enc(systemModel)}/helpdesk_bios/", L.T("Страница BIOS на сайте ASUS")),
                "HP" => (systemModel, "https://support.hp.com/drivers", L.T("Поддержка HP: найдите свою модель")),
                "Lenovo" => (systemModel, "https://pcsupport.lenovo.com/", L.T("Поддержка Lenovo: найдите свою модель")),
                "Dell" => (systemModel, "https://www.dell.com/support/home/", L.T("Поддержка Dell: найдите свою модель")),
                _ => (systemModel != "" ? systemModel : model, "", ""),
            };
        }
        if (model == "") return ("", "", "");
        return maker switch
        {
            "MSI" when slug != "" => (model, $"https://www.msi.com/Motherboard/{slug}/support#bios", L.T("Страница BIOS на сайте MSI")),
            "ASUS" => (model, $"https://www.asus.com/supportonly/{Enc(model)}/helpdesk_bios/", L.T("Страница BIOS на сайте ASUS")),
            // У GIGABYTE в адресе есть ревизия платы («rev-10» для 1.0), она записана в BaseBoardVersion.
            "GIGABYTE" when slug != "" => (model, $"https://www.gigabyte.com/Motherboard/{slug}{GigabyteRev(boardVersion)}/support#support-dl-bios", L.T("Страница BIOS на сайте GIGABYTE")),
            "ASRock" when cpuVendor is "Intel" or "AMD" => (model, $"https://www.asrock.com/mb/{cpuVendor}/{Enc(model)}/index.asp#BIOS", L.T("Страница BIOS на сайте ASRock")),
            "HP" => (model, "https://support.hp.com/drivers", L.T("Поддержка HP: найдите свою модель")),
            "Lenovo" => (model, "https://pcsupport.lenovo.com/", L.T("Поддержка Lenovo: найдите свою модель")),
            "Dell" => (model, "https://www.dell.com/support/home/", L.T("Поддержка Dell: найдите свою модель")),
            _ => (model, "", ""),
        };
    }

    private static string GigabyteRev(string v)
    {
        var m = Regex.Match(v ?? "", @"^(\d+)\.(\d+)$");
        return m.Success ? $"-rev-{m.Groups[1].Value}{m.Groups[2].Value}" : "";
    }

    /// <summary>Возраст BIOS словами — как сведения, не как тревога.</summary>
    public static string BiosAge(BiosInfo b) => b.AgeDays < 0 ? "" : GpuDriver.Age(b.AgeDays);

    // ================================================================== сеть

    /// <summary>Текст по адресу https (с тайм-аутом). Не больше 2 МБ. Локальные файлы не читаются.</summary>
    private static string FetchText(string url, int timeoutMs, bool github = false)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !Allowed(u)) throw new InvalidOperationException("адрес не https");
        using var cts = new CancellationTokenSource(timeoutMs);
        using var req = new HttpRequestMessage(HttpMethod.Get, u);
        if (github) req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).GetAwaiter().GetResult();
        return ReadLimited(resp, cts.Token);
    }

    /// <summary>Текст из официального репозитория: каждый адрес цепочки перенаправлений проверяется (OfficialHop).</summary>
    private static string FetchOfficialText(string url, int timeoutMs, bool api)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) throw new InvalidOperationException("адрес не https");
        using var cts = new CancellationTokenSource(timeoutMs);
        using var resp = SendOfficial(u, api, cts.Token);
        return ReadLimited(resp, cts.Token);
    }

    /// <summary>
    /// GET без автоматических перенаправлений: каждое перенаправление (до 5) проверяется по OfficialHop, иначе — ошибка.
    /// Ответ с кодом не 3xx возвращается как есть (освобождает вызывающий).
    /// </summary>
    private static HttpResponseMessage SendOfficial(Uri uri, bool api, CancellationToken ct)
    {
        for (int hop = 0; hop <= 5; hop++)
        {
            if (!OfficialHop(uri, hop == 0, api)) throw new InvalidDataException("адрес не из официального репозитория GitHub (" + uri.Host + ")");
            var req = new HttpRequestMessage(HttpMethod.Get, uri);
            if (api) req.Headers.Accept.ParseAdd("application/vnd.github+json");
            var resp = strict.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).GetAwaiter().GetResult();
            int code = (int)resp.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308)
            {
                var location = resp.Headers.Location;
                resp.Dispose();
                req.Dispose();
                if (location == null) throw new InvalidDataException("GitHub прислал перенаправление без адреса");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            return resp;
        }
        throw new InvalidDataException("слишком много перенаправлений");
    }

    /// <summary>Тело ответа текстом, не больше 2 МБ; код не 2xx — HttpStatusException.</summary>
    private static string ReadLimited(HttpResponseMessage resp, CancellationToken ct)
    {
        if (!resp.IsSuccessStatusCode) throw new HttpStatusException((int)resp.StatusCode);
        using var s = resp.Content.ReadAsStream(ct);
        using var ms = new MemoryStream();
        var buf = new byte[16384];
        int n;
        while ((n = s.ReadAsync(buf, 0, buf.Length, ct).GetAwaiter().GetResult()) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > 2 * 1024 * 1024) throw new InvalidDataException("ответ слишком большой");
        }
        return Encoding.UTF8.GetString(ms.ToArray()).TrimStart('\uFEFF');
    }

    private sealed class HttpStatusException : Exception
    {
        public int Code;
        public HttpStatusException(int code) : base("HTTP " + code) => Code = code;
    }

    private static string StatusText(int code) => code switch
    {
        403 => "сервер отказал в доступе (403)",
        404 => "файл не найден (404)",
        429 => "слишком много запросов, сервер просит подождать (429)",
        >= 500 => $"ошибка на сервере ({code})",
        _ => $"сервер ответил кодом {code}",
    };

    /// <summary>Понятная причина сбоя сети.</summary>
    private static string NetError(Exception ex, string who)
    {
        while (ex is AggregateException { InnerException: not null } ag) ex = ag.InnerException;
        return ex switch
        {
            HttpStatusException h => $"{who}: {StatusText(h.Code)}",
            TaskCanceledException or OperationCanceledException or TimeoutException => $"{who} не ответил за {TimeoutMs / 1000}{Nb}секунд",
            HttpRequestException { InnerException: SocketException } => "нет связи с интернетом или сервер недоступен",
            HttpRequestException => "нет связи с сервером",
            FileNotFoundException or DirectoryNotFoundException => "файл источника не найден",
            UnauthorizedAccessException => "нет доступа к файлу источника",
            InvalidDataException d => d.Message,
            _ => ex.Message,
        };
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        } : "";

    private static int Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n
            : v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int m) ? m : 0 : 0;

    // ================================================================== самопроверка

    public static string SelfTest()
    {
        var sb = new StringBuilder();
        var fails = new List<string>();
        void Expect(bool ok, string what) { sb.AppendLine((ok ? "  да   " : "  НЕТ  ") + what); if (!ok) fails.Add(what); }

        try
        {
            sb.AppendLine("Сравнение версий:");
            Expect(CompareVersions("621.24", "625.10") < 0, "621.24 < 625.10");
            Expect(CompareVersions("621.24", "621.24") == 0, "621.24 = 621.24");
            Expect(CompareVersions("566.36", "621.24") < 0, "566.36 < 621.24");
            Expect(CompareVersions("1.10.0", "1.9.3") > 0, "1.10.0 > 1.9.3");
            Expect(CompareVersions("v1.4.1", "1.4.0") > 0, "v1.4.1 > 1.4.0");
            Expect(CompareVersions("1.4", "1.4.0") == 0, "1.4 = 1.4.0");
            Expect(CompareVersions("1.4.1.1", "1.4.1") > 0 && CompareVersions("1.4.1.0", "1.4.1") == 0, "1.4.1.1 > 1.4.1, 1.4.1.0 = 1.4.1 (четыре части)");
            Expect(Version4("v1.4.2") == "1.4.2.0" && Version4("1.4.1.1") == "1.4.1.1", "версия для подписи: 1.4.2 → 1.4.2.0");
            Expect(Regex.IsMatch(Installer.Version, @"^\d+\.\d+\.\d+(\.[1-9]\d*)?$") && CompareVersions(Installer.Version, typeof(Installer).Assembly.GetName().Version?.ToString()) == 0,
                "версия программы " + Installer.Version + " совпадает с версией сборки во всех четырёх частях");
            Expect(!SignatureRequired("1.4.1") && !SignatureRequired("1.4.2") && SignatureRequired("1.4.2.1") && SignatureRequired("1.5"), "подпись обязательна для версий новее 1.4.2");
            sb.AppendLine(SigningKeyEmbedded
                ? "  ключ подписи встроен в сборку"
                : "  ВНИМАНИЕ: открытый ключ подписи не встроен (build-release.ps1 -NewSigningKey) — обновления новее 1.4.2 не будут ставиться");
            Expect(GpuDriver.FriendlyVersion("NVIDIA", "32.0.16.2124") == "621.24", "WMI 32.0.16.2124 → 621.24");

            sb.AppendLine("Ответ NVIDIA (подставной):");
            var nv = ParseNvidia(SampleNvidia("625.10", "2026-12-03T00:00:00+0000", 0), out string e1);
            Expect(nv != null && nv.Version == "625.10" && nv.Date == new DateTime(2026, 12, 3) && !nv.Beta && nv.Whql, "версия 625.10 от 03.12.2026, WHQL, не бета");
            Expect(nv?.DetailsUrl == "https://www.nvidia.com/en-us/drivers/details/999999/", "ссылка «Что нового» на nvidia.com");
            var beta = ParseNvidia(SampleNvidia("626.02", "2026-12-10T00:00:00+0000", 1), out _);
            Expect(beta?.Beta == true, "бета отмечается");
            var none = ParseNvidia("{\"criteria\":{\"IsSupported\":{\"state\":\"false\"}}}", out string e2);
            Expect(none == null && e2.Contains("не нашла"), "неизвестная видеокарта → «" + e2 + "»");
            var junk = ParseNvidia("<html>Access Denied</html>", out string e3);
            Expect(junk == null && e3 != "", "не JSON → «" + e3 + "»");
            var evil = ParseNvidia(SampleNvidia("625.10", "2026-12-03T00:00:00+0000", 0).Replace("https:\\/\\/www.nvidia.com", "https:\\/\\/evil.example"), out _);
            Expect(evil != null && evil.DetailsUrl == "", "ссылки не на nvidia.com отбрасываются");
            Expect(NvidiaUrl("2803").StartsWith(NvidiaService) && NvidiaUrl("2803").Contains("2803_10DE"), "адрес запроса содержит только код видеокарты 2803_10DE");

            sb.AppendLine("Источник обновлений:");
            Expect(Classify("", out _) == FeedKind.None, "пусто → не настроен");
            Expect(Classify("someone/pulsepc", out string n1) == FeedKind.GitHub && n1 == "someone/pulsepc", "someone/pulsepc → GitHub");
            Expect(Classify("https://github.com/someone/pulsepc", out string n2) == FeedKind.GitHub && n2 == "someone/pulsepc", "адрес github.com → GitHub");
            Expect(Classify("github:someone/pulse-pc", out _) == FeedKind.GitHub, "github:… → GitHub");
            Expect(Classify("https://example.com/pulse/update.json", out _) == FeedKind.Manifest, "https://…/update.json → манифест");
            Expect(Classify(@"C:\Updates\update.json", out _) == FeedKind.Invalid, @"C:\…\update.json (файл на диске) → отказ");
            Expect(Classify("file:///C:/Updates/update.json", out _) == FeedKind.Invalid, "file:/// → отказ");
            Expect(Classify(@"\\server\share\update.json", out _) == FeedKind.Invalid, @"\\сервер\… → отказ");
            Expect(Classify("http://example.com/update.json", out _) == FeedKind.Invalid, "http → отказ");
            Expect(Classify("http://127.0.0.1:8080/update.json", out _) == FeedKind.Invalid, "http://127.0.0.1 вне самопроверки → отказ");
            Expect(Classify("ftp://example.com/update.json", out _) == FeedKind.Invalid, "ftp → отказ");

            sb.AppendLine("Откуда скачивается обновление (только GitHub, только https):");
            string dl = $"https://github.com/{DefaultFeed}/releases/download/v1.4.2/PulsePC-Setup-1.4.2.exe";
            Expect(OfficialHop(new Uri(dl), true, false), "файл выпуска " + DefaultFeed + " на github.com");
            Expect(!OfficialHop(new Uri("https://github.com/evil/pulse-pc/releases/download/v9/PulsePC-Setup-9.0.exe"), true, false), "чужой репозиторий → отказ");
            Expect(!OfficialHop(new Uri(dl.Replace("https:", "http:")), true, false), "http → отказ");
            Expect(!OfficialHop(new Uri(dl.Replace("github.com/", "github.com:8443/")), true, false), "нестандартный порт → отказ");
            Expect(!OfficialHop(new Uri("https://example.com/PulsePC-Setup-1.4.2.exe"), true, false) && !OfficialHop(new Uri("file:///C:/x/PulsePC-Setup-1.4.2.exe"), true, false), "другой сайт и file:/// → отказ");
            Expect(!OfficialHop(new Uri("https://objects.githubusercontent.com/x"), true, false) && OfficialHop(new Uri("https://objects.githubusercontent.com/x"), false, false)
                   && OfficialHop(new Uri("https://release-assets.githubusercontent.com/x"), false, false), "хранилище файлов GitHub — только после перенаправления");
            Expect(OfficialHop(new Uri($"https://api.github.com/repos/{DefaultFeed}/releases/latest"), true, true) && !OfficialHop(new Uri("https://api.github.com/repos/evil/x/releases/latest"), true, true),
                "API — только официальный репозиторий");
            Expect(Download(new AppRelease { Version = "1.4.1", Sha256 = new string('a', 64), Url = "https://example.com/PulsePC-Setup-1.4.1.exe" }, Path.GetTempPath()).Message.Contains("GitHub"),
                "Download с чужого сайта отказывается до запроса в сеть");
            Expect(Download(new AppRelease { Version = "9.0", Sha256 = new string('a', 64), Url = dl.Replace("1.4.2", "9.0") }, Path.GetTempPath()).Message.Contains("подпись"),
                "Download версии новее 1.4.2 без подписи отказывается до запроса в сеть");
            var man = ParseManifest("{\"version\":\"1.4.1\",\"url\":\"PulsePC-Setup-1.4.1.exe\",\"sha256\":\"" + new string('a', 64) + "\",\"date\":\"2026-11-20\",\"notes\":[\"Раздел «Новые версии»\",\" \"]}",
                new Uri("https://example.com/pulse/update.json"), out _);
            Expect(man?.Url == "https://example.com/pulse/PulsePC-Setup-1.4.1.exe" && man.Notes.Count == 1, "относительный url в манифесте → от адреса манифеста");
            var noSha = ParseManifest("{\"version\":\"1.4.1\",\"url\":\"x.exe\"}", new Uri("https://example.com/u.json"), out string e4);
            Expect(noSha == null && e4.Contains("SHA-256"), "без sha256 → отказ («" + e4 + "»)");
            var httpExe = ParseManifest("{\"version\":\"1.4.1\",\"url\":\"http://example.com/x.exe\",\"sha256\":\"" + new string('a', 64) + "\"}", new Uri("https://example.com/u.json"), out _);
            Expect(httpExe == null, "установщик по http с чужого сервера → отказ");

            sb.AppendLine("Установщик (только проверки во временной папке, ничего не ставится):");
            string readyName = "PulsePC.Setup.Ready." + Guid.NewGuid().ToString("N");
            var req = SetupRequest.Parse(new[] { "--setup", "--ready-event", readyName }, @"C:\x\PulsePC.exe");
            Expect(req?.ReadyEvent == readyName, "--ready-event с правильным именем принят");
            Expect(SetupRequest.Parse(new[] { "--setup", "--ready-event", @"Global\PulsePC.Exit.Admin" }, @"C:\x\PulsePC.exe")?.ReadyEvent == null, "чужое имя события отклонено");
            using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName))
            {
                Installer.SignalReady(req);
                Expect(ready.WaitOne(0), "установщик подаёт знак «окно открылось» запустившей программе");
            }
            string sand = Path.Combine(Path.GetTempPath(), "PulsePC-dir-test-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(sand);
                Expect(Installer.CheckInstallDir(Path.Combine(sand, "Пульс ПК"), true) == null, "новая пустая папка подходит");
                File.WriteAllBytes(Path.Combine(sand, "PulsePC.exe"), new byte[] { 0 });
                Expect(Installer.CheckInstallDir(sand, true) == null, "папка прежней установки подходит");
                File.WriteAllBytes(Path.Combine(sand, "version.dll"), new byte[] { 0 });
                Expect(Installer.CheckInstallDir(sand, true)?.Contains("version.dll") == true, "папка с чужой DLL отклонена (её подхватила бы программа)");
                Expect(Installer.CheckInstallDir(@"\\server\share\Пульс ПК") != null, "сетевая папка отклонена");
                Expect(Installer.CheckInstallDir("Пульс ПК") != null, "неполный путь отклонён");
            }
            finally { try { Directory.Delete(sand, true); } catch { } }

            sb.AppendLine("Обновление без нажатия (логика, ничего не ставится):");
            var quiet = SetupRequest.Parse(new[] { "--setup", "--quiet", "--log", @"C:\x\a.log", "--relaunch-minimized", "--no-desktop" }, @"C:\x\PulsePC.exe");
            Expect(quiet is { Quiet: true, Relaunch: true, RelaunchMinimized: true, NoDesktop: true, Autostart: false } && quiet.Log == @"C:\x\a.log", "установщик понимает --quiet --relaunch-minimized --no-desktop");
            Expect(SetupRequest.Parse(new[] { "--setup", "--relaunch" }, @"C:\x\PulsePC.exe") is { Relaunch: false }, "перезапуск — только для установки без окна");
            Expect(SetupRequest.Parse(new[] { "--uninstall", "--quiet", "--relaunch" }, @"C:\x\PulsePC.exe") is { Relaunch: false }, "после удаления программа не запускается снова");
            var args = AutoArgs(@"C:\p\auto-install.log", windowVisible: false, desktopShortcut: false, autostart: true);
            Expect(args.SequenceEqual(new[] { "--quiet", "--log", @"C:\p\auto-install.log", "--relaunch-minimized", "--no-desktop", "--autostart" }),
                "ключи: без окна, журнал, перезапуск свёрнутой, ярлык и автозапуск — как были: " + string.Join(" ", args));
            Expect(AutoArgs("l", true, true, false).SequenceEqual(new[] { "--quiet", "--log", "l", "--relaunch" }), "окно было открыто — перезапуск с окном, ярлык не трогается");
            Expect(InstallMoment(true, 1, false, false, 2, 0), "первые минуты после запуска, игры нет — ставить");
            Expect(InstallMoment(true, 1, false, false, 300, IdleMinutes + 1), "человек 11 минут не трогал мышь — ставить");
            Expect(!InstallMoment(true, 1, false, false, 300, 2), "человек за компьютером, программа давно запущена — не ставить");
            Expect(!InstallMoment(true, 1, false, true, 1, 60), "идёт игра — не ставить никогда");
            Expect(!InstallMoment(false, 1, false, false, 1, 60) && !InstallMoment(true, 0, false, false, 1, 60) && !InstallMoment(true, 1, true, false, 1, 60),
                "выключено, нет новой версии или эта версия уже не встала — не ставить");
            var st = new AppSettings();
            Expect(!AutoInstall(st) && !AutoAsked(st), "по умолчанию выключено и не спрошено");
            st.Set(KAutoInstall, "0");
            Expect(!AutoInstall(st) && AutoAsked(st), "ответ «нет» запоминается — второй раз не спрашиваем");
            st.Set(KLastRun, "1.4.3");
            st.Set(KAutoPending, "99.0");
            st.Set("update.version", Installer.Version);
            st.Set("update.notes", "Первое\nВторое");
            AfterUpdate(st);
            Expect(st.Get(KLastRun) == Installer.Version && st.Get(KUpdatedTo) == Installer.Version && st.Get(KUpdatedFrom) == "1.4.3" && RecentUpdate(st).Recent,
                "после обновления: «Обновлено до " + Installer.Version + "» (было 1.4.3)");
            Expect(st.Get(KAutoFailed) == "99.0" && st.Get(KAutoPending) == "", "версия, которая не встала сама, больше сама не ставится (только кнопкой)");
            Expect(WhatsNew(st).SequenceEqual(new[] { "Первое", "Второе" }), "что нового — из описания выпуска");
            var fresh = new AppSettings();
            AfterUpdate(fresh);
            Expect(fresh.Get(KUpdatedTo) == "" && fresh.Get(KLastRun) == Installer.Version, "первая установка — без уведомления «Обновлено»");
            var old = new AppSettings();
            old.Set("versions.checked", "2026-11-20 10:00:00");
            AfterUpdate(old);
            Expect(old.Get(KUpdatedTo) == Installer.Version, "обновление с версии до 1.5 (номер не запоминался) тоже замечается");
            sb.AppendLine("  можно ли здесь ставить самой: " + (AutoState() is var a && a.Ok ? "да" : "нет — " + a.Why));

            sb.AppendLine("BIOS — адрес страницы по шаблону:");
            var msi = BoardPage("MSI", "PRO B760-P WIFI DDR4 (MS-7D98)", "1.0", "Intel");
            Expect(msi.Url == "https://www.msi.com/Motherboard/PRO-B760-P-WIFI-DDR4/support#bios", "MSI → " + msi.Url);
            var asus = BoardPage("ASUS", "ROG STRIX B550-F GAMING (WI-FI)", "Rev X.0x", "AMD");
            Expect(asus.Url == "https://www.asus.com/supportonly/ROG%20STRIX%20B550-F%20GAMING%20%28WI-FI%29/helpdesk_bios/", "ASUS → " + asus.Url);
            var giga = BoardPage("GIGABYTE", "B550 AORUS ELITE V2", "1.0", "AMD");
            Expect(giga.Url == "https://www.gigabyte.com/Motherboard/B550-AORUS-ELITE-V2-rev-10/support#support-dl-bios", "GIGABYTE → " + giga.Url);
            var asr = BoardPage("ASRock", "B550M Pro4", "", "AMD");
            Expect(asr.Url == "https://www.asrock.com/mb/AMD/B550M%20Pro4/index.asp#BIOS", "ASRock → " + asr.Url);
            var other = BoardPage("Biostar", "B760MX2-E", "", "Intel");
            Expect(other.Url == "", "неизвестный производитель → только поиск");

            sb.AppendLine("Скачивание и проверка (локальный сервер 127.0.0.1 вместо GitHub, временный ключ подписи, во временной папке):");
            LocalUpdateTest(sb, Expect);
        }
        catch (Exception ex) { fails.Add(ex.GetType().Name + ": " + ex.Message); sb.AppendLine("Сбой: " + ex); }

        // ---- что видно на этом ПК (только чтение; один запрос к NVIDIA, если видеокарта NVIDIA)
        sb.AppendLine("На этом компьютере:");
        try
        {
            sb.AppendLine("  «Пульс ПК» " + Installer.Version + "; папка для обновлений: " + UpdateFolder);
            var settings = AppSettings.Load();
            string feed = Feed(settings);
            sb.AppendLine("  источник обновлений: " + (feed == "" ? "не настроен" : feed + " (" + Classify(feed, out _) + ")"));
            var gpu = SafeMainGpu();
            if (gpu == null) sb.AppendLine("  видеокарта с драйвером не найдена");
            else
            {
                string dev = DeviceId(gpu);
                sb.AppendLine($"  видеокарта: {gpu.Name} [{gpu.Vendor}], код устройства {dev}, драйвер {gpu.RawVersion} → {gpu.Version}");
                if (gpu.Vendor == "NVIDIA" && dev != "")
                {
                    var sw = Stopwatch.StartNew();
                    var rel = QueryNvidia(dev, out string err);
                    if (rel == null) sb.AppendLine($"  сервис NVIDIA: не ответил — {err} ({sw.Elapsed.TotalSeconds:0.0} с)");
                    else
                    {
                        int diff = CompareVersions(rel.Version, gpu.Version);
                        sb.AppendLine($"  сервис NVIDIA за {sw.Elapsed.TotalSeconds:0.0} с: {rel.Name} {rel.Version} от {rel.Date:dd.MM.yyyy}{(rel.Beta ? ", бета" : "")}{(rel.Whql ? ", WHQL" : "")}");
                        sb.AppendLine("    итог: " + (diff > 0 ? "есть новее установленного" : diff == 0 ? "установлена последняя версия" : "установлена новее, чем на сайте"));
                        sb.AppendLine("    что нового: " + rel.DetailsUrl);
                        sb.AppendLine("    скачать: " + rel.DownloadUrl);
                    }
                    sb.AppendLine("  NVIDIA App: " + (NvidiaAppPath() ?? "не установлена"));
                }
                else sb.AppendLine("  страница драйверов: " + GpuDriver.DownloadUrl(gpu.Vendor) + " (производитель не спрашивается)");
            }
            var bios = ReadBios();
            sb.AppendLine($"  BIOS {bios.Version} от {(bios.Date == null ? "неизвестной даты" : bios.Date.Value.ToString("dd.MM.yyyy") + " (" + BiosAge(bios) + ")")}, {bios.Vendor}");
            sb.AppendLine($"  плата: {bios.BoardMaker} {bios.Board} (ревизия {bios.BoardVersion}) → {bios.Maker} {bios.Model}{(bios.Laptop ? ", ноутбук " + bios.SystemModel : "")}");
            sb.AppendLine("  страница BIOS: " + (bios.PageUrl == "" ? "шаблона нет" : bios.PageUrl) + " (программа её не запрашивает)");
            sb.AppendLine("  поиск: " + bios.SearchUrl);
        }
        catch (Exception ex) { sb.AppendLine("  сбой чтения: " + ex.Message); }

        return (fails.Count == 0 ? "OK" : "FAIL: " + string.Join("; ", fails)) + "\n" + sb;
    }

    private static string SampleNvidia(string version, string date, int beta) =>
        "{\"criteria\":{\"IsSupported\":{\"state\":\"true\"}},\"DriverAttributes\":{\"Name\":\"GeForce Game Ready Driver\",\"NameLocalized\":\"GeForce Game Ready Driver\"," +
        $"\"ReleaseDateTime\":\"{date}\",\"Version\":\"{version}\",\"IsBeta\":{beta},\"IsWHQL\":1," +
        "\"DetailsURL\":\"https:\\/\\/www.nvidia.com\\/en-us\\/drivers\\/details\\/999999\\/\"," +
        $"\"DownloadURLAdmin\":\"https:\\/\\/us.download.nvidia.com\\/Windows\\/{version}\\/{version}-desktop-win10-win11-64bit-international-dch-whql.exe\"}}}}";

    /// <summary>
    /// Полный путь обновления на локальных файлах: манифест и «GitHub» с сервера на 127.0.0.1 (вместо api.github.com и github.com),
    /// временный ключ подписи, скачивание, проверка SHA-256 и подписи, удаление подменённого файла, запуск — только «на словах» (dryRun).
    /// </summary>
    private static void LocalUpdateTest(StringBuilder sb, Action<bool, string> expect)
    {
        string root = Path.Combine(Path.GetTempPath(), "PulsePC-update-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        string api = GitHubApi, repo = OfficialRepo;
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        TestLoopback = true;
        TestKey = signer.ExportParameters(false);
        try
        {
            // Подставной «установщик»: заголовок программы Windows и случайные байты.
            var payload = new byte[300_000];
            new Random(7).NextBytes(payload);
            payload[0] = (byte)'M'; payload[1] = (byte)'Z';
            string sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            string next = "99.0.0";
            string Sign(string version, string hash) => Convert.ToBase64String(signer.SignData(Encoding.UTF8.GetBytes(SignedText(version, hash)), HashAlgorithmName.SHA256));
            string sig = Sign(next, sha);

            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string bas = $"http://127.0.0.1:{port}";
            string Release(string dir, bool withSig) =>
                $"{{\"tag_name\":\"v{next}\",\"published_at\":\"2026-11-21T10:00:00Z\",\"body\":\"## Что нового\\r\\n- Первое\\r\\n- Второе\",\"assets\":[" +
                $"{{\"name\":\"PulsePC-Setup-{next}.exe\",\"browser_download_url\":\"{bas}/{dir}/PulsePC-Setup-{next}.exe\"}}," +
                (withSig ? $"{{\"name\":\"PulsePC-Setup-{next}.exe.sig\",\"browser_download_url\":\"{bas}/{dir}/PulsePC-Setup-{next}.exe.sig\"}}," : "") +
                $"{{\"name\":\"PulsePC-Setup-{next}.exe.sha256\",\"browser_download_url\":\"{bas}/{dir}/PulsePC-Setup-{next}.exe.sha256\"}}]}}";
            byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["/feed/update.json"] = Encoding.UTF8.GetBytes($"{{\"version\":\"{next}\",\"url\":\"PulsePC-Setup-{next}.exe\",\"sha256\":\"{sha}\",\"date\":\"2026-11-20\",\"notes\":[\"Проверка обновления\"]}}"),
                // Официальный выпуск: установщик, .sha256 и .sig.
                ["/repos/test/pulse/releases/latest"] = Encoding.UTF8.GetBytes(Release("gh", true)),
                [$"/gh/PulsePC-Setup-{next}.exe"] = payload,
                [$"/gh/PulsePC-Setup-{next}.exe.sha256"] = Ascii($"{sha.ToUpperInvariant()}  PulsePC-Setup-{next}.exe\n"),
                [$"/gh/PulsePC-Setup-{next}.exe.sig"] = Ascii(sig + "\n"),
                // Выпуск без подписи.
                ["/repos/nosig/pulse/releases/latest"] = Encoding.UTF8.GetBytes(Release("nosig", false)),
                [$"/nosig/PulsePC-Setup-{next}.exe"] = payload,
                [$"/nosig/PulsePC-Setup-{next}.exe.sha256"] = Ascii(sha),
                // Подпись от другой версии (старый подписанный файл выдают за новый).
                ["/repos/oldsig/pulse/releases/latest"] = Encoding.UTF8.GetBytes(Release("oldsig", true)),
                [$"/oldsig/PulsePC-Setup-{next}.exe"] = payload,
                [$"/oldsig/PulsePC-Setup-{next}.exe.sha256"] = Ascii(sha),
                [$"/oldsig/PulsePC-Setup-{next}.exe.sig"] = Ascii(Sign("98.0.0", sha)),
                // Контрольная сумма не та.
                ["/repos/badsha/pulse/releases/latest"] = Encoding.UTF8.GetBytes(Release("badsha", true)),
                [$"/badsha/PulsePC-Setup-{next}.exe"] = payload,
                [$"/badsha/PulsePC-Setup-{next}.exe.sha256"] = Ascii(new string('0', 64)),
                [$"/badsha/PulsePC-Setup-{next}.exe.sig"] = Ascii(Sign(next, new string('0', 64))),
            };
            // Перенаправление на чужой сайт: скачивание должно остановиться, не обращаясь туда.
            var redirects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [$"/redir/PulsePC-Setup-{next}.exe"] = $"https://evil.example/PulsePC-Setup-{next}.exe",
            };
            var served = new List<string>();
            var server = new Thread(() =>
            {
                while (true)
                {
                    TcpClient c;
                    try { c = listener.AcceptTcpClient(); } catch { return; }
                    try
                    {
                        using (c)
                        {
                            var ns = c.GetStream();
                            var reader = new StreamReader(ns, Encoding.ASCII, false, 1024, leaveOpen: true);
                            string line = reader.ReadLine() ?? "";
                            while (!string.IsNullOrEmpty(reader.ReadLine())) { }
                            string path = line.Split(' ').ElementAtOrDefault(1) ?? "/";
                            lock (served) served.Add(path);
                            byte[] head, body;
                            if (redirects.TryGetValue(path, out string to))
                            {
                                body = Array.Empty<byte>();
                                head = Ascii($"HTTP/1.1 302 Found\r\nLocation: {to}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                            }
                            else
                            {
                                bool ok = files.TryGetValue(path, out body);
                                body ??= Ascii("not found");
                                head = Ascii($"HTTP/1.1 {(ok ? "200 OK" : "404 Not Found")}\r\nContent-Length: {body.Length}\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n");
                            }
                            ns.Write(head, 0, head.Length);
                            ns.Write(body, 0, body.Length);
                        }
                    }
                    catch { }
                }
            }) { IsBackground = true };
            server.Start();
            GitHubApi = bas;

            // 1. Манифест по http://127.0.0.1 — только сведения о версии (сам установщик берётся из официального выпуска).
            var rel = QueryFeed(bas + "/feed/update.json", out string err);
            expect(rel != null && rel.Version == next && rel.Sha256 == sha && rel.Url == $"{bas}/feed/PulsePC-Setup-{next}.exe", "манифест прочитан: версия " + next + (err == "" ? "" : " — " + err));
            expect(rel != null && CompareVersions(rel.Version, Installer.Version) > 0, $"{next} новее установленной {Installer.Version}");

            // 2. Официальный выпуск: версия, SHA-256 и подпись.
            OfficialRepo = "test/pulse";
            var gh = QueryOfficial(out string ghErr);
            expect(gh != null && gh.Version == next && gh.Sha256 == sha && gh.Signature == sig && gh.Date == "2026-11-21" && gh.Notes.SequenceEqual(new[] { "Что нового", "Первое", "Второе" }),
                "GitHub: версия из tag_name, sha256 из .sha256, подпись из .sig, описание выпуска" + (ghErr == "" ? "" : " — " + ghErr));
            string dl = Path.Combine(root, "update");
            long seen = 0;
            var d = Download(gh, dl, (done, total) => seen = done);
            expect(d.Ok && File.Exists(d.Path) && Path.GetFileName(d.Path) == $"PulsePC-Setup-{next}.exe" && seen == payload.Length && d.Message.Contains("подпись"), "скачан и проверен: " + d.Message);
            expect(d.Path != null && !File.Exists(d.Path + ".part"), "недокачанного файла не осталось");
            expect(AlreadyDownloadedIn(Path.GetDirectoryName(d.Path), gh) == d.Path, "уже скачанный и совпавший по SHA-256 файл второй раз не качается");
            using (var autoRun = Launch(d.Path, gh, dryRun: true, extraArgs: AutoArgs(Path.Combine(root, "auto-install.log"), true, true, false)))
                expect(!autoRun.Started && LastDryRunArgs.Contains("--quiet") && LastDryRunArgs.Contains("--relaunch"),
                    "без нажатия — тот же запуск с теми же проверками, установщику уходит: " + LastDryRunArgs);
            using (var run = Launch(d.Path, gh, dryRun: true))
            {
                expect(!run.Started && run.Message.StartsWith("Проверка: был бы запущен"), "запуск (проверка): " + run.Message);
                sb.AppendLine("       → " + run.Message);
            }
            // Пока файл держит запуск, записать в него нельзя.
            bool locked;
            using (new FileStream(d.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { using var w = new FileStream(d.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); locked = false; }
                catch (IOException) { locked = true; }
            }
            expect(locked, "проверенный файл открыт без права записи для других — подменить его до запуска нельзя");

            // 2а. Обновление без нажатия — те же шаги, что раз в минуту в программе: скачать, предупредить, через минуту запустить.
            var auto = new AppSettings();
            auto.Set("update.feed", "test/pulse");
            auto.Set("update.source", "test/pulse");
            auto.Set("update.version", next);
            auto.Set(KAutoInstall, "1");
            string autoDir = Path.Combine(root, "update-auto");
            var t0 = DateTime.Now;
            ready = null; armed = null; lastAttempt = DateTime.MinValue;
            var steps = new List<string>
            {
                AutoStep(auto, () => true, t0, 0, autoDir, true),                                 // идёт игра
                AutoStep(auto, () => false, t0, 0, autoDir, true),                                // скачать
                AutoStep(auto, () => false, t0.AddSeconds(30), 0, autoDir, true),                 // предупредить
                AutoStep(auto, () => false, t0.AddSeconds(50), 0, autoDir, true),                 // ждать минуту
                AutoStep(auto, () => true, t0.AddSeconds(95), 0, autoDir, true),                  // за минуту запустили игру
                AutoStep(auto, () => false, t0.AddSeconds(100), 0, autoDir, true),                // снова предупредить
                AutoStep(auto, () => false, t0.AddSeconds(170), 0, autoDir, true),                // запустить (проверка)
            };
            expect(steps[0] == "игра" && steps[1] == "скачано" && steps[2] == "предупреждение" && steps[3] == "ждёт после предупреждения"
                   && steps[4] == "игра" && steps[5] == "предупреждение" && steps[6].StartsWith("Проверка: был бы запущен"),
                "без нажатия: игра — ждёт; скачал; предупредил; игра началась — отложил; через минуту — запуск: " + string.Join(" → ", steps.Select(x => x.Length > 30 ? x[..30] + "…" : x)));
            expect(LastDryRunArgs.Contains("--quiet") && LastDryRunArgs.Contains("--relaunch") && auto.Get(KAutoPending) == "" && auto.Get(KAutoFailed) == "",
                "установщику — без окна и с перезапуском; в проверке ничего не запущено, версия не помечена как неудачная");
            ready = null; armed = null; lastAttempt = DateTime.MinValue;
            int exeBefore;
            lock (served) exeBefore = served.Count(x => x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            var later = AutoStep(auto, () => false, t0.AddHours(2), 0, autoDir, true);
            int exeAfter;
            lock (served) exeAfter = served.Count(x => x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            expect(later == "скачано" && Directory.GetFiles(autoDir, "*.exe").Length == 1 && exeAfter == exeBefore,
                "повторный запуск: уже скачанный файл не качается заново (" + later + ")");
            auto.Set(KAutoInstall, "0");
            expect(AutoStep(auto, () => false, t0, 0, autoDir, true) == "выключено", "выключено — ничего не делает");
            ready = null; armed = null; lastAttempt = DateTime.MinValue;

            // 3. Файл изменили после скачивания — запуск отказывается и удаляет его.
            File.WriteAllBytes(d.Path, payload.Take(1000).ToArray());
            using (var run2 = Launch(d.Path, gh, dryRun: true))
                expect(!run2.Started && !File.Exists(d.Path), "изменённый после скачивания файл не запускается и удаляется");

            // 4. Подпись: нет, от другой версии, неверная контрольная сумма.
            OfficialRepo = "nosig/pulse";
            var nosig = QueryOfficial(out _);
            var dNo = Download(nosig, Path.Combine(root, "update-nosig"));
            expect(nosig != null && nosig.Signature == "" && !dNo.Ok && dNo.Message.Contains("подпись"), "выпуск без .sig не скачивается: " + dNo.Message);
            OfficialRepo = "oldsig/pulse";
            var oldsig = QueryOfficial(out _);
            var dOld = Download(oldsig, Path.Combine(root, "update-oldsig"));
            expect(!dOld.Ok && !File.Exists(Path.Combine(root, "update-oldsig", $"PulsePC-Setup-{next}.exe")) && dOld.Message.Contains("Подпись"), "подпись от другой версии отклонена, файл удалён: " + dOld.Message);
            // 4а. Без нажатия: выпуск не прошёл проверку подписи — эта версия сама больше не качается (не 400 МБ каждый час).
            var autoBad = new AppSettings();
            autoBad.Set("update.feed", "test/pulse");
            autoBad.Set("update.source", "test/pulse");
            autoBad.Set("update.version", next);
            autoBad.Set(KAutoInstall, "1");
            ready = null; armed = null; lastAttempt = DateTime.MinValue;
            string badDir = Path.Combine(root, "update-auto-bad");
            var bad1 = AutoStep(autoBad, () => false, DateTime.Now, 0, badDir, true);
            lastAttempt = DateTime.MinValue;
            var bad2 = AutoStep(autoBad, () => false, DateTime.Now.AddHours(2), 0, badDir, true);
            expect(bad1.StartsWith("не скачано") && autoBad.Get(KAutoFailed) == next && bad2 == "эта версия уже не встала",
                "без нажатия: выпуск с чужой подписью не скачивается снова через час (" + (bad1.Length > 40 ? bad1[..40] + "…" : bad1) + " → " + bad2 + ")");
            ready = null; armed = null; lastAttempt = DateTime.MinValue;
            using (var forged = Launch(d.Path, new AppRelease { Version = next, Sha256 = sha, Signature = Sign("98.0.0", sha) }, dryRun: true))
                expect(!forged.Started, "запуск с чужой подписью отклонён");
            OfficialRepo = "badsha/pulse";
            var badsha = QueryOfficial(out _);
            string badFile = Path.Combine(root, "update-bad", $"PulsePC-Setup-{next}.exe");
            var dBad = Download(badsha, Path.Combine(root, "update-bad"));
            expect(!dBad.Ok && !File.Exists(badFile) && !File.Exists(badFile + ".part") && dBad.Message.Contains("не совпала"), "подменённый файл удалён: " + dBad.Message);

            // 5. Перенаправление на чужой сайт.
            var dRedir = Download(new AppRelease { Version = next, Sha256 = sha, Signature = sig, Url = $"{bas}/redir/PulsePC-Setup-{next}.exe" }, Path.Combine(root, "update-redir"));
            expect(!dRedir.Ok && dRedir.Message.Contains("evil.example"), "перенаправление не на GitHub остановлено: " + dRedir.Message);

            // 6. Папка для скачивания подменена ссылкой — ссылка убирается, файл ложится в настоящую папку.
            string target = Path.Combine(root, "elsewhere"), link = Path.Combine(root, "update-link");
            Directory.CreateDirectory(target);
            var mk = Shell.Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), $"/d /c mklink /J \"{link}\" \"{target}\"", 10000);
            if (mk.Code == 0 && IsReparse(link))
            {
                OfficialRepo = "test/pulse";
                var dLink = Download(QueryOfficial(out _), link);
                expect(dLink.Ok && !IsReparse(link) && Directory.GetFiles(target).Length == 0, "точка соединения на месте папки обновлений убрана, в чужую папку ничего не записано");
            }
            else sb.AppendLine("       (точку соединения создать не удалось — проверка пропущена)");

            // 7. Нет сервера — понятная ошибка, а не зависание.
            var down = QueryFeed("http://127.0.0.1:9/update.json", out string downErr, 3000);
            expect(down == null && downErr != "", "сервер недоступен → «" + downErr + "»");
            sb.AppendLine("       запросы к локальному серверу: " + string.Join(", ", served.Distinct()));
        }
        finally
        {
            GitHubApi = api;
            OfficialRepo = repo;
            TestLoopback = false;
            TestKey = null;
            try { listener.Stop(); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
