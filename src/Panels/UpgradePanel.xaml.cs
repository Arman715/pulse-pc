using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PulsePC.Services;

namespace PulsePC.Panels;

/// <summary>
/// Раздел «Совет по апгрейду»: выводы службы UpgradeAdvisor по убыванию пользы, планки и слоты памяти,
/// что показали записанные игры и на каких дисках стоят игры. Только чтение; ничего не меняет.
/// </summary>
public partial class UpgradePanel : UserControl, ISystemPanel
{
    private const string Nb = " ";
    private PanelHost host;
    private volatile bool reading;
    private DateTime readAt;

    public UpgradePanel()
    {
        InitializeComponent();
        RamTotal.Unit = Fmt.GbUnit;
    }

    public void Attach(PanelHost h) => host = h;

    public void Shown()
    {
        if (host == null || reading) return;
        // Железо не меняется на ходу: перечитываем не чаще раза в 5 минут (сессии — каждый раз, они дешёвые).
        if ((DateTime.Now - readAt).TotalMinutes < 5 && last != null)
        {
            last.Facts = UpgradeAdvisor.Facts(host.Sessions());
            Show(last);
            return;
        }
        reading = true;
        host.BeginBusy();
        Task.Run(() =>
        {
            AdvisorInput input = null;
            try { input = UpgradeAdvisor.Read(host); }
            catch { }
            host.Ui(() =>
            {
                reading = false;
                host.EndBusy();
                if (input == null) { State.Text = L.T("Не удалось прочитать железо."); return; }
                readAt = DateTime.Now;
                last = input;
                Show(input);
            });
        });
    }

    private AdvisorInput last;

    // ================================================================== вывод
    private sealed class AdviceItem
    {
        public string Number { get; init; }
        public Severity Level { get; init; }
        public string Title { get; init; }
        public string Tag { get; init; }
        public string Why { get; init; }
        public string What { get; init; }
        public Visibility WhatVisible { get; init; }
        public string NotIf { get; init; }
        public Visibility NotIfVisible { get; init; }
    }

    private void Show(AdvisorInput x)
    {
        var advice = UpgradeAdvisor.Advise(x);
        var f = x.Facts;
        State.Text = UpgradeAdvisor.Verdict(advice, f);
        Basis.Text = f.Count == 0 ? L.T("Записанных игр пока нет — совет только по железу.")
            : L.F("Записано {0}, {1} игры, с {2:d MMMM} по {3:d MMMM}.", Fmt.Plural(f.Count, "сессия", "сессии", "сессий"), Hours(f.Hours), f.From, f.To);

        var todo = advice.Where(a => !a.Fine).ToList();
        AdviceList.ItemsSource = todo.Select((a, i) => new AdviceItem
        {
            Number = $"{i + 1}.",
            Level = a.Level,
            Title = UpgradeAdvisor.TitleText(a),
            Tag = (a.Free ? L.T("бесплатно") + " · " : "") + UpgradeAdvisor.PartName(a.Part),
            Why = a.Why,
            What = a.What,
            WhatVisible = a.What.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
            NotIf = a.NotIf,
            NotIfVisible = a.NotIf.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
        }).ToList();
        AdviceBlock.Visibility = todo.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var fine = advice.Where(a => a.Fine).ToList();
        FineList.ItemsSource = fine.Select(a => new AdviceItem { Title = UpgradeAdvisor.TitleText(a), Why = a.Why }).ToList();
        FineBlock.Visibility = fine.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        FewNote.Visibility = f.Count < UpgradeAdvisor.MinSessions ? Visibility.Visible : Visibility.Collapsed;
        FewText.Text = (f.Count == 0
                           ? L.F("Чтобы судить, что ограничивает игры, нужно хотя бы {0} записанные сессии, а пока нет ни одной.", UpgradeAdvisor.MinSessions)
                           : L.F("Чтобы судить, что ограничивает игры, нужно хотя бы {0} записанные сессии, а сейчас {1}.", UpgradeAdvisor.MinSessions, f.Count))
                       + " " + L.T("Пока здесь только то, что видно по железу. Сыграйте ещё — совет дополнится сам.");

        ShowRam(x.Ram);
        ShowFacts(f);
        ShowDrives(x.Drives);
    }

    private static string Hours(double h) => h >= 1 ? h.ToString("0", L.Culture) + L.Pick(" ч", " h") : (h * 60).ToString("0", L.Culture) + L.Pick(" мин", " min");

    // ================================================================== память
    private void ShowRam(RamInfo ram)
    {
        Slots.Children.Clear();
        if (!ram.Loaded || ram.Sticks.Count == 0)
        {
            RamEmpty.Text = L.T("Windows не сообщает, какие планки стоят.");
            RamEmpty.Visibility = Visibility.Visible;
            RamBody.Visibility = Visibility.Collapsed;
            return;
        }
        RamEmpty.Visibility = Visibility.Collapsed;
        RamBody.Visibility = Visibility.Visible;
        RamTotal.Value = ram.TotalGb;
        int mts = ram.Sticks.Max(s => s.Mts);
        RamTotalCaption.Text = $"{ram.Type}{(mts > 0 ? "-" + mts : "")}{(ram.Laptop ? " SO-DIMM" : "")}".Trim();
        SlotsCaption.Text = ram.Slots > 0
            ? Ink.Track(L.F("СЛОТЫ: {0}, СВОБОДНО {1}", ram.Slots, ram.FreeSlots))
            : Ink.Track("СЛОТЫ: WINDOWS НЕ СООБЩАЕТ");

        foreach (var s in ram.Sticks)
            Slots.Children.Add(SlotRow(true, $"{s.SizeGb:0}{Nb}{Fmt.GbUnit}", $"{(s.Maker.Length > 0 ? s.Maker + " " : "")}{s.Part}".Trim(),
                s.Slot + (s.Mts > 0 ? " · " + s.Mts + L.Pick(" МТ/с", " MT/s") : "")));
        for (int i = 0; i < Math.Max(0, ram.FreeSlots); i++)
            Slots.Children.Add(SlotRow(false, L.Pick("пусто", "empty"),"", L.T("свободный слот")));

        var notes = new List<string>();
        if (ram.MaxGb > 0) notes.Add(L.F("Плата принимает до {0:0} ГБ.", ram.MaxGb));
        if (UpgradeAdvisor.Mixed(ram)) notes.Add(L.T("Планки разные: вся память работает на частоте самой медленной."));
        else if (ram.Sticks.Count > 1) notes.Add(L.T("Планки одинаковые."));
        RamNote.Text = string.Join(" ", notes);
    }

    private static FrameworkElement SlotRow(bool used, string size, string model, string where)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var glyph = new DimmGlyph { Used = used, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(glyph);
        var sz = new TextBlock { Text = size, Style = (Style)Application.Current.FindResource("MonoNum"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 14, 0) };
        if (!used) sz.Foreground = Palette.Ink3;
        Grid.SetColumn(sz, 1);
        g.Children.Add(sz);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (model.Length > 0) info.Children.Add(new TextBlock { Text = model, Style = (Style)Application.Current.FindResource("Body"), Foreground = Palette.Ink1, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
        info.Children.Add(new TextBlock { Text = where, Style = (Style)Application.Current.FindResource("Caption") });
        Grid.SetColumn(info, 2);
        g.Children.Add(info);
        return g;
    }

    // ================================================================== из сессий
    private sealed class FactItem
    {
        public string Name { get; init; }
        public string Value { get; init; }
        public string Note { get; init; }
        public Brush Ink { get; init; }
        public Visibility MarkVisible { get; init; }
    }

    private void ShowFacts(SessionFacts f)
    {
        FactsEmpty.Visibility = f.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FactsBody.Visibility = f.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (f.Count == 0) return;
        var rows = new List<FactItem>();
        FactItem Row(string name, string value, string note, bool warn) => new()
        {
            Name = name, Value = value, Note = note, Ink = warn ? Palette.Warn : Palette.Ink1, MarkVisible = warn ? Visibility.Visible : Visibility.Collapsed,
        };
        string Of(int k, int n) => n == 0 ? L.T("нет замера") : string.Format(L.Culture, L.Pick("{0} из {1}", "{0} of {1}"), k, n);
        string Pct(double v) => double.IsNaN(v) ? L.T("нет замера") : $"{v:0}" + L.Pick(Nb + "%", "%");

        rows.Add(Row(L.T("Доступной памяти меньше 1 ГБ"), Of(f.RamShort, f.RamMeasured),
            f.RamMeasured > 0 ? L.Pick("меньше всего ", "lowest ") + Fmt.Gb(f.RamWorst) : "",
            f.RamMeasured > 0 && f.RamShort * 4 >= f.RamMeasured && f.RamShort > 0));
        rows.Add(Row(L.T("Видеокарта загружена полностью"), Pct(f.GpuBound), L.T("доля времени"), false));
        rows.Add(Row(L.T("Видеопамять занята почти вся"), Of(f.VramFull, f.VramN),
            f.VramN > 0 ? L.F("до {0:0.0} из {1:0} ГБ", f.VramMax, f.VramTotal) : "",
            f.VramN > 0 && f.VramFull * 2 >= f.VramN && f.VramFull > 0));
        rows.Add(Row(L.T("Видеокарта ждала процессор"), Pct(f.CpuBound), f.TopCpuGame.Length > 0 && f.CpuBound >= 10 ? L.Pick("больше всего — ", "most in ") + f.TopCpuGame : L.T("доля времени"), false));
        rows.Add(Row(L.T("Процессор ниже базовой частоты"), Pct(f.CpuSlow), L.T("доля времени в игре"), !double.IsNaN(f.CpuSlow) && f.CpuSlow >= 15));
        FactList.ItemsSource = rows;
    }

    // ================================================================== диски
    private sealed class DriveItem
    {
        public string Letter { get; init; }
        public string Disk { get; init; }
        public string Games { get; init; }
        public string Kind { get; init; }
        public Visibility MarkVisible { get; init; }
    }

    private void ShowDrives(List<GameDrive> drives)
    {
        DrivesEmpty.Text = L.T("Установленных игр не найдено. Программа ищет их в магазинах (Steam, Epic, GOG, Xbox и других) и на дисках; игру, которой нет в списке, можно добавить вручную на вкладке «Сессии».");
        DrivesEmpty.Visibility = drives.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DriveList.ItemsSource = drives.Select(d => new DriveItem
        {
            Letter = d.Letter + ":",
            Disk = d.Disk.Length > 0 ? d.Disk : L.T("диск не определён"),
            Games = Fmt.Plural(d.Games.Count, "игра", "игры", "игр") + ": " + (d.Games.Count > 5
                ? string.Format(L.Culture, L.Pick("{0} и ещё {1}", "{0} and {1} more"), string.Join(", ", d.Games.Take(5)), d.Games.Count - 5)
                : string.Join(", ", d.Games)),
            Kind = d.Kind switch { "HDD" => L.T("жёсткий диск"), "SSD" => "SSD", "NVMe" => "NVMe", "USB" => L.T("по USB"), _ => L.T("тип неизвестен") },
            MarkVisible = d.Kind is "HDD" or "USB" ? Visibility.Visible : Visibility.Collapsed,
        }).ToList();
    }
}

/// <summary>
/// Слот памяти, вид сверху: занятый — планка тушью с ключом-вырезом и контактами; свободный — пунктирный контур слота.
/// </summary>
public sealed class DimmGlyph : FrameworkElement
{
    public bool Used { get; set; }

    protected override Size MeasureOverride(Size s) => new(0, double.IsNaN(Height) ? 18 : Height);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 20 || h < 6) return;
        if (!Used)
        {
            dc.DrawRectangle(null, Ink.Pen(Palette.Ink4, 1, true), new Rect(0.5, 0.5, w - 1, h - 1));
            return;
        }
        // Планка: корпус, ключ-вырез на 58 % длины снизу, микросхемы — заштрихованные прямоугольники.
        var body = new Rect(0.5, 0.5, w - 1, h - 1);
        dc.DrawRectangle(Palette.Base, Ink.Pen(Palette.Ink1, 1), body);
        double key = Math.Round(w * 0.58);
        dc.DrawRectangle(Palette.Base, null, new Rect(key - 2, h - 4, 5, 4));
        dc.DrawLine(Ink.Pen(Palette.Ink1, 1), new Point(key - 2.5, h - 0.5), new Point(key - 2.5, h - 4.5));
        dc.DrawLine(Ink.Pen(Palette.Ink1, 1), new Point(key + 3.5, h - 0.5), new Point(key + 3.5, h - 4.5));
        dc.DrawLine(Ink.Pen(Palette.Ink1, 1), new Point(key - 2.5, h - 4.5), new Point(key + 3.5, h - 4.5));
        var chip = SegmentBar.Pattern(0, true);
        int chips = 8;
        double cw = (w - 16) / chips;
        for (int i = 0; i < chips; i++)
        {
            double x = Math.Round(8 + i * cw + 2);
            var r = new Rect(x + 0.5, 3.5, Math.Max(2, Math.Round(cw - 4)) - 1, h - 9);
            dc.DrawRectangle(chip, Ink.Pen(Palette.Ink1, 1), r);
        }
    }
}
