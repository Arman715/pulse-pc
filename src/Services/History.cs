using System.Text.Json;
using System.Text.Json.Serialization;

namespace PulsePC.Services;

/// <summary>Одна минута истории: средние значения и максимум температуры.</summary>
public class HistoryPoint
{
    public DateTime T { get; set; }
    public float Cpu { get; set; }
    public float Gpu { get; set; }
    public float Temp { get; set; }
    public float RamUsed { get; set; }
}

/// <summary>
/// История показателей вне игр: показания сворачиваются в поминутные точки и хранятся двое суток.
/// Копится, пока программа запущена (в том числе свёрнутая к часам), и переживает перезапуск.
/// </summary>
public sealed class HistoryLog
{
    private const int KeepMinutes = 48 * 60;
    private static readonly JsonSerializerOptions Json = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private readonly List<HistoryPoint> points = new();
    private DateTime minute = DateTime.MinValue, lastSave = DateTime.Now;
    private double cpuSum, gpuSum, ramSum, tempMax;
    private int cpuN, gpuN, ramN;

    private static string FilePath => Path.Combine(SessionStore.Folder, "history.json");

    public int Count => points.Count;

    /// <summary>Прочитать историю; испорченный файл — из копии «.bak» (раньше пустой список при ошибке затирал двое суток истории).</summary>
    public void Load()
    {
        try
        {
            var loaded = AtomicFile.Read(FilePath, text => JsonSerializer.Deserialize<List<HistoryPoint>>(text, Json));
            if (loaded == null)
            {
                if (File.Exists(FilePath)) Health.Fail("История", L.T("history.json и его копия не читаются — история начнётся заново"));
                return;
            }
            DateTime cutoff = DateTime.Now.AddMinutes(-KeepMinutes);
            points.AddRange(loaded.Where(p => p != null && p.T > cutoff).OrderBy(p => p.T));
        }
        catch (Exception ex) { Health.Fail("История", L.F("чтение: {0}", ex.Message)); }
    }

    /// <summary>Сохранить атомарно: обрыв питания посреди записи оставляет прежний файл, а не обрезок.</summary>
    public void Save()
    {
        lastSave = DateTime.Now;        // и при ошибке: следующая попытка — через 5 минут, а не на каждом такте
        try
        {
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(points, Json));
            Health.Ok("История");
        }
        catch (Exception ex) { Health.Fail("История", L.F("сохранение: {0}", ex.Message)); }
    }

    /// <summary>Добавить показание (вызывается каждые 1,5 секунды).</summary>
    public void Add(double cpu, double gpu, double temp, double ramUsedGb)
    {
        var now = DateTime.Now;
        var thisMinute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
        if (thisMinute != minute)
        {
            Flush();
            minute = thisMinute;
        }
        if (!double.IsNaN(cpu)) { cpuSum += cpu; cpuN++; }
        if (!double.IsNaN(gpu)) { gpuSum += gpu; gpuN++; }
        if (!double.IsNaN(temp) && temp > tempMax) tempMax = temp;
        if (ramUsedGb > 0) { ramSum += ramUsedGb; ramN++; }

        if ((now - lastSave).TotalMinutes >= 5) Save();
    }

    private void Flush()
    {
        // Точка пишется, если есть хоть одно показание: раньше без загрузки процессора пропадала вся минута.
        if (minute != DateTime.MinValue && (cpuN > 0 || gpuN > 0 || ramN > 0))
        {
            points.Add(new HistoryPoint
            {
                T = minute,
                Cpu = cpuN > 0 ? (float)(cpuSum / cpuN) : float.NaN,
                Gpu = gpuN > 0 ? (float)(gpuSum / gpuN) : float.NaN,
                Temp = tempMax > 0 ? (float)tempMax : float.NaN,
                RamUsed = ramN > 0 ? (float)(ramSum / ramN) : float.NaN,
            });
            DateTime cutoff = DateTime.Now.AddMinutes(-KeepMinutes);
            points.RemoveAll(p => p.T <= cutoff);
        }
        cpuSum = gpuSum = ramSum = tempMax = 0;
        cpuN = gpuN = ramN = 0;
    }

    /// <summary>
    /// Ряды за последние N минут на равномерной сетке (точка на минуту, для длинных окон — на несколько минут).
    /// Там, где программа не работала, в рядах пропуски — на графике это разрыв линии.
    /// </summary>
    public (double[] Cpu, double[] Gpu, double[] Temp, double[] Ram, DateTime From) Window(int minutes)
    {
        int step = minutes > 720 ? 4 : minutes > 180 ? 2 : 1;      // чтобы на графике было не больше ~360 точек
        int n = minutes / step;
        var cpu = Fill(n); var gpu = Fill(n); var temp = Fill(n); var ram = Fill(n);
        var counts = new int[n];
        DateTime now = DateTime.Now;
        DateTime from = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0).AddMinutes(-minutes + 1);

        foreach (var p in points)
        {
            // Точки раньше начала окна отсекаются до деления: (int)(−3) / 4 = 0, и минуты до окна попадали бы в его первую точку.
            double since = (p.T - from).TotalMinutes;
            if (since < 0) continue;
            int i = (int)since / step;
            if (i >= n) continue;
            if (counts[i] == 0) { cpu[i] = p.Cpu; gpu[i] = p.Gpu; temp[i] = p.Temp; ram[i] = p.RamUsed; }
            else
            {
                cpu[i] = Avg(cpu[i], p.Cpu, counts[i]); gpu[i] = Avg(gpu[i], p.Gpu, counts[i]); ram[i] = Avg(ram[i], p.RamUsed, counts[i]);
                if (!float.IsNaN(p.Temp) && !(temp[i] >= p.Temp)) temp[i] = p.Temp;      // температура - максимум, а не среднее
            }
            counts[i]++;
        }
        return (cpu, gpu, temp, ram, from);
    }

    private static double[] Fill(int n) { var a = new double[n]; Array.Fill(a, double.NaN); return a; }

    private static double Avg(double current, double add, int count) =>
        double.IsNaN(add) ? current : double.IsNaN(current) ? add : (current * count + add) / (count + 1);
}
