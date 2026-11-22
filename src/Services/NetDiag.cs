using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace PulsePC.Services;

/// <summary>Вывод проверки сети: маркер состояния, заголовок, что это значит и что сделать.</summary>
public sealed record NetNote(Severity Level, string Title, string Text);

/// <summary>
/// Проверки сети по кнопке (вкладка «Сеть»): Wi‑Fi через WLAN API, пинг под нагрузкой (bufferbloat), трассировка с потерями по участкам,
/// скорость DNS. Ничего не меняет в системе: только чтение, пинги, DNS-запросы и короткое скачивание с официального тестового
/// сервера Cloudflare. Обращения — только к шлюзу, 1.1.1.1, 8.8.8.8, 77.88.8.8, текущему DNS, серверу игры и speed.cloudflare.com.
/// </summary>
public static class NetDiag
{
    private const string Nb = " ";
    public const string SpeedHost = "speed.cloudflare.com";
    /// <summary>Число со знаком «минус» (U+2212), как в типографике программы: «−71».</summary>
    public static string Signed(int v) => v < 0 ? "\u2212" + (-v) : v.ToString();
    public static string Ms(double v) => double.IsNaN(v) ? "—" : (v < 10 ? v.ToString("0.0", L.Culture) : v.ToString("0", L.Culture)) + Nb + L.Pick("мс", "ms");

    // ====================================================================================== Wi‑Fi
    public sealed class WifiInfo
    {
        public bool Present, Connected, CarriesInternet, ListDenied;
        public Guid Id;
        public string Adapter = "", Ssid = "", Error = "";
        public int Phy, Quality, Rssi, Channel, RxMbps, TxMbps;
        public double FreqMhz;
        public int SameChannel, Overlapping;   // другие сети на том же и на перекрывающихся каналах (2,4 ГГц)

        /// <summary>Диапазон: 2.4, 5, 6 ГГц; 0 — неизвестен.</summary>
        public double Band => BandOf(FreqMhz, Channel);
    }

    public static double BandOf(double freqMhz, int channel)
    {
        if (freqMhz >= 2400 && freqMhz < 2500) return 2.4;
        if (freqMhz >= 4900 && freqMhz < 5925) return 5;
        if (freqMhz >= 5925 && freqMhz < 7200) return 6;
        if (freqMhz <= 0 && channel is >= 1 and <= 14) return 2.4;
        if (freqMhz <= 0 && channel >= 32 && channel <= 177) return 5;   // по одному номеру канала 6 ГГц не отличить — считаем 5
        return 0;
    }

    public static int ChannelOf(double freqMhz) => freqMhz switch
    {
        2484 => 14,
        >= 2407 and < 2500 => (int)Math.Round((freqMhz - 2407) / 5),
        >= 5925 and < 7200 => (int)Math.Round((freqMhz - 5950) / 5),
        >= 4900 and < 5925 => (int)Math.Round((freqMhz - 5000) / 5),
        _ => 0,
    };

    public static string BandText(double band) => band switch
    {
        2.4 => L.Pick("2,4" + Nb + "ГГц", "2.4" + Nb + "GHz"),
        5 => "5" + Nb + L.Pick("ГГц", "GHz"),
        6 => "6" + Nb + L.Pick("ГГц", "GHz"),
        _ => L.T("неизвестен"),
    };

    public static string Standard(int phy, double band) => phy switch
    {
        4 => "802.11a",
        5 => "802.11b",
        6 => "802.11g",
        7 => "Wi‑Fi 4 (802.11n)",
        8 => "Wi‑Fi 5 (802.11ac)",
        10 => band == 6 ? "Wi‑Fi 6E (802.11ax)" : "Wi‑Fi 6 (802.11ax)",
        11 => "Wi‑Fi 7 (802.11be)",
        _ => L.T("неизвестен"),
    };

    /// <summary>Подключённый Wi‑Fi через WLAN API (не через netsh: его вывод зависит от языка Windows). Только чтение, без поиска сетей.</summary>
    public static WifiInfo ReadWifi()
    {
        var w = new WifiInfo();
        IntPtr handle = IntPtr.Zero, list = IntPtr.Zero;
        try
        {
            uint err = WlanOpenHandle(2, IntPtr.Zero, out _, out handle);
            if (err != 0) { w.Error = err == 1062 ? L.T("служба беспроводной сети не запущена") : L.F("код {0}", err); return w; }
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out list) != 0 || list == IntPtr.Zero) return w;
            int count = Marshal.ReadInt32(list, 0);
            for (int i = 0; i < count; i++)
            {
                IntPtr item = list + 8 + i * 532;
                var id = Marshal.PtrToStructure<Guid>(item);
                string desc = Marshal.PtrToStringUni(item + 16) ?? "";
                int state = Marshal.ReadInt32(item, 528);
                w.Present = true;
                if (w.Adapter == "") w.Adapter = desc;
                if (state != 1) continue;   // wlan_interface_state_connected
                w.Connected = true; w.Id = id; w.Adapter = desc;
                ReadConnection(handle, w);
                break;
            }
        }
        catch (DllNotFoundException) { }
        catch (Exception ex) { w.Error = ex.Message; }
        finally
        {
            if (list != IntPtr.Zero) WlanFreeMemory(list);
            if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
        }
        if (w.Connected)
        {
            try
            {
                var nic = NetMonitor.InternetAdapter(out _);
                w.CarriesInternet = nic != null && Guid.TryParse(nic.Id, out var g) && g == w.Id;
            }
            catch { }
        }
        return w;
    }

    private static void ReadConnection(IntPtr handle, WifiInfo w)
    {
        var id = w.Id;
        byte[] ourBssid = null;
        if (WlanQueryInterface(handle, ref id, 7 /* current_connection */, IntPtr.Zero, out uint size, out IntPtr data, out _) == 0 && data != IntPtr.Zero)
        {
            try
            {
                if (size >= 588)
                {
                    int ssidLen = Math.Clamp(Marshal.ReadInt32(data, 520), 0, 32);
                    var ssid = new byte[ssidLen];
                    Marshal.Copy(data + 524, ssid, 0, ssidLen);
                    w.Ssid = Encoding.UTF8.GetString(ssid);
                    ourBssid = new byte[6];
                    Marshal.Copy(data + 560, ourBssid, 0, 6);
                    w.Phy = Marshal.ReadInt32(data, 568);
                    w.Quality = Marshal.ReadInt32(data, 576);
                    w.RxMbps = Marshal.ReadInt32(data, 580) / 1000;
                    w.TxMbps = Marshal.ReadInt32(data, 584) / 1000;
                }
            }
            finally { WlanFreeMemory(data); }
        }
        if (WlanQueryInterface(handle, ref id, 8 /* channel_number */, IntPtr.Zero, out _, out data, out _) == 0 && data != IntPtr.Zero)
        {
            w.Channel = Marshal.ReadInt32(data);
            WlanFreeMemory(data);
        }
        if (WlanQueryInterface(handle, ref id, 0x10000102 /* rssi */, IntPtr.Zero, out _, out data, out _) == 0 && data != IntPtr.Zero)
        {
            w.Rssi = Marshal.ReadInt32(data);
            WlanFreeMemory(data);
        }

        // Список сетей из последнего поиска Windows (новый поиск не запускается: он даёт скачок пинга).
        uint err = WlanGetNetworkBssList(handle, ref id, IntPtr.Zero, 3 /* any */, false, IntPtr.Zero, out IntPtr bss);
        if (err == 5) { w.ListDenied = true; return; }
        if (err != 0 || bss == IntPtr.Zero) return;
        try
        {
            var all = new List<(byte[] Bssid, double Freq, int Rssi)>();
            int n = Marshal.ReadInt32(bss, 4);
            for (int i = 0; i < n && i < 512; i++)
            {
                IntPtr e = bss + 8 + i * 360;
                var mac = new byte[6];
                Marshal.Copy(e + 40, mac, 0, 6);
                all.Add((mac, Marshal.ReadInt32(e, 92) / 1000.0, Marshal.ReadInt32(e, 56)));
            }
            var mine = ourBssid == null ? default : all.FirstOrDefault(a => a.Bssid.AsSpan().SequenceEqual(ourBssid));
            if (mine.Bssid != null)
            {
                w.FreqMhz = mine.Freq;
                if (w.Rssi == 0) w.Rssi = mine.Rssi;
                if (w.Channel == 0) w.Channel = ChannelOf(mine.Freq);
            }
            (w.SameChannel, w.Overlapping) = Neighbours(ourBssid, w.FreqMhz, w.Channel, all.Select(a => (a.Bssid, a.Freq, a.Rssi)));
        }
        finally { WlanFreeMemory(bss); }
    }

    /// <summary>
    /// Чистая функция: соседние сети на том же канале и на перекрывающихся (2,4 ГГц: ближе 5 каналов). Свои же точки того же роутера
    /// (гостевая сеть: тот же адрес, кроме первого и последнего байта) и совсем слабые (ниже −85 дБм) не считаются.
    /// </summary>
    public static (int Same, int Overlap) Neighbours(byte[] ours, double freq, int channel, IEnumerable<(byte[] Bssid, double Freq, int Rssi)> all)
    {
        double band = BandOf(freq, channel);
        int ch = channel > 0 ? channel : ChannelOf(freq);
        if (band == 0 || ch == 0) return (0, 0);
        int same = 0, overlap = 0;
        foreach (var (b, f, rssi) in all)
        {
            if (b == null || (ours != null && b.AsSpan(1, 4).SequenceEqual(ours.AsSpan(1, 4)))) continue;
            if (rssi < -85 || BandOf(f, 0) != band) continue;
            int c = ChannelOf(f);
            if (c == ch) same++;
            else if (band == 2.4 && Math.Abs(c - ch) < 5) overlap++;
        }
        return (same, overlap);
    }

    /// <summary>Чистая функция: советы по Wi‑Fi.</summary>
    public static List<NetNote> WifiAdvice(WifiInfo w)
    {
        var list = new List<NetNote>();
        if (w == null || !w.Connected) return list;
        if (!w.CarriesInternet)
        {
            list.Add(new NetNote(Severity.Info, L.T("Wi‑Fi подключён, но в интернет компьютер ходит не через него"),
                L.T("Игры идут по другому подключению (обычно по кабелю) — советы по Wi‑Fi к ним не относятся.")));
            return list;
        }
        double band = w.Band;
        if (band == 2.4)
            list.Add(new NetNote(Severity.Warn, L.T("Wi‑Fi на 2,4 ГГц"),
                L.T("На 2,4 ГГц работают почти все соседние сети, Bluetooth и микроволновки — пинг скачет сильнее. Если роутер раздаёт и 5 ГГц (часто это отдельная сеть с «5G» в названии), подключитесь к ней. Для игр ещё лучше кабель.")));
        if (w.Rssi < -75 && w.Rssi != 0 || w.Rssi == 0 && w.Quality > 0 && w.Quality < 40)
            list.Add(new NetNote(Severity.Warn, w.Rssi != 0 ? L.F("Слабый сигнал: {0} дБм", Signed(w.Rssi)) : L.F("Слабый сигнал: {0} %", w.Quality),
                L.T("Пакеты теряются и отправляются повторно — отсюда скачки пинга. Поставьте компьютер ближе к роутеру, уберите преграды (стены, металл, зеркала) или подключитесь кабелем.")));
        else if (w.Rssi < -67 && w.Rssi != 0)
            list.Add(new NetNote(Severity.Info, L.F("Сигнал средний: {0} дБм", Signed(w.Rssi)),
                L.T("Для игр хватает, но в соседней комнате или за стеной пинг может скакать. Лучше, когда сигнал выше −67 дБм.")));
        int crowd = w.SameChannel + w.Overlapping;
        if (band == 2.4 && crowd >= 4)
            list.Add(new NetNote(Severity.Warn, L.F("Канал {0} перегружен: рядом {1}", w.Channel, Fmt.Plural(crowd, "сеть", "сети", "сетей")),
                L.T("Соседние сети делят с вашей один эфир. В настройках роутера выберите канал 1, 6 или 11 — тот, где меньше соседей, — или автоматический выбор канала. Ещё лучше перейти на 5 ГГц.")));
        else if (band >= 5 && w.SameChannel >= 3)
            list.Add(new NetNote(Severity.Info, L.F("На канале {0} ещё {1}", w.Channel, Fmt.Plural(w.SameChannel, "сеть", "сети", "сетей")),
                L.T("В настройках роутера выберите другой канал 5 ГГц или автоматический выбор канала.")));
        if (w.Phy is >= 4 and <= 6)
            list.Add(new NetNote(Severity.Info, L.F("Старый стандарт Wi‑Fi: {0}", Standard(w.Phy, band)),
                L.T("Скорость и задержка хуже, чем у Wi‑Fi 5 и 6. Проверьте, не включён ли в роутере режим совместимости со старыми устройствами, и поддерживает ли Wi‑Fi 5 или 6 сам компьютер.")));
        if (w.ListDenied)
            list.Add(new NetNote(Severity.Info, L.T("Соседние сети не видны"),
                L.T("Windows не дала программе список соседних сетей — так бывает, если выключен доступ приложений к расположению (Параметры → Конфиденциальность и защита → Расположение). Загруженность канала поэтому не проверяется.")));
        if (!list.Any(n => n.Level <= Severity.Warn))
            list.Insert(0, new NetNote(Severity.Ok, L.T("Wi‑Fi подходит для игр"),
                L.F("{0}, сигнал {1}, канал {2}. Кабель всё равно стабильнее, если его можно протянуть.", BandText(band),
                    w.Rssi != 0 ? Signed(w.Rssi) + Nb + L.Pick("дБм", "dBm") : w.Quality + Nb + "%", w.Channel > 0 ? w.Channel.ToString() : "—")));
        return list;
    }

    // ====================================================================================== пинг под нагрузкой
    public sealed class LoadResult
    {
        public string Gateway = "";
        public double GwIdle = double.NaN, GwLoaded = double.NaN, GwLoadedHigh = double.NaN;
        public double InIdle = double.NaN, InLoaded = double.NaN, InLoadedHigh = double.NaN;
        public int InSent, InLost, GwSent, GwLost;   // во время скачивания
        public double Mbps;
        public long Bytes;
        public string Error = "";
        public bool Cancelled;
    }

    public static double Median(IEnumerable<double> values) => Percentile(values, 0.5);

    public static double Percentile(IEnumerable<double> values, double p)
    {
        var v = values.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray();
        if (v.Length == 0) return double.NaN;
        double pos = p * (v.Length - 1);
        int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
        return v[lo] + (v[hi] - v[lo]) * (pos - lo);
    }

    /// <summary>
    /// Пинг до роутера и 1.1.1.1: 4 с в покое, затем ~10 с во время скачивания (4 потока по 25 МБ с speed.cloudflare.com, не больше 250 МБ).
    /// Всего не дольше 15 с. Отменяется токеном.
    /// </summary>
    public static async Task<LoadResult> RunLoad(IProgress<string> progress, CancellationToken ct)
    {
        var r = new LoadResult();
        NetMonitor.InternetAdapter(out var gw);
        r.Gateway = gw?.ToString() ?? "";
        var inet = IPAddress.Parse("1.1.1.1");
        var idleIn = new List<double>(); var idleGw = new List<double>();
        var loadIn = new List<double>(); var loadGw = new List<double>();

        progress?.Report(L.T("Пинг в покое…"));
        await PingFor(inet, gw, TimeSpan.FromSeconds(4), idleIn, idleGw, ct);
        if (ct.IsCancellationRequested) { r.Cancelled = true; return r; }

        progress?.Report(L.T("Скачиваю тестовые данные и меряю пинг…"));
        using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        loadCts.CancelAfter(TimeSpan.FromSeconds(10.5));
        long bytes = 0;
        var sw = Stopwatch.StartNew();
        long rampBytes = 0; double rampAt = 1.5;
        string error = null;
        using var http = new HttpClient(new SocketsHttpHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.None, PooledConnectionLifetime = TimeSpan.FromSeconds(30) })
        { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PulsePC/" + (typeof(NetDiag).Assembly.GetName().Version?.ToString(3) ?? "1"));
        // Не больше 250 МБ: этого хватает, чтобы занять канал до ~200 Мбит/с на все 10 с (а на быстрых линиях — на 2–3 с, чего
        // достаточно, чтобы заметить рост пинга), и тест не съедает заметную часть мобильного или лимитного трафика.
        const long Cap = 250L << 20;
        async Task Stream()
        {
            var buf = new byte[81920];
            while (!loadCts.IsCancellationRequested && Interlocked.Read(ref bytes) < Cap)
            {
                try
                {
                    using var resp = await http.GetAsync($"https://{SpeedHost}/__down?bytes=25000000", HttpCompletionOption.ResponseHeadersRead, loadCts.Token);
                    if (!resp.IsSuccessStatusCode) { error ??= L.F("сервер ответил {0}", (int)resp.StatusCode); loadCts.Cancel(); return; }
                    using var s = await resp.Content.ReadAsStreamAsync(loadCts.Token);
                    int n;
                    while ((n = await s.ReadAsync(buf, loadCts.Token)) > 0)
                    {
                        long total = Interlocked.Add(ref bytes, n);
                        if (rampBytes == 0 && sw.Elapsed.TotalSeconds >= rampAt) Interlocked.CompareExchange(ref rampBytes, total, 0);
                        if (total >= Cap) { loadCts.Cancel(); break; }
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { error ??= ex.InnerException?.Message ?? ex.Message; loadCts.Cancel(); return; }
            }
        }
        var streams = Enumerable.Range(0, 4).Select(_ => Task.Run(Stream)).ToArray();
        // Пинг во время скачивания: первые 1,5 с очередь в роутере ещё набирается — они не считаются.
        var pinging = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(rampAt), loadCts.Token).ContinueWith(_ => { });
            await PingFor(inet, gw, TimeSpan.FromSeconds(9), loadIn, loadGw, loadCts.Token, r);
        });
        try { await Task.WhenAll(streams); } catch { }
        loadCts.Cancel();
        try { await pinging; } catch { }
        double seconds = sw.Elapsed.TotalSeconds;

        r.Bytes = bytes;
        long measured = bytes - (rampBytes > 0 ? rampBytes : 0);
        double span = seconds - (rampBytes > 0 ? rampAt : 0);
        r.Mbps = span > 0.5 ? measured * 8 / span / 1e6 : 0;
        r.Error = error ?? "";
        r.Cancelled = ct.IsCancellationRequested;
        r.InIdle = Median(idleIn); r.GwIdle = Median(idleGw);
        r.InLoaded = Median(loadIn); r.GwLoaded = Median(loadGw);
        r.InLoadedHigh = Percentile(loadIn, 0.9); r.GwLoadedHigh = Percentile(loadGw, 0.9);
        return r;
    }

    /// <summary>Пинг двух адресов раз в 250 мс в течение заданного времени. Потерянный — NaN.</summary>
    private static async Task PingFor(IPAddress inet, IPAddress gw, TimeSpan time, List<double> outIn, List<double> outGw, CancellationToken ct, LoadResult counts = null)
    {
        using var pa = new Ping();
        using var pb = new Ping();
        var until = DateTime.UtcNow + time;
        while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            var a = One(pa, inet);
            var b = gw != null ? One(pb, gw) : Task.FromResult(double.NaN);
            double ai = await a, bi = await b;
            lock (outIn)
            {
                outIn.Add(ai);
                if (gw != null) outGw.Add(bi);
                if (counts != null)
                {
                    counts.InSent++; if (double.IsNaN(ai)) counts.InLost++;
                    if (gw != null) { counts.GwSent++; if (double.IsNaN(bi)) counts.GwLost++; }
                }
            }
            int rest = 250 - (int)(DateTime.UtcNow - started).TotalMilliseconds;
            if (rest > 0) { try { await Task.Delay(rest, ct); } catch { break; } }
        }
    }

    private static async Task<double> One(Ping p, IPAddress a)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var reply = await p.SendPingAsync(a, 1000);
            if (reply.Status != IPStatus.Success) return double.NaN;
            // RoundtripTime — целые миллисекунды; до роутера часто «0»: берём точное время ожидания, если оно меньше.
            return Math.Max(0.3, reply.RoundtripTime > 0 ? reply.RoundtripTime : sw.Elapsed.TotalMilliseconds);
        }
        catch { return double.NaN; }
    }

    /// <summary>Чистая функция: вывод по пингу под нагрузкой.</summary>
    public static List<NetNote> LoadVerdict(LoadResult r)
    {
        var list = new List<NetNote>();
        if (r == null) return list;
        if (r.Cancelled) { list.Add(new NetNote(Severity.Info, L.T("Проверка прервана"), L.T("Запустите её ещё раз, когда сеть свободна."))); return list; }
        if (double.IsNaN(r.InIdle))
        {
            list.Add(new NetNote(Severity.Warn, L.T("1.1.1.1 не отвечает на пинг"), L.T("Без ответа в покое сравнивать не с чем. Проверьте подключение к интернету и повторите.")));
            return list;
        }
        if (r.Error != "" && r.Bytes < 5_000_000)
        {
            list.Add(new NetNote(Severity.Info, L.T("Тестовые данные скачать не удалось"),
                L.F("Сервер проверки скорости Cloudflare не отдал файл ({0}). Проверить пинг под нагрузкой можно на сайте speed.cloudflare.com — там он называется «Loaded latency».", r.Error)));
            return list;
        }
        double rise = r.InLoaded - r.InIdle;
        double gwRise = r.GwLoaded - r.GwIdle;
        string numbers = L.F("До 1.1.1.1 в покое {0}, во время скачивания {1} (в худших 10 % замеров — {2}).", Ms(r.InIdle), Ms(r.InLoaded), Ms(r.InLoadedHigh));
        string speed = r.Mbps > 0 ? L.F(" Скорость скачивания во время проверки — около {0:0} Мбит/с.", r.Mbps) : "";
        bool home = !double.IsNaN(gwRise) && gwRise >= 15 && gwRise >= 0.6 * rise;
        string where = home
            ? L.F(" Пинг растёт уже до роутера (+{0}): очередь копится в домашней сети, чаще всего в Wi‑Fi. Кабель или 5 ГГц помогут больше, чем настройки роутера.", Ms(gwRise))
            : L.T(" Очередь копится на выходе в интернет: роутер или провайдер держат слишком большой буфер.");
        string Sqm = L.T(" В настройках роутера включите SQM (Smart Queue Management, cake, fq_codel) или «адаптивный QoS» и ограничьте скорость на 85–90 % от тарифа. Если такой функции нет — не качайте во время игры: обновления Steam, торренты и облака поднимут пинг.");
        if (double.IsNaN(rise))
            list.Add(new NetNote(Severity.Warn, L.T("Под нагрузкой 1.1.1.1 перестал отвечать"),
                L.T("Во время скачивания пинг не проходил вовсе — канал забивается целиком.") + Sqm));
        else if (rise < 10)
            list.Add(new NetNote(Severity.Ok, L.T("Пинг под нагрузкой почти не растёт"), numbers + speed + L.T(" Скачивания и обновления в фоне игре почти не мешают.")));
        else if (rise < 30)
            list.Add(new NetNote(Severity.Ok, L.F("Пинг под нагрузкой растёт немного: +{0}", Ms(rise)), numbers + speed + L.T(" Для игр это терпимо.")));
        else
            list.Add(new NetNote(Severity.Warn, L.F("Пинг под нагрузкой растёт {0}: +{1}", rise < 100 ? L.T("заметно") : L.T("сильно"), Ms(rise)),
                numbers + speed + where + (home ? "" : Sqm)));
        if (r.Error != "")
            list.Add(new NetNote(Severity.Info, L.T("Скачивание закончилось раньше"),
                L.F("Сервер проверки скорости остановил скачивание ({0}). Вывод сделан по тому, что успело скачаться.", r.Error)));
        double loss = r.InSent > 0 ? 100.0 * r.InLost / r.InSent : 0;
        if (loss >= 5 && !double.IsNaN(rise))
            list.Add(new NetNote(Severity.Warn, L.F("Под нагрузкой теряется {0:0} % пакетов", loss),
                L.T("Когда канал занят, часть пакетов пропадает: в игре это рывки и «телепорты» противников.")));
        return list;
    }

    // ====================================================================================== трассировка
    public sealed class Hop
    {
        public int Ttl;
        public string Address = "";
        public int Sent, Received;
        public double Best = double.NaN, Avg = double.NaN;
        public bool Destination;
        public string Segment = "";            // home / isp / far
        public double LossPct => Sent > 0 ? 100.0 * (Sent - Received) / Sent : 0;
        internal readonly Dictionary<string, int> seen = new();
        internal double sum;
    }

    public sealed class TraceResult
    {
        public string Target = "", Title = "";
        public List<Hop> Hops = new();
        public bool Reached;
        public string Error = "";
        public bool Cancelled;
    }

    /// <summary>
    /// Трассировка ICMP с TTL, как mtr: первый проход находит путь (все TTL сразу), потом ещё 9 проходов раз в секунду по всем узлам.
    /// Всего ~10–11 с.
    /// </summary>
    public static async Task<TraceResult> RunTrace(IPAddress target, string title, IProgress<string> progress, CancellationToken ct)
    {
        var r = new TraceResult { Target = target.ToString(), Title = title };
        var hops = Enumerable.Range(1, 30).Select(t => new Hop { Ttl = t }).ToArray();
        int maxTtl = 30;
        var payload = new byte[32];
        // count = false — проход поиска пути: все TTL сразу, и десятки пакетов одновременно летят к цели (она может часть
        // отбросить, бережёт себя) — в потери он не идёт.
        async Task Probe(Hop h, bool count)
        {
            using var p = new Ping();
            var sw = Stopwatch.StartNew();
            PingReply reply = null;
            try { reply = await p.SendPingAsync(target, 1000, payload, new PingOptions(h.Ttl, true)); } catch { }
            double ms = sw.Elapsed.TotalMilliseconds;
            lock (h)
            {
                bool ok = reply != null && reply.Status is IPStatus.Success or IPStatus.TtlExpired or IPStatus.TimeExceeded;
                if (ok && reply.Status == IPStatus.Success) h.Destination = true;
                if (ok) { string first = reply.Address?.ToString() ?? ""; h.seen[first] = h.seen.TryGetValue(first, out int c0) ? c0 + (count ? 1 : 0) : 1; }
                if (!count) return;
                h.Sent++;
                if (!ok) return;
                h.Received++;
                // Время ответа Windows отдаёт только для ответа цели; для промежуточных узлов — по часам программы.
                double rtt = reply.Status == IPStatus.Success && reply.RoundtripTime > 0 ? reply.RoundtripTime : Math.Max(0.3, ms);
                h.sum += rtt;
                h.Best = double.IsNaN(h.Best) ? rtt : Math.Min(h.Best, rtt);
            }
        }

        try
        {
            progress?.Report(L.T("Ищу путь…"));
            await Task.WhenAll(hops.Select(h => Probe(h, false)));
            var dest = hops.FirstOrDefault(h => h.Destination);
            if (dest != null) maxTtl = dest.Ttl;
            else
            {
                var last = hops.LastOrDefault(h => h.seen.Count > 0);
                maxTtl = last == null ? 0 : Math.Min(30, last.Ttl + 1);
            }
            if (maxTtl == 0) { r.Error = L.T("ни один узел не ответил"); return r; }
            for (int round = 1; round <= 10 && !ct.IsCancellationRequested; round++)
            {
                progress?.Report(L.F("Проверяю узлы: проход {0} из 10…", round));
                var started = DateTime.UtcNow;
                await Task.WhenAll(hops.Take(maxTtl).Select(h => Probe(h, true)));
                int rest = 1000 - (int)(DateTime.UtcNow - started).TotalMilliseconds;
                if (rest > 0) { try { await Task.Delay(rest, ct); } catch { } }
            }
        }
        catch (Exception ex) { r.Error = ex.Message; }
        r.Cancelled = ct.IsCancellationRequested;
        foreach (var h in hops.Take(maxTtl))
        {
            h.Avg = h.Received > 0 ? h.sum / h.Received : double.NaN;
            h.Address = h.seen.Count > 0 ? h.seen.OrderByDescending(kv => kv.Value).First().Key : "";
            // Узлы после цели (TTL больше пути) отвечают как сама цель — их не показываем.
            r.Hops.Add(h);
            if (h.Destination && h.Address == r.Target) { r.Reached = true; break; }
        }
        if (!r.Reached && r.Hops.Count > 0 && r.Hops[^1].Received == 0) r.Hops.RemoveAt(r.Hops.Count - 1);
        return r;
    }

    /// <summary>Частный адрес (домашняя сеть): 10/8, 172.16/12, 192.168/16, локальные IPv6.</summary>
    public static bool IsPrivate(string a)
    {
        if (!IPAddress.TryParse(a, out var ip)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>Адрес провайдера внутри его сети (NAT провайдера 100.64/10).</summary>
    public static bool IsCarrier(string a) => IPAddress.TryParse(a, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
                                              && ip.GetAddressBytes() is var b && b[0] == 100 && (b[1] & 0xC0) == 64;

    /// <summary>
    /// Чистая функция: участки пути. «Дома» — узлы до роутера включительно (и второй роутер 192.168.x.x за ним).
    /// «У провайдера» — дальше частные адреса и адреса NAT провайдера, плюс первые два внешних узла.
    /// «Дальше» — всё остальное. Граница провайдера — приблизительная.
    /// </summary>
    public static void Classify(List<Hop> hops, string gateway)
    {
        // Дом — до роутера включительно; если роутер на пути не узнан — первый узел (частный или молчащий).
        // Следом 192.168.x.x — второй роутер дома (двойной NAT); 10.x и 172.16/12 после роутера обычно уже сеть провайдера.
        int homeEnd = hops.FindIndex(h => h.Address != "" && h.Address == gateway);
        if (homeEnd < 0) homeEnd = hops.Count > 0 && (IsPrivate(hops[0].Address) || hops[0].Address == "") ? 0 : -1;
        while (homeEnd >= 0 && homeEnd + 1 < hops.Count && hops[homeEnd + 1].Address.StartsWith("192.168.", StringComparison.Ordinal)) homeEnd++;
        int i = 0;
        for (; i <= homeEnd; i++) hops[i].Segment = "home";
        int publicSeen = 0;
        var nets = new HashSet<string>();   // сети /16 узлов провайдера: дальше узлы из тех же сетей — тоже провайдер
        static string Net16(string a) => IPAddress.TryParse(a, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork ? string.Join(".", ip.GetAddressBytes().Take(2)) : a;
        for (; i < hops.Count; i++)
        {
            var h = hops[i];
            bool inner = h.Address == "" || IsPrivate(h.Address) || IsCarrier(h.Address);
            if (!inner)
            {
                if (publicSeen >= 2 && !nets.Contains(Net16(h.Address))) break;
                publicSeen++;
                nets.Add(Net16(h.Address));
            }
            h.Segment = "isp";
        }
        for (; i < hops.Count; i++) hops[i].Segment = "far";
        // Цель всегда «дальше», если она не в домашней сети.
        if (hops.Count > 0 && hops[^1].Destination && hops[^1].Segment == "isp" && hops.Count > 3) hops[^1].Segment = "far";
    }

    public static string SegmentName(string s) => s switch { "home" => L.T("у вас дома"), "isp" => L.T("у провайдера"), "far" => L.T("дальше"), _ => "" };

    /// <summary>
    /// Чистая функция: где начинаются потери. Потеря настоящая, только если она тянется до конца пути: промежуточные узлы часто
    /// отвечают на пинг через раз (бережут процессор), а пакеты через них идут нормально.
    /// </summary>
    public static List<NetNote> TraceVerdict(TraceResult r, string gateway)
    {
        var list = new List<NetNote>();
        if (r == null) return list;
        if (r.Cancelled) { list.Add(new NetNote(Severity.Info, L.T("Проверка прервана"), L.T("Запустите её ещё раз."))); return list; }
        if (r.Hops.Count == 0)
        {
            list.Add(new NetNote(Severity.Warn, L.T("Путь не найден"), L.F("Ни один узел не ответил ({0}). Проверьте подключение к интернету.", r.Error == "" ? "—" : r.Error)));
            return list;
        }
        Classify(r.Hops, gateway);
        var responding = r.Hops.Where(h => h.Received > 0).ToList();
        var end = r.Reached ? r.Hops[^1] : responding.LastOrDefault();
        if (end == null) { list.Add(new NetNote(Severity.Warn, L.T("Узлы не отвечают"), L.T("Ни один узел на пути не ответил на пинг."))); return list; }
        double endLoss = end.LossPct;
        string caveat = r.Reached ? "" : L.T(" Сама цель на пинг не отвечает (для игровых серверов это обычно), поэтому вывод — по последнему ответившему узлу.");
        string limited = string.Join(", ", r.Hops.Where(h => h != end && h.Received > 0 && h.LossPct >= 20).Select(h => h.Ttl.ToString()));

        // 10 запросов на узел: один потерянный — ещё не вывод (это могла быть случайность), потеря — от двух.
        int endLost = end.Sent - end.Received;
        if (endLost <= 1)
        {
            list.Add(new NetNote(Severity.Ok, endLost == 0 ? L.T("Пакеты доходят без потерь") : L.T("Потерь почти нет"),
                (endLost == 0
                    ? L.F("До {0} потерь нет, пинг {1}.", r.Reached ? r.Target : end.Address, Ms(end.Avg))
                    : L.F("До {0} не вернулся один ответ из {1} — для такого короткого замера это не показатель. Пинг {2}.", r.Reached ? r.Target : end.Address, end.Sent, Ms(end.Avg)))
                + caveat
                + (limited != "" ? L.F(" Узлы {0} отвечают не на все запросы — это ограничение самих узлов, а не потеря: до конца пути пакеты доходят.", limited) : "")));
            return list;
        }
        // Первый узел, с которого потеря держится до конца пути.
        int idx = responding.IndexOf(end);
        // Цель ответила только в проходе поиска пути, а в замерах — ни разу: в списке ответивших её нет, но потеря считается до неё.
        if (idx < 0) { responding.Add(end); idx = responding.Count - 1; }
        int start = idx;
        for (int i = idx; i >= 0; i--)
        {
            if (responding[i].Sent - responding[i].Received >= 2 && responding[i].LossPct >= endLoss * 0.5) start = i; else break;
        }
        var from = responding[start];
        string seg = from.Segment;
        string text = L.F("Потери начинаются с узла {0} ({1}) — {2} — и держатся до конца пути: {3:0} %.", from.Ttl, from.Address, SegmentName(seg), endLoss) + caveat;
        string what = seg switch
        {
            "home" => L.T(" Проверьте кабель и порт роутера; на Wi‑Fi — сигнал и диапазон. Перезагрузите роутер."),
            "isp" => L.T(" Это сеть провайдера: если потери держатся днями — позвоните в поддержку и назовите адрес узла и процент потерь."),
            _ => L.T(" Это за пределами вашего провайдера — повлиять почти нельзя. Если так только с одной игрой, попробуйте другой регион сервера."),
        };
        list.Add(new NetNote(endLoss >= 30 ? Severity.Bad : Severity.Warn, L.F("Пакеты теряются {0}", SegmentName(seg)), text + what));
        return list;
    }

    // ====================================================================================== DNS
    public sealed class DnsServer
    {
        public string Address = "", Name = "";
        public bool Current;
        public readonly List<double> Times = new();
        public int Fail;
        public double MedianMs => Median(Times);
    }

    private static readonly string[] DnsNames =
        { "www.google.com", "www.youtube.com", "store.steampowered.com", "www.cloudflare.com", "ya.ru", "www.microsoft.com", "discord.com", "www.twitch.tv" };

    /// <summary>DNS-запрос «имя, A, рекурсия». id — номер запроса.</summary>
    public static byte[] BuildQuery(ushort id, string name)
    {
        var b = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new ArgumentException("label");
            b.Add((byte)bytes.Length); b.AddRange(bytes);
        }
        b.AddRange(new byte[] { 0, 0, 1, 0, 1 });
        return b.ToArray();
    }

    /// <summary>Ответ на наш запрос: тот же номер, бит ответа, код «нет ошибки» или «нет такого имени» (тоже ответ).</summary>
    public static bool IsAnswer(byte[] resp, int len, ushort id) =>
        resp != null && len >= 12 && BinaryPrimitives.ReadUInt16BigEndian(resp) == id && (resp[2] & 0x80) != 0 && (resp[3] & 0x0F) is 0 or 3;

    /// <summary>Текущие DNS-серверы подключения к интернету (IPv4).</summary>
    public static List<string> CurrentDns()
    {
        try
        {
            var nic = NetMonitor.InternetAdapter(out _);
            return nic?.GetIPProperties().DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).Distinct().Take(2).ToList()
                   ?? new List<string>();
        }
        catch { return new List<string>(); }
    }

    /// <summary>По одному запросу каждого из 8 имён ко всем серверам одновременно. Не дольше ~8 с.</summary>
    public static async Task<List<DnsServer>> RunDns(IProgress<string> progress, CancellationToken ct)
    {
        var servers = new List<DnsServer>();
        foreach (var a in CurrentDns()) servers.Add(new DnsServer { Address = a, Name = PublicName(a), Current = true });
        foreach (var (a, n) in new[] { ("1.1.1.1", "Cloudflare"), ("8.8.8.8", "Google"), ("77.88.8.8", L.T("Яндекс")) })
            if (!servers.Any(s => s.Address == a)) servers.Add(new DnsServer { Address = a, Name = n });
        int round = 0;
        foreach (var name in DnsNames)
        {
            if (ct.IsCancellationRequested) break;
            progress?.Report(L.F("Спрашиваю адреса сайтов: {0} из {1}…", ++round, DnsNames.Length));
            await Task.WhenAll(servers.Select(s => Ask(s, name, ct)));
        }
        return servers;
    }

    private static string PublicName(string a) => a switch
    {
        "1.1.1.1" or "1.0.0.1" => "Cloudflare", "8.8.8.8" or "8.8.4.4" => "Google", "77.88.8.8" or "77.88.8.1" => L.T("Яндекс"), _ => "",
    };

    private static async Task Ask(DnsServer s, string name, CancellationToken ct)
    {
        try
        {
            ushort id = (ushort)Random.Shared.Next(1, 65535);
            var q = BuildQuery(id, name);
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Connect(IPAddress.Parse(s.Address), 53);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(2000);
            var sw = Stopwatch.StartNew();
            await udp.SendAsync(q, cts.Token);
            while (true)
            {
                var res = await udp.ReceiveAsync(cts.Token);
                if (IsAnswer(res.Buffer, res.Buffer.Length, id)) { lock (s) s.Times.Add(sw.Elapsed.TotalMilliseconds); return; }
            }
        }
        catch { lock (s) s.Fail++; }
    }

    /// <summary>Чистая функция: вывод по DNS. Текущий заметно медленнее лучшего общедоступного — совет с инструкцией (сама программа DNS не меняет).</summary>
    public static List<NetNote> DnsVerdict(List<DnsServer> servers)
    {
        var list = new List<NetNote>();
        if (servers == null || servers.Count == 0) return list;
        var current = servers.Where(s => s.Current).ToList();
        var publics = servers.Where(s => !s.Current && s.Times.Count > 0).OrderBy(s => s.MedianMs).ToList();
        var best = publics.FirstOrDefault();
        string How = L.T(" Сменить DNS можно самому: Параметры → Сеть и Интернет → ваше подключение → «Назначение DNS-сервера» → «Изменить» → «Вручную», IPv4. Программа DNS не меняет. На пинг в игре DNS не влияет — только на то, как быстро открываются сайты и находятся серверы.");
        if (current.Count == 0)
        {
            list.Add(new NetNote(Severity.Info, L.T("Текущий DNS не найден"), L.T("У подключения нет DNS-сервера IPv4 — сравнить не с чем.")));
            return list;
        }
        var cur = current.OrderBy(s => s.Times.Count > 0 ? s.MedianMs : double.MaxValue).First();
        if (cur.Times.Count == 0)
        {
            list.Add(new NetNote(Severity.Warn, L.F("Текущий DNS {0} не отвечает", cur.Address),
                L.T("Сайты и игры могут не находить серверы или открываться с задержкой.") + (best != null ? L.F(" {0} ({1}) отвечает за {2}.", best.Name, best.Address, Ms(best.MedianMs)) : "") + How));
            return list;
        }
        double c = cur.MedianMs;
        if (best != null && c > best.MedianMs + 20 && c > best.MedianMs * 1.5)
            list.Add(new NetNote(Severity.Info, L.F("Текущий DNS медленнее: {0} против {1}", Ms(c), Ms(best.MedianMs)),
                L.F("Текущий DNS {0} отвечает в среднем за {1}, а {2} ({3}) — за {4}.", cur.Address, Ms(c), best.Name, best.Address, Ms(best.MedianMs)) + How));
        else
            list.Add(new NetNote(Severity.Ok, L.T("Текущий DNS отвечает быстро"),
                L.F("Текущий DNS {0} отвечает за {1}", cur.Address, Ms(c)) + (best != null ? L.F(", самый быстрый из общедоступных — {0}, {1}. Менять незачем.", best.Name, Ms(best.MedianMs)) : ".")));
        if (cur.Fail >= 2)
            list.Add(new NetNote(Severity.Warn, L.F("Текущий DNS не ответил {0} раз из {1}", cur.Fail, cur.Fail + cur.Times.Count),
                L.T("Иногда сайты и лаунчеры будут думать по несколько секунд.") + How));
        return list;
    }

    // ====================================================================================== самопроверка
    /// <summary>Логика на подставных данных; плюс что видно на этом ПК (только чтение: Wi‑Fi, шлюз, текущий DNS). В сеть не обращается.</summary>
    public static string SelfTest()
    {
        var fails = new List<string>();
        int n = 0;
        void Check(string what, bool ok) { n++; if (!ok) fails.Add(what); }

        // Wi‑Fi
        Check("2412 МГц → канал 1, 2,4 ГГц", ChannelOf(2412) == 1 && BandOf(2412, 0) == 2.4);
        Check("2484 МГц → канал 14", ChannelOf(2484) == 14);
        Check("5180 МГц → канал 36, 5 ГГц", ChannelOf(5180) == 36 && BandOf(5180, 0) == 5);
        Check("5955 МГц → канал 1, 6 ГГц", ChannelOf(5955) == 1 && BandOf(5955, 0) == 6);
        Check("без частоты: канал 6 → 2,4 ГГц, канал 149 → 5", BandOf(0, 6) == 2.4 && BandOf(0, 149) == 5);
        Check("Wi‑Fi 6E на 6 ГГц", Standard(10, 6).Contains("6E") && Standard(10, 5) == "Wi‑Fi 6 (802.11ax)");
        byte[] ours = { 0x10, 0xAA, 0xBB, 0xCC, 0xDD, 0x01 };
        var air = new List<(byte[], double, int)>
        {
            (new byte[] { 0x12, 0xAA, 0xBB, 0xCC, 0xDD, 0x02 }, 2437, -40),   // гостевая сеть того же роутера — не сосед
            (new byte[] { 1, 2, 3, 4, 5, 6 }, 2437, -60),                     // тот же канал 6
            (new byte[] { 1, 2, 3, 4, 5, 7 }, 2427, -70),                     // канал 4 — перекрывается
            (new byte[] { 1, 2, 3, 4, 5, 8 }, 2462, -70),                     // канал 11 — не перекрывается
            (new byte[] { 1, 2, 3, 4, 5, 9 }, 2437, -90),                     // слишком слабая
            (new byte[] { 1, 2, 3, 4, 6, 1 }, 5180, -50),                     // другой диапазон
        };
        var (same, overlap) = Neighbours(ours, 2437, 6, air);
        Check($"соседи на 2,4 ГГц (тот же {same}, перекрытие {overlap})", same == 1 && overlap == 1);

        var bad24 = new WifiInfo { Connected = true, CarriesInternet = true, FreqMhz = 2437, Channel = 6, Rssi = -78, Phy = 7, SameChannel = 3, Overlapping = 2 };
        var adv = WifiAdvice(bad24);
        Check($"2,4 ГГц + слабый сигнал + перегруженный канал ({adv.Count})", adv.Count == 3 && adv.All(a => a.Level == Severity.Warn));
        var good5 = new WifiInfo { Connected = true, CarriesInternet = true, FreqMhz = 5500, Channel = 100, Rssi = -52, Phy = 10 };
        var adv5 = WifiAdvice(good5);
        Check("5 ГГц, сильный сигнал — «подходит для игр»", adv5.Count == 1 && adv5[0].Level == Severity.Ok);
        Check("Wi‑Fi не для интернета — без советов по сигналу", WifiAdvice(new WifiInfo { Connected = true, CarriesInternet = false, FreqMhz = 2412, Rssi = -85 }).Count == 1);
        Check("старый стандарт 802.11g", WifiAdvice(new WifiInfo { Connected = true, CarriesInternet = true, FreqMhz = 2412, Channel = 1, Rssi = -50, Phy = 6 }).Any(a => a.Title.Contains("802.11g")));
        Check("нет подключения — пусто", WifiAdvice(new WifiInfo()).Count == 0);

        // Пинг под нагрузкой
        Check("медиана и 90-й процентиль", Median(new double[] { 5, 1, 3, double.NaN }) == 3 && Math.Abs(Percentile(new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, 0.9) - 9.1) < 1e-9);
        var calm = LoadVerdict(new LoadResult { InIdle = 12, InLoaded = 15, InLoadedHigh = 18, GwIdle = 1, GwLoaded = 1.5, Mbps = 480, Bytes = 600_000_000 });
        Check("рост +3 мс — в порядке", calm.Count == 1 && calm[0].Level == Severity.Ok);
        var bloat = LoadVerdict(new LoadResult { InIdle = 12, InLoaded = 160, InLoadedHigh = 240, GwIdle = 1, GwLoaded = 2, Mbps = 90, Bytes = 100_000_000 });
        Check("рост +148 мс после роутера — совет SQM", bloat.Count == 1 && bloat[0].Level == Severity.Warn && bloat[0].Text.Contains("SQM"));
        var wifi = LoadVerdict(new LoadResult { InIdle = 12, InLoaded = 90, GwIdle = 2, GwLoaded = 75, Mbps = 60, Bytes = 70_000_000 });
        Check("рост уже до роутера — про домашнюю сеть, без SQM", wifi[0].Level == Severity.Warn && wifi[0].Text.Contains("Wi‑Fi") && !wifi[0].Text.Contains("SQM"));
        var denied = LoadVerdict(new LoadResult { InIdle = 12, Error = "сервер ответил 403", Bytes = 1 });
        Check("сервер отказал — ссылка, без вывода", denied.Count == 1 && denied[0].Level == Severity.Info && denied[0].Text.Contains("speed.cloudflare.com"));
        Check("1.1.1.1 молчит — нечего сравнивать", LoadVerdict(new LoadResult()).Single().Level == Severity.Warn);
        var lossy = LoadVerdict(new LoadResult { InIdle = 12, InLoaded = 20, InSent = 36, InLost = 6, Bytes = 80_000_000 });
        Check("потери под нагрузкой", lossy.Count == 2 && lossy[1].Title.Contains("17"));

        // Трассировка
        Hop H(int ttl, string a, int recv, double avg, bool dest = false) => new() { Ttl = ttl, Address = a, Sent = 10, Received = recv, Avg = avg, Destination = dest };
        var path = new List<Hop> { H(1, "192.168.1.1", 10, 1), H(2, "100.70.0.1", 10, 4), H(3, "", 0, double.NaN), H(4, "95.167.1.1", 10, 6), H(5, "188.254.1.1", 6, 7), H(6, "172.70.1.1", 10, 20), H(7, "185.25.182.77", 10, 21, true) };
        Classify(path, "192.168.1.1");
        Check($"участки: {string.Join(",", path.Select(h => h.Segment))}",
            path[0].Segment == "home" && path[1].Segment == "isp" && path[2].Segment == "isp" && path[3].Segment == "isp" && path[4].Segment == "isp" && path[5].Segment == "far" && path[6].Segment == "far");
        var clean = TraceVerdict(new TraceResult { Target = "185.25.182.77", Reached = true, Hops = path }, "192.168.1.1");
        Check("промежуточный узел отвечает через раз — не потеря", clean.Count == 1 && clean[0].Level == Severity.Ok && clean[0].Text.Contains("5"));
        var lossPath = new List<Hop> { H(1, "192.168.1.1", 10, 1), H(2, "100.70.0.1", 7, 4), H(3, "95.167.1.1", 7, 6), H(4, "185.25.182.77", 7, 21, true) };
        var lossV = TraceVerdict(new TraceResult { Target = "185.25.182.77", Reached = true, Hops = lossPath }, "192.168.1.1");
        Check($"потери с узла 2 — у провайдера ({lossV[0].Title})", lossV[0].Level == Severity.Bad && lossV[0].Title.Contains(SegmentName("isp")) && (L.Code != "ru" || lossV[0].Text.Contains("узла 2")));
        var one = new List<Hop> { H(1, "192.168.1.1", 10, 1), H(2, "46.19.98.210", 10, 2), H(3, "46.19.99.2", 10, 2), H(4, "46.19.99.3", 10, 2), H(5, "1.1.1.1", 9, 4, true) };
        var oneV = TraceVerdict(new TraceResult { Target = "1.1.1.1", Reached = true, Hops = one }, "192.168.1.1");
        Check($"один потерянный из 10 — не потеря; узлы той же сети /16 — провайдер ({string.Join(",", one.Select(h => h.Segment))})",
            oneV.Count == 1 && oneV[0].Level == Severity.Ok && one[3].Segment == "isp" && one[4].Segment == "far");
        var homeLoss = new List<Hop> { H(1, "192.168.0.1", 8, 3), H(2, "10.0.0.1", 8, 5), H(3, "8.8.8.8", 8, 15, true) };
        var homeV = TraceVerdict(new TraceResult { Target = "8.8.8.8", Reached = true, Hops = homeLoss }, "192.168.0.1");
        Check("потери с роутера — дома", homeV[0].Title.Contains(SegmentName("home")));
        var silent = new List<Hop> { H(1, "192.168.1.1", 10, 1), H(2, "100.70.0.1", 10, 4), H(3, "95.167.1.1", 10, 6) };
        var silentV = TraceVerdict(new TraceResult { Target = "203.0.113.5", Reached = false, Hops = silent }, "192.168.1.1");
        Check("сервер не отвечает на пинг — оговорка", silentV[0].Level == Severity.Ok && (L.Code != "ru" || silentV[0].Text.Contains("не отвечает")));
        var lostEnd = new List<Hop> { H(1, "192.168.1.1", 10, 1), H(2, "95.167.1.1", 10, 6), H(3, "185.25.182.77", 0, double.NaN, true) };
        List<NetNote> lostEndV = null;
        try { lostEndV = TraceVerdict(new TraceResult { Target = "185.25.182.77", Reached = true, Hops = lostEnd }, "192.168.1.1"); } catch { }
        Check("цель ответила только при поиске пути — вывод о потерях, без ошибки", lostEndV is { Count: 1 } && lostEndV[0].Level == Severity.Bad);
        Check("частные и NAT провайдера", IsPrivate("172.20.1.1") && !IsPrivate("172.32.1.1") && IsCarrier("100.100.1.1") && !IsCarrier("100.128.0.1") && !IsPrivate("8.8.8.8"));

        // DNS
        var q = BuildQuery(0x1234, "ya.ru");
        Check("DNS-запрос: заголовок и имя", q.Length == 12 + 7 + 4 && q[0] == 0x12 && q[1] == 0x34 && q[2] == 1 && q[12] == 2 && q[15] == 2 && q[^1] == 1);
        var ans = (byte[])q.Clone(); ans[2] |= 0x80; ans[3] = 0x80;
        var nx = (byte[])ans.Clone(); nx[3] = 0x83;
        var fail = (byte[])ans.Clone(); fail[3] = 0x82;
        Check("DNS-ответ: свой номер, NXDOMAIN — тоже ответ, SERVFAIL — нет", IsAnswer(ans, ans.Length, 0x1234) && IsAnswer(nx, nx.Length, 0x1234)
              && !IsAnswer(fail, fail.Length, 0x1234) && !IsAnswer(ans, ans.Length, 0x4321) && !IsAnswer(q, q.Length, 0x1234));
        DnsServer S(string a, bool cur, int fails, params double[] t) { var s = new DnsServer { Address = a, Name = PublicName(a), Current = cur, Fail = fails }; s.Times.AddRange(t); return s; }
        var slow = DnsVerdict(new List<DnsServer> { S("192.168.1.1", true, 0, 80, 95, 70), S("1.1.1.1", false, 0, 8, 9, 10), S("8.8.8.8", false, 0, 20, 22) });
        Check("текущий 80 мс против 9 — совет с инструкцией", slow.Count == 1 && slow[0].Level == Severity.Info && (L.Code != "ru" || slow[0].Text.Contains("Вручную")));
        var fast = DnsVerdict(new List<DnsServer> { S("192.168.1.1", true, 0, 3, 4), S("1.1.1.1", false, 0, 8, 9) });
        Check("текущий быстрый — менять незачем", fast.Count == 1 && fast[0].Level == Severity.Ok);
        var dead = DnsVerdict(new List<DnsServer> { S("192.168.1.1", true, 8), S("77.88.8.8", false, 0, 12) });
        Check("текущий молчит — предупреждение", dead[0].Level == Severity.Warn);
        var flaky = DnsVerdict(new List<DnsServer> { S("192.168.1.1", true, 3, 5, 5, 5, 5, 5), S("1.1.1.1", false, 0, 8) });
        Check("текущий теряет ответы", flaky.Count == 2 && flaky[1].Level == Severity.Warn);

        // Что видно на этом ПК (только чтение, без запросов в сеть).
        var log = new StringBuilder();
        var nic = NetMonitor.InternetAdapter(out var gw);
        log.AppendLine($"на этом ПК: подключение к интернету — {(nic == null ? "нет" : $"{nic.Description} ({nic.NetworkInterfaceType})")}, шлюз {gw?.ToString() ?? "—"}");
        var dnsNow = CurrentDns();
        log.AppendLine($"  текущий DNS: {(dnsNow.Count == 0 ? "—" : string.Join(", ", dnsNow))}");
        var w = ReadWifi();
        if (!w.Present) log.AppendLine("  Wi‑Fi: адаптера нет" + (w.Error != "" ? $" ({w.Error})" : ""));
        else if (!w.Connected) log.AppendLine($"  Wi‑Fi: {w.Adapter}, не подключён");
        else
        {
            log.AppendLine($"  Wi‑Fi: {w.Adapter}; {BandText(w.Band)}, канал {w.Channel}, {Standard(w.Phy, w.Band)}, сигнал {w.Quality} % / {w.Rssi} дБм, " +
                           $"{w.RxMbps}/{w.TxMbps} Мбит/с, соседей на канале {w.SameChannel}, перекрытие {w.Overlapping}{(w.ListDenied ? ", список сетей закрыт" : "")}, " +
                           $"интернет через него: {(w.CarriesInternet ? "да" : "нет")}");
            foreach (var a in WifiAdvice(w)) log.AppendLine($"    [{a.Level}] {a.Title}");
        }
        return (fails.Count == 0 ? $"OK ({n} проверок)" : "FAIL: " + string.Join("; ", fails)) + "\n" + log;
    }

    // ====================================================================================== WLAN API
    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out uint size, out IntPtr data, out int valueType);
    [DllImport("wlanapi.dll")] private static extern uint WlanGetNetworkBssList(IntPtr handle, ref Guid iface, IntPtr ssid, int bssType, [MarshalAs(UnmanagedType.Bool)] bool securityEnabled, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
}

/// <summary>
/// Живая проверка сети (только по ручному запуску: PulsePC.exe --feature-test NetDiagLive файл.txt): DNS, трассировка до 1.1.1.1
/// и пинг под нагрузкой — то же, что кнопки на вкладке «Сеть». Ничего не меняет; скачивает тестовые данные Cloudflare ~10 с.
/// </summary>
public static class NetDiagLive
{
    public static string SelfTest()
    {
        var sb = new StringBuilder("OK (живая проверка, на общий итог не влияет)\n");
        string only = Environment.GetEnvironmentVariable("PULSEPC_NETDIAG_LIVE") ?? "dns,trace,load";
        try
        {
            NetMonitor.InternetAdapter(out var gw);
            if (only.Contains("dns"))
            {
                var sw = Stopwatch.StartNew();
                var d = Task.Run(() => NetDiag.RunDns(null, CancellationToken.None)).GetAwaiter().GetResult();
                sb.AppendLine($"DNS ({sw.Elapsed.TotalSeconds:0.0} с):");
                foreach (var s in d) sb.AppendLine($"  {s.Address,-15} {s.Name,-10} {(s.Current ? "текущий" : ""),-8} медиана {NetDiag.Ms(s.MedianMs)}, ответов {s.Times.Count}, без ответа {s.Fail}");
                foreach (var n in NetDiag.DnsVerdict(d)) sb.AppendLine($"  [{n.Level}] {n.Title} — {n.Text}");
            }
            if (only.Contains("trace"))
            {
                var sw = Stopwatch.StartNew();
                var t = Task.Run(() => NetDiag.RunTrace(IPAddress.Parse("1.1.1.1"), "1.1.1.1", null, CancellationToken.None)).GetAwaiter().GetResult();
                var v = NetDiag.TraceVerdict(t, gw?.ToString() ?? "");
                sb.AppendLine($"Трассировка до 1.1.1.1 ({sw.Elapsed.TotalSeconds:0.0} с), цель {(t.Reached ? "ответила" : "не ответила")}:");
                foreach (var h in t.Hops) sb.AppendLine($"  {h.Ttl,2} {h.Address,-16} {NetDiag.SegmentName(h.Segment),-14} {NetDiag.Ms(h.Avg),8}  потери {h.LossPct:0} % ({h.Received}/{h.Sent})");
                foreach (var n in v) sb.AppendLine($"  [{n.Level}] {n.Title} — {n.Text}");
            }
            if (only.Contains("load"))
            {
                var sw = Stopwatch.StartNew();
                var r = Task.Run(() => NetDiag.RunLoad(null, CancellationToken.None)).GetAwaiter().GetResult();
                sb.AppendLine($"Пинг под нагрузкой ({sw.Elapsed.TotalSeconds:0.0} с): роутер {r.Gateway} {NetDiag.Ms(r.GwIdle)} → {NetDiag.Ms(r.GwLoaded)} (90 %: {NetDiag.Ms(r.GwLoadedHigh)}); " +
                              $"1.1.1.1 {NetDiag.Ms(r.InIdle)} → {NetDiag.Ms(r.InLoaded)} (90 %: {NetDiag.Ms(r.InLoadedHigh)}); скачано {r.Bytes / 1e6:0} МБ, {r.Mbps:0} Мбит/с" +
                              (r.Error != "" ? $"; ошибка: {r.Error}" : ""));
                foreach (var n in NetDiag.LoadVerdict(r)) sb.AppendLine($"  [{n.Level}] {n.Title} — {n.Text}");
            }
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION " + ex); }
        return sb.ToString();
    }
}
