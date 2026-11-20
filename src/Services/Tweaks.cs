using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PulsePC.Services;

/// <summary>
/// Одно значение реестра: что записать при применении. Default — к чему вернуть, только если исходное значение не было
/// запомнено (настройку применила прежняя версия программы); null = удалить значение. Part — не всё значение, а одна пара
/// «имя=значение» внутри строки вида «A=1;B=2;» (DirectXUserGlobalSettings): меняется и возвращается только она.
/// </summary>
public record RegValue(RegistryHive Hive, string Path, string Name, RegistryValueKind Kind, object Apply, object Default, string Part = null);

/// <summary>
/// Процессы, которые программа не трогает никогда — ни закрытием («Перед игрой»), ни сжатием памяти («Память в игре»):
/// «Безопасность Windows» (Defender, SmartScreen, центр безопасности) и античиты. Один список на всю программу.
/// </summary>
public static class ProtectedProcesses
{
    public static readonly IReadOnlyCollection<string> Security = new[]
    {
        "MsMpEng", "NisSrv", "MpDefenderCoreService", "MpCmdRun", "MsSense", "SenseNdr", "SenseCncProxy", "SenseIR",
        "SecurityHealthService", "SecurityHealthSystray", "SecurityHealthHost", "SecHealthUI",
        "smartscreen", "SgrmBroker", "SgrmLpac",
        "vgc", "vgtray", "BEService", "EasyAntiCheat", "EasyAntiCheat_EOS", "FACEIT", "faceitclient", "faceitservice",
    };

    private static readonly HashSet<string> set = new(Security, StringComparer.OrdinalIgnoreCase);

    public static bool IsSecurity(string processName) => processName != null && set.Contains(processName);
}

public static class Tweaks
{
    private const string GuidPattern = "[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}";
    private const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string HighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string UltimateTemplate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    private const string UltimateLocal = "892e74a9-4547-4b4b-ae66-dd35bc2f1297";   // копия схемы, уже созданная на этом ПК

    /// <summary>
    /// Название прежнего переключателя схемы питания. Теперь схемой ведает раздел «Питание» (режим «всегда максимальная»),
    /// а название осталось прежним: оно уже записано у пользователей в KnownApplied, и сторож продолжает его узнавать.
    /// </summary>
    public const string PowerTitle = "Схема питания «Максимальная производительность»";
    /// <summary>Ускорение мыши — теперь в разделе «Клавиатура и мышь»; название прежнее по той же причине.</summary>
    public const string MouseTitle = "Ускорение мыши выключено";

    private const RegistryHive U = RegistryHive.CurrentUser, M = RegistryHive.LocalMachine;
    private const RegistryValueKind D = RegistryValueKind.DWord, S = RegistryValueKind.String;
    private const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string DxPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    private static readonly RegValue[] GameDvr =
    {
        new(U, @"System\GameConfigStore", "GameDVR_Enabled", D, 0, 1),
        new(U, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", D, 0, null),
    };
    private static readonly RegValue[] GameMode =
    {
        new(U, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", D, 1, null),
        new(U, @"Software\Microsoft\GameBar", "AllowAutoGameMode", D, 1, null),
    };
    private static readonly RegValue[] Hags =
    {
        new(M, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", D, 2, null),
    };
    // Внутри строки DirectXUserGlobalSettings могут быть и другие настройки (AutoHDR и пр.) — меняются только эти две пары.
    private static readonly RegValue[] Windowed =
    {
        new(U, DxPath, "DirectXUserGlobalSettings", S, "1", null, "VRROptimizeEnable"),
        new(U, DxPath, "DirectXUserGlobalSettings", S, "1", null, "SwapEffectUpgradeEnable"),
    };
    private static readonly RegValue[] Ads =
    {
        new(U, Cdm, "SilentInstalledAppsEnabled", D, 0, 1),
        new(U, Cdm, "PreInstalledAppsEnabled", D, 0, 1),
        new(U, Cdm, "OemPreInstalledAppsEnabled", D, 0, 1),
        new(U, Cdm, "SystemPaneSuggestionsEnabled", D, 0, 1),
        new(U, Cdm, "SoftLandingEnabled", D, 0, 1),
        new(U, Cdm, "RotatingLockScreenOverlayEnabled", D, 0, 1),
        new(U, Cdm, "SubscribedContent-338388Enabled", D, 0, null),
        new(U, Cdm, "SubscribedContent-338389Enabled", D, 0, 1),
        new(U, Cdm, "SubscribedContent-338393Enabled", D, 0, null),
        new(U, Cdm, "SubscribedContent-353694Enabled", D, 0, null),
        new(U, Cdm, "SubscribedContent-353696Enabled", D, 0, null),
        new(U, Adv, "ShowSyncProviderNotifications", D, 0, null),
        new(U, Adv, "Start_IrisRecommendations", D, 0, null),
        new(U, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", D, 1, null),
    };
    private static readonly RegValue[] Mouse =
    {
        new(U, @"Control Panel\Mouse", "MouseSpeed", S, "0", "1"),
        new(U, @"Control Panel\Mouse", "MouseThreshold1", S, "0", "6"),
        new(U, @"Control Panel\Mouse", "MouseThreshold2", S, "0", "10"),
    };

    /// <summary>Переключатели страницы «Оптимизация».</summary>
    public static List<TweakItem> BuildItems()
    {
        var items = new List<TweakItem>
        {
            Reg("Фоновая запись игр выключена",
                L.T("Windows не пишет видео игры в фоне (Game DVR) и не тратит на это ресурсы видеокарты и диска."), false, GameDvr),
            Reg("Игровой режим включён",
                L.T("Windows отдаёт игре приоритет и не ставит обновления, пока вы играете."), false, GameMode),
            Reg("Аппаратное планирование видеокарты (HAGS)",
                L.T("Нужно для генерации кадров DLSS и немного снижает задержку. Заработает после перезагрузки."), true, Hags),
            Reg("Оптимизации оконных игр и переменная частота",
                L.T("Игры в окне без рамки выводят картинку так же быстро, как в полноэкранном режиме, и с переменной частотой монитора."), false, Windowed),
            Reg("Реклама и рекомендации Windows выключены",
                L.T("Пропадут советы и рекомендации в «Пуске» и «Параметрах», тихая установка рекламных приложений и результаты из интернета в поиске."), false, Ads),
            new()
            {
                Title = "Энергосбережение сетевой карты выключено",
                Hint = L.T("Сетевая карта не «засыпает» между пакетами и не снижает скорость (Green Ethernet, Gigabit Lite, Power Saving) — меньше редких скачков пинга. Пока настройка применяется, интернет пропадёт на 2–3 секунды."),
                Check = NetPower.IsOff,
                Apply = NetPower.TurnOff,
                Revert = NetPower.Restore,
                DropsNetwork = true,
            },
        };
        return items;
    }

    /// <summary>
    /// Всё, что сторож проверяет и при сбросе возвращает: переключатели «Оптимизации» и перенесённые в разделы «Системы» —
    /// ускорение мыши и схема питания (только в режиме «всегда максимальная»).
    /// </summary>
    public static List<TweakItem> Watched(AppSettings settings)
    {
        var list = BuildItems();
        list.Add(MouseAccel());
        if (PowerAuto.ModeOf(settings) == PowerAuto.ModeAlways) list.Add(PowerAlwaysMax());
        return list;
    }

    /// <summary>Где теперь живёт настройка, которой нет в списке «Оптимизации».</summary>
    public static string HomeOf(string title) => title == MouseTitle ? "Клавиатура и мышь" : title == PowerTitle ? "Питание" : "";

    /// <summary>Запомнить решение пользователя для сторожа: включённое — следить и возвращать, выключенное — забыть.</summary>
    public static void Remember(AppSettings settings, string title, bool applied)
    {
        var known = settings.KnownApplied ??= new();
        lock (known)
        {
            known.Remove(title);
            if (applied) known.Add(title);
        }
        settings.RevertedTweaks?.Remove(title);
    }

    /// <summary>
    /// Ускорение мыши («Повышенная точность указателя») выключено. Действует сразу, без выхода из системы.
    /// «Вернуть» ставит значения, которые были до применения (и в реестре, и в текущем сеансе Windows).
    /// </summary>
    public static TweakItem MouseAccel() => new()
    {
        Title = MouseTitle,
        Hint = L.T("Прицел сдвигается ровно на столько, на сколько вы сдвинули мышь, — скорость руки не важна."),
        Check = () => Mouse.All(IsSet),
        Apply = () =>
        {
            Backup(Mouse, saveMouse: true);
            foreach (var v in Mouse) Write(v, v.Apply, v.Kind);
            SetMouse(0, 0, 0);
        },
        Revert = () =>
        {
            int[] spi = RestoreBackup(Mouse, restoreMouse: true);
            SetMouse(spi[0], spi[1], spi[2]);
        },
    };

    // Сообщить Windows сразу: иначе новое значение подхватывается только после выхода из системы.
    private static void SetMouse(int threshold1, int threshold2, int speed)
    {
        try { var p = new[] { threshold1, threshold2, speed }; SystemParametersInfo(0x0004 /* SPI_SETMOUSE */, 0, p, 0x01 | 0x02); } catch { }
    }

    private static int[] GetMouse()
    {
        try { var p = new int[3]; return SystemParametersInfo(0x0003 /* SPI_GETMOUSE */, 0, p, 0) ? p : null; } catch { return null; }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfo(uint action, uint param, int[] value, uint winIni);

    /// <summary>Режим раздела «Питание» «всегда максимальная»: сторож возвращает схему, если её сбросило обновление Windows.</summary>
    private static TweakItem PowerAlwaysMax() => new()
    {
        Title = PowerTitle,
        Hint = L.T("Задаётся в разделе «Питание»: режим «всегда максимальная»."),
        Check = PowerAuto.ActiveIsMax,
        Apply = PowerAuto.ApplyMax,
        // Возвращается схема, которая была до режима «всегда» (а не «Сбалансированная»).
        Revert = () => PowerAuto.RevertAlways(),
    };

    private static TweakItem Reg(string title, string hint, bool reboot, RegValue[] values) => new()
    {
        Title = title,
        Hint = hint,
        NeedsReboot = reboot,
        Check = () => values.All(IsSet),
        Apply = () =>
        {
            Backup(values, saveMouse: false);
            foreach (var v in values) Write(v, v.Apply, v.Kind);
        },
        Revert = () => RestoreBackup(values, restoreMouse: false),
    };

    private static bool IsSet(RegValue v)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(v.Hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(v.Path);
            object current = key?.GetValue(v.Name);
            if (v.Part != null) return GetPart(current as string, v.Part) == v.Apply.ToString();
            return current != null && string.Equals(current.ToString(), v.Apply.ToString(), StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>Записать значение (null — удалить). Для пары внутри строки меняется только она, остальное остаётся.</summary>
    private static void Write(RegValue v, object value, RegistryValueKind kind)
    {
        using var root = RegistryKey.OpenBaseKey(v.Hive, RegistryView.Registry64);
        if (v.Part != null)
        {
            using var key = value == null ? root.OpenSubKey(v.Path, true) : root.CreateSubKey(v.Path, true);
            if (key == null) return;
            string next = SetPart(key.GetValue(v.Name) as string, v.Part, value?.ToString());
            if (next == "") key.DeleteValue(v.Name, false);
            else key.SetValue(v.Name, next, RegistryValueKind.String);
            return;
        }
        if (value == null)
        {
            using var key = root.OpenSubKey(v.Path, true);
            key?.DeleteValue(v.Name, false);
        }
        else
        {
            using var key = root.CreateSubKey(v.Path, true);
            key.SetValue(v.Name, value, kind);
        }
    }

    // ------------------------------------------------------------------ строка «A=1;B=2;»

    /// <summary>Значение пары name внутри строки «A=1;B=2;» или null, если пары нет.</summary>
    internal static string GetPart(string text, string name)
    {
        foreach (var item in (text ?? "").Split(';'))
        {
            int eq = item.IndexOf('=');
            if (eq > 0 && item[..eq].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) return item[(eq + 1)..].Trim();
        }
        return null;
    }

    /// <summary>Поставить (value != null) или убрать (null) одну пару; остальные пары и их порядок сохраняются.</summary>
    internal static string SetPart(string text, string name, string value)
    {
        var items = (text ?? "").Split(';').Where(i => i.Trim() != "").ToList();
        int at = items.FindIndex(i => { int eq = i.IndexOf('='); return eq > 0 && i[..eq].Trim().Equals(name, StringComparison.OrdinalIgnoreCase); });
        if (value == null) { if (at >= 0) items.RemoveAt(at); }
        else if (at >= 0) items[at] = name + "=" + value;
        else items.Add(name + "=" + value);
        return items.Count == 0 ? "" : string.Join(";", items) + ";";
    }

    // ------------------------------------------------------------------ копия исходных значений

    // Исходные значения до первого применения: «Вернуть как было» ставит именно их. Лежат в защищённой папке —
    // по ним программа с правами администратора пишет в реестр (в том числе в HKLM).
    private const string BackupName = "tweaks-backup.json";
    private const string Absent = "absent";
    private const string MouseKey = "mouse.spi";
    private static string BackupPath => SecureStore.PathOf(BackupName);
    private static readonly object BackupGate = new();
    private static readonly JsonSerializerOptions BackupJson = new() { WriteIndented = true };

    private static IEnumerable<RegValue> AllValues => GameDvr.Concat(GameMode).Concat(Hags).Concat(Windowed).Concat(Ads).Concat(Mouse);

    private static string KeyOf(RegValue v) => $"{v.Hive}|{v.Path}|{v.Name}" + (v.Part != null ? "#" + v.Part : "");

    /// <summary>Снимок текущего значения: «absent» или «Вид:значение».</summary>
    private static string Snapshot(RegValue v)
    {
        using var root = RegistryKey.OpenBaseKey(v.Hive, RegistryView.Registry64);
        using var key = root.OpenSubKey(v.Path);
        object value = key?.GetValue(v.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (v.Part != null)
        {
            string part = GetPart(value as string, v.Part);
            return part == null ? Absent : "String:" + part;
        }
        if (value == null) return Absent;
        var kind = key.GetValueKind(v.Name);
        return kind switch
        {
            RegistryValueKind.DWord => "DWord:" + Convert.ToInt32(value),
            RegistryValueKind.QWord => "QWord:" + Convert.ToInt64(value),
            RegistryValueKind.String => "String:" + value,
            RegistryValueKind.ExpandString => "ExpandString:" + value,
            RegistryValueKind.Binary => "Binary:" + Convert.ToBase64String((byte[])value),
            _ => throw new InvalidOperationException($"{v.Name}: значение вида {kind} не запоминается"),
        };
    }

    /// <summary>
    /// Разобрать запомненное значение. false — запись испорчена (её не используем). Проверяется вид и содержимое:
    /// файл мог лежать в папке пользователя до переноса.
    /// </summary>
    internal static bool TryParseSaved(string saved, bool part, out object value, out RegistryValueKind kind)
    {
        value = null; kind = RegistryValueKind.Unknown;
        if (saved == Absent) return true;
        int colon = saved?.IndexOf(':') ?? -1;
        if (colon <= 0 || saved.Length > 4096) return false;
        string head = saved[..colon], body = saved[(colon + 1)..];
        if (!Regex.IsMatch(head, "^[A-Za-z]+$") || !Enum.TryParse(head, false, out kind)) return false;
        if (part)
        {
            if (kind != RegistryValueKind.String || !Regex.IsMatch(body, @"^[0-9A-Za-z.\-]{0,32}$")) return false;
            value = body;
            return true;
        }
        switch (kind)
        {
            case RegistryValueKind.DWord: if (int.TryParse(body, out int i)) { value = i; return true; } return false;
            case RegistryValueKind.QWord: if (long.TryParse(body, out long l)) { value = l; return true; } return false;
            case RegistryValueKind.String:
            case RegistryValueKind.ExpandString: value = body; return true;
            case RegistryValueKind.Binary: try { value = Convert.FromBase64String(body); return true; } catch { return false; }
            default: return false;
        }
    }

    private static Dictionary<string, string> LoadBackup()
    {
        var raw = AtomicFile.Read(BackupPath, t => JsonSerializer.Deserialize<Dictionary<string, string>>(t)) ?? new();
        // Только известные ключи с правильным содержимым — остальное отбрасывается.
        var known = AllValues.ToDictionary(KeyOf, v => v.Part != null);
        var clean = new Dictionary<string, string>();
        foreach (var (k, val) in raw)
        {
            if (k == MouseKey) { if (ParseSpi(val) != null) clean[k] = val; continue; }
            if (val != null && known.TryGetValue(k, out bool part) && TryParseSaved(val, part, out _, out _)) clean[k] = val;
            else Log.Info($"{BackupName}: пропущена неизвестная или испорченная запись «{k}»");
        }
        return clean;
    }

    private static void SaveBackup(Dictionary<string, string> map)
    {
        if (map.Count == 0)
        {
            foreach (var f in new[] { BackupPath, BackupPath + ".bak" }) { try { File.Delete(f); } catch { } }
            return;
        }
        AtomicFile.WriteAllText(BackupPath, JsonSerializer.Serialize(map, BackupJson));
    }

    private static int[] ParseSpi(string text)
    {
        var p = (text ?? "").Split(',');
        if (p.Length != 3) return null;
        var r = new int[3];
        for (int i = 0; i < 3; i++) if (!int.TryParse(p[i], out r[i]) || r[i] < 0 || r[i] > 100) return null;
        return r;
    }

    /// <summary>
    /// Запомнить исходные значения перед применением. Уже запомненные не перезаписываются: повторное применение
    /// (в том числе сторожем после сброса обновлением Windows) не должно затереть настоящие исходные значения.
    /// Если копию записать не удалось — исключение, и применение не идёт.
    /// </summary>
    private static void Backup(RegValue[] values, bool saveMouse)
    {
        lock (BackupGate)
        {
            var map = LoadBackup();
            bool changed = false;
            foreach (var v in values)
            {
                string k = KeyOf(v);
                if (map.ContainsKey(k)) continue;
                map[k] = Snapshot(v);
                changed = true;
            }
            if (saveMouse && !map.ContainsKey(MouseKey) && GetMouse() is { } spi) { map[MouseKey] = string.Join(",", spi); changed = true; }
            if (changed) SaveBackup(map);
        }
    }

    /// <summary>
    /// Вернуть запомненные значения и убрать их из копии. Значение без копии (применено прежней версией программы)
    /// возвращается к значению Windows по умолчанию, как раньше. Возвращает параметры мыши для текущего сеанса.
    /// </summary>
    private static int[] RestoreBackup(RegValue[] values, bool restoreMouse)
    {
        lock (BackupGate)
        {
            var map = LoadBackup();
            foreach (var v in values)
            {
                string k = KeyOf(v);
                if (map.TryGetValue(k, out var saved) && TryParseSaved(saved, v.Part != null, out object value, out var kind))
                    Write(v, value, value == null ? v.Kind : kind);
                else
                    Write(v, v.Default, v.Kind);
                map.Remove(k);
            }
            int[] spi = new[] { 6, 10, 1 };
            if (restoreMouse)
            {
                if (map.TryGetValue(MouseKey, out var m) && ParseSpi(m) is { } saved) spi = saved;
                map.Remove(MouseKey);
            }
            SaveBackup(map);
            return spi;
        }
    }

    public static string ActiveScheme()
    {
        var (_, output) = Shell.Run("powercfg.exe", "/getactivescheme", 15000);
        var m = Regex.Match(output ?? "", GuidPattern);
        return m.Success ? m.Value.ToLowerInvariant() : "";
    }

    /// <summary>Включить схему «Максимальная производительность» (создаётся из шаблона, если её ещё нет).</summary>
    public static void ApplyUltimatePlan()
    {
        var (_, list) = Shell.Run("powercfg.exe", "/list", 15000);
        list = (list ?? "").ToLowerInvariant();
        string target = list.Contains(UltimateLocal) ? UltimateLocal : list.Contains(UltimateTemplate) ? UltimateTemplate : null;
        if (target == null)
        {
            // Копия создаётся с заранее известным номером: иначе на каждом ПК он случайный, проверка «применено»
            // его не узнаёт, и каждое применение плодило бы ещё одну копию схемы.
            var (_, dup) = Shell.Run("powercfg.exe", $"-duplicatescheme {UltimateTemplate} {UltimateLocal}", 15000);
            var m = Regex.Match(dup ?? "", GuidPattern);
            target = m.Success ? m.Value.ToLowerInvariant() : HighPerf;
        }
        Shell.Run("powercfg.exe", "/setactive " + target, 15000);
    }

    /// <summary>Самопроверка чистых частей (строка DirectX и разбор копии). Систему не трогает.</summary>
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        string fail = null;
        void Check(string what, bool ok) { sb.AppendLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) fail ??= what; }

        string user = "AutoHDREnable=1;VRROptimizeEnable=0;";
        string applied = SetPart(SetPart(user, "VRROptimizeEnable", "1"), "SwapEffectUpgradeEnable", "1");
        Check("DirectX: свои пары поставлены, AutoHDR остался", applied == "AutoHDREnable=1;VRROptimizeEnable=1;SwapEffectUpgradeEnable=1;");
        Check("DirectX: чтение пары", GetPart(applied, "SwapEffectUpgradeEnable") == "1" && GetPart(applied, "Нет") == null);
        string back = SetPart(SetPart(applied, "VRROptimizeEnable", "0"), "SwapEffectUpgradeEnable", null);
        Check("DirectX: возврат — исходная строка без лишних пар", back == user);
        Check("DirectX: пустая строка после возврата — значение удаляется", SetPart("VRROptimizeEnable=1;", "VRROptimizeEnable", null) == "");
        Check("копия: DWord", TryParseSaved("DWord:1", false, out var d, out var dk) && d is int i && i == 1 && dk == RegistryValueKind.DWord);
        Check("копия: «absent» — удалить", TryParseSaved(Absent, false, out var a, out _) && a == null);
        Check("копия: мусор отвергается", !TryParseSaved("DWord:x", false, out _, out _) && !TryParseSaved("Bogus:1", false, out _, out _)
                                           && !TryParseSaved("String:1;Evil=1", true, out _, out _));
        Check("мышь: разбор", ParseSpi("6,10,1") is { } spi && spi[1] == 10 && ParseSpi("1,2") == null && ParseSpi("1,2,999") == null);
        Check("защищённые процессы: SmartScreen и Defender", ProtectedProcesses.IsSecurity("smartscreen") && ProtectedProcesses.IsSecurity("msmpeng"));
        return (fail == null ? "OK" : "FAIL: " + fail) + Environment.NewLine + sb;
    }
}

/// <summary>Кнопка «Перед игрой»: поиск и закрытие тяжёлых программ.</summary>
public static class GameApps
{
    // Известные тяжёлые программы: название для пользователя -> имена процессов. Остальные находятся по памяти (см. Scan).
    private static readonly (string Title, string[] Procs)[] Targets =
    {
        ("Яндекс Браузер", new[] { "browser" }),
        ("Google Chrome", new[] { "chrome" }),
        ("Microsoft Edge", new[] { "msedge" }),
        ("Firefox", new[] { "firefox" }),
        ("Opera", new[] { "opera" }),
        ("Brave", new[] { "brave" }),
        ("Vivaldi", new[] { "vivaldi" }),
        ("Claude", new[] { "claude" }),
        ("ChatGPT", new[] { "ChatGPT" }),
        ("Gemini", new[] { "Gemini", "GeminiAppLauncher" }),
        ("Telegram", new[] { "Telegram" }),
        ("Spotify", new[] { "Spotify" }),
        ("Яндекс Музыка", new[] { "Яндекс Музыка" }),
        ("NVIDIA App (окно)", new[] { "NVIDIA App" }),
        ("Blender", new[] { "blender", "blender-launcher" }),
        ("Adobe Photoshop", new[] { "Photoshop" }),
        ("Adobe Premiere Pro", new[] { "Adobe Premiere Pro" }),
        ("Visual Studio Code", new[] { "Code" }),
        ("Visual Studio", new[] { "devenv" }),
    };

    // Никогда не закрываются: «Безопасность Windows» и античиты (общий список ProtectedProcesses), лаунчеры игр, Discord,
    // сама Windows и эта программа.
    private static readonly HashSet<string> Protected = new(ProtectedProcesses.Security, StringComparer.OrdinalIgnoreCase)
    {
        "Discord", "steam", "steamwebhelper", "EpicGamesLauncher", "EpicWebHelper", "RiotClientServices", "RiotClientUx", "RiotClientUxRender",
        "Battle.net", "Agent", "EADesktop", "EABackgroundService", "UbisoftConnect", "upc", "GalaxyClient", "AstrumPlay",
        "explorer", "dwm", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "TextInputHost", "ApplicationFrameHost",
        "SystemSettings", "Taskmgr", "PulsePC", "powershell", "pwsh", "WindowsTerminal", "cmd", "conhost", "obs64", "obs32",
    };

    /// <summary>Тяжёлое окно, не попавшее в известный список, предлагается к закрытию от этого объёма памяти.</summary>
    private const long HeavyBytes = 400L * 1024 * 1024;

    private static List<Process> Find(string[] names)
    {
        var result = new List<Process>();
        int self = Environment.ProcessId;
        foreach (var name in names)
        {
            if (Protected.Contains(name)) continue;
            foreach (var p in Process.GetProcessesByName(name))
            {
                if (p.Id == self) continue;
                if (name == "browser")
                {
                    // "browser" - слишком общее имя: берём только Яндекс Браузер.
                    string path = null;
                    try { path = p.MainModule?.FileName; } catch { }
                    if (path != null && path.IndexOf(@"\Yandex\", StringComparison.OrdinalIgnoreCase) < 0) continue;
                }
                result.Add(p);
            }
        }
        return result;
    }

    public static List<GameAppItem> Scan()
    {
        var list = new List<GameAppItem>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (title, procs) in Targets)
        {
            foreach (var n in procs) known.Add(n);
            var found = Find(procs);
            if (found.Count == 0) continue;
            long bytes = 0;
            foreach (var p in found) { try { bytes += p.WorkingSet64; } catch { } }
            list.Add(new GameAppItem { Title = L.T(title), ProcessName = string.Join("|", procs), Bytes = bytes });
        }

        // Любая другая программа с окном, занимающая много памяти: на чужом ПК известного списка мало.
        // Игры, лаунчеры, системные процессы и защищённые не предлагаются.
        try
        {
            var running = GameDetector.Find();
            foreach (var g in Process.GetProcesses().GroupBy(p => p.ProcessName))
            {
                if (known.Contains(g.Key) || Protected.Contains(g.Key)) continue;
                var withWindow = g.Where(p => { try { return p.MainWindowHandle != IntPtr.Zero && p.SessionId != 0; } catch { return false; } }).ToList();
                if (withWindow.Count == 0) continue;
                if (running != null && g.Any(p => p.Id == running.Pid)) continue;
                long bytes = 0;
                foreach (var p in g) { try { bytes += p.WorkingSet64; } catch { } }
                if (bytes < HeavyBytes) continue;
                string path = null;
                try { path = withWindow[0].MainModule?.FileName; } catch { }
                if (path != null && (path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)
                                     || GameDetector.LooksLikeGame(path))) continue;
                string title = g.Key;
                try { var d = path != null ? FileVersionInfo.GetVersionInfo(path).FileDescription : null; if (!string.IsNullOrWhiteSpace(d) && d.Length <= 40) title = d.Trim(); } catch { }
                list.Add(new GameAppItem { Title = title, ProcessName = g.Key, Bytes = bytes });
            }
        }
        catch { }
        return list.OrderByDescending(x => x.Bytes).ToList();
    }

    /// <summary>
    /// Чем открыть программу снова после игры: для приложений из Магазина — их идентификатор в оболочке,
    /// для обычных — путь к файлу. Берётся до закрытия, пока процесс ещё жив.
    /// </summary>
    public static string RestoreTarget(GameAppItem item)
    {
        var found = Find(item.ProcessName.Split('|'));
        // Главный процесс программы - тот, у которого есть окно; у вспомогательных его нет.
        foreach (var p in found.OrderByDescending(p => { try { return p.MainWindowHandle != IntPtr.Zero; } catch { return false; } }))
        {
            IntPtr h = OpenProcess(0x1000, false, p.Id);
            if (h == IntPtr.Zero) continue;
            try
            {
                uint length = 260;
                var id = new StringBuilder((int)length);
                if (GetApplicationUserModelId(h, ref length, id) == 0 && id.Length > 0 && id.ToString().Contains('!'))
                    return @"shell:AppsFolder\" + id;

                var path = new StringBuilder(1024);
                int size = path.Capacity;
                if (QueryFullProcessImageName(h, 0, path, ref size) && File.Exists(path.ToString())) return path.ToString();
            }
            catch { }
            finally { CloseHandle(h); }
        }
        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(IntPtr h, ref uint length, StringBuilder id);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    public static void Close(GameAppItem item)
    {
        string[] names = item.ProcessName.Split('|');

        // Сначала вежливо: закрыть окна, чтобы программа сохранила состояние (вкладки, проекты).
        for (int i = 0; i < 6; i++)
        {
            var withWindow = Find(names).Where(p => { try { return p.MainWindowHandle != IntPtr.Zero; } catch { return false; } }).ToList();
            if (withWindow.Count == 0) break;
            foreach (var p in withWindow) { try { p.CloseMainWindow(); } catch { } }
            Thread.Sleep(700);
        }
        // Что осталось в фоне (трей, фоновый режим) - завершить.
        Thread.Sleep(600);
        foreach (var p in Find(names)) { try { p.Kill(); } catch { } }
    }
}
