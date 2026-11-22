using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PulsePC.Panels;

namespace PulsePC.Services;

/// <summary>Планка памяти так, как её видит Windows (Win32_PhysicalMemory).</summary>
public sealed class RamStick
{
    public string Slot = "", Maker = "", Part = "";
    /// <summary>BankLabel: у плат AMD в нём канал («P0 CHANNEL A»).</summary>
    public string Bank = "";
    public double SizeGb;
    /// <summary>Скорость, на которой планка работает сейчас, и паспортная (JEDEC), МТ/с.</summary>
    public int Mts, RatedMts;
    /// <summary>Число рангов (Attributes); 0 — неизвестно.</summary>
    public int Ranks;
    /// <summary>8 — DIMM (настольный), 12 — SO-DIMM (ноутбук), 0 — неизвестно.</summary>
    public int FormFactor;
}

/// <summary>Память компьютера: планки, слоты и предел платы (Win32_PhysicalMemoryArray).</summary>
public sealed class RamInfo
{
    public List<RamStick> Sticks = new();
    /// <summary>Слотов на плате (MemoryDevices); 0 — Windows не сообщает.</summary>
    public int Slots;
    /// <summary>Сколько памяти принимает плата, ГБ (MaxCapacityEx); 0 — неизвестно.</summary>
    public double MaxGb;
    public string Type = "";
    public bool Laptop;
    public bool Loaded;
    public double TotalGb => Sticks.Sum(s => s.SizeGb);
    public int FreeSlots => Slots > 0 ? Math.Max(0, Slots - Sticks.Count) : -1;
}

/// <summary>Диск, на котором стоят игры.</summary>
public sealed class GameDrive
{
    public string Letter = "", Disk = "", Kind = "";
    public List<string> Games = new();
}

/// <summary>Что следует из записанных сессий. Проценты — среднее по времени игры.</summary>
public sealed class SessionFacts
{
    public int Count;
    public double Hours;
    public DateTime From, To;
    public int RamMeasured, RamShort;
    public double RamWorst = double.NaN;
    public int GpuN, CpuN, SlowN;
    public double GpuBound = double.NaN, CpuBound = double.NaN, CpuSlow = double.NaN;
    public string TopCpuGame = "", TopGpuGame = "";
    public int VramN, VramFull;
    public double VramTotal, VramMax;
    public int RamDips, DiskDips;
}

/// <summary>Один вывод совета.</summary>
public sealed class Advice
{
    /// <summary>Память, Видеокарта, Процессор, Диск.</summary>
    public string Part = "";
    public string Title = "", Why = "", What = "", NotIf = "";
    /// <summary>Коротко для вердикта: «в 4 из 14 сессий её не хватало». Пусто — берётся заголовок.</summary>
    public string Gist = "";
    public double Score;
    public Severity Level = Severity.Info;
    /// <summary>Бесплатно: настройка, а не покупка.</summary>
    public bool Free;
    /// <summary>В норме: менять не нужно.</summary>
    public bool Fine;
}

public sealed class AdvisorInput
{
    public RamInfo Ram = new();
    public SessionFacts Facts = new();
    public List<GameDrive> Drives = new();
    public string Cpu = "", Socket = "", Gpu = "";
    public double VramGb;
}

/// <summary>
/// «Совет по апгрейду»: по записанным сессиям и паспорту железа — что из деталей ограничивало игры и что докупить в первую очередь.
/// Только чтение (WMI через PowerShell, список дисков и игр). Цены не показываются. Меньше 3 сессий — честное «данных мало».
/// </summary>
public static class UpgradeAdvisor
{
    private const string Nb = " ";
    public const int MinSessions = 3;

    // ================================================================== чтение
    public static RamInfo ReadRam()
    {
        // Проверочная подмена железа для снимков (PULSEPC_TEST_HW): выводы о памяти показываются на подставных планках.
        if (MemoryProfile.TestRam() is RamInfo fake) return fake;
        var info = new RamInfo();
        const string script =
            "$m = @(Get-CimInstance Win32_PhysicalMemory | ForEach-Object { [pscustomobject]@{ Slot=\"$($_.DeviceLocator)\"; Bank=\"$($_.BankLabel)\"; Maker=\"$($_.Manufacturer)\"; " +
            "Part=\"$($_.PartNumber)\".Trim(); Bytes=[double]$_.Capacity; Mhz=[int]$_.ConfiguredClockSpeed; Rated=[int]$_.Speed; Type=[int]$_.SMBIOSMemoryType; Form=[int]$_.FormFactor; Rank=[int]$_.Attributes } }); " +
            "$a = @(Get-CimInstance Win32_PhysicalMemoryArray | Where-Object { $_.Use -eq 3 -or $_.Use -eq $null } | ForEach-Object { [pscustomobject]@{ Slots=[int]$_.MemoryDevices; MaxKb=[double]$(if ($_.MaxCapacityEx) { $_.MaxCapacityEx } else { $_.MaxCapacity }) } }); " +
            "[pscustomobject]@{ Ram=$m; Arr=$a } | ConvertTo-Json -Compress -Depth 3";
        try
        {
            var (code, output) = Shell.PowerShell(script, 60000);
            int start = output?.IndexOf('{') ?? -1;
            if (code != 0 || start < 0) return info;
            using var doc = JsonDocument.Parse(output[start..]);
            info = ParseRam(doc.RootElement);
        }
        catch { }
        try { if (!info.Laptop && HardwareChecks.IsLaptop()) info.Laptop = true; } catch { }
        return info;
    }

    /// <summary>Разбор ответа PowerShell (отдельно — чтобы проверить на подставном JSON).</summary>
    public static RamInfo ParseRam(JsonElement r)
    {
        var info = new RamInfo();
        static string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString().Trim() : "";
        static double N(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        static List<JsonElement> Arr(JsonElement r, string n) =>
            !r.TryGetProperty(n, out var a) ? new() : a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToList() : a.ValueKind == JsonValueKind.Object ? new() { a } : new();

        var raw = Arr(r, "Ram");
        int type = raw.Select(m => (int)N(m, "Type")).DefaultIfEmpty(0).Max();
        foreach (var m in raw)
        {
            double gb = Math.Round(N(m, "Bytes") / Fmt.Gib, 1);
            if (gb <= 0) continue;
            info.Sticks.Add(new RamStick
            {
                Slot = S(m, "Slot"), Bank = S(m, "Bank"), Maker = HardwareInfo.RamMaker(S(m, "Maker")), Part = S(m, "Part"), SizeGb = gb,
                Mts = HardwareInfo.RamMts(type, (int)N(m, "Mhz"), (int)N(m, "Rated")), RatedMts = HardwareInfo.RamMts(type, (int)N(m, "Rated"), 0),
                FormFactor = (int)N(m, "Form"), Ranks = (int)N(m, "Rank") is int rk and > 0 and <= 8 ? rk : 0,
            });
        }
        info.Type = HardwareInfo.RamTypeFor(type, info.Sticks.Select(s => s.Mts).DefaultIfEmpty(0).Max(), info.Sticks.Select(s => s.Part));
        // Массивов бывает несколько (у серверных плат); слоты складываются, предел — тоже.
        var arrays = Arr(r, "Arr");
        info.Slots = arrays.Sum(a => (int)N(a, "Slots"));
        double kb = arrays.Sum(a => N(a, "MaxKb"));
        info.MaxGb = kb > 0 ? Math.Round(kb / (1024.0 * 1024.0)) : 0;
        info.Laptop = info.Sticks.Any(s => s.FormFactor == 12);
        info.Loaded = true;
        return info;
    }

    /// <summary>Диски, на которых стоят игры: буква → тип подключения (NVMe, SSD, HDD, USB).</summary>
    public static List<GameDrive> ReadDrives()
    {
        var result = new List<GameDrive>();
        try
        {
            var games = GameLibrary.Load().Where(g => !string.IsNullOrEmpty(g.InstallDir)).ToList();
            if (games.Count == 0) return result;
            var disks = Metrics.QueryDisks();
            foreach (var g in games.GroupBy(g => char.ToUpperInvariant(Path.GetPathRoot(g.InstallDir)?.FirstOrDefault() ?? '?')))
            {
                string letter = g.Key.ToString();
                var disk = disks.FirstOrDefault(d => d.Letters.Contains(letter));
                result.Add(new GameDrive
                {
                    Letter = letter, Disk = disk?.Name ?? "", Kind = disk?.Kind ?? "",
                    Games = g.Select(x => x.Title).Distinct().OrderBy(x => x).ToList(),
                });
            }
        }
        catch { }
        return result.OrderBy(d => d.Letter).ToList();
    }

    /// <summary>Всё, что нужно для совета. Долго (несколько секунд): вызывать в фоне.</summary>
    public static AdvisorInput Read(PanelHost host)
    {
        var hw = host?.Hardware();
        if (hw == null || !hw.Loaded) hw = HardwareInfo.Query();
        var gpu = hw.MainGpu;
        return new AdvisorInput
        {
            Ram = ReadRam(),
            Facts = Facts(host?.Sessions() ?? Array.Empty<GameSession>()),
            Drives = ReadDrives(),
            Cpu = HardwareInfo.ShortCpu(MemoryProfile.TestCpu ?? hw.CpuName),
            Socket = hw.SocketName ?? "",
            Gpu = gpu?.Name ?? "",
            VramGb = gpu?.VramGb ?? 0,
        };
    }

    // ================================================================== сессии
    public static SessionFacts Facts(IEnumerable<GameSession> sessions)
    {
        var list = (sessions ?? Enumerable.Empty<GameSession>()).Where(s => s.End > s.Start).ToList();
        var f = new SessionFacts { Count = list.Count };
        if (list.Count == 0) return f;
        static double W(GameSession s) => Math.Max(1, s.Duration.TotalMinutes);
        f.Hours = list.Sum(s => s.Duration.TotalHours);
        f.From = list.Min(s => s.Start); f.To = list.Max(s => s.Start);

        var ram = list.Where(s => s.MinRamGb > 0).ToList();
        f.RamMeasured = ram.Count;
        f.RamShort = ram.Count(s => s.MinRamGb < MemoryBoost.ShortGb);
        if (ram.Count > 0) f.RamWorst = ram.Min(s => s.MinRamGb);

        (int n, double avg, string top) Weighted(Func<GameSession, double?> pick)
        {
            var have = list.Where(s => pick(s).HasValue).ToList();
            if (have.Count == 0) return (0, double.NaN, "");
            double avg = have.Sum(s => pick(s).Value * W(s)) / have.Sum(W);
            string top = have.GroupBy(s => s.Game)
                .Select(g => (Game: g.Key, V: g.Sum(s => pick(s).Value * W(s)) / g.Sum(W)))
                .OrderByDescending(x => x.V).First().Game;
            return (have.Count, avg, top);
        }
        (f.GpuN, f.GpuBound, f.TopGpuGame) = Weighted(s => s.BoundGpuPct);
        (f.CpuN, f.CpuBound, f.TopCpuGame) = Weighted(s => s.BoundCpuPct);
        (f.SlowN, f.CpuSlow, _) = Weighted(s => s.CpuSlowPct);

        var vram = list.Where(s => s.VramTotalGb > 0 && s.MaxVramGb > 0).ToList();
        f.VramN = vram.Count;
        f.VramFull = vram.Count(s => s.MaxVramGb >= s.VramTotalGb * 0.95);
        if (vram.Count > 0) { f.VramTotal = vram.Max(s => s.VramTotalGb); f.VramMax = vram.Max(s => s.MaxVramGb); }

        foreach (var s in list.Where(s => s.Dips != null))
        {
            f.RamDips += s.Dips.Count(d => d.Cause == "ram");
            f.DiskDips += s.Dips.Count(d => d.Cause == "disk");
        }
        return f;
    }

    // ================================================================== память: что докупить
    /// <summary>Скорость из маркировки планки: «KF3200C16D4/8GX», «F4-3600C18-16GVK», «CMK16GX4M2B3200C16» → 3200, 3600, 3200. 0 — не видно.</summary>
    public static int PartSpeed(string part)
    {
        var m = Regex.Match(part ?? "", @"(?<!\d)(2666|2800|2933|3000|3200|3333|3466|3600|3733|3800|3866|4000|4133|4266|4400|4600|4800|5200|5600|6000|6200|6400|6600|6800|7200|7600|8000)(?!\d)");
        return m.Success ? int.Parse(m.Value, CultureInfo.InvariantCulture) : 0;
    }

    public static bool Mixed(RamInfo ram) =>
        ram.Sticks.Select(s => (s.Part.ToUpperInvariant(), s.SizeGb, s.Mts)).Distinct().Count() > 1;

    private static string Spec(RamInfo ram, double sizeGb)
    {
        // Скорость по маркировке, а не текущая: при выключенном профиле планки работают на 2133, а куплены как 3200.
        int mts = ram.Sticks.Select(s => Math.Max(s.Mts, MemoryProfile.PartSpeed(s.Part))).DefaultIfEmpty(0).Max();
        string type = ram.Type.Length > 0 ? ram.Type + (mts > 0 ? "-" + mts : "") : mts > 0 ? mts + Nb + L.T("МТ/с") : "";
        return $"{sizeGb:0}{Nb}{Fmt.GbUnit}" + (type.Length > 0 ? " " + type : "") + (ram.Laptop ? " SO-DIMM" : "");
    }

    private static double Pow2(double gb) { double p = 4; while (p < gb - 0.01) p *= 2; return p; }

    /// <summary>Что купить, чтобы памяти стало вдвое больше: та же модель в свободные слоты или замена комплекта.</summary>
    public static string RamPlan(RamInfo ram)
    {
        var s = ram.Sticks;
        int n = s.Count;
        if (n == 0) return L.T("Windows не сообщает, какие планки стоят. Тип и частоту можно посмотреть в инструкции к плате или ноутбуку.");
        double total = ram.TotalGb;
        double target = Pow2(total + 0.5);
        if (ram.MaxGb > 0) target = Math.Min(target, ram.MaxGb);
        if (target <= total + 0.01) return L.F("Плата принимает не больше {0:0} ГБ — столько уже стоит. Больше памяти на этой плате не поставить.", ram.MaxGb);

        bool uniform = !Mixed(ram);
        var first = s[0];
        string model = first.Part.Length > 0 ? $"{(first.Maker.Length > 0 ? first.Maker + " " : "")}{first.Part}" : "";
        // Про XMP — только у настольного ПК и с предупреждением о рисках (в BIOS ноутбуков профиля обычно нет).
        string after = ram.Laptop ? " " + L.T("После установки проверьте здесь, что частота памяти осталась прежней.")
            : " " + L.T("После установки проверьте здесь, что частота осталась прежней. Если раньше в BIOS был включён профиль XMP/EXPO, после смены планок его иногда приходится включить заново; если после этого начнутся сбои — выключите его.");

        if (ram.Slots <= 0)
        {
            string text = ram.Laptop
                ? L.F("Windows не сообщает, сколько слотов. Посмотрите в инструкции ноутбука, есть ли свободный: если есть — подойдёт {0}", Spec(ram, first.SizeGb))
                : L.F("Windows не сообщает, сколько слотов. Посмотрите в инструкции платы, есть ли свободный: если есть — подойдёт {0}", Spec(ram, first.SizeGb));
            if (model.Length > 0) text += L.F(", лучше та же модель {0}", model);
            text += ".";
            if (ram.Laptop) text += " " + L.T("В части ноутбуков память распаяна и не меняется.");
            return text;
        }

        if (uniform && ram.FreeSlots >= n && total * 2 <= target + 0.01)
        {
            string text = L.F("Докупите {0} × {1} в свободные слоты", n, Spec(ram, first.SizeGb));
            if (model.Length > 0) text += L.F(" — лучше той же модели {0}", model) + (n > 1 ? L.T(", их продают и комплектом") : "");
            text += n == 1
                ? L.F(". Будет {0:0} ГБ, и память заработает в двухканальном режиме.", total * 2)
                : L.F(". Будет {0:0} ГБ, двухканальный режим сохранится.", total * 2);
            if (n + n >= 4 && ram.Type.StartsWith("DDR5"))
                text += " " + L.F("Но с четырьмя планками DDR5 платы часто снижают частоту — надёжнее комплект 2 × {0} вместо нынешних.", Spec(ram, target / 2));
            else if (model.Length > 0)
                text += " " + L.T("Другая модель тоже заработает, но вся память пойдёт на частоте и задержках самой медленной планки.");
            return text + after;
        }

        string kit = Spec(ram, target / 2);
        string why = !uniform
            ? L.F("Сейчас стоят разные планки, поэтому проще заменить всё на комплект 2 × {0} ({1:0} ГБ). Нынешние планки придётся вынуть.", kit, target)
            : ram.FreeSlots == 0
                ? L.F("Свободных слотов нет, поэтому проще заменить всё на комплект 2 × {0} ({1:0} ГБ). Нынешние планки придётся вынуть.", kit, target)
                : L.F("Свободных слотов не хватает на пару, поэтому проще заменить всё на комплект 2 × {0} ({1:0} ГБ). Нынешние планки придётся вынуть.", kit, target);
        return why + after;
    }

    // ================================================================== выводы
    /// <summary>Выводы по убыванию пользы: сначала то, что стоит сделать, потом — что в норме. Чистая функция.</summary>
    public static List<Advice> Advise(AdvisorInput x)
    {
        var list = new List<Advice>();
        var ram = x.Ram;
        var f = x.Facts;
        bool enough = f.Count >= MinSessions;

        // ---------- память: бесплатное прежде покупок
        if (ram.Sticks.Count > 0)
        {
            // Частота и каналы — общий вывод MemoryProfile (тот же, что в осмотре и «Диагностике»): XMP/EXPO советуется только
            // настольному ПК, только если частота не выше штатной для процессора, и всегда с предупреждением о рисках.
            var setup = MemoryProfile.FromRam(ram, x.Cpu);
            var speed = MemoryProfile.Speed(setup);
            if (speed.AdviseProfile)
                list.Add(new Advice
                {
                    Part = "Память", Score = 80, Level = Severity.Info, Free = true,
                    Title = speed.Title, Gist = L.T("похоже, профиль XMP/EXPO выключен"),
                    Why = speed.Text, What = speed.What, NotIf = speed.Warning,
                });
            else if (speed.Kind is MemKind.AtOfficial or MemKind.LaptopBelow or MemKind.AbovePartial)
                list.Add(new Advice { Part = "Память", Fine = true, Level = Severity.Ok, Title = speed.Title, Why = speed.Text });

            var channels = MemoryProfile.Channels(setup);
            if (channels.Kind == ChannelKind.SameChannel && !setup.Mobile)
                list.Add(new Advice
                {
                    Part = "Память", Score = 90, Level = Severity.Warn, Free = true,
                    Title = channels.Title, Gist = L.T("планки стоят в одном канале"),
                    Why = channels.Text, What = channels.What,
                    NotIf = L.T("Прибавка будет небольшой, если FPS ограничивает видеокарта; больше всего выигрывают игры, которые упираются в процессор, и встроенная графика."),
                });

            if (ram.Sticks.Count == 1 && ram.Slots >= 2)
                list.Add(new Advice
                {
                    Part = "Память", Score = 88, Level = Severity.Warn,
                    Title = "Память работает в одноканальном режиме",
                    Gist = L.T("вторая такая же планка включит двухканальный режим"),
                    Why = L.F("Стоит одна планка {0}. С двумя одинаковыми планками память обменивается данными вдвое быстрее.", Spec(ram, ram.Sticks[0].SizeGb)),
                    What = RamPlan(ram),
                    NotIf = L.T("Прибавка будет небольшой, если FPS ограничивает видеокарта; больше всего выигрывают игры, которые упираются в процессор, и встроенная графика."),
                });
        }

        // ---------- память: хватает ли
        bool ramAdded = list.Any(a => a.Title.StartsWith("Память работает в одноканальном"));
        if (enough && f.RamMeasured >= MinSessions && f.RamShort > 0)
        {
            double share = (double)f.RamShort / f.RamMeasured;
            list.Add(new Advice
            {
                Part = "Память", Score = 30 + 60 * share, Level = share >= 0.25 ? Severity.Warn : Severity.Info,
                Title = share >= 0.25 ? "Памяти не хватает в играх" : "Памяти иногда не хватает",
                Gist = L.F("в {0} из {1} сессий её не хватало", f.RamShort, f.RamMeasured),
                Why = L.F("В {0} из {1} сессий доступной памяти оставалось меньше 1 ГБ (меньше всего — {2:0.0} ГБ)", f.RamShort, f.RamMeasured, f.RamWorst) +
                      (f.RamDips > 0 ? L.F(", и {0} с нехваткой памяти", Fmt.Plural(f.RamDips, "просадка FPS совпала", "просадки FPS совпали", "просадок FPS совпали")) : "") +
                      ". " + L.T("Тогда Windows уносит данные в файл подкачки, и в игре бывают рывки."),
                What = ramAdded ? L.T("Та же покупка, что выше: вторая планка решит и это.") : RamPlan(ram),
                NotIf = L.T("Не поможет, если память занимают фоновые программы: сначала попробуйте закрывать браузер перед игрой или включить «Память в игре». FPS от новой памяти не вырастет — уйдут рывки."),
            });
        }
        else if (!ramAdded && ram.TotalGb > 0 && ram.TotalGb <= 8.5)
            list.Add(new Advice
            {
                Part = "Память", Score = 60, Level = Severity.Warn,
                // Заголовок — по-русски (по нему узнаётся вывод); на экран — через L.T, по шаблону «{0} ГБ памяти — мало для новых игр».
                Title = $"{ram.TotalGb:0}{Nb}ГБ памяти — мало для новых игр",
                Why = L.T("Современным играм вместе с Windows и браузером нужно от 12–16 ГБ; при нехватке Windows уносит данные в файл подкачки."),
                What = RamPlan(ram),
                NotIf = L.T("Не нужно, если вы играете только в нетребовательные игры и рывков нет."),
            });
        else if (enough && f.RamMeasured >= MinSessions)
            list.Add(new Advice
            {
                Part = "Память", Fine = true, Level = Severity.Ok,
                Title = "Памяти в играх хватало",
                Why = L.F("Ни в одной из {0} сессий доступной памяти не оставалось меньше 1 ГБ (меньше всего — {1:0.0} ГБ).", f.RamMeasured, f.RamWorst),
            });

        if (Mixed(ram))
            list.Add(new Advice
            {
                Part = "Память", Score = 30, Level = Severity.Info,
                Title = "Стоят разные планки",
                Why = L.F("Планки отличаются {0}. Вся память работает на частоте и задержках самой медленной, а разные модели иногда сбоят вместе.", MixedHow(ram)),
                What = L.T("Если будете докупать — берите комплект одинаковых планок. Пока сбоев нет, менять ничего не нужно."),
            });

        // ---------- видеокарта
        if (enough && f.GpuN >= MinSessions && !double.IsNaN(f.GpuBound))
        {
            if (f.GpuBound >= 50)
                list.Add(new Advice
                {
                    Part = "Видеокарта", Score = 20 + 0.6 * f.GpuBound, Level = Severity.Info,
                    Title = "FPS чаще всего ограничивает видеокарта",
                    Gist = L.F("{0:0} % времени FPS упирался в неё", f.GpuBound),
                    Why = L.F("{0:0} % времени в играх видеокарта была загружена полностью", f.GpuBound) + (f.TopGpuGame.Length > 0 ? L.F(", больше всего — в {0}", f.TopGpuGame) : "") +
                          ". " + L.T("Это нормально: в хорошо собранном компьютере упор обычно именно в видеокарту."),
                    What = L.T("Сначала бесплатно: снизить самые тяжёлые настройки (тени, сглаживание, освещение) или включить DLSS / FSR / XeSS. Новая видеокарта нужна, только если FPS не устраивает и при этих настройках."),
                    NotIf = L.T("Не поможет, если FPS и так выше частоты монитора: лишние кадры не видны."),
                });
            else
                list.Add(new Advice
                {
                    Part = "Видеокарта", Fine = true, Level = Severity.Ok,
                    Title = "Видеокарта справляется",
                    Why = L.F("Полностью загружена она была {0:0} % времени: FPS ограничивало что-то другое.", f.GpuBound),
                });
        }
        if (enough && f.VramN >= MinSessions)
        {
            double share = (double)f.VramFull / f.VramN;
            if (f.VramFull >= 2 && share >= 0.3)
                list.Add(new Advice
                {
                    Part = "Видеокарта", Score = 25 + 40 * share, Level = share >= 0.5 ? Severity.Warn : Severity.Info,
                    Title = "Видеопамяти впритык",
                    Why = L.F("В {0} из {1} сессий видеопамять была занята почти вся ({2:0.0} из {3:0} ГБ).", f.VramFull, f.VramN, f.VramMax, f.VramTotal),
                    What = L.F("Снизьте качество текстур на ступень — это главный расход видеопамяти. Если будете менять видеокарту, берите с видеопамятью больше {0:0} ГБ.", f.VramTotal),
                    NotIf = L.T("Полная видеопамять сама по себе не беда: игры занимают столько, сколько им дают. Плохо, только если при этом есть рывки."),
                });
        }

        // ---------- процессор
        if (enough && f.SlowN >= MinSessions && f.CpuSlow >= 15)
            list.Add(new Advice
            {
                Part = "Процессор", Score = 70, Level = Severity.Warn, Free = true,
                Title = "Процессор сбрасывает частоту под нагрузкой",
                Why = L.F("{0:0} % времени игры процессор работал ниже своей базовой частоты.", f.CpuSlow),
                What = L.T("Чаще всего это перегрев или ограничение питания: почистите кулер от пыли, проверьте, что он плотно прижат, и что в BIOS не снижены лимиты мощности. Это дешевле нового процессора."),
                NotIf = L.T("Новый процессор с тем же кулером будет сбрасывать частоту так же."),
            });
        if (enough && f.CpuN >= MinSessions && !double.IsNaN(f.CpuBound))
        {
            if (f.CpuBound >= 35)
                list.Add(new Advice
                {
                    Part = "Процессор", Score = 25 + 0.7 * f.CpuBound, Level = Severity.Info,
                    Title = "FPS ограничивает процессор",
                    Gist = L.F("{0:0} % времени видеокарта ждала его", f.CpuBound),
                    Why = L.F("{0:0} % времени видеокарта ждала процессор", f.CpuBound) + (f.TopCpuGame.Length > 0 ? L.F(", чаще всего — в {0}", f.TopCpuGame) : "") + ".",
                    What = L.T("Сначала бесплатно: закрыть фоновые программы и посмотреть выводы про память выше — работает ли она в двух каналах и на какой частоте.") +
                           (x.Socket.Length > 0 && x.Socket != "BGA" ? " " + L.F("Новый процессор — того же разъёма {0}, если плата его поддерживает (может понадобиться обновить BIOS).", x.Socket) : ""),
                    NotIf = L.T("Не поможет, если FPS упирается в лимит кадров в игре или в драйвере."),
                });
            else if (!list.Any(a => a.Part == "Процессор"))
                list.Add(new Advice
                {
                    Part = "Процессор", Fine = true, Level = Severity.Ok,
                    Title = "Процессор не мешает",
                    Why = L.F("Видеокарта ждала его {0:0} % времени", f.CpuBound) + (f.SlowN >= MinSessions ? L.T(", частоту под нагрузкой он держал.") : "."),
                });
        }

        // ---------- диск
        var hdd = x.Drives.Where(d => d.Kind == "HDD").ToList();
        var usb = x.Drives.Where(d => d.Kind == "USB").ToList();
        if (hdd.Count > 0)
        {
            var games = hdd.SelectMany(d => d.Games).ToList();
            list.Add(new Advice
            {
                Part = "Диск", Score = 50 + Math.Min(20, f.DiskDips * 4), Level = Severity.Warn,
                Title = "Игры стоят на жёстком диске",
                Why = L.F("{0} — на {1} (жёсткий диск). Загрузки и подгрузка уровней с него в разы медленнее, чем с SSD", Names(games), string.Join(", ", hdd.Select(d => d.Letter + ":"))) +
                      (f.DiskDips > 0 ? L.F("; {0} с занятым диском.", Fmt.Plural(f.DiskDips, "просадка FPS совпала", "просадки FPS совпали", "просадок FPS совпали")) : "."),
                What = L.T("Перенесите игры на SSD, если на нём есть место (в Steam: свойства игры → «Установленные файлы» → «Переместить»). Если места нет — SSD нужного объёма; NVMe, если на плате есть свободный разъём M.2."),
                NotIf = L.T("FPS от SSD не вырастет: быстрее станут загрузки и меньше рывков при подгрузке."),
            });
        }
        if (usb.Count > 0)
            list.Add(new Advice
            {
                Part = "Диск", Score = 35, Level = Severity.Info,
                Title = "Игры стоят на внешнем диске",
                Why = L.F("{0} — на {1}, подключённом по USB.", Names(usb.SelectMany(d => d.Games).ToList()), string.Join(", ", usb.Select(d => d.Letter + ":"))),
                What = L.T("Если на внутреннем SSD есть место, перенесите игры туда: загрузки станут быстрее."),
            });
        var fast = x.Drives.Where(d => d.Kind is "NVMe" or "SSD").ToList();
        if (hdd.Count == 0 && usb.Count == 0 && fast.Count > 0)
            list.Add(new Advice
            {
                Part = "Диск", Fine = true, Level = Severity.Ok,
                Title = fast.All(d => d.Kind == "NVMe") ? "Игры на NVMe — диск менять не нужно" : "Игры на SSD — диск менять не нужно",
                Why = fast.Any(d => d.Kind == "SSD")
                    ? L.T("Разница между обычным SSD и NVMe в играх почти не видна: загрузки быстрее на секунды, FPS тот же.")
                    : L.T("Это самый быстрый тип диска для игр."),
            });

        return list.Where(a => !a.Fine).OrderByDescending(a => a.Score)
            .Concat(list.Where(a => a.Fine))
            .ToList();
    }

    private static string MixedHow(RamInfo ram)
    {
        var how = new List<string>();
        if (ram.Sticks.Select(s => s.SizeGb).Distinct().Count() > 1)
            how.Add(L.F("объёмом ({0} ГБ)", string.Join(L.Pick(" и ", " and "), ram.Sticks.Select(s => $"{s.SizeGb:0}").Distinct())));
        if (ram.Sticks.Select(s => s.Mts).Distinct().Count() > 1) how.Add(L.T("частотой"));
        if (ram.Sticks.Select(s => s.Part.ToUpperInvariant()).Distinct().Count() > 1) how.Add(L.T("моделью"));
        return how.Count > 0 ? string.Join(", ", how) : L.T("маркировкой");
    }

    private static string Names(List<string> games)
    {
        if (games.Count == 0) return L.T("Игры");
        if (games.Count <= 3) return string.Join(", ", games);
        return string.Format(L.Culture, L.Pick("{0} и ещё {1}", "{0} and {1} more"), string.Join(", ", games.Take(2)), games.Count - 2);
    }

    /// <summary>Вердикт одной фразой. Заголовки выводов — русские ключи, сюда они попадают переведёнными (TitleText).</summary>
    public static string Verdict(List<Advice> advice, SessionFacts f)
    {
        var top = advice.FirstOrDefault(a => !a.Fine);
        if (f.Count < MinSessions)
            return top != null && top.Score >= 50 ? L.F("Данных мало, но уже видно: {0}.", Lower(TitleText(top)))
                : f.Count == 0 ? L.T("Данных мало: записанных игр пока нет.")
                : L.F("Данных мало: записано {0} из нужных трёх.", Fmt.Plural(f.Count, "сессия", "сессии", "сессий"));
        if (top == null || top.Score < 35) return L.T("Апгрейд сейчас не нужен: в играх ничего серьёзно не упиралось.");
        if (top.Score < 45 && !top.Free) return L.F("Апгрейд не срочный: {0}.", top.Gist.Length > 0 ? top.Gist : Lower(TitleText(top)));
        return top.Free ? L.F("Сначала бесплатное: {0}.", Lower(TitleText(top)))
            : L.F("Больше всего пользы даст {0}: {1}.", PartWord(top.Part), top.Gist.Length > 0 ? top.Gist : Lower(TitleText(top)));
    }

    /// <summary>Заголовок вывода на языке интерфейса (сам Title остаётся русским: по нему выводы узнаются).</summary>
    public static string TitleText(Advice a) => L.T(a.Title);

    /// <summary>Деталь на языке интерфейса, со строчной буквы: «память», «видеокарта» (Part — русский ключ).</summary>
    public static string PartName(string part) => part switch
    {
        "Память" => L.T("память"), "Видеокарта" => L.T("видеокарта"), "Процессор" => L.T("процессор"), "Диск" => L.T("диск"), _ => part.ToLower(L.Culture),
    };

    private static string PartWord(string part) => part == "Диск" ? "SSD" : PartName(part);

    /// <summary>Первая буква строчная; аббревиатуры (FPS, RAM, SSD) не трогаются.</summary>
    private static string Lower(string s) =>
        string.IsNullOrEmpty(s) || s.Length > 1 && char.IsUpper(s[1]) ? s : char.ToLower(s[0], L.Culture) + s[1..];

    // ================================================================== самопроверка
    /// <summary>Выводы — на подставных данных; на этом ПК только чтение (WMI, диски, игры). PulsePC.exe --feature-test UpgradeAdvisor файл.txt</summary>
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        string fail = null;
        void Check(string what, bool ok) { sb.AppendLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) fail ??= what; }
        // Тексты советов сверяются только на русском: по-английски они приходят из словаря.
        bool en = L.En;

        Check("скорость по маркировке: Kingston, G.Skill, Corsair, Samsung",
            PartSpeed("KF3200C16D4/8GX") == 3200 && PartSpeed("F4-3600C18-16GVK") == 3600 && PartSpeed("CMK16GX4M2B3200C16") == 3200 && PartSpeed("M378A1K43DB2-CTD") == 0);

        using (var doc = JsonDocument.Parse("""
            {"Ram":[{"Slot":"DIMM 1","Maker":"Kingston","Part":"KF3200C16D4/8GX","Bytes":8589934592,"Mhz":3200,"Rated":2400,"Type":26,"Form":8},
                    {"Slot":"DIMM 2","Maker":"Kingston","Part":"KF3200C16D4/8GX","Bytes":8589934592,"Mhz":3200,"Rated":2400,"Type":26,"Form":8}],
             "Arr":{"Slots":4,"MaxKb":134217728}}
            """))
        {
            var r = ParseRam(doc.RootElement);
            Check("разбор WMI: 2 × 8 ГБ DDR4-3200, 4 слота, до 128 ГБ, не ноутбук",
                r.Sticks.Count == 2 && r.TotalGb == 16 && r.Type == "DDR4" && r.Sticks[0].Mts == 3200 && r.Slots == 4 && r.MaxGb == 128 && !r.Laptop && r.FreeSlots == 2);
        }

        RamInfo Ram(int slots, double max, string type, params (double gb, int mts, string part)[] sticks) => new()
        {
            Slots = slots, MaxGb = max, Type = type, Loaded = true,
            Sticks = sticks.Select((s, i) => new RamStick { Slot = "DIMM " + i, Maker = "Kingston", Part = s.part, SizeGb = s.gb, Mts = s.mts }).ToList(),
        };
        var two = Ram(4, 128, "DDR4", (8, 3200, "KF3200C16D4/8GX"), (8, 3200, "KF3200C16D4/8GX"));
        string plan = RamPlan(two);
        Check("2×8 при 4 слотах: докупить 2 × 8 ГБ DDR4-3200 той же модели, будет 32 ГБ — " + plan,
            plan.Contains("KF3200C16D4/8GX") && (en || plan.StartsWith($"Докупите 2 × 8{Nb}ГБ DDR4-3200") && plan.Contains($"Будет 32{Nb}ГБ")));
        string full = RamPlan(Ram(2, 64, "DDR4", (8, 3200, "X"), (8, 3200, "X")));
        Check("2×8 при 2 слотах: замена на комплект 2 × 16 ГБ — " + full, en || full.Contains($"комплект 2 × 16{Nb}ГБ DDR4-3200") && full.StartsWith("Свободных слотов нет"));
        string ddr5 = RamPlan(Ram(4, 192, "DDR5", (16, 6000, "F5-6000J3038F16G"), (16, 6000, "F5-6000J3038F16G")));
        Check("DDR5 2×16 при 4 слотах: предупреждение про четыре планки", en || ddr5.Contains("четырьмя планками DDR5") && ddr5.Contains($"2 × 32{Nb}ГБ DDR5-6000"));
        string cap = RamPlan(Ram(2, 32, "DDR4", (16, 3200, "X"), (16, 3200, "X")));
        Check("предел платы 32 ГБ уже стоит — больше не поставить", en || cap.StartsWith($"Плата принимает не больше 32{Nb}ГБ"));
        string mixedPlan = RamPlan(Ram(4, 64, "DDR4", (8, 3200, "A"), (16, 3200, "B")));
        Check("разные планки — замена комплектом", Mixed(Ram(4, 64, "DDR4", (8, 3200, "A"), (16, 3200, "B"))) && (en || mixedPlan.StartsWith("Сейчас стоят разные планки")));
        Check("неизвестно, сколько слотов — подсказка, где посмотреть", en || RamPlan(Ram(0, 0, "DDR4", (8, 3200, "X"))).StartsWith("Windows не сообщает, сколько слотов"));

        GameSession S(string game, double ram, double gpu, double cpu, double slow = 0, double vram = 5) => new()
        {
            Game = game, Start = new DateTime(2026, 11, 10, 20, 0, 0), End = new DateTime(2026, 11, 10, 21, 0, 0),
            MinRamGb = ram, BoundGpuPct = gpu, BoundCpuPct = cpu, CpuSlowPct = slow, MaxVramGb = vram, VramTotalGb = 8,
        };
        var sess = new List<GameSession> { S("PUBG", 0.8, 75, 5, vram: 7.9), S("PUBG", 0.9, 70, 8, vram: 7.8), S("Deadlock", 1.4, 20, 60), S("Deadlock", 1.6, 25, 55), S("CS2", 2.5, 40, 40) };
        var f = Facts(sess);
        Check("сессии: 5, нехватка памяти в 2, наименьший запас 0,8", f.Count == 5 && f.RamShort == 2 && f.RamMeasured == 5 && f.RamWorst == 0.8);
        Check("сессии: упор в видеокарту в среднем 46 %, процессор — 33,6 %, больше всего в Deadlock",
            Math.Abs(f.GpuBound - 46) < 0.01 && Math.Abs(f.CpuBound - 33.6) < 0.01 && f.TopCpuGame == "Deadlock" && f.TopGpuGame == "PUBG");
        Check("сессии: видеопамять почти вся — в 2 из 5", f.VramFull == 2 && f.VramN == 5);

        var input = new AdvisorInput { Ram = two, Facts = f, Socket = "LGA1700", Drives = new() { new GameDrive { Letter = "C", Kind = "NVMe", Games = new() { "PUBG" } } } };
        var adv = Advise(input);
        Check("первым — память (нехватка в 2 из 5), с планом докупки",
            adv.Count > 0 && adv[0].Part == "Память" && !adv[0].Fine && (en || adv[0].What.StartsWith("Докупите 2 × 8")));
        Check("видеокарта в норме (46 % < 50), процессор — нет вывода «упор» (33,6 % < 35) и он «в норме»",
            adv.Any(a => a.Part == "Видеокарта" && a.Fine) && adv.Any(a => a.Part == "Процессор" && a.Fine));
        Check("видеопамять: 2 из 5 = 40 % — вывод «впритык»", adv.Any(a => a.Title == "Видеопамяти впритык"));
        Check("диск NVMe — в норме, выводы «в норме» в конце",
            adv.Any(a => a.Part == "Диск" && a.Fine) && adv.SkipWhile(a => !a.Fine).All(a => a.Fine));
        Check("вердикт: больше всего пользы — память", en || Verdict(adv, f).StartsWith("Больше всего пользы даст память"));

        var fewInput = new AdvisorInput { Ram = two, Facts = Facts(sess.Take(2)), Drives = new() };
        var few = Advise(fewInput);
        Check("меньше 3 сессий — выводов по сессиям нет, вердикт «данных мало»",
            !few.Any(a => a.Part is "Видеокарта" or "Процессор") && !few.Any(a => a.Title.Contains("не хватает")) && (en || Verdict(few, fewInput.Facts).StartsWith("Данных мало")));

        var oneStick = Ram(2, 64, "DDR4", (8, 2133, "KF3200C16D4/8GX"));
        var single = Advise(new AdvisorInput { Ram = oneStick, Facts = new SessionFacts(), Cpu = "Core i5-12400F" });
        Check("XMP похоже выключен (i5-12400F, маркировка 3200, работает 2133) — бесплатно и с предупреждением о рисках",
            single.Any(a => a.Free && a.Title == "Похоже, профиль XMP/EXPO выключен" && a.NotIf.Length > 0));
        var unknownCpu = Advise(new AdvisorInput { Ram = oneStick, Facts = new SessionFacts(), Cpu = "" });
        Check("процессор неизвестен — про XMP ничего не утверждаем", !unknownCpu.Any(a => a.Title.Contains("XMP")));
        var laptopRam = Ram(2, 64, "DDR5", (16, 5200, "M425R2GA3BB0-CWMOD"), (16, 5200, "M425R2GA3BB0-CWMOD"));
        laptopRam.Laptop = true;
        laptopRam.Sticks.ForEach(s => s.RatedMts = 5600);
        var lap = Advise(new AdvisorInput { Ram = laptopRam, Facts = new SessionFacts(), Cpu = "Ryzen 9 8940HX" });
        Check("ноутбук Ryzen 9 8940HX, 5600 на 5200 — XMP не советуется, «штатно» в норме", !lap.Any(a => !a.Fine && a.Title.Contains("XMP")) && lap.Any(a => a.Fine && a.Title == "Память работает на штатной скорости процессора"));
        var sameCh = Ram(4, 128, "DDR4", (8, 3200, "X"), (8, 3200, "X"));
        sameCh.Sticks[0].Slot = "DIMM_A1"; sameCh.Sticks[1].Slot = "DIMM_A2";
        Check("две планки в одном канале — бесплатный совет переставить", Advise(new AdvisorInput { Ram = sameCh, Facts = new SessionFacts() }).Any(a => a.Free && a.Title == "Планки памяти стоят в одном канале"));
        Check("одна планка при двух слотах — одноканальный режим, докупить такую же",
            single.Any(a => a.Title.Contains("одноканальном") && (en || a.What.Contains("Докупите 1 × 8"))));
        Check("8 ГБ без сессий — не дублируется с одноканальным советом", !single.Any(a => a.Title.Contains("мало для новых игр")));

        var hddAdv = Advise(new AdvisorInput { Ram = two, Facts = new SessionFacts(), Drives = new() { new GameDrive { Letter = "D", Kind = "HDD", Games = new() { "PUBG", "Deadlock" } } } });
        Check("игры на HDD — вывод про SSD", hddAdv.Any(a => a.Part == "Диск" && !a.Fine && (en ? a.Why.StartsWith("PUBG, Deadlock") : a.Why.StartsWith("PUBG, Deadlock — на D:"))));
        var gpuHeavy = Advise(new AdvisorInput { Ram = Ram(4, 128, "DDR4", (16, 3200, "X"), (16, 3200, "X")), Facts = Facts(new[] { S("A", 3, 90, 2), S("A", 3, 85, 3), S("B", 3, 95, 1) }) });
        Check("упор в видеокарту 90 % — первый вывод про видеокарту, память в норме",
            gpuHeavy[0].Part == "Видеокарта" && gpuHeavy.Any(a => a.Part == "Память" && a.Fine));
        Check("всё в норме — вердикт «апгрейд не нужен»",
            en || Verdict(Advise(new AdvisorInput { Ram = Ram(4, 128, "DDR4", (16, 3200, "X"), (16, 3200, "X")), Facts = Facts(new[] { S("A", 3, 30, 10), S("A", 3, 35, 12), S("B", 4, 20, 9) }) }), Facts(new[] { S("A", 3, 30, 10), S("A", 3, 35, 12), S("B", 4, 20, 9) }))
                .StartsWith("Апгрейд сейчас не нужен"));

        var rare = Facts(new[] { S("A", 0.9, 30, 10), S("A", 2, 30, 10), S("A", 2, 30, 10), S("B", 3, 20, 9), S("B", 3, 20, 9) });
        var rareAdv = Advise(new AdvisorInput { Ram = two, Facts = rare });
        Check("нехватка в 1 из 5 — «памяти иногда не хватает», вердикт «не срочный»",
            rareAdv[0].Title == "Памяти иногда не хватает" && (en || Verdict(rareAdv, rare).StartsWith("Апгрейд не срочный: в 1 из 5")));

        // ---------- этот ПК, только чтение
        sb.AppendLine();
        sb.AppendLine("Что видно на этом ПК (только чтение):");
        var ram = ReadRam();
        if (!ram.Loaded) { sb.AppendLine("  планки памяти не прочитались"); fail ??= "планки памяти не прочитались"; }
        else
        {
            foreach (var s in ram.Sticks)
                sb.AppendLine($"  {s.Slot}: {s.SizeGb:0} ГБ, {s.Mts} МТ/с (паспорт {s.RatedMts}), {s.Maker} {s.Part}, форм-фактор {s.FormFactor}, по маркировке {PartSpeed(s.Part)}");
            sb.AppendLine($"  тип {ram.Type}, слотов {ram.Slots} (свободно {ram.FreeSlots}), плата принимает до {ram.MaxGb:0} ГБ, ноутбук: {(ram.Laptop ? "да" : "нет")}, разные планки: {(Mixed(ram) ? "да" : "нет")}");
            sb.AppendLine("  план: " + RamPlan(ram));
        }
        var drives = ReadDrives();
        sb.AppendLine(drives.Count == 0 ? "  установленных игр не найдено" :
            "  игры по дискам: " + string.Join("; ", drives.Select(d => $"{d.Letter}: {d.Kind} {d.Disk} — {d.Games.Count} ({string.Join(", ", d.Games.Take(4))})")));
        var real = SessionStore.Load();
        var rf = Facts(real);
        sb.AppendLine($"  сессии ({(Environment.GetEnvironmentVariable("PULSEPC_DATA") is { Length: > 0 } ? "тестовая папка" : "настоящие")}): {rf.Count}, " +
                      $"нехватка памяти {rf.RamShort} из {rf.RamMeasured}, упор в видеокарту {Pct(rf.GpuBound)} ({rf.GpuN}), в процессор {Pct(rf.CpuBound)} ({rf.CpuN}), " +
                      $"процессор ниже базовой частоты {Pct(rf.CpuSlow)}, видеопамять почти вся в {rf.VramFull} из {rf.VramN}");
        var live = Advise(new AdvisorInput { Ram = ram, Facts = rf, Drives = drives, Socket = "" });
        sb.AppendLine("  вердикт: " + Verdict(live, rf));
        foreach (var a in live) sb.AppendLine($"   {(a.Fine ? "норма" : $"{a.Score,5:0}")}  {a.Part}: {a.Title}");
        return (fail == null ? "OK" : "FAIL: " + fail) + "\n" + sb;
    }

    private static string Pct(double v) => double.IsNaN(v) ? "—" : $"{v:0}" + (L.En ? "%" : " %");
}
