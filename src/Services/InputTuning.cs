using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PulsePC.Panels;

namespace PulsePC.Services;

/// <summary>Сочетание специальных возможностей, которое Windows вызывает по долгому или частому нажатию.</summary>
internal enum AccessKind { Sticky, Filter, Toggle }

/// <summary>Чтение и запись флагов специальных возможностей. Настоящая система или подделка для проверки.</summary>
internal interface IAccessBackend
{
    /// <summary>Структура целиком: [0] — размер, [1] — флаги, дальше — остальные поля. null — прочитать не удалось.</summary>
    uint[] Get(AccessKind kind);
    bool Set(AccessKind kind, uint[] data);
}

/// <summary>Включён ли вызов сочетанием у каждой из трёх функций.</summary>
public sealed record HotkeyState(bool Sticky, bool Filter, bool Toggle, bool Readable)
{
    public int Count => (Sticky ? 1 : 0) + (Filter ? 1 : 0) + (Toggle ? 1 : 0);
}

/// <summary>Итог замера частоты опроса мыши.</summary>
public sealed record MouseRate(bool Enough, double Hz, int Rounded, int Reports);

/// <summary>Что помнит перехват про каждую клавишу Win: 0 — не нажата, 1 — нажатие пропущено в систему, 2 — проглочено.</summary>
internal struct WinKeys { public byte Left, Right; }

/// <summary>
/// Клавиатура и мышь в играх: сочетания специальных возможностей, блокировка клавиши Win на время игры,
/// замер частоты опроса мыши и справка по указателю.
/// </summary>
public static class InputTuning
{
    public const string WinLockKey = "input.winlock";

    private static PanelHost host;

    /// <summary>
    /// Подписка на начало и конец игры. Временное изменение здесь одно — перехват клавиатуры; он живёт только
    /// в нашем процессе и исчезает вместе с ним, поэтому после сбоя убирать в системе нечего.
    /// </summary>
    public static void Init(PanelHost panelHost)
    {
        host = panelHost;
        // Игра могла закончиться, пока очередь GameHooks дошла до обработчика: тогда перехват не ставится.
        GameHooks.Started += (_, pid) => { if (WinLockWanted && GameHooks.IsCurrent(pid)) StartHook(pid); };
        GameHooks.Ended += StopHook;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => StopHook();
        if (GameHooks.InGame && WinLockWanted)
        {
            int pid = GameHooks.Pid;
            Task.Run(() => StartHook(pid));
        }
    }

    // ======================================================================================
    // Сочетания специальных возможностей
    // ======================================================================================

    private const uint HotkeyActive = 0x4;   // SKF_HOTKEYACTIVE = FKF_HOTKEYACTIVE = TKF_HOTKEYACTIVE

    private static readonly (AccessKind Kind, string Key, string Title)[] Hotkeys =
    {
        (AccessKind.Sticky, "sticky", "залипание клавиш"),
        (AccessKind.Filter, "filter", "фильтрация ввода"),
        (AccessKind.Toggle, "toggle", "озвучивание переключений"),
    };

    private static readonly IAccessBackend LiveAccess = new LiveAccessBackend();
    private static string HotkeyBackupFile => Path.Combine(SessionStore.Folder, "input-hotkeys-backup.txt");
    private static readonly object HotkeyGate = new();

    public static bool HasHotkeyBackup => File.Exists(HotkeyBackupFile);

    /// <summary>Только чтение.</summary>
    public static HotkeyState ReadHotkeys() => ReadHotkeys(LiveAccess);

    internal static HotkeyState ReadHotkeys(IAccessBackend backend)
    {
        var flags = Hotkeys.Select(h => backend.Get(h.Kind)).ToArray();
        bool On(int i) => flags[i] != null && (flags[i][1] & HotkeyActive) != 0;
        return new HotkeyState(On(0), On(1), On(2), flags.All(f => f != null));
    }

    /// <summary>
    /// Выключить вызов сочетанием у всех трёх функций. Меняется один флаг; сами функции и остальные их настройки
    /// остаются как были. Исходные флаги запоминаются в файл при первом изменении.
    /// </summary>
    public static (bool Ok, string Message) DisableHotkeys() => DisableHotkeys(LiveAccess, HotkeyBackupFile);

    internal static (bool Ok, string Message) DisableHotkeys(IAccessBackend backend, string backupFile)
    {
        lock (HotkeyGate)
        {
            var current = Hotkeys.Select(h => (h.Kind, h.Key, h.Title, Data: backend.Get(h.Kind))).ToList();
            var active = current.Where(c => c.Data != null && (c.Data[1] & HotkeyActive) != 0).ToList();
            if (active.Count == 0)
                return current.Any(c => c.Data == null)
                    ? (false, "Windows не отдала настройки специальных возможностей. Попробуйте ещё раз.")
                    : (true, "Сочетания уже отключены, менять нечего.");

            // Исходное запоминается один раз: повторное применение не должно затереть настоящее «как было».
            if (!File.Exists(backupFile))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backupFile));
                File.WriteAllLines(backupFile, current.Where(c => c.Data != null).Select(c => $"{c.Key}={c.Data[1]}"));
            }

            var failed = new List<string>();
            foreach (var c in active)
            {
                c.Data[1] &= ~HotkeyActive;
                if (!backend.Set(c.Kind, c.Data)) failed.Add(c.Title);
            }
            return failed.Count == 0
                ? (true, $"Отключено: {string.Join(", ", active.Select(c => c.Title))}. Сами функции остались в «Параметрах» → «Специальные возможности».")
                : (false, $"Не удалось отключить: {string.Join(", ", failed)}. Попробуйте ещё раз.");
        }
    }

    /// <summary>Вернуть флаг вызова сочетанием таким, каким он был до первого изменения. Остальные флаги не трогаются.</summary>
    public static (bool Ok, string Message) RestoreHotkeys() => RestoreHotkeys(LiveAccess, HotkeyBackupFile);

    internal static (bool Ok, string Message) RestoreHotkeys(IAccessBackend backend, string backupFile)
    {
        lock (HotkeyGate)
        {
            if (!File.Exists(backupFile)) return (false, "Копии «как было» нет: программа сочетания не меняла.");
            var saved = new Dictionary<string, uint>();
            foreach (var line in File.ReadAllLines(backupFile))
            {
                var p = line.Split('=');
                if (p.Length == 2 && uint.TryParse(p[1].Trim(), out uint f)) saved[p[0].Trim()] = f;
            }

            int failed = 0;
            foreach (var h in Hotkeys)
            {
                if (!saved.TryGetValue(h.Key, out uint was)) continue;
                var data = backend.Get(h.Kind);
                if (data == null) { failed++; continue; }
                if ((data[1] & HotkeyActive) == (was & HotkeyActive)) continue;
                data[1] = (data[1] & ~HotkeyActive) | (was & HotkeyActive);
                if (!backend.Set(h.Kind, data)) failed++;
            }
            if (failed > 0) return (false, "Вернуть удалось не всё. Копия сохранена, попробуйте ещё раз.");
            File.Delete(backupFile);
            return (true, "Сочетания возвращены такими, какими были до изменений.");
        }
    }

    // ======================================================================================
    // Клавиша Win в игре
    // ======================================================================================

    private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const int WH_KEYBOARD_LL = 13, WM_KEYUP = 0x0101, WM_SYSKEYUP = 0x0105, WM_QUIT = 0x0012;

    private static readonly object HookGate = new();
    private static Thread hookThread;
    private static uint hookThreadId;
    private static IntPtr hookHandle;
    private static LowLevelProc hookProc;      // ссылка держит делегат живым, пока перехват стоит
    private static volatile int gamePid;
    private static WinKeys winKeys;            // читается и меняется только в потоке перехвата

    private static bool WinLockWanted => host?.Settings.GetBool(WinLockKey) == true;

    /// <summary>Перехват сейчас стоит (идёт игра и блокировка включена).</summary>
    public static bool WinLockActive => hookHandle != IntPtr.Zero;

    /// <summary>
    /// Включить или выключить блокировку. Настройка сохраняется; если игра уже идёт, перехват ставится или снимается сразу.
    /// Вызывать не из потока интерфейса: постановка перехвата ждёт свой поток.
    /// </summary>
    public static void SetWinLock(AppSettings settings, bool on)
    {
        settings.SetBool(WinLockKey, on);
        settings.Save();
        if (host == null || !ReferenceEquals(host.Settings, settings)) return;   // раздел открыт вне программы (снимок для проверки)
        if (on && GameHooks.InGame) StartHook(GameHooks.Pid);
        if (!on) StopHook();
    }

    /// <summary>
    /// Решение для одного события клавиши Win. Чистая функция: всё, что нужно, приходит параметрами.
    /// up — отпускание; gameInFront — окно на переднем плане принадлежит игре; systemSeesDown — система уже считает
    /// клавишу нажатой (значит, идёт автоповтор, а не новое нажатие). Возвращает true — событие проглотить.
    /// Правило: нажатие и отпускание всегда получают одну судьбу, иначе клавиша «залипнет» в системе.
    /// </summary>
    internal static bool DecideWin(ref WinKeys keys, int vk, bool up, bool gameInFront, bool systemSeesDown)
    {
        if (vk != VK_LWIN && vk != VK_RWIN) return false;
        ref byte k = ref keys.Left;
        if (vk == VK_RWIN) k = ref keys.Right;

        if (up)
        {
            bool swallow = k == 2;
            k = 0;
            return swallow;
        }
        // Отпускание прошло мимо нас (например, на экране блокировки): это новое нажатие, решаем заново.
        if (k == 1 && !systemSeesDown) k = 0;
        // Игра ушла с переднего плана, пока клавишу держали: дальше клавиша обычная.
        if (k == 2 && !gameInFront) k = 0;
        if (k == 0) k = gameInFront ? (byte)2 : (byte)1;
        return k == 2;
    }

    private static void StartHook(int pid)
    {
        lock (HookGate)
        {
            // Под замком: если конец игры уже пришёл, перехват не ставим (StopHook ждёт этот же замок, поэтому
            // поставленный перехват всегда снимется).
            if (pid <= 0 || !GameHooks.IsCurrent(pid)) return;
            gamePid = pid;
            if (hookThread != null && hookThread.IsAlive) return;
            var ready = new ManualResetEventSlim();
            // Перехвату нужен поток с циклом сообщений. Он отдельный и ничем другим не занят: если обработчик
            // не ответит вовремя, Windows молча снимет перехват, а ввод в игре начнёт запаздывать.
            var thread = new Thread(() => HookLoop(ready)) { IsBackground = true, Name = "PulsePC.WinLock", Priority = ThreadPriority.Highest };
            hookThread = thread;
            thread.Start();
            ready.Wait(3000);   // поток сам завершится, если перехват поставить не удалось
        }
    }

    private static void HookLoop(ManualResetEventSlim ready)
    {
        try
        {
            hookThreadId = GetCurrentThreadId();
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);   // создаёт очередь сообщений потока до того, как ему пошлют WM_QUIT
            winKeys = default;
            hookProc = HookProc;
            hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, GetModuleHandle(null), 0);
        }
        finally { try { ready.Set(); } catch { } }
        if (hookHandle == IntPtr.Zero) return;

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        UnhookWindowsHookEx(hookHandle);
        hookHandle = IntPtr.Zero;
    }

    private static void StopHook()
    {
        lock (HookGate)
        {
            var thread = hookThread;
            if (thread == null) return;
            hookThread = null;
            PostThreadMessage(hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            thread.Join(2000);
        }
    }

    private static IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code == 0)
            {
                int vk = Marshal.ReadInt32(lParam);   // KBDLLHOOKSTRUCT.vkCode
                if (vk == VK_LWIN || vk == VK_RWIN)
                {
                    int msg = (int)wParam;
                    bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                    // Внутри перехвата GetAsyncKeyState ещё показывает состояние до этого события.
                    bool systemDown = (GetAsyncKeyState(vk) & 0x8000) != 0;
                    if (DecideWin(ref winKeys, vk, up, GameInFront(), systemDown)) return (IntPtr)1;
                }
            }
        }
        catch { }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private static bool GameInFront()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out uint pid);
        return pid != 0 && pid == (uint)gamePid;
    }

    // ======================================================================================
    // Частота опроса мыши
    // ======================================================================================

    public const double MeasureSeconds = 3;
    private const int WM_INPUT = 0x00FF;
    private static readonly int[] StandardRates = { 125, 250, 500, 1000, 2000, 4000, 8000 };

    /// <summary>
    /// Попросить Windows присылать окну сырые отчёты мыши (WM_INPUT). Обычные сообщения мыши при этом остаются.
    /// Отчёты идут, пока окно активно; background — получать их и в неактивном окне (нужно только для проверки).
    /// </summary>
    public static bool BeginMouseCapture(IntPtr hwnd, bool background = false)
    {
        var device = new[] { new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = background ? 0x100u /* RIDEV_INPUTSINK */ : 0, hwndTarget = hwnd } };
        return RegisterRawInputDevices(device, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    /// <summary>Снять регистрацию сырых отчётов мыши.</summary>
    public static void EndMouseCapture()
    {
        var device = new[] { new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = 0x1 /* RIDEV_REMOVE */, hwndTarget = IntPtr.Zero } };
        RegisterRawInputDevices(device, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    /// <summary>Сообщение окна — отчёт мыши? Возвращает устройство, от которого он пришёл.</summary>
    public static bool IsMouseReport(int msg, IntPtr lParam, out IntPtr device)
    {
        device = IntPtr.Zero;
        if (msg != WM_INPUT) return false;
        uint size = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        if (GetRawInputData(lParam, 0x10000005 /* RID_HEADER */, out RAWINPUTHEADER header, ref size, size) == unchecked((uint)-1)) return false;
        if (header.dwType != 0 /* RIM_TYPEMOUSE */) return false;
        device = header.hDevice;
        return true;
    }

    /// <summary>Частота по отчётам самой активной мыши (если их несколько, например мышь и сенсорная панель).</summary>
    public static MouseRate Estimate(IReadOnlyList<(long Ticks, IntPtr Device)> reports)
    {
        if (reports.Count == 0) return new MouseRate(false, 0, 0, 0);
        var main = reports.GroupBy(r => r.Device).OrderByDescending(g => g.Count()).First();
        return Estimate(main.Select(r => r.Ticks).ToList(), Stopwatch.Frequency);
    }

    /// <summary>
    /// Частота опроса по меткам времени отчётов. Мышь шлёт отчёт на каждый опрос только пока двигается, поэтому
    /// замер делится на отрезки по 0,1 с и берётся частота в «быстрых» отрезках (75-й процентиль): паузы и
    /// медленные участки её не занижают, а редкий всплеск из-за задержки в очереди сообщений — не завышает.
    /// </summary>
    internal static MouseRate Estimate(IReadOnlyList<long> ticks, long frequency)
    {
        int n = ticks.Count;
        if (n < 40) return new MouseRate(false, 0, 0, n);

        const double window = 0.1;
        long start = ticks[0];
        int full = (int)Math.Floor((ticks[n - 1] - start) / (double)frequency / window);   // последний неполный отрезок не считается
        if (full < 5) return new MouseRate(false, 0, 0, n);
        var counts = new int[full];
        foreach (long t in ticks)
        {
            int i = (int)((t - start) / (double)frequency / window);
            if (i >= 0 && i < full) counts[i]++;
        }
        var busy = counts.Where(c => c > 0).OrderBy(c => c).ToList();
        if (busy.Count < 5) return new MouseRate(false, 0, 0, n);

        // Отрезки, где мышь двигалась всё время, дают почти одинаковое число отчётов; усредняем их вокруг 75-го процентиля,
        // чтобы не терять точность на счёте целыми отчётами (144 Гц — это то 14, то 15 отчётов за 0,1 с).
        double top = busy[(int)Math.Ceiling(busy.Count * 0.75) - 1];
        double tolerance = Math.Max(1.0, top * 0.03);
        double hz = busy.Where(c => Math.Abs(c - top) <= tolerance).Average() / window;
        return new MouseRate(true, hz, RoundRate(hz), n);
    }

    /// <summary>К ближайшей принятой частоте, если она рядом (в пределах 15 %); иначе — до десятков.</summary>
    internal static int RoundRate(double hz)
    {
        int nearest = StandardRates.OrderBy(r => Math.Abs(r - hz) / r).First();
        return Math.Abs(nearest - hz) / nearest <= 0.15 ? nearest : (int)Math.Round(hz / 10) * 10;
    }

    /// <summary>Мышь, от которой пришло больше всего отчётов (если их несколько, например мышь и сенсорная панель).</summary>
    public static IntPtr MainDevice(IReadOnlyList<(long Ticks, IntPtr Device)> reports) =>
        reports.Count == 0 ? IntPtr.Zero : reports.GroupBy(r => r.Device).OrderByDescending(g => g.Count()).First().Key;

    /// <summary>Короткая оценка частоты для игр.</summary>
    public static string RateVerdict(int hz)
    {
        if (hz < 200) return "Мало для игр: мышь сообщает о движении раз в 8 мс. Если она умеет больше, выберите 500–1000 Гц в её программе или кнопкой на корпусе.";
        if (hz < 400) return "Для игр терпимо, но лучше 500–1000 Гц, если мышь это умеет.";
        if (hz <= 1200) return "Хорошо для игр.";
        return "Выше 1000 Гц — на любителя: разницу трудно заметить, а процессор нагружается сильнее.";
    }

    // ======================================================================================
    // Модель мыши, совет по частоте и последний замер
    // ======================================================================================

    public const string MouseHzKey = "input.mouse.hz", MouseAtKey = "input.mouse.at", MouseIdKey = "input.mouse.id";
    /// <summary>Замер не выше этого — показывать, где поднять частоту.</summary>
    public const int LowRate = 250;

    /// <summary>Марка мыши по коду производителя USB (VID): название, фирменная программа и что в ней нажать.</summary>
    private sealed record MouseBrand(string Name, string Advice);

    private const string AnyApp = " Выберите 1000 Гц. У большинства игровых мышей значение хранится в самой мыши, так что программу потом можно не держать в автозагрузке.";

    private static readonly Dictionary<int, MouseBrand> Brands = new()
    {
        [0x03F0] = new("HyperX", "Откройте HyperX NGenuity (бесплатно, есть в Microsoft Store), выберите мышь и найдите в её настройках «Polling Rate» — это и есть частота опроса." + AnyApp),
        [0x0951] = new("HyperX", "Откройте HyperX NGenuity (бесплатно, есть в Microsoft Store), выберите мышь и найдите в её настройках «Polling Rate» — это и есть частота опроса." + AnyApp),
        [0x046D] = new("Logitech", "Откройте Logitech G HUB, выберите мышь и раздел чувствительности (DPI): там есть «Report rate» — частота опроса." + AnyApp),
        [0x1532] = new("Razer", "Откройте Razer Synapse, выберите мышь и вкладку «Производительность» (Performance): там есть «Polling Rate» — частота опроса." + AnyApp),
        [0x1038] = new("SteelSeries", "Откройте SteelSeries GG, раздел Engine, выберите мышь и найдите «Polling Rate» — частоту опроса." + AnyApp),
        [0x1B1C] = new("Corsair", "Откройте Corsair iCUE, выберите мышь и её настройки устройства: там есть «Polling Rate» — частота опроса." + AnyApp),
        [0x0B05] = new("ASUS", "Откройте Armoury Crate, выберите мышь и раздел «Производительность»: там есть «Polling Rate» — частота опроса." + AnyApp),
        [0x1E7D] = new("ROCCAT", "Откройте ROCCAT Swarm (у новых моделей — Turtle Beach Swarm II), выберите мышь и найдите «Polling Rate» — частоту опроса." + AnyApp),
        [0x2516] = new("Cooler Master", "Откройте Cooler Master MasterPlus+, выберите мышь и найдите «Polling Rate» — частоту опроса." + AnyApp),
        [0x1462] = new("MSI", "Откройте MSI Center, раздел устройств, выберите мышь и найдите «Polling Rate» — частоту опроса." + AnyApp),
        [0x09DA] = new("A4Tech / Bloody", "Откройте программу Bloody (для мышей Bloody) или A4Tech Oscar, выберите мышь и найдите частоту опроса (Report Rate)." + AnyApp),
        [0x1AF3] = new("ZOWIE", "У мышей ZOWIE нет программы: частота меняется кнопкой снизу корпуса. Нажимайте её, глядя на индикатор, — какой цвет какой частоте соответствует, написано в инструкции к модели. Нужна 1000 Гц."),
        [0x3367] = new("Endgame Gear", "Скачайте с сайта Endgame Gear программу для своей модели и выберите в ней «Polling Rate» 1000 Гц. У части моделей частоту меняет и кнопка снизу корпуса."),
        [0x258A] = new("Sinowealth", "Эта микросхема стоит в мышах многих марок (Glorious, Redragon и других). Частоту меняет программа производителя вашей мыши — найдите её на сайте марки — или сочетание кнопок из инструкции. Нужна 1000 Гц."),
        [0x045E] = new("Microsoft", ""),
        [0x413C] = new("Dell", ""),
        [0x17EF] = new("Lenovo", ""),
    };

    /// <summary>Обычные офисные марки: частота у их мышей, как правило, 125 Гц и не меняется.</summary>
    private static readonly HashSet<int> OfficeBrands = new() { 0x045E, 0x413C, 0x17EF };

    private const string GenericAdvice =
        "Найдите на сайте производителя мыши фирменную программу: частота опроса в ней называется «Polling rate» или «Report rate», выберите 1000 Гц. "
        + "У многих игровых мышей частоту меняет кнопка снизу корпуса или сочетание кнопок из инструкции. Обычные офисные мыши работают на 125 Гц, и поднять её нельзя.";

    private const string BluetoothAdvice =
        "Мышь подключена по Bluetooth: так частота почти всегда около 125 Гц. Для игр подключите её через USB-приёмник (2,4 ГГц) или кабелем — тогда доступна 1000 Гц.";

    /// <summary>Мышь, которую видит Windows.</summary>
    public sealed class MouseDevice
    {
        public IntPtr Handle;
        public string Path = "";
        /// <summary>Код производителя и модели USB; -1 — в пути устройства их нет (сенсорная панель, удалённый рабочий стол).</summary>
        public int Vid = -1, Pid = -1;
        /// <summary>Название из самой мыши (HID); пусто — мышь его не сообщает.</summary>
        public string Product = "";
        public bool Bluetooth;
        public string Brand => BrandName(Vid, Product);
        public string Id => Vid >= 0 ? $"{Vid:X4}:{Pid:X4}" : "";
        public string Title => Product.Length > 0 ? Product : Brand.Length > 0 ? "Мышь " + Brand : Vid >= 0 ? "Мышь без названия" : "Встроенное указывающее устройство";
        public string Caption => string.Join(" · ", new[]
        {
            Brand.Length > 0 && !Product.Contains(Brand.Split(' ')[0], StringComparison.OrdinalIgnoreCase) ? Brand : "",
            Vid >= 0 ? $"VID_{Vid:X4} PID_{Pid:X4}" : "",
            Bluetooth ? "Bluetooth" : Vid >= 0 ? "USB" : "",
        }.Where(x => x.Length > 0));
    }

    /// <summary>VID и PID из пути устройства: USB («VID_046D&amp;PID_C08B») и Bluetooth («VID&amp;02046d_PID&amp;b023»).</summary>
    internal static (int Vid, int Pid, bool Bluetooth) ParseDevicePath(string path)
    {
        path ??= "";
        bool bt = path.Contains("{00001124-", StringComparison.OrdinalIgnoreCase) || path.Contains("{00001812-", StringComparison.OrdinalIgnoreCase)
                  || path.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase) || path.Contains("BTHLE", StringComparison.OrdinalIgnoreCase);
        var m = System.Text.RegularExpressions.Regex.Match(path, @"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success)
            m = System.Text.RegularExpressions.Regex.Match(path, @"VID&(?:0001|0002|01|02)([0-9A-F]{4})_PID&([0-9A-F]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return (-1, -1, bt);
        return (Convert.ToInt32(m.Groups[1].Value, 16), Convert.ToInt32(m.Groups[2].Value, 16), bt);
    }

    /// <summary>Марка по VID; у HP (03F0) — HyperX, если это видно из названия.</summary>
    internal static string BrandName(int vid, string product)
    {
        if (vid == 0x03F0)
            return (product ?? "").Contains("HyperX", StringComparison.OrdinalIgnoreCase) ? "HyperX"
                 : (product ?? "").Contains("OMEN", StringComparison.OrdinalIgnoreCase) ? "HP OMEN" : "HP / HyperX";
        return Brands.TryGetValue(vid, out var b) ? b.Name : "";
    }

    /// <summary>Где поднять частоту опроса для этой мыши. Неизвестная марка — общий совет.</summary>
    public static string RaiseAdvice(int vid, string product, bool bluetooth)
    {
        if (bluetooth) return BluetoothAdvice;
        if (vid == 0x03F0 && (product ?? "").Contains("OMEN", StringComparison.OrdinalIgnoreCase))
            return "Откройте OMEN Gaming Hub, выберите мышь и найдите «Polling Rate» — частоту опроса." + AnyApp;
        if (OfficeBrands.Contains(vid) || (vid == 0x03F0 && !(product ?? "").Contains("HyperX", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(product)))
            return "Похоже, это обычная офисная мышь: такие почти всегда работают на 125 Гц, и поменять это нельзя. Если в её описании есть частота опроса 500 или 1000 Гц, ищите настройку в программе производителя; иначе для игр нужна игровая мышь.";
        return Brands.TryGetValue(vid, out var b) && b.Advice.Length > 0 ? b.Advice : GenericAdvice;
    }

    /// <summary>Мыши, которые видит Windows (по одной на модель). Только чтение.</summary>
    public static List<MouseDevice> ListMice()
    {
        var list = new List<MouseDevice>();
        uint count = 0, size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        if (GetRawInputDeviceList(null, ref count, size) == unchecked((uint)-1) || count == 0) return list;
        var devices = new RAWINPUTDEVICELIST[count];
        uint got = GetRawInputDeviceList(devices, ref count, size);
        if (got == unchecked((uint)-1)) return list;
        for (int i = 0; i < got; i++)
            if (devices[i].dwType == 0 /* RIM_TYPEMOUSE */ && Describe(devices[i].hDevice) is { } d) list.Add(d);
        // Приёмник и мышь часто дают несколько одинаковых устройств: оставляем одно на модель, с названием, если есть.
        return list.GroupBy(d => d.Vid >= 0 ? d.Id + (d.Bluetooth ? "bt" : "") : d.Path)
                   .Select(g => g.OrderByDescending(d => d.Product.Length).First())
                   .Where(d => !d.Path.Contains("RDP_MOU", StringComparison.OrdinalIgnoreCase))
                   .OrderBy(d => d.Vid < 0).ThenBy(d => d.Title)
                   .ToList();
    }

    /// <summary>Мышь по дескриптору сырого ввода (например, та, что прислала отчёты при замере). null — не прочиталась.</summary>
    public static MouseDevice Describe(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return null;
        uint chars = 0;
        GetRawInputDeviceInfo(handle, 0x20000007 /* RIDI_DEVICENAME */, null, ref chars);
        if (chars == 0 || chars > 2048) return null;
        var name = new StringBuilder((int)chars + 1);
        if (GetRawInputDeviceInfo(handle, 0x20000007, name, ref chars) == unchecked((uint)-1)) return null;
        var d = new MouseDevice { Handle = handle, Path = name.ToString() };
        (d.Vid, d.Pid, d.Bluetooth) = ParseDevicePath(d.Path);
        if (d.Vid >= 0) d.Product = HidProduct(d.Path);
        return d;
    }

    /// <summary>Название модели из самой мыши. Устройство открывается без прав на чтение и запись — только для запроса строки.</summary>
    private static string HidProduct(string path)
    {
        IntPtr h = CreateFile(path, 0, 3 /* FILE_SHARE_READ | FILE_SHARE_WRITE */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) return "";
        try
        {
            var buf = new byte[512];
            if (!HidD_GetProductString(h, buf, (uint)buf.Length)) return "";
            string s = Encoding.Unicode.GetString(buf);
            int zero = s.IndexOf('\0');
            s = (zero >= 0 ? s[..zero] : s).Trim();
            // Некоторые приёмники отдают общее имя без пользы для человека.
            return s.Equals("USB Receiver", StringComparison.OrdinalIgnoreCase) || s.Equals("Wireless Receiver", StringComparison.OrdinalIgnoreCase)
                   || s.Length < 2 ? "" : s;
        }
        catch { return ""; }
        finally { CloseHandle(h); }
    }

    /// <summary>Последний сохранённый замер: частота, когда, какая мышь. Hz = 0 — замеров не было.</summary>
    public static (int Hz, DateTime At, string Id) LastMeasure(AppSettings s)
    {
        if (s == null || !int.TryParse(s.Get(MouseHzKey), out int hz) || hz <= 0) return (0, default, "");
        DateTime.TryParse(s.Get(MouseAtKey), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime at);
        return (hz, at, s.Get(MouseIdKey));
    }

    /// <summary>Запомнить замер в настройках (сохранение в файл — у вызывающего).</summary>
    public static void RememberMeasure(AppSettings s, int hz, DateTime at, string id)
    {
        s.Set(MouseHzKey, hz.ToString());
        s.Set(MouseAtKey, at.ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture));
        s.Set(MouseIdKey, id ?? "");
    }

    /// <summary>
    /// Сравнение с прошлым замером той же мыши: «Было 125 Гц (19 ноября) → стало 1000 Гц.» Пусто — сравнивать не с чем
    /// (замера не было или он был у другой мыши).
    /// </summary>
    public static string CompareText(int beforeHz, DateTime beforeAt, string beforeId, int nowHz, string nowId, DateTime today)
    {
        if (beforeHz <= 0 || nowHz <= 0) return "";
        if (beforeId.Length > 0 && nowId.Length > 0 && beforeId != nowId) return "";
        string when = beforeAt == default ? "" : beforeAt.Date == today.Date ? $" (сегодня в {beforeAt:HH:mm})"
            : $" ({beforeAt.ToString("d MMMM", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))})";
        return beforeHz == nowHz ? $"Как и в прошлый раз{when}: {nowHz} Гц." : $"Было {beforeHz} Гц{when} → стало {nowHz} Гц.";
    }

    // ======================================================================================
    // Справка по указателю (только чтение)
    // ======================================================================================

    /// <summary>Скорость указателя (1–20, обычная — 10) и включено ли ускорение («Повышенная точность указателя»).</summary>
    public static (bool Ok, int Speed, bool Acceleration) ReadPointer()
    {
        try
        {
            int speed = 0;
            var mouse = new int[3];
            bool ok = SystemParametersInfo(0x0070 /* SPI_GETMOUSESPEED */, 0, ref speed, 0);
            ok &= SystemParametersInfo(0x0003 /* SPI_GETMOUSE */, 0, mouse, 0);
            return (ok, speed, mouse[2] != 0);
        }
        catch { return (false, 0, false); }
    }

    // ======================================================================================
    // Самопроверка (только чтение и расчёты)
    // ======================================================================================

    public static string SelfTest()
    {
        var sb = new StringBuilder();
        var problems = new List<string>();

        var hot = ReadHotkeys();
        if (!hot.Readable) problems.Add("не читаются флаги специальных возможностей");
        foreach (var h in Hotkeys)
        {
            var data = LiveAccess.Get(h.Kind);
            sb.AppendLine($"Вызов сочетанием — {h.Title}: " + (data == null ? "не читается"
                : ((data[1] & HotkeyActive) != 0 ? "срабатывает" : "отключён") + $" (флаги {data[1]} = 0x{data[1]:X})"));
        }
        sb.AppendLine("Копия «как было» для сочетаний: " + (HasHotkeyBackup ? HotkeyBackupFile : "нет, программа их не меняла"));

        var pointer = ReadPointer();
        if (!pointer.Ok) problems.Add("не читаются настройки указателя");
        sb.AppendLine($"Скорость указателя: {pointer.Speed} из 20; повышенная точность указателя: {(pointer.Acceleration ? "включена" : "выключена")}");
        sb.AppendLine($"Блокировка клавиши Win: настройка {(WinLockWanted ? "включена" : "выключена")}, перехват {(WinLockActive ? "стоит" : "не стоит")}");

        // Решение «проглотить или пропустить» — по входным данным, без перехвата.
        var winCases = new (string Name, Func<bool> Check)[]
        {
            ("нажатие и отпускание Win в игре проглатываются", () =>
            {
                var k = new WinKeys();
                return DecideWin(ref k, VK_LWIN, false, true, false) && DecideWin(ref k, VK_LWIN, false, true, false)
                       && DecideWin(ref k, VK_LWIN, true, true, false) && k.Left == 0;
            }),
            ("вне игры Win работает как обычно", () =>
            {
                var k = new WinKeys();
                return !DecideWin(ref k, VK_RWIN, false, false, false) && !DecideWin(ref k, VK_RWIN, true, false, true) && k.Right == 0;
            }),
            ("другие клавиши не трогаются", () =>
            {
                var k = new WinKeys();
                return !DecideWin(ref k, 0x44, false, true, false) && !DecideWin(ref k, 0x44, true, true, false) && k.Left == 0 && k.Right == 0;
            }),
            ("Win зажата вне игры, игра вышла вперёд: отпускание пропускается, клавиша не залипает", () =>
            {
                var k = new WinKeys();
                return !DecideWin(ref k, VK_LWIN, false, false, false)      // нажали на рабочем столе
                       && !DecideWin(ref k, VK_LWIN, false, true, true)     // автоповтор уже в игре
                       && !DecideWin(ref k, VK_LWIN, true, true, true)      // отпустили в игре
                       && DecideWin(ref k, VK_LWIN, false, true, false);    // следующее нажатие в игре — проглочено
            }),
            ("Win зажата в игре, игра закрылась: система видит и нажатие, и отпускание", () =>
            {
                var k = new WinKeys();
                return DecideWin(ref k, VK_LWIN, false, true, false)        // проглочено в игре
                       && !DecideWin(ref k, VK_LWIN, false, false, false)   // автоповтор уже вне игры — пропущен
                       && !DecideWin(ref k, VK_LWIN, true, false, true);    // отпускание — пропущено
            }),
            ("отпускание потеряно на экране блокировки: следующее нажатие в игре проглатывается", () =>
            {
                var k = new WinKeys();
                bool first = !DecideWin(ref k, VK_LWIN, false, false, false);   // Win+L: нажатие пропущено, отпускания мы не увидели
                return first && DecideWin(ref k, VK_LWIN, false, true, false) && DecideWin(ref k, VK_LWIN, true, true, false);
            }),
            ("левая и правая Win считаются отдельно", () =>
            {
                var k = new WinKeys();
                return !DecideWin(ref k, VK_LWIN, false, false, false) && DecideWin(ref k, VK_RWIN, false, true, false)
                       && !DecideWin(ref k, VK_LWIN, true, true, true) && DecideWin(ref k, VK_RWIN, true, true, false);
            }),
        };
        foreach (var c in winCases)
        {
            bool ok = c.Check();
            if (!ok) problems.Add("клавиша Win: " + c.Name);
            sb.AppendLine($"  [{(ok ? "верно" : "ОШИБКА")}] Win: {c.Name}");
        }

        // Расчёт частоты — на придуманных метках времени.
        const long f = 10_000_000;
        var rateCases = new (string Name, List<long> Ticks, int Expect)[]
        {
            ("ровные 125 Гц", Synthetic(125, 3, f), 125),
            ("ровные 500 Гц", Synthetic(500, 3, f), 500),
            ("ровные 1000 Гц", Synthetic(1000, 3, f), 1000),
            ("ровные 8000 Гц", Synthetic(8000, 3, f), 8000),
            ("1000 Гц с паузами в движении (треть времени мышь стоит)", Synthetic(1000, 3, f, pauseEvery: 0.73, pauseLength: 0.25), 1000),
            ("1000 Гц, сообщения приходят пачками (задержки по 30 мс)", Synthetic(1000, 3, f, stallEvery: 0.37, stallLength: 0.03), 1000),
            ("4000 Гц, пачки по 5 мс каждые 23 мс", Synthetic(4000, 3, f, stallEvery: 0.023, stallLength: 0.005), 4000),
            ("500 Гц с разбросом меток ±0,4 мс", Synthetic(500, 3, f, jitter: 0.0004), 500),
            ("250 Гц, мышь двигали только первую секунду", Synthetic(250, 1, f), 250),
            ("нестандартные 144 Гц", Synthetic(144, 3, f), 140),
        };
        foreach (var c in rateCases)
        {
            var r = Estimate(c.Ticks, f);
            bool ok = r.Enough && r.Rounded == c.Expect;
            if (!ok) problems.Add("частота мыши: " + c.Name);
            sb.AppendLine($"  [{(ok ? "верно" : "ОШИБКА")}] мышь: {c.Name} → {(r.Enough ? $"{r.Hz:0} Гц, показано «около {r.Rounded} Гц»" : "мало данных")} ({r.Reports} отчётов)");
        }
        var few = Estimate(Synthetic(1000, 0.03, f), f);
        var shortMove = Estimate(Synthetic(1000, 0.3, f), f);
        bool refuses = !few.Enough && !shortMove.Enough && !Estimate(new List<(long, IntPtr)>()).Enough;
        if (!refuses) problems.Add("частота мыши: при нехватке данных должен быть отказ");
        sb.AppendLine($"  [{(refuses ? "верно" : "ОШИБКА")}] мышь: при нехватке данных (мышь почти не двигали) число не показывается");

        // Модель мыши и совет — на подставных путях устройств.
        var pathCases = new (string Name, string Path, int Vid, int Pid, bool Bt)[]
        {
            ("USB-мышь", @"\\?\HID#VID_03F0&PID_028E&MI_00#7&1a2b3c&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}", 0x03F0, 0x028E, false),
            ("приёмник Logitech, коллекция", @"\\?\HID#VID_046D&PID_C539&MI_01&Col01#8&2f1e&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}", 0x046D, 0xC539, false),
            ("Bluetooth LE", @"\\?\HID#{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&02046d_PID&b023_REV&0011#9&1&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}", 0x046D, 0xB023, true),
            ("сенсорная панель без VID", @"\\?\HID#ELAN0001&Col02#5&2c4f&0&0001#{378de44c-56ef-11d1-bc8c-00a0c91405dd}", -1, -1, false),
        };
        foreach (var c in pathCases)
        {
            var (vid, pid, bt) = ParseDevicePath(c.Path);
            bool ok = vid == c.Vid && pid == c.Pid && bt == c.Bt;
            if (!ok) problems.Add("путь мыши: " + c.Name);
            sb.AppendLine($"  [{(ok ? "верно" : "ОШИБКА")}] путь мыши: {c.Name} → {(vid >= 0 ? $"VID {vid:X4}, PID {pid:X4}" : "кодов нет")}{(bt ? ", Bluetooth" : "")}");
        }
        var adviceCases = new (string Name, bool Ok)[]
        {
            ("HyperX → NGenuity", BrandName(0x03F0, "HyperX Pulsefire Haste 2") == "HyperX" && RaiseAdvice(0x03F0, "HyperX Pulsefire Haste 2", false).Contains("NGenuity")),
            ("HP без «HyperX» в названии → офисная мышь", RaiseAdvice(0x03F0, "HP USB Optical Mouse", false).Contains("офисная")),
            ("Logitech → G HUB", BrandName(0x046D, "") == "Logitech" && RaiseAdvice(0x046D, "", false).Contains("G HUB")),
            ("Razer → Synapse", RaiseAdvice(0x1532, "", false).Contains("Synapse")),
            ("SteelSeries → GG", RaiseAdvice(0x1038, "", false).Contains("SteelSeries GG")),
            ("ZOWIE → кнопка снизу", RaiseAdvice(0x1AF3, "", false).Contains("кнопкой снизу")),
            ("неизвестная марка → общий совет", BrandName(0x1234, "") == "" && RaiseAdvice(0x1234, "", false) == GenericAdvice),
            ("Bluetooth → совет про приёмник", RaiseAdvice(0x046D, "MX Master 3", true).Contains("Bluetooth")),
        };
        foreach (var c in adviceCases)
        {
            if (!c.Ok) problems.Add("совет по мыши: " + c.Name);
            sb.AppendLine($"  [{(c.Ok ? "верно" : "ОШИБКА")}] совет: {c.Name}");
        }
        var day = new DateTime(2026, 11, 19, 15, 0, 0);
        var compareCases = new (string Name, string Got, string Expect)[]
        {
            ("было 125 → стало 1000", CompareText(125, new DateTime(2026, 11, 18, 21, 40, 0), "03F0:028E", 1000, "03F0:028E", day), "Было 125\u00A0Гц (18 ноября) → стало 1000\u00A0Гц."),
            ("сегодня, то же значение", CompareText(1000, new DateTime(2026, 11, 19, 9, 5, 0), "03F0:028E", 1000, "03F0:028E", day), "Как и в прошлый раз (сегодня в 09:05): 1000\u00A0Гц."),
            ("другая мышь — не сравнивать", CompareText(125, day, "046D:C539", 1000, "03F0:028E", day), ""),
            ("замера не было — не сравнивать", CompareText(0, default, "", 1000, "03F0:028E", day), ""),
        };
        foreach (var c in compareCases)
        {
            bool ok = c.Got == c.Expect;
            if (!ok) problems.Add("сравнение замеров: " + c.Name);
            sb.AppendLine($"  [{(ok ? "верно" : "ОШИБКА")}] замер: {c.Name} → «{c.Got}»");
        }
        var store = new AppSettings();
        RememberMeasure(store, 125, new DateTime(2026, 11, 19, 10, 15, 0), "03F0:028E");
        var last = LastMeasure(store);
        bool kept = last.Hz == 125 && last.At == new DateTime(2026, 11, 19, 10, 15, 0) && last.Id == "03F0:028E" && LastMeasure(new AppSettings()).Hz == 0;
        if (!kept) problems.Add("последний замер не сохраняется");
        sb.AppendLine($"  [{(kept ? "верно" : "ОШИБКА")}] последний замер: запись и чтение (в подставных настройках)");

        sb.AppendLine("Мыши на этом ПК (только чтение):");
        var mice = ListMice();
        if (mice.Count == 0) sb.AppendLine("  не найдено ни одной");
        foreach (var m in mice)
            sb.AppendLine($"  {m.Title} — {(m.Caption.Length > 0 ? m.Caption : "без кодов")}; марка: {(m.Brand.Length > 0 ? m.Brand : "неизвестна")}; путь {m.Path}");
        var lastHere = LastMeasure(AppSettings.Load());
        sb.AppendLine("Последний замер в настройках программы: " + (lastHere.Hz > 0 ? $"{lastHere.Hz} Гц, {lastHere.At:dd.MM.yyyy HH:mm}, мышь {lastHere.Id}" : "нет"));

        return (problems.Count == 0 ? "OK" : "FAIL: " + string.Join("; ", problems)) + "\n" + sb;
    }

    /// <summary>Метки времени для проверки расчёта: ровный опрос, паузы в движении, задержки очереди, разброс.</summary>
    private static List<long> Synthetic(double hz, double seconds, long frequency, double pauseEvery = 0, double pauseLength = 0,
        double stallEvery = 0, double stallLength = 0, double jitter = 0)
    {
        var list = new List<long>();
        var random = new Random(7);
        int total = (int)(hz * seconds);
        for (int i = 0; i < total; i++)
        {
            double t = i / hz;
            if (pauseEvery > 0 && t % pauseEvery < pauseLength) continue;                       // мышь стоит — отчётов нет
            if (stallEvery > 0 && t % stallEvery < stallLength) t = t - t % stallEvery + stallLength;   // отчёты дождались конца задержки
            if (jitter > 0) t += (random.NextDouble() * 2 - 1) * jitter;
            list.Add((long)(Math.Max(0, t) * frequency));
        }
        list.Sort();
        return list;
    }

    // ---------- Windows ----------

    private delegate IntPtr LowLevelProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER { public uint dwType; public uint dwSize; public IntPtr hDevice; public IntPtr wParam; }

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, LowLevelProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(IntPtr rawInput, uint command, out RAWINPUTHEADER header, ref uint size, uint headerSize);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[] list, ref uint count, uint size);
    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder data, ref uint size);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("hid.dll")] private static extern bool HidD_GetProductString(IntPtr device, byte[] buffer, uint length);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, [In, Out] int[] value, uint winIni);
}

/// <summary>Настоящая система: SystemParametersInfo для STICKYKEYS, FILTERKEYS и TOGGLEKEYS.</summary>
internal sealed class LiveAccessBackend : IAccessBackend
{
    // Код чтения, код записи, размер структуры в 32-битных словах.
    private static (uint Get, uint Set, int Words) Codes(AccessKind kind) => kind switch
    {
        AccessKind.Sticky => (0x003A, 0x003B, 2),   // STICKYKEYS: cbSize, dwFlags
        AccessKind.Filter => (0x0032, 0x0033, 6),   // FILTERKEYS: cbSize, dwFlags и четыре задержки
        _ => (0x0034, 0x0035, 2),                   // TOGGLEKEYS: cbSize, dwFlags
    };

    public uint[] Get(AccessKind kind)
    {
        var (get, _, words) = Codes(kind);
        var data = new uint[words];
        data[0] = (uint)(words * 4);
        try { return SystemParametersInfo(get, data[0], data, 0) ? data : null; }
        catch { return null; }
    }

    public bool Set(AccessKind kind, uint[] data)
    {
        var (_, set, words) = Codes(kind);
        if (data == null || data.Length != words) return false;
        data[0] = (uint)(words * 4);
        try { return SystemParametersInfo(set, data[0], data, 0x1 | 0x2 /* SPIF_UPDATEINIFILE | SPIF_SENDCHANGE */); }
        catch { return false; }
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, [In, Out] uint[] data, uint winIni);
}
