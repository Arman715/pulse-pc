using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Windows.Media.Imaging;

namespace PulsePC.Services;

/// <summary>
/// Обратная связь разработчику: сообщение о проблеме (описание, снимки экрана, сведения о программе) и отзыв с оценкой.
/// Программа ничего не отправляет сама и не хранит паролей и ключей: она собирает архив в «Документы\Пульс ПК\Отчёты»,
/// открывает письмо на адрес разработчика в почте пользователя (mailto:) и показывает архив в Проводнике —
/// пользователь прикрепляет его и нажимает «Отправить». Что уходит, видно до отправки.
/// </summary>
public static class Feedback
{
    /// <summary>Адрес разработчика для писем. Пусто — адрес ещё не задан: архив собирается, письмо не открывается.</summary>
    public const string Address = "pulsepc.feedback@gmail.com";

    public const string RepoUrl = "https://github.com/Arman715/pulse-pc";

    public const int MaxImages = 6;

    public enum Kind { Problem, Review }

    public sealed class Draft
    {
        public Kind Kind;
        /// <summary>Для проблемы: «Ошибка или сбой» / «Работает не так» / «Неудобно, есть идея» (по-русски, для письма).</summary>
        public string Category = "";
        public string Text = "";
        public string Contact = "";
        public int Stars;
        public bool IncludeReport = true;
        public List<(string Name, byte[] Png)> Images = new();
    }

    public sealed class Result
    {
        public string Archive;          // null — без архива (отзыв без снимков)
        public string Subject = "", Body = "";
        public bool MailOpened;
        public string Error;
    }

    public static bool HasAddress => Address.Contains('@');

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), L.T("Пульс ПК"), L.T("Отчёты"));

    /// <summary>Тема письма: по ней разработчик сортирует почту, поэтому она одинаковая на любом языке интерфейса.</summary>
    public static string Subject(Draft d) => d.Kind == Kind.Review
        ? $"Pulse PC {Installer.Version}: review {d.Stars}/5"
        : $"Pulse PC {Installer.Version}: problem — {Short(d.Text, 60)}";

    /// <summary>Текст письма (и файл description.txt в архиве).</summary>
    public static string Body(Draft d, string archiveName)
    {
        var sb = new StringBuilder();
        if (d.Kind == Kind.Review)
        {
            sb.AppendLine(L.F("Оценка: {0} из 5", d.Stars) + " " + new string('★', d.Stars) + new string('☆', 5 - d.Stars));
            sb.AppendLine();
            sb.AppendLine(d.Text.Trim().Length > 0 ? d.Text.Trim() : L.T("(без комментария)"));
        }
        else
        {
            sb.AppendLine(L.T("Что случилось") + ": " + d.Category);
            sb.AppendLine();
            sb.AppendLine(d.Text.Trim());
        }
        sb.AppendLine();
        if (d.Contact.Trim().Length > 0) sb.AppendLine(L.T("Ответить на") + ": " + d.Contact.Trim());
        sb.AppendLine($"Pulse PC {Installer.Version} · Windows {Environment.OSVersion.Version.Build} · {L.Code}");
        if (archiveName != null) sb.AppendLine(L.F("Вложение: {0} — прикрепите этот файл к письму.", archiveName));
        return sb.ToString();
    }

    /// <summary>Собрать архив (если есть что прикладывать) и открыть письмо. Вызывать не из потока окна: сбор сведений занимает секунды.</summary>
    public static Result Prepare(Draft d, bool admin)
    {
        var r = new Result();
        try
        {
            // Без адреса письмо не открыть — тогда сохраняется хотя бы архив с текстом, чтобы его можно было отправить вручную.
            bool needArchive = !HasAddress || d.Images.Count > 0 || (d.Kind == Kind.Problem && d.IncludeReport);
            string name = null;
            if (needArchive)
            {
                Directory.CreateDirectory(Folder);
                name = (d.Kind == Kind.Review ? "PulsePC-review-" : "PulsePC-report-") + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip";
                r.Archive = Path.Combine(Folder, name);
                using var zip = ZipFile.Open(r.Archive, ZipArchiveMode.Create);
                Add(zip, "description.txt", Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Body(d, null))).ToArray());
                if (d.Kind == Kind.Problem && d.IncludeReport)
                    Add(zip, "report.txt", Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(SafeReport(admin))).ToArray());
                int i = 1;
                foreach (var (_, png) in d.Images) Add(zip, $"screenshot-{i++}.png", png);
            }
            r.Subject = Subject(d);
            r.Body = Body(d, name);
            // Письмо открывается не сразу: человек выбирает, где у него почта (см. Open). Ссылка mailto: у большинства
            // открывала просто браузер — почтовой программы в Windows нет, а почта открыта на сайте.
            if (!HasAddress && r.Archive != null) ShowInFolder(r.Archive);
        }
        catch (Exception ex)
        {
            r.Error = Installer.Reason(ex);
            Log.Error("обратная связь: подготовка письма", ex);
        }
        return r;
    }

    private static void Add(ZipArchive zip, string name, byte[] data)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = e.Open();
        s.Write(data, 0, data.Length);
    }

    /// <summary>Сведения о программе: версии, части программы, хвост журнала. Путь к папке пользователя заменяется на %USERPROFILE%.</summary>
    public static string SafeReport(bool admin)
    {
        string text;
        try { text = HealthWatch.Report(admin, 150); }
        catch (Exception ex) { text = "report failed: " + ex.Message; }
        // Окно обещает «версия, процессор и видеокарта» — модели берутся из реестра (только названия, ничего личного).
        try { text = Hardware() + Environment.NewLine + text; } catch { }
        // Те же правила, что в самом отчёте: имя пользователя и компьютера — только целым словом. Простая замена подстроки
        // при коротком имени («Ann», «PC») портила весь отчёт («ch%USERNAME%el»), а при пустом имени бросала исключение.
        return HealthWatch.Scrub(text);
    }

    /// <summary>«Processor: …; Graphics: …; RAM: … GB; laptop: yes/no» — по-английски, чтобы отчёт читался одинаково при любом языке.</summary>
    private static string Hardware()
    {
        string cpu = "";
        using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
            cpu = (k?.GetValue("ProcessorNameString") as string ?? "").Trim();
        var gpus = new List<string>();
        using (var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
        {
            foreach (string sub in cls?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                if (!int.TryParse(sub, out _)) continue;
                using var a = cls.OpenSubKey(sub);
                string name = (a?.GetValue("DriverDesc") as string ?? "").Trim();
                string ver = (a?.GetValue("DriverVersion") as string ?? "").Trim();
                if (name.Length > 0 && !gpus.Any(g => g.StartsWith(name, StringComparison.OrdinalIgnoreCase)))
                    gpus.Add(ver.Length > 0 ? $"{name} (driver {ver})" : name);
            }
        }
        double ram = 0;
        try { ram = Math.Round(Metrics.ReadRam().TotalGb); } catch { }
        return $"Processor: {(cpu == "" ? "?" : cpu)}{Environment.NewLine}Graphics: {(gpus.Count == 0 ? "?" : string.Join("; ", gpus))}{Environment.NewLine}"
             + $"RAM: {ram:0} GB; laptop: {(LaptopCheck.Applies ? "yes" : "no")}";
    }

    /// <summary>mailto: с темой и текстом (длинный текст укорачивается, см. <see cref="ComposeUrl"/>).</summary>
    public static string MailTo(string subject, string body) => ComposeUrl(Service.App, subject, body);

    /// <summary>Где у человека почта: сайт почты (новое письмо открывается в браузере) или почтовая программа.</summary>
    public enum Service { Gmail, Yandex, MailRu, Outlook, App }

    public static string ServiceName(Service s) => s switch
    {
        Service.Gmail => "Gmail",
        Service.Yandex => L.T("Яндекс Почта"),
        Service.MailRu => "Mail.ru",
        Service.Outlook => "Outlook.com",
        _ => L.T("Почтовая программа"),
    };

    /// <summary>
    /// Самый длинный адрес письма. Длиннее — обрезают почтовые сайты и сам Windows: ссылка-ярлык .url, через которую программа
    /// открывает длинные адреса (Проводник режет свой параметр на 259 знаках), читается целиком примерно до 5000 знаков.
    /// </summary>
    public const int MaxUrl = 4500;

    /// <summary>
    /// Адрес «нового письма» с уже заполненными адресом, темой и текстом. Кириллица в адресе вырастает вшестеро (%D0%9F),
    /// поэтому длинный текст укорачивается так, чтобы адрес поместился, а в конец письма дописывается, что полный текст —
    /// в буфере обмена (окно копирует его туда, когда открывает письмо).
    /// </summary>
    public static string ComposeUrl(Service s, string subject, string body)
    {
        body ??= "";
        string url = BuildUrl(s, subject, body);
        if (body.Length <= 1500 && url.Length <= MaxUrl) return url;
        string note = "\n…\n" + L.T("(Текст не поместился в ссылку целиком. Полный текст уже в буфере обмена — выделите эту строку и вставьте его: Ctrl+V.)");
        int lo = 0, hi = Math.Min(body.Length, 1500);
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (BuildUrl(s, subject, body[..mid] + note).Length <= MaxUrl) lo = mid; else hi = mid - 1;
        }
        return BuildUrl(s, subject, body[..lo].TrimEnd() + note);
    }

    private static string BuildUrl(Service s, string subject, string body)
    {
        string to = Uri.EscapeDataString(Address), su = Uri.EscapeDataString(subject), b = Uri.EscapeDataString(Crlf(body));
        return s switch
        {
            Service.Gmail => $"https://mail.google.com/mail/?view=cm&fs=1&to={to}&su={su}&body={b}",
            Service.Yandex => $"https://mail.yandex.ru/compose?to={to}&subject={su}&body={b}",
            Service.MailRu => $"https://e.mail.ru/compose/?to={to}&subject={su}&body={b}",
            Service.Outlook => $"https://outlook.live.com/mail/0/deeplink/compose?to={to}&subject={su}&body={b}",
            _ => "mailto:" + Address + "?subject=" + su + "&body=" + b,
        };
    }

    /// <summary>Открыть новое письмо в выбранной почте и показать архив, который нужно к нему прикрепить.</summary>
    public static void Open(Service s, Result r)
    {
        GameLibrary.OpenAsUser(ComposeUrl(s, r.Subject, r.Body));
        if (r.Archive != null) ShowInFolder(r.Archive);
    }

    private static string Crlf(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");

    public static void ShowInFolder(string file)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + file + "\"") { UseShellExecute = false })?.Dispose(); }
        catch (Exception ex) { Log.Error("обратная связь: показать архив", ex); }
    }

    public static void OpenRepo() => GameLibrary.OpenAsUser(RepoUrl);

    /// <summary>Картинка в PNG (снимки окна, экрана, из буфера и из файлов).</summary>
    public static byte[] Png(BitmapSource image)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static string Short(string s, int max)
    {
        s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Length <= max ? (s.Length == 0 ? "—" : s) : s[..max].TrimEnd() + "…";
    }

    // ------------------------------------------------------------------ просьба оценить

    private const string KRated = "feedback.rated";       // «1» — оценил или попросил больше не спрашивать
    private const string KLater = "feedback.later";       // до какой даты не спрашивать (ISO)

    /// <summary>
    /// Показать ли в «Сессиях» просьбу оценить программу: не раньше трёх записанных игр и двух дней пользования,
    /// «Позже» — через две недели, «Не спрашивать» и уже оставленный отзыв — никогда.
    /// </summary>
    public static bool ShouldAskRating(AppSettings s, IReadOnlyCollection<GameSession> sessions)
    {
        if (s == null || s.GetBool(KRated)) return false;
        if (DateTime.TryParse(s.Get(KLater), null, System.Globalization.DateTimeStyles.RoundtripKind, out var later) && DateTime.Now < later) return false;
        if (sessions == null || sessions.Count < 3) return false;
        var first = sessions.Min(x => x.Start);
        return (DateTime.Now - first).TotalDays >= 2;
    }

    public static void RatingDone(AppSettings s) { s.SetBool(KRated, true); s.Save(); }
    public static void RatingLater(AppSettings s) { s.Set(KLater, DateTime.Now.AddDays(14).ToString("o")); s.Save(); }
}
