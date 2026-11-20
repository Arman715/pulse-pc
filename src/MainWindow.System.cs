using System.Windows;
using System.Windows.Controls;
using PulsePC.Panels;
using PulsePC.Services;

namespace PulsePC;

/// <summary>Вкладка «Система»: список разделов слева, открытый раздел справа. Разделы — отдельные элементы из Panels\.</summary>
public partial class MainWindow
{
    public sealed class SystemSection
    {
        /// <summary>Группа в списке и в шапке раздела: «ИГРА», «ВИДЕОКАРТА»…</summary>
        public string Group { get; init; }
        /// <summary>Название — по-русски: по нему открывают раздел («sys:…») и узнают «Состояние программы». Показывается TitleText.</summary>
        public string Title { get; init; }
        public string TitleText => L.T(Title);
        public Func<FrameworkElement> Create { get; init; }
        /// <summary>Для разделов, которые не реализуют ISystemPanel (автозагрузка живёт в разметке окна).</summary>
        public Action Shown { get; init; }
        public FrameworkElement View;
    }

    private readonly List<SystemSection> sections = new();
    private PanelHost panelHost;
    private volatile LiveData live = new();
    private volatile IReadOnlyList<GameSession> sessionSnapshot = Array.Empty<GameSession>();

    private void InitSystem()
    {
        sessionSnapshot = sessions.ToList();
        sessions.CollectionChanged += (_, _) => sessionSnapshot = sessions.ToList();
        // Отметки «просмотрено» общие для «Диагностики», осмотра, раздела «Драйвер» и сторожа — в одних настройках.
        LogMarks.Settings = settings;

        panelHost = new PanelHost
        {
            Admin = admin,
            Settings = settings,
            BeginBusy = () => OnUi(BeginBusy),
            EndBusy = () => OnUi(EndBusy),
            Notify = (title, text, warning, click) => OnUi(() => { if (settings.AlertsEnabled) tray.Notify(title, text, warning, click); }),
            Live = () => live,
            Sessions = () => sessionSnapshot,
            Hardware = () => hw,
            Ui = OnUi,
            Open = target => OnUi(() => OpenTarget(target)),
        };

        sections.AddRange(SystemSections.Build());
        // Автозагрузка была отдельной вкладкой; её разметка лежит в окне и переносится в раздел.
        StartupHolder.Children.Remove(StartupBody);
        sections.Insert(sections.FindIndex(s => s.Group == "WINDOWS"), new SystemSection
        {
            Group = "WINDOWS", Title = "Автозагрузка", Create = () => StartupBody,
            Shown = () => { if (!startupLoaded) LoadStartup(); },
        });
        var view = new System.Windows.Data.ListCollectionView(sections);
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(SystemSection.Group)));
        SysList.ItemsSource = view;

        // Службы разделов: подписка на начало и конец игры, возврат временных изменений после сбоя.
        SystemSections.InitServices(panelHost);
    }

    /// <summary>Снимок показаний для разделов и панели на телефоне (вызывается на каждом такте).</summary>
    private void UpdateLive()
    {
        var (ping, _, _, _, count) = net.Internet.Stats();
        live = new LiveData
        {
            Recording = recorder.IsRecording,
            Game = recorder.IsRecording ? recorder.CurrentGame ?? "" : "",
            SessionSeconds = recorder.IsRecording ? (DateTime.Now - recorder.StartedAt).TotalSeconds : 0,
            Fps = recorder.IsRecording ? recorder.LiveFps : double.NaN,
            CpuLoad = load,
            CpuGhz = ghz,
            GpuLoad = gpuOk ? gpu.Load : double.NaN,
            GpuTemp = gpuOk ? gpu.Temp : double.NaN,
            GpuFanPct = gpuOk ? gpu.Fan : double.NaN,
            GpuFanRpm = gpuOk ? gpu.FanRpm : double.NaN,
            GpuPowerW = gpuOk ? gpu.Power : double.NaN,
            VramUsedGb = gpuOk && !double.IsNaN(gpu.MemUsed) ? gpu.MemUsed / 1024 : double.NaN,
            RamFreeGb = total > 0 ? avail : double.NaN,
            RamTotalGb = total > 0 ? total : double.NaN,
            PingMs = net.Active && count > 0 ? ping : double.NaN,
            GpuName = hw?.MainGpu?.Name ?? "",
        };
    }

    private void OpenSystem()
    {
        if (SysList.SelectedItem == null) SysList.SelectedIndex = 0;
        else if (SysList.SelectedItem is SystemSection s) { (s.View as ISystemPanel)?.Shown(); s.Shown?.Invoke(); }
    }

    /// <summary>
    /// Перейти по ссылке внутри программы: «tab:Очистка», «tab:Оптимизация», «tab:Диагностика»,
    /// «sys:&lt;раздел «Системы»&gt;», «clean:&lt;пункт очистки&gt;» — «Очистка», где отмечен только этот пункт.
    /// </summary>
    private void OpenTarget(string target)
    {
        if (string.IsNullOrEmpty(target)) return;
        int colon = target.IndexOf(':');
        string kind = colon < 0 ? target : target[..colon], name = colon < 0 ? "" : target[(colon + 1)..];
        switch (kind)
        {
            case "sys": ShowSystemSection(name); break;
            case "clean": OpenCleanFor(name); break;
            case "tab":
                int index = name switch { "Обзор" => PgOverview, "Сессии" => PgSessions, "История" => PgHistory, "Сеть" => PgNet, "Диагностика" => PgDiag, "Очистка" => PgClean, "Оптимизация" => PgTweaks, "Система" => PgSystem, _ => -1 };
                if (index >= 0) SelectTab(index);
                break;
        }
    }

    /// <summary>Открыть вкладку «Система» на нужном разделе (из уведомлений и подсказок).</summary>
    private void ShowSystemSection(string title)
    {
        SelectTab(PgSystem);
        var s = sections.FirstOrDefault(x => x.Title == title);
        if (s != null) SysList.SelectedItem = s;
    }

    private void SysList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SysList.SelectedItem is not SystemSection s) return;
        if (s.View == null)
        {
            try
            {
                s.View = s.Create();
                (s.View as ISystemPanel)?.Attach(panelHost);
            }
            catch (Exception ex)
            {
                s.View = new TextBlock { Text = L.F("Не удалось открыть раздел: {0}", ex.Message), Style = (Style)FindResource("Body") };
            }
        }
        SysKicker.Source = L.T(s.Group) + "  ·  " + s.TitleText.ToUpperInvariant();
        SysHost.Content = s.View;
        SysScroll.ScrollToTop();
        (s.View as ISystemPanel)?.Shown();
        s.Shown?.Invoke();
        Motion.Reveal(SysHost, 0, 6);
    }
}

/// <summary>Состав вкладки «Система». Новые разделы добавляются сюда.</summary>
public static class SystemSections
{
    public static List<MainWindow.SystemSection> Build() => new()
    {
        Section("ИГРА", "Питание", "PowerPanel"),
        Section("ИГРА", "Обновления Windows", "UpdatesPanel"),
        Section("ИГРА", "Память в игре", "MemoryPanel"),
        Section("ИГРА", "Монитор", "DisplayPanel"),
        Section("ИГРА", "Клавиатура и мышь", "InputPanel"),
        Section("ИГРА", "Звук", "SoundPanel"),
        Section("ВИДЕОКАРТА", "Драйвер", "GpuDriverPanel"),
        Section("ВИДЕОКАРТА", GpuVendors.SectionTitle(GpuVendors.MainVendor), "NvidiaPanel"),
        Section("ВИДЕОКАРТА", "Вентиляторы", "FansPanel"),
        Section("WINDOWS", "Оформление", "VisualPanel"),
        Section("WINDOWS", "Файл подкачки", "PagefilePanel"),
        Section("WINDOWS", "Новые версии", "VersionsPanel"),
        Section("ПРИБОРЫ", "Совет по апгрейду", "UpgradePanel"),
        Section("ПРИБОРЫ", "Электричество", "PowerCostPanel"),
        Section("ПРИБОРЫ", "Панель на телефоне", "PhonePanel"),
        Section("ПРИБОРЫ", "Замер до и после", "BenchPanel"),
        Section("ПРОЧЕЕ", "Язык · Language", "LanguagePanel"),
        Section("ПРОЧЕЕ", "Состояние программы", "StatusPanel"),
    };

    private static readonly string[] Services =
    {
        "PowerAuto", "UpdateGuard", "MemoryBoost", "DisplayCheck", "InputTuning", "AudioDevices", "GpuDriver", "NvidiaProfiles", "FanControl",
        "VisualFx", "Pagefile", "VersionCheck", "UpgradeAdvisor", "PowerCost", "PhoneServer", "Bench",
    };

    // Пока разделы пишутся, они подключаются по имени: отсутствующий раздел показывает заглушку, а не ломает сборку.
    private static MainWindow.SystemSection Section(string group, string title, string panel) => new()
    {
        Group = group, Title = title,
        Create = () =>
        {
            var type = typeof(SystemSections).Assembly.GetType("PulsePC.Panels." + panel);
            return type != null
                ? (FrameworkElement)Activator.CreateInstance(type)
                : new TextBlock { Text = L.T("Раздел ещё не подключён."), Style = (Style)Application.Current.FindResource("Body") };
        },
    };

    public static void InitServices(PanelHost host)
    {
        foreach (var name in Services)
        {
            try
            {
                var type = typeof(SystemSections).Assembly.GetType("PulsePC.Services." + name);
                type?.GetMethod("Init", new[] { typeof(PanelHost) })?.Invoke(null, new object[] { host });
            }
            catch { }
        }
    }
}
