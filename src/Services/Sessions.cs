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

    private static CustomGame[] custom = Array.Empty<CustomGame>();

    /// <summary>Игры, добавленные вручную (из настроек). Вызывается при загрузке настроек и после каждого изменения списка.</summary>
    public static void SetCustom(IEnumerable<CustomGame> games) =>
        Volatile.Write(ref custom, games?.Where(g => !string.IsNullOrWhiteSpace(g?.Exe)).Select(g => new CustomGame { Name = g.Name, Exe = g.Exe }).ToArray()
                                   ?? Array.Empty<CustomGame>());

    /// <summary>
    /// Добавленная вручную игра для файла процесса: тот же .exe или любой .exe из папки игры и её подпапок
    /// (часто запускают лаунчер, а играет другой файл рядом — «…-Win64-Shipping.exe»). Служебные файлы — античит, отчёт о сбое,
    /// программа обновления — не в счёт.
    /// </summary>
    public static CustomGame MatchCustom(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string exe = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        foreach (var g in Volatile.Read(ref custom))
        {
            if (string.Equals(path, g.Exe, StringComparison.OrdinalIgnoreCase)) return g;
            string dir = Path.GetDirectoryName(g.Exe);
            if (string.IsNullOrEmpty(dir) || !CustomGame.FolderUsable(dir)) continue;
            if (path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) && !SkipExeParts.Any(exe.Contains)) return g;
        }
        return null;
    }

    /// <summary>Самопроверка распознавания игр, добавленных вручную (на выдуманных путях, процессы не трогаются).</summary>
    public static string SelfTest()
    {
        var saved = Volatile.Read(ref custom);
        var fails = new List<string>();
        void Check(string what, bool ok) { if (!ok) fails.Add(what); }
        try
        {
            SetCustom(new[]
            {
                new CustomGame { Name = "Моя игра", Exe = @"D:\Games\MyGame\Launcher.exe" },
                new CustomGame { Name = "Из загрузок", Exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "tiny.exe") },
            });
            Check("тот же файл", MatchCustom(@"D:\Games\MyGame\Launcher.exe")?.Name == "Моя игра");
            Check("другой файл в подпапке игры", MatchCustom(@"D:\Games\MyGame\Binaries\Win64\MyGame-Win64-Shipping.exe")?.Name == "Моя игра");
            Check("античит и отчёт о сбое — не игра", MatchCustom(@"D:\Games\MyGame\EasyAntiCheat\EasyAntiCheat_Setup.exe") == null
                                                      && MatchCustom(@"D:\Games\MyGame\CrashReporter.exe") == null);
            Check("соседняя папка — не игра", MatchCustom(@"D:\Games\MyGameTools\Tool.exe") == null && MatchCustom(@"D:\Games\Other\Other.exe") == null);
            Check("из «Загрузок» узнаётся только сам файл", MatchCustom(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "other.exe")) == null
                                                         && MatchCustom(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "tiny.exe"))?.Name == "Из загрузок");
            Check("корень диска и общие папки лаунчеров не годятся как папка игры",
                !CustomGame.FolderUsable(@"D:\") && !CustomGame.FolderUsable(@"D:\SteamLibrary\steamapps\common") && !CustomGame.FolderUsable(@"C:\Games")
                && !CustomGame.FolderUsable(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) && CustomGame.FolderUsable(@"D:\Games\MyGame"));
            Check("пустой путь", MatchCustom(null) == null && MatchCustom("") == null);
        }
        catch (Exception ex) { fails.Add("исключение: " + ex.Message); }
        finally { Volatile.Write(ref custom, saved); }
        return fails.Count == 0 ? "OK (7 проверок)" : "FAIL: " + string.Join("; ", fails);
    }

    public static List<CustomGame> CustomList() => Volatile.Read(ref custom).Select(g => new CustomGame { Name = g.Name, Exe = g.Exe }).ToList();

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
                    // Игры занимают сотни мегабайт: дешёвый отсев до обращения к процессу. Для добавленных вручную порог ниже —
                    // небольшие и старые игры бывают легче.
                    // Найденные поиском и по поведению (GameFinder) тоже бывают лёгкими — для них порог такой же.
                    bool anyCustom = Volatile.Read(ref custom).Length > 0 || GameFinder.HasIndex;
                    if (mem < (anyCustom ? 60L : 300L) * 1024 * 1024) continue;
                    string path = PathOf(p.Id);
                    string game = MatchCustom(path)?.Name;
                    if (game == null && mem >= 300L * 1024 * 1024) game = GameName(path, name);
                    game ??= GameFinder.Match(path, mem);     // GOG, Ubisoft, Xbox, EA, Battle.net…, игры на дисках и найденные по поведению
                    if (game != null && mem > bestMem) { best = new DetectedGame { Name = game, Pid = p.Id, Path = path }; bestMem = mem; }
                }
            }
            catch { }
        }
        // Античит игры (осторожный режим): папка игры, службы, запустившиеся с ней, список — в фоне, процесс игры не открывается.
        AntiCheat.Observe(best?.Name, best?.Path);
        return best;
    }

    /// <summary>Файл похож на игру (лежит в библиотеке Steam, Epic, Riot и т.д.) — такие не предлагается закрывать.</summary>
    public static bool LooksLikeGame(string path) => MatchCustom(path) != null || GameName(path, Path.GetFileNameWithoutExtension(path ?? "")) != null
                                                     || GameFinder.Match(path, long.MaxValue) != null;

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

/// <summary>Игра, добавленная вручную: название и файл .exe.</summary>
public class CustomGame
{
    public string Name { get; set; } = "";
    public string Exe { get; set; } = "";

    /// <summary>
    /// По папке игры можно узнавать её файлы, только если это действительно папка игры, а не корень диска,
    /// «Program Files», «Загрузки» или рабочий стол: иначе «игрой» стала бы любая программа оттуда.
    /// </summary>
    public static bool FolderUsable(string dir)
    {
        try
        {
            string full = Path.GetFullPath(dir).TrimEnd('\\');
            if (Path.GetPathRoot(full)?.TrimEnd('\\').Equals(full, StringComparison.OrdinalIgnoreCase) == true) return false;
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] broad =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.System),
                user, Path.Combine(user, "Downloads"), Path.Combine(user, "Desktop"), Path.Combine(user, "Documents"),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            };
            if (broad.Any(b => !string.IsNullOrEmpty(b) && b.TrimEnd('\\').Equals(full, StringComparison.OrdinalIgnoreCase))) return false;
            // Общие папки лаунчеров: «…\steamapps\common», «…\Epic Games» — в них лежат все игры сразу.
            string name = Path.GetFileName(full);
            return !new[] { "common", "steamapps", "Epic Games", "Games", "Игры", "AstrumPlay", "Riot Games" }.Contains(name, StringComparer.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Название по файлу: «Название продукта» из свойств .exe, иначе имя папки игры, иначе имя файла.</summary>
    public static string GuessName(string exe)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            foreach (string s in new[] { info.ProductName, info.FileDescription })
            {
                string t = s?.Trim();
                if (!string.IsNullOrEmpty(t) && t.Length <= 60 && !t.Contains("Unreal", StringComparison.OrdinalIgnoreCase)
                    && !t.Contains("Unity", StringComparison.OrdinalIgnoreCase) && !t.Contains("Launcher", StringComparison.OrdinalIgnoreCase)) return t;
            }
        }
        catch { }
        string dir = Path.GetFileName(Path.GetDirectoryName(exe) ?? "");
        if (!string.IsNullOrEmpty(dir) && !new[] { "bin", "x64", "Win64", "Binaries", "Game" }.Contains(dir, StringComparer.OrdinalIgnoreCase))
            return dir.Replace('_', ' ');
        return Path.GetFileNameWithoutExtension(exe).Replace('_', ' ');
    }
}

public class DetectedGame
{
    public string Name;
    public int Pid;
    /// <summary>Файл процесса игры (прочитан с минимальными правами; null — не прочитался).</summary>
    public string Path;
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
    /// <summary>Самая высокая температура видеокарты, при которой срабатывал предел температуры (с 1.5.1; у старых — null).</summary>
    public double? GpuThermalTempC { get; set; }
    public double? GpuHardLimitPct { get; set; }
    public double? AvgGpuClock { get; set; }
    public double? AvgCpuGhz { get; set; }
    public double? CpuSlowPct { get; set; }
    public double? MaxPagesIn { get; set; }
    /// <summary>Замеров, где подгрузка страниц (от 1000/с) и нехватка свободной памяти совпали по времени — настоящая подкачка (у старых — null).</summary>
    public int? PagingSamples { get; set; }
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

    // Отчёт после игры (с версии 1.5; у старых сессий всё это null — отчёт показывает то, что есть).
    /// <summary>Сжатый ряд времени кадров и самые заметные рывки.</summary>
    public FrameTimeline Frames { get; set; }
    /// <summary>Плавность 0–100 (см. SessionAnalysis.SmoothnessScore).</summary>
    public double? Smoothness { get; set; }
    public int? Stutters { get; set; }
    public double? StutterPerMin { get; set; }
    /// <summary>Вид рывков: сборка шейдеров, диск, процессор, память, видеопамять.</summary>
    public List<StutterGroup> StutterKinds { get; set; }
    /// <summary>Что ещё нагружало процессор и диск за всю сессию (без самой игры и программы).</summary>
    public List<Culprit> Background { get; set; }
    /// <summary>Отметки на графиках: игра свёрнута, пауза в кадрах, сброс драйвера видеокарты, скачки пинга, виновники.</summary>
    public List<SessionEvent> Events { get; set; }
    /// <summary>Какой видеоадаптер рисовал игру (по счётчикам Windows «GPU Engine» процесса игры).</summary>
    public string GpuUsed { get; set; }
    public bool? GpuUsedIntegrated { get; set; }
    /// <summary>Отдельная видеокарта, которая была в компьютере, но игру не рисовала (только если рисовала встроенная).</summary>
    public string GpuIdle { get; set; }
    /// <summary>Ноутбук: встроенная и отдельная графика переключаются (влияет только на совет, как переключить).</summary>
    public bool? GpuHybrid { get; set; }
    /// <summary>Условия игры: драйвер видеокарты, схема питания, свободная память в начале.</summary>
    public string Driver { get; set; }
    public string PowerPlan { get; set; }
    public double? StartFreeRamGb { get; set; }
    /// <summary>Заметка пользователя: что он менял перед этой игрой.</summary>
    public string UserNote { get; set; }
    /// <summary>Сколько процессора в среднем занимала сама программа вместе с записью кадров, % от всего процессора.</summary>
    public double? AppCpuPct { get; set; }
    /// <summary>Осторожный режим (античит уровня ядра): кадры, сеть игры и опрос процесса игры не записывались. У старых сессий — null.</summary>
    public bool? Cautious { get; set; }
    /// <summary>Чьё решение: auto — по античиту, manual — включил человек, full — человек разрешил полную запись.</summary>
    public string CautiousBy { get; set; }
    /// <summary>Запись кадров возобновилась посреди игры (человек разрешил полную запись): FPS — только с этого момента.</summary>
    public DateTime? FramesFrom { get; set; }
    /// <summary>Античит игры, если известен (название), и работает ли он на уровне ядра.</summary>
    public string AntiCheat { get; set; }
    public bool? AntiCheatKernel { get; set; }

    /// <summary>Сессия не легла на диск: причина (null — сохранена). В файл не пишется.</summary>
    [JsonIgnore] public string SaveError { get; set; }
    /// <summary>Подпись в списке сессий: дата, длительность и плавность, если она есть.</summary>
    [JsonIgnore] public string ListCaption => Smoothness is double sm ? Caption + " · " + L.F("плавность {0:0}", sm) : Caption;
    /// <summary>Заметка в списке сессий (пусто — строки нет).</summary>
    [JsonIgnore] public string NoteLine => string.IsNullOrWhiteSpace(UserNote) ? "" : UserNote.Trim().Replace('\n', ' ').Replace("\r", "");

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
    private PingTarget.Mark netMark;

    private FpsCapture fps;
    private GameNetMonitor gameNet;
    private readonly List<SessionSample> samples = new();
    // Отчёт после игры (1.5): лёгкий опрос других процессов, адаптер игры, условия игры, нагрузка самой программы.
    private ProcessActivity procs;
    private GameGpuProbe gpuProbe;
    private string gameImage, driver, powerPlan;
    private double startFreeRam = double.NaN, selfCpuSum;
    private int sampleNo, selfCpuN;
    private readonly Stopwatch extraCost = new();
    private readonly Dictionary<int, string> memoryAt = new();   // номер замера → кто занимал память в момент просадки
    private readonly List<double> recentFps = new();
    private DateTime lastMemoryProbe;
    private volatile bool probing;
    private int probeGen;     // номер сессии (под замком memoryAt): проба, начатая в прошлой сессии, в новую не попадает
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

    /// <summary>Идущая игра для оверлея (null — записи нет). Вызывать в потоке интерфейса, как и Update.</summary>
    public LiveGameFeed LiveFeed()
    {
        if (CurrentGame == null) return null;
        var watcher = gameNet;
        return new LiveGameFeed
        {
            Pid = GamePid, Game = CurrentGame, Image = gameImage ?? "", Frames = fps,
            ServerPingMs = () => watcher?.ServerPingNow() ?? double.NaN,
        };
    }

    /// <summary>Сессия закончилась и сохранена. Вызывается из фонового потока.</summary>
    public event Action<GameSession> Finished;

    private static int MinSeconds =>
        int.TryParse(Environment.GetEnvironmentVariable("PULSEPC_TEST_MINSEC"), out int s) ? s : 120;

    /// <summary>
    /// В этой игре кадры не записываются: осторожный режим (античит уровня ядра или выбор человека) — см. <see cref="CautiousMode"/>.
    /// Раньше это был жёсткий список VALORANT и League of Legends; теперь они попадают сюда по встроенному списку и папке игры.
    /// </summary>
    public static bool SkipsFps(string game) => CautiousMode.For(game).Cautious;

    /// <summary>Решение для идущей записи: осторожный режим или полная запись.</summary>
    private CautiousDecision cautious;
    private string gamePath;

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
            RecheckCautious();
            Add(sample, gpuSample, net);
        }
        else if (CurrentGame != null)
        {
            // Игра могла на секунду пропасть из списка (перезапуск через лаунчер) - не рвём сессию сразу.
            if (++misses >= 8) Finish(net);
            else Add(sample, gpuSample, net);
        }
    }

    /// <summary>Завершить запись немедленно (при выходе из программы).</summary>
    public void Stop(NetMonitor net) { if (CurrentGame != null) Finish(net, wait: true); }

    /// <summary>
    /// Процесс ещё работает. Открывается только с правом PROCESS_QUERY_LIMITED_INFORMATION (код завершения) — так можно и для игр
    /// в осторожном режиме. Нет доступа (защита античита) — считаем живым: рвать сессию из-за этого нельзя.
    /// </summary>
    private static bool IsAlive(int pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return Marshal.GetLastWin32Error() != 87;      // 87 — такого процесса нет
        try { return !GetExitCodeProcess(h, out uint code) || code == 259; }   // 259 — STILL_ACTIVE
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    /// <summary>
    /// Античит мог найтись не сразу (его служба запускается через несколько секунд после игры): тогда запись кадров и сеть игры
    /// останавливаются посреди сессии, и сессия считается записанной в осторожном режиме.
    /// </summary>
    private void RecheckCautious()
    {
        // Проверяется и без записи кадров (нет прав администратора): опрос «GPU Engine» по процессу игры тоже нужно остановить,
        // а Protects(pid) — включить, иначе другие части программы открывали бы процесс игры как обычный.
        if (cautious == null || CurrentGame == null || sampleNo % 4 != 0) return;
        var now = CautiousMode.For(CurrentGame, gamePath);
        if (cautious.Cautious)
        {
            // Человек сам разрешил полную запись посреди игры — запись кадров и сети начинается заново с этого момента.
            // Явный выбор: «full» (полная запись при античите) или снятый ручной осторожный режим (у игр с EasyAntiCheat, BattlEye
            // и без античита он снимается, и решение снова «auto»). Осторожный режим «по античиту» сам по себе не снимается.
            if (!now.Cautious && (now.By == "full" || cautious.By == "manual")) ResumeFull(now);
            return;
        }
        if (!now.Cautious) return;
        cautious = now;
        CautiousMode.SetCurrent(now, GamePid);
        var capture = fps; var watcher = gameNet;
        fps = null; gameNet = null; gpuProbe = null;
        framesDropped = true; framesFrom = null;
        Log.Info($"осторожный режим для {CurrentGame}: античит {now.AntiCheat?.Name} найден во время игры — запись кадров и сети игры остановлена");
        Task.Run(() =>
        {
            try { capture?.Stop(); } catch { }
            try { watcher?.Dispose(); } catch { }
        });
    }

    /// <summary>Когда посреди игры возобновилась запись кадров (FPS в отчёте — только с этого момента).</summary>
    private DateTime? framesFrom;
    /// <summary>Запись кадров остановилась посреди игры (осторожный режим): FPS этой сессии не сохраняется.</summary>
    private bool framesDropped;

    private void ResumeFull(CautiousDecision now)
    {
        cautious = now;
        framesFrom = DateTime.Now;
        CautiousMode.SetCurrent(now, GamePid);
        gpuProbe = now.AllowGpuProbe ? new GameGpuProbe() : null;
        if (RecordFps && fps == null)
        {
            var capture = new FpsCapture();
            if (capture.Start(GamePid)) fps = capture;
            var watcher = new GameNetMonitor();
            if (watcher.Start(GamePid)) gameNet = watcher; else watcher.Dispose();
        }
        Log.Info($"полная запись для {CurrentGame} включена вручную во время игры — кадры и сеть записываются с этого момента");
    }

    private void Begin(DetectedGame game, NetMonitor net)
    {
        CurrentGame = game.Name; GamePid = game.Pid; StartedAt = DateTime.Now; startedUtc = DateTime.UtcNow; misses = 0;
        framesFrom = null; framesDropped = false;
        lastAddTs = 0; recordedSec = 0;
        cpu.Clear(); gpu.Clear(); temp.Clear(); fan.Clear();
        samples.Clear(); recentFps.Clear();
        // Под замком: фоновая проба памяти прошлой сессии может как раз дописывать словарь.
        lock (memoryAt) { memoryAt.Clear(); probeGen++; }
        minRam = double.MaxValue; maxVram = 0; vramTotal = 0; powerSum = 0; powerCount = 0;
        netMark = net?.Internet.Begin();
        procs?.Dispose();
        procs = new ProcessActivity();
        gameImage = null; driver = null; powerPlan = null;
        startFreeRam = double.NaN; selfCpuSum = 0; selfCpuN = 0; sampleNo = 0;
        extraCost.Reset();
        // Осторожный режим (античит уровня ядра или выбор человека): решение — до запуска любой трассировки.
        gamePath = game.Path;
        cautious = CautiousMode.For(game.Name, game.Path);
        CautiousMode.SetCurrent(cautious, game.Pid);
        // «GPU Engine» по процессу игры — только вне осторожного режима.
        gpuProbe = cautious.AllowGpuProbe ? new GameGpuProbe() : null;
        // Имя файла игры: из пути (уже прочитан с минимальными правами) или из списка процессов Windows (процесс не открывается).
        if (!string.IsNullOrEmpty(game.Path)) gameImage = Path.GetFileNameWithoutExtension(game.Path);
        else if (cautious.AllowProcessOpen) try { using var gp = Process.GetProcessById(game.Pid); gameImage = gp.ProcessName; } catch { }

        fps = null; gameNet = null;
        // Кадры и сетевые события — только с правами администратора и не в осторожном режиме.
        if (RecordFps && cautious.AllowFrames && cautious.AllowGameNet)
        {
            var capture = new FpsCapture();
            if (capture.Start(game.Pid)) fps = capture;
            var watcher = new GameNetMonitor();
            if (watcher.Start(game.Pid)) gameNet = watcher; else watcher.Dispose();
        }
    }

    private void Add(SessionSample s, GpuSample g, NetMonitor net)
    {
        double cpuLoad = s.Cpu, ramAvailGb = s.RamAvail;
        s.At = DateTime.Now;
        AddExtra(ref s, g, net);
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
        if (capture != null) capture.Away = s.Unfocused;
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
    /// Отчёт после игры (1.5): другие процессы (один системный вызов), чтение диска игрой, игра на переднем плане, пинг,
    /// видеопамять; условия игры — драйвер, схема питания, свободная память в начале; редкий опрос адаптера игры в фоне.
    /// Всё это замеряется по времени (extraCost) — в журнал после сессии.
    /// </summary>
    private void AddExtra(ref SessionSample s, GpuSample g, NetMonitor net)
    {
        extraCost.Start();
        try
        {
            sampleNo++;
            if (double.IsNaN(startFreeRam) && s.RamAvail > 0) startFreeRam = s.RamAvail;
            if (driver == null && !string.IsNullOrEmpty(g?.Driver)) driver = g.Driver;
            // Через полминуты: к этому времени «Питание» успевает включить игровую схему, если так настроено.
            if (sampleNo == 20) { try { powerPlan = Bench.ActivePowerPlan(); } catch { } }
            s.GameIoMBs = double.NaN;
            if (procs != null)
            {
                s.Top = procs.Sample(GamePid, gameImage, out double gameIo);
                s.GameIoMBs = gameIo;
                if (!double.IsNaN(procs.SelfCpu)) { selfCpuSum += procs.SelfCpu; selfCpuN++; }
            }
            s.Unfocused = SessionCulprits.ForegroundPid() != GamePid;
            s.VramGb = g != null && !double.IsNaN(g.MemUsed) ? g.MemUsed / 1024 : 0;
            var pings = net?.Internet.Snapshot();
            s.PingMs = pings is { Length: > 0 } && !double.IsNaN(pings[^1]) ? pings[^1] : 0;
            gpuProbe?.Tick(GamePid, StartedAt);
        }
        catch (Exception ex) { Log.Error("запись сессии: доп. замер", ex); }
        finally { extraCost.Stop(); }
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
        bool memoryTight = (s.RamAvail > 0 && s.RamAvail < 1.2) || SessionAnalysis.MemoryShort(s.RamAvail, s.PagesIn, SessionAnalysis.TotalRamGb(), 300);
        if (!dip || !memoryTight) return;

        int index = samples.Count - 1, pid = GamePid, gen;
        lock (memoryAt) gen = probeGen;
        lastMemoryProbe = DateTime.Now; probing = true;
        Task.Run(() =>
        {
            try
            {
                string who = ProcessMemory.Describe(ProcessMemory.Top(3, pid));
                lock (memoryAt) { if (gen == probeGen) memoryAt[index] = who; }
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
            Driver = driver, PowerPlan = string.IsNullOrEmpty(powerPlan) ? null : powerPlan,
            StartFreeRamGb = double.IsNaN(startFreeRam) ? null : Math.Round(startFreeRam, 2),
            AppCpuPct = selfCpuN > 0 ? Math.Round(selfCpuSum / selfCpuN, 2) : null,
            Cautious = cautious?.Cautious, CautiousBy = cautious?.By, FramesFrom = framesFrom,
            AntiCheat = cautious?.AntiCheat?.Name, AntiCheatKernel = cautious?.AntiCheat?.Kernel,
        };
        cautious = null; gamePath = null;
        CautiousMode.SetCurrent(null, 0);
        var activity = procs; var probe = gpuProbe;
        procs = null; gpuProbe = null;
        activity?.Dispose();      // буфер опроса больше не нужен; счётчики стоимости остаются для журнала
        double tickMs = extraCost.Elapsed.TotalMilliseconds;
        int ticks = Math.Max(1, sampleNo);

        if (net != null && netMark != null)
        {
            // Разброс — только между замерами внутри сессии: разница первого замера в игре с последним до неё не считается.
            var d = net.Internet.End(netMark, out double crossDiff);
            netMark = null;
            if (d.Sent >= 10)
            {
                session.LossPct = 100.0 * d.Lost / d.Sent;
                if (d.Received > 0) session.AvgPingMs = d.SumMs / d.Received;
                if (d.Received > 1) session.JitterMs = Math.Max(0, d.SumAbsDiff - crossDiff) / (d.Received - 1);
            }
        }
        var capture = fps;
        var watcher = gameNet;
        var recorded = samples.ToList();
        // Запись кадров остановилась посреди игры (осторожный режим): FPS сессии не сохраняется — и просадки FPS по отдельным
        // замерам тоже не ищутся, иначе отчёт говорил бы «FPS не записывался» и тут же показывал просадки FPS.
        if (framesDropped && cautious?.Cautious == true)
            for (int i = 0; i < recorded.Count; i++) { var x = recorded[i]; x.Fps = double.NaN; recorded[i] = x; }
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
            try { SessionAnalysis.ApplyFrames(session, result, recorded); }
            catch (Exception ex) { Log.Error("время кадров " + session.Game, ex); }
            try { SessionCulprits.Apply(session, recorded, result); }
            catch (Exception ex) { Log.Error("виновники и отметки " + session.Game, ex); }
            try
            {
                if (probe?.Result() is { } used)
                {
                    session.GpuUsed = used.Used; session.GpuUsedIntegrated = used.Integrated;
                    session.GpuIdle = used.Discrete; session.GpuHybrid = IsLaptop();
                }
            }
            catch (Exception ex) { Log.Error("видеоадаптер игры " + session.Game, ex); }
            try
            {
                // Нагрузка самой записи — в журнал: сколько стоил такт и редкие опросы адаптера.
                Log.Info($"запись {session.Game}: {ticks} замеров, доп. работа {tickMs / ticks:0.00} мс на замер (процессы {activity?.CostMs / Math.Max(1, activity?.Calls ?? 1):0.00} мс), " +
                         $"адаптер {probe?.WorkMs:0} мс за {probe?.Probes} опросов, программа с PresentMon в среднем {session.AppCpuPct:0.00} % процессора");
            }
            catch { }
            session.Notes = BuildNotes(session);
            session.SaveError = SessionStore.Add(session);
            if (session.SaveError != null)
                session.Notes.Insert(0, L.F("Отчёт не сохранён на диск ({0}) — после перезапуска программы его не будет.", session.SaveError));
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
                catch (Exception ex) { Health.Fail("Запись сессий", L.F("сохранение при выходе: {0}", ex.Message)); }
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
            catch (Exception ex) { Health.Fail("Запись сессий", L.F("итог сессии: {0}", ex.Message)); }
        });
    }

    /// <summary>
    /// Ноутбук (там встроенная графика при отдельной — схема с переключением, а не кабель в плате). Общий признак LaptopCheck.Applies
    /// (мобильная платформа, корпус ноутбука или батарея; батарея ИБП не в счёт).
    /// Флаги «гибридной» графики у видеокарт для этого не годятся: Windows ставит их и настольным видеокартам.
    /// </summary>
    private static bool IsLaptop() => LaptopCheck.Applies;

    internal static List<string> BuildNotes(GameSession s)
    {
        var notes = new List<string>();
        if (s.GpuUsedIntegrated == true && !string.IsNullOrEmpty(s.GpuIdle))
            notes.Add(L.F("Игра шла на встроенной графике ({0}), хотя в компьютере есть {1}. Отсюда низкий FPS — как переключить, написано вверху отчёта.", s.GpuUsed, s.GpuIdle));
        if (s.Smoothness is double sm && sm < 70 && s.StutterKinds is { Count: > 0 })
        {
            var main = s.StutterKinds.FirstOrDefault(g => g.Kind != "other") ?? s.StutterKinds[0];
            // «Чаще всего — …» только если этот вид и правда самый частый: 10 рывков из-за диска при 190 «без видимой причины» — не «чаще всего».
            int unexplained = s.StutterKinds.Where(g => g.Kind == "other").Sum(g => g.Count);
            if (main.Kind != "other" && main.Count < unexplained) main = s.StutterKinds.First(g => g.Kind == "other");
            notes.Add(main.Kind == "other"
                ? L.F("Плавность {0:0} из 100 (рывков в минуту: {1:0.#}). Подробности — в «Виде рывков».", sm, s.StutterPerMin)
                : L.F("Плавность {0:0} из 100 (рывков в минуту: {1:0.#}), чаще всего — {2}. Подробности — в «Виде рывков».", sm, s.StutterPerMin, SessionAnalysis.StutterTitle(main.Kind)));
        }
        int resets = s.Events?.Count(e => e.Kind == "driver") ?? 0;
        if (resets > 0)
            notes.Add(L.F("Драйвер видеокарты сбрасывался во время игры ({0}): экран на секунду гас, игра могла вылететь. Если это повторяется — смотрите раздел «Драйвер» на вкладке «Система».", SessionAnalysis.Times(resets)));
        if (s.AvgFps > 0 && s.Low1Fps > 0 && s.Low1Fps < 0.5 * s.AvgFps)
            notes.Add(L.F("Сильные просадки: в среднем {0:0} FPS, а в худшие моменты — {1:0} FPS. Игра шла рывками.", s.AvgFps, s.Low1Fps));
        if (s.MinRamGb > 0 && s.MinRamGb < SessionAnalysis.LowRamGb)
            notes.Add(L.F("Не хватало памяти: в худший момент свободно было {0:0.0} ГБ. Перед игрой закрывайте тяжёлые программы.", s.MinRamGb));
        if (s.Dips is { Count: > 0 })
        {
            var main = s.Dips.GroupBy(d => d.Cause).OrderByDescending(g => g.Count()).First();
            string count = Fmt.Plural(s.Dips.Count, "резкая просадка", "резкие просадки", "резких просадок");
            notes.Add(main.Key == "unknown"
                ? L.F("{0} FPS. Разбор каждой — ниже, в «Просадках».", count)
                : L.F("{0} FPS. Чаще всего причина — {1} ({2} из {3}). Разбор каждой — ниже, в «Просадках».",
                      count, SessionAnalysis.CauseTitle(main.Key), main.Count(), s.Dips.Count));
        }
        if (s.BoundGpuPct == null)
        {
            // Старый способ: по средней загрузке, если подробных замеров нет.
            if (s.AvgGpu >= 93)
                notes.Add(L.F("Игра упиралась в видеокарту (в среднем {0:0} %). Поднять FPS можно настройками графики или {1}.", s.AvgGpu, GameTuning.IsNvidia ? "DLSS" : "FSR / XeSS"));
            else if (s.AvgGpu > 0 && s.AvgGpu < 75 && s.AvgCpu >= 40)
                notes.Add(L.F("Игра упиралась в процессор: видеокарта была загружена в среднем на {0:0} %. Настройки графики на FPS почти не влияют.", s.AvgGpu));
        }
        bool coolingTipShown = false;
        if (s.GpuThermalLimitPct >= 5)
        {
            // Предел температуры ниже 80 °C — не перегрев: так его задаёт ноутбук (тихий режим) или программа разгона.
            double t = s.GpuThermalTempC ?? s.MaxTemp;
            if (t > 0 && t < SessionAnalysis.TempLimitHotC(IsLaptop()))
                notes.Add(IsLaptop()
                    ? L.F("{0:0} % времени под нагрузкой видеокарта держалась у предела температуры, который задаёт ноутбук (около {1:0} °C), и снижала частоту. Это не перегрев. Если нужен FPS — включите режим «Производительность» или «Турбо» (раздел «Ноутбук» на вкладке «Система»).", s.GpuThermalLimitPct, t)
                    : L.F("{0:0} % времени под нагрузкой видеокарта держалась у своего предела температуры (около {1:0} °C) и снижала частоту. Это не перегрев: проверьте, не занижен ли предел температуры в MSI Afterburner или похожей программе.", s.GpuThermalLimitPct, t));
            else
                notes.Add(IsLaptop()
                    ? L.F("{0:0} % времени под нагрузкой видеокарта сбрасывала частоту из-за нагрева.", s.GpuThermalLimitPct) + " " + CoolingTipOnce(ref coolingTipShown)
                    : L.F("{0:0} % времени под нагрузкой видеокарта сбрасывала частоту из-за нагрева. Почистите радиатор и проверьте обдув.", s.GpuThermalLimitPct));
        }
        if (s.GpuHardLimitPct >= 5)
            notes.Add(IsLaptop()
                ? L.F("{0:0} % времени видеокарта аварийно снижала частоту по сигналу питания. Играйте от зарядки родным адаптером: от батареи или слабого адаптера ноутбуку не хватает мощности.", s.GpuHardLimitPct)
                : L.F("{0:0} % времени видеокарта аварийно снижала частоту по сигналу от блока питания или платы. Проверьте, плотно ли вставлен разъём питания видеокарты.", s.GpuHardLimitPct));
        if (s.CpuSlowPct >= 15)
            notes.Add(IsLaptop()
                ? L.F("{0:0} % времени под нагрузкой процессор работал ниже базовой частоты — скорее всего, из-за перегрева или ограничения питания. Играйте от зарядки и не закрывайте отверстия снизу ноутбука.", s.CpuSlowPct)
                :L.F("{0:0} % времени под нагрузкой процессор работал ниже базовой частоты — скорее всего, из-за перегрева или ограничения питания. Проверьте кулер процессора.", s.CpuSlowPct));
        // Подкачка — только если в те же моменты и свободной памяти было мало: много подгружаемых страниц при достатке памяти — это игра
        // читает свои файлы (SessionAnalysis.PagingHurt). Меньше 1 ГБ — уже сказано выше «не хватало памяти».
        if (s.MinRamGb >= SessionAnalysis.LowRamGb && SessionAnalysis.PagingHurt(s))
            notes.Add(L.T("Windows часто обращалась к файлу подкачки: игре не хватало памяти, отсюда рывки."));
        if (s.NetGaps >= 3)
            notes.Add(L.F("Связь с сервером игры прерывалась {0} (самый долгий обрыв — {1:0.0} с). Отсюда «телепорты» и запоздалые действия.", SessionAnalysis.Times(s.NetGaps.Value), s.NetMaxGapSec));
        // Горячо — если так было заметную часть игры (от 5 % графика температуры), а не один короткий пик.
        if (s.MaxTemp >= SessionAnalysis.HotMaxTempC(IsLaptop()) && SessionAnalysis.HotLong(s.TempSeries, SessionAnalysis.HotMaxTempC(IsLaptop())))   // у ноутбуков видеокарта рассчитана на 87 °C
            notes.Add(IsLaptop()
                ? L.F("Видеокарта нагревалась до {0:0} °C.", s.MaxTemp) + " " + CoolingTipOnce(ref coolingTipShown)
                :L.F("Видеокарта нагревалась до {0:0} °C. Проверьте обдув корпуса.", s.MaxTemp));
        if (s.VramTotalGb > 0 && s.MaxVramGb >= 0.92 * s.VramTotalGb && VramHurt(s))
            notes.Add(L.F("Видеопамять была заполнена почти полностью ({0:0.0} из {1:0.0} ГБ). Снизьте качество текстур.", s.MaxVramGb, s.VramTotalGb));
        if (s.LossPct >= 1)
            notes.Add(L.F("Терялось {0:0.0} % пакетов — в сетевой игре это «телепорты» и рывки.", s.LossPct));
        if (s.JitterMs >= 15)
            notes.Add(L.F("Пинг «гулял»: разброс {0:0} мс при среднем {1:0} мс.", s.JitterMs, s.AvgPingMs));
        if (notes.Count == 0)
            notes.Add(L.T("Замечаний нет: в записанных показателях проблем не найдено."));
        return notes;
    }

    /// <summary>
    /// Мешала ли заполненная видеопамять. Многие игры нарочно занимают почти всю видеопамять под запас, и само по себе это не беда —
    /// вывод нужен, только если были рывки из-за видеопамяти. Без замера кадров (старые сессии, нет прав) — только при 98 % и выше.
    /// </summary>
    /// <summary>
    /// Видеопамять мешала — только если рывки разобраны как нехватка видеопамяти. Без записи кадров (нет прав, осторожный режим)
    /// доказательств нет: игры занимают почти всю видеопамять нарочно, а замер — по всей видеокарте, а не по игре.
    /// </summary>
    private static bool VramHurt(GameSession s) => s.StutterKinds != null && s.StutterKinds.Any(k => k.Kind == "vram");

    /// <summary>Совет по охлаждению ноутбука — один раз на отчёт.</summary>
    private static string CoolingTipOnce(ref bool shown)
    {
        if (shown) return "";
        shown = true;
        return LaptopCheck.CoolingTip();
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
                if (list != null) { RepairOld(list); return list; }
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
                if (File.Exists(SessionsFile)) Health.Fail("Запись сессий", L.T("sessions.json не читается — копия сохранена как sessions.unreadable.json"));
            }
            catch (Exception ex) { KeepBroken(); Health.Fail("Запись сессий", L.F("чтение сессий: {0}", ex.Message)); }
            return new List<GameSession>();
        }
    }

    /// <summary>
    /// Сессии, записанные до 1.5.1, могли содержать ложные выводы («не хватало памяти» при достатке свободной, «нагрев» при 70 °C
    /// или по сигналу питания). Исправляем их при чтении (в памяти; файл перезапишется при следующей записи сессии).
    /// </summary>
    private static void RepairOld(List<GameSession> list)
    {
        // Пустые записи («null» в файле, правка вручную) отчёт и списки не переварили бы — их просто нет.
        list.RemoveAll(s => s == null);
        foreach (var s in list)
        {
            try
            {
                s.Dips?.RemoveAll(d => d == null);
                s.StutterKinds?.RemoveAll(k => k == null);
                s.Events?.RemoveAll(e => e == null);
                s.Notes?.RemoveAll(n => n == null);
                bool notesWrong = s.Notes != null && (s.Notes.Any(n => n.Contains("файлу подкачки") || n.Contains("page file"))
                                  && !(s.MinRamGb > 0 && s.MinRamGb < SessionAnalysis.TightRamGb(SessionAnalysis.TotalRamGb())));
                bool heatWrong = s.GpuThermalLimitPct >= 5 && (s.GpuThermalTempC ?? s.MaxTemp) is double t && t > 0 && t < SessionAnalysis.TempLimitHotC(LaptopCheck.Applies);
                // Замечания — чистая функция сохранённых полей, поэтому у сессий с подробными замерами (MaxPagesIn есть с 1.2)
                // они пересобираются всегда: так к старым отчётам применяются исправленные правила и язык интерфейса.
                if (SessionAnalysis.RepairOld(s) || notesWrong || heatWrong || s.MaxPagesIn != null)
                    s.Notes = SessionRecorder.BuildNotes(s);
            }
            catch (Exception ex) { Log.Error("исправление старой сессии", ex); }
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

    /// <summary>
    /// Список для перезаписи файла. Если файл есть, но не прочитался совсем (заблокирован, испорчен и копия тоже), Load вернул бы
    /// пустой список — и запись новой сессии поверх него стёрла бы всю историю. Тогда запись отменяется с понятной причиной.
    /// </summary>
    private static List<GameSession> LoadForWrite()
    {
        var all = Load();
        if (all.Count == 0)
        {
            var info = new FileInfo(SessionsFile);
            if (info.Exists && info.Length > 16)
                throw new IOException(L.T("файл сессий сейчас не читается — чтобы не затереть историю, запись отложена"));
        }
        return all;
    }

    /// <summary>
    /// Записать заметку к сессии (найдена по началу и игре). Возвращает причину ошибки или null. Заметка не длиннее 500 знаков.
    /// </summary>
    public static string SetNote(DateTime start, string game, string note)
    {
        lock (AtomicFile.LockFor(SessionsFile))
        {
            try
            {
                var all = LoadForWrite();
                var s = all.FirstOrDefault(x => x.Start == start && x.Game == game);
                if (s == null) return L.T("сессия не найдена в файле");
                note = note?.Trim();
                s.UserNote = string.IsNullOrEmpty(note) ? null : note.Length > 500 ? note[..500] : note;
                AtomicFile.WriteAllText(SessionsFile, JsonSerializer.Serialize(all, Json));
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("заметка к сессии", ex);
                return Services.Installer.Reason(ex);
            }
        }
    }

    /// <summary>Разобрать сессии из текста JSON (для самопроверки чтения старых файлов).</summary>
    public static List<GameSession> Parse(string json) => JsonSerializer.Deserialize<List<GameSession>>(json, Json);
    public static string Serialize(List<GameSession> list) => JsonSerializer.Serialize(list, Json);

    /// <summary>Удалить из файла сессии, подходящие под условие. Возвращает, сколько удалено, и причину ошибки (null — всё в порядке).</summary>
    public static (int Removed, string Error) Remove(Func<GameSession, bool> match)
    {
        lock (AtomicFile.LockFor(SessionsFile))
        {
            try
            {
                var all = LoadForWrite();
                int removed = all.RemoveAll(s => match(s));
                if (removed > 0) AtomicFile.WriteAllText(SessionsFile, JsonSerializer.Serialize(all, Json));
                return (removed, null);
            }
            catch (Exception ex)
            {
                Log.Error("удаление сессий", ex);
                return (0, Services.Installer.Reason(ex));
            }
        }
    }

    /// <summary>
    /// Дописать сессию в файл. Возвращает null, если сессия на диске, иначе — причину (по ней окно не скажет «Сессия записана»:
    /// раньше ошибка уходила только в журнал, а после перезапуска отчёта не оказывалось).
    /// </summary>
    public static string Add(GameSession session)
    {
        // Чтение и запись под одним замком: две сессии, закончившиеся одновременно (смена игры, выход), не теряют друг друга.
        lock (AtomicFile.LockFor(SessionsFile))
        {
            try
            {
                var all = LoadForWrite();
                all.Insert(0, session);
                if (all.Count > 200) all.RemoveRange(200, all.Count - 200);     // хватает на сводки за несколько недель
                AtomicFile.WriteAllText(SessionsFile, JsonSerializer.Serialize(all, Json));
                Health.Ok("Запись сессий");
                return null;
            }
            catch (Exception ex)
            {
                Health.Fail("Запись сессий", L.F("сессия {0} не сохранена: {1}", session?.Game, ex.Message));
                return Services.Installer.Reason(ex);
            }
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
    /// <summary>Игры, добавленные вручную: программа узнаёт их при запуске и записывает, как найденные сама.</summary>
    public List<CustomGame> CustomGames { get; set; } = new();
    /// <summary>Настройки разделов вкладки «Система»: ключ вида «раздел.параметр» → значение строкой.</summary>
    public Dictionary<string, string> Values { get; set; } = new();

    /// <summary>
    /// Один замок на настройки для всех потоков: Save() и Set()/Get() из фоновых служб (память, вентиляторы, ввод, питание,
    /// обновления, сторож) шли одновременно, и «Collection was modified» посреди сохранения молча терял запись.
    /// Кто меняет списки и словари настроек не из окна — делает это под этим замком.
    /// </summary>
    public static readonly object SyncRoot = new();

    /// <summary>Выбранный язык интерфейса ("ru", "en" или пусто) — читается до запуска окна, прямо из файла настроек.</summary>
    public static string LanguageSetting() => StartupValue(L.SettingKey);

    /// <summary>Выбранная тема ("light", "dark" или пусто — как в Windows).</summary>
    public static string ThemeSetting() => StartupValue(Theme.SettingKey);

    private static AppSettings startupCopy;
    private static string StartupValue(string key)
    {
        try { return (startupCopy ??= Load()).Get(key); } catch { return ""; }
    }

    /// <summary>Копия списка добавленных вручную игр для фоновых потоков (библиотека игр).</summary>
    public static List<CustomGame> CurrentCustom() => GameDetector.CustomList();

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
            if (s != null) { s.Values ??= new(); s.DiskStates ??= new(); s.CustomGames ??= new(); GameDetector.SetCustom(s.CustomGames); return s; }
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
                    copy.CustomGames = CustomGames?.Select(g => new CustomGame { Name = g.Name, Exe = g.Exe }).ToList() ?? new();
                    AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(copy));
                    return;
                }
                catch (InvalidOperationException) when (attempt < 3) { Thread.Sleep(20); }   // список меняли без замка — ещё раз
                catch (Exception ex) { Log.Error("сохранение настроек", ex); return; }
            }
        }
    }
}
