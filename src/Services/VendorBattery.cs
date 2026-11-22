using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PulsePC.Services;

/// <summary>
/// Заряд устройств, у которых свой протокол марки (а не стандартное значение HID): мыши и гарнитуры HyperX, SteelSeries, Corsair,
/// Razer, Logitech G435 и других. Свой код: таблица «устройство → в какой коллекции HID спросить → какой запрос → где в ответе заряд»
/// (сама таблица — в VendorBattery.Devices.cs).
///
/// Правила:
///  • только запросы чтения (getter). Опрос, который прерывает звук (Logitech G435), — у протоколов с <see cref="Protocol.QuietMark"/>:
///    вне игры только в тишине, в игре — лишь пока она загружается, не чаще раза в <see cref="Protocol.MinMinutes"/> минут;
///  • только служебная коллекция марки (страница 0xFF00 и выше). Исключение — feature-отчёт Razer: он открывается без прав
///    на чтение и запись, как и описание любой коллекции; ввод мыши и клавиатуры программа не читает и не пишет;
///  • короткое ожидание: спящее устройство не отвечает — тогда полчаса показывается последний известный заряд;
///  • фирменная программа (G HUB, NGENUITY, Synapse, GG) может читать ту же коллекцию — ответ узнаётся по своим байтам, чужие пропускаются.
/// </summary>
internal static partial class VendorBattery
{
    /// <summary>Показание. Asleep — устройство не ответило (спит или выключено). Unsupported — у этой модели заряда нет (проводная).</summary>
    internal readonly record struct Reading(int? Percent, bool? Charging, bool Approx, bool Asleep, bool Unsupported = false)
    {
        public static readonly Reading NoAnswer = new(null, null, false, true);
        public static readonly Reading None = new(null, null, false, false, true);
        public static Reading Pct(int? pct, bool? charging = null, bool approx = false) =>
            pct is int p && p >= 0 && p <= 100 ? new(p, charging, approx, false) : new(null, null, false, false);
    }

    internal sealed record Model(int Pid, string Name, DeviceKind Kind);

    /// <summary>Протокол одной линейки устройств.</summary>
    internal sealed class Protocol
    {
        public string Id = "";
        public string Brand = "";
        public int Vid;
        /// <summary>Модели. null — любая модель этой марки (название — из самого устройства).</summary>
        public Model[] Models;
        /// <summary>Коллекция: страница (0 — любая служебная, 0xFF00 и выше), использование (0 — любое), интерфейс USB (−1 — любой).</summary>
        public ushort Page, Usage;
        public int Mi = -1;
        /// <summary>Длины отчётов из описания коллекции (0 — не важна).</summary>
        public int InLen, OutLen, FeatLen;
        /// <summary>Только feature-отчёты, коллекция открывается без прав чтения и записи (Razer: отчёт на коллекции мыши).</summary>
        public bool FeatureOnly;
        /// <summary>Устройство само присылает заряд во входных отчётах (геймпады): программа только слушает и ничего не отправляет.</summary>
        public bool Passive;
        /// <summary>Опрос прерывает звук: вне игры — только когда в звуковом устройстве с этим названием тишина.</summary>
        public string QuietMark;
        /// <summary>Не чаще, чем раз в столько минут (0 — при каждом опросе).</summary>
        public int MinMinutes;
        /// <summary>Не опрашивать, пока идёт игра (опрос прерывает звук).</summary>
        public bool NotInGame;
        /// <summary>Проверено этой программой на настоящем устройстве.</summary>
        public bool Tested;
        public Func<Port, int, Reading> Read;

        public bool Matches(DeviceBattery.HidIface d)
        {
            if (d.Vid != Vid || (Models != null && Models.All(m => m.Pid != d.Pid))) return false;
            if (Mi >= 0 && InterfaceOf(d.Path) != Mi) return false;
            if (FeatureOnly) return d.FeatLen > 0 && (FeatLen == 0 || d.FeatLen == FeatLen);
            if (Passive) return d.UsagePage == Page && d.Usage == Usage;
            if (Page == 0 ? d.UsagePage < 0xFF00 : d.UsagePage != Page) return false;
            if (Usage != 0 && d.Usage != Usage) return false;
            return (InLen == 0 || d.InLen == InLen) && (OutLen == 0 || d.OutLen == OutLen) && (FeatLen == 0 || d.FeatLen == FeatLen);
        }
    }

    /// <summary>Номер интерфейса USB из пути коллекции («…&amp;mi_03…»); −1 — в пути его нет (устройство с одним интерфейсом, Bluetooth).</summary>
    internal static int InterfaceOf(string path)
    {
        var m = Regex.Match(path ?? "", @"&mi_([0-9a-f]{2})", RegexOptions.IgnoreCase);
        return m.Success ? Convert.ToInt32(m.Groups[1].Value, 16) : -1;
    }

    // ───────────────────────── канал к устройству ─────────────────────────

    /// <summary>
    /// Открытая коллекция HID. Отчёты дополняются нулями до длины из описания; первый байт — номер отчёта (0, если номеров нет).
    /// Чтение входных отчётов — с тайм-аутом, ответы, которые не подошли, пропускаются.
    /// </summary>
    internal sealed class Port : IDisposable
    {
        private readonly SafeFileHandle handle;
        private readonly FileStream stream;
        public readonly int InLen, OutLen, FeatLen;
        /// <summary>Почему не удалось отправить или прочитать (для журнала самопроверки).</summary>
        public string Problem { get; private set; }

        public bool Bluetooth { get; private init; }

        private Port(SafeFileHandle h, bool io, bool readOnly, int inLen, int outLen, int featLen)
        {
            handle = h; InLen = inLen; OutLen = readOnly ? 0 : outLen; FeatLen = readOnly ? 0 : featLen;
            if (io) stream = new FileStream(h, readOnly ? FileAccess.Read : FileAccess.ReadWrite, 1, true);
        }

        public static Port Open(DeviceBattery.HidIface d, bool featureOnly, bool passive = false)
        {
            // Слушать входные отчёты геймпада — только право чтения: записать в него программа ничего не может.
            uint access = featureOnly ? 0 : passive ? GenericRead : GenericRead | GenericWrite;
            var h = CreateFile(d.Path, access, FileShareReadWrite, IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
            if (h.IsInvalid) { int e = Marshal.GetLastWin32Error(); h.Dispose(); throw new IOException($"коллекция не открылась (код {e})"); }
            return new Port(h, !featureOnly, passive, d.InLen, d.OutLen, d.FeatLen) { Bluetooth = d.Bluetooth };
        }

        /// <summary>Выбросить накопившиеся входные отчёты: ответ на прошлый запрос (свой или чужой) не примется за новый.</summary>
        public void Flush() { try { HidD_FlushQueue(handle); } catch { } }

        private static byte[] Pad(byte[] data, int len)
        {
            var buf = new byte[Math.Max(len, data.Length)];
            Array.Copy(data, buf, data.Length);
            return buf;
        }

        /// <summary>Выходной отчёт (WriteFile).</summary>
        public bool Send(byte[] data, int timeoutMs = 600)
        {
            if (stream == null || OutLen <= 0) { Problem = "у коллекции нет выходного отчёта"; return false; }
            using var cts = new CancellationTokenSource(timeoutMs);
            try { stream.WriteAsync(Pad(data, OutLen), cts.Token).AsTask().GetAwaiter().GetResult(); return true; }
            catch (Exception ex) { Problem = "отправка: " + ex.Message; return false; }
        }

        /// <summary>Ждать входной отчёт, который подходит под match. null — не пришёл за timeoutMs.</summary>
        public byte[] Receive(Func<byte[], bool> match, int timeoutMs)
        {
            if (stream == null || InLen <= 0) return null;
            long deadline = Environment.TickCount64 + timeoutMs;
            var buf = new byte[InLen];
            while (true)
            {
                long left = deadline - Environment.TickCount64;
                if (left <= 0) return null;
                int n;
                using (var cts = new CancellationTokenSource((int)left))
                {
                    try { n = stream.ReadAsync(buf, 0, buf.Length, cts.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { return null; }
                    catch (Exception ex) { Problem = "чтение: " + ex.Message; return null; }
                }
                if (n <= 0) continue;
                var r = buf.AsSpan(0, n).ToArray();
                if (match(r)) return r;
            }
        }

        /// <summary>Все входные отчёты за timeoutMs (для ответов, разбитых на несколько отчётов).</summary>
        public List<byte[]> Collect(int timeoutMs)
        {
            var all = new List<byte[]>();
            Receive(r => { all.Add(r); return false; }, timeoutMs);
            return all;
        }

        /// <summary>Запрос и ответ: очистить очередь, отправить, ждать подходящий ответ.</summary>
        public byte[] Ask(byte[] request, Func<byte[], bool> match, int timeoutMs = 800)
        {
            Flush();
            return Send(request) ? Receive(match, timeoutMs) : null;
        }

        public bool SetFeature(byte[] data)
        {
            if (FeatLen <= 0) { Problem = "у коллекции нет feature-отчёта"; return false; }
            bool ok = HidD_SetFeature(handle, Pad(data, FeatLen), FeatLen);
            if (!ok) Problem = $"feature-отчёт не принят (код {Marshal.GetLastWin32Error()})";
            return ok;
        }

        /// <summary>Feature-отчёт с номером reportId. null — не прочитан.</summary>
        public byte[] GetFeature(byte reportId)
        {
            if (FeatLen <= 0) return null;
            var buf = new byte[FeatLen];
            buf[0] = reportId;
            return HidD_GetFeature(handle, buf, FeatLen) ? buf : null;
        }

        /// <summary>Feature-запрос и ответ: отправить, подождать, читать, пока ответ не подойдёт (или не выйдет время).</summary>
        public byte[] AskFeature(byte[] request, byte replyId, Func<byte[], bool> match, int firstWaitMs, int stepMs, int tries)
        {
            if (!SetFeature(request)) return null;
            Thread.Sleep(firstWaitMs);
            for (int i = 0; i < tries; i++)
            {
                var r = GetFeature(replyId);
                if (r != null && match(r)) return r;
                Thread.Sleep(stepMs);
            }
            return null;
        }

        public void Dispose()
        {
            try { stream?.Dispose(); } catch { }
            try { handle.Dispose(); } catch { }
        }
    }

    // ───────────────────────── разбор ─────────────────────────

    /// <summary>Напряжение → проценты по точкам (mV, %) от большего к меньшему; между точками — по прямой.</summary>
    internal static int? Curve(int mv, params (int Mv, int Pct)[] points)
    {
        if (mv <= 0 || points.Length == 0) return null;
        if (mv >= points[0].Mv) return points[0].Pct;
        for (int i = 1; i < points.Length; i++)
        {
            if (mv < points[i].Mv) continue;
            var (hiMv, hiPct) = points[i - 1];
            var (loMv, loPct) = points[i];
            return (int)Math.Round(loPct + (double)(mv - loMv) * (hiPct - loPct) / (hiMv - loMv));
        }
        return points[^1].Pct;
    }

    /// <summary>Ступени («0…4», «0…8») → примерные проценты.</summary>
    internal static int? Steps(int level, int max) => level >= 0 && level <= max ? (int)Math.Round(level * 100.0 / max) : null;

    internal static int Be16(byte[] r, int at) => r.Length > at + 1 ? (r[at] << 8) | r[at + 1] : 0;
    internal static int Le16(byte[] r, int at) => r.Length > at + 1 ? r[at] | (r[at + 1] << 8) : 0;
    internal static bool Has(byte[] r, params (int At, byte Value)[] marks) => r != null && marks.All(m => r.Length > m.At && r[m.At] == m.Value);

    /// <summary>Найти последовательность байт в отчёте (−1 — нет).</summary>
    internal static int Find(byte[] r, params byte[] seq)
    {
        if (r == null) return -1;
        for (int i = 0; i + seq.Length <= r.Length; i++)
        {
            int k = 0;
            while (k < seq.Length && r[i + k] == seq[k]) k++;
            if (k == seq.Length) return i;
        }
        return -1;
    }

    // ───────────────────────── опрос ─────────────────────────

    private sealed record Last(Reading Value, DateTime At);
    private static readonly Dictionary<string, Last> lastGood = new();
    private static readonly Dictionary<string, DateTime> lastAsked = new();
    /// <summary>Сколько минут показывать последний заряд уснувшего устройства.</summary>
    internal const int KeepAsleepMinutes = 30;
    /// <summary>Сколько показывать заряд, прочитанный при запуске игры, если во время игры устройство не опрашивается (батареи гарнитур хватает на много часов).</summary>
    internal const int KeepInGameMinutes = 300;
    /// <summary>Сколько секунд после запуска игры ещё можно опросить устройство, прерывающее звук (игра в это время загружается).</summary>
    internal const int GameStartReadSec = 90;

    /// <summary>Марки, у которых программа знает свой протокол заряда.</summary>
    internal static string BrandsText => string.Join(", ", Protocols.Select(p => p.Brand).Distinct());

    /// <summary>Один опрос всех известных устройств. inGame — идёт игра; quiet — проверка тишины в звуковом устройстве (для проверок — подставная).</summary>
    internal static List<DeviceCharge> Read(List<DeviceBattery.HidIface> hid, bool inGame, List<string> log, Func<string, bool?> quiet = null)
    {
        quiet ??= AudioQuiet.IsQuiet;
        var res = new List<DeviceCharge>();
        var done = new HashSet<string>();          // одно подключение — один протокол
        var unsupported = new HashSet<string>();   // VID:PID, у которых заряда нет (проводная модель): другие интерфейсы не спрашиваются
        foreach (var proto in Protocols)
        {
            foreach (var group in hid.Where(proto.Matches).GroupBy(d => d.Group))
            {
                // Коллекции подключения: сначала те, куда можно отправить запрос (есть выходной или feature-отчёт).
                var candidates = group.OrderByDescending(d => d.OutLen > 0 || proto.Passive).ThenByDescending(d => d.FeatLen > 0).ToList();
                var iface = candidates[0];
                if (!done.Add(iface.Group)) continue;
                var model = proto.Models?.First(m => m.Pid == iface.Pid);
                var all = hid.Where(x => x.Group == iface.Group || (x.Vid == iface.Vid && x.Pid == iface.Pid)).ToList();
                string product = all.Select(x => x.Product).FirstOrDefault(p => p.Length > 0) ?? "";
                var kind = model?.Kind ?? DeviceBattery.KindFromCollections(all.Select(x => (x.UsagePage, x.Usage)), product);
                string hw = $"{proto.Vid:X4}:{iface.Pid:X4}";
                // Геймпадов одной модели может быть два (вдвоём, по кабелю и по Bluetooth) — у каждого подключения своя строка.
                string key = "hid:" + hw + (proto.Passive ? ":" + (uint)iface.Group.GetHashCode() : "");
                if (!proto.Passive && (res.Any(x => x.Hw == hw && x.Known) || unsupported.Contains(hw))) continue;   // то же устройство через другой интерфейс
                string name = DeviceBattery.FriendlyName(kind, string.IsNullOrEmpty(model?.Name) ? DeviceBattery.CleanModel(product) : model.Name, proto.Brand);
                DeviceCharge Make(Reading r, string note) => new()
                {
                    Key = key, Name = name, Kind = kind, Percent = r.Percent, Charging = r.Percent != null ? r.Charging : null, Approx = r.Approx,
                    Source = L.F("протокол {0}", proto.Brand), Note = note ?? "", Hw = hw,
                };

                DateTime asked;
                bool early;
                lock (lastGood) early = proto.MinMinutes > 0 && lastAsked.TryGetValue(key, out asked) && (DateTime.Now - asked).TotalMinutes < proto.MinMinutes;
                // Опрос, прерывающий звук, во время игры — только в первые полторы минуты (игра ещё загружается); дальше — заряд на начало игры.
                bool starting = inGame && GameHooks.StartedAt != DateTime.MinValue && (DateTime.Now - GameHooks.StartedAt).TotalSeconds < GameStartReadSec;
                bool gameBlock = proto.NotInGame && inGame && !starting;
                // Вне игры такое устройство опрашивается только в тишине: в гарнитуре не играет звук и не говорят в микрофон.
                bool quietBlock = proto.QuietMark != null && !gameBlock && !early && !starting && quiet(proto.QuietMark) != true;
                bool skip = gameBlock || early || quietBlock;
                Reading reading;
                if (skip)
                {
                    log?.Add($"{proto.Id} {hw}: в этот раз не опрашивается ({(gameBlock ? "идёт игра" : quietBlock ? "в гарнитуре звук" : "рано")})");
                    reading = Reading.NoAnswer;
                }
                else
                {
                    lock (lastGood) lastAsked[key] = DateTime.Now;
                    string problem = null;
                    reading = Reading.NoAnswer;
                    foreach (var c in candidates)
                    {
                        // Следующая коллекция — только если в эту запрос не ушёл (нет такого отчёта, не открылась).
                        try
                        {
                            using var port = Port.Open(c, proto.FeatureOnly, proto.Passive);
                            reading = proto.Read(port, c.Pid);
                            problem = port.Problem;
                        }
                        catch (Exception ex) { problem = ex.Message; reading = Reading.NoAnswer; }
                        if (!reading.Asleep || problem == null) break;
                    }
                    log?.Add($"{proto.Id} {hw}: {(reading.Percent is int p ? p + " %" + (reading.Charging == true ? ", заряжается" : "") : reading.Unsupported ? "заряда нет (проводная модель)" : reading.Asleep ? "не ответило" : "заряд не прочитан")}" +
                             (problem != null && reading.Percent == null ? $" ({problem})" : ""));
                }
                if (reading.Unsupported) { unsupported.Add(hw); continue; }

                if (reading.Percent != null)
                {
                    Last before;
                    lock (lastGood) lastGood.TryGetValue(key, out before);
                    reading = Smooth(reading, before?.Value, before == null ? 0 : (DateTime.Now - before.At).TotalHours);
                    lock (lastGood) lastGood[key] = new Last(reading, DateTime.Now);
                    res.Add(Make(reading, proto.Tested ? null : L.T("Эта модель читается по описанию протокола — если заряд расходится с фирменной программой, напишите нам.")));
                    continue;
                }
                Last last;
                lock (lastGood) lastGood.TryGetValue(key, out last);
                bool lastAskOk;
                lock (lastGood) lastAskOk = lastGood.TryGetValue(key, out var lg) && (!lastAsked.TryGetValue(key, out var la) || lg.At >= la);
                bool held = gameBlock || quietBlock || (early && proto.QuietMark != null && lastAskOk);
                if (reading.Charging != true && last != null && (DateTime.Now - last.At).TotalMinutes <= (held ? KeepInGameMinutes : KeepAsleepMinutes))
                {
                    int ago = Math.Max(1, (int)(DateTime.Now - last.At).TotalMinutes);
                    string at = last.At.ToString("HH:mm", L.Culture);
                    res.Add(Make(last.Value,
                        gameBlock ? L.F("Во время игры заряд этой гарнитуры не читается — показан на {0}, обновится после игры.", at)
                        : quietBlock ? L.F("Показан заряд на {0}: обновится в паузе, когда в гарнитуре стихнет звук (при чтении он прерывается на долю секунды).", at)
                        : skip ? null : L.F("Устройство сейчас не отвечает (спит или выключено) — показан заряд {0} мин назад.", ago)));
                }
                else if (reading.Charging == true)
                    res.Add(Make(default, L.T("Заряжается — пока устройство на зарядке, процент оно не сообщает.")));
                else if (gameBlock)
                    res.Add(Make(default, L.T("Заряд прочитается после игры: во время игры чтение прервало бы звук в гарнитуре.")));
                else if (quietBlock)
                    res.Add(Make(default, L.T("Заряд прочитается в паузе, когда в гарнитуре стихнет звук: при чтении он прерывается на долю секунды.")));
                else if (reading.Asleep)
                    res.Add(Make(default, kind == DeviceKind.Mouse
                        ? L.T("Мышь не ответила — наверное, спит. Пошевелите ею — заряд появится при следующем опросе.")
                        : L.T("Устройство не ответило — наверное, спит или выключено. Включите его — заряд появится при следующем опросе.")));
                else
                    res.Add(Make(default, L.T("Устройство ответило, но заряд в ответе не указан.")));
            }
        }
        return res;
    }

    /// <summary>Заряд по напряжению не падает быстрее этого (проценты в час): гарнитуры и мыши садятся за много часов.</summary>
    internal const double MaxDrainPerHour = 10;

    /// <summary>
    /// Проценты по напряжению (Approx) скачут: под нагрузкой (громкий звук, подсветка) напряжение проседает, потом возвращается.
    /// Поэтому без зарядки показание не падает быстрее <see cref="MaxDrainPerHour"/> от прошлого; рост — как есть (напряжение восстановилось).
    /// </summary>
    internal static Reading Smooth(Reading now, Reading? before, double hours)
    {
        if (now.Percent is not int p || !now.Approx || now.Charging == true) return now;
        if (before is not { Percent: int was } b || b.Charging == true || hours < 0 || hours > 6) return now;
        int floor = was - (int)Math.Ceiling(hours * MaxDrainPerHour);
        return p < floor ? now with { Percent = Math.Clamp(floor, 0, 100) } : now;
    }

    // ───────────────────────── Windows ─────────────────────────

    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, FileShareReadWrite = 0x3, OpenExisting = 3, FileFlagOverlapped = 0x40000000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("hid.dll")] private static extern bool HidD_FlushQueue(SafeFileHandle h);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
}
