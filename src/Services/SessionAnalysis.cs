using System.Diagnostics;

namespace PulsePC.Services;

/// <summary>Один замер во время игры (раз в 1,5 с). Хранится только в памяти, пока идёт запись.</summary>
public struct SessionSample
{
    public DateTime At;
    public double Fps, Cpu, MaxCore, Ghz, Gpu, GpuTemp, GpuClock, RamAvail, PagesIn, DiskBusy;
    public bool LimitPower, LimitThermal, LimitHardware;
    // С версии 1.5: чтение/запись самой игры (МБ/с, NaN — нет данных), занятая видеопамять (ГБ, 0 — нет данных),
    // пинг до интернета (мс, 0 — нет), игра не на переднем плане, самые заметные другие процессы.
    public double GameIoMBs, VramGb, PingMs;
    public bool Unfocused;
    public ProcShot[] Top;
}

/// <summary>Просадка FPS и её вероятная причина.</summary>
public class FpsDip
{
    public DateTime At { get; set; }
    public double Seconds { get; set; }
    public double FromFps { get; set; }
    public double ToFps { get; set; }
    /// <summary>ram, disk, cpu, gpu-heat, gpu-temp-limit, gpu-power, gpu, net, unknown.</summary>
    public string Cause { get; set; } = "unknown";
    public string Detail { get; set; } = "";
    /// <summary>Кто ещё занимал процессор и диск в момент просадки (с версии 1.5; у старых сессий — null).</summary>
    public List<Culprit> Culprits { get; set; }
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
        "browser" => L.T("Яндекс Браузер"),
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
        // Подкачка — в один и тот же момент много подгружаемых страниц и мало свободной памяти. Максимум подгрузки и минимум памяти
        // по отдельности бывают в разное время (карта грузится при 6 ГБ свободных, а 1,3 ГБ — минутой позже), и вывод по ним был ложным.
        double tight = TightRamGb(TotalRamGb());
        // Память — только пока игра была на переднем плане (и не в первый замер после возврата в неё): свёрнутую игру Windows ужимает,
        // а при возврате подгружает обратно — это не нехватка памяти в игре. Если окно игры почти всегда «не впереди» (лаунчер,
        // другой процесс окна) — признак ненадёжен, считаются все замеры.
        bool focusKnown = samples.Count(x => x.Unfocused) < 0.8 * samples.Count;
        bool InGame(int i) => !focusKnown || !(samples[i].Unfocused || (i > 0 && samples[i - 1].Unfocused));
        s.PagingSamples = Enumerable.Range(0, samples.Count)
            .Count(i => InGame(i) && !double.IsNaN(samples[i].PagesIn) && samples[i].PagesIn >= 1000 && samples[i].RamAvail > 0 && samples[i].RamAvail < tight);
        var focusedRam = Enumerable.Range(0, samples.Count).Where(i => InGame(i) && samples[i].RamAvail > 0).Select(i => samples[i].RamAvail).ToList();
        if (focusedRam.Count > 0) s.MinRamGb = focusedRam.Min();
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
            // hw_slowdown у NVIDIA включается и от перегрева (вместе с hw_thermal_slowdown): такой замер — уже нагрев, а не
            // «сигнал питания». Иначе одно событие давало бы два вывода, и второй («проверьте разъём питания») — ложный.
            s.GpuHardLimitPct = 100.0 * gpuBusy.Count(x => x.LimitHardware && !x.LimitThermal) / gpuBusy.Count;
            // При какой температуре срабатывал предел: 85 °C — перегрев, 70 °C — предел, заданный ноутбуком или программой.
            var thermalTemps = gpuBusy.Where(x => x.LimitThermal && !double.IsNaN(x.GpuTemp) && x.GpuTemp > 0).Select(x => x.GpuTemp).ToList();
            s.GpuThermalTempC = thermalTemps.Count > 0 ? Math.Round(thermalTemps.Max()) : null;
            var clocks = gpuBusy.Where(x => !double.IsNaN(x.GpuClock)).Select(x => x.GpuClock).ToList();
            if (clocks.Count > 0) s.AvgGpuClock = clocks.Average();
        }
        var cpuBusy = samples.Where(x => !double.IsNaN(x.Ghz) && (x.Cpu >= 30 || x.MaxCore >= 60)).ToList();
        if (cpuBusy.Count >= 10)
        {
            s.AvgCpuGhz = cpuBusy.Average(x => x.Ghz);
            // Под настоящей нагрузкой процессор должен держать частоту не ниже базовой; заметно ниже — он её сбрасывает (питание или
            // температура). Средняя частота «_Total» включает и слабо занятые ядра, которые штатно работают медленнее (особенно у
            // ноутбуков в сбалансированном режиме), поэтому считаем только замеры с полной загрузкой ядра и запасом 20 % от базовой.
            var hard = cpuBusy.Where(x => x.Cpu >= 50 || x.MaxCore >= 90).ToList();
            if (cpuBaseGhz > 0 && hard.Count >= 10) s.CpuSlowPct = 100.0 * hard.Count(x => x.Ghz < cpuBaseGhz * 0.8) / hard.Count;
            else if (cpuBaseGhz > 0) s.CpuSlowPct = 0;
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

        // Игра свёрнута или окно другой программы впереди (Alt+Tab, второй монитор): многие игры тогда сами режут FPS до 30–60 —
        // это не просадка. Если «не на переднем плане» почти всю сессию, окно принадлежит другому процессу (лаунчер) — тогда не учитываем.
        bool focusKnown = samples.Count(x => x.Unfocused) < 0.8 * samples.Count;
        for (int i = 0; i < samples.Count; i++)
        {
            double f = samples[i].Fps;
            // Замер сразу после возврата в игру тоже не в счёт: FPS за последнюю секунду ещё включает кадры свёрнутой игры.
            bool away = focusKnown && (samples[i].Unfocused || (i > 0 && samples[i - 1].Unfocused));
            if (double.IsNaN(f) || away) { if (start >= 0 && i - last > 2) Close(); continue; }

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

        double ram = Min(x => x.RamAvail > 0 ? x.RamAvail : double.NaN), pages = Max(x => x.PagesIn), disk = Max(x => x.DiskBusy);
        double core = Max(x => x.MaxCore), cpu = Max(x => x.Cpu), gpu = Max(x => x.Gpu), temp = Max(x => x.GpuTemp);
        // Предел температуры и аварийный сигнал питания — разные причины: второе у ноутбуков бывает от батареи или слабого адаптера
        // при любой температуре, и называть его «нагревом» нельзя.
        bool heat = range.Any(x => x.LimitThermal);
        bool brake = range.Any(x => x.LimitHardware);
        var end = dip.At.AddSeconds(dip.Seconds);
        var gap = netGaps.FirstOrDefault(g => g >= dip.At.AddSeconds(-2) && g <= end.AddSeconds(2));

        string who = "";
        for (int i = from; i <= to; i++) if (memoryAtSample.TryGetValue(i, out var m)) { who = m; break; }

        // Нехватка памяти — по одному и тому же замеру (мало свободной памяти и подкачка в один момент), а не по минимуму и максимуму
        // за всю просадку: они бывают в разные секунды.
        double total = TotalRamGb();
        int shortAt = range.FindIndex(x => MemoryShort(x.RamAvail > 0 ? x.RamAvail : double.NaN, x.PagesIn, total));
        if (shortAt >= 0) { ram = range[shortAt].RamAvail; pages = double.IsNaN(range[shortAt].PagesIn) ? 0 : range[shortAt].PagesIn; }
        if (shortAt >= 0)
        {
            dip.Cause = "ram";
            bool paged = pages >= 500;
            dip.Detail = who != ""
                ? (paged ? L.F("Свободной памяти оставалось {0:0.0} ГБ, Windows доставала данные из файла подкачки. Больше всего занимали: {1}.", ram, who)
                         : L.F("Свободной памяти оставалось {0:0.0} ГБ. Больше всего занимали: {1}.", ram, who))
                : (paged ? L.F("Свободной памяти оставалось {0:0.0} ГБ, Windows доставала данные из файла подкачки.", ram)
                         : L.F("Свободной памяти оставалось {0:0.0} ГБ.", ram));
        }
        else if (heat)
        {
            (dip.Cause, dip.Detail) = GpuTempLimit(temp, LaptopCheck.Applies);
        }
        else if (brake)
        {
            dip.Cause = "gpu-power";
            dip.Detail = PowerBrakeText(LaptopCheck.Applies);
        }
        else if (disk >= 90)
        {
            dip.Cause = "disk";
            dip.Detail = L.F("Диск был занят на {0:0} %: игра подгружала данные или фоновая программа читала диск.", disk);
        }
        else if (pages >= 500)
        {
            // Много подгружаемых страниц при достатке свободной памяти — это игра читает свои файлы (карту, текстуры), а не подкачка.
            dip.Cause = "disk";
            dip.Detail = double.IsNaN(ram)
                ? L.T("Игра подгружала данные с диска (карту, текстуры).")
                : L.F("Игра подгружала данные с диска (карту, текстуры). Памяти при этом хватало: свободно было {0:0.0} ГБ.", ram);
        }
        else if (core >= 95 || cpu >= 90)
        {
            dip.Cause = "cpu";
            // Счётчик ядер недоступен (NaN) — без «ядро — NaN %» в отчёте.
            dip.Detail = double.IsNaN(core) ? L.F("Процессор был загружен на {0:0} %.", cpu)
                                            : L.F("Самое загруженное ядро — {0:0} %, весь процессор — {1:0} %.", core, cpu);
        }
        else if (gpu >= 97)
        {
            dip.Cause = "gpu";
            dip.Detail = L.T("Тяжёлая сцена — дым, взрывы, много игроков на экране: видеокарта работала на полную.");
        }
        else if (gap != default)
        {
            dip.Cause = "net";
            dip.Detail = L.T("В этот момент от сервера игры не приходили данные — игра ждала сеть.");
        }
        else
        {
            dip.Cause = "unknown";
            dip.Detail = L.T("Датчики ничего не показали. Так бывает при подгрузке участка карты, из-за событий на сервере или особенностей самой игры.");
        }
    }

    // ------------------------------------------------------------------ общие правила выводов (с версии 1.5.1)
    /// <summary>Нагрев видеокарты с этой температуры — перегрев; ниже — она дошла до предела, заданного ноутбуком или программой.</summary>
    public const double HotGpuC = 80;

    /// <summary>Подмена объёма памяти для самопроверки (NaN — настоящий).</summary>
    internal static double TestTotalRamGb = double.NaN;
    private static double totalRamGb = double.NaN;

    /// <summary>Объём памяти этого компьютера, ГБ (для порога «памяти мало»).</summary>
    public static double TotalRamGb()
    {
        if (!double.IsNaN(TestTotalRamGb)) return TestTotalRamGb;
        if (double.IsNaN(totalRamGb))
        {
            double t = 0;
            try { t = Metrics.ReadRam().TotalGb; } catch { }
            totalRamGb = t > 0 ? t : 16;
        }
        return totalRamGb;
    }

    /// <summary>Порог «свободной памяти мало, пока идёт подкачка»: 8 % объёма, не меньше 1,5 ГБ (16 ГБ → 1,5; 32 ГБ → 2,6).</summary>
    public static double TightRamGb(double totalGb) => Math.Max(1.5, 0.08 * (totalGb > 0 ? totalGb : 16));

    /// <summary>
    /// Действительно ли не хватает памяти. Счётчик «Pages Input/sec» растёт и тогда, когда игра просто читает свои файлы
    /// (карты и текстуры подгружаются через проецирование файлов в память), поэтому сам по себе он нехватку не доказывает:
    /// нужна и мало свободной памяти. availGb ≤ 0 или NaN — нет данных, вывода нет.
    /// </summary>
    public static bool MemoryShort(double availGb, double pagesIn, double totalGb, double pagesMin = 500)
    {
        if (double.IsNaN(availGb) || availGb <= 0) return false;
        if (availGb < LowRamGb) return true;
        return !double.IsNaN(pagesIn) && pagesIn >= pagesMin && availGb < TightRamGb(totalGb);
    }

    /// <summary>
    /// Windows действительно подкачивала память во время игры (замечание к сессии и «Файл подкачки»). Новые сессии — по замерам,
    /// где подгрузка страниц и нехватка памяти совпали по времени (хотя бы два замера, 3 с). У старых сессий таких замеров нет —
    /// тогда нужно подтверждение: просадка FPS или рывки, разобранные как нехватка памяти (там совпадение проверялось).
    /// </summary>
    public static bool PagingHurt(GameSession s)
    {
        if (s == null || !(s.MinRamGb > 0) || s.MinRamGb >= TightRamGb(TotalRamGb())) return false;
        if (s.PagingSamples is int n) return n >= 2;
        return s.MaxPagesIn >= 1000 && (s.Dips?.Any(d => d?.Cause == "ram") == true || s.StutterKinds?.Any(k => k?.Kind == "memory") == true);
    }

    /// <summary>Мало свободной памяти — граница для замечания «не хватало памяти» (та же, что в <see cref="MemoryShort"/>).</summary>
    public const double LowRamGb = 0.8;

    /// <summary>Температура была не ниже limit хотя бы на 5 % графика (не один пик). Нет графика (старые сессии) — по максимуму.</summary>
    public static bool HotLong(double[] series, double limit)
    {
        var ok = series?.Where(v => !double.IsNaN(v) && v > 0).ToList();
        if (ok == null || ok.Count < 10) return true;
        return ok.Count(v => v >= limit) >= Math.Max(1, 0.05 * ok.Count);
    }

    /// <summary>Видеокарта нагрелась заметно: от 83 °C у настольного ПК, от 87 °C у ноутбука (его видеокарта рассчитана на 87 °C).</summary>
    public static double HotMaxTempC(bool laptop) => laptop ? 87 : 83;

    /// <summary>С какой температуры предел температуры — это нагрев: ноутбуки рассчитаны на 87 °C, настольные — 80 °C.</summary>
    public static double TempLimitHotC(bool laptop) => laptop ? 87 : HotGpuC;

    /// <summary>
    /// Видеокарта упёрлась в предел температуры: от 80 °C — это нагрев; ниже — предел, заданный ноутбуком (тихий режим) или
    /// программой разгона, а не перегрев, и советовать «почистить радиатор» нельзя.
    /// </summary>
    public static (string Cause, string Detail) GpuTempLimit(double tempC, bool laptop)
    {
        if (double.IsNaN(tempC) || tempC <= 0 || tempC >= TempLimitHotC(laptop))
            return ("gpu-heat", double.IsNaN(tempC) || tempC <= 0 ? L.T("Видеокарта сбросила частоту из-за нагрева.")
                                                                  : L.F("Видеокарта сбросила частоту из-за нагрева ({0:0} °C).", tempC));
        return ("gpu-temp-limit", laptop
            ? L.F("Видеокарта дошла до предела температуры, который задаёт ноутбук ({0:0} °C), и снизила частоту. Это не перегрев: в тихом и сбалансированном режиме ноутбук держит видеокарту холоднее ценой частоты.", tempC)
            : L.F("Видеокарта дошла до своего предела температуры ({0:0} °C) и снизила частоту. Это не перегрев: похоже, предел температуры занижен — например, в MSI Afterburner или фирменной программе видеокарты.", tempC));
    }

    /// <summary>Аварийное снижение частоты по сигналу питания (hw slowdown / power brake) — не нагрев.</summary>
    public static string PowerBrakeText(bool laptop) => laptop
        ? L.T("Видеокарта резко снизила частоту по сигналу питания: ноутбуку не хватило мощности (игра от батареи или не родной адаптер).")
        : L.T("Видеокарта резко снизила частоту по сигналу от блока питания или платы.");

    /// <summary>
    /// Исправить выводы в сессиях, записанных версиями до 1.5.1: просадки «память» при достатке свободной памяти (это была подгрузка
    /// с диска) и «нагрев видеокарты» при температуре ниже 80 °C или по сигналу питания. Возвращает true, если что-то поменялось.
    /// </summary>
    public static bool RepairOld(GameSession s)
    {
        if (s?.Dips == null || s.Dips.Count == 0) return false;
        // Сессия записана уже по исправленным правилам (у неё есть PagingSamples) — её выводы не трогаем: например, настоящий нагрев
        // в просадке при видеокарте ниже 50 % (такие замеры не входят в GpuThermalLimitPct) не должен стать «питанием».
        if (s.PagingSamples != null) return false;
        bool changed = false;
        bool laptop = LaptopCheck.Applies;
        bool ramWasFine = s.MinRamGb >= TightRamGb(TotalRamGb());
        double t = s.GpuThermalTempC ?? s.MaxTemp;
        bool onlyBrake = (s.GpuHardLimitPct ?? 0) > 0 && (s.GpuThermalLimitPct ?? 0) <= 0;
        foreach (var d in s.Dips)
        {
            if (d.Cause == "ram" && ramWasFine)
            {
                d.Cause = "disk";
                d.Detail = L.F("Игра подгружала данные с диска (карту, текстуры). Памяти при этом хватало: свободно было не меньше {0:0.0} ГБ.", s.MinRamGb);
                changed = true;
            }
            else if (d.Cause == "gpu-heat" && onlyBrake)
            {
                d.Cause = "gpu-power";
                d.Detail = PowerBrakeText(laptop);
                changed = true;
            }
            else if (d.Cause == "gpu-heat" && t > 0 && t < TempLimitHotC(laptop))
            {
                (d.Cause, d.Detail) = GpuTempLimit(t, laptop);
                changed = true;
            }
        }
        return changed;
    }

    public static string CauseTitle(string cause) => cause switch
    {
        "ram" => L.T("память"),
        "gpu-heat" => L.T("нагрев видеокарты"),
        "gpu-temp-limit" => L.T("предел температуры видеокарты"),
        "gpu-power" => L.T("питание видеокарты"),
        "disk" => L.T("диск"),
        "cpu" => L.T("процессор"),
        "gpu" => L.T("видеокарта"),
        "net" => L.T("сеть"),
        _ => L.T("не видна"),
    };

    // ------------------------------------------------------------------ время кадров и плавность (с версии 1.5)
    /// <summary>
    /// Плавность 0–100: от 100 отнимается 6 за каждый рывок в минуту (не больше 60) и до 40 за неровность — во сколько раз
    /// худший 1 % кадров дольше обычного кадра (до 1,5 раза — ничего, 2,5 раза и больше — все 40).
    /// </summary>
    public static double SmoothnessScore(double stuttersPerMin, double worstToNormal)
    {
        double a = Math.Min(60, 6 * Math.Max(0, stuttersPerMin));
        double b = 40 * Math.Clamp((worstToNormal - 1.5) / 1.0, 0, 1);
        return Math.Round(Math.Clamp(100 - a - b, 0, 100));
    }

    /// <summary>Как считается плавность — одной фразой для отчёта.</summary>
    public static string SmoothnessRule() =>
        L.T("Как считается: от 100 отнимается 6 за каждый рывок в минуту (не больше 60) и до 40 за неровность — если худший 1 % кадров идёт в 1,5–2,5 раза дольше обычного кадра. Рывок — кадр вдвое дольше обычного и дольше хотя бы на 8 мс.");

    /// <summary>
    /// Время кадров в отчёт: сжатый ряд для графика (одинаковые отрезки по часам от начала до конца сессии), самые заметные рывки,
    /// плавность, вид рывков. Ряд FPS пересчитывается по тем же отрезкам — графики кадров, времени кадра и отметки совпадают по времени.
    /// </summary>
    public static void ApplyFrames(GameSession s, FpsResult r, List<SessionSample> samples)
    {
        if (r == null || r.PerSecond == null || r.PerSecond.Count < 10) return;
        double total = Math.Max(1, (s.End - s.Start).TotalSeconds);
        int buckets = (int)Math.Clamp(Math.Ceiling(total), 2, 600);
        double step = total / buckets;
        var med = new double[buckets];
        var worst = new double[buckets];
        var fpsFrames = new double[buckets];
        var fpsSecs = new double[buckets];
        var medSum = new double[buckets];
        var medN = new int[buckets];
        for (int i = 0; i < buckets; i++) { worst[i] = double.NaN; }
        foreach (var sec in r.PerSecond)
        {
            double off = (sec.At - s.Start).TotalSeconds;
            if (off < -2 || off > total + 2) continue;
            int b = Math.Clamp((int)(off / step), 0, buckets - 1);
            medSum[b] += sec.P50; medN[b]++;
            worst[b] = double.IsNaN(worst[b]) ? sec.P99 : Math.Max(worst[b], sec.P99);
            fpsFrames[b] += sec.Frames; fpsSecs[b] += 1;
        }
        for (int i = 0; i < buckets; i++) med[i] = medN[i] > 0 ? Math.Round(medSum[i] / medN[i], 2) : double.NaN;
        for (int i = 0; i < buckets; i++) if (!double.IsNaN(worst[i])) worst[i] = Math.Round(worst[i], 1);

        double playMin = r.PerSecond.Count / 60.0;
        var jolts = r.Spikes.OrderByDescending(x => x.Ms).Take(300)
                     .Select(x => new FrameJolt { T = Math.Round((x.At - s.Start).TotalSeconds, 1), Ms = Math.Round(x.Ms, 1) })
                     .OrderBy(x => x.T).ToList();
        s.Frames = new FrameTimeline
        {
            BucketSec = Math.Round(step, 3), Median = med, Worst = worst, Jolts = jolts,
            MedianMs = Math.Round(r.MedianMs, 2), P99Ms = Math.Round(r.P99Ms, 2), PlayMinutes = Math.Round(playMin, 1),
        };
        // Ряд FPS по часам: 240 отрезков, где кадров не было (свёрнута, загрузка) — пропуск, а не ноль.
        int fb = Math.Min(240, buckets);
        var fps = new double[fb];
        for (int i = 0; i < fb; i++)
        {
            int from = i * buckets / fb, to = Math.Max(from + 1, (i + 1) * buckets / fb);
            double f = 0, sc = 0;
            for (int k = from; k < to; k++) { f += fpsFrames[k]; sc += fpsSecs[k]; }
            fps[i] = sc > 0 ? Math.Round(f / sc, 1) : double.NaN;
        }
        s.FpsSeries = fps;

        s.Stutters = (int)Math.Min(int.MaxValue, r.Stutters);
        s.StutterPerMin = playMin > 0 ? Math.Round(r.Stutters / playMin, 2) : null;
        double ratio = r.MedianMs > 0 ? r.P99Ms / r.MedianMs : 1;
        s.Smoothness = SmoothnessScore(s.StutterPerMin ?? 0, ratio);
        s.StutterKinds = StutterKinds(r, samples, s.VramTotalGb);
    }

    /// <summary>
    /// Вид рывков: каждый рывок сверяется с ближайшим замером датчиков. Память и видеопамять — по их нехватке, диск — по чтению самой
    /// игры или занятости диска, процессор — по загрузке ядра или фоновой программе. Остальные рывки первых 5 минут — сборка шейдеров,
    /// если в начале их заметно больше, чем потом (иначе — «не видно»).
    /// </summary>
    public static List<StutterGroup> StutterKinds(FpsResult r, List<SessionSample> samples, double vramTotalGb)
    {
        if (r.Spikes == null || r.Spikes.Count == 0) return new List<StutterGroup>();
        var times = samples.Select(x => x.At).ToList();
        DateTime first = r.PerSecond.Count > 0 ? r.PerSecond[0].At : r.Spikes[0].At;
        DateTime last = r.PerSecond.Count > 0 ? r.PerSecond[^1].At : r.Spikes[^1].At;
        double early = 300, spanSec = Math.Max(1, (last - first).TotalSeconds);
        var count = new Dictionary<string, int>();
        double diskPeak = 0, corePeak = 0, ramMin = double.MaxValue, vramMax = 0;
        int earlyOther = 0, lateOther = 0;

        foreach (var sp in r.Spikes)
        {
            string kind = "other";
            int i = Nearest(times, sp.At);
            if (i >= 0)
            {
                double ram = double.MaxValue, pages = 0, gameIo = 0, disk = 0, core = 0, cpu = 0, vram = 0, bg = 0;
                for (int k = Math.Max(0, i - 1); k <= Math.Min(samples.Count - 1, i + 1); k++)
                {
                    var x = samples[k];
                    if (Math.Abs((x.At - sp.At).TotalSeconds) > 2.5) continue;
                    if (x.RamAvail > 0) ram = Math.Min(ram, x.RamAvail);
                    if (!double.IsNaN(x.PagesIn)) pages = Math.Max(pages, x.PagesIn);
                    if (!double.IsNaN(x.GameIoMBs)) gameIo = Math.Max(gameIo, x.GameIoMBs);
                    if (!double.IsNaN(x.DiskBusy)) disk = Math.Max(disk, x.DiskBusy);
                    if (!double.IsNaN(x.MaxCore)) core = Math.Max(core, x.MaxCore);
                    if (!double.IsNaN(x.Cpu)) cpu = Math.Max(cpu, x.Cpu);
                    vram = Math.Max(vram, x.VramGb);
                    if (x.Top != null) foreach (var p in x.Top) bg = Math.Max(bg, p.Cpu);
                }
                if (MemoryShort(ram == double.MaxValue ? double.NaN : ram, pages, TotalRamGb())) { kind = "memory"; ramMin = Math.Min(ramMin, ram); }
                else if (vramTotalGb > 0 && vram >= 0.97 * vramTotalGb) { kind = "vram"; vramMax = Math.Max(vramMax, vram); }
                else if (gameIo >= 15 || disk >= 80) { kind = "disk"; diskPeak = Math.Max(diskPeak, gameIo); }
                else if (core >= 95 || cpu >= 90 || bg >= 15) { kind = "cpu"; corePeak = Math.Max(corePeak, Math.Max(core, cpu)); }
            }
            if (kind == "other")
            {
                if ((sp.At - first).TotalSeconds <= early) { kind = "early"; earlyOther++; } else lateOther++;
            }
            count[kind] = count.GetValueOrDefault(kind) + 1;
        }

        // Сборка шейдеров: рывки «без причины» сосредоточены в первых минутах — частота там хотя бы вдвое выше, чем потом.
        if (count.TryGetValue("early", out int e))
        {
            double earlyMin = Math.Min(early, spanSec) / 60, lateMin = Math.Max(0.5, (spanSec - early) / 60);
            bool shader = e >= 3 && (spanSec <= early * 1.2 ? false : e / earlyMin >= 2 * (lateOther / lateMin));
            count.Remove("early");
            if (shader) count["shader"] = e;
            else count["other"] = count.GetValueOrDefault("other") + e;
        }
        int total = r.Spikes.Count;
        return count.Where(x => x.Value > 0)
            .Select(x => new StutterGroup
            {
                Kind = x.Key, Count = x.Value,
                Value = x.Key switch
                {
                    "shader" => Math.Round(100.0 * x.Value / Math.Max(1, earlyOther + lateOther), 0),
                    "disk" => Math.Round(diskPeak, 0),
                    "cpu" => Math.Round(corePeak, 0),
                    "memory" => ramMin == double.MaxValue ? 0 : Math.Round(ramMin, 1),
                    "vram" => Math.Round(vramMax, 1),
                    _ => 0,
                },
            })
            .OrderByDescending(g => g.Kind == "other" ? -1 : g.Count).ToList();
    }

    private static int Nearest(List<DateTime> times, DateTime t)
    {
        if (times.Count == 0) return -1;
        int i = times.BinarySearch(t);
        if (i >= 0) return i;
        i = ~i;
        if (i == 0) return 0;
        if (i >= times.Count) return times.Count - 1;
        return (t - times[i - 1]) <= (times[i] - t) ? i - 1 : i;
    }

    public static string StutterTitle(string kind) => kind switch
    {
        "shader" => L.T("сборка шейдеров"),
        "disk" => L.T("подгрузка с диска"),
        "cpu" => L.T("скачки нагрузки процессора"),
        "memory" => L.T("нехватка памяти"),
        "vram" => L.T("нехватка видеопамяти"),
        _ => L.T("причина не видна"),
    };

    /// <summary>Что было видно и что сделать — по виду рывков.</summary>
    public static string StutterAdvice(StutterGroup g) => g.Kind switch
    {
        "shader" => L.F("Рывки сосредоточены в первых минутах ({0:0} % от «беспричинных»): игра собирает шейдеры под вашу видеокарту при первом показе сцен. Обычно это проходит само к следующему заходу. Не очищайте кэш шейдеров без нужды; если в настройках игры есть предварительная сборка шейдеров — дождитесь её окончания перед игрой.", g.Value),
        // Значение 0 — рывки совпали только с полной занятостью диска, чтение самой игры не измерено: без «до 0 МБ/с».
        "disk" when g.Value < 1 => L.T("В эти моменты диск был занят полностью: игра подгружала данные или диск читала другая программа. Если игра стоит на жёстком диске (HDD), перенесите её на SSD; закройте программы, которые пишут на диск во время игры (облако, торренты)."),
        "disk" => L.F("В эти моменты игра читала диск (до {0:0} МБ/с) или диск был занят полностью: игра подгружала данные. Если игра стоит на жёстком диске (HDD), перенесите её на SSD; закройте программы, которые пишут на диск во время игры (облако, торренты).", g.Value),
        "cpu" => L.F("Рывки совпали с пиками нагрузки процессора (до {0:0} %): одно ядро или весь процессор были заняты полностью. Закройте фоновые программы (кто мешал — ниже), снизьте настройки, которые нагружают процессор: дальность прорисовки, толпы, физику.", g.Value),
        "memory" => L.F("Не хватало памяти: свободно оставалось {0:0.0} ГБ, Windows доставала данные из файла подкачки. Перед игрой закройте браузер и тяжёлые программы.", g.Value),
        "vram" => L.F("Видеопамять была заполнена ({0:0.0} ГБ): видеокарта подгружала текстуры через память компьютера. Снизьте качество текстур на одну ступень.", g.Value),
        _ => L.T("Датчики в эти моменты ничего необычного не показали: так бывает из-за самой игры или сервера."),
    };

    /// <summary>«3 раза» / «3 times», «1 раз» / «once». У русского «раз» одна форма для 1 и 5 — словарь их не различит.</summary>
    public static string Times(int n) => L.En ? (n == 1 ? "once" : n + " times") : Fmt.Plural(n, "раз", "раза", "раз");

    private static double Median(List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        int n = s.Count;
        return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2;
    }
}
