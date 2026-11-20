using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;

namespace PulsePC.Services;

public class BootRecord
{
    public DateTime At;
    public double Seconds;        // от включения до готового рабочего стола и фоновой догрузки
    public double MainSeconds;    // до рабочего стола
    public double PostSeconds;    // догрузка после появления рабочего стола
}

public class BootSlowItem
{
    public string Kind = "";      // программа, драйвер, служба, устройство
    public string Name = "";
    public double Seconds;        // насколько задержал загрузку
}

public class BootReport
{
    public List<BootRecord> Boots = new();
    public List<BootSlowItem> Slow = new();
    public DateTime LastBoot;
    public string Error;          // почему подробностей нет (например, нет прав администратора)
}

/// <summary>
/// Время загрузки Windows из журнала «Diagnostics-Performance»: Windows сама замеряет каждую загрузку (событие 100)
/// и отдельно записывает, кто её затянул (события 101–110). Журнал читается только с правами администратора.
/// </summary>
public static class BootPerf
{
    private const string Log = "Microsoft-Windows-Diagnostics-Performance/Operational";

    public static BootReport Read()
    {
        var report = new BootReport();
        try { report.LastBoot = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64); } catch { }
        try
        {
            var query = new EventLogQuery(Log, PathType.LogName, "*[System[(EventID>=100 and EventID<=110)]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            var slowEvents = new List<(DateTime At, BootSlowItem Item)>();
            int read = 0;
            for (EventRecord e = reader.ReadEvent(); e != null && read < 400; e = reader.ReadEvent(), read++)
            {
                using (e)
                {
                    var data = Data(e);
                    DateTime at = e.TimeCreated ?? DateTime.MinValue;
                    if (e.Id == 100)
                    {
                        if (report.Boots.Count >= 12) continue;
                        double total = Ms(data, "BootTime"), main = Ms(data, "MainPathBootTime"), post = Ms(data, "BootPostBootTime");
                        if (total <= 0) continue;
                        report.Boots.Add(new BootRecord { At = at, Seconds = total / 1000, MainSeconds = main / 1000, PostSeconds = post / 1000 });
                    }
                    else
                    {
                        string kind = e.Id switch { 101 => L.T("программа"), 102 => L.T("драйвер"), 103 => L.T("служба"), 109 => L.T("устройство"), _ => null };
                        if (kind == null) continue;
                        string name = Str(data, "FriendlyName");
                        if (string.IsNullOrWhiteSpace(name)) name = Str(data, "Name");
                        double ms = Ms(data, "DegradationTime");
                        if (ms <= 0) ms = Ms(data, "TotalTime");
                        if (string.IsNullOrWhiteSpace(name) || ms <= 0) continue;
                        slowEvents.Add((at, new BootSlowItem { Kind = kind, Name = name.Trim(), Seconds = ms / 1000 }));
                    }
                }
            }
            // Виновники последней замеренной загрузки — записи, сделанные рядом с её событием 100.
            if (report.Boots.Count > 0)
            {
                var last = report.Boots[0].At;
                report.Slow = slowEvents.Where(x => Math.Abs((x.At - last).TotalMinutes) <= 10)
                    .Select(x => x.Item).GroupBy(x => x.Kind + x.Name).Select(g => g.OrderByDescending(x => x.Seconds).First())
                    .OrderByDescending(x => x.Seconds).Take(6).ToList();
            }
            report.Boots.Reverse();     // по порядку времени: старые слева
        }
        catch (UnauthorizedAccessException) { report.Error = L.T("Подробности загрузки Windows видно только с правами администратора."); }
        catch (Exception ex) { report.Error = L.F("Не удалось прочитать журнал загрузки: {0}", ex.Message); }
        return report;
    }

    private static Dictionary<string, string> Data(EventRecord e)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var xml = XDocument.Parse(e.ToXml());
            foreach (var d in xml.Descendants().Where(x => x.Name.LocalName == "Data"))
            {
                string key = (string)d.Attribute("Name");
                if (key != null) map[key] = d.Value;
            }
        }
        catch { }
        return map;
    }

    private static double Ms(Dictionary<string, string> data, string key) =>
        data.TryGetValue(key, out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static string Str(Dictionary<string, string> data, string key) => data.TryGetValue(key, out var s) ? s : "";

    /// <summary>Текст для самопроверки: что удалось прочитать.</summary>
    public static string SelfTest()
    {
        var r = Read();
        if (r.Error != null) return "Загрузка Windows: " + r.Error;
        var lines = new List<string> { $"Загрузка Windows: замеров {r.Boots.Count}, последняя загрузка {r.LastBoot:dd.MM HH:mm}" };
        foreach (var b in r.Boots.TakeLast(3)) lines.Add($"  {b.At:dd.MM HH:mm}: всего {b.Seconds:0.0} с (до рабочего стола {b.MainSeconds:0.0} с, догрузка {b.PostSeconds:0.0} с)");
        foreach (var s in r.Slow) lines.Add($"  задержал: {s.Kind} «{s.Name}» +{s.Seconds:0.0} с");
        return string.Join(Environment.NewLine, lines);
    }
}
