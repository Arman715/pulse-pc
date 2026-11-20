using System.Text;

namespace PulsePC.Services;

/// <summary>
/// Самопроверка кода удаления: всё происходит в отдельной папке-песочнице внутри временной папки.
/// Запуск: PulsePC.exe --selftest [файл отчёта]. Код возврата 0 - все проверки пройдены.
/// </summary>
public static class SelfTest
{
    public static int Run(string reportPath)
    {
        var log = new StringBuilder();
        int failed = 0;
        void Check(string name, bool ok) { log.AppendLine((ok ? "OK    " : "FAIL  ") + name); if (!ok) failed++; }

        string box = Path.Combine(Path.GetTempPath(), "PulsePC-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // --- Подготовка песочницы ---
            string victim = Path.Combine(box, "victim");       // это будем удалять
            string keep = Path.Combine(box, "keep");           // это должно уцелеть (цель ссылки)
            Directory.CreateDirectory(Path.Combine(victim, "sub", "deep"));
            Directory.CreateDirectory(keep);
            File.WriteAllText(Path.Combine(victim, "a.txt"), new string('x', 1000));
            File.WriteAllText(Path.Combine(victim, "sub", "b.txt"), new string('y', 2000));
            File.WriteAllText(Path.Combine(victim, "sub", "deep", "c.txt"), new string('z', 3000));
            string readOnly = Path.Combine(victim, "readonly.txt");
            File.WriteAllText(readOnly, "ro");
            File.SetAttributes(readOnly, FileAttributes.ReadOnly);
            string precious = Path.Combine(keep, "precious.txt");
            File.WriteAllText(precious, "must survive");

            // Ссылка-переход (junction) из удаляемой папки на сохраняемую.
            string link = Path.Combine(victim, "link");
            Shell.Run("cmd.exe", $"/c mklink /J \"{link}\" \"{keep}\"", 15000);
            bool linkMade = Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
            Check("ссылка-переход создана", linkMade);

            // --- Подсчёт размера не заходит в ссылку ---
            var (size, _) = SafeFs.Size(victim);
            Check($"размер считается без содержимого ссылки (получено {size}, ожидалось 6002)", size == 6002);

            // --- Удаление ---
            long freed = SafeFs.Delete(victim);
            Check($"освобождено {freed} байт (ожидалось 6002)", freed == 6002);
            Check("удаляемая папка исчезла", !Directory.Exists(victim));
            Check("файл за ссылкой уцелел", File.Exists(precious) && File.ReadAllText(precious) == "must survive");

            // --- Правило: возраст и исключения ---
            string rules = Path.Combine(box, "rules");
            Directory.CreateDirectory(Path.Combine(rules, "claude"));
            Directory.CreateDirectory(Path.Combine(rules, "olddir"));
            File.WriteAllText(Path.Combine(rules, "claude", "session.txt"), "keep me");
            File.WriteAllText(Path.Combine(rules, "olddir", "old-inside.txt"), "12345");
            File.WriteAllText(Path.Combine(rules, "old.tmp"), "1234567890");
            File.WriteAllText(Path.Combine(rules, "fresh.tmp"), "fresh");
            DateTime past = DateTime.Now.AddDays(-5);
            File.SetLastWriteTime(Path.Combine(rules, "old.tmp"), past);
            File.SetLastWriteTime(Path.Combine(rules, "olddir", "old-inside.txt"), past);
            File.SetLastWriteTime(Path.Combine(rules, "claude", "session.txt"), past);

            var rule = new CleanRule(rules, 1, new[] { "claude" });
            long scan = Cleaner.RunRule(rule, false);
            Check($"подсчёт по правилу: {scan} байт (ожидалось 15)", scan == 15);
            Check("подсчёт ничего не удалил", File.Exists(Path.Combine(rules, "old.tmp")));
            long cleaned = Cleaner.RunRule(rule, true);
            Check($"очистка по правилу: {cleaned} байт (ожидалось 15)", cleaned == 15);
            Check("старый файл удалён", !File.Exists(Path.Combine(rules, "old.tmp")));
            Check("старая папка удалена", !Directory.Exists(Path.Combine(rules, "olddir")));
            Check("свежий файл остался", File.Exists(Path.Combine(rules, "fresh.tmp")));
            Check("исключённая папка осталась", File.Exists(Path.Combine(rules, "claude", "session.txt")));

            // --- Защита путей ---
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (var bad in new[] { @"C:\", win, Path.Combine(win, "System32"), user, Path.Combine(user, "Documents"),
                         Path.Combine(user, "Desktop"), Path.Combine(user, "AppData"), @"C:\Program Files", @"C:\Users", @"D:\Anything\Deep\Folder", "" })
                Check($"защита отклоняет: '{bad}'", !SafeFs.IsSafe(bad));
            foreach (var good in new[] { box, Path.Combine(win, "Temp"), Path.GetTempPath() })
                Check($"защита пропускает: '{good}'", SafeFs.IsSafe(good));

            // --- Разбор вывода PresentMon: 1000 кадров по 10 мс и 20 кадров по 50 мс ---
            var fps = new FpsCapture();
            fps.Feed("warning: some line before the header");
            fps.Feed("Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped,TimeInSeconds,msInPresentAPI,msBetweenPresents");
            double clock = 0;
            for (int i = 0; i < 1020; i++)
            {
                double ms = i % 51 == 50 ? 50 : 10;
                clock += ms / 1000;
                fps.Feed(FormattableString.Invariant($"game.exe,1234,0x1,DXGI,0,0,0,{clock:0.0000},0.5,{ms:0.000}"));
            }
            var r = fps.Result();
            Check($"кадры посчитаны (получено {r?.Frames}, ожидалось 1020)", r != null && r.Frames == 1020);
            Check($"средний FPS около 92,7 (получено {r?.Avg:0.0})", r != null && Math.Abs(r.Avg - 1000.0 * 1020 / (1000 * 10 + 20 * 50)) < 0.5);
            Check($"1 % low около 20 FPS (получено {r?.Low1:0.0})", r != null && Math.Abs(r.Low1 - 20) < 1);
            Check($"ряд по 5 секунд построен (точек {r?.Series.Length})", r != null && r.Series.Length >= 1 && r.Series.All(v => v > 80 && v < 100));

            // Правило с небезопасным путём не должно ничего считать и удалять.
            Check("правило с запрещённым путём ничего не делает", Cleaner.RunRule(new CleanRule(user), true) == 0 && Directory.Exists(user));

            // --- Вентиляторы: кривая, защита от перегрева, сводка (сами вентиляторы только читаются) ---
            string fans = FanControl.SelfTest();
            log.AppendLine().AppendLine("Вентиляторы видеокарты: " + fans.Replace("\n", "\n  "));
            if (!fans.StartsWith("OK")) failed++;

            // --- Откат настроек Windows и сетевой карты: разбор копий, кавычки PowerShell (чистые функции, система не трогается) ---
            foreach (var (name, run) in new (string, Func<string>)[] { ("Настройки Windows", Tweaks.SelfTest), ("Сетевая карта", NetPower.SelfTest) })
            {
                string res = run();
                log.AppendLine().AppendLine(name + ": " + res.Replace("\n", "\n  "));
                if (!res.StartsWith("OK")) failed++;
            }
        }
        catch (Exception ex)
        {
            log.AppendLine("EXCEPTION  " + ex);
            failed++;
        }
        finally
        {
            try { if (Directory.Exists(box)) SafeFs.Delete(box); } catch { }
        }

        // --- Сеть игры: отдельный отчёт, на общий итог не влияет ---
        try { log.AppendLine().Append(GameNetMonitor.SelfTest()); } catch (Exception ex) { log.AppendLine("Сеть игры: EXCEPTION " + ex.Message); }
        // Сведения (не влияют на итог): что отдают журнал загрузки, датчики и диски на этом ПК.
        try { log.AppendLine().AppendLine().Append(BootPerf.SelfTest()); } catch (Exception ex) { log.AppendLine("Загрузка: EXCEPTION " + ex.Message); }
        try { log.AppendLine().AppendLine().Append(SensorsInfo()); } catch (Exception ex) { log.AppendLine("Датчики: EXCEPTION " + ex.Message); }
        try { log.AppendLine().AppendLine().Append(TuningInfo()); } catch (Exception ex) { log.AppendLine("Настройки игр: EXCEPTION " + ex.Message); }
        // Запись настроек игры и откат — на искусственном файле, влияет на итог.
        try
        {
            string tune = TuningCheck(out bool ok);
            log.AppendLine().AppendLine().Append(tune);
            if (!ok) failed++;
        }
        catch (Exception ex) { log.AppendLine("Настройки игр (запись): EXCEPTION " + ex.Message); failed++; }
        // Разбор просадок на искусственной сессии — это проверка, она влияет на итог.
        try
        {
            string dips = DipsCheck(out bool ok);
            log.AppendLine().AppendLine().Append(dips);
            if (!ok) failed++;
        }
        catch (Exception ex) { log.AppendLine("Просадки: EXCEPTION " + ex.Message); failed++; }
        // Чужое железо на выдуманных данных: видеокарты AMD/Intel/NVIDIA, драйверы, процессоры, память, батарея — влияет на итог.
        try
        {
            string hw = GpuVendorChecks(out int bad);
            log.AppendLine().AppendLine().Append(hw);
            failed += bad;
        }
        catch (Exception ex) { log.AppendLine("Чужое железо: EXCEPTION " + ex); failed++; }

        log.Insert(0, (failed == 0 ? "ВСЕ ПРОВЕРКИ ПРОЙДЕНЫ" : $"НЕ ПРОЙДЕНО: {failed}") + Environment.NewLine);
        try { File.WriteAllText(reportPath, log.ToString(), Encoding.UTF8); } catch { }
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Что отдают датчики на этом ПК: причины снижения частоты видеокарты, ядра, подкачка, диски.</summary>
    private static string SensorsInfo()
    {
        var lines = new List<string> { "Датчики:" };
        using var m = new Metrics();
        m.Start();
        // Счётчики Windows создаются несколько секунд.
        for (int i = 0; i < 30 && (double.IsNaN(m.ReadCpu().Load) || double.IsNaN(m.ReadExtra().MaxCore)); i++) Thread.Sleep(500);
        Thread.Sleep(1000);
        var (load, ghz) = m.ReadCpu();
        var x = m.ReadExtra();
        var g = m.Gpu;
        lines.Add($"  процессор {load:0} %, {ghz:0.00} ГГц (база {m.CpuBaseMhz} МГц), самый загруженный поток {x.MaxCore:0} %");
        lines.Add($"  подкачка {x.PagesIn:0} стр/с, диски заняты {x.DiskBusy:0} %");
        lines.Add(g.At == DateTime.MinValue
            ? "  видеокарта: nvidia-smi не ответил"
            : $"  видеокарта {g.Name}: {g.Load:0} %, {g.Temp:0} °C, {g.Clock:0} МГц, лимит питания {g.PowerLimit:0} Вт; упор в питание {g.LimitPower}, нагрев {g.LimitThermal}, аварийно {g.LimitHardware}");
        foreach (var d in Metrics.QueryDisks())
            lines.Add($"  диск {d.Name}: {d.Health}, ошибок чтения {d.ReadErrors?.ToString() ?? "нет данных"}, наработка {d.Hours?.ToString() ?? "?"} ч");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Что программа видит в настройках установленных игр (только чтение).</summary>
    private static string TuningInfo()
    {
        var lines = new List<string> { $"Настройки игр (монитор {GameTuning.Refresh()} Гц, лимит {GameTuning.FrameCap()}):" };
        var tunes = GameTuning.Scan(GameLibrary.Load(), GameTuning.Profiles(SessionStore.Load()));
        foreach (var t in tunes)
        {
            lines.Add($"  {t.Game} — {t.Engine}; {(t.ConfigPath ?? t.Note)}");
            foreach (var c in t.Changes) lines.Add($"      [{c.Section}] {c.Key}: {c.From} → {c.To}  ({c.Title})");
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Запись и откат настроек на искусственных файлах: Unreal (UTF-16, только чтение) и Source 2.</summary>
    private static string TuningCheck(out bool ok)
    {
        var lines = new List<string>();
        string dir = Path.Combine(Path.GetTempPath(), "PulsePC-tune-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        bool allGood = true;
        try
        {
            void Check(string what, bool good) { lines.Add((good ? "OK    " : "FAIL  ") + what); if (!good) allGood = false; }

            // Unreal: упор в процессор → дальность и тени снижаются, синхронизация и лимит — под G-SYNC.
            string ini = Path.Combine(dir, "GameUserSettings.ini");
            string original = "[ScalabilityGroups]\r\nsg.ViewDistanceQuality=4\r\nsg.ShadowQuality=3\r\nsg.TextureQuality=4\r\n[/Script/Engine.GameUserSettings]\r\nbUseVSync=True\r\nFrameRateLimit=0.000000\r\n";
            File.WriteAllText(ini, original, Encoding.Unicode);
            File.SetAttributes(ini, FileAttributes.ReadOnly);
            byte[] before = File.ReadAllBytes(ini);
            var t = GameTuning.ForFile("selftest-tune", "ini", ini, new GameProfile { Sessions = 3, CpuBound = 70, GpuBound = 10 });
            var keys = t.Changes.Select(c => c.Key).OrderBy(k => k).ToList();
            // На NVIDIA лимит FPS ставится в профиле драйвера, а не в файле игры (одно место для одной настройки).
            var expected = GameTuning.CapInDriver
                ? new[] { "bUseVSync", "sg.ShadowQuality", "sg.ViewDistanceQuality" }
                : new[] { "bUseVSync", "FrameRateLimit", "sg.ShadowQuality", "sg.ViewDistanceQuality" };
            Check($"рекомендации Unreal: {string.Join(", ", keys)}", keys.SequenceEqual(expected));
            GameTuning.Apply(t, t.Changes);
            byte[] afterBytes = File.ReadAllBytes(ini);
            string after = Encoding.Unicode.GetString(afterBytes, 2, afterBytes.Length - 2);
            bool capOk = GameTuning.CapInDriver ? after.Contains("FrameRateLimit=0.000000") : after.Contains($"FrameRateLimit={GameTuning.FrameCap()}.000000");
            Check("значения записаны", after.Contains("bUseVSync=False") && capOk
                                       && after.Contains("sg.ViewDistanceQuality=2") && after.Contains("sg.TextureQuality=4"));
            Check("кодировка UTF-16 и «только чтение» сохранены", afterBytes[0] == 0xFF && afterBytes[1] == 0xFE && File.GetAttributes(ini).HasFlag(FileAttributes.ReadOnly));
            var again = GameTuning.ForFile("selftest-tune", "ini", ini, new GameProfile { Sessions = 3, CpuBound = 70, GpuBound = 10 });
            Check($"после применения рекомендаций не осталось (осталось {again.Changes.Count}) и есть копия", again.Changes.Count == 0 && again.HasBackup);
            GameTuning.Restore(again);
            Check("откат вернул файл байт в байт", File.ReadAllBytes(ini).SequenceEqual(before));

            // Source 2: размытие и синхронизация.
            string kv = Path.Combine(dir, "video.txt");
            File.WriteAllText(kv, "\"video.cfg\"\n{\n\t\"setting.mat_vsync\"\t\t\"1\"\n\t\"setting.r_citadel_motion_blur\"\t\t\"1\"\n\t\"setting.r_low_latency\"\t\t\"1\"\n}\n");
            var s = GameTuning.ForFile("selftest-tune", "kv", kv, null);
            GameTuning.Apply(s, s.Changes);
            string text = File.ReadAllText(kv);
            Check($"Source 2: изменено {s.Changes.Count} (ожидалось 2), Reflex не тронут",
                s.Changes.Count == 2 && text.Contains("\"setting.mat_vsync\"\t\t\"0\"") && text.Contains("\"setting.r_citadel_motion_blur\"\t\t\"0\"") && text.Contains("\"setting.r_low_latency\"\t\t\"1\""));
        }
        finally
        {
            try { foreach (var f in Directory.GetFiles(dir)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(dir, true); } catch { }
            try { Directory.Delete(Path.Combine(SecureStore.PathOf("game-backups"), "selftest-tune"), true); } catch { }
        }
        ok = allGood;
        return "Настройки игр (запись и откат):" + Environment.NewLine + string.Join(Environment.NewLine, lines.Select(l => "  " + l));
    }

    /// <summary>Разбор просадок на искусственной сессии с заранее известными причинами.</summary>
    private static string DipsCheck(out bool ok)
    {
        var start = new DateTime(2026, 1, 1, 20, 0, 0);
        var samples = new List<SessionSample>();
        for (int i = 0; i < 200; i++)
        {
            var s = new SessionSample
            {
                At = start.AddSeconds(i * 1.5), Fps = 150, Cpu = 40, MaxCore = 70, Ghz = 4.2, Gpu = 98, GpuTemp = 65, GpuClock = 2600,
                RamAvail = 3, PagesIn = 0, DiskBusy = 5, LimitPower = true,
            };
            if (i >= 60 && i <= 62) { s.Fps = 50; s.RamAvail = 0.5; s.PagesIn = 900; }      // память
            if (i >= 120 && i <= 121) { s.Fps = 60; s.MaxCore = 99; s.Gpu = 70; }           // процессор
            if (i == 169) s.Gpu = 80;                                                          // перед ней видеокарта не на пределе
            if (i == 170) { s.Fps = 70; s.Gpu = 80; }                                          // без видимой причины
            samples.Add(s);
        }
        var session = new GameSession { Game = "проверка", Start = start, End = start.AddSeconds(300) };
        SessionAnalysis.Apply(session, samples, 2.5, new Dictionary<int, string> { [61] = "Браузер 2,0 ГБ" }, new List<DateTime>());
        var causes = session.Dips?.Select(d => d.Cause).ToList() ?? new List<string>();
        ok = causes.SequenceEqual(new[] { "ram", "cpu", "unknown" })
             && session.Dips[0].Detail.Contains("Браузер")
             && session.BoundGpuPct > 90
             && session.GpuPowerLimitPct > 99 && session.CpuSlowPct == 0;
        return $"{(ok ? "OK  " : "FAIL")}  Просадки: найдено {causes.Count} ({string.Join(", ", causes)}), ожидалось 3 (ram, cpu, unknown); "
               + $"упор в видеокарту {session.BoundGpuPct:0} %, лимит питания {session.GpuPowerLimitPct:0} %, процессор ниже базы {session.CpuSlowPct:0} %"
               + Environment.NewLine + "      первая: " + session.Dips?.FirstOrDefault()?.Detail;
    }

    /// <summary>
    /// Решения, зависящие от производителя железа, на выдуманных данных: списки видеокарт (AMD + встроенная, две NVIDIA, Intel Arc, ноутбуки),
    /// строки версий драйверов AMD/Intel/NVIDIA, названия процессоров Intel/AMD/Snapdragon, память, батарея, лимит кадров под разные мониторы.
    /// Ничего не читает из системы. Возвращает отчёт; bad — число непройденных проверок.
    /// </summary>
    public static string GpuVendorChecks(out int bad)
    {
        var lines = new List<string>();
        int fails = 0;
        void Check(string what, bool good) { lines.Add((good ? "OK    " : "FAIL  ") + what); if (!good) fails++; }
        GpuAdapter A(string name, double vram, string vendor = null)
        {
            string v = vendor ?? GpuVendors.FromName(name);
            return new GpuAdapter { Name = name, Vendor = v, VramGb = vram, Integrated = GpuVendors.IsIntegrated(v, name) };
        }
        string Main(params GpuAdapter[] list) => GpuVendors.PickMain(list)?.Name ?? "";

        // --- какая видеокарта главная
        Check("Ryzen со встроенной + Radeon RX 7800 XT → RX 7800 XT",
            Main(A("AMD Radeon(TM) Graphics", 0.5), A("AMD Radeon RX 7800 XT", 16)) == "AMD Radeon RX 7800 XT");
        Check("ноутбук: Intel UHD + RTX 4060 Laptop → RTX 4060",
            Main(A("Intel(R) UHD Graphics", 0.1), A("NVIDIA GeForce RTX 4060 Laptop GPU", 8)) == "NVIDIA GeForce RTX 4060 Laptop GPU");
        Check("ноутбук: Radeon 780M (2 ГБ) + RTX 4070 Laptop → RTX 4070",
            Main(A("AMD Radeon 780M Graphics", 2), A("NVIDIA GeForce RTX 4070 Laptop GPU", 8)) == "NVIDIA GeForce RTX 4070 Laptop GPU");
        Check("две NVIDIA: GTX 1650 (4) + RTX 3060 (12) → RTX 3060",
            Main(A("NVIDIA GeForce GTX 1650", 4), A("NVIDIA GeForce RTX 3060", 12)) == "NVIDIA GeForce RTX 3060");
        Check("Intel UHD 770 + Arc A770 → Arc A770",
            Main(A("Intel(R) UHD Graphics 770", 0.1), A("Intel(R) Arc(TM) A770 Graphics", 16)) == "Intel(R) Arc(TM) A770 Graphics");
        Check("только встроенная + Parsec Virtual Display → встроенная",
            Main(A("Parsec Virtual Display Adapter", 0, ""), A("Intel(R) Iris(R) Xe Graphics", 0.1)) == "Intel(R) Iris(R) Xe Graphics");
        Check("пустой список → нет главной", GpuVendors.PickMain(new List<GpuAdapter>()) == null);

        // --- встроенная или отдельная
        (string Name, string Vendor, bool Integrated)[] kinds =
        {
            ("AMD Radeon(TM) Graphics", "AMD", true), ("AMD Radeon 780M Graphics", "AMD", true), ("AMD Radeon 890M", "AMD", true),
            ("AMD Radeon(TM) Vega 8 Graphics", "AMD", true), ("AMD Radeon(TM) RX Vega 11 Graphics", "AMD", true), ("AMD Radeon(TM) 8060S Graphics", "AMD", true),
            ("AMD Radeon RX 7600M XT", "AMD", false), ("Radeon (TM) RX 480 Graphics", "AMD", false), ("AMD Radeon RX 6700 XT", "AMD", false), ("AMD Radeon PRO W7800", "AMD", false),
            ("Intel(R) UHD Graphics 770", "Intel", true), ("Intel(R) Arc(TM) Graphics", "Intel", true), ("Intel(R) Arc(TM) 140V GPU (16GB)", "Intel", true),
            ("Intel(R) Arc(TM) A770 Graphics", "Intel", false), ("Intel(R) Arc(TM) A370M Graphics", "Intel", false), ("Intel(R) Arc(TM) B580 Graphics", "Intel", false),
            ("Intel(R) Iris(R) Xe MAX Graphics", "Intel", false), ("NVIDIA GeForce RTX 4060 Ti", "NVIDIA", false), ("Qualcomm(R) Adreno(TM) X1-85 GPU", "Qualcomm", true),
        };
        foreach (var (name, vendor, integrated) in kinds)
            Check($"{name}: {GpuVendors.FromName(name)}, {(integrated ? "встроенная" : "отдельная")}",
                GpuVendors.FromName(name) == vendor && GpuVendors.IsIntegrated(vendor, name) == integrated);
        Check("производитель по коду устройства: VEN_1002 → AMD, VEN_8086 → Intel, VEN_10DE → NVIDIA",
            GpuVendors.FromPnp(@"PCI\VEN_1002&DEV_747E&SUBSYS_0E0E1DA2") == "AMD" && GpuVendors.FromPnp(@"PCI\VEN_8086&DEV_A780") == "Intel"
            && GpuVendors.FromPnp(@"PCI\VEN_10DE&DEV_2803") == "NVIDIA" && GpuVendors.FromPnp(@"SWD\REMOTEDISPLAYENUM") == "");

        // --- счётчики Windows: загрузка нужной видеокарты, а не встроенной
        string igpu = "luid_0x00000000_0x0000a001", dgpu = "luid_0x00000000_0x0000b002";
        var engines = new List<(string, double)>
        {
            ("pid_100_luid_0x00000000_0x0000A001_phys_0_eng_0_engtype_3D", 10), ("pid_200_luid_0x00000000_0x0000A001_phys_0_eng_0_engtype_3D", 5),
            ("pid_300_luid_0x00000000_0x0000B002_phys_0_eng_0_engtype_3D", 40), ("pid_400_luid_0x00000000_0x0000B002_phys_0_eng_0_engtype_3D", 30),
            ("pid_300_luid_0x00000000_0x0000B002_phys_0_eng_3_engtype_Copy", 90), ("pid_300_luid_0x00000000_0x0000B002_phys_0_eng_5_engtype_VideoDecode", 95),
        };
        var e = GpuVendors.ParseEngine("pid_19688_luid_0x00000000_0x00010316_phys_0_eng_0_engtype_3D");
        Check($"разбор экземпляра счётчика → {e.Luid}, двигатель {e.Engine}, {e.Type}", e.Luid == "luid_0x00000000_0x00010316" && e.Engine == 0 && e.Type == "3D");
        Check($"загрузка отдельной видеокарты = 70 (получено {GpuVendors.Load(engines, dgpu):0}), встроенной = 15 ({GpuVendors.Load(engines, igpu):0})",
            GpuVendors.Load(engines, dgpu) == 70 && GpuVendors.Load(engines, igpu) == 15);
        Check("без адаптера — самая загруженная; без счётчиков — NaN",
            GpuVendors.Load(engines, "") == 70 && double.IsNaN(GpuVendors.Load(new List<(string, double)>(), dgpu)));
        Check("LUID в имени счётчика", GpuVendors.LuidKey(0, 0x10316) == "luid_0x00000000_0x00010316");
        Check("загрузку дают 3D и вычисления, а не копирование и видео",
            GpuVendors.IsLoadEngine("3D") && GpuVendors.IsLoadEngine("Compute_0") && GpuVendors.IsLoadEngine("High Priority 3D")
            && !GpuVendors.IsLoadEngine("Copy") && !GpuVendors.IsLoadEngine("VideoDecode") && !GpuVendors.IsLoadEngine("Video Codec_0"));
        Check("две NVIDIA: игра на второй (5 % и 80 %) → вторая; 70 и 75 % — прежняя остаётся",
            GpuVendors.PickBusiest(new[] { 5.0, 80 }, 0) == 1 && GpuVendors.PickBusiest(new[] { 70.0, 75 }, 0) == 0 && GpuVendors.PickBusiest(new[] { double.NaN, 20 }, -1) == 1);
        var smi = Metrics.ParseSmiLine("NVIDIA GeForce RTX 3060, 566.36, 45, 97, 6000, 12288, 150.5, 60, 1800, 170, Not Active, Not Active, Active, Not Active, Not Active, 00000000:01:00.0");
        Check("строка nvidia-smi: температура, загрузка, нагрев, адрес на шине",
            smi != null && smi.Temp == 45 && smi.Load == 97 && smi.LimitThermal && !smi.LimitPower && smi.Source == "00000000:01:00.0"
            && Metrics.ParseSmiLine("NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver.") == null);

        // --- датчики Windows (D3DKMT): разбор байтов
        var perf = new byte[64];
        BitConverter.GetBytes(7_000_000_000UL / 7).CopyTo(perf, 8);   // 1000 МГц память
        BitConverter.GetBytes(0u).CopyTo(perf, 48);                    // вентилятор стоит
        BitConverter.GetBytes(455u).CopyTo(perf, 52);                  // 45,5 % от предела
        BitConverter.GetBytes(652u).CopyTo(perf, 56);                  // 65,2 °C
        var kp = new KmtPerf();
        Kmt.Decode(perf, ref kp);
        Check($"датчики Windows: {kp.TempC} °C, {kp.PowerPct} %, вентилятор {(double.IsNaN(kp.FanRpm) ? "нет оборотов" : kp.FanRpm.ToString())}",
            kp.TempC == 65.2 && kp.PowerPct == 45.5 && double.IsNaN(kp.FanRpm) && Math.Abs(kp.MemClockMhz - 1000) < 0.01);
        Check("нет датчика температуры (0) и мусор → NaN", double.IsNaN(Kmt.TempFromDeci(0)) && double.IsNaN(Kmt.TempFromDeci(65535)) && Kmt.TempFromDeci(400) == 40);
        var na = new GpuSample();
        Check("без датчиков: потребление и вентилятор — прочерк", na.PowerText == "—" && na.FanText == "—"
            && new GpuSample { PowerPct = 12 }.PowerText == "12 % от макс." && new GpuSample { Power = 120 }.PowerText == "120 Вт");

        // --- версии драйверов
        (string Vendor, string Raw, string Radeon, string Want)[] versions =
        {
            ("NVIDIA", "32.0.15.6636", null, "566.36"), ("NVIDIA", "31.0.15.5222", null, "552.22"), ("NVIDIA", "30.0.14.7005", null, "470.05"),
            ("NVIDIA", "32.0.16.3", null, "600.03"),
            ("AMD", "31.0.24033.1003", "24.3.1", "Adrenalin 24.3.1"), ("AMD", "32.0.12033.1030", " 25.3.1 ", "Adrenalin 25.3.1"),
            ("AMD", "31.0.21921.1000", null, "31.0.21921.1000"), ("AMD", "15.201.1151.1008", "15.7.1 Beta", "Adrenalin 15.7.1"),
            ("Intel", "32.0.101.6913", null, "32.0.101.6913"), ("Intel", "31.0.101.5186", null, "31.0.101.5186"),
        };
        foreach (var (vendor, raw, radeon, want) in versions)
        {
            string got = GpuDriver.FriendlyVersion(vendor, raw, radeon);
            Check($"версия {vendor} {raw}{(radeon == null ? "" : " / " + radeon.Trim())} → {got}", got == want);
        }
        Check("страницы загрузки есть у NVIDIA, AMD, Intel", GpuDriver.DownloadUrl("NVIDIA") != "" && GpuDriver.DownloadUrl("AMD") != "" && GpuDriver.DownloadUrl("Intel") != "");

        // --- раздел «Настройки … по играм» на чужих видеокартах
        Check("название раздела: NVIDIA — «Настройки NVIDIA по играм», AMD и Intel — «Настройки видеокарты по играм»",
            GpuVendors.SectionTitle("NVIDIA") == "Настройки NVIDIA по играм" && GpuVendors.SectionTitle("AMD") == "Настройки видеокарты по играм" && GpuVendors.SectionTitle("") == "Настройки видеокарты по играм");
        var amd = new NvState();
        NvidiaProfiles.Advise(amd, "AMD", "AMD Radeon RX 7800 XT", false, false, "", 157, 165, @"C:\fake\RadeonSoftware.exe", false);
        Check("AMD: подсказка про Anti-Lag и лимит 157, кнопка «Открыть AMD Software», без совета про батарею",
            amd.Reason.Contains("AMD Software") && amd.Advice[0].HasLaunch && amd.Advice[0].Text.Contains("Anti-Lag") && amd.Advice[0].Text.Contains("157")
            && !amd.Advice.Any(a => a.Title == "Электропитание"));
        var intel = new NvState();
        NvidiaProfiles.Advise(intel, "Intel", "Intel(R) UHD Graphics", true, false, "", 59, 60, "", true);
        Check("Intel встроенная на ноутбуке: без кнопки, с советами про встроенную графику и питание от сети",
            !intel.Advice.Any(a => a.HasLaunch) && intel.Advice.Any(a => a.Title == "Встроенная графика") && intel.Advice.Any(a => a.Title == "Электропитание")
            && intel.Advice[0].Text.Contains("Intel Graphics Software"));
        var basic = new NvState();
        NvidiaProfiles.Advise(basic, "AMD", "AMD Radeon RX 580", false, true, "", 138, 144, "", false);
        Check("драйвер не установлен: одна фраза, без советов", basic.Reason.Contains("не установлен") && basic.Advice.Count == 0);
        var nv = new NvState();
        NvidiaProfiles.Advise(nv, "NVIDIA", "NVIDIA GeForce RTX 4060", false, false, "Драйвер не отвечает", 157, 165, "", false);
        Check("NVIDIA без ответа драйвера: причина как есть", nv.Reason == "Драйвер не отвечает");

        // --- лимит кадров под разные мониторы
        var caps = new[] { (60, 59), (75, 73), (120, 116), (144, 138), (165, 157), (240, 224), (360, 324) };
        foreach (var (hz, want) in caps) Check($"монитор {hz} Гц → лимит {GameTuning.FrameCapFor(hz)} (ожидалось {want})", GameTuning.FrameCapFor(hz) == want && want < hz);

        // --- процессоры
        (string Cpu, string Socket, string Short, string Vendor)[] cpus =
        {
            ("12th Gen Intel(R) Core(TM) i5-12400F", "LGA1700", "Core i5-12400F", "Intel"),
            ("13th Gen Intel(R) Core(TM) i9-13980HX", "BGA", "Core i9-13980HX", "Intel"),
            ("12th Gen Intel(R) Core(TM) i7-1255U", "BGA", "Core i7-1255U", "Intel"),
            ("Intel(R) Core(TM) i7-10700K CPU @ 3.80GHz", "LGA1200", "Core i7-10700K", "Intel"),
            ("Intel(R) Core(TM) i5-9400F CPU @ 2.90GHz", "LGA1151", "Core i5-9400F", "Intel"),
            ("Intel(R) Core(TM) Ultra 7 265K", "LGA1851", "Core Ultra 7 265K", "Intel"),
            ("Intel(R) Core(TM) Ultra 7 155H", "BGA", "Core Ultra 7 155H", "Intel"),
            ("AMD Ryzen 7 7800X3D 8-Core Processor", "AM5", "Ryzen 7 7800X3D", "AMD"),
            ("AMD Ryzen 7 5800X3D 8-Core Processor", "AM4", "Ryzen 7 5800X3D", "AMD"),
            ("AMD Ryzen 5 5600X 6-Core Processor", "AM4", "Ryzen 5 5600X", "AMD"),
            ("AMD Ryzen 7 8700G w/ Radeon 780M Graphics", "AM5", "Ryzen 7 8700G", "AMD"),
            ("AMD Ryzen 9 7945HX with Radeon Graphics", "BGA", "Ryzen 9 7945HX", "AMD"),
            ("AMD Ryzen AI 9 HX 370 w/ Radeon 890M", "BGA", "Ryzen AI 9 HX 370", "AMD"),
            ("Snapdragon(R) X Elite - X1E78100 - Qualcomm(R) Oryon(TM) CPU", "BGA", "Snapdragon X Elite X1E78100", "Qualcomm"),
        };
        foreach (var (cpu, socket, shortName, vendor) in cpus)
        {
            string s = HardwareInfo.SocketFor(cpu), n = HardwareInfo.ShortCpu(cpu), v = HardwareInfo.CpuVendor(cpu);
            Check($"{cpu} → {s}, «{n}», {v}", s == socket && n == shortName && v == vendor);
        }
        Check("разъём из Windows, если модель незнакома: «AM5» → AM5, «U3E1» → пусто",
            HardwareInfo.SocketFor("AMD Ryzen Embedded V3C48", "AM5") == "AM5" && HardwareInfo.SocketFor("Some CPU", "U3E1") == "");

        // --- чипсет
        (string Cpu, string Board, string Want)[] boards =
        {
            ("12th Gen Intel(R) Core(TM) i5-12400F", "PRO B760-P WIFI DDR4 (MS-7D98)", "Intel B760"),
            ("Intel(R) Core(TM) i7-10700K", "PRIME B460M-A", "Intel B460"),
            ("Intel(R) Core(TM) i9-14900K", "ROG MAXIMUS Z790 HERO", "Intel Z790"),
            ("AMD Ryzen 7 7800X3D", "X670E AORUS MASTER", "AMD X670E"),
            ("AMD Ryzen 5 5600X", "ROG STRIX B550-F GAMING", "AMD B550"),
            ("AMD Ryzen 7 9800X3D", "MAG B650M MORTAR WIFI (MS-7D76)", "AMD B650"),
            ("11th Gen Intel(R) Core(TM) i5-1135G7", "X515EA", ""),
            ("AMD Ryzen 7 7840HS", "FA507XV", ""),
        };
        foreach (var (cpu, board, want) in boards) Check($"чипсет {board} → «{HardwareInfo.ChipsetFor(cpu, board)}»", HardwareInfo.ChipsetFor(cpu, board) == want);

        // --- память
        Check("тип памяти по коду SMBIOS: 26 DDR4, 34 DDR5, 35 LPDDR5, 30 LPDDR4",
            HardwareInfo.RamTypeFor(26, 3200) == "DDR4" && HardwareInfo.RamTypeFor(34, 6000) == "DDR5" && HardwareInfo.RamTypeFor(35, 7500) == "LPDDR5" && HardwareInfo.RamTypeFor(30, 4266) == "LPDDR4");
        Check("без кода: по маркировке G.Skill F5- → DDR5, по скорости 3200 → DDR4, ничего → пусто",
            HardwareInfo.RamTypeFor(0, 6000, new[] { "F5-6000J3038F16G" }) == "DDR5" && HardwareInfo.RamTypeFor(0, 3200) == "DDR4" && HardwareInfo.RamTypeFor(0, 0) == "");
        Check("скорость в МТ/с: DDR5 2400 (половина) → 4800, DDR4 3200 → 3200, нет настроенной → паспортная",
            HardwareInfo.RamMts(34, 2400, 4800) == 4800 && HardwareInfo.RamMts(26, 3200, 3200) == 3200 && HardwareInfo.RamMts(26, 0, 2666) == 2666);
        Check("производитель планок: 80CE000080CE → Samsung, 0x029E → Corsair, Unknown → пусто, Kingston → Kingston",
            HardwareInfo.RamMaker("80CE000080CE") == "Samsung" && HardwareInfo.RamMaker("0x029E") == "Corsair" && HardwareInfo.RamMaker("Unknown") == ""
            && HardwareInfo.RamMaker("Kingston") == "Kingston" && HardwareInfo.RamMaker("Micron Technology") == "Micron" && HardwareInfo.RamMaker("04CD") == "G.Skill");

        // --- накопители: тип по шине и носителю
        Check("тип диска: NVMe по шине, SATA SSD/HDD по носителю, USB, неизвестно → пусто",
            PhysDisk.KindOf("NVMe", "SSD") == "NVMe" && PhysDisk.KindOf("NVMe", "Unspecified") == "NVMe" && PhysDisk.KindOf("SATA", "SSD") == "SSD"
            && PhysDisk.KindOf("SATA", "HDD") == "HDD" && PhysDisk.KindOf("USB", "Unspecified") == "USB" && PhysDisk.KindOf("SATA", "Unspecified") == "" && PhysDisk.KindOf(null, null) == "");

        // --- батарея ноутбука
        Check("настольный: про батарею ничего", BatteryInfo.Advice(new BatteryInfo(), true) == "");
        string onBattery = BatteryInfo.Advice(new BatteryInfo { HasBattery = true, OnBattery = true, Percent = 42 }, true);
        Check("ноутбук от батареи: предупреждение с зарядом", onBattery.Contains("от батареи") && onBattery.Contains("42 %") && onBattery.Contains("зарядку"));
        Check("ноутбук на зарядке: совет играть от зарядки", BatteryInfo.Advice(new BatteryInfo { HasBattery = true, OnBattery = false, Percent = 100 }, false).Contains("от зарядки"));

        bad = fails;
        return $"Чужое железо (выдуманные данные): {(fails == 0 ? "все проверки пройдены" : "не пройдено " + fails)}" + Environment.NewLine
               + string.Join(Environment.NewLine, lines.Select(l => "  " + l));
    }
}
