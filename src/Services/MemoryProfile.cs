using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PulsePC.Services;

/// <summary>Планка памяти для выводов о частоте и каналах (из первичного осмотра или из «Совета по апгрейду»).</summary>
public sealed class MemStick
{
    public string Slot = "", Bank = "", Maker = "", Part = "";
    public double SizeGb;
    /// <summary>Скорость сейчас и паспортная (JEDEC или то, что отдал BIOS), МТ/с.</summary>
    public int Mts, RatedMts;
    /// <summary>Число рангов (Win32_PhysicalMemory.Attributes); 0 — неизвестно.</summary>
    public int Ranks;
    /// <summary>8 — DIMM, 12 — SO-DIMM (ноутбук, мини-ПК), 0 — неизвестно.</summary>
    public int FormFactor;
}

/// <summary>Память компьютера вместе с процессором — всё, что нужно для вывода о частоте и каналах.</summary>
public sealed class MemSetup
{
    /// <summary>Название процессора как его отдаёт Windows («12th Gen Intel(R) Core(TM) i5-12400F») или короткое («Core i5-12400F»).</summary>
    public string Cpu = "";
    public bool Laptop;
    /// <summary>DDR4, DDR5, LPDDR4, LPDDR5…</summary>
    public string Type = "";
    /// <summary>Слотов на плате; 0 — неизвестно.</summary>
    public int Slots;
    public List<MemStick> Sticks = new();

    /// <summary>Ноутбук или распаянная память: советов «включить XMP» и «переставить планки» здесь не бывает.</summary>
    public bool Mobile => Laptop || Type.StartsWith("LP", StringComparison.OrdinalIgnoreCase) || Sticks.Any(s => s.FormFactor == 12);
}

/// <summary>Официальная (штатная) скорость памяти для семейства процессоров, МТ/с; 0 — этот тип памяти процессор не поддерживает или неизвестно.</summary>
public sealed class CpuMemSpec
{
    public string Family = "";
    public bool Mobile;
    /// <summary>DDR4: одна и две планки на канал.</summary>
    public int Ddr4, Ddr4Four;
    /// <summary>DDR5: одна планка на канал (одноранговая / двухранговая) и две планки на канал (одноранговые / двухранговые).</summary>
    public int Ddr5, Ddr5Dual, Ddr5Four, Ddr5FourDual;
    public int Lp4, Lp5;
}

public enum MemKind { None, Normal, AboveOfficial, AbovePartial, AtOfficial, ProfileLikelyOff, BelowOfficial, LaptopBelow, Unknown }

/// <summary>Вывод о частоте памяти: один и тот же текст для осмотра, «Совета по апгрейду» и «Диагностики».</summary>
public sealed class MemVerdict
{
    public MemKind Kind = MemKind.None;
    public Severity Level = Severity.Ok;
    /// <summary>Заголовок — русская строка-ключ (на экран — через L.T).</summary>
    public string Title = "";
    public string Text = "", What = "", Warning = "";
    /// <summary>Советуем включить профиль XMP/EXPO (тогда рядом всегда Warning).</summary>
    public bool AdviseProfile;
    public int Current, Marked, Official;
    public string Cpu = "", Spec = "";

    /// <summary>Всё одним абзацем: что видно, что сделать, предупреждение.</summary>
    public string Full => string.Join(" ", new[] { Text, What, Warning }.Where(s => s.Length > 0));
    /// <summary>Есть что сказать в осмотре (расхождение маркировки и частоты).</summary>
    public bool ForScan => Kind is MemKind.ProfileLikelyOff or MemKind.AtOfficial or MemKind.AbovePartial or MemKind.LaptopBelow or MemKind.Unknown or MemKind.BelowOfficial;
}

public enum ChannelKind { None, Dual, OneStick, SameChannel, MixedSizes }

/// <summary>Вывод о каналах памяти (одна планка, две в одном канале, разный объём) — один на всё.</summary>
public sealed class ChannelVerdict
{
    public ChannelKind Kind = ChannelKind.None;
    public Severity Level = Severity.Ok;
    public string Title = "", Text = "", What = "";
    public string Full => string.Join(" ", new[] { Text, What }.Where(s => s.Length > 0));
}

/// <summary>
/// Частота памяти и XMP/EXPO — с осторожностью. По одной разнице «маркировка планки против текущей частоты» нельзя утверждать,
/// что профиль выключен: процессор официально поддерживает свою скорость, и на ноутбуке (например, Ryzen 9 8940HX — DDR5-5200)
/// модули DDR5-5600 штатно работают на 5200. Поэтому вывод сверяется с таблицей штатных скоростей процессоров (Intel 6–14-го
/// поколения и Core Ultra, AMD Ryzen 1000–9000 и Ryzen AI, настольные и мобильные), с числом планок и рангов.
/// Совет «включить профиль» — только для настольного ПК, только если частота не выше штатной и по маркировке это комплект
/// с профилем; рядом всегда предупреждение о рисках. Только чтение.
/// </summary>
public static class MemoryProfile
{
    private const string Nb = " ";

    // ================================================================== единицы
    public static string Mts(int v) => v.ToString(L.Culture) + Nb + (L.En ? "MT/s" : "МТ/с");

    /// <summary>«DDR5-5200»; для двух планок на канал — «DDR5-3600 (с четырьмя планками)».</summary>
    private static string SpecText(string type, int mts) => (type.Length > 0 ? type : "DDR") + "-" + mts.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ================================================================== штатные скорости процессоров
    private static CpuMemSpec D4(string fam, bool mob, int v, int four = 0, int lp4 = 0) =>
        new() { Family = fam, Mobile = mob, Ddr4 = v, Ddr4Four = four > 0 ? four : v, Lp4 = lp4 };

    private static CpuMemSpec D5(string fam, bool mob, int v, int dual = 0, int four = 0, int fourDual = 0, int ddr4 = 0, int ddr4Four = 0, int lp4 = 0, int lp5 = 0) =>
        new()
        {
            Family = fam, Mobile = mob, Ddr5 = v, Ddr5Dual = dual > 0 ? dual : v, Ddr5Four = four > 0 ? four : (dual > 0 ? dual : v),
            Ddr5FourDual = fourDual > 0 ? fourDual : four > 0 ? four : (dual > 0 ? dual : v),
            Ddr4 = ddr4, Ddr4Four = ddr4Four > 0 ? ddr4Four : ddr4, Lp4 = lp4, Lp5 = lp5,
        };

    private static CpuMemSpec Lp(string fam, int lp5, int lp4 = 0) => new() { Family = fam, Mobile = true, Lp5 = lp5, Lp4 = lp4 };

    /// <summary>
    /// Штатная скорость памяти для процессора по названию модели (паспорта Intel ARK и AMD). null — модель не распознана.
    /// Таблица — распространённые семейства; для двух планок на канал и двухранговых DDR5 — то, что пишут производители процессоров.
    /// </summary>
    public static CpuMemSpec SpecFor(string cpu)
    {
        string c = Regex.Replace(cpu ?? "", @"\((R|TM|tm|r)\)|®|™", " ");
        c = Regex.Replace(c, @"\s+", " ").Trim();
        if (c.Length == 0) return null;
        Match m;

        // ---------- Intel Core Ultra
        if ((m = Regex.Match(c, @"Ultra\s*[3579]\s+(\d)(\d{2})([A-Z]*)", RegexOptions.IgnoreCase)).Success)
        {
            char series = m.Groups[1].Value[0];
            string sfx = m.Groups[3].Value.ToUpperInvariant();
            if (series == '1') return D5("Core Ultra (Series 1)", true, 5600, lp5: 7467);
            if (series == '2')
            {
                if (sfx == "V") return Lp("Core Ultra 200V", 8533);
                if (sfx.StartsWith("HX")) return D5("Core Ultra 200HX", true, 6400, 5600);
                if (sfx.StartsWith("H")) return D5("Core Ultra 200H", true, 6400, lp5: 8400);
                if (sfx.StartsWith("U")) return D5("Core Ultra 200U", true, 5600, lp5: 7467);
                return D5("Core Ultra 200S", false, 6400, 5600, 4800, 4400);
            }
            return null;
        }

        // ---------- Intel Core 3/5/7 без «Ultra» (Core 5 120F, Core 7 150U, Core 5 210H)
        if ((m = Regex.Match(c, @"\bCore\s+([3579])\s+([12])(\d{2})([A-Z]*)", RegexOptions.IgnoreCase)).Success)
        {
            string sfx = m.Groups[4].Value.ToUpperInvariant();
            bool mob = Regex.IsMatch(sfx, "[HUP]");
            return mob ? D5("Core (Series " + m.Groups[2].Value + ")", true, 5200, ddr4: 3200, lp5: 6400)
                       : D5("Core (Series 1)", false, 4800, 4400, 4000, 3600, 3200, 2933);
        }

        // ---------- Intel Core i3/i5/i7/i9
        if ((m = Regex.Match(c, @"\bi([3579])-(\d{4,5})([A-Z]*)(\d?)", RegexOptions.IgnoreCase)).Success)
        {
            int tier = m.Groups[1].Value[0] - '0';
            string num = m.Groups[2].Value, sfx = m.Groups[3].Value.ToUpperInvariant();
            bool g = sfx.StartsWith("G") || m.Groups[4].Value.Length > 0;
            int gen;
            if (num.Length == 5) gen = int.Parse(num[..2]);
            else if (num[0] == '1') gen = int.Parse(num[..2]);      // i5-1035G1, i7-1165G7, i5-1235U, i7-1360P — только ноутбуки
            else gen = num[0] - '0';
            bool mob = (num.Length == 4 && num[0] == '1') || g || Regex.IsMatch(sfx, "[HUYPMG]");
            bool k = sfx.StartsWith("K");
            string fam = "Intel " + gen;

            if (!mob)
                return gen switch
                {
                    6 => D4(fam, false, 2133),
                    7 => D4(fam, false, 2400),
                    8 or 9 => D4(fam, false, tier == 3 ? 2400 : 2666),
                    10 => D4(fam, false, tier <= 5 ? 2666 : 2933),
                    11 => D4(fam, false, 3200, 2933),
                    12 => D5(fam, false, 4800, 4400, 4000, 3600, 3200, 2933),
                    // 13-е и 14-е: i7, i9 и K — DDR5-5600; i3 и i5 без K (13400, 14400, 14600) — DDR5-4800, как 12-е.
                    13 or 14 when k || tier >= 7 => D5(fam, false, 5600, 5200, 4400, 4000, 3200, 2933),
                    13 or 14 => D5(fam, false, 4800, 4400, 4000, 3600, 3200, 2933),
                    _ => null,
                };

            bool hx = sfx.StartsWith("HX");
            bool h = sfx.StartsWith("H");
            return gen switch
            {
                6 => D4(fam, true, 2133),
                7 => D4(fam, true, 2400),
                8 or 9 => D4(fam, true, h ? 2666 : 2400),
                10 when g => D4(fam, true, 3200, lp4: 3733),                     // Ice Lake: i5-1035G1, i7-1065G7
                10 => D4(fam, true, h ? 2933 : 2666),
                11 => D4(fam, true, 3200, lp4: 4267),
                12 => D5(fam, true, 4800, ddr4: 3200, lp4: 4267, lp5: 5200),
                13 or 14 when hx => D5(fam, true, 5600, ddr4: 3200),
                13 => D5(fam, true, 5200, ddr4: 3200, lp5: 6400),
                _ => null,
            };
        }

        // ---------- AMD Ryzen AI (Strix Point, Krackan, Strix Halo)
        if (Regex.IsMatch(c, @"Ryzen\s+AI\s+Max", RegexOptions.IgnoreCase)) return Lp("Ryzen AI Max", 8000);
        if (Regex.IsMatch(c, @"Ryzen\s+AI\b", RegexOptions.IgnoreCase)) return D5("Ryzen AI 300", true, 5600, lp5: 7500);
        if (Regex.IsMatch(c, @"Ryzen\s+Z\d", RegexOptions.IgnoreCase)) return Lp("Ryzen Z", 7500);

        // ---------- AMD Ryzen 3/5/7/9
        if ((m = Regex.Match(c, @"Ryzen\s+[3579]\s+(?:PRO\s+)?(\d)(\d)(\d)(\d)([A-Z0-9]*)", RegexOptions.IgnoreCase)).Success)
        {
            int gen = m.Groups[1].Value[0] - '0';
            int arch = m.Groups[3].Value[0] - '0', feature = m.Groups[4].Value[0] - '0';
            string sfx = m.Groups[5].Value.ToUpperInvariant();
            bool mob = Regex.IsMatch(sfx, @"^(U|H|HS|HX|HX3D|M|C)$");
            bool apu = sfx.StartsWith("G");
            string fam = "Ryzen " + gen + "000";
            if (!mob)
                return gen switch
                {
                    1 => D4(fam, false, 2666, 2133),
                    2 => D4(fam, false, 2933, 2400),
                    3 => apu ? D4(fam, false, 2933, 2667) : D4(fam, false, 3200, 2667),
                    4 or 5 => D4(fam, false, 3200, 2667),
                    7 or 8 => D5(fam, false, 5200, 5200, 3600, 3600),
                    9 => D5(fam, false, 5600, 5600, 3600, 3600),
                    _ => null,
                };
            return gen switch
            {
                2 or 3 => D4(fam, true, 2400),
                4 or 5 => D4(fam, true, 3200, lp4: 4266),
                6 => D5(fam, true, 4800, lp5: 6400),
                7 => (arch, feature) switch
                {
                    (2, _) => Lp(fam, 5500),                                   // Mendocino: 7520U
                    (3, 0) => D4(fam, true, 3200, lp4: 4266),                  // Barcelo-R: 7530U, 7730U
                    (3, _) => D5(fam, true, 4800, lp5: 6400),                  // Rembrandt-R: 7735HS
                    (4, 5) => D5(fam, true, 5200),                             // Dragon Range: 7945HX
                    (4, _) => D5(fam, true, 5600, lp5: 7500),                  // Phoenix: 7840HS
                    _ => null,
                },
                8 => sfx.StartsWith("HX") ? D5(fam, true, 5200) : D5(fam, true, 5600, lp5: 7500),   // 8940HX/8945HX — DDR5-5200
                9 => D5(fam, true, 5600),
                _ => null,
            };
        }
        return null;
    }

    /// <summary>Штатная скорость для типа памяти, числа планок и рангов. 0 — неизвестно.</summary>
    public static int Official(CpuMemSpec s, string type, int sticks, int ranks)
    {
        if (s == null) return 0;
        bool four = sticks > 2, dual = ranks >= 2;
        type = (type ?? "").ToUpperInvariant();
        return type switch
        {
            "DDR4" => four ? s.Ddr4Four : s.Ddr4,
            "DDR5" => four ? (dual ? s.Ddr5FourDual : s.Ddr5Four) : (dual ? s.Ddr5Dual : s.Ddr5),
            "LPDDR4" => s.Lp4,
            "LPDDR5" => s.Lp5,
            _ => 0,
        };
    }

    // ================================================================== маркировка планок
    private static readonly int[] KnownSpeeds =
        { 2133, 2400, 2666, 2800, 2933, 3000, 3200, 3333, 3466, 3600, 3733, 3800, 3866, 4000, 4133, 4266, 4400, 4600, 4800, 5200, 5600, 6000, 6200, 6400, 6600, 6800, 7000, 7200, 7600, 8000, 8200, 8400 };

    /// <summary>Скорость из маркировки планки, МТ/с; 0 — не распознана.</summary>
    public static int PartSpeed(string part)
    {
        if (string.IsNullOrWhiteSpace(part)) return 0;
        string p = part.Trim().ToUpperInvariant();
        Match m;
        int V(string s) => int.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        if ((m = Regex.Match(p, @"^F[345]-(\d{4})[CJUA]")).Success) return V(m.Groups[1].Value);                 // G.Skill F4-3600C16, F5-6000J3038
        if ((m = Regex.Match(p, @"^CM\w*?X[345]M\d[A-Z](\d{4})C\d")).Success) return V(m.Groups[1].Value);        // Corsair CMK16GX4M2B3200C16
        if ((m = Regex.Match(p, @"^(?:KF|HX)[345](\d{2})[A-Z]{1,2}\d")).Success) return Round(V(m.Groups[1].Value) * 100); // Kingston KF432C16, KF552C40, HX426C16
        if ((m = Regex.Match(p, @"^(?:KF|HX)(\d{4})C\d")).Success) return V(m.Groups[1].Value);                    // Kingston KF3200C16D4/8GX
        if ((m = Regex.Match(p, @"^(?:BL|CP|CT)\d*K?\d+G(\d{2})[CS]\d")).Success)                                // Crucial BL8G32C16, CP16G56C46U5, CT16G56C46S5
            return Round(V(m.Groups[1].Value) * 100);
        if ((m = Regex.Match(p, @"-C(PB|RC|TD|VF|WE|QK|WM)")).Success && p.StartsWith("M"))                       // Samsung M378A1K43DB2-CTD
            return m.Groups[1].Value switch { "PB" => 2133, "RC" => 2400, "TD" => 2666, "VF" => 2933, "WE" => 3200, "QK" => 4800, "WM" => 5600, _ => 0 };
        if ((m = Regex.Match(p, @"^HMA\w+-(TF|UH|VK|WM|XN)")).Success)                                          // SK hynix HMA81GU6CJR8N-XN
            return m.Groups[1].Value switch { "TF" => 2133, "UH" => 2400, "VK" => 2666, "WM" => 2933, "XN" => 3200, _ => 0 };
        if ((m = Regex.Match(p, @"^MTA\w+-([23])G([1-9])")).Success)                                              // Micron MTA8ATF1G64AZ-3G2E1
            return (m.Groups[1].Value + "G" + m.Groups[2].Value) switch { "2G1" => 2133, "2G4" => 2400, "2G6" => 2666, "2G9" => 2933, "3G2" => 3200, _ => 0 };
        if ((m = Regex.Match(p, @"(?<!\d)(\d{4})(?!\d)")).Success)
            foreach (Match t in Regex.Matches(p, @"(?<!\d)(\d{4})(?!\d)"))
                if (KnownSpeeds.Contains(V(t.Groups[1].Value)) && V(t.Groups[1].Value) >= 2666) return V(t.Groups[1].Value);
        return 0;
    }

    /// <summary>«26» в маркировке — 2666, «29» — 2933.</summary>
    private static int Round(int v) => v == 2600 ? 2666 : v == 2900 ? 2933 : v;

    /// <summary>
    /// Комплект с профилем разгона (XMP/EXPO) по маркировке: Kingston FURY/HyperX, Corsair Vengeance/Dominator (не ValueSelect),
    /// G.Skill, TeamGroup T-Force, Crucial Ballistix и Pro, Patriot Viper, ADATA XPG, Netac Shadow, Apacer NOX, GeIL, PNY XLR8 и т. п.
    /// </summary>
    public static bool ProfileKit(string maker, string part)
    {
        string p = (part ?? "").Trim().ToUpperInvariant();
        if (p.Length == 0) return false;
        if (p.Contains("XMP") || p.Contains("EXPO")) return true;
        return Regex.IsMatch(p, @"^(KF\d|HX\d|CM(?!V)[A-Z]|F[345]-|TF\d|TF[A-Z]\d|TLZ|TDZ|TTZ|FF\d|BL\d|BLS|BLM|CP\d*K?\d+G\d{2}C|PV[SBE]?\d|AX[45]U|NTSD|AH[45]U|GL[A-Z]?\d|GAS|GWS|GOS|MD\d+GK|R0\d{2})");
    }

    private static string ProfileName(string cpu, string type)
    {
        string vendor = HardwareInfo.CpuVendor(cpu);
        if (vendor == "AMD") return type.ToUpperInvariant() == "DDR5" ? L.T("EXPO (на части плат — XMP)") : L.T("XMP (на платах ASUS он называется DOCP)");
        if (vendor == "Intel") return "XMP";
        return "XMP/EXPO";
    }

    /// <summary>Предупреждение о рисках — рядом с любым советом включить XMP/EXPO.</summary>
    public static string RiskWarning => L.T("XMP/EXPO — профиль разгона памяти. На некоторых компьютерах он вызывает вылеты, зависания, ошибки памяти или проблемы с загрузкой. Если после включения появятся сбои — выключите профиль в BIOS или сбросьте настройки BIOS.");

    // ================================================================== частота
    public static MemVerdict Speed(MemSetup s)
    {
        var v = new MemVerdict();
        if (s == null || s.Sticks.Count == 0) return v;
        var sticks = s.Sticks.Where(x => x.Mts > 0).ToList();
        if (sticks.Count == 0) return v;
        int cur = sticks.Min(x => x.Mts);
        var best = s.Sticks.OrderByDescending(x => Math.Max(PartSpeed(x.Part), x.RatedMts)).First();
        int marked = s.Sticks.Max(x => Math.Max(PartSpeed(x.Part), x.RatedMts));
        bool kit = s.Sticks.Any(x => ProfileKit(x.Maker, x.Part));
        string type = s.Type.Length > 0 ? s.Type : HardwareInfo.RamTypeFor(0, cur, s.Sticks.Select(x => x.Part));
        var spec = SpecFor(s.Cpu);
        int ranks = s.Sticks.Select(x => x.Ranks).DefaultIfEmpty(0).Max();
        int official = Official(spec, type, s.Sticks.Count, ranks);
        string cpu = HardwareInfo.ShortCpu(s.Cpu);
        if (cpu.Length == 0) cpu = L.T("этот процессор");
        bool four = s.Sticks.Count > 2;
        string specText = official > 0 ? SpecText(type, official) : "";
        string part = best.Part.Length > 0 ? best.Part : (best.Maker.Length > 0 ? best.Maker : "");
        v.Current = cur; v.Marked = marked; v.Official = official; v.Cpu = cpu; v.Spec = specText;

        bool atMark = marked <= cur + 100;
        bool mobile = s.Mobile;

        if (atMark)
        {
            if (official > 0 && cur > official + 100)
            {
                v.Kind = MemKind.AboveOfficial; v.Level = Severity.Ok; v.Title = "Память работает на своей частоте";
                v.Text = L.F("{0} — как указано на планках. Это выше официальной скорости {1} ({2}): работает профиль разгона памяти. Если появятся вылеты или ошибки памяти, сначала проверьте без него.", Mts(cur), cpu, specText);
            }
            else
            {
                v.Kind = MemKind.Normal; v.Level = Severity.Ok; v.Title = "Память работает на своей частоте";
                v.Text = official > 0 ? L.F("{0} — как указано в сведениях планок; для {1} это штатная скорость.", Mts(cur), cpu)
                                      : L.F("{0} — как указано в сведениях планок.", Mts(cur));
            }
            return v;
        }

        // Маркировка выше текущей частоты: что это значит — зависит от процессора и от того, ноутбук ли это.
        if (official == 0)
        {
            v.Kind = MemKind.Unknown; v.Level = Severity.Info; v.Title = "Штатная скорость памяти для этого процессора неизвестна";
            v.Text = mobile
                ? L.F("Планки рассчитаны на {0}, а работают на {1}. Штатную скорость памяти для {2} программа не знает, поэтому не может сказать, нормально ли это. В ноутбуке частоту памяти обычно задаёт производитель.", Mts(marked), Mts(cur), cpu)
                : L.F("Планки рассчитаны на {0}, а работают на {1}. Штатную скорость памяти для {2} программа не знает, поэтому не может сказать, нормально ли это или профиль XMP/EXPO выключен.", Mts(marked), Mts(cur), cpu);
            return v;
        }

        if (mobile)
        {
            if (cur >= official - 100)
            {
                v.Kind = MemKind.AtOfficial; v.Level = Severity.Info; v.Title = "Память работает на штатной скорости процессора";
                v.Text = L.F("{0} — штатно для {1}: модули рассчитаны на {2}, но процессор официально поддерживает {3}. Это норма, менять ничего не нужно.", Mts(cur), cpu, Mts(marked), specText);
            }
            else
            {
                v.Kind = MemKind.LaptopBelow; v.Level = Severity.Info; v.Title = "Память работает медленнее штатной скорости процессора";
                v.Text = L.F("Сейчас {0}; {1} официально поддерживает {2}, модули рассчитаны на {3}. В ноутбуке частоту памяти задаёт производитель, и в BIOS она обычно не меняется.", Mts(cur), cpu, specText, Mts(marked));
            }
            return v;
        }

        if (cur > official + 100)
        {
            v.Kind = MemKind.AbovePartial; v.Level = Severity.Info; v.Title = "Частота памяти выше штатной, но ниже маркировки";
            v.Text = L.F("Планки рассчитаны на {0}, работают на {1} — это уже выше официальной скорости {2} ({3}), значит, профиль или ручная настройка в BIOS включены. Полную частоту, видимо, не держат плата или процессор — так бывает, менять ничего не нужно.", Mts(marked), Mts(cur), cpu, specText);
            return v;
        }

        if (kit)
        {
            v.Kind = MemKind.ProfileLikelyOff; v.Level = Severity.Info; v.Title = "Похоже, профиль XMP/EXPO выключен";
            v.AdviseProfile = true;
            v.Text = L.F("Планки {0} рассчитаны на {1}, а работают на {2} — на такой скорости память запускается без профиля разгона.", part, Mts(marked), Mts(cur)) + " " +
                     (marked <= official + 100
                         ? L.F("Официально {0} поддерживает {1}, но включить эту частоту на таких планках может только профиль.", cpu, specText)
                         : L.F("Официально {0} поддерживает {1}; {2} — это уже разгон, который и включает профиль.", cpu, specText, Mts(marked)));
            v.What = L.F("Если хотите, включите в BIOS профиль {0} — память заработает на {1}. Это может прибавить FPS в играх, которые упираются в процессор. Сначала проверьте, что ваша плата поддерживает эту частоту: список совместимой памяти (QVL) — на странице платы на сайте производителя.",
                         ProfileName(s.Cpu, type), Mts(marked));
            if (four && type.ToUpperInvariant() == "DDR5")
                v.What += " " + L.T("С четырьмя планками DDR5 полная частота профиля часто не заводится — тогда её снижают в BIOS.");
            v.Warning = RiskWarning;
            return v;
        }

        if (cur >= official - 100)
        {
            v.Kind = MemKind.AtOfficial; v.Level = Severity.Info; v.Title = "Память работает на штатной скорости процессора";
            v.Text = L.F("{0} — штатно для {1}: модули рассчитаны на {2}, но процессор официально поддерживает {3}. Это норма, менять ничего не нужно.", Mts(cur), cpu, Mts(marked), specText);
            return v;
        }

        v.Kind = MemKind.BelowOfficial; v.Level = Severity.Info; v.Title = "Память работает медленнее, чем позволяют планки и процессор";
        v.Text = L.F("Планки рассчитаны на {0}, {1} официально поддерживает {2}, а память работает на {3}. Если в BIOS частота памяти задана вручную, верните «Авто».", Mts(marked), cpu, specText, Mts(cur)) +
                 (four ? " " + L.T("С четырьмя планками плата может снижать частоту сама — это нормально.") : "");
        return v;
    }

    // ================================================================== каналы
    /// <summary>
    /// Канал по названию слота: «ChannelA-DIMM0», «P0 CHANNEL A» → A; «DIMM_A1», «Controller0-DIMMA2», «DDR4_B2», «A1» → A/B и номер.
    /// '\0' — по названию не понять.
    /// </summary>
    public static (char Channel, int Index) ChannelOf(string slot, string bank)
    {
        string s = ((slot ?? "") + " | " + (bank ?? "")).ToUpperInvariant();
        Match m;
        int idx = -1;
        if ((m = Regex.Match(s, @"DIMM[_\s-]?([A-H])(\d)\b")).Success) return (m.Groups[1].Value[0], int.Parse(m.Groups[2].Value));
        if ((m = Regex.Match(s, @"CHANNEL\s*([A-H])(?:\s*[-_]?\s*DIMM\s*(\d))?")).Success)
        {
            if (m.Groups[2].Success) idx = int.Parse(m.Groups[2].Value);
            else if ((m = Regex.Match((slot ?? "").ToUpperInvariant(), @"DIMM\s*(\d)")).Success) idx = int.Parse(m.Groups[1].Value);
            var ch = Regex.Match(s, @"CHANNEL\s*([A-H])").Groups[1].Value[0];
            return (ch, idx);
        }
        if ((m = Regex.Match((slot ?? "").ToUpperInvariant(), @"(?:^|[_\s-])([A-H])(\d)$")).Success) return (m.Groups[1].Value[0], int.Parse(m.Groups[2].Value));
        if ((m = Regex.Match((slot ?? "").ToUpperInvariant(), @"^DIMM\s+([A-H])$")).Success) return (m.Groups[1].Value[0], -1);
        return ('\0', -1);
    }

    public static ChannelVerdict Channels(MemSetup s)
    {
        var v = new ChannelVerdict();
        if (s == null || s.Sticks.Count == 0) return v;
        if (s.Type.StartsWith("LP", StringComparison.OrdinalIgnoreCase)) return v;     // распаянная память: каналы задал производитель
        bool mobile = s.Mobile;

        if (s.Sticks.Count == 1)
        {
            v.Kind = ChannelKind.OneStick; v.Title = "Одна планка памяти";
            if (mobile)
            {
                v.Level = Severity.Info;
                v.Text = L.T("Память работает в одноканальном режиме. Если в ноутбуке есть свободный слот, вторая такая же планка включит двухканальный режим: есть ли слот, написано в инструкции к ноутбуку, а в части ноутбуков память распаяна.");
            }
            else
            {
                v.Level = Severity.Warn;
                v.Text = L.T("Память работает в одноканальном режиме, и в играх, где много считает процессор, это заметно снижает FPS. Вторая такая же планка включит двухканальный режим.");
            }
            return v;
        }

        var known = s.Sticks.Select(x => (Stick: x, Ch: ChannelOf(x.Slot, x.Bank))).ToList();
        bool allKnown = known.All(x => x.Ch.Channel != '\0');
        var distinctSlots = known.Select(x => (x.Stick.Slot.Trim() + "|" + x.Stick.Bank.Trim()).ToUpperInvariant()).Distinct().Count();
        string slots = string.Join(", ", s.Sticks.Select(x => x.Slot.Trim()).Where(x => x.Length > 0));
        // Intel 12-го поколения и новее нумерует контроллеры памяти: «Controller0-ChannelA-DIMM1» и «Controller1-ChannelA-DIMM1» —
        // это два разных канала, хотя буква одна. Канал = контроллер + буква.
        static string Ctl(MemStick x) => Regex.Match(((x.Slot ?? "") + " " + (x.Bank ?? "")).ToUpperInvariant(), @"CONTROLLER\s*(\d+)").Groups[1].Value;
        int channelCount = known.Select(x => Ctl(x.Stick) + x.Ch.Channel).Distinct().Count();
        if (allKnown && distinctSlots == s.Sticks.Count && channelCount == 1)
        {
            v.Kind = ChannelKind.SameChannel; v.Title = "Планки памяти стоят в одном канале";
            if (mobile)
            {
                v.Level = Severity.Info;
                v.Text = L.F("Похоже, планки стоят в одном канале ({0}) — так слоты называет BIOS. В ноутбуке так решил производитель.", slots);
            }
            else
            {
                v.Level = Severity.Warn;
                v.Text = L.F("Похоже, все планки стоят в одном канале ({0}) — так слоты называет BIOS. Тогда память работает в одноканальном режиме, и в играх, где много считает процессор, FPS ниже.", slots);
                v.What = s.Sticks.Count == 2
                    ? L.T("Выключите компьютер, отключите его от розетки и переставьте планки в слоты разных каналов — обычно A2 и B2, то есть второй и четвёртый слот, если считать от процессора. Точная схема — в инструкции к плате.")
                    : L.T("Выключите компьютер, отключите его от розетки и распределите планки поровну между каналами A и B. Точная схема — в инструкции к плате.");
            }
            return v;
        }

        if (s.Sticks.Select(x => Math.Round(x.SizeGb)).Distinct().Count() > 1)
        {
            v.Kind = ChannelKind.MixedSizes; v.Level = Severity.Info; v.Title = "Планки памяти разного объёма";
            v.Text = L.T("Часть памяти может работать в одноканальном режиме. Лучше всего ставить одинаковые планки парами.");
            return v;
        }

        if (allKnown && channelCount >= 2)
        {
            v.Kind = ChannelKind.Dual; v.Level = Severity.Ok; v.Title = "Память работает в двухканальном режиме";
            v.Text = L.F("Планки стоят в разных каналах: {0}.", slots);
        }
        return v;
    }

    // ================================================================== откуда данные
    /// <summary>Из «Совета по апгрейду» (RamInfo).</summary>
    public static MemSetup FromRam(RamInfo ram, string cpu) => new()
    {
        Cpu = cpu ?? "", Laptop = ram?.Laptop ?? false, Type = ram?.Type ?? "", Slots = ram?.Slots ?? 0,
        Sticks = (ram?.Sticks ?? new()).Select(x => new MemStick
        {
            Slot = x.Slot, Bank = x.Bank, Maker = x.Maker, Part = x.Part, SizeGb = x.SizeGb, Mts = x.Mts, RatedMts = x.RatedMts, Ranks = x.Ranks, FormFactor = x.FormFactor,
        }).ToList(),
    };

    /// <summary>Проверочная подмена железа: PULSEPC_TEST_HW=&lt;файл.json&gt; {"Cpu":"…","Laptop":true,"Ram":{…как ReadRam…}}. Только для снимков.</summary>
    public static string TestFile => Environment.GetEnvironmentVariable("PULSEPC_TEST_HW") is { Length: > 0 } f && File.Exists(f) ? f : null;

    private static JsonElement? TestJson()
    {
        if (TestFile is not string f) return null;
        try { using var d = JsonDocument.Parse(File.ReadAllText(f)); return d.RootElement.Clone(); } catch { return null; }
    }

    /// <summary>Подменённые планки (PULSEPC_TEST_HW) или null.</summary>
    public static RamInfo TestRam()
    {
        if (TestJson() is not JsonElement j || !j.TryGetProperty("Ram", out var r)) return null;
        var info = UpgradeAdvisor.ParseRam(r);
        if (j.TryGetProperty("Laptop", out var l) && l.ValueKind == JsonValueKind.True) info.Laptop = true;
        return info;
    }

    /// <summary>Подменённое название процессора (PULSEPC_TEST_HW) или null.</summary>
    public static string TestCpu => TestJson() is JsonElement j && j.TryGetProperty("Cpu", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;

    /// <summary>Название процессора из реестра (быстро, без прав).</summary>
    public static string CpuName()
    {
        if (TestCpu is string t) return t;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (k?.GetValue("ProcessorNameString") as string ?? "").Trim();
        }
        catch { return ""; }
    }

    private static MemSetup cached;
    private static DateTime cachedAt;
    private static readonly object CacheGate = new();

    /// <summary>Память этого компьютера (WMI через PowerShell, несколько секунд; запоминается на 10 минут). Вызывать в фоне.</summary>
    public static MemSetup Current()
    {
        lock (CacheGate)
        {
            if (cached != null && DateTime.Now - cachedAt < TimeSpan.FromMinutes(10)) return cached;
            var ram = UpgradeAdvisor.ReadRam();
            var s = FromRam(ram, CpuName());
            if (TestRam() == null) s.Laptop |= HardwareChecks.IsLaptop();
            if (ram.Loaded) { cached = s; cachedAt = DateTime.Now; }
            return s;
        }
    }

    // ================================================================== самопроверка
    /// <summary>PulsePC.exe --feature-test MemoryProfile файл.txt — выводы на подставных данных и что видно на этом ПК (только чтение).</summary>
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        string fail = null;
        bool en = L.En;
        void Check(string what, bool ok) { sb.AppendLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) fail ??= what; }
        MemSetup Setup(string cpu, bool laptop, string type, params (string slot, string part, int mts, int rated, double gb)[] st) => new()
        {
            Cpu = cpu, Laptop = laptop, Type = type, Slots = 4,
            Sticks = st.Select(x => new MemStick { Slot = x.slot, Part = x.part, Maker = "", Mts = x.mts, RatedMts = x.rated, SizeGb = x.gb, FormFactor = laptop ? 12 : 8 }).ToList(),
        };

        // --- таблица штатных скоростей
        int O(string cpu, string type, int n = 2, int ranks = 1) => Official(SpecFor(cpu), type, n, ranks);
        Check("i5-12400F: DDR4-3200, DDR5-4800", O("12th Gen Intel(R) Core(TM) i5-12400F", "DDR4") == 3200 && O("12th Gen Intel(R) Core(TM) i5-12400F", "DDR5") == 4800);
        Check("i7-13700K: DDR5-5600, двухранговые 5200, четыре планки 4400", O("13th Gen Intel(R) Core(TM) i7-13700K", "DDR5") == 5600 && O("13th Gen Intel(R) Core(TM) i7-13700K", "DDR5", 2, 2) == 5200 && O("13th Gen Intel(R) Core(TM) i7-13700K", "DDR5", 4) == 4400);
        Check("i5-14400F: DDR5-4800 (не K)", O("Intel(R) Core(TM) i5-14400F", "DDR5") == 4800);
        Check("i7-8700K: DDR4-2666; i3-8100: 2400; i9-10900K: 2933; i5-10400F: 2666; i5-11400F: 3200",
            O("Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz", "DDR4") == 2666 && O("Intel(R) Core(TM) i3-8100 CPU @ 3.60GHz", "DDR4") == 2400 &&
            O("Intel(R) Core(TM) i9-10900K CPU @ 3.70GHz", "DDR4") == 2933 && O("Intel(R) Core(TM) i5-10400F CPU @ 2.90GHz", "DDR4") == 2666 && O("11th Gen Intel(R) Core(TM) i5-11400F @ 2.60GHz", "DDR4") == 3200);
        Check("ноутбуки Intel: i7-12700H DDR5-4800, i7-13700H DDR5-5200, i9-13980HX DDR5-5600, i7-10750H DDR4-2933, i5-1135G7 DDR4-3200",
            O("12th Gen Intel(R) Core(TM) i7-12700H", "DDR5") == 4800 && O("13th Gen Intel(R) Core(TM) i7-13700H", "DDR5") == 5200 &&
            O("13th Gen Intel(R) Core(TM) i9-13980HX", "DDR5") == 5600 && O("Intel(R) Core(TM) i7-10750H CPU @ 2.60GHz", "DDR4") == 2933 && O("11th Gen Intel(R) Core(TM) i5-1135G7 @ 2.40GHz", "DDR4") == 3200);
        Check("Core Ultra: 9 285K DDR5-6400, 7 155H DDR5-5600, 7 258V LPDDR5-8533",
            O("Intel(R) Core(TM) Ultra 9 285K", "DDR5") == 6400 && O("Intel(R) Core(TM) Ultra 7 155H", "DDR5") == 5600 && O("Intel(R) Core(TM) Ultra 7 258V", "LPDDR5") == 8533);
        Check("AMD настольные: 1700 DDR4-2666, 2600 2933, 3600 3200, 5800X3D 3200, 7800X3D DDR5-5200 (четыре планки 3600), 9800X3D 5600, 8700G 5200",
            O("AMD Ryzen 7 1700 Eight-Core Processor", "DDR4") == 2666 && O("AMD Ryzen 5 2600 Six-Core Processor", "DDR4") == 2933 &&
            O("AMD Ryzen 5 3600 6-Core Processor", "DDR4") == 3200 && O("AMD Ryzen 7 5800X3D 8-Core Processor", "DDR4") == 3200 &&
            O("AMD Ryzen 7 7800X3D 8-Core Processor", "DDR5") == 5200 && O("AMD Ryzen 7 7800X3D 8-Core Processor", "DDR5", 4) == 3600 &&
            O("AMD Ryzen 7 9800X3D 8-Core Processor", "DDR5") == 5600 && O("AMD Ryzen 7 8700G w/ Radeon 780M Graphics", "DDR5") == 5200);
        Check("AMD ноутбуки: 8940HX DDR5-5200, 7945HX 5200, 7840HS 5600, 7735HS 4800, 6800H 4800, 5800H DDR4-3200, 9955HX 5600, Ryzen AI 9 HX 370 5600",
            O("AMD Ryzen 9 8940HX with Radeon Graphics", "DDR5") == 5200 && O("AMD Ryzen 9 7945HX with Radeon Graphics", "DDR5") == 5200 &&
            O("AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", "DDR5") == 5600 && O("AMD Ryzen 7 7735HS with Radeon Graphics", "DDR5") == 4800 &&
            O("AMD Ryzen 7 6800H with Radeon Graphics", "DDR5") == 4800 && O("AMD Ryzen 7 5800H with Radeon Graphics", "DDR4") == 3200 &&
            O("AMD Ryzen 9 9955HX 16-Core Processor", "DDR5") == 5600 && O("AMD Ryzen AI 9 HX 370 w/ Radeon 890M", "DDR5") == 5600);
        Check("неизвестный процессор и DDR5 на старом Ryzen — 0 (не знаем)", O("Some Future CPU 9000", "DDR5") == 0 && O("AMD Ryzen 5 5600X 6-Core Processor", "DDR5") == 0 && SpecFor("") == null);

        // --- маркировка
        Check("маркировка: KF3200C16D4/8GX 3200, KF552C40BBK2-32 5200, F5-6000J3038F16G 6000, CMK32GX5M2B5600C36 5600, CP16G56C46U5 5600, BL8G32C16U4B 3200, M378A1K43DB2-CTD 2666, M425R2GA3BB0-CWMOD 5600",
            PartSpeed("KF3200C16D4/8GX") == 3200 && PartSpeed("KF552C40BBK2-32") == 5200 && PartSpeed("F5-6000J3038F16G") == 6000 && PartSpeed("CMK32GX5M2B5600C36") == 5600 &&
            PartSpeed("CP16G56C46U5") == 5600 && PartSpeed("BL8G32C16U4B.M8FE1") == 3200 && PartSpeed("M378A1K43DB2-CTD") == 2666 && PartSpeed("M425R2GA3BB0-CWMOD") == 5600);
        Check("комплект с профилем: Kingston FURY, G.Skill, Corsair Vengeance, T-Force, Ballistix — да; Samsung, Corsair ValueSelect, Kingston ValueRAM — нет",
            ProfileKit("Kingston", "KF3200C16D4/8GX") && ProfileKit("G.Skill", "F5-6000J3038F16G") && ProfileKit("Corsair", "CMK16GX4M2B3200C16") &&
            ProfileKit("TeamGroup", "TF13D416G3200HC16F") && ProfileKit("Crucial", "BL8G32C16U4B.M8FE1") &&
            !ProfileKit("Samsung", "M378A1K43DB2-CTD") && !ProfileKit("Corsair", "CMV8GX4M1A2666C18") && !ProfileKit("Kingston", "KVR32N22S8/8"));

        // --- четыре случая из задания
        var a = Speed(Setup("12th Gen Intel(R) Core(TM) i5-12400F", false, "DDR4", ("Controller0-DIMMA2", "KF3200C16D4/8GX", 2133, 2666, 8), ("Controller1-DIMMB2", "KF3200C16D4/8GX", 2133, 2666, 8)));
        Check("i5-12400F + KF3200C16D4 на 2133: «похоже, выключен», совет с предупреждением о рисках и про список памяти платы — " + a.Title,
            a.Kind == MemKind.ProfileLikelyOff && a.AdviseProfile && a.Warning.Length > 0 && a.Official == 3200 && a.Marked == 3200 &&
            (en || a.Title.StartsWith("Похоже") && a.Warning.Contains("вылеты") && a.What.Contains("QVL") && a.Text.Contains("i5-12400F") && a.Text.Contains("DDR4-3200")));
        var b = Speed(Setup("AMD Ryzen 9 8940HX with Radeon Graphics", true, "DDR5", ("DIMM A", "M425R2GA3BB0-CWMOD", 5200, 5600, 16), ("DIMM B", "M425R2GA3BB0-CWMOD", 5200, 5600, 16)));
        Check("ноутбук Ryzen 9 8940HX, DDR5-5600 на 5200: «штатно», никакого совета про XMP — " + b.Text,
            b.Kind == MemKind.AtOfficial && !b.AdviseProfile && b.What.Length == 0 && b.Warning.Length == 0 && b.Official == 5200 &&
            (en || b.Text.StartsWith($"5200{Nb}МТ/с — штатно для Ryzen 9 8940HX") && b.Text.Contains("DDR5-5200") && !b.Full.Contains("XMP")));
        var c = Speed(Setup("AMD Ryzen 7 7800X3D 8-Core Processor", false, "DDR5", ("DIMM_A2", "F5-6000J3038F16G", 4800, 4800, 16), ("DIMM_B2", "F5-6000J3038F16G", 4800, 4800, 16)));
        Check("Ryzen 7 7800X3D, DDR5-6000 на 4800: совет включить EXPO с предупреждением", c.Kind == MemKind.ProfileLikelyOff && c.AdviseProfile && c.Warning.Length > 0 && c.Official == 5200 && (en || c.What.Contains("EXPO")));
        var d = Speed(Setup("Some Future CPU 9000", false, "DDR5", ("DIMM_A2", "F5-6000J3038F16G", 4800, 4800, 16), ("DIMM_B2", "F5-6000J3038F16G", 4800, 4800, 16)));
        Check("неизвестный процессор: без утверждений и без совета", d.Kind == MemKind.Unknown && !d.AdviseProfile && d.What.Length == 0 && (en || d.Text.Contains("программа не знает")));

        // --- остальные ветки
        var on = Speed(Setup("12th Gen Intel(R) Core(TM) i5-12400F", false, "DDR4", ("A2", "KF3200C16D4/8GX", 3200, 3200, 8), ("B2", "KF3200C16D4/8GX", 3200, 3200, 8)));
        Check("профиль включён (3200 = маркировка) — «на своей частоте», без совета", on.Kind == MemKind.Normal && on.Level == Severity.Ok && !on.AdviseProfile);
        var oc = Speed(Setup("AMD Ryzen 7 7800X3D 8-Core Processor", false, "DDR5", ("A2", "F5-6000J3038F16G", 6000, 4800, 16), ("B2", "F5-6000J3038F16G", 6000, 4800, 16)));
        Check("DDR5-6000 на 6000 при штатной 5200 — профиль работает, напоминание проверить без него при сбоях", oc.Kind == MemKind.AboveOfficial && !oc.AdviseProfile);
        var part = Speed(Setup("AMD Ryzen 7 7800X3D 8-Core Processor", false, "DDR5", ("A2", "F5-6400J3239G16G", 5600, 4800, 16), ("B2", "F5-6400J3239G16G", 5600, 4800, 16)));
        Check("6400 на 5600 при штатной 5200 — уже выше штатной, совета нет", part.Kind == MemKind.AbovePartial && !part.AdviseProfile);
        var jedec = Speed(Setup("Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz", false, "DDR4", ("A2", "M378A1K43DB2-CWE", 2666, 3200, 8), ("B2", "M378A1K43DB2-CWE", 2666, 3200, 8)));
        Check("обычные планки 3200 на i7-8700K (штатно 2666) — «штатно», без XMP", jedec.Kind == MemKind.AtOfficial && !jedec.AdviseProfile && (en || !jedec.Full.Contains("XMP")));
        var lapKit = Speed(Setup("13th Gen Intel(R) Core(TM) i7-13700H", true, "DDR5", ("DIMM A", "KF548S38IB-16", 4800, 4800, 16)));
        Check("ноутбук с планкой Kingston FURY 4800… на 4800 при штатной 5200 — без совета", !lapKit.AdviseProfile);
        var lapSlow = Speed(Setup("13th Gen Intel(R) Core(TM) i7-13700H", true, "DDR5", ("DIMM A", "KF556S40IB-16", 4800, 5600, 16)));
        Check("ноутбук: 5600 на 4800 при штатной 5200 — только описание, без XMP", lapSlow.Kind == MemKind.LaptopBelow && !lapSlow.AdviseProfile && (en || !lapSlow.Full.Contains("XMP")));
        var four = Speed(Setup("AMD Ryzen 7 7800X3D 8-Core Processor", false, "DDR5", ("A1", "F5-6000J3038F16G", 3600, 4800, 16), ("A2", "F5-6000J3038F16G", 3600, 4800, 16), ("B1", "F5-6000J3038F16G", 3600, 4800, 16), ("B2", "F5-6000J3038F16G", 3600, 4800, 16)));
        Check("четыре планки DDR5-6000 на 3600 (штатно для четырёх 3600) — совет с оговоркой про четыре планки", four.AdviseProfile && four.Official == 3600 && (en || four.What.Contains("четырьмя планками")));

        // --- каналы
        Check("слоты: ChannelA-DIMM0 → A0, P0 CHANNEL B/DIMM 1 → B1, DIMM_A1 → A1, Controller1-DIMMB2 → B2, DDR4_A2 → A2, «DIMM 0» без банка — неизвестно",
            ChannelOf("ChannelA-DIMM0", "BANK 0") == ('A', 0) && ChannelOf("DIMM 1", "P0 CHANNEL B") == ('B', 1) && ChannelOf("DIMM_A1", "BANK 0") == ('A', 1) &&
            ChannelOf("Controller1-DIMMB2", "") == ('B', 2) && ChannelOf("DDR4_A2", "") == ('A', 2) && ChannelOf("DIMM 0", "BANK 0").Channel == '\0');
        var same = Channels(Setup("12th Gen Intel(R) Core(TM) i5-12400F", false, "DDR4", ("DIMM_A1", "X", 3200, 3200, 8), ("DIMM_A2", "X", 3200, 3200, 8)));
        Check("две планки в A1 и A2 — один канал, совет переставить в A2 и B2", same.Kind == ChannelKind.SameChannel && same.Level == Severity.Warn && (en || same.What.Contains("A2 и B2") && same.What.Contains("инструкции к плате")));
        var sameLap = Channels(Setup("13th Gen Intel(R) Core(TM) i7-13700H", true, "DDR5", ("ChannelA-DIMM0", "X", 4800, 4800, 8), ("ChannelA-DIMM1", "X", 4800, 4800, 8)));
        Check("ноутбук, две планки в одном канале — только описание", sameLap.Kind == ChannelKind.SameChannel && sameLap.What.Length == 0 && sameLap.Level == Severity.Info);
        var dual = Channels(Setup("12th Gen Intel(R) Core(TM) i5-12400F", false, "DDR4", ("Controller0-DIMMA2", "X", 3200, 3200, 8), ("Controller1-DIMMB2", "X", 3200, 3200, 8)));
        Check("A2 и B2 — двухканальный режим", dual.Kind == ChannelKind.Dual && dual.Level == Severity.Ok);
        var ctl = Channels(Setup("12th Gen Intel(R) Core(TM) i5-12400F", false, "DDR5", ("Controller0-ChannelA-DIMM1", "X", 4800, 4800, 16), ("Controller1-ChannelA-DIMM1", "X", 4800, 4800, 16)));
        Check("Controller0-ChannelA и Controller1-ChannelA — разные каналы, не «один канал»", ctl.Kind == ChannelKind.Dual);
        var one = Channels(Setup("12th Gen Intel(R) Core(TM) i5-12400F", false, "DDR4", ("DIMM_A2", "X", 3200, 3200, 8)));
        Check("одна планка — одноканальный режим (одна находка на всё)", one.Kind == ChannelKind.OneStick && one.Level == Severity.Warn);
        var mixed = Channels(Setup("12th Gen Intel(R) Core(TM) i5-12400F", false, "DDR4", ("DIMM 0", "X", 3200, 3200, 8), ("DIMM 1", "Y", 3200, 3200, 16)));
        Check("8 + 16 ГБ, слоты не понять — «разного объёма»", mixed.Kind == ChannelKind.MixedSizes);
        var bogus = Channels(Setup("12th Gen Intel(R) Core(TM) i5-12400F", false, "DDR4", ("ChannelA-DIMM0", "X", 3200, 3200, 8), ("ChannelA-DIMM0", "X", 3200, 3200, 8)));
        Check("BIOS назвал обе планки одинаково — вывода «один канал» нет", bogus.Kind != ChannelKind.SameChannel);
        Check("распаянная LPDDR5 — о каналах молчим", Channels(Setup("AMD Ryzen AI 9 HX 370", true, "LPDDR5", ("A", "X", 7500, 7500, 16))).Kind == ChannelKind.None);

        // --- этот ПК, только чтение
        sb.AppendLine();
        sb.AppendLine("Что видно на этом ПК (только чтение):");
        var me = Current();
        var spec = SpecFor(me.Cpu);
        sb.AppendLine($"  процессор: {me.Cpu} → {(spec == null ? "нет в таблице" : $"{spec.Family}, {(spec.Mobile ? "ноутбук" : "настольный")}; DDR4 {spec.Ddr4}/{spec.Ddr4Four}, DDR5 {spec.Ddr5}/{spec.Ddr5Dual}/{spec.Ddr5Four}/{spec.Ddr5FourDual}, LPDDR4 {spec.Lp4}, LPDDR5 {spec.Lp5}")}");
        sb.AppendLine($"  ноутбук: {(me.Laptop ? "да" : "нет")}, тип {me.Type}, слотов {me.Slots}{(TestFile != null ? " (ПОДМЕНА PULSEPC_TEST_HW)" : "")}");
        foreach (var x in me.Sticks)
            sb.AppendLine($"  {x.Slot} [{x.Bank}] → канал {ChannelOf(x.Slot, x.Bank).Channel}{ChannelOf(x.Slot, x.Bank).Index}: {x.SizeGb:0} ГБ, {x.Mts} МТ/с (паспорт {x.RatedMts}), {x.Maker} {x.Part}, по маркировке {PartSpeed(x.Part)}, профиль: {(ProfileKit(x.Maker, x.Part) ? "да" : "нет")}, рангов {x.Ranks}");
        var sp = Speed(me);
        sb.AppendLine($"  частота: [{sp.Kind}] {sp.Title} — {sp.Full}");
        var ch = Channels(me);
        sb.AppendLine($"  каналы: [{ch.Kind}] {ch.Title} — {ch.Full}");
        return (fail == null ? "OK" : "FAIL: " + fail) + "\n" + sb;
    }
}
