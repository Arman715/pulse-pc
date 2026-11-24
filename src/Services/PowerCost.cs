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

    /// <summary>Средняя цена киловатт-часа в этом состоянии с учётом зон тарифа (NaN, если тариф не задан).</summary>
    public double Price = double.NaN;

    /// <summary>Без блока питания и монитора.</summary>
    public double DcW => GpuW + CpuW + BaseW;
    /// <summary>Из розетки, Вт (оценка, если хоть одна часть оценена).</summary>
    public double TotalW => DcW + LossW + MonitorW;
    public double KwhPerDay => double.IsNaN(HoursPerDay) || double.IsNaN(TotalW) ? double.NaN : TotalW * HoursPerDay / 1000;
    public double RubPerDay => KwhPerDay * Price;
}

/// <summary>Зона тарифа: своя цена в свои часы по времени этого компьютера («ночь 23–7», «пик 7–10, 17–21»).</summary>
public sealed class TariffZone
{
    public string Name = "";
    public double Price = double.NaN;
    /// <summary>Отрезки в минутах от полуночи, конец не входит. From больше To, если отрезок идёт через полночь.</summary>
    public List<(int From, int To)> Hours = new();

    public bool Covers(int minute) => Hours.Any(h => h.From < h.To ? minute >= h.From && minute < h.To : minute >= h.From || minute < h.To);
}

/// <summary>Расход по одной игре за последние 30 дней.</summary>
public sealed class GameCost
{
    public string Game = "";
    public int Sessions;
    public double Hours;
    public double GpuW = double.NaN, CpuLoad = double.NaN, TotalW = double.NaN;
    /// <summary>Средняя цена киловатт-часа по времени сессий этой игры.</summary>
    public double Price = double.NaN;
    public double KwhPerHour => TotalW / 1000;
    public double Kwh => TotalW * Hours / 1000;
    public double Rub => Kwh * Price;
    public double RubPerHour => KwhPerHour * Price;
}

/// <summary>Сводка «Электричество».</summary>
public sealed class PowerReport
{
    public PowerMode Game = new(), Idle = new();
    public string CpuName = "";
    public int Tdp = PowerCost.DefaultTdp;
    public bool TdpKnown;
    /// <summary>Тариф, ₽ за кВт·ч (NaN, если не задан). При зонах это основная цена: она действует в часы, не занятые зонами.</summary>
    public double Tariff = double.NaN;
    public string TariffName = "";
    /// <summary>Зоны со своей ценой и часами; пусто, если цена одна на все часы.</summary>
    public List<TariffZone> Zones = new();
    /// <summary>Доля времени работы в каждой зоне (основная первой); только при зонах.</summary>
    public List<(string Name, double Share)> ZoneShares = new();
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
    public bool HasZones => Zones.Count > 0;
    /// <summary>Рубли в сутки: игры и простой, каждый по своей средней цене.</summary>
    public double RubDay => HasTariff
        ? (double.IsNaN(Game.KwhPerDay) ? 0 : Game.RubPerDay) + (IdleKnown && !double.IsNaN(Idle.KwhPerDay) ? Idle.RubPerDay : 0)
        : double.NaN;

    public TariffZone ZoneAt(DateTime t)
    {
        int minute = t.Hour * 60 + t.Minute;
        return Zones.FirstOrDefault(z => z.Covers(minute));
    }

    public double PriceAt(DateTime t) => HasTariff ? ZoneAt(t)?.Price ?? Tariff : double.NaN;
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
    public const string KTariff = "power.tariff", KMonitor = "power.monitor", KTariffName = "power.tariff.name", KZones = "power.zones";
    public const int DefaultTdp = 65;
    /// <summary>Плата, память, диски, вентиляторы, подсветка — середина обычных 40–60 Вт.</summary>
    public const double BaseW = 50;
    /// <summary>Ноутбук: плата, память, SSD, вентиляторы и встроенный экран — около 15 Вт (оценка).</summary>
    public const double LaptopBaseW = 15;
    /// <summary>Для самопроверки: подменяет признак ноутбука (null — общий признак LaptopCheck).</summary>
    internal static bool? TestLaptop;
    /// <summary>Постоянная часть расхода для этого компьютера: у ноутбука она втрое меньше.</summary>
    public static double BaseNow => (TestLaptop ?? LaptopCheck.Applies) ? LaptopBaseW : BaseW;
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

    /// <summary>Название основной цены; пока его не меняли, это «День» на языке программы.</summary>
    public static string TariffName(AppSettings s) => s.Get(KTariffName) is { Length: > 0 } n ? n : L.T("День");
    public static List<TariffZone> Zones(AppSettings s) => ParseZones(s.Get(KZones));

    // ================================================================== зоны тарифа
    private static readonly Regex Span = new(@"^\s*([0-9]{1,2})(?:[:.]([0-9]{2}))?\s*[-\u2013\u2014]\s*([0-9]{1,2})(?:[:.]([0-9]{2}))?\s*$");

    /// <summary>Часы зоны: «23–7», «7:30-10, 17–21». Отрезок через полночь можно; пустой отрезок и 25:00 нельзя.</summary>
    public static bool TryParseHours(string text, out List<(int From, int To)> hours)
    {
        hours = new();
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (string part in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var m = Span.Match(part);
            if (!m.Success || !TryMinute(m.Groups[1].Value, m.Groups[2].Value, out int from) || !TryMinute(m.Groups[3].Value, m.Groups[4].Value, out int to) || from == to)
            {
                hours.Clear();
                return false;
            }
            hours.Add((from, to));
        }
        return hours.Count > 0;

        static bool TryMinute(string hour, string min, out int minute)
        {
            int h = int.Parse(hour), m = min.Length > 0 ? int.Parse(min) : 0;
            minute = (h * 60 + m) % (24 * 60);         // «24» и «0» – одна и та же полночь
            return m < 60 && h * 60 + m <= 24 * 60;
        }
    }

    /// <summary>Для поля ввода: «23–7», «7:30–10, 17–21», «22–24» (конец в полночь – «24», а не «0»).</summary>
    public static string ShowHours(IEnumerable<(int From, int To)> hours) =>
        string.Join(", ", hours.Select(h => Clock(h.From) + "–" + Clock(h.To == 0 ? 24 * 60 : h.To)));

    private static string Clock(int minute) => minute % 60 == 0 ? (minute / 60).ToString(CultureInfo.InvariantCulture) : $"{minute / 60}:{minute % 60:00}";

    /// <summary>Зоны для настроек: по строке на зону, «название|цена|часы».</summary>
    public static string FormatZones(IEnumerable<TariffZone> zones) =>
        string.Join("\n", zones.Select(z => CleanName(z.Name) + "|" + z.Price.ToString(CultureInfo.InvariantCulture) + "|" + ShowHours(z.Hours)));

    /// <summary>Название зоны без «|» и переводов строки: ими разделены зоны в настройках.</summary>
    public static string CleanName(string name) => Regex.Replace(name ?? "", @"[|\r\n]", " ").Trim();

    /// <summary>Зоны из настроек; испорченная строка пропускается.</summary>
    public static List<TariffZone> ParseZones(string text)
    {
        var zones = new List<TariffZone>();
        foreach (string line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split('|');
            if (f.Length == 3 && f[0].Trim().Length > 0 && TryParseTariff(f[1], out double price) && TryParseHours(f[2], out var hours))
                zones.Add(new TariffZone { Name = f[0].Trim(), Price = price, Hours = hours });
        }
        return zones;
    }

    /// <summary>Пересекаются ли часы двух зон: тогда цена минуты была бы неоднозначной.</summary>
    public static bool Overlap(IReadOnlyList<TariffZone> zones, out string first, out string second)
    {
        first = second = null;
        for (int minute = 0; minute < 24 * 60; minute++)
        {
            var hit = zones.Where(z => z.Covers(minute)).Take(2).ToList();
            if (hit.Count < 2) continue;
            first = hit[0].Name;
            second = hit[1].Name;
            return true;
        }
        return false;
    }

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
        p.BaseW = BaseNow;
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
                                    double tariff, double monitorW, DateTime now,
                                    IReadOnlyList<TariffZone> zones = null, string tariffName = null)
    {
        var r = new PowerReport
        {
            CpuName = cpuName ?? "", Tariff = tariff, MonitorW = monitorW,
            TariffName = string.IsNullOrWhiteSpace(tariffName) ? L.T("День") : tariffName,
            Zones = zones?.ToList() ?? new(),
        };
        r.Tdp = CpuTdp(cpuName, out r.TdpKnown);

        var mins = (minutes ?? Array.Empty<FanMinute>()).Where(m => m.T > now.AddDays(-UsageDays) && m.T <= now).ToList();
        var recent = (sessions ?? Array.Empty<GameSession>()).Where(s => s.End > now.AddDays(-GameDays) && s.End > s.Start).ToList();
        var week = recent.Where(s => s.End > now.AddDays(-UsageDays)).ToList();

        // ---- время работы
        if (mins.Count > 0)
        {
            r.Days = Math.Clamp((int)(now.Date - mins.Min(m => m.T).Date).TotalDays + 1, 1, UsageDays);
            r.Game.HoursPerDay = mins.Count(m => m.Game) / 60.0 / r.Days;
            r.Idle.HoursPerDay = mins.Count(m => !m.Game) / 60.0 / r.Days;
            r.IdleKnown = true;
        }
        else if (week.Count > 0)
        {
            r.Days = Math.Clamp((int)(now.Date - week.Min(s => s.Start).Date).TotalDays + 1, 1, UsageDays);
            r.Game.HoursPerDay = week.Sum(s => s.Duration.TotalHours) / r.Days;
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

        // ---- цена: по тем же минутам, по которым посчитано время работы, каждая минута по цене своей зоны
        var time = mins.Count > 0 ? mins.Select(m => (m.T, W: 1.0, m.Game)).ToList() : SessionMinutes(week).Select(x => (x.T, x.W, Game: true)).ToList();
        r.Game.Price = AvgPrice(r, time.Where(x => x.Game).Select(x => (x.T, x.W)));
        r.Idle.Price = AvgPrice(r, time.Where(x => !x.Game).Select(x => (x.T, x.W)));
        double total = time.Sum(x => x.W);
        if (r.HasZones && total > 0)
            r.ZoneShares = new TariffZone[] { null }.Concat(r.Zones)
                .Select(z => (z?.Name ?? r.TariffName, time.Where(x => r.ZoneAt(x.T) == z).Sum(x => x.W) / total))
                .Where(z => z.Item2 > 0).ToList();

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
            c.Price = AvgPrice(r, SessionMinutes(g));
            r.Games.Add(c);
        }
        r.Games = r.Games.OrderByDescending(c => c.Hours).ToList();
        return r;
    }

    /// <summary>Минуты сессий: начало минуты и её доля (последняя бывает неполной).</summary>
    private static IEnumerable<(DateTime T, double W)> SessionMinutes(IEnumerable<GameSession> sessions)
    {
        foreach (var s in sessions)
            for (var t = s.Start; t < s.End; t = t.AddMinutes(1))
                yield return (t, Math.Min(1, (s.End - t).TotalMinutes));
    }

    /// <summary>Средняя цена киловатт-часа за эти минуты; если минут нет, основная цена.</summary>
    private static double AvgPrice(PowerReport r, IEnumerable<(DateTime T, double W)> time)
    {
        double price = Weighted(time.Select(x => (r.PriceAt(x.T), x.W)));
        return double.IsNaN(price) ? r.Tariff : price;
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
        return Build(FanControl.History(), h?.Sessions() ?? Array.Empty<GameSession>(), CpuName(h), Tariff(s), Monitor(s), DateTime.Now,
                     Zones(s), TariffName(s));
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
        if (L.En) return Math.Round(v).ToString("#,0", L.Culture);         // «1,240»
        return Math.Round(v).ToString("#,0", CultureInfo.InvariantCulture).Replace(",", " ");
    }

    // ================================================================== самопроверка
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        string fail = null;
        void Check(string what, bool ok) { sb.AppendLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) fail ??= what; }

        TestLaptop = true;
        Check("ноутбук: постоянная часть 15 Вт", BaseNow == LaptopBaseW);
        TestLaptop = false;   // расчёты ниже — для настольного компьютера
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
        Check("расчёт: сутки 0,826 кВт·ч, 4,96 ₽; месяц ×30", Math.Abs(r.KwhDay - 0.82610) < 0.0001 && Math.Abs(r.RubDay - 4.9566) < 0.001 && r.HasForecast);
        var rm = Build(mins, Array.Empty<GameSession>(), "Intel(R) Core(TM) i5-12400F", 6, 30, now);
        Check("монитор 30 Вт прибавляется отдельно, без потерь блока питания", Math.Abs(rm.Game.TotalW - 285.75) < 0.01);
        var rn = Build(mins, Array.Empty<GameSession>(), "", double.NaN, 0, now);
        Check("без тарифа — рубли не считаются, киловатт-часы есть", !rn.HasTariff && double.IsNaN(rn.RubDay) && double.IsNaN(rn.Game.Price) && rn.KwhDay > 0);

        // Зоны: ночь 23–7 по 3 ₽, пик 7–10 и 17–21 по 9 ₽, остальное по 6 ₽ (основная цена).
        Check("часы: «23–7», «7:30-10, 17–21», «22-24» читаются; «0-24», «25-3», «7-7», «7:60-8», буквы, широкие цифры и пусто – отказ",
            TryParseHours("23–7", out var h1) && h1.Count == 1 && h1[0] == (23 * 60, 7 * 60)
            && TryParseHours("7:30-10, 17–21", out var h2) && h2.Count == 2 && h2[0] == (450, 600) && ShowHours(h2) == "7:30–10, 17–21"
            && TryParseHours("22-24", out var h3) && h3[0] == (22 * 60, 0) && ShowHours(h3) == "22–24" && !TryParseHours("２３-７", out _)
            && !TryParseHours("0-24", out _) && !TryParseHours("25-3", out _) && !TryParseHours("7-7", out _)
            && !TryParseHours("7:60-8", out _) && !TryParseHours("abc", out _) && !TryParseHours("", out _) && !TryParseHours("23-7, x", out _));
        var night = new TariffZone { Name = "Ночь", Price = 3, Hours = new() { (23 * 60, 7 * 60) } };
        var peak = new TariffZone { Name = "Пик", Price = 9, Hours = new() { (7 * 60, 10 * 60), (17 * 60, 21 * 60) } };
        var zones = new List<TariffZone> { night, peak };
        var back = ParseZones(FormatZones(new[] { night, new TariffZone { Name = "Пик|2", Price = 9.5, Hours = peak.Hours } }));
        Check("зоны: в настройках и обратно, «|» в названии не ломает строку",
            back.Count == 2 && back[0].Name == "Ночь" && back[0].Price == 3 && back[0].Covers(0) && !back[0].Covers(7 * 60)
            && back[1].Name == "Пик 2" && back[1].Price == 9.5 && back[1].Covers(18 * 60) && ParseZones("мусор\nбез|цены|23-7").Count == 0);
        var early = new TariffZone { Name = "Утро", Price = 5, Hours = new() { (6 * 60, 8 * 60) } };
        Check("зоны: пересечение «Ночь» 23–7 и «Утро» 6–8 найдено, «Ночь» и «Пик» встык – нет",
            Overlap(new[] { night, early }, out string o1, out string o2) && o1 == "Ночь" && o2 == "Утро" && !Overlap(zones, out _, out _));
        // Простой 9:00–13:00: час пика и три часа по основной цене, (9 + 3 × 6) / 4 = 6,75 ₽; игра 19–21 – вся в пике, 9 ₽.
        var rz = Build(mins, Array.Empty<GameSession>(), "Intel(R) Core(TM) i5-12400F", 6, 0, now, zones, "День");
        Check("зоны: игра по 9 ₽, простой в среднем 6,75 ₽, сутки 6,727 ₽",
            Math.Abs(rz.Game.Price - 9) < 1e-9 && Math.Abs(rz.Idle.Price - 6.75) < 1e-9 && Math.Abs(rz.RubDay - (0.5115 * 9 + 0.3146 * 6.75)) < 0.001
            && Math.Abs(rz.KwhDay - r.KwhDay) < 1e-9);
        Check("зоны: время по зонам – день 50 %, пик 50 %, ночи нет",
            rz.ZoneShares.Count == 2 && rz.ZoneShares[0] == ("День", 0.5) && rz.ZoneShares[1] == ("Пик", 0.5));
        var rzBack = Build(mins, Array.Empty<GameSession>(), "", 6, 0, now, new List<TariffZone> { peak, night });
        Check("зоны: порядок зон не меняет итог", Math.Abs(rzBack.RubDay - rz.RubDay) < 1e-9);
        // Сессия 22:00–00:00: час по 6 ₽ и час ночи по 3 ₽.
        var late = new List<GameSession> { new() { Game = "Deadlock", Start = now.Date.AddHours(-2), End = now.Date, AvgPowerW = 150, AvgCpu = 50 } };
        var rl = Build(Array.Empty<FanMinute>(), late, "", 6, 0, now, zones);
        Check("зоны: игра через 23:00 делится – в среднем 4,5 ₽, и в прогнозе, и по игре",
            Math.Abs(rl.Games[0].Price - 4.5) < 1e-9 && Math.Abs(rl.Game.Price - 4.5) < 1e-9 && Math.Abs(rl.Games[0].Rub - rl.Games[0].Kwh * 4.5) < 1e-9);

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
        Check("числа: 1 240 ₽ с неразрывным пробелом, 0,42 кВт·ч", Money(1240.4) == (L.En ? "1,240" : "1 240") && Kwh(0.4216).Length == 4);

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
        TestLaptop = null;
        return (fail == null ? "OK" : "FAIL: " + fail) + "\n" + sb;
    }
}
