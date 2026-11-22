using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using PulsePC.Panels;

namespace PulsePC.Services;

/// <summary>Понятное имя драйвера или модуля: кто это и что с ним делать.</summary>
public sealed class DriverRole
{
    public string Role = "", Advice = "", Kind = "", Link = "", LinkText = "";
}

/// <summary>
/// Понятные имена системных драйверов и модулей («ndis.sys — сетевая подсистема Windows»): для синих экранов,
/// задержек драйверов и вылетов игр. Один справочник на все проверки.
/// </summary>
public static class DriverNames
{
    private sealed record Rule(string Pattern, string Role, string Kind);

    // Шаблон: точное имя файла или начало имени с «*» на конце. Kind определяет совет.
    private static readonly Rule[] Rules =
    {
        new("nvlddmkm.sys", "драйвер видеокарты NVIDIA", "gpu"),
        new("nvwgf2um*.dll", "драйвер видеокарты NVIDIA (DirectX)", "gpu"),
        new("nvd3dum*.dll", "драйвер видеокарты NVIDIA (DirectX)", "gpu"),
        new("nvoglv*.dll", "драйвер видеокарты NVIDIA (OpenGL)", "gpu"),
        new("nvgpucomp*.dll", "драйвер видеокарты NVIDIA (сборка шейдеров)", "gpu"),
        new("nvldumd*.dll", "драйвер видеокарты NVIDIA", "gpu"),
        new("nvwgf2*.dll", "драйвер видеокарты NVIDIA", "gpu"),
        new("amdkmdag.sys", "драйвер видеокарты AMD", "gpu"),
        new("amdkmdap.sys", "драйвер видеокарты AMD", "gpu"),
        new("atikmdag.sys", "драйвер видеокарты AMD", "gpu"),
        new("atikmpag.sys", "драйвер видеокарты AMD", "gpu"),
        new("amdxx64.dll", "драйвер видеокарты AMD (DirectX)", "gpu"),
        new("atidxx64.dll", "драйвер видеокарты AMD (DirectX)", "gpu"),
        new("atiumd*.dll", "драйвер видеокарты AMD (DirectX)", "gpu"),
        new("amdvlk*.dll", "драйвер видеокарты AMD (Vulkan)", "gpu"),
        new("atio6axx.dll", "драйвер видеокарты AMD (OpenGL)", "gpu"),
        new("igdkmd*.sys", "драйвер графики Intel", "gpu"),
        new("igd10iumd*.dll", "драйвер графики Intel (DirectX)", "gpu"),
        new("igd12um*.dll", "драйвер графики Intel (DirectX 12)", "gpu"),
        new("igdumdim*.dll", "драйвер графики Intel", "gpu"),
        new("igc64.dll", "драйвер графики Intel (сборка шейдеров)", "gpu"),
        new("dxgkrnl.sys", "графическое ядро Windows (DirectX)", "dx"),
        new("dxgmms2.sys", "планировщик видеокарты Windows", "dx"),
        new("dxgmms1.sys", "планировщик видеокарты Windows", "dx"),
        new("watchdog.sys", "сторож видеодрайвера Windows", "dx"),
        new("d3d12.dll", "DirectX 12", "dx"),
        new("d3d12core.dll", "DirectX 12", "dx"),
        new("d3d11.dll", "DirectX 11", "dx"),
        new("dxgi.dll", "DirectX (вывод кадров)", "dx"),
        new("d3dscache.dll", "кэш шейдеров DirectX", "dx"),
        new("vulkan-1.dll", "Vulkan", "dx"),
        new("ndis.sys", "сетевая подсистема Windows (обычно за ней драйвер сетевой карты)", "net"),
        new("tcpip.sys", "сетевой стек Windows", "net"),
        new("netio.sys", "сетевой стек Windows", "net"),
        new("afd.sys", "сетевые соединения Windows", "net"),
        new("netwtw*.sys", "драйвер Wi‑Fi Intel", "wifi"),
        new("netwbw*.sys", "драйвер Wi‑Fi Intel", "wifi"),
        new("rtwlan*.sys", "драйвер Wi‑Fi Realtek", "wifi"),
        new("rtw89*.sys", "драйвер Wi‑Fi Realtek", "wifi"),
        new("mtkwl*.sys", "драйвер Wi‑Fi MediaTek", "wifi"),
        new("athw*.sys", "драйвер Wi‑Fi Qualcomm", "wifi"),
        new("qcamain*.sys", "драйвер Wi‑Fi Qualcomm", "wifi"),
        new("bcmwl*.sys", "драйвер Wi‑Fi Broadcom", "wifi"),
        new("rt640x64.sys", "драйвер сетевой карты Realtek", "net"),
        new("rt68cx21x64.sys", "драйвер сетевой карты Realtek", "net"),
        new("rtcx21x64.sys", "драйвер сетевой карты Realtek 2,5 Гбит/с", "net"),
        new("rtux64w10.sys", "драйвер USB-сетевой карты Realtek", "net"),
        new("e1d*.sys", "драйвер сетевой карты Intel", "net"),
        new("e1r*.sys", "драйвер сетевой карты Intel", "net"),
        new("e2f*.sys", "драйвер сетевой карты Intel 2,5 Гбит/с", "net"),
        new("killer*.sys", "драйвер сетевой карты Killer", "net"),
        new("storport.sys", "дисковая подсистема Windows", "disk"),
        new("stornvme.sys", "драйвер NVMe-дисков Windows", "disk"),
        new("storahci.sys", "драйвер SATA-дисков Windows", "disk"),
        new("iastor*.sys", "драйвер дисков Intel RST", "disk"),
        new("classpnp.sys", "дисковая подсистема Windows", "disk"),
        new("disk.sys", "драйвер дисков Windows", "disk"),
        new("ntfs.sys", "файловая система NTFS", "disk"),
        new("amdsata.sys", "драйвер SATA AMD", "disk"),
        new("acpi.sys", "управление питанием платы (ACPI, BIOS)", "acpi"),
        new("intelppm.sys", "управление питанием процессора Intel", "acpi"),
        new("amdppm.sys", "управление питанием процессора AMD", "acpi"),
        new("usbxhci.sys", "контроллер USB", "usb"),
        new("usbport.sys", "контроллер USB", "usb"),
        new("usbhub3.sys", "USB-концентратор", "usb"),
        new("usbhub.sys", "USB-концентратор", "usb"),
        new("ucx01000.sys", "контроллер USB", "usb"),
        new("hidusb.sys", "устройство ввода USB (мышь, клавиатура)", "usb"),
        new("hdaudbus.sys", "шина звука HD Audio", "audio"),
        new("portcls.sys", "звуковая подсистема Windows", "audio"),
        new("rtkvhd64.sys", "драйвер звука Realtek", "audio"),
        new("rtkvhda64.sys", "драйвер звука Realtek", "audio"),
        new("nvhda64v.sys", "звук HDMI видеокарты NVIDIA", "audio"),
        new("atihdwt6.sys", "звук HDMI видеокарты AMD", "audio"),
        new("nvvad64v.sys", "виртуальный звук NVIDIA", "audio"),
        new("wdf01000.sys", "каркас драйверов Windows (за ним стоит другой драйвер)", "wdf"),
        new("ntoskrnl.exe", "ядро Windows", "kernel"),
        new("ntkrnlmp.exe", "ядро Windows", "kernel"),
        new("hal.dll", "слой оборудования Windows", "kernel"),
        new("win32kbase.sys", "графический интерфейс Windows", "kernel"),
        new("win32kfull.sys", "графический интерфейс Windows", "kernel"),
        new("vgk.sys", "античит Riot Vanguard", "anticheat"),
        new("vgc.exe", "античит Riot Vanguard", "anticheat"),
        new("easyanticheat*", "античит Easy Anti-Cheat", "anticheat"),
        new("beclient*.dll", "античит BattlEye", "anticheat"),
        new("bedaisy.sys", "античит BattlEye", "anticheat"),
        new("gameguard*", "античит GameGuard", "anticheat"),
        new("fltmgr.sys", "фильтры файловой системы (антивирусы, облачные диски)", "filter"),
        new("wdfilter.sys", "фильтр файлов Защитника Windows", "filter"),
        new("gameoverlayrenderer*.dll", "оверлей Steam", "overlay"),
        new("discordhook*.dll", "оверлей Discord", "overlay"),
        new("rtsshooks*.dll", "оверлей RivaTuner (MSI Afterburner)", "overlay"),
        new("nvspcap*.dll", "оверлей NVIDIA (запись и показ FPS)", "overlay"),
        new("graphics-hook*.dll", "захват OBS", "overlay"),
        new("overlay*.dll", "оверлей", "overlay"),
        new("ucrtbase.dll", "библиотеки Visual C++", "vcrt"),
        new("vcruntime*.dll", "библиотеки Visual C++", "vcrt"),
        new("msvcp*.dll", "библиотеки Visual C++", "vcrt"),
        new("kernelbase.dll", "системная библиотека Windows (ошибку подняла сама игра)", "game"),
        new("ntdll.dll", "системная библиотека Windows", "ntdll"),
        new("clr.dll", "среда .NET", "game"),
        new("coreclr.dll", "среда .NET", "game"),
        new("unityplayer.dll", "движок Unity (часть игры)", "game"),
        new("gameassembly.dll", "код игры (Unity)", "game"),
    };

    /// <summary>Имя файла по шаблону со звёздочкой в любом месте («netwtw*.sys»), без учёта регистра.</summary>
    private static bool Like(string file, string pattern) =>
        pattern.Contains('*') ? Regex.IsMatch(file, "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase) : string.Equals(file, pattern, StringComparison.OrdinalIgnoreCase);

    public static DriverRole Describe(string file)
    {
        string f = Path.GetFileName(file ?? "").ToLowerInvariant();
        var rule = Rules.FirstOrDefault(r => Like(f, r.Pattern));
        string kind = rule?.Kind ?? (f.EndsWith(".sys") ? "driver" : "other");
        var d = new DriverRole { Kind = kind, Role = rule != null ? L.T(rule.Role) : f.EndsWith(".sys") ? L.T("драйвер устройства") : L.T("модуль программы") };
        switch (kind)
        {
            case "gpu":
                d.Advice = L.T("Поставьте другую версию драйвера видеокарты (свежую или, если сбои начались после обновления, прошлую), уберите разгон видеокарты, если он есть.");
                d.Link = "sys:Драйвер"; d.LinkText = L.T("Версия драйвера и где его скачать — в разделе «Драйвер»");
                break;
            case "dx":
                d.Advice = L.T("Сама Windows тут обычно ни при чём — чаще виноват драйвер видеокарты: обновите или переустановите его. Ещё помогает очистка кэша шейдеров.");
                d.Link = "sys:Драйвер"; d.LinkText = L.T("Версия драйвера и где его скачать — в разделе «Драйвер»");
                break;
            case "net":
                d.Advice = L.T("Обновите драйвер сетевой карты с сайта производителя платы или карты и выключите энергосбережение сетевой карты.");
                d.Link = "tab:Оптимизация"; d.LinkText = L.T("Энергосбережение сетевой карты — на вкладке «Оптимизация»");
                break;
            case "wifi":
                d.Advice = L.T("Обновите драйвер Wi‑Fi с сайта производителя ноутбука, платы или адаптера. Если играете по Wi‑Fi и есть возможность — подключите кабель.");
                break;
            case "disk":
                d.Advice = L.T("Обновите драйвер чипсета или контроллера дисков и прошивку SSD (программа производителя диска). Проверьте состояние дисков.");
                break;
            case "acpi":
                d.Advice = L.T("Обычно помогает обновление BIOS и драйвера чипсета с сайта производителя платы или ноутбука.");
                break;
            case "usb":
                d.Advice = L.T("Отключайте USB-устройства по одному (хабы, подсветку, звуковые карты, геймпады) и повторяйте проверку — так найдётся виновник.");
                break;
            case "audio":
                d.Advice = L.T("Обновите драйвер звука с сайта производителя платы или ноутбука; ненужные звуковые устройства можно выключить в «Параметрах звука».");
                break;
            case "wdf":
                d.Advice = L.T("Сам каркас редко виноват — это один из драйверов, построенных на нём. Обновите драйверы чипсета и устройств с сайта производителя.");
                break;
            case "kernel":
                d.Advice = L.T("Ядро Windows само по себе редко виновато: обычно это следствие сбоя драйвера или памяти. Обновите драйверы и проверьте память.");
                break;
            case "anticheat":
                d.Advice = L.T("Переустановите античит игры или проверьте целостность файлов игры в лаунчере; если не помогло — напишите в поддержку игры.");
                break;
            case "filter":
                d.Advice = L.T("Этот фильтр ставят антивирусы и облачные диски. Если стоит сторонний антивирус — обновите его.");
                break;
            case "overlay":
                d.Advice = L.T("Выключите этот оверлей для игры и проверьте, повторится ли вылет.");
                break;
            case "vcrt":
                d.Advice = L.T("Переустановите «Распространяемый пакет Visual C++» (x64) с сайта Microsoft и проверьте целостность файлов игры в лаунчере.");
                break;
            case "ntdll":
                d.Advice = L.T("Часто это повреждённые файлы игры или нестабильная память: проверьте целостность файлов игры в лаунчере, а если включён разгон или XMP — проверьте без него.");
                break;
            case "game":
                d.Advice = L.T("Сбой в коде самой игры: проверьте целостность файлов в лаунчере, обновите игру, выключите моды и оверлеи.");
                break;
            case "driver":
                d.Advice = L.T("Обновите драйвер этого устройства с сайта производителя или удалите программу, которая его поставила.");
                break;
            default:
                d.Advice = L.T("Проверьте целостность файлов игры в лаунчере и обновите драйвер видеокарты.");
                break;
        }
        return d;
    }
}

/// <summary>Код синего экрана: имя, что значит простыми словами и что делать.</summary>
public sealed class BugCheckInfo
{
    public uint Code;
    public string Name = "", Meaning = "", Advice = "";
    public string Hex => "0x" + Code.ToString("X", CultureInfo.InvariantCulture);
}

/// <summary>Синий экран из журнала Windows (событие 1001 WER-SystemErrorReporting или Kernel-Power 41 с кодом).</summary>
public sealed class BsodEvent
{
    public DateTime Time;
    public uint Code;
    public ulong[] Params = new ulong[4];
    public string DumpPath = "";
}

/// <summary>Вылет или зависание программы (Application Error 1000 / Application Hang 1002).</summary>
public sealed class CrashEvent
{
    public DateTime Time;
    public string App = "", Path = "", Module = "", Exception = "";
    public bool Hang;
    /// <summary>Название игры, если программа — игра. Пусто — не игра.</summary>
    public string Game = "";
}

/// <summary>Аппаратная ошибка WHEA.</summary>
public sealed class WheaEvent
{
    public DateTime Time;
    public int Id;
    public bool Fatal;
    /// <summary>cpu, memory, pcie, other.</summary>
    public string Kind = "other";
    public string Device = "";
}

/// <summary>Результат проверки хранилища компонентов Windows (DISM /CheckHealth).</summary>
public enum DismState { Unknown, Healthy, Repairable, NotRepairable, NeedAdmin, Failed }

public sealed class DismResult
{
    public DateTime At { get; set; }
    public DismState State { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>Итог «Средства проверки памяти Windows» из журнала.</summary>
public sealed class MemTestResult
{
    public DateTime Time;
    public bool Errors;
}

/// <summary>
/// Стабильность Windows (версия 1.5): синие экраны простыми словами (журнал + малые дампы), аппаратные ошибки WHEA по видам,
/// вылеты игр с модулем-виновником, целостность Windows (DISM /CheckHealth — только чтение), ожидание перезагрузки,
/// «драйвер стал хуже», время загрузки Windows (тренд и кто тормозит), результат проверки памяти, задержки драйверов.
/// Всё только читает журналы и файлы; единственные действия — по кнопкам: DISM /CheckHealth (ничего не меняет)
/// и запуск средства проверки памяти Windows (после подтверждения, перезагрузку выбирает человек в окне Windows).
/// </summary>
public static class StabilityChecks
{
    public const string DismKey = "stab.dism.last", MemAskedKey = "stab.memtest.asked", MemNotifiedKey = "stab.memtest.notified";
    private const string Nb = "\u00A0";
    private static PanelHost host;

    public static void Init(PanelHost h)
    {
        host = h;
        // После перезагрузки с проверкой памяти — одно уведомление с её итогом.
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(45));
                var r = ReadMemTest();
                var s = h.Settings;
                if (r == null || (DateTime.Now - r.Time).TotalDays > 3) return;
                if (DateTime.TryParse(s.Get(MemNotifiedKey), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var done) && done >= r.Time) return;
                s.Set(MemNotifiedKey, r.Time.ToString("o", CultureInfo.InvariantCulture));
                s.Save();
                h.Notify(r.Errors ? L.T("Проверка памяти нашла ошибки") : L.T("Проверка памяти: ошибок нет"),
                         r.Errors ? L.T("Средство проверки памяти Windows нашло аппаратные ошибки. Что делать — на вкладке «Диагностика».")
                                  : L.F("Средство проверки памяти Windows ({0:d MMMM}) ошибок не нашло.", r.Time),
                         r.Errors, () => h.Open("tab:Диагностика"));
            }
            catch (Exception ex) { Log.Error("стабильность: итог проверки памяти", ex); }
        });
    }

    private static string Q(string s) => L.Pick("«" + s + "»", "\"" + s + "\"");
    private static string When(DateTime t) => t.ToString(L.Pick("dd.MM 'в' HH:mm", "MMM d 'at' HH:mm"), L.Culture);
    private static string Day(DateTime t) => t.ToString(L.Pick("d MMMM", "MMMM d"), L.Culture);
    /// <summary>После двоеточия по-русски — со строчной буквы (кроме названий вроде «Windows»).</summary>
    private static string AfterColon(string s) => L.En || string.IsNullOrEmpty(s) || s.Length < 2 || char.IsUpper(s[1]) ? s : char.ToLower(s[0], L.Culture) + s[1..];

    // ================================================================== подставные данные для снимков
    /// <summary>
    /// PULSEPC_TEST_STABILITY=1 — «Диагностика» показывает выводы о стабильности на подставных данных (синий экран с дампом,
    /// WHEA, вылеты игры, драйвер стал хуже, загрузка, проверки). Только для снимков и проверки вида; журналы не читаются.
    /// </summary>
    public static bool Demo => Environment.GetEnvironmentVariable("PULSEPC_TEST_STABILITY") == "1";

    private static readonly (string, ulong, uint)[] DemoModules = { ("ntoskrnl.exe", 0xFFFFF80500000000UL, 0x1000000u), ("nvlddmkm.sys", 0xFFFFF80620000000UL, 0x3000000u), ("ndis.sys", 0xFFFFF80610000000UL, 0x200000u) };

    private static List<DiagItem> DemoBsod()
    {
        var now = DateTime.Now;
        var ev = new BsodEvent { Time = now.AddDays(-1).Date.AddHours(21).AddMinutes(14), Code = 0x116, DumpPath = @"C:\Windows\Minidump\demo-1.dmp" };
        var ev2 = new BsodEvent { Time = now.AddDays(-4).Date.AddHours(23).AddMinutes(2), Code = 0x116, DumpPath = @"C:\Windows\Minidump\demo-2.dmp" };
        var d1 = new MinidumpInfo { File = ev.DumpPath }; Minidump.Parse(Minidump.BuildFake(0x116, new[] { 0xFFFFC00012345678UL, 0xFFFFF80620123456UL, 0UL, 0UL }, DemoModules, null, ev.Time), d1); d1.File = ev.DumpPath;
        var d2 = new MinidumpInfo { File = ev2.DumpPath }; Minidump.Parse(Minidump.BuildFake(0x116, new[] { 0xFFFFC00012345678UL, 0xFFFFF80620123456UL, 0UL, 0UL }, DemoModules, null, ev2.Time), d2); d2.File = ev2.DumpPath;
        return new List<DiagItem> { BsodFinding(new[] { ev, ev2 }, new[] { ev, ev2 }, new[] { d1, d2 }, false, "") };
    }

    private static List<DiagItem> DemoWhea()
    {
        var now = DateTime.Now;
        var w = Enumerable.Range(0, 14).Select(i => new WheaEvent { Time = now.AddHours(-7 * i - 3), Id = 19, Fatal = i == 0, Kind = "cpu" }).ToList();
        // Подставные только сами события. Процессор, память и «ноутбук или нет» — настоящие (только чтение), иначе вывод спорил бы
        // с соседними выводами на том же экране (другой процессор, «профиль XMP включён», когда память выше показана без него).
        string cpu = ""; bool lap = false; MemVerdict mem = null;
        try { cpu = MemoryProfile.CpuName(); } catch { }
        try { lap = HardwareChecks.IsLaptop(); } catch { }
        if (!lap) { try { mem = MemoryProfile.Speed(MemoryProfile.Current()); } catch { } }
        return new List<DiagItem> { WheaFinding(w, w.Concat(w.Take(5)).ToList(), mem, cpu, lap, "") };
    }

    private static List<DiagItem> DemoCrashes(out HashSet<string> apps)
    {
        var now = DateTime.Now;
        apps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "TslGame.exe" };
        var c = new List<CrashEvent>
        {
            new() { Time = now.AddHours(-20), App = "TslGame.exe", Module = "nvwgf2umx.dll", Exception = "0xc0000005", Game = "PUBG: BATTLEGROUNDS" },
            new() { Time = now.AddDays(-2), App = "TslGame.exe", Module = "nvwgf2umx.dll", Exception = "0xc0000005", Game = "PUBG: BATTLEGROUNDS" },
            new() { Time = now.AddDays(-3), App = "TslGame.exe", Hang = true, Game = "PUBG: BATTLEGROUNDS" },
        };
        return CrashFindings(c, null);
    }

    private static List<DiagItem> DemoOther()
    {
        var now = DateTime.Now;
        var res = new List<DiagItem>();
        var inst = now.AddDays(-5);
        var fl = new List<Failure> { new() { Time = now.AddDays(-12), Kind = "reset" } };
        fl.AddRange(Enumerable.Range(0, 7).Select(i => new Failure { Time = now.AddDays(-4).AddHours(i * 11), Kind = i % 3 == 0 ? "bsod" : i % 2 == 0 ? "crash" : "reset" }));
        res.Add(DriverWorseFinding(inst, "621.24", fl, new List<(DateTime, double)>(), now));
        var br = new BootReport { LastBoot = now };
        foreach (var x in new[] { 24.0, 22, 25, 23, 46, 51, 49 }) br.Boots.Add(new BootRecord { At = now.AddDays(-7 + br.Boots.Count), Seconds = x, MainSeconds = x * 0.6, PostSeconds = x * 0.4 });
        br.SlowAll = new List<BootSlowStat> { new() { Kind = L.T("программа"), Name = "Game Launcher Updater", Boots = 3, AvgSeconds = 11.5 } };
        res.Add(BootFinding(br));
        res.Add(RebootFinding(false, false, 3));
        res.Add(MemTestFinding(new MemTestResult { Time = now.AddDays(-2) }, false, now));
        res.Add(DpcLatency.Finding(DemoDpc()));
        res.Add(DismFinding(DemoDism()));
        return res.Where(x => x != null).ToList();
    }

    public static DpcResult DemoDpc() => new()
    {
        At = DateTime.Now.AddMinutes(-12), Seconds = 30, Events = 41200,
        Drivers = new()
        {
            new DpcDriver { Name = "ndis.sys", MaxUs = 2860, TotalMs = 41.2, Count = 9100 },
            new DpcDriver { Name = "nvlddmkm.sys", MaxUs = 610, TotalMs = 88.4, Count = 23000, MaxIsIsr = true },
            new DpcDriver { Name = "storport.sys", MaxUs = 180, TotalMs = 6.1, Count = 3100 },
            new DpcDriver { Name = "HDAudBus.sys", MaxUs = 95, TotalMs = 3.3, Count = 2800 },
        },
    };

    public static DismResult DemoDism() => new() { At = DateTime.Now.AddMinutes(-3), State = DismState.Repairable };

    public static MemTestResult DemoMemTest() => new() { Time = DateTime.Now.AddDays(-2) };

    // ================================================================== 1. синие экраны
    private static readonly Dictionary<uint, (string Name, string Meaning, string Advice)> Codes = new()
    {
        [0x0A] = ("IRQL_NOT_LESS_OR_EQUAL", "драйвер обратился к памяти, к которой в этот момент обращаться нельзя", "drv"),
        [0xD1] = ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", "драйвер обратился к памяти, к которой в этот момент обращаться нельзя", "drv"),
        [0x1E] = ("KMODE_EXCEPTION_NOT_HANDLED", "в драйвере или ядре произошла ошибка, которую никто не обработал", "drv"),
        [0x3B] = ("SYSTEM_SERVICE_EXCEPTION", "ошибка в системном вызове — обычно драйвер", "drv"),
        [0x7E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "системный поток (обычно драйвера) упал с ошибкой", "drv"),
        [0x50] = ("PAGE_FAULT_IN_NONPAGED_AREA", "обращение к несуществующей памяти — драйвер или неисправная память", "drvmem"),
        [0x1A] = ("MEMORY_MANAGEMENT", "сбой в управлении памятью — часто неисправная или нестабильная память", "mem"),
        [0x4E] = ("PFN_LIST_CORRUPT", "испорчены служебные таблицы памяти — память или драйвер", "mem"),
        [0x19] = ("BAD_POOL_HEADER", "испорчена область памяти ядра — обычно драйвер", "drv"),
        [0xC2] = ("BAD_POOL_CALLER", "драйвер неправильно работает с памятью ядра", "drv"),
        [0xC5] = ("DRIVER_CORRUPTED_EXPOOL", "драйвер испортил память ядра", "drv"),
        [0x139] = ("KERNEL_SECURITY_CHECK_FAILURE", "ядро нашло испорченные данные — драйвер или память", "drvmem"),
        [0x13A] = ("KERNEL_MODE_HEAP_CORRUPTION", "драйвер испортил память ядра", "drv"),
        [0xBE] = ("ATTEMPTED_WRITE_TO_READONLY_MEMORY", "драйвер попытался писать туда, куда нельзя", "drv"),
        [0xFC] = ("ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY", "попытка выполнить данные как код — драйвер или память", "drvmem"),
        [0x9F] = ("DRIVER_POWER_STATE_FAILURE", "драйвер не ответил при переходе в сон или выходе из него", "power"),
        [0x133] = ("DPC_WATCHDOG_VIOLATION", "драйвер слишком долго не отпускал процессор", "dpc"),
        [0x101] = ("CLOCK_WATCHDOG_TIMEOUT", "одно из ядер процессора перестало отвечать — часто разгон или понижение напряжения", "cpu"),
        [0x124] = ("WHEA_UNCORRECTABLE_ERROR", "оборудование сообщило о неисправимой ошибке — процессор, память или шина", "hw"),
        [0x9C] = ("MACHINE_CHECK_EXCEPTION", "процессор сообщил об аппаратной ошибке", "hw"),
        [0x116] = ("VIDEO_TDR_FAILURE", "видеокарта зависла, и драйвер не смог её перезапустить", "gpu"),
        [0x117] = ("VIDEO_TDR_TIMEOUT_DETECTED", "видеокарта не ответила вовремя", "gpu"),
        [0x119] = ("VIDEO_SCHEDULER_INTERNAL_ERROR", "сбой планировщика видеокарты", "gpu"),
        [0x10E] = ("VIDEO_MEMORY_MANAGEMENT_INTERNAL", "сбой в работе с видеопамятью", "gpu"),
        [0xEF] = ("CRITICAL_PROCESS_DIED", "неожиданно завершился важный процесс Windows", "sys"),
        [0xF4] = ("CRITICAL_OBJECT_TERMINATION", "неожиданно завершился важный процесс Windows — часто из-за диска", "disk"),
        [0x7A] = ("KERNEL_DATA_INPAGE_ERROR", "Windows не смогла прочитать данные с диска", "disk"),
        [0x7B] = ("INACCESSIBLE_BOOT_DEVICE", "Windows потеряла доступ к системному диску", "disk"),
        [0x77] = ("KERNEL_STACK_INPAGE_ERROR", "Windows не смогла прочитать данные с диска", "disk"),
        [0x154] = ("UNEXPECTED_STORE_EXCEPTION", "сбой хранилища памяти Windows — чаще всего диск", "disk"),
        [0x7F] = ("UNEXPECTED_KERNEL_MODE_TRAP", "процессор поймал ошибку, которую ядро не ожидало — железо или драйвер", "hw"),
        [0x109] = ("CRITICAL_STRUCTURE_CORRUPTION", "испорчены важные данные ядра — драйвер или память", "drvmem"),
        [0xC4] = ("DRIVER_VERIFIER_DETECTED_VIOLATION", "проверка драйверов (Driver Verifier) поймала ошибку драйвера", "drv"),
        [0x1000007E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED_M", "системный поток (обычно драйвера) упал с ошибкой", "drv"),
        [0x1000008E] = ("KERNEL_MODE_EXCEPTION_NOT_HANDLED_M", "в драйвере или ядре произошла ошибка, которую никто не обработал", "drv"),
        [0x8E] = ("KERNEL_MODE_EXCEPTION_NOT_HANDLED", "в драйвере или ядре произошла ошибка, которую никто не обработал", "drv"),
        [0x18] = ("REFERENCE_BY_POINTER", "драйвер неправильно считает ссылки на объекты", "drv"),
        [0xA0] = ("INTERNAL_POWER_ERROR", "сбой управления питанием", "power"),
        [0x1D8] = ("UNEXPECTED_STORE_EXCEPTION", "сбой хранилища памяти Windows", "disk"),
    };

    public static BugCheckInfo Code(uint code)
    {
        var info = new BugCheckInfo { Code = code };
        if (!Codes.TryGetValue(code, out var c))
        {
            info.Name = "";
            info.Meaning = L.T("редкий код — его смысл можно найти по номеру на сайте Microsoft («Bug Check Code Reference»)");
            info.Advice = L.T("Обновите драйверы видеокарты, чипсета и сетевой карты; если включён разгон или XMP — проверьте без него.");
            return info;
        }
        info.Name = c.Name;
        info.Meaning = L.T(c.Meaning);
        info.Advice = c.Advice switch
        {
            "drv" => L.T("Почти всегда виноват драйвер: обновите драйвер, который назван виновником, а если его не видно — драйверы видеокарты, сетевой карты и чипсета."),
            "drvmem" => L.T("Виноват драйвер или память: обновите драйвер-виновник, проверьте память средством Windows; если включён разгон или XMP — проверьте без него."),
            "mem" => L.T("Проверьте память средством Windows (вкладка «Диагностика», «Проверка памяти»). Если включён профиль XMP/EXPO или разгон — проверьте без него."),
            "power" => L.T("Обновите драйвер устройства-виновника (часто сетевая карта, Wi‑Fi или USB) и BIOS; на время проверки можно выключить «быстрый запуск» и сон."),
            "dpc" => L.T("Обновите драйвер-виновник и драйверы чипсета и SSD; проверьте задержки драйверов (вкладка «Диагностика», «Задержки драйверов»)."),
            "cpu" => L.T("Если процессор разогнан или ему понижали напряжение (андервольт, Curve Optimizer) — верните настройки BIOS по умолчанию. Обновите BIOS."),
            "hw" => LaptopCheck.Applies
                ? L.T("Это аппаратная ошибка: если меняли настройки BIOS или разгоняли ноутбук — верните настройки по умолчанию, обновите BIOS, проверьте, не перегревается ли ноутбук (подставка, чистка в сервисе), и подключайте родную зарядку.")
                : L.T("Это аппаратная ошибка: верните разгон процессора и памяти (включая XMP/EXPO) к настройкам по умолчанию, обновите BIOS, проверьте температуру и блок питания."),
            "gpu" => L.T("Поставьте другую версию драйвера видеокарты, уберите разгон видеокарты; если повторяется под нагрузкой — проверьте питание видеокарты и её охлаждение."),
            "sys" => L.T("Проверьте целостность Windows (вкладка «Диагностика», «Целостность Windows») и состояние системного диска."),
            "disk" => L.T("Проверьте системный диск: состояние, кабель (для SATA), свежую прошивку SSD. Сделайте копию важных данных."),
            _ => "",
        };
        return info;
    }

    /// <summary>«0x00000133 (0x0000000000000001, 0x0000000000001e00, 0xfffff8…, 0x…)» → код и параметры.</summary>
    public static bool ParseBugcheck(string text, out uint code, out ulong[] p)
    {
        code = 0; p = new ulong[4];
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = Regex.Matches(text, @"0x([0-9a-fA-F]+)");
        if (m.Count == 0) return false;
        if (!uint.TryParse(m[0].Groups[1].Value.TrimStart('0').PadLeft(1, '0'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) return false;
        for (int i = 1; i < m.Count && i <= 4; i++)
            ulong.TryParse(m[i].Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out p[i - 1]);
        return code != 0;
    }

    public static List<BsodEvent> ReadBsods(DateTime since)
    {
        var list = new List<BsodEvent>();
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, LogMarks.Query(LogMarks.Bsod, since)) { ReverseDirection = true });
            for (EventRecord e = reader.ReadEvent(); e != null && list.Count < 200; e = reader.ReadEvent())
                using (e)
                {
                    var b = new BsodEvent { Time = e.TimeCreated ?? DateTime.MinValue };
                    try
                    {
                        if (e.Properties.Count > 0 && ParseBugcheck(e.Properties[0].Value as string, out var c, out var p)) { b.Code = c; b.Params = p; }
                        if (e.Properties.Count > 1) b.DumpPath = (e.Properties[1].Value as string ?? "").Trim();
                    }
                    catch { }
                    list.Add(b);
                }
            // Синий экран без записи 1001 (дамп не сохранился): Kernel-Power 41 с ненулевым кодом.
            string q = $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41) and {LogMarks.TimeFrom(since)}]]";
            using var kp = new EventLogReader(new EventLogQuery("System", PathType.LogName, q) { ReverseDirection = true });
            for (EventRecord e = kp.ReadEvent(); e != null; e = kp.ReadEvent())
                using (e)
                {
                    var data = Data(e);
                    if (!data.TryGetValue("BugcheckCode", out var raw) || !uint.TryParse(raw, out uint code) || code == 0) continue;
                    var t = e.TimeCreated ?? DateTime.MinValue;
                    if (list.Any(x => Math.Abs((x.Time - t).TotalMinutes) < 30)) continue;
                    var b = new BsodEvent { Time = t, Code = code };
                    for (int i = 0; i < 4; i++)
                        if (data.TryGetValue("BugcheckParameter" + (i + 1), out var pv)) ulong.TryParse(pv.Replace("0x", ""), pv.StartsWith("0x") ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out b.Params[i]);
                    list.Add(b);
                }
        }
        catch (Exception ex) { Log.Error("стабильность: синие экраны", ex); }
        return list.OrderByDescending(x => x.Time).ToList();
    }

    /// <summary>Дамп к синему экрану: по пути из события, иначе — ближайший по времени (±15 минут).</summary>
    public static MinidumpInfo DumpFor(BsodEvent b, IList<MinidumpInfo> dumps)
    {
        if (dumps == null || dumps.Count == 0) return null;
        var byPath = dumps.FirstOrDefault(d => b.DumpPath.Length > 0 && string.Equals(Path.GetFileName(d.File), Path.GetFileName(b.DumpPath), StringComparison.OrdinalIgnoreCase));
        if (byPath != null) return byPath;
        return dumps.Where(d => Math.Abs((d.Time - b.Time).TotalMinutes) <= 15).OrderBy(d => Math.Abs((d.Time - b.Time).TotalMinutes)).FirstOrDefault();
    }

    /// <summary>
    /// Вывод о синих экранах (чистая функция): week — за период «Диагностики», month — за 30 дней (для «за неделю нет»),
    /// dumps — разобранные малые дампы (пусто, если нет прав), denied — папку дампов прочитать нельзя.
    /// </summary>
    public static DiagFinding BsodFinding(IList<BsodEvent> week, IList<BsodEvent> month, IList<MinidumpInfo> dumps, bool denied, string period)
    {
        if (week.Count == 0)
        {
            var older = month.Where(m => !week.Contains(m)).ToList();
            return new DiagFinding
            {
                Level = Severity.Ok, Title = L.T("Синих экранов нет"),
                Detail = older.Count > 0 ? period + "; " + L.F("за 30 дней — {0}, последний {1}", older.Count, Day(older[0].Time)) : period,
            };
        }
        var last = week[0];
        var info = Code(last.Code);
        var sb = new StringBuilder();
        sb.Append(L.F("Последний — {0}: код {1}", When(last.Time), info.Hex));
        if (info.Name.Length > 0) sb.Append(" (" + info.Name + ")");
        sb.Append(" — ").Append(info.Meaning).Append('.');

        // Виновник по дампам: у каждого синего экрана свой дамп, считаем, какой драйвер встречается чаще.
        var culprits = week.Select(b => DumpFor(b, dumps)).Where(d => d != null && d.Culprit.Length > 0).ToList();
        if (culprits.Count > 0)
        {
            var top = culprits.GroupBy(d => d.Culprit, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).First();
            var role = DriverNames.Describe(top.Key);
            bool guess = top.All(d => d.CulpritFromStack);
            sb.Append(' ').Append(guess ? L.F("По дампу памяти вероятный виновник — {0} ({1})", top.Key, role.Role) : L.F("По дампу памяти виновник — {0} ({1})", top.Key, role.Role));
            if (week.Count > 1) sb.Append(L.F(", в {0} из {1}", top.Count(), Fmt.Plural(week.Count, "случая", "случаев", "случаев")));
            sb.Append('.');
            string adv = role.Kind is "kernel" ? info.Advice : role.Advice;
            sb.Append(' ').Append(L.T("Что сделать:")).Append(' ').Append(AfterColon(adv));
            var f = new DiagFinding { Level = Severity.Bad, Title = L.T("Синие экраны"), Count = week.Count.ToString(), Kind = LogMarks.Bsod, Detail = sb.ToString() };
            if (role.Link.Length > 0) { f.Link = role.Link; f.LinkText = role.LinkText; }
            return Codes2(week, f);
        }

        sb.Append(' ').Append(L.T("Что сделать:")).Append(' ').Append(AfterColon(info.Advice));
        bool hasDumpFile = week.Any(b => b.DumpPath.Length > 0);
        if (denied) sb.Append(' ').Append(L.T("Какой драйвер виноват, видно из файла дампа памяти, но прочитать его можно только с правами администратора."));
        else if (!hasDumpFile && (dumps == null || dumps.Count == 0))
            sb.Append(' ').Append(L.T("Файл дампа памяти не сохранился, поэтому виновный драйвер не определить. Сохранение включается в «Параметрах системы»: Дополнительно → Загрузка и восстановление → «Малый дамп памяти»."));
        return Codes2(week, new DiagFinding { Level = Severity.Bad, Title = L.T("Синие экраны"), Count = week.Count.ToString(), Kind = LogMarks.Bsod, Detail = sb.ToString() });
    }

    /// <summary>Если кодов несколько — дописать сводку «коды: 0x133 (2), 0x3B (1)».</summary>
    private static DiagFinding Codes2(IList<BsodEvent> week, DiagFinding f)
    {
        var codes = week.GroupBy(b => b.Code).OrderByDescending(g => g.Count()).ToList();
        if (codes.Count > 1)
            f.Detail += " " + L.F("Коды: {0}.", string.Join(", ", codes.Take(4).Select(g => $"0x{g.Key:X} ({g.Count()})")));
        return f;
    }

    /// <summary>Строка «Диагностики» о синих экранах: журнал за период (с отметкой «просмотрено») + малые дампы (с правами).</summary>
    public static List<DiagItem> Bsod(AppSettings settings)
    {
        if (Demo) return DemoBsod();
        int days = Diagnostics.Days;
        var since = LogMarks.Since(LogMarks.Bsod, days, settings);
        var month = ReadBsods(LogMarks.Since(LogMarks.Bsod, 30, settings));
        var week = month.Where(b => b.Time >= since).ToList();
        bool denied = false;
        List<MinidumpInfo> dumps = week.Count > 0 ? Minidump.Recent(days + 1, 20, out denied) : new();
        string period = LogMarks.SeenWithin(LogMarks.Bsod, days, settings) is DateTime seen ? L.F("{0} — новых нет", LogMarks.SeenText(seen)) : L.F("за {0} дней", days);
        return new List<DiagItem> { BsodFinding(week, month, dumps, denied, period) };
    }

    // ================================================================== 2. WHEA
    private static readonly Dictionary<string, string> PciVendors = new(StringComparer.OrdinalIgnoreCase)
    { ["10DE"] = "NVIDIA", ["1002"] = "AMD", ["1022"] = "AMD", ["8086"] = "Intel", ["144D"] = "Samsung", ["15B7"] = "WD", ["1987"] = "Phison", ["10EC"] = "Realtek", ["1C5C"] = "SK hynix", ["126F"] = "Silicon Motion", ["1E0F"] = "Kioxia", ["1CC1"] = "ADATA", ["2646"] = "Kingston", ["C0A9"] = "Crucial", ["1344"] = "Micron" };

    /// <summary>Вид ошибки по номеру события и названиям полей (тексты событий зависят от языка Windows — по ним не судим).</summary>
    public static string WheaKind(int id, ICollection<string> fields, string errorSource)
    {
        bool Has(params string[] names) => names.Any(n => fields.Contains(n, StringComparer.OrdinalIgnoreCase));
        if (errorSource == "4" || Has("VendorID", "DeviceID", "PortType", "PrimaryBusNumber") || id is 17 or 20 or 21) return "pcie";
        if (Has("PhysicalAddress", "MemoryModule", "Card", "Rank", "Row", "Column", "ErrorPage") || id is 46 or 47 or 48) return "memory";
        if (Has("MciStat", "MCABank", "ApicId", "MciAddr") || id is 18 or 19) return "cpu";
        return "other";
    }

    public static List<WheaEvent> ReadWhea(DateTime since)
    {
        var list = new List<WheaEvent>();
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, LogMarks.Query(LogMarks.Whea, since)) { ReverseDirection = true });
            for (EventRecord e = reader.ReadEvent(); e != null && list.Count < 3000; e = reader.ReadEvent())
                using (e)
                {
                    var data = list.Count < 300 ? Data(e) : new Dictionary<string, string>();
                    data.TryGetValue("ErrorSource", out var src);
                    var w = new WheaEvent { Time = e.TimeCreated ?? DateTime.MinValue, Id = e.Id, Fatal = e.Level is 1 or 2, Kind = WheaKind(e.Id, data.Keys, src ?? "") };
                    if (w.Kind == "pcie" && data.TryGetValue("VendorID", out var v))
                    {
                        string hex = v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? v[2..] : uint.TryParse(v, out uint dv) ? dv.ToString("X4") : v;
                        hex = hex.TrimStart('0').PadLeft(4, '0');
                        w.Device = PciVendors.TryGetValue(hex, out var name) ? name : "";
                    }
                    list.Add(w);
                }
        }
        catch (Exception ex) { Log.Error("стабильность: WHEA", ex); }
        return list;
    }

    private static string KindName(string kind) => kind switch
    {
        "cpu" => L.T("процессор"), "memory" => L.T("память"), "pcie" => L.T("шина PCIe"), _ => L.T("другое"),
    };

    /// <summary>
    /// Вывод об аппаратных ошибках (чистая функция). week/month — события за неделю и за месяц; mem — вывод о частоте памяти
    /// (для совета «проверьте без XMP/EXPO» — сам текст о рисках профиля живёт в MemoryProfile и разделе «Совет по апгрейду»);
    /// cpu — название процессора; laptop — ноутбук (про XMP и разгон не говорим).
    /// </summary>
    public static DiagFinding WheaFinding(IList<WheaEvent> week, IList<WheaEvent> month, MemVerdict mem, string cpu, bool laptop, string period)
    {
        if (month.Count == 0) return null;
        if (week.Count == 0)
            return new DiagFinding { Level = Severity.Ok, Title = L.T("Аппаратных ошибок нет"), Detail = period + "; " + L.F("за 30 дней — {0}", month.Count) };
        int fatal = week.Count(w => w.Fatal);
        var kinds = week.GroupBy(w => w.Kind).OrderByDescending(g => g.Count()).ToList();
        string main = kinds[0].Key;
        var sb = new StringBuilder();
        sb.Append(L.F("За неделю: {0}, за месяц: {1}.", week.Count, month.Count)).Append(' ');
        if (fatal > 0) sb.Append(L.F("Неисправленных (могли вызвать сбой): {0}.", fatal)).Append(' ');
        else sb.Append(L.T("Все исправлены самим оборудованием — на работу это пока не повлияло.")).Append(' ');
        sb.Append(L.F("Источник: {0}.", string.Join(", ", kinds.Select(g =>
        {
            string dev = g.Key == "pcie" ? g.Select(w => w.Device).FirstOrDefault(x => x.Length > 0) ?? "" : "";
            return KindName(g.Key) + (dev.Length > 0 ? " (" + dev + ")" : "") + " — " + g.Count();
        })))).Append(' ');
        sb.Append(L.F("Последняя — {0}.", When(week.Max(w => w.Time)))).Append(' ');

        bool profileOn = !laptop && mem != null && mem.Kind is MemKind.AboveOfficial or MemKind.AbovePartial;
        string link = "", linkText = "";
        switch (main)
        {
            case "cpu":
                sb.Append(laptop ? L.T("Обычно это перегрев или сбой питания процессора: проверьте охлаждение и обновите BIOS.")
                                 : L.T("Чаще всего это нестабильный разгон или понижение напряжения процессора (андервольт, Curve Optimizer, PBO). Верните настройки процессора в BIOS по умолчанию и посмотрите, повторяются ли ошибки."));
                if (Regex.IsMatch(cpu ?? "", @"i[579]-1[34]\d{3}", RegexOptions.IgnoreCase))
                    sb.Append(' ').Append(L.T("Для Core 13-го и 14-го поколения Intel выпустила исправление нестабильности в обновлениях BIOS (микрокод 0x12B и новее) — обновите BIOS платы."));
                break;
            case "memory":
                sb.Append(L.T("Проверьте память средством Windows (вкладка «Диагностика», «Проверка памяти»); если ошибки подтвердятся — проверяйте планки по одной."));
                break;
            case "pcie":
                sb.Append(fatal > 0 || week.Count >= 50
                    ? L.T("Ошибки на шине PCIe: часто это плохой контакт видеокарты или NVMe-диска в слоте, райзер-кабель или энергосбережение шины. Проверьте, плотно ли сидит карта (райзер на время уберите), обновите BIOS и драйвер чипсета.")
                    : L.T("Единичные исправленные ошибки шины PCIe обычно безвредны. Если их станет больше — обновите BIOS и драйвер чипсета, проверьте, плотно ли сидит видеокарта."));
                break;
            default:
                sb.Append(L.T("Обновите BIOS и драйвер чипсета; если включён разгон — проверьте без него."));
                break;
        }
        if (profileOn && main is "cpu" or "memory" or "other")
        {
            sb.Append(' ').Append(L.T("Память сейчас работает выше штатной скорости процессора — включён профиль XMP/EXPO или разгон. Сначала проверьте без него."));
            link = "sys:Совет по апгрейду"; linkText = L.T("Про профиль XMP/EXPO и его риски — в разделе «Совет по апгрейду»");
        }
        var level = fatal > 0 ? Severity.Bad : week.Count >= 50 ? Severity.Warn : Severity.Info;
        string title = fatal > 0 ? L.F("Аппаратные ошибки: {0}", KindName(main)) : L.F("Исправленные аппаратные ошибки: {0}", KindName(main));
        return new DiagFinding { Level = level, Title = title, Detail = sb.ToString().Trim(), Count = week.Count.ToString(), Kind = LogMarks.Whea, Link = link, LinkText = linkText };
    }

    public static List<DiagItem> Whea(AppSettings settings)
    {
        if (Demo) return DemoWhea();
        int days = Diagnostics.Days;
        var since = LogMarks.Since(LogMarks.Whea, days, settings);
        var month = ReadWhea(LogMarks.Since(LogMarks.Whea, 30, settings));
        var week = month.Where(w => w.Time >= since).ToList();
        string period = LogMarks.SeenWithin(LogMarks.Whea, days, settings) is DateTime seen ? L.F("{0} — новых нет", LogMarks.SeenText(seen)) : L.F("за {0} дней", days);
        MemVerdict mem = null;
        bool lap = false;
        string cpu = "";
        if (week.Count > 0)
        {
            try { lap = HardwareChecks.IsLaptop(); } catch { }
            try { cpu = MemoryProfile.CpuName(); } catch { }
            if (!lap && week.Any(w => w.Kind != "pcie")) { try { mem = MemoryProfile.Speed(MemoryProfile.Current()); } catch { } }
        }
        var f = WheaFinding(week, month, mem, cpu, lap, period);
        if (f == null && LogMarks.SeenWithin(LogMarks.Whea, days, settings) != null)
            f = new DiagFinding { Level = Severity.Ok, Title = L.T("Аппаратных ошибок нет"), Detail = period };
        return f == null ? new List<DiagItem>() : new List<DiagItem> { f };
    }

    /// <summary>Одна строка для первичного осмотра: виды аппаратных ошибок за неделю и совет — тем же выводом, что в «Диагностике».</summary>
    public static string WheaScanText(int days)
    {
        try
        {
            var since = LogMarks.Since(LogMarks.Whea, days);
            var month = ReadWhea(LogMarks.Since(LogMarks.Whea, 30));
            var week = month.Where(w => w.Time >= since).ToList();
            if (week.Count == 0) return null;
            bool lap = HardwareChecks.IsLaptop();
            MemVerdict mem = null;
            if (!lap && week.Any(w => w.Kind != "pcie")) { try { mem = MemoryProfile.Speed(MemoryProfile.Current()); } catch { } }
            return WheaFinding(week, month, mem, MemoryProfile.CpuName(), lap, "")?.Detail;
        }
        catch { return null; }
    }

    // ================================================================== 3. вылеты игр
    public static List<CrashEvent> ReadCrashes(DateTime since)
    {
        var list = new List<CrashEvent>();
        try
        {
            string q = $"*[System[((Provider[@Name='Application Error'] and EventID=1000) or (Provider[@Name='Application Hang'] and EventID=1002)) and {LogMarks.TimeFrom(since)}]]";
            using var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, q) { ReverseDirection = true });
            for (EventRecord e = reader.ReadEvent(); e != null && list.Count < 1000; e = reader.ReadEvent())
                using (e)
                {
                    try
                    {
                        var p = e.Properties;
                        string S(int i) => p.Count > i ? Convert.ToString(p[i].Value, CultureInfo.InvariantCulture) ?? "" : "";
                        var c = new CrashEvent { Time = e.TimeCreated ?? DateTime.MinValue, App = S(0), Hang = e.Id == 1002 };
                        if (c.Hang) c.Path = S(5);
                        else { c.Module = S(3); c.Exception = S(6); c.Path = S(10); }
                        list.Add(c);
                    }
                    catch { }
                }
        }
        catch (Exception ex) { Log.Error("стабильность: вылеты", ex); }
        return list;
    }

    private static readonly string[] NotGameParts = { "crashreport", "crashhandler", "crashpad", "unitycrashhandler", "setup", "uninst", "installer", "updater", "launcher", "webhelper", "cefprocess" };

    /// <summary>Определитель «это игра?» по пути файла: магазины, найденные поиском, добавленные вручную.</summary>
    public static Func<string, string, string> GameMatcher()
    {
        var dirs = new List<(string Dir, string Title)>();
        try { foreach (var g in GameLibrary.Load()) if (!string.IsNullOrEmpty(g.InstallDir) && !string.IsNullOrEmpty(g.Title)) dirs.Add((g.InstallDir.TrimEnd('\\') + "\\", g.Title)); } catch { }
        try { foreach (var g in GameFinder.Found()) { if (g.Dir.Length > 3) dirs.Add((g.Dir.TrimEnd('\\') + "\\", g.Title)); } } catch { }
        try { foreach (var g in GameDetector.CustomList()) { var d = Path.GetDirectoryName(g.Exe); if (!string.IsNullOrEmpty(d) && CustomGame.FolderUsable(d)) dirs.Add((d.TrimEnd('\\') + "\\", g.Name)); } } catch { }
        dirs = dirs.Where(d => d.Dir.Length > 4).OrderByDescending(d => d.Dir.Length).ToList();
        return (path, app) => MatchGame(path, app, dirs);
    }

    /// <summary>Чистая функция: название игры по пути вылетевшей программы; null — не игра.</summary>
    public static string MatchGame(string path, string app, IList<(string Dir, string Title)> dirs)
    {
        string exe = (string.IsNullOrEmpty(app) ? Path.GetFileName(path ?? "") : app).ToLowerInvariant();
        if (exe.Length == 0 || NotGameParts.Any(exe.Contains)) return null;
        if (!string.IsNullOrEmpty(path))
        {
            foreach (var (dir, title) in dirs)
                if (path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return title;
            try { if (GameDetector.LooksLikeGame(path)) return Path.GetFileNameWithoutExtension(path); } catch { }
        }
        return null;
    }

    /// <summary>Совет по модулю и коду исключения вылета.</summary>
    public static (string Text, DriverRole Role) CrashAdvice(CrashEvent c, bool lowMemory)
    {
        if (c.Hang) return (L.T("Игра перестала отвечать и была закрыта. Частые причины — драйвер видеокарты, нехватка памяти или долгая подгрузка с медленного диска."), new DriverRole { Kind = "hang" });
        string exc = (c.Exception ?? "").ToLowerInvariant();
        if (lowMemory || exc.Contains("c0000017"))
            return (L.T("В это время Windows сообщала о нехватке памяти — игре её не хватило. Закройте лишние программы перед игрой и проверьте файл подкачки (он должен быть включён)."),
                    new DriverRole { Kind = "memory", Link = "sys:Файл подкачки", LinkText = L.T("Проверить — в разделе «Файл подкачки»") });
        var role = DriverNames.Describe(c.Module);
        bool own = c.Module.Length > 0 && string.Equals(Path.GetFileNameWithoutExtension(c.Module), Path.GetFileNameWithoutExtension(c.App), StringComparison.OrdinalIgnoreCase);
        if (own) role = new DriverRole { Kind = "game", Role = L.T("файл самой игры"), Advice = DriverNames.Describe("kernelbase.dll").Advice };
        string text = c.Module.Length > 0 ? L.F("Сбой в {0} — это {1}.", c.Module, role.Role) + " " + role.Advice : role.Advice;
        return (text, role);
    }

    /// <summary>Вывод о вылетах игр (чистая функция): по одной строке на игру, не больше трёх.</summary>
    public static List<DiagItem> CrashFindings(IList<CrashEvent> crashes, Func<DateTime, bool> lowMemoryAt)
    {
        var res = new List<DiagItem>();
        foreach (var g in crashes.Where(c => c.Game.Length > 0).GroupBy(c => c.Game, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(3))
        {
            var items = g.OrderByDescending(c => c.Time).ToList();
            var last = items[0];
            // Самый частый модуль среди вылетов (не зависаний) — по нему совет.
            var byModule = items.Where(c => !c.Hang && c.Module.Length > 0).GroupBy(c => c.Module, StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Count()).FirstOrDefault();
            var basis = byModule?.First() ?? last;
            var (advice, role) = CrashAdvice(basis, lowMemoryAt != null && items.Any(c => lowMemoryAt(c.Time)));
            int hangs = items.Count(c => c.Hang);
            string head = L.F("Последний — {0}.", When(last.Time)) + (hangs > 0 && hangs < items.Count ? " " + L.F("Из них зависаний: {0}.", hangs) : "");
            if (byModule != null && byModule.Count() > 1 && byModule.Count() < items.Count && role.Kind is not ("memory" or "hang"))
                advice = L.F("Чаще всего (в {0} из {1}) сбой в {2} — это {3}.", byModule.Count(), items.Count, byModule.Key, role.Role) + " " + role.Advice;
            var f = new DiagFinding
            {
                Level = Severity.Warn, Title = L.F("Вылеты игры: {0}", g.Key), Count = items.Count.ToString(), Kind = LogMarks.Crash,
                Detail = head + " " + advice,
            };
            if (role.Link.Length > 0) { f.Link = role.Link; f.LinkText = role.LinkText; }
            res.Add(f);
        }
        return res;
    }

    /// <summary>Времена, когда Windows сообщала о нехватке памяти (Resource-Exhaustion-Detector 2004).</summary>
    private static List<DateTime> LowMemoryTimes(DateTime since)
    {
        var list = new List<DateTime>();
        try
        {
            string q = $"*[System[Provider[@Name='Microsoft-Windows-Resource-Exhaustion-Detector'] and (EventID=2004) and {LogMarks.TimeFrom(since)}]]";
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, q));
            for (EventRecord e = reader.ReadEvent(); e != null && list.Count < 500; e = reader.ReadEvent()) using (e) if (e.TimeCreated is DateTime t) list.Add(t);
        }
        catch { }
        return list;
    }

    /// <summary>
    /// Вылеты игр за период «Диагностики». gameApps — имена файлов игр (для строки «Падения программ», чтобы не повторять то же самое).
    /// </summary>
    public static List<DiagItem> GameCrashes(AppSettings settings, out HashSet<string> gameApps)
    {
        gameApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Demo) return DemoCrashes(out gameApps);
        try
        {
            var since = LogMarks.Since(LogMarks.Crash, Diagnostics.Days, settings);
            var crashes = ReadCrashes(since);
            if (crashes.Count == 0) return new List<DiagItem>();
            var match = GameMatcher();
            foreach (var c in crashes) c.Game = match(c.Path, c.App) ?? "";
            foreach (var c in crashes.Where(c => c.Game.Length > 0)) gameApps.Add(c.App);
            if (gameApps.Count == 0) return new List<DiagItem>();
            var low = LowMemoryTimes(since.AddMinutes(-15));
            return CrashFindings(crashes, t => low.Any(x => Math.Abs((x - t).TotalMinutes) <= 10));
        }
        catch (Exception ex) { Log.Error("стабильность: вылеты игр", ex); return new List<DiagItem>(); }
    }

    // ================================================================== 4. целостность Windows и перезагрузка
    /// <summary>Разбор вывода «DISM /English /Online /Cleanup-Image /CheckHealth» (на всякий случай — и русского).</summary>
    public static DismState ParseDism(int code, string output)
    {
        string o = (output ?? "").ToLowerInvariant();
        if (code == 740 || o.Contains("error: 740") || o.Contains("elevated permissions") || o.Contains("требуются повышенные")) return DismState.NeedAdmin;
        if (o.Contains("no component store corruption detected") || o.Contains("повреждение хранилища компонентов не обнаружено") || o.Contains("не обнаружено повреждени")) return DismState.Healthy;
        if (o.Contains("cannot be repaired") || o.Contains("невозможно восстановить") || o.Contains("не подлежит восстановлению")) return DismState.NotRepairable;
        if (o.Contains("component store is repairable") || o.Contains("подлежит восстановлению") || o.Contains("можно восстановить")) return DismState.Repairable;
        return code == 0 ? DismState.Unknown : DismState.Failed;
    }

    /// <summary>Команды для восстановления, которые человек запускает сам (программа их не выполняет).</summary>
    public const string RepairCommands = "DISM /Online /Cleanup-Image /RestoreHealth\r\nsfc /scannow";

    public static string DismText(DismResult r) => r.State switch
    {
        DismState.Healthy => L.F("Хранилище компонентов Windows не повреждено (проверено {0:d MMMM, HH:mm}).", r.At),
        DismState.Repairable => L.T("Хранилище компонентов Windows повреждено, но его можно восстановить. Откройте «Терминал (администратор)» и выполните по очереди две команды ниже — это займёт 10–30 минут, нужен интернет. Программа их сама не запускает."),
        DismState.NotRepairable => L.T("Хранилище компонентов Windows повреждено так, что само не восстанавливается. Попробуйте команды ниже; если не помогут — нужна переустановка Windows поверх («Параметры → Система → Восстановление → Исправить проблемы с помощью Центра обновления Windows»)."),
        DismState.NeedAdmin => L.T("Проверку целостности Windows можно запустить только с правами администратора."),
        DismState.Failed => L.F("Проверка не удалась: {0}", r.Note.Length > 0 ? r.Note : L.T("DISM вернул ошибку")),
        _ => L.T("DISM не сообщил понятного результата."),
    };

    /// <summary>Запустить DISM /CheckHealth (только чтение: смотрит отметку о повреждении, ничего не исправляет). Вызывать в фоне.</summary>
    public static DismResult RunDism()
    {
        var r = new DismResult { At = DateTime.Now };
        if (!Shell.IsAdmin()) { r.State = DismState.NeedAdmin; return r; }
        try
        {
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe");
            var (code, output) = Shell.Run(exe, "/English /Online /Cleanup-Image /CheckHealth", 180000);
            r.State = ParseDism(code, output);
            if (r.State == DismState.Failed) r.Note = (output ?? "").Split('\n').Select(x => x.Trim()).LastOrDefault(x => x.StartsWith("Error", StringComparison.OrdinalIgnoreCase)) ?? L.F("код {0}", code);
        }
        catch (Exception ex) { r.State = DismState.Failed; r.Note = ex.Message; }
        return r;
    }

    public static void SaveDism(AppSettings s, DismResult r)
    {
        if (s == null || r == null || r.State is DismState.NeedAdmin) return;
        try { s.Set(DismKey, JsonSerializer.Serialize(r)); s.Save(); } catch { }
    }

    public static DismResult LastDism(AppSettings s)
    {
        try { string j = s?.Get(DismKey) ?? ""; return j.Length == 0 ? null : JsonSerializer.Deserialize<DismResult>(j); } catch { return null; }
    }

    public static DiagFinding DismFinding(DismResult r)
    {
        if (r == null || (DateTime.Now - r.At).TotalDays > 30) return null;
        return r.State switch
        {
            DismState.Healthy => new DiagFinding { Level = Severity.Ok, Title = L.T("Целостность Windows в порядке"), Detail = L.F("проверено {0:d MMMM}", r.At) },
            DismState.Repairable or DismState.NotRepairable => new DiagFinding
            {
                Level = r.State == DismState.Repairable ? Severity.Warn : Severity.Bad, Title = L.T("Файлы Windows повреждены"),
                Detail = r.State == DismState.Repairable
                    ? L.F("Хранилище компонентов Windows повреждено, но его можно восстановить: выполните в «Терминале (администратор)» по очереди {0} и {1} (10–30 минут, нужен интернет). Команды можно скопировать слева, в блоке «Целостность Windows».", "DISM /Online /Cleanup-Image /RestoreHealth", "sfc /scannow")
                    : DismText(r) + " " + L.T("Команды:") + " DISM /Online /Cleanup-Image /RestoreHealth; sfc /scannow.",
            },
            _ => null,
        };
    }

    /// <summary>Ждёт ли Windows перезагрузки: обновление, установка компонентов, отложенная замена файлов.</summary>
    public static (bool Update, bool Components, int Renames) ReadPendingReboot()
    {
        bool upd = false, cbs = false; int renames = 0;
        try
        {
            using (var a = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")) upd = a != null;
            using (var b = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")) cbs = b != null;
            using var sm = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            if (sm?.GetValue("PendingFileRenameOperations") is string[] ops) renames = CountRenames(ops);
        }
        catch { }
        return (upd, cbs, renames);
    }

    /// <summary>PendingFileRenameOperations — пары строк «откуда, куда»; считаем операции с непустым источником.</summary>
    public static int CountRenames(string[] ops)
    {
        int n = 0;
        for (int i = 0; i + 1 <= ops.Length; i += 2) if (!string.IsNullOrWhiteSpace(ops[i])) n++;
        return n;
    }

    public static DiagFinding RebootFinding(bool update, bool components, int renames)
    {
        if (update || components)
            return new DiagFinding
            {
                Level = Severity.Warn, Title = L.T("Windows ждёт перезагрузки"),
                Detail = (update ? L.T("Обновление Windows закончится только после перезагрузки.") : L.T("Установка компонентов Windows закончится только после перезагрузки."))
                         + (renames > 0 ? " " + L.F("Ещё ждут замены файлов: {0}.", renames) : ""),
            };
        if (renames > 0)
            return new DiagFinding
            {
                Level = Severity.Info, Title = L.T("После перезагрузки Windows заменит файлы"), Count = renames.ToString(),
                Detail = L.T("Это оставили установщики программ или драйверов: файлы были заняты и заменятся при следующем запуске Windows. Делать ничего не нужно — перезагрузите компьютер, когда будет удобно."),
            };
        return null;
    }

    // ================================================================== 5. драйвер стал хуже
    public sealed class Failure { public DateTime Time; public string Kind = ""; }

    /// <summary>
    /// Чистая функция: сбоев после установки драйвера заметно больше, чем до неё. Сравниваются одинаковые отрезки времени
    /// до и после установки (до 30 дней); если в обоих есть игры (sessions) — частота на час игры, иначе на день.
    /// </summary>
    public static DiagFinding DriverWorseFinding(DateTime installed, string version, IList<Failure> failures, IList<(DateTime Start, double Hours)> sessions, DateTime now)
    {
        double afterDays = (now - installed).TotalDays;
        if (afterDays < 1 || afterDays > 45) return null;
        double window = Math.Min(afterDays, 30);
        var from = installed.AddDays(-window);
        var before = failures.Where(f => f.Time >= from && f.Time < installed).ToList();
        var after = failures.Where(f => f.Time >= installed && f.Time <= now).ToList();
        if (after.Count < 3) return null;
        double hb = sessions.Where(s => s.Start >= from && s.Start < installed).Sum(s => s.Hours);
        double ha = sessions.Where(s => s.Start >= installed && s.Start <= now).Sum(s => s.Hours);
        bool perHour = hb >= 1 && ha >= 1;
        double rb = before.Count / (perHour ? hb : window), ra = after.Count / (perHour ? ha : afterDays);
        if (before.Count > 0 && ra < 2 * rb) return null;
        string Count(IList<Failure> l)
        {
            var parts = new List<string>();
            int r = l.Count(f => f.Kind == "reset"), b = l.Count(f => f.Kind == "bsod"), c = l.Count(f => f.Kind == "crash");
            if (r > 0) parts.Add(Fmt.Plural(r, "сброс драйвера", "сброса драйвера", "сбросов драйвера"));
            if (b > 0) parts.Add(Fmt.Plural(b, "синий экран", "синих экрана", "синих экранов"));
            if (c > 0) parts.Add(Fmt.Plural(c, "вылет игры", "вылета игры", "вылетов игр"));
            return parts.Count == 0 ? L.T("ни одного") : string.Join(", ", parts);
        }
        string span(double d) => Fmt.Plural((int)Math.Max(1, Math.Round(d)), "день", "дня", "дней");
        string detail = L.F("Драйвер {0} стоит с {1}. За {2} до этого: {3}; за {4} после: {5}.", version, Day(installed), span(window), Count(before), span(afterDays), Count(after));
        if (perHour) detail += " " + L.F("На час игры: было {0:0.##}, стало {1:0.##}.", rb, ra);
        detail += " " + L.T("Похоже, с новым драйвером стало хуже. Верните прошлую версию: «Диспетчер устройств» → «Видеоадаптеры» → ваша видеокарта → «Свойства» → «Драйвер» → «Откатить», или скачайте прошлую версию на сайте производителя. Программа сама драйвер не откатывает.");
        return new DiagFinding { Level = Severity.Warn, Title = L.T("После обновления драйвера видеокарты сбоев стало больше"), Detail = detail, Count = after.Count.ToString(), Link = "sys:Драйвер", LinkText = L.T("Версия драйвера и где его скачать — в разделе «Драйвер»") };
    }

    private static DiagFinding DriverWorse(IReadOnlyList<GameSession> sessions)
    {
        var main = GpuDriver.ReadAdapters().FirstOrDefault(a => !a.Integrated) ?? GpuDriver.ReadAdapters().FirstOrDefault();
        if (main == null) return null;
        var installed = GpuDriver.InstalledAt(main.Service, 4000);
        if (installed == null || (DateTime.Now - installed.Value).TotalDays > 45 || (DateTime.Now - installed.Value).TotalDays < 1) return null;
        var since = installed.Value.AddDays(-31);
        var fails = new List<Failure>();
        try
        {
            string q = $"*[System[Provider[@Name='Display'] and (EventID=4101) and {LogMarks.TimeFrom(since)}]]";
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, q));
            for (EventRecord e = reader.ReadEvent(); e != null && fails.Count < 2000; e = reader.ReadEvent()) using (e) if (e.TimeCreated is DateTime t) fails.Add(new Failure { Time = t, Kind = "reset" });
        }
        catch { }
        foreach (var b in ReadBsods(since)) fails.Add(new Failure { Time = b.Time, Kind = "bsod" });
        var crashes = ReadCrashes(since);
        if (crashes.Count > 0)
        {
            var match = GameMatcher();
            foreach (var c in crashes) if (!c.Hang && match(c.Path, c.App) != null) fails.Add(new Failure { Time = c.Time, Kind = "crash" });
        }
        var ss = (sessions ?? Array.Empty<GameSession>()).Select(s => (s.Start, s.Duration.TotalHours)).ToList();
        return DriverWorseFinding(installed.Value, main.Version, fails, ss, DateTime.Now);
    }

    // ================================================================== 6. загрузка Windows
    /// <summary>Чистая функция: тренд времени загрузки и кто её затягивает (по всем замеренным загрузкам).</summary>
    public static DiagFinding BootFinding(BootReport r)
    {
        if (r == null || r.Error != null || r.Boots.Count == 0) return null;
        var boots = r.Boots;     // старые первыми
        var last = boots[^1];
        string Sec(double s) => s.ToString("0", L.Culture) + Nb + L.Pick("с", "s");
        var repeated = (r.SlowAll ?? new List<BootSlowStat>()).Where(x => x.Boots >= 2 || boots.Count < 3).OrderByDescending(x => x.Boots * x.AvgSeconds).Take(3).ToList();
        string Culprits() => repeated.Count == 0 ? "" : " " + L.F("Чаще всего загрузку затягивает: {0}.", string.Join("; ", repeated.Select(x =>
            Q(x.Name) + " (" + x.Kind + ", " + (boots.Count >= 2 ? L.F("в {0} из {1}", x.Boots, Fmt.Plural(boots.Count, "загрузки", "загрузок", "загрузок")) + ", " : "") + "+" + x.AvgSeconds.ToString("0.#", L.Culture) + Nb + L.Pick("с", "s") + ")")));
        bool app = repeated.Any(x => x.Kind == L.T("программа"));
        string advice = repeated.Count == 0 ? "" : " " + (app ? L.T("Программы, которые не нужны сразу после включения, уберите из автозагрузки.") : L.T("Драйверы и службы из списка обновите или удалите, если устройство и программа не нужны."));

        if (boots.Count >= 4)
        {
            int recentN = Math.Min(3, boots.Count / 2);
            double recent = boots.TakeLast(recentN).Average(b => b.Seconds);
            double earlier = boots.Take(boots.Count - recentN).Average(b => b.Seconds);
            if (recent >= earlier * 1.3 && recent - earlier >= 10)
                return new DiagFinding
                {
                    Level = Severity.Warn, Title = L.T("Windows стала загружаться дольше"), Count = Sec(recent),
                    Detail = L.F("Раньше — около {0}, в последних {1} — около {2}.", Sec(earlier), Fmt.Plural(recentN, "загрузке", "загрузках", "загрузках"), Sec(recent)) + Culprits() + advice,
                    Link = app ? "sys:Автозагрузка" : "", LinkText = app ? L.T("Убрать — в разделе «Автозагрузка»") : "",
                };
        }
        if (last.Seconds >= 90)
            return new DiagFinding
            {
                Level = Severity.Info, Title = L.T("Windows загружается долго"), Count = Sec(last.Seconds),
                Detail = L.F("Последняя замеренная загрузка ({0}) — {1}: рабочий стол через {2}, догрузка программ ещё {3}.", Day(last.At), Sec(last.Seconds), Sec(last.MainSeconds), Sec(last.PostSeconds)) + Culprits() + advice,
                Link = app ? "sys:Автозагрузка" : "", LinkText = app ? L.T("Убрать — в разделе «Автозагрузка»") : "",
            };
        return new DiagFinding { Level = Severity.Ok, Title = L.T("Загрузка Windows без замедления"), Detail = L.F("последняя — {0}, в среднем {1}", Sec(last.Seconds), Sec(boots.Average(b => b.Seconds))) };
    }

    // ================================================================== 7. проверка памяти
    public static MemTestResult ReadMemTest()
    {
        try
        {
            string q = "*[System[Provider[@Name='Microsoft-Windows-MemoryDiagnostics-Results']]]";
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, q) { ReverseDirection = true });
            using var e = reader.ReadEvent();
            if (e == null) return null;
            return MemTestFrom(e.Id, e.Level ?? 4, e.TimeCreated ?? DateTime.MinValue);
        }
        catch { return null; }
    }

    /// <summary>1101 и 1201 — ошибок нет; 1102 и 1202 — найдены ошибки. Неизвестный номер — по уровню записи.</summary>
    public static MemTestResult MemTestFrom(int id, byte level, DateTime time) => new()
    {
        Time = time,
        Errors = id is 1102 or 1202 || (id is not (1101 or 1201) && level is 1 or 2),
    };

    public static DiagFinding MemTestFinding(MemTestResult r, bool laptop, DateTime now)
    {
        if (r == null) return null;
        double age = (now - r.Time).TotalDays;
        if (r.Errors && age <= 90)
            return new DiagFinding
            {
                Level = Severity.Bad, Title = L.T("Проверка памяти нашла ошибки"),
                Detail = L.F("Средство проверки памяти Windows {0} нашло аппаратные ошибки.", Day(r.Time)) + " " +
                         (laptop ? L.T("В ноутбуке это обычно неисправная планка: обратитесь в сервис или замените память, если она съёмная.")
                                 : L.T("Если включён профиль XMP/EXPO или разгон памяти — выключите его в BIOS и проверьте ещё раз. Если ошибки остаются — проверяйте планки по одной и замените неисправную.")),
                Link = laptop ? "" : "sys:Совет по апгрейду", LinkText = laptop ? "" : L.T("Про профиль XMP/EXPO и его риски — в разделе «Совет по апгрейду»"),
            };
        if (!r.Errors && age <= 30)
            return new DiagFinding { Level = Severity.Ok, Title = L.T("Проверка памяти: ошибок нет"), Detail = L.F("средство Windows, {0}", Day(r.Time)) };
        return null;
    }

    /// <summary>Открыть «Средство проверки памяти Windows» (оно само предложит перезагрузиться сейчас или при следующем включении).</summary>
    public static string StartMemTest(AppSettings s)
    {
        try
        {
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mdsched.exe");
            if (!File.Exists(exe)) return L.T("Средство проверки памяти Windows не найдено.");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose();
            s?.Set(MemAskedKey, DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
            s?.Save();
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    // ================================================================== всё вместе для «Диагностики»
    /// <summary>
    /// Остальные выводы о стабильности (кроме синих экранов, WHEA и вылетов — у них свои места в Diagnostics.Run):
    /// ожидание перезагрузки, драйвер стал хуже, загрузка Windows, проверка памяти, задержки драйверов, целостность Windows.
    /// </summary>
    public static List<DiagItem> Diagnose(AppSettings settings)
    {
        var s = settings ?? host?.Settings ?? LogMarks.Settings ?? AppSettings.Load();
        if (Demo) return DemoOther();
        var res = new List<DiagItem>();
        void Try(Func<DiagFinding> f, string what) { try { var x = f(); if (x != null) res.Add(x); } catch (Exception ex) { Log.Error("стабильность: " + what, ex); } }
        Try(() => { var (u, c, n) = ReadPendingReboot(); return RebootFinding(u, c, n); }, "перезагрузка");
        Try(() => DriverWorse(host?.Sessions() ?? SafeSessions()), "драйвер стал хуже");
        Try(() => BootFinding(BootPerf.Read()), "загрузка Windows");
        Try(() => MemTestFinding(ReadMemTest(), HardwareChecks.IsLaptop(), DateTime.Now), "проверка памяти");
        Try(() => { var d = DpcLatency.Last(s); return d != null && (DateTime.Now - d.At).TotalDays <= 14 ? DpcLatency.Finding(d) : null; }, "задержки драйверов");
        Try(() => DismFinding(LastDism(s)), "целостность Windows");
        return res;
    }

    private static IReadOnlyList<GameSession> SafeSessions() { try { return SessionStore.Load(); } catch { return Array.Empty<GameSession>(); } }

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

    // ================================================================== самопроверка
    /// <summary>PulsePC.exe --feature-test StabilityChecks файл.txt — разбор на подставных данных и что видно на этом ПК (только чтение).</summary>
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        string fail = null;
        int n = 0;
        bool en = L.En;
        void Check(string what, bool ok) { n++; sb.AppendLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) fail ??= what; }
        var now = new DateTime(2026, 11, 21, 12, 0, 0);

        try
        {
            // --- 1. синие экраны: разбор строки события и подставной малый дамп
            sb.AppendLine("Синие экраны:");
            Check("строка события 1001 разбирается: код 0x133 и четыре параметра",
                ParseBugcheck("0x00000133 (0x0000000000000001, 0x0000000000001e00, 0xfffff80512345678, 0x0000000000000000)", out var code, out var p) && code == 0x133 && p[0] == 1 && p[2] == 0xfffff80512345678);
            Check("мусор вместо кода — не синий экран", !ParseBugcheck("нет данных", out _, out _) && !ParseBugcheck("", out _, out _));
            var mods = new[] { ("ntoskrnl.exe", 0xFFFFF80500000000UL, 0x1000000u), ("nvlddmkm.sys", 0xFFFFF80620000000UL, 0x3000000u), ("ndis.sys", 0xFFFFF80610000000UL, 0x200000u) };
            var dump = new MinidumpInfo();
            Minidump.Parse(Minidump.BuildFake(0x116, new[] { 0xFFFFC00012345678UL, 0xFFFFF80620123456UL, 0UL, 0UL }, mods), dump);
            Check($"малый дамп 0x116: код, 3 драйвера, виновник по параметру — nvlddmkm.sys (получено «{dump.Culprit}», ошибка {dump.Error ?? "нет"})",
                dump.Error == null && dump.Code == 0x116 && dump.Modules.Count == 3 && dump.Culprit == "nvlddmkm.sys" && !dump.CulpritFromStack && dump.Time.Year == 2026);
            var dump2 = new MinidumpInfo();
            Minidump.Parse(Minidump.BuildFake(0x133, new[] { 1UL, 0x1E00UL, 0xFFFFF80500ABCDEFUL, 0UL }, mods, new[] { 0x10UL, 0xFFFFF80500001000UL, 0xFFFFF80610001234UL, 0xFFFFF80620000010UL }), dump2);
            Check($"малый дамп 0x133: в параметрах только ядро — виновник по стеку ndis.sys, «вероятно» (получено «{dump2.Culprit}»)",
                dump2.Culprit == "ndis.sys" && dump2.CulpritFromStack);
            var bad = new MinidumpInfo();
            Minidump.Parse(new byte[100], bad);
            Check("не дамп — честная ошибка, без исключения", bad.Error != null && bad.Culprit == "");
            // Усечённый и испорченный дамп: разбор не падает ни на одной длине и ни на одном испорченном байте заголовка и списка.
            string fuzz = "";
            try
            {
                var whole = Minidump.BuildFake(0x133, new[] { 1UL, 0x1E00UL, 0xFFFFF80500ABCDEFUL, 0UL }, mods, new[] { 0xFFFFF80610001234UL });
                for (int len = 0; len <= whole.Length && fuzz == ""; len += 7)
                    try { Minidump.Parse(whole.AsSpan(0, len).ToArray(), new MinidumpInfo()); } catch (Exception ex) { fuzz = $"длина {len}: {ex.GetType().Name}"; }
                var rnd = new Random(7);
                for (int i = 0; i < 3000 && fuzz == ""; i++)
                {
                    var copy = (byte[])whole.Clone();
                    for (int k = 0; k < 8; k++) copy[rnd.Next(k < 5 ? 0x2000 : 0x38, copy.Length)] = (byte)rnd.Next(256);   // больше — в разделе triage и списке драйверов
                    try { Minidump.Parse(copy, new MinidumpInfo()); } catch (Exception ex) { fuzz = $"порча №{i}: {ex.GetType().Name}"; }
                }
            }
            catch (Exception ex) { fuzz = ex.Message; }
            Check("усечённый и испорченный дамп — без исключения" + (fuzz != "" ? $" ({fuzz})" : ""), fuzz == "");
            var ev = new BsodEvent { Time = new DateTime(2026, 11, 20, 21, 14, 30), Code = 0x116, DumpPath = @"C:\Windows\Minidump\112026-1.dmp" };
            dump.File = @"C:\Windows\Minidump\112026-1.dmp";
            var bf = BsodFinding(new[] { ev }, new[] { ev }, new[] { dump }, false, "");
            Check("вывод: «Синие экраны», код с названием, виновник nvlddmkm.sys, совет и ссылка на «Драйвер» — " + bf.Detail,
                bf.Level == Severity.Bad && bf.Kind == LogMarks.Bsod && bf.Count == "1" && bf.Detail.Contains("0x116") && bf.Detail.Contains("VIDEO_TDR_FAILURE") && bf.Detail.Contains("nvlddmkm.sys") && bf.Link == "sys:Драйвер");
            var nf = BsodFinding(new[] { ev }, new[] { ev }, new List<MinidumpInfo>(), true, "");
            Check("без прав: честно «нужны права администратора»", en || nf.Detail.Contains("только с правами администратора"));
            var older = new BsodEvent { Time = now.AddDays(-20), Code = 0x3B };
            var okf = BsodFinding(new List<BsodEvent>(), new[] { older }, null, false, "за 7 дней");
            Check("за неделю нет, за месяц один — строка «в норме» со сводкой", okf.Level == Severity.Ok && (en || okf.Detail.Contains("за 30 дней — 1")));
            Check("неизвестный код — без выдумок", Code(0x12345).Name == "" && Code(0x12345).Meaning.Length > 0);

            // --- 2. WHEA
            sb.AppendLine("Аппаратные ошибки (WHEA):");
            Check("вид по полям: PCIe, процессор, память", WheaKind(17, new[] { "VendorID" }, "") == "pcie" && WheaKind(19, new[] { "MciStat" }, "") == "cpu" && WheaKind(47, Array.Empty<string>(), "") == "memory" && WheaKind(1, Array.Empty<string>(), "4") == "pcie");
            var cpuW = Enumerable.Range(0, 3).Select(i => new WheaEvent { Time = now.AddDays(-i), Id = 18, Fatal = i == 0, Kind = "cpu" }).ToList();
            var oc = new MemVerdict { Kind = MemKind.AboveOfficial };
            var wf = WheaFinding(cpuW, cpuW, oc, "Intel(R) Core(TM) i9-13900K", false, "");
            Check("неисправленная ошибка процессора на i9-13900K с профилем памяти: серьёзно, совет про разгон, BIOS 0x12B и ссылка на XMP — " + wf.Detail,
                wf.Level == Severity.Bad && wf.Kind == LogMarks.Whea && wf.Link == "sys:Совет по апгрейду" && (en || wf.Detail.Contains("0x12B") && wf.Detail.Contains("XMP/EXPO") && wf.Detail.Contains("За неделю: 3")));
            var lapf = WheaFinding(cpuW, cpuW, oc, "AMD Ryzen 9 8940HX", true, "");
            Check("на ноутбуке — ни XMP, ни разгона", lapf.Link == "" && (en || !lapf.Detail.Contains("XMP")));
            var pcie = Enumerable.Range(0, 5).Select(i => new WheaEvent { Time = now.AddHours(-i), Id = 17, Kind = "pcie", Device = "NVIDIA" }).ToList();
            var pf = WheaFinding(pcie, pcie, oc, "", false, "");
            Check("единичные исправленные PCIe — «к сведению», с устройством, без ссылки на XMP", pf.Level == Severity.Info && pf.Detail.Contains("NVIDIA") && pf.Link == "");
            Check("ничего за месяц — строки нет", WheaFinding(new List<WheaEvent>(), new List<WheaEvent>(), null, "", false, "") == null);

            // --- 3. вылеты игр
            sb.AppendLine("Вылеты игр:");
            var dirs = new List<(string, string)> { (@"D:\SteamLibrary\steamapps\common\PUBG\", "PUBG: BATTLEGROUNDS") };
            Check("игра по папке установки", MatchGame(@"D:\SteamLibrary\steamapps\common\PUBG\TslGame\Binaries\Win64\TslGame.exe", "TslGame.exe", dirs) == "PUBG: BATTLEGROUNDS");
            Check("отчёт о сбое и лаунчер — не игра", MatchGame(@"D:\SteamLibrary\steamapps\common\PUBG\CrashReportClient.exe", "CrashReportClient.exe", dirs) == null);
            Check("браузер — не игра", MatchGame(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "chrome.exe", dirs) == null);
            var crashes = new List<CrashEvent>
            {
                new() { Time = now.AddHours(-1), App = "TslGame.exe", Module = "nvwgf2umx.dll", Exception = "0xc0000005", Game = "PUBG: BATTLEGROUNDS" },
                new() { Time = now.AddHours(-5), App = "TslGame.exe", Module = "nvwgf2umx.dll", Exception = "0xc0000005", Game = "PUBG: BATTLEGROUNDS" },
                new() { Time = now.AddHours(-9), App = "TslGame.exe", Hang = true, Game = "PUBG: BATTLEGROUNDS" },
                new() { Time = now.AddHours(-2), App = "notepad.exe", Module = "ntdll.dll" },
            };
            var cf = CrashFindings(crashes, null);
            Check("одна строка на игру, 3 случая, модуль драйвера NVIDIA и ссылка на «Драйвер» — " + (cf.FirstOrDefault()?.Detail ?? ""),
                cf.Count == 1 && cf[0].Count == "3" && cf[0].Detail.Contains("nvwgf2umx.dll") && ((DiagFinding)cf[0]).Link == "sys:Драйвер" && ((DiagFinding)cf[0]).Kind == LogMarks.Crash);
            var mf = CrashFindings(new[] { new CrashEvent { Time = now, App = "game.exe", Module = "game.exe", Exception = "0xc0000005", Game = "Game" } }, _ => true);
            Check("нехватка памяти в момент вылета — совет про файл подкачки", ((DiagFinding)mf[0]).Link == "sys:Файл подкачки");
            var vf = CrashAdvice(new CrashEvent { App = "x.exe", Module = "d3d12.dll" }, false);
            var ac = CrashAdvice(new CrashEvent { App = "x.exe", Module = "EasyAntiCheat_EOS.dll" }, false);
            var own = CrashAdvice(new CrashEvent { App = "Game-Win64-Shipping.exe", Module = "Game-Win64-Shipping.exe" }, false);
            Check("d3d12.dll — DirectX, античит — античит, сбой в своём файле — «файл самой игры»", vf.Role.Kind == "dx" && ac.Role.Kind == "anticheat" && own.Role.Kind == "game");

            // --- 4. целостность и перезагрузка
            sb.AppendLine("Целостность Windows:");
            Check("DISM: «No component store corruption detected.» — не повреждено", ParseDism(0, "Deployment Image Servicing and Management tool\r\nVersion: 10.0.26100.1\r\n\r\nImage Version: 10.0.26300.1\r\n\r\nNo component store corruption detected.\r\nThe operation completed successfully.") == DismState.Healthy);
            Check("DISM: «The component store is repairable.» — можно восстановить", ParseDism(0, "The component store is repairable.\r\nThe operation completed successfully.") == DismState.Repairable);
            Check("DISM: «cannot be repaired» — не восстанавливается", ParseDism(0, "The component store cannot be repaired.") == DismState.NotRepairable);
            Check("DISM: ошибка 740 — нужны права", ParseDism(740, "Error: 740\r\n\r\nElevated permissions are required to run DISM.") == DismState.NeedAdmin);
            Check("DISM по-русски: «Повреждение хранилища компонентов не обнаружено.»", ParseDism(0, "Повреждение хранилища компонентов не обнаружено.") == DismState.Healthy);
            Check("DISM: непонятный вывод с ошибкой — «не удалось»", ParseDism(87, "Error: 87") == DismState.Failed);
            Check("ожидание перезагрузки: обновление — внимание", RebootFinding(true, false, 2)?.Level == Severity.Warn);
            Check("только замена файлов — к сведению, с числом", RebootFinding(false, false, 3) is { Level: Severity.Info, Count: "3" } && RebootFinding(false, false, 0) == null);
            Check("пары «откуда, куда»: 2 операции", CountRenames(new[] { @"\??\C:\a.tmp", "", @"\??\C:\b.dll", @"\??\C:\c.dll" }) == 2);

            // --- 5. драйвер стал хуже
            sb.AppendLine("Драйвер стал хуже:");
            var inst = now.AddDays(-4);
            var fl = new List<Failure> { new() { Time = now.AddDays(-10), Kind = "reset" } };
            fl.AddRange(Enumerable.Range(0, 6).Select(i => new Failure { Time = now.AddDays(-3).AddHours(i * 9), Kind = i % 2 == 0 ? "reset" : "crash" }));
            var dw = DriverWorseFinding(inst, "621.24", fl, new List<(DateTime, double)>(), now);
            Check("1 сбой за 4 дня до, 6 после — совет вернуть прошлую версию со ссылкой на «Драйвер» — " + dw?.Detail, dw != null && dw.Link == "sys:Драйвер" && dw.Count == "6");
            var same = new List<Failure>(fl.Take(1));
            same.AddRange(Enumerable.Range(0, 5).Select(i => new Failure { Time = inst.AddDays(-3).AddHours(i * 9), Kind = "reset" }));
            same.AddRange(Enumerable.Range(0, 4).Select(i => new Failure { Time = now.AddDays(-3).AddHours(i * 9), Kind = "reset" }));
            Check("до и после примерно поровну — вывода нет", DriverWorseFinding(inst, "621.24", same, new List<(DateTime, double)>(), now) == null);
            Check("драйвер поставлен меньше суток назад — рано судить", DriverWorseFinding(now.AddHours(-5), "621.24", fl, new List<(DateTime, double)>(), now) == null);

            // --- 6. загрузка Windows
            sb.AppendLine("Загрузка Windows:");
            var br = new BootReport { LastBoot = now };
            foreach (var s in new[] { 22.0, 24, 21, 23, 45, 48, 50 }) br.Boots.Add(new BootRecord { At = now.AddDays(-br.Boots.Count), Seconds = s, MainSeconds = s * 0.6, PostSeconds = s * 0.4 });
            br.SlowAll = new List<BootSlowStat> { new() { Kind = L.T("программа"), Name = "Updater", Boots = 3, AvgSeconds = 12 }, new() { Kind = L.T("драйвер"), Name = "Один раз", Boots = 1, AvgSeconds = 30 } };
            var bt = BootFinding(br);
            Check("было ~22 с, стало ~48 с — «стала загружаться дольше», повторяющийся виновник, ссылка на «Автозагрузку» — " + bt?.Detail,
                bt?.Level == Severity.Warn && bt.Detail.Contains("Updater") && !bt.Detail.Contains("Один раз") && bt.Link == "sys:Автозагрузка");
            var flat = new BootReport { Boots = new List<BootRecord> { new() { Seconds = 30 }, new() { Seconds = 28 }, new() { Seconds = 31 }, new() { Seconds = 29 } } };
            Check("ровная загрузка — «в норме»", BootFinding(flat)?.Level == Severity.Ok);
            Check("без прав — вывода нет (левая колонка объясняет сама)", BootFinding(new BootReport { Error = "нужны права" }) == null);

            // --- 7. проверка памяти
            sb.AppendLine("Проверка памяти:");
            Check("1201 — ошибок нет, 1202 — ошибки, 1101 — нет, 1102 — есть", !MemTestFrom(1201, 4, now).Errors && MemTestFrom(1202, 4, now).Errors && !MemTestFrom(1101, 4, now).Errors && MemTestFrom(1102, 2, now).Errors);
            Check("ошибки на ПК — серьёзно, совет про XMP и ссылка", MemTestFinding(new MemTestResult { Time = now.AddDays(-1), Errors = true }, false, now) is { Level: Severity.Bad, Link: "sys:Совет по апгрейду" });
            Check("ошибки на ноутбуке — без XMP", MemTestFinding(new MemTestResult { Time = now.AddDays(-1), Errors = true }, true, now) is { Link: "" } lf && (en || !lf.Detail.Contains("XMP")));
            Check("чисто, но давно — строки нет", MemTestFinding(new MemTestResult { Time = now.AddDays(-60) }, false, now) == null);

            // --- 8. задержки драйверов на подставных записях ETW
            sb.AppendLine("Задержки драйверов (подставные события ETW):");
            var agg = new DpcLatency.Aggregator();
            double freq = 10_000_000;   // 10 МГц: 1 тик = 0,1 мкс
            var recs = new List<IntPtr>
            {
                DpcLatency.FakeRecord(DpcLatency.ImageLoadGuid, 3, 0, DpcLatency.ImagePayload(0xFFFFF80610000000UL, 0x200000, @"\SystemRoot\System32\drivers\ndis.sys")),
                DpcLatency.FakeRecord(DpcLatency.ImageLoadGuid, 3, 0, DpcLatency.ImagePayload(0xFFFFF80620000000UL, 0x3000000, @"\SystemRoot\System32\DriverStore\FileRepository\nv.inf_amd64\nvlddmkm.sys")),
                DpcLatency.FakeRecord(DpcLatency.ImageLoadGuid, 3, 0, DpcLatency.ImagePayload(0x00007FF600000000UL, 0x100000, @"C:\Windows\explorer.exe")),
                DpcLatency.FakeRecord(DpcLatency.PerfInfoGuid, 68, 1_000_000 + 25_000, DpcLatency.DpcPayload(1_000_000, 0xFFFFF80610001000UL)),    // ndis 2500 мкс
                DpcLatency.FakeRecord(DpcLatency.PerfInfoGuid, 68, 2_000_000 + 1_000, DpcLatency.DpcPayload(2_000_000, 0xFFFFF80610002000UL)),     // ndis 100 мкс
                DpcLatency.FakeRecord(DpcLatency.PerfInfoGuid, 67, 3_000_000 + 3_000, DpcLatency.DpcPayload(3_000_000, 0xFFFFF80620000100UL)),     // nvlddmkm ISR 300 мкс
                DpcLatency.FakeRecord(DpcLatency.PerfInfoGuid, 66, 4_000_000 + 500, DpcLatency.DpcPayload(4_000_000, 0xFFFFF80699999999UL)),       // неизвестный 50 мкс
                DpcLatency.FakeRecord(DpcLatency.PerfInfoGuid, 50, 5_000_000, DpcLatency.DpcPayload(1, 0xFFFFF80610001000UL)),                    // не DPC — мимо
            };
            foreach (var r in recs) { DpcLatency.Feed(r, agg, freq); DpcLatency.FreeFake(r); }
            var top = agg.Top();
            Check($"модули ядра: 2 (обычная программа не в счёт), событий 4 (получено {agg.ModuleCount} и {agg.Events})", agg.ModuleCount == 2 && agg.Events == 4);
            Check($"худший — ndis.sys, 2500 мкс, всего 2,6 мс (получено {top.FirstOrDefault()?.Name} {top.FirstOrDefault()?.MaxUs} мкс, {top.FirstOrDefault()?.TotalMs} мс)",
                top.Count == 3 && top[0].Name == "ndis.sys" && Math.Abs(top[0].MaxUs - 2500) < 0.5 && Math.Abs(top[0].TotalMs - 2.6) < 0.01 && top[0].Count == 2);
            Check("nvlddmkm.sys — 300 мкс, отмечен как прерывание (ISR)", top[1].Name == "nvlddmkm.sys" && Math.Abs(top[1].MaxUs - 300) < 0.5 && top[1].MaxIsIsr);
            var df = DpcLatency.Finding(new DpcResult { At = now, Seconds = 30, Drivers = top });
            Check("вывод: ndis.sys — сетевая подсистема, совет и ссылка на энергосбережение сетевой карты — " + df?.Detail,
                df is { Level: Severity.Warn, Link: "tab:Оптимизация" } && df.Detail.Contains("ndis.sys"));
            Check("всё быстрее 500 мкс — «в норме»", DpcLatency.Finding(new DpcResult { At = now, Drivers = new() { new DpcDriver { Name = "ndis.sys", MaxUs = 120 } } })?.Level == Severity.Ok);
            Check("ошибка проверки — без вывода", DpcLatency.Finding(new DpcResult { Error = "нужны права" }) == null);
            Check("понятные имена: ndis.sys, Netwtw14.sys, nvlddmkm.sys, неизвестный .sys",
                DriverNames.Describe("ndis.sys").Kind == "net" && DriverNames.Describe("Netwtw14.sys").Kind == "wifi" && DriverNames.Describe("nvlddmkm.sys").Kind == "gpu" && DriverNames.Describe("foo123.sys").Kind == "driver");
        }
        catch (Exception ex) { Check("исключение: " + ex, false); }

        // --- что видно на этом ПК (только чтение; ничего не запускается)
        sb.AppendLine();
        sb.AppendLine("НА ЭТОМ ПК (только чтение):");
        try
        {
            bool admin = Shell.IsAdmin();
            sb.AppendLine($"  права администратора: {(admin ? "да" : "нет")}");
            var b = ReadBsods(DateTime.Now.AddDays(-30));
            sb.AppendLine($"  синих экранов за 30 дней: {b.Count}" + (b.Count > 0 ? $", последний {b[0].Time:dd.MM HH:mm} код 0x{b[0].Code:X}" : ""));
            var dumps = Minidump.Recent(30, 5, out bool denied);
            sb.AppendLine($"  малые дампы за 30 дней: {(denied ? "папка закрыта — нужны права администратора" : dumps.Count.ToString())}" +
                          string.Concat(dumps.Where(d => d.Error == null).Select(d => $"; {Path.GetFileName(d.File)}: 0x{d.Code:X}, драйверов {d.Modules.Count}, виновник {d.Culprit}")));
            var w = ReadWhea(DateTime.Now.AddDays(-30));
            sb.AppendLine($"  WHEA за 30 дней: {w.Count}" + (w.Count > 0 ? " (" + string.Join(", ", w.GroupBy(x => x.Kind).Select(g => $"{g.Key} {g.Count()}")) + $", неисправленных {w.Count(x => x.Fatal)})" : ""));
            var c = ReadCrashes(DateTime.Now.AddDays(-7));
            var match = GameMatcher();
            int games = 0;
            foreach (var x in c) if ((x.Game = match(x.Path, x.App) ?? "").Length > 0) games++;
            sb.AppendLine($"  вылетов и зависаний программ за 7 дней: {c.Count}, из них игр: {games}" +
                          string.Concat(c.Where(x => x.Game.Length > 0).Take(3).Select(x => $"; {x.Game} {x.Time:dd.MM HH:mm} {(x.Hang ? "зависание" : x.Module)}")));
            var (u, cb, rn) = ReadPendingReboot();
            sb.AppendLine($"  ждёт перезагрузки: обновление {(u ? "да" : "нет")}, компоненты {(cb ? "да" : "нет")}, замена файлов {rn}");
            var mt = ReadMemTest();
            sb.AppendLine("  проверка памяти Windows: " + (mt == null ? "в журнале нет" : $"{mt.Time:dd.MM.yyyy} — {(mt.Errors ? "ошибки" : "ошибок нет")}"));
            var boot = BootPerf.Read();
            sb.AppendLine("  загрузка Windows: " + (boot.Error ?? $"замеров {boot.Boots.Count}, повторяющихся задержек {boot.SlowAll.Count(x => x.Boots >= 2)}; вывод: {BootFinding(boot)?.Title ?? "—"}"));
            var dw = DriverWorse(SafeSessions());
            sb.AppendLine("  драйвер стал хуже: " + (dw == null ? "нет (или драйвер старше 45 дней / моложе суток)" : dw.Detail));
            sb.AppendLine("  DISM /CheckHealth, задержки драйверов и проверка памяти при самопроверке не запускались (только по кнопке).");
        }
        catch (Exception ex) { sb.AppendLine("  ошибка чтения: " + ex.Message); }

        return (fail == null ? $"OK ({n} проверок)" : "FAIL: " + fail) + Environment.NewLine + sb;
    }
}
