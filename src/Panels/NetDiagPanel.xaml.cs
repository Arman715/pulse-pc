using System.Net;
using System.Windows;
using System.Windows.Controls;
using PulsePC.Services;

namespace PulsePC.Panels;

/// <summary>
/// Проверки сети на вкладке «Сеть» (под живыми графиками пинга): Wi‑Fi, пинг под нагрузкой, где теряются пакеты, скорость DNS.
/// Wi‑Fi читается при открытии вкладки; остальное — только по кнопке, по одной проверке за раз и не во время игры.
/// Ничего не меняет в системе. Для снимков с готовыми результатами: PULSEPC_NETDIAG_DEMO=1.
/// </summary>
public partial class NetDiagPanel : UserControl, ISystemPanel
{
    public sealed class NoteVm
    {
        public Severity Level { get; init; }
        public string Title { get; init; } = "";
        public string Text { get; init; } = "";
    }

    public sealed class FactVm
    {
        public string Name { get; init; } = "";
        public string Value { get; init; } = "";
        public string Note { get; init; } = "";
    }

    public sealed class HopVm
    {
        public string Ttl { get; init; } = "";
        public string Address { get; init; } = "";
        public string Segment { get; init; } = "";
        public string Ping { get; init; } = "";
        public string Loss { get; init; } = "";
        public Severity LossLevel { get; init; }
        public Visibility MarkVisible { get; init; } = Visibility.Collapsed;
    }

    public sealed class DnsVm
    {
        public string Address { get; init; } = "";
        public string Who { get; init; } = "";
        public string Median { get; init; } = "";
        public string Fails { get; init; } = "";
    }

    private PanelHost host;
    private bool busy;
    private string gameServer;
    private static bool Demo => Environment.GetEnvironmentVariable("PULSEPC_NETDIAG_DEMO") == "1";

    public NetDiagPanel() => InitializeComponent();

    public void Attach(PanelHost h) => host = h;

    public void Shown()
    {
        if (host == null) return;
        FindGameServer();
        if (Demo) { ShowDemo(); return; }
        Task.Run(() =>
        {
            NetDiag.WifiInfo w = null;
            try { w = NetDiag.ReadWifi(); } catch { }
            host.Ui(() => ShowWifi(w));
        });
    }

    // ================================================================== Wi‑Fi
    private void ShowWifi(NetDiag.WifiInfo w)
    {
        if (w == null || !w.Present) { WifiBlock.Visibility = Visibility.Collapsed; return; }
        WifiBlock.Visibility = Visibility.Visible;
        if (!w.Connected)
        {
            WifiState.Text = L.F("Адаптер {0} к Wi‑Fi не подключён — компьютер в сети по кабелю или сети нет.", w.Adapter);
            WifiFacts.ItemsSource = null;
            WifiNotes.ItemsSource = null;
            return;
        }
        double band = w.Band;
        WifiState.Text = w.Ssid != "" ? L.F("Подключено к сети «{0}» · {1}", w.Ssid, w.Adapter) : w.Adapter;
        var facts = new List<FactVm>
        {
            new() { Name = L.T("Диапазон"), Value = NetDiag.BandText(band), Note = band == 2.4 ? L.T("больше помех, чем на 5 ГГц") : "" },
            new() { Name = L.T("Канал"), Value = w.Channel > 0 ? w.Channel.ToString() : "—",
                    Note = w.ListDenied ? L.T("соседние сети не видны") : w.SameChannel + w.Overlapping > 0
                        ? L.F("сетей на этом и соседних каналах: {0}", w.SameChannel + w.Overlapping) : L.T("соседей на канале нет") },
            new() { Name = L.T("Сигнал"), Value = w.Rssi != 0 ? NetDiag.Signed(w.Rssi) + " " + L.Pick("дБм", "dBm") : w.Quality + " %",
                    Note = w.Rssi != 0 ? L.F("качество {0} %", w.Quality) + " · " + SignalWord(w.Rssi) : "" },
            new() { Name = L.T("Стандарт"), Value = NetDiag.Standard(w.Phy, band) },
            new() { Name = L.T("Скорость соединения"), Value = w.RxMbps > 0 || w.TxMbps > 0 ? L.F("{0} / {1} Мбит/с", w.RxMbps, w.TxMbps) : "—",
                    Note = L.T("приём / отдача; это скорость до роутера, а не тариф") },
        };
        WifiFacts.ItemsSource = facts;
        WifiNotes.ItemsSource = NetDiag.WifiAdvice(w).Select(Note).ToList();
    }

    private static string SignalWord(int rssi) => rssi >= -60 ? L.T("сильный") : rssi >= -67 ? L.T("хороший") : rssi >= -75 ? L.T("средний") : L.T("слабый");

    private static NoteVm Note(NetNote n) => new() { Level = n.Level, Title = n.Title, Text = n.Text };

    // ================================================================== общее
    /// <summary>Можно ли начать проверку: одна за раз и не во время игры (проверка нагружает сеть).</summary>
    private bool CanStart(TextBlock status)
    {
        if (busy) return false;
        if (GameHooks.InGame || host?.Live().Recording == true)
        {
            status.Text = L.T("Во время игры проверки не запускаются: они нагружают сеть и мешают игре.");
            return false;
        }
        return true;
    }

    private void SetBusy(bool on)
    {
        busy = on;
        BtnLoad.IsEnabled = BtnDns.IsEnabled = BtnTraceInet.IsEnabled = BtnTraceGame.IsEnabled = !on;
        if (on) host?.BeginBusy(); else host?.EndBusy();
    }

    private Progress<string> Progress(TextBlock status) => new(s => status.Text = s);

    // ================================================================== пинг под нагрузкой
    private async void BtnLoad_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStart(LoadStatus)) return;
        SetBusy(true);
        LoadFacts.ItemsSource = null; LoadNotes.ItemsSource = null;
        NetDiag.LoadResult r = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var prog = Progress(LoadStatus); r = await Task.Run(() => NetDiag.RunLoad(prog, cts.Token));
        }
        catch (Exception ex) { r = new NetDiag.LoadResult { Error = ex.Message }; }
        SetBusy(false);
        ShowLoad(r);
        LoadStatus.Text = L.F("проверено в {0:HH:mm}", DateTime.Now);
    }

    private void ShowLoad(NetDiag.LoadResult r)
    {
        var facts = new List<FactVm>();
        if (r.Gateway != "")
            facts.Add(new FactVm { Name = L.F("Роутер {0}", r.Gateway), Value = $"{NetDiag.Ms(r.GwIdle)} → {NetDiag.Ms(r.GwLoaded)}", Note = Rise(r.GwIdle, r.GwLoaded) });
        facts.Add(new FactVm { Name = L.T("Интернет · 1.1.1.1"), Value = $"{NetDiag.Ms(r.InIdle)} → {NetDiag.Ms(r.InLoaded)}", Note = Rise(r.InIdle, r.InLoaded) });
        if (r.Bytes > 0)
            facts.Add(new FactVm { Name = L.T("Скачано за проверку"), Value = L.F("{0:0} МБ", r.Bytes / 1e6), Note = r.Mbps > 0 ? L.F("около {0:0} Мбит/с", r.Mbps) : "" });
        LoadFacts.ItemsSource = facts;
        LoadNotes.ItemsSource = NetDiag.LoadVerdict(r).Select(Note).ToList();
    }

    private static string Rise(double idle, double loaded) =>
        double.IsNaN(idle) || double.IsNaN(loaded) ? L.T("в покое → во время скачивания") : L.F("в покое → во время скачивания, {0}{1}", loaded >= idle ? "+" : "−", NetDiag.Ms(Math.Abs(loaded - idle)));

    // ================================================================== трассировка
    private void FindGameServer()
    {
        gameServer = null;
        try
        {
            var s = host.Sessions().FirstOrDefault(x => !string.IsNullOrEmpty(x.Server) && (DateTime.Now - x.Start).TotalDays <= 30 && IPAddress.TryParse(x.Server, out _));
            if (s != null)
            {
                gameServer = s.Server;
                TraceTarget.Text = L.F("Сервер последней игры: {0} · {1} ({2:d}). Игровые серверы часто не отвечают на пинг — тогда вывод делается по последнему ответившему узлу.", s.Game, s.Server, s.Start);
            }
            else TraceTarget.Text = L.T("Сервер игры программа узнаёт во время записи игры (нужны права администратора). Пока проверка идёт до 1.1.1.1.");
        }
        catch { }
        BtnTraceGame.Visibility = gameServer != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void BtnTrace_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStart(TraceStatus)) return;
        bool game = sender == BtnTraceGame && gameServer != null;
        var target = IPAddress.Parse(game ? gameServer : "1.1.1.1");
        string title = game ? L.F("до сервера игры {0}", gameServer) : L.T("до 1.1.1.1 (Cloudflare)");
        SetBusy(true);
        TraceBody.Visibility = Visibility.Collapsed;
        NetDiag.TraceResult r;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var prog = Progress(TraceStatus); r = await Task.Run(() => NetDiag.RunTrace(target, title, prog, cts.Token));
        }
        catch (Exception ex) { r = new NetDiag.TraceResult { Target = target.ToString(), Title = title, Error = ex.Message }; }
        SetBusy(false);
        NetMonitor.InternetAdapter(out var gw);
        ShowTrace(r, gw?.ToString() ?? "");
        TraceStatus.Text = L.F("проверено в {0:HH:mm}", DateTime.Now);
    }

    private void ShowTrace(NetDiag.TraceResult r, string gateway)
    {
        var notes = NetDiag.TraceVerdict(r, gateway);   // заодно размечает участки
        TraceTitle.Text = L.F("Путь {0}: {1}", r.Title, Fmt.Plural(r.Hops.Count, "узел", "узла", "узлов"));
        Hops.ItemsSource = r.Hops.Select(h => new HopVm
        {
            Ttl = h.Ttl.ToString(),
            Address = h.Address == "" ? L.T("не отвечает") : h.Address,
            Segment = NetDiag.SegmentName(h.Segment),
            Ping = h.Received > 0 ? NetDiag.Ms(h.Avg) : "—",
            Loss = h.Sent > 0 ? h.LossPct.ToString("0", L.Culture) + " %" : "—",
            LossLevel = h.LossPct >= 5 ? Severity.Warn : Severity.Info,
            MarkVisible = h.Received > 0 && h.LossPct >= 5 ? Visibility.Visible : Visibility.Collapsed,
        }).ToList();
        TraceNotes.ItemsSource = notes.Select(Note).ToList();
        TraceBody.Visibility = Visibility.Visible;
    }

    // ================================================================== DNS
    private async void BtnDns_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStart(DnsStatus)) return;
        SetBusy(true);
        DnsRows.ItemsSource = null; DnsNotes.ItemsSource = null;
        List<NetDiag.DnsServer> servers;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var prog = Progress(DnsStatus); servers = await Task.Run(() => NetDiag.RunDns(prog, cts.Token));
        }
        catch { servers = new List<NetDiag.DnsServer>(); }
        SetBusy(false);
        ShowDns(servers);
        DnsStatus.Text = L.F("проверено в {0:HH:mm}", DateTime.Now);
    }

    private void ShowDns(List<NetDiag.DnsServer> servers)
    {
        DnsRows.ItemsSource = servers.Select(s => new DnsVm
        {
            Address = s.Address,
            Who = (s.Current ? L.T("текущий") + (s.Name != "" ? " · " + s.Name : "") : s.Name),
            Median = s.Times.Count > 0 ? NetDiag.Ms(s.MedianMs) : "—",
            Fails = s.Fail > 0 ? L.F("без ответа: {0} из {1}", s.Fail, s.Fail + s.Times.Count) : L.T("ответил на все"),
        }).ToList();
        DnsNotes.ItemsSource = NetDiag.DnsVerdict(servers).Select(Note).ToList();
    }

    // ================================================================== снимки
    /// <summary>Готовые результаты на подставных данных — только для снимков вида (PULSEPC_NETDIAG_DEMO=1).</summary>
    private void ShowDemo()
    {
        ShowWifi(new NetDiag.WifiInfo
        {
            Present = true, Connected = true, CarriesInternet = true, Adapter = "Intel(R) Wi-Fi 6E AX211 160MHz", Ssid = "Home-2G",
            FreqMhz = 2437, Channel = 6, Rssi = -71, Quality = 58, Phy = 10, RxMbps = 144, TxMbps = 173, SameChannel = 3, Overlapping = 2,
        });
        ShowLoad(new NetDiag.LoadResult
        {
            Gateway = "192.168.1.1", GwIdle = 1.8, GwLoaded = 3.1, InIdle = 12, InLoaded = 148, InLoadedHigh = 212, Bytes = 118_000_000, Mbps = 92, InSent = 36,
        });
        LoadStatus.Text = L.F("проверено в {0:HH:mm}", DateTime.Now);
        NetDiag.Hop H(int ttl, string a, int recv, double avg, bool dest = false) => new() { Ttl = ttl, Address = a, Sent = 10, Received = recv, Avg = avg, Destination = dest };
        ShowTrace(new NetDiag.TraceResult
        {
            Target = "185.25.182.77", Title = L.F("до сервера игры {0}", "185.25.182.77"), Reached = true,
            Hops = new() { H(1, "192.168.1.1", 10, 2), H(2, "100.70.0.1", 8, 5), H(3, "", 0, double.NaN), H(4, "95.167.10.4", 8, 7), H(5, "188.254.2.1", 4, 9), H(6, "185.25.182.1", 8, 24), H(7, "185.25.182.77", 8, 25, true) },
        }, "192.168.1.1");
        TraceStatus.Text = L.F("проверено в {0:HH:mm}", DateTime.Now);
        var dns = new List<NetDiag.DnsServer>
        {
            new() { Address = "192.168.1.1", Current = true }, new() { Address = "1.1.1.1", Name = "Cloudflare" },
            new() { Address = "8.8.8.8", Name = "Google" }, new() { Address = "77.88.8.8", Name = L.T("Яндекс") },
        };
        dns[0].Times.AddRange(new double[] { 64, 71, 58, 80, 66 }); dns[0].Fail = 1;
        dns[1].Times.AddRange(new double[] { 9, 8, 11, 10 });
        dns[2].Times.AddRange(new double[] { 21, 19, 24 });
        dns[3].Times.AddRange(new double[] { 14, 13, 15 });
        ShowDns(dns);
        DnsStatus.Text = L.F("проверено в {0:HH:mm}", DateTime.Now);
    }
}
