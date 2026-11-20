using PulsePC.Services;
using WinForms = System.Windows.Forms;

namespace PulsePC;

/// <summary>Значок у часов: подсказка с показаниями, меню и всплывающие предупреждения.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon icon;
    private readonly WinForms.ToolStripMenuItem alertsItem, closeToTrayItem;
    private readonly AppSettings settings;

    public event Action ShowRequested;
    public event Action ExitRequested;
    public event Action SettingsChanged;

    public TrayIcon(AppSettings settings)
    {
        this.settings = settings;

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(L.T("Открыть Пульс ПК"), null, (_, _) => ShowRequested?.Invoke());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        alertsItem = new WinForms.ToolStripMenuItem(L.T("Показывать предупреждения")) { CheckOnClick = true, Checked = settings.AlertsEnabled };
        alertsItem.CheckedChanged += (_, _) => { settings.AlertsEnabled = alertsItem.Checked; settings.Save(); };
        closeToTrayItem = new WinForms.ToolStripMenuItem(L.T("При закрытии сворачивать к часам")) { CheckOnClick = true, Checked = settings.CloseToTray };
        closeToTrayItem.CheckedChanged += (_, _) => { settings.CloseToTray = closeToTrayItem.Checked; settings.Save(); };
        var fpsItem = new WinForms.ToolStripMenuItem(L.T("Записывать FPS в играх")) { CheckOnClick = true, Checked = settings.RecordFps };
        fpsItem.CheckedChanged += (_, _) => { settings.RecordFps = fpsItem.Checked; settings.Save(); SettingsChanged?.Invoke(); };
        menu.Items.Add(alertsItem);
        menu.Items.Add(fpsItem);
        menu.Items.Add(closeToTrayItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(L.T("Выход"), null, (_, _) => ExitRequested?.Invoke());

        icon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = L.T("Пульс ПК"),
            ContextMenuStrip = menu,
            Visible = true,
        };
        icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) ShowRequested?.Invoke(); };
        icon.BalloonTipClicked += (_, _) =>
        {
            var action = clickAction; clickAction = null;
            if (action != null) action(); else ShowRequested?.Invoke();
        };
        icon.BalloonTipClosed += (_, _) => clickAction = null;
    }

    private Action clickAction;

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
            if (resource != null)
                using (resource.Stream)
                    return new System.Drawing.Icon(resource.Stream, WinForms.SystemInformation.SmallIconSize);
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>Подсказка при наведении (Windows ограничивает её длину).</summary>
    public void SetTooltip(string text)
    {
        if (text.Length > 120) text = text[..120];
        if (icon.Text != text) icon.Text = text;
    }

    /// <summary>Всплывающее уведомление; onClick — что сделать по нажатию на него (по умолчанию открыть окно).</summary>
    public void Notify(string title, string text, bool warning = false, Action onClick = null)
    {
        clickAction = onClick;
        if (text.Length > 250) text = text[..249] + "…";     // предел Windows для текста уведомления
        try { icon.ShowBalloonTip(8000, title, text, warning ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.None); } catch { }
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }
}

/// <summary>
/// Предупреждения: перегрев видеокарты, нехватка памяти, мало места на системном диске.
/// Срабатывают, только если состояние держится несколько замеров подряд, и не чаще раза в 10 минут каждое.
/// </summary>
public sealed class Alerts
{
    private readonly TrayIcon tray;
    private readonly AppSettings settings;
    private int hot, lowRam, diskTicks;
    private DateTime lastHot = DateTime.MinValue, lastRam = DateTime.MinValue, lastDisk = DateTime.MinValue;
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);

    public Alerts(TrayIcon tray, AppSettings settings) { this.tray = tray; this.settings = settings; }

    public void Check(double gpuTemp, double ramAvailGb)
    {
        if (!settings.AlertsEnabled) return;
        var now = DateTime.Now;

        hot = !double.IsNaN(gpuTemp) && gpuTemp >= 85 ? hot + 1 : 0;
        if (hot >= 3 && now - lastHot > Cooldown)
        {
            lastHot = now;
            tray.Notify(L.T("Видеокарта перегревается"), L.F("Сейчас {0:0} °C. Проверьте, крутятся ли вентиляторы и не забит ли корпус пылью.", gpuTemp), true);
        }

        lowRam = ramAvailGb > 0 && ramAvailGb < 1.0 ? lowRam + 1 : 0;
        if (lowRam >= 4 && now - lastRam > Cooldown)
        {
            lastRam = now;
            tray.Notify(L.T("Заканчивается память"), L.F("Доступно {0:0.0} ГБ. Закройте тяжёлые программы — список на вкладке «Оптимизация».", ramAvailGb), true);
        }

        // Диск проверяется раз в пять минут, предупреждение - не чаще раза в сутки.
        if (++diskTicks % 200 == 1 && now - lastDisk > TimeSpan.FromHours(24))
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
                double freePct = 100.0 * drive.AvailableFreeSpace / drive.TotalSize;
                if (freePct < 10)
                {
                    lastDisk = now;
                    tray.Notify(L.T("Мало места на системном диске"), L.F("Свободно {0}. Вкладка «Очистка» покажет, что можно удалить.", Fmt.Size(drive.AvailableFreeSpace)), true);
                }
            }
            catch { }
        }
    }
}
