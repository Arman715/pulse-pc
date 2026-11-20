using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PulsePC.Panels;

namespace PulsePC.Services;

/// <summary>
/// «Панель на телефоне»: страница в домашней сети с показаниями во время игры.
/// Маленький HTTP-сервер на TcpListener внутри программы. Отдаёт только страницу и показания — никаких действий
/// и никакого доступа к файлам. Правила доступа в сеть программа не создаёт и не меняет: при первом включении
/// Windows сама спрашивает пользователя.
/// Кого пускает: только устройства из подсетей подключений, которые Windows считает частными или доменными (не «общедоступная
/// сеть» в кафе или гостинице) и которые не похожи на VPN или виртуальную сеть; не больше двух соединений с одного адреса;
/// заголовок запроса — за 1,5 секунды. Ключ в адресе — 12 знаков из 31 (около 59 бит).
/// Настройки: phone.enabled (по умолчанию выключено), phone.key (случайный ключ в адресе), phone.port (первый порт, 8765).
/// Для проверки без запроса Windows: переменная PULSEPC_PHONE_TEST=127.0.0.1[:порт[:ключ]] — сервер слушает только этот
/// адрес (допускаются лишь адреса 127.x) и считается включённым, настройки при этом не записываются.
/// </summary>
public static class PhoneServer
{
    public const int DefaultPort = 8765;
    private const int PortTries = 20;
    private const int MaxConnections = 8;        // одновременно; остальные закрываются сразу
    private const int MaxPerAddress = 2;         // одновременно с одного адреса
    private const int MaxRequestBytes = 4096;    // строка запроса и заголовки
    private const int HeaderTimeoutMs = 1500;    // столько ждём строку запроса и заголовки
    private const int IoTimeoutMs = 4000;        // весь ответ
    private const int HistorySize = 80;          // две минуты при шаге 1,5 с
    private const string KeyAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";   // без похожих знаков: i, l, o, 0, 1
    private const int KeyLength = 12;            // 31^12 ≈ 2^59: перебором не подобрать

    /// <summary>Что показать в разделе.</summary>
    public sealed class State
    {
        public bool Enabled, Running, Test;
        /// <summary>Старый короткий ключ заменён длинным: QR-код и значок на телефоне нужно получить заново.</summary>
        public bool KeyRenewed;
        public string Url = "", Adapter = "", Problem = "";
        public int Devices;
        public DateTime LastRequest;
    }

    private static readonly object Gate = new();
    private static readonly object StartGate = new();      // запуск и остановка идут строго по одному
    private static PanelHost host;
    private static Instance current;
    private static string adapterText = "", problem = "";
    private static Timer addressTimer, watchTimer;
    private static (IPAddress Address, int Port, string Key) test;
    private static bool testOn = true;          // в проверочном режиме переключатель живёт только в памяти
    private static bool keyRenewed;

    /// <summary>Состояние изменилось (вызывается из фонового потока).</summary>
    public static event Action Changed;

    public static bool Enabled => host != null && (test.Address != null ? testOn : host.Settings.GetBool("phone.enabled"));

    // ───────────── Договор службы ─────────────

    public static void Init(PanelHost panelHost)
    {
        lock (Gate)
        {
            if (host != null || panelHost == null) return;
            host = panelHost;
            test = TestEndpoint();
        }
        // Адрес компьютера может смениться (другая сеть, перезапуск роутера): сервер переезжает на новый.
        try
        {
            NetworkChange.NetworkAddressChanged += (_, _) =>
            {
                categoriesStale = true;          // другая сеть — тип сети спросить у Windows заново
                try { addressTimer?.Change(2500, Timeout.Infinite); } catch { }
            };
        }
        catch { }
        addressTimer = new Timer(_ => { if (Enabled && test.Address == null) Restart(onlyIfMoved: true); }, null, Timeout.Infinite, Timeout.Infinite);
        // Страховка: если панель включена, а сервер не работает (не было сети при запуске, адрес пропадал), пробуем поднять раз в 20 с.
        // Раз в 5 минут работающий сервер сверяет тип сети: сеть могли отметить общедоступной без смены адреса.
        int ticks = 0;
        watchTimer = new Timer(_ =>
        {
            bool down; lock (Gate) down = current == null;
            if (Enabled && down) Restart(onlyIfMoved: false);
            else if (Enabled && test.Address == null && ++ticks % 15 == 0) Restart(onlyIfMoved: true);
        }, null, 20000, 20000);
        try { AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { current?.Dispose(); } catch { } }; } catch { }
        if (Enabled) Task.Run(() => Restart(onlyIfMoved: false));
    }

    /// <summary>Включить или выключить панель. Настройка сохраняется; сервер поднимается и закрывается в фоне.</summary>
    public static void SetEnabled(bool on, Action done = null)
    {
        if (host == null) return;
        if (test.Address != null) testOn = on;
        else
        {
            host.Settings.SetBool("phone.enabled", on);
            host.Ui(host.Settings.Save);
        }
        Task.Run(() =>
        {
            if (on) Restart(onlyIfMoved: false); else Stop();
            done?.Invoke();
        });
    }

    /// <summary>Новый случайный ключ: старый адрес сразу перестаёт открываться.</summary>
    public static void ChangeKey(Action done = null)
    {
        if (host == null) return;
        keyRenewed = false;
        host.Settings.Set("phone.key", NewKey());
        if (test.Address == null) host.Ui(host.Settings.Save);
        Task.Run(() =>
        {
            if (Enabled) Restart(onlyIfMoved: false);
            done?.Invoke();
        });
    }

    public static State Snapshot()
    {
        lock (Gate)
        {
            var s = new State { Enabled = Enabled, Test = test.Address != null, Adapter = adapterText, Problem = problem, KeyRenewed = keyRenewed };
            if (current != null)
            {
                s.Running = true;
                s.Url = current.Url;
                (s.Devices, s.LastRequest) = current.Clients();
            }
            return s;
        }
    }

    /// <summary>Закрыть сервер (при выключении панели и при выходе из программы).</summary>
    public static void Stop()
    {
        lock (StartGate)
        {
            Instance old;
            lock (Gate) { old = current; current = null; problem = ""; }
            old?.Dispose();
        }
        Changed?.Invoke();
    }

    private static void Restart(bool onlyIfMoved)
    {
        lock (StartGate) RestartLocked(onlyIfMoved);
        Changed?.Invoke();
    }

    private static void RestartLocked(bool onlyIfMoved)
    {
        try
        {
            IPAddress address; string adapter, trouble;
            if (test.Address != null) (address, adapter, trouble) = (test.Address, "проверка на этом компьютере", "");
            else (address, adapter, trouble) = FindAddress();

            lock (Gate)
            {
                // Адрес сервера по-прежнему наш и его сеть по-прежнему частная — работающий сервер не трогаем (новый адрес на миг
                // не определился, сеть переподключается, меняются временные адреса): иначе панель гасла от любого сетевого события.
                // Сеть стала общедоступной или адреса больше нет — сервер закрывается, в разделе видна причина.
                if (onlyIfMoved && current != null && StillMine(current.Address) && Volatile.Read(ref trusted).Any(n => n.Contains(current.Address))) return;
            }
            Instance old;
            lock (Gate) { old = current; current = null; }
            old?.Dispose();

            Instance fresh = null;
            if (address != null)
            {
                string key = host.Settings.Get("phone.key");
                if (test.Address != null && ValidKey(test.Key) && !ValidKey(key)) host.Settings.Set("phone.key", key = test.Key);
                if (!ValidKey(key))
                {
                    // Ключ из прежних версий (шесть знаков) больше не годится: старые QR-коды и значки перестанут открываться.
                    if (!string.IsNullOrEmpty(key)) keyRenewed = true;
                    key = NewKey();
                    host.Settings.Set("phone.key", key);
                    if (test.Address == null) host.Ui(host.Settings.Save);
                }
                int port = test.Address != null && test.Port > 0 ? test.Port
                    : int.TryParse(host.Settings.Get("phone.port"), out int p) && p is >= 1024 and <= 65000 ? p : DefaultPort;
                fresh = Instance.TryStart(address, port, PortTries, key, host.Live, VramTotalGb, out trouble);
            }
            lock (Gate)
            {
                // Пока сервер поднимался, панель могли выключить.
                if (fresh != null && !Enabled) { fresh.Dispose(); fresh = null; trouble = ""; }
                current = fresh; adapterText = adapter ?? ""; problem = trouble ?? "";
            }
        }
        catch (Exception ex)
        {
            lock (Gate) problem = "Панель не запустилась: " + ex.Message;
        }
    }

    private static double VramTotalGb()
    {
        try { return host?.Hardware()?.MainGpu?.VramGb ?? 0; } catch { return 0; }
    }

    // ───────────── Ключ ─────────────

    private static string NewKey()
    {
        var sb = new StringBuilder(KeyLength);
        for (int i = 0; i < KeyLength; i++) sb.Append(KeyAlphabet[RandomNumberGenerator.GetInt32(KeyAlphabet.Length)]);
        return sb.ToString();
    }

    /// <summary>Ключ не короче 12 знаков и только из алфавита ключей. Шестизначные ключи прежних версий отклоняются и заменяются.</summary>
    private static bool ValidKey(string key) => key is { Length: >= KeyLength and <= 32 } && key.All(c => KeyAlphabet.Contains(c));

    // ───────────── Адрес ─────────────

    private static (IPAddress, int, string) TestEndpoint()
    {
        string raw = Environment.GetEnvironmentVariable("PULSEPC_PHONE_TEST");
        if (string.IsNullOrWhiteSpace(raw)) return (null, 0, null);
        var parts = raw.Trim().Split(':');
        // Только адреса этого же компьютера: проверочный режим не должен открывать панель в сеть.
        if (!IPAddress.TryParse(parts[0], out var address) || !IPAddress.IsLoopback(address)) return (null, 0, null);
        return (address, parts.Length > 1 && int.TryParse(parts[1], out int port) ? port : 0, parts.Length > 2 ? parts[2] : null);
    }

    private static readonly string[] VirtualMarks =
    {
        "radmin", "vpn", "virtual", "hyper-v", "vmware", "virtualbox", "tap-", "wintun", "wireguard", "hamachi", "zerotier", "tailscale", "loopback", "bluetooth",
    };

    private static bool LooksVirtual(NetworkInterface nic)
    {
        string text = (nic.Description + " " + nic.Name).ToLowerInvariant();
        return VirtualMarks.Any(text.Contains);
    }

    /// <summary>Адреса домашних сетей: 10.x, 172.16–31.x, 192.168.x. На других адресах панель не поднимается.</summary>
    public static bool IsHomeAddress(IPAddress a)
    {
        if (a == null || a.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    // ───────────── Доверенные сети ─────────────

    /// <summary>Подсеть IPv4: адрес сети и маска.</summary>
    private readonly record struct Subnet(uint Network, uint Mask)
    {
        public bool Contains(IPAddress a) => a != null && a.AddressFamily == AddressFamily.InterNetwork && (ToUint(a) & Mask) == Network;
        public override string ToString() => $"{Network >> 24}.{(Network >> 16) & 255}.{(Network >> 8) & 255}.{Network & 255}/{System.Numerics.BitOperations.PopCount(Mask)}";
    }

    private static uint ToUint(IPAddress a)
    {
        byte[] b = a.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    private static Subnet MakeSubnet(IPAddress address, int prefix)
    {
        uint mask = prefix <= 0 ? 0 : uint.MaxValue << (32 - prefix);
        return new Subnet(ToUint(address) & mask, mask);
    }

    /// <summary>Адрес a в той же подсети, что и адрес net с длиной префикса prefix (для самопроверки и разбора).</summary>
    public static bool InSubnet(IPAddress a, IPAddress net, int prefix) => MakeSubnet(net, prefix).Contains(a);

    /// <summary>
    /// Подсети частных и доменных подключений (обновляются при каждом выборе адреса). Пусто — с других устройств не пускаем никого.
    /// </summary>
    private static Subnet[] trusted = Array.Empty<Subnet>();

    /// <summary>Пускать ли устройство: только из доверенной подсети и только с домашнего адреса; этот же компьютер — всегда.</summary>
    private static bool ClientAllowed(IPAddress remote)
    {
        if (remote == null) return false;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (IPAddress.IsLoopback(remote)) return true;
        if (!IsHomeAddress(remote)) return false;
        var nets = Volatile.Read(ref trusted);
        return nets.Any(n => n.Contains(remote));
    }

    private static readonly object CategoryGate = new();
    private static Dictionary<int, int> categories;
    private static DateTime categoriesAt, categoriesFailedAt;
    private static volatile bool categoriesStale = true;

    /// <summary>
    /// Тип сети каждого подключения: номер интерфейса → 0 общедоступная, 1 частная, 2 доменная (Get-NetConnectionProfile).
    /// powershell.exe из System32, не чаще раза в 5 минут и заново после смены адресов. null — Windows не ответила.
    /// </summary>
    private static Dictionary<int, int> NetworkCategories()
    {
        lock (CategoryGate)
        {
            var now = DateTime.UtcNow;
            if (!categoriesStale && categories != null && (now - categoriesAt).TotalMinutes < 5) return categories;
            if ((now - categoriesFailedAt).TotalSeconds < 60) return categories;      // недавно не вышло — не дёргаем PowerShell каждые 20 секунд
            string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            const string script = "Get-NetConnectionProfile | ForEach-Object { '' + $_.InterfaceIndex + ' ' + [int]$_.NetworkCategory }";
            string b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var (code, output) = Shell.Run(ps, "-NoProfile -NonInteractive -EncodedCommand " + b64, 20000);
            var map = new Dictionary<int, int>();
            foreach (string line in (output ?? "").Split('\n'))
            {
                var m = System.Text.RegularExpressions.Regex.Match(line.Trim(), @"^(\d+) (\d)$");
                if (m.Success) map[int.Parse(m.Groups[1].Value)] = int.Parse(m.Groups[2].Value);
            }
            if (code != 0 && map.Count == 0)
            {
                categoriesFailedAt = now;
                return categories;          // прежний ответ (или null)
            }
            categories = map;
            categoriesAt = now;
            categoriesStale = false;
            return map;
        }
    }

    /// <summary>
    /// Адрес этого компьютера на том подключении, через которое Windows ходит в интернет (как в NetMonitor:
    /// сокет ничего не отправляет, система лишь выбирает маршрут). Подходят только подключения, которые Windows считает
    /// частными или доменными, и не виртуальные (Radmin VPN, ZeroTier, Hyper-V…): телефон в них не состоит, а в общедоступной
    /// сети (кафе, гостиница) панель видна чужим. Заодно обновляется список подсетей, из которых сервер пускает устройства.
    /// </summary>
    public static (IPAddress Address, string Adapter, string Problem) FindAddress()
    {
        IPAddress routed = null;
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("1.1.1.1", 53);
            routed = (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch { }

        var cats = NetworkCategories();
        var subnets = new List<Subnet>();
        (IPAddress, string)? chosen = null, fallback = null;
        bool routedIsPublic = false;
        string publicNet = null;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp) continue;
                var props = nic.GetIPProperties();
                int index = -1;
                try { index = props.GetIPv4Properties()?.Index ?? -1; } catch { }
                bool virtualNic = LooksVirtual(nic);
                bool privateNet = cats != null && cats.TryGetValue(index, out int cat) && cat is 1 or 2;
                bool hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var unicast in props.UnicastAddresses)
                {
                    var a = unicast.Address;
                    if (a.AddressFamily != AddressFamily.InterNetwork) continue;
                    bool isRouted = routed != null && a.Equals(routed);
                    if (isRouted && !IsHomeAddress(a) && !virtualNic) routedIsPublic = true;
                    if (!IsHomeAddress(a) || virtualNic) continue;
                    if (!privateNet) { publicNet ??= nic.Name; continue; }
                    int prefix = unicast.PrefixLength;
                    if (prefix is < 8 or > 32) continue;
                    subnets.Add(MakeSubnet(a, prefix));
                    if (isRouted) chosen ??= (a, Describe(nic));
                    if (hasGateway && fallback == null) fallback = (a, Describe(nic));
                }
            }
        }
        catch { }
        Volatile.Write(ref trusted, subnets.Distinct().ToArray());

        if (chosen != null) return (chosen.Value.Item1, chosen.Value.Item2, "");
        // Интернет идёт через VPN или его нет вовсе: берём обычное подключение к роутеру.
        if (fallback != null) return (fallback.Value.Item1, fallback.Value.Item2, "");
        if (publicNet != null && cats != null)
            return (null, "", $"Windows считает сеть «{publicNet}» общедоступной (как в кафе или гостинице) — в такой сети панель не включается, её увидели бы чужие. "
                              + "Если это ваша домашняя сеть, отметьте её как частную: «Параметры» → «Сеть и Интернет» → свойства подключения → «Частная сеть».");
        if (publicNet != null)
            return (null, "", "Windows не сообщила, частная ли это сеть, — на всякий случай панель не включается. Программа попробует снова через минуту.");
        if (routedIsPublic)
            return (null, "", "Компьютер подключён к интернету напрямую, без роутера. Так панель не включается: её было бы видно из интернета.");
        return (null, "", "Компьютер не подключён к домашней сети. Подключите его к роутеру кабелем или по Wi-Fi — панель включится сама.");
    }

    /// <summary>
    /// Адрес по-прежнему принадлежит этому компьютеру. Когда компьютер подключён к роутеру и кабелем, и по Wi-Fi,
    /// Windows то и дело меняет, через что ходит в интернет; адрес в значке на телефоне при этом должен оставаться прежним.
    /// </summary>
    private static bool StillMine(IPAddress a)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Any(u => u.Address.Equals(a));
        }
        catch { return false; }
    }

    private static string Describe(NetworkInterface nic) =>
        (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "кабель") + " · " + nic.Description;

    // ───────────── Сервер ─────────────

    private sealed class Instance : IDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource stop = new();
        private readonly string key;
        private readonly Func<LiveData> live;
        private readonly Func<double> vramTotal;
        private readonly Dictionary<string, DateTime> clients = new();
        private readonly Dictionary<IPAddress, int> perAddress = new();     // открытые соединения по адресам (под замком perAddress, с active)
        private readonly double[] history = new double[HistorySize];
        private readonly Timer sampler;
        private int historyCount, active;
        private string historyGame = "";
        private DateTime lastRequest;

        public IPAddress Address { get; }
        public int Port { get; }
        public string Url => $"http://{Address}:{Port}/{key}/";
        public string Key => key;
        private readonly byte[] pageBytes;

        private Instance(TcpListener listener, IPAddress address, int port, string key, Func<LiveData> live, Func<double> vramTotal)
        {
            this.listener = listener; Address = address; Port = port; this.key = key; this.live = live; this.vramTotal = vramTotal;
            pageBytes = Encoding.UTF8.GetBytes(PageHtml.Replace("{K}", key));
            sampler = new Timer(_ => Sample(), null, 0, 1500);
            _ = Task.Run(AcceptLoop);
        }

        /// <summary>Занять первый свободный порт начиная с заданного. port = 0 — любой свободный (для самопроверки).</summary>
        public static Instance TryStart(IPAddress address, int port, int tries, string key, Func<LiveData> live, Func<double> vramTotal, out string problem)
        {
            problem = "";
            for (int i = 0; i < tries; i++)
            {
                // Слушаем все адреса компьютера: при кабеле и Wi-Fi в одной сети значок на телефоне открывается по любому из них.
                // Кого пускать, решает AcceptLoop: только устройства из подсетей частных сетей (общедоступные, VPN и интернет — нет).
                var l = new TcpListener(IPAddress.IsLoopback(address) ? address : IPAddress.Any, port == 0 ? 0 : port + i);
                try
                {
                    l.Server.ExclusiveAddressUse = true;
                    l.Start(16);
                    return new Instance(l, address, ((IPEndPoint)l.LocalEndpoint).Port, key, live, vramTotal);
                }
                catch (SocketException) { try { l.Stop(); } catch { } }
            }
            problem = $"Панель не запустилась: порты с {port} по {port + tries - 1} заняты другими программами.";
            return null;
        }

        public (int Devices, DateTime Last) Clients()
        {
            lock (clients)
            {
                var now = DateTime.UtcNow;
                foreach (var gone in clients.Where(c => (now - c.Value).TotalSeconds > 60).Select(c => c.Key).ToList()) clients.Remove(gone);
                return (clients.Count(c => (now - c.Value).TotalSeconds < 6), lastRequest);
            }
        }

        // Ряд FPS копится на компьютере: страница, открытая посреди игры, сразу показывает последние две минуты.
        private void Sample()
        {
            try
            {
                var d = live();
                lock (history)
                {
                    if (d == null || !d.Recording) { historyCount = 0; historyGame = ""; return; }
                    if (d.Game != historyGame) { historyCount = 0; historyGame = d.Game ?? ""; }
                    if (historyCount == history.Length) Array.Copy(history, 1, history, 0, --historyCount);
                    history[historyCount++] = d.Fps;
                }
            }
            catch { }
        }

        private async Task AcceptLoop()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop.Token); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch { if (stop.IsCancellationRequested) break; await Task.Delay(50); continue; }

                // Только частная домашняя сеть: подключения из общедоступных и виртуальных сетей (кафе, VPN, ZeroTier, Radmin VPN)
                // и из интернета закрываются молча, ещё до чтения запроса. С одного адреса — не больше двух соединений сразу.
                var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                if (remote != null && remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
                bool admit = false;
                if (ClientAllowed(remote))
                {
                    lock (perAddress)
                    {
                        perAddress.TryGetValue(remote, out int mine);
                        if (active < MaxConnections && mine < MaxPerAddress)
                        {
                            active++;
                            perAddress[remote] = mine + 1;
                            admit = true;
                        }
                    }
                }
                if (!admit)
                {
                    try { client.Client.LingerState = new LingerOption(true, 0); } catch { }
                    try { client.Dispose(); } catch { }
                    continue;
                }
                _ = Task.Run(() => Serve(client, remote));
            }
        }

        private void Release(IPAddress remote)
        {
            lock (perAddress)
            {
                active--;
                if (perAddress.TryGetValue(remote, out int n) && n > 1) perAddress[remote] = n - 1;
                else perAddress.Remove(remote);
            }
        }

        private async Task Serve(TcpClient client, IPAddress remote)
        {
            try
            {
                using var _ = client;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(IoTimeoutMs);
                client.NoDelay = true;
                var stream = client.GetStream();

                // Читаем только заголовок, не больше 4 КБ и не дольше 1,5 секунды: тело запросов нам не нужно,
                // а медленный «запрос по байту» не должен держать соединение.
                using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                headerTimeout.CancelAfter(HeaderTimeoutMs);
                var buffer = new byte[MaxRequestBytes];
                int length = 0, end = -1;
                while (end < 0 && length < buffer.Length)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), headerTimeout.Token);
                    if (read == 0) return;
                    length += read;
                    end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
                }
                if (end < 0) { await Send(stream, 431, "Request Header Fields Too Large", "text/plain; charset=utf-8", "Запрос слишком длинный."u8.ToArray(), false, timeout.Token); return; }

                int lineEnd = buffer.AsSpan(0, length).IndexOf("\r\n"u8);
                string[] parts = Encoding.ASCII.GetString(buffer, 0, lineEnd).Split(' ');
                if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
                { await Send(stream, 400, "Bad Request", "text/plain; charset=utf-8", "Непонятный запрос."u8.ToArray(), false, timeout.Token); return; }

                bool head = parts[0] == "HEAD";
                if (parts[0] != "GET" && !head)
                { await Send(stream, 405, "Method Not Allowed", "text/plain; charset=utf-8", "Страница только для чтения."u8.ToArray(), false, timeout.Token, "Allow: GET, HEAD\r\n"); return; }

                string path = parts[1];
                int query = path.IndexOf('?');
                if (query >= 0) path = path[..query];

                // Адрес сверяется целиком и за постоянное время; путь никогда не превращается в имя файла.
                bool page = Same(path, "/" + key + "/") || Same(path, "/" + key);
                bool data = Same(path, "/" + key + "/d");
                bool icon = Same(path, "/" + key + "/icon.png");
                bool manifest = Same(path, "/" + key + "/manifest.json");
                bool profile = Same(path, "/" + key + "/pulse.mobileconfig");
                if (!page && !data && !icon && !manifest && !profile)
                {
                    await Task.Delay(300, timeout.Token);        // подбор ключа перебором становится бессмысленно долгим
                    await Send(stream, 404, "Not Found", "text/plain; charset=utf-8", "Такой страницы нет."u8.ToArray(), head, timeout.Token);
                    return;
                }

                string who = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
                lock (clients) { clients[who] = DateTime.UtcNow; lastRequest = DateTime.Now; }

                if (page) await Send(stream, 200, "OK", "text/html; charset=utf-8", pageBytes, head, timeout.Token,
                    "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; img-src 'self'; manifest-src 'self'; base-uri 'none'; form-action 'none'\r\n");
                else if (icon) await Send(stream, 200, "OK", "image/png", IconBytes, head, timeout.Token);
                else if (manifest) await Send(stream, 200, "OK", "application/manifest+json; charset=utf-8", Encoding.UTF8.GetBytes(Manifest(key)), head, timeout.Token);
                else if (profile) await Send(stream, 200, "OK", "application/x-apple-aspen-config", Profile(Url, key), head, timeout.Token,
                    "Content-Disposition: attachment; filename=\"PulsePC.mobileconfig\"\r\n");
                else await Send(stream, 200, "OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(Json()), head, timeout.Token);
            }
            catch { }
            finally { Release(remote); }
        }

        private static bool Same(string a, string b)
        {
            byte[] x = Encoding.UTF8.GetBytes(a), y = Encoding.UTF8.GetBytes(b);
            return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
        }

        private static async Task Send(NetworkStream stream, int code, string reason, string type, byte[] body, bool headOnly, CancellationToken ct, string extra = "")
        {
            string header = $"HTTP/1.1 {code} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n" +
                            "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\n" + extra + "\r\n";
            byte[] head = Encoding.ASCII.GetBytes(header);
            await stream.WriteAsync(head, ct);
            if (!headOnly) await stream.WriteAsync(body, ct);
            await stream.FlushAsync(ct);
            try { stream.Socket.Shutdown(SocketShutdown.Send); } catch { }
        }

        /// <summary>Показания одной строкой JSON. Чего нет (NaN) — null: страница покажет прочерк.</summary>
        private string Json()
        {
            var d = live() ?? new LiveData();
            double total = 0;
            try { total = vramTotal?.Invoke() ?? 0; } catch { }
            var sb = new StringBuilder(1024);
            sb.Append("{\"rec\":").Append(d.Recording ? "true" : "false");
            sb.Append(",\"game\":").Append(JsonSerializer.Serialize(d.Game ?? ""));
            sb.Append(",\"nofps\":").Append(d.Recording && SessionRecorder.SkipsFps(d.Game ?? "") ? "true" : "false");
            sb.Append(",\"sec\":").Append(Num(d.SessionSeconds, 0));
            sb.Append(",\"fps\":").Append(Num(d.Fps, 0));
            sb.Append(",\"cpu\":").Append(Num(d.CpuLoad, 0));
            sb.Append(",\"gpu\":").Append(Num(d.GpuLoad, 0));
            sb.Append(",\"gpuT\":").Append(Num(d.GpuTemp, 0));
            sb.Append(",\"vram\":").Append(Num(d.VramUsedGb, 1));
            sb.Append(",\"vramT\":").Append(total > 0 ? Num(total, 1) : "null");
            sb.Append(",\"ramF\":").Append(Num(d.RamFreeGb, 1));
            sb.Append(",\"ramT\":").Append(Num(d.RamTotalGb, 1));
            sb.Append(",\"ping\":").Append(Num(d.PingMs, 0));
            sb.Append(",\"h\":[");
            lock (history)
                for (int i = 0; i < historyCount; i++) { if (i > 0) sb.Append(','); sb.Append(Num(history[i], 0)); }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string Num(double v, int digits) =>
            double.IsNaN(v) || double.IsInfinity(v) ? "null" : Math.Round(v, digits).ToString(CultureInfo.InvariantCulture);

        public void Dispose()
        {
            try { stop.Cancel(); } catch { }
            try { sampler.Dispose(); } catch { }
            try { listener.Stop(); } catch { }
        }
    }

    // ───────────── Самопроверка ─────────────

    /// <summary>
    /// Только чтение: показывает, какой адрес был бы выбран, и на несколько секунд поднимает отдельный сервер
    /// на 127.0.0.1 (свободный порт, свой ключ, выдуманные показания), чтобы проверить ответы. Настройки не трогает.
    /// </summary>
    public static string SelfTest()
    {
        var lines = new List<string>();
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            lines.Add((ok ? "  да   " : "  НЕТ  ") + name + (detail.Length > 0 ? " — " + detail : ""));
            if (!ok) fails.Add(name);
        }

        var (address, adapter, trouble) = FindAddress();
        lines.Add(address != null ? $"Адрес для панели: {address} ({adapter})" : "Адрес для панели не выбран: " + trouble);
        var nets = Volatile.Read(ref trusted);
        lines.Add("Подсети частных сетей, из которых пускаем: " + (nets.Length == 0 ? "нет" : string.Join(", ", nets)));
        var cats = NetworkCategories();
        lines.Add("Тип сетей по Windows (интерфейс → 0 общедоступная, 1 частная, 2 доменная): "
                  + (cats == null ? "не ответила" : string.Join(", ", cats.Select(c => $"{c.Key} → {c.Value}"))));
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                string ips = string.Join(", ", nic.GetIPProperties().UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork).Select(u => u.Address.ToString()));
                if (ips.Length == 0) continue;
                lines.Add($"  подключение: {nic.Description} [{ips}]{(LooksVirtual(nic) ? " — виртуальное, не берём" : "")}");
            }
            bool busy = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == DefaultPort);
            lines.Add($"Порт {DefaultPort}: {(busy ? "занят, будет взят следующий свободный" : "свободен")}");
        }
        catch (Exception ex) { lines.Add("Список подключений не прочитан: " + ex.Message); }

        lines.Add("Сервер на 127.0.0.1:");
        const string key = "selftest";
        var fake = new LiveData { Recording = true, Game = "Проверка \"<игра>\"", SessionSeconds = 754, Fps = 143.6, CpuLoad = 37.2, GpuLoad = 91, VramUsedGb = 5.44, RamFreeGb = 3.14, RamTotalGb = 15.8 };
        Instance server = null;
        try
        {
            server = Instance.TryStart(IPAddress.Loopback, 0, 1, key, () => fake, () => 8, out string problem);
            Check("сервер поднялся", server != null, server != null ? "порт " + server.Port : problem);
            if (server != null)
            {
                int port = server.Port;
                string Ask(string request, int waitMs = 3000) => Raw(port, Encoding.ASCII.GetBytes(request), waitMs);
                static int Code(string response) => response.Length >= 12 && int.TryParse(response.AsSpan(9, 3), out int c) ? c : 0;
                static string Body(string response) { int i = response.IndexOf("\r\n\r\n", StringComparison.Ordinal); return i < 0 ? "" : response[(i + 4)..]; }

                string page = Ask($"GET /{key}/ HTTP/1.1\r\nHost: x\r\n\r\n");
                Check("страница по ключу", Code(page) == 200 && Body(page).StartsWith("<!doctype html>") && page.Contains("Content-Security-Policy:"), $"{Body(page).Length} знаков");
                Check("страница без внешних ресурсов", !Body(page).Contains("src=\"http") && !Body(page).Contains("href=\"http") && !Body(page).Contains("url(") && !Body(page).Contains("@import"));
                Check("страница не упоминает температуру процессора", !Body(page).Contains("cpuT"));

                Thread.Sleep(200);          // первый замер для ряда FPS
                string json = Ask($"GET /{key}/d HTTP/1.1\r\nHost: x\r\n\r\n");
                bool jsonOk = false; string jsonNote = "";
                try
                {
                    using var doc = JsonDocument.Parse(Body(json));
                    var r = doc.RootElement;
                    jsonOk = Code(json) == 200 && r.GetProperty("rec").GetBoolean() && r.GetProperty("game").GetString() == fake.Game
                             && r.GetProperty("fps").GetDouble() == 144 && r.GetProperty("vram").GetDouble() == 5.4 && r.GetProperty("vramT").GetDouble() == 8
                             && r.GetProperty("gpuT").ValueKind == JsonValueKind.Null && r.GetProperty("ping").ValueKind == JsonValueKind.Null
                             && r.GetProperty("h").GetArrayLength() >= 1;
                    jsonNote = Body(json);
                }
                catch (Exception ex) { jsonNote = ex.Message; }
                Check("показания в JSON, пустые — null", jsonOk, jsonNote);

                Check("без ключа — 404", Code(Ask("GET / HTTP/1.1\r\nHost: x\r\n\r\n")) == 404);
                Check("чужой ключ — 404", Code(Ask("GET /abcdef/ HTTP/1.1\r\nHost: x\r\n\r\n")) == 404 && Code(Ask("GET /abcdef/d HTTP/1.1\r\nHost: x\r\n\r\n")) == 404);
                Check("путь к файлам — 404", Code(Ask($"GET /{key}/../../Windows/win.ini HTTP/1.1\r\nHost: x\r\n\r\n")) == 404
                                             && Code(Ask($"GET /{key}/d/..%2f..%2fsettings.json HTTP/1.1\r\nHost: x\r\n\r\n")) == 404);
                Check("запись запрещена — 405", Code(Ask($"POST /{key}/d HTTP/1.1\r\nHost: x\r\nContent-Length: 0\r\n\r\n")) == 405);
                Check("длинный запрос — 431", Code(Ask($"GET /{key}/ HTTP/1.1\r\nHost: x\r\nX-Pad: {new string('a', 6000)}\r\n\r\n")) == 431);
                Check("мусор вместо запроса — 400", Code(Ask("\x16\x03\x01 мусор\r\n\r\n")) == 400);

                var watch = System.Diagnostics.Stopwatch.StartNew();
                string slow = Ask($"GET /{key}/ HTTP/1.1\r\nHost:", HeaderTimeoutMs + 3000);      // запрос оборван на полуслове
                Check("незаконченный заголовок обрывается за 1,5 с", slow.Length == 0 && watch.ElapsedMilliseconds < HeaderTimeoutMs + 1200, $"{watch.ElapsedMilliseconds} мс");

                // С одного адреса — не больше двух соединений сразу; после их закрытия сервер снова отвечает.
                var idle = new List<TcpClient>();
                try
                {
                    for (int i = 0; i < MaxPerAddress; i++) { var c = new TcpClient(); c.Connect(IPAddress.Loopback, port); idle.Add(c); }
                    Thread.Sleep(300);
                    string extra = Ask($"GET /{key}/d HTTP/1.1\r\nHost: x\r\n\r\n", 1000);
                    Check("третье соединение с одного адреса не обслуживается", Code(extra) != 200);
                }
                finally { foreach (var c in idle) c.Dispose(); }
                Thread.Sleep(300);
                Check("после этого сервер отвечает", Code(Ask($"GET /{key}/d HTTP/1.1\r\nHost: x\r\n\r\n")) == 200);

                var (devices, last) = server.Clients();
                Check("счётчик устройств", devices == 1 && (DateTime.Now - last).TotalSeconds < 10, $"устройств: {devices}");

                string url = server.Url;
                var qr = QrCode.Encode(url);
                Check("QR-код адреса собирается и разбирается обратно", qr != null && QrCode.DecodeForTest(qr) == url, qr != null ? $"{url} → {qr.GetLength(0)}×{qr.GetLength(0)}" : url);
                Check("QR-код самого длинного адреса", QrCode.DecodeForTest(QrCode.Encode("http://192.168.255.255:65000/" + new string('z', 32) + "/")) != null);
            }
        }
        catch (Exception ex) { Check("самопроверка сервера", false, ex.Message); }
        finally { server?.Dispose(); }

        Check("после закрытия порт свободен", server == null || Raw(server.Port, "GET / HTTP/1.1\r\n\r\n"u8.ToArray(), 500).Length == 0);
        Check("ключ: двенадцать знаков без похожих", ValidKey(NewKey()) && NewKey().Length == KeyLength && NewKey() != NewKey() && NewKey().All(c => KeyAlphabet.Contains(c)));
        Check("шестизначный ключ прежних версий не принимается (будет заменён)", !ValidKey("abcdef") && !ValidKey("abcdefghjkm") && !ValidKey("ABCDEFGHJKMN") && ValidKey("abcdefghjkmn"));
        Check("подсети: 192.168.1.77 в 192.168.1.5/24, 192.168.2.1 — нет, 10.9.8.7 в 10.0.0.1/8",
            InSubnet(IPAddress.Parse("192.168.1.77"), IPAddress.Parse("192.168.1.5"), 24) && !InSubnet(IPAddress.Parse("192.168.2.1"), IPAddress.Parse("192.168.1.5"), 24)
            && InSubnet(IPAddress.Parse("10.9.8.7"), IPAddress.Parse("10.0.0.1"), 8));
        var saved = Volatile.Read(ref trusted);
        try
        {
            Volatile.Write(ref trusted, new[] { MakeSubnet(IPAddress.Parse("192.168.1.5"), 24) });
            Check("пускаем: этот компьютер и частная подсеть; не пускаем: соседнюю подсеть, VPN 26.x, интернет",
                ClientAllowed(IPAddress.Loopback) && ClientAllowed(IPAddress.Parse("192.168.1.20")) && ClientAllowed(IPAddress.Parse("192.168.1.20").MapToIPv6())
                && !ClientAllowed(IPAddress.Parse("192.168.7.20")) && !ClientAllowed(IPAddress.Parse("10.0.0.5")) && !ClientAllowed(IPAddress.Parse("26.1.2.3")) && !ClientAllowed(IPAddress.Parse("8.8.8.8")));
            Volatile.Write(ref trusted, Array.Empty<Subnet>());
            Check("в общедоступной сети не пускаем никого, кроме этого компьютера", !ClientAllowed(IPAddress.Parse("192.168.1.20")) && ClientAllowed(IPAddress.Loopback));
        }
        finally { Volatile.Write(ref trusted, saved); }
        Check("домашние адреса", IsHomeAddress(IPAddress.Parse("192.168.1.5")) && IsHomeAddress(IPAddress.Parse("10.1.2.3")) && IsHomeAddress(IPAddress.Parse("172.20.0.1"))
                                 && !IsHomeAddress(IPAddress.Parse("26.10.20.30")) && !IsHomeAddress(IPAddress.Parse("8.8.8.8")) && !IsHomeAddress(IPAddress.Parse("172.32.0.1")) && !IsHomeAddress(IPAddress.Loopback));

        lines.Insert(0, fails.Count == 0 ? "OK" : "FAIL: " + string.Join("; ", fails));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Сырой запрос к 127.0.0.1 и весь ответ текстом (пусто — соединение не удалось или закрыто без ответа).</summary>
    private static string Raw(int port, byte[] request, int waitMs)
    {
        try
        {
            using var client = new TcpClient();
            if (!client.ConnectAsync(IPAddress.Loopback, port).Wait(1000)) return "";
            client.ReceiveTimeout = waitMs; client.SendTimeout = 2000;
            var stream = client.GetStream();
            stream.Write(request);
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) memory.Write(buffer, 0, read);
            return Encoding.UTF8.GetString(memory.ToArray());
        }
        catch { return ""; }
    }

    // ───────────── Страница ─────────────

    // ───────────── Значок и установка на iPhone ─────────────
    // Своё приложение для iPhone без Mac и подписи Apple не собрать, поэтому панель ставится как приложение «на экран Домой»:
    // значок на рабочем столе телефона, открывается во весь экран без строки адреса Safari.
    // Профиль .mobileconfig содержит только этот значок (Web Clip) — никаких других настроек телефона.

    private static readonly byte[] IconBytes = LoadIcon();

    private static byte[] LoadIcon()
    {
        try
        {
            using var s = typeof(PhoneServer).Assembly.GetManifestResourceStream("apple-touch-icon.png");
            if (s == null) return Array.Empty<byte>();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return Array.Empty<byte>(); }
    }

    private static string Manifest(string key) =>
        "{\"name\":\"Пульс ПК\",\"short_name\":\"Пульс ПК\",\"start_url\":\"/" + key + "/\",\"scope\":\"/" + key + "/\"," +
        "\"display\":\"standalone\",\"background_color\":\"#F2EEE5\",\"theme_color\":\"#F2EEE5\"," +
        "\"icons\":[{\"src\":\"/" + key + "/icon.png\",\"sizes\":\"180x180\",\"type\":\"image/png\"}]}";

    /// <summary>Постоянный идентификатор из строки: повторная установка профиля заменяет прежний, а не добавляет второй значок.</summary>
    private static string StableUuid(string seed)
    {
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes("PulsePC.phone." + seed));
        h[6] = (byte)((h[6] & 0x0F) | 0x50); h[8] = (byte)((h[8] & 0x3F) | 0x80);
        return new Guid(h.AsSpan(0, 16)).ToString().ToUpperInvariant();
    }

    /// <summary>Профиль для iPhone: один значок «Пульс ПК», открывающий панель во весь экран.</summary>
    public static byte[] Profile(string url, string key)
    {
        static string X(string s) => System.Security.SecurityElement.Escape(s);
        string clip = StableUuid("clip"), root = StableUuid("root");
        string xml = $"""
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>PayloadContent</key>
	<array>
		<dict>
			<key>PayloadType</key><string>com.apple.webClip.managed</string>
			<key>PayloadVersion</key><integer>1</integer>
			<key>PayloadIdentifier</key><string>ru.pulsepc.phone.webclip</string>
			<key>PayloadUUID</key><string>{clip}</string>
			<key>PayloadDisplayName</key><string>Пульс ПК</string>
			<key>PayloadDescription</key><string>Значок панели «Пульс ПК» на экране «Домой».</string>
			<key>Label</key><string>Пульс ПК</string>
			<key>URL</key><string>{X(url)}</string>
			<key>Icon</key><data>{Convert.ToBase64String(IconBytes)}</data>
			<key>FullScreen</key><true/>
			<key>IgnoreManifestScope</key><true/>
			<key>IsRemovable</key><true/>
			<key>Precomposed</key><true/>
		</dict>
	</array>
	<key>PayloadType</key><string>Configuration</string>
	<key>PayloadVersion</key><integer>1</integer>
	<key>PayloadIdentifier</key><string>ru.pulsepc.phone</string>
	<key>PayloadUUID</key><string>{root}</string>
	<key>PayloadDisplayName</key><string>Пульс ПК — панель на телефоне</string>
	<key>PayloadDescription</key><string>Добавляет значок «Пульс ПК» на экран «Домой». Он открывает показания компьютера во время игры во весь экран. Других настроек телефона профиль не меняет; удалить его можно в «Настройки» → «Основные» → «VPN и управление устройством».</string>
	<key>PayloadOrganization</key><string>Пульс ПК</string>
	<key>PayloadRemovalDisallowed</key><false/>
</dict>
</plist>
""";
        return Encoding.UTF8.GetBytes(xml);
    }

    /// <summary>Профиль для текущего адреса панели (null — панель не запущена).</summary>
    public static byte[] CurrentProfile()
    {
        lock (Gate)
        {
            if (current == null) return null;
            return Profile(current.Url, current.Key);
        }
    }

    /// <summary>Вся страница одним файлом: без внешних шрифтов, картинок и библиотек.</summary>
    private const string PageHtml = """
<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="theme-color" content="#F2EEE5">
<meta name="format-detection" content="telephone=no">
<meta name="apple-mobile-web-app-capable" content="yes">
<meta name="mobile-web-app-capable" content="yes">
<meta name="apple-mobile-web-app-title" content="Пульс ПК">
<meta name="apple-mobile-web-app-status-bar-style" content="default">
<link rel="apple-touch-icon" href="/{K}/icon.png">
<link rel="icon" type="image/png" href="/{K}/icon.png">
<link rel="manifest" href="/{K}/manifest.json">
<title>Пульс ПК</title>
<style>
:root {
  --paper: #F2EEE5; --ink: #1A1917; --ink2: #45423C; --ink3: #6F6A60; --ink4: #A8A194;
  --line: #CFC7B8; --rule: #A39B8C; --grid: #E4DED2; --red: #C23B22; --warn: #95580F;
  --num: Bahnschrift, "DIN Alternate", "DIN Condensed", "Avenir Next Condensed", "Roboto Condensed", "Arial Narrow", sans-serif-condensed, sans-serif;
  --text: -apple-system, "Segoe UI", Roboto, "Noto Sans", "Helvetica Neue", Arial, sans-serif;
}
* { box-sizing: border-box; margin: 0; padding: 0; }
html { -webkit-text-size-adjust: 100%; text-size-adjust: 100%; background: var(--paper); }
body {
  background: var(--paper); color: var(--ink); font: 15px/1.35 var(--text);
  padding: 14px 18px;
  padding: max(14px, env(safe-area-inset-top)) max(18px, env(safe-area-inset-right)) max(14px, env(safe-area-inset-bottom)) max(18px, env(safe-area-inset-left));
  -webkit-tap-highlight-color: transparent;
}
.sheet { max-width: 1000px; margin: 0 auto; display: grid; grid-template-columns: minmax(0, 1fr); column-gap: 32px; }
.head { grid-column: 1 / -1; display: flex; justify-content: space-between; align-items: center; border-bottom: 1px solid var(--ink); padding-bottom: 8px; }
.kicker { font: 600 11px/14px var(--num); letter-spacing: .14em; text-transform: uppercase; color: var(--ink3); white-space: nowrap; }
.link { display: flex; align-items: center; gap: 7px; }
.dot { width: 7px; height: 7px; background: var(--red); }
.off .dot { background: none; border: 1px solid var(--ink4); }
.lost { grid-column: 1 / -1; display: none; border-bottom: 1px solid var(--line); padding: 12px 0; color: var(--ink); }
.lost b { display: block; font-weight: 600; font-size: 16px; }
.lost span { display: block; color: var(--ink2); font-size: 13.5px; margin-top: 3px; }
.off .lost { display: block; }

.hero { padding: 18px 0 4px; min-height: 148px; }
.fps { display: flex; align-items: baseline; gap: 10px; font-family: var(--num); font-stretch: condensed; }
.fps .n { font-weight: 700; font-size: 124px; line-height: .9; letter-spacing: -.01em; font-variant-numeric: tabular-nums; }
.fps .u { font-weight: 600; font-size: 15px; color: var(--ink3); letter-spacing: .04em; }
.idle { display: none; font: 600 30px/1.15 var(--text); padding: 26px 0 6px; }
.rest .fps { display: none; }
.rest .idle { display: block; }
.game { font-weight: 600; font-size: 19px; line-height: 1.25; margin-top: 10px; overflow-wrap: anywhere; }
.sub { color: var(--ink3); font-size: 13.5px; margin-top: 2px; min-height: 18px; }

.plot { padding: 14px 0 6px; }
.plot canvas { display: block; width: 100%; height: 120px; margin-top: 8px; }
.rest .plot { display: none; }

.rows { border-top: 1px solid var(--ink); margin-top: 12px; }
.row { padding: 11px 0 12px; border-bottom: 1px solid var(--line); }
.row:last-child { border-bottom-color: var(--ink); }
.top { display: flex; justify-content: space-between; align-items: baseline; gap: 12px; }
.name { font-weight: 600; font-size: 14.5px; color: var(--ink2); }
.val { font: 600 23px/1 var(--num); font-stretch: condensed; font-variant-numeric: tabular-nums; white-space: nowrap; }
.val small { font-size: 13.5px; font-weight: 600; color: var(--ink3); }
.bar { position: relative; height: 4px; margin-top: 9px; }
.bar::before { content: ""; position: absolute; left: 0; right: 0; top: 2px; height: 1px; background: var(--line); }
.bar i { position: absolute; left: 0; top: 0; height: 4px; width: 0; background: var(--ink); }
.warn .val { color: var(--warn); }
.warn .bar i { background: repeating-linear-gradient(135deg, var(--warn) 0 1.5px, transparent 1.5px 4px); outline: 1px solid var(--warn); outline-offset: -1px; }
.warn .name::before { content: ""; display: inline-block; width: 8px; height: 8px; margin-right: 7px; border: 1px solid var(--warn);
  background: repeating-linear-gradient(135deg, var(--warn) 0 1px, transparent 1px 3px); }
.note { color: var(--ink3); font-size: 12px; padding: 12px 0 4px; }
.off .hero, .off .plot, .off .rows { opacity: .38; }

@media (orientation: landscape) and (min-width: 560px) {
  .sheet { grid-template-columns: minmax(0, 1.05fr) minmax(0, 1fr); }
  .left { grid-column: 1; } .right { grid-column: 2; }
  .hero { padding-top: 12px; min-height: 0; }
  .fps .n { font-size: 104px; }
  .idle { font-size: 26px; padding-top: 18px; }
  .plot canvas { height: 96px; }
  .rows { margin-top: 12px; }
  .row { padding: 8px 0 9px; }
  .val { font-size: 21px; }
}
@media (max-width: 340px) { .fps .n { font-size: 100px; } .val { font-size: 21px; } }
</style>
</head>
<body>
<div class="sheet rest" id="sheet">
  <div class="head">
    <span class="kicker">Пульс ПК · приборы</span>
    <span class="kicker link"><span class="dot"></span><span id="link">подключаюсь</span></span>
  </div>
  <div class="lost"><b id="lostTitle">Нет связи с компьютером</b><span id="lostText">Проверьте, что телефон в той же сети Wi-Fi, что и компьютер, и что программа «Пульс ПК» запущена.</span></div>

  <div class="left">
    <div class="hero">
      <div class="fps"><span class="n" id="fps">—</span><span class="u">FPS</span></div>
      <div class="idle" id="idle">подключаюсь…</div>
      <div class="game" id="game"></div>
      <div class="sub" id="sub"></div>
    </div>
    <div class="plot">
      <div class="kicker">FPS за последние две минуты</div>
      <canvas id="chart"></canvas>
    </div>
  </div>

  <div class="right">
    <div class="rows">
      <div class="row" id="cpu"><div class="top"><span class="name">Процессор</span><span class="val">—</span></div><div class="bar"><i></i></div></div>
      <div class="row" id="gpu"><div class="top"><span class="name">Видеокарта</span><span class="val">—</span></div><div class="bar"><i></i></div></div>
      <div class="row" id="vram"><div class="top"><span class="name">Видеопамять</span><span class="val">—</span></div><div class="bar"><i></i></div></div>
      <div class="row" id="ram"><div class="top"><span class="name">Память</span><span class="val">—</span></div><div class="bar"><i></i></div></div>
      <div class="row" id="ping"><div class="top"><span class="name">Пинг</span><span class="val">—</span></div><div class="bar"><i></i></div></div>
    </div>
    <div class="note">Страница только показывает числа с компьютера и обновляется сама.</div>
  </div>
</div>
<script>
(function () {
  'use strict';
  var $ = function (id) { return document.getElementById(id); };
  var sheet = $('sheet'), chart = $('chart');
  var url = location.pathname.replace(/\/+$/, '') + '/d';
  var data = null, fails = 0, gone = false, timer = 0;

  function has(v) { return typeof v === 'number' && isFinite(v); }
  function num(v, digits) { return has(v) ? v.toFixed(digits || 0).replace('.', ',') : '—'; }
  function esc(s) { return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;'); }
  function span(sec) {
    if (!has(sec)) return '';
    var m = Math.floor(sec / 60), h = Math.floor(m / 60);
    if (h > 0) return h + ' ч ' + (m % 60) + ' мин';
    if (m > 0) return m + ' мин';
    return Math.max(0, Math.floor(sec)) + ' с';
  }
  // Строка-полоса: текст значения, доля заполнения 0..1 (null — без полосы), предупреждение.
  function row(id, html, part, warn) {
    var el = $(id);
    el.querySelector('.val').innerHTML = html;
    el.querySelector('i').style.width = has(part) ? Math.max(0, Math.min(1, part)) * 100 + '%' : '0';
    el.className = warn ? 'row warn' : 'row';
  }
  function unit(text) { return '<small> ' + text + '</small>'; }

  function render(d) {
    var h = d.h || [], any = false, i;
    for (i = 0; i < h.length; i++) if (has(h[i])) { any = true; break; }
    // Крупное число и график — только когда кадры есть; иначе короткая надпись на их месте.
    sheet.classList.toggle('rest', !d.rec || (!has(d.fps) && !any));
    $('idle').textContent = !d.rec ? 'игра не запущена' : d.nofps ? 'FPS не считается' : 'считаю FPS…';
    $('fps').textContent = num(d.fps);
    $('game').textContent = d.rec ? d.game : '';
    $('sub').textContent = !d.rec ? 'Числа ниже — как компьютер работает сейчас.'
      : d.nofps ? 'идёт ' + span(d.sec) + ' · в этой игре строгий античит, поэтому FPS не считается'
      : 'идёт ' + span(d.sec);

    row('cpu', num(d.cpu) + unit('%'), has(d.cpu) ? d.cpu / 100 : null, false);
    row('gpu', num(d.gpu) + unit('%') + (has(d.gpuT) ? '<small> ·</small> ' + num(d.gpuT) + unit('°C') : ''),
      has(d.gpu) ? d.gpu / 100 : null, has(d.gpuT) && d.gpuT >= 85);
    var full = has(d.vram) && has(d.vramT) && d.vramT > 0 ? d.vram / d.vramT : null;
    row('vram', num(d.vram, 1) + (has(d.vramT) && has(d.vram) ? unit('из ' + num(d.vramT, d.vramT % 1 ? 1 : 0) + ' ГБ') : unit('ГБ')), full, has(full) && full >= 0.93);
    var free = has(d.ramF) && has(d.ramT) && d.ramT > 0 ? d.ramF / d.ramT : null;
    row('ram', (has(d.ramF) ? '<small>свободно </small>' : '') + num(d.ramF, 1) + (has(d.ramT) && has(d.ramF) ? unit('из ' + num(d.ramT, 1) + ' ГБ') : unit('ГБ')),
      has(free) ? 1 - free : null, has(d.ramF) && d.ramF < 1);
    row('ping', num(d.ping) + unit('мс'), has(d.ping) ? d.ping / 150 : null, has(d.ping) && d.ping >= 90);
    draw(h);
  }

  // График: клетка, оси слева и снизу, линия тушью, пунктир среднего, точка на последнем значении.
  function draw(h) {
    var ratio = window.devicePixelRatio || 1, w = chart.clientWidth, ht = chart.clientHeight;
    if (!w || !ht) return;
    if (chart.width !== Math.round(w * ratio) || chart.height !== Math.round(ht * ratio)) { chart.width = Math.round(w * ratio); chart.height = Math.round(ht * ratio); }
    var g = chart.getContext('2d');
    g.setTransform(ratio, 0, 0, ratio, 0, 0);
    g.clearRect(0, 0, w, ht);
    var css = getComputedStyle(document.documentElement);
    var c = function (name) { return css.getPropertyValue(name).trim(); };
    var left = 30, right = w - 34, top = 6, bottom = ht - 1, pw = right - left, ph = bottom - top;
    var peak = 0, sum = 0, n = 0, i;
    for (i = 0; i < h.length; i++) if (has(h[i])) { peak = Math.max(peak, h[i]); sum += h[i]; n++; }
    var steps = [30, 60, 90, 120, 180, 240, 360, 480, 720, 1000], max = 1000;
    for (i = 0; i < steps.length; i++) if (peak * 1.08 <= steps[i]) { max = steps[i]; break; }

    g.lineWidth = 1;
    for (i = 1; i <= 4; i++) {
      var gy = Math.round(bottom - ph * i / 4) + .5;
      g.strokeStyle = c('--grid'); g.beginPath(); g.moveTo(left, gy); g.lineTo(right, gy); g.stroke();
    }
    for (i = 1; i <= 8; i++) {
      var gx = Math.round(left + pw * i / 8) + .5;
      g.strokeStyle = c('--grid'); g.beginPath(); g.moveTo(gx, top); g.lineTo(gx, bottom); g.stroke();
    }
    g.strokeStyle = c('--rule'); g.beginPath(); g.moveTo(left + .5, top); g.lineTo(left + .5, bottom + .5); g.lineTo(right, bottom + .5); g.stroke();
    g.fillStyle = c('--ink3'); g.font = '11px ' + c('--num'); g.textBaseline = 'middle'; g.textAlign = 'right';
    g.fillText(String(max), left - 6, top + 5); g.fillText(String(max / 2), left - 6, top + ph / 2); g.fillText('0', left - 6, bottom - 4);
    if (!n) return;

    var cap = 80, stepX = pw / (cap - 1);
    var X = function (k) { return right - (h.length - 1 - k) * stepX; };
    var Y = function (v) { return bottom - ph * Math.min(1, v / max); };
    var avg = sum / n, ay = Math.round(Y(avg)) + .5;
    g.setLineDash([4, 3]); g.strokeStyle = c('--ink3'); g.beginPath(); g.moveTo(left, ay); g.lineTo(right, ay); g.stroke(); g.setLineDash([]);
    g.textAlign = 'left'; g.fillText(num(avg), right + 6, ay);

    g.strokeStyle = c('--ink'); g.lineWidth = 1.5; g.lineJoin = 'round'; g.beginPath();
    var open = false, lx = 0, ly = 0;
    for (i = 0; i < h.length; i++) {
      if (!has(h[i])) { open = false; continue; }
      lx = X(i); ly = Y(h[i]);
      if (open) g.lineTo(lx, ly); else g.moveTo(lx, ly);
      open = true;
    }
    g.stroke();
    g.fillStyle = c('--ink'); g.beginPath(); g.arc(lx, ly, 2.5, 0, 6.2832); g.fill();
  }

  function status() {
    var off = gone || fails >= 2;
    sheet.classList.toggle('off', off);
    $('link').textContent = off ? 'нет связи' : data ? 'на связи' : 'подключаюсь';
    if (!data) $('idle').textContent = off ? 'показаний нет' : 'подключаюсь…';
    $('lostTitle').textContent = gone ? 'Этот адрес больше не действует' : 'Нет связи с компьютером';
    $('lostText').textContent = gone
      ? 'На компьютере сменили ключ. Откройте в «Пульс ПК» вкладку «Система» → «Панель на телефоне» и наведите камеру на новый код.'
      : 'Проверьте, что телефон в той же сети Wi-Fi, что и компьютер, и что программа «Пульс ПК» запущена. Страница подключится сама.';
  }

  function poll() {
    var stopper = window.AbortController ? new AbortController() : null;
    var guard = stopper ? setTimeout(function () { stopper.abort(); }, 4000) : 0;
    fetch(url, { cache: 'no-store', signal: stopper ? stopper.signal : undefined })
      .then(function (r) { gone = r.status === 404; if (!r.ok) throw new Error(String(r.status)); return r.json(); })
      .then(function (d) { fails = 0; data = d; render(d); })
      .catch(function () { fails++; })
      .then(function () { clearTimeout(guard); status(); clearTimeout(timer); if (!document.hidden) timer = setTimeout(poll, 1500); });
  }

  document.addEventListener('visibilitychange', function () { clearTimeout(timer); if (!document.hidden) poll(); });
  window.addEventListener('resize', function () { if (data) draw(data.h || []); });
  poll();
})();
</script>
</body>
</html>
""";
}
