using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Text;
using System.Windows.Interop;
using Microsoft.Win32;
using PulsePC.Panels;

namespace PulsePC.Services;

/// <summary>Состояние системных «улучшений» звука у устройства.</summary>
public enum SysFxState { Unknown, On, Off }

/// <summary>Звуковое устройство, как его показывает Windows.</summary>
public sealed class AudioDevice
{
    public string Id { get; init; } = "";
    /// <summary>Понятное имя: «Наушники (название гарнитуры)».</summary>
    public string Name { get; init; } = "";
    /// <summary>Основное для программ и игр.</summary>
    public bool IsDefault { get; init; }
    /// <summary>Основное для связи (звонки, голосовые чаты).</summary>
    public bool IsDefaultComm { get; init; }
    /// <summary>«48 000 Гц, 24 бит, стерео» или пустая строка, если формат не читается.</summary>
    public string Format { get; init; } = "";
    public SysFxState Enhancements { get; init; }
    /// <summary>Только для устройств записи: включено ли «Прослушивать с данного устройства».</summary>
    public bool Listening { get; init; }
}

/// <summary>Сочетание клавиш для переключения звука: Ctrl / Alt / Shift / Win и одна клавиша из списка.</summary>
public readonly record struct SoundHotkey(bool Ctrl, bool Alt, bool Shift, bool Win, string Key)
{
    /// <summary>Клавиши, из которых можно выбирать: обозначение для настроек, подпись, код клавиши Windows.</summary>
    public static readonly IReadOnlyList<(string Token, string Title, int Vk)> Keys = BuildKeys();

    private static List<(string, string, int)> BuildKeys()
    {
        var list = new List<(string, string, int)>();
        for (int i = 1; i <= 12; i++) list.Add(("F" + i, "F" + i, 0x70 + i - 1));
        for (char c = 'A'; c <= 'Z'; c++) list.Add((c.ToString(), c.ToString(), c));
        for (char c = '0'; c <= '9'; c++) list.Add((c.ToString(), c.ToString(), c));
        for (int i = 0; i <= 9; i++) list.Add(("Num" + i, "Num " + i, 0x60 + i));
        list.Add(("NumMul", "Num *", 0x6A));
        list.Add(("NumAdd", "Num +", 0x6B));
        list.Add(("NumSub", "Num −", 0x6D));
        list.Add(("NumDiv", "Num /", 0x6F));
        return list;
    }

    public bool IsEmpty => string.IsNullOrEmpty(Key);

    /// <summary>Код клавиши Windows; 0 — клавиши нет в списке.</summary>
    public int Vk
    {
        get
        {
            string key = Key;
            foreach (var k in Keys) if (string.Equals(k.Token, key, StringComparison.OrdinalIgnoreCase)) return k.Vk;
            return 0;
        }
    }

    /// <summary>Флаги для RegisterHotKey: MOD_ALT 1, MOD_CONTROL 2, MOD_SHIFT 4, MOD_WIN 8.</summary>
    public uint Modifiers => (Alt ? 1u : 0u) | (Ctrl ? 2u : 0u) | (Shift ? 4u : 0u) | (Win ? 8u : 0u);

    private bool IsNumpad => !IsEmpty && Key.StartsWith("Num", StringComparison.OrdinalIgnoreCase);
    private bool IsFunction => !IsEmpty && Key.Length >= 2 && (Key[0] == 'F' || Key[0] == 'f') && char.IsDigit(Key[1]);

    private IEnumerable<string> Parts(string keyText)
    {
        if (Ctrl) yield return "Ctrl";
        if (Alt) yield return "Alt";
        if (Shift) yield return "Shift";
        if (Win) yield return "Win";
        yield return keyText;
    }

    /// <summary>Вид для файла настроек: «Ctrl+Alt+F10».</summary>
    public override string ToString() => IsEmpty ? "" : string.Join("+", Parts(Key));

    /// <summary>Вид для человека: «Ctrl + Alt + F10».</summary>
    public string Text
    {
        get
        {
            if (IsEmpty) return "";
            string key = Key, title = key;
            foreach (var k in Keys) if (string.Equals(k.Token, key, StringComparison.OrdinalIgnoreCase)) title = k.Title;
            return string.Join(" + ", Parts(title));
        }
    }

    /// <summary>Разбор строки из настроек. Непонятная строка — пустое сочетание.</summary>
    public static SoundHotkey Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        bool ctrl = false, alt = false, shift = false, win = false;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" when !ctrl: ctrl = true; break;
                case "alt" when !alt: alt = true; break;
                case "shift" when !shift: shift = true; break;
                case "win" when !win: win = true; break;
                default: return default;
            }
        }
        string last = parts[^1];
        foreach (var k in Keys)
            if (string.Equals(k.Token, last, StringComparison.OrdinalIgnoreCase)) return new SoundHotkey(ctrl, alt, shift, win, k.Token);
        return default;
    }

    /// <summary>Почему такое сочетание нельзя назначить; null — можно.</summary>
    public string Problem()
    {
        if (IsEmpty || Vk == 0) return L.T("Выберите клавишу.");
        if (IsNumpad && Shift)
            return L.T("С клавишами цифрового блока Shift не срабатывает: Windows принимает такое нажатие за стрелки. Снимите флажок Shift.");
        if (!IsFunction && !Ctrl && !Alt && !Win)
            return L.T("Буква или цифра без Ctrl, Alt или Win перестанет печататься во всех программах. Добавьте Ctrl, Alt или Win.");
        if (string.Equals(Key, "F12", StringComparison.OrdinalIgnoreCase) && Modifiers == 0)
            return L.T("Клавишу F12 без Ctrl, Alt или Shift Windows оставляет себе. Добавьте одну из них.");
        return null;
    }
}

/// <summary>
/// «Улучшения» звука устройства: обработка Windows и драйвера (виртуальный объём, выравнивание громкости и т. п.).
/// Windows хранит выключатель в отдельном хранилище обработки звука (FxProperties), а не в общем хранилище свойств устройства:
/// IMMDevice::OpenPropertyStore его не показывает. Поэтому значение читается и записывается там, куда его кладут
/// «Параметры» и окно «Звук». Прежнее значение запоминается в файл, откат возвращает именно его.
/// </summary>
public sealed class AudioEnhancements
{
    /// <summary>PKEY_AudioEndpoint_Disable_SysFx: 1 — обработка выключена, 0 или нет значения — включена.</summary>
    public const string ValueName = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";

    private readonly RegistryHive hive;
    private readonly string root;
    private readonly string backupFile;

    /// <param name="root">Раздел со звуковыми устройствами (в нём Render и Capture). Параметр нужен для проверки в «песочнице».</param>
    public AudioEnhancements(RegistryHive hive, string root, string backupFile)
    {
        this.hive = hive; this.root = root; this.backupFile = backupFile;
    }

    /// <summary>Путь к хранилищу обработки звука устройства по его идентификатору вида «{0.0.0.00000000}.{…}».</summary>
    internal string KeyPath(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        int cut = id.IndexOf("}.{", StringComparison.Ordinal);
        if (cut < 0 || !id.EndsWith('}')) return null;
        if (!Guid.TryParse(id[(cut + 2)..], out Guid guid)) return null;
        var head = id[..cut].Split('.');
        string flow = head.Length >= 3 && head[2] == "1" ? "Capture" : "Render";
        return $@"{root}\{flow}\{guid:B}\FxProperties";
    }

    private RegistryKey Open(string id, bool write)
    {
        string path = KeyPath(id);
        if (path == null) return null;
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        // На запись просим только «читать и изменять значения»: создавать подразделы здесь не разрешено даже администратору,
        // и обычное открытие «на запись» получило бы отказ.
        return write
            ? baseKey.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.QueryValues | RegistryRights.SetValue)
            : baseKey.OpenSubKey(path, false);
    }

    public SysFxState Read(string id)
    {
        try
        {
            using var key = Open(id, false);
            if (key == null) return SysFxState.Unknown;
            object v = key.GetValue(ValueName);
            if (v == null) return SysFxState.On;
            return v is int i ? (i == 0 ? SysFxState.On : i == 1 ? SysFxState.Off : SysFxState.Unknown) : SysFxState.Unknown;
        }
        catch { return SysFxState.Unknown; }
    }

    /// <summary>Можно ли менять настройку на этой системе. Ничего не записывает: только открывает хранилище с правом записи.</summary>
    public bool CanWrite(string id)
    {
        if (Read(id) == SysFxState.Unknown) return false;
        try { using var key = Open(id, true); return key != null; }
        catch { return false; }
    }

    public bool HasBackup => LoadBackup().Count > 0;

    /// <summary>Выключить (или включить) улучшения у устройства. Прежнее значение запоминается при первом изменении.</summary>
    public bool Set(string id, bool disabled, out string error)
    {
        error = null;
        try
        {
            using var key = Open(id, true);
            if (key == null) { error = L.T("Windows не хранит настройку улучшений для этого устройства."); return false; }
            object was = key.GetValue(ValueName);
            if (was != null && was is not int) { error = L.T("Настройка улучшений записана в незнакомом виде, менять её небезопасно."); return false; }
            int? old = was == null ? null : (int)was;
            if ((old == 1) == disabled) return true;
            Remember(id, old);
            key.SetValue(ValueName, disabled ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            error = L.T("Windows не разрешила изменить настройку этого устройства.");
            return false;
        }
        if ((Read(id) == SysFxState.Off) != disabled) { error = L.T("Настройка записана, но Windows показывает прежнее значение."); return false; }
        return true;
    }

    /// <summary>Вернуть значения, которые были до первого изменения. Возвращает число восстановленных устройств.</summary>
    public int Restore(out string error)
    {
        error = null;
        var saved = LoadBackup();
        var failed = new List<(string Id, int? Old)>();
        int done = 0;
        foreach (var (id, old) in saved)
        {
            try
            {
                using var key = Open(id, true);
                if (key == null) continue;      // устройство удалено из системы — возвращать нечего
                if (old == null) key.DeleteValue(ValueName, false);
                else key.SetValue(ValueName, old.Value, RegistryValueKind.DWord);
                done++;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
            {
                failed.Add((id, old));
            }
        }
        try
        {
            if (failed.Count == 0) File.Delete(backupFile);
            else File.WriteAllLines(backupFile, failed.Select(Line));
        }
        catch { }
        if (failed.Count > 0) error = L.F("Windows не разрешила вернуть настройку: {0}.", Fmt.Plural(failed.Count, "устройство", "устройства", "устройств"));
        return done;
    }

    private static string Line((string Id, int? Old) x) => x.Id + "|" + (x.Old == null ? "none" : x.Old.Value.ToString(CultureInfo.InvariantCulture));

    internal List<(string Id, int? Old)> LoadBackup()
    {
        var list = new List<(string, int?)>();
        try
        {
            if (!File.Exists(backupFile)) return list;
            foreach (var line in File.ReadAllLines(backupFile))
            {
                var p = line.Split('|');
                if (p.Length != 2 || p[0].Length == 0) continue;
                if (p[1] == "none") list.Add((p[0], null));
                else if (int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) list.Add((p[0], v));
            }
        }
        catch { }
        return list;
    }

    // Запоминаем исходное только один раз: повторное применение не должно затереть настоящее «как было».
    private void Remember(string id, int? old)
    {
        var saved = LoadBackup();
        if (saved.Any(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))) return;
        saved.Add((id, old));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(backupFile)));
        File.WriteAllLines(backupFile, saved.Select(Line));
    }
}

/// <summary>
/// Звук: устройства вывода, основное устройство, переключение основного горячей клавишей, «улучшения», микрофон.
/// Устройства читаются через Core Audio (IMMDeviceEnumerator). Основное устройство меняется через IPolicyConfig —
/// недокументированный, но общепринятый интерфейс Windows (им пользуются SoundSwitch, EarTrumpet, AudioSwitcher).
/// Сама служба ничего не меняет без явного действия: кнопки в разделе или нажатия назначенной клавиши.
/// </summary>
public static class AudioDevices
{
    private const int Render = 0, Capture = 1;
    private const int RoleConsole = 0, RoleMultimedia = 1, RoleCommunications = 2;
    private const int DeviceStateActive = 1;
    private const int StgmRead = 0;
    private const int NotFound = unchecked((int)0x80070490);
    private const int NoInterface = unchecked((int)0x80004002);

    private static PanelHost host;

    /// <summary>Улучшения звука на настоящей системе.</summary>
    public static AudioEnhancements Enhancements { get; } = new(RegistryHive.LocalMachine,
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio", SecureBackup("sound-enhancements-backup.txt"));

    /// <summary>
    /// Копия «как было» в защищённой папке: по ней программа с правами администратора пишет в HKLM. Старая копия из папки
    /// пользователя переносится один раз; строки при чтении проверяются (идентификатор устройства разбирается как GUID).
    /// </summary>
    private static string SecureBackup(string name)
    {
        SecureStore.Migrate(name);
        return SecureStore.PathOf(name);
    }

    // ───────────────────────── запуск ─────────────────────────

    /// <summary>Один раз при запуске программы, в потоке интерфейса: скрытое окно для горячей клавиши и сама клавиша, если она задана.</summary>
    public static void Init(PanelHost panelHost)
    {
        host = panelHost;
        ActiveHotkey = default;
        HotkeyProblem = null;
        try
        {
            EnsureWindow();
            var saved = SavedHotkey(host.Settings);
            if (saved.IsEmpty || SavedCycle(host.Settings).Count < 2) return;
            int code = Registrar(saved);
            if (code == 0) ActiveHotkey = saved;
            else HotkeyProblem = RegisterError(saved, code);
        }
        catch (Exception ex) { HotkeyProblem = L.F("Горячая клавиша не включена: {0}", ex.Message); }
    }

    /// <summary>Раздел сообщает свой контекст, если Init ещё не вызывали (например, при проверке вида). Ничего не меняет.</summary>
    public static void Bind(PanelHost panelHost) => host ??= panelHost;

    // ───────────────────────── устройства ─────────────────────────

    /// <summary>Включённые устройства вывода: основное первым, остальные по имени.</summary>
    public static List<AudioDevice> Outputs() => ListDevices(Render, out _);

    /// <summary>Основное устройство записи или null, если микрофона нет.</summary>
    public static AudioDevice DefaultInput() => ListDevices(Capture, out _).FirstOrDefault(d => d.IsDefault);

    /// <summary>Название устройства по идентификатору, даже если оно сейчас отключено; null — Windows его больше не знает.</summary>
    public static string NameOf(string id)
    {
        IMMDeviceEnumerator enumerator = null;
        IMMDevice device = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDevice(id, out device) < 0 || device == null) return null;
            string name = ReadProperties(device).Name;
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch { return null; }
        finally { Release(device); Release(enumerator); }
    }

    internal static List<AudioDevice> ListDevices(int flow, out string error)
    {
        error = null;
        var list = new List<AudioDevice>();
        IMMDeviceEnumerator enumerator = null;
        IMMDeviceCollection devices = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            string main = DefaultId(enumerator, flow, RoleConsole) ?? DefaultId(enumerator, flow, RoleMultimedia);
            string comm = DefaultId(enumerator, flow, RoleCommunications);
            int hr = enumerator.EnumAudioEndpoints(flow, DeviceStateActive, out devices);
            if (hr < 0) { error = $"Windows не отдала список устройств (код 0x{hr:X8})."; return list; }
            devices.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                IMMDevice device = null;
                try
                {
                    if (devices.Item(i, out device) < 0 || device == null) continue;
                    if (device.GetId(out string id) < 0 || string.IsNullOrEmpty(id)) continue;
                    var props = ReadProperties(device);
                    list.Add(new AudioDevice
                    {
                        Id = id,
                        Name = string.IsNullOrWhiteSpace(props.Name) ? L.T("Устройство без названия") : props.Name.Trim(),
                        IsDefault = SameId(id, main),
                        IsDefaultComm = SameId(id, comm),
                        Format = props.Format,
                        Listening = props.Listening,
                        Enhancements = flow == Render ? Enhancements.Read(id) : SysFxState.Unknown,
                    });
                }
                catch { }
                finally { Release(device); }
            }
        }
        catch (Exception ex) { error = "Служба звука Windows не отвечает: " + ex.Message; }
        finally { Release(devices); Release(enumerator); }
        return list.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string DefaultId(IMMDeviceEnumerator enumerator, int flow, int role)
    {
        IMMDevice device = null;
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(flow, role, out device) < 0 || device == null) return null;
            return device.GetId(out string id) < 0 ? null : id;
        }
        catch { return null; }
        finally { Release(device); }
    }

    /// <summary>Идентификатор основного устройства вывода или null.</summary>
    public static string DefaultOutputId()
    {
        IMMDeviceEnumerator enumerator = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            return DefaultId(enumerator, Render, RoleConsole);
        }
        catch { return null; }
        finally { Release(enumerator); }
    }

    private static readonly PropertyKey KeyFriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
    private static readonly PropertyKey KeyDeviceDesc = new("a45c254e-df1c-4efd-8020-67d146a850e0", 2);
    private static readonly PropertyKey KeyDeviceFormat = new("f19f064d-082c-4e27-bc73-6882a1bb8e4c", 0);
    /// <summary>Флажок «Прослушивать с данного устройства» в свойствах микрофона.</summary>
    private static readonly PropertyKey KeyListen = new("24dbb0fc-9311-4b3d-9cf0-18ff155639d4", 1);

    private const ushort VtBool = 11, VtLpwstr = 31, VtBlob = 65;

    private static (string Name, string Format, bool Listening) ReadProperties(IMMDevice device)
    {
        IPropertyStore store = null;
        try
        {
            if (device.OpenPropertyStore(StgmRead, out store) < 0 || store == null) return ("", "", false);
            string name = ReadString(store, KeyFriendlyName);
            if (string.IsNullOrWhiteSpace(name)) name = ReadString(store, KeyDeviceDesc);

            string format = "";
            var key = KeyDeviceFormat;
            if (store.GetValue(ref key, out PropVariant pv) >= 0)
            {
                try
                {
                    int size = (int)(pv.a.ToInt64() & 0xFFFFFFFF);
                    if (pv.vt == VtBlob && pv.b != IntPtr.Zero && size >= 16 && size <= 4096)
                    {
                        var bytes = new byte[size];
                        Marshal.Copy(pv.b, bytes, 0, size);
                        format = DescribeFormat(bytes);
                    }
                }
                finally { PropVariantClear(ref pv); }
            }

            bool listening = false;
            key = KeyListen;
            if (store.GetValue(ref key, out pv) >= 0)
            {
                listening = pv.vt == VtBool && (pv.a.ToInt64() & 0xFFFF) != 0;
                PropVariantClear(ref pv);
            }
            return (name ?? "", format, listening);
        }
        catch { return ("", "", false); }
        finally { Release(store); }
    }

    private static string ReadString(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out PropVariant pv) < 0) return null;
        try { return pv.vt == VtLpwstr && pv.a != IntPtr.Zero ? Marshal.PtrToStringUni(pv.a) : null; }
        finally { PropVariantClear(ref pv); }
    }

    /// <summary>Формат устройства из структуры WAVEFORMATEX(TENSIBLE): «48 000 Гц, 24 бит, стерео». Пустая строка — не разобрать.</summary>
    internal static string DescribeFormat(byte[] wave)
    {
        if (wave == null || wave.Length < 16) return "";
        int tag = BitConverter.ToUInt16(wave, 0);
        int channels = BitConverter.ToUInt16(wave, 2);
        int rate = BitConverter.ToInt32(wave, 4);
        int bits = BitConverter.ToUInt16(wave, 14);
        // В расширенном формате настоящая разрядность лежит отдельно: 24 бита часто хранятся в 32-битной ячейке.
        if (tag == 0xFFFE && wave.Length >= 20)
        {
            int valid = BitConverter.ToUInt16(wave, 18);
            if (valid > 0 && valid <= bits) bits = valid;
        }
        if (rate <= 0 || bits <= 0 || channels <= 0) return "";
        string layout = channels switch
        {
            1 => L.T("моно"),
            2 => L.T("стерео"),
            6 => "5.1",
            8 => "7.1",
            _ => Fmt.Plural(channels, "канал", "канала", "каналов"),
        };
        return L.F("{0:#,0}\u00A0Гц, {1}\u00A0бит, {2}", rate, bits, layout);
    }

    internal static bool SameId(string a, string b) => a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ───────────────────────── основное устройство ─────────────────────────

    /// <summary>
    /// Сделать устройство основным для всех трёх ролей Windows: программы и игры, мультимедиа, связь.
    /// Вызывается только по явному действию пользователя: кнопка в разделе или назначенная клавиша.
    /// </summary>
    public static bool SetDefault(string id, out string error)
    {
        error = null;
        object client = null;
        try
        {
            client = new PolicyConfigClient();
            foreach (int role in new[] { RoleConsole, RoleMultimedia, RoleCommunications })
            {
                int hr = SetDefaultEndpoint(client, id, role);
                if (hr >= 0) continue;
                error = hr == NotFound ? L.T("Устройство отключено или выключено в Windows.")
                    : hr == NoInterface ? L.T("Эта версия Windows не даёт программам переключать звук. Переключите устройство в «Параметрах».")
                    : L.F("Windows не переключила устройство (код 0x{0:X8}).", hr);
                return false;
            }
        }
        catch (Exception ex) { error = L.F("Windows не дала переключить устройство: {0}", ex.Message); return false; }
        finally { Release(client); }

        // Windows может сообщить о смене с небольшим опозданием: спрашиваем несколько раз.
        for (int attempt = 0; attempt < 6; attempt++)
        {
            if (SameId(DefaultOutputId(), id)) return true;
            Thread.Sleep(50);
        }
        error = L.T("Windows приняла команду, но основное устройство осталось прежним.");
        return false;
    }

    // У интерфейса один и тот же порядок методов, но в разных сборках Windows 10 у него были разные идентификаторы.
    // Основной (Windows 7 и новее, включая Windows 11) — F8679F50…; остальные перечислены в SoundSwitch и AudioSwitcher.
    private static readonly (string Name, Guid Iid)[] OtherPolicyConfigs =
    {
        ("IPolicyConfig (вариант SoundSwitch)", new Guid("8F9FB2AA-1C0B-4D54-B6BB-B2F2A10CE03C")),
        ("IPolicyConfig (Windows 10, вариант 3)", new Guid("5731289F-3E89-4209-86BE-6599A8E05E67")),
        ("IPolicyConfig (Windows 10, вариант 2)", new Guid("098FF37B-1062-4B1A-AD73-2A2D530FEAB6")),
        ("IPolicyConfig (Windows 10, вариант 1)", new Guid("00632A31-4D49-4167-8AE1-27F82CE135B1")),
        ("IPolicyConfig (Windows 10)", new Guid("CA286FC3-91FD-42C3-8E9B-CAAFA66242E3")),
    };

    /// <summary>Номер SetDefaultEndpoint в таблице методов: три метода IUnknown и десять методов перед ним.</summary>
    internal const int SetDefaultEndpointSlot = 3 + 10;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetDefaultEndpointCall(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string id, int role);

    private static int SetDefaultEndpoint(object client, string id, int role)
    {
        if (client is IPolicyConfig config) return config.SetDefaultEndpoint(id, role);

        IntPtr unknown = Marshal.GetIUnknownForObject(client);
        try
        {
            foreach (var (_, iid) in OtherPolicyConfigs)
            {
                if (Marshal.QueryInterface(unknown, in iid, out IntPtr face) < 0) continue;
                try { return Marshal.GetDelegateForFunctionPointer<SetDefaultEndpointCall>(Slot(face, SetDefaultEndpointSlot))(face, id, role); }
                finally { Marshal.Release(face); }
            }
        }
        finally { Marshal.Release(unknown); }
        return NoInterface;
    }

    /// <summary>Адрес метода COM-объекта по номеру в таблице методов.</summary>
    internal static IntPtr Slot(IntPtr face, int slot) => Marshal.ReadIntPtr(Marshal.ReadIntPtr(face), slot * IntPtr.Size);

    /// <summary>Какой вариант интерфейса переключения есть в этой Windows; null — никакого. Только спрашивает, ничего не вызывает.</summary>
    public static string PolicyConfigName()
    {
        object client = null;
        try
        {
            client = new PolicyConfigClient();
            if (client is IPolicyConfig) return "IPolicyConfig";
            IntPtr unknown = Marshal.GetIUnknownForObject(client);
            try
            {
                foreach (var (name, iid) in OtherPolicyConfigs)
                {
                    if (Marshal.QueryInterface(unknown, in iid, out IntPtr face) < 0) continue;
                    Marshal.Release(face);
                    return name;
                }
            }
            finally { Marshal.Release(unknown); }
            return null;
        }
        catch { return null; }
        finally { Release(client); }
    }

    // ───────────────────────── переключение по кругу ─────────────────────────

    /// <summary>
    /// Следующее устройство из отмеченных после текущего основного. Отключённые пропускаются.
    /// null — переключать не на что (нет подключённых отмеченных, или подключено только текущее).
    /// </summary>
    public static string NextInCycle(IReadOnlyList<string> cycle, IReadOnlyCollection<string> active, string current)
    {
        if (cycle == null || active == null || cycle.Count == 0) return null;
        int start = -1;
        for (int i = 0; i < cycle.Count && start < 0; i++) if (SameId(cycle[i], current)) start = i;
        for (int step = 1; step <= cycle.Count; step++)
        {
            string candidate = cycle[(start + step + cycle.Count) % cycle.Count];
            if (SameId(candidate, current)) continue;
            if (active.Any(a => SameId(a, candidate))) return candidate;
        }
        return null;
    }

    /// <summary>Что делать по нажатию клавиши: на какое устройство переключить либо что сказать, если переключать не на что.</summary>
    internal static (string Target, string Title, string Text) PlanSwitch(IReadOnlyList<string> cycle, IReadOnlyList<AudioDevice> outputs)
    {
        string title = L.T("Звук не переключён");
        if (outputs == null || outputs.Count == 0) return (null, title, L.T("Звуковых устройств не найдено."));
        var ids = outputs.Select(o => o.Id).ToList();
        string current = outputs.FirstOrDefault(o => o.IsDefault)?.Id;
        string next = NextInCycle(cycle, ids, current);
        if (next != null) return (next, null, null);

        var alive = outputs.Where(o => cycle != null && cycle.Any(c => SameId(c, o.Id))).ToList();
        if (alive.Count == 0) return (null, title, L.T("Отмеченные для переключения устройства сейчас не подключены."));
        return (null, title, L.F("Из отмеченных устройств подключено только «{0}» — переключать не на что.", alive[0].Name));
    }

    /// <summary>Переключить звук на следующее отмеченное устройство и показать уведомление у часов.</summary>
    public static void SwitchNext()
    {
        var h = host;
        if (h == null) return;
        var outputs = Outputs();
        var (target, title, text) = PlanSwitch(SavedCycle(h.Settings), outputs);
        bool warning = true;
        if (target != null)
        {
            string name = outputs.First(o => SameId(o.Id, target)).Name;
            if (SetDefault(target, out string error)) { title = L.F("Звук: {0}", name); text = L.T("Теперь это основное устройство вывода."); warning = false; }
            else { title = L.T("Звук не переключён"); text = L.Pick($"«{name}»: {error}", $"“{name}”: {error}"); }
        }
        Changed?.Invoke();
        h.Ui(() => h.Notify(title, text, warning, null));
    }

    /// <summary>Основное устройство сменилось по клавише: открытому разделу пора перечитать список. Вызывается в фоновом потоке.</summary>
    public static event Action Changed;

    // ───────────────────────── горячая клавиша ─────────────────────────

    private const int HotkeyId = 0x5A01;
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const int HotkeyTaken = 1409;       // ERROR_HOTKEY_ALREADY_REGISTERED

    private static HwndSource window;
    private static int switching;

    /// <summary>Сочетание, которое сейчас действует; пустое — клавиша не включена.</summary>
    public static SoundHotkey ActiveHotkey { get; private set; }
    /// <summary>Почему сохранённое сочетание не включилось при запуске; null — всё в порядке.</summary>
    public static string HotkeyProblem { get; private set; }

    // Подменяются при проверке, чтобы не занимать настоящие клавиши и не трогать файл настроек.
    internal static Func<SoundHotkey, int> Registrar = RegisterInWindows;
    internal static Action Unregistrar = UnregisterInWindows;
    internal static Action<AppSettings> Saver = s => s.Save();

    public static SoundHotkey SavedHotkey(AppSettings settings) => SoundHotkey.Parse(settings?.Get("sound.hotkey"));

    public static List<string> SavedCycle(AppSettings settings) =>
        (settings?.Get("sound.cycle") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Включить клавишу: занять сочетание в Windows и запомнить его вместе с отмеченными устройствами.
    /// Вызывать из потока интерфейса. Если сочетание занято, прежнее остаётся в силе.
    /// </summary>
    public static bool SetHotkey(SoundHotkey hotkey, IReadOnlyList<string> cycle, out string error)
    {
        error = hotkey.Problem();
        if (error != null) return false;
        var ids = (cycle ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count < 2) { error = L.T("Отметьте хотя бы два устройства: между ними и будет переключать клавиша."); return false; }
        if (host == null) { error = L.T("Раздел ещё не готов. Откройте его заново."); return false; }

        var previous = ActiveHotkey;
        if (!previous.IsEmpty) Unregistrar();
        int code = Registrar(hotkey);
        if (code != 0)
        {
            ActiveHotkey = !previous.IsEmpty && Registrar(previous) == 0 ? previous : default;
            error = RegisterError(hotkey, code);
            return false;
        }
        ActiveHotkey = hotkey;
        HotkeyProblem = null;
        host.Settings.Set("sound.hotkey", hotkey.ToString());
        host.Settings.Set("sound.cycle", string.Join("|", ids));
        Saver(host.Settings);
        return true;
    }

    /// <summary>Отключить клавишу. Отмеченные устройства остаются в настройках.</summary>
    public static void ClearHotkey()
    {
        if (!ActiveHotkey.IsEmpty) Unregistrar();
        ActiveHotkey = default;
        HotkeyProblem = null;
        if (host == null) return;
        host.Settings.Set("sound.hotkey", null);
        Saver(host.Settings);
    }

    private static string RegisterError(SoundHotkey hotkey, int code) => code == HotkeyTaken
        ? L.F("Сочетание {0} уже занято другой программой или самой Windows. Выберите другое.", hotkey.Text)
        : L.F("Windows не приняла сочетание {0} (ошибка {1}). Выберите другое.", hotkey.Text, code);

    private static void EnsureWindow()
    {
        if (window != null) return;
        // Окно только для сообщений: его нет ни на экране, ни на панели задач.
        var p = new HwndSourceParameters("PulsePC.Sound") { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 };
        window = new HwndSource(p);
        window.AddHook(WndProc);
    }

    private static int RegisterInWindows(SoundHotkey hotkey)
    {
        EnsureWindow();
        return RegisterHotKey(window.Handle, HotkeyId, hotkey.Modifiers | ModNoRepeat, (uint)hotkey.Vk) ? 0 : Math.Max(1, Marshal.GetLastWin32Error());
    }

    private static void UnregisterInWindows()
    {
        if (window != null) UnregisterHotKey(window.Handle, HotkeyId);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey || wParam.ToInt64() != HotkeyId) return IntPtr.Zero;
        handled = true;
        // Пока идёт одно переключение, повторные нажатия не копятся.
        if (Interlocked.Exchange(ref switching, 1) == 0)
            Task.Run(() => { try { SwitchNext(); } catch { } finally { Volatile.Write(ref switching, 0); } });
        return IntPtr.Zero;
    }

    // ───────────────────────── системные окна ─────────────────────────

    /// <summary>Системное окно «Звук», вкладка «Воспроизведение»: там свойства каждого устройства.</summary>
    public static bool OpenSoundWindow()
    {
        try { Process.Start(new ProcessStartInfo("control.exe", "mmsys.cpl,,0") { UseShellExecute = true }); return true; }
        catch { return false; }
    }

    // ───────────────────────── самопроверка ─────────────────────────

    /// <summary>Только чтение: устройства, основное, формат, улучшения, микрофон, наличие интерфейса переключения.</summary>
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        var problems = new List<string>();

        var outputs = ListDevices(Render, out string error);
        if (error != null) problems.Add(error);
        if (outputs.Count == 0) sb.AppendLine("Устройств вывода нет.");
        else
        {
            sb.AppendLine("Устройств вывода: " + outputs.Count);
            foreach (var o in outputs)
            {
                string role = o.IsDefault && o.IsDefaultComm ? "основное" : o.IsDefault ? "основное (кроме связи)" : o.IsDefaultComm ? "для связи" : "не основное";
                string fx = o.Enhancements switch { SysFxState.On => "улучшения включены", SysFxState.Off => "улучшения выключены", _ => "улучшения: нет сведений" };
                sb.AppendLine($"  {o.Name} — {role}; {(o.Format.Length > 0 ? o.Format : "формат не читается")}; {fx}");
            }
            if (!outputs.Any(o => o.IsDefault)) sb.AppendLine("Основное устройство вывода не выбрано.");
        }

        var inputs = ListDevices(Capture, out error);
        if (error != null) problems.Add(error);
        var mic = inputs.FirstOrDefault(d => d.IsDefault);
        sb.AppendLine(mic == null ? "Микрофон: основного устройства записи нет."
            : $"Микрофон: {mic.Name}; прослушивание {(mic.Listening ? "включено" : "выключено")}");

        string policy = PolicyConfigName();
        sb.AppendLine("Переключение основного устройства: " + (policy != null ? "интерфейс Windows найден (" + policy + ")" : "интерфейс Windows не найден"));
        if (policy == null && outputs.Count > 0) problems.Add("нет интерфейса для переключения основного устройства");

        if (outputs.Count > 0)
            sb.AppendLine("Улучшения можно менять: " + (outputs.Any(o => Enhancements.CanWrite(o.Id)) ? "да" : "нет (нужны права администратора или Windows не разрешает)"));
        sb.AppendLine("Копия прежних улучшений: " + (Enhancements.HasBackup ? "есть" : "нет"));

        var hotkey = ActiveHotkey;
        sb.AppendLine("Клавиша переключения: " + (hotkey.IsEmpty ? "не включена" : hotkey.Text) + (HotkeyProblem != null ? " — " + HotkeyProblem : ""));

        var (passed, failed) = LogicChecks();
        sb.AppendLine($"Разбор сочетаний, круг переключения, формат: пройдено проверок {passed} из {passed + failed.Count}");
        foreach (var f in failed) sb.AppendLine("  не прошла: " + f);
        if (failed.Count > 0) problems.Add("логика: " + failed[0]);

        return (problems.Count == 0 ? "OK" : "FAIL: " + string.Join("; ", problems)) + "\n" + sb.ToString().TrimEnd();
    }

    /// <summary>Проверки чистой логики: ничего не читают и не меняют в системе.</summary>
    internal static (int Passed, List<string> Failed) LogicChecks()
    {
        int passed = 0;
        var failed = new List<string>();
        void Check(string name, bool ok) { if (ok) passed++; else failed.Add(name); }

        var hk = new SoundHotkey(true, true, false, false, "F10");
        Check("запись сочетания", hk.ToString() == "Ctrl+Alt+F10" && hk.Text == "Ctrl + Alt + F10");
        Check("разбор сочетания", SoundHotkey.Parse("ctrl + ALT+f10") == hk);
        Check("флаги и код клавиши", hk.Modifiers == 3 && hk.Vk == 0x79);
        Check("цифровой блок", SoundHotkey.Parse("Win+Num7") is { Win: true, Vk: 0x67 } && SoundHotkey.Parse("Ctrl+NumAdd").Text == "Ctrl + Num +");
        Check("туда и обратно для всех клавиш", SoundHotkey.Keys.All(k => SoundHotkey.Parse("Ctrl+Shift+" + k.Token) is var p && p.Vk == k.Vk && p.ToString() == "Ctrl+Shift+" + k.Token));
        Check("мусор не разбирается", SoundHotkey.Parse("Ctrl+Ctrl+A").IsEmpty && SoundHotkey.Parse("Hyper+A").IsEmpty && SoundHotkey.Parse("Ctrl+F13").IsEmpty && SoundHotkey.Parse("").IsEmpty);
        Check("буква без Ctrl, Alt, Win отклоняется", new SoundHotkey(false, false, true, false, "A").Problem() != null && new SoundHotkey(false, false, false, false, "5").Problem() != null);
        Check("Shift с цифровым блоком отклоняется", new SoundHotkey(true, false, true, false, "Num1").Problem() != null && new SoundHotkey(true, false, false, false, "Num1").Problem() == null);
        Check("F12 без дополнений отклоняется", new SoundHotkey(false, false, false, false, "F12").Problem() != null && new SoundHotkey(false, false, false, false, "F9").Problem() == null);

        string[] cycle = { "a", "b", "c" };
        Check("следующее по кругу", NextInCycle(cycle, new[] { "a", "b", "c" }, "a") == "b" && NextInCycle(cycle, new[] { "a", "b", "c" }, "c") == "a");
        Check("отключённое пропускается", NextInCycle(cycle, new[] { "a", "c" }, "a") == "c" && NextInCycle(cycle, new[] { "A", "C" }, "c") == "a");
        Check("основное не из круга", NextInCycle(cycle, new[] { "x", "b", "c" }, "x") == "b" && NextInCycle(cycle, new[] { "b" }, null) == "b");
        Check("переключать не на что", NextInCycle(cycle, new[] { "a" }, "a") == null && NextInCycle(cycle, new[] { "x" }, "x") == null && NextInCycle(Array.Empty<string>(), new[] { "a" }, "a") == null);

        var pcm = new byte[] { 1, 0, 2, 0, 0x44, 0xAC, 0, 0, 0x10, 0xB1, 2, 0, 4, 0, 16, 0 };
        Check("формат 44 100 Гц, 16 бит", DescribeFormat(pcm).Replace(' ', ' ').Replace(' ', ' ') == L.Pick("44 100 Гц, 16 бит, стерео", "44,100 Hz, 16-bit, stereo"));
        var ext = new byte[40];
        new byte[] { 0xFE, 0xFF, 6, 0, 0x80, 0xBB, 0, 0, 0, 0, 0, 0, 24, 0, 32, 0, 22, 0, 24, 0 }.CopyTo(ext, 0);
        Check("расширенный формат: 24 бита в 32-битной ячейке", DescribeFormat(ext).Replace(' ', ' ').Replace(' ', ' ') == L.Pick("48 000 Гц, 24 бит, 5.1", "48,000 Hz, 24-bit, 5.1"));
        Check("обрезанный формат", DescribeFormat(new byte[8]) == "" && DescribeFormat(null) == "");
        return (passed, failed);
    }

    // ───────────────────────── Core Audio ─────────────────────────

    private static void Release(object com)
    {
        try { if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com); } catch { }
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid Fmtid;
        public uint Pid;
        public PropertyKey(string fmtid, uint pid) { Fmtid = new Guid(fmtid); Pid = pid; }
    }

    /// <summary>PROPVARIANT: тип и два поля размером с указатель (строка — в a; блок данных — размер в a, адрес в b).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public IntPtr a;
        public IntPtr b;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    internal class PolicyConfigClient { }

    /// <summary>
    /// Порядок методов сверен с SoundSwitch (IPolicyConfig.cs), AudioSwitcher (IPolicyConfig.cs), EarTrumpet (IPolicyConfig.cs)
    /// и заголовком PolicyConfig.h из Sunshine: во всех SetDefaultEndpoint — одиннадцатый метод интерфейса.
    /// Программа вызывает только его; остальные объявлены, чтобы сохранить порядок.
    /// </summary>
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool isDefault, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool isDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool visible);
    }
}
