using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using PulsePC.Panels;

namespace PulsePC.Services;

/// <summary>Служба обновлений в списке раздела.</summary>
public sealed class UpdateService
{
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string Hint { get; set; } = "";
    /// <summary>running, stopped, starting, stopping, missing, unknown.</summary>
    public string State { get; set; } = "unknown";
    /// <summary>Остановлена программой на время игры.</summary>
    public bool Held { get; set; }
    public string Value => State switch
    {
        "running" => "работает",
        "stopped" => Held ? "остановлена на время игры" : "не запущена",
        "starting" => "запускается",
        "stopping" => "останавливается",
        "missing" => "нет на компьютере",
        _ => "не удалось узнать",
    };
}

/// <summary>Снимок состояния для раздела «Обновления Windows».</summary>
public sealed class UpdateState
{
    public bool Guard, NoReboot, InGame;
    public List<UpdateService> Services = new();
    /// <summary>Сколько файлов Windows качает сейчас; −1 — узнать не удалось.</summary>
    public int Downloading = -1;
    public long DownloadLeftBytes;
    public bool RebootPending;
    /// <summary>Период активности Windows (часы 0–23); null — не задан.</summary>
    public int? HoursStart, HoursEnd;
    /// <summary>Период сейчас сдвинут программой; HoursWas — каким он был.</summary>
    public bool HoursShifted;
    public string HoursWas = "";
    /// <summary>Период активности задан политикой организации: программа его не трогает.</summary>
    public bool HoursPolicy;
    public DateTime? Last;
    public string LastText = "";
}

/// <summary>
/// На время игры останавливает службы обновлений Windows и сдвигает период активности, чтобы Windows не перезагрузила ПК.
/// После игры всё возвращается. Тип запуска служб не меняется, обновления насовсем не отключаются.
/// Что было остановлено, хранится в настройках, прежний период активности — в файле: возврат срабатывает и после сбоя программы.
/// </summary>
public static class UpdateGuard
{
    private const string KeyGuard = "updates.guard";         // останавливать службы на время игры
    private const string KeyNoReboot = "updates.noreboot";   // сдвигать период активности на время игры
    private const string KeyStopped = "updates.stopped";     // службы, остановленные программой (через запятую); есть только пока идёт игра
    private const string KeyLast = "updates.last";           // когда защита срабатывала последний раз
    private const string KeyLastText = "updates.lastText";   // что именно она сделала

    private const string HoursBackupName = "update-hours-backup.txt";

    // Только эти три службы. Службы «Безопасности Windows» программа не трогает и не опрашивает.
    private static readonly (string Name, string Title, string Hint)[] Targets =
    {
        ("wuauserv", "Центр обновления Windows", "Ищет, скачивает и ставит обновления."),
        ("DoSvc", "Оптимизация доставки", "Качает обновления Windows и приложений из Магазина, может раздавать их другим компьютерам."),
        ("BITS", "Фоновая передача файлов", "Качает файлы в фоне, когда сеть свободна. Ею пользуются обновления Windows и некоторых программ."),
    };

    private static readonly object Gate = new();
    private static PanelHost host;
    private static bool hooked;
    private static System.Threading.Timer watch;
    private static readonly Dictionary<string, int> restops = new();

    /// <summary>Состояние изменилось (началась или закончилась игра). Может прийти из фонового потока.</summary>
    public static event Action Changed;

    private static bool migrated;

    /// <summary>
    /// Копия прежнего периода активности — в защищённой папке: по ней программа с правами администратора пишет в HKLM.
    /// Старая копия из папки пользователя переносится один раз; значения при чтении всё равно проверяются (часы 0–23).
    /// </summary>
    private static string HoursBackup
    {
        get
        {
            lock (Gate) { if (!migrated) { SecureStore.Migrate(HoursBackupName); migrated = true; } }
            return SecureStore.PathOf(HoursBackupName);
        }
    }
    private static RegistryKey Machine => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    public static void Init(PanelHost h)
    {
        host = h;
        Hook();
        // Программа закрылась посреди игры: службы остались остановленными или период активности — сдвинутым. Вернуть.
        if (h.Settings.Get(KeyStopped) != "" || File.Exists(HoursBackup))
            Task.Run(() => { if (WaitNoGame()) OnEnded(); });
    }

    private static void Hook()
    {
        lock (Gate)
        {
            if (hooked) return;
            hooked = true;
            GameHooks.Started += OnStarted;
            GameHooks.Ended += OnEnded;
        }
    }

    /// <summary>Дождаться, пока станет ясно, что игра не идёт. false — игру ведут события GameHooks: всё вернёт обработчик конца игры.</summary>
    private static bool WaitNoGame()
    {
        while (true)
        {
            if (GameHooks.InGame) return false;
            DetectedGame game = null;
            try { game = GameDetector.Find(); } catch { }
            if (game == null) return !GameHooks.InGame;
            Thread.Sleep(10000);
        }
    }

    /// <summary>Переключатель «Останавливать обновления на время игры». Действует со следующего запуска игры.</summary>
    public static void SetGuard(PanelHost h, bool on) => SetFlag(h, KeyGuard, on);

    /// <summary>Переключатель «Не перезагружать, пока я играю». Действует со следующего запуска игры.</summary>
    public static void SetNoReboot(PanelHost h, bool on) => SetFlag(h, KeyNoReboot, on);

    private static void SetFlag(PanelHost h, string key, bool on)
    {
        host ??= h;
        Hook();
        h.Settings.SetBool(key, on);
        Save(h.Settings);
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ события игры

    private static void OnStarted(string game, int pid)
    {
        if (host == null) return;
        var s = host.Settings;
        if (!s.GetBool(KeyGuard) && !s.GetBool(KeyNoReboot)) return;
        // Служба может останавливаться до 20 секунд. Обработчики GameHooks идут по очереди, поэтому работаем в своей задаче:
        // остальные (схема питания и прочее) не ждут.
        if (!GameHooks.IsCurrent(pid)) return;
        Task.Run(() => PauseForGame(game, pid));
    }

    private static void PauseForGame(string game, int pid)
    {
        var s = host.Settings;
        bool guard = s.GetBool(KeyGuard), noReboot = s.GetBool(KeyNoReboot);
        lock (Gate)
        {
            // Игра успела закрыться (или уже идёт другая), пока задача ждала очереди: останавливать нечего —
            // обработчик конца игры ждёт этот же замок и вернул бы всё до нас.
            if (!GameHooks.IsCurrent(pid)) return;
            if (!Shell.IsAdmin())
            {
                Remember(s, $"Игра «{game}» запущена, но программа работает без прав администратора: обновления не приостановлены.");
            }
            else
            {
                var done = new List<string>();
                if (guard) done.Add(StopForGame(s));
                if (noReboot) done.Add(ShiftHoursForGame(6));
                Remember(s, $"Игра «{game}». " + string.Join(" ", done.Where(x => x != "")));
                restops.Clear();
                watch?.Dispose();
                watch = new System.Threading.Timer(_ => Watch(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Остановить запущенные службы обновлений. Список запоминается до остановки: сбой посреди работы ничего не потеряет.</summary>
    private static string StopForGame(AppSettings s)
    {
        var held = Split(s.Get(KeyStopped));
        var running = Targets.Select(t => t.Name).Where(n => !held.Contains(n) && Query(n) == "running").ToList();
        if (running.Count == 0) return held.Count > 0 ? "Службы обновлений уже были остановлены программой." : "Службы обновлений и так не работали.";

        s.Set(KeyStopped, string.Join(",", held.Concat(running)));
        Save(s);

        var stopped = new List<string>();
        var failed = new List<string>();
        foreach (string name in running)
        {
            var (ok, error) = StopService(name);
            if (ok) stopped.Add(name); else failed.Add($"«{TitleOf(name)}» — {error}");
        }
        var keep = held.Concat(stopped).ToList();
        s.Set(KeyStopped, keep.Count > 0 ? string.Join(",", keep) : null);
        Save(s);

        string text = stopped.Count > 0 ? "Остановлено на время игры: " + string.Join(", ", stopped.Select(n => "«" + TitleOf(n) + "»")) + "." : "";
        // Служба не остановилась — не настаиваем: она остаётся работать, это записано.
        if (failed.Count > 0) text += (text == "" ? "" : " ") + "Не удалось остановить: " + string.Join("; ", failed) + ".";
        return text;
    }

    private static string ShiftHoursForGame(double needHours)
    {
        try
        {
            using var hive = Machine;
            if (ActiveHours.ManagedByPolicy(hive)) return "Период активности задан политикой организации — программа его не трогает.";
            string set = ActiveHours.Shift(hive, ActiveHours.SettingsPath, HoursBackup, DateTime.Now, needHours);
            return set == null ? "Период активности уже покрывает время игры." : $"Период активности сдвинут: {set}.";
        }
        catch (Exception ex) { return "Период активности сдвинуть не удалось: " + ex.Message; }
    }

    /// <summary>
    /// Раз в две минуты, пока идёт игра: Windows могла сама запустить остановленную службу — останавливаем снова,
    /// но не больше трёх раз на службу. И продлеваем период активности, если игра затянулась.
    /// </summary>
    private static void Watch()
    {
        if (host == null || !GameHooks.InGame || !Monitor.TryEnter(Gate)) return;
        try
        {
            if (!GameHooks.InGame) return;
            var s = host.Settings;
            foreach (string name in Split(s.Get(KeyStopped)))
            {
                if (Query(name) != "running") continue;
                restops.TryGetValue(name, out int count);
                if (count >= 3) continue;
                restops[name] = count + 1;
                StopService(name);
            }
            // Игра затянулась, и до конца периода активности осталось меньше двух часов — продлеваем.
            if (s.GetBool(KeyNoReboot) || File.Exists(HoursBackup)) ShiftHoursForGame(2);
        }
        catch { }
        finally { Monitor.Exit(Gate); }
    }

    private static void OnEnded()
    {
        if (host == null) return;
        bool changed;
        lock (Gate) changed = ResumeAfterGame();
        if (changed) Changed?.Invoke();
    }

    /// <summary>Запустить остановленные службы и вернуть период активности. false — возвращать было нечего.</summary>
    private static bool ResumeAfterGame()
    {
        watch?.Dispose();
        watch = null;
        var s = host.Settings;
        var held = Split(s.Get(KeyStopped));
        bool hours = File.Exists(HoursBackup);
        if (held.Count == 0 && !hours) return false;

        if (!Shell.IsAdmin())
        {
            // Ключи не очищаем: вернём при следующем запуске с правами администратора.
            Append(s, "После игры вернуть не удалось: у программы нет прав администратора. Запустите её с правами администратора — и всё вернётся.");
            return true;
        }

        var notes = new List<string>();
        var retry = new List<string>();
        foreach (string name in held)
        {
            var (ok, again, error) = StartService(name);
            if (!ok) { notes.Add($"«{TitleOf(name)}» не запустилась — {error}"); if (again) retry.Add(name); }
        }
        s.Set(KeyStopped, retry.Count > 0 ? string.Join(",", retry) : null);

        if (hours)
        {
            try
            {
                using var hive = Machine;
                if (!ActiveHours.Restore(hive, ActiveHours.SettingsPath, HoursBackup))
                    notes.Add("период активности за время игры изменили — он оставлен как есть");
            }
            catch (Exception ex) { notes.Add("период активности вернуть не удалось — " + ex.Message); }
        }
        Append(s, notes.Count == 0 ? "После игры всё возвращено." : "После игры: " + string.Join("; ", notes) + ".");
        return true;
    }

    private static void Remember(AppSettings s, string text)
    {
        s.Set(KeyLast, DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
        s.Set(KeyLastText, text.Trim());
        Save(s);
    }

    private static void Append(AppSettings s, string text)
    {
        string was = s.Get(KeyLastText);
        int cut = was.IndexOf(" После игры", StringComparison.Ordinal);
        if (cut >= 0) was = was[..cut];
        s.Set(KeyLastText, (was + " " + text).Trim());
        Save(s);
    }

    // Запись настроек под тем же замком, что и AppSettings.Set: другой раздел не изменит словарь посреди сохранения.
    private static void Save(AppSettings s)
    {
        var values = s.Values;
        if (values == null) { s.Save(); return; }
        lock (values) s.Save();
    }

    private static List<string> Split(string list) =>
        (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => Targets.Any(t => t.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string TitleOf(string name) => Targets.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Title ?? name;

    // ------------------------------------------------------------------ чтение

    /// <summary>Состояние для раздела. Только чтение; занимает до пары секунд — вызывать из фонового потока.</summary>
    public static UpdateState Read(AppSettings s)
    {
        var st = new UpdateState { Guard = s.GetBool(KeyGuard), NoReboot = s.GetBool(KeyNoReboot), InGame = GameHooks.InGame };
        var held = Split(s.Get(KeyStopped));
        foreach (var t in Targets)
            st.Services.Add(new UpdateService { Name = t.Name, Title = t.Title, Hint = t.Hint, State = Query(t.Name), Held = held.Contains(t.Name, StringComparer.OrdinalIgnoreCase) });

        (st.Downloading, st.DownloadLeftBytes) = Downloads(st.Services);
        st.RebootPending = RebootPending();

        try
        {
            using var hive = Machine;
            (st.HoursStart, st.HoursEnd) = ActiveHours.Read(hive, ActiveHours.SettingsPath);
            st.HoursPolicy = ActiveHours.ManagedByPolicy(hive);
            var saved = ActiveHours.LoadBackup(HoursBackup);
            if (saved != null) { st.HoursShifted = true; st.HoursWas = ActiveHours.Format(saved.Value.Start, saved.Value.End); }
        }
        catch { }

        if (DateTime.TryParse(s.Get(KeyLast), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last)) st.Last = last;
        st.LastText = s.Get(KeyLastText);
        return st;
    }

    /// <summary>
    /// Качает ли Windows обновления сейчас: число файлов и сколько осталось. Спрашиваем «Оптимизацию доставки», и только
    /// когда она уже работает — запрос к остановленной службе запустил бы её. (−1, 0) — узнать не удалось.
    /// </summary>
    private static (int Count, long Left) Downloads(List<UpdateService> services)
    {
        string dosvc = services.FirstOrDefault(x => x.Name == "DoSvc")?.State ?? "unknown";
        if (dosvc is "stopped" or "missing") return (0, 0);
        if (dosvc != "running") return (-1, 0);
        const string script =
            "$ErrorActionPreference='Stop'; $d = @(Get-DeliveryOptimizationStatus | Where-Object { $_.Status -eq 'Downloading' }); " +
            "$left = 0; foreach ($x in $d) { $left += [double]$x.FileSize - [double]$x.TotalBytesDownloaded }; \"count=$($d.Count)|left=$([long]$left)\"";
        try
        {
            var (code, output) = Shell.PowerShell(script, 30000);
            if (code != 0) return (-1, 0);
            foreach (string line in (output ?? "").Split('\n'))
            {
                var p = line.Trim().Split('|');
                if (p.Length == 2 && p[0].StartsWith("count=") && p[1].StartsWith("left=")
                    && int.TryParse(p[0][6..], out int count) && long.TryParse(p[1][5..], out long left))
                    return (count, Math.Max(0, left));
            }
        }
        catch { }
        return (-1, 0);
    }

    /// <summary>Windows ждёт перезагрузки, чтобы доставить обновления (те же признаки, что в «Диагностике»).</summary>
    private static bool RebootPending()
    {
        try
        {
            using var a = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            using var b = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            return a != null || b != null;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ самопроверка

    /// <summary>Только чтение: состояние трёх служб, загрузка, ожидание перезагрузки, период активности, настройки защиты.</summary>
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        var s = host?.Settings ?? AppSettings.Load();
        var st = Read(s);
        bool read = st.Services.Count == Targets.Length && st.Services.All(x => x.State != "unknown");
        sb.AppendLine(read ? "OK" : "FAIL: не удалось узнать состояние служб: " + string.Join(", ", st.Services.Where(x => x.State == "unknown").Select(x => x.Name)));
        foreach (var x in st.Services)
            sb.AppendLine($"  {x.Name} ({x.Title}): {x.Value}");
        sb.AppendLine("Загрузка обновлений: " + (st.Downloading < 0 ? "узнать не удалось" : st.Downloading == 0 ? "не идёт" : $"идёт, файлов: {st.Downloading}, осталось {Fmt.Size(st.DownloadLeftBytes)}"));
        sb.AppendLine("Перезагрузка для установки обновлений: " + (st.RebootPending ? "Windows её ждёт" : "не требуется"));
        sb.AppendLine("Период активности Windows: " + (st.HoursStart == null || st.HoursEnd == null ? "не задан" : ActiveHours.Format(st.HoursStart, st.HoursEnd)));
        if (st.HoursStart != null && st.HoursEnd != null)
        {
            double left = ActiveHours.HoursLeft(st.HoursStart.Value, st.HoursEnd.Value, DateTime.Now);
            var (ns, ne) = ActiveHours.Window(DateTime.Now);
            sb.AppendLine($"  сейчас {DateTime.Now:HH:mm}: " + (left > 0 ? $"внутри периода, до конца {left:0.0} ч" : "вне периода")
                          + (left >= 6 ? "; при запуске игры период не изменился бы" : $"; при запуске игры период стал бы {ActiveHours.Format(ns, ne)}"));
        }
        sb.AppendLine("Период задан политикой организации: " + (st.HoursPolicy ? "да — программа его не трогает" : "нет"));
        sb.AppendLine("Копия прежнего периода активности: " + (st.HoursShifted ? "есть, был " + st.HoursWas : "нет"));
        sb.AppendLine($"{KeyGuard}: {(st.Guard ? "включено" : "выключено")}");
        sb.AppendLine($"{KeyNoReboot}: {(st.NoReboot ? "включено" : "выключено")}");
        sb.AppendLine($"{KeyStopped}: {(s.Get(KeyStopped) == "" ? "пусто" : s.Get(KeyStopped))}");
        sb.AppendLine("Последнее срабатывание: " + (st.Last == null ? "не было" : st.Last.Value.ToString("dd.MM.yyyy HH:mm") + " — " + st.LastText));
        sb.AppendLine($"Игра идёт (GameHooks.InGame): {(GameHooks.InGame ? "да" : "нет")}");
        sb.AppendLine($"Права администратора: {(Shell.IsAdmin() ? "есть" : "нет — службы и период активности менять нельзя")}");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ службы Windows

    private const uint ScConnect = 0x0001, SvcQuery = 0x0004, SvcStart = 0x0010, SvcStop = 0x0020;
    private const uint StateStopped = 1, StateStartPending = 2, StateStopPending = 3, StateRunning = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint Type, State, Controls, ExitCode, ServiceExitCode, CheckPoint, WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr OpenSCManager(string machine, string database, uint access);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool StartService(IntPtr service, uint argc, IntPtr argv);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumDependentServices(IntPtr service, uint state, IntPtr buffer, uint size, out uint needed, out uint count);

    private const uint SvcEnumDependents = 0x0008, ServiceActive = 0x1;

    /// <summary>
    /// Работающие службы, которые зависят от name (их пришлось бы остановить вместе с ней), в порядке, в каком их
    /// останавливать: сначала зависящие от зависящих. Только чтение; пустой список — таких нет или прочитать не удалось.
    /// </summary>
    internal static List<string> ActiveDependents(string name)
    {
        var list = new List<string>();
        IntPtr manager = IntPtr.Zero, service = IntPtr.Zero, buffer = IntPtr.Zero;
        try
        {
            manager = OpenSCManager(null, null, ScConnect);
            if (manager == IntPtr.Zero) return list;
            service = OpenService(manager, name, SvcEnumDependents);
            if (service == IntPtr.Zero) return list;
            if (EnumDependentServices(service, ServiceActive, IntPtr.Zero, 0, out uint needed, out uint count) || needed == 0) return list;
            buffer = Marshal.AllocHGlobal((int)needed);
            if (!EnumDependentServices(service, ServiceActive, buffer, needed, out needed, out count)) return list;
            int size = IntPtr.Size * 2 + Marshal.SizeOf<ServiceStatus>();     // ENUM_SERVICE_STATUSW
            for (int i = 0; i < count; i++)
            {
                string n = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, i * size));
                if (!string.IsNullOrEmpty(n)) list.Add(n);
            }
        }
        catch { }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            if (manager != IntPtr.Zero) CloseServiceHandle(manager);
        }
        return list;
    }

    /// <summary>Состояние службы: running, stopped, starting, stopping, missing, unknown. Только чтение.</summary>
    internal static string Query(string name)
    {
        IntPtr manager = IntPtr.Zero, service = IntPtr.Zero;
        try
        {
            manager = OpenSCManager(null, null, ScConnect);
            if (manager == IntPtr.Zero) return "unknown";
            service = OpenService(manager, name, SvcQuery);
            if (service == IntPtr.Zero) return Marshal.GetLastWin32Error() == 1060 ? "missing" : "unknown";
            if (!QueryServiceStatus(service, out var st)) return "unknown";
            return st.State switch { StateRunning => "running", StateStopped => "stopped", StateStartPending => "starting", StateStopPending => "stopping", _ => "running" };
        }
        catch { return "unknown"; }
        finally
        {
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            if (manager != IntPtr.Zero) CloseServiceHandle(manager);
        }
    }

    /// <summary>Попросить службу остановиться и подождать до 20 секунд. Тип запуска не меняется.</summary>
    internal static (bool Ok, string Error) StopService(string name)
    {
        IntPtr manager = IntPtr.Zero, service = IntPtr.Zero;
        try
        {
            manager = OpenSCManager(null, null, ScConnect);
            if (manager == IntPtr.Zero) return (false, Why(Marshal.GetLastWin32Error()));
            service = OpenService(manager, name, SvcStop | SvcQuery);
            if (service == IntPtr.Zero) return (false, Why(Marshal.GetLastWin32Error()));
            if (QueryServiceStatus(service, out var st) && st.State == StateStopped) return (true, "");
            if (!ControlService(service, 1, out st))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 1062) return (true, "");            // уже не работает
                if (error != 1061) return (false, Why(error));   // 1061 — служба занята, подождём
            }
            for (int i = 0; i < 80; i++)
            {
                if (QueryServiceStatus(service, out st) && st.State == StateStopped) return (true, "");
                Thread.Sleep(250);
            }
            return (false, "не остановилась за 20 секунд");
        }
        catch (Exception ex) { return (false, ex.Message); }
        finally
        {
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            if (manager != IntPtr.Zero) CloseServiceHandle(manager);
        }
    }

    /// <summary>Запустить службу. Again — стоит ли пробовать ещё раз при следующем запуске программы.</summary>
    internal static (bool Ok, bool Again, string Error) StartService(string name)
    {
        IntPtr manager = IntPtr.Zero, service = IntPtr.Zero;
        try
        {
            manager = OpenSCManager(null, null, ScConnect);
            if (manager == IntPtr.Zero) return (false, true, Why(Marshal.GetLastWin32Error()));
            service = OpenService(manager, name, SvcStart | SvcQuery);
            if (service == IntPtr.Zero) { int e = Marshal.GetLastWin32Error(); return (false, e != 1060, Why(e)); }
            if (QueryServiceStatus(service, out var st) && st.State is StateRunning or StateStartPending) return (true, false, "");
            if (StartService(service, 0, IntPtr.Zero)) return (true, false, "");
            int error = Marshal.GetLastWin32Error();
            if (error == 1056) return (true, false, "");         // уже запущена
            // 1058 — службу отключили (не мы: тип запуска программа не меняет). Повторять бессмысленно.
            return (false, error != 1058 && error != 1060, Why(error));
        }
        catch (Exception ex) { return (false, true, ex.Message); }
        finally
        {
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            if (manager != IntPtr.Zero) CloseServiceHandle(manager);
        }
    }

    private static string Why(int error) => error switch
    {
        5 => "Windows не дала доступа",
        1051 => "от неё зависят другие работающие службы",
        1052 => "Windows не разрешает её останавливать",
        1058 => "служба отключена в настройках Windows",
        1060 => "такой службы на компьютере нет",
        _ => "ошибка Windows " + error,
    };
}

/// <summary>
/// Период активности Windows: в эти часы система не перезагружает ПК ради обновлений. На время игры период сдвигается так,
/// чтобы покрыть ближайшие часы; прежние значения лежат в файле и возвращаются после игры. Групповые политики не трогаются.
/// Куст и путь реестра — параметры, чтобы запись можно было проверить в песочнице.
/// </summary>
internal static class ActiveHours
{
    public const string SettingsPath = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    private const string PolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    private const string StartName = "ActiveHoursStart", EndName = "ActiveHoursEnd";
    /// <summary>Длина периода, который ставит программа, в часах (Windows допускает до 18).</summary>
    public const int Span = 12;

    public static (int? Start, int? End) Read(RegistryKey hive, string path)
    {
        using var key = hive.OpenSubKey(path);
        return (Hour(key?.GetValue(StartName)), Hour(key?.GetValue(EndName)));
    }

    private static int? Hour(object value) => value is int i && i >= 0 && i <= 23 ? i : null;

    /// <summary>Период задан политикой организации: тогда значения пользователя не действуют, и программа их не меняет.</summary>
    public static bool ManagedByPolicy(RegistryKey hive)
    {
        try
        {
            using var key = hive.OpenSubKey(PolicyPath);
            return key?.GetValue("SetActiveHours") is int v && v == 1;
        }
        catch { return false; }
    }

    /// <summary>Сколько часов осталось до конца периода, если сейчас мы внутри него; 0 — вне периода.</summary>
    public static double HoursLeft(int start, int end, DateTime now)
    {
        int length = (end - start + 24) % 24;
        if (length == 0) return 0;
        double offset = (now.Hour + now.Minute / 60.0 - start + 24) % 24;
        return offset < length ? length - offset : 0;
    }

    /// <summary>Период, который покрывает ближайшие часы: с начала текущего часа на Span часов вперёд.</summary>
    public static (int Start, int End) Window(DateTime now) => (now.Hour, (now.Hour + Span) % 24);

    public static string Format(int? start, int? end) => start == null || end == null ? "не задан" : $"с {start:00}:00 до {end:00}:00";

    /// <summary>
    /// Сдвинуть период, если он не покрывает ближайшие needHours часов. Прежние значения сохраняются в файл (первая копия
    /// не затирается). Возвращает новый период текстом или null, если менять не пришлось.
    /// </summary>
    public static string Shift(RegistryKey hive, string path, string backupFile, DateTime now, double needHours)
    {
        var (start, end) = Read(hive, path);
        if (start != null && end != null && HoursLeft(start.Value, end.Value, now) >= needHours) return null;

        var (newStart, newEnd) = Window(now);
        var saved = LoadBackup(backupFile);
        // В файле — исходные значения и то, что записали мы: по второму после игры видно, не менял ли период кто-то ещё.
        File.WriteAllLines(backupFile, new[]
        {
            StartName + "=" + (saved != null ? Text(saved.Value.Start) : Text(start)),
            EndName + "=" + (saved != null ? Text(saved.Value.End) : Text(end)),
            $"Written={newStart}-{newEnd}",
        });
        using var key = hive.CreateSubKey(path, true);
        key.SetValue(StartName, newStart, RegistryValueKind.DWord);
        key.SetValue(EndName, newEnd, RegistryValueKind.DWord);
        return Format(newStart, newEnd);
    }

    /// <summary>
    /// Вернуть прежний период и удалить копию. false — период за время игры изменил кто-то другой (пользователь или Windows):
    /// тогда он оставлен как есть.
    /// </summary>
    public static bool Restore(RegistryKey hive, string path, string backupFile)
    {
        var saved = LoadBackup(backupFile);
        if (saved == null) { if (File.Exists(backupFile)) File.Delete(backupFile); return true; }

        var (start, end) = Read(hive, path);
        bool ours = saved.Value.Written == "" || saved.Value.Written == $"{start}-{end}";
        if (ours)
        {
            using var key = hive.CreateSubKey(path, true);
            Put(key, StartName, saved.Value.Start);
            Put(key, EndName, saved.Value.End);
        }
        File.Delete(backupFile);
        return ours;
    }

    private static void Put(RegistryKey key, string name, int? value)
    {
        if (value == null) key.DeleteValue(name, false);
        else key.SetValue(name, value.Value, RegistryValueKind.DWord);
    }

    private static string Text(int? hour) => hour?.ToString(CultureInfo.InvariantCulture) ?? "";

    /// <summary>Копия прежнего периода: null — файла нет или он испорчен. Пустое значение — параметра в реестре не было.</summary>
    public static (int? Start, int? End, string Written)? LoadBackup(string backupFile)
    {
        try
        {
            if (!File.Exists(backupFile)) return null;
            int? start = null, end = null; string written = ""; bool seen = false;
            foreach (string raw in File.ReadAllLines(backupFile))
            {
                string line = raw.Trim();
                if (line.StartsWith(StartName + "=")) { seen = true; start = int.TryParse(line[(StartName.Length + 1)..], out int v) ? Hour(v) : null; }
                else if (line.StartsWith(EndName + "=")) { seen = true; end = int.TryParse(line[(EndName.Length + 1)..], out int v) ? Hour(v) : null; }
                else if (line.StartsWith("Written=")) written = line[8..];
            }
            return seen ? (start, end, written) : null;
        }
        catch { return null; }
    }
}
