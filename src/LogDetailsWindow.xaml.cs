using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PulsePC.Services;

namespace PulsePC;

/// <summary>
/// «Подробнее и что делать» для замечания из журналов Windows: что это значит, план по шагам, состояние дисков (для дисков),
/// сами записи простыми словами и открытие ровно этих записей в «Просмотре событий». Только чтение.
/// </summary>
public partial class LogDetailsWindow : Window
{
    private readonly string kind;
    private readonly IReadOnlyList<PhysDisk> disks;
    private readonly AppSettings settings;
    private LogDetailsReport report;

    public LogDetailsWindow(Window owner, string kind, string title, Severity level, IReadOnlyList<PhysDisk> disks, AppSettings settings)
    {
        InitializeComponent();
        WindowFit.Attach(this);
        Owner = owner;
        this.kind = kind;
        this.disks = disks ?? Array.Empty<PhysDisk>();
        this.settings = settings;
        Title = L.T("Пульс ПК") + " — " + title;
        Heading.Text = title;
        Mark.Level = level;
        Motion.Init(this);
        Loaded += (_, _) => Load();
    }

    private void Load()
    {
        Task.Run(() =>
        {
            LogDetailsReport r = null;
            try { r = LogDetails.Build(kind, disks, settings); } catch (Exception ex) { Log.Error("подробнее о записях журнала", ex); }
            Dispatcher.BeginInvoke(new Action(() => Show(r)));
        });
    }

    private void Show(LogDetailsReport r)
    {
        report = r;
        Loading.Visibility = Visibility.Collapsed;
        if (r == null) { Intro.Text = L.T("Журнал Windows не прочитался."); return; }
        if (r.Failed) Intro.Text = L.T("Журнал Windows не прочитался.");
        Body.Visibility = Visibility.Visible;
        BtnOpen.IsEnabled = r.Total > 0 || r.Failed;
        if (!r.Failed) Intro.Text = r.Total > 0
            ? L.F("Записей: {0}, с {1:d MMMM HH:mm}.", r.Total, r.Since)
            : L.F("Новых записей нет с {0:d MMMM HH:mm}.", r.Since);
        Meaning.Text = r.Meaning;

        Steps.Children.Clear();
        for (int i = 0; i < r.Steps.Count; i++)
        {
            var g = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var n = new TextBlock { Text = (i + 1) + ".", FontWeight = FontWeights.SemiBold };
            n.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            var t = new TextBlock { Text = r.Steps[i] };
            t.SetResourceReference(StyleProperty, "Body");
            Grid.SetColumn(t, 1);
            g.Children.Add(n); g.Children.Add(t);
            Steps.Children.Add(g);
        }

        // Диски — для записей о дисках: что каждый сообщает сам о себе.
        DisksBlock.Visibility = kind == LogMarks.Disk && r.Disks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Disks.Children.Clear();
        foreach (var d in r.Disks)
        {
            bool bad = LogDetails.DiskBad(d);
            var parts = new List<string>();
            if (d.Kind != "") parts.Add(d.Kind);
            if (d.Letters.Count > 0) parts.Add(string.Join(", ", d.Letters.Select(l => l + ":")));
            if (d.Hours is double h) parts.Add(L.F("наработка {0:N0} ч", h));
            if (d.Temp is double tc && tc > 0) parts.Add(L.F("{0:0} °C", tc));
            if (d.Wear is double w) parts.Add(L.F("износ {0:0} %", w));
            string state = d.Health != "" && !d.Health.Equals("Healthy", StringComparison.OrdinalIgnoreCase) ? L.F("сообщает: «{0}»", d.Health)
                : d.ReadErrors > 0 ? L.F("неисправленных ошибок чтения: {0:0}", d.ReadErrors)
                : d.Wear >= 80 ? L.F("износ {0:0} %", d.Wear)
                : d.Health == "" ? L.T("нет данных") : L.T("исправен");
            Disks.Children.Add(Row(d.Name, string.Join(" · ", parts), state, bad));
        }

        EventsCaption.Text = r.Failed ? L.T("Записи не прочитались — их можно посмотреть в «Просмотре событий».") : r.Total > r.Events.Count
            ? L.F("Последние {0} из {1}. Все — в «Просмотре событий».", r.Events.Count, r.Total)
            : r.Total == 0 ? L.T("Записей нет.") : "";
        Events.Children.Clear();
        foreach (var e in r.Events)
        {
            var box = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };
            var head = new DockPanel();
            var time = new TextBlock { Text = e.At.ToString("dd.MM HH:mm:ss", L.Culture), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
            time.SetResourceReference(StyleProperty, "MonoNum");
            DockPanel.SetDock(time, Dock.Left);
            var src = new TextBlock { Text = $"{e.Provider} · {e.Id}", Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            src.SetResourceReference(StyleProperty, "Caption");
            DockPanel.SetDock(src, Dock.Right);
            var plain = new TextBlock { Text = e.Plain, TextWrapping = TextWrapping.Wrap };
            plain.SetResourceReference(StyleProperty, "RowTitle");
            head.Children.Add(time); head.Children.Add(src); head.Children.Add(plain);
            box.Children.Add(head);
            if (e.Message != "")
            {
                var msg = new TextBlock { Text = L.T("Текст Windows: ") + e.Message, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
                msg.SetResourceReference(StyleProperty, "Caption");
                box.Children.Add(msg);
            }
            Events.Children.Add(new Border { Child = box, BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = (Brush)FindResource("Line") });
        }
    }

    private Border Row(string title, string detail, string value, bool bad)
    {
        var g = new Grid { Margin = new Thickness(0, 10, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var s = new StackPanel();
        var t = new TextBlock { Text = title };
        t.SetResourceReference(StyleProperty, "RowTitle");
        var d = new TextBlock { Text = detail, Margin = new Thickness(0, 2, 0, 0) };
        d.SetResourceReference(StyleProperty, "Caption");
        s.Children.Add(t); s.Children.Add(d);
        var v = new TextBlock { Text = value, Margin = new Thickness(20, 0, 2, 0), VerticalAlignment = VerticalAlignment.Top, TextAlignment = TextAlignment.Right };
        v.SetResourceReference(StyleProperty, "Body");
        v.SetResourceReference(TextBlock.ForegroundProperty, bad ? "Bad" : "Ink2");
        Grid.SetColumn(v, 1);
        g.Children.Add(s); g.Children.Add(v);
        return new Border { Child = g, BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = (Brush)FindResource("Line") };
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (report == null) return;
        BtnOpen.IsEnabled = false;
        var since = report.Since;
        Task.Run(() =>
        {
            var (ok, text) = LogDetails.OpenInEventViewer(kind, since);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                BtnOpen.IsEnabled = true;
                Footer.Text = ok ? L.T("Открыто в «Просмотре событий»: там только эти записи. Журналы Windows не менялись.")
                                 : L.F("Не открылось: {0}", text);
            }));
        });
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
