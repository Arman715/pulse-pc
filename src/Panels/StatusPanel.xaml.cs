using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using PulsePC.Services;

namespace PulsePC.Panels;

/// <summary>
/// Раздел «Состояние программы»: какие части программы работают и когда ошиблись, версия, папка данных, права,
/// хвост журнала, «Открыть журнал» и «Скопировать отчёт». Пока раздел на экране, сводка обновляется раз в 5 секунд.
/// Ничего не меняет в системе.
/// </summary>
public partial class StatusPanel : UserControl, ISystemPanel
{
    public sealed class PartRowVm
    {
        public string Name { get; init; } = "";
        public string Detail { get; init; } = "";
        public string Status { get; init; } = "";
        public Severity Level { get; init; }
        public Brush Ink { get; init; } = Palette.Ink1;
        /// <summary>Исходный текст ошибки, если он отличается от пересказа.</summary>
        public string Tip { get; init; }
    }

    public sealed class Fact
    {
        public string Title { get; init; } = "";
        public string Value { get; init; } = "";
    }

    private sealed class Snapshot
    {
        public List<PartState> Parts = new();
        public List<string> Log = new();
        public string Windows = "";
    }

    private PanelHost host;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool loading;
    private static string windowsBuild;

    public StatusPanel()
    {
        InitializeComponent();
        timer.Tick += (_, _) => Refresh();
        // Обновляется, только пока раздел виден: ушли на другой раздел или вкладку — таймер стоит.
        IsVisibleChanged += (_, _) => { if (IsVisible) { timer.Start(); Refresh(); } else timer.Stop(); };
    }

    public void Attach(PanelHost h)
    {
        host = h;
        HealthWatch.Start();
        // Снимок раздела для проверки вида (--render-panel … --set status.demo=1): подставные части, только в папке проверки.
        if (Environment.GetEnvironmentVariable("PULSEPC_DATA") is { Length: > 0 } && h.Settings.Get("status.demo") == "1") Demo();
    }

    public void Shown()
    {
        if (IsVisible) timer.Start();
        Refresh();
    }

    private void Refresh()
    {
        if (host == null || loading) return;
        loading = true;
        Task.Run(() =>
        {
            var snap = new Snapshot();
            try
            {
                snap.Parts = HealthWatch.Current.Snapshot();
                snap.Log = Log.Tail(30);
                snap.Windows = windowsBuild ??= HealthWatch.WindowsBuild();
            }
            catch { }
            host.Ui(() => { loading = false; Show(snap); });
        });
    }

    private void Show(Snapshot snap)
    {
        var parts = snap.Parts;
        var lasting = parts.Where(p => p.Alarm).ToList();
        var failing = parts.Where(p => p.Level == Severity.Bad).ToList();
        var quiet = parts.Where(p => p.Level == Severity.Warn).ToList();
        State.Text = parts.Count == 0 ? L.T("Части программы ещё не отметились.")
            : lasting.Count > 0 ? L.F("Не работает больше {0} минут: {1}.", HealthWatch.AlarmMinutes, Names(lasting))
            : failing.Count > 0 ? L.F("Ошибка: {0}. Программа пробует снова.", Names(failing))
            : quiet.Count > 0 ? L.F("Нет свежих данных: {0}.", Names(quiet))
            : L.T("Все части программы работают.");
        AlarmMark.Visibility = lasting.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        PartsEmpty.Visibility = parts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PartList.ItemsSource = parts.Select(p => new PartRowVm
        {
            Name = L.T(p.Name), Detail = p.Detail, Status = p.Status, Level = p.Level,
            Ink = p.Level == Severity.Bad ? Palette.Bad : p.Level == Severity.Warn ? Palette.Warn : p.Level == Severity.Info ? Palette.Ink3 : Palette.Ink1,
            Tip = string.IsNullOrWhiteSpace(p.Raw) || p.Detail.Contains(p.Raw.Trim()) ? null : L.F("Как записала программа: {0}", p.Raw.Trim()),
        }).ToList();

        FactList.ItemsSource = new[]
        {
            new Fact { Title = L.T("Версия"), Value = Installer.Version },
            new Fact { Title = "Windows", Value = snap.Windows },
            new Fact { Title = L.T("Права администратора"), Value = host.Admin ? L.T("есть") : L.T("нет — программа только показывает состояние") },
            new Fact { Title = L.T("Папка данных"), Value = SessionStore.Folder },
        };

        LogPath.Text = System.IO.Path.GetFileName(Log.FilePath);
        LogText.Inlines.Clear();
        if (snap.Log.Count == 0) LogText.Inlines.Add(new Run(L.T("Журнал пуст: программа пока ничего в него не записала.")));
        for (int i = 0; i < snap.Log.Count; i++)
        {
            string line = snap.Log[i];
            // Ошибки — тушью, сведения — серым: глаз сразу находит, что сломалось.
            var run = new Run(line) { Foreground = line.Contains("[ошибка]") ? Palette.Ink1 : Palette.Ink3 };
            LogText.Inlines.Add(run);
            if (i < snap.Log.Count - 1) LogText.Inlines.Add(new LineBreak());
        }
    }

    private static string Names(List<PartState> parts) =>
        string.Join(", ", parts.Take(3).Select(p => L.En ? "“" + L.T(p.Name) + "”" : "«" + p.Name + "»")) + (parts.Count > 3 ? " " + L.F("и ещё {0}", parts.Count - 3) : "");

    // ================================================================ кнопки

    private void BtnLog_Click(object sender, RoutedEventArgs e)
    {
        string path = Log.FilePath;
        if (!File.Exists(path)) { Say(L.T("Журнала ещё нет: программа пока ничего в него не записала.")); return; }
        try
        {
            // Блокнот запускается через Проводник, то есть с обычными правами, а не с правами администратора программы.
            string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe");
            if (File.Exists(notepad)) GameLibrary.Launch(new GameEntry { Target = notepad, Arguments = "\"" + path + "\"" });
            else GameLibrary.OpenAsUser(path);
            Say("");
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = false });
                Say(L.F("Блокнот не открылся — журнал показан в папке: {0}.", Path.GetFileName(path)));
            }
            catch (Exception ex) { Say(L.F("Не удалось открыть журнал: {0}", ex.Message)); }
        }
    }

    private void BtnReport_Click(object sender, RoutedEventArgs e)
    {
        if (host == null) return;
        bool admin = host.Admin;
        BtnReport.IsEnabled = false;
        Task.Run(() =>
        {
            string text;
            try { text = HealthWatch.Report(admin); } catch (Exception ex) { text = null; Log.Error("отчёт о состоянии", ex); }
            host.Ui(() =>
            {
                BtnReport.IsEnabled = true;
                if (text == null) { Say(L.T("Не удалось составить отчёт. Подробности — в журнале.")); return; }
                Say(Copy(text)
                    ? L.T("Отчёт скопирован: версия, сборка Windows, состояние частей и последние 100 строк журнала. Имя пользователя и личные папки в нём заменены. Вставьте его в сообщение (Ctrl+V).")
                    : L.T("Буфер обмена занят другой программой. Нажмите ещё раз через пару секунд."));
            });
        });
    }

    /// <summary>Буфер обмена бывает на мгновение занят другой программой — несколько попыток.</summary>
    private static bool Copy(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetDataObject(text, true); return true; }
            catch { Thread.Sleep(60); }
        }
        return false;
    }

    private void Say(string text)
    {
        Result.Text = text;
        Result.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Подставные части для снимка раздела. Пишет только в журнал папки проверки (PULSEPC_DATA).</summary>
    private static void Demo()
    {
        foreach (var name in new[] { "Видеокарта", "Диски", "История", "Датчики процессора" }) Health.Ok(name);
        Health.Fail("Сеть игры", "Отказано в доступе к таблице соединений");
        Health.Fail("Кадры (PresentMon)", "PresentMon завершился с кодом 5");
        Log.Info("запуск, версия " + Installer.Version);
    }
}
