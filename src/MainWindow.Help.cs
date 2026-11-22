using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PulsePC.Help;

namespace PulsePC;

/// <summary>
/// Лист «Справка»: открывается кнопкой «?» в заголовке окна. Слева разделы, справа текст, нарисованный стилями программы;
/// ссылки «Открыть …» ведут прямо в нужную вкладку или раздел «Системы».
/// </summary>
public partial class MainWindow
{
    private const int PgHelp = 8;
    private bool helpBuilt;

    private void Help_Click(object sender, RoutedEventArgs e) => ShowHelp();

    /// <summary>Открыть справку (section — заголовок раздела или null — первый либо последний открытый).</summary>
    public void ShowHelp(string section = null)
    {
        BuildHelp();
        if (page != PgHelp)
        {
            int old = page;
            // Вкладку снимаем, иначе повторный щелчок по ней не вернёт со справки.
            foreach (var child in Tabs.Children) if (child is RadioButton r) r.IsChecked = false;
            page = PgHelp;
            for (int i = 0; i < pages.Length; i++) pages[i].Visibility = i == PgHelp ? Visibility.Visible : Visibility.Collapsed;
            Motion.Reveal(pages[PgHelp], 0, 12, Motion.Fast, Motion.Normal);
            MoveTabLine(true);
            UpdateSheetNo();
            PlayPageHeader(PgHelp);
            if (old == PgNet) net.Active = recorder.IsRecording;
        }
        BtnHelp.Foreground = Palette.Accent;
        // Заголовки разделов уже на языке интерфейса: искать и по исходному русскому названию.
        var s = section == null ? null : HelpContent.Sections.FirstOrDefault(x => x.Title == section || x.Title == L.T(section));
        if (s != null) HelpList.SelectedItem = s;
        else if (HelpList.SelectedItem == null) HelpList.SelectedIndex = 0;
    }

    private void BuildHelp()
    {
        if (helpBuilt) return;
        helpBuilt = true;
        var view = new System.Windows.Data.ListCollectionView(HelpContent.Sections);
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(HelpSection.Group)));
        HelpList.ItemsSource = view;
    }

    private void HelpList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HelpList.SelectedItem is not HelpSection s) return;
        // Group — русский ключ группировки (переводится только при показе), Title уже переведён.
        HelpKicker.Source = L.T(s.Group) + "  ·  " + s.Title.ToUpperInvariant();
        HelpBody.Children.Clear();
        HelpBody.Children.Add(new TextBlock { Text = s.Title, Style = (Style)FindResource("Verdict") });
        if (s.Lead.Length > 0) HelpBody.Children.Add(Text(s.Lead, "Body", new Thickness(0, 8, 0, 0)));
        foreach (var b in s.Blocks) HelpBody.Children.Add(Render(b));
        HelpScroll.ScrollToTop();
        Motion.Reveal(HelpBody, 0, 6);
    }

    private FrameworkElement Render(HelpBlock block)
    {
        switch (block)
        {
            case H h:
                var head = new StackPanel { Margin = new Thickness(0, 26, 0, 0) };
                head.Children.Add(new TrackedText { Source = h.Text.ToUpperInvariant(), Style = (Style)FindResource("Label"), Margin = new Thickness(0, 0, 0, 6) });
                head.Children.Add(new Rectangle { Style = (Style)FindResource("Hairline") });
                return head;

            case P p:
                return Text(p.Text, "Body", new Thickness(0, 12, 0, 0));

            case Steps st:
            {
                var list = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
                for (int i = 0; i < st.Items.Length; i++)
                {
                    var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
                    var num = new TextBlock { Text = (i + 1).ToString("00"), Style = (Style)FindResource("MonoNum"), Foreground = Palette.Accent,
                                              TextAlignment = TextAlignment.Left, HorizontalAlignment = HorizontalAlignment.Left,
                                              VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 16, 0) };
                    DockPanel.SetDock(num, Dock.Left);
                    row.Children.Add(num);
                    row.Children.Add(Text(st.Items[i], "Body", new Thickness(0)));
                    list.Children.Add(row);
                }
                return list;
            }

            case Dash d:
            {
                var list = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
                foreach (var item in d.Items)
                {
                    var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                    row.Children.Add(Text("—", "Body", new Thickness(0)));
                    row.Children.Add(Text(item, "Body", new Thickness(18, 0, 0, 0)));
                    list.Children.Add(row);
                }
                return list;
            }

            case Rows r:
            {
                var list = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
                foreach (var (name, text) in r.Items)
                {
                    var grid = new Grid { Margin = new Thickness(0, 10, 0, 10) };
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var title = Text(name, "RowTitle", new Thickness(0, 0, 16, 0));
                    var body = Text(text, "Body", new Thickness(0));
                    Grid.SetColumn(body, 1);
                    grid.Children.Add(title);
                    grid.Children.Add(body);
                    list.Children.Add(new Border { BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Child = grid });
                }
                return list;
            }

            case Note n:
            {
                var dock = new DockPanel();
                var marker = new StateMarker { Level = Severity.Info, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 10, 0) };
                DockPanel.SetDock(marker, Dock.Left);
                dock.Children.Add(marker);
                dock.Children.Add(Text(n.Text, "Body", new Thickness(0)));
                return new Border
                {
                    BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1), Background = (Brush)FindResource("Raised"),
                    Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 16, 0, 0), Child = dock,
                };
            }

            case Go g:
            {
                // Раздел «Ноутбук» есть только на ноутбуке — на настольном ПК ссылка в него не показывается.
                if (g.Target == "sys:Ноутбук" && !IsLaptopPc) return new TextBlock { Visibility = Visibility.Collapsed };
                var link = new Button { Content = g.Text + "  →", Tag = g.Target, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
                link.Click += (_, _) => GoFromHelp(g.Target);
                return link;
            }
        }
        return new TextBlock();
    }

    private TextBlock Text(string text, string style, Thickness margin) =>
        new() { Text = text, Style = (Style)FindResource(style), TextWrapping = TextWrapping.Wrap, Margin = margin };

    private void GoFromHelp(string target)
    {
        if (target == "scan") { ShowScan(); return; }
        if (target == "whyslow") { WhySlow_Click(this, new RoutedEventArgs()); return; }
        OpenTarget(target);
    }
}
