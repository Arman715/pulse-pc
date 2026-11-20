using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PulsePC.Services;

namespace PulsePC;

/// <summary>
/// Маленькие кнопки в заголовке окна: язык (RU/EN), тема (светлая/тёмная), обратная связь (проблема, отзыв, звезда на GitHub).
/// Язык и тема меняются после перезапуска — кнопка спрашивает подтверждение. И просьба оценить программу на вкладке «Сессии».
/// </summary>
public partial class MainWindow
{
    private Popup titlePopup;

    private void InitTitleButtons()
    {
        BtnLang.Content = L.En ? "RU" : "EN";
        BtnLang.ToolTip = L.En ? "Язык: русский (программа перезапустится)" : "Language: English (the app will restart)";
        System.Windows.Automation.AutomationProperties.SetName(BtnLang, L.En ? "Switch language to Russian" : "Сменить язык на английский");
        BtnTheme.Content = Theme.Dark ? "" : "";          // солнце / луна
        BtnTheme.ToolTip = Theme.Dark ? L.T("Светлая тема") : L.T("Тёмная тема");
        System.Windows.Automation.AutomationProperties.SetName(BtnTheme, (string)BtnTheme.ToolTip);
        UpdateRateBar();
        if (TabBar.Parent is FrameworkElement title) title.SizeChanged += (_, _) => FitTitle();
        ContentRendered += (_, _) => FitTitle();
    }

    // ================================================================== заголовок в узком окне

    private readonly Dictionary<RadioButton, object> tabIcons = new();

    /// <summary>
    /// Вкладки, кнопки языка, темы, обратной связи и окна не помещаются в одну строку (узкое окно, длинные английские
    /// названия) — вкладки показываются без пиктограмм и плотнее; когда место есть — снова с пиктограммами.
    /// </summary>
    private void FitTitle()
    {
        if (TabBar.Parent is not Grid title || title.ActualWidth <= 0) return;
        var tabs = Tabs.Children.OfType<RadioButton>().ToList();
        bool compact = tabIcons.Count > 0;
        double others = 0;
        foreach (UIElement child in title.Children)
            if (child != TabBar && Grid.GetColumn(child) != 2 && child is FrameworkElement fe && fe.Visibility == Visibility.Visible)
            {
                fe.Measure(new Size(double.PositiveInfinity, title.ActualHeight));
                others += fe.DesiredSize.Width;
            }
        TabBar.Measure(new Size(double.PositiveInfinity, title.ActualHeight));
        double tabsNow = TabBar.DesiredSize.Width;
        double tabsFull = compact ? tabsNow + tabs.Count * (24 + 6) : tabsNow;     // пиктограмма 16 + отступ 8, поля 12 вместо 9
        bool fits = others + tabsFull + 16 <= title.ActualWidth;
        if (Environment.GetEnvironmentVariable("PULSEPC_DEBUG_TITLE") == "1")
            Log.Info($"FitTitle: width {title.ActualWidth:0}, others {others:0}, tabs {tabsNow:0}/{tabsFull:0}, compact {compact}, fits {fits}");
        if (fits && compact)
        {
            foreach (var (tab, icon) in tabIcons) tab.Tag = icon;
            tabIcons.Clear();
        }
        else if (!fits && !compact)
        {
            foreach (var tab in tabs) { tabIcons[tab] = tab.Tag; tab.Tag = null; }
        }
        if ((fits && compact) || (!fits && !compact)) Dispatcher.BeginInvoke(new Action(() => MoveTabLine(false)), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ================================================================== язык и тема

    private void Lang_Click(object sender, RoutedEventArgs e)
    {
        string target = L.En ? "ru" : "en";
        // Текст — на обоих языках: его должен понять и тот, кто нажал случайно.
        string text = target == "en"
            ? "Switch Pulse PC to English? The app will restart; recording and settings are kept.\nПереключить на английский? Программа перезапустится."
            : "Переключить Пульс ПК на русский? Программа перезапустится, запись и настройки сохранятся.\nSwitch to Russian? The app will restart.";
        ShowTitlePopup(BtnLang, text, new[]
        {
            (target == "en" ? "Restart in English" : "Перезапустить на русском", true, (Action)(() => SaveAndRestart(L.SettingKey, target))),
            (target == "en" ? "Cancel · Отмена" : "Отмена · Cancel", false, (Action)null),
        });
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        string target = Theme.Dark ? "light" : "dark";
        string text = target == "dark"
            ? L.T("Включить тёмную тему? Программа перезапустится, запись и настройки сохранятся.")
            : L.T("Включить светлую тему? Программа перезапустится, запись и настройки сохранятся.");
        ShowTitlePopup(BtnTheme, text, new[]
        {
            (L.T("Перезапустить"), true, (Action)(() => SaveAndRestart(Theme.SettingKey, target))),
            (L.T("Отмена"), false, (Action)null),
        });
    }

    private void SaveAndRestart(string key, string value)
    {
        settings.Set(key, value);
        settings.Save();
        App.Restart();
    }

    // ================================================================== обратная связь

    private void Feedback_Click(object sender, RoutedEventArgs e)
    {
        ShowTitlePopup(BtnFeedback, null, new[]
        {
            (L.T("Сообщить о проблеме…"), false, (Action)(() => OpenFeedback(Feedback.Kind.Problem))),
            (L.T("Оставить отзыв и оценку…"), false, (Action)(() => OpenFeedback(Feedback.Kind.Review))),
            (L.T("Поставить звезду на GitHub"), false, (Action)Feedback.OpenRepo),
        }, menu: true);
    }

    private void OpenFeedback(Feedback.Kind kind)
    {
        var w = new FeedbackWindow(kind, this, admin, settings);
        if (kind == Feedback.Kind.Review) w.Closed += (_, _) => { Feedback.RatingDone(settings); UpdateRateBar(); };
        w.Show();
    }

    /// <summary>Небольшая панель под кнопкой заголовка: текст и кнопки (или пункты меню).</summary>
    private void ShowTitlePopup(FrameworkElement anchor, string text, (string Label, bool Primary, Action Act)[] buttons, bool menu = false)
    {
        if (titlePopup != null) titlePopup.IsOpen = false;
        var panel = new StackPanel { MaxWidth = 340 };
        if (text != null)
            panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Body"), Foreground = (Brush)FindResource("Ink1"), Margin = new Thickness(0, 0, 0, 12) });
        var row = new WrapPanel();
        foreach (var (label, primary, act) in buttons)
        {
            var b = new Button { Content = label, Margin = menu ? new Thickness(0) : new Thickness(0, 0, 8, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
            if (menu)
            {
                b.Style = (Style)FindResource("Link");
                b.Padding = new Thickness(2, 6, 2, 6);
                b.HorizontalContentAlignment = HorizontalAlignment.Left;
            }
            else if (primary) b.Style = (Style)FindResource("Primary");
            b.Click += (_, _) => { titlePopup.IsOpen = false; act?.Invoke(); };
            if (menu) panel.Children.Add(b); else row.Children.Add(b);
        }
        if (!menu) panel.Children.Add(row);
        titlePopup = new Popup
        {
            PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = new Border
            {
                Background = (Brush)FindResource("Raised"), BorderBrush = (Brush)FindResource("LineStrong"), BorderThickness = new Thickness(1),
                Padding = menu ? new Thickness(14, 8, 18, 8) : new Thickness(16, 14, 16, 14), Child = panel,
            },
        };
        System.Windows.Documents.TextElement.SetFontFamily(titlePopup.Child, (FontFamily)FindResource("TextFont"));
        System.Windows.Documents.TextElement.SetFontSize(titlePopup.Child, 13.5);
        System.Windows.Documents.TextElement.SetForeground(titlePopup.Child, (Brush)FindResource("Ink1"));
        titlePopup.IsOpen = true;
    }

    // ================================================================== просьба оценить

    private void UpdateRateBar()
    {
        RateBar.Visibility = Feedback.ShouldAskRating(settings, sessions) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RateYes_Click(object sender, RoutedEventArgs e) => OpenFeedback(Feedback.Kind.Review);

    private void RateLater_Click(object sender, RoutedEventArgs e)
    {
        Feedback.RatingLater(settings);
        UpdateRateBar();
    }

    private void RateNever_Click(object sender, RoutedEventArgs e)
    {
        Feedback.RatingDone(settings);
        UpdateRateBar();
    }
}
