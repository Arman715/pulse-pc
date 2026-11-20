using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PulsePC.Services;

/// <summary>Поиск запущенной игры по расположению её файла (библиотеки Steam, Riot, Epic, Astrum, Minecraft).</summary>
public static class GameDetector
{
    private static readonly string[] SkipFolders = { "Blender", "Godot Engine", "Steamworks Shared", "SteamVR", "wallpaper_engine" };
    private static readonly string[] SkipExeParts =
    {
        "crash", "launcher", "execpubg", "_be", "_zk", "easyanticheat", "beservice", "start_protected",
        "webhelper", "cefprocess", "overlay", "updater", "installer", "redist",
    };
    private static readonly Dictionary<string, string> Pretty = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PUBG"] = "PUBG: BATTLEGROUNDS",
        ["Rematch"] = "REMATCH",
        ["Backrooms_Escape_Together"] = "Backrooms: Escape Together",
        ["Car Mechanic Simulator 2027 Demo"] = "Car Mechanic Simulator 2027",
    };

    /// <summary>Запущенная игра или null. Если подходят несколько процессов, берётся самый «тяжёлый».</summary>
    public static DetectedGame Find()
    {
        string test = Environment.GetEnvironmentVariable("PULSEPC_TEST_GAME");   // для проверки записи без настоящей игры
        DetectedGame best = null; long bestMem = 0;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                using (p)
                {
                    string name = p.ProcessName;
                    if (!string.IsNullOrEmpty(test) && name.Equals(test, StringComparison.OrdinalIgnoreCase))
                        return new DetectedGame { Name = "Тестовая игра", Pid = p.Id };
                    long mem = p.WorkingSet64;
                    if (mem < 300L * 1024 * 1024) continue;        // игры занимают сотни мегабайт: дешёвый отсев до обращения к процессу
                    string game = GameName(PathOf(p.Id), name);
                    if (game != null && mem > bestMem) { best = new DetectedGame { Name = game, Pid = p.Id }; bestMem = mem; }
                }
            }
            catch { }
        }
        return best;
    }

    /// <summary>Файл похож на игру (лежит в библиотеке Steam, Epic, Riot и т.д.) — такие не предлагается закрывать.</summary>
    public static bool LooksLikeGame(string path) => GameName(path, Path.GetFileNameWithoutExtension(path ?? "")) != null;

    /// <summary>Запущена ли сейчас программа из этой папки (игра перезаписывает свои настройки при выходе).</summary>
    public static bool IsRunningFrom(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return false;
        string root = dir.TrimEnd('\\') + "\\";
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                using (p)
                {
                    string path = PathOf(p.Id);
                    if (path != null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
        }
        return false;
    }

    private static string GameName(string path, string exe)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string lowerExe = exe.ToLowerInvariant();
        if (SkipExeParts.Any(lowerExe.Contains)) return null;

        string folder = FolderAfter(path, @"\steamapps\common\");
        if (folder != null) return SkipFolders.Contains(folder, StringComparer.OrdinalIgnoreCase) ? null : Name(folder);

        if (Has(path, @"\Riot Games\VALORANT\")) return "VALORANT";
        if (Has(path, @"\Riot Games\League of Legends\")) return "League of Legends";

        folder = FolderAfter(path, @"\AstrumPlay\");
        if (folder != null && !Has(path, @"\AppData\")) return Name(folder);

        if (Has(path, @"\Epic Games\") && !Has(path, @"\Epic Games\Launcher\") && !Has(path, @"\Epic Games\UE_") && !Has(path, @"\Epic Online Services\"))
        {
            folder = FolderAfter(path, @"\Epic Games\");
            if (folder != null) return Name(folder);
        }

        if (lowerExe == "javaw" && Has(path, ".minecraft")) return "Minecraft";
        return null;
    }

    private static bool Has(string path, string part) => path.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string FolderAfter(string path, string marker)
    {
        int i = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        string rest = path[(i + marker.Length)..];
        int slash = rest.IndexOf('\\');
        return slash > 0 ? rest[..slash] : null;
    }

    private static string Name(string folder) => Pretty.TryGetValue(folder, out var nice) ? nice : folder.Replace('_', ' ');

    // Путь к файлу процесса с минимальными правами доступа: работает и для процессов, защищённых античитом.
    private static string PathOf(int pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}

public class DetectedGame
{
    public string Name;
    public int Pid;
}

/// <summary>Итог одной игровой сессии.</summary>
public class GameSession
{
    public double? AvgFps { get; set; }
    public double? Low1Fps { get; set; }
    public double? Low01Fps { get; set; }
    public double[] FpsSeries { get; set; } = Array.Empty<double>();
    public string Game { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public double AvgCpu { get; set; }
    public double MaxCpu { get; set; }
    public double AvgGpu { get; set; }
    public double MaxGpu { get; set; }
    public double AvgTemp { get; set; }
    public double MaxTemp { get; set; }
    public double MinRamGb { get; set; }
    public double MaxVramGb { get; set; }
    public double VramTotalGb { get; set; }
    public double AvgPowerW { get; set; }
    /// <summary>Вентиляторы видеокарты, % от максимальной скорости (есть у сессий начиная с версии 1.3).</summary>
    public double? AvgFanPct { get; set; }
    public double? MaxFanPct { get; set; }
    public double? AvgPingMs { get; set; }
    public double? JitterMs { get; set; }
    public double? LossPct { get; set; }
    public double[] CpuSeries { get; set; } = Array.Empty<double>();
    public double[] GpuSeries { get; set; } = Array.Empty<double>();
    public double[] TempSeries { get; set; } = Array.Empty<double>();
    public List<string> Notes { get; set; } = new();

    // Разбор сессии (есть у сессий, записанных начиная с 18.11.2026).
    public List<FpsDip> Dips { get; set; }
    public double? BoundGpuPct { get; set; }
    public double? BoundCpuPct { get; set; }
    public double? BoundOtherPct { get; set; }
    public double? GpuPowerLimitPct { get; set; }
    public double? GpuThermalLimitPct { get; set; }
    public double? GpuHardLimitPct { get; set; }
    public double? AvgGpuClock { get; set; }
    public double? AvgCpuGhz { get; set; }
    public double? CpuSlowPct { get; set; }
    public double? MaxPagesIn { get; set; }
    // Сервер игры
    public string Server { get; set; }
    public double? ServerPingMs { get; set; }
    public double? ServerJitterMs { get; set; }
    public double? ServerLossPct { get; set; }
    public bool ServerPingBlocked { get; set; }
    public int? NetGaps { get; set; }
    public double? NetMaxGapSec { get; set; }

    /// <summary>
    /// Сколько времени реально записано, секунд (есть у сессий начиная с 19.11.2026). End − Start по местным часам врёт:
    /// сон компьютера посреди игры и перевод часов раздували длительность. У старых сессий — null, для них End − Start.
    /// </summary>
    public double? Seconds { get; set; }
    /// <summary>Начало и конец по UTC (у новых сессий; Start/End остаются местным временем, как раньше).</summary>
    public DateTime? StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }

    [JsonIgnore] public TimeSpan Duration => Seconds is double sec ? TimeSpan.FromSeconds(Math.Max(0, sec)) : End - Start;
    [JsonIgnore] public string Title => Game;
    [JsonIgnore] public string Caption => $"{Start:d MMM, HH:mm} · {Fmt.Duration(Duration)}";
}

/// <summary>Запись сессии: копит показания, пока игра запущена, и подводит итог, когда она закрыта.</summary>
public sealed class SessionRecorder
{
    private readonly List<double> cpu = new(), gpu = new(), temp = new(), fan = new();
    private double minRam = double.MaxValue, maxVram, vramTotal, powerSum;
    private int powerCount, misses;
    private NetCounters netStart;

    private FpsCapture fps;
    private GameNetMonitor gameNet;
    private readonly List<SessionSample> samples = new();
    private readonly Dictionary<int, string> memoryAt = new();   // номер замера → кто занимал память в момент просадки
    private readonly List<double> recentFps = new();
    private DateTime lastMemoryProbe;
    private volatile bool probing;
    // Реально записанное время: сумма промежутков между замерами, кроме разрывов дольше 10 с (сон компьютера, зависание).
    // Считается по Stopwatch, а не по часам Windows: перевод часов и сон не раздувают длительность.
    private long lastAddTs;
    private double recordedSec;
    private DateTime startedUtc, lastBeat;
    private const double MaxGapSec = 10;

    public string CurrentGame { get; private set; }
    public int GamePid { get; private set; }
    /// <summary>Базовая частота процессора, ГГц: ниже неё под нагрузкой — значит, процессор сбрасывает частоту.</summary>
    public double CpuBaseGhz { get; set; }
    public DateTime StartedAt { get; private set; }
    /// <summary>Сколько уже записано (без сна компьютера и разрывов) — для «записано …» на экране.</summary>
    public TimeSpan Recorded => TimeSpan.FromSeconds(recordedSec);
    public bool IsRecording => CurrentGame != null;
    /// <summary>Записывать кадры через PresentMon (нужны права администратора).</summary>
    public bool RecordFps { get; set; }
    /// <summary>FPS за последнюю секунду, если идёт запись кадров.</summary>
    public double LiveFps => fps?.LiveFps ?? double.NaN;

    /// <summary>Сессия закончилась и сохранена. Вызывается из фонового потока.</summary>
    public event Action<GameSession> Finished;

    private static int MinSeconds =>
        int.TryParse(Environment.GetEnvironmentVariable("PULSEPC_TEST_MINSEC"), out int s) ? s : 120;

    // Игры с античитом уровня ядра, для которых запись кадров не включаем: рисковать блокировкой аккаунта незачем.
    private static readonly string[] NoFpsGames = { "VALORANT", "League of Legends" };

    public static bool SkipsFps(string game) => NoFpsGames.Contains(game, StringComparer.OrdinalIgnoreCase);

    /// <summary>Такт записи. sample — показания процессора, памяти и диска; FPS и видеокарту рекордер дописывает сам.</summary>
    public void Update(DetectedGame detected, SessionSample sample, GpuSample gpuSample, NetMonitor net)
    {
        string game = detected?.Name;
        if (game != null)
        {
            // Другая игра — или та же, но перезапущенная (новый процесс, а прежний закрыт): прежняя сессия закрывается.
            // Иначе запись кадров и сети и «игровые» настройки остались бы привязаны к процессу, которого уже нет.
            if (CurrentGame != null && (CurrentGame != game || (detected.Pid != GamePid && !IsAlive(GamePid)))) Finish(net);
            if (CurrentGame == null) Begin(detected, net);
            misses = 0;
            Add(sample, gpuSample);
        }
        else if (CurrentGame != null)
        {
            // Игра могла на секунду пропасть из списка (перезапуск через лаунчер) - не рвём сессию сразу.
            if (++misses >= 8) Finish(net);
            else Add(sample, gpuSample);
        }
    }

    /// <summary>Завершить запись немедленно (при выходе из программы).</summary>
    public void Stop(NetMonitor net) { if (CurrentGame != null) Finish(net, wait: true); }

    /// <summary>Процесс ещё работает. Нет доступа (защита античита) — считаем живым: рвать сессию из-за этого нельзя.</summary>
    private static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch { return true; }
    }

    private void Begin(DetectedGame game, NetMonitor net)
    {
        CurrentGame = game.Name; GamePid = game.Pid; StartedAt = DateTime.Now; startedUtc = DateTime.UtcNow; misses = 0;
        lastAddTs = 0; recordedSec = 0;
        cpu.Clear(); gpu.Clear(); temp.Clear(); fan.Clear();
        samples.Clear(); memoryAt.Clear(); recentFps.Clear();
        minRam = double.MaxValue; maxVram = 0; vramTotal = 0; powerSum = 0; powerCount = 0;
        netStart = net?.Internet.Counters ?? default;

        fps = null; gameNet = null;
        // Кадры и сетевые события — только с правами администратора и не в играх с античитом уровня ядра.
        if (RecordFps && !NoFpsGames.Contains(game.Name, StringComparer.OrdinalIgnoreCase))
        {
            var capture = new FpsCapture();
            if (capture.Start(game.Pid)) fps = capture;
            var watcher = new GameNetMonitor();
            if (watcher.Start(game.Pid)) gameNet = watcher; else watcher.Dispose();
        }
    }

    private void Add(SessionSample s, GpuSample g)
    {
        double cpuLoad = s.Cpu, ramAvailGb = s.RamAvail;
        s.At = DateTime.Now;
        long ts = Stopwatch.GetTimestamp();
        if (lastAddTs != 0)
        {
            double dt = (ts - lastAddTs) / (double)Stopwatch.Frequency;
            if (dt > 0 && dt <= MaxGapSec) recordedSec += dt;
        }
        lastAddTs = ts;
        var capture = fps;
        // Раз в минуту, пока идёт запись, — отметка здоровья игровых частей: иначе они отмечались бы только в начале и конце
        // игры, и экран состояния через 5 минут любой игры показывал бы «нет данных».
        if ((DateTime.UtcNow - lastBeat).TotalSeconds >= 60)
        {
            lastBeat = DateTime.UtcNow;
            Health.Ok("Запись сессий");
            if (capture?.Running == true) Health.Ok("Кадры (PresentMon)");
            if (gameNet?.Running == true) Health.Ok("Сеть игры");
        }
        s.Fps = capture != null && (DateTime.Now - capture.LiveAt).TotalSeconds < 2.5 ? capture.LiveFps : double.NaN;
        s.Gpu = g?.Load ?? double.NaN; s.GpuTemp = g?.Temp ?? double.NaN; s.GpuClock = g?.Clock ?? double.NaN;
        s.LimitPower = g?.LimitPower ?? false; s.LimitThermal = g?.LimitThermal ?? false; s.LimitHardware = g?.LimitHardware ?? false;
        samples.Add(s);
        ProbeMemoryOnDip(s);

        cpu.Add(cpuLoad);
        gpu.Add(g?.Load ?? double.NaN);
        temp.Add(g?.Temp ?? double.NaN);
        if (g != null && !double.IsNaN(g.Fan)) fan.Add(g.Fan);
        if (ramAvailGb > 0 && ramAvailGb < minRam) minRam = ramAvailGb;
        if (g != null)
        {
            if (!double.IsNaN(g.MemUsed)) maxVram = Math.Max(maxVram, g.MemUsed / 1024);
            if (!double.IsNaN(g.MemTotal)) vramTotal = g.MemTotal / 1024;
            if (!double.IsNaN(g.Power)) { powerSum += g.Power; powerCount++; }
        }
    }

    /// <summary>
    /// В момент просадки при нехватке памяти запоминаем, кто её занимал: после игры этого уже не узнать.
    /// Перебор процессов идёт в фоне и не чаще раза в 15 секунд.
    /// </summary>
    private void ProbeMemoryOnDip(SessionSample s)
    {
        if (double.IsNaN(s.Fps)) return;
        recentFps.Add(s.Fps);
        if (recentFps.Count > 40) recentFps.RemoveAt(0);
        if (recentFps.Count < 10 || probing || (DateTime.Now - lastMemoryProbe).TotalSeconds < 15) return;
        double median = recentFps.OrderBy(x => x).ElementAt(recentFps.Count / 2);
        bool dip = median >= 40 && s.Fps < 0.65 * median;
        bool memoryTight = s.RamAvail < 1.2 || s.PagesIn >= 300;
        if (!dip || !memoryTight) return;

        int index = samples.Count - 1, pid = GamePid;
        lastMemoryProbe = DateTime.Now; probing = true;
        Task.Run(() =>
        {
            try
            {
                string who = ProcessMemory.Describe(ProcessMemory.Top(3, pid));
                lock (memoryAt) memoryAt[index] = who;
            }
            catch { }
            finally { probing = false; }
        });
    }

    /// <summary>
    /// Подвести итог сессии. Обычно — в фоне: сначала остановить PresentMon и сеть (несколько секунд, зато все кадры), потом сохранить.
    /// wait — выход из программы или из Windows: сначала сохранить с тем, что уже насчитано, и только потом останавливать.
    /// Остановка сеансов трассировки может занять дольше, чем Windows ждёт при выходе, — раньше сессия в этом случае терялась.
    /// </summary>
    private void Finish(NetMonitor net, bool wait = false)
    {
        var session = new GameSession
        {
            Game = CurrentGame, Start = StartedAt, End = DateTime.Now,
            Seconds = Math.Round(recordedSec, 1), StartUtc = startedUtc, EndUtc = DateTime.UtcNow,
            AvgCpu = Avg(cpu), MaxCpu = Max(cpu), AvgGpu = Avg(gpu), MaxGpu = Max(gpu),
            AvgTemp = Avg(temp), MaxTemp = Max(temp),
            MinRamGb = minRam == double.MaxValue ? 0 : minRam,
            MaxVramGb = maxVram, VramTotalGb = vramTotal,
            AvgPowerW = powerCount > 0 ? powerSum / powerCount : 0,
            AvgFanPct = fan.Count > 0 ? fan.Average() : null, MaxFanPct = fan.Count > 0 ? fan.Max() : null,
            CpuSeries = Downsample(cpu, 240), GpuSeries = Downsample(gpu, 240), TempSeries = Downsample(temp, 240),
        };

        if (net != null)
        {
            var d = net.Internet.Counters.Minus(netStart);
            if (d.Sent >= 10)
            {
                session.LossPct = 100.0 * d.Lost / d.Sent;
                if (d.Received > 0) session.AvgPingMs = d.SumMs / d.Received;
                if (d.Received > 1) session.JitterMs = d.SumAbsDiff / (d.Received - 1);
            }
        }
        var capture = fps;
        var watcher = gameNet;
        var recorded = samples.ToList();
        Dictionary<int, string> memory;
        lock (memoryAt) memory = new Dictionary<int, string>(memoryAt);
        double baseGhz = CpuBaseGhz;
        fps = null; gameNet = null;
        CurrentGame = null;
        bool keep = session.Duration.TotalSeconds >= MinSeconds;     // слишком короткий запуск - не сессия

        void Complete(FpsResult result, GameNetStats netStats)
        {
            if (result != null)
            {
                session.AvgFps = result.Avg; session.Low1Fps = result.Low1; session.Low01Fps = result.Low01;
                session.FpsSeries = Downsample(result.Series.ToList(), 240);
            }
            if (netStats?.Server != null)
            {
                session.Server = netStats.Server;
                session.ServerPingMs = netStats.AvgPingMs; session.ServerJitterMs = netStats.JitterMs; session.ServerLossPct = netStats.LossPct;
                session.ServerPingBlocked = netStats.PingBlocked;
                session.NetGaps = netStats.Gaps; session.NetMaxGapSec = netStats.MaxGapSec;
            }
            try { SessionAnalysis.Apply(session, recorded, baseGhz, memory, netStats?.GapTimes); }
            catch (Exception ex) { Log.Error("разбор сессии " + session.Game, ex); }
            session.Notes = BuildNotes(session);
            SessionStore.Add(session);
        }

        if (wait)
        {
            if (keep)
            {
                try
                {
                    FpsResult partial = null;
                    GameNetStats netNow = null;
                    try { partial = capture?.Result(); } catch (Exception ex) { Log.Error("кадры при выходе", ex); }
                    try { netNow = watcher?.Snapshot(); } catch (Exception ex) { Log.Error("сеть игры при выходе", ex); }
                    Complete(partial, netNow);
                    Task.Run(() => { try { Finished?.Invoke(session); } catch { } });
                }
                catch (Exception ex) { Health.Fail("Запись сессий", "сохранение при выходе: " + ex.Message); }
            }
            // Сессия уже на диске — теперь можно спокойно гасить PresentMon и сеанс сети (не дольше 5 с; что не успеет,
            // завершит Windows через задание для дочерних процессов, а остаток сеанса уберётся при следующем запуске).
            var stop = Task.Run(() =>
            {
                try { capture?.Stop(); } catch { }
                try { watcher?.Dispose(); } catch { }
            });
            try { stop.Wait(5000); } catch { }
            return;
        }

        // Остановка записи кадров занимает несколько секунд, поэтому итог подводится в фоне.
        Task.Run(() =>
        {
            try
            {
                var result = capture?.Stop();
                GameNetStats netStats = null;
                try { netStats = watcher?.Stop(); } catch (Exception ex) { Log.Error("сеть игры: остановка", ex); }
                try { watcher?.Dispose(); } catch { }
                if (!keep) return;
                Complete(result, netStats);
                Finished?.Invoke(session);
            }
            catch (Exception ex) { Health.Fail("Запись сессий", "итог сессии: " + ex.Message); }
        });
    }

    private static List<string> BuildNotes(GameSession s)
    {
        var notes = new List<string>();
        if (s.AvgFps > 0 && s.Low1Fps > 0 && s.Low1Fps < 0.5 * s.AvgFps)
            notes.Add($"Сильные просадки: в среднем {s.AvgFps:0} FPS, а в худшие моменты — {s.Low1Fps:0} FPS. Игра шла рывками.");
        if (s.MinRamGb > 0 && s.MinRamGb < 1.0)
            notes.Add($"Не хватало памяти: в худший момент свободно было {s.MinRamGb:0.0} ГБ. Перед игрой закрывайте тяжёлые программы.");
        if (s.Dips is { Count: > 0 })
        {
            var main = s.Dips.GroupBy(d => d.Cause).OrderByDescending(g => g.Count()).First();
            string why = main.Key == "unknown" ? "" : $" Чаще всего причина — {SessionAnalysis.CauseTitle(main.Key)} ({main.Count()} из {s.Dips.Count}).";
            notes.Add($"{Fmt.Plural(s.Dips.Count, "резкая просадка", "резкие просадки", "резких просадок")} FPS.{why} Разбор каждой — ниже, в «Просадках».");
        }
        if (s.BoundGpuPct == null)
        {
            // Старый способ: по средней загрузке, если подробных замеров нет.
            if (s.AvgGpu >= 93)
                notes.Add($"Игра упиралась в видеокарту (в среднем {s.AvgGpu:0} %). Поднять FPS можно настройками графики или {(GameTuning.IsNvidia ? "DLSS" : "FSR / XeSS")}.");
            else if (s.AvgGpu > 0 && s.AvgGpu < 75 && s.AvgCpu >= 40)
                notes.Add($"Игра упиралась в процессор: видеокарта была загружена в среднем на {s.AvgGpu:0} %. Настройки графики на FPS почти не влияют.");
        }
        if (s.GpuThermalLimitPct >= 5)
            notes.Add($"{s.GpuThermalLimitPct:0} % времени под нагрузкой видеокарта сбрасывала частоту из-за нагрева. Почистите радиатор и проверьте обдув.");
        if (s.GpuHardLimitPct >= 5)
            notes.Add($"{s.GpuHardLimitPct:0} % времени видеокарта аварийно снижала частоту по сигналу от блока питания или платы. Проверьте, плотно ли вставлен разъём питания видеокарты.");
        if (s.CpuSlowPct >= 10)
            notes.Add($"{s.CpuSlowPct:0} % времени под нагрузкой процессор работал ниже базовой частоты — скорее всего, из-за перегрева или ограничения питания. Проверьте кулер процессора.");
        if (s.MaxPagesIn >= 1000 && !(s.MinRamGb > 0 && s.MinRamGb < 1.0))
            notes.Add("Windows часто обращалась к файлу подкачки: игре не хватало памяти, отсюда рывки.");
        if (s.NetGaps >= 3)
            notes.Add($"Связь с сервером игры прерывалась {Fmt.Plural(s.NetGaps.Value, "раз", "раза", "раз")} (самый долгий обрыв — {s.NetMaxGapSec:0.0} с). Отсюда «телепорты» и запоздалые действия.");
        if (s.MaxTemp >= 83)
            notes.Add($"Видеокарта нагревалась до {s.MaxTemp:0} °C. Проверьте обдув корпуса.");
        if (s.VramTotalGb > 0 && s.MaxVramGb >= 0.92 * s.VramTotalGb)
            notes.Add($"Видеопамять была заполнена почти полностью ({s.MaxVramGb:0.0} из {s.VramTotalGb:0.0} ГБ). Снизьте качество текстур.");
        if (s.LossPct >= 1)
            notes.Add($"Терялось {s.LossPct:0.0} % пакетов — в сетевой игре это «телепорты» и рывки.");
        if (s.JitterMs >= 15)
            notes.Add($"Пинг «гулял»: разброс {s.JitterMs:0} мс при среднем {s.AvgPingMs:0} мс.");
        if (notes.Count == 0)
            notes.Add("Замечаний нет: температура, память и загрузка были в норме.");
        return notes;
    }

    private static double Avg(List<double> v) { var ok = v.Where(x => !double.IsNaN(x)).ToList(); return ok.Count > 0 ? ok.Average() : 0; }
    private static double Max(List<double> v) { var ok = v.Where(x => !double.IsNaN(x)).ToList(); return ok.Count > 0 ? ok.Max() : 0; }

    /// <summary>Сжимает ряд до заданного числа точек усреднением (для графика в отчёте).</summary>
    private static double[] Downsample(List<double> values, int target)
    {
        if (values.Count <= target) return values.ToArray();
        var result = new double[target];
        for (int i = 0; i < target; i++)
        {
            int from = (int)((long)i * values.Count / target), to = Math.Max(from + 1, (int)((long)(i + 1) * values.Count / target));
            double sum = 0; int n = 0;
            for (int k = from; k < to && k < values.Count; k++) if (!double.IsNaN(values[k])) { sum += values[k]; n++; }
            result[i] = n > 0 ? sum / n : double.NaN;
        }
        return result;
    }
}

/// <summary>История сессий и настройки в папке %LOCALAPPDATA%\PulsePC.</summary>
public static class SessionStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string Folder
    {
        get
        {
            // PULSEPC_DATA — отдельная папка данных для проверок, чтобы подставные данные не смешались с настоящими.
            string dir = Environment.GetEnvironmentVariable("PULSEPC_DATA") is { Length: > 0 } test ? test
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulsePC");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string SessionsFile => Path.Combine(Folder, "sessions.json");

    /// <summary>
    /// Все сессии. Файл пишется атомарно (временный файл + подмена с копией «.bak»), поэтому при сбое берётся копия.
    /// Если испорчены оба, записи вытаскиваются по одной: одна битая запись не прячет остальные.
    /// Чтение идёт под тем же замком, что и запись: иначе чтение посреди подмены приняло бы занятый файл за испорченный.
    /// </summary>
    public static List<GameSession> Load()
    {
        lock (AtomicFile.LockFor(SessionsFile))
        {
            try
            {
                var list = AtomicFile.Read(SessionsFile, text => JsonSerializer.Deserialize<List<GameSession>>(text, Json));
                if (list != null) return list;
                foreach (var path in new[] { SessionsFile, SessionsFile + ".bak" })
                {
                    if (!File.Exists(path)) continue;
                    // Исходный файл сохраняется рядом — иначе следующая запись сессии молча затёрла бы всю историю.
                    KeepBroken();
                    var salvaged = Salvage(path);
                    if (salvaged.Count > 0)
                    {
                        Log.Info($"sessions.json испорчен: по одной восстановлено {salvaged.Count} сессий из {Path.GetFileName(path)}");
                        return salvaged;
                    }
                }
                if (File.Exists(SessionsFile)) Health.Fail("Запись сессий", "sessions.json не читается — копия сохранена как sessions.unreadable.json");
            }
            catch (Exception ex) { KeepBroken(); Health.Fail("Запись сессий", "чтение сессий: " + ex.Message); }
            return new List<GameSession>();
        }
    }

    private static List<GameSession> Salvage(string path)
    {
        var list = new List<GameSession>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                try { if (item.Deserialize<GameSession>(Json) is { } s) list.Add(s); } catch { }
            }
        }
        catch (Exception ex) { Log.Error("разбор " + Path.GetFileName(path) + " по записям", ex); }
        return list;
    }

    /// <summary>Копия файла сессий, который не удалось прочитать целиком (одна, первая не затирается).</summary>
    private static void KeepBroken()
    {
        try
        {
            string copy = Path.Combine(Folder, "sessions.unreadable.json");
            if (File.Exists(SessionsFile) && !File.Exists(copy)) File.Copy(SessionsFile, copy);
        }
        catch { }
    }

    public static void Add(GameSession session)
    {
        // Чтение и запись под одним замком: две сессии, закончившиеся одновременно (смена игры, выход), не теряют друг друга.
        lock (AtomicFile.LockFor(SessionsFile))
        {
            try
            {
                var all = Load();
                all.Insert(0, session);
                if (all.Count > 200) all.RemoveRange(200, all.Count - 200);     // хватает на сводки за несколько недель
                AtomicFile.WriteAllText(SessionsFile, JsonSerializer.Serialize(all, Json));
                Health.Ok("Запись сессий");
            }
            catch (Exception ex) { Health.Fail("Запись сессий", $"сессия {session?.Game} не сохранена: {ex.Message}"); }
        }
    }
}

public class DiskState
{
    public string Health { get; set; } = "";
    public double ReadErrors { get; set; }
    public double Hours { get; set; }
    public DateTime Checked { get; set; }
}

public class AppSettings
{
    public bool AlertsEnabled { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool TrayHintShown { get; set; }
    /// <summary>Записывать FPS в играх через PresentMon.</summary>
    public bool RecordFps { get; set; } = true;
    /// <summary>После игры открывать программы, закрытые кнопкой «Играть».</summary>
    public bool RestoreApps { get; set; } = true;
    /// <summary>Игра, выбранная в прошлый раз для кнопки «Играть».</summary>
    public string LastGame { get; set; } = "";
    // Сторож
    public DateTime LastWatch { get; set; } = DateTime.MinValue;
    public string LastWatchSummary { get; set; } = "";
    public List<string> KnownApplied { get; set; } = new();
    /// <summary>Состояние дисков на прошлой проверке сторожа (по имени диска).</summary>
    public Dictionary<string, DiskState> DiskStates { get; set; } = new();
    /// <summary>Когда последний раз показывалась сводка за неделю.</summary>
    public DateTime LastWeekly { get; set; } = DateTime.MinValue;
    /// <summary>Настройки, применённые раньше, сбросило обновление Windows — возвращать их сразу, без вопроса.</summary>
    public bool AutoRestoreTweaks { get; set; } = true;
    /// <summary>Сброшенные настройки, которые ждут возврата (если автовозврат выключен или не удался).</summary>
    public List<string> RevertedTweaks { get; set; } = new();
    /// <summary>Плановая очистка безопасных категорий раз в неделю.</summary>
    public bool AutoCleanWeekly { get; set; } = true;
    public DateTime LastAutoClean { get; set; } = DateTime.MinValue;
    /// <summary>Первичный осмотр системы пройден (показывается при первом запуске).</summary>
    public bool FirstScanDone { get; set; }
    /// <summary>Игры, о рекомендациях для которых уже сообщалось у часов.</summary>
    public List<string> TuneNotified { get; set; } = new();
    /// <summary>Настройки разделов вкладки «Система»: ключ вида «раздел.параметр» → значение строкой.</summary>
    public Dictionary<string, string> Values { get; set; } = new();

    /// <summary>
    /// Один замок на настройки для всех потоков: Save() и Set()/Get() из фоновых служб (память, вентиляторы, ввод, питание,
    /// обновления, сторож) шли одновременно, и «Collection was modified» посреди сохранения молча терял запись.
    /// Кто меняет списки и словари настроек не из окна — делает это под этим замком.
    /// </summary>
    public static readonly object SyncRoot = new();

    public string Get(string key, string fallback = "")
    {
        lock (SyncRoot) return Values != null && Values.TryGetValue(key, out var v) ? v : fallback;
    }
    public bool GetBool(string key, bool fallback = false) => Get(key, fallback ? "1" : "0") == "1";
    /// <summary>Записать значение (null — удалить). В файл попадёт после Save().</summary>
    public void Set(string key, string value)
    {
        lock (SyncRoot)
        {
            Values ??= new();
            if (value == null) Values.Remove(key); else Values[key] = value;
        }
    }
    public void SetBool(string key, bool value) => Set(key, value ? "1" : "0");

    private static string FilePath => Path.Combine(SessionStore.Folder, "settings.json");

    /// <summary>
    /// Настройки из файла, при его порче — из копии «.bak». Если не читаются оба, это пишется в журнал (испорченный файл
    /// откладывается рядом), а не подменяется молча настройками по умолчанию.
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            var s = AtomicFile.Read(FilePath, text => JsonSerializer.Deserialize<AppSettings>(text));
            if (s != null) { s.Values ??= new(); s.DiskStates ??= new(); return s; }
            if (File.Exists(FilePath) || File.Exists(FilePath + ".bak"))
                Log.Error("settings.json и его копия не читаются — настройки по умолчанию (испорченный файл сохранён рядом)");
        }
        catch (Exception ex) { Log.Error("чтение настроек", ex); }
        return new AppSettings();
    }

    public void Save()
    {
        lock (SyncRoot)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    // Снимок под замком: словари и списки копируются, сериализуется копия.
                    var copy = (AppSettings)MemberwiseClone();
                    copy.Values = Values != null ? new Dictionary<string, string>(Values) : new();
                    copy.DiskStates = DiskStates != null ? new Dictionary<string, DiskState>(DiskStates) : new();
                    copy.KnownApplied = KnownApplied?.ToList() ?? new();
                    copy.RevertedTweaks = RevertedTweaks?.ToList() ?? new();
                    copy.TuneNotified = TuneNotified?.ToList() ?? new();
                    AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(copy));
                    return;
                }
                catch (InvalidOperationException) when (attempt < 3) { Thread.Sleep(20); }   // список меняли без замка — ещё раз
                catch (Exception ex) { Log.Error("сохранение настроек", ex); return; }
            }
        }
    }
}
