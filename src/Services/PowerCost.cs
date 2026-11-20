using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PulsePC.Panels;

namespace PulsePC.Services;

/// <summary>Расход в одном состоянии компьютера: в игре или без игры.</summary>
public sealed class PowerMode
{
    /// <summary>Видеокарта, Вт. Measured — по её датчику; иначе оценка.</summary>
    public double GpuW = double.NaN;
    public bool GpuMeasured;
    /// <summary>Загрузка процессора, %. LoadMeasured — средняя по записям; иначе принято.</summary>
    public double CpuLoad = double.NaN;
    public bool LoadMeasured;
    public double CpuW, BaseW, LossW, MonitorW;
    /// <summary>Сколько часов в сутки компьютер в этом состоянии (NaN — неизвестно).</summary>
    public double HoursPerDay = double.NaN;

    /// <summary>Без блока питания и монитора.</summary>
    public double DcW => GpuW + CpuW + BaseW;
    /// <summary>Из розетки, Вт (оценка, если хоть одна часть оценена).</summary>
    public double TotalW => DcW + LossW + MonitorW;
    public double KwhPerDay => double.IsNaN(HoursPerDay) || double.IsNaN(TotalW) ? double.NaN : TotalW * HoursPerDay / 1000;
}

/// <summary>Расход по одной игре за последние 30 дней.</summary>
public sealed class GameCost
{
    public string Game = "";
    public int Sessions;
    public double Hours;
    public double GpuW = double.NaN, CpuLoad = double.NaN, TotalW = double.NaN;
    public double KwhPerHour => TotalW / 1000;
    public double Kwh => TotalW * Hours / 1000;
}

/// <summary>Сводка «Электричество».</summary>
public sealed class PowerReport
{
    public PowerMode Game = new(), Idle = new();
    public string CpuName = "";
    public int Tdp = PowerCost.DefaultTdp;
    public bool TdpKnown;
    /// <summary>Тариф, ₽ за кВт·ч (NaN — не задан).</summary>
    public double Tariff = double.NaN;
    /// <summary>Монитор, Вт (0 — не учитывается).</summary>
    public double MonitorW;
    /// <summary>По скольким дням посчитано время работы (0 — данных нет).</summary>
    public int Days;
    /// <summary>Время работы без игр известно (есть поминутная статистика).</summary>
    public bool IdleKnown;
    public List<GameCost> Games = new();

    public double KwhDay => (double.IsNaN(Game.KwhPerDay) ? 0 : Game.KwhPerDay) + (IdleKnown && !double.IsNaN(Idle.KwhPerDay) ? Idle.KwhPerDay : 0);
    public bool HasForecast => Days > 0 && (!double.IsNaN(Game.KwhPerDay) || IdleKnown && !double.IsNaN(Idle.KwhPerDay)) && KwhDay > 0;
    public bool HasTariff => !double.IsNaN(Tariff);
    public double Rub(double kwh) => HasTariff ? kwh * Tariff : double.NaN;
}

/// <summary>
/// «Электричество»: сколько берёт компьютер и во что это обходится.
/// Видеокарта — измерено (её датчик: поминутная статистика «Вентиляторов» и записанные сессии, GameSession.AvgPowerW).
/// Процессор — оценка: паспортная мощность (TDP) по модели × загрузка. Плата, память, диски, вентиляторы — постоянная часть,
/// блок питания — потери около 10 %. Монитор — только если пользователь вписал его мощность.
/// Ничего не меняет в системе: читает статистику и сессии, пишет только свои настройки программы.
/// </summary>
public static class PowerCost
{
    public const string KTariff = "power.tariff", KMonitor = "power.monitor";
    public const int DefaultTdp = 65;
    /// <summary>Плата, память, диски, вентиляторы, подсветка — середина обычных 40–60 Вт.</summary>
    public const double BaseW = 50;
    /// <summary>Потери в блоке питания (КПД около 90 %).</summary>
    public const double LossShare = 0.10;
    /// <summary>Видеокарта в простое, если датчика нет, — оценка.</summary>
    public const double IdleGpuGuessW = 15;
    public const double IdleCpuGuess = 10, GameCpuGuess = 50;
    public const int GameDays = 30, UsageDays = 7;

    // ================================================================== настройки
    /// <summary>Тариф из строки: «6,43», «6.43», «6,43 ₽». Разумные пределы — от 0,1 до 100 ₽.</summary>
    public static bool TryParseTariff(string text, out double tariff) => TryNumber(text, 0.1, 100, out tariff);

    /// <summary>Мощность монитора из строки: пусто — не учитывать (0), иначе 5–300 Вт.</summary>
    public static bool TryParseMonitor(string text, out double watts)
    {
        watts = 0;
        if (string.IsNullOrWhiteSpace(text)) return true;
        return TryNumber(text, 5, 300, out watts);
    }

    private static bool TryNumber(string text, double min, double max, out double value)
    {
        value = double.NaN;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = Regex.Replace(text, @"[^\d,\.]", "").Replace(',', '.');
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v < min || v > max) return false;
        value = v;
        return true;
    }

    public static double Tariff(AppSettings s) => TryParseTariff(s.Get(KTariff), out double t) ? t : double.NaN;
    public static double Monitor(AppSettings s) => TryParseMonitor(s.Get(KMonitor), out double w) ? w : 0;

    /// <summary>Для поля ввода: «6,43».</summary>
    public static string Show(double v) => double.IsNaN(v) || v <= 0 ? "" : v.ToString(v % 1 == 0 ? "0" : "0.##", CultureInfo.CurrentCulture);

    // ================================================================== процессор
    /// <summary>Название процессора: из паспорта железа, иначе из реестра (только чтение).</summary>
    public static string CpuName(PanelHost h)
    {
        string name = h?.Hardware()?.CpuName;
        if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (k?.GetValue("ProcessorNameString") as string ?? "").Trim();
        }
        catch { return ""; }
    }

    /// <summary>Распространённые модели, у которых паспортная мощность не следует из общего правила.</summary>
    private static readonly (Regex Pattern, int W)[] Exact =
    {
        (new(@"Ryzen 9 (7950X3D|7900X3D|9950X3D|9900X3D)\b", RegexOptions.IgnoreCase), 120),
        (new(@"Ryzen 7 (7800X3D|9800X3D)\b", RegexOptions.IgnoreCase), 120),
        (new(@"Ryzen [57] (5800X3D|5700X3D|5600X3D)\b", RegexOptions.IgnoreCase), 105),
        (new(@"Ryzen 9 (7950X|7900X|9950X)\b", RegexOptions.IgnoreCase), 170),
        (new(@"Ryzen 9 9900X\b", RegexOptions.IgnoreCase), 120),
        (new(@"Ryzen [57] (9600X|9700X)\b", RegexOptions.IgnoreCase), 65),
        (new(@"Ryzen [57] (7600X|7700X)\b", RegexOptions.IgnoreCase), 105),
        (new(@"Ryzen 9 (5950X|5900X|3950X|3900X)\b", RegexOptions.IgnoreCase), 105),
        (new(@"Ryzen 7 (5800X|3800X|2700X)\b", RegexOptions.IgnoreCase), 105),
        (new(@"Ryzen 5 (5600X|5500X3D)\b", RegexOptions.IgnoreCase), 65),
        (new(@"Ryzen 5 (3600X|2600X)\b", RegexOptions.IgnoreCase), 95),
        (new(@"Ryzen 7 (3700X|5700X)\b", RegexOptions.IgnoreCase), 65),
        (new(@"i9-1[34]900KS\b", RegexOptions.IgnoreCase), 150),
        (new(@"i[579]-[6789]\d{3}K", RegexOptions.IgnoreCase), 91),
    };

    private static readonly Regex IntelCore = new(@"\bi[3579]-(\d{4,5})([A-Z]*\d?)", RegexOptions.IgnoreCase);
    private static readonly Regex IntelUltra = new(@"Ultra [3579] (\d{3})([A-Z]*)", RegexOptions.IgnoreCase);
    private static readonly Regex Ryzen = new(@"Ryzen (?:Threadripper |AI )?(?:PRO )?\d+ (?:PRO )?(\d{3,4})([A-Z0-9]*)", RegexOptions.IgnoreCase);

    /// <summary>Паспортная мощность процессора, Вт. known=false — модель не узнана, взято 65 Вт.</summary>
    public static int CpuTdp(string name, out bool known)
    {
        known = true;
        name ??= "";
        foreach (var (p, w) in Exact) if (p.IsMatch(name)) return w;

        var m = IntelUltra.Match(name);
        if (m.Success)
        {
            string s = m.Groups[2].Value.ToUpperInvariant();
            return s switch { "K" or "KF" => 125, "T" => 35, "HX" => 55, "H" => 28, "U" => 15, "V" => 17, _ => 65 };
        }
        m = IntelCore.Match(name);
        if (m.Success)
        {
            string s = m.Groups[2].Value.ToUpperInvariant();
            if (s.StartsWith("KS")) return 150;
            if (s.StartsWith("K")) return 125;
            if (s.StartsWith("HX")) return 55;
            if (s.StartsWith("H")) return 45;
            if (s.StartsWith("U") || Regex.IsMatch(s, @"^G\d")) return 15;
            if (s.StartsWith("P")) return 28;
            if (s.StartsWith("T")) return 35;
            return 65;                                  // без буквы и «F» — обычный настольный
        }
        m = Ryzen.Match(name);
        if (m.Success)
        {
            string s = m.Groups[2].Value.ToUpperInvariant();
            if (s.Contains("X3D")) return 120;
            if (s.StartsWith("HX")) return 55;
            if (s.StartsWith("HS")) return 35;
            if (s.StartsWith("H")) return 45;
            if (s.StartsWith("U")) return 15;
            if (s.StartsWith("GE") || s == "E") return 35;
            if (s.StartsWith("X")) return 105;
            return 65;
        }
        known = false;
        return DefaultTdp;
    }

    // ================================================================== расчёт
    /// <summary>Заполнить части расхода: процессор по TDP и загрузке, постоянная часть, потери блока питания, монитор.</summary>
    public static void Finish(PowerMode p, int tdp, double monitorW)
    {
        p.CpuW = tdp * Math.Clamp(double.IsNaN(p.CpuLoad) ? 0 : p.CpuLoad, 0, 100) / 100;
        p.BaseW = BaseW;
        p.LossW = double.IsNaN(p.GpuW) ? double.NaN : (p.GpuW + p.CpuW + p.BaseW) * LossShare;
        p.MonitorW = Math.Max(0, monitorW);
    }

    private static double Weighted(IEnumerable<(double V, double W)> items)
    {
        double sum = 0, weight = 0;
        foreach (var (v, w) in items) { if (double.IsNaN(v) || w <= 0) continue; sum += v * w; weight += w; }
        return weight > 0 ? sum / weight : double.NaN;
    }

    /// <summary>
    /// Сводка на подставляемых данных. minutes — поминутная статистика за 7 дней (FanControl.History), sessions — записанные игры.
    /// Время работы и простой — по статистике (пока программа запущена); если её нет, время игр — по сессиям, простой неизвестен.
    /// </summary>
    public static PowerReport Build(IReadOnlyList<FanMinute> minutes, IReadOnlyList<GameSession> sessions, string cpuName,
                                    double tariff, double monitorW, DateTime now)
    {
        var r = new PowerReport { CpuName = cpuName ?? "", Tariff = tariff, MonitorW = monitorW };
        r.Tdp = CpuTdp(cpuName, out r.TdpKnown);

        var mins = (minutes ?? Array.Empty<FanMinute>()).Where(m => m.T > now.AddDays(-UsageDays) && m.T <= now).ToList();
        var recent = (sessions ?? Array.Empty<GameSession>()).Where(s => s.End > now.AddDays(-GameDays) && s.End > s.Start).ToList();

        // ---- время работы
        if (mins.Count > 0)
        {
            r.Days = Math.Clamp((int)(now.Date - mins.Min(m => m.T).Date).TotalDays + 1, 1, UsageDays);
            r.Game.HoursPerDay = mins.Count(m => m.Game) / 60.0 / r.Days;
            r.Idle.HoursPerDay = mins.Count(m => !m.Game) / 60.0 / r.Days;
            r.IdleKnown = true;
        }
        else
        {
            var week = recent.Where(s => s.End > now.AddDays(-UsageDays)).ToList();
            if (week.Count > 0)
            {
                r.Days = Math.Clamp((int)(now.Date - week.Min(s => s.Start).Date).TotalDays + 1, 1, UsageDays);
                r.Game.HoursPerDay = week.Sum(s => s.Duration.TotalHours) / r.Days;
            }
        }

        // ---- видеокарта и процессор в игре: сначала сессии (дольше история), потом статистика
        var powered = recent.Where(s => s.AvgPowerW > 0).ToList();
        r.Game.GpuW = Weighted(powered.Select(s => (s.AvgPowerW, s.Duration.TotalSeconds)));
        if (double.IsNaN(r.Game.GpuW)) r.Game.GpuW = Avg(mins.Where(m => m.Game).Select(m => m.Watts));
        r.Game.GpuMeasured = !double.IsNaN(r.Game.GpuW);
        r.Game.CpuLoad = Weighted(recent.Where(s => s.AvgCpu > 0).Select(s => (s.AvgCpu, s.Duration.TotalSeconds)));
        if (double.IsNaN(r.Game.CpuLoad)) r.Game.CpuLoad = Avg(mins.Where(m => m.Game).Select(m => m.Cpu));
        r.Game.LoadMeasured = !double.IsNaN(r.Game.CpuLoad);
        if (!r.Game.LoadMeasured) r.Game.CpuLoad = GameCpuGuess;

        // ---- без игр: только статистика
        r.Idle.GpuW = Avg(mins.Where(m => !m.Game).Select(m => m.Watts));
        r.Idle.GpuMeasured = !double.IsNaN(r.Idle.GpuW);
        if (!r.Idle.GpuMeasured) r.Idle.GpuW = IdleGpuGuessW;
        r.Idle.CpuLoad = Avg(mins.Where(m => !m.Game).Select(m => m.Cpu));
        r.Idle.LoadMeasured = !double.IsNaN(r.Idle.CpuLoad);
        if (!r.Idle.LoadMeasured) r.Idle.CpuLoad = IdleCpuGuess;

        Finish(r.Game, r.Tdp, monitorW);
        Finish(r.Idle, r.Tdp, monitorW);

        // ---- по играм
        foreach (var g in recent.GroupBy(s => s.Game))
        {
            var c = new GameCost
            {
                Game = g.Key,
                Sessions = g.Count(),
                Hours = g.Sum(s => s.Duration.TotalHours),
                GpuW = Weighted(g.Where(s => s.AvgPowerW > 0).Select(s => (s.AvgPowerW, s.Duration.TotalSeconds))),
                CpuLoad = Weighted(g.Where(s => s.AvgCpu > 0).Select(s => (s.AvgCpu, s.Duration.TotalSeconds))),
            };
            var p = new PowerMode { GpuW = c.GpuW, CpuLoad = double.IsNaN(c.CpuLoad) ? GameCpuGuess : c.CpuLoad };
            Finish(p, r.Tdp, monitorW);
            c.TotalW = p.TotalW;
            r.Games.Add(c);
        }
        r.Games = r.Games.OrderByDescending(c => c.Hours).ToList();
        return r;
    }

    private static double Avg(IEnumerable<float> values)
    {
        double sum = 0; int n = 0;
        foreach (var v in values) if (!float.IsNaN(v)) { sum += v; n++; }
        return n > 0 ? sum / n : double.NaN;
    }

    /// <summary>Сводка по настоящим данным программы (только чтение).</summary>
    public static PowerReport Current(PanelHost h)
    {
        var s = h?.Settings ?? new AppSettings();
        return Build(FanControl.History(), h?.Sessions() ?? Array.Empty<GameSession>(), CpuName(h), Tariff(s), Monitor(s), DateTime.Now);
    }

    /// <summary>Сейчас: видеокарта по датчику, процессор — оценка по загрузке.</summary>
    public static PowerMode Now(LiveData live, int tdp, double monitorW)
    {
        var p = new PowerMode
        {
            GpuW = live != null && live.GpuPowerW is > 0 and < 2000 ? live.GpuPowerW : double.NaN,
            CpuLoad = live?.CpuLoad ?? double.NaN,
        };
        p.GpuMeasured = !double.IsNaN(p.GpuW);
        p.LoadMeasured = !double.IsNaN(p.CpuLoad);
        Finish(p, tdp, monitorW);
        return p;
    }

    // ================================================================== числа
    /// <summary>«0,42», «3,1», «62».</summary>
    public static string Kwh(double v) => double.IsNaN(v) ? "—" : v.ToString(v < 10 ? "0.00" : v < 100 ? "0.0" : "0", CultureInfo.CurrentCulture);

    /// <summary>«1,53», «24,8», «1 240».</summary>
    public static string Money(double v)
    {
        if (double.IsNaN(v)) return "—";
        if (v < 10) return v.ToString("0.00", CultureInfo.CurrentCulture);
        if (v < 100) return v.ToString("0.0", CultureInfo.CurrentCulture);
        return Math.Round(v).ToString("#,0", CultureInfo.InvariantCulture).Replace(",", " ");
    }

    // ================================================================== самопроверка
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        string fail = null;
        void Check(string what, bool ok) { sb.AppendLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) fail ??= what; }

        Check("TDP: i5-12400F — 65 Вт", CpuTdp("12th Gen Intel(R) Core(TM) i5-12400F", out bool k1) == 65 && k1);
        Check("TDP: i7-13700K — 125 Вт", CpuTdp("13th Gen Intel(R) Core(TM) i7-13700K", out _) == 125);
        Check("TDP: i9-14900KS — 150 Вт", CpuTdp("Intel(R) Core(TM) i9-14900KS", out _) == 150);
        Check("TDP: i7-7700K — 91 Вт", CpuTdp("Intel(R) Core(TM) i7-7700K CPU @ 4.20GHz", out _) == 91);
        Check("TDP: i7-12700H (ноутбук) — 45 Вт", CpuTdp("12th Gen Intel(R) Core(TM) i7-12700H", out _) == 45);
        Check("TDP: Core Ultra 7 265K — 125 Вт", CpuTdp("Intel(R) Core(TM) Ultra 7 265K", out _) == 125);
        Check("TDP: Ryzen 7 7800X3D — 120 Вт", CpuTdp("AMD Ryzen 7 7800X3D 8-Core Processor", out _) == 120);
        Check("TDP: Ryzen 5 5600X — 65 Вт", CpuTdp("AMD Ryzen 5 5600X 6-Core Processor", out _) == 65);
        Check("TDP: Ryzen 9 7950X — 170 Вт", CpuTdp("AMD Ryzen 9 7950X 16-Core Processor", out _) == 170);
        Check("TDP: Ryzen 5 7600 — 65 Вт", CpuTdp("AMD Ryzen 5 7600 6-Core Processor", out _) == 65);
        Check("TDP: Ryzen 7 5800H — 45 Вт", CpuTdp("AMD Ryzen 7 5800H with Radeon Graphics", out _) == 45);
        Check("TDP: неизвестный процессор — 65 Вт, помечен как оценка", CpuTdp("Some Future CPU", out bool k2) == 65 && !k2 && CpuTdp(null, out _) == 65);

        Check("тариф: «6,43», «6.43», «5 ₽» читаются",
            TryParseTariff("6,43", out double t1) && t1 == 6.43 && TryParseTariff(" 6.43 ", out double t2) && t2 == 6.43 && TryParseTariff("5 ₽", out double t3) && t3 == 5);
        Check("тариф: пусто, буквы, 0 и 1000 — отказ",
            !TryParseTariff("", out _) && !TryParseTariff("abc", out _) && !TryParseTariff("0", out _) && !TryParseTariff("1000", out _));
        Check("монитор: пусто — не учитывать, 30 — 30 Вт, 2000 — отказ",
            TryParseMonitor("", out double m0) && m0 == 0 && TryParseMonitor("30", out double m1) && m1 == 30 && !TryParseMonitor("2000", out _));

        // Подставные данные: 3 дня, каждый день 2 ч игры (видеокарта 150 Вт, процессор 50 %) и 4 ч без игр (15 Вт, 10 %).
        var now = new DateTime(2026, 11, 19, 23, 0, 0);
        var mins = new List<FanMinute>();
        for (int d = 0; d < 3; d++)
        {
            var day = now.Date.AddDays(-2 + d);
            for (int i = 0; i < 240; i++) mins.Add(new FanMinute { T = day.AddHours(9).AddMinutes(i), Temp = 40, Fan = 0, Watts = 15, Cpu = 10, Load = 2 });
            for (int i = 0; i < 120; i++) mins.Add(new FanMinute { T = day.AddHours(19).AddMinutes(i), Temp = 70, Fan = 50, Watts = 150, Cpu = 50, Load = 97, Game = true });
        }
        var r = Build(mins, Array.Empty<GameSession>(), "Intel(R) Core(TM) i5-12400F", 6, 0, now);
        // Игра: 150 + 65 × 0,5 + 50 = 232,5 Вт, с потерями 255,75 Вт × 2 ч = 0,5115 кВт·ч.
        // Простой: 15 + 6,5 + 50 = 71,5 Вт, с потерями 78,65 Вт × 4 ч = 0,3146 кВт·ч.
        Check("расчёт: 3 дня по статистике, 2 ч игры и 4 ч простоя в сутки", r.Days == 3 && Math.Abs(r.Game.HoursPerDay - 2) < 1e-9 && Math.Abs(r.Idle.HoursPerDay - 4) < 1e-9);
        Check("расчёт: в игре 255,75 Вт (видеокарта измерена, процессор — оценка)", Math.Abs(r.Game.TotalW - 255.75) < 0.01 && r.Game.GpuMeasured && r.Game.LoadMeasured);
        Check("расчёт: без игр 78,65 Вт", Math.Abs(r.Idle.TotalW - 78.65) < 0.01 && r.Idle.GpuMeasured);
        Check("расчёт: сутки 0,826 кВт·ч, 4,96 ₽; месяц ×30", Math.Abs(r.KwhDay - 0.82610) < 0.0001 && Math.Abs(r.Rub(r.KwhDay) - 4.9566) < 0.001 && r.HasForecast);
        var rm = Build(mins, Array.Empty<GameSession>(), "Intel(R) Core(TM) i5-12400F", 6, 30, now);
        Check("монитор 30 Вт прибавляется отдельно, без потерь блока питания", Math.Abs(rm.Game.TotalW - 285.75) < 0.01);
        var rn = Build(mins, Array.Empty<GameSession>(), "", double.NaN, 0, now);
        Check("без тарифа — рубли не считаются, киловатт-часы есть", !rn.HasTariff && double.IsNaN(rn.Rub(1)) && rn.KwhDay > 0);

        var sessions = new List<GameSession>
        {
            new() { Game = "Deadlock", Start = now.AddHours(-30), End = now.AddHours(-28), AvgPowerW = 160, AvgCpu = 60 },
            new() { Game = "Deadlock", Start = now.AddHours(-6), End = now.AddHours(-5), AvgPowerW = 130, AvgCpu = 60 },
            new() { Game = "PUBG", Start = now.AddDays(-40), End = now.AddDays(-40).AddHours(3), AvgPowerW = 170, AvgCpu = 40 },
        };
        var rs = Build(Array.Empty<FanMinute>(), sessions, "Intel(R) Core(TM) i5-12400F", 6, 0, now);
        var dl = rs.Games.FirstOrDefault();
        Check("по играм: только последние 30 дней, видеокарта — среднее по времени (150 Вт)",
            rs.Games.Count == 1 && dl?.Game == "Deadlock" && dl.Sessions == 2 && Math.Abs(dl.Hours - 3) < 1e-9 && Math.Abs(dl.GpuW - 150) < 0.01);
        Check("без статистики: время игр по сессиям, простой неизвестен", !rs.IdleKnown && rs.Days == 2 && Math.Abs(rs.Game.HoursPerDay - 1.5) < 1e-9);
        var empty = Build(Array.Empty<FanMinute>(), Array.Empty<GameSession>(), "", 6, 0, now);
        Check("нет данных — нет прогноза", !empty.HasForecast && empty.Days == 0 && empty.Games.Count == 0);
        Check("числа: 1 240 ₽ с неразрывным пробелом, 0,42 кВт·ч", Money(1240.4) == "1 240" && Kwh(0.4216).Length == 4);

        // ---- что видно на этом ПК (только чтение)
        sb.AppendLine();
        string cpu = CpuName(null);
        int tdp = CpuTdp(cpu, out bool known);
        sb.AppendLine($"Процессор: {(cpu.Length > 0 ? cpu : "не прочитан")} — {tdp} Вт{(known ? "" : " (модель не узнана, взято по умолчанию)")}");
        var hist = FanControl.History();
        sb.AppendLine($"Статистика за 7 дней: {hist.Count} мин, с потреблением видеокарты — {hist.Count(m => !float.IsNaN(m.Watts))} мин, с загрузкой процессора — {hist.Count(m => !float.IsNaN(m.Cpu))} мин");
        try
        {
            var all = SessionStore.Load();
            sb.AppendLine($"Сессии: {all.Count}, с потреблением видеокарты — {all.Count(s => s.AvgPowerW > 0)}, за последние {GameDays} дней — {all.Count(s => s.End > DateTime.Now.AddDays(-GameDays))}");
            var real = Build(hist, all, cpu, double.NaN, 0, DateTime.Now);
            if (real.HasForecast)
                sb.AppendLine($"Оценка: в игре {real.Game.TotalW:0} Вт, без игр {real.Idle.TotalW:0} Вт, в сутки {Kwh(real.KwhDay)} кВт·ч (по {real.Days} дн.)");
        }
        catch (Exception ex) { sb.AppendLine("Сессии не прочитаны: " + ex.Message); }
        return (fail == null ? "OK" : "FAIL: " + fail) + "\n" + sb;
    }
}
