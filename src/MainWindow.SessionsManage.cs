using System.Windows;
using PulsePC.Services;

namespace PulsePC;

/// <summary>
/// Удаление сессий: одной — кнопкой в отчёте, многих — «Очистить список…» (старше 30 или 90 дней, все).
/// Удаляется и из файла, и из списка на экране; перед удалением программа спрашивает подтверждение.
/// </summary>
public partial class MainWindow
{
    private void DeleteSession_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not GameSession s) return;
        ShowTitlePopup(BtnDeleteSession, L.F("Удалить сессию «{0}» от {1}? Отчёт пропадёт насовсем.", s.Game, s.Start.ToString(L.Pick("d MMMM, HH:mm", "MMMM d, HH:mm"), L.Culture)), new[]
        {
            (L.T("Удалить"), true, (Action)(() => RemoveSessions(x => Same(x, s), selectNext: true))),
            (L.T("Отмена"), false, (Action)null),
        });
    }

    private void CleanSessions_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        int old30 = sessions.Count(x => (now - x.Start).TotalDays > 30), old90 = sessions.Count(x => (now - x.Start).TotalDays > 90), all = sessions.Count;
        if (all == 0) return;
        var items = new List<(string, bool, Action)>();
        if (old90 > 0) items.Add((L.F("Старше 90 дней — {0}", Fmt.Plural(old90, "сессия", "сессии", "сессий")), false, () => ConfirmClean(old90, x => (now - x.Start).TotalDays > 90)));
        if (old30 > 0 && old30 != old90) items.Add((L.F("Старше 30 дней — {0}", Fmt.Plural(old30, "сессия", "сессии", "сессий")), false, () => ConfirmClean(old30, x => (now - x.Start).TotalDays > 30)));
        items.Add((L.F("Все — {0}", Fmt.Plural(all, "сессия", "сессии", "сессий")), false, () => ConfirmClean(all, _ => true)));
        ShowTitlePopup(BtnCleanSessions, null, items.ToArray(), menu: true);
    }

    private void ConfirmClean(int count, Func<GameSession, bool> match)
    {
        ShowTitlePopup(BtnCleanSessions, L.F("Удалить {0}? Отчёты пропадут насовсем, из сводок за неделю они тоже уйдут.", Fmt.Plural(count, "сессию", "сессии", "сессий")), new[]
        {
            (L.T("Удалить"), true, (Action)(() => RemoveSessions(match, selectNext: false))),
            (L.T("Отмена"), false, (Action)null),
        });
    }

    private static bool Same(GameSession a, GameSession b) => a.Start == b.Start && a.Game == b.Game;

    private async void RemoveSessions(Func<GameSession, bool> match, bool selectNext)
    {
        var doomed = sessions.Where(match).ToList();
        if (doomed.Count == 0) return;
        int index = SessionList.SelectedIndex;
        BeginBusy();
        int removed; string error;
        try { (removed, error) = await Task.Run(() => SessionStore.Remove(x => doomed.Any(d => Same(d, x)))); }
        finally { EndBusy(); }
        if (error != null)
        {
            var anchor = selectNext ? (FrameworkElement)BtnDeleteSession : BtnCleanSessions;
            ShowTitlePopup(anchor, L.F("Не удалось удалить: {0}", error), new[] { (L.T("Понятно"), true, (Action)null) });
            return;
        }
        foreach (var s in doomed) sessions.Remove(s);
        if (selectNext && sessions.Count > 0) SessionList.SelectedIndex = Math.Clamp(index, 0, sessions.Count - 1);
        else if (sessions.Count == 0) ShowSession(null);
        UpdateCleanSessions();
    }

    private void UpdateCleanSessions() =>
        BtnCleanSessions.Visibility = sessions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
}
