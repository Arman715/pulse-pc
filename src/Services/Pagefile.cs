using System.Globalization;
using System.Text;
using System.Text.Json;
using PulsePC.Panels;

namespace PulsePC.Services;

/// <summary>Один файл подкачки: где лежит, какой размер задан и сколько занято. Размеры — в мегабайтах.</summary>
public sealed class PagefileEntry
{
    public string Path = "";
    public string Drive = "";            // буква диска
    public bool HasSetting;              // размер задан вручную (есть запись в настройках Windows)
    public long InitialMb, MaximumMb;    // 0 и 0 — размер на этом диске выбирает Windows
    public bool InUse;                   // файл существует и используется сейчас
    public long AllocatedMb, UsedMb, PeakMb;
    public bool Temporary;               // Windows создала временный файл: заданная конфигурация не сработала
}

/// <summary>Диск с буквой: тип накопителя и свободное место.</summary>
public sealed class PagefileDrive
{
    public string Letter = "", DiskName = "";
    /// <summary>nvme, ssd, hdd или пусто (тип определить не удалось).</summary>
    public string Kind = "";
    public long SizeBytes, FreeBytes;
    public bool IsSsd => Kind is "nvme" or "ssd";
    public string KindText => Kind switch { "nvme" => "SSD NVMe", "ssd" => "SSD", "hdd" => "жёсткий диск", _ => "тип не определён" };
}

/// <summary>Конфигурация файла подкачки: то, что записывается в систему и хранится в копии «как было».</summary>
public sealed class PagefilePlan
{
    public bool Auto;
    public List<(string Path, long InitialMb, long MaximumMb)> Files = new();

    public string Describe() =>
        Auto ? "размером управляет Windows"
        : Files.Count == 0 ? "файл подкачки выключен"
        : string.Join("; ", Files.Select(f => f.Path + (f.InitialMb == 0 && f.MaximumMb == 0 ? " — размер выбирает Windows" : $" — от {Pagefile.Mb(f.InitialMb)} до {Pagefile.Mb(f.MaximumMb)}")));

    public bool Same(PagefilePlan other) =>
        other != null && Auto == other.Auto
        && (Auto || Files.Select(f => $"{f.Path.ToLowerInvariant()}|{f.InitialMb}|{f.MaximumMb}").OrderBy(x => x)
            .SequenceEqual(other.Files.Select(f => $"{f.Path.ToLowerInvariant()}|{f.InitialMb}|{f.MaximumMb}").OrderBy(x => x)));
}

/// <summary>Вердикт и рекомендация.</summary>
public sealed class PagefileAdvice
{
    public Severity Level = Severity.Ok;
    public string Verdict = "";      // одна фраза в заголовок раздела
    public string Title = "";        // что сделать
    public string Why = "";          // почему
    /// <summary>Что запишет кнопка «Применить рекомендацию»; null — менять нечего.</summary>
    public PagefilePlan Plan;
}

/// <summary>Как часто в записанных играх не хватало памяти.</summary>
public sealed class PagefileGames
{
    public int Sessions;          // сессий с показаниями памяти
    public int Analysed;          // из них с разбором подкачки
    public int Paged;             // в скольких Windows активно читала файл подкачки
    public int RamDips;           // просадок FPS из-за нехватки памяти
    public int Tight;             // в скольких свободной памяти оставалось меньше 2 ГБ
    public double TightMinGb, TightMaxGb;   // разброс «оставалось свободно» в таких сессиях
    public double MinFreeGb = double.NaN;   // меньше всего свободной памяти за все сессии
}

public sealed class PagefileState
{
    public bool Loaded;
    public string Error = "";
    public bool Auto;
    public double RamGb;
    public string SystemDrive = "";
    public long CommitLimitMb, CommitFreeMb;
    public List<PagefileEntry> Files = new();
    public List<PagefileDrive> Drives = new();
    public PagefileGames Games = new();
    public PagefileAdvice Advice = new();
    /// <summary>Честное слово о памяти: показывается, когда в играх её не хватает. Пусто — не нужно.</summary>
    public string MemoryNote = "";
    public PagefilePlan Current = new();
    public PagefilePlan Backup;      // сохранённая копия «как было»; null — её нет

    public long TotalAllocatedMb => Files.Where(f => f.InUse).Sum(f => f.AllocatedMb);
    public long TotalUsedMb => Files.Where(f => f.InUse).Sum(f => f.UsedMb);
    public long TotalPeakMb => Files.Where(f => f.InUse).Sum(f => f.PeakMb);
    public PagefileDrive DriveOf(PagefileEntry f) => Drives.FirstOrDefault(d => d.Letter.Equals(f.Drive, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Файл подкачки: чтение настройки, вердикт и обратимая запись рекомендации. Изменение вступает в силу после перезагрузки.
/// Настройка файла подкачки не прибавляет кадров в секунду — она защищает от вылетов при нехватке памяти.
/// </summary>
public static class Pagefile
{
    private const string BackupName = "pagefile-backup.txt";
    private static PanelHost host;

    private static bool migrated;

    /// <summary>
    /// Копия «как было» — в защищённой папке: по ней программа с правами администратора перезаписывает файл подкачки.
    /// folder — только для проверок в песочнице.
    /// </summary>
    private static string BackupFile(string folder = null)
    {
        if (folder != null) return Path.Combine(folder, BackupName);
        if (!migrated) { SecureStore.Migrate(BackupName); migrated = true; }
        return SecureStore.PathOf(BackupName);
    }

    private static void DeleteBackup(string folder = null)
    {
        string file = BackupFile(folder);
        foreach (var f in new[] { file, file + ".bak" }) { try { File.Delete(f); } catch { } }
    }

    /// <summary>Службе нечего делать при запуске: она ничего не меняет на время игры.</summary>
    public static void Init(PanelHost h) => host = h;

    // ------------------------------------------------------------------ чтение

    private const string ReadScript =
        "$ErrorActionPreference='SilentlyContinue'; " +
        "$cs = Get-CimInstance Win32_ComputerSystem; $os = Get-CimInstance Win32_OperatingSystem; " +
        "$set = @(Get-CimInstance Win32_PageFileSetting | ForEach-Object { [pscustomobject]@{ Name=\"$($_.Name)\"; Initial=[double]$_.InitialSize; Max=[double]$_.MaximumSize } }); " +
        "$use = @(Get-CimInstance Win32_PageFileUsage | ForEach-Object { [pscustomobject]@{ Name=\"$($_.Name)\"; Alloc=[double]$_.AllocatedBaseSize; Used=[double]$_.CurrentUsage; Peak=[double]$_.PeakUsage; Temp=[bool]$_.TempPageFile } }); " +
        // Диск с буквой → накопители под ним. Номер диска и номер накопителя совпадают не всегда, поэтому сверяем по UniqueId;
        // том на «Дисковых пространствах» раскрываем до накопителей; в крайнем случае — по номеру, если совпал и размер.
        "$pd = @(Get-PhysicalDisk); $under = @{}; " +
        "Get-Disk | ForEach-Object { $d = $_; $m = @($pd | Where-Object { $_.UniqueId -and $_.UniqueId -eq $d.UniqueId }); " +
        "if ($m.Count -eq 0 -and \"$($d.BusType)\" -eq 'Spaces') { $m = @(Get-VirtualDisk | Where-Object { $_.UniqueId -eq $d.UniqueId } | Get-PhysicalDisk) }; " +
        "if ($m.Count -eq 0) { $m = @($pd | Where-Object { \"$($_.DeviceId)\" -eq \"$($d.Number)\" -and $_.Size -eq $d.Size }) }; " +
        "$under[\"$($d.Number)\"] = $m }; " +
        "$parts = @{}; Get-Partition | Where-Object { $_.DriveLetter } | ForEach-Object { $parts[\"$($_.DriveLetter)\"] = \"$($_.DiskNumber)\" }; " +
        "$drv = @(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object { $l = $_.DeviceID.Substring(0,1); $k = $parts[$l]; $m = @(); if ($k) { $m = @($under[$k]) }; " +
        "$media = ''; $bus = ''; $name = ''; if ($m.Count -gt 0) { $ms = @($m | ForEach-Object { \"$($_.MediaType)\" }); $bs = @($m | ForEach-Object { \"$($_.BusType)\" }); " +
        "if ($ms -contains 'HDD') { $media = 'HDD' } elseif (@($ms | Where-Object { $_ -ne 'SSD' }).Count -eq 0) { $media = 'SSD' } else { $media = 'Unspecified' }; " +
        "if (@($bs | Where-Object { $_ -ne 'NVMe' }).Count -eq 0) { $bus = 'NVMe' } else { $bus = $bs[0] }; " +
        "$name = (@($m | ForEach-Object { \"$($_.FriendlyName)\".Trim() }) -join ' + ') }; " +
        "[pscustomobject]@{ Letter=$l; Size=[double]$_.Size; Free=[double]$_.FreeSpace; Disk=$name; Media=$media; Bus=$bus } }); " +
        "[pscustomobject]@{ Read=[bool]$cs; Auto=[bool]$cs.AutomaticManagedPagefile; Ram=[double]$cs.TotalPhysicalMemory; CommitKb=[double]$os.TotalVirtualMemorySize; " +
        "FreeCommitKb=[double]$os.FreeVirtualMemory; Sys=\"$env:SystemDrive\".Substring(0,1); Set=$set; Use=$use; Drives=$drv } | ConvertTo-Json -Compress -Depth 4";

    /// <summary>Прочитать всё для раздела. Только чтение, занимает 1–3 секунды: вызывать из фонового потока.</summary>
    public static PagefileState Read(IReadOnlyList<GameSession> sessions, string backupFolder = null)
    {
        var st = new PagefileState();
        try
        {
            var (code, output) = Shell.PowerShell(ReadScript, 60000);
            int start = output?.IndexOf('{') ?? -1;
            if (start < 0) { st.Error = "Windows не ответила на запрос о файле подкачки."; return st; }
            using var doc = JsonDocument.Parse(output[start..]);
            var r = doc.RootElement;
            if (!B(r, "Read")) { st.Error = "Windows не отдала сведения о памяти и файле подкачки."; return st; }

            st.Auto = B(r, "Auto");
            st.RamGb = N(r, "Ram") / Fmt.Gib;
            st.SystemDrive = S(r, "Sys");
            st.CommitLimitMb = (long)(N(r, "CommitKb") / 1024);
            st.CommitFreeMb = (long)(N(r, "FreeCommitKb") / 1024);

            foreach (var d in Arr(r, "Drives"))
            {
                string media = S(d, "Media"), bus = S(d, "Bus");
                st.Drives.Add(new PagefileDrive
                {
                    Letter = S(d, "Letter"), DiskName = S(d, "Disk"), SizeBytes = (long)N(d, "Size"), FreeBytes = (long)N(d, "Free"),
                    // Windows не всегда знает тип накопителя («Unspecified»): тогда не гадаем и советов про перенос не даём.
                    Kind = media.Equals("HDD", StringComparison.OrdinalIgnoreCase) ? "hdd"
                        : bus.Equals("NVMe", StringComparison.OrdinalIgnoreCase) ? "nvme"
                        : media.Equals("SSD", StringComparison.OrdinalIgnoreCase) ? "ssd" : "",
                });
            }

            foreach (var e in Arr(r, "Set"))
            {
                string path = S(e, "Name");
                if (path == "") continue;
                st.Files.Add(new PagefileEntry { Path = path, Drive = path[..1].ToUpperInvariant(), HasSetting = true, InitialMb = (long)N(e, "Initial"), MaximumMb = (long)N(e, "Max") });
            }
            foreach (var e in Arr(r, "Use"))
            {
                string path = S(e, "Name");
                if (path == "") continue;
                var f = st.Files.FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (f == null) st.Files.Add(f = new PagefileEntry { Path = path, Drive = path[..1].ToUpperInvariant() });
                f.InUse = true; f.AllocatedMb = (long)N(e, "Alloc"); f.UsedMb = (long)N(e, "Used"); f.PeakMb = (long)N(e, "Peak"); f.Temporary = B(e, "Temp");
            }

            st.Current = new PagefilePlan { Auto = st.Auto };
            if (!st.Auto) foreach (var f in st.Files.Where(x => x.HasSetting)) st.Current.Files.Add((f.Path, f.InitialMb, f.MaximumMb));
            st.Loaded = true;
        }
        catch (Exception ex) { st.Error = "Не удалось прочитать настройку файла подкачки: " + ex.Message; return st; }

        st.Games = Summarize(sessions);
        st.Advice = Advise(st);
        st.MemoryNote = MemoryNote(st);
        st.Backup = LoadBackup(backupFolder);
        return st;
    }

    /// <summary>Сводка по записанным играм: берутся последние 40 сессий с показаниями памяти.</summary>
    public static PagefileGames Summarize(IReadOnlyList<GameSession> sessions)
    {
        var g = new PagefileGames();
        if (sessions == null) return g;
        var tight = new List<double>();
        foreach (var s in sessions.Where(x => x != null && x.MinRamGb > 0).Take(40))
        {
            g.Sessions++;
            if (double.IsNaN(g.MinFreeGb) || s.MinRamGb < g.MinFreeGb) g.MinFreeGb = s.MinRamGb;
            if (s.MinRamGb < 2.0) tight.Add(s.MinRamGb);
            if (s.MaxPagesIn != null)
            {
                g.Analysed++;
                if (s.MaxPagesIn >= 1000) g.Paged++;       // тот же порог, что в замечаниях к сессии
            }
            if (s.Dips != null) g.RamDips += s.Dips.Count(d => d != null && d.Cause == "ram");
        }
        g.Tight = tight.Count;
        if (tight.Count > 0) { g.TightMinGb = tight.Min(); g.TightMaxGb = tight.Max(); }
        return g;
    }

    // ------------------------------------------------------------------ вердикт

    public static PagefileAdvice Advise(PagefileState st)
    {
        var a = new PagefileAdvice();
        var auto = new PagefilePlan { Auto = true };
        string ram = Ram(st.RamGb);
        var used = st.Files.Where(f => f.InUse).ToList();
        var configured = st.Files.Where(f => f.HasSetting).ToList();
        var sys = st.Drives.FirstOrDefault(d => d.Letter.Equals(st.SystemDrive, StringComparison.OrdinalIgnoreCase));

        // 1. Файла нет вовсе.
        if (!st.Auto && configured.Count == 0)
        {
            string tail = used.Count > 0 ? " Сейчас файл ещё работает: выключение вступит в силу после перезагрузки." : "";
            if (st.RamGb <= 33)
            {
                a.Level = Severity.Bad;
                a.Verdict = "Файл подкачки выключен.";
                a.Title = "Включить файл подкачки и отдать его размер Windows";
                a.Why = $"Когда память заполнится, Windows будет некуда убрать лишнее — игра или программа закроется с ошибкой «недостаточно памяти». При {ram} памяти в тяжёлых играх так и бывает.{tail}";
            }
            else
            {
                a.Level = Severity.Info;
                a.Verdict = "Файл подкачки выключен.";
                a.Title = "Надёжнее включить файл подкачки";
                a.Why = $"При {ram} памяти без файла подкачки обычно можно обойтись, но некоторые игры и программы без него закрываются с ошибкой, даже когда память свободна. С файлом, которым управляет Windows, такого не бывает.{tail}";
            }
            a.Plan = auto;
            return a;
        }

        // 2. Заданная конфигурация не сработала: Windows создала временный файл.
        if (used.Any(f => f.Temporary))
        {
            a.Level = Severity.Warn;
            a.Verdict = "Файл подкачки настроен с ошибкой.";
            a.Title = "Отдать управление файлом подкачки Windows";
            a.Why = "Windows не смогла создать файл подкачки с заданными настройками и работает на временном. Обычно причина — нет места на диске или указан диск, которого больше нет.";
            a.Plan = auto;
            return a;
        }

        var where = (used.Count > 0 ? used : configured).Select(f => st.DriveOf(f)).Where(d => d != null).Distinct().ToList();
        var onHdd = where.Where(d => d.Kind == "hdd").ToList();
        bool allSsd = where.Count > 0 && where.All(d => d.IsSsd);

        // 3. Лежит на жёстком диске, а в компьютере есть SSD.
        if (onHdd.Count > 0)
        {
            long need = (long)((st.RamGb + 8) * Fmt.Gib);       // запас под файл, размер которого выберет Windows
            var ssd = st.Drives.Where(d => d.IsSsd && d.FreeBytes >= need)
                .OrderByDescending(d => d.Letter.Equals(st.SystemDrive, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(d => d.Kind == "nvme").ThenByDescending(d => d.FreeBytes).FirstOrDefault();
            string hddName = Named(onHdd[0]);
            if (ssd != null)
            {
                bool toSystem = ssd.Letter.Equals(st.SystemDrive, StringComparison.OrdinalIgnoreCase);
                a.Level = Severity.Warn;
                a.Verdict = "Файл подкачки лежит на жёстком диске.";
                a.Title = $"Перенести файл подкачки на SSD —  :";
                a.Why = $"Сейчас файл лежит на  : ({hddName}). Когда Windows обращается к нему во время игры, жёсткий диск отвечает в десятки раз медленнее SSD — получаются долгие подвисания. " +
                        $"На  : ({Named(ssd)}) свободно {Fmt.Size(ssd.FreeBytes)} — файл поместится." +
                        (toSystem ? " Это системный диск, поэтому достаточно отдать управление Windows."
                                  : " Размер файла на новом месте выберет Windows. Учтите: когда файла нет на системном диске, Windows при сбое может не записать снимок памяти для разбора ошибки — на работу и игры это не влияет.");
                a.Plan = toSystem ? auto : new PagefilePlan { Files = { (ssd.Letter + @":\pagefile.sys", 0, 0) } };
                return a;
            }
            if (!TooSmall(st, configured, out _))
            {
                bool anySsd = st.Drives.Any(d => d.IsSsd);
                a.Level = Severity.Info;
                a.Verdict = "Файл подкачки лежит на жёстком диске.";
                a.Title = anySsd ? "На SSD не хватает места для файла подкачки" : "Быстрее сделать нельзя: SSD в компьютере нет";
                a.Why = $"Файл лежит на  : ({hddName}). " +
                        (anySsd ? $"На SSD для него нужно около {Fmt.Size(need)} свободного места. Освободите место — и рекомендация появится."
                                : "Обращения к нему во время игры — это подвисания. Настройкой это не исправить: поможет SSD или больше памяти.");
                return a;
            }
        }

        // 4. Размер задан вручную, и его не хватает.
        if (TooSmall(st, configured, out string smallWhy))
        {
            long peak = st.TotalPeakMb;
            long suggest = Math.Max(4096, (long)Math.Ceiling(peak * 1.5 / 1024.0) * 1024);
            a.Level = Severity.Warn;
            a.Verdict = "Файл подкачки задан вручную и слишком мал.";
            a.Title = "Отдать размер файла подкачки Windows";
            a.Why = smallWhy + " Если места не хватит, программы начнут закрываться с ошибкой. Windows сама увеличивает файл, когда нужно. " +
                    $"Если хотите оставить свой размер — задайте начальный не меньше {Mb(suggest)}.";
            a.Plan = auto;
            return a;
        }

        // 5. Всё как надо.
        a.Level = Severity.Ok;
        a.Verdict = "Файл подкачки настроен как надо.";
        a.Title = "Менять ничего не нужно";
        string place = where.Count == 0 ? "" : allSsd ? $" Файл лежит на SSD —  : ({Named(where[0])})." : $"  : — {where[0].KindText}.";
        a.Why = st.Auto
            ? "Размером управляет Windows: файл растёт, когда памяти не хватает, и не занимает лишнего." + place
            : $"Размер задан вручную ({st.Current.Describe()}). С включения компьютера было занято не больше {Mb(st.TotalPeakMb)} — запас есть." + place;
        return a;
    }

    /// <summary>Заданный вручную размер мал: файл заполнялся почти целиком или ему приходилось расти на ходу.</summary>
    private static bool TooSmall(PagefileState st, List<PagefileEntry> configured, out string why)
    {
        why = "";
        if (st.Auto) return false;
        var fixedFiles = configured.Where(f => f.MaximumMb > 0).ToList();
        if (fixedFiles.Count == 0 || configured.Any(f => f.MaximumMb == 0)) return false;     // хотя бы один файл растит сама Windows
        long initial = fixedFiles.Sum(f => f.InitialMb), max = fixedFiles.Sum(f => f.MaximumMb), peak = st.TotalPeakMb;
        if (peak <= 0) return false;
        string size = initial == max ? Mb(max) : $"от {Mb(initial)} до {Mb(max)}";
        if (peak >= 0.8 * max)
        {
            why = $"Размер задан вручную: {size}. С включения компьютера в нём было занято до {Mb(peak)} — почти весь.";
            return true;
        }
        if (peak > initial)
        {
            why = $"Размер задан вручную: {size}. С включения компьютера в нём было занято до {Mb(peak)} — больше начального размера, файл приходилось увеличивать на ходу, а это рывки.";
            return true;
        }
        return false;
    }

    /// <summary>Памяти в играх не хватает: файл подкачки — страховка, а не замена памяти.</summary>
    public static string MemoryNote(PagefileState st)
    {
        var g = st.Games;
        if (g.Sessions == 0 || g.Tight == 0 || g.Tight * 4 < g.Sessions) return "";       // тесно реже, чем в каждой четвёртой игре
        string range = Math.Round(g.TightMinGb, 1) == Math.Round(g.TightMaxGb, 1) ? Fmt.Gb(g.TightMinGb) : $"{g.TightMinGb:0.0}–{g.TightMaxGb:0.0} ГБ";
        string often = g.Tight == g.Sessions ? "во всех записанных играх" : $"в {g.Tight} из {Fmt.Plural(g.Sessions, "записанной игры", "записанных игр", "записанных игр")}";
        string cure = st.RamGb <= 20 ? "Настоящее решение — добавить память: 32 ГБ современным играм хватает с запасом."
            : "Настоящее решение — закрывать тяжёлые программы перед игрой или добавить память.";
        return $"В играх на этом компьютере свободной памяти оставалось {range} из {Ram(st.RamGb)} ({often}). " +
               "Файл подкачки не даёт играм закрываться с ошибкой, но память не заменяет: с диска данные читаются в сотни раз медленнее, отсюда рывки. " + cure;
    }

    // ------------------------------------------------------------------ запись (только с правами администратора)

    /// <summary>
    /// Команды PowerShell, которые записывают конфигурацию. dryRun — к каждой меняющей команде добавляется -WhatIf:
    /// Windows только сообщает, что сделала бы. Пути и размеры в текст команды не вставляются: сценарий берёт их из
    /// переменной окружения PULSE_PAGEFILES (строки «путь|начальный|максимальный», см. PlanEnv). Копия «как было» — данные,
    /// и вписать через неё свой код в команду администратора нельзя.
    /// </summary>
    public static string BuildScript(PagefilePlan plan, bool dryRun = false)
    {
        string w = dryRun ? " -WhatIf" : "";
        var sb = new StringBuilder("$ErrorActionPreference='Stop'; $cs = Get-CimInstance Win32_ComputerSystem; ");
        if (plan.Auto)
        {
            sb.Append($"Set-CimInstance -InputObject $cs -Property @{{ AutomaticManagedPagefile = $true }}{w}; ");
        }
        else
        {
            sb.Append($"if ($cs.AutomaticManagedPagefile) {{ Set-CimInstance -InputObject $cs -Property @{{ AutomaticManagedPagefile = $false }}{w} }}; ");
            sb.Append($"Get-CimInstance Win32_PageFileSetting | ForEach-Object {{ Remove-CimInstance -InputObject $_{w} }}; ");
            sb.Append("foreach ($line in @(\"$env:PULSE_PAGEFILES\" -split '\\r?\\n' | Where-Object { $_ })) { ");
            sb.Append("$p = $line.Split('|'); if ($p.Count -ne 3) { throw 'испорченная строка конфигурации' }; ");
            sb.Append("$name = $p[0]; $ini = [uint32]$p[1]; $max = [uint32]$p[2]; ");
            sb.Append($"New-CimInstance -ClassName Win32_PageFileSetting -Property @{{ Name = $name }}{w} | Out-Null; ");
            sb.Append("if ($ini -gt 0 -or $max -gt 0) { ");
            sb.Append($"Get-CimInstance Win32_PageFileSetting | Where-Object {{ $_.Name -eq $name }} | Set-CimInstance -Property @{{ InitialSize = $ini; MaximumSize = $max }}{w} }} }}; ");
        }
        sb.Append("'PULSE-DONE'");
        return sb.ToString();
    }

    /// <summary>Данные для BuildScript: файлы подкачки построчно в PULSE_PAGEFILES. Неправильный план — исключение.</summary>
    public static Dictionary<string, string> PlanEnv(PagefilePlan plan)
    {
        if (!plan.Auto && plan.Files.Any(f => !ValidFile(f.Path, f.InitialMb, f.MaximumMb)))
            throw new InvalidOperationException("в конфигурации файла подкачки недопустимый путь или размер");
        return new Dictionary<string, string>
        {
            ["PULSE_PAGEFILES"] = plan.Auto ? "" : string.Join("\n", plan.Files.Select(f => $"{f.Path}|{f.InitialMb}|{f.MaximumMb}")),
        };
    }

    private static (int Code, string Output) Write(PagefilePlan plan) => Shell.PowerShell(BuildScript(plan), 90000, PlanEnv(plan));

    /// <summary>Путь вида «C:\pagefile.sys» без управляющих символов и «|»; размеры в пределах uint32.</summary>
    internal static bool ValidFile(string path, long initialMb, long maximumMb) =>
        path != null && path.Length is >= 4 and <= 260
        && (char.IsAsciiLetter(path[0]) || path[0] == '?') && path[1] == ':' && path[2] == '\\'
        && !path.Any(c => char.IsControl(c) || c is '|' or '"' or '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B' or '`' or '$')
        && initialMb is >= 0 and <= uint.MaxValue && maximumMb is >= 0 and <= uint.MaxValue;

    /// <summary>Только конфигурация (без дисков и размеров): быстрый запрос перед записью и после неё.</summary>
    private static PagefilePlan ReadPlan()
    {
        const string script =
            "$ErrorActionPreference='Stop'; $cs = Get-CimInstance Win32_ComputerSystem; " +
            "\"auto=$([int][bool]$cs.AutomaticManagedPagefile)\"; " +
            "Get-CimInstance Win32_PageFileSetting | ForEach-Object { \"file|$($_.Name)|$([long]$_.InitialSize)|$([long]$_.MaximumSize)\" }";
        var (code, output) = Shell.PowerShell(script, 60000);
        if (code != 0) return null;
        return ParsePlan((output ?? "").Split('\n'));
    }

    public static string[] FormatPlan(PagefilePlan plan)
    {
        var lines = new List<string> { "auto=" + (plan.Auto ? "1" : "0") };
        if (!plan.Auto) lines.AddRange(plan.Files.Select(f => $"file|{f.Path}|{f.InitialMb}|{f.MaximumMb}"));
        return lines.ToArray();
    }

    /// <summary>
    /// Разбор копии. null — в строках нет признака «auto=…» (это не конфигурация) или есть испорченная строка файла:
    /// по неполной копии возвращать нельзя.
    /// </summary>
    public static PagefilePlan ParsePlan(IEnumerable<string> lines)
    {
        PagefilePlan plan = null;
        bool broken = false;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.StartsWith("auto=", StringComparison.Ordinal))
            {
                if (line != "auto=1" && line != "auto=0") { broken = true; continue; }
                plan ??= new PagefilePlan(); plan.Auto = line == "auto=1"; continue;
            }
            if (!line.StartsWith("file|", StringComparison.Ordinal)) continue;
            var p = line.Split('|');
            if (p.Length == 4 && long.TryParse(p[2], out long initial) && long.TryParse(p[3], out long max) && ValidFile(p[1], initial, max))
            {
                plan ??= new PagefilePlan();
                plan.Files.Add((p[1], initial, max));
            }
            else broken = true;
        }
        if (broken) return null;
        if (plan != null && plan.Auto) plan.Files.Clear();
        return plan;
    }

    public static PagefilePlan LoadBackup(string folder = null)
    {
        try { return AtomicFile.Read(BackupFile(folder), t => ParsePlan(t.Split('\n'))); }
        catch { return null; }
    }

    /// <summary>Сохранить копию «как было». Первая копия повторным применением не затирается.</summary>
    public static bool SaveBackup(PagefilePlan current, string folder = null)
    {
        string file = BackupFile(folder);
        if (LoadBackup(folder) != null) return true;
        if (!current.Auto && current.Files.Any(f => !ValidFile(f.Path, f.InitialMb, f.MaximumMb))) return false;
        AtomicFile.WriteAllText(file, string.Join("\n", FormatPlan(current)) + "\n");
        return LoadBackup(folder) != null;
    }

    /// <summary>Записать рекомендацию. Прежняя конфигурация сохраняется в копию до записи.</summary>
    public static (bool Ok, string Message) Apply(PagefilePlan plan)
    {
        if (plan == null) return (false, "Менять нечего.");
        if (!Shell.IsAdmin()) return (false, "Нужны права администратора.");
        try
        {
            var current = ReadPlan();
            if (current == null) return (false, "Не удалось прочитать текущую настройку, поэтому ничего не изменено.");
            if (current.Same(plan)) return (true, "Эта настройка уже записана. Если компьютер после этого не перезагружался — перезагрузите его.");
            bool hadBackup = LoadBackup() != null;
            if (!SaveBackup(current)) return (false, "Не удалось сохранить копию прежней настройки, поэтому ничего не изменено.");

            var (code, output) = Write(plan);
            var now = ReadPlan();
            if (code == 0 && (output ?? "").Contains("PULSE-DONE") && now != null && now.Same(plan))
                return (true, $"Записано: {plan.Describe()}. Изменение вступит в силу после перезагрузки компьютера. Прежняя настройка сохранена — её вернёт кнопка «Вернуть как было».");

            // Запись оборвалась на полпути: нельзя оставлять систему с наполовину записанной настройкой — возвращаем прежнюю сразу.
            string reason = Short(output);
            if (now != null && now.Same(current))
            {
                if (!hadBackup) DeleteBackup();
                return (false, "Windows не приняла настройку: " + reason + " Ничего не изменилось.");
            }
            Write(current);
            var after = ReadPlan();
            if (after != null && after.Same(current))
            {
                if (!hadBackup) DeleteBackup();
                return (false, "Windows не приняла настройку: " + reason + " Прежняя настройка возвращена, ничего не изменилось.");
            }
            return (false, "Windows не приняла настройку: " + reason + " Сейчас записано: " + (after?.Describe() ?? "прочитать не удалось") +
                           ". Нажмите «Вернуть как было», чтобы вернуть прежнюю настройку, и не перезагружайте компьютер до этого.");
        }
        catch (Exception ex) { return (false, "Не удалось изменить настройку: " + ex.Message); }
    }

    /// <summary>Вернуть конфигурацию из копии и удалить копию.</summary>
    public static (bool Ok, string Message) Restore()
    {
        if (!Shell.IsAdmin()) return (false, "Нужны права администратора.");
        try
        {
            var saved = LoadBackup();
            if (saved == null) return (false, "Копии прежней настройки нет: программа файл подкачки не меняла.");
            var (code, output) = Write(saved);
            var now = ReadPlan();
            if (code == 0 && (output ?? "").Contains("PULSE-DONE") && now != null && now.Same(saved))
            {
                DeleteBackup();
                return (true, $"Возвращено как было: {saved.Describe()}. Вступит в силу после перезагрузки компьютера.");
            }
            return (false, "Windows не приняла прежнюю настройку: " + Short(output) + " Копия сохранена, можно попробовать ещё раз.");
        }
        catch (Exception ex) { return (false, "Не удалось вернуть настройку: " + ex.Message); }
    }

    // ------------------------------------------------------------------ самопроверка

    /// <summary>Только чтение: настройка, файлы, диски, сводка по играм, вердикт и команды, которые записала бы кнопка.</summary>
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        IReadOnlyList<GameSession> sessions;
        try { sessions = host?.Sessions() ?? SessionStore.Load(); } catch { sessions = Array.Empty<GameSession>(); }
        var st = Read(sessions);
        sb.AppendLine(st.Loaded ? "OK" : "FAIL: " + st.Error);
        if (!st.Loaded) return sb.ToString();

        sb.AppendLine($"Размером управляет Windows: {(st.Auto ? "да" : "нет")}");
        sb.AppendLine($"Память: {Ram(st.RamGb)} ({st.RamGb:0.0} ГБ по счёту Windows)");
        sb.AppendLine($"Предел выделения памяти (память + подкачка): {Mb(st.CommitLimitMb)}, свободно {Mb(st.CommitFreeMb)}");
        sb.AppendLine($"Системный диск: {st.SystemDrive}:");
        sb.AppendLine($"Файлов подкачки: {st.Files.Count}");
        foreach (var f in st.Files)
        {
            var d = st.DriveOf(f);
            sb.AppendLine($"  {f.Path} — {(f.HasSetting ? (f.MaximumMb == 0 ? "размер выбирает Windows" : $"задано от {Mb(f.InitialMb)} до {Mb(f.MaximumMb)}") : "без ручной настройки")}; " +
                          (f.InUse ? $"сейчас {Mb(f.AllocatedMb)}, занято {Mb(f.UsedMb)}, пик {Mb(f.PeakMb)}{(f.Temporary ? ", временный" : "")}" : "сейчас не используется") +
                          $"; диск: {(d == null ? "не найден" : d.KindText + " " + d.DiskName)}");
        }
        sb.AppendLine("Диски:");
        foreach (var d in st.Drives)
            sb.AppendLine($"  {d.Letter}: {d.KindText}, {d.DiskName}, свободно {Fmt.Size(d.FreeBytes)} из {Fmt.Size(d.SizeBytes)}");
        var g = st.Games;
        sb.AppendLine($"Игры: сессий с показаниями памяти {g.Sessions}, с разбором подкачки {g.Analysed}, подкачка шла в {g.Paged}, просадок из-за памяти {g.RamDips}, " +
                      $"свободно меньше 2 ГБ в {g.Tight}, минимум свободной памяти {(double.IsNaN(g.MinFreeGb) ? "нет данных" : Fmt.Gb(g.MinFreeGb))}");
        sb.AppendLine($"Вердикт ({st.Advice.Level}): {st.Advice.Verdict}");
        sb.AppendLine($"Рекомендация: {st.Advice.Title}");
        sb.AppendLine($"Почему: {st.Advice.Why}");
        sb.AppendLine($"О памяти: {(st.MemoryNote == "" ? "замечаний нет" : st.MemoryNote)}");
        sb.AppendLine($"Сейчас записано: {st.Current.Describe()}");
        sb.AppendLine($"Кнопка «Применить» записала бы: {(st.Advice.Plan == null ? "ничего" : st.Advice.Plan.Describe())}");
        if (st.Advice.Plan != null) sb.AppendLine("  команды: " + BuildScript(st.Advice.Plan) + "  [PULSE_PAGEFILES: " + PlanEnv(st.Advice.Plan)["PULSE_PAGEFILES"].Replace("\n", "; ") + "]");
        bool parseOk = ParsePlan(new[] { "auto=0", "file|C:\\pagefile.sys|4096|8192" }) is { Files.Count: 1 }
                       && ParsePlan(new[] { "auto=0", "file|C:\\x’; calc; ‘|1|2" }) == null
                       && ParsePlan(new[] { "auto=0", "file|C:\\pagefile.sys|-1|2" }) == null;
        sb.AppendLine($"Разбор копии отвергает кавычки и неверные размеры: {(parseOk ? "да" : "FAIL")}");
        sb.AppendLine($"Копия «как было»: {(st.Backup == null ? "нет" : st.Backup.Describe())}");
        sb.AppendLine($"Права администратора: {(Shell.IsAdmin() ? "есть" : "нет — запись недоступна")}");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ текст и JSON

    /// <summary>«512 МБ», «4 ГБ», «23,5 ГБ».</summary>
    public static string Mb(long mb) =>
        mb < 1024 ? mb + " МБ" : (mb / 1024.0).ToString(mb % 1024 == 0 ? "0" : "0.0", CultureInfo.CurrentCulture) + " ГБ";

    /// <summary>Объём памяти, как его называют: 15,8 ГБ по счёту Windows — это «16 ГБ».</summary>
    public static string Ram(double gb)
    {
        int[] usual = { 4, 6, 8, 12, 16, 20, 24, 32, 48, 64, 96, 128, 192, 256 };
        foreach (int u in usual) if (gb > u * 0.93 && gb <= u * 1.01) return u + " ГБ";
        return Math.Round(gb) + " ГБ";
    }

    private static string Named(PagefileDrive d) => d.DiskName == "" ? d.KindText : d.KindText + ", " + d.DiskName;

    private static string Short(string output)
    {
        string text = (output ?? "").Replace("PULSE-DONE", "").Trim();
        string first = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        if (first == "") return "причина не названа.";
        if (first.Length > 200) first = first[..200] + "…";
        return first.EndsWith('.') ? first : first + ".";
    }

    private static string S(JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString().Trim() : "";

    private static double N(JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static bool B(JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;

    private static List<JsonElement> Arr(JsonElement e, string n)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(n, out var v)) return new();
        return v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : v.ValueKind == JsonValueKind.Object ? new() { v } : new();
    }
}
