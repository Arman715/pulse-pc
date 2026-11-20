using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PulsePC.Services;

/// <summary>Накопительные счётчики пинга: по разнице двух снимков считается статистика за игровую сессию.</summary>
public struct NetCounters
{
    public long Sent, Lost, Received;
    public double SumMs, SumAbsDiff;

    public NetCounters Minus(NetCounters start) => new()
    {
        Sent = Sent - start.Sent, Lost = Lost - start.Lost, Received = Received - start.Received,
        SumMs = SumMs - start.SumMs, SumAbsDiff = SumAbsDiff - start.SumAbsDiff,
    };
}

/// <summary>Один адрес, до которого измеряется задержка. Хранит последние 60 замеров (две минуты).</summary>
public sealed class PingTarget
{
    private const int Capacity = 60;
    private readonly object gate = new();
    private readonly List<double> values = new();    // NaN = пакет потерян
    private NetCounters counters;
    private double lastOk = double.NaN;

    public string Host { get; set; }

    public NetCounters Counters { get { lock (gate) return counters; } }

    /// <summary>
    /// Отметка начала отрезка (игровой сессии). Первый замер после отметки сравнивается с последним замером до неё —
    /// эта разница не принадлежит отрезку и вычитается из разброса (см. End).
    /// </summary>
    public sealed class Mark
    {
        internal NetCounters Start;
        internal bool Anchored, Seen;
        internal double CrossDiff;
    }

    private readonly List<Mark> marks = new();

    public Mark Begin()
    {
        lock (gate)
        {
            var m = new Mark { Start = counters, Anchored = !double.IsNaN(lastOk) };
            marks.Add(m);
            return m;
        }
    }

    /// <summary>Счётчики за отрезок и разница через его начало (0, если её не было).</summary>
    public NetCounters End(Mark m, out double crossDiff)
    {
        lock (gate)
        {
            marks.Remove(m);
            crossDiff = m.Seen && m.Anchored ? m.CrossDiff : 0;
            return counters.Minus(m.Start);
        }
    }

    public void Add(double ms)
    {
        lock (gate)
        {
            values.Add(ms);
            if (values.Count > Capacity) values.RemoveAt(0);
            counters.Sent++;
            if (double.IsNaN(ms)) { counters.Lost++; return; }
            counters.Received++;
            counters.SumMs += ms;
            double diff = double.IsNaN(lastOk) ? 0 : Math.Abs(ms - lastOk);
            if (!double.IsNaN(lastOk)) counters.SumAbsDiff += diff;
            foreach (var m in marks)
                if (!m.Seen) { m.Seen = true; m.CrossDiff = double.IsNaN(lastOk) ? 0 : diff; m.Anchored &= !double.IsNaN(lastOk); }
            lastOk = ms;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            values.Clear();
            lastOk = double.NaN;
            // Ряд начат заново: первый новый замер не сравнивается со старым, вычитать нечего.
            foreach (var m in marks) if (!m.Seen) m.Anchored = false;
        }
    }

    public double[] Snapshot() { lock (gate) return values.ToArray(); }

    /// <summary>Последнее значение, среднее, разброс (среднее изменение между соседними замерами) и потери за окно.</summary>
    public (double Last, double Avg, double Jitter, double LossPct, int Count) Stats()
    {
        double[] v = Snapshot();
        if (v.Length == 0) return (double.NaN, double.NaN, double.NaN, double.NaN, 0);
        var ok = v.Where(x => !double.IsNaN(x)).ToArray();
        double jitter = double.NaN;
        if (ok.Length > 1)
        {
            double sum = 0;
            for (int i = 1; i < ok.Length; i++) sum += Math.Abs(ok[i] - ok[i - 1]);
            jitter = sum / (ok.Length - 1);
        }
        return (v[^1], ok.Length > 0 ? ok.Average() : double.NaN, jitter, 100.0 * (v.Length - ok.Length) / v.Length, v.Length);
    }
}

/// <summary>
/// Сетевой монитор: раз в две секунды измеряет задержку до роутера и до интернета.
/// Работает только когда это нужно (открыта страница «Сеть» или идёт запись игровой сессии).
/// </summary>
public sealed class NetMonitor : IDisposable
{
    private volatile bool disposed;
    private int loops;

    public PingTarget Internet { get; } = new() { Host = "1.1.1.1" };
    public PingTarget Router { get; } = new() { Host = null };
    public volatile string AdapterText = "";
    public volatile bool Active;

    /// <summary>Пришёл новый замер (вызывается из фонового потока).</summary>
    public event Action Sampled;

    public void Start() => Task.Run(Loop);

    private async Task Loop()
    {
        using var pingA = new Ping();
        using var pingB = new Ping();
        while (!disposed)
        {
            if (!Active) { await Task.Delay(500); continue; }
            try
            {
                if (loops++ % 30 == 0) RefreshAdapter();
                var started = DateTime.UtcNow;
                var internet = Send(pingA, Internet.Host);
                var router = Router.Host != null ? Send(pingB, Router.Host) : Task.FromResult(double.NaN);
                Internet.Add(await internet);
                if (Router.Host != null) Router.Add(await router);
                Sampled?.Invoke();
                int rest = 2000 - (int)(DateTime.UtcNow - started).TotalMilliseconds;
                if (rest > 0) await Task.Delay(rest);
            }
            catch { await Task.Delay(2000); }
        }
    }

    private static async Task<double> Send(Ping ping, string host)
    {
        try
        {
            var reply = await ping.SendPingAsync(host, 1000);
            return reply.Status == IPStatus.Success ? Math.Max(reply.RoundtripTime, 0.5) : double.NaN;
        }
        catch { return double.NaN; }
    }

    /// <summary>
    /// Адрес этого компьютера на том подключении, через которое Windows на самом деле ходит в интернет.
    /// Сокет ничего не отправляет: система лишь выбирает маршрут. Так виртуальные сети (Radmin VPN и подобные),
    /// у которых тоже есть свой шлюз, не принимаются за домашний роутер.
    /// </summary>
    private static IPAddress LocalAddressToInternet()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("1.1.1.1", 53);
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch { return null; }
    }

    /// <summary>Активное подключение и адрес роутера.</summary>
    private void RefreshAdapter()
    {
        try
        {
            var local = LocalAddressToInternet();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = nic.GetIPProperties();
                // Нужен именно тот адаптер, чей адрес система выбрала для выхода в интернет.
                if (local != null && !props.UnicastAddresses.Any(u => u.Address.Equals(local))) continue;
                var gateway = props.GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
                if (gateway == null) continue;

                string host = gateway.ToString();
                if (Router.Host != host) { Router.Host = host; Router.Reset(); }
                string kind = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : L.T("кабель");
                string speed = nic.Speed >= 1_000_000_000 ? L.F("{0:0.#} Гбит/с", nic.Speed / 1e9) : L.F("{0:0} Мбит/с", nic.Speed / 1e6);
                AdapterText = $"{kind} · {nic.Description} · {speed} · " + L.F("роутер {0}", host);
                return;
            }
            // Шлюза больше нет: старый роутер не пингуется и не рисуется, пока не появится новый.
            if (Router.Host != null) { Router.Host = null; Router.Reset(); }
            AdapterText = L.T("нет подключения к сети");
        }
        catch { }
    }

    public void Dispose() => disposed = true;
}

/// <summary>Самопроверка счётчиков пинга и сохранения сессий (на выдуманных данных, сеть и настоящие сессии не трогаются).</summary>
public static class SessionChecks
{
    public static string SelfTest()
    {
        var fails = new List<string>();
        void Check(string what, bool ok) { if (!ok) fails.Add(what); }

        // Разброс внутри сессии: до записи 100 мс, в игре десять замеров по 10 мс — разброс 0, а не 10.
        var t = new PingTarget();
        t.Add(100);
        var mark = t.Begin();
        for (int i = 0; i < 10; i++) t.Add(10);
        var d = t.End(mark, out double cross);
        double jitter = Math.Max(0, d.SumAbsDiff - cross) / (d.Received - 1);
        Check($"разброс без замера до сессии (получено {jitter:0.##})", d.Received == 10 && Math.Abs(jitter) < 1e-9);

        // Без замера до сессии вычитать нечего: 10, 20, 10 → разброс 10.
        var t2 = new PingTarget();
        var m2 = t2.Begin();
        foreach (var v in new double[] { 10, 20, 10 }) t2.Add(v);
        var d2 = t2.End(m2, out double cross2);
        Check("разброс сессии с первого замера", Math.Abs((d2.SumAbsDiff - cross2) / (d2.Received - 1) - 10) < 1e-9);

        // Потерянный первый пакет не ломает подсчёт: 50 | потеря, 30, 30 → разница 50→30 не в сессии.
        var t3 = new PingTarget();
        t3.Add(50);
        var m3 = t3.Begin();
        t3.Add(double.NaN); t3.Add(30); t3.Add(30);
        var d3 = t3.End(m3, out double cross3);
        Check("потеря в начале сессии", d3.Received == 2 && Math.Abs(d3.SumAbsDiff - cross3) < 1e-9);

        // Пауза в кадрах (игра свёрнута 30 с): после неё FPS «сейчас» и ряд — по кадрам после паузы, а не «около 0».
        var cap = new FpsCapture();
        cap.Feed("Application,ProcessID,TimeInSeconds,msBetweenPresents");
        double clock = 0;
        void Frame(double ms) { clock += ms / 1000; cap.Feed(FormattableString.Invariant($"game.exe,1,{clock:0.0000},{ms:0.000}")); }
        for (int i = 0; i < 700; i++) Frame(10);
        Frame(30000);
        for (int i = 0; i < 150; i++) Frame(10);
        var fr = cap.Result();
        Check($"FPS после паузы в кадрах (сейчас {cap.LiveFps:0.#}, ряд {(fr == null ? "-" : string.Join("/", fr.Series.Select(v => v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))))})",
            cap.LiveFps > 90 && cap.LiveFps < 110 && fr != null && fr.Frames == 850 && fr.Series.Length >= 1 && fr.Series.All(v => v > 80));

        // Сохранение сессии в папку, где на месте файла сессий — папка: Add должен вернуть причину, а не молчать.
        string old = Environment.GetEnvironmentVariable("PULSEPC_DATA");
        string box = Path.Combine(Path.GetTempPath(), "PulsePC-sessions-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(box, "sessions.json"));
            Environment.SetEnvironmentVariable("PULSEPC_DATA", box);
            string error = SessionStore.Add(new GameSession { Game = "проверка", Start = DateTime.Now, End = DateTime.Now });
            Check("ошибка записи сессии возвращается", !string.IsNullOrEmpty(error));
            Directory.Delete(Path.Combine(box, "sessions.json"));
            Check("успешная запись сессии — без ошибки", SessionStore.Add(new GameSession { Game = "проверка", Start = DateTime.Now, End = DateTime.Now }) == null);
            SessionStore.Add(new GameSession { Game = "другая", Start = DateTime.Now.AddDays(-100), End = DateTime.Now.AddDays(-100) });
            var (removed, removeError) = SessionStore.Remove(s => (DateTime.Now - s.Start).TotalDays > 90);
            var left = SessionStore.Load();
            Check($"удаление старых сессий (удалено {removed}, осталось {left.Count})", removeError == null && removed == 1 && left.Count == 1 && left[0].Game == "проверка");
        }
        catch (Exception ex) { fails.Add("исключение: " + ex.Message); }
        finally
        {
            Environment.SetEnvironmentVariable("PULSEPC_DATA", old);
            try { Directory.Delete(box, true); } catch { }
        }
        return fails.Count == 0 ? "OK (7 проверок)" : "FAIL: " + string.Join("; ", fails);
    }
}
