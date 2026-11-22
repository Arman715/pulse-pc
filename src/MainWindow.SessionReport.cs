using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PulsePC.Services;

namespace PulsePC;

public class StutterRow
{
    public string Title { get; set; } = "";
    public string Count { get; set; } = "";
    public string Advice { get; set; } = "";
    public Brush Ink { get; set; }
}

public class EventRow
{
    public string Time { get; set; } = "";
    public string Text { get; set; } = "";
    public Brush Ink { get; set; }
}

public class BackgroundRow
{
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    public Brush Ink { get; set; }
}

public class PairRow
{
    public bool IsText { get; set; }
    /// <summary>Заметка к сессии: текст в несколько строк — слева, а не по правому краю, как числа.</summary>
    public bool IsNote { get; set; }
    public string Label { get; set; } = "";
    public string This { get; set; } = "";
    public string Other { get; set; } = "";
    public string Delta { get; set; } = "";
    public Brush Ink { get; set; }
}

/// <summary>
/// Отчёт после игры (версия 1.5): заметка к сессии, игра на встроенной графике, время кадров и плавность, отметки на графиках,
/// вид рывков, фоновые программы, сравнение двух сессий рядом, лучшие условия, нагрузка самой программы.
/// У старых сессий новых данных нет — соответствующие блоки просто не показываются.
/// </summary>
public partial class MainWindow
{
    /// <summary>Вторая сессия для сравнения рядом (той же игры) и сессия, для которой она выбрана.</summary>
    private GameSession comparePartner, partnerFor;

    private void ShowReport15(GameSession s)
    {
        var same = sessions.Where(x => x.Game == s.Game).OrderByDescending(x => x.Start).ToList();
        ShowNote(s);
        ShowIgpu(s);
        ShowFrames(s);
        ShowEvents(s);
        ShowStutters(s);
        ShowBackground(s);
        ShowPair(s);
        ShowBest(s, same);
        ShowAppLoad(s);
    }

    private static string Cap(string t) => string.IsNullOrEmpty(t) ? t : char.ToUpper(t[0], L.Culture) + t[1..];

    // ================================================================== заметка
    private GameSession noteSession;
    private bool noteLoading;

    private void ShowNote(GameSession s)
    {
        noteSession = s;
        noteLoading = true;
        SessNoteBox.Text = s.UserNote ?? "";
        noteLoading = false;
        SessNoteSaved.Text = "";
        SessNoteHint.Visibility = SessNoteBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SessNoteBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SessNoteHint.Visibility = SessNoteBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!noteLoading) SessNoteSaved.Text = "";
    }

    private void SessNoteBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { SaveNote(); e.Handled = true; }
        else if (e.Key == Key.Escape && noteSession != null)
        {
            noteLoading = true; SessNoteBox.Text = noteSession.UserNote ?? ""; noteLoading = false;
            e.Handled = true;
        }
    }

    private void SessNoteBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => SaveNote();

    /// <summary>Сохранить заметку в файл сессий (в фоне) и обновить строку в списке.</summary>
    private async void SaveNote()
    {
        var s = noteSession;
        if (s == null) return;
        string text = SessNoteBox.Text.Trim();
        if ((s.UserNote ?? "") == text) return;
        DateTime start = s.Start; string game = s.Game;
        string error = await Task.Run(() => SessionStore.SetNote(start, game, text));
        if (error != null)
        {
            if (ReferenceEquals(noteSession, s)) { SessNoteSaved.Text = L.F("не сохранено: {0}", error); SessNoteSaved.Foreground = Palette.Warn; }
            return;
        }
        s.UserNote = text.Length == 0 ? null : text;
        if (ReferenceEquals(noteSession, s)) { SessNoteSaved.Text = L.T("сохранено"); SessNoteSaved.Foreground = Palette.Ink3; }
        // Строка в списке пересоздаётся по шаблону — заметка видна сразу.
        if (SessionList.ItemContainerGenerator.ContainerFromItem(s) is ListBoxItem li)
        {
            var t = li.ContentTemplate ?? SessionList.ItemTemplate;
            li.ContentTemplate = null; li.ContentTemplate = t;
        }
        if (SessionList.SelectedItem is GameSession cur && (ReferenceEquals(cur, s) || ReferenceEquals(comparePartner, s))) ShowPair(cur);
    }

    // ================================================================== встроенная графика
    private void ShowIgpu(GameSession s)
    {
        bool show = s.GpuUsedIntegrated == true && !string.IsNullOrEmpty(s.GpuIdle);
        SessIgpuBlock.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        SessIgpuTitle.Text = L.F("Игра шла на встроенной графике — {0}", s.GpuUsed);
        SessIgpuText.Text = L.F("В компьютере есть видеокарта {0}, но эту игру рисовала встроенная графика процессора. Она в разы слабее — отсюда низкий FPS и рывки.", s.GpuIdle);
        SessIgpuHow.Text = s.GpuHybrid == true
            ? L.T("Как переключить: Параметры Windows → Система → Дисплей → Графика → выберите игру (или «Добавить» и её файл .exe) → «Высокая производительность». То же можно в панели видеокарты: NVIDIA — «Управление параметрами 3D» → «Программные настройки» → «Высокопроизводительный процессор NVIDIA»; AMD — AMD Software → «Игры». Затем перезапустите игру.")
            : L.T("Скорее всего, кабель монитора включён в разъём материнской платы, а не видеокарты: тогда игры идут на встроенной графике. Переключите кабель в разъём видеокарты — он ниже, в горизонтальной планке на задней стенке. Если кабель уже в видеокарте: Параметры Windows → Система → Дисплей → Графика → игра → «Высокая производительность».");
    }

    // ================================================================== время кадров и плавность
    private void ShowFrames(GameSession s)
    {
        var f = s.Frames;
        bool show = s.AvgFps > 0 && f?.Median?.Length >= 2;
        SessFrameBlock.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        SessFrameChart.SetData(f, Math.Max(1, (s.End - s.Start).TotalSeconds));
        double ratio = f.MedianMs > 0 ? f.P99Ms / f.MedianMs : 1;
        int n = s.Stutters ?? 0;
        string head = s.Smoothness is double sm ? L.F("Плавность {0:0} из 100.", sm) + " " : "";
        SessSmoothText.Text = head + L.F("Обычный кадр — {0:0.0} мс, худший 1 % кадров — {1:0.0} мс (в {2:0.0} раза дольше).", f.MedianMs, f.P99Ms, ratio) + " "
            + (n == 0 ? L.T("Рывков не было.")
                      : L.F("Рывков — {0} за {1}, это {2:0.#} в минуту.", n, Fmt.Duration(TimeSpan.FromMinutes(f.PlayMinutes)), s.StutterPerMin ?? 0));
        SessSmoothRule.Text = SessionAnalysis.SmoothnessRule();
    }

    // ================================================================== отметки
    private static string EventText(SessionEvent e, GameSession s)
    {
        string dur = Fmt.Duration(TimeSpan.FromSeconds(Math.Max(1, e.Seconds)));
        bool inDip = s.Dips?.Any(d => e.At <= d.At.AddSeconds(d.Seconds + 1.6) && e.At.AddSeconds(e.Seconds) >= d.At.AddSeconds(-1.6)) == true;
        switch (e.Kind)
        {
            case "minimized": return L.F("Игра была свёрнута или в фокусе было другое окно — {0}.", dur);
            case "pause": return L.F("Кадров не было {0}: загрузка или пауза в игре.", dur);
            case "driver": return L.T("Сброс драйвера видеокарты (событие Windows 4101): экран на миг гас, драйвер перезапустился.");
            case "ping": return L.F("Скачок пинга до {0:0} мс ({1}).", e.Value, dur);
            default:
                string who = SessionCulprits.Pretty(e.Name);
                bool disk = e.DiskMBs >= 30, cpu = e.Cpu >= 15 || !disk;
                // Защитник Windows называется только по имени процесса: его настройки программа не читает.
                string what = SessionCulprits.IsDefender(e.Name) && disk ? L.F("Защитник Windows проверял файлы ({0})", SessionCulprits.Load(e.Cpu, e.DiskMBs))
                    : disk && cpu ? L.F("Процессор и диск занимал {0} ({1}, {2})", who, SessionCulprits.Pct(e.Cpu), SessionCulprits.MBs(e.DiskMBs))
                    : disk ? L.F("Диск занимал {0} ({1})", who, SessionCulprits.MBs(e.DiskMBs))
                    : L.F("Процессор занимал {0} ({1})", who, SessionCulprits.Pct(e.Cpu));
                return what + (inDip ? L.T(" — в момент просадки FPS.") : ".");
        }
    }

    private void ShowEvents(GameSession s)
    {
        var events = s.Events ?? new List<SessionEvent>();
        double total = Math.Max(1, (s.End - s.Start).TotalSeconds);
        double U(DateTime t) => Math.Clamp((t - s.Start).TotalSeconds / total, 0, 1);
        bool strip = events.Count > 0 && SessFpsChartBlock.Visibility == Visibility.Visible;
        SessEventStrip.Visibility = SessEventLegend.Visibility = strip ? Visibility.Visible : Visibility.Collapsed;
        SessEventStrip.SetItems(events.Select(e => new EventStrip.Item
        {
            Kind = e.Kind, From = U(e.At), To = U(e.At.AddSeconds(e.Seconds)),
            Text = e.At.ToString("HH:mm:ss") + " · " + EventText(e, s),
        }));
        LegMin.Visibility = events.Any(e => e.Kind is "minimized" or "pause") ? Visibility.Visible : Visibility.Collapsed;
        LegDrv.Visibility = events.Any(e => e.Kind == "driver") ? Visibility.Visible : Visibility.Collapsed;
        LegPing.Visibility = events.Any(e => e.Kind == "ping") ? Visibility.Visible : Visibility.Collapsed;
        LegBg.Visibility = events.Any(e => e.Kind == "culprit") ? Visibility.Visible : Visibility.Collapsed;

        SessEventsBlock.Visibility = events.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SessEventList.ItemsSource = events.Take(40).Select(e => new EventRow
        {
            Time = e.At.ToString("HH:mm:ss"),
            Text = EventText(e, s),
            Ink = e.Kind == "driver" ? Palette.Bad : e.Kind == "culprit" ? Palette.Ink1 : Palette.Ink2,
        }).ToList();
    }

    // ================================================================== вид рывков
    private void ShowStutters(GameSession s)
    {
        var kinds = s.StutterKinds;
        bool show = s.AvgFps > 0 && kinds is { Count: > 0 };
        SessStutterBlock.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        int total = Math.Max(1, kinds.Sum(k => k.Count));
        SessStutterNote.Text = L.T("Вид определяется по тому, что показывали датчики в момент рывка, — это оценка, а не точный диагноз.");
        bool rough = s.Smoothness < 70;
        var main = kinds.FirstOrDefault(k => k.Kind != "other");
        SessStutters.ItemsSource = kinds.Select(k => new StutterRow
        {
            Title = Cap(SessionAnalysis.StutterTitle(k.Kind)),
            Count = L.F("рывков: {0} ({1:0} %)", k.Count, 100.0 * k.Count / total),
            Advice = SessionAnalysis.StutterAdvice(k),
            Ink = k.Kind == "other" ? Palette.Ink3 : rough && ReferenceEquals(k, main) ? Palette.Warn : Palette.Ink1,
        }).ToList();
    }

    // ================================================================== фоновые программы
    private void ShowBackground(GameSession s)
    {
        var bg = s.Background;
        SessBgBlock.Visibility = bg is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        if (bg is not { Count: > 0 }) return;
        SessBg.ItemsSource = bg.Select(c => new BackgroundRow
        {
            Name = SessionCulprits.Pretty(c.Name),
            Text = L.F("в среднем: {0}; пик: {1}", SessionCulprits.Load(c.Cpu, c.DiskMBs), SessionCulprits.Load(c.PeakCpu, c.PeakDiskMBs)),
            Ink = c.PeakCpu >= 15 || c.PeakDiskMBs >= 30 ? Palette.Warn : Palette.Ink1,
        }).ToList();
    }

    // ================================================================== сравнение двух сессий
    private void FillCompareRows(GameSession s, List<GameSession> same)
    {
        if (same.Count < 2) { SessCompare.ItemsSource = null; return; }
        var rows = same.Take(8).ToList();
        if (comparePartner != null && !rows.Contains(comparePartner) && same.Contains(comparePartner)) rows.Add(comparePartner);
        SessCompare.ItemsSource = rows.Select(x => new CompareRow
        {
            Session = x,
            When = x.Start.ToString(L.Pick("d MMM, HH:mm", "MMM d, HH:mm"), L.Culture),
            Length = Fmt.Duration(x.Duration),
            Fps = x.AvgFps > 0 ? x.AvgFps.Value.ToString("0") : "—",
            Low = x.Low1Fps > 0 ? x.Low1Fps.Value.ToString("0") : "—",
            Smooth = x.Smoothness is double sm ? sm.ToString("0") : "—",
            Temp = x.MaxTemp > 0 ? x.MaxTemp.ToString("0") : "—",
            Gpu = x.AvgGpu > 0 ? x.AvgGpu.ToString("0") + L.Pick(" %", "%") : "—",
            Ram = x.MinRamGb > 0 ? x.MinRamGb.ToString("0.0", L.Culture) + " " + Fmt.GbUnit : "—",
            Ink = ReferenceEquals(x, s) || ReferenceEquals(x, comparePartner) ? Palette.Ink1 : Palette.Ink2,
            Weight = ReferenceEquals(x, s) ? FontWeights.SemiBold : FontWeights.Normal,
            PartnerMark = ReferenceEquals(x, comparePartner) ? Visibility.Visible : Visibility.Collapsed,
            Hint = ReferenceEquals(x, s) ? null : L.T("Сравнить эту сессию с этой строкой рядом"),
        }).ToList();
    }

    private void SessCompareRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: GameSession x } || SessionList.SelectedItem is not GameSession s || ReferenceEquals(x, s)) return;
        PickPartner(s, x);
    }

    private void BtnBestCompare_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not GameSession s || BtnBestCompare.Tag is not GameSession best || ReferenceEquals(best, s)) return;
        PickPartner(s, best);
    }

    private void PickPartner(GameSession s, GameSession other)
    {
        comparePartner = other;
        var same = sessions.Where(x => x.Game == s.Game).OrderByDescending(x => x.Start).ToList();
        FillCompareRows(s, same);
        ShowPair(s);
        ShowBest(s, same);
        SessPairBlock.BringIntoView();
    }

    private void ShowPair(GameSession s)
    {
        var o = comparePartner;
        bool show = o != null && !ReferenceEquals(o, s) && o.Game == s.Game;
        SessPairBlock.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        SessPairLabel.Source = L.F("СРАВНЕНИЕ С СЕССИЕЙ {0}", SessionCompare.When(o)).ToUpper(L.Culture);
        SessPairThis.Text = L.T("эта");
        SessPairOther.Text = SessionCompare.When(o);
        SessPair.ItemsSource = SessionCompare.Lines(s, o).Skip(1).Select(l => new PairRow
        {
            Label = Cap(l.Label), This = l.This, Other = l.Other, Delta = l.Delta, IsText = l.IsText, IsNote = l.Label == L.T("заметка"),
            Ink = l.Better > 0 ? Palette.Ink1 : l.Better < 0 ? Palette.Warn : Palette.Ink3,
        }).ToList();
        var diff = SessionCompare.Differences(o, s);
        if (diff.Count == 0)
            diff.Add(L.T("Условия, которые записывает программа (драйвер, схема питания, свободная память, заметки), не отличались — или у старой сессии они не записывались."));
        SessPairDiff.ItemsSource = diff;
    }

    // ================================================================== лучшие условия
    private void ShowBest(GameSession s, List<GameSession> same)
    {
        bool show = same.Count >= 2 || s.Smoothness != null;
        SessBestBlock.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var r = SessionCompare.Best(same, s);
        SessBestHead.Text = r.Headline;
        SessBestHead.Foreground = r.Enough ? Palette.Ink1 : Palette.Ink2;
        // Лучшая уже стоит в сравнении рядом — отличия там, второй раз их не повторяем.
        bool inPair = r.Best != null && ReferenceEquals(r.Best, comparePartner) && SessPairBlock.Visibility == Visibility.Visible;
        SessBestDiff.ItemsSource = inPair ? new List<string> { L.T("Чем она отличалась — в сравнении выше.") } : r.Differences;
        BtnBestCompare.Tag = r.Best;
        BtnBestCompare.Visibility = r.Best != null && !ReferenceEquals(r.Best, s) && !ReferenceEquals(r.Best, comparePartner) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================================================================== нагрузка самой программы
    private void ShowAppLoad(GameSession s)
    {
        SessAppLoad.Visibility = s.AppCpuPct != null ? Visibility.Visible : Visibility.Collapsed;
        if (s.AppCpuPct is double p)
            SessAppLoad.Text = L.F("Сама программа во время записи занимала в среднем {0} процессора (вместе с записью кадров).", SessionCulprits.Pct(p));
    }
}
