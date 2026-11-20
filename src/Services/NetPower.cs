using System.Text;
using System.Text.RegularExpressions;

namespace PulsePC.Services;

/// <summary>
/// Энергосбережение проводной сетевой карты: Green Ethernet, Gigabit Lite, Power Saving Mode, EEE, Selective Suspend.
/// С ними карта «засыпает» между пакетами и снижает скорость линии — в играх это редкие скачки задержки.
/// Исходные значения запоминаются в файл (в защищённой папке), откат возвращает именно их.
/// Имена адаптеров, ключи и значения передаются в PowerShell только через переменные окружения, не текстом команды:
/// копия «как было» — данные, и из неё нельзя «вписать» в команду администратора свой код.
/// </summary>
public static class NetPower
{
    // Realtek: Green Ethernet, Gigabit Lite, Power Saving Mode, Advanced EEE; Intel: Reduce Speed On Power Down, Ultra Low Power, EEE Link Advertisement; общие: EEE, Selective Suspend.
    internal static readonly string[] KnownKeys =
    {
        "EnableGreenEthernet", "GigaLite", "PowerSavingMode", "*EEE", "AdvancedEEE", "*SelectiveSuspend", "ReduceSpeedOnPowerDown", "ULPMode", "EEELinkAdvertisement",
    };

    // Список ключей — константы программы, не данные из файлов.
    private static readonly string KeysPs = string.Join(",", KnownKeys.Select(k => "'" + k + "'"));

    // Только включённые проводные адаптеры (802.3). Wi-Fi не трогаем: перезапуск оборвал бы беспроводное соединение,
    // а его энергосбережение настраивается иначе. Виртуальные (Radmin VPN) тоже не трогаем.
    // -Name у сетевых команд понимает * и ? как шаблон, поэтому имя всегда экранируется ([WildcardPattern]::Escape).
    private static string ReadScript =>
        "$k=@(" + KeysPs + "); Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Up' -and $_.NdisPhysicalMedium -eq 14 } | ForEach-Object { $n=$_.Name; " +
        "$w=[Management.Automation.WildcardPattern]::Escape($n); " +
        "Get-NetAdapterAdvancedProperty -Name $w -AllProperties -ErrorAction SilentlyContinue | Where-Object { $k -contains $_.RegistryKeyword } | ForEach-Object { " +
        "\"$n|$($_.RegistryKeyword)|$(@($_.RegistryValue) -join ',')\" } }";

    private const string BackupName = "nic-power-backup.txt";
    private static readonly object Gate = new();
    private static bool migrated;

    private static string BackupFile
    {
        get
        {
            lock (Gate)
            {
                if (!migrated) { SecureStore.Migrate(BackupName); migrated = true; }
            }
            return SecureStore.PathOf(BackupName);
        }
    }

    // ------------------------------------------------------------------ проверка данных

    private static readonly Regex ValueRx = new(@"^[0-9A-Za-z_.\-]{1,32}$");

    internal static bool ValidKey(string key) => KnownKeys.Contains(key, StringComparer.Ordinal);
    internal static bool ValidValue(string value) => value != null && ValueRx.IsMatch(value);
    internal static bool ValidName(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 256 && !name.Any(char.IsControl) && !name.Contains('|');

    /// <summary>Разбор копии: только строки «адаптер|известный ключ|значение» с допустимыми символами.</summary>
    internal static List<(string Adapter, string Key, string Value)> ParseBackup(string text)
    {
        var list = new List<(string, string, string)>();
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split('|');
            if (p.Length == 3 && ValidName(p[0]) && ValidKey(p[1]) && ValidValue(p[2])
                && !list.Any(x => x.Item1 == p[0] && x.Item2 == p[1]))
                list.Add((p[0], p[1], p[2]));
            else Log.Info($"{BackupName}: пропущена испорченная строка");
        }
        return list;
    }

    private static List<(string Adapter, string Key, string Value)> LoadBackup() =>
        ParseBackup(AtomicFile.Read(BackupFile, t => t) ?? "");

    private static void SaveBackup(List<(string Adapter, string Key, string Value)> list)
    {
        string file = BackupFile;
        if (list.Count == 0)
        {
            foreach (var f in new[] { file, file + ".bak" }) { try { File.Delete(f); } catch { } }
            return;
        }
        AtomicFile.WriteAllText(file, string.Join("\n", list.Select(x => $"{x.Adapter}|{x.Key}|{x.Value}")) + "\n");
    }

    // ------------------------------------------------------------------ чтение

    /// <summary>Строки «адаптер|ключ|значение» для свойств энергосбережения.</summary>
    private static List<(string Adapter, string Key, string Value)> Read()
    {
        var (_, output) = Shell.PowerShell(ReadScript, 60000);
        var list = new List<(string, string, string)>();
        foreach (var line in (output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split('|');
            if (p.Length == 3 && ValidName(p[0]) && ValidKey(p[1])) list.Add((p[0], p[1], p[2]));
        }
        return list;
    }

    /// <summary>Имена всех физических адаптеров: имя из копии используется, только если такой адаптер есть.</summary>
    private static HashSet<string> Adapters()
    {
        var (_, output) = Shell.PowerShell("Get-NetAdapter -Physical | ForEach-Object { $_.Name }", 30000);
        return new HashSet<string>((output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(ValidName),
                                   StringComparer.Ordinal);
    }

    private static bool IsZero(string value) => value.Split(',').All(v => v.Trim() == "0");

    public static bool IsOff() => Read().All(x => IsZero(x.Value));

    // ------------------------------------------------------------------ запись

    // Сценарий записи: адаптер в $env:PULSE_NIC, пары «ключ|значение» построчно в $env:PULSE_ITEMS.
    // Для каждой пары после записи значение перечитывается; «PULSE-OK|ключ» — значение действительно стало нужным.
    private const string SetScript =
        "$n = \"$env:PULSE_NIC\"; $w = [Management.Automation.WildcardPattern]::Escape($n); " +
        "foreach ($line in @(\"$env:PULSE_ITEMS\" -split '\\r?\\n' | Where-Object { $_ })) { " +
        "$i = $line.IndexOf('|'); if ($i -lt 1) { continue }; $k = $line.Substring(0, $i); $v = $line.Substring($i + 1); " +
        "try { Get-NetAdapterAdvancedProperty -Name $w -AllProperties -ErrorAction Stop | Where-Object { $_.RegistryKeyword -eq $k } | " +
        "Set-NetAdapterAdvancedProperty -RegistryValue $v -NoRestart -ErrorAction Stop; " +
        "$now = @((Get-NetAdapterAdvancedProperty -Name $w -AllProperties -ErrorAction Stop | Where-Object { $_.RegistryKeyword -eq $k }).RegistryValue)[0]; " +
        "if (\"$now\" -eq $v) { \"PULSE-OK|$k\" } } catch { } }; ";

    /// <summary>
    /// Перезапуск адаптера, чтобы новые значения вступили в силу. Restart-NetAdapter на некоторых драйверах оставляет
    /// карту выключенной (так на этом ПК пропал кабельный интернет), поэтому включение проверяется и повторяется.
    /// Ожидает $w — экранированное имя адаптера.
    /// </summary>
    private const string Bounce =
        "Disable-NetAdapter -Name $w -Confirm:$false -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2; " +
        "for ($i = 0; $i -lt 10; $i++) { Enable-NetAdapter -Name $w -Confirm:$false -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2; " +
        "if ((Get-NetAdapter -Name $w -ErrorAction SilentlyContinue).Status -ne 'Disabled') { break } }";

    /// <summary>Записать пары адаптеру и перезапустить его. Возвращает ключи, которые точно получили нужное значение.</summary>
    private static HashSet<string> Set(string adapter, IEnumerable<(string Key, string Value)> items)
    {
        var env = new Dictionary<string, string>
        {
            ["PULSE_NIC"] = adapter,
            ["PULSE_ITEMS"] = string.Join("\n", items.Select(x => x.Key + "|" + x.Value)),
        };
        var (_, output) = Shell.PowerShell(SetScript + Bounce, 90000, env);
        return new HashSet<string>((output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith("PULSE-OK|", StringComparison.Ordinal)).Select(l => l[9..]), StringComparer.Ordinal);
    }

    public static void TurnOff()
    {
        var current = Read().Where(x => !IsZero(x.Value)).ToList();
        if (current.Count == 0) return;
        lock (Gate)
        {
            // Исходное значение запоминается для каждой пары «адаптер|ключ» один раз: повторное применение не затирает
            // настоящие исходные значения, а новые адаптеры (или новые ключи) дописываются.
            var saved = LoadBackup();
            bool changed = false;
            foreach (var x in current)
            {
                string first = x.Value.Split(',')[0].Trim();
                if (!ValidValue(first) || saved.Any(s => s.Adapter == x.Adapter && s.Key == x.Key)) continue;
                saved.Add((x.Adapter, x.Key, first));
                changed = true;
            }
            if (changed) SaveBackup(saved);     // не удалось записать копию — исключение, и ничего не меняется
        }
        var known = LoadBackup();
        foreach (var adapter in current.GroupBy(x => x.Adapter))
        {
            // Меняем только то, что запомнено: без копии вернуть было бы нечем.
            var items = adapter.Where(x => known.Any(s => s.Adapter == x.Adapter && s.Key == x.Key)).Select(x => (x.Key, "0")).ToList();
            if (items.Count > 0) Set(adapter.Key, items);
        }
    }

    public static void Restore()
    {
        var saved = LoadBackup();
        if (saved.Count == 0) { SaveBackup(saved); return; }
        var present = Adapters();
        var restored = new List<(string Adapter, string Key)>();
        int failed = 0;
        foreach (var adapter in saved.GroupBy(p => p.Adapter))
        {
            if (!present.Contains(adapter.Key)) continue;      // адаптера сейчас нет — копия для него остаётся
            var ok = Set(adapter.Key, adapter.Select(p => (p.Key, p.Value)));
            foreach (var p in adapter)
            {
                if (ok.Contains(p.Key)) restored.Add((p.Adapter, p.Key));
                else failed++;
            }
        }
        lock (Gate)
        {
            // Из копии убираются только вернувшиеся пары; остальные останутся до следующей попытки.
            var left = LoadBackup().Where(x => !restored.Contains((x.Adapter, x.Key))).ToList();
            SaveBackup(left);
        }
        if (failed > 0)
            throw new InvalidOperationException(L.F("Сетевая карта не приняла {0} из {1} прежних значений. Копия сохранена — можно попробовать ещё раз.", failed, saved.Count));
    }

    /// <summary>Включить обратно проводные адаптеры, оставшиеся выключенными после прежнего перезапуска.</summary>
    public static void EnsureEnabled()
    {
        var names = LoadBackup().Select(x => x.Adapter).Distinct().ToList();
        if (names.Count == 0) return;
        var present = Adapters();
        foreach (var name in names.Where(present.Contains))
            Shell.PowerShell("$n = \"$env:PULSE_NIC\"; $w = [Management.Automation.WildcardPattern]::Escape($n); " +
                             "$a = Get-NetAdapter -Name $w -ErrorAction SilentlyContinue; if ($a -and $a.Status -eq 'Disabled') { " + Bounce + " }",
                             60000, new Dictionary<string, string> { ["PULSE_NIC"] = name });
    }

    /// <summary>Самопроверка разбора копии (систему не трогает).</summary>
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        string fail = null;
        void Check(string what, bool ok) { sb.AppendLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) fail ??= what; }
        var parsed = ParseBackup("Ethernet|*EEE|1\nEthernet|GigaLite|1\nEthernet 2|Evil|1\nX|*EEE|1'; calc; '\nY|*EEE|1’; calc; ‘\nEthernet|*EEE|0\n");
        Check("копия: берутся только известные ключи и простые значения", parsed.Count == 2 && parsed[0] == ("Ethernet", "*EEE", "1"));
        Check("копия: кавычки любого вида в значении отвергаются", !parsed.Any(p => p.Adapter is "X" or "Y"));
        Check("имя с управляющими символами отвергается", !ValidName("a\nb") && !ValidName("a|b") && ValidName("Ethernet 2"));
        Check("строка PowerShell: все виды кавычек удваиваются", Shell.PsLiteral("a'b’c‘d‚e‛f") == "'a''b’’c‘‘d‚‚e‛‛f'");
        return (fail == null ? "OK" : "FAIL: " + fail) + Environment.NewLine + sb;
    }
}
