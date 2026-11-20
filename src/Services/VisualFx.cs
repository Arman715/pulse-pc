using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using PulsePC.Panels;

namespace PulsePC.Services;

/// <summary>Готовый набор эффектов оформления. Custom — состояние не совпало ни с одним набором.</summary>
public enum FxProfile { Custom, Pretty, Balance, Speed }

/// <summary>Как хранится одно системное значение, из которых складывается эффект.</summary>
internal enum FxKind
{
    /// <summary>SystemParametersInfo: чтение через указатель, запись значением в pvParam (диапазон 0x1000).</summary>
    SpiBool,
    /// <summary>SystemParametersInfo: чтение через указатель, запись значением в uiParam (старые параметры).</summary>
    SpiUi,
    /// <summary>SystemParametersInfo с ANIMATIONINFO: сворачивание и разворачивание окон.</summary>
    Animation,
    /// <summary>Число в реестре пользователя.</summary>
    Reg,
}

/// <summary>
/// Одно системное значение. Сырое значение — int?: для параметров системы 0 или 1 (null — прочитать не удалось),
/// для реестра — само число (null — значения нет). Master — эффект работает только при включённом общем
/// переключателе «эффекты интерфейса» (SPI_GETUIEFFECTS).
/// </summary>
internal sealed record FxSlot(string Key, FxKind Kind, uint Get = 0, uint Set = 0, string Path = null, string Name = null,
    int On = 1, int Off = 0, int Missing = 1, bool Master = false)
{
    public bool IsOn(int? raw) => Kind == FxKind.Reg ? (raw ?? Missing) == On : raw == 1;
    public int Raw(bool on) => Kind == FxKind.Reg ? (on ? On : Off) : (on ? 1 : 0);
}

/// <summary>Эффект оформления, как его видит пользователь: один переключатель, одно или несколько системных значений.</summary>
public sealed class FxEffect
{
    public string Title { get; init; }
    public string Hint { get; init; }
    /// <summary>Остаётся включённым в наборе «Баланс».</summary>
    public bool InBalance { get; init; }
    /// <summary>Остаётся включённым в наборе «Скорость».</summary>
    public bool InSpeed { get; init; }
    internal FxSlot[] Slots { get; init; }
}

/// <summary>Чтение и запись системных значений. Настоящая система или песочница для проверки.</summary>
internal interface IFxBackend
{
    int? Read(FxSlot slot);
    bool Write(FxSlot slot, int? raw);
    /// <summary>Сообщить программам, что значения в реестре изменились (ключи изменённых значений).</summary>
    void Notify(IReadOnlyCollection<string> changed);
}

/// <summary>
/// Визуальные эффекты Windows: анимации, прозрачность, тени, сглаживание шрифтов.
/// Всё это настройки пользователя (HKCU и параметры сеанса). Перед первым изменением исходные значения
/// записываются в файл, «Вернуть как было» возвращает именно их.
/// </summary>
public static class VisualFx
{
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    // Общий переключатель эффектов интерфейса: при выключенном не работают анимации меню, списков, подсказок и тень указателя.
    private static readonly FxSlot MasterSlot = new("uieffects", FxKind.SpiBool, 0x103E, 0x103F);
    // Что выбрано в окне «Параметры быстродействия»: 0 — на усмотрение Windows, 1 — лучший вид, 2 — быстродействие, 3 — особые.
    private static readonly FxSlot ModeSlot = new("mode", FxKind.Reg,
        Path: @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", Name: "VisualFXSetting", On: 3, Off: 0, Missing: 0);

    public static IReadOnlyList<FxEffect> Effects { get; } = new[]
    {
        new FxEffect
        {
            Title = L.T("Анимация окон при сворачивании и разворачивании"),
            Hint = L.T("Окно плавно уходит в панель задач и возвращается; без неё сворачивается сразу."),
            Slots = new[] { new FxSlot("minanimate", FxKind.Animation, 0x0048, 0x0049) },
        },
        new FxEffect
        {
            Title = L.T("Анимации внутри окон"),
            Hint = L.T("Плавное появление кнопок, списков и страниц внутри программ; в «Параметрах» это переключатель «Эффекты анимации»."),
            Slots = new[] { new FxSlot("clientanim", FxKind.SpiBool, 0x1042, 0x1043) },
        },
        new FxEffect
        {
            Title = L.T("Анимации панели задач"),
            Hint = L.T("Плавное появление значков и эскизов окон на панели задач."),
            Slots = new[] { new FxSlot("taskbaranim", FxKind.Reg, Path: Advanced, Name: "TaskbarAnimations") },
        },
        new FxEffect
        {
            Title = L.T("Прозрачность"),
            Hint = L.T("Полупрозрачные панель задач, меню «Пуск» и окна; размытие фона немного нагружает видеокарту."),
            Slots = new[] { new FxSlot("transparency", FxKind.Reg, Path: @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", Name: "EnableTransparency") },
        },
        new FxEffect
        {
            Title = L.T("Тени под окнами"),
            Hint = L.T("Тень по краю окна помогает отличать окна друг от друга."),
            InBalance = true,
            Slots = new[] { new FxSlot("dropshadow", FxKind.SpiBool, 0x1024, 0x1025) },
        },
        new FxEffect
        {
            Title = L.T("Тень под указателем"),
            Hint = L.T("Небольшая тень у стрелки мыши."),
            Slots = new[] { new FxSlot("cursorshadow", FxKind.SpiBool, 0x101A, 0x101B, Master: true) },
        },
        new FxEffect
        {
            Title = L.T("Сглаживание шрифтов"),
            Hint = L.T("Делает края букв ровными; без него текст рваный и читать тяжело, поэтому оно остаётся во всех наборах."),
            InBalance = true, InSpeed = true,
            Slots = new[] { new FxSlot("fontsmoothing", FxKind.SpiUi, 0x004A, 0x004B) },
        },
        new FxEffect
        {
            Title = L.T("Эскизы вместо значков"),
            Hint = L.T("В папках видны уменьшенные картинки и кадры видео, а не одинаковые значки."),
            InBalance = true,
            Slots = new[] { new FxSlot("iconsonly", FxKind.Reg, Path: Advanced, Name: "IconsOnly", On: 0, Off: 1, Missing: 0) },
        },
        new FxEffect
        {
            Title = L.T("Содержимое окна при перетаскивании"),
            Hint = L.T("Окно двигается целиком; без этого при перетаскивании видна только рамка."),
            InBalance = true,
            Slots = new[] { new FxSlot("dragfull", FxKind.SpiUi, 0x0026, 0x0025) },
        },
        new FxEffect
        {
            Title = L.T("Плавная прокрутка списков"),
            Hint = L.T("Списки прокручиваются и раскрываются плавно, а не рывком."),
            Slots = new[]
            {
                new FxSlot("listscroll", FxKind.SpiBool, 0x1006, 0x1007, Master: true),
                new FxSlot("comboanim", FxKind.SpiBool, 0x1004, 0x1005, Master: true),
            },
        },
        new FxEffect
        {
            Title = L.T("Затухание меню и подсказок"),
            Hint = L.T("Меню и всплывающие подсказки появляются и исчезают плавно, а не сразу."),
            Slots = new[]
            {
                new FxSlot("menuanim", FxKind.SpiBool, 0x1002, 0x1003, Master: true),
                new FxSlot("tooltipanim", FxKind.SpiBool, 0x1016, 0x1017, Master: true),
                new FxSlot("selectionfade", FxKind.SpiBool, 0x1014, 0x1015, Master: true),
            },
        },
        new FxEffect
        {
            Title = L.T("Просмотр рабочего стола (Aero Peek)"),
            Hint = L.T("Показывает рабочий стол сквозь окна, когда указатель стоит на правом крае панели задач."),
            InBalance = true,
            Slots = new[] { new FxSlot("peek", FxKind.Reg, Path: @"Software\Microsoft\Windows\DWM", Name: "EnableAeroPeek") },
        },
    };

    private static IEnumerable<FxSlot> EffectSlots => Effects.SelectMany(e => e.Slots);
    private static IEnumerable<FxSlot> AllSlots => EffectSlots.Append(MasterSlot).Append(ModeSlot);

    private static readonly IFxBackend Live = new LiveFxBackend("");
    private const string BackupName = "visual-effects-backup.txt";
    private static bool migrated;

    /// <summary>
    /// Копия «как было» — в защищённой папке (старая из папки пользователя переносится один раз). При чтении берутся
    /// только известные значения (ключи AllSlots) с числовым содержимым.
    /// </summary>
    private static string BackupFile
    {
        get
        {
            if (!migrated) { SecureStore.Migrate(BackupName); migrated = true; }
            return SecureStore.PathOf(BackupName);
        }
    }
    private static readonly object Gate = new();

    /// <summary>Оформление меняется только по кнопке и насовсем, временных изменений нет — убирать после сбоя нечего.</summary>
    public static void Init(PanelHost host) { }

    public static string ProfileName(FxProfile p) => p switch
    {
        FxProfile.Pretty => L.T("Красиво"),
        FxProfile.Balance => L.T("Баланс"),
        FxProfile.Speed => L.T("Скорость"),
        _ => L.T("свой набор"),
    };

    /// <summary>Положения переключателей для набора (по порядку Effects).</summary>
    public static bool[] ProfileStates(FxProfile p) => Effects.Select(e =>
        p == FxProfile.Pretty || (p == FxProfile.Balance && e.InBalance) || (p == FxProfile.Speed && e.InSpeed)).ToArray();

    /// <summary>С каким набором совпадают переключатели.</summary>
    public static FxProfile Match(IReadOnlyList<bool> state)
    {
        foreach (var p in new[] { FxProfile.Pretty, FxProfile.Balance, FxProfile.Speed })
            if (ProfileStates(p).SequenceEqual(state)) return p;
        return FxProfile.Custom;
    }

    /// <summary>«профиль «Баланс», включено 5 из 12» или «свой набор, включено 7 из 12».</summary>
    public static string Describe(IReadOnlyList<bool> state)
    {
        var p = Match(state);
        string name = p == FxProfile.Custom ? L.T("свой набор") : L.F("профиль «{0}»", ProfileName(p));
        return L.F("{0}, включено {1} из {2}", name, state.Count(x => x), state.Count);
    }

    // ---------- Чтение ----------

    /// <summary>Какие эффекты сейчас включены (по порядку Effects). Только чтение.</summary>
    public static bool[] Read() => Read(Live);

    internal static bool[] Read(IFxBackend backend)
    {
        var raw = Snapshot(backend);
        return Effects.Select(e => EffectOn(e, raw)).ToArray();
    }

    private static Dictionary<string, int?> Snapshot(IFxBackend backend) => AllSlots.ToDictionary(s => s.Key, s => backend.Read(s));

    /// <summary>Работает ли значение на деле: для зависимых — только при включённом общем переключателе.</summary>
    private static bool SlotOn(FxSlot slot, Dictionary<string, int?> raw) =>
        slot.IsOn(raw[slot.Key]) && (!slot.Master || raw[MasterSlot.Key] == 1);

    /// <summary>Эффект из нескольких значений считается включённым, если работает хотя бы одно.</summary>
    private static bool EffectOn(FxEffect e, Dictionary<string, int?> raw) => e.Slots.Any(s => SlotOn(s, raw));

    // ---------- Запись ----------

    public static bool HasBackup => File.Exists(BackupFile);

    /// <summary>
    /// Привести систему к выбранным положениям. Записываются только те эффекты, чьё положение отличается от текущего.
    /// Возвращает число изменённых эффектов и названия тех, что изменить не удалось.
    /// </summary>
    public static (int Changed, List<string> Failed) Apply(IReadOnlyList<bool> desired) => Apply(Live, BackupFile, desired);

    internal static (int Changed, List<string> Failed) Apply(IFxBackend backend, string backupFile, IReadOnlyList<bool> desired)
    {
        lock (Gate)
        {
            var failed = new List<string>();
            var before = Snapshot(backend);
            var changedEffects = Effects.Where((e, i) => EffectOn(e, before) != desired[i]).ToList();
            if (changedEffects.Count == 0) return (0, failed);

            // Исходное запоминается один раз: повторное применение не должно затереть настоящее «как было».
            if (!File.Exists(backupFile)) SaveBackup(backupFile, before);

            // Цель для каждого значения: у изменённых эффектов — новое положение, у остальных — то, что работает сейчас.
            var target = new Dictionary<string, bool>();
            for (int i = 0; i < Effects.Count; i++)
            {
                bool changed = changedEffects.Contains(Effects[i]);
                foreach (var s in Effects[i].Slots) target[s.Key] = changed ? desired[i] : SlotOn(s, before);
            }

            var written = new List<string>();
            // Зависимый эффект не заработает при выключенном общем переключателе — сначала включаем его.
            if (before[MasterSlot.Key] != 1 && EffectSlots.Any(s => s.Master && target[s.Key]))
            {
                if (backend.Write(MasterSlot, 1)) written.Add(MasterSlot.Key);
            }

            // После общего переключателя читаем заново: он мог изменить зависимые значения.
            var now = Snapshot(backend);
            bool masterOn = now[MasterSlot.Key] == 1;
            foreach (var e in Effects)
            {
                bool ok = true;
                foreach (var s in e.Slots)
                {
                    bool want = target[s.Key];
                    // При выключенном общем переключателе зависимые значения и так не работают — их не трогаем.
                    if (s.Master && !masterOn && !want) continue;
                    if (s.IsOn(now[s.Key]) == want) continue;
                    if (backend.Write(s, s.Raw(want))) written.Add(s.Key); else ok = false;
                }
                if (!ok) failed.Add(e.Title);
            }

            // Чтобы окно «Параметры быстродействия» показывало «Особые эффекты», а не старый выбор.
            if (written.Count > 0 && now[ModeSlot.Key] != 3 && backend.Write(ModeSlot, 3)) written.Add(ModeSlot.Key);
            if (written.Count > 0) backend.Notify(written);
            return (changedEffects.Count - failed.Count, failed);
        }
    }

    /// <summary>Вернуть значения, записанные в копию перед первым применением. Копия после этого удаляется.</summary>
    public static (bool Ok, string Message) Restore() => Restore(Live, BackupFile);

    internal static (bool Ok, string Message) Restore(IFxBackend backend, string backupFile)
    {
        lock (Gate)
        {
            if (!File.Exists(backupFile)) return (false, L.T("Копии «как было» нет: программа ещё ничего не меняла."));
            var saved = LoadBackup(backupFile);
            if (saved.Count == 0) return (false, L.T("Копия «как было» не читается. Настройте эффекты вручную и нажмите «Применить»."));

            var written = new List<string>();
            int failed = 0;
            // Сначала общий переключатель: он может сдвинуть зависимые значения, они выставляются следом.
            foreach (var slot in AllSlots.OrderBy(s => s == MasterSlot ? 0 : 1))
            {
                if (!saved.TryGetValue(slot.Key, out int? was)) continue;
                if (slot.Kind != FxKind.Reg && was == null) continue;   // тогда прочитать не удалось — возвращать нечего
                if (backend.Read(slot) == was) continue;
                if (backend.Write(slot, was)) written.Add(slot.Key); else failed++;
            }
            if (written.Count > 0) backend.Notify(written);
            if (failed > 0) return (false, L.F("Не удалось вернуть {0}. Копия сохранена, попробуйте ещё раз.", Fmt.Plural(failed, "значение", "значения", "значений")));
            File.Delete(backupFile);
            return (true, L.T("Оформление возвращено таким, каким было до первого изменения."));
        }
    }

    private static void SaveBackup(string file, Dictionary<string, int?> raw)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        // Через временный файл: обрыв посреди записи не оставит обрезанную копию, по которой «Вернуть» вернуло бы не всё.
        AtomicFile.WriteAllText(file, string.Join(Environment.NewLine, raw.Select(p => $"{p.Key}={(p.Value == null ? "-" : p.Value.Value.ToString())}")) + Environment.NewLine);
    }

    private static Dictionary<string, int?> LoadBackup(string file)
    {
        var result = new Dictionary<string, int?>();
        var known = new HashSet<string>(AllSlots.Select(s => s.Key), StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(file))
        {
            int i = line.IndexOf('=');
            if (i <= 0 || !known.Contains(line[..i])) continue;
            string value = line[(i + 1)..].Trim();
            if (value == "-") result[line[..i]] = null;
            else if (int.TryParse(value, out int n)) result[line[..i]] = n;
        }
        return result;
    }

    // ---------- Самопроверка (только чтение) ----------

    public static string SelfTest()
    {
        var sb = new StringBuilder();
        var problems = new List<string>();

        var raw = Snapshot(Live);
        foreach (var s in AllSlots.Where(s => s.Kind != FxKind.Reg && raw[s.Key] == null))
            problems.Add("не читается параметр " + s.Key);

        var state = Effects.Select(e => EffectOn(e, raw)).ToArray();
        sb.AppendLine("Сейчас: " + Describe(state));
        for (int i = 0; i < Effects.Count; i++)
        {
            string parts = string.Join(", ", Effects[i].Slots.Select(s => $"{s.Key}={(raw[s.Key] == null ? "нет" : raw[s.Key].ToString())}"));
            sb.AppendLine($"  [{(state[i] ? "вкл " : "выкл")}] {Effects[i].Title}  ({parts})");
        }
        sb.AppendLine($"Общий переключатель эффектов интерфейса: {(raw[MasterSlot.Key] == 1 ? "включён" : "выключен")}");
        sb.AppendLine("Выбор в «Параметрах быстродействия» (VisualFXSetting): " + (raw[ModeSlot.Key] == null ? "не задан" : raw[ModeSlot.Key].ToString()));
        sb.AppendLine("Копия «как было»: " + (HasBackup ? BackupFile : "нет, программа оформление не меняла"));

        // Сверка с реестром: параметры диапазона 0x1000 Windows хранит битами в UserPreferencesMask (бит = (код − 0x1000) / 2).
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            if (key?.GetValue("UserPreferencesMask") is byte[] mask)
            {
                int same = 0, total = 0;
                var diff = new List<string>();
                foreach (var s in AllSlots.Where(s => s.Kind == FxKind.SpiBool && raw[s.Key] != null))
                {
                    int bit = (int)(s.Get - 0x1000) / 2;
                    if (bit / 8 >= mask.Length) continue;
                    total++;
                    bool inMask = (mask[bit / 8] & (1 << (bit % 8))) != 0;
                    if (inMask == (raw[s.Key] == 1)) same++; else diff.Add(s.Key);
                }
                sb.AppendLine($"Сверка с UserPreferencesMask ({BitConverter.ToString(mask)}): совпало {same} из {total}"
                              + (diff.Count > 0 ? ", расходятся: " + string.Join(", ", diff) : ""));
            }
            else sb.AppendLine("UserPreferencesMask в реестре нет — сверка пропущена.");
        }
        catch (Exception ex) { sb.AppendLine("Сверка с UserPreferencesMask не удалась: " + ex.Message); }

        // Логика наборов — без обращения к системе.
        if (Effects.Count != 12) problems.Add("эффектов должно быть 12, а их " + Effects.Count);
        if (AllSlots.Select(s => s.Key).Distinct().Count() != AllSlots.Count()) problems.Add("ключи значений повторяются");
        if (Match(ProfileStates(FxProfile.Pretty)) != FxProfile.Pretty || Match(ProfileStates(FxProfile.Balance)) != FxProfile.Balance
            || Match(ProfileStates(FxProfile.Speed)) != FxProfile.Speed) problems.Add("набор не узнаёт сам себя");
        int font = Effects.ToList().FindIndex(e => e.Slots[0].Key == "fontsmoothing");
        if (!ProfileStates(FxProfile.Speed)[font] || ProfileStates(FxProfile.Speed).Count(x => x) != 1) problems.Add("в «Скорости» должно остаться только сглаживание шрифтов");
        if (!ProfileStates(FxProfile.Pretty).All(x => x)) problems.Add("в «Красиво» должны быть включены все эффекты");
        sb.AppendLine("Наборы: «Красиво» " + ProfileStates(FxProfile.Pretty).Count(x => x) + ", «Баланс» " + ProfileStates(FxProfile.Balance).Count(x => x)
                      + ", «Скорость» " + ProfileStates(FxProfile.Speed).Count(x => x) + " из " + Effects.Count);

        return (problems.Count == 0 ? "OK" : "FAIL: " + string.Join("; ", problems)) + "\n" + sb;
    }
}

/// <summary>
/// Настоящая система: параметры сеанса через SystemParametersInfo (с записью в профиль пользователя и рассылкой
/// WM_SETTINGCHANGE), остальное — числа в реестре пользователя. regRoot — приставка к пути в реестре:
/// пустая для настоящей работы, «Software\PulsePC-Test\» для проверки.
/// </summary>
internal sealed class LiveFxBackend : IFxBackend
{
    private readonly string regRoot;
    public LiveFxBackend(string regRoot) => this.regRoot = regRoot ?? "";

    private const uint UpdateAndSend = 0x1 | 0x2;   // SPIF_UPDATEINIFILE | SPIF_SENDCHANGE

    public int? Read(FxSlot slot)
    {
        try
        {
            switch (slot.Kind)
            {
                case FxKind.Reg:
                    return RegRead(slot);
                case FxKind.Animation:
                    var info = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>() };
                    return SystemParametersInfo(slot.Get, info.cbSize, ref info, 0) ? (info.iMinAnimate != 0 ? 1 : 0) : null;
                default:
                    int value = 0;
                    return SystemParametersInfo(slot.Get, 0, ref value, 0) ? (value != 0 ? 1 : 0) : null;
            }
        }
        catch { return null; }
    }

    public bool Write(FxSlot slot, int? raw)
    {
        try
        {
            switch (slot.Kind)
            {
                case FxKind.Reg:
                    RegWrite(slot, raw);
                    return true;
                case FxKind.Animation:
                    var info = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>(), iMinAnimate = raw == 1 ? 1 : 0 };
                    return SystemParametersInfo(slot.Set, info.cbSize, ref info, UpdateAndSend);
                case FxKind.SpiUi:
                    return SystemParametersInfo(slot.Set, raw == 1 ? 1u : 0u, IntPtr.Zero, UpdateAndSend);
                default:
                    return SystemParametersInfo(slot.Set, 0, (IntPtr)(raw == 1 ? 1 : 0), UpdateAndSend);
            }
        }
        catch { return false; }
    }

    public int? RegRead(FxSlot slot)
    {
        using var key = Registry.CurrentUser.OpenSubKey(regRoot + slot.Path);
        return key?.GetValue(slot.Name) is int n ? n : null;
    }

    public void RegWrite(FxSlot slot, int? raw)
    {
        if (raw == null)
        {
            using var key = Registry.CurrentUser.OpenSubKey(regRoot + slot.Path, true);
            key?.DeleteValue(slot.Name, false);
        }
        else
        {
            using var key = Registry.CurrentUser.CreateSubKey(regRoot + slot.Path, true);
            key.SetValue(slot.Name, raw.Value, RegistryValueKind.DWord);
        }
    }

    /// <summary>
    /// Параметры сеанса рассылают уведомление сами (SPIF_SENDCHANGE). Значениям в реестре нужно отдельное:
    /// «ImmersiveColorSet» — для прозрачности, общее — для Проводника и панели задач.
    /// Рассылка с ограничением по времени, чтобы зависшее чужое окно не остановило программу.
    /// </summary>
    public void Notify(IReadOnlyCollection<string> changed)
    {
        if (regRoot.Length > 0) return;   // песочница: настоящим программам сообщать нечего
        try
        {
            if (changed.Contains("transparency"))
                SendMessageTimeout(Broadcast, WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet", SMTO_ABORTIFHUNG, 1500, out _);
            if (changed.Contains("taskbaranim") || changed.Contains("iconsonly") || changed.Contains("peek") || changed.Contains("mode"))
                SendMessageTimeout(Broadcast, WM_SETTINGCHANGE, IntPtr.Zero, null, SMTO_ABORTIFHUNG, 1500, out _);
            // Проводник перечитывает вид папок (эскизы или значки) по этому событию.
            if (changed.Contains("iconsonly")) SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);   // SHCNE_ASSOCCHANGED, без ожидания
        }
        catch { }
    }

    private static readonly IntPtr Broadcast = (IntPtr)0xFFFF;
    private const uint WM_SETTINGCHANGE = 0x001A, SMTO_ABORTIFHUNG = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct ANIMATIONINFO { public uint cbSize; public int iMinAnimate; }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint winIni);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref ANIMATIONINFO value, uint winIni);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeoutMs, out IntPtr result);
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
