using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using PulsePC.Panels;

namespace PulsePC.Services;

/// <summary>Состояние памяти: из чего складывается «доступно». Всё в ГБ; NaN — не прочиталось.</summary>
public sealed class MemState
{
    public double TotalGb = double.NaN;
    /// <summary>Доступно программам: кэш ожидания + пустая память (так Windows считает «доступно»).</summary>
    public double AvailGb = double.NaN;
    /// <summary>Пустая память: свободные и обнулённые страницы.</summary>
    public double FreeGb = double.NaN;
    /// <summary>Кэш ожидания (standby): данные про запас, Windows отдаёт их по первому требованию.</summary>
    public double StandbyGb = double.NaN;
    /// <summary>Изменённые страницы: ждут записи на диск, пока не доступны.</summary>
    public double ModifiedGb = double.NaN;
    /// <summary>Занято программами и системой (всё, что не доступно).</summary>
    public double UsedGb => TotalGb - AvailGb;
    public bool HasLists => !double.IsNaN(FreeGb) && !double.IsNaN(StandbyGb);
    public bool Ok => !double.IsNaN(TotalGb) && TotalGb > 0;
}

/// <summary>Итог «Освободить память».</summary>
public sealed class BoostResult
{
    public bool Ok;
    public string Error = "";
    public bool Auto;
    public string Game = "";
    public DateTime At;
    public double FreeBefore = double.NaN, FreeAfter = double.NaN, AvailBefore = double.NaN, AvailAfter = double.NaN;
    public double StandbyBefore = double.NaN, StandbyAfter = double.NaN;
    public bool Purged;
    public int Trimmed;
    public long TrimmedBytes;
}

/// <summary>Процесс для решения, сжимать ли его рабочий набор.</summary>
public sealed record MemProc(int Pid, string Name, int SessionId, long WorkingSet);

/// <summary>
/// «Память в игре»: что занимает память, очистка кэша ожидания (NtSetSystemInformation → MemoryPurgeStandbyList)
/// и, по отдельной галочке, сжатие рабочих наборов фоновых программ (EmptyWorkingSet). Ничего не закрывает и не завершает.
/// Авторежим: в начале игры (GameHooks.Started), если доступно меньше заданного. Только с правами администратора.
/// </summary>
public static class MemoryBoost
{
    private const string Nb = " ";
    public const string KAuto = "mem.auto", KAutoGb = "mem.autoGb", KTrim = "mem.trim", KExclude = "mem.exclude",
        KLast = "mem.last", KLastAt = "mem.lastAt", KNotified = "mem.notified", KStartGb = "mem.startGb", KStartGame = "mem.startGame", KStartAt = "mem.startAt";
    public static readonly int[] Thresholds = { 2, 4, 6, 8 };
    public const int DefaultThreshold = 4;
    /// <summary>Меньше этого программы рабочий набор не сжимается: выигрыша нет, а программа зря подгружается заново.</summary>
    public const long MinTrimBytes = 64L << 20;

    private static PanelHost host;
    private static bool inited;
    private static int running;
    private static DateTime lastAuto = DateTime.MinValue;

    /// <summary>Сделана очистка (кнопкой или авторежимом). Вызывается из фонового потока.</summary>
    public static event Action<BoostResult> Done;

    // Системное и то, что нельзя трогать никогда: ядро, оболочка, звук, сама программа, а также «Безопасность Windows»
    // и античиты — их список общий с кнопкой «Перед игрой» (ProtectedProcesses в Tweaks.cs).
    private static readonly HashSet<string> Never = new(ProtectedProcesses.Security, StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Memory Compression", "Idle", "Secure System", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "LsaIso",
        "svchost", "dwm", "explorer", "fontdrvhost", "audiodg", "ctfmon",
        "sihost", "taskhostw", "TextInputHost", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "RuntimeBroker", "dllhost",
        "conhost", "spoolsv", "WmiPrvSE", "vmmem", "vmmemWSL", "vmcompute", "NVDisplay.Container", "nvcontainer", "atiesrxx", "atieclxx",
        "igfxEM", "PulsePC", "PresentMon",
    };

    /// <summary>По умолчанию без сжатия: голос и запись идут вместе с игрой, им нельзя запинаться.</summary>
    public const string DefaultExclude = "Discord, obs64, ts3client_win64, TeamSpeak, Mumble, Medal";

    // ================================================================== запуск
    public static void Init(PanelHost h)
    {
        lock (Never)
        {
            host = h;
            if (inited) return;
            inited = true;
        }
        GameHooks.Started += OnGameStarted;
    }

    private static void OnGameStarted(string game, int pid)
    {
        var h = host;
        // Игра могла закончиться, пока очередь событий GameHooks дошла сюда: тогда ничего не делаем.
        if (h == null || !GameHooks.IsCurrent(pid)) return;
        var st = Read();
        if (st.Ok)
        {
            // Сколько было доступно в момент запуска: по этому числу удобно выбрать порог авторежима.
            h.Settings.Set(KStartGb, st.AvailGb.ToString("0.00", CultureInfo.InvariantCulture));
            h.Settings.Set(KStartGame, game);
            h.Settings.Set(KStartAt, DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
            h.Settings.Save();
        }
        if (!ShouldAuto(h.Settings.GetBool(KAuto), h.Admin, st.AvailGb, Threshold(h.Settings), lastAuto, DateTime.Now)) return;
        lastAuto = DateTime.Now;
        var r = Boost(h.Settings.GetBool(KTrim), pid, game, true);
        Remember(h.Settings, r);
        if (r.Ok && h.Settings.Get(KNotified) != DateTime.Today.ToString("yyyy-MM-dd"))
        {
            h.Settings.Set(KNotified, DateTime.Today.ToString("yyyy-MM-dd"));
            h.Settings.Save();
            h.Ui(() => h.Notify(L.T("Память освобождена перед игрой"), Describe(r), false, () => h.Open("sys:Память в игре")));
        }
    }

    public static int Threshold(AppSettings s) =>
        int.TryParse(s.Get(KAutoGb), out int v) && Thresholds.Contains(v) ? v : DefaultThreshold;

    /// <summary>Запускать ли авторежим: включён, есть права, памяти меньше порога и с прошлого раза прошло не меньше 10 минут.</summary>
    public static bool ShouldAuto(bool enabled, bool admin, double availGb, double thresholdGb, DateTime last, DateTime now) =>
        enabled && admin && !double.IsNaN(availGb) && availGb < thresholdGb && (now - last).TotalMinutes >= 10;

    public static void Remember(AppSettings s, BoostResult r)
    {
        s.Set(KLast, Describe(r));
        s.Set(KLastAt, r.At.ToString("o", CultureInfo.InvariantCulture));
        s.Save();
    }

    // ================================================================== состояние
    private static readonly object CounterGate = new();
    private static PerformanceCounter cFreeZero, cModified, cStandbyCore, cStandbyNormal, cStandbyReserve;
    private static bool countersTried;

    /// <summary>Прочитать память. Только чтение; первый вызов — около полсекунды (счётчики Windows).</summary>
    public static MemState Read()
    {
        var st = new MemState();
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref m))
        {
            st.TotalGb = m.ullTotalPhys / Fmt.Gib;
            st.AvailGb = m.ullAvailPhys / Fmt.Gib;
        }
        lock (CounterGate)
        {
            try
            {
                if (!countersTried)
                {
                    countersTried = true;
                    cFreeZero = new PerformanceCounter("Memory", "Free & Zero Page List Bytes");
                    cModified = new PerformanceCounter("Memory", "Modified Page List Bytes");
                    cStandbyCore = new PerformanceCounter("Memory", "Standby Cache Core Bytes");
                    cStandbyNormal = new PerformanceCounter("Memory", "Standby Cache Normal Priority Bytes");
                    cStandbyReserve = new PerformanceCounter("Memory", "Standby Cache Reserve Bytes");
                }
                if (cFreeZero != null)
                {
                    st.FreeGb = cFreeZero.NextValue() / Fmt.Gib;
                    st.ModifiedGb = cModified.NextValue() / Fmt.Gib;
                    st.StandbyGb = (cStandbyCore.NextValue() + cStandbyNormal.NextValue() + cStandbyReserve.NextValue()) / Fmt.Gib;
                }
            }
            catch { cFreeZero = null; }
        }
        return st;
    }

    /// <summary>Программы по занятой памяти (все процессы одной программы вместе), крупные первыми.</summary>
    public static List<(string Name, int Count, long Bytes)> Top(int count)
    {
        var list = new List<(string, int, long)>();
        try
        {
            var all = Process.GetProcesses();
            foreach (var g in all.GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase))
            {
                if (g.Key == "Idle") continue;
                long sum = 0;
                foreach (var p in g) { try { sum += p.WorkingSet64; } catch { } }
                list.Add((g.Key, g.Count(), sum));
            }
            foreach (var p in all) p.Dispose();
        }
        catch { }
        return list.OrderByDescending(x => x.Item3).Take(count).ToList();
    }

    /// <summary>Системная ли это программа (её рабочий набор не сжимается никогда).</summary>
    public static bool IsSystem(string name) => Never.Contains(name ?? "");

    // ================================================================== выбор программ для сжатия
    public static HashSet<string> ParseExclude(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (text ?? "").Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string n = part.Trim().Trim('"', '«', '»');
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
            if (n.Length > 0) set.Add(n);
        }
        return set;
    }

    /// <summary>
    /// Кого сжимать. Никогда: игру (её процесс и все процессы с тем же именем), саму программу, процессы служб (сеанс 0),
    /// системные, исключения пользователя и мелкие (меньше 64 МБ). Чистая функция — проверяется на подставных процессах.
    /// </summary>
    public static List<MemProc> Pick(IEnumerable<MemProc> procs, int gamePid, int selfPid, ISet<string> exclude)
    {
        var all = procs.ToList();
        string gameName = gamePid > 0 ? all.FirstOrDefault(p => p.Pid == gamePid)?.Name : null;
        return all.Where(p =>
                p.Pid != gamePid && p.Pid != selfPid && p.Pid > 4
                && p.SessionId != 0
                && !(gameName != null && p.Name.Equals(gameName, StringComparison.OrdinalIgnoreCase))
                && !Never.Contains(p.Name)
                && !(exclude?.Contains(p.Name) ?? false)
                && p.WorkingSet >= MinTrimBytes)
            .OrderByDescending(p => p.WorkingSet)
            .ToList();
    }

    // ================================================================== действие
    /// <summary>
    /// Освободить память: сжать фоновые программы (если trim) и очистить кэш ожидания. Нужны права администратора.
    /// Ничего не закрывает: сжатые программы при обращении подгружают своё обратно.
    /// </summary>
    public static BoostResult Boost(bool trim, int gamePid, string game, bool auto)
    {
        var r = new BoostResult { Auto = auto, Game = game ?? "", At = DateTime.Now };
        if (Interlocked.Exchange(ref running, 1) == 1) { r.Error = L.T("Очистка уже идёт."); return r; }
        try
        {
            if (!Shell.IsAdmin()) { r.Error = L.T("Нужны права администратора."); return r; }
            var before = Read();
            r.FreeBefore = before.FreeGb; r.AvailBefore = before.AvailGb; r.StandbyBefore = before.StandbyGb;

            if (trim)
            {
                var exclude = ParseExclude(host?.Settings.Get(KExclude, DefaultExclude) ?? DefaultExclude);
                foreach (var p in Pick(Snapshot(), gamePid, Environment.ProcessId, exclude))
                {
                    IntPtr hp = OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_LIMITED_INFORMATION, false, p.Pid);
                    if (hp == IntPtr.Zero) continue;
                    try { if (K32EmptyWorkingSet(hp)) { r.Trimmed++; r.TrimmedBytes += p.WorkingSet; } }
                    finally { CloseHandle(hp); }
                }
                Thread.Sleep(400);
            }

            if (!EnablePrivilege("SeProfileSingleProcessPrivilege", out string why)) { r.Error = why; }
            else
            {
                int command = MemoryPurgeStandbyList;
                int status = NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
                if (status == 0) r.Purged = true;
                else r.Error = status == unchecked((int)0xC0000061) ? L.T("Windows не дала права очищать кэш (нет привилегии).") : L.F("Windows отказала: код 0x{0:X8}.", status);
            }
            Thread.Sleep(300);
            var after = Read();
            r.FreeAfter = after.FreeGb; r.AvailAfter = after.AvailGb; r.StandbyAfter = after.StandbyGb;
            r.Ok = r.Purged || r.Trimmed > 0;
        }
        catch (Exception ex) { r.Error = ex.Message; }
        finally { Interlocked.Exchange(ref running, 0); }
        try { Done?.Invoke(r); } catch { }
        return r;
    }

    public static bool Running => Volatile.Read(ref running) == 1;

    private static List<MemProc> Snapshot()
    {
        var list = new List<MemProc>();
        foreach (var p in Process.GetProcesses())
        {
            try { list.Add(new MemProc(p.Id, p.ProcessName, p.SessionId, p.WorkingSet64)); } catch { }
            finally { p.Dispose(); }
        }
        return list;
    }

    /// <summary>«Освобождено 2,4 ГБ: пустой памяти было 1,1 → стало 3,5 ГБ…» — одной-двумя фразами, только измеренное.</summary>
    public static string Describe(BoostResult r)
    {
        if (r == null) return "";
        if (!r.Ok) return L.F("Память не освобождена: {0}.", r.Error.Length > 0 ? r.Error.TrimEnd('.') : L.T("неизвестная ошибка"));
        var sb = new StringBuilder();
        double gained = r.FreeAfter - r.FreeBefore;
        if (!double.IsNaN(gained))
        {
            sb.Append(gained >= 0.05
                ? L.F("Освобождено {0}: пустой памяти было {1} → стало {2}.", Gb(gained), Num(r.FreeBefore), Gb(r.FreeAfter))
                : L.F("Освобождать почти нечего: пустой памяти было {0} → стало {1}.", Num(r.FreeBefore), Gb(r.FreeAfter)));
        }
        else sb.Append(L.T("Кэш ожидания очищен."));
        if (r.Trimmed > 0)
            sb.Append(!double.IsNaN(r.AvailBefore) && !double.IsNaN(r.AvailAfter)
                ? L.F(" Фоновых программ сжато: {0}, доступно было {1} → стало {2}.", r.Trimmed, Num(r.AvailBefore), Gb(r.AvailAfter))
                : L.F(" Фоновых программ сжато: {0}.", r.Trimmed));
        if (!r.Purged && r.Error.Length > 0) sb.Append(L.F(" Кэш не очищен: {0}", r.Error));
        if (r.Auto && r.Game.Length > 0) sb.Insert(0, L.F("Перед запуском {0}. ", r.Game));
        return sb.ToString();
    }

    public static string Gb(double v) => double.IsNaN(v) ? "—" : v.ToString("0.0", L.Culture) + Nb + Fmt.GbUnit;
    public static string Num(double v) => double.IsNaN(v) ? "—" : v.ToString("0.0", L.Culture);

    // ================================================================== статистика сессий
    public const double ShortGb = 1.0;

    /// <summary>Сессии с замером памяти (MinRamGb &gt; 0) и сколько из них с запасом меньше limit.</summary>
    public static (int Measured, int Short, double Worst, double AvgMin) Shortage(IEnumerable<GameSession> sessions, double limit = ShortGb)
    {
        var m = (sessions ?? Enumerable.Empty<GameSession>()).Where(s => s.MinRamGb > 0).ToList();
        if (m.Count == 0) return (0, 0, double.NaN, double.NaN);
        return (m.Count, m.Count(s => s.MinRamGb < limit), m.Min(s => s.MinRamGb), m.Average(s => s.MinRamGb));
    }

    // ================================================================== самопроверка
    /// <summary>Логика — на подставных данных; в системе только чтение (память и список процессов). Ничего не очищает.</summary>
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        string fail = null;
        void Check(string what, bool ok) { sb.AppendLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) fail ??= what; }

        var procs = new List<MemProc>
        {
            new(10, "Deadlock", 1, 6L << 30),           // игра
            new(11, "Deadlock", 1, 300L << 20),         // второй процесс игры
            new(20, "browser", 1, 900L << 20),
            new(21, "browser", 1, 400L << 20),
            new(22, "browser", 1, 20L << 20),           // мелкий
            new(30, "Discord", 1, 500L << 20),          // исключение
            new(40, "svchost", 0, 200L << 20),          // служба
            new(41, "SomeService", 0, 300L << 20),      // сеанс 0
            new(50, "dwm", 1, 150L << 20),              // системное
            new(60, "PulsePC", 1, 250L << 20),          // сама программа
            new(70, "claude", 1, 1200L << 20),
            new(80, "vgc", 1, 100L << 20),              // античит
            new(90, "smartscreen", 1, 500L << 20),      // «Безопасность Windows»
            new(91, "SecHealthUI", 1, 300L << 20),
            new(92, "MsMpEng", 1, 400L << 20),
            new(93, "SgrmBroker", 1, 200L << 20),
        };
        var ex = ParseExclude(" Discord.exe ; «obs64», ,");
        Check("исключения: имена без .exe и кавычек, пустые отброшены", ex.Count == 2 && ex.Contains("discord") && ex.Contains("OBS64"));
        var pick = Pick(procs, 10, 60, ex);
        Check("сжимаются только фоновые: claude и два процесса браузера, крупные первыми",
            pick.Select(p => p.Pid).SequenceEqual(new[] { 70, 20, 21 }));
        Check("игра (оба процесса), программа, службы, системные, античит и исключения не трогаются",
            !pick.Any(p => p.Name is "Deadlock" or "PulsePC" or "svchost" or "dwm" or "vgc" or "Discord" || p.SessionId == 0));
        Check("«Безопасность Windows» (SmartScreen, центр безопасности, Defender) не сжимается",
            !Pick(procs, 0, 60, null).Any(p => p.Pid >= 90 && p.Pid <= 93));
        Check("без игры (кнопка в разделе) — то же, кроме игры", Pick(procs, 0, 60, ex).Any(p => p.Name == "Deadlock"));
        Check("исключения по умолчанию — голос и запись", ParseExclude(DefaultExclude).Contains("discord") && ParseExclude(DefaultExclude).Contains("obs64"));

        var now = new DateTime(2026, 11, 19, 20, 0, 0);
        Check("авторежим: включён, админ, 1,5 ГБ при пороге 4 — запускать", ShouldAuto(true, true, 1.5, 4, DateTime.MinValue, now));
        Check("авторежим: памяти хватает — не запускать", !ShouldAuto(true, true, 5, 4, DateTime.MinValue, now));
        Check("авторежим: выключен или нет прав — не запускать", !ShouldAuto(false, true, 1, 4, DateTime.MinValue, now) && !ShouldAuto(true, false, 1, 4, DateTime.MinValue, now));
        Check("авторежим: не чаще раза в 10 минут", !ShouldAuto(true, true, 1, 4, now.AddMinutes(-3), now) && ShouldAuto(true, true, 1, 4, now.AddMinutes(-11), now));
        Check("авторежим: память не прочиталась — не запускать", !ShouldAuto(true, true, double.NaN, 4, DateTime.MinValue, now));
        var s = new AppSettings();
        bool byDefault = Threshold(s) == 4;
        s.Set(KAutoGb, "5");
        Check("порог: по умолчанию 4 ГБ, чужое значение — тоже 4", byDefault && Threshold(s) == 4);
        s.Set(KAutoGb, "6");
        Check("порог: 6 ГБ читается", Threshold(s) == 6);

        string d = Describe(new BoostResult { Ok = true, Purged = true, FreeBefore = 1.1, FreeAfter = 3.5, AvailBefore = 4.0, AvailAfter = 4.1 });
        // Тексты итога сверяются по-русски; в английском интерфейсе проверяется только, что итог есть.
        if (L.En) Check("итог по-английски не пустой и без кириллицы: " + d, d.Length > 10 && !System.Text.RegularExpressions.Regex.IsMatch(d, "[А-Яа-яЁё]"));
        else {
        Check("итог: «Освобождено 2,4 ГБ: пустой памяти было 1,1 → стало 3,5 ГБ.»", d == $"Освобождено 2,4{Nb}ГБ: пустой памяти было 1,1 → стало 3,5{Nb}ГБ.");
        string d2 = Describe(new BoostResult { Ok = true, Purged = true, Trimmed = 3, FreeBefore = 0.4, FreeAfter = 2.0, AvailBefore = 1.2, AvailAfter = 2.9, Auto = true, Game = "PUBG" });
        Check("итог со сжатием и игрой: доступно было 1,2 → стало 2,9 — " + d2, d2.StartsWith("Перед запуском PUBG.", StringComparison.Ordinal) && d2.Contains("Фоновых программ сжато: 3,") &&d2.Contains($"доступно было 1,2 → стало 2,9{Nb}ГБ"));
        Check("итог при отказе — причина", Describe(new BoostResult { Error = "Нужны права администратора." }) == "Память не освобождена: Нужны права администратора.");
        Check("итог: ничего не освободилось — честно", Describe(new BoostResult { Ok = true, Purged = true, FreeBefore = 2.0, FreeAfter = 2.02 }).StartsWith("Освобождать почти нечего"));
        }

        var sess = new[]
        {
            new GameSession { MinRamGb = 0.8 }, new GameSession { MinRamGb = 1.6 }, new GameSession { MinRamGb = 0.95 },
            new GameSession { MinRamGb = 0 }, new GameSession { MinRamGb = 3.0 },
        };
        var sh = Shortage(sess);
        Check("статистика: замер в 4 сессиях, меньше 1 ГБ — в двух, наименьший 0,8", sh.Measured == 4 && sh.Short == 2 && sh.Worst == 0.8);
        Check("статистика: сессий нет — пусто, без ошибки", Shortage(Array.Empty<GameSession>()).Measured == 0);

        sb.AppendLine();
        sb.AppendLine("Что видно на этом ПК (только чтение):");
        var st = Read();
        if (st.Ok)
        {
            sb.AppendLine($"  всего {Gb(st.TotalGb)}, доступно {Gb(st.AvailGb)}, занято {Gb(st.UsedGb)}");
            sb.AppendLine(st.HasLists
                ? $"  кэш ожидания {Gb(st.StandbyGb)}, пусто {Gb(st.FreeGb)}, изменённые {Gb(st.ModifiedGb)}; кэш + пусто = {Gb(st.StandbyGb + st.FreeGb)} (Windows: доступно {Gb(st.AvailGb)})"
                : "  разбивка на кэш и пустую память не прочиталась (счётчики Windows недоступны)");
        }
        else { sb.AppendLine("  память не прочиталась"); fail ??= "память не прочиталась"; }
        var top = Top(6);
        sb.AppendLine("  больше всего занимают: " + string.Join(", ", top.Select(t => $"{t.Name}{(t.Count > 1 ? " ×" + t.Count : "")} {Fmt.Size(t.Bytes)}")));
        var live = Pick(Snapshot(), 0, Environment.ProcessId, ParseExclude(DefaultExclude));
        sb.AppendLine($"  кнопка со сжатием тронула бы {live.Count} программ(ы): " + string.Join(", ", live.Take(8).Select(p => $"{p.Name} {Fmt.Size(p.WorkingSet)}")));
        sb.AppendLine("  права администратора: " + (Shell.IsAdmin() ? "есть" : "нет — очистка из раздела недоступна"));
        var real = SessionStore.Load();
        var rs = Shortage(real);
        sb.AppendLine(rs.Measured == 0 ? "  сессий с замером памяти нет" :
            $"  сессии ({(Environment.GetEnvironmentVariable("PULSEPC_DATA") is { Length: > 0 } ? "тестовая папка" : "настоящие")}): с замером {rs.Measured}, меньше 1 ГБ — {rs.Short}, наименьший запас {Gb(rs.Worst)}");
        return (fail == null ? "OK" : "FAIL: " + fail) + "\n" + sb;
    }

    // ================================================================== Windows
    private const int SystemMemoryListInformation = 80, MemoryPurgeStandbyList = 4;
    private const uint PROCESS_SET_QUOTA = 0x0100, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 2;

    private static bool EnablePrivilege(string name, out string why)
    {
        why = "";
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token)) { why = L.T("Не открылся маркер доступа программы."); return false; }
        try
        {
            if (!LookupPrivilegeValue(null, name, out long luid)) { why = L.T("Windows не знает нужной привилегии."); return false; }
            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) { why = L.T("Привилегия не включилась."); return false; }
            if (Marshal.GetLastWin32Error() == 1300) { why = L.T("Нужны права администратора."); return false; }   // ERROR_NOT_ALL_ASSIGNED
            return true;
        }
        finally { CloseHandle(token); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public long Luid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
    [DllImport("ntdll.dll")] private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool K32EmptyWorkingSet(IntPtr process);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, uint len, IntPtr prev, IntPtr retLen);
}
