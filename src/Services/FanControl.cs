using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PulsePC.Panels;

namespace PulsePC.Services;

/// <summary>Точка кривой: при температуре T видеокарты вентиляторы крутятся на L процентов.</summary>
public readonly record struct FanPoint(int T, int L);

/// <summary>Минута статистики вентиляторов видеокарты.</summary>
public sealed class FanMinute
{
    public DateTime T { get; set; }
    /// <summary>Максимум температуры видеокарты за минуту, °C.</summary>
    public float Temp { get; set; }
    /// <summary>Средняя скорость, % от максимальной.</summary>
    public float Fan { get; set; }
    /// <summary>Средние обороты, об/мин.</summary>
    public float Rpm { get; set; }
    /// <summary>Доля замеров, когда вентиляторы стояли, 0..1.</summary>
    public float Stop { get; set; }
    /// <summary>Хотя бы часть минуты шла игра.</summary>
    public bool Game { get; set; }
    // С версии 1.4.0. В старых файлах этих полей нет — тогда NaN.
    /// <summary>Средняя загрузка видеокарты, %.</summary>
    public float Load { get; set; } = float.NaN;
    /// <summary>Среднее потребление видеокарты, Вт (по её датчику).</summary>
    public float Watts { get; set; } = float.NaN;
    /// <summary>Средняя загрузка процессора, % (для оценки расхода электричества).</summary>
    public float Cpu { get; set; } = float.NaN;
}

/// <summary>
/// Период для сравнения охлаждения («как было» и «как сейчас»): только минуты игры при загрузке видеокарты от 85 %
/// в те дни, когда есть и простой, — чтобы вычесть летнюю жару (разница «в игре минус в простое за тот же день»).
/// </summary>
public sealed class DustPeriod
{
    public DateTime From, To;
    /// <summary>Минуты игры под полной нагрузкой (в днях, где есть простой для сравнения).</summary>
    public int Minutes;
    public int Days;
    public double SumTemp, SumIdle, SumFan, SumWatts;
    public int FanN, WattsN;

    public double GameTemp => Minutes > 0 ? SumTemp / Minutes : double.NaN;
    /// <summary>Температура в простое в те же дни (взвешена по минутам игры).</summary>
    public double IdleTemp => Minutes > 0 ? SumIdle / Minutes : double.NaN;
    /// <summary>Насколько видеокарта в игре горячее, чем в простое, °C.</summary>
    public double Delta => GameTemp - IdleTemp;
    public double Fan => FanN > 0 ? SumFan / FanN : double.NaN;
    public double Watts => WattsN > 0 ? SumWatts / WattsN : double.NaN;
    public bool Enough => Minutes >= FanControl.DustMinMinutes;

    public void Add(DustPeriod d)
    {
        if (d == null || d.Minutes == 0) return;
        if (Minutes == 0 || d.From < From) From = d.From;
        if (d.To > To) To = d.To;
        Minutes += d.Minutes; Days += d.Days;
        SumTemp += d.SumTemp; SumIdle += d.SumIdle; SumFan += d.SumFan; SumWatts += d.SumWatts;
        FanN += d.FanN; WattsN += d.WattsN;
    }

    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>Строка для настроек: «от;до;минуты;дни;сумма t;сумма простоя;n скорости;сумма скорости;n Вт;сумма Вт».</summary>
    public string Pack() => Minutes == 0 ? "" : string.Join(";", From.ToString("yyyy-MM-dd", Inv), To.ToString("yyyy-MM-dd", Inv), Minutes.ToString(Inv), Days.ToString(Inv),
        SumTemp.ToString("0.##", Inv), SumIdle.ToString("0.##", Inv), FanN.ToString(Inv), SumFan.ToString("0.##", Inv), WattsN.ToString(Inv), SumWatts.ToString("0.##", Inv));

    public static DustPeriod Unpack(string text)
    {
        var p = new DustPeriod();
        var a = (text ?? "").Split(';');
        if (a.Length != 10) return p;
        try
        {
            p.From = DateTime.ParseExact(a[0], "yyyy-MM-dd", Inv); p.To = DateTime.ParseExact(a[1], "yyyy-MM-dd", Inv);
            p.Minutes = int.Parse(a[2], Inv); p.Days = int.Parse(a[3], Inv);
            p.SumTemp = double.Parse(a[4], Inv); p.SumIdle = double.Parse(a[5], Inv);
            p.FanN = int.Parse(a[6], Inv); p.SumFan = double.Parse(a[7], Inv);
            p.WattsN = int.Parse(a[8], Inv); p.SumWatts = double.Parse(a[9], Inv);
        }
        catch { return new DustPeriod(); }
        return p;
    }
}

/// <summary>Что видно в подразделе «Пыль».</summary>
public sealed class DustView
{
    /// <summary>Опорный период: готовый (BaseReady) или ещё копящийся.</summary>
    public DustPeriod Base = new();
    /// <summary>Последние 7 дней после опорного периода.</summary>
    public DustPeriod Now = new();
    public bool BaseReady;
    public DateTime Since;
    /// <summary>Какой по счёту день идёт с начала опорного периода.</summary>
    public int BaseDay;
    /// <summary>Скорость вентиляторов может меняться сама (не постоянная скорость) и читается в обоих периодах.</summary>
    public bool FansFree;
    public double TempRise = double.NaN, FanRise = double.NaN;
    /// <summary>Нагрузка стала тяжелее (потребление выросло больше чем на 15 %): сравнение нечестное.</summary>
    public bool Heavier;
    public bool Dusty;
    public string Text = "";
}

/// <summary>Что видно в разделе «Вентиляторы».</summary>
public sealed class FanView
{
    /// <summary>Есть показания оборотов или скорости.</summary>
    public bool HasReading;
    /// <summary>Скоростью можно управлять (видеокарта NVIDIA, драйвер отдаёт нужные функции).</summary>
    public bool CanControl;
    /// <summary>Почему управлять нельзя (понятная фраза).</summary>
    public string Why = "";
    public string Vendor = "";
    public int Coolers;
    public double Rpm = double.NaN, Level = double.NaN, Temp = double.NaN;
    public int Min = 30, Max = 100;
    /// <summary>Скоростью сейчас управляет программа (а не драйвер).</summary>
    public bool Held;
    /// <summary>Почему программа держит или отпустила вентиляторы сейчас.</summary>
    public string Reason = "";
    public FanControl.Mode Mode;
    public FanPoint[] Curve = Array.Empty<FanPoint>();
    public int Fixed;
    public bool GamesOnly, StopCold;
    public string LastAction = "";
    public DateTime LastActionAt;
    public bool Testing;
    /// <summary>Первый замер уже был (до него не ясно, можно ли управлять).</summary>
    public bool Ready;
}

/// <summary>Сводка статистики за период: в играх и вне игр.</summary>
public sealed class FanSummary
{
    public int Minutes;
    public double AvgFan = double.NaN, MaxFan = double.NaN, AvgRpm = double.NaN, MaxRpm = double.NaN, MaxTemp = double.NaN, StopPct = double.NaN;
}

/// <summary>
/// Вентиляторы видеокарты: показания, статистика и управление скоростью.
///
/// Управление — только для NVIDIA, через функции драйвера ClientFanCoolers (nvapi64.dll), без сторонних драйверов.
/// Режимы: «как решает драйвер» (по умолчанию), «по кривой» и «постоянная скорость». Защита:
///   • от 84 °C — всегда 100 %, что бы ни было задано;
///   • температура не читается дольше 6 секунд — вентиляторы возвращаются драйверу;
///   • выход из программы (в том числе завершение Windows) — вентиляторы возвращаются драйверу;
///   • программа упала, пока держала вентиляторы, — при следующем запуске они сразу возвращаются драйверу.
/// Вентиляторы корпуса и процессора подключены к материнской плате: без стороннего драйвера Windows их не показывает.
/// </summary>
public static class FanControl
{
    public enum Mode { Driver, Curve, Fixed }

    public const int SafeTemp = 84;
    private const int KeepMinutes = 7 * 24 * 60;
    private const string KMode = "fan.mode", KCurve = "fan.curve", KFixed = "fan.fixed", KGames = "fan.games", KStop = "fan.stop", KHeld = "fan.held";

    public static readonly FanPoint[] Quiet = { new(50, 30), new(60, 38), new(70, 52), new(78, 70), new(83, 100) };
    public static readonly FanPoint[] Balanced = { new(45, 30), new(55, 40), new(65, 55), new(74, 75), new(82, 100) };
    public static readonly FanPoint[] Cool = { new(40, 35), new(50, 48), new(60, 62), new(70, 80), new(78, 100) };

    /// <summary>Состояние изменилось (новый замер, режим, действие). Вызывается из фонового потока.</summary>
    public static event Action Changed;

    private static PanelHost host;
    private static Timer loop;
    private static readonly object Gate = new();
    private static int busy;
    private static bool inited;

    // Последний замер и решение
    private static double rpm = double.NaN, level = double.NaN, temp = double.NaN, smooth = double.NaN;
    private static int coolers, minLevel = 30, maxLevel = 100;
    private static bool readOnce;
    private static DateTime tempAt;
    private static int applied = -1;                 // -1 — вентиляторы у драйвера, иначе заданный процент
    private static string reason = "", lastAction = "";
    private static DateTime lastActionAt;
    private static DateTime testUntil;

    // Статистика
    private static readonly List<FanMinute> minutes = new();
    private static DateTime minute = DateTime.MinValue, lastSave = DateTime.Now;
    private static double tSum, fSum, rSum, tMax;
    private static int fN, rN, stopN, allN;
    private static double lSum, wSum, cSum;          // загрузка и потребление видеокарты, загрузка процессора (с 1.4.0)
    private static int lN, wN, cN;
    private static bool gameAny;
    private static bool loaded;

    private static string StatsPath => Path.Combine(SessionStore.Folder, "fans.json");
    private static readonly JsonSerializerOptions Json = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    // ================================================================== запуск и выход
    public static void Init(PanelHost h)
    {
        lock (Gate)
        {
            host = h;
            if (inited) return;
            inited = true;
        }
        LoadStats();
        // Программа завершилась аварийно, пока держала вентиляторы: вернуть их драйверу сразу. Отметка снимается только
        // после того, как драйвер принял возврат; если драйвер NVIDIA ещё не готов, возврат повторит такт (ReleaseStale).
        if (h.Settings.GetBool(KHeld) && Nv.Ready(out _))
            lock (Gate) ReleaseStale();
        try { AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown(); } catch { }
        GameHooks.Started += (_, _) => Kick();
        GameHooks.Ended += Kick;
        loop = new Timer(_ => Tick(), null, 1000, 2000);
    }

    private static volatile bool shuttingDown;

    /// <summary>
    /// Выход из программы: вентиляторы — драйверу, статистика — в файл. Можно вызывать из любого потока и сколько угодно раз
    /// (обычный выход, завершение сеанса Windows, необработанная ошибка): после первого вызова такт больше не берёт вентиляторы.
    /// Замок ждётся не дольше 3 секунд — если его держит зависший поток, вентиляторы отпускаются напрямую.
    /// </summary>
    public static void Shutdown()
    {
        shuttingDown = true;
        try { loop?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        bool locked = false;
        try
        {
            Monitor.TryEnter(Gate, 3000, ref locked);
            if (locked)
            {
                if (applied >= 0) Release(L.T("Программа закрыта — вентиляторы у драйвера."));
            }
            else if (Volatile.Read(ref applied) >= 0)
            {
                // Замок занят (сбой в другом потоке): отпускаем без него. Отметку «держит» не трогаем —
                // если возврат не прошёл, следующий запуск вернёт вентиляторы драйверу.
                try { if (Nv.SetLevel(0, manual: false) == 0) Volatile.Write(ref applied, -1); } catch { }
            }
        }
        catch { }
        finally { if (locked) Monitor.Exit(Gate); }
        SaveStats(3000);
    }

    /// <summary>
    /// Продолжить работу после Shutdown, если процесс на самом деле не завершился (выход из Windows отменили):
    /// такт снова запускается и через пару секунд возвращает выбранный режим (кривая, постоянная скорость).
    /// </summary>
    public static void Resume()
    {
        lock (Gate) { if (!inited || !shuttingDown) return; }
        shuttingDown = false;
        try { loop?.Change(1000, 2000); } catch { }
        Log.Info("вентиляторы: работа продолжена после отменённого выхода из Windows");
        Kick();
    }

    private static void Kick()
    {
        if (!shuttingDown) ThreadPool.QueueUserWorkItem(_ => Tick());
    }

    // ================================================================== настройки
    public static Mode CurrentMode(AppSettings s) => s.Get(KMode) switch { "curve" => Mode.Curve, "fixed" => Mode.Fixed, _ => Mode.Driver };

    public static FanPoint[] CurrentCurve(AppSettings s) => ParseCurve(s.Get(KCurve)) ?? Balanced;

    public static int CurrentFixed(AppSettings s) => int.TryParse(s.Get(KFixed), out int v) ? Math.Clamp(v, 30, 100) : 50;

    /// <summary>Сохранить режим и сразу применить. Только с правами администратора.</summary>
    public static void Configure(Mode mode, FanPoint[] curve, int fixedLevel, bool gamesOnly, bool stopCold)
    {
        var h = host;
        if (h == null || !h.Admin) return;
        h.Settings.Set(KMode, mode switch { Mode.Curve => "curve", Mode.Fixed => "fixed", _ => "driver" });
        h.Settings.Set(KCurve, FormatCurve(Normalize(curve)));
        h.Settings.Set(KFixed, Math.Clamp(fixedLevel, 30, 100).ToString());
        h.Settings.SetBool(KGames, gamesOnly);
        h.Settings.SetBool(KStop, stopCold);
        h.Settings.Save();
        Kick();
    }

    /// <summary>Проверка: 100 % на шесть секунд, затем снова по выбранному режиму. Видно и слышно, что управление работает.</summary>
    public static void Test()
    {
        if (host == null || !host.Admin) return;
        testUntil = DateTime.Now.AddSeconds(6);
        Kick();
    }

    // ================================================================== такт
    private static void Tick()
    {
        if (shuttingDown || Interlocked.Exchange(ref busy, 1) == 1) return;
        try
        {
            var h = host;
            if (h == null) return;
            var live = h.Live();
            bool nv = Nv.Ready(out _);

            lock (Gate)
            {
                if (shuttingDown) return;
                // Отметка «держит» осталась с прошлого запуска, а драйвер тогда не был готов: отпускаем сейчас.
                if (nv && h.Admin && applied < 0 && h.Settings.GetBool(KHeld)) ReleaseStale();
                if (nv && Nv.Status(out var st))
                {
                    coolers = st.Count; rpm = st.Rpm; level = st.Level; minLevel = st.Min; maxLevel = st.Max;
                }
                else
                {
                    coolers = 0;
                    rpm = live.GpuFanRpm;
                    level = live.GpuFanPct;
                }
                double t = nv ? Nv.Temperature() : double.NaN;
                if (double.IsNaN(t)) t = live.GpuTemp;
                if (!double.IsNaN(t)) { temp = t; tempAt = DateTime.Now; smooth = Smooth(smooth, t); }
                else if ((DateTime.Now - tempAt).TotalSeconds > 6) { temp = double.NaN; smooth = double.NaN; }

                readOnce = true;
                Record(live.Recording || GameHooks.InGame, live);

                if (nv && coolers > 0 && h.Admin)
                {
                    var s = h.Settings;
                    bool testing = DateTime.Now < testUntil;
                    int target = testing ? maxLevel
                        : Decide(CurrentMode(s), GameHooks.InGame || live.Recording, s.GetBool(KGames), s.GetBool(KStop, true),
                                 CurrentCurve(s), CurrentFixed(s), smooth, minLevel, maxLevel, applied >= 0, out reason);
                    if (testing) reason = L.T("Проверка: полная скорость на несколько секунд.");
                    Apply(target);
                }
                else
                {
                    reason = "";
                    // Программа держит вентиляторы, а состояние их каналов больше не читается (сбой или перезапуск драйвера):
                    // защита выше (84 °C, потеря температуры) здесь не работает — вентиляторы отдаются драйверу.
                    if (applied >= 0) Release(L.T("Вентиляторы снова у драйвера."));
                }
            }
            // Пыль: сравнение с опорным периодом — раз в полчаса (только чтение статистики и запись в настройки программы).
            if ((DateTime.Now - dustChecked).TotalMinutes >= 30) { dustChecked = DateTime.Now; Dust(notify: true); }
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref busy, 0);
            try { Changed?.Invoke(); } catch { }
        }
    }

    /// <summary>
    /// Сглаживание: рост температуры учитывается сразу, спад — не быстрее градуса за такт.
    /// Так вентиляторы не дёргаются вверх-вниз от каждого скачка нагрузки.
    /// </summary>
    public static double Smooth(double prev, double raw) =>
        double.IsNaN(prev) || raw >= prev ? raw : Math.Max(raw, prev - 1.0);

    /// <summary>
    /// Какую скорость задать: -1 — отдать вентиляторы драйверу. holding — программа уже держит вентиляторы
    /// (для запаса в 3 °C при отпускании, иначе на границе вентиляторы то стоят, то крутятся).
    /// </summary>
    public static int Decide(Mode mode, bool inGame, bool gamesOnly, bool stopCold, FanPoint[] curve, int fixedLevel,
                             double t, int min, int max, bool holding, out string why)
    {
        if (mode == Mode.Driver) { why = L.T("Скоростью управляет драйвер видеокарты."); return -1; }
        if (double.IsNaN(t)) { why = L.T("Температура видеокарты не читается — скоростью управляет драйвер."); return -1; }
        if (t >= SafeTemp) { why = L.F("Видеокарта нагрелась до {0:0} °C — вентиляторы на полной скорости.", t); return max; }
        if (gamesOnly && !inGame) { why = L.T("Игра не запущена — скоростью управляет драйвер."); return -1; }

        if (mode == Mode.Fixed)
        {
            int f = Math.Clamp(fixedLevel, min, max);
            why = L.F("Постоянная скорость {0} %.", f);
            return f;
        }

        var c = Normalize(curve);
        if (stopCold && t < c[0].T - (holding ? 3 : 0))
        {
            why = L.F("Видеокарта холодная ({0:0} °C) — вентиляторы может остановить драйвер.", t);
            return -1;
        }
        int l = Math.Clamp((int)Math.Round(Interpolate(c, t)), min, max);
        why = L.F("По кривой: {0:0} °C → {1} %.", t, l);
        return l;
    }

    /// <summary>Скорость по кривой: между точками — по прямой, ниже первой — как у первой, выше последней — как у последней.</summary>
    public static double Interpolate(FanPoint[] c, double t)
    {
        if (c.Length == 0) return 100;
        if (t <= c[0].T) return c[0].L;
        for (int i = 1; i < c.Length; i++)
        {
            if (t <= c[i].T)
            {
                var a = c[i - 1]; var b = c[i];
                return b.T == a.T ? b.L : a.L + (b.L - a.L) * (t - a.T) / (b.T - a.T);
            }
        }
        return c[^1].L;
    }

    /// <summary>
    /// Привести кривую в порядок: точки по возрастанию температуры, скорость не убывает, температура 25–95 °C, скорость 30–100 %,
    /// последняя точка — не выше 85 °C и 100 %: выше этой температуры вентиляторы обязаны крутиться в полную силу.
    /// </summary>
    public static FanPoint[] Normalize(FanPoint[] curve)
    {
        var pts = (curve ?? Array.Empty<FanPoint>()).Select(p => new FanPoint(Math.Clamp(p.T, 25, 95), Math.Clamp(p.L, 30, 100)))
                                                    .OrderBy(p => p.T).ToList();
        if (pts.Count < 2) return Balanced;
        var outp = new List<FanPoint>();
        int lastL = 0, lastT = int.MinValue;
        foreach (var p in pts)
        {
            int t = Math.Max(p.T, lastT + 1);
            int l = Math.Max(p.L, lastL);
            outp.Add(new FanPoint(t, l));
            lastT = t; lastL = l;
        }
        var end = outp[^1];
        outp[^1] = new FanPoint(Math.Min(end.T, 85), 100);
        // Если сдвиг последней точки сломал порядок — подтянуть предыдущие.
        for (int i = outp.Count - 2; i >= 0; i--)
            if (outp[i].T >= outp[i + 1].T) outp[i] = new FanPoint(outp[i + 1].T - 1, Math.Min(outp[i].L, outp[i + 1].L));
        return outp.ToArray();
    }

    public static string FormatCurve(FanPoint[] c) => string.Join(",", c.Select(p => $"{p.T}:{p.L}"));

    public static FanPoint[] ParseCurve(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var list = new List<FanPoint>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':');
            if (kv.Length == 2 && int.TryParse(kv[0], out int t) && int.TryParse(kv[1], out int l)) list.Add(new FanPoint(t, l));
        }
        return list.Count >= 2 ? Normalize(list.ToArray()) : null;
    }

    private static void Apply(int target)
    {
        if (shuttingDown && target >= 0) return;     // при выходе вентиляторы только отпускаются
        if (target < 0)
        {
            if (applied >= 0) Release(L.T("Вентиляторы снова у драйвера."));
            return;
        }
        // Мелкие изменения не отправляются: шаг в 2 % на слух не отличить, а драйвер лишний раз не дёргается.
        if (applied >= 0 && Math.Abs(target - applied) < 2 && !(target == maxLevel && applied != maxLevel)) return;
        bool first = applied < 0;
        // Отметка «держит» — до того, как вентиляторы перейдут в ручной режим: если программа упадёт сразу после,
        // следующий запуск вернёт их драйверу.
        bool marked = false;
        if (first && host != null && !host.Settings.GetBool(KHeld)) { host.Settings.SetBool(KHeld, true); host.Settings.Save(); marked = true; }
        int st = Nv.SetLevel(target, manual: true);
        if (st == 0)
        {
            applied = target;
            if (first) Note(L.F("Программа взяла вентиляторы: {0} %.", target));
        }
        else
        {
            if (marked) { host.Settings.SetBool(KHeld, false); host.Settings.Save(); }
            Note(L.F("Драйвер не принял скорость вентиляторов: {0}.", Nv.Error(st)));
        }
    }

    /// <summary>
    /// Отдать вентиляторы драйверу. Отметка «держит» (KHeld) снимается только после того, как драйвер принял возврат:
    /// иначе ручной режим остался бы, а программа о нём забыла. Вызывать под Gate.
    /// </summary>
    private static bool Release(string note)
    {
        int st = Nv.SetLevel(0, manual: false);
        bool stale = applied < 0 && host?.Settings.GetBool(KHeld) == true;
        if (st == 0 || (applied < 0 && !stale))
        {
            applied = -1;
            if (host != null && host.Settings.GetBool(KHeld)) { host.Settings.SetBool(KHeld, false); host.Settings.Save(); }
            Note(note);
            return true;
        }
        Note(L.F("Драйвер не принял возврат вентиляторов: {0}. Программа попробует ещё раз; перезагрузка тоже вернёт их драйверу.", Nv.Error(st)));
        return false;
    }

    private static DateTime staleTried = DateTime.MinValue;

    /// <summary>Вернуть драйверу вентиляторы, которые держал прошлый запуск (не чаще раза в минуту). Под Gate.</summary>
    private static void ReleaseStale()
    {
        if ((DateTime.Now - staleTried).TotalSeconds < 60) return;
        staleTried = DateTime.Now;
        Release(L.T("Вентиляторы вернулись драйверу: в прошлый раз программа закрылась, не успев их отпустить."));
    }

    private static void Note(string text) { lastAction = text; lastActionAt = DateTime.Now; }

    /// <summary>Снимок для раздела. Можно вызывать из любого потока.</summary>
    public static FanView Snapshot()
    {
        var h = host;
        bool nv = Nv.Ready(out string why);
        lock (Gate)
        {
            var s = h?.Settings ?? new AppSettings();
            string vendor = GpuVendors.MainVendor;
            var v = new FanView
            {
                Vendor = vendor,
                Coolers = coolers,
                Rpm = rpm, Level = level, Temp = temp,
                Min = minLevel, Max = maxLevel,
                HasReading = !double.IsNaN(rpm) || !double.IsNaN(level),
                CanControl = nv && coolers > 0,
                Held = applied >= 0,
                Reason = reason,
                Mode = CurrentMode(s), Curve = CurrentCurve(s), Fixed = CurrentFixed(s),
                GamesOnly = s.GetBool(KGames), StopCold = s.GetBool(KStop, true),
                LastAction = lastAction, LastActionAt = lastActionAt,
                Testing = DateTime.Now < testUntil,
                Ready = readOnce,
            };
            v.Why = v.CanControl ? ""
                : vendor == "AMD" ? L.T("У видеокарт AMD скоростью вентиляторов управляет AMD Software: «Производительность» → «Настройка» → «Вентиляторы».")
                : vendor == "Intel" ? L.T("У видеокарт Intel скорость вентиляторов задаёт Intel Graphics Software или программа производителя карты.")
                : nv ? L.T("Драйвер NVIDIA не отдаёт управление вентиляторами этой видеокарты. Так бывает у ноутбуков: там вентиляторами управляет сам ноутбук.")
                : why;
            return v;
        }
    }

    // ================================================================== статистика
    private static void Record(bool game, LiveData live)
    {
        if (!loaded) return;
        var now = DateTime.Now;
        var thisMinute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
        if (thisMinute != minute) { Flush(); minute = thisMinute; }
        if (!double.IsNaN(temp)) { tSum += temp; tMax = Math.Max(tMax, temp); }
        if (!double.IsNaN(level)) { fSum += level; fN++; }
        if (!double.IsNaN(rpm)) { rSum += rpm; rN++; if (rpm < 1) stopN++; }
        if (live != null)
        {
            if (live.GpuLoad is >= 0 and <= 100) { lSum += live.GpuLoad; lN++; }
            if (live.GpuPowerW is > 0 and < 2000) { wSum += live.GpuPowerW; wN++; }
            if (live.CpuLoad is >= 0 and <= 100) { cSum += live.CpuLoad; cN++; }
        }
        allN++;
        gameAny |= game;
        if ((now - lastSave).TotalMinutes >= 5) SaveStats();
    }

    private static void Flush()
    {
        if (minute != DateTime.MinValue && (fN > 0 || rN > 0))
        {
            minutes.Add(new FanMinute
            {
                T = minute,
                Temp = tMax > 0 ? (float)tMax : float.NaN,
                Fan = fN > 0 ? (float)(fSum / fN) : float.NaN,
                Rpm = rN > 0 ? (float)(rSum / rN) : float.NaN,
                Stop = rN > 0 ? (float)stopN / rN : float.NaN,
                Game = gameAny,
                Load = lN > 0 ? (float)(lSum / lN) : float.NaN,
                Watts = wN > 0 ? (float)(wSum / wN) : float.NaN,
                Cpu = cN > 0 ? (float)(cSum / cN) : float.NaN,
            });
            DateTime cutoff = DateTime.Now.AddMinutes(-KeepMinutes);
            if (minutes.Count > 0 && minutes[0].T <= cutoff) minutes.RemoveAll(p => p.T <= cutoff);
        }
        tSum = fSum = rSum = tMax = 0; fN = rN = stopN = allN = 0; gameAny = false;
        lSum = wSum = cSum = 0; lN = wN = cN = 0;
    }

    private static void LoadStats()
    {
        lock (Gate)
        {
            if (loaded) return;         // уже прочитано (например, разделом «Электричество» до запуска службы)
            try
            {
                if (File.Exists(StatsPath))
                {
                    var list = JsonSerializer.Deserialize<List<FanMinute>>(File.ReadAllText(StatsPath), Json);
                    DateTime cutoff = DateTime.Now.AddMinutes(-KeepMinutes);
                    if (list != null) minutes.AddRange(list.Where(p => p.T > cutoff).OrderBy(p => p.T));
                }
            }
            catch { }
            loaded = true;
        }
    }

    private static void SaveStats(int lockTimeoutMs = Timeout.Infinite)
    {
        List<FanMinute> copy;
        bool locked = false;
        try
        {
            Monitor.TryEnter(Gate, lockTimeoutMs, ref locked);
            if (!locked || !loaded) return;
            copy = minutes.ToList(); lastSave = DateTime.Now;
        }
        finally { if (locked) Monitor.Exit(Gate); }
        // Запись через временный файл и под замком файла: Shutdown может прийти из двух потоков сразу.
        try { AtomicFile.WriteAllText(StatsPath, JsonSerializer.Serialize(copy, Json)); } catch { }
    }

    /// <summary>Температура и скорость за последние N минут на равномерной сетке (не больше ~360 точек). Пропуски — NaN.</summary>
    public static (double[] Temp, double[] Fan, DateTime From) Window(int span)
    {
        int step = Math.Max(1, (int)Math.Ceiling(span / 360.0));
        int n = span / step;
        var t = Enumerable.Repeat(double.NaN, n).ToArray();
        var f = Enumerable.Repeat(double.NaN, n).ToArray();
        var cnt = new int[n];
        DateTime now = DateTime.Now;
        DateTime from = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0).AddMinutes(-span + 1);
        List<FanMinute> copy;
        lock (Gate) copy = minutes.ToList();
        foreach (var p in copy)
        {
            int i = (int)((p.T - from).TotalMinutes) / step;
            if (i < 0 || i >= n) continue;
            if (!float.IsNaN(p.Temp) && !(t[i] >= p.Temp)) t[i] = p.Temp;
            if (!float.IsNaN(p.Fan)) f[i] = double.IsNaN(f[i]) ? p.Fan : (f[i] * cnt[i] + p.Fan) / (cnt[i] + 1);
            cnt[i]++;
        }
        return (t, f, from);
    }

    /// <summary>Сводка за последние N минут: отдельно игры (game=true) и всё остальное (false).</summary>
    public static FanSummary Summary(int span, bool game)
    {
        DateTime cutoff = DateTime.Now.AddMinutes(-span);
        List<FanMinute> pick;
        lock (Gate) pick = minutes.Where(p => p.T > cutoff && p.Game == game).ToList();
        return Summarize(pick);
    }

    public static FanSummary Summarize(IReadOnlyList<FanMinute> pick)
    {
        var s = new FanSummary { Minutes = pick.Count };
        var fan = pick.Where(p => !float.IsNaN(p.Fan)).Select(p => (double)p.Fan).ToList();
        var r = pick.Where(p => !float.IsNaN(p.Rpm)).Select(p => (double)p.Rpm).ToList();
        var t = pick.Where(p => !float.IsNaN(p.Temp)).Select(p => (double)p.Temp).ToList();
        var stop = pick.Where(p => !float.IsNaN(p.Stop)).Select(p => (double)p.Stop).ToList();
        if (fan.Count > 0) { s.AvgFan = fan.Average(); s.MaxFan = fan.Max(); }
        if (r.Count > 0) { s.AvgRpm = r.Average(); s.MaxRpm = r.Max(); }
        if (t.Count > 0) s.MaxTemp = t.Max();
        if (stop.Count > 0) s.StopPct = 100 * stop.Average();
        return s;
    }

    /// <summary>Сколько минут статистики накоплено.</summary>
    public static int StatsMinutes { get { lock (Gate) return minutes.Count; } }

    /// <summary>
    /// Копия поминутной статистики за 7 дней (по возрастанию времени). Если служба ещё не запущена — читает fans.json.
    /// Только чтение: вентиляторы не трогаются.
    /// </summary>
    public static List<FanMinute> History()
    {
        LoadStats();
        lock (Gate) return minutes.ToList();
    }

    // ================================================================== пыль
    public const int DustLoad = 85, DustIdleLoad = 20, DustMinMinutes = 120, DustDays = 7, DustTempRise = 5, DustFanRise = 10, DustNoteDays = 14;
    private const string KDustSig = "fan.dust.sig", KDustSince = "fan.dust.since", KDustUpto = "fan.dust.upto", KDustAcc = "fan.dust.acc",
                         KDustDone = "fan.dust.done", KDustNote = "fan.dust.note";
    private static readonly object DustGate = new();
    private static DateTime dustChecked = DateTime.MinValue;

    /// <summary>Подпись режима вентиляторов: при её смене опорный период начинается заново (другая кривая — другие температуры).</summary>
    public static string DustSignature(AppSettings s) => CurrentMode(s) switch
    {
        Mode.Curve => "curve:" + FormatCurve(CurrentCurve(s)),
        Mode.Fixed => "fixed:" + CurrentFixed(s),
        _ => "driver",
    };

    /// <summary>
    /// Один день: минуты игры при загрузке видеокарты от 85 % и простой (без игры, загрузка до 20 %) в тот же день.
    /// Без простоя (меньше 5 минут) день не годится: не с чем сравнить жару в комнате.
    /// </summary>
    public static DustPeriod DustDay(IEnumerable<FanMinute> day)
    {
        var p = new DustPeriod();
        var list = day.ToList();
        var game = list.Where(m => m.Game && m.Load >= DustLoad && !float.IsNaN(m.Temp)).ToList();
        var idle = list.Where(m => !m.Game && m.Load <= DustIdleLoad && !float.IsNaN(m.Temp)).Select(m => (double)m.Temp).OrderBy(x => x).ToList();
        if (game.Count == 0 || idle.Count < 5) return p;
        double idleT = idle[idle.Count / 2];        // середина: короткие всплески в простое не сдвигают
        p.From = p.To = game[0].T.Date;
        p.Days = 1;
        p.Minutes = game.Count;
        p.SumTemp = game.Sum(m => (double)m.Temp);
        p.SumIdle = idleT * game.Count;
        foreach (var m in game)
        {
            if (!float.IsNaN(m.Fan)) { p.SumFan += m.Fan; p.FanN++; }
            if (!float.IsNaN(m.Watts)) { p.SumWatts += m.Watts; p.WattsN++; }
        }
        return p;
    }

    /// <summary>Сумма по дням в промежутке [from, to).</summary>
    public static DustPeriod DustRange(IEnumerable<FanMinute> mins, DateTime from, DateTime to)
    {
        var p = new DustPeriod();
        foreach (var g in mins.Where(m => m.T >= from && m.T < to).GroupBy(m => m.T.Date).OrderBy(g => g.Key)) p.Add(DustDay(g));
        return p;
    }

    /// <summary>
    /// Шаг сравнения на подставляемых данных: копит опорный период по целым дням (статистика живёт 7 дней, поэтому суммы — в настройках),
    /// фиксирует его, когда прошло 7 дней и набралось 120 минут игры под нагрузкой, и сравнивает с последними 7 днями.
    /// Пишет только в переданные настройки (без Save). Возвращает, менялись ли настройки.
    /// </summary>
    public static bool DustStep(AppSettings s, IReadOnlyList<FanMinute> mins, DateTime now, string sig, bool fixedMode, out DustView v)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        bool changed = false;
        string stored = s.Get(KDustSig);
        if (stored != sig)
        {
            // Первый запуск — опора с первой минуты, где уже записана загрузка видеокарты (в статистике версии 1.3 её нет);
            // смена режима или «Почистил» — с этой минуты.
            DateTime start = stored.Length == 0 && mins.FirstOrDefault(m => !float.IsNaN(m.Load)) is { } first ? first.T : now;
            s.Set(KDustSig, sig);
            s.Set(KDustSince, start.ToString("o", inv));
            s.Set(KDustUpto, start.Date.AddDays(-1).ToString("yyyy-MM-dd", inv));
            s.Set(KDustAcc, null);
            s.Set(KDustDone, null);
            changed = true;
        }
        DateTime since = DateTime.TryParse(s.Get(KDustSince), inv, System.Globalization.DateTimeStyles.RoundtripKind, out var a) ? a : now;
        DateTime upto = DateTime.TryParseExact(s.Get(KDustUpto), "yyyy-MM-dd", inv, System.Globalization.DateTimeStyles.None, out var u) ? u : since.Date.AddDays(-1);
        var acc = DustPeriod.Unpack(s.Get(KDustAcc));
        bool done = s.GetBool(KDustDone);

        if (!done)
        {
            for (var d = upto.AddDays(1); d < now.Date; d = d.AddDays(1))
            {
                acc.Add(DustRange(mins, d < since ? since : d, d.AddDays(1)));
                upto = d;
                changed = true;
            }
            if (acc.Enough && (now.Date - since.Date).TotalDays >= DustDays) done = true;
            if (changed)
            {
                s.Set(KDustAcc, acc.Pack().Length > 0 ? acc.Pack() : null);
                s.Set(KDustUpto, upto.ToString("yyyy-MM-dd", inv));
                if (done) s.SetBool(KDustDone, true);
            }
        }

        v = new DustView { Base = acc, BaseReady = done, Since = since, BaseDay = (int)(now.Date - since.Date).TotalDays + 1 };
        if (done)
        {
            DateTime from = now.AddDays(-DustDays);
            if (from < upto.AddDays(1)) from = upto.AddDays(1);
            v.Now = DustRange(mins, from, now.AddMinutes(1));
            if (v.Now.Enough) Judge(v, fixedMode);
        }
        v.Text = DustText(v);
        return changed;
    }

    /// <summary>Вывод: температура (за вычетом простоя) выросла на 5 °C и вентиляторы — на 10 п. п. (если их скорость может меняться).</summary>
    public static void Judge(DustView v, bool fixedMode)
    {
        var b = v.Base; var c = v.Now;
        v.FansFree = !fixedMode && b.FanN > 0 && c.FanN > 0;
        v.TempRise = c.Delta - b.Delta;
        v.FanRise = v.FansFree ? c.Fan - b.Fan : double.NaN;
        v.Heavier = b.WattsN > 0 && c.WattsN > 0 && c.Watts > b.Watts * 1.15;
        v.Dusty = !v.Heavier && v.TempRise >= DustTempRise && (!v.FansFree || v.FanRise >= DustFanRise);
    }

    private static string DustText(DustView v)
    {
        if (!v.BaseReady)
        {
            int m = Math.Min(v.Base.Minutes, DustMinMinutes);
            return v.BaseDay <= DustDays
                ? L.F("Программа запоминает, как охлаждается видеокарта сейчас: день {0} из {1}, набрано {2} из {3} минут игры под полной нагрузкой. Потом будет с чем сравнивать.", v.BaseDay, DustDays, m, DustMinMinutes)
                : L.F("Программа запоминает, как охлаждается видеокарта сейчас: набрано {0} из {1} минут игры под полной нагрузкой. Как только наберётся, будет с чем сравнивать.", m, DustMinMinutes);
        }
        if (!v.Now.Enough)
            return L.F("Как охлаждалась видеокарта раньше, программа запомнила. Для сравнения нужно ещё {0} минут игры под полной нагрузкой за последние {1} дней.", DustMinMinutes - v.Now.Minutes, DustDays);
        if (v.Dusty && LaptopCheck.Applies)   // ноутбук сам не разобрать — совет про сервис, без «корпуса»
            return v.FansFree
                ? L.F("Похоже, пора почистить ноутбук от пыли: при той же нагрузке видеокарта в играх стала горячее на {0:0} °C, а вентиляторы крутятся быстрее на {1:0} % от полной скорости. Значит, радиатор хуже отдаёт тепло. Ноутбук сложно разобрать самому — чистку лучше доверить сервису. Потом нажмите «Почистил».", v.TempRise, v.FanRise)
                : L.F("Похоже, пора почистить ноутбук от пыли: при той же нагрузке видеокарта в играх стала горячее на {0:0} °C. Значит, радиатор хуже отдаёт тепло. Ноутбук сложно разобрать самому — чистку лучше доверить сервису. Потом нажмите «Почистил».", v.TempRise);
        if (v.Dusty)
            return v.FansFree
                ? L.F("Похоже, пора почистить компьютер от пыли: при той же нагрузке видеокарта в играх стала горячее на {0:0} °C, а вентиляторы крутятся быстрее на {1:0} % от полной скорости. Значит, радиатор хуже отдаёт тепло. Выключите компьютер, продуйте или почистите кисточкой радиатор и вентиляторы видеокарты и корпуса, потом нажмите «Почистил».", v.TempRise, v.FanRise)
                : L.F("Похоже, пора почистить компьютер от пыли: при той же нагрузке видеокарта в играх стала горячее на {0:0} °C. Значит, радиатор хуже отдаёт тепло. Выключите компьютер, продуйте или почистите кисточкой радиатор и вентиляторы видеокарты и корпуса, потом нажмите «Почистил».", v.TempRise);
        if (v.Heavier && v.TempRise >= DustTempRise)
            return L.F("Видеокарта в играх стала горячее на {0:0} °C, но и работает тяжелее: потребление выросло с {1:0} до {2:0} Вт. Это скорее игры, чем пыль.", v.TempRise, v.Base.Watts, v.Now.Watts);
        return Math.Abs(v.TempRise) < 0.5 ? L.T("Охлаждение работает как раньше: температура в играх за вычетом простоя та же, что в опорный период.")
            : v.TempRise > 0 ? L.F("Охлаждение работает как раньше: температура в играх за вычетом простоя выше на {0:0} °C, чем в опорный период.", v.TempRise)
            : L.F("Охлаждение работает как раньше: температура в играх за вычетом простоя ниже на {0:0} °C, чем в опорный период.", -v.TempRise);
    }

    /// <summary>
    /// Сравнение для раздела и для уведомления. notify — показать уведомление у часов (не чаще раза в 14 дней).
    /// Читает статистику, пишет только настройки программы.
    /// </summary>
    public static DustView Dust(bool notify)
    {
        var h = host;
        var s = h?.Settings;
        if (s == null) return new DustView { Text = "" };
        var mins = History();
        DustView v;
        bool changed;
        lock (DustGate) changed = DustStep(s, mins, DateTime.Now, DustSignature(s), CurrentMode(s) == Mode.Fixed, out v);
        if (notify && v.Dusty)
        {
            var last = DateTime.TryParse(s.Get(KDustNote), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var n) ? n : DateTime.MinValue;
            if ((DateTime.Now - last).TotalDays >= DustNoteDays)
            {
                s.Set(KDustNote, DateTime.Now.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
                changed = true;
                string body = v.FansFree
                    ? L.F("При той же нагрузке видеокарта в играх стала горячее на {0:0} °C, а вентиляторы крутятся быстрее на {1:0} % от полной скорости. Подробности — в разделе «Вентиляторы».", v.TempRise, v.FanRise)
                    : L.F("При той же нагрузке видеокарта в играх стала горячее на {0:0} °C. Подробности — в разделе «Вентиляторы».", v.TempRise);
                h.Ui(() => h.Notify(LaptopCheck.Applies ? L.T("Похоже, пора почистить ноутбук от пыли") : L.T("Похоже, пора почистить компьютер от пыли"), body,
                    true, () => h.Open("sys:Вентиляторы")));
            }
        }
        if (changed) s.Save();
        return v;
    }

    /// <summary>«Почистил»: новый опорный период с этой минуты.</summary>
    public static void DustReset()
    {
        var h = host;
        if (h == null || !h.Admin) return;
        var s = h.Settings;
        lock (DustGate)
        {
            s.Set(KDustSig, DustSignature(s));
            s.Set(KDustSince, DateTime.Now.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            s.Set(KDustUpto, DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            s.Set(KDustAcc, null);
            s.Set(KDustDone, null);
            s.Set(KDustNote, null);
        }
        s.Save();
    }

    // ================================================================== NVAPI
    /// <summary>Функции драйвера NVIDIA для вентиляторов. Номера — те же, что использует LibreHardwareMonitor.</summary>
    private static class Nv
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr QueryInterface(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Plain();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumGpus([Out] IntPtr[] handles, out int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OnGpu(IntPtr gpu, IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Thermal(IntPtr gpu, int sensor, IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ErrorMessage(int status, IntPtr text);

        private const int StatusItem = 52, StatusSize = 8 + 32 + 32 * StatusItem;      // NV_GPU_CLIENT_FAN_COOLERS_STATUS v1
        private const int ControlItem = 44, ControlSize = 12 + 32 + 32 * ControlItem;   // NV_GPU_CLIENT_FAN_COOLERS_CONTROL v1
        private const int ThermalSize = 8 + 3 * 20;                                     // NV_GPU_THERMAL_SETTINGS v2

        private static OnGpu getStatus, getControl, setControl;
        private static Thermal thermal;
        private static ErrorMessage errorText;
        private static IntPtr gpu;
        private static bool tried, ready;
        private static string why = "";
        private static readonly object Lock = new();

        public static bool Ready(out string reason)
        {
            lock (Lock)
            {
                if (!tried)
                {
                    tried = true;
                    try { ready = Load(); }
                    catch (Exception ex) { ready = false; why = L.F("Драйвер NVIDIA не отвечает ({0}).", ex.Message); }
                }
                reason = why;
                return ready;
            }
        }

        private static bool Load()
        {
            string dll = Path.Combine(Environment.SystemDirectory, "nvapi64.dll");
            if (!Environment.Is64BitProcess || !File.Exists(dll)) { why = L.T("Видеокарты NVIDIA нет или её драйвер не установлен."); return false; }
            if (!NativeLibrary.TryLoad(dll, out IntPtr lib) || !NativeLibrary.TryGetExport(lib, "nvapi_QueryInterface", out IntPtr entry))
            { why = L.T("Библиотека драйвера NVIDIA не загрузилась. Переустановите драйвер."); return false; }
            var query = Marshal.GetDelegateForFunctionPointer<QueryInterface>(entry);
            T Fn<T>(uint id) where T : Delegate { IntPtr p = query(id); return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p); }

            var init = Fn<Plain>(0x0150E828);                   // NvAPI_Initialize
            if (init == null || init() != 0) { why = L.T("Драйвер NVIDIA не отвечает."); return false; }
            var enumGpus = Fn<EnumGpus>(0xE5AC921F);            // NvAPI_EnumPhysicalGPUs
            getStatus = Fn<OnGpu>(0x35AED5E8);                  // NvAPI_GPU_ClientFanCoolersGetStatus
            getControl = Fn<OnGpu>(0x814B209F);                 // NvAPI_GPU_ClientFanCoolersGetControl
            setControl = Fn<OnGpu>(0xA58971A5);                 // NvAPI_GPU_ClientFanCoolersSetControl
            thermal = Fn<Thermal>(0xE3640A56);                  // NvAPI_GPU_GetThermalSettings
            errorText = Fn<ErrorMessage>(0x6C2D048C);           // NvAPI_GetErrorMessage
            if (enumGpus == null || getStatus == null || getControl == null || setControl == null)
            { why = L.T("В установленном драйвере NVIDIA нет функций управления вентиляторами. Обновите драйвер."); return false; }

            var handles = new IntPtr[64];
            if (enumGpus(handles, out int n) != 0 || n < 1) { why = L.T("Драйвер NVIDIA не нашёл видеокарту."); return false; }
            gpu = handles[0];       // основная видеокарта; у ПК с двумя картами NVIDIA вторая не управляется
            return true;
        }

        public readonly record struct Reading(int Count, int Rpm, int Level, int Min, int Max);

        public static bool Status(out Reading r)
        {
            r = default;
            lock (Lock)
            {
                if (!ready) return false;
                IntPtr b = Alloc(StatusSize);
                try
                {
                    Marshal.WriteInt32(b, 0, StatusSize | (1 << 16));
                    if (getStatus(gpu, b) != 0) return false;
                    int count = Math.Min(Marshal.ReadInt32(b, 4), 32);
                    if (count < 1) { r = new Reading(0, 0, 0, 30, 100); return true; }
                    // Несколько каналов (у больших карт): обороты — максимум, скорость — максимум, пределы — общие.
                    int maxRpm = 0, maxLevel = 0, min = 0, max = 100;
                    for (int k = 0; k < count; k++)
                    {
                        int o = 40 + k * StatusItem;
                        maxRpm = Math.Max(maxRpm, Marshal.ReadInt32(b, o + 4));
                        min = Math.Max(min, Marshal.ReadInt32(b, o + 8));
                        max = Math.Min(max, Math.Max(1, Marshal.ReadInt32(b, o + 12)));
                        maxLevel = Math.Max(maxLevel, Marshal.ReadInt32(b, o + 16));
                    }
                    r = new Reading(count, maxRpm, maxLevel, Math.Clamp(min, 0, 100), Math.Clamp(max, 1, 100));
                    return true;
                }
                finally { Marshal.FreeHGlobal(b); }
            }
        }

        public static double Temperature()
        {
            lock (Lock)
            {
                if (!ready || thermal == null) return double.NaN;
                IntPtr b = Alloc(ThermalSize);
                try
                {
                    Marshal.WriteInt32(b, 0, ThermalSize | (2 << 16));
                    if (thermal(gpu, 15, b) != 0) return double.NaN;      // 15 — все датчики
                    int count = Math.Min(Marshal.ReadInt32(b, 4), 3);
                    for (int k = 0; k < count; k++)
                    {
                        int o = 8 + k * 20;
                        if (Marshal.ReadInt32(b, o + 16) == 1)            // датчик самого графического процессора
                        {
                            int t = Marshal.ReadInt32(b, o + 12);
                            return t is > 0 and < 130 ? t : double.NaN;
                        }
                    }
                    return double.NaN;
                }
                finally { Marshal.FreeHGlobal(b); }
            }
        }

        /// <summary>manual=false — вернуть все каналы драйверу. Возвращает код драйвера (0 — принято).</summary>
        public static int SetLevel(int level, bool manual)
        {
            lock (Lock)
            {
                if (!ready) return -1;
                IntPtr b = Alloc(ControlSize);
                try
                {
                    Marshal.WriteInt32(b, 0, ControlSize | (1 << 16));
                    int st = getControl(gpu, b);
                    if (st != 0) return st;
                    int count = Math.Min(Marshal.ReadInt32(b, 8), 32);
                    for (int k = 0; k < count; k++)
                    {
                        int o = 44 + k * ControlItem;
                        Marshal.WriteInt32(b, o + 4, manual ? Math.Clamp(level, 0, 100) : 0);
                        Marshal.WriteInt32(b, o + 8, manual ? 1 : 0);        // 1 — вручную, 0 — драйвер
                    }
                    return setControl(gpu, b);
                }
                finally { Marshal.FreeHGlobal(b); }
            }
        }

        public static string Error(int status)
        {
            if (errorText != null)
            {
                IntPtr buf = Marshal.AllocHGlobal(64);
                try
                {
                    Marshal.WriteByte(buf, 0, 0);
                    if (errorText(status, buf) == 0) { string t = Marshal.PtrToStringAnsi(buf); if (!string.IsNullOrEmpty(t)) return t + ", " + L.F("код {0}", status); }
                }
                catch { }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return L.F("код {0}", status);
        }

        private static IntPtr Alloc(int size)
        {
            IntPtr b = Marshal.AllocHGlobal(size);
            for (int i = 0; i < size; i++) Marshal.WriteByte(b, i, 0);
            return b;
        }

        /// <summary>Для самопроверки: прочитать настройки каналов, ничего не меняя.</summary>
        public static string DescribeControl()
        {
            lock (Lock)
            {
                if (!ready) return "нет";
                IntPtr b = Alloc(ControlSize);
                try
                {
                    Marshal.WriteInt32(b, 0, ControlSize | (1 << 16));
                    int st = getControl(gpu, b);
                    if (st != 0) return "ошибка " + Error(st);
                    int count = Math.Min(Marshal.ReadInt32(b, 8), 32);
                    var sb = new StringBuilder();
                    for (int k = 0; k < count; k++)
                    {
                        int o = 44 + k * ControlItem;
                        sb.Append($"канал {Marshal.ReadInt32(b, o)}: {Marshal.ReadInt32(b, o + 4)} %, {(Marshal.ReadInt32(b, o + 8) == 1 ? "вручную" : "драйвер")}; ");
                    }
                    return sb.ToString();
                }
                finally { Marshal.FreeHGlobal(b); }
            }
        }
    }

    // ================================================================== самопроверка
    /// <summary>Кривая, решения и защита — на подставных числах; вентиляторы только читаются.</summary>
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        string fail = null;
        void Check(string what, bool ok) { sb.AppendLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) fail ??= what; }

        var c = Balanced;
        Check("кривая: ниже первой точки — скорость первой", Interpolate(c, 30) == 30);
        Check("кривая: между 55 и 65 °C — посередине", Math.Abs(Interpolate(c, 60) - 47.5) < 0.01);
        Check("кривая: выше последней — 100 %", Interpolate(c, 90) == 100);

        var messy = Normalize(new[] { new FanPoint(70, 40), new FanPoint(50, 60), new FanPoint(90, 50) });
        Check("порядок: точки по температуре, скорость не падает, последняя ≤ 85 °C и 100 %",
            messy.Length == 3 && messy[0].T == 50 && messy[1].L >= messy[0].L && messy[^1].T <= 85 && messy[^1].L == 100);
        Check("запись и чтение кривой", FormatCurve(ParseCurve(FormatCurve(Quiet))) == FormatCurve(Quiet));
        Check("испорченная строка кривой — нет кривой", ParseCurve("abc") == null && ParseCurve("") == null);

        Check("режим «драйвер» — вентиляторы у драйвера", Decide(Mode.Driver, true, false, true, c, 50, 70, 30, 100, false, out _) == -1);
        Check("от 84 °C — полная скорость в любом режиме, кроме «драйвер»",
            Decide(Mode.Fixed, true, false, true, c, 40, 86, 30, 100, true, out _) == 100 && Decide(Mode.Curve, false, true, true, c, 40, 84, 30, 100, false, out _) == 100);
        Check("температуры нет — отдать драйверу", Decide(Mode.Curve, true, false, false, c, 50, double.NaN, 30, 100, true, out _) == -1);
        Check("«только в игре» вне игры — драйвер", Decide(Mode.Curve, false, true, false, c, 50, 70, 30, 100, false, out _) == -1);
        Check("постоянная скорость ниже предела карты поднимается до предела", Decide(Mode.Fixed, true, false, true, c, 20, 50, 35, 100, false, out _) == 35);
        Check("холодная карта — драйвер (вентиляторы могут стоять)", Decide(Mode.Curve, true, false, true, c, 50, 40, 30, 100, false, out _) == -1);
        Check("запас 3 °C при отпускании: 43 °C при пороге 45 — ещё держим",
            Decide(Mode.Curve, true, false, true, c, 50, 43, 30, 100, true, out _) == 30 && Decide(Mode.Curve, true, false, true, c, 50, 43, 30, 100, false, out _) == -1);
        Check("сглаживание: рост сразу, спад по градусу", Smooth(60, 70) == 70 && Smooth(70, 60) == 69);

        var sum = Summarize(new List<FanMinute>
        {
            new() { Fan = 40, Rpm = 1200, Temp = 60, Stop = 0 },
            new() { Fan = 60, Rpm = 1800, Temp = 70, Stop = 0 },
            new() { Fan = float.NaN, Rpm = 0, Temp = 41, Stop = 1 },
        });
        Check("сводка: средняя 50 %, максимум 60 %, стояли треть времени", sum.AvgFan == 50 && sum.MaxFan == 60 && Math.Abs(sum.StopPct - 33.33) < 0.1 && sum.MaxTemp == 70);

        // ---- статистика: старые файлы без новых полей
        try
        {
            var old = JsonSerializer.Deserialize<List<FanMinute>>(
                "[{\"T\":\"2026-11-10T20:00:00\",\"Temp\":61,\"Fan\":44,\"Rpm\":1300,\"Stop\":0,\"Game\":true}]", Json);
            Check("старый fans.json без загрузки и потребления читается (поля — NaN)",
                old?.Count == 1 && old[0].Temp == 61 && float.IsNaN(old[0].Load) && float.IsNaN(old[0].Watts) && float.IsNaN(old[0].Cpu));
            var back = JsonSerializer.Deserialize<List<FanMinute>>(JsonSerializer.Serialize(new List<FanMinute>
                { new() { T = new DateTime(2026, 11, 10, 20, 0, 0), Temp = 70, Fan = float.NaN, Load = 97, Watts = 151.5f, Cpu = 48 } }, Json), Json);
            Check("новые поля записываются и читаются", back?.Count == 1 && back[0].Load == 97 && back[0].Watts == 151.5f && back[0].Cpu == 48 && float.IsNaN(back[0].Fan));
        }
        catch (Exception ex) { Check("чтение fans.json: " + ex.Message, false); }

        // ---- пыль
        static List<FanMinute> Days(DateTime start, int days, double gameT, double idleT, double fan, double watts, int gameMin = 30, double load = 95)
        {
            var list = new List<FanMinute>();
            for (int d = 0; d < days; d++)
            {
                var day = start.Date.AddDays(d);
                for (int i = 0; i < 20; i++)
                    list.Add(new FanMinute { T = day.AddHours(10).AddMinutes(i), Temp = (float)idleT, Fan = 0, Rpm = 0, Stop = 1, Load = 3, Watts = 14 });
                for (int i = 0; i < gameMin; i++)
                    list.Add(new FanMinute { T = day.AddHours(20).AddMinutes(i), Temp = (float)gameT, Fan = (float)fan, Rpm = 1500, Stop = 0, Game = true, Load = (float)load, Watts = (float)watts });
            }
            return list;
        }
        static DustView Compare(List<FanMinute> b, List<FanMinute> c, bool fixedMode)
        {
            var v = new DustView { Base = DustRange(b, DateTime.MinValue, DateTime.MaxValue), Now = DustRange(c, DateTime.MinValue, DateTime.MaxValue), BaseReady = true };
            Judge(v, fixedMode);
            return v;
        }
        var d0 = new DateTime(2026, 1, 1);
        var day1 = DustDay(Days(d0, 1, 70, 40, 55, 150));
        Check("пыль: день — в игре 70 °C, в простое 40 °C → разница 30 °C", day1.Minutes == 30 && Math.Abs(day1.Delta - 30) < 0.01 && day1.Fan == 55 && day1.Watts == 150);
        Check("пыль: день без простоя не годится", DustDay(Days(d0, 1, 70, 40, 55, 150).Where(m => m.Game)).Minutes == 0);
        Check("пыль: минуты с загрузкой видеокарты ниже 85 % не считаются", DustDay(Days(d0, 1, 70, 40, 55, 150, load: 60)).Minutes == 0);
        var packed = DustPeriod.Unpack(DustRange(Days(d0, 3, 70, 40, 55, 150), DateTime.MinValue, DateTime.MaxValue).Pack());
        Check("пыль: опорный период сохраняется в настройках и читается", packed.Minutes == 90 && packed.Days == 3 && Math.Abs(packed.Delta - 30) < 0.01 && packed.From == d0 && packed.To == d0.AddDays(2));
        Check("пыль: испорченная строка — пустой период", DustPeriod.Unpack("x;y").Minutes == 0 && DustPeriod.Unpack(null).Minutes == 0);

        var hot = Compare(Days(d0, 7, 70, 40, 55, 150), Days(d0.AddDays(30), 7, 76, 46, 57, 150), false);
        Check("пыль: летняя жара (простой тоже +6 °C) — не пыль", !hot.Dusty && Math.Abs(hot.TempRise) < 0.01);
        var dusty = Compare(Days(d0, 7, 70, 40, 55, 150), Days(d0.AddDays(30), 7, 77, 40, 67, 150), false);
        Check("пыль: +7 °C при той же нагрузке и вентиляторы +12 п. п. — пора чистить", dusty.Dusty && Math.Abs(dusty.TempRise - 7) < 0.01 && Math.Abs(dusty.FanRise - 12) < 0.01);
        Check("пыль: +7 °C, а вентиляторы как были — не пыль", !Compare(Days(d0, 7, 70, 40, 55, 150), Days(d0.AddDays(30), 7, 77, 40, 58, 150), false).Dusty);
        Check("пыль: постоянная скорость — достаточно одной температуры", Compare(Days(d0, 7, 70, 40, 55, 150), Days(d0.AddDays(30), 7, 76, 40, 55, 150), true).Dusty);
        Check("пыль: потребление выросло на 20 % — это игры, не пыль", !Compare(Days(d0, 7, 70, 40, 55, 150), Days(d0.AddDays(30), 7, 78, 40, 70, 180), false).Dusty);

        var st = new AppSettings();
        var baseMins = Days(d0, 8, 70, 40, 55, 150);
        DustStep(st, baseMins, d0.AddDays(3).AddHours(12), "driver", false, out var early);
        Check("пыль: на 4-й день опорный период ещё копится", !early.BaseReady && early.Base.Minutes == 90 && early.BaseDay == 4 && early.Text.Contains(L.Pick("день 4 из 7", "day 4 of 7")));
        DustStep(st, baseMins, d0.AddDays(8).AddHours(12), "driver", false, out var ready);
        Check("пыль: через 7 дней и 240 минут опорный период готов", ready.BaseReady && ready.Base.Minutes == 240 && Math.Abs(ready.Base.Delta - 30) < 0.01);
        var all = baseMins.Concat(Days(d0.AddDays(8), 7, 77, 40, 67, 150)).ToList();
        DustStep(st, all, d0.AddDays(15).AddHours(12), "driver", false, out var later);
        Check("пыль: последние 7 дней сравниваются с опорными — пора чистить", later.BaseReady && later.Now.Enough && later.Dusty && later.Text.Length > 0
            && (L.Code != "ru" || later.Text.StartsWith("Похоже, пора почистить компьютер от пыли")));
        DustStep(st, all, d0.AddDays(15).AddHours(13), "curve:" + FormatCurve(Balanced), false, out var reset);
        Check("пыль: смена режима вентиляторов — опорный период заново", !reset.BaseReady && reset.Base.Minutes == 0 && reset.BaseDay == 1);
        var hist = History();
        sb.AppendLine();
        sb.AppendLine($"Статистика на этом ПК: {hist.Count} мин, с загрузкой и потреблением видеокарты — {hist.Count(m => !float.IsNaN(m.Load))} мин"
                      + $", из них в играх при загрузке от {DustLoad} % — {hist.Count(m => m.Game && m.Load >= DustLoad)} мин");

        sb.AppendLine();
        if (Nv.Ready(out string why))
        {
            if (Nv.Status(out var r)) sb.AppendLine($"Вентиляторы сейчас: каналов {r.Count}, {r.Rpm} об/мин, {r.Level} %, пределы {r.Min}–{r.Max} %");
            sb.AppendLine("Температура видеокарты (драйвер): " + Nv.Temperature());
            sb.AppendLine("Управление: " + Nv.DescribeControl());
        }
        else sb.AppendLine("Драйвер NVIDIA: " + why);
        return (fail == null ? "OK" : "FAIL: " + fail) + "\n" + sb;
    }
}
