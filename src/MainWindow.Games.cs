using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PulsePC.Services;

namespace PulsePC;

/// <summary>Игры, добавленные вручную: программа не нашла игру сама — игрок указывает её файл .exe на вкладке «Сессии».</summary>
public partial class MainWindow
{
    private void InitCustomGames()
    {
        settings.CustomGames ??= new();
        GameDetector.SetCustom(settings.CustomGames);
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

    private void RenderCustomGames()
    {
        CustomGamesPanel.Children.Clear();
        foreach (var g in settings.CustomGames.ToList())
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 16, 0), ToolTip = g.Exe };
            var remove = new Button { Style = (Style)FindResource("Link"), Content = L.T("убрать"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            remove.Click += (_, _) => RemoveCustomGame(g);
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = g.Name, Style = (Style)FindResource("Body"), Foreground = (Brush)FindResource("Ink1"), TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock
            {
                Text = File.Exists(g.Exe) ? Path.GetFileName(g.Exe) : L.F("{0} — файл не найден", Path.GetFileName(g.Exe)),
                Style = (Style)FindResource("Caption"), TextTrimming = TextTrimming.CharacterEllipsis,
            });
            row.Children.Add(text);
            CustomGamesPanel.Children.Add(row);
        }
        if (customNote != null) CustomGamesPanel.Children.Add(customNote);
    }
}
