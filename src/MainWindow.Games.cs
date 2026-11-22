using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PulsePC.Services;

namespace PulsePC;

/// <summary>
/// Игры, добавленные вручную: программа не нашла игру сама — игрок указывает её файл .exe на вкладке «Сессии».
/// Здесь же — игры, найденные по поведению (<see cref="GameFinder"/>), с кнопкой «это не игра» в списке и в отчёте сессии.
/// </summary>
public partial class MainWindow
{
    private void InitCustomGames()
    {
        settings.CustomGames ??= new();
        GameDetector.SetCustom(settings.CustomGames);
        CautiousMode.UseSettings(settings);      // осторожный режим: ручной выбор и найденные античиты (MainWindow.Cautious.cs)
        HookGameFinder();
        RenderCustomGames();
    }

    private void BtnAddGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("Файл игры (.exe)"),
            Filter = L.T("Программы (*.exe)|*.exe"),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        string exe = dialog.FileName;

        string problem = CheckCustomExe(exe);
        if (problem != null) { ShowCustomNote(problem, warn: true); return; }

        string name = CustomGame.GuessName(exe);
        settings.CustomGames.RemoveAll(g => string.Equals(g.Exe, exe, StringComparison.OrdinalIgnoreCase));
        settings.CustomGames.Add(new CustomGame { Name = name, Exe = exe });
        settings.Save();
        GameDetector.SetCustom(settings.CustomGames);
        RenderCustomGames();
        ShowCustomNote(CustomGame.FolderUsable(Path.GetDirectoryName(exe))
            ? L.F("«{0}» добавлена: запись начнётся сама, когда игра запустится. Узнаётся и любой другой файл из её папки.", name)
            : L.F("«{0}» добавлена: запись начнётся, когда запустится именно этот файл.", name), warn: false);
        OfferCautiousForAdded(name, exe);     // античит в папке игры — осторожный режим (MainWindow.Cautious.cs)
    }

    /// <summary>Причина, по которой файл не годится в игры, или null.</summary>
    private static string CheckCustomExe(string exe)
    {
        string file = Path.GetFileName(exe);
        string dir = Path.GetDirectoryName(exe) ?? "";
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (dir.StartsWith(win, StringComparison.OrdinalIgnoreCase)) return L.T("Это файл Windows, а не игра.");
        if (string.Equals(file, "PulsePC.exe", StringComparison.OrdinalIgnoreCase)) return L.T("Это сам Пульс ПК.");
        string lower = file.ToLowerInvariant();
        if (lower.Contains("unins") || lower.Contains("setup") || lower.Contains("crash") || lower.Contains("updater") || lower.Contains("redist"))
            return L.F("«{0}» похож на установщик или служебный файл. Выберите файл, который запускает саму игру.", file);
        return null;
    }

    private void RemoveCustomGame(CustomGame game)
    {
        settings.CustomGames.RemoveAll(g => string.Equals(g.Exe, game.Exe, StringComparison.OrdinalIgnoreCase));
        settings.Save();
        GameDetector.SetCustom(settings.CustomGames);
        RenderCustomGames();
        ShowCustomNote(L.F("«{0}» убрана из списка. Записанные сессии остались.", game.Name), warn: false);
    }

    private TextBlock customNote;

    private void ShowCustomNote(string text, bool warn)
    {
        customNote ??= new TextBlock { Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 16, 0) };
        if (customNote.Parent == null) CustomGamesPanel.Children.Add(customNote);
        customNote.Text = text;
        customNote.Foreground = warn ? (Brush)FindResource("Warn") : (Brush)FindResource("Ink3");
        Motion.Reveal(customNote, 0, 4);
    }

    /// <summary>В невысоком окне добавленные игры показаны не все (верх колонки без прокрутки) — «показать все» раскрывает список.</summary>
    private bool customGamesAll;
    private const int CustomGamesCompact = 2;

    private void RenderCustomGames()
    {
        CustomGamesPanel.Children.Clear();
        var rows = new List<FrameworkElement>();
        foreach (var g in settings.CustomGames.ToList())
        {
            var remove = new Button { Style = (Style)FindResource("Link"), Content = L.T("убрать"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            remove.Click += (_, _) => RemoveCustomGame(g);
            rows.Add(GameRow(g.Name,
                File.Exists(g.Exe) ? Path.GetFileName(g.Exe) : L.F("{0} — файл не найден", Path.GetFileName(g.Exe)), g.Exe, remove));
        }
        // Найденные по поведению — здесь же, с «это не игра»: программа сама решила, что это игра, и человек может её поправить.
        foreach (var f in GameFinder.Found().Where(x => x.Source == "behavior"))
        {
            var reject = new Button { Style = (Style)FindResource("Link"), Content = L.T("это не игра"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            reject.Click += (_, _) => RejectGame(f.Title, f.Exe, reject);
            rows.Add(GameRow(f.Title, L.F("найдена по поведению · {0}", Path.GetFileName(f.Exe)), f.Exe, reject));
        }
        bool cut = sessionsCompact && !customGamesAll && rows.Count > CustomGamesCompact + 1;
        foreach (var row in cut ? rows.Take(CustomGamesCompact) : rows) CustomGamesPanel.Children.Add(row);
        if (cut || customGamesAll && sessionsCompact && rows.Count > CustomGamesCompact + 1)
        {
            var more = new Button
            {
                Style = (Style)FindResource("Link"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0),
                Content = cut ? L.F("показать все ({0})", rows.Count) : L.T("свернуть"),
            };
            more.Click += (_, _) => { customGamesAll = !customGamesAll; RenderCustomGames(); };
            CustomGamesPanel.Children.Add(more);
        }
        if (customNote != null) CustomGamesPanel.Children.Add(customNote);
    }

    private DockPanel GameRow(string title, string caption, string tip, Button action)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 16, 0), ToolTip = tip };
        DockPanel.SetDock(action, Dock.Right);
        row.Children.Add(action);
        var text = new StackPanel();
        var name = new TextBlock { Text = title, Style = (Style)FindResource("Body"), TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Ink1");
        text.Children.Add(name);
        text.Children.Add(new TextBlock { Text = caption, Style = (Style)FindResource("Caption"), TextTrimming = TextTrimming.CharacterEllipsis });
        row.Children.Add(text);
        return row;
    }

    // ================================================================== найденные по поведению: «Это не игра»
    private bool finderHooked;

    /// <summary>Подписка на изменения списка найденных игр (поиск закончился, новая находка, «Это не игра» в разделе программ).</summary>
    private void HookGameFinder()
    {
        if (finderHooked) return;
        finderHooked = true;
        GameFinder.UseSettings(settings);
        GameFinder.Changed += () => OnUi(() =>
        {
            RenderCustomGames();
            if (SessionList.SelectedItem is GameSession cur) ShowBehaviorMark(cur);
            if (!GameFinder.Scanning) LoadGames();
        });
    }

    /// <summary>
    /// «Это не игра»: программа больше не записывает этот файл как игру. Если у него уже есть записанные сессии — спросить,
    /// удалить ли их (отчёты о браузере или плеере никому не нужны) или оставить.
    /// </summary>
    private void RejectGame(string title, string path, FrameworkElement anchor)
    {
        int count = sessions.Count(s => s.Game == title);
        string text = L.F("«{0}» больше не будет считаться игрой: запись для неё не начнётся.", title);
        (string, bool, Action)[] buttons;
        if (count > 0)
        {
            text += " " + L.F("Удалить и её записанные сессии ({0})?", count);
            buttons = new (string, bool, Action)[]
            {
                (L.T("Не игра, удалить сессии"), true, () => { GameFinder.MarkNotGame(path); RemoveSessions(s => s.Game == title, selectNext: false); }),
                (L.T("Не игра, сессии оставить"), false, () => GameFinder.MarkNotGame(path)),
                (L.T("Отмена"), false, null),
            };
        }
        else buttons = new (string, bool, Action)[] { (L.T("Это не игра"), true, () => GameFinder.MarkNotGame(path)), (L.T("Отмена"), false, null) };
        ShowTitlePopup(anchor, text, buttons);
    }

    private DockPanel behaviorMark;
    private TextBlock behaviorText;
    private Button behaviorReject;

    /// <summary>Под заголовком отчёта: игра найдена по поведению — откуда программа это взяла и кнопка «Это не игра».</summary>
    private void ShowBehaviorMark(GameSession s)
    {
        HookGameFinder();
        var g = s == null ? null : GameFinder.BehaviorGame(s.Game);
        if (behaviorMark == null)
        {
            if (g == null || SessWhen.Parent is not Panel parent) return;
            behaviorMark = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            behaviorReject = new Button { Style = (Style)FindResource("Link"), Content = L.T("Это не игра"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            behaviorReject.Click += (_, _) => { if (behaviorReject.Tag is FoundGame f) RejectGame(f.Title, f.Exe, behaviorReject); };
            DockPanel.SetDock(behaviorReject, Dock.Right);
            behaviorMark.Children.Add(behaviorReject);
            behaviorText = new TextBlock { Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap };
            behaviorMark.Children.Add(behaviorText);
            parent.Children.Insert(parent.Children.IndexOf(SessWhen) + 1, behaviorMark);
        }
        behaviorMark.Visibility = g != null ? Visibility.Visible : Visibility.Collapsed;
        if (g == null) return;
        behaviorReject.Tag = g;
        behaviorText.Text = L.F("Найдена по поведению: {0} больше минуты рисовала кадры на весь экран и нагружала видеокарту, поэтому программа записала её как игру.",
            Path.GetFileName(g.Exe));
    }
}
