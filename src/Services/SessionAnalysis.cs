using System.Diagnostics;

namespace PulsePC.Services;

/// <summary>Один замер во время игры (раз в 1,5 с). Хранится только в памяти, пока идёт запись.</summary>
public struct SessionSample
{
    public DateTime At;
    public double Fps, Cpu, MaxCore, Ghz, Gpu, GpuTemp, GpuClock, RamAvail, PagesIn, DiskBusy;
    public bool LimitPower, LimitThermal, LimitHardware;
}

/// <summary>Просадка FPS и её вероятная причина.</summary>
public class FpsDip
{
    public DateTime At { get; set; }
    public double Seconds { get; set; }
    public double FromFps { get; set; }
    public double ToFps { get; set; }
    /// <summary>ram, disk, cpu, gpu-heat, gpu, net, unknown.</summary>
    public string Cause { get; set; } = "unknown";
    public string Detail { get; set; } = "";
}

/// <summary>Кто занимает память: имя программы (все её процессы вместе) и объём.</summary>
public static class ProcessMemory
{
    // Системные процессы закрыть нельзя — в подсказках их не называем.
    private static readonly string[] SystemNames =
    {
        "System", "Registry", "Memory Compression", "svchost", "csrss", "wininit", "winlogon", "services", "lsass", "smss",
        "dwm", "explorer", "Idle", "fontdrvhost", "audiodg", "MsMpEng", "NisSrv", "SecurityHealthService", "SearchHost",
        "StartMenuExperienceHost", "ShellExperienceHost", "RuntimeBroker", "ctfmon", "sihost", "taskhostw", "PulsePC", "nvcontainer",
        "NVDisplay.Container", "dllhost", "conhost", "spoolsv", "WmiPrvSE", "TextInputHost", "LsaIso", "vmmem", "vmmemWSL",
    };

    public static List<(string Name, long Bytes)> Top(int count, int excludePid = -1)
    {
        var result = new List<(string, long)>();
        try
        {
            var all = Process.GetProcesses();
            foreach (var g in all.GroupBy(p => p.ProcessName))
            {
                if (SystemNames.Contains(g.Key, StringComparer.OrdinalIgnoreCase)) continue;
                long sum = 0; bool isGame = false;
                foreach (var p in g) { try { if (p.Id == excludePid) isGame = true; sum += p.WorkingSet64; } catch { } }
                if (!isGame) result.Add((g.Key, sum));
            }
            foreach (var p in all) p.Dispose();
        }
        catch { }
        return result.OrderByDescending(x => x.Item2).Take(count).ToList();
    }

    public static string Describe(IEnumerable<(string Name, long Bytes)> top) =>
        string.Join(", ", top.Select(t => $"{Pretty(t.Name)} {Fmt.Size(t.Bytes)}"));

    public static string Pretty(string name) => name.ToLowerInvariant() switch
    {
        "browser" => "Яндекс Браузер",
        "chrome" => "Chrome",
        "msedge" => "Edge",
        "claude" => "Claude",
        "steamwebhelper" => "Steam",
        "discord" => "Discord",
        _ => name,
    };
}

/// <summary>Разбор записанной сессии: просадки FPS, что ограничивало игру, снижения частот.</summary>
public static class SessionAnalysis
{
    private const double Step = 1.5;     // секунд между замерами

    /// <summary>Заполнить в отчёте всё, что выводится из замеров.</summary>
    public static void Apply(GameSession s, List<SessionSample> samples, double cpuBaseGhz,
        Dictionary<int, string> memoryAtSample, List<DateTime> netGaps)
    {
        if (samples.Count < 10) return;
        Bottleneck(s, samples);
        Throttling(s, samples, cpuBaseGhz);
        s.MaxPagesIn = samples.Max(x => double.IsNaN(x.PagesIn) ? 0 : x.PagesIn);
        s.Dips = FindDips(samples, memoryAtSample, netGaps ?? new List<DateTime>());
    }

    // ------------------------------------------------------------------ узкое место
    private static void Bottleneck(GameSession s, List<SessionSample> samples)
    {
        // Первые полминуты — загрузка игры и меню: в вывод не берём.
        var busy = samples.Skip(20).Where(x => !double.IsNaN(x.Gpu) && !double.IsNaN(x.Cpu)).ToList();
        if (busy.Count < 20) return;
        int gpu = 0, cpu = 0, other = 0;
        foreach (var x in busy)
        {
            double core = double.IsNaN(x.MaxCore) ? x.Cpu : x.MaxCore;
            if (x.Gpu >= 95) gpu++;
            else if (x.Gpu < 85 && (core >= 90 || x.Cpu >= 85)) cpu++;
            else other++;
        }
        s.BoundGpuPct = 100.0 * gpu / busy.Count;
        s.BoundCpuPct = 100.0 * cpu / busy.Count;
        s.BoundOtherPct = 100.0 * other / busy.Count;
    }

    // ------------------------------------------------------------------ частоты
    private static void Throttling(GameSession s, List<SessionSample> samples, double cpuBaseGhz)
    {
        var gpuBusy = samples.Where(x => x.Gpu >= 50).ToList();
        if (gpuBusy.Count >= 10)
        {
            s.GpuPowerLimitPct = 100.0 * gpuBusy.Count(x => x.LimitPower) / gpuBusy.Count;
            s.GpuThermalLimitPct = 100.0 * gpuBusy.Count(x => x.LimitThermal) / gpuBusy.Count;
            s.GpuHardLimitPct = 100.0 * gpuBusy.Count(x => x.LimitHardware) / gpuBusy.Count;
            var clocks = gpuBusy.Where(x => !double.IsNaN(x.GpuClock)).Select(x => x.GpuClock).ToList();
            if (clocks.Count > 0) s.AvgGpuClock = clocks.Average();
        }
        var cpuBusy = samples.Where(x => !double.IsNaN(x.Ghz) && (x.Cpu >= 30 || x.MaxCore >= 60)).ToList();
        if (cpuBusy.Count >= 10)
        {
            s.AvgCpuGhz = cpuBusy.Average(x => x.Ghz);
            // Под нагрузкой процессор должен держать частоту не ниже базовой; ниже — он её сбрасывает (перегрев или питание).
            if (cpuBaseGhz > 0) s.CpuSlowPct = 100.0 * cpuBusy.Count(x => x.Ghz < cpuBaseGhz * 0.9) / cpuBusy.Count;
        }
    }

    // ------------------------------------------------------------------ просадки
    public static List<FpsDip> FindDips(List<SessionSample> samples, Dictionary<int, string> memoryAtSample, List<DateTime> netGaps)
    {
        var dips = new List<FpsDip>();
        var window = new List<double>();
        int start = -1, last = -1;
        double baseAtStart = 0, low = double.MaxValue;

        void Close()
        {
            if (start < 0) return;
            var dip = new FpsDip
            {
                At = samples[start].At,
                Seconds = (last - start + 1) * Step,
                FromFps = baseAtStart,
                ToFps = low,
            };
            Explain(dip, samples, Math.Max(0, start - 1), last, memoryAtSample, netGaps);
            dips.Add(dip);
            start = -1; low = double.MaxValue;
        }

        for (int i = 0; i < samples.Count; i++)
        {
            double f = samples[i].Fps;
            if (double.IsNaN(f)) { if (start >= 0 && i - last > 2) Close(); continue; }

            double baseline = window.Count >= 10 ? Median(window) : double.NaN;
            bool skip = i < 20 || i >= samples.Count - 7;     // загрузка игры и выход из неё
            bool isDip = !skip && !double.IsNaN(baseline) && baseline >= 40 && f < 0.65 * baseline && baseline - f >= 15;

            if (isDip)
            {
                if (start < 0) { start = i; baseAtStart = baseline; }
                last = i; low = Math.Min(low, f);
            }
            else
            {
                if (start >= 0 && i - last > 2) Close();
                window.Add(f);                                // в «норму» просадки не попадают
                if (window.Count > 40) window.RemoveAt(0);    // минута
            }
        }
        Close();

        // Не больше 30 — самые глубокие, но в отчёте по порядку времени.
        return dips.OrderByDescending(d => d.FromFps - d.ToFps).Take(30).OrderBy(d => d.At).ToList();
    }

    private static void Explain(FpsDip dip, List<SessionSample> samples, int from, int to, Dictionary<int, string> memoryAtSample, List<DateTime> netGaps)
    {
        var range = samples.Skip(from).Take(to - from + 1).ToList();
        double Min(Func<SessionSample, double> f) => range.Select(f).Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Min();
        double Max(Func<SessionSample, double> f) => range.Select(f).Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Max();

        double ram = Min(x => x.RamAvail), pages = Max(x => x.PagesIn), disk = Max(x => x.DiskBusy);
        double core = Max(x => x.MaxCore), cpu = Max(x => x.Cpu), gpu = Max(x => x.Gpu), temp = Max(x => x.GpuTemp);
        bool heat = range.Any(x => x.LimitThermal || x.LimitHardware);
        var end = dip.At.AddSeconds(dip.Seconds);
        var gap = netGaps.FirstOrDefault(g => g >= dip.At.AddSeconds(-2) && g <= end.AddSeconds(2));

        string who = "";
        for (int i = from; i <= to; i++) if (memoryAtSample.TryGetValue(i, out var m)) { who = m; break; }

        if (ram < 0.8 || pages >= 500)
        {
            dip.Cause = "ram";
            dip.Detail = $"Свободной памяти оставалось {ram:0.0} ГБ" + (pages >= 500 ? ", Windows доставала данные из файла подкачки" : "")
                         + (who != "" ? $". Больше всего занимали: {who}." : ".");
        }
        else if (heat)
        {
            dip.Cause = "gpu-heat";
            dip.Detail = double.IsNaN(temp) ? "Видеокарта сбросила частоту из-за нагрева." : $"Видеокарта сбросила частоту из-за нагрева ({temp:0} °C).";
        }
        else if (disk >= 90)
        {
            dip.Cause = "disk";
            dip.Detail = $"Диск был занят на {disk:0} %: игра подгружала данные или фоновая программа читала диск.";
        }
        else if (core >= 95 || cpu >= 90)
        {
            dip.Cause = "cpu";
            dip.Detail = $"Самое загруженное ядро — {core:0} %, весь процессор — {cpu:0} %.";
        }
        else if (gpu >= 97)
        {
            dip.Cause = "gpu";
            dip.Detail = "Тяжёлая сцена — дым, взрывы, много игроков на экране: видеокарта работала на полную.";
        }
        else if (gap != default)
        {
            dip.Cause = "net";
            dip.Detail = "В этот момент от сервера игры не приходили данные — игра ждала сеть.";
        }
        else
        {
            dip.Cause = "unknown";
            dip.Detail = "Датчики ничего не показали. Так бывает при подгрузке участка карты, из-за событий на сервере или особенностей самой игры.";
        }
    }

    public static string CauseTitle(string cause) => cause switch
    {
        "ram" => "память",
        "gpu-heat" => "нагрев видеокарты",
        "disk" => "диск",
        "cpu" => "процессор",
        "gpu" => "видеокарта",
        "net" => "сеть",
        _ => "не видна",
    };

    private static double Median(List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        int n = s.Count;
        return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2;
    }
}
