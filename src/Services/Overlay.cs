using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PulsePC.Panels;

namespace PulsePC.Services;

/// <summary>Что можно показать в оверлее (порядок перечисления — порядок в табличке).</summary>
/// Battery — заряд беспроводных устройств (DeviceBattery); хранится отдельной настройкой overlay.battery, см. OverlayOptions.Battery.
public enum OverlayItem { Fps, Low1, FrameGraph, Gpu, Cpu, Vram, Ping, Clock, Battery }

/// <summary>Почему оверлей сейчас виден или спрятан.</summary>
public enum OverlayState { Off, NoGame, Minimized, NotForeground, HiddenByKey, Shown }

/// <summary>Настройки оверлея (ключи overlay.* в настройках программы; система не меняется, права администратора не нужны).</summary>
public sealed class OverlayOptions
{
    public const string KOn = "overlay.on", KItems = "overlay.items", KCorner = "overlay.corner", KSize = "overlay.size",
        KLayout = "overlay.layout", KOpacity = "overlay.opacity", KHotkey = "overlay.hotkey", KBattery = "overlay.battery";
    public static readonly OverlayItem[] DefaultItems = { OverlayItem.Fps, OverlayItem.Low1, OverlayItem.Gpu, OverlayItem.Cpu };
    /// <summary>Непрозрачность подложки, %: ниже 50 светлые цифры на светлой картинке читаются плохо.</summary>
    public static readonly int[] OpacitySteps = { 50, 65, 80, 95 };
    public static readonly SoundHotkey DefaultHotkey = new(true, false, true, false, "O");

    /// <summary>Включён ли оверлей (по умолчанию выключен).</summary>
    public bool Enabled { get; set; }
    public List<OverlayItem> Items { get; set; } = DefaultItems.ToList();
    /// <summary>Угол экрана: tl, tr, bl, br.</summary>
    public string Corner { get; set; } = "tr";
    /// <summary>Размер: s, m, l.</summary>
    public string Size { get; set; } = "m";
    /// <summary>Столбиком (true) или строкой (false).</summary>
    public bool Column { get; set; }
    public int Opacity { get; set; } = 80;
    public SoundHotkey Hotkey { get; set; } = DefaultHotkey;
    /// <summary>
    /// Показывать заряд устройств: null — сам (включено, если хотя бы у одного устройства известен заряд), true/false — выбор человека.
    /// Хранится отдельно от Items: «по умолчанию включено» должно работать и у тех, кто уже менял список показаний.
    /// </summary>
    public bool? Battery { get; set; }

    public bool Has(OverlayItem item) => Items.Contains(item);

    /// <summary>Показывать ли заряд, если устройства с зарядом есть (found) — с учётом «сам».</summary>
    public bool BatteryOn(bool found) => Battery ?? found;

    /// <summary>Набор показаний для таблички: заряд — только если он включён и есть что показать (haveCharges).</summary>
    public OverlayOptions WithBattery(bool haveCharges)
    {
        var o = Clone();
        o.Items = Items.Where(i => i != OverlayItem.Battery).ToList();
        if (haveCharges && BatteryOn(true)) o.Items.Add(OverlayItem.Battery);
        o.Items = o.Items.Distinct().OrderBy(x => x).ToList();
        return o;
    }

    public OverlayOptions Clone() => new()
    {
        Enabled = Enabled, Items = Items.ToList(), Corner = Corner, Size = Size, Column = Column, Opacity = Opacity, Hotkey = Hotkey, Battery = Battery,
    };

    public static OverlayOptions Load(AppSettings s)
    {
        var o = new OverlayOptions();
        if (s == null) return o;
        o.Enabled = s.GetBool(KOn);
        string items = s.Get(KItems, null);
        if (items != null)
        {
            o.Items = items.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => Enum.TryParse(x, true, out OverlayItem i) && Enum.IsDefined(i) ? (OverlayItem?)i : null)
                .Where(x => x != null && x != OverlayItem.Battery).Select(x => x.Value).Distinct().OrderBy(x => x).ToList();
        }
        o.Battery = s.Get(KBattery, "") switch { "1" => true, "0" => false, _ => null };
        string corner = s.Get(KCorner, "tr");
        o.Corner = corner is "tl" or "tr" or "bl" or "br" ? corner : "tr";
        string size = s.Get(KSize, "m");
        o.Size = size is "s" or "m" or "l" ? size : "m";
        o.Column = s.Get(KLayout, "row") == "column";
        o.Opacity = int.TryParse(s.Get(KOpacity, "80"), out int op) ? OpacitySteps.OrderBy(x => Math.Abs(x - op)).First() : 80;
        var hk = SoundHotkey.Parse(s.Get(KHotkey, null));
        o.Hotkey = hk.IsEmpty || hk.Problem() != null ? DefaultHotkey : hk;
        return o;
    }

    /// <summary>Записать в настройки (файл сохраняет вызывающий — Settings.Save()).</summary>
    public void Save(AppSettings s)
    {
        if (s == null) return;
        s.SetBool(KOn, Enabled);
        var items = Items.Where(i => i != OverlayItem.Battery).Distinct().OrderBy(x => x).ToList();
        s.Set(KItems, items.Count == 0 ? "none" : string.Join(",", items));
        if (Battery != null) s.SetBool(KBattery, Battery.Value);
        s.Set(KCorner, Corner);
        s.Set(KSize, Size);
        s.Set(KLayout, Column ? "column" : "row");
        s.Set(KOpacity, Opacity.ToString(System.Globalization.CultureInfo.InvariantCulture));
        s.Set(KHotkey, Hotkey.ToString());
    }
}

/// <summary>Показания для таблички. NaN — показания нет (в табличке «—»).</summary>
public sealed class OverlayReading
{
    public double Fps = double.NaN, Low1 = double.NaN, MedianFps = double.NaN;
    public double GpuLoad = double.NaN, GpuTemp = double.NaN, CpuLoad = double.NaN, VramGb = double.NaN, PingMs = double.NaN;
    /// <summary>Пинг до сервера игры (иначе — до интернета).</summary>
    public bool PingToServer;
    /// <summary>Время кадра за ~10 с по отрезкам, мс (NaN — в отрезке не было кадров).</summary>
    public float[] FrameMs = Array.Empty<float>();
    /// <summary>Заряд беспроводных устройств (до трёх, самые разряженные первыми); пусто — показывать нечего.</summary>
    public DeviceCharge[] Charges = Array.Empty<DeviceCharge>();
    public DateTime Now = DateTime.Now;

    public bool FpsDrop => Overlay.IsFpsDrop(Fps, MedianFps);
    public bool GpuHot => Overlay.IsGpuHot(GpuTemp);
    public bool PingBad => Overlay.IsPingBad(PingMs);

    /// <summary>Подставные числа: предпросмотр в разделе и проверочный режим без игры.</summary>
    public static OverlayReading Sample(int tick = 0, bool alarm = false)
    {
        var frames = new float[60];
        for (int i = 0; i < frames.Length; i++)
        {
            int k = (i + tick) % 60;
            frames[i] = (float)(6.9 + 0.6 * Math.Sin(k * 0.7) + 0.35 * Math.Sin(k * 2.3));
            if (k == 17 || k == 44) frames[i] = alarm ? 31f : 11.5f;      // рывок (красным) — только в тревожном примере
        }
        return new OverlayReading
        {
            Fps = alarm ? 52 : 144 + 3 * Math.Sin(tick * 0.9), Low1 = alarm ? 41 : 97, MedianFps = 143,
            GpuLoad = 93 + (tick % 3), GpuTemp = alarm ? 87 : 67, CpuLoad = 61 - (tick % 4), VramGb = 5.4, PingMs = 14 + (tick % 2),
            FrameMs = frames, Now = DateTime.Now, Charges = DeviceBattery.SampleOverlay(),
        };
    }
}

/// <summary>
/// Оверлей поверх игры: отдельное прозрачное окно программы (как у Xbox Game Bar), в процесс игры ничего не встраивается —
/// ни хуков DirectX, ни библиотек, ни чтения памяти игры. Виден, только если он включён, идёт запись игры и окно игры
/// на переднем плане. Показания — из уже существующих источников: LiveData (видеокарта, процессор, видеопамять, пинг до
/// интернета), запись кадров PresentMon (FPS, скользящий 1 %, время кадра) и пинг до сервера игры (GameNet). Новых опросов нет.
/// </summary>
public static class Overlay
{
    public const double GpuHotC = 85, PingBadMs = 150, FpsDropShare = 0.6, MinFpsForDrop = 30;
    /// <summary>Отступ от края экрана, DIP.</summary>
    public const double EdgeMargin = 16;
    public const string Dash = "—";

    private static PanelHost host;
    private static OverlayOptions options = new();
    private static OverlayWindow window;
    private static DispatcherTimer timer;
    private static bool hiddenByKey, hotkeyWanted, hotkeyRegistered, closed;
    private static int gamePid, tickNo;
    private static HwndSource msgWindow;
    private static string demo, shotsDir;
    private static bool demoAlarm;
    private static DateTime lastError = DateTime.MinValue;
    private static readonly Stopwatch tickCost = new();
    private static long tickCount;
    private static readonly Dictionary<int, string> imageByPid = new();

    /// <summary>Почему оверлей сейчас виден или спрятан (для раздела).</summary>
    public static OverlayState State { get; private set; } = OverlayState.Off;
    /// <summary>Игра, над которой оверлей показывается или ждёт.</summary>
    public static string Game { get; private set; } = "";
    /// <summary>Почему горячая клавиша не включилась; null — всё в порядке.</summary>
    public static string HotkeyProblem { get; private set; }

    /// <summary>Игра сейчас в исключительном полноэкранном режиме: программа выводит оверлей, но Windows не показывает его поверх игры.</summary>
    public static bool Exclusive { get; private set; }

    /// <summary>Игры, в которых за этот запуск программы встречался исключительный полноэкранный режим (для подсказки в разделе).</summary>
    public static IReadOnlyCollection<string> ExclusiveGames { get { lock (exclusiveGames) return exclusiveGames.ToArray(); } }
    private static readonly HashSet<string> exclusiveGames = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Сочетание сейчас занято программой (игра на переднем плане).</summary>
    public static bool HotkeyActive => hotkeyRegistered;
    /// <summary>Состояние или настройки изменились (поток интерфейса).</summary>
    public static event Action Changed;

    public static bool Enabled => options.Enabled;
    /// <summary>Служба запущена (Init был); в снимке раздела без окна программы — нет.</summary>
    public static bool Ready => host != null;
    public static OverlayOptions Current => options.Clone();
    /// <summary>Сколько в среднем занимает один такт оверлея в потоке интерфейса, мс (для замера нагрузки).</summary>
    public static double AvgTickMs => tickCount == 0 ? 0 : tickCost.Elapsed.TotalMilliseconds / tickCount;

    // ───────────────────────── запуск и настройки ─────────────────────────

    public static void Init(PanelHost panelHost)
    {
        host = panelHost;
        options = OverlayOptions.Load(host.Settings);
        // Проверочный режим без игры (только без прав администратора): подставные числа поверх тестового окна.
        // PULSEPC_OVERLAY_DEMO=1 — «игра» = любое окно другой программы на переднем плане;
        // PULSEPC_OVERLAY_DEMO=<заголовок окна> — «игра» = это окно, табличка — в углу этого окна;
        // PULSEPC_OVERLAY_SHOTS=<папка> — снимки экрана под окном для всех размеров и раскладок.
        if (!host.Admin)
        {
            demo = Environment.GetEnvironmentVariable("PULSEPC_OVERLAY_DEMO") is { Length: > 0 } d ? d : null;
            shotsDir = demo != null && Environment.GetEnvironmentVariable("PULSEPC_OVERLAY_SHOTS") is { Length: > 0 } s ? s : null;
            demoAlarm = Environment.GetEnvironmentVariable("PULSEPC_OVERLAY_DEMO_ALARM") == "1";
        }
        // Выход из программы: окно оверлея закрывается вместе с главным (иначе оно держало бы программу открытой).
        var main = Application.Current?.MainWindow;
        if (main != null) main.Closed += (_, _) => Shutdown();
        if (Application.Current != null) Application.Current.Exit += (_, _) => Shutdown();
        Restart();
    }

    /// <summary>Включить или выключить оверлей (меню значка у часов, раздел). Сохраняет настройку.</summary>
    public static void SetEnabled(bool on)
    {
        if (host == null) return;
        options.Enabled = on;
        try { options.Save(host.Settings); host.Settings.Save(); } catch (Exception ex) { Log.Error("оверлей: сохранение настройки", ex); }
        Restart();
        Changed?.Invoke();
    }

    /// <summary>Новые настройки из раздела (уже сохранены разделом).</summary>
    public static void Apply(OverlayOptions o)
    {
        if (o == null || host == null) return;
        var hk = options.Hotkey;
        options = o.Clone();
        options.Hotkey = hk;      // клавиша меняется только через TrySetHotkey: там проверяется, свободна ли она
        Restart();
        if (State == OverlayState.Shown) Tick();
        Changed?.Invoke();
    }

    private static void Restart()
    {
        if (closed) return;
        if (options.Enabled || demo != null)
        {
            timer ??= MakeTimer();
            if (!timer.IsEnabled) timer.Start();
            HookForeground(true);
        }
        else
        {
            timer?.Stop();
            HookForeground(false);
            SetHotkey(false);
            HideWindow();
            SetState(OverlayState.Off, "");
        }
    }

    private static DispatcherTimer MakeTimer()
    {
        // Два раза в секунду, без анимаций; приоритет ниже ввода и отрисовки окна программы.
        var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        t.Tick += (_, _) => Tick();
        return t;
    }

    private static void Shutdown()
    {
        if (closed) return;
        closed = true;
        try { timer?.Stop(); } catch { }
        try { HookForeground(false); } catch { }
        try { SetHotkey(false); } catch { }
        try { window?.Close(); } catch { }
        window = null;
        try { msgWindow?.Dispose(); } catch { }
        msgWindow = null;
    }

    // ───────────────────────── когда виден ─────────────────────────

    /// <summary>
    /// Решение «показывать ли»: включён И идёт запись игры И окно игры на переднем плане и не свёрнуто И не спрятан клавишей.
    /// </summary>
    public static OverlayState Decide(bool enabled, bool recording, bool gameForeground, bool minimized, bool hiddenByHotkey)
    {
        if (!enabled) return OverlayState.Off;
        if (!recording) return OverlayState.NoGame;
        if (minimized) return OverlayState.Minimized;
        if (!gameForeground) return OverlayState.NotForeground;
        if (hiddenByHotkey) return OverlayState.HiddenByKey;
        return OverlayState.Shown;
    }

    /// <summary>Одна фраза для раздела: что с оверлеем сейчас.</summary>
    public static string StateText(OverlayState state, string game, SoundHotkey hotkey, bool exclusive = false) => state switch
    {
        OverlayState.Shown when exclusive => L.F("«{0}» в исключительном полноэкранном режиме — поверх него Windows может не показывать оверлей. Если его не видно, включите в игре режим экрана «Без рамки» или «Полноэкранный в окне».", game),
        OverlayState.Off => L.T("Оверлей выключен."),
        OverlayState.NoGame => L.T("Оверлей включён: он появится поверх игры, когда она запущена и её окно на переднем плане."),
        OverlayState.Minimized => L.F("Оверлей включён. Игра «{0}» свёрнута — оверлей спрятан.", game),
        OverlayState.NotForeground => L.F("Оверлей включён. Игра «{0}» сейчас не на переднем плане — оверлей спрятан.", game),
        OverlayState.HiddenByKey => L.F("Оверлей спрятан клавишей {0}. Нажмите её ещё раз в игре, чтобы вернуть.", hotkey.Text),
        _ => L.F("Оверлей виден поверх «{0}».", game),
    };

    private static void SetState(OverlayState state, string game)
    {
        if (state == State && game == Game) return;
        State = state; Game = game ?? "";
        Changed?.Invoke();
    }

    private static void Tick()
    {
        if (closed) return;
        tickCost.Start();
        try { TickCore(); }
        catch (Exception ex)
        {
            // Никаких исключений наружу: оверлей просто прячется, ошибка — в журнал не чаще раза в 10 минут.
            if ((DateTime.Now - lastError).TotalMinutes >= 10) { lastError = DateTime.Now; Log.Error("оверлей", ex); }
            HideWindow();
            // Оверлей спрятан — сочетание не должно оставаться занятым (следующий такт займёт его снова, если всё в порядке).
            try { SetHotkey(false); } catch { }
        }
        finally { tickCost.Stop(); tickCount++; }
    }

    private static void TickCore()
    {
        tickNo++;
        LiveGameFeed feed = demo != null ? new LiveGameFeed { Pid = -1, Game = L.T("Проверка оверлея") } : host?.GameFeed?.Invoke();
        int pid = feed?.Pid ?? 0;
        // Новая игра — снова видно; имена процессов — заново (номер процесса мог достаться другой программе).
        if (pid != gamePid) { gamePid = pid; hiddenByKey = false; imageByPid.Clear(); lastLogged = -1; logsThisGame = 0; exclusiveWarned = false; }

        var game = feed == null ? default : demo != null ? DemoWindow() : FindGameWindow(feed);
        var state = Decide(options.Enabled || demo != null, feed != null, game.Foreground, game.Minimized, hiddenByKey);
        if (pendingExclusive != null && state is OverlayState.NoGame or OverlayState.NotForeground or OverlayState.Minimized or OverlayState.Off) NotifyExclusive();
        // Клавиша занята, только пока игра на переднем плане: в других программах сочетание остаётся свободным.
        SetHotkey(state is OverlayState.Shown or OverlayState.HiddenByKey);
        SetState(state, feed?.Game);
        if (demo == null && feed != null && options.Enabled) Diagnose(state);
        if (state != OverlayState.Shown || game.Hwnd == IntPtr.Zero) { HideWindow(); return; }

        var reading = demo != null ? OverlayReading.Sample(tickNo, demoAlarm) : Read(feed);
        // Заряд — только если включён и хотя бы у одного устройства он известен.
        var shown = options.WithBattery(reading.Charges.Length > 0);
        if (shown.Items.Count == 0) { HideWindow(); return; }
        EnsureWindow();
        window.View.Show(reading, shown);
        Place(game.Hwnd, reassert: tickNo % 2 == 0);
        if (!window.IsVisible) { window.Show(); Place(game.Hwnd, reassert: true); }
        if (shotsDir != null) ShotStep(game.Hwnd);
        else if (demo != null && tickNo % 120 == 0)
            Log.Info($"оверлей (проверка): тактов {tickCount}, в среднем {AvgTickMs:0.000} мс на такт в потоке интерфейса");
    }

    // ───────────────────────── почему не виден ─────────────────────────
    private static int lastLogged = -1, logsThisGame;
    private static bool exclusiveWarned;
    /// <summary>Игра, о которой ещё не сказали, что оверлей поверх неё мог быть не виден.</summary>
    private static string pendingExclusive;

    private static void NotifyExclusive()
    {
        string game = pendingExclusive;
        pendingExclusive = null;
        if (game == null) return;
        host?.Notify(L.T("Оверлей мог быть не виден поверх игры"), L.F("«{0}» шла в исключительном полноэкранном режиме — поверх него Windows может не показывать оверлей. Включите в игре режим «Без рамки».", game), false, () => host.Open("sys:Оверлей"));
    }

    /// <summary>
    /// Журнал причин (смена состояния, не больше 40 записей за игру) и исключительный полноэкранный режим: в нём Windows не
    /// показывает поверх игры чужие окна, и внешний оверлей не виден, хотя программа его выводит. Признак — от самой Windows
    /// (SHQueryUserNotificationState = QUNS_RUNNING_D3D_FULL_SCREEN). Человеку — одно уведомление за игру, что переключить.
    /// </summary>
    private static void Diagnose(OverlayState state)
    {
        int quns = 0;
        try { SHQueryUserNotificationState(out quns); } catch { }
        bool exclusive = quns == 3;
        // Признак — только пока игра на переднем плане и оверлей должен быть виден: в другое время Windows говорит о другом окне.
        if (state == OverlayState.Shown)
        {
            bool changed = exclusive != Exclusive;
            Exclusive = exclusive;
            lock (exclusiveGames) { if (exclusive) exclusiveGames.Add(Game); else changed |= exclusiveGames.Remove(Game); }
            if (changed) Changed?.Invoke();
        }
        else if (Exclusive) { Exclusive = false; Changed?.Invoke(); }
        int key = (int)state * 10 + (exclusive ? 1 : 0);
        if (key != lastLogged && logsThisGame < 40)
        {
            lastLogged = key; logsThisGame++;
            IntPtr fg = GetForegroundWindow();
            int fgPid = 0;
            if (fg != IntPtr.Zero) GetWindowThreadProcessId(fg, out fgPid);
            Log.Info($"оверлей: {state}; игра «{Game}» (процесс {gamePid}); впереди окно процесса {fgPid} «{(fgPid > 0 ? ImageOf(fgPid) : "")}»"
                     + (exclusive ? "; игра в исключительном полноэкранном режиме — Windows не показывает поверх неё другие окна" : ""));
        }
        // Уведомление — не сразу: пока полноэкранная игра впереди, Windows придерживает уведомления. Покажется, когда игра уйдёт
        // с переднего плана или закроется (TickCore).
        if (exclusive && state == OverlayState.Shown && !exclusiveWarned)
        {
            exclusiveWarned = true;
            pendingExclusive = Game;
        }
    }

    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

    private static OverlayReading Read(LiveGameFeed feed)
    {
        var live = host.Live();
        var r = new OverlayReading
        {
            GpuLoad = live.GpuLoad, GpuTemp = live.GpuTemp, CpuLoad = live.CpuLoad, VramGb = live.VramUsedGb, Now = DateTime.Now,
            // Из памяти: заряд опрашивается раз в несколько минут службой DeviceBattery, оверлей новых запросов не делает.
            Charges = DeviceBattery.ForOverlay(),
        };
        var cap = feed.Frames;
        if (cap != null)
        {
            // FPS «сейчас» — как на «Обзоре»: за последнюю секунду; кадры не идут дольше 2,5 с — показания нет.
            bool fresh = (DateTime.Now - cap.LiveAt).TotalSeconds < 2.5;
            if (fresh) r.Fps = cap.LiveFps;
            if (options.Has(OverlayItem.Low1) || options.Has(OverlayItem.FrameGraph) || options.Has(OverlayItem.Fps))
            {
                // Кадры не идут (загрузка, пауза) — 1 % и график тоже «—», а не числа полуминутной давности.
                if (fresh)
                {
                    var recent = cap.Recent(30, 10, 60);
                    r.Low1 = recent.Low1Fps; r.MedianFps = recent.MedianFps; r.FrameMs = recent.FrameMs;
                }
            }
        }
        if (options.Has(OverlayItem.Ping))
        {
            double server = feed.ServerPingMs?.Invoke() ?? double.NaN;
            r.PingToServer = !double.IsNaN(server);
            r.PingMs = r.PingToServer ? server : live.PingMs;
        }
        return r;
    }

    // ───────────────────────── окно игры ─────────────────────────

    private readonly struct GameWindow
    {
        public IntPtr Hwnd { get; init; }
        public bool Foreground { get; init; }
        public bool Minimized { get; init; }
    }

    /// <summary>Окно переднего плана принадлежит игре: тот же процесс или процесс с тем же именем. Описатели процессов не держатся.</summary>
    private static GameWindow FindGameWindow(LiveGameFeed feed)
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return default;
        GetWindowThreadProcessId(fg, out int pid);
        if (pid == 0 || pid == Environment.ProcessId) return default;
        bool mine = pid == feed.Pid || (feed.Image.Length > 0 && string.Equals(ImageOf(pid), feed.Image, StringComparison.OrdinalIgnoreCase));
        if (!mine) return default;
        return new GameWindow { Hwnd = fg, Foreground = true, Minimized = IsIconic(fg) };
    }

    private static string ImageOf(int pid)
    {
        if (imageByPid.TryGetValue(pid, out var name)) return name;
        try { using var p = Process.GetProcessById(pid); name = p.ProcessName; } catch { name = ""; }
        if (imageByPid.Count > 256) imageByPid.Clear();
        imageByPid[pid] = name;
        return name;
    }

    /// <summary>Проверочный режим: «игра» — окно с заданным заголовком (или любое чужое окно на переднем плане при «1»).</summary>
    private static GameWindow DemoWindow()
    {
        if (demo == "1")
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return default;
            GetWindowThreadProcessId(fg, out int pid);
            var cls = new StringBuilder(64);
            GetClassName(fg, cls, cls.Capacity);
            if (pid == Environment.ProcessId || cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return default;
            return new GameWindow { Hwnd = fg, Foreground = true, Minimized = IsIconic(fg) };
        }
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var title = new StringBuilder(256);
            GetWindowText(h, title, title.Capacity);
            if (title.ToString() != demo) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found == IntPtr.Zero ? default : new GameWindow { Hwnd = found, Foreground = true, Minimized = IsIconic(found) };
    }

    // ───────────────────────── окно оверлея ─────────────────────────

    private static void EnsureWindow()
    {
        if (window != null) return;
        window = new OverlayWindow();
        window.EnsureHandle();
    }

    private static void HideWindow()
    {
        try { if (window?.IsVisible == true) window.Hide(); } catch { }
    }

    /// <summary>
    /// Левый верхний угол окна размером w×h в выбранном углу области area с отступом margin (всё в пикселях экрана).
    /// </summary>
    public static (int X, int Y) PlaceIn(int left, int top, int right, int bottom, int w, int h, string corner, int margin)
    {
        int x = corner is "tl" or "bl" ? left + margin : right - margin - w;
        int y = corner is "tl" or "tr" ? top + margin : bottom - margin - h;
        // Табличка шире области (маленькое окно игры) — прижать к левому/верхнему краю, но не за пределы.
        x = Math.Max(left, Math.Min(x, right - w));
        y = Math.Max(top, Math.Min(y, bottom - h));
        return (x, y);
    }

    /// <summary>
    /// Поставить окно в угол монитора, на котором окно игры (с учётом масштаба этого монитора), и подтвердить «поверх всех».
    /// Игра на весь экран — угол всего монитора, игра в окне — угол рабочей области (без панели задач).
    /// </summary>
    private static void Place(IntPtr gameHwnd, bool reassert)
    {
        IntPtr mon = MonitorFromWindow(gameHwnd, MonitorDefaultToNearest);
        var mi = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return;
        var area = mi.Work;
        bool gotGame = GetWindowRect(gameHwnd, out var g);
        if (gotGame && g.Left <= mi.Monitor.Left && g.Top <= mi.Monitor.Top && g.Right >= mi.Monitor.Right && g.Bottom >= mi.Monitor.Bottom) area = mi.Monitor;
        if (demo != null && demo != "1" && gotGame) area = g;      // проверочный режим: угол тестового окна, а не всего экрана
        uint dpi = 96;
        try { if (GetDpiForMonitor(mon, 0, out uint dx, out _) == 0 && dx > 0) dpi = dx; } catch { }
        double scale = dpi / 96.0;
        window.View.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = window.View.DesiredSize;
        int w = (int)Math.Ceiling(size.Width * scale), h = (int)Math.Ceiling(size.Height * scale);
        var (x, y) = PlaceIn(area.Left, area.Top, area.Right, area.Bottom, w, h, options.Corner, (int)Math.Round(EdgeMargin * scale));
        IntPtr me = window.Handle;
        bool moved = !GetWindowRect(me, out var cur) || cur.Left != x || cur.Top != y;
        if (moved || reassert)
            SetWindowPos(me, HwndTopmost, x, y, 0, 0, SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
    }

    // ───────────────────────── смена окна переднего плана ─────────────────────────

    private static IntPtr fgHook;
    private static WinEventProc fgProc;        // делегат держится в поле, иначе сборщик мусора убрал бы его из-под Windows

    /// <summary>
    /// Сообщение Windows «сменилось окно переднего плана» (обычное событие для программ, без внедрения в игру): после Alt+Tab
    /// оверлей прячется и сочетание освобождается сразу, а не через полсекунды на следующем такте.
    /// </summary>
    private static void HookForeground(bool on)
    {
        if (on && fgHook == IntPtr.Zero)
        {
            fgProc ??= (_, _, _, _, _, _, _) =>
            {
                if (closed) return;
                Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Tick));
            };
            fgHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, fgProc, 0, 0, WinEventOutOfContext);
        }
        else if (!on && fgHook != IntPtr.Zero)
        {
            UnhookWinEvent(fgHook);
            fgHook = IntPtr.Zero;
        }
    }

    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    private const uint EventSystemForeground = 0x0003, WinEventOutOfContext = 0x0000;
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);

    // ───────────────────────── горячая клавиша ─────────────────────────

    private const int HotkeyId = 0x5A02, WmHotkey = 0x0312, HotkeyTaken = 1409;
    private const uint ModNoRepeat = 0x4000;

    private static void SetHotkey(bool want)
    {
        if (want == hotkeyWanted) return;
        hotkeyWanted = want;
        if (want)
        {
            int code = Register(options.Hotkey);
            hotkeyRegistered = code == 0;
            string problem = code == 0 ? null : RegisterError(options.Hotkey, code);
            if (problem != HotkeyProblem) { HotkeyProblem = problem; Changed?.Invoke(); }
        }
        else if (hotkeyRegistered)
        {
            Unregister();
            hotkeyRegistered = false;
        }
    }

    /// <summary>
    /// Сменить сочетание (из раздела, поток интерфейса). Сначала проверяется, свободно ли оно в Windows; если занято — прежнее
    /// остаётся. Сочетание держится только пока игра на переднем плане, поэтому для проверки оно занимается на миг.
    /// </summary>
    public static bool TrySetHotkey(SoundHotkey hotkey, AppSettings settings, out string error)
    {
        error = hotkey.Problem();
        if (error != null) return false;
        var previous = options.Hotkey;
        if (hotkeyRegistered) { Unregister(); hotkeyRegistered = false; }
        int code = Register(hotkey);
        if (code != 0)
        {
            error = RegisterError(hotkey, code);
            if (hotkeyWanted) hotkeyRegistered = Register(previous) == 0;
            return false;
        }
        if (hotkeyWanted) hotkeyRegistered = true; else Unregister();
        options.Hotkey = hotkey;
        HotkeyProblem = null;
        if (settings != null)
        {
            settings.Set(OverlayOptions.KHotkey, hotkey.ToString());
            try { settings.Save(); } catch (Exception ex) { Log.Error("оверлей: сохранение клавиши", ex); }
        }
        Changed?.Invoke();
        return true;
    }

    private static string RegisterError(SoundHotkey hotkey, int code) => code == HotkeyTaken
        ? L.F("Сочетание {0} уже занято другой программой или самой Windows. Выберите другое.", hotkey.Text)
        : L.F("Windows не приняла сочетание {0} (ошибка {1}). Выберите другое.", hotkey.Text, code);

    private static int Register(SoundHotkey hotkey)
    {
        try
        {
            if (msgWindow == null)
            {
                // Окно только для сообщений: его нет ни на экране, ни на панели задач.
                msgWindow = new HwndSource(new HwndSourceParameters("PulsePC.Overlay") { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 });
                msgWindow.AddHook(WndProc);
            }
            return RegisterHotKey(msgWindow.Handle, HotkeyId, hotkey.Modifiers | ModNoRepeat, (uint)hotkey.Vk) ? 0 : Math.Max(1, Marshal.GetLastWin32Error());
        }
        catch { return 1; }
    }

    private static void Unregister()
    {
        try { if (msgWindow != null) UnregisterHotKey(msgWindow.Handle, HotkeyId); } catch { }
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey || wParam.ToInt64() != HotkeyId) return IntPtr.Zero;
        handled = true;
        // Нажали в тот миг, когда игра уже ушла с переднего плана, — не переключать: такт сразу освободит сочетание.
        bool gameInFront;
        try { gameInFront = demo != null || (host?.GameFeed?.Invoke() is { } feed && FindGameWindow(feed).Foreground); }
        catch { gameInFront = false; }
        if (gameInFront) hiddenByKey = !hiddenByKey;
        Tick();
        return IntPtr.Zero;
    }

    // ───────────────────────── числа и подписи ─────────────────────────

    /// <summary>Кусок значения: число, единица, тревога (красным).</summary>
    public readonly record struct Seg(string Text, string Unit, bool Alarm);

    public static bool IsFpsDrop(double fps, double medianFps) =>
        !double.IsNaN(fps) && !double.IsNaN(medianFps) && medianFps >= MinFpsForDrop && fps < FpsDropShare * medianFps;
    public static bool IsGpuHot(double temp) => !double.IsNaN(temp) && temp >= GpuHotC;
    public static bool IsPingBad(double ms) => !double.IsNaN(ms) && ms >= PingBadMs;

    public static string Whole(double v) => double.IsNaN(v) || double.IsInfinity(v) ? Dash : Math.Round(v).ToString("0", L.Culture);
    public static string Tenth(double v) => double.IsNaN(v) || double.IsInfinity(v) ? Dash : v.ToString("0.0", L.Culture);

    /// <summary>Подпись в табличке (коротко, заглавными).</summary>
    public static string Label(OverlayItem item) => item switch
    {
        OverlayItem.Fps => "FPS",
        OverlayItem.Low1 => L.Pick("1 %", "1% LOW"),
        OverlayItem.FrameGraph => L.Pick("ВРЕМЯ КАДРА", "FRAME TIME"),
        OverlayItem.Gpu => L.Pick("ВИДЕОКАРТА", "GPU"),
        OverlayItem.Cpu => L.Pick("ПРОЦЕССОР", "CPU"),
        OverlayItem.Vram => L.Pick("ВИДЕОПАМЯТЬ", "VRAM"),
        OverlayItem.Ping => L.Pick("ПИНГ", "PING"),
        OverlayItem.Battery => L.Pick("ЗАРЯД", "BATTERY"),
        _ => L.Pick("ВРЕМЯ", "TIME"),
    };

    /// <summary>Значение показания кусками. Температура процессора не показывается никогда — её здесь нет и не будет.</summary>
    public static Seg[] Segments(OverlayItem item, OverlayReading r)
    {
        static Seg Part(string text, string unit, bool alarm = false) => new(text, text == Dash ? "" : unit, alarm && text != Dash);
        switch (item)
        {
            case OverlayItem.Fps: return new[] { Part(Whole(r.Fps), "", r.FpsDrop) };
            case OverlayItem.Low1: return new[] { Part(Whole(r.Low1), "") };
            case OverlayItem.Gpu:
                if (double.IsNaN(r.GpuTemp)) return new[] { Part(Whole(r.GpuLoad), "%") };
                if (double.IsNaN(r.GpuLoad)) return new[] { Part(Whole(r.GpuTemp), "°C", r.GpuHot) };
                return new[] { Part(Whole(r.GpuLoad), "%"), Part(Whole(r.GpuTemp), "°C", r.GpuHot) };
            case OverlayItem.Cpu: return new[] { Part(Whole(r.CpuLoad), "%") };
            case OverlayItem.Vram: return new[] { Part(Tenth(r.VramGb), L.T("ГБ")) };
            case OverlayItem.Ping: return new[] { Part(Whole(r.PingMs), L.T("мс"), r.PingBad) };
            case OverlayItem.Clock: return new[] { Part(r.Now.ToString("HH:mm", L.Culture), "") };
            case OverlayItem.Battery:
                // Значки устройств и молнию «заряжается» рисует табличка (BatteryStrip); здесь — числа.
                if (r.Charges.Length == 0) return new[] { Part(Dash, "") };
                return r.Charges.Select(c => Part((c.Approx ? "≈" : "") + Whole(c.Percent ?? double.NaN), "%", DeviceBattery.IsLow(c))).ToArray();
            default: return Array.Empty<Seg>();
        }
    }

    /// <summary>Самые широкие значения — по ним ширина ячейки, чтобы табличка не «прыгала» при смене цифр.</summary>
    public static Seg[] Widest(OverlayItem item) => item switch
    {
        OverlayItem.Fps or OverlayItem.Low1 => new[] { new Seg("000", "", false) },
        OverlayItem.Gpu => new[] { new Seg("100", "%", false), new Seg("100", "°C", false) },
        OverlayItem.Cpu => new[] { new Seg("100", "%", false) },
        OverlayItem.Vram => new[] { new Seg("00,0", L.T("ГБ"), false) },
        OverlayItem.Ping => new[] { new Seg("000", L.T("мс"), false) },
        OverlayItem.Clock => new[] { new Seg("00:00", "", false) },
        OverlayItem.Battery => new[] { new Seg("100", "%", false) },
        _ => Array.Empty<Seg>(),
    };

    // ───────────────────────── проверочные снимки ─────────────────────────

    private static int shotIdx = -1, shotWait;

    private static readonly (string Name, string Size, bool Column, string Corner, bool All)[] ShotPlan =
    {
        ("s-row", "s", false, "tr", false), ("m-row", "m", false, "tr", false), ("l-row", "l", false, "tr", false),
        ("s-col", "s", true, "tr", false), ("m-col", "m", true, "tr", false), ("l-col", "l", true, "tr", false),
        ("m-row-all", "m", false, "tl", true), ("m-col-all", "m", true, "br", true), ("l-col-all", "l", true, "bl", true),
    };

    /// <summary>Проверочный режим: по очереди все размеры и раскладки, каждый — снимок экрана под тестовым окном.</summary>
    private static void ShotStep(IntPtr gameHwnd)
    {
        if (shotIdx < 0) { shotIdx = 0; ApplyShot(); return; }
        if (++shotWait < 3) return;       // полторы секунды: окно перестроилось и встало на место
        try
        {
            Directory.CreateDirectory(shotsDir);
            if (GetWindowRect(gameHwnd, out var r))
            {
                using var bmp = new System.Drawing.Bitmap(r.Right - r.Left, r.Bottom - r.Top);
                using (var gr = System.Drawing.Graphics.FromImage(bmp))
                {
                    // Снимок экрана с CAPTUREBLT: в него попадают и прозрачные (layered) окна — то есть сам оверлей.
                    IntPtr screen = GetDC(IntPtr.Zero), dst = gr.GetHdc();
                    try { BitBlt(dst, 0, 0, bmp.Width, bmp.Height, screen, r.Left, r.Top, 0x00CC0020 | 0x40000000); }
                    finally { gr.ReleaseHdc(dst); ReleaseDC(IntPtr.Zero, screen); }
                }
                bmp.Save(Path.Combine(shotsDir, $"{L.Code}-{(demoAlarm ? "alarm-" : "")}{ShotPlan[shotIdx].Name}.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        catch (Exception ex) { Log.Error("оверлей: проверочный снимок", ex); }
        shotIdx++;
        shotWait = 0;
        if (shotIdx >= ShotPlan.Length)
        {
            try
            {
                File.WriteAllText(Path.Combine(shotsDir, $"{L.Code}-done.txt"),
                    $"тактов {tickCount}, в среднем {AvgTickMs:0.000} мс на такт (поток интерфейса); процессорное время программы {Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds:0.00} с");
            }
            catch { }
            shotsDir = null;
            return;
        }
        ApplyShot();
    }

    private static void ApplyShot()
    {
        var p = ShotPlan[shotIdx];
        options.Size = p.Size; options.Column = p.Column; options.Corner = p.Corner;
        options.Items = p.All ? Enum.GetValues<OverlayItem>().ToList() : OverlayOptions.DefaultItems.ToList();
    }

    // ───────────────────────── самопроверка ─────────────────────────

    /// <summary>
    /// Логика на подставных данных: настройки, «когда виден», размещение, числа и подписи, скользящий 1 %, раскладка таблички,
    /// фильтры «игр по поведению». Плюс что видно на этом ПК (мониторы и их масштаб). Ничего не меняет и окон не показывает.
    /// PULSEPC_OVERLAY_RENDER=&lt;папка&gt; — ещё и снимки таблички во всех размерах и раскладках на светлой и тёмной картинке
    /// (картинки — PULSEPC_OVERLAY_BG_LIGHT / PULSEPC_OVERLAY_BG_DARK, иначе заливка).
    /// </summary>
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        var fails = new List<string>();
        void Check(string what, bool ok) { sb.AppendLine((ok ? "  ok  " : "  FAIL ") + what); if (!ok) fails.Add(what); }

        // Настройки: по умолчанию выключено, FPS, 1 %, видеокарта, процессор; Ctrl+Shift+O; запись и чтение.
        var fresh = OverlayOptions.Load(new AppSettings());
        Check("по умолчанию выключен", !fresh.Enabled);
        Check("по умолчанию: FPS, 1 %, видеокарта, процессор", fresh.Items.SequenceEqual(new[] { OverlayItem.Fps, OverlayItem.Low1, OverlayItem.Gpu, OverlayItem.Cpu }));
        Check("клавиша по умолчанию Ctrl+Shift+O и она допустима", fresh.Hotkey.ToString() == "Ctrl+Shift+O" && fresh.Hotkey.Problem() == null && fresh.Hotkey.Vk == 'O');
        var st = new AppSettings();
        var o = new OverlayOptions { Enabled = true, Items = new() { OverlayItem.Clock, OverlayItem.Ping, OverlayItem.Fps }, Corner = "bl", Size = "l", Column = true, Opacity = 65, Hotkey = new SoundHotkey(true, true, false, false, "F9") };
        o.Save(st);
        var back = OverlayOptions.Load(st);
        Check("настройки записываются и читаются", back.Enabled && back.Corner == "bl" && back.Size == "l" && back.Column && back.Opacity == 65
              && back.Hotkey.ToString() == "Ctrl+Alt+F9" && back.Items.SequenceEqual(new[] { OverlayItem.Fps, OverlayItem.Ping, OverlayItem.Clock }));
        var none = new OverlayOptions { Items = new() };
        none.Save(st);
        Check("пустой выбор остаётся пустым", OverlayOptions.Load(st).Items.Count == 0);
        st.Set(OverlayOptions.KCorner, "middle"); st.Set(OverlayOptions.KSize, "xxl"); st.Set(OverlayOptions.KOpacity, "7"); st.Set(OverlayOptions.KHotkey, "Q");
        st.Set(OverlayOptions.KItems, "Fps,CpuTemp,Garbage,Gpu");
        var bad = OverlayOptions.Load(st);
        Check("непонятные значения заменяются допустимыми (температуры процессора нет)", bad.Corner == "tr" && bad.Size == "m" && bad.Opacity == 50
              && bad.Hotkey == OverlayOptions.DefaultHotkey && bad.Items.SequenceEqual(new[] { OverlayItem.Fps, OverlayItem.Gpu }));
        Check("в списке показаний нет температуры процессора", !Enum.GetNames<OverlayItem>().Any(n => n.Contains("CpuTemp", StringComparison.OrdinalIgnoreCase)));

        // Заряд устройств: по умолчанию «сам» — виден, если хотя бы у одного устройства известен заряд; выбор человека хранится отдельно.
        Check("заряд по умолчанию: есть устройства — виден, нет — не виден",
              fresh.Battery == null && fresh.WithBattery(true).Has(OverlayItem.Battery) && !fresh.WithBattery(false).Has(OverlayItem.Battery));
        var bst = new AppSettings();
        new OverlayOptions { Items = new() { OverlayItem.Fps, OverlayItem.Battery }, Battery = false }.Save(bst);
        var bOff = OverlayOptions.Load(bst);
        Check("заряд выключен человеком — не виден даже при устройствах; в списке показаний его нет",
              bOff.Battery == false && !bOff.WithBattery(true).Has(OverlayItem.Battery) && bst.Get(OverlayOptions.KItems) == "Fps");
        new OverlayOptions { Items = new(), Battery = true }.Save(bst);
        var bOnly = OverlayOptions.Load(bst);
        Check("только заряд: табличка не пустая, пока есть устройства", bOnly.WithBattery(true).Items.SequenceEqual(new[] { OverlayItem.Battery }) && bOnly.WithBattery(false).Items.Count == 0);
        var br = new OverlayReading { Charges = new[] { new DeviceCharge { Percent = 12, Kind = DeviceKind.Mouse }, new DeviceCharge { Percent = 64, Kind = DeviceKind.Headset }, new DeviceCharge { Percent = 9, Charging = true } } };
        Check("заряд: 12 % — красным, 64 % — нет, 9 % на зарядке — нет", Segments(OverlayItem.Battery, br) is [{ Text: "12", Unit: "%", Alarm: true }, { Alarm: false }, { Text: "9", Alarm: false }]);
        Check("заряд: устройств нет — «—»", Segments(OverlayItem.Battery, new OverlayReading()) is [{ Text: Dash }]);

        // Когда виден.
        Check("выключен в настройках — не виден", Decide(false, true, true, false, false) == OverlayState.Off);
        Check("нет игры — не виден", Decide(true, false, false, false, false) == OverlayState.NoGame);
        Check("игра не на переднем плане (Alt+Tab) — не виден", Decide(true, true, false, false, false) == OverlayState.NotForeground);
        Check("игра свёрнута — не виден", Decide(true, true, true, true, false) == OverlayState.Minimized);
        Check("спрятан клавишей — не виден", Decide(true, true, true, false, true) == OverlayState.HiddenByKey);
        Check("включён, игра на переднем плане — виден", Decide(true, true, true, false, false) == OverlayState.Shown);
        Check("выключен, хотя игра на переднем плане — не виден", Decide(false, true, true, false, false) != OverlayState.Shown);
        Check("фраза для раздела есть у каждого состояния", Enum.GetValues<OverlayState>().All(s => StateText(s, "Deadlock", OverlayOptions.DefaultHotkey).Length > 5));

        // Размещение: углы, отступ, второй монитор слева (отрицательные координаты), табличка шире окна.
        Check("правый верхний угол", PlaceIn(0, 0, 1920, 1080, 200, 50, "tr", 16) == (1704, 16));
        Check("левый нижний угол", PlaceIn(0, 0, 1920, 1080, 200, 50, "bl", 16) == (16, 1014));
        Check("правый нижний угол над панелью задач", PlaceIn(0, 0, 1920, 1032, 200, 50, "br", 20) == (1700, 962));
        Check("монитор слева от основного", PlaceIn(-2560, 0, 0, 1440, 300, 60, "tl", 20) == (-2540, 20));
        Check("табличка шире окна — не уходит за край", PlaceIn(100, 100, 300, 200, 400, 50, "tr", 16) == (100, 116));

        // Числа и подписи.
        var r = OverlayReading.Sample();
        r.Fps = 147.6; r.GpuLoad = 93.2; r.GpuTemp = 67.4; r.CpuLoad = double.NaN; r.VramGb = 5.43; r.PingMs = 14.4;
        Check("FPS округляется: 148", Segments(OverlayItem.Fps, r)[0].Text == "148");
        Check("нет показания — «—» без единицы", Segments(OverlayItem.Cpu, r) is [{ Text: Dash, Unit: "" }]);
        Check("видеокарта: загрузка и температура", Segments(OverlayItem.Gpu, r) is [{ Text: "93", Unit: "%" }, { Text: "67", Unit: "°C", Alarm: false }]);
        r.GpuTemp = 85;
        Check("видеокарта от 85 °C — красным", Segments(OverlayItem.Gpu, r)[1].Alarm && !IsGpuHot(84.9));
        r.GpuTemp = double.NaN;
        Check("нет температуры видеокарты — только загрузка", Segments(OverlayItem.Gpu, r) is [{ Text: "93", Unit: "%" }]);
        Check("видеопамять с десятыми", Segments(OverlayItem.Vram, r)[0].Text == 5.43.ToString("0.0", L.Culture));
        Check("пинг в мс; от 150 мс — красным", Segments(OverlayItem.Ping, r)[0].Unit == L.T("мс") && !Segments(OverlayItem.Ping, r)[0].Alarm && IsPingBad(150));
        Check("время ЧЧ:ММ", Segments(OverlayItem.Clock, new OverlayReading { Now = new DateTime(2026, 11, 22, 9, 5, 0) })[0].Text == "09:05");
        Check("падение FPS: 52 при обычных 143 — красным", IsFpsDrop(52, 143) && !IsFpsDrop(120, 143) && !IsFpsDrop(10, 20) && !IsFpsDrop(double.NaN, 143));
        Check("подписи есть у всех показаний", Enum.GetValues<OverlayItem>().All(i => Label(i).Length > 0));

        // Скользящий 1 %: 10 с плохих кадров, потом 30 с ровных 10 мс с редкими 25 мс — старые в окно не попадают.
        var cap = new FpsCapture();
        Check("без кадров 1 % неизвестен", double.IsNaN(cap.Recent().Low1Fps) && cap.Recent().Count == 0);
        cap.Feed("Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped,TimeInSeconds,msInPresentAPI,msBetweenPresents");
        double clock = 0;
        void Frame(double ms) { clock += ms / 1000; cap.Feed(FormattableString.Invariant($"game.exe,1,0x1,DXGI,0,0,0,{clock:0.000000},0.5,{ms:0.000}")); }
        for (int i = 0; i < 200; i++) Frame(50);                          // 10 с по 50 мс (20 FPS)
        for (int i = 0; i < 3000; i++) Frame(i % 50 == 25 ? 25 : 10);    // ~31 с ровно, 2 % кадров по 25 мс
        var recent = cap.Recent(30, 10, 60);
        var all = cap.Result();
        Check($"скользящий 1 % за 30 с ≈ 40 FPS (получено {recent.Low1Fps:0.0}), старые рывки не учтены",
              Math.Abs(recent.Low1Fps - 40) < 1.5 && recent.Count >= 2800 && recent.Count <= 3000);
        Check($"обычный FPS окна ≈ 100 (получено {recent.MedianFps:0.0})", Math.Abs(recent.MedianFps - 100) < 1);
        Check($"итог записи не изменился: 1 % всей записи ниже (получено {all?.Low1:0.0})", all != null && all.Low1 < 30 && all.Frames == 3200);
        Check("график времени кадра: 60 отрезков, рывок 25 мс виден", recent.FrameMs.Length == 60 && recent.FrameMs.Where(v => !float.IsNaN(v)).Max() >= 24.9f);
        var tiny = new FpsCapture();
        tiny.Feed("TimeInSeconds,msBetweenPresents");
        for (int i = 1; i <= 10; i++) tiny.Feed(FormattableString.Invariant($"{i * 0.01:0.000},10"));
        Check("кадров мало — 1 % «—»", double.IsNaN(tiny.Recent().Low1Fps));
        for (int i = 0; i < 40000; i++) Frame(1);                         // кольцо переполнено: 40 000 кадров по 1 мс
        var wrapped = cap.Recent(30, 10, 60);
        Check($"кольцо переполняется без ошибок (кадров в окне {wrapped.Count})", wrapped.Count >= 29990 && wrapped.Count <= 30001 && Math.Abs(wrapped.Low1Fps - 1000) < 1);

        // Свой процесс не считается игрой и не попадает в «Что мешало» (там он учитывается как нагрузка самой программы).
        Check("«Пульс ПК» не может стать «игрой по поведению»", GameFinder.NotGameReason(@"C:\Program Files\Пульс ПК\PulsePC.exe") != null);

        // Раскладка таблички (тот же элемент, что в окне оверлея и в предпросмотре).
        if (Application.Current != null && Application.Current.Dispatcher.CheckAccess())
        {
            var sizes = new Dictionary<string, Size>();
            foreach (string size in new[] { "s", "m", "l" })
                foreach (bool column in new[] { false, true })
                {
                    var view = new OverlayView();
                    view.Show(OverlayReading.Sample(), new OverlayOptions { Size = size, Column = column, Items = Enum.GetValues<OverlayItem>().ToList() });
                    view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    sizes[size + (column ? "-col" : "-row")] = view.DesiredSize;
                }
            Check("строкой — длинная и низкая, столбиком — узкая и высокая",
                  new[] { "s", "m", "l" }.All(z => sizes[z + "-row"].Width > sizes[z + "-row"].Height * 4
                      && sizes[z + "-col"].Width < sizes[z + "-row"].Width / 2 && sizes[z + "-col"].Height > sizes[z + "-row"].Height * 3));
            Check("три размера: маленький < средний < крупный",
                  sizes["s-row"].Width < sizes["m-row"].Width && sizes["m-row"].Width < sizes["l-row"].Width && sizes["s-col"].Height < sizes["m-col"].Height && sizes["m-col"].Height < sizes["l-col"].Height);
            var v1 = new OverlayView();
            var opt = new OverlayOptions { Items = OverlayOptions.DefaultItems.ToList() };
            var low = OverlayReading.Sample(); low.Fps = 9; low.Low1 = 8;
            v1.Show(low, opt); v1.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); double w1 = v1.DesiredSize.Width;
            var high = OverlayReading.Sample(); high.Fps = 288; high.Low1 = 199; high.GpuLoad = 100;
            v1.Show(high, opt); v1.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); double w2 = v1.DesiredSize.Width;
            Check($"ширина не прыгает при смене цифр ({w1:0.#} и {w2:0.#})", Math.Abs(w1 - w2) < 0.5);
            sb.AppendLine("  размеры таблички (DIP, все показания): " + string.Join(", ", sizes.Select(k => $"{k.Key} {k.Value.Width:0}×{k.Value.Height:0}")));
            string dir = Environment.GetEnvironmentVariable("PULSEPC_OVERLAY_RENDER");
            if (!string.IsNullOrEmpty(dir))
            {
                try { sb.AppendLine("  снимки: " + RenderShots(dir)); }
                catch (Exception ex) { sb.AppendLine("  снимки не получились: " + ex.Message); }
            }
        }

        // Что видно на этом ПК: мониторы и их масштаб (только чтение).
        try
        {
            var mons = new List<string>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (m, _, _, _) =>
            {
                var mi = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(m, ref mi))
                {
                    uint dpi = 0;
                    try { GetDpiForMonitor(m, 0, out dpi, out _); } catch { }
                    mons.Add($"{mi.Monitor.Right - mi.Monitor.Left}×{mi.Monitor.Bottom - mi.Monitor.Top} в ({mi.Monitor.Left}; {mi.Monitor.Top}), масштаб {(dpi > 0 ? dpi * 100 / 96 + " %" : "?")}");
                }
                return true;
            }, IntPtr.Zero);
            sb.AppendLine("На этом ПК мониторов: " + mons.Count + (mons.Count > 0 ? " — " + string.Join("; ", mons) : ""));
        }
        catch (Exception ex) { sb.AppendLine("Мониторы не прочитаны: " + ex.Message); }

        return (fails.Count == 0 ? "OK" : "FAIL: " + string.Join("; ", fails)) + Environment.NewLine + sb;
    }

    /// <summary>Снимки таблички (без окна): все размеры и раскладки, светлая и тёмная картинка под ней, обычные числа и тревога.</summary>
    private static string RenderShots(string dir)
    {
        Directory.CreateDirectory(dir);
        int count = 0;
        foreach (string bg in new[] { "light", "dark" })
        {
            ImageSource picture = null;
            string file = Environment.GetEnvironmentVariable(bg == "light" ? "PULSEPC_OVERLAY_BG_LIGHT" : "PULSEPC_OVERLAY_BG_DARK");
            if (!string.IsNullOrEmpty(file) && File.Exists(file))
            {
                var bi = new BitmapImage();
                bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.UriSource = new Uri(Path.GetFullPath(file)); bi.EndInit();
                picture = bi;
            }
            foreach (string size in new[] { "s", "m", "l" })
                foreach (bool column in new[] { false, true })
                    foreach (bool all in new[] { false, true })
                    {
                        if (all && size == "s") continue;
                        var o = new OverlayOptions { Size = size, Column = column, Corner = "tr", Items = all ? Enum.GetValues<OverlayItem>().ToList() : OverlayOptions.DefaultItems.ToList() };
                        RenderOne(Path.Combine(dir, $"{L.Code}-{bg}-{size}-{(column ? "col" : "row")}{(all ? "-all" : "")}.png"), picture, bg == "dark", o, OverlayReading.Sample(3));
                        count++;
                    }
            RenderOne(Path.Combine(dir, $"{L.Code}-{bg}-m-row-alarm.png"), picture, bg == "dark",
                new OverlayOptions { Size = "m", Items = Enum.GetValues<OverlayItem>().ToList() }, OverlayReading.Sample(3, alarm: true));
            RenderOne(Path.Combine(dir, $"{L.Code}-{bg}-m-row-50pct.png"), picture, bg == "dark",
                new OverlayOptions { Size = "m", Opacity = 50 }, OverlayReading.Sample(3));
            // Обычный набор + заряд устройств (так видит табличку тот, у кого найдена гарнитура или мышь с зарядом).
            foreach (string size in new[] { "s", "m", "l" })
                foreach (bool column in new[] { false, true })
                {
                    RenderOne(Path.Combine(dir, $"{L.Code}-{bg}-{size}-{(column ? "col" : "row")}-battery.png"), picture, bg == "dark",
                        new OverlayOptions { Size = size, Column = column }.WithBattery(true), OverlayReading.Sample(3));
                    count++;
                }
            count += 2;
        }
        return $"{count} файлов в {dir}";
    }

    private static void RenderOne(string file, ImageSource picture, bool dark, OverlayOptions o, OverlayReading reading)
    {
        const double W = 720, H = 405;
        var view = new OverlayView();
        view.Show(reading, o);
        view.HorizontalAlignment = HorizontalAlignment.Right;
        view.VerticalAlignment = VerticalAlignment.Top;
        view.Margin = new Thickness(EdgeMargin);
        var grid = new Grid { Width = W, Height = H };
        if (picture != null) grid.Children.Add(new System.Windows.Controls.Image { Source = picture, Stretch = Stretch.UniformToFill });
        else grid.Background = new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x26, 0x30) : Color.FromRgb(0xE8, 0xEE, 0xF4));
        grid.Children.Add(view);
        grid.Measure(new Size(W, H));
        grid.Arrange(new Rect(0, 0, W, H));
        grid.UpdateLayout();
        const double scale = 1.25;
        var bmp = new RenderTargetBitmap((int)(W * scale), (int)(H * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(grid);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    // ───────────────────────── Windows ─────────────────────────

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x1, SwpNoActivate = 0x10, SwpNoOwnerZOrder = 0x200;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public RECT Monitor, Work; public uint Flags; }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
