using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;

namespace PulsePC.Services;

/// <summary>Состояние одной части программы для экрана «Состояние программы» и для отчёта.</summary>
public sealed class PartState
{
    public string Name { get; init; } = "";
    public Severity Level { get; init; }
    /// <summary>«работает», «нет данных 12 мин», «ошибка», «ждёт игры».</summary>
    public string Status { get; init; } = "";
    /// <summary>Когда часть последний раз отработала и чем кончилась последняя ошибка — простыми словами.</summary>
    public string Detail { get; init; } = "";
    /// <summary>Текст ошибки как его записала сама часть (для подсказки и отчёта); пусто, если ошибок не было.</summary>
    public string Raw { get; init; } = "";
    /// <summary>Часть не работает дольше <see cref="HealthWatch.AlarmMinutes"/> минут.</summary>
    public bool Alarm { get; init; }
}

/// <summary>
/// Сторож здоровья частей программы. Раз в 5 секунд читает <see cref="Health.All"/> и запоминает то, чего там нет:
/// с какого момента часть ошибается без перерыва и как часто она обычно отмечается (чтобы отличать «нет данных»
/// от редкой, но исправной работы). <see cref="Alarm"/> — какая-то часть не работает дольше 10 минут: тогда у раздела
/// «Состояние программы» в списке вкладки «Система» стоит красная метка. Уведомлений сторож не показывает.
/// Запускается при первом обращении к <see cref="Current"/> (разметка окна обращается к нему при загрузке).
/// </summary>
public sealed class HealthWatch : Notifier
{
    public static readonly HealthWatch Current = new();
    public const int AlarmMinutes = 10;

    /// <summary>Части, которые работают только во время игры: вне игры их молчание — не неисправность.</summary>
    private static readonly string[] GameOnly = { "Запись сессий", "Сеть игры", "Кадры" };
    /// <summary>Сколько молчания считается нормой, пока не известно, как часто часть отмечается.</summary>
    private const double QuietSeconds = 300;

    private sealed class Track
    {
        public DateTime LastOk = DateTime.MinValue;
        public double GapSeconds;          // обычный промежуток между отметками (наибольший из недавних)
        public DateTime? FailingSince;     // первая замеченная ошибка в непрерывной череде ошибок
    }

    private readonly Dictionary<string, Track> tracks = new();
    private readonly object gate = new();
    private readonly Timer timer;
    private bool alarm;

    private HealthWatch()
    {
        timer = new Timer(_ => Poll(), null, 0, 5000);
    }

    /// <summary>Запустить сторожа, если он ещё не запущен (повторный вызов ничего не делает).</summary>
    public static void Start() => _ = Current.timer;

    /// <summary>Какая-то часть не работает дольше 10 минут.</summary>
    public bool Alarm
    {
        get => alarm;
        private set
        {
            if (alarm == value) return;
            alarm = value;
            var d = Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess()) Raise(nameof(Alarm));
            else d.BeginInvoke(new Action(() => Raise(nameof(Alarm))));
        }
    }

    private void Poll()
    {
        try
        {
            var now = DateTime.Now;
            bool any = false;
            lock (gate)
            {
                foreach (var p in Health.All())
                {
                    if (!tracks.TryGetValue(p.Name, out var t)) tracks[p.Name] = t = new Track();
                    if (p.LastOk > t.LastOk)
                    {
                        if (t.LastOk != DateTime.MinValue)
                        {
                            double gap = (p.LastOk - t.LastOk).TotalSeconds;
                            t.GapSeconds = Math.Max(gap, t.GapSeconds * 0.9);
                        }
                        t.LastOk = p.LastOk;
                    }
                    bool failing = p.LastFail > p.LastOk;
                    // Игровая часть вне игры: её ошибка осталась от прошлой игры и сейчас ничему не мешает — длительной
                    // неисправностью не считается (в следующей игре счёт начнётся заново).
                    if (!failing || PastGameError(p)) t.FailingSince = null;
                    else t.FailingSince ??= p.LastFail;
                    if (t.FailingSince is DateTime since && now - since > TimeSpan.FromMinutes(AlarmMinutes)) any = true;
                }
            }
            Alarm = any;
        }
        catch { }
    }

    /// <summary>Состояние всех частей, которые уже отметились, по алфавиту.</summary>
    public List<PartState> Snapshot()
    {
        Poll();
        var now = DateTime.Now;
        var list = new List<PartState>();
        foreach (var p in Health.All())
        {
            Track t;
            lock (gate) tracks.TryGetValue(p.Name, out t);
            bool failing = p.LastFail > p.LastOk;
            bool hadError = p.LastFail > DateTime.MinValue && !string.IsNullOrWhiteSpace(p.Error);
            string lastError = hadError ? " " + L.F("Последняя ошибка {0}: {1}", When(p.LastFail), Plain(p.Error)) : "";

            // Игровая часть ждёт: игры нет — или в текущей игре она не включалась (античит, запись кадров выключена).
            bool waitsForGame = IsGameOnly(p.Name) && (!GameHooks.InGame || p.LastOk.AddMinutes(1) < GameHooks.StartedAt);
            if (failing && PastGameError(p))
            {
                // Ошибка была в прошлой игре; сейчас часть ждёт следующей — красной метки нет.
                list.Add(new PartState
                {
                    Name = p.Name, Level = Severity.Warn, Status = L.T("ошибка в прошлой игре"), Raw = p.Error ?? "",
                    Detail = L.T("Работает только во время игры.") + (p.LastOk == DateTime.MinValue ? "" : " " + L.F("Последний раз работала {0}.", When(p.LastOk)))
                             + " " + L.F("В прошлый раз была ошибка {0}: {1} В следующей игре программа попробует снова.", When(p.LastFail), Plain(p.Error)),
                });
                continue;
            }
            if (failing)
            {
                var since = t?.FailingSince ?? p.LastFail;
                bool lasting = now - since > TimeSpan.FromMinutes(AlarmMinutes);
                string ok = p.LastOk == DateTime.MinValue ? L.T("С запуска программы ни разу не сработала.") : L.F("Последний раз работала {0}.", When(p.LastOk));
                list.Add(new PartState
                {
                    Name = p.Name, Level = Severity.Bad, Status = L.T("ошибка"), Raw = p.Error ?? "", Alarm = lasting,
                    Detail = Plain(p.Error) + " " + ok + " " + (lasting ? L.F("Не работает уже {0}.", Span(now - since)) : L.T("Программа пробует снова сама.")),
                });
                continue;
            }
            if (p.LastOk == DateTime.MinValue) continue;

            // Часть, объявившая свой срок (Health.Expect, например диски — раз в 30 минут), молчит законно до двух таких сроков.
            double quiet = Math.Max(Math.Max(QuietSeconds, 4 * (t?.GapSeconds ?? 0)), 2 * Health.ExpectedPeriod(p.Name).TotalSeconds);
            var silent = now - p.LastOk;
            if (silent.TotalSeconds <= quiet)
            {
                list.Add(new PartState
                {
                    Name = p.Name, Level = Severity.Ok, Status = L.T("работает"), Raw = hadError ? p.Error : "",
                    Detail = L.F("Последний раз отметилась {0}.", When(p.LastOk)) + (hadError ? lastError : " " + L.T("Ошибок не было.")),
                });
            }
            else if (waitsForGame)
            {
                list.Add(new PartState
                {
                    Name = p.Name, Level = Severity.Info, Status = L.T("ждёт игры"), Raw = hadError ? p.Error : "",
                    Detail = L.F("Работает только во время игры. Последний раз — {0}.", When(p.LastOk)) + lastError,
                });
            }
            else
            {
                list.Add(new PartState
                {
                    Name = p.Name, Level = Severity.Warn, Status = L.F("нет данных {0}", Span(silent)), Raw = hadError ? p.Error : "",
                    Detail = L.F("Последний раз отметилась {0}. Если так и останется, перезапустите программу.", When(p.LastOk)) + lastError,
                });
            }
        }
        return list;
    }

    private static bool IsGameOnly(string name) => GameOnly.Any(g => name.StartsWith(g, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Ошибка игровой части осталась от прошлой игры: сейчас игры нет или ошибка случилась раньше начала текущей
    /// (минута запаса: запись начинается чуть раньше, чем приходит событие «игра началась»). Длительной неисправностью не считается.
    /// </summary>
    private static bool PastGameError(Health.Part p) =>
        IsGameOnly(p.Name) && (!GameHooks.InGame || p.LastFail.AddMinutes(1) < GameHooks.StartedAt);

    // ================================================================ тексты

    /// <summary>«в 21:40:05», «вчера в 21:40», «18 ноября в 21:40».</summary>
    public static string When(DateTime t)
    {
        int days = (DateTime.Today - t.Date).Days;
        return days == 0 ? L.F("в {0:HH:mm:ss}", t) : days == 1 ? L.F("вчера в {0:HH:mm}", t) : L.F("{0:d MMMM} в {0:HH:mm}", t);
    }

    /// <summary>«меньше минуты», «12 мин», «2 ч 5 мин».</summary>
    public static string Span(TimeSpan s)
    {
        if (s.TotalMinutes < 1) return L.T("меньше минуты");
        if (s.TotalHours < 1) return L.F("{0} мин", (int)s.TotalMinutes);
        return s.Minutes == 0 ? L.F("{0} ч", (int)s.TotalHours) : L.F("{0} ч {1} мин", (int)s.TotalHours, s.Minutes);
    }

    /// <summary>Ошибка простыми словами. Незнакомый текст остаётся как есть (части пишут его по-русски).</summary>
    public static string Plain(string error)
    {
        if (string.IsNullOrWhiteSpace(error)) return L.T("Причина не записана.");
        string e = error.Trim(), low = e.ToLowerInvariant();
        string plain =
            low.Contains("unauthorizedaccess") || low.Contains("access is denied") || low.Contains("отказано в доступе")
                ? L.T("Windows не дала доступ. Обычно помогает запуск программы с правами администратора.")
            : low.Contains("used by another process") || low.Contains("используется другим процессом")
                ? L.T("Файл занят другой программой.")
            : low.Contains("timeout") || low.Contains("timed out") || low.Contains("время ожидания")
                ? L.T("Не дождалась ответа.")
            : low.Contains("filenotfound") || low.Contains("directorynotfound") || low.Contains("could not find")
                ? L.T("Не найден нужный файл.")
            : low.Contains("managementexception") || low.Contains("comexception") || low.Contains("rpc server")
                ? L.T("Служба Windows не ответила на запрос.")
            : low.Contains("outofmemory")
                ? L.T("Не хватило памяти.")
            : low.Contains("not enough space") || low.Contains("disk full") || low.Contains("недостаточно места")
                ? L.T("На диске нет места.")
            : null;
        if (plain != null) return plain;
        if (e.Length > 180) e = e[..180].TrimEnd() + "…";
        return e.EndsWith('.') || e.EndsWith('…') ? e : e + ".";
    }

    // ================================================================ отчёт

    /// <summary>«Windows 11, сборка 26300.7171 (25H2)».</summary>
    public static string WindowsBuild()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string build = k?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString();
            string ubr = k?.GetValue("UBR") is int u ? "." + u : "";
            string display = k?.GetValue("DisplayVersion") as string;
            int.TryParse(build, out int b);
            return L.F("Windows {0}, сборка {1}", b >= 22000 ? "11" : "10", build + ubr) + (string.IsNullOrEmpty(display) ? "" : $" ({display})");
        }
        catch { return L.F("Windows, сборка {0}", Environment.OSVersion.Version.Build); }
    }

    /// <summary>
    /// Отчёт для вопроса на странице программы: версия, сборка Windows, состояние частей и хвост журнала.
    /// Личное убрано: папка профиля заменена на %USERPROFILE%, имя пользователя и компьютера — на переменные.
    /// </summary>
    public static string Report(bool admin, int logLines = 100)
    {
        var sb = new StringBuilder();
        sb.AppendLine(L.T("Пульс ПК — отчёт о состоянии программы"));
        sb.AppendLine(L.F("Версия: {0}", Installer.Version));
        sb.AppendLine(WindowsBuild());
        sb.AppendLine(L.F("Права администратора: {0}", admin ? L.T("есть") : L.T("нет")));
        sb.AppendLine(L.F("Папка данных: {0}", SessionStore.Folder));
        sb.AppendLine(L.F("Составлен: {0:dd.MM.yyyy HH:mm:ss}", DateTime.Now));
        sb.AppendLine();
        sb.AppendLine(L.T("Части программы:"));
        var parts = Current.Snapshot();
        if (parts.Count == 0) sb.AppendLine(L.T("— ни одна часть ещё не отметилась"));
        foreach (var p in parts)
        {
            sb.Append("— ").Append(L.T(p.Name)).Append(": ").Append(p.Status).Append(". ").Append(p.Detail);
            if (!string.IsNullOrWhiteSpace(p.Raw)) sb.Append(" [").Append(L.T("исходный текст:")).Append(' ').Append(p.Raw.Trim()).Append(']');
            sb.AppendLine();
        }
        sb.AppendLine();
        var log = Log.Tail(logLines);
        sb.AppendLine(L.F("Журнал, последние строки ({0}):", log.Count));
        foreach (var line in log) sb.AppendLine(line);
        return Scrub(sb.ToString());
    }

    /// <summary>Убрать из текста имя пользователя, имя компьютера и пути к личным папкам.</summary>
    public static string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        try
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (profile.Length > 3) text = Regex.Replace(text, Regex.Escape(profile), "%USERPROFILE%", RegexOptions.IgnoreCase);
            // Профили других учётных записей (программа могла записать путь, запущенная от имени администратора).
            text = Regex.Replace(text, @"[A-Za-z]:\\Users\\[^\\\s""'<>|:]+", "%USERPROFILE%", RegexOptions.IgnoreCase);
            Word(ref text, Environment.UserName, "%USERNAME%");
            Word(ref text, Environment.MachineName, "%COMPUTERNAME%");
        }
        catch { }
        return text;
    }

    private static void Word(ref string text, string word, string with)
    {
        if (string.IsNullOrEmpty(word) || word.Length < 3) return;
        text = Regex.Replace(text, @"(?<![\p{L}\p{Nd}_])" + Regex.Escape(word) + @"(?![\p{L}\p{Nd}_])", with, RegexOptions.IgnoreCase);
    }
}
