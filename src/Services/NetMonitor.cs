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
            if (!double.IsNaN(lastOk)) counters.SumAbsDiff += Math.Abs(ms - lastOk);
            lastOk = ms;
        }
    }

    public void Reset() { lock (gate) { values.Clear(); lastOk = double.NaN; } }

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
                string kind = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "кабель";
                string speed = nic.Speed >= 1_000_000_000 ? $"{nic.Speed / 1e9:0.#} Гбит/с" : $"{nic.Speed / 1e6:0} Мбит/с";
                AdapterText = $"{kind} · {nic.Description} · {speed} · роутер {host}";
                return;
            }
            AdapterText = "нет подключения к сети";
        }
        catch { }
    }

    public void Dispose() => disposed = true;
}
