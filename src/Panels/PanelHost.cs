using PulsePC.Services;

namespace PulsePC.Panels;

/// <summary>Живые показания для разделов и для панели на телефоне. NaN — показания нет.</summary>
public sealed class LiveData
{
    public bool Recording { get; init; }
    public string Game { get; init; } = "";
    public double SessionSeconds { get; init; }
    public double Fps { get; init; } = double.NaN;
    public double CpuLoad { get; init; } = double.NaN;
    public double CpuGhz { get; init; } = double.NaN;
    public double GpuLoad { get; init; } = double.NaN;
    public double GpuTemp { get; init; } = double.NaN;
    /// <summary>Скорость вентиляторов видеокарты, % от максимальной, и их обороты.</summary>
    public double GpuFanPct { get; init; } = double.NaN;
    public double GpuFanRpm { get; init; } = double.NaN;
    /// <summary>Потребление видеокарты, Вт (NaN — датчика нет).</summary>
    public double GpuPowerW { get; init; } = double.NaN;
    public double VramUsedGb { get; init; } = double.NaN;
    public double RamFreeGb { get; init; } = double.NaN;
    public double RamTotalGb { get; init; } = double.NaN;
    public double PingMs { get; init; } = double.NaN;
    public string GpuName { get; init; } = "";
}

/// <summary>
/// Всё, что раздел вкладки «Система» получает от окна программы. Раздел не обращается к MainWindow напрямую.
/// </summary>
public sealed class PanelHost
{
    /// <summary>Программа запущена с правами администратора. Без них разделы только показывают состояние.</summary>
    public bool Admin { get; init; }
    public AppSettings Settings { get; init; } = new();
    /// <summary>Линия занятости в заголовке окна: включить на время фоновой работы и выключить после.</summary>
    public Action BeginBusy { get; init; } = () => { };
    public Action EndBusy { get; init; } = () => { };
    /// <summary>Уведомление у часов: заголовок, текст, предупреждение ли это, действие по нажатию (может быть null).</summary>
    public Action<string, string, bool, Action> Notify { get; init; } = (_, _, _, _) => { };
    /// <summary>Последние показания (обновляются раз в 1,5 с). Можно вызывать из любого потока.</summary>
    public Func<LiveData> Live { get; init; } = () => new LiveData();
    /// <summary>Записанные игровые сессии, новые первыми. Снимок: можно читать из фонового потока.</summary>
    public Func<IReadOnlyList<GameSession>> Sessions { get; init; } = () => Array.Empty<GameSession>();
    /// <summary>Идущая игра для оверлея (процесс, запись кадров, пинг до сервера); null — записи нет. Вызывать в потоке интерфейса.</summary>
    public Func<LiveGameFeed> GameFeed { get; init; } = () => null;
    /// <summary>Паспорт железа; null, пока не прочитан.</summary>
    public Func<HardwareInfo> Hardware { get; init; } = () => null;
    /// <summary>Выполнить действие в потоке интерфейса.</summary>
    public Action<Action> Ui { get; init; } = a => a();
    /// <summary>
    /// Перейти в другое место программы (ссылка из раздела): «tab:Очистка», «tab:Оптимизация», «tab:Диагностика»,
    /// «clean:&lt;пункт очистки&gt;» — «Очистка» с отмеченным только этим пунктом, «sys:&lt;раздел вкладки «Система»&gt;». Вызывать в потоке интерфейса.
    /// </summary>
    public Action<string> Open { get; init; } = _ => { };
}

/// <summary>
/// Раздел вкладки «Система»: UserControl без собственной прокрутки (прокручивает страница), ширина — до 780.
/// </summary>
public interface ISystemPanel
{
    /// <summary>Один раз после создания. Ничего не меняет в системе и не блокирует поток интерфейса.</summary>
    void Attach(PanelHost host);
    /// <summary>Каждый раз, когда раздел открывают: перечитать состояние в фоне. Ничего не меняет в системе.</summary>
    void Shown();
}
