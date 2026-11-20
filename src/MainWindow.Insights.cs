using System.Windows;
using System.Windows.Media;
using PulsePC.Services;

namespace PulsePC;

public class DipRow
{
    public string No { get; set; } = "";
    public string Time { get; set; } = "";
    public string Fps { get; set; } = "";
    public string Length { get; set; } = "";
    public string Cause { get; set; } = "";
    public string Detail { get; set; } = "";
    public Brush CauseInk { get; set; }
}

public class BarVm
{
    public string Label { get; set; } = "";
    public double Fraction { get; set; }
    public string Text { get; set; } = "";
    public Brush Ink { get; set; }
}

public class BootSlowVm
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>Разбор игровых сессий, сводка за неделю и загрузка Windows.</summary>
public partial class MainWindow
{
    // ================================================================== разбор сессии
    private void ShowSessionAnalysis(GameSession s)
    {
        ShowDips(s);
        ShowBottleneck(s);
        ShowClocks(s);
        ShowServer(s);
        // Другая сессия — прежнее «картинка сохранена» к ней не относится.
        ShareDone.Visibility = Visibility.Collapsed;
        BtnShare.IsEnabled = true;
    }

    private static string CauseName(string cause) => ShareCard.CauseName(cause);

    private void ShowDips(GameSession s)
    {
        bool hasFps = s.AvgFps > 0;
        // Просадки ищутся по записанным кадрам; у старых сессий разбора нет.
        if (!hasFps || s.Dips == null)
        {
            SessDipsBlock.Visibility = Visibility.Collapsed;
            SessFpsChart.Marks = null;
            return;
        }
        SessDipsBlock.Visibility = Visibility.Visible;
        SessDipsNone.Visibility = s.Dips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // График кадров растянут на всю запись от начала до конца по часам (точки идут подряд, без вырезанного сна), и время
        // просадки (d.At) тоже по часам — доля считается от End − Start, а не от Duration (там только реально записанное время).
        double total = Math.Max(1, (s.End - s.Start).TotalSeconds);
        SessFpsChart.Marks = s.Dips.Select(d => Math.Clamp((d.At - s.Start).TotalSeconds / total, 0, 1)).ToArray();
        SessDips.ItemsSource = s.Dips.Select((d, i) => new DipRow
        {
            No = (i + 1).ToString(),
            Time = d.At.ToString("HH:mm:ss"),
            Fps = $"{d.FromFps:0} → {d.ToFps:0} FPS",
            Length = $"{d.Seconds:0.#} с",
            Cause = CauseName(d.Cause),
            Detail = d.Detail,
            CauseInk = d.Cause is "ram" or "gpu-heat" ? Palette.Warn : d.Cause == "unknown" ? Palette.Ink3 : Palette.Ink1,
        }).ToList();
    }

    private void ShowBottleneck(GameSession s)
    {
        // Вывод и полосы общие с карточкой «Поделиться отчётом»: на картинке то же, что в программе.
        if (ShareCard.Bottleneck(s) is not { } b)
        {
            SessBoundBlock.Visibility = Visibility.Collapsed;
            return;
        }
        SessBoundBlock.Visibility = Visibility.Visible;
        SessBoundVerdict.Text = b.Verdict;
        SessBoundAdvice.Text = b.Advice;
        var bars = ShareCard.BoundBars(s);
        double max = bars.Max(x => x.Pct);
        SessBoundBars.ItemsSource = bars.Select(x => new BarVm
        {
            Label = x.Label, Fraction = x.Pct / 100, Text = $"{x.Pct:0} %", Ink = x.Pct == max ? Palette.Accent : Palette.Ink2,
        }).ToList();
    }

    // ================================================================ поделиться отчётом
    private string sharedPath;

    /// <summary>
    /// «Поделиться отчётом»: карточка сессии 1080×1350 в «Изображения\Пульс ПК» и в буфер обмена — вставить в чат или соцсеть.
    /// </summary>
    private void BtnShare_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not GameSession s) return;
        BtnShare.IsEnabled = false;
        try
        {
            var (path, image) = ShareCard.Save(s);
            sharedPath = path;
            bool copied = CopyImage(image, path);
            ShareResult.Text = copied
                ? "Картинка сохранена в «Изображения\\Пульс ПК» и скопирована: её можно вставить в чат (Ctrl+V)."
                : "Картинка сохранена в «Изображения\\Пульс ПК». Скопировать не вышло: буфер обмена занят другой программой.";
            ShareMark.Level = Severity.Ok;
            BtnShareFolder.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Error("карточка сессии", ex);
            ShareResult.Text = "Не удалось сохранить картинку: " + ex.Message;
            ShareMark.Level = Severity.Bad;
            BtnShareFolder.Visibility = Visibility.Collapsed;
        }
        BtnShare.IsEnabled = true;
        ShareDone.Visibility = Visibility.Visible;
        Motion.Reveal(ShareDone, 0, 4);
    }

    /// <summary>В буфер — и сама картинка (для чатов и редакторов), и файл (для Проводника и мессенджеров, которые берут файлы).</summary>
    private static bool CopyImage(System.Windows.Media.Imaging.BitmapSource image, string path)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                var data = new DataObject();
                data.SetImage(image);
                data.SetFileDropList(new System.Collections.Specialized.StringCollection { path });
                Clipboard.SetDataObject(data, true);
                return true;
            }
            catch { Thread.Sleep(60); }
        }
        return false;
    }

    private void BtnShareFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Проводник открывает окно с обычными правами, даже если программа запущена от администратора.
            if (sharedPath != null && File.Exists(sharedPath))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + sharedPath + "\"") { UseShellExecute = false });
            else GameLibrary.OpenAsUser(ShareCard.Folder);
        }
        catch (Exception ex) { ShareResult.Text = "Не удалось открыть папку: " + ex.Message; }
    }

    private void ShowClocks(GameSession s)
    {
        if (s.GpuPowerLimitPct == null && s.CpuSlowPct == null)
        {
            SessClockBlock.Visibility = Visibility.Collapsed;
            return;
        }
        SessClockBlock.Visibility = Visibility.Visible;
        var parts = new List<string>();
        if (s.AvgGpuClock > 0) parts.Add($"Видеокарта под нагрузкой держала в среднем {s.AvgGpuClock:0} МГц");
        if (s.AvgCpuGhz > 0) parts.Add($"процессор — {s.AvgCpuGhz:0.0#} ГГц");
        string head = parts.Count > 0 ? string.Join(", ", parts) + ". " : "";
        bool heat = s.GpuThermalLimitPct >= 5 || s.GpuHardLimitPct >= 5 || s.CpuSlowPct >= 10;
        SessClockSummary.Text = head + (heat
            ? "Частоты сбрасывались — это стоит проверить, подробности в выводах ниже."
            : "Частоты не сбрасывались. Упор в лимит питания — нормальная работа видеокарты: она разгоняется, пока хватает питания.");

        var bars = new List<BarVm>();
        void Add(string label, double? pct, double warnFrom, Brush warnInk)
        {
            if (pct == null) return;
            double v = pct.Value;
            bars.Add(new BarVm { Label = label, Fraction = v / 100, Text = $"{v:0} %", Ink = v >= warnFrom ? warnInk : Palette.Ink2 });
        }
        Add("видеокарта: упор в лимит питания", s.GpuPowerLimitPct, 101, Palette.Ink2);
        Add("видеокарта: снижение из-за нагрева", s.GpuThermalLimitPct, 5, Palette.Warn);
        Add("видеокарта: аварийное снижение", s.GpuHardLimitPct, 5, Palette.Bad);
        Add("процессор: ниже базовой частоты", s.CpuSlowPct, 10, Palette.Warn);
        SessClockBars.ItemsSource = bars;
    }

    private void ShowServer(GameSession s)
    {
        if (string.IsNullOrEmpty(s.Server))
        {
            SessServerBlock.Visibility = Visibility.Collapsed;
            return;
        }
        SessServerBlock.Visibility = Visibility.Visible;
        SessServerAddr.Text = $"адрес {s.Server} — найден по сетевому трафику игры";
        SessSrvPing.Value = s.ServerPingMs ?? double.NaN;
        SessSrvJitter.Value = s.ServerJitterMs ?? double.NaN;
        SessSrvLoss.Value = s.ServerLossPct ?? double.NaN;
        SessSrvLoss.Foreground = s.ServerLossPct >= 1 ? Palette.Bad : Palette.Ink1;
        SessSrvGaps.Value = s.NetGaps ?? double.NaN;
        SessSrvGaps.Foreground = s.NetGaps >= 3 ? Palette.Warn : Palette.Ink1;
        SessSrvMaxGap.Value = s.NetMaxGapSec ?? double.NaN;

        var notes = new List<string>();
        if (s.ServerPingBlocked)
            notes.Add("Сервер не отвечает на пинг — многие игровые серверы так настроены, это нормально. Качество связи оценено по обрывам в потоке данных от него.");
        else if (s.ServerPingMs > 0 && s.AvgPingMs > 0 && s.ServerPingMs - s.AvgPingMs >= 30)
            notes.Add($"До сервера на {s.ServerPingMs - s.AvgPingMs:0} мс дольше, чем до ближайшего узла интернета: сервер далеко. Если в игре можно выбрать регион — выберите ближайший.");
        if (s.NetGaps >= 3)
            notes.Add(s.LossPct >= 1 || s.JitterMs >= 15
                ? "Обрывы совпали с перебоями вашего подключения — подробности на вкладке «Сеть»."
                : "Ваше подключение в это время работало нормально — обрывы на стороне сервера или по пути до него.");
        else if (s.NetGaps != null)
            notes.Add("Поток данных от сервера шёл ровно, без обрывов.");
        SessServerNote.Text = string.Join(" ", notes);
    }

    // ================================================================== сводка за неделю
    private void BtnWeek_Click(object sender, RoutedEventArgs e) => ShowWeek();

    private void ShowWeek()
    {
        if (page != PgSessions) SelectTab(PgSessions);
        SessionList.SelectedItem = null;
        SessionEmpty.Visibility = Visibility.Collapsed;
        SessionReport.Visibility = Visibility.Collapsed;
        WeekReport.Visibility = Visibility.Visible;

        var w = Weekly.Build(sessions.ToList(), DateTime.Now);
        WeekRange.Text = $"{w.From:d MMMM} — {w.To:d MMMM}";
        bool empty = w.Empty;
        WeekTitle.Text = empty ? "За неделю игр не было" : "Неделя в играх";
        WeekNumbers.Visibility = WeekGamesBlock.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        WeekNotesLabel.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (!empty)
        {
            WeekHours.Value = Math.Round(w.Hours, 1);
            WeekHoursCap.Text = "в играх" + (w.PrevHours > 0 ? $"\nнеделей раньше — {Weekly.Hours(w.PrevHours)}" : "");
            WeekSessions.Value = w.Sessions;
            WeekDips.Value = w.Dips;
            WeekTemp.Value = w.MaxTemp;
            WeekTemp.Foreground = w.MaxTemp >= 83 ? Palette.Warn : Palette.Ink1;
        }
        WeekGames.ItemsSource = w.Games;
        WeekNotes.ItemsSource = w.Notes;
        SessionScroll.ScrollToTop();
        Motion.Reveal(WeekReport, 0, 8);
    }

    /// <summary>По понедельникам (первый запуск на неделе) — сводка у часов, если за прошлую неделю были игры.</summary>
    private void CheckWeeklyNotice()
    {
        var now = DateTime.Now;
        if (settings.LastWeekly == DateTime.MinValue) { settings.LastWeekly = now; settings.Save(); return; }   // первое знакомство
        int sinceMonday = ((int)now.DayOfWeek + 6) % 7;
        var monday = now.Date.AddDays(-sinceMonday);
        if (settings.LastWeekly >= monday) return;
        settings.LastWeekly = now; settings.Save();
        if (!settings.AlertsEnabled) return;

        var w = Weekly.Build(sessions.ToList(), now);
        if (w.Empty) return;
        string top = w.Games.Count > 0 ? $" Больше всего — {w.Games[0].Game} ({w.Games[0].Hours})." : "";
        tray.Notify("Неделя в играх", $"{Weekly.Hours(w.Hours)}, {Fmt.Plural(w.Sessions, "сессия", "сессии", "сессий")}.{top} Нажмите, чтобы открыть сводку.", false,
            () => { ShowFromTray(); ShowWeek(); });
    }

    // ================================================================== загрузка Windows
    private bool bootLoaded;

    private void LoadBoot()
    {
        if (bootLoaded) return;
        bootLoaded = true;
        Task.Run(() =>
        {
            BootReport r;
            try { r = BootPerf.Read(); } catch (Exception ex) { r = new BootReport { Error = ex.Message }; }
            OnUi(() => ShowBoot(r));
        });
    }

    private void ShowBoot(BootReport r)
    {
        if (r.Boots.Count == 0)
        {
            BootSeconds.Visibility = Visibility.Collapsed;
            BootCaption.Margin = new Thickness(0, 8, 0, 0);
            BootCaption.Text = r.LastBoot > DateTime.MinValue ? $"Windows запущена {r.LastBoot:d MMMM, HH:mm}." : "";
            BootChart.Visibility = Visibility.Collapsed;
            BootSlow.ItemsSource = null;
            BootNote.Text = r.Error ?? "Замеров загрузки в журнале нет: Windows записывает их только при полной загрузке, а не при выходе из сна.";
            return;
        }
        var last = r.Boots[^1];
        BootSeconds.Visibility = Visibility.Visible;
        BootCaption.Margin = new Thickness(0);
        BootSeconds.Value = Math.Round(last.Seconds);
        BootSeconds.Foreground = last.Seconds >= 90 ? Palette.Warn : Palette.Ink1;
        BootCaption.Text = $"последняя полная загрузка {last.At:d MMMM, HH:mm}: рабочий стол через {last.MainSeconds:0} с, догрузка программ — ещё {last.PostSeconds:0} с";
        if (r.Boots.Count >= 2)
        {
            BootChart.Visibility = Visibility.Visible;
            BootChart.SetData(r.Boots.Select(b => b.Seconds).ToArray());
        }
        else BootChart.Visibility = Visibility.Collapsed;
        BootSlow.ItemsSource = r.Slow.Select(x => new BootSlowVm { Name = x.Name, Kind = x.Kind, Text = $"+{x.Seconds:0.#} с" }).ToList();
        double avg = r.Boots.Average(b => b.Seconds);
        // Включение с «быстрым запуском» Windows не замеряет: тогда последний замер старше последнего включения.
        bool stale = r.LastBoot > DateTime.MinValue && (r.LastBoot - last.At).TotalDays >= 1;
        if (stale)
        {
            BootCaption.Text = $"последний замер — {last.At:d MMMM}: рабочий стол через {last.MainSeconds:0} с, догрузка программ — ещё {last.PostSeconds:0} с";
            BootNote.Text = $"Последнее включение ({r.LastBoot:d MMMM, HH:mm}) прошло через «быстрый запуск», а такие загрузки Windows не замеряет. "
                            + "Свежая цифра появится после перезагрузки (Пуск → Перезагрузка)."
                            + (r.Slow.Count > 0 ? " Список ниже относится к последней замеренной загрузке." : "");
            return;
        }
        BootNote.Text = r.Slow.Count > 0
            ? $"Windows отметила, кто затянул последнюю загрузку. Среднее за {Fmt.Plural(r.Boots.Count, "замер", "замера", "замеров")} — {avg:0} с."
            : $"Windows не отметила ничего, что затягивало загрузку. Среднее за {Fmt.Plural(r.Boots.Count, "замер", "замера", "замеров")} — {avg:0} с. На графике — замеры по порядку, последний справа.";
    }
}
