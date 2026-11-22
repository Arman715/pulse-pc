using System.Diagnostics;
using System.Globalization;

namespace PulsePC.Services;

/// <summary>
/// Самопроверка «отчёта после игры» на подставных данных (ничего в системе не меняет): время кадров и плавность, вид рывков,
/// виновники и отметки, сравнение и лучшие условия, чтение старых сессий. В конце — сколько стоят опросы на этом ПК (только чтение).
/// Запуск: PulsePC.exe --feature-test SessionCulprits файл.txt
/// </summary>
internal static class SessionReportTest
{
    public static string Run()
    {
        var fails = new List<string>();
        var lines = new List<string>();
        int checks = 0;
        void Check(string what, bool ok, string detail = "") { checks++; if (!ok) fails.Add(what + (detail != "" ? " (" + detail + ")" : "")); }

        var t0 = new DateTime(2026, 11, 20, 21, 0, 0);
        try
        {
            // ---------------------------------------------------------------- кадры: 10 минут при ~165 FPS
            // 30 рывков по 40 мс в первые 5 минут (как сборка шейдеров), 3 — потом, одна пауза 20 с (игра свёрнута).
            var cap = new FpsCapture { Clock = () => t0 };
            cap.Feed("Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped,TimeInSeconds,msInPresentAPI,msBetweenPresents");
            var rnd = new Random(7);
            double clock = 0;
            var early = Enumerable.Range(0, 30).Select(i => 12.0 + i * 9.5).ToList();          // 12 … 287 с
            var late = new List<double> { 380, 450, 560 };
            int ei = 0, li = 0;
            bool paused = false;
            var sw = Stopwatch.StartNew();
            long fed = 0;
            while (clock < 620)
            {
                double ms = 6.06 + (rnd.NextDouble() - 0.5) * 0.6;
                if (ei < early.Count && clock >= early[ei]) { ms = 40; ei++; }
                else if (li < late.Count && clock >= late[li]) { ms = 40; li++; }
                if (!paused && clock >= 320) { ms = 20000; paused = true; }
                clock += ms / 1000;
                cap.Feed(string.Format(CultureInfo.InvariantCulture, "game.exe,1234,0x1,DXGI,0,0,0,{0:0.0000},0.5,{1:0.000}", clock, ms));
                fed++;
            }
            sw.Stop();
            double nsPerFrame = sw.Elapsed.TotalMilliseconds * 1e6 / fed;
            var r = cap.Result();
            Check("кадры записаны", r != null);
            Check("33 рывка", r.Stutters == 33, r.Stutters.ToString());
            Check("секунды без паузы (~600)", r.PerSecond.Count is >= 590 and <= 602, r.PerSecond.Count.ToString());
            Check("обычный кадр ~6 мс", Math.Abs(r.MedianMs - 6.05) < 0.3, r.MedianMs.ToString("0.00"));
            Check("пауза 20 с отмечена", r.Pauses.Count == 1 && Math.Abs((r.Pauses[0].To - r.Pauses[0].From).TotalSeconds - 20) < 0.5);
            Check("рывок привязан ко времени", r.Spikes.Count == 33 && Math.Abs((r.Spikes[0].At - t0).TotalSeconds - 12) < 0.2,
                  r.Spikes.Count > 0 ? (r.Spikes[0].At - t0).TotalSeconds.ToString("0.0") : "-");

            // Замеры датчиков раз в 1,5 с: всё спокойно.
            var quiet = Samples(t0, 640, _ => { });
            var s = new GameSession { Game = "Тест", Start = t0, End = t0.AddSeconds(640), VramTotalGb = 8 };
            SessionAnalysis.ApplyFrames(s, r, quiet);
            Check("ряд времени кадров", s.Frames != null && s.Frames.Median.Length == 600 && s.Frames.Median.Count(double.IsNaN) is >= 30 and <= 45,
                  s.Frames == null ? "null" : s.Frames.Median.Count(double.IsNaN).ToString());
            Check("FPS по часам с пропуском паузы", s.FpsSeries.Length == 240 && s.FpsSeries.Count(double.IsNaN) >= 5 && s.FpsSeries.Where(x => !double.IsNaN(x)).Average() is > 150 and < 175);
            Check("плавность ~80", s.Smoothness is >= 78 and <= 82, s.Smoothness?.ToString());
            Check("рывки ранние → сборка шейдеров", s.StutterKinds?.FirstOrDefault()?.Kind == "shader" && s.StutterKinds[0].Count == 30,
                  string.Join(",", s.StutterKinds?.Select(g => g.Kind + g.Count) ?? Array.Empty<string>()));
            lines.Add($"кадры: {r.Frames} шт, {r.Stutters} рывков, плавность {s.Smoothness}, вид: " +
                      string.Join(", ", s.StutterKinds.Select(g => $"{SessionAnalysis.StutterTitle(g.Kind)} {g.Count}")));

            // Те же рывки, но игра читала диск — подгрузка; ядро занято — процессор; памяти нет — память.
            var disk = Samples(t0, 640, x => x.GameIoMBs = 85);
            Check("рывки при чтении диска игрой → диск", SessionAnalysis.StutterKinds(r, disk, 8).First().Kind == "disk");
            var cpu = Samples(t0, 640, x => x.MaxCore = 99);
            Check("рывки при занятом ядре → процессор", SessionAnalysis.StutterKinds(r, cpu, 8).First().Kind == "cpu");
            var ram = Samples(t0, 640, x => { x.RamAvail = 0.5; x.PagesIn = 900; });
            Check("рывки при нехватке памяти → память", SessionAnalysis.StutterKinds(r, ram, 8).First().Kind == "memory");
            var vram = Samples(t0, 640, x => x.VramGb = 7.9);
            Check("рывки при полной видеопамяти → видеопамять", SessionAnalysis.StutterKinds(r, vram, 8).First().Kind == "vram");

            Check("формула плавности", SessionAnalysis.SmoothnessScore(0, 1.0) == 100 && SessionAnalysis.SmoothnessScore(10, 3) == 0
                                       && SessionAnalysis.SmoothnessScore(1, 2.0) == 74 && SessionAnalysis.SmoothnessScore(20, 1.2) == 40);
            Check("порог рывка", FpsCapture.IsStutter(34, 16.7) && !FpsCapture.IsStutter(8, 3.3) && FpsCapture.IsStutter(12, 3.3) && !FpsCapture.IsStutter(20, 0));

            // ---------------------------------------------------------------- виновники и отметки
            var busy = Samples(t0, 120, x =>
            {
                double sec = (x.At - t0).TotalSeconds;
                if (sec is >= 60 and <= 66) x.Top = new[] { new ProcShot { Name = "OneDrive", Io = 120, Cpu = 3 } };
                if (sec is >= 30 and <= 36) x.Top = new[] { new ProcShot { Name = "MsMpEng", Cpu = 22, Io = 40 } };
                if (sec is >= 90 and <= 96) x.Unfocused = true;
                x.PingMs = sec is >= 75 and <= 76 ? 210 : 22;
            });
            var dip = new FpsDip { At = t0.AddSeconds(61.5), Seconds = 3, FromFps = 160, ToFps = 70, Cause = "disk" };
            var cs = new GameSession { Game = "Тест", Start = t0, End = t0.AddSeconds(120), Dips = new List<FpsDip> { dip } };
            SessionCulprits.Apply(cs, busy, null, new List<DateTime> { t0.AddSeconds(100) });
            string who = SessionCulprits.Describe(dip.Culprits);
            Check("виновник просадки — OneDrive с диском", dip.Culprits?.Count == 1 && who.Contains("OneDrive") && who.Contains("120"), who);
            Check("отметка «свёрнута»", cs.Events.Count(e => e.Kind == "minimized") == 1);
            Check("отметка скачка пинга", cs.Events.Count(e => e.Kind == "ping") == 1 && cs.Events.First(e => e.Kind == "ping").Value == 210);
            Check("отметка сброса драйвера", cs.Events.Count(e => e.Kind == "driver") == 1);
            var defender = cs.Events.FirstOrDefault(e => e.Kind == "culprit" && e.Name == "MsMpEng");
            Check("Защитник — по имени процесса", defender != null && SessionCulprits.Pretty("MsMpEng") == L.T("Защитник Windows") && defender.Cpu == 22);
            Check("фоновые за сессию", cs.Background?.Any(c => c.Name == "OneDrive" && c.PeakDiskMBs == 120) == true);
            lines.Add("просадка: " + who + "; отметки: " + string.Join(", ", cs.Events.Select(e => e.Kind + "@" + (e.At - t0).TotalSeconds.ToString("0"))));

            // Сворачивание не видно, если окно игры почти всегда «чужое» (окно принадлежит лаунчеру).
            var alien = Samples(t0, 60, x => x.Unfocused = true);
            Check("окно другого процесса — без ложных «свёрнута»", SessionCulprits.Minimized(alien, null).Count == 0);

            // ---------------------------------------------------------------- какой адаптер рисовал
            string luidI = GpuVendors.LuidKey(0, 0x1111), luidD = GpuVendors.LuidKey(0, 0x2222);
            var adapters = new List<KmtAdapter>
            {
                new() { LuidLow = 0x1111, Name = "Intel(R) UHD Graphics", VendorId = 0x8086, TypeFlags = 1 | 32 },
                new() { LuidLow = 0x2222, Name = "NVIDIA GeForce RTX 4060 Laptop GPU", VendorId = 0x10DE, TypeFlags = 1 | 16 },
            };
            string busiest = GameGpuProbe.Busiest(new[]
            {
                ($"pid_42_{luidI}_phys_0_eng_0_engtype_3D", 71.0), ($"pid_42_{luidD}_phys_0_eng_0_engtype_3D", 0.4),
            });
            var used = GameGpuProbe.Describe(busiest, adapters);
            Check("игра на встроенной при наличии отдельной", busiest == luidI && used?.Integrated == true && used?.Discrete?.Contains("RTX") == true && used?.Hybrid == true);
            var usedD = GameGpuProbe.Describe(luidD, adapters);
            Check("игра на отдельной — без вывода", usedD?.Integrated == false && usedD?.Discrete == null);

            // ---------------------------------------------------------------- сравнение и лучшие условия
            GameSession S(int day, double? sm, string driver, string plan, double free, string note = null) => new()
            {
                Game = "Игра", Start = t0.AddDays(-day), End = t0.AddDays(-day).AddHours(1), AvgFps = 140, Low1Fps = 90,
                Smoothness = sm, Driver = driver, PowerPlan = plan, StartFreeRamGb = free, UserNote = note, MaxTemp = 70,
            };
            var a1 = S(1, 71, "572.16", "Сбалансированная", 2.1);
            var a2 = S(2, 92, "566.36", "Высокая производительность", 6.0, "закрыл браузер");
            var two = Best(new[] { a1, a2 }, a1);
            Check("меньше трёх — «сессий мало»", !two.Enough && (L.En || two.Headline.StartsWith("Сессий мало")), two.Headline);
            var old = S(5, null, null, null, 0);
            var none = Best(new[] { old }, old);
            Check("старые без оценки — честно", !none.Enough && (L.En || none.Headline.Contains("нет оценки")), none.Headline);
            var a3 = S(3, 80, "566.36", "Сбалансированная", 3.0);
            var three = Best(new[] { a1, a2, a3 }, a1);
            Check("лучшая — самая плавная", three.Enough && ReferenceEquals(three.Best, a2));
            Check("отличия: драйвер, схема, память, заметка", three.Differences.Count == 4
                  && three.Differences.Any(d => d.Contains("566.36")) && three.Differences.Any(d => d.Contains("закрыл браузер")),
                  string.Join(" | ", three.Differences));
            var self = Best(new[] { a1, a2, a3 }, a2);
            Check("выбрана лучшая — сравнение с самой неровной", (L.En || self.Headline.StartsWith("Эта сессия — самая плавная")) && self.Differences.Count >= 2);
            var cmp = SessionCompare.Lines(a1, a2);
            var smLine = cmp.First(c => c.Label == L.T("плавность из 100"));
            Check("строки сравнения", smLine.This == "71" && smLine.Other == "92" && smLine.Delta == "−21" && smLine.Better == -1
                  && cmp.Any(c => c.Label == L.T("драйвер видеокарты") && c.Delta == L.T("сменился")));
            lines.Add("лучшие условия: " + three.Headline + " " + string.Join(" ", three.Differences));

            // ---------------------------------------------------------------- старые сессии и запись в файл
            const string oldJson = "[{\"AvgFps\":118,\"Low1Fps\":81,\"FpsSeries\":[117.0,119.8],\"Game\":\"Cyberpunk 2077\",\"Start\":\"2026-11-19T10:32:00\"," +
                                   "\"End\":\"2026-11-19T12:06:00\",\"AvgCpu\":46,\"Notes\":[\"x\"],\"Dips\":[{\"At\":\"2026-11-19T11:00:00\",\"Seconds\":3,\"FromFps\":120,\"ToFps\":60,\"Cause\":\"disk\",\"Detail\":\"d\"}]}]";
            var parsed = SessionStore.Parse(oldJson);
            var p0 = parsed[0];
            Check("старая сессия читается", parsed.Count == 1 && p0.Frames == null && p0.Smoothness == null && p0.Events == null && p0.Dips[0].Culprits == null
                  && p0.ListCaption == p0.Caption && p0.NoteLine == "");
            Check("старая сессия в сравнении и лучших условиях", SessionCompare.Lines(p0, p0).Count > 5 && !Best(parsed, p0).Enough);
            s.UserNote = "обновил драйвер";
            var back = SessionStore.Parse(SessionStore.Serialize(new List<GameSession> { s, p0 }));
            Check("новая сессия с пропусками (NaN) сохраняется и читается", back[0].Frames.Median.Count(double.IsNaN) == s.Frames.Median.Count(double.IsNaN)
                  && back[0].UserNote == "обновил драйвер" && back[0].StutterKinds.Count == s.StutterKinds.Count && back[1].Frames == null);

            // ---------------------------------------------------------------- стоимость на этом ПК (только чтение)
            using (var pa = new ProcessActivity())
            {
                pa.Sample(-1, null, out _);
                ProcShot[] top = Array.Empty<ProcShot>();
                for (int i = 0; i < 6; i++) { Thread.Sleep(300); top = pa.Sample(-1, null, out _); }
                double per = (pa.CostMs - pa.FirstMs) / Math.Max(1, pa.Calls - 1);
                lines.Add($"опрос процессов: первый {pa.FirstMs:0.0} мс (имена всех процессов), дальше {per:0.00} мс на замер ({pa.Calls} замеров) → {per / 1500 * 100:0.000} % одного ядра при замере раз в 1,5 с; " +
                          $"сейчас заметны: {(top.Length == 0 ? "никто" : string.Join(", ", top.Select(x => $"{SessionCulprits.Pretty(x.Name)} {x.Cpu:0.#}%/{x.Io:0.#}МБ/с")))}");
                Check("опрос процессов дешёвый (< 15 мс)", per < 15, per.ToString("0.00"));
            }
            lines.Add($"разбор кадров: {nsPerFrame:0} нс на кадр → при 165 FPS {nsPerFrame * 165 / 1e7:0.0000} % одного ядра, при 500 FPS {nsPerFrame * 500 / 1e7:0.0000} %");
            Check("разбор кадра дешёвый (< 20 мкс)", nsPerFrame < 20000, nsPerFrame.ToString("0"));
            try
            {
                var gsw = Stopwatch.StartNew();
                new PerformanceCounterCategory("GPU Engine").ReadCategory();
                gsw.Stop();
                lines.Add($"выборка «GPU Engine» (адаптер игры): {gsw.Elapsed.TotalMilliseconds:0} мс, две выборки через 45 с, 3 мин и дальше раз в 10 мин");
            }
            catch (Exception ex) { lines.Add("счётчики «GPU Engine» недоступны: " + ex.Message); }
            var dsw = Stopwatch.StartNew();
            var resets = SessionCulprits.DriverResets(DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);
            lines.Add($"сбросы драйвера видеокарты (Display 4101) за 7 дней: {resets.Count}, чтение журнала {dsw.Elapsed.TotalMilliseconds:0} мс");
            lines.Add($"окно на переднем плане: процесс {SessionCulprits.ForegroundPid()}");
            var kmt = Kmt.Enumerate();
            lines.Add("видеоадаптеры: " + string.Join("; ", kmt.Select(x => $"{x.Name} ({(x.Integrated ? "встроенная" : "отдельная")}{(x.HybridDiscrete || x.HybridIntegrated ? ", гибрид" : "")})")));
        }
        catch (Exception ex) { fails.Add("исключение: " + ex); }

        string head = fails.Count == 0 ? $"OK ({checks} проверок)" : "FAIL: " + string.Join("; ", fails);
        return head + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    private static BestConditions Best(IEnumerable<GameSession> list, GameSession cur) => SessionCompare.Best(list, cur);

    private static List<SessionSample> Samples(DateTime t0, double seconds, Action<Box> tweak)
    {
        var list = new List<SessionSample>();
        for (double t = 0; t <= seconds; t += 1.5)
        {
            var b = new Box { X = new SessionSample { At = t0.AddSeconds(t), Fps = 160, Cpu = 40, MaxCore = 70, Gpu = 90, RamAvail = 4, PagesIn = 0, DiskBusy = 5, GameIoMBs = 0.5 } };
            tweak(b);
            list.Add(b.X);
        }
        return list;
    }

    /// <summary>Обёртка, чтобы менять замер в лямбде (структура).</summary>
    private sealed class Box
    {
        public SessionSample X;
        public double GameIoMBs { set => X.GameIoMBs = value; }
        public double MaxCore { set => X.MaxCore = value; }
        public double RamAvail { set => X.RamAvail = value; }
        public double PagesIn { set => X.PagesIn = value; }
        public double VramGb { set => X.VramGb = value; }
        public double PingMs { set => X.PingMs = value; }
        public bool Unfocused { set => X.Unfocused = value; }
        public ProcShot[] Top { set => X.Top = value; }
        public DateTime At => X.At;
    }
}
