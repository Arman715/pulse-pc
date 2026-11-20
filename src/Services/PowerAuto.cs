using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using PulsePC.Panels;

namespace PulsePC.Services;

/// <summary>Схема питания Windows: номер, название и чем она отличается.</summary>
public sealed class PowerScheme
{
    public string Guid { get; set; } = "";
    public string Name { get; set; } = "";
    public string Hint { get; set; } = "";
    /// <summary>Пометка справа в списке: «включена», «включится в игре» или пусто.</summary>
    public string Mark { get; set; } = "";
    public bool Active { get; set; }
    /// <summary>Это «Максимальная производительность» (сама схема Windows или её копия).</summary>
    public bool IsMax { get; set; }
}

/// <summary>Снимок состояния для раздела «Питание».</summary>
public sealed class PowerState
{
    public List<PowerScheme> Schemes = new();
    public PowerScheme Active;
    public bool Enabled, InGame, HasBalanced;
    /// <summary>Режим раздела: PowerAuto.ModeOff, ModeGame или ModeAlways.</summary>
    public string Mode = PowerAuto.ModeOff;
    /// <summary>Сейчас включена схема, которая держит процессор на полной частоте (максимальная или высокая производительность).</summary>
    public bool ActiveIsFast;
    /// <summary>Название схемы, которая включится в игре.</summary>
    public string MaxName = "";
    /// <summary>Название обычной («Сбалансированной») схемы на языке системы; пусто — её на ПК нет.</summary>
    public string BalancedName = "";
    /// <summary>Название схемы, которая вернётся после игры (пусто — игра не идёт или схема не менялась).</summary>
    public string BeforeName = "";
    public DateTime? Last;
    public string LastText = "";
    /// <summary>Батарея ноутбука (у настольного компьютера HasBattery = false).</summary>
    public BatteryInfo Battery;
}

/// <summary>
/// Автоматическая смена схемы питания: игра запущена — «Максимальная производительность», игра закрыта — возвращается
/// схема, которая была до игры. Схема до игры запоминается в настройках, поэтому возврат срабатывает и после сбоя программы.
/// </summary>
public static class PowerAuto
{
    public const string BalancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string HighPerfGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string SaverGuid = "a1841308-3541-4fab-bc81-f71556f20b4a";
    private const string UltimateTemplate = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    private const string KeyMode = "power.mode";         // off | game | always
    private const string KeyAuto = "power.auto";         // прежний ключ режима «по игре»; ведётся вместе с KeyMode
    private const string KeyBefore = "power.before";     // схема до игры (GUID); есть только пока идёт игра
    private const string KeyMax = "power.max";           // копия «Максимальной производительности» на этом ПК (GUID)
    private const string KeyLast = "power.last";         // когда было последнее автопереключение
    private const string KeyLastText = "power.lastText"; // что именно произошло

    // Схема, которая была включена до режима «всегда». Лежит в защищённой папке: по ней программа с правами
    // администратора включает схему, а в папку пользователя может писать любая программа.
    private const string OriginalName = "power-original.txt";
    private static string OriginalFile => SecureStore.PathOf(OriginalName);

    private static readonly object Gate = new();
    private static PanelHost host;
    private static bool hooked;

    /// <summary>Режимы раздела: не трогать схему, максимальная только в игре, максимальная всегда.</summary>
    public const string ModeOff = "off", ModeGame = "game", ModeAlways = "always";

    /// <summary>Режим «менять схему по игре» включён. По этому свойству остальная программа узнаёт о режиме.</summary>
    public static bool Enabled { get; private set; }

    /// <summary>
    /// Режим раздела. Если он ещё не записан (настройки прежней версии): «по игре» — если был включён прежний переключатель раздела,
    /// «всегда» — если пользователь применял схему максимальной производительности на вкладке «Оптимизация» (она есть в KnownApplied), иначе «не трогать».
    /// </summary>
    public static string ModeOf(AppSettings s)
    {
        if (s == null) return ModeOff;
        string mode = s.Get(KeyMode);
        if (mode is ModeOff or ModeGame or ModeAlways) return mode;
        if (s.GetBool(KeyAuto)) return ModeGame;
        return s.KnownApplied != null && s.KnownApplied.Contains(Tweaks.PowerTitle) ? ModeAlways : ModeOff;
    }

    /// <summary>Записать режим, выведенный из прежних настроек (один раз; дальше он просто читается).</summary>
    public static void Migrate(AppSettings s)
    {
        if (s == null || s.Get(KeyMode) != "") return;
        s.Set(KeyMode, ModeOf(s));
    }

    /// <summary>Схема или режим изменились: раздел перечитывает состояние. Может прийти из фонового потока.</summary>
    public static event Action Changed;

    public static void Init(PanelHost h)
    {
        host = h;
        if (h.Settings.Get(KeyMode) == "") { Migrate(h.Settings); Save(h.Settings); }
        Enabled = ModeOf(h.Settings) == ModeGame;
        Hook();
        // Программа закрылась посреди игры: схема до игры осталась в настройках — вернуть её.
        if (h.Settings.Get(KeyBefore) != "")
            Task.Run(() => { if (WaitNoGame()) OnEnded(); });
    }

    private static void Hook()
    {
        lock (Gate)
        {
            if (hooked) return;
            hooked = true;
            GameHooks.Started += OnStarted;
            GameHooks.Ended += OnEnded;
        }
    }

    /// <summary>
    /// Дождаться, пока станет ясно, что игра не идёт. false — игру уже ведут события GameHooks, возвращать схему рано:
    /// её вернёт обработчик конца игры.
    /// </summary>
    private static bool WaitNoGame()
    {
        while (true)
        {
            if (GameHooks.InGame) return false;
            DetectedGame game = null;
            try { game = GameDetector.Find(); } catch { }
            if (game == null) return !GameHooks.InGame;
            Thread.Sleep(10000);
        }
    }

    /// <summary>Включить или выключить режим «по игре» (для прежних вызовов; раздел пользуется SetMode).</summary>
    public static void SetEnabled(PanelHost h, bool on) => SetMode(h, on ? ModeGame : ModeOff);

    /// <summary>
    /// Выбрать режим раздела. Вызывать в фоновом потоке: смена схемы может занять секунду.
    /// «Всегда» — включить максимальную сейчас и следить, чтобы обновление Windows её не сбросило;
    /// «только в игре» — вне игры сразу вернуть «Сбалансированную», если стоит быстрая схема; «не трогать» — схему не менять.
    /// </summary>
    public static string SetMode(PanelHost h, string mode)
    {
        host ??= h;
        Hook();
        var s = h.Settings;
        s.Set(KeyMode, mode);
        s.SetBool(KeyAuto, mode == ModeGame);
        Enabled = mode == ModeGame;
        string message;
        if (mode == ModeAlways)
        {
            lock (Gate)
            {
                // Запоминаем схему, которая была до режима (один раз: повторный выбор не должен затереть настоящую исходную).
                // Если режим выбрали посреди игры в режиме «по игре», исходная — схема до игры, а не уже включённая максимальная.
                if (LoadOriginal() == "")
                {
                    string before = s.Get(KeyBefore), active = ActiveGuid();
                    string original = IsGuid(before) ? before : active;
                    if (IsGuid(original)) SaveOriginal(original);
                }
                s.Set(KeyBefore, null);     // после игры возвращать нечего: схема остаётся максимальной
                ApplyMaxLocked(s);
            }
            bool ok = ActiveIsMax();
            Tweaks.Remember(s, Tweaks.PowerTitle, ok);
            message = ok ? "Схема максимальной производительности включена и останется вне игр. Если обновление Windows её сбросит, программа вернёт её сама."
                         : "Windows не дала включить схему максимальной производительности. Попробуйте ещё раз или выберите её в «Панели управления» → «Электропитание».";
        }
        else
        {
            Tweaks.Remember(s, Tweaks.PowerTitle, false);
            // Уходим из режима «всегда»: возвращается схема, которая была до него (а не «Сбалансированная»).
            string restored = null;
            if (LoadOriginal() != "")
            {
                if (mode == ModeGame && GameHooks.InGame)
                    lock (Gate)
                    {
                        // Игра идёт: исходная схема вернётся после игры как схема «до игры».
                        string original = LoadOriginal();
                        if (original != "" && s.Get(KeyBefore) == "") s.Set(KeyBefore, original);
                        ClearOriginal();
                    }
                else
                    lock (Gate) restored = RestoreOriginalLocked().Message;
            }
            if (mode == ModeGame && GameHooks.InGame)
            {
                // Режим включили посреди игры: схема меняется сразу, игре это не мешает.
                lock (Gate) SwitchForGame(GameHooks.Game, GameHooks.Pid);
                message = "Только в игре: игра уже идёт, поэтому схема переключена сразу. После игры вернётся прежняя.";
            }
            else if (mode == ModeGame && restored != null)
                message = restored + " В игре программа включит схему максимальной производительности.";
            else if (mode == ModeGame)
            {
                var st = Read(s);
                if (st.ActiveIsFast && st.HasBalanced && !GameHooks.InGame)
                {
                    bool ok;
                    lock (Gate) ok = SetActive(BalancedGuid);
                    message = ok ? $"Только в игре: сейчас включена «{st.BalancedName}», в игре включится «{st.MaxName}»."
                                 : $"Режим выбран, но Windows не дала включить «{st.BalancedName}». В игре программа всё равно включит «{st.MaxName}».";
                }
                else message = "Только в игре: схема переключится при следующем запуске игры.";
            }
            else message = restored != null ? restored + " Дальше программа схему питания не меняет."
                                            : "Программа больше не меняет схему питания: остаётся та, что включена сейчас.";
        }
        Save(s);
        Changed?.Invoke();
        return message;
    }

    /// <summary>Сейчас включена «Максимальная производительность» (сама схема Windows или её копия).</summary>
    public static bool ActiveIsMax() => Schemes(host?.Settings).Any(x => x.Active && x.IsMax);

    /// <summary>Включить «Максимальную производительность» (создать из шаблона, если её нет). Для сторожа и режима «всегда».</summary>
    public static void ApplyMax()
    {
        var s = host?.Settings;
        lock (Gate) ApplyMaxLocked(s);
        Changed?.Invoke();
    }

    private static void ApplyMaxLocked(AppSettings s)
    {
        var target = Schemes(s).FirstOrDefault(x => x.IsMax);
        if (target != null) { SetActive(target.Guid); return; }
        Tweaks.ApplyUltimatePlan();
        var now = Schemes(s).FirstOrDefault(x => x.Active);
        if (s != null && now != null && now.Guid != BalancedGuid && now.Guid != SaverGuid) s.Set(KeyMax, now.Guid);
    }

    // ------------------------------------------------------------------ схема до режима «всегда»

    private static bool IsGuid(string text) => !string.IsNullOrEmpty(text) && System.Guid.TryParseExact(text, "D", out _);

    /// <summary>Схема до режима «всегда» (GUID в нижнем регистре) или пусто. Содержимое проверяется: это должен быть GUID.</summary>
    private static string LoadOriginal()
    {
        string text = AtomicFile.Read(OriginalFile, t => t.Trim());
        return IsGuid(text) ? text.ToLowerInvariant() : "";
    }

    private static void SaveOriginal(string guid)
    {
        try { AtomicFile.WriteAllText(OriginalFile, guid.ToLowerInvariant()); }
        catch (Exception ex) { Log.Error("питание: запись исходной схемы", ex); }
    }

    private static void ClearOriginal()
    {
        foreach (var f in new[] { OriginalFile, OriginalFile + ".bak" }) { try { File.Delete(f); } catch { } }
    }

    /// <summary>
    /// Вернуть схему, которая была до режима «всегда»; если её удалили — «Сбалансированную». Копия стирается только
    /// после удачного возврата. Вызывать под Gate.
    /// </summary>
    private static (bool Ok, string Message) RestoreOriginalLocked()
    {
        string original = LoadOriginal();
        var list = Schemes(host?.Settings);
        var back = (original != "" ? list.FirstOrDefault(x => x.Guid == original) : null) ?? list.FirstOrDefault(x => x.Guid == BalancedGuid);
        if (back == null)
        {
            ClearOriginal();
            return (false, "Схемы, которая была до режима «всегда», на этом компьютере больше нет, и «Сбалансированной» тоже. Схема осталась прежней.");
        }
        bool same = list.Any(x => x.Active && x.Guid == back.Guid);
        if (!same && !SetActive(back.Guid))
            return (false, $"Windows не дала вернуть схему «{back.Name}». Включите её сами: «Панель управления» → «Электропитание».");
        ClearOriginal();
        return (true, back.Guid == original ? $"Возвращена схема «{back.Name}», которая была до режима «всегда»."
                                            : $"Схемы, которая была до режима «всегда», больше нет — включена «{back.Name}».");
    }

    /// <summary>
    /// Отмена режима «всегда» (откат переключателя сторожа): вернуть схему, которая была до режима. Если её не запоминали
    /// (режим выбран в прежней версии программы) — «Сбалансированная», как раньше.
    /// </summary>
    public static (bool Ok, string Message) RevertAlways()
    {
        (bool Ok, string Message) r;
        lock (Gate)
        {
            if (GameHooks.InGame) return (false, "Идёт игра. Схему можно сменить после неё.");
            r = LoadOriginal() == "" ? (false, null) : RestoreOriginalLocked();
        }
        if (r.Message == null) return SwitchToBalanced();
        Changed?.Invoke();
        return r;
    }

    // ------------------------------------------------------------------ события игры

    private static void OnStarted(string game, int pid)
    {
        if (!Enabled || host == null || !GameHooks.IsCurrent(pid)) return;
        lock (Gate) SwitchForGame(game, pid);
        Changed?.Invoke();
    }

    private static void OnEnded()
    {
        if (host == null) return;
        bool changed;
        lock (Gate) changed = SwitchBack();
        if (changed) Changed?.Invoke();
    }

    private static void SwitchForGame(string game, int pid)
    {
        // Игра могла закончиться, пока очередь событий GameHooks дошла сюда: тогда схему не трогаем (как в UpdateGuard).
        if (!GameHooks.IsCurrent(pid)) return;
        var s = host.Settings;
        var list = Schemes(s);
        var active = list.FirstOrDefault(x => x.Active);
        if (active == null) { Remember(s, $"Игра «{game}» запущена, но текущую схему питания прочитать не удалось. Схема осталась прежней."); return; }

        // Схема до игры запоминается один раз: если ключ остался с прошлого раза, в нём настоящая исходная схема.
        bool fresh = s.Get(KeyBefore) == "";
        if (fresh) { s.Set(KeyBefore, active.Guid); Save(s); }

        if (active.IsMax) { Remember(s, $"Игра «{game}» запущена: «{active.Name}» уже была включена."); return; }

        var target = list.FirstOrDefault(x => x.IsMax);
        if (target != null) SetActive(target.Guid);
        else Tweaks.ApplyUltimatePlan();      // схемы ещё нет на этом ПК — она создаётся из шаблона Windows

        var now = Schemes(s).FirstOrDefault(x => x.Active);
        if (now == null || now.Guid == active.Guid)
        {
            if (fresh) s.Set(KeyBefore, null);
            Remember(s, $"Игра «{game}» запущена, но Windows не дала включить схему максимальной производительности. Осталась схема «{active.Name}».");
            return;
        }
        // Запоминаем созданную копию, чтобы при следующей игре включить её же, а не создавать новую.
        if (target == null && now.Guid != BalancedGuid && now.Guid != SaverGuid) s.Set(KeyMax, now.Guid);
        Remember(s, $"Игра «{game}» запущена: «{active.Name}» → «{now.Name}».");
    }

    /// <summary>Вернуть схему, которая была до игры. false — возвращать было нечего.</summary>
    private static bool SwitchBack()
    {
        var s = host.Settings;
        string before = s.Get(KeyBefore);
        if (before == "") return false;

        var list = Schemes(s);
        var active = list.FirstOrDefault(x => x.Active);
        // Схему до игры могли удалить, пока шла игра: тогда возвращаемся на «Сбалансированную».
        var back = list.FirstOrDefault(x => x.Guid == before) ?? list.FirstOrDefault(x => x.Guid == BalancedGuid);
        if (back == null) { s.Set(KeyBefore, null); Remember(s, "Игра закрыта, но схемы, которая была до игры, на этом компьютере больше нет. Схема осталась прежней."); return true; }

        if (active != null && active.Guid == back.Guid)
        {
            s.Set(KeyBefore, null);
            Remember(s, $"Игра закрыта: осталась схема «{back.Name}», она же была до игры.");
        }
        else if (SetActive(back.Guid))
        {
            s.Set(KeyBefore, null);
            Remember(s, $"Игра закрыта: возвращена схема «{back.Name}».");
        }
        else
            // Ключ не очищаем: возврат повторится при следующем запуске программы.
            Remember(s, $"Игра закрыта, но вернуть схему «{back.Name}» не удалось. Программа попробует ещё раз при следующем запуске.");
        return true;
    }

    private static void Remember(AppSettings s, string text)
    {
        s.Set(KeyLast, DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
        s.Set(KeyLastText, text);
        Save(s);
    }

    // Запись настроек под тем же замком, что и AppSettings.Set: другой раздел не изменит словарь посреди сохранения.
    private static void Save(AppSettings s)
    {
        var values = s.Values;
        if (values == null) { s.Save(); return; }
        lock (values) s.Save();
    }

    // ------------------------------------------------------------------ действия по кнопке

    /// <summary>Перейти на «Сбалансированную» (кнопка в разделе; сама программа этого без спроса не делает).</summary>
    public static (bool Ok, string Message) SwitchToBalanced()
    {
        string name;
        lock (Gate)
        {
            if (GameHooks.InGame) return (false, "Идёт игра. Схему можно сменить после неё.");
            name = Schemes(null).FirstOrDefault(x => x.Guid == BalancedGuid)?.Name;
            if (name == null)
                return (false, "Сбалансированной схемы на этом компьютере нет. Её можно вернуть в «Панели управления» → «Электропитание».");
            if (!SetActive(BalancedGuid)) return (false, "Windows не дала сменить схему питания. Включите её сами: «Панель управления» → «Электропитание».");
        }
        Changed?.Invoke();
        return (true, Enabled ? $"Включена схема «{name}». При запуске игры программа сама включит схему максимальной производительности." : $"Включена схема «{name}».");
    }

    // ------------------------------------------------------------------ чтение

    /// <summary>Состояние для раздела. Только чтение; можно вызывать из фонового потока.</summary>
    public static PowerState Read(AppSettings s)
    {
        var st = new PowerState { Schemes = Schemes(s), InGame = GameHooks.InGame, Battery = BatteryInfo.Read() };
        st.Active = st.Schemes.FirstOrDefault(x => x.Active);
        st.Mode = ModeOf(s);
        st.Enabled = st.Mode == ModeGame;
        st.BalancedName = st.Schemes.FirstOrDefault(x => x.Guid == BalancedGuid)?.Name ?? "";
        st.HasBalanced = st.BalancedName != "";
        st.ActiveIsFast = st.Active != null && (st.Active.IsMax || st.Active.Guid == HighPerfGuid);
        st.MaxName = st.Schemes.FirstOrDefault(x => x.IsMax)?.Name ?? TemplateName() ?? "Максимальная производительность";

        string before = s.Get(KeyBefore);
        if (before != "") st.BeforeName = st.Schemes.FirstOrDefault(x => x.Guid == before)?.Name ?? "";

        if (DateTime.TryParse(s.Get(KeyLast), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last)) st.Last = last;
        st.LastText = s.Get(KeyLastText);

        foreach (var x in st.Schemes)
            x.Mark = x.Active ? "включена" : st.Enabled && x.IsMax && !st.Schemes.Any(a => a.Active && a.IsMax) ? "включится в игре" : "";
        return st;
    }

    /// <summary>Все схемы питания на ПК с названиями на языке системы. settings нужен, чтобы узнать копию максимальной схемы.</summary>
    public static List<PowerScheme> Schemes(AppSettings settings)
    {
        var list = new List<PowerScheme>();
        string active = ActiveGuid();
        string known = settings?.Get(KeyMax) ?? "";
        string template = TemplateName();
        try
        {
            for (uint i = 0; i < 64; i++)
            {
                var id = System.Guid.Empty;
                uint size = 16;
                if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, i, ref id, ref size) != 0) break;
                string guid = id.ToString("D").ToLowerInvariant();
                string name = FriendlyName(id);
                if (string.IsNullOrWhiteSpace(name)) name = "Схема без названия";
                bool max = guid == UltimateTemplate || (known != "" && guid == known)
                           || (template != null && name.Equals(template, StringComparison.OrdinalIgnoreCase))
                           || name.Equals("Максимальная производительность", StringComparison.OrdinalIgnoreCase)
                           || name.Equals("Ultimate Performance", StringComparison.OrdinalIgnoreCase);
                list.Add(new PowerScheme { Guid = guid, Name = name, Active = guid == active, IsMax = max, Hint = HintFor(guid, max) });
            }
        }
        catch { }
        // Одна схема может встретиться дважды (так её иногда отдаёт Windows) — показываем один раз.
        return list.GroupBy(x => x.Guid).Select(g => g.First()).ToList();
    }

    private static string HintFor(string guid, bool max) =>
        max ? "Процессор не снижает частоту и не засыпает между задачами: полная скорость, но больше нагрев и шум."
        : guid == BalancedGuid ? "Частота процессора меняется по нагрузке: без нагрузки компьютер тише и прохладнее."
        : guid == HighPerfGuid ? "Процессор держит высокую частоту, энергосбережение почти выключено."
        : guid == SaverGuid ? "Процессор и экран экономят энергию, скорость ниже."
        : "Схема производителя компьютера или созданная вручную.";

    public static string ActiveGuid()
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr p) != 0 || p == IntPtr.Zero) return "";
            try { return Marshal.PtrToStructure<Guid>(p).ToString("D").ToLowerInvariant(); }
            finally { LocalFree(p); }
        }
        catch { return ""; }
    }

    private static string FriendlyName(Guid id)
    {
        try
        {
            uint size = 0;
            PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, null, ref size);
            if (size == 0 || size > 4096) return "";
            var buffer = new byte[size];
            if (PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0) return "";
            return Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0').Trim();
        }
        catch { return ""; }
    }

    /// <summary>
    /// Название «Максимальной производительности» на языке системы — из шаблона Windows. По нему узнаётся копия схемы,
    /// у которой на каждом ПК свой номер. null — шаблона нет (старая Windows).
    /// </summary>
    private static string TemplateName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\User\Default\PowerSchemes\" + UltimateTemplate);
            string raw = key?.GetValue("FriendlyName") as string;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (!raw.StartsWith('@')) return raw.Trim();
            var sb = new StringBuilder(512);
            if (SHLoadIndirectString(raw, sb, (uint)sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0) return sb.ToString().Trim();
            int comma = raw.LastIndexOf(',');
            return comma >= 0 && comma < raw.Length - 1 ? raw[(comma + 1)..].Trim() : null;
        }
        catch { return null; }
    }

    /// <summary>Сделать схему активной. true — после вызова активна именно она.</summary>
    private static bool SetActive(string guid)
    {
        try
        {
            var id = System.Guid.Parse(guid);
            if (PowerSetActiveScheme(IntPtr.Zero, ref id) != 0) Shell.Run("powercfg.exe", "/setactive " + guid, 15000);
        }
        catch { }
        return ActiveGuid() == guid;
    }

    // ------------------------------------------------------------------ самопроверка

    /// <summary>Только чтение: схемы питания, активная схема, настройки режима.</summary>
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        var s = host?.Settings ?? AppSettings.Load();
        var st = Read(s);
        sb.AppendLine(st.Schemes.Count > 0 && st.Active != null ? "OK" : "FAIL: не удалось прочитать схемы питания или активную схему");
        sb.AppendLine($"Схем на ПК: {st.Schemes.Count}");
        foreach (var x in st.Schemes)
            sb.AppendLine($"  {(x.Active ? "*" : " ")} {x.Name}  [{x.Guid}]{(x.IsMax ? "  максимальная" : "")}");
        sb.AppendLine($"Активная: {st.Active?.Name ?? "не прочитана"}");
        sb.AppendLine($"Активная по powercfg (Tweaks.ActiveScheme): {Tweaks.ActiveScheme()}");
        sb.AppendLine($"Название максимальной схемы из шаблона Windows: {TemplateName() ?? "шаблона нет"}");
        sb.AppendLine($"В игре включится: {st.MaxName}{(st.Schemes.Any(x => x.IsMax) ? "" : " (будет создана из шаблона)")}");
        sb.AppendLine($"«Сбалансированная» есть: {(st.HasBalanced ? "да" : "нет")}");
        sb.AppendLine($"Сейчас стоит быстрая схема (есть смысл предложить «Сбалансированную»): {(st.ActiveIsFast ? "да" : "нет")}");
        sb.AppendLine($"Режим {KeyMode}: {st.Mode}{(s.Get(KeyMode) == "" ? " (выведен из прежних настроек)" : "")}; {KeyAuto}: {(s.GetBool(KeyAuto) ? "1" : "0")}");
        sb.AppendLine($"{KeyBefore}: {(s.Get(KeyBefore) == "" ? "пусто" : s.Get(KeyBefore))}");
        sb.AppendLine($"{KeyMax}: {(s.Get(KeyMax) == "" ? "пусто" : s.Get(KeyMax))}");
        sb.AppendLine($"Схема до режима «всегда» ({OriginalFile}): {(LoadOriginal() == "" ? "не запомнена" : LoadOriginal())}");
        sb.AppendLine($"Последнее автопереключение: {(st.Last == null ? "не было" : st.Last.Value.ToString("dd.MM.yyyy HH:mm") + " — " + st.LastText)}");
        sb.AppendLine($"Игра идёт (GameHooks.InGame): {(GameHooks.InGame ? "да" : "нет")}");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Windows

    private const uint AccessScheme = 16;

    [DllImport("powrprof.dll")] private static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr subgroup, uint access, uint index, ref Guid buffer, ref uint size);
    [DllImport("powrprof.dll")] private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, byte[] buffer, ref uint size);
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int SHLoadIndirectString(string source, StringBuilder buffer, uint size, IntPtr reserved);
}

/// <summary>Батарея ноутбука: есть ли она и от чего сейчас работает компьютер. Только чтение.</summary>
public readonly struct BatteryInfo
{
    /// <summary>Есть батарея ноутбука (не ИБП: батарея ИБП считается «кратковременной» и здесь не учитывается).</summary>
    public bool HasBattery { get; init; }
    /// <summary>Сейчас работает от батареи (зарядка не подключена).</summary>
    public bool OnBattery { get; init; }
    /// <summary>Заряд, %; −1 — неизвестен.</summary>
    public int Percent { get; init; }

    public static BatteryInfo Read()
    {
        try
        {
            if (!GetSystemPowerStatus(out var st)) return default;
            bool present = (st.BatteryFlag & 128) == 0 && st.BatteryFlag != 255;
            // Батарея ИБП у настольного компьютера: Windows называет её кратковременной.
            var caps = new byte[76];
            if (CallNtPowerInformation(4 /* SystemPowerCapabilities */, IntPtr.Zero, 0, caps, (uint)caps.Length) == 0)
                present = caps[30] != 0 && caps[31] == 0;
            return new BatteryInfo { HasBattery = present, OnBattery = present && st.ACLineStatus == 0, Percent = st.BatteryLifePercent <= 100 ? st.BatteryLifePercent : -1 };
        }
        catch { return default; }
    }

    /// <summary>
    /// Предупреждение для раздела «Питание» (пусто — сказать нечего). Чистая функция: проверяется на выдуманных состояниях.
    /// fastScheme — включена или включится в игре схема полной скорости.
    /// </summary>
    public static string Advice(BatteryInfo b, bool fastScheme)
    {
        if (!b.HasBattery) return "";
        string charge = b.Percent >= 0 ? $" Заряд — {b.Percent} %." : "";
        if (b.OnBattery)
            return (fastScheme
                ? "Ноутбук работает от батареи, а схема полной скорости держит процессор на максимуме — заряд уйдёт за час-два."
                : "Ноутбук работает от батареи: процессор и видеокарта сейчас работают вполсилы, а в игре заряд уйдёт быстро.")
                + charge + " Для игр подключите зарядку.";
        return "Это ноутбук: играйте от зарядки. От батареи процессор и видеокарта работают вполсилы, а схема полной скорости быстро сажает заряд.";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
    [DllImport("powrprof.dll")] private static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputLength, byte[] output, uint outputLength);
}
