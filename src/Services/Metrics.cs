using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace PulsePC.Services;

public class GpuSample
{
    public string Name = "";
    public string Driver = "";
    public double Temp = double.NaN, Load = double.NaN, MemUsed = double.NaN, MemTotal = double.NaN;
    public double Power = double.NaN, Fan = double.NaN, Clock = double.NaN, PowerLimit = double.NaN;
    /// <summary>Потребление в процентах от предела (так его сообщает Windows для видеокарт не NVIDIA); обороты вентилятора.</summary>
    public double PowerPct = double.NaN, FanRpm = double.NaN;
    /// <summary>Температура, которую драйвер считает пределом нормы (NaN — не сообщает).</summary>
    public double TempWarning = double.NaN;
    // Почему видеокарта сейчас не берёт полную частоту (по данным драйвера).
    public bool LimitPower, LimitThermal, LimitHardware;
    public string Vendor = "";
    /// <summary>Откуда показания: «nvidia-smi» или «windows» (счётчики и датчики Windows).</summary>
    public string Source = "";
    public DateTime At = DateTime.MinValue;

    /// <summary>Потребление для экрана: ватты, если драйвер их сообщает, иначе доля от предела.</summary>
    public string PowerText => !double.IsNaN(Power) ? L.F("{0:0} Вт", Power) : !double.IsNaN(PowerPct) ? L.F("{0:0} % от макс.", PowerPct) : "—";
    /// <summary>Вентилятор для экрана: проценты, иначе обороты; «—» — датчика нет.</summary>
    public string FanText => !double.IsNaN(Fan) ? Fan.ToString("0", L.Culture) + (L.En ? "%" : " %") : !double.IsNaN(FanRpm) ? L.F("{0:0} об/мин", FanRpm) : "—";
}

/// <summary>Дополнительные показания для разбора игровой сессии.</summary>
public struct ExtraSample
{
    public double MaxCore;      // самый загруженный поток процессора, %
    public double PagesIn;      // чтение подкачки с диска, страниц/с: память кончилась и Windows достаёт данные из файла подкачки
    public double DiskBusy;     // занятость дисков, %
}

public class PhysDisk
{
    public string Name = "";
    public string Media = "";
    /// <summary>Подключение: NVMe, SATA, USB, RAID, SD, SAS… (BusType из Get-PhysicalDisk; имена перечисления не переводятся). Пусто — неизвестно.</summary>
    public string BusType = "";
    /// <summary>Номер диска в Windows (как в «Управлении дисками»); −1 — неизвестен.</summary>
    public int DiskNumber = -1;
    /// <summary>Буквы разделов на этом диске: «C», «D»… Пусто — разделов с буквой нет.</summary>
    public List<string> Letters = new();
    public string Health = "";
    public double SizeGb;

    /// <summary>Тип для экрана: «NVMe», «SSD», «HDD», «USB» — по подключению, иначе по типу носителя. Пусто — неизвестно.</summary>
    public string Kind => KindOf(BusType, Media);

    /// <summary>Чистая функция (проверяется на выдуманных данных): NVMe и USB — по шине, SATA/SAS/RAID — по носителю.</summary>
    public static string KindOf(string bus, string media)
    {
        bus ??= ""; media ??= "";
        if (bus.Equals("NVMe", StringComparison.OrdinalIgnoreCase)) return "NVMe";
        if (bus.Equals("USB", StringComparison.OrdinalIgnoreCase)) return "USB";
        if (bus.Equals("SD", StringComparison.OrdinalIgnoreCase) || bus.Equals("MMC", StringComparison.OrdinalIgnoreCase)) return "SD";
        if (media.Equals("SSD", StringComparison.OrdinalIgnoreCase)) return "SSD";
        if (media.Equals("HDD", StringComparison.OrdinalIgnoreCase)) return "HDD";
        return "";
    }
    public double? Temp, Wear, Hours, ReadErrors;
    /// <summary>Предельная температура по данным диска (MSFT_StorageReliabilityCounter.TemperatureMax), °C; null — не сообщает.</summary>
    public double? TempMax;
}

/// <summary>Показатели в реальном времени: процессор, память, видеокарта, диски.</summary>
public sealed class Metrics : IDisposable
{
    // Части программы для экрана «Состояние программы».
    private const string CpuPart = "Датчики процессора", GpuPart = "Видеокарта", DiskPart = "Диски";

    // Загрузка процессора — по времени ядра (GetSystemTimes): этот способ не ломается. Счётчики Windows нужны только для частоты
    // и подробностей (ядра, подкачка, диск): раньше их создавали один раз, и после сбоя загрузка была NaN до перезапуска —
    // а с ней не писались ни сессии, ни история.
    private volatile PerformanceCounter cpuPerformance, pagesIn, diskIdle;
    private Dictionary<string, CounterSample> corePrev = new();
    private DateTime counterTry = DateTime.MinValue;
    private int counterFails, creating;
    private volatile string counterError;
    private readonly object cpuGate = new();
    private long prevIdle, prevTotal;

    private volatile bool disposed;

    public string CpuName { get; private set; } = "";
    public int CpuBaseMhz { get; private set; }
    public volatile GpuSample Gpu = new();
    public volatile List<PhysDisk> Disks = new();
    public volatile bool DisksLoaded;

    /// <summary>
    /// Идёт игра (найдена или записывается). Окно ставит это на каждом такте: во время игры не опрашиваются диски,
    /// а на ноутбуке nvidia-smi работает только в игре.
    /// </summary>
    public volatile bool GameActive;

    public void Start()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            CpuName = (key?.GetValue("ProcessorNameString") as string ?? "").Trim();
            CpuBaseMhz = Convert.ToInt32(key?.GetValue("~MHz") ?? 0);
        }
        catch (Exception ex) { Log.Error("имя процессора", ex); }

        CpuLoadFromTimes();             // первая точка отсчёта: на первом такте загрузка уже будет
        EnsureCounters();               // счётчики создаются долго, поэтому в фоне

        // Главная видеокарта — по данным ядра графики Windows: отдельная раньше встроенной, на ней идут игры.
        try { Adapters = Kmt.Enumerate(); } catch { Adapters = new List<KmtAdapter>(); }
        MainAdapter = PickMain(Adapters);
        nvidiaPath = MainAdapter == null || MainAdapter.Vendor == "NVIDIA";
        // Для проверки пути AMD/Intel на машине с NVIDIA: PULSEPC_GPU_SOURCE=windows.
        if (Environment.GetEnvironmentVariable("PULSEPC_GPU_SOURCE") == "windows") nvidiaPath = false;
        // У NVIDIA всё отдаёт nvidia-smi (с причинами снижения частоты); у AMD, Intel и прочих — счётчики и датчики Windows.
        if (nvidiaPath)
        {
            battery = BatteryInfo.Read(); batteryAt = DateTime.UtcNow;
            smiWanted = SmiWantedNow();
            if (smiWanted) { StartGpuStream(); nextSmiStart = DateTime.UtcNow.AddSeconds(5); }
            Background(GpuSupervisor, "PulsePC nvidia-smi");
        }
        Background(GpuCounterLoop, "PulsePC GPU counters");
        Background(DiskLoop, "PulsePC disks");
    }

    private static void Background(Action body, string name) => new Thread(() => body()) { IsBackground = true, Name = name }.Start();

    /// <summary>Видеокарты, которые видит Windows (без программных адаптеров), и главная из них.</summary>
    public List<KmtAdapter> Adapters { get; private set; } = new();
    public KmtAdapter MainAdapter { get; private set; }

    public static KmtAdapter PickMain(List<KmtAdapter> adapters)
    {
        if (adapters == null || adapters.Count == 0) return null;
        var pairs = adapters.Select(a => (Kmt: a, Gpu: a.ToAdapter())).ToList();
        var main = GpuVendors.PickMain(pairs.Select(p => p.Gpu));
        return pairs.FirstOrDefault(p => ReferenceEquals(p.Gpu, main)).Kmt;
    }

    /// <summary>Имя и объём видеопамяти главной видеокарты для запасного источника (из паспорта компьютера).</summary>
    public volatile string FallbackGpuName = "", FallbackGpuDriver = "";
    public double FallbackVramGb;

    // --- NVIDIA: присмотр за nvidia-smi ---
    private bool nvidiaPath;
    private volatile bool smiWanted;
    private BatteryInfo battery;
    private DateTime batteryAt = DateTime.MinValue, nextSmiStart = DateTime.MinValue;
    private TimeSpan smiBackoff = TimeSpan.FromSeconds(5);
    private long smiStartedTicks, lastSmiTicks;     // UTC, в тиках: читаются из разных потоков

    private static DateTime Utc(ref long ticks) => new(Volatile.Read(ref ticks), DateTimeKind.Utc);

    /// <summary>
    /// Ноутбук с переключаемой графикой (Optimus) или работа от батареи: nvidia-smi раз в 2 с не даёт отдельной видеокарте уснуть
    /// и сажает батарею, пока программа сидит у часов. Тогда видеокарта опрашивается только во время игры, иначе — Windows.
    /// </summary>
    private bool SmiWantedNow() => !(MainAdapter?.HybridDiscrete == true || battery.OnBattery) || GameActive;

    /// <summary>Строки от nvidia-smi идут (не старше 8 с).</summary>
    private bool SmiFresh => smiWanted && (DateTime.UtcNow - Utc(ref lastSmiTicks)).TotalSeconds < 8;

    /// <summary>nvidia-smi только что запущен и первой строки ещё нет: Windows пока не подменяет его.</summary>
    private bool SmiWarmingUp
    {
        get
        {
            var started = Utc(ref smiStartedTicks);
            return smiWanted && Utc(ref lastSmiTicks) < started && (DateTime.UtcNow - started).TotalSeconds < 8;
        }
    }

    /// <summary>
    /// Показания Windows нужны: у видеокарт не NVIDIA — всегда, у NVIDIA — пока nvidia-smi выключен или молчит.
    /// Как только nvidia-smi снова присылает строки, счётчики Windows замолкают: два источника не пишут показания по очереди.
    /// </summary>
    private bool WindowsGpuNeeded => !nvidiaPath || !smiWanted || (!SmiFresh && !SmiWarmingUp);

    /// <summary>
    /// Раз в 2 с: нужен ли сейчас nvidia-smi, жив ли он и присылает ли строки. Завершился (обновление драйвера, сбой видеокарты)
    /// или молчит дольше 10 с — перезапуск; повторные неудачи — с растущей паузой (5 с, 10 с… до 5 минут).
    /// </summary>
    private void GpuSupervisor()
    {
        while (!disposed)
        {
            try
            {
                var now = DateTime.UtcNow;
                if ((now - batteryAt).TotalSeconds >= 30) { battery = BatteryInfo.Read(); batteryAt = now; }
                bool want = SmiWantedNow();
                smiWanted = want;
                if (!want) StopSmi();
                else
                {
                    bool dead, had;
                    lock (smiGate) { had = smi != null; dead = !had || Exited(smi); }
                    var heard = Utc(ref lastSmiTicks);
                    var started = Utc(ref smiStartedTicks);
                    bool silent = !dead && (now - (heard > started ? heard : started)).TotalSeconds > 10;
                    if ((dead || silent) && now >= nextSmiStart)
                    {
                        if (had)        // не запустился вовсе — об этом уже сказал StartGpuStream
                            Health.Fail(GpuPart, dead ? L.T("nvidia-smi завершился — перезапускаю") : L.T("nvidia-smi молчит больше 10 с — перезапускаю"));
                        StopSmi();
                        StartGpuStream();
                        nextSmiStart = now + smiBackoff;
                        smiBackoff = TimeSpan.FromSeconds(Math.Min(smiBackoff.TotalSeconds * 2, 300));
                    }
                    else if (!dead && (now - heard).TotalSeconds < 5) smiBackoff = TimeSpan.FromSeconds(5);
                }
            }
            catch (Exception ex) { Log.Error("присмотр за nvidia-smi", ex); }
            for (int i = 0; i < 4 && !disposed; i++) Thread.Sleep(500);
        }
        StopSmi();
    }

    private static bool Exited(Process p)
    {
        try { return p.HasExited; } catch { return true; }
    }

    // --- Видеокарты не NVIDIA (и NVIDIA, пока nvidia-smi выключен или молчит): счётчики «GPU Engine» и «GPU Adapter Memory»
    // (Windows 10 1709+) и датчики ядра графики (WDDM 2.4+). Имена счётчиков и экземпляров в Windows не переводятся.
    // Категория читается целиком один раз за такт (ReadCategory) и считается по разнице с прошлой выборкой: раньше на каждый
    // экземпляр был свой PerformanceCounter, и каждый из них перечитывал всю категорию — сотни экземпляров давали квадрат.
    private void GpuCounterLoop()
    {
        KmtAdapter main = null;
        string luid = "", driver = "";
        uint handle = 0;
        var caps = (Max: double.NaN, Warning: double.NaN, MaxFanRpm: double.NaN);
        var prev = new Dictionary<string, CounterSample>();
        bool reopen = true;
        DateTime lastReopen = DateTime.MinValue;
        while (!disposed)
        {
            if (!WindowsGpuNeeded) { prev.Clear(); Thread.Sleep(1000); continue; }
            try
            {
                if (reopen && (DateTime.UtcNow - lastReopen).TotalSeconds >= 30)
                {
                    reopen = false; lastReopen = DateTime.UtcNow;
                    Kmt.Close(handle); handle = 0;
                    // Адаптеры перечисляются заново: после переустановки драйвера у видеокарты новый LUID, по старому счётчиков нет.
                    try { var list = Kmt.Enumerate(); if (list.Count > 0) { Adapters = list; MainAdapter = PickMain(list); } }
                    catch (Exception ex) { Log.Error("список видеокарт", ex); }
                    main = MainAdapter;
                    luid = main?.Key ?? "";              // пусто — адаптер неизвестен, берётся самый загруженный
                    handle = main == null ? 0 : Kmt.Open(main.LuidHigh, main.LuidLow);
                    caps = Kmt.Caps(handle);
                    try
                    {
                        var d = GpuDriver.ReadAdapters();
                        driver = (d.FirstOrDefault(x => main != null && x.Name.Equals(main.Name, StringComparison.OrdinalIgnoreCase)) ?? d.FirstOrDefault())?.Version ?? "";
                    }
                    catch { }
                    prev.Clear();
                }

                // Нет категории «GPU Engine» (виртуальная машина, отключённые счётчики) — загрузки нет, но датчики ядра
                // (температура, частота) могут работать: это не повод считать всю часть неисправной.
                InstanceDataCollection engines = null;
                try { engines = ReadCounter("GPU Engine", "Utilization Percentage"); }
                catch (Exception ex) { AuxFailed(ref gpuAuxLogged, GpuPart, "счётчики загрузки «GPU Engine»: " + ex.Message); }
                var next = new Dictionary<string, CounterSample>();
                var samples = new List<(string, double)>();
                if (engines != null)
                    foreach (InstanceData d in engines.Values)
                    {
                        var e = GpuVendors.ParseEngine(d.InstanceName);
                        if (!GpuVendors.IsLoadEngine(e.Type)) continue;
                        if (luid != "" && !e.Luid.Equals(luid, StringComparison.OrdinalIgnoreCase)) continue;
                        next[d.InstanceName] = d.Sample;
                        if (prev.TryGetValue(d.InstanceName, out var p))
                        {
                            try { samples.Add((d.InstanceName, CounterSample.Calculate(p, d.Sample))); } catch { }
                        }
                    }
                bool firstRound = prev.Count == 0 && next.Count > 0;
                prev = next;
                if (firstRound) { Thread.Sleep(1000); continue; }     // загрузка — по разнице двух выборок
                double load = GpuVendors.Load(samples, luid);

                const double mib = 1024 * 1024;
                double used = double.NaN, total = double.NaN;
                bool integrated = main?.Integrated == true, adapterSeen = false;
                int memInstances = 0;
                InstanceDataCollectionCollection memory = null;
                try { memory = ReadCategory("GPU Adapter Memory"); } catch { }      // нет категории — память неизвестна, загрузка всё равно есть
                var dedicated = memory != null && memory.Contains("Dedicated Usage") ? memory["Dedicated Usage"] : null;
                var shared = memory != null && memory.Contains("Shared Usage") ? memory["Shared Usage"] : null;
                if (dedicated != null)
                    foreach (InstanceData d in dedicated.Values)
                    {
                        memInstances++;
                        if (luid == "") { used = Math.Max(double.IsNaN(used) ? 0 : used, d.RawValue / mib); continue; }
                        if (adapterSeen || !GpuVendors.ParseEngine(d.InstanceName).Luid.Equals(luid, StringComparison.OrdinalIgnoreCase)) continue;
                        adapterSeen = true;
                        used = d.RawValue / mib;
                        // Встроенная графика берёт память у системы: считаем и её.
                        if (integrated && shared != null && shared.Contains(d.InstanceName)) used += shared[d.InstanceName].RawValue / mib;
                    }

                if (engines != null && luid != "" && memInstances > 0 && !adapterSeen && next.Count == 0)
                {
                    // Адаптера с таким LUID в счётчиках нет (драйвер переустановлен): «0 %» было бы неправдой и давало ложный
                    // вывод «игра упирается в процессор». Показаний нет, видеокарта ищется заново.
                    load = double.NaN; used = double.NaN;
                    reopen = true;
                    Health.Fail(GpuPart, L.T("видеокарта сменила номер в Windows (переустановка драйвера?) — ищу заново"));
                }
                else if (engines != null && double.IsNaN(load) && luid != "" && (adapterSeen || next.Count > 0)) load = 0;   // адаптер на месте, программ с графикой нет

                if (integrated && main != null) total = (main.DedicatedBytes + main.SharedBytes) / mib;
                else if (FallbackVramGb > 0) total = FallbackVramGb * 1024;
                else if (main?.DedicatedBytes > 0) total = main.DedicatedBytes / mib;

                var perf = Kmt.Read(handle, main?.Node3D ?? 0);
                double fan = double.NaN;
                if (!double.IsNaN(caps.MaxFanRpm))
                {
                    // Обороты 0 при известном пределе — вентилятор стоит (тихий режим без нагрузки).
                    double rpm = double.IsNaN(perf.FanRpm) ? 0 : perf.FanRpm;
                    fan = Math.Clamp(100 * rpm / caps.MaxFanRpm, 0, 100);
                }
                if (!WindowsGpuNeeded) continue;          // пока считали, вернулся nvidia-smi — его показания главнее
                Gpu = new GpuSample
                {
                    Name = main?.Name is string mn && mn != "" ? mn : FallbackGpuName,
                    Driver = driver != "" ? driver : FallbackGpuDriver,
                    Vendor = main?.Vendor ?? "", Source = "windows",
                    Load = load, MemUsed = used, MemTotal = total,
                    Temp = perf.TempC, TempWarning = caps.Warning,
                    Clock = perf.ClockMhz, PowerPct = perf.PowerPct, FanRpm = perf.FanRpm, Fan = fan,
                    // Причин снижения частоты Windows не сообщает; достигнутый предел температуры из драйвера — надёжный признак.
                    LimitThermal = !double.IsNaN(perf.TempC) && !double.IsNaN(caps.Max) && perf.TempC >= caps.Max,
                    At = DateTime.Now,
                };
                if (!double.IsNaN(load) || !double.IsNaN(perf.TempC)) Health.Ok(GpuPart);
                else if (engines == null) Health.Fail(GpuPart, L.T("Windows не отдаёт ни загрузку, ни датчики видеокарты"));
            }
            catch (Exception ex) { Health.Fail(GpuPart, L.F("счётчики Windows: {0}", ex.Message)); }
            Thread.Sleep(2000);
        }
        Kmt.Close(handle);
    }

    /// <summary>Вся категория счётчиков за один проход (нет категории — исключение).</summary>
    private static InstanceDataCollectionCollection ReadCategory(string category) =>
        new PerformanceCounterCategory(category).ReadCategory();

    private static InstanceDataCollection ReadCounter(string category, string counter)
    {
        var all = ReadCategory(category);
        return all != null && all.Contains(counter) ? all[counter] : null;
    }

    /// <summary>
    /// Самопроверка показаний (только чтение): видеокарты глазами Windows, главная из них; для NVIDIA — сверка датчиков Windows с nvidia-smi;
    /// загрузка по счётчикам Windows для главной видеокарты. Запуск: PulsePC.exe --feature-test Metrics файл.txt
    /// </summary>
    public static string SelfTest()
    {
        var sb = new System.Text.StringBuilder();
        string fail = null;
        using var m = new Metrics();
        m.Start();
        sb.AppendLine($"Видеокарт у Windows (без программных адаптеров): {m.Adapters.Count}");
        foreach (var a in m.Adapters)
            sb.AppendLine($"  {a.Name} [{a.Vendor}, код {a.VendorId:X4}:{a.DeviceId:X4}, {(a.Integrated ? "встроенная" : "отдельная")}, флаги 0x{a.TypeFlags:X}] " +
                          $"видеопамять {a.DedicatedBytes / Fmt.Gib:0.0} ГБ, общая {a.SharedBytes / Fmt.Gib:0.0} ГБ, двигатель 3D №{a.Node3D}, {a.Key}");
        sb.AppendLine("Главная: " + (m.MainAdapter?.Name ?? "не определена"));
        if (m.MainAdapter == null) fail = "Windows не отдала список видеокарт";

        // Ждём первое показание (nvidia-smi или счётчики Windows).
        for (int i = 0; i < 40 && m.Gpu.At == DateTime.MinValue; i++) Thread.Sleep(500);
        var g = m.Gpu;
        sb.AppendLine(g.At == DateTime.MinValue ? "Показаний видеокарты нет."
            : $"Показания ({g.Source}): {g.Name}, загрузка {g.Load:0} %, {g.Temp:0.0} °C, частота {g.Clock:0} МГц, память {g.MemUsed:0}/{g.MemTotal:0} МБ, " +
              $"потребление {g.PowerText}, вентилятор {g.FanText}, драйвер {g.Driver}");

        var w = m.ReadWindowsSensors();
        uint h = m.MainAdapter == null ? 0 : Kmt.Open(m.MainAdapter.LuidHigh, m.MainAdapter.LuidLow);
        var caps = Kmt.Caps(h);
        Kmt.Close(h);
        sb.AppendLine($"Датчики Windows (D3DKMT): {w.TempC:0.0} °C, частота {w.ClockMhz:0} МГц (предел {w.MaxClockMhz:0}), память {w.MemClockMhz:0} МГц, " +
                      $"потребление {w.PowerPct:0.0} % от предела, вентилятор {(double.IsNaN(w.FanRpm) ? "0 / нет" : w.FanRpm.ToString("0"))} об/мин; " +
                      $"предел температуры {caps.Max:0} °C, тревога {caps.Warning:0} °C, вентилятор до {caps.MaxFanRpm:0} об/мин");
        if (g.Source == "nvidia-smi" && !double.IsNaN(w.TempC) && !double.IsNaN(g.Temp))
        {
            double diff = Math.Abs(w.TempC - g.Temp);
            sb.AppendLine($"Сверка с nvidia-smi: температура {g.Temp:0.0} и {w.TempC:0.0} °C — расхождение {diff:0.0} °C");
            if (diff > 3) fail ??= $"температура Windows расходится с nvidia-smi на {diff:0.0} °C";
        }

        // Загрузка по счётчикам Windows для главной видеокарты — тот же способ, что у AMD и Intel.
        try
        {
            string luid = m.MainAdapter?.Key ?? "";
            var counters = new PerformanceCounterCategory("GPU Engine").GetInstanceNames()
                .Where(n => { var e = GpuVendors.ParseEngine(n); return GpuVendors.IsLoadEngine(e.Type) && (luid == "" || e.Luid == luid); })
                .Select(n => new PerformanceCounter("GPU Engine", "Utilization Percentage", n)).ToList();
            foreach (var c in counters) c.NextValue();
            Thread.Sleep(1000);
            var samples = counters.Select(c => { try { return (c.InstanceName, (double)c.NextValue()); } catch { return (c.InstanceName, double.NaN); } }).ToList();
            foreach (var c in counters) c.Dispose();
            var smiNow = m.Gpu;
            sb.AppendLine($"Загрузка по счётчикам Windows: {GpuVendors.Load(samples, luid):0} % ({counters.Count} счётчиков); nvidia-smi в это время: {smiNow.Load:0} %");
        }
        catch (Exception ex) { sb.AppendLine("Счётчики Windows: " + ex.Message); }

        foreach (var d in QueryDisks())
            sb.AppendLine($"Диск {d.DiskNumber}: {d.Name} · шина {d.BusType} · носитель {d.Media} → {d.Kind} · {d.SizeGb:0} ГБ · буквы {string.Join(" ", d.Letters)} · {d.Health} · " +
                          $"температура {(d.Temp?.ToString() ?? "нет данных")}");
        var bat = BatteryInfo.Read();
        sb.AppendLine($"Батарея: {(bat.HasBattery ? $"есть, {(bat.OnBattery ? "от батареи" : "от сети")}, {bat.Percent} %" : "нет (настольный компьютер или ИБП)")}");
        sb.AppendLine($"Монитор {GameTuning.Refresh()} Гц → лимит {GameTuning.FrameCap()}; производитель главной видеокарты по реестру: {GpuVendors.MainVendor}; " +
                      $"приложение настроек: {(GpuVendors.FindControlApp(GpuVendors.MainVendor) is string app && app != "" ? app : "—")}");
        sb.AppendLine();
        sb.Append(PulsePC.Services.SelfTest.GpuVendorChecks(out int bad));
        if (bad > 0) fail ??= $"не пройдено проверок на выдуманных данных: {bad}";
        return (fail == null ? "OK" : "FAIL: " + fail) + "\n" + sb;
    }

    /// <summary>Показания Windows для главной видеокарты разово (для сверки с nvidia-smi в самопроверке).</summary>
    public KmtPerf ReadWindowsSensors()
    {
        var main = MainAdapter;
        if (main == null) return new KmtPerf { TempC = double.NaN, FanRpm = double.NaN, PowerPct = double.NaN, ClockMhz = double.NaN, MemClockMhz = double.NaN, MaxClockMhz = double.NaN };
        uint h = Kmt.Open(main.LuidHigh, main.LuidLow);
        try { return Kmt.Read(h, main.Node3D); } finally { Kmt.Close(h); }
    }

    /// <summary>
    /// Загрузка процессора (%) и текущая частота (ГГц). Загрузка — по времени ядра Windows и есть всегда (NaN — только на самом
    /// первом вызове); частота — по счётчику и NaN, пока он не готов.
    /// </summary>
    public (double Load, double Ghz) ReadCpu()
    {
        double load = CpuLoadFromTimes();
        double ghz = double.NaN;
        EnsureCounters();
        var perf = cpuPerformance;
        if (perf != null && CpuBaseMhz > 0)
        {
            try { ghz = perf.NextValue() / 100.0 * CpuBaseMhz / 1000.0; CounterOk(); }
            catch (Exception ex) { CounterFailed(ex); }
        }
        if (double.IsNaN(load) && Volatile.Read(ref prevTotal) == 0) Health.Fail(CpuPart, L.T("Windows не отдаёт время работы процессора"));
        else
        {
            // Главное — загрузка — считается: часть работает. Вспомогательные счётчики (частота, подкачка, простой диска)
            // недоступны — это предупреждение в журнале (один раз на текст ошибки), а не красная метка.
            Health.Ok(CpuPart);
            AuxFailed(ref cpuAuxLogged, CpuPart, counterError);
        }
        return (load, ghz);
    }

    private string cpuAuxLogged, gpuAuxLogged;

    /// <summary>Записать в журнал отказ вспомогательного счётчика — один раз, пока текст ошибки не сменится.</summary>
    private static void AuxFailed(ref string logged, string part, string error)
    {
        if (error == null || error == logged) return;
        logged = error;
        Log.Info($"{part}: основные показания есть, но часть счётчиков недоступна — {error}");
    }

    /// <summary>Загрузка по разнице времени простоя и работы с прошлого вызова (как в Диспетчере задач до Windows 8).</summary>
    private double CpuLoadFromTimes()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user)) return double.NaN;
        lock (cpuGate)
        {
            long total = kernel + user;      // время ядра включает простой
            double load = double.NaN;
            if (prevTotal != 0)
            {
                long dt = total - prevTotal, di = idle - prevIdle;
                if (dt > 0) load = Math.Clamp(100.0 * (dt - di) / dt, 0, 100);
            }
            prevTotal = total; prevIdle = idle;
            return load;
        }
    }

    /// <summary>
    /// Создать недостающие счётчики в фоне. Не вышло (служба производительности не готова, счётчики отключены) —
    /// новая попытка через 30 с, а не никогда, как раньше.
    /// </summary>
    private void EnsureCounters()
    {
        if (disposed || (cpuPerformance != null && pagesIn != null && diskIdle != null)) return;
        if ((DateTime.UtcNow - counterTry).TotalSeconds < 30) return;
        if (Interlocked.Exchange(ref creating, 1) == 1) return;
        counterTry = DateTime.UtcNow;
        Task.Run(() =>
        {
            try
            {
                if (cpuPerformance == null)
                {
                    var perf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total");
                    perf.NextValue();
                    cpuPerformance = perf;
                }
                if (pagesIn == null)
                {
                    var pages = new PerformanceCounter("Memory", "Pages Input/sec");
                    pages.NextValue();
                    pagesIn = pages;
                }
                if (diskIdle == null)
                {
                    var idle = new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total");
                    idle.NextValue();
                    diskIdle = idle;
                }
                counterError = null;
            }
            catch (Exception ex) { counterError = ex.Message; }
            finally { Volatile.Write(ref creating, 0); }
        });
    }

    /// <summary>Счётчик прочитан: серия ошибок прервана; если все счётчики на месте — ошибки больше нет.</summary>
    private void CounterOk()
    {
        counterFails = 0;
        if (cpuPerformance != null && pagesIn != null && diskIdle != null) counterError = null;
    }

    /// <summary>Ошибка чтения счётчика. Пять подряд — счётчики пересоздаются: после сбоя службы производительности старые не оживают.</summary>
    private void CounterFailed(Exception ex)
    {
        counterError = ex.Message;
        if (++counterFails < 5) return;
        counterFails = 0;
        if (Volatile.Read(ref creating) == 1) return;
        var old = new[] { cpuPerformance, pagesIn, diskIdle };
        cpuPerformance = null; pagesIn = null; diskIdle = null;
        corePrev = new();
        foreach (var c in old) { try { c?.Dispose(); } catch { } }
        Log.Info("счётчики процессора пересоздаются после ошибок: " + ex.Message);
    }

    /// <summary>Самое загруженное ядро, подкачка и диск. NaN, пока счётчики не готовы.</summary>
    public ExtraSample ReadExtra()
    {
        var s = new ExtraSample { MaxCore = double.NaN, PagesIn = double.NaN, DiskBusy = double.NaN };
        EnsureCounters();
        try { s.MaxCore = MaxCoreLoad(); }
        catch (Exception ex) { CounterFailed(ex); }
        var pages = pagesIn;
        if (pages != null)
        {
            try { s.PagesIn = Math.Max(0, pages.NextValue()); CounterOk(); }
            catch (Exception ex) { CounterFailed(ex); }
        }
        var idle = diskIdle;
        if (idle != null)
        {
            try { s.DiskBusy = Math.Clamp(100 - idle.NextValue(), 0, 100); CounterOk(); }
            catch (Exception ex) { CounterFailed(ex); }
        }
        return s;
    }

    /// <summary>
    /// Самый загруженный поток процессора: вся категория «Processor Information» одним проходом и разница с прошлой выборкой
    /// (раньше на каждое ядро был свой счётчик, и каждый перечитывал всю категорию).
    /// </summary>
    private double MaxCoreLoad()
    {
        var util = ReadCounter("Processor Information", "% Processor Utility");
        if (util == null) return double.NaN;
        var next = new Dictionary<string, CounterSample>();
        var prev = corePrev;
        double max = double.NaN;
        foreach (InstanceData d in util.Values)
        {
            if (d.InstanceName.Contains("_Total", StringComparison.OrdinalIgnoreCase)) continue;
            next[d.InstanceName] = d.Sample;
            if (!prev.TryGetValue(d.InstanceName, out var p)) continue;
            double v = CounterSample.Calculate(p, d.Sample);
            if (!double.IsNaN(v)) max = double.IsNaN(max) ? v : Math.Max(max, v);
        }
        corePrev = next;
        if (!double.IsNaN(max)) CounterOk();
        return double.IsNaN(max) ? double.NaN : Math.Clamp(max, 0, 100);
    }

    public static (double TotalGb, double AvailGb) ReadRam()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return (0, 0);
        const double gb = 1024.0 * 1024 * 1024;
        return (m.ullTotalPhys / gb, m.ullAvailPhys / gb);
    }

    // --- Видеокарта NVIDIA: один долгоживущий nvidia-smi, который раз в 2 секунды печатает строку ---
    private readonly object smiGate = new();
    private Process smi;
    private int smiGen;             // номер запуска: строки от уже остановленного nvidia-smi отбрасываются

    private void StartGpuStream()
    {
        lock (smiGate)
        {
            int gen = ++smiGen;
            Volatile.Write(ref smiStartedTicks, DateTime.UtcNow.Ticks);
            Process p = null;
            bool started = false;
            try
            {
                var psi = new ProcessStartInfo("nvidia-smi.exe",
                    "--query-gpu=name,driver_version,temperature.gpu,utilization.gpu,memory.used,memory.total,power.draw,fan.speed,clocks.gr," +
                    "power.limit,clocks_event_reasons.sw_power_cap,clocks_event_reasons.sw_thermal_slowdown,clocks_event_reasons.hw_thermal_slowdown," +
                    "clocks_event_reasons.hw_slowdown,clocks_event_reasons.hw_power_brake_slowdown,pci.bus_id --format=csv,noheader,nounits -l 2")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (_, e) => { if (e.Data != null) ParseGpuLine(e.Data, gen); };
                p.Exited += (_, _) => { if (gen == Volatile.Read(ref smiGen) && !disposed) Log.Info("nvidia-smi завершился сам — будет перезапущен"); };
                started = p.Start();
                ChildJob.Add(p);            // программа закрылась или упала — Windows завершит и nvidia-smi
                p.BeginOutputReadLine();
                smi = p;
            }
            catch (Exception ex)
            {
                // Запущенный nvidia-smi без присмотра (сбой после Start) остался бы висеть: завершаем и освобождаем.
                if (p != null)
                {
                    smiGen++;               // его строки и «завершился сам» больше не учитываются
                    if (started) { try { if (!p.HasExited) p.Kill(true); } catch { } }
                    try { p.Dispose(); } catch { }
                }
                // Нет nvidia-smi — показания берутся у Windows, новая попытка — позже (пауза растёт).
                smi = null;
                Health.Fail(GpuPart, L.F("nvidia-smi не запускается: {0}", ex.Message));
            }
        }
    }

    private void StopSmi()
    {
        Process p;
        lock (smiGate) { p = smi; smi = null; smiGen++; }
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(true); } catch { }
        try { p.Dispose(); } catch { }
    }

    // Несколько видеокарт NVIDIA: nvidia-smi печатает по строке на каждую. Главная — та, что загружена игрой.
    private readonly Dictionary<string, GpuSample> smiCards = new();
    private readonly List<string> smiOrder = new();
    private int smiMain = -1;

    private void ParseGpuLine(string line, int gen)
    {
        if (gen != Volatile.Read(ref smiGen) || !smiWanted) return;
        var g = ParseSmiLine(line);
        if (g == null) return;
        Volatile.Write(ref lastSmiTicks, DateTime.UtcNow.Ticks);
        string bus = g.Source;          // временно: адрес на шине из последней колонки
        g.Source = "nvidia-smi";
        lock (smiCards)
        {
            if (!smiCards.ContainsKey(bus)) smiOrder.Add(bus);
            smiCards[bus] = g;
            if (smiOrder.Count == 1) Gpu = g;
            else
            {
                smiMain = GpuVendors.PickBusiest(smiOrder.Select(b => smiCards[b].Load).ToList(), smiMain);
                Gpu = smiCards[smiOrder[Math.Max(0, smiMain)]];
            }
        }
        Health.Ok(GpuPart);
    }

    /// <summary>Строка nvidia-smi → показания (null — строка не того вида). Вынесено ради проверки на выдуманных строках.</summary>
    public static GpuSample ParseSmiLine(string line)
    {
        var parts = (line ?? "").Split(',');
        if (parts.Length < 9) return null;
        static double Num(string s) =>
            double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;

        return new GpuSample
        {
            Vendor = "NVIDIA",
            Source = parts.Length > 15 ? parts[15].Trim() : "",
            Name = parts[0].Trim(),
            Driver = parts[1].Trim(),
            Temp = Num(parts[2]),
            Load = Num(parts[3]),
            MemUsed = Num(parts[4]),
            MemTotal = Num(parts[5]),
            Power = Num(parts[6]),
            Fan = Num(parts[7]),
            Clock = Num(parts[8]),
            PowerLimit = parts.Length > 9 ? Num(parts[9]) : double.NaN,
            LimitPower = Active(10),
            LimitThermal = Active(11) || Active(12),
            LimitHardware = Active(13) || Active(14),
            At = DateTime.Now,
        };
        bool Active(int i) => parts.Length > i && parts[i].Trim().Equals("Active", StringComparison.OrdinalIgnoreCase);
    }

    // --- Физические диски: состояние, температура и износ (нужны права администратора) ---
    // Опрос — PowerShell с Get-PhysicalDisk / Get-StorageReliabilityCounter: он будит уснувшие жёсткие диски и нагружает
    // процессор. Поэтому раз в 30 минут и по просьбе экрана дисков (RequestDiskRefresh), и никогда — во время игры.
    private readonly AutoResetEvent diskWake = new(false);
    private static readonly TimeSpan DiskPeriod = TimeSpan.FromMinutes(30), DiskOnDemandGap = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Обновить сведения о дисках сейчас (пользователь открыл экран дисков). Не чаще раза в 2 минуты; во время игры
    /// просьба откладывается до её конца.
    /// </summary>
    public void RequestDiskRefresh()
    {
        diskRequested = true;
        try { diskWake.Set(); } catch { }
    }

    private volatile bool diskRequested;

    /// <summary>Пустой ответ или сбой опроса дисков с правами администратора — повтор через 3 минуты, не больше стольких раз подряд.</summary>
    private static readonly TimeSpan DiskRetryGap = TimeSpan.FromMinutes(3);
    private const int DiskRetries = 3;

    private void DiskLoop()
    {
        Health.Expect(DiskPart, DiskPeriod);       // отмечается раз в 30 минут — молчание до этого срока не «нет данных»
        DateTime last = DateTime.MinValue;
        int failures = 0;                           // неудачных опросов подряд (только с правами администратора)
        while (!disposed)
        {
            try
            {
                var now = DateTime.UtcNow;
                // После неудачи (обычно — вход в систему, служба хранилища ещё не готова) следующий опрос через 3 минуты.
                var period = failures > 0 && failures <= DiskRetries ? DiskRetryGap : DiskPeriod;
                bool due = now - last >= period || (diskRequested && now - last >= DiskOnDemandGap);
                if (due && !GameActive)
                {
                    diskRequested = false;
                    last = now;
                    bool admin = Shell.IsAdmin();
                    string error = null;
                    var list = new List<PhysDisk>();
                    try { list = QueryDisks(); }
                    catch (Exception ex) { error = ex.Message; }
                    if (list.Count > 0)
                    {
                        Disks = list;
                        failures = 0;
                        DisksLoaded = true;
                        Health.Ok(DiskPart);
                    }
                    else if (!admin)
                    {
                        DisksLoaded = true;                 // без прав администратора Windows сведений не даёт — это не сбой
                        if (error != null) Log.Error("опрос дисков", new Exception(error));
                    }
                    else
                    {
                        // Разовый сбой не стирает прошлые сведения и не ставит «прочитано, но пусто»: экран показал бы
                        // «нужны права администратора» на полчаса. Повторы исчерпаны — тогда уже честно «пусто».
                        failures++;
                        if (failures > DiskRetries) DisksLoaded = true;
                        Health.Fail(DiskPart, error ?? L.T("Windows не отдала сведения о дисках"));
                    }
                }
            }
            catch (Exception ex) { Health.Fail(DiskPart, ex.Message); }
            try { diskWake.WaitOne(TimeSpan.FromSeconds(30)); } catch { break; }
        }
    }

    public static List<PhysDisk> QueryDisks()
    {
        const string script =
            "Get-PhysicalDisk | ForEach-Object { $r = $_ | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue; " +
            "$n = -1; [void][int]::TryParse(\"$($_.DeviceId)\", [ref]$n); " +
            "$l = @(if ($n -ge 0) { Get-Partition -DiskNumber $n -ErrorAction SilentlyContinue | Where-Object { \"$($_.DriveLetter)\" -match '^[A-Za-z]$' } | ForEach-Object { \"$($_.DriveLetter)\" } }); " +
            "[pscustomobject]@{ Name=$_.FriendlyName; Media=\"$($_.MediaType)\"; Bus=\"$($_.BusType)\"; Number=$n; Letters=$l; Health=\"$($_.HealthStatus)\"; SizeGb=[math]::Round($_.Size/1GB); " +
            "Temp=$r.Temperature; TempMax=$r.TemperatureMax; Wear=$r.Wear; Hours=$r.PowerOnHours; ReadErrors=$r.ReadErrorsUncorrected } } | ConvertTo-Json -Compress -Depth 3";
        var (code, output) = Shell.PowerShell(script, 60000);
        var list = new List<PhysDisk>();
        if (code != 0 || string.IsNullOrWhiteSpace(output)) return list;

        int start = output.IndexOfAny(new[] { '[', '{' });
        if (start < 0) return list;
        using var doc = JsonDocument.Parse(output[start..]);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().ToList()
            : new List<JsonElement> { doc.RootElement };

        foreach (var e in items)
        {
            static double? Opt(JsonElement el, string name) =>
                el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            static string Str(JsonElement el, string name) =>
                el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

            list.Add(new PhysDisk
            {
                Name = Str(e, "Name"),
                Media = Str(e, "Media"),
                BusType = Str(e, "Bus") is string bus && bus != "Unknown" ? bus : "",
                DiskNumber = (int)(Opt(e, "Number") ?? -1),
                Letters = e.TryGetProperty("Letters", out var ls) && ls.ValueKind == JsonValueKind.Array
                    ? ls.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString().ToUpperInvariant()).ToList()
                    : e.TryGetProperty("Letters", out var one) && one.ValueKind == JsonValueKind.String ? new List<string> { one.GetString().ToUpperInvariant() } : new List<string>(),
                Health = Str(e, "Health"),
                SizeGb = Opt(e, "SizeGb") ?? 0,
                Temp = Opt(e, "Temp"),
                Wear = Opt(e, "Wear"),
                Hours = Opt(e, "Hours"),
                ReadErrors = Opt(e, "ReadErrors"),
                TempMax = Opt(e, "TempMax"),
            });
        }
        return list;
    }

    public void Dispose()
    {
        disposed = true;
        try { diskWake.Set(); } catch { }
        StopSmi();
        try { cpuPerformance?.Dispose(); pagesIn?.Dispose(); diskIdle?.Dispose(); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    // FILETIME — 64-битное число в единицах по 100 нс.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}
