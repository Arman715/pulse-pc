using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PulsePC.Services;

/// <summary>
/// Таблица устройств: какая коллекция, какой запрос и где в ответе заряд. Байты запросов — по открытым описаниям протоколов;
/// смещения — в буфере Windows (номер отчёта в байте 0; у отчётов без номера там 0).
/// </summary>
internal static partial class VendorBattery
{

    private static Model M(int pid, string name, DeviceKind kind) => new(pid, name, kind);
    private static Model[] Ms(DeviceKind kind, string name, params int[] pids) => pids.Select(p => M(p, name, kind)).ToArray();
    private const DeviceKind Mouse = DeviceKind.Mouse, Headset = DeviceKind.Headset;

    internal static readonly List<Protocol> Protocols = new()
    {
        // ───────── HyperX ─────────
        // Pulsefire Haste (первая модель): MI_02, FF00/0001, 65 байт без номера. 00 51 → [5] процент, [6] 0 батарея / 1 заряжается / 2 заряжена.
        new Protocol
        {
            Id = "hyperx-haste", Brand = "HyperX", Vid = 0x03F0, Page = 0xFF00, Usage = 0x0001, InLen = 65, OutLen = 65,
            Models = Ms(Mouse, "Pulsefire Haste Wireless", 0x028E, 0x048E), Tested = true,
            Read = (p, _) => PercentStatus(p.Ask(new byte[] { 0x00, 0x51 }, r => Has(r, (0, 0x00), (1, 0x51)) && r.Length > 6), 5, 6),
        },
        // Pulsefire Dart: тот же запрос, но ответ годен, только если [1..4] = 51 00 00 03 (иначе мышь спит).
        new Protocol
        {
            Id = "hyperx-dart", Brand = "HyperX", Vid = 0x0951, Page = 0xFF00, Usage = 0x0001, Mi = 2, OutLen = 65,
            Models = Ms(Mouse, "Pulsefire Dart", 0x16E1),
            Read = (p, _) =>
            {
                var r = p.Ask(new byte[] { 0x00, 0x51 }, x => Has(x, (1, 0x51)) && x.Length > 6);
                if (r == null || !Has(r, (2, 0x00), (3, 0x00), (4, 0x03))) return Reading.NoAnswer;
                return Reading.Pct(r[5] is >= 1 and <= 100 ? r[5] : null, r[6] == 1);
            },
        },
        // Pulsefire Haste 2 и Cloud Alpha 2: 52 01 → 53 01, [2] = мышь на связи; 50 02 → 51 02, [2] процент, [3] 1 заряжается / 2 заряжена.
        new Protocol
        {
            Id = "hyperx-haste2", Brand = "HyperX", Vid = 0x03F0, Page = 0xFF00, Usage = 0x0001, Mi = 2,
            Models = Ms(Mouse, "Pulsefire Haste 2 Wireless", 0x0F98, 0x0D97).Concat(new[] { M(0x0AB5, "Pulsefire Haste 2 Core Wireless", Mouse), M(0x0DA0, "Pulsefire Haste 2 Mini Wireless", Mouse) }).ToArray(),
            Read = (p, _) => Haste2(p),
        },
        new Protocol
        {
            Id = "hyperx-alpha2", Brand = "HyperX", Vid = 0x03F0, Page = 0xFF13, Usage = 0xFF00,
            Models = Ms(Headset, "Cloud Alpha 2 Wireless", 0x08BE),
            Read = (p, _) => Haste2(p),
        },
        // Cloud III Wireless и Cloud II Core Wireless: 66 82 → [2] на связи; 66 89 → [4] процент; 66 8A → [2] = 1 заряжается.
        new Protocol
        {
            Id = "hyperx-cloud3", Brand = "HyperX", Vid = 0x03F0, Page = 0, OutLen = 62,
            Models = Ms(Headset, "Cloud III Wireless", 0x0C9D, 0x05B7, 0x0FCC).Concat(Ms(Headset, "Cloud II Core Wireless", 0x069F, 0x0995)).ToArray(),
            Read = (p, _) =>
            {
                byte[] Cmd(byte c) => p.Ask(new byte[] { 0x66, c }, r => Has(r, (0, 0x66), (1, c)) && r.Length > 4);
                var link = Cmd(0x82);
                if (link == null || link[2] == 0) return Reading.NoAnswer;
                var bat = Cmd(0x89);
                if (bat == null) return Reading.NoAnswer;
                var chg = Cmd(0x8A);
                return Reading.Pct(bat[4], chg?[2] == 1);
            },
        },
        // Cloud II Wireless (HP): FF90/0303. 06 FF BB 02 → [7] процент; 06 FF BB 03 → [4] = 1 заряжается.
        new Protocol
        {
            Id = "hyperx-cloud2", Brand = "HyperX", Vid = 0x03F0, Page = 0xFF90, Usage = 0x0303,
            Models = Ms(Headset, "Cloud II Wireless", 0x0696, 0x018B, 0x0D93, 0x0C8C),
            Read = (p, _) =>
            {
                byte[] Cmd(byte c) => p.Ask(new byte[] { 0x06, 0xFF, 0xBB, c }, r => Has(r, (0, 0x06), (1, 0xFF), (2, 0xBB), (3, c)) && r.Length > 7);
                var bat = Cmd(0x02);
                if (bat == null) return Reading.NoAnswer;
                return Reading.Pct(bat[7], Cmd(0x03)?[4] == 1);
            },
        },
        // Cloud II Wireless (Kingston) и Cloud Flight S: FF13/0001, 62 байта. Длинный запрос с кодом команды в [15]; ответ 0B .. BB cmd.
        new Protocol
        {
            Id = "hyperx-cloud2k", Brand = "HyperX", Vid = 0x0951, Page = 0xFF13, Usage = 0x0001,
            Models = new[] { M(0x1718, "Cloud II Wireless", Headset), M(0x16EA, "Cloud Flight S", Headset), M(0x16EB, "Cloud Flight S", Headset) },
            Read = (p, _) =>
            {
                byte[] Cmd(byte c) => p.Ask(new byte[] { 0x06, 0x00, 0x02, 0x00, 0x9A, 0x00, 0x00, 0x68, 0x4A, 0x8E, 0x0A, 0x00, 0x00, 0x00, 0xBB, c, 0x00 },
                                            r => Has(r, (0, 0x0B), (2, 0xBB), (3, c)) && r.Length > 7);
                var bat = Cmd(0x02);
                if (bat == null) return Reading.NoAnswer;
                return Reading.Pct(bat[7], Cmd(0x03)?[4] == 1);
            },
        },
        // Cloud Alpha Wireless: 21 BB 03 → [3] = 1 выключена; 21 BB 0B → [3] процент; 21 BB 0C → [3] = 1 заряжается.
        new Protocol
        {
            Id = "hyperx-alpha", Brand = "HyperX", Vid = 0x03F0, Page = 0,
            Models = Ms(Headset, "Cloud Alpha Wireless", 0x098D),
            Read = (p, _) =>
            {
                byte[] Cmd(byte c) => p.Ask(new byte[] { 0x21, 0xBB, c }, r => Has(r, (0, 0x21), (2, c)) && r.Length > 3);
                var off = Cmd(0x03);
                if (off == null || off[3] == 1) return Reading.NoAnswer;
                var bat = Cmd(0x0B);
                if (bat == null) return Reading.NoAnswer;
                return Reading.Pct(bat[3], Cmd(0x0C)?[3] == 1);
            },
        },
        // Cloud Flight: 21 FF 05 → [3..4] напряжение (старший байт первым). Выше 4107 mV — на зарядке.
        new Protocol
        {
            Id = "hyperx-flight", Brand = "HyperX", Vid = 0x0951, Page = 0,
            Models = Ms(Headset, "Cloud Flight", 0x16C4, 0x1723, 0x1749),
            Read = (p, _) => FlightVoltage(p),
        },
        new Protocol
        {
            Id = "hyperx-flight-hp", Brand = "HyperX", Vid = 0x03F0, Page = 0,
            Models = Ms(Headset, "Cloud Flight", 0x0E90),
            Read = (p, _) => FlightVoltage(p),
        },
        // Cloud Stinger Core Wireless: feature-отчёт FF; ответ FF 12 05, [9] 03 батарея / 05 заряжается / 06 заряжена, [11..12] напряжение.
        new Protocol
        {
            Id = "hyperx-stinger", Brand = "HyperX", Vid = 0x03F0, Page = 0xFF00, Usage = 0x0001, Mi = 3,
            Models = Ms(Headset, "Cloud Stinger Core Wireless", 0x0A93, 0x0A8E),
            Read = (p, _) => Stinger(p),
        },
        new Protocol
        {
            Id = "hyperx-stinger-k", Brand = "HyperX", Vid = 0x0951, Page = 0xFF00, Usage = 0x0001, Mi = 3,
            Models = Ms(Headset, "Cloud Stinger Core Wireless", 0x170B, 0x1713, 0x173F),
            Read = (p, _) => Stinger(p),
        },

        // ───────── SteelSeries ─────────
        // Arctis 1 / 7X / 7P: FF43/0202. 06 12 → [2] = 1 выключена, [3] процент.
        new Protocol
        {
            Id = "ss-arctis1", Brand = "SteelSeries", Vid = 0x1038, Page = 0xFF43, Usage = 0x0202,
            Models = new[] { M(0x12B3, "Arctis 1 Wireless", Headset), M(0x12B6, "Arctis 1 Wireless", Headset), M(0x12D7, "Arctis 7X", Headset), M(0x12D5, "Arctis 7P", Headset) },
            Read = (p, _) =>
            {
                var r = p.Ask(new byte[] { 0x06, 0x12 }, x => Has(x, (0, 0x06), (1, 0x12)) && x.Length > 3, 1000);
                return r == null || r[2] == 1 ? Reading.NoAnswer : Reading.Pct(r[3]);
            },
        },
        // Arctis 7 / Pro (2019): FF43/0202. 06 18 → [2] процент (0 — выключена).
        new Protocol
        {
            Id = "ss-arctis7", Brand = "SteelSeries", Vid = 0x1038, Page = 0xFF43, Usage = 0x0202,
            Models = Ms(Headset, "", 0x1260, 0x12AD, 0x1252, 0x1280),
            Read = (p, _) =>
            {
                var r = p.Ask(new byte[] { 0x06, 0x18 }, x => Has(x, (0, 0x06), (1, 0x18)) && x.Length > 2, 1000);
                return r == null || r[2] == 0 ? Reading.NoAnswer : Reading.Pct(r[2]);
            },
        },
        // Arctis Nova Pro Wireless: MI_04. 06 B0 → [6] уровень 0…8, [15] 1 выключена / 2 заряжается.
        new Protocol
        {
            Id = "ss-novapro", Brand = "SteelSeries", Vid = 0x1038, Page = 0, Mi = 4,
            Models = Ms(Headset, "Arctis Nova Pro Wireless", 0x12E0, 0x12E5, 0x225D),
            Read = (p, _) =>
            {
                var r = p.Ask(new byte[] { 0x06, 0xB0 }, x => Has(x, (0, 0x06), (1, 0xB0)) && x.Length > 15, 1000);
                if (r == null || r[15] == 1) return Reading.NoAnswer;
                return Reading.Pct(Steps(r[6], 8), r[15] == 2, approx: true);
            },
        },
        // Arctis Nova 7 / 5 / 3P / 7+: FFC0/0001, MI_03, 64 байта без номера. 00 B0 → поля зависят от модели.
        new Protocol
        {
            Id = "ss-nova", Brand = "SteelSeries", Vid = 0x1038, Page = 0xFFC0, Usage = 0x0001, Mi = 3,
            Models = Ms(Headset, "Arctis Nova 7", 0x2202, 0x2206, 0x220A, 0x223A, 0x227A, 0x22A4, 0x22A1, 0x227E, 0x2258, 0x229E, 0x22AD, 0x22A9, 0x22A5, 0x22A7)
                .Concat(Ms(Headset, "Arctis Nova 5", 0x2232, 0x2253)).Concat(Ms(Headset, "Arctis Nova 3P", 0x2269, 0x226D))
                .Concat(Ms(Headset, "Arctis 7+", 0x220E, 0x2212, 0x2216, 0x2236)).ToArray(),
            Read = (p, pid) =>
            {
                var r = p.Ask(new byte[] { 0x00, 0xB0 }, x => Has(x, (1, 0xB0)) && x.Length > 5, 1000);
                if (r == null) return Reading.NoAnswer;
                if (pid is 0x2232 or 0x2253 or 0x2269 or 0x226D)          // Nova 5 / 3P: процент
                    return r[2] == 2 ? Reading.NoAnswer : Reading.Pct(r[4], r[5] == 1);
                if (pid is 0x220E or 0x2212 or 0x2216 or 0x2236)          // 7+: уровень 0…4
                    return r[2] == 1 ? Reading.NoAnswer : Reading.Pct(Steps(r[3], 4), r[4] == 1, approx: true);
                if (r[4] == 0) return Reading.NoAnswer;                    // Nova 7: [4] 0 — выключена, 1 и 2 — заряжается
                bool steps = pid is 0x2202 or 0x2206 or 0x220A or 0x223A or 0x227A or 0x22A4;
                return Reading.Pct(steps ? Steps(r[3], 4) : r[3], r[4] is 1 or 2, approx: steps);
            },
        },
        // Мыши Aerox и Prime: MI_03, без номера. Через приёмник 00 D2, по проводу 00 92. [2]: младшие 7 бит n = 1…21 → (n−1)·5 %, бит 7 — зарядка.
        // Aerox 3 Gen 2: [2] 0 батарея / 1 заряжается / 2 заряжена, [3] процент. Ответ 40 FF — приёмник не видит мышь.
        new Protocol
        {
            Id = "ss-aerox", Brand = "SteelSeries", Vid = 0x1038, Page = 0, Mi = 3,
            Models = new[]
            {
                M(0x1838, "Aerox 3 Wireless", Mouse), M(0x183A, "Aerox 3 Wireless", Mouse), M(0x1878, "Aerox 3 Wireless", Mouse), M(0x187A, "Aerox 3 Wireless", Mouse),
                M(0x1852, "Aerox 5 Wireless", Mouse), M(0x1854, "Aerox 5 Wireless", Mouse), M(0x185C, "Aerox 5 Wireless Destiny", Mouse), M(0x185E, "Aerox 5 Wireless Destiny", Mouse),
                M(0x1858, "Aerox 9 Wireless", Mouse), M(0x185A, "Aerox 9 Wireless", Mouse), M(0x1840, "Prime Wireless", Mouse), M(0x1842, "Prime Wireless", Mouse),
                M(0x1860, "Aerox 5 Wireless Diablo", Mouse), M(0x1890, "Aerox 3 Wireless Gen 2", Mouse), M(0x1892, "Aerox 3 Wireless Gen 2", Mouse),
            },
            Read = (p, pid) =>
            {
                bool cable = pid is 0x183A or 0x187A or 0x1854 or 0x185E or 0x185A or 0x1842 or 0x1892;
                byte cmd = cable ? (byte)0x92 : (byte)0xD2;
                byte[] r = null;
                for (int i = 0; i < 8 && r == null; i++)               // SteelSeries GG может перехватить ответ — повтор каждые 120 мс
                    r = p.Ask(new byte[] { 0x00, cmd }, x => x.Length > 3 && (Has(x, (1, cmd)) || Has(x, (1, 0x40), (2, 0xFF))), 120);
                if (r == null || r[1] == 0x40) return Reading.NoAnswer;
                if (pid is 0x1890 or 0x1892) return r[3] == 0xFF ? Reading.NoAnswer : Reading.Pct(r[3], r[2] is 1 or 2);
                int n = r[2] & 0x7F;
                if (n == 0) return Reading.NoAnswer;
                return Reading.Pct(n is >= 1 and <= 21 ? (n - 1) * 5 : null, (r[2] & 0x80) != 0, approx: true);
            },
        },

        // ───────── Corsair ─────────
        // Void / HS70 / HS80 (старые приёмники): FFC5/0001, MI_03. C9 64 → входной отчёт 64: [2] & 7F процент, [4] 0 выключена, 4–5 заряжается.
        new Protocol
        {
            Id = "corsair-void", Brand = "Corsair", Vid = 0x1B1C, Page = 0xFFC5, Usage = 0x0001, Mi = 3,
            Models = Ms(Headset, "", 0x1B1C, 0x1B27, 0x0A14, 0x0A16, 0x0A17, 0x0A1D, 0x0A1A, 0x1B2A, 0x1B23, 0x1B29, 0x0A55, 0x0A51, 0x0A52, 0x0A38, 0x0A4F, 0x0A2B, 0x0A75, 0x0A56),
            Read = (p, _) =>
            {
                var r = p.Ask(new byte[] { 0xC9, 0x64 }, x => Has(x, (0, 0x64)) && x.Length > 4, 1000);
                if (r == null || r[4] == 0) return Reading.NoAnswer;
                return Reading.Pct(r[2] & 0x7F, r[4] is 4 or 5);
            },
        },
        // Новые приёмники (Void V2, Virtuoso MAX, HS80 MAX и др.): FF42. Запрос свойства 02 <цель> 02 <свойство>; 0F — заряд 0…1000, 10 — 1 заряжается / 3 заряжена.
        new Protocol
        {
            // Любая модель с этой коллекцией (гарнитуры, мыши и клавиатуры на приёмниках Slipstream): запрос свойства — только чтение.
            Id = "corsair-bragi", Brand = "Corsair", Vid = 0x1B1C, Page = 0xFF42,
            Read = (p, _) => Bragi(p),
        },

        // ───────── Razer ─────────
        // Гарнитура BlackShark V2 HyperSpeed: FF14, MI_03, выходной отчёт 02 (64 байта). Команда 21 — заряд, 2A — зарядка; ответ [13].
        new Protocol
        {
            Id = "razer-blackshark", Brand = "Razer", Vid = 0x1532, Page = 0xFF14, Mi = 3,
            Models = Ms(Headset, "BlackShark V2 HyperSpeed", 0x0565),
            Read = (p, _) =>
            {
                byte[] Cmd(byte c, byte seq)
                {
                    var q = new byte[64];
                    q[0] = 0x02; q[2] = (byte)(0x60 | seq); q[6] = 0x04; q[9] = 0x80; q[10] = c;
                    byte x = 0; for (int i = 0; i < 62; i++) x ^= q[i];
                    q[62] = x;
                    return p.Ask(q, r => Has(r, (0, 0x02), (10, c)) && r.Length > 13 && (r[2] & 0x1F) == seq && r[12] >= 1, 1000);
                }
                var bat = Cmd(0x21, 1);
                if (bat == null) return Reading.NoAnswer;
                return Reading.Pct(bat[13], Cmd(0x2A, 2)?[13] == 1);
            },
        },
        // Мыши и клавиатуры: feature-отчёт 90 байт (91 с номером). Класс 07, команда 80 — заряд 0…255, 84 — зарядка.
        new Protocol
        {
            Id = "razer", Brand = "Razer", Vid = 0x1532, FeatureOnly = true, FeatLen = 91,
            Read = (p, pid) => Razer(p, pid),
        },

        // ───────── Logitech G435 ─────────
        // Старый приёмник 0ACB: чтение — только через служебный режим приёмника (звук и микрофон пропадают на ~0,4 с). Поэтому вне игры —
        // только в тишине, в игре — лишь пока она загружается, и не чаще раза в 20 минут.
        new Protocol
        {
            Id = "logi-g435", Brand = "Logitech", Vid = 0x046D, Page = 0xFFC0, Usage = 0x0001, FeatLen = 64,
            Models = Ms(Headset, "G435", 0x0ACB), Tested = true,
            QuietMark = "G435", NotInGame = true, MinMinutes = 20,
            Read = (p, _) => G435Old(p),
        },
        // Новый приёмник 0ADF: FF00/0001, 65 байт без номера; кадры с CRC. Те же правила, что у старого.
        new Protocol
        {
            Id = "logi-g435-new", Brand = "Logitech", Vid = 0x046D, Page = 0xFF00, Usage = 0x0001, OutLen = 65,
            Models = Ms(Headset, "G435", 0x0ADF),
            QuietMark = "G435", NotInGame = true, MinMinutes = 20,
            Read = (p, _) => G435New(p),
        },

        // ───────── геймпады PlayStation: заряд в обычном входном отчёте, программа только слушает ─────────
        // DualShock 4: отчёт 01 (USB, приёмник) — [30], по Bluetooth отчёт 11 — [32]: младшие 4 бита — уровень 0…10 (11 — заряжен), бит 4 — подключён кабель.
        new Protocol
        {
            Id = "sony-ds4", Brand = "Sony", Vid = 0x054C, Page = 0x0001, Usage = 0x0005, Passive = true,
            Models = Ms(DeviceKind.Gamepad, "DualShock 4", 0x05C4, 0x09CC, 0x0BA0),
            Read = (p, _) =>
            {
                // По Bluetooth обычный отчёт 01 короткий (Windows дополняет его нулями) — заряда в нём нет; полный отчёт — 11.
                var r = p.Receive(x => p.Bluetooth ? x[0] is 0x01 or 0x11 : x[0] == 0x01, 600);
                if (r == null) return Reading.NoAnswer;
                if (p.Bluetooth && r[0] == 0x01) return Reading.None;
                return r.Length > (r[0] == 0x11 ? 32 : 30) ? Ds4Battery(r[0] == 0x11 ? r[32] : r[30]) : Reading.None;
            },
        },
        // DualSense: отчёт 01 (USB) — [53], по Bluetooth отчёт 31 — [54]: младшие 4 бита — уровень 0…10, старшие — 0 от батареи / 1 заряжается / 2 заряжен.
        new Protocol
        {
            Id = "sony-dualsense", Brand = "Sony", Vid = 0x054C, Page = 0x0001, Usage = 0x0005, Passive = true,
            Models = new[] { M(0x0CE6, "DualSense", DeviceKind.Gamepad), M(0x0DF2, "DualSense Edge", DeviceKind.Gamepad) },
            Read = (p, _) =>
            {
                // По Bluetooth обычный отчёт 01 короткий (Windows дополняет его нулями) — заряда в нём нет; полный отчёт — 31.
                var r = p.Receive(x => p.Bluetooth ? x[0] is 0x01 or 0x31 : x[0] == 0x01, 600);
                if (r == null) return Reading.NoAnswer;
                if (p.Bluetooth && r[0] == 0x01) return Reading.None;
                return r.Length > (r[0] == 0x31 ? 54 : 53) ? DualSenseBattery(r[0] == 0x31 ? r[54] : r[53]) : Reading.None;
            },
        },

        // ───────── другие мыши ─────────
        // Glorious: feature-отчёт 0 (65 байт). [3]=02 [4]=02 [6]=83 → [1] A1 на связи / A4 спит / A0 просыпается, [7] зарядка, [8] процент.
        new Protocol
        {
            Id = "glorious", Brand = "Glorious", Vid = 0x258A, Page = 0, FeatLen = 65,
            Models = new[]
            {
                M(0x2022, "Model O Wireless", Mouse), M(0x2011, "Model O Wireless", Mouse), M(0x2023, "Model D Wireless", Mouse), M(0x2012, "Model D Wireless", Mouse),
                M(0x2024, "Model O- Wireless", Mouse), M(0x2013, "Model O- Wireless", Mouse), M(0x2025, "Model D- Wireless", Mouse), M(0x2014, "Model D- Wireless", Mouse),
                M(0x2033, "Model O 2 Wireless", Mouse), M(0x2034, "Model D 2 PRO", Mouse), M(0x2036, "Model D 2 PRO", Mouse),
            },
            Read = (p, _) =>
            {
                var q = new byte[65]; q[3] = 0x02; q[4] = 0x02; q[6] = 0x83;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var r = p.AskFeature(q, 0x00, x => Has(x, (6, 0x83)) && x.Length > 8, 50, 50, 4);
                    if (r == null) return Reading.NoAnswer;
                    if (r[1] == 0xA4) return Reading.NoAnswer;
                    if (r[1] == 0xA1) return Reading.Pct(r[8], r[7] == 1);
                    Thread.Sleep(400);                              // A0 — мышь просыпается
                }
                return Reading.NoAnswer;
            },
        },
        // Endgame Gear (приёмник 4K): FF01/0002, feature A1. A1 B4 00 → [1] 01 готово / 03 занято / 08, 11 спит; [16] процент.
        new Protocol
        {
            Id = "endgame", Brand = "Endgame Gear", Vid = 0x3367, Page = 0xFF01, Usage = 0x0002,
            Models = Ms(Mouse, "", 0x1970),
            Read = (p, _) =>
            {
                var r = p.AskFeature(new byte[] { 0xA1, 0xB4, 0x00 }, 0xA1, x => x.Length > 16 && x[1] != 0x03, 150, 50, 8);
                if (r == null || r[1] is 0x08 or 0x11) return Reading.NoAnswer;
                return r[1] == 0x01 ? Reading.Pct(r[16]) : Reading.NoAnswer;
            },
        },
        // ASUS ROG: MI_00, без номера. 00 12 07 → [1..2] = 12 07, [5] уровень, [10] зарядка; FF AA — мышь спит. У части моделей уровень 0…4.
        new Protocol
        {
            Id = "asus-rog", Brand = "ASUS ROG", Vid = 0x0B05, Page = 0, Mi = 0,
            Models = new[]
            {
                M(0x18E5, "Chakram", Mouse), M(0x18E3, "Chakram", Mouse), M(0x18A0, "Gladius II Wireless", Mouse), M(0x1960, "Keris Wireless", Mouse), M(0x195E, "Keris Wireless", Mouse),
                M(0x197F, "Gladius III Wireless", Mouse), M(0x197D, "Gladius III Wireless", Mouse), M(0x1A68, "Keris Wireless AimPoint", Mouse), M(0x1A66, "Keris Wireless AimPoint", Mouse),
                M(0x1A94, "Harpe Ace", Mouse), M(0x1A92, "Harpe Ace", Mouse),
            },
            Read = (p, pid) =>
            {
                var r = p.Ask(new byte[] { 0x00, 0x12, 0x07 }, x => x.Length > 10 && (Has(x, (1, 0x12), (2, 0x07)) || Has(x, (1, 0xFF), (2, 0xAA))));
                if (r == null || r[1] == 0xFF) return Reading.NoAnswer;
                bool quarters = pid is 0x18E5 or 0x18E3 or 0x18A0 or 0x1960 or 0x195E;
                return Reading.Pct(quarters ? r[5] * 25 : r[5], r[10] != 0, approx: quarters);
            },
        },
        // Zowie EC-CW: 65 байт без номера. 00 A5 03 → [1..2] напряжение, [3] процент.
        new Protocol
        {
            Id = "zowie", Brand = "Zowie", Vid = 0x04A5, Page = 0, OutLen = 65,
            Models = Ms(Mouse, "EC-CW", 0x8006, 0x8005),
            Read = (p, _) =>
            {
                var r = p.Ask(new byte[] { 0x00, 0xA5, 0x03 }, x => x.Length > 3 && x[3] <= 100 && Le16(x, 1) is >= 3000 and <= 4500);
                return r == null ? Reading.NoAnswer : Reading.Pct(r[3]);
            },
        },
        // Pulsar X2 V3 и родственные: FFFF/0001, MI_03, feature-отчёт 0 (65 байт). 00 00 08 81 01 + сумма [1..62] в [63..64]; ответ [1] = 01, [2..4] = 08 81 01, [7] процент.
        new Protocol
        {
            Id = "pulsar", Brand = "Pulsar", Vid = 0x3710, Page = 0xFFFF, Usage = 0x0001, Mi = 3,
            Models = new[] { M(0x3402, "", Mouse), M(0x3406, "", Mouse), M(0x5402, "", Mouse), M(0x5403, "", Mouse), M(0x5404, "Feinmann", Mouse) },
            Read = (p, _) =>
            {
                var q = new byte[65]; q[2] = 0x08; q[3] = 0x81; q[4] = 0x01;
                int sum = 0; for (int i = 1; i <= 62; i++) sum += q[i];
                q[63] = (byte)(sum & 0xFF); q[64] = (byte)(sum >> 8);
                var r = p.AskFeature(q, 0x00, x => x.Length > 7 && Has(x, (1, 0x01), (2, 0x08), (3, 0x81), (4, 0x01)), 50, 50, 20);
                return r == null || r[7] == 0xFF ? Reading.NoAnswer : Reading.Pct(r[7]);
            },
        },
    };

    // ───────────────────────── разбор по протоколам ─────────────────────────

    /// <summary>Ответ «процент + состояние»: процент 1…100 (0 и 0xFF — неизвестен), состояние 1 — заряжается, 2 — заряжена (на проводе).</summary>
    internal static Reading PercentStatus(byte[] r, int pctAt, int stateAt)
    {
        if (r == null) return Reading.NoAnswer;
        int pct = r.Length > pctAt ? r[pctAt] : 0;
        int st = r.Length > stateAt ? r[stateAt] : 0;
        return Reading.Pct(pct is >= 1 and <= 100 ? pct : null, st is 1 or 2);
    }

    private static Reading Haste2(Port p)
    {
        // После переподключения приёмник шлёт служебные отчёты 44/45/FB — они не подходят под ответ и пропускаются.
        var link = p.Ask(new byte[] { 0x52, 0x01 }, r => Has(r, (0, 0x53), (1, 0x01)) && r.Length > 2);
        if (link == null || link[2] == 0) return Reading.NoAnswer;
        var r = p.Ask(new byte[] { 0x50, 0x02 }, x => Has(x, (0, 0x51), (1, 0x02)) && x.Length > 3);
        return r == null ? Reading.NoAnswer : Reading.Pct(r[2], r[3] is 1 or 2);
    }

    /// <summary>Cloud Flight: напряжение → проценты (кривая производителя: до 3648 mV почти линейно, выше 3975 — полный).</summary>
    internal static int? FlightPercent(int mv)
    {
        if (mv <= 0) return null;
        if (mv <= 3648) return (int)Math.Clamp(Math.Round(0.00125 * mv), 0, 100);
        if (mv > 3975) return 100;
        double v = mv;
        double pct = 2.547505e-8 * Math.Pow(v, 4) - 3.900299e-4 * Math.Pow(v, 3) + 2.238321 * v * v - 5706.256 * v + 5452299;
        return (int)Math.Clamp(Math.Round(pct), 0, 100);
    }

    private static Reading FlightVoltage(Port p)
    {
        var r = p.Ask(new byte[] { 0x21, 0xFF, 0x05 }, x => Has(x, (0, 0x21), (1, 0xFF), (2, 0x05)) && x.Length > 4);
        if (r == null) return Reading.NoAnswer;
        int mv = Be16(r, 3);
        if (mv < 3000 || mv > 4400) return Reading.NoAnswer;
        bool charging = mv > 0x100B;
        return charging ? new Reading(null, true, true, false) : Reading.Pct(FlightPercent(mv), false, approx: true);
    }

    /// <summary>Stinger Core: таблица «процент → напряжение» шагом 10 % (на зарядке напряжение выше).</summary>
    private static readonly int[] StingerCharging = { 4130, 4090, 4050, 4000, 3967, 3946, 3930, 3916, 3871, 3831, 3400 };
    private static readonly int[] StingerBattery = { 4121, 4042, 3954, 3905, 3838, 3785, 3763, 3738, 3700, 3651, 3517 };

    internal static int? StingerPercent(int mv, bool charging)
    {
        var t = charging ? StingerCharging : StingerBattery;
        return Curve(mv, t.Select((v, i) => (v, 100 - i * 10)).ToArray());
    }

    private static Reading Stinger(Port p)
    {
        // Первым приходит прошлый ответ FF 01 00 — читаем, пока не придёт ответ на заряд FF 12 05.
        var r = p.AskFeature(new byte[] { 0xFF, 0x07, 0x00, 0xFD, 0x04, 0x0C, 0xF1, 0x02, 0x01, 0x04, 0xF0, 0x0C }, 0xFF,
                             x => Has(x, (0, 0xFF), (1, 0x12), (2, 0x05)) && x.Length > 12, 20, 20, 10);
        if (r == null) return Reading.NoAnswer;
        int mv = Le16(r, 11);
        if (mv < 3000) return Reading.NoAnswer;
        bool charging = r[9] is 0x05 or 0x06;
        return Reading.Pct(r[9] == 0x06 ? 100 : StingerPercent(mv, charging), charging, approx: true);
    }

    private static Reading Bragi(Port p)
    {
        int off = p.OutLen == 65 ? 1 : 0;
        bool replied = false;
        int? Get(byte target, byte prop)
        {
            var q = off == 1 ? new byte[] { 0x00, 0x02, target, 0x02, prop } : new byte[] { 0x02, target, 0x02, prop };
            var r = p.Ask(q, x => x.Length > off + 7 && x[off + 2] == 0x02 && x[off + 1] == (target & 7), 300);
            if (r == null) return null;
            replied = true;
            if (r[off + 3] != 0) return null;                 // свойства нет (проводное устройство, приёмник без устройства)
            return r[off + 4] | (r[off + 5] << 8) | (r[off + 6] << 16) | (r[off + 7] << 24);
        }
        foreach (byte target in new byte[] { 0x08, 0x09 })
        {
            var level = Get(target, 0x0F);
            if (level is not int v || v < 0 || v > 1000) continue;
            var state = Get(target, 0x10);
            return Reading.Pct((int)Math.Round(v / 10.0), state is 1 or 3);
        }
        return replied ? Reading.None : Reading.NoAnswer;
    }

    // ───────── PlayStation ─────────

    internal static Reading Ds4Battery(byte b)
    {
        int level = b & 0x0F;
        bool cable = (b & 0x10) != 0;
        if (level > 11 || (level == 0 && !cable)) return Reading.NoAnswer;   // 0 без кабеля — приёмник без геймпада
        return Reading.Pct(Math.Min(100, level * 10), cable && level < 11, approx: true);
    }

    internal static Reading DualSenseBattery(byte b)
    {
        int level = b & 0x0F, state = b >> 4;
        if (level > 10 || state > 2) return Reading.NoAnswer;
        return Reading.Pct(state == 2 ? 100 : Math.Min(100, level * 10 + 5), state == 1, approx: true);
    }

    // ───────── Razer: feature-отчёт 90 байт ─────────

    private static readonly byte[] RazerTransactions = { 0x1F, 0x3F, 0xFF, 0x08, 0x9F };
    private static readonly Dictionary<int, byte> razerTxn = new();

    internal static byte[] RazerRequest(byte txn, byte cmd)
    {
        var q = new byte[91];
        q[2] = txn; q[6] = 0x02; q[7] = 0x07; q[8] = cmd;
        byte x = 0; for (int i = 3; i <= 88; i++) x ^= q[i];
        q[89] = x;
        return q;
    }

    /// <summary>Ответ Razer: состояние в [1]; значение в [10]. null — ответа на этот запрос нет.</summary>
    internal static (int Status, int Value)? RazerReply(byte[] r, byte txn, byte cmd)
    {
        if (r == null || r.Length < 91 || r[2] != txn || r[6] != 0x02 || r[7] != 0x07 || r[8] != cmd || r[3] != 0 || r[4] != 0 || r[5] != 0) return null;
        byte x = 0; for (int i = 3; i <= 88; i++) x ^= r[i];
        if (x != r[89]) return null;
        return (r[1], r[10]);
    }

    private static (int Status, int Value) RazerAsk(Port p, byte txn, byte cmd)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (!p.SetFeature(RazerRequest(txn, cmd))) return (0xFF, 0);
            Thread.Sleep(attempt == 0 ? 15 : 40);
            for (int read = 0; read < 4; read++)
            {
                var rep = RazerReply(p.GetFeature(0x00), txn, cmd);
                if (rep == null) break;                    // ответ на чужой запрос (Synapse) — отправить снова
                if (rep.Value.Status == 0x01) { Thread.Sleep(20); continue; }       // занято — перечитать
                return rep.Value;
            }
        }
        return (0xFF, 0);                                  // ответа так и не прочитали — это не «спит»
    }

    private static Reading Razer(Port p, int pid)
    {
        byte[] order;
        lock (razerTxn) order = razerTxn.TryGetValue(pid, out var known) ? new[] { known }.Concat(RazerTransactions.Where(t => t != known)).ToArray() : RazerTransactions;
        bool asleep = false;
        foreach (byte txn in order)
        {
            var (status, value) = RazerAsk(p, txn, 0x80);
            if (status == 0x02)
            {
                lock (razerTxn) razerTxn[pid] = txn;
                var (cs, cv) = RazerAsk(p, txn, 0x84);
                return Reading.Pct((value * 100 + 127) / 255, cs == 0x02 ? cv == 1 : null);
            }
            if (status == 0x04) asleep = true;
        }
        return asleep ? Reading.NoAnswer : Reading.None;         // все ответили «не поддерживается» — проводная модель
    }

    // ───────── Logitech G435 ─────────

    /// <summary>Напряжение батареи G435 → проценты (верх — по описанию производителя, низ — по замеру полного разряда).</summary>
    internal static int? G435Percent(int mv) => Curve(mv,
        (4200, 100), (4150, 95), (4100, 90), (4050, 85), (4000, 80), (3950, 75), (3900, 70), (3850, 62), (3800, 55),
        (3750, 41), (3720, 34), (3705, 29), (3700, 27), (3690, 17), (3675, 11), (3650, 9), (3600, 7), (3550, 5), (3500, 4), (3450, 2), (3400, 1), (3300, 0));

    /// <summary>Кадр команды гарнитуре: 50 41 &lt;категория&gt; &lt;номер&gt; &lt;длина&gt; &lt;данные&gt;. У категории 06 длина на 1 меньше данных.</summary>
    internal static byte[] G435Frame(byte category, byte seq, params byte[] payload)
    {
        var f = new List<byte> { 0x50, 0x41, category, seq, (byte)(category == 0x06 ? payload.Length - 1 : payload.Length) };
        f.AddRange(payload);
        return f.ToArray();
    }

    /// <summary>
    /// Приёмник 0ACB. Порядок: войти в служебный режим (06 01 03), очистить очередь, «на связи?», режим, при нужде режим 1, заряд, выйти (06 08 03).
    /// Выход — всегда, даже при ошибке: иначе приёмник остался бы в служебном режиме до переподключения. Заряд никогда не спрашивается первым.
    /// </summary>
    private static Reading G435Old(Port p)
    {
        byte seq = 0;
        byte Next() { seq = (byte)(seq % 127 + 1); return seq; }
        bool Bridge(params byte[] msg) =>
            p.Ask(msg, r => r.Length > 1 && r[0] == 0x06 && r[1] is 0x5A or 0xFE, 800) != null;
        bool Command(byte[] frame)
        {
            var msg = new List<byte> { 0x06, 0x04, 0x03, (byte)frame.Length, 0x00 };
            msg.AddRange(frame);
            return Bridge(msg.ToArray());
        }
        // Ответ гарнитуры приходит feature-отчётами 6; [1] = FF — пока пусто.
        byte[] Poll(Func<byte[], int> find, out int at, int timeoutMs = 1500)
        {
            at = -1;
            long deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                var r = p.GetFeature(0x06);
                if (r != null && r[1] != 0xFF && (at = find(r)) >= 0) return r;
                Thread.Sleep(30);
            }
            return null;
        }
        int? Link(byte cmd)
        {
            if (!Command(G435Frame(0x0E, Next(), cmd))) return null;
            var r = Poll(x => Find(x, 0x02, 0x00, cmd), out int at);
            return r != null && at + 3 < r.Length ? r[at + 3] : null;
        }
        int? Battery()
        {
            byte s = Next();
            if (!Command(G435Frame(0x06, s, 0x00, 0x31))) return null;
            var r = Poll(x => Find(x, 0x07, 0x00, 0x06, (byte)(s | 0x80), 0x00), out int at);
            return r != null && at + 6 < r.Length ? Le16(r, at + 5) : null;
        }

        bool entered = Bridge(0x06, 0x01, 0x03);
        int? mode = null;
        bool changed = false;
        try
        {
            if (!entered) return Reading.NoAnswer;
            for (int i = 0; i < 16; i++) { var d = p.GetFeature(0x06); if (d == null || d[1] == 0xFF) break; }
            var connected = Link(0xE3);
            if (connected == 0) return Reading.NoAnswer;                 // гарнитура выключена
            mode = Link(0xE0);
            if (mode != 1) { Command(G435Frame(0x0E, Next(), 0xE1, 0x01)); changed = true; }
            int? mv = Battery();
            if (mv is not (>= 2500 and <= 4600) && !changed)
            {
                Command(G435Frame(0x0E, Next(), 0xE1, 0x01));
                changed = true;
                mv = Battery();
            }
            return mv is >= 2500 and <= 4600 ? Reading.Pct(G435Percent(mv.Value), null, approx: true) : Reading.NoAnswer;
        }
        finally
        {
            // Вернуть приёмнику тот режим, что был до чтения, и выйти из служебного режима — всегда, даже если вход не подтвердился.
            if (changed && mode is int m && m != 1) Command(G435Frame(0x0E, Next(), 0xE1, (byte)m));
            Bridge(0x06, 0x08, 0x03);
        }
    }

    /// <summary>CRC-16/XMODEM (многочлен 0x1021, начальное 0, без отражения).</summary>
    internal static ushort Crc16Xmodem(IEnumerable<byte> data)
    {
        ushort crc = 0;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }

    /// <summary>Кадр приёмника 0ADF: 00 (без номера отчёта), команда, длина, CRC (младший байт первым), данные; CRC — по 64 байтам кадра с нулями на месте CRC.</summary>
    internal static byte[] G435NewFrame(byte cmd, params byte[] body)
    {
        var f = new byte[64];
        f[0] = cmd; f[1] = (byte)body.Length;
        Array.Copy(body, 0, f, 4, body.Length);
        ushort crc = Crc16Xmodem(f);
        f[2] = (byte)(crc & 0xFF); f[3] = (byte)(crc >> 8);
        var msg = new byte[65];
        Array.Copy(f, 0, msg, 1, 64);
        return msg;
    }

    private static Reading G435New(Port p)
    {
        var replies = new List<byte[]>();
        void Step(byte[] msg, int waitMs) { if (p.Send(msg)) replies.AddRange(p.Collect(waitMs)); }
        p.Flush();
        try
        {
            Step(G435NewFrame(0x84, 0x01), 50);
            Step(G435NewFrame(0x85, 0x01), 50);
            Step(G435NewFrame(0x80, 0x50, 0x41, 0x06, 0x01, 0x0D, 0x00, 0x25, 0x34, 0x12, 0x5A, 0x5A, 0x01, 0x00, 0x00, 0x00, 0xF0, 0x00, 0x00, 0x00), 450);
            Step(G435NewFrame(0x80, G435Frame(0x0E, 2, 0xE0)), 120);
            Step(G435NewFrame(0x80, G435Frame(0x0E, 3, 0xE1, 0x01)), 120);
            Step(G435NewFrame(0x80, G435Frame(0x0E, 4, 0xE0)), 120);
            Step(G435NewFrame(0x80, G435Frame(0x06, 5, 0x00, 0x31)), 250);
        }
        finally { Step(G435NewFrame(0x85, 0x02), 50); }
        foreach (var r in replies)
        {
            int at = Find(r, 0x00, 0x07, 0x00, 0x06);
            if (at < 0 || at + 7 >= r.Length || r[at + 5] != 0x00) continue;
            int mv = Le16(r, at + 6);
            if (mv is >= 2500 and <= 4500) return Reading.Pct(G435Percent(mv), null, approx: true);
        }
        return Reading.NoAnswer;
    }

    // ───────────────────────── самопроверка ─────────────────────────

    internal static IEnumerable<(string Name, bool Ok)> Checks()
    {
        Protocol P(string id) => Protocols.First(x => x.Id == id);
        var r = PercentStatus(new byte[] { 0x00, 0x51, 0, 0, 0, 73, 0 }, 5, 6);
        yield return ("HyperX Haste: 73 %, от батареи", r.Percent == 73 && r.Charging == false && !r.Asleep);
        r = PercentStatus(new byte[] { 0x00, 0x51, 0, 0, 0, 100, 2 }, 5, 6);
        yield return ("HyperX Haste: 100 %, на проводе", r.Percent == 100 && r.Charging == true);
        r = PercentStatus(new byte[] { 0x00, 0x51, 0, 0, 0, 0xFF, 0 }, 5, 6);
        yield return ("HyperX Haste: 0xFF — заряд неизвестен", r.Percent == null && !r.Asleep);
        yield return ("нет ответа — спит", PercentStatus(null, 5, 6).Asleep);

        var mouseCol = new DeviceBattery.HidIface { Vid = 0x03F0, Pid = 0x028E, UsagePage = 0x0001, Usage = 0x0002, InLen = 12, Path = @"\\?\hid#vid_03f0&pid_028e&mi_00#x" };
        var vendorCol = new DeviceBattery.HidIface { Vid = 0x03F0, Pid = 0x028E, UsagePage = 0xFF00, Usage = 0x0001, InLen = 65, OutLen = 65, Path = @"\\?\hid#vid_03f0&pid_028e&mi_02#x" };
        yield return ("коллекция мыши не открывается, служебная — да", !P("hyperx-haste").Matches(mouseCol) && P("hyperx-haste").Matches(vendorCol));
        yield return ("номер интерфейса из пути: mi_02 → 2, без него → −1", InterfaceOf(vendorCol.Path) == 2 && InterfaceOf(@"\\?\hid#vid_1&pid_2#x") == -1);
        yield return ("в таблице только служебные коллекции (кроме feature-отчёта Razer и геймпадов, которые только слушаются)",
                      Protocols.All(x => x.FeatureOnly ? x.Id == "razer" : x.Passive || x.Page == 0 || x.Page >= 0xFF00));
        yield return ("геймпады только слушаются: в протоколе нет отправки", Protocols.Where(x => x.Passive).All(x => x.Brand == "Sony"));
        yield return ("DualShock 4: уровень 6 от батареи → 60 %, 11 с кабелем — заряжен",
                      Ds4Battery(0x06) is { Percent: 60, Charging: false } && Ds4Battery(0x1B) is { Percent: 100, Charging: false });
        yield return ("DualSense: уровень 4, заряжается → 45 %; заряжен → 100 %",
                      DualSenseBattery(0x14) is { Percent: 45, Charging: true } && DualSenseBattery(0x2A) is { Percent: 100 });
        yield return ("у каждого протокола есть чтение, у каждой модели — свой протокол",
                      Protocols.All(x => x.Read != null) &&
                      Protocols.Where(x => x.Models != null).SelectMany(x => x.Models.Select(m => (x.Vid, m.Pid))).GroupBy(k => k).All(g => g.Count() == 1));
        yield return ("опрос, прерывающий звук, — только в тишине, не в игре и не чаще раза в 20 минут",
                      Protocols.Where(x => x.Id.StartsWith("logi-g435")).All(x => x.QuietMark == "G435" && x.NotInGame && x.MinMinutes >= 20));

        yield return ("кривая: выше верхней точки — 100, ниже нижней — 0, между — по прямой",
                      Curve(4300, (4200, 100), (3300, 0)) == 100 && Curve(3200, (4200, 100), (3300, 0)) == 0 && Curve(3750, (4200, 100), (3300, 0)) == 50);
        yield return ("ступени: 2 из 4 → 50 %, 8 из 8 → 100 %, вне границ — нет", Steps(2, 4) == 50 && Steps(8, 8) == 100 && Steps(5, 4) == null);
        yield return ("G435: 3768 mV → около 45 %", G435Percent(3768) is >= 40 and <= 50);
        var was = Reading.Pct(65, null, approx: true);
        yield return ("по напряжению: 65 % → через полчаса 38 % — показывается 60 % (не быстрее 10 % в час)", Smooth(Reading.Pct(38, null, approx: true), was, 0.5).Percent == 60);
        yield return ("по напряжению: рост и точные проценты — как есть", Smooth(Reading.Pct(70, null, approx: true), was, 0.5).Percent == 70 && Smooth(Reading.Pct(30), was, 0.5).Percent == 30);
        yield return ("по напряжению: прошлое старше 6 часов не учитывается", Smooth(Reading.Pct(20, null, approx: true), was, 7).Percent == 20);
        yield return ("G435: кадр заряда 50 41 06 05 01 00 31", G435Frame(0x06, 5, 0x00, 0x31).SequenceEqual(new byte[] { 0x50, 0x41, 0x06, 0x05, 0x01, 0x00, 0x31 }));
        yield return ("CRC-16/XMODEM «123456789» = 31C3", Crc16Xmodem("123456789"u8.ToArray()) == 0x31C3);
        var f = G435NewFrame(0x84, 0x01);
        yield return ("G435 (0ADF): кадр 84 01 → 00 84 01 D1 55 01", f.Length == 65 && f.Take(6).SequenceEqual(new byte[] { 0x00, 0x84, 0x01, 0xD1, 0x55, 0x01 }));
        yield return ("Cloud Flight: 3800 mV — между 0 и 100, 4000 — 100", FlightPercent(3800) is > 0 and < 100 && FlightPercent(4000) == 100);
        yield return ("Stinger: 3954 mV от батареи → 80 %", StingerPercent(3954, false) == 80);

        var q = RazerRequest(0x1F, 0x80);
        var reply = (byte[])q.Clone(); reply[1] = 0x02; reply[10] = 128;
        byte x2 = 0; for (int i = 3; i <= 88; i++) x2 ^= reply[i];
        reply[89] = x2;
        yield return ("Razer: запрос 90 байт, класс 07, контрольная сумма", q.Length == 91 && q[7] == 0x07 && q[8] == 0x80 && q[89] == (0x02 ^ 0x07 ^ 0x80));
        yield return ("Razer: ответ 128 из 255, состояние «готово»", RazerReply(reply, 0x1F, 0x80) is { Status: 0x02, Value: 128 });
        yield return ("Razer: ответ на чужой запрос не принимается", RazerReply(reply, 0x3F, 0x80) == null);
    }
}
