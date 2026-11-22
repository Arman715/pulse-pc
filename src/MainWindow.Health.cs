using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PulsePC.Services;

namespace PulsePC;

/// <summary>Строка «что снижает оценку» на «Обзоре».</summary>
public sealed class HealthRow
{
    public string Points { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Link { get; set; } = "";
    public Brush Ink { get; set; } = Palette.Ink1;
}

/// <summary>
/// «Обзор»: оценка здоровья ПК (Services\HealthScore) — число, график за месяц и что снижает; кнопка «Почему тормозит»
/// в заголовке окна и ссылка здесь же (WhySlowWindow). Оценка считается раз в сутки в фоне (не во время игры) и после
/// каждой проверки на вкладке «Диагностика».
/// </summary>
public partial class MainWindow
{
    private DispatcherTimer healthTimer;
    private bool healthBusy;
    private WhySlowWindow whySlow;
    private const int HealthShown = 4;

    private void InitHealth()
    {
        HealthScore.Changed += () => OnUi(ShowHealth);
        ShowHealth();
        // Первый расчёт — через минуту после запуска (не мешать старту), дальше раз в час проверяем, не пора ли.
        healthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        healthTimer.Tick += (_, _) =>
        {
            healthTimer.Interval = TimeSpan.FromHours(1);
            if (HealthScore.Due(HealthScore.Load(), DateTime.Now)) RecalcHealth(manual: false);
            else ShowHealth();      // после полуночи «сегодня» на графике и «вчера в …» в подписи сдвигаются вместе
        };
        healthTimer.Start();
    }

    private void ShowHealth()
    {
        var f = HealthScore.Load();
        var r = f.Last;
        var series = HealthScore.Series(f, DateTime.Today);
        HealthChartEl.Series = series;
        HealthFrom.Text = DateTime.Today.AddDays(1 - HealthScore.ChartDays).ToString(L.En ? "MMMM d" : "d MMMM", L.Culture);
        if (r == null)
        {
            HealthValue.Text = "—";
            HealthVerdict.Text = healthBusy ? L.T("Оценка считается…") : L.T("Оценка появится через минуту после запуска.");
            HealthList.ItemsSource = null;
            HealthNote.Text = L.T("Оценка 0–100 складывается из находок «Диагностики» и того, как шли игры за две недели. Ничего не меняется.");
            return;
        }
        HealthValue.Text = r.Score.ToString(L.Culture);
        HealthValue.Foreground = r.Score < 50 ? Palette.Bad : r.Score < 75 ? Palette.Warn : Palette.Ink1;
        HealthVerdict.Text = HealthScore.Verdict(r.Score);
        HealthList.ItemsSource = r.Penalties.Take(HealthShown).Select(p => new HealthRow
        {
            Points = "−" + p.Points.ToString(L.Culture), Title = p.Title, Detail = p.Detail,
            Link = p.Link != "" ? p.Link : p.Source == "games" ? "tab:Сессии" : "tab:Диагностика",
            Ink = p.Level == Severity.Bad ? Palette.Bad : Palette.Warn,
        }).ToList();
        int days = series.Count(x => x != null);
        var note = new List<string>();
        if (r.Penalties.Count > HealthShown) note.Add(L.F("Ещё замечаний: {0} — на вкладке «Диагностика».", r.Penalties.Count - HealthShown));
        if (r.Penalties.Count == 0) note.Add(L.T("Ничего не снижает оценку."));
        string data = HealthScore.DataNote(r, days);
        if (data != "") note.Add(data);
        note.Add(healthBusy ? L.T("Пересчитываю…") : L.F("Посчитано {0}.", VersionCheck.When(r.At)));
        // Последний столбик графика — сегодня. Если сегодня ещё не считали, он пустой, а число выше — за прошлый расчёт: сказать прямо.
        if (!healthBusy && r.At.Date < DateTime.Today) note.Add(L.T("Сегодня оценка ещё не считалась — последний столбик графика пустой."));
        HealthNote.Text = string.Join(" ", note);
    }

    /// <summary>Пересчитать: проверка «Диагностики» в фоне (журналы, диски, железо — несколько секунд) и игры за 14 дней.</summary>
    private async void RecalcHealth(bool manual)
    {
        if (healthBusy || diagRunning) return;
        if (!manual && GameHooks.InGame) return;          // во время игры не нагружаем компьютер проверкой
        healthBusy = true;
        BtnHealthRecalc.IsEnabled = false;
        ShowHealth();
        if (manual) BeginBusy();
        try
        {
            var snapshot = tweaks.ToList();
            var games = sessions.ToList();
            var items = await Task.Run(() => Diagnostics.Run(metrics, snapshot, settings));
            await Task.Run(() => HealthScore.Record(HealthScore.Compute(items, games, DateTime.Now)));
        }
        catch (Exception ex) { Log.Error("оценка здоровья", ex); }
        finally
        {
            if (manual) EndBusy();
            healthBusy = false;
            BtnHealthRecalc.IsEnabled = true;
            ShowHealth();
        }
    }

    /// <summary>
    /// Проверка на вкладке «Диагностика» закончилась: оценка — по тем же находкам. Число, график и «Посчитано …» на «Обзоре»
    /// обновляются сразу по окончании записи (не только по событию HealthScore.Changed).
    /// </summary>
    private async void RecordHealth(IEnumerable<DiagItem> items)
    {
        var list = items.ToList();
        var games = sessions.ToList();
        try { await Task.Run(() => HealthScore.Record(HealthScore.Compute(list, games, DateTime.Now))); }
        catch (Exception ex) { Log.Error("оценка здоровья", ex); }
        ShowHealth();
    }

    private void HealthRecalc_Click(object sender, RoutedEventArgs e) => RecalcHealth(manual: true);

    private void HealthItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string link && link != "") OpenTarget(link);
    }

    // ================================================================== почему тормозит

    private void WhySlow_Click(object sender, RoutedEventArgs e)
    {
        if (panelHost == null) return;
        if (whySlow != null && whySlow.IsLoaded)
        {
            if (whySlow.WindowState == WindowState.Minimized) whySlow.WindowState = WindowState.Normal;
            whySlow.Activate();
            return;
        }
        whySlow = new WhySlowWindow(this, panelHost);
        whySlow.Closed += (_, _) => whySlow = null;
        whySlow.Show();
    }
}
