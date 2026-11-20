using System.Windows;
using System.Windows.Controls;
using PulsePC.Services;

namespace PulsePC;

/// <summary>Обслуживание системы: возврат сброшенных настроек, плановая очистка, настройки графики игр.</summary>
public partial class MainWindow
{
    private void InitUpkeep()
    {
        AutoRestoreCheck.IsChecked = settings.AutoRestoreTweaks;
        // Плановая очистка — только по явному согласию (ключ clean.weekly.optin); прежнее «включено по умолчанию» не считается.
        AutoCleanCheck.IsChecked = Cleaner.WeeklyOptedIn(settings);
        AutoRestoreCheck.IsEnabled = AutoCleanCheck.IsEnabled = admin;
        TweakCells.Cells = TweakCells.LayoutCells = tweaks.Count;
        UpdateRevertedBar();
        UpdateAutoCleanText();
    }

    // ================================================================== сброшенные настройки
    private void AutoRestoreCheck_Click(object sender, RoutedEventArgs e)
    {
        settings.AutoRestoreTweaks = AutoRestoreCheck.IsChecked == true;
        settings.Save();
    }

    private void UpdateRevertedBar()
    {
        var left = settings.RevertedTweaks ?? new List<string>();
        RevertedBar.Visibility = left.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RevertedText.Text = left.Count == 0 ? "" : "Обновление Windows сбросило: " + string.Join("; ", left) + ".";
        BtnRestoreTweaks.IsEnabled = admin;
    }

    /// <summary>
    /// Вернуть сброшенное: переключатели этой страницы отметить и применить как обычно; перенесённые в разделы «Системы»
    /// (схема питания, ускорение мыши) — применить сразу тем же кодом, каким их возвращает сторож.
    /// </summary>
    private async void BtnRestoreTweaks_Click(object sender, RoutedEventArgs e)
    {
        var names = settings.RevertedTweaks ?? new List<string>();
        var moved = names.Where(n => !tweaks.Any(t => t.Title == n)).ToList();
        if (moved.Count > 0)
        {
            BtnRestoreTweaks.IsEnabled = false;
            BeginBusy();
            var back = await Task.Run(() =>
            {
                var done = new List<string>();
                foreach (var item in Tweaks.Watched(settings).Where(t => moved.Contains(t.Title)))
                {
                    try { item.Apply(); if (item.Check()) done.Add(item.Title); } catch { }
                }
                return done;
            });
            EndBusy();
            foreach (var title in back) Tweaks.Remember(settings, title, true);
            settings.Save();
            UpdateRevertedBar();
        }
        foreach (var t in tweaks.Where(t => names.Contains(t.Title) && t.Applied == false)) t.Desired = true;
        BtnApply_Click(sender, e);
    }

    // ================================================================== ссылка на пункт очистки
    /// <summary>Открыть «Очистку», где отмечен только этот пункт (ссылка из раздела «Драйвер» для кэша шейдеров).</summary>
    private void OpenCleanFor(string title)
    {
        SelectTab(PgClean);
        var item = cleanAll.FirstOrDefault(i => i.Title == title);
        if (item == null) return;
        // Пришли за одним пунктом — остальные (корзина, кэш обновлений) не должны удалиться заодно.
        foreach (var i in cleanAll) i.Selected = i == item;
        if (!scanned && BtnScan.IsEnabled) BtnScan_Click(BtnScan, new RoutedEventArgs());
        else Motion.SetText(CleanSummary, $"Отмечен только пункт «{title}». Проверьте размер и нажмите «Очистить».");
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (CleanList.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement row) row.BringIntoView();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ================================================================== плановая очистка
    private void AutoCleanCheck_Click(object sender, RoutedEventArgs e)
    {
        Cleaner.SetWeeklyOptIn(settings, AutoCleanCheck.IsChecked == true);
        settings.AutoCleanWeekly = AutoCleanCheck.IsChecked == true;
        settings.Save();
        UpdateAutoCleanText();
    }

    private void UpdateAutoCleanText()
    {
        AutoCleanLast.Text = !admin ? "Работает только с правами администратора."
            : settings.LastAutoClean == DateTime.MinValue ? (Cleaner.WeeklyOptedIn(settings) ? "Первая плановая очистка — через несколько минут после запуска." : "")
            : $"Последняя плановая очистка — {settings.LastAutoClean:d MMMM}" + (Cleaner.WeeklyOptedIn(settings) ? $", следующая — около {settings.LastAutoClean.AddDays(7):d MMMM}." : ".");
    }

    private bool autoCleaning;

    /// <summary>Раз в неделю — безопасные категории, только с правами администратора и не во время игры.</summary>
    private void CheckWeeklyClean()
    {
        if (!Cleaner.WeeklyOptedIn(settings) || !admin || autoCleaning || recorder.IsRecording || detectedGame != null) return;
        if ((DateTime.Now - settings.LastAutoClean).TotalDays < 7) return;
        autoCleaning = true;
        Task.Run(() =>
        {
            long freed = 0;
            foreach (var item in Cleaner.WeeklyItems(settings))
            {
                try { freed += item.Clean(); } catch (Exception ex) { Log.Error("плановая очистка: " + item.Title, ex); }
            }
            OnUi(() =>
            {
                autoCleaning = false;
                settings.LastAutoClean = DateTime.Now;
                settings.Save();
                UpdateAutoCleanText();
                UpdateDrives();
                if (settings.AlertsEnabled && freed >= 50L * 1024 * 1024)
                    tray.Notify("Плановая очистка", $"Удалены временные файлы и старые журналы: освобождено {Fmt.Size(freed)}.");
            });
        });
    }

    // ================================================================== настройки графики игр
    private List<GameTune> tunes = new();
    private bool tuning;
    private string tuneMessage;   // итог применения: показывается после пересчёта списка

    private void BtnTuneRescan_Click(object sender, RoutedEventArgs e) => LoadTunes(notify: false);

    /// <summary>Найти игры и их настройки в фоне. notify — сообщить у часов о новых рекомендациях.</summary>
    private void LoadTunes(bool notify)
    {
        if (tuning) return;
        tuning = true;
        string keep = (TuneList.SelectedItem as GameTune)?.Game;
        var snapshot = sessions.ToList();
        Task.Run(() =>
        {
            List<GameTune> found;
            try { found = GameTuning.Scan(GameLibrary.Load(), GameTuning.Profiles(snapshot)); }
            catch { found = new List<GameTune>(); }
            OnUi(() =>
            {
                tuning = false;
                tunes = found;
                TuneList.ItemsSource = tunes;
                TuneEmpty.Text = tunes.Count == 0 ? "Установленных игр не найдено (Steam, Epic, Riot, Astrum Play, Minecraft)." : "";
                TuneEmpty.Visibility = tunes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                TuneList.SelectedItem = tunes.FirstOrDefault(t => t.Game == keep) ?? tunes.FirstOrDefault();
                if (tuneMessage != null) { TuneResult.Text = tuneMessage; tuneMessage = null; }
                if (notify) NotifyNewTunes();
            });
        });
    }

    /// <summary>Новая игра (или новые рекомендации для неё) — один раз сообщить у часов.</summary>
    private void NotifyNewTunes()
    {
        var fresh = tunes.Where(t => t.CanEdit && t.Changes.Count > 0 && !settings.TuneNotified.Contains(t.Game)).ToList();
        if (fresh.Count == 0) return;
        settings.TuneNotified.AddRange(fresh.Select(t => t.Game));
        settings.Save();
        if (!settings.AlertsEnabled) return;
        string text = fresh.Count == 1
            ? $"Для {fresh[0].Game} есть {Fmt.Plural(fresh[0].Changes.Count, "рекомендация", "рекомендации", "рекомендаций")} по настройкам графики. Нажмите, чтобы посмотреть."
            : $"Есть рекомендации по настройкам графики для {Fmt.Plural(fresh.Count, "игры", "игр", "игр")}: {string.Join(", ", fresh.Take(4).Select(t => t.Game))}. Нажмите, чтобы посмотреть.";
        tray.Notify("Настройки игр", text, false, () =>
        {
            ShowFromTray();
            SelectTab(PgTweaks);
            TuneList.SelectedItem = fresh[0];
            Dispatcher.BeginInvoke(new Action(() => TuneDetail.BringIntoView()), System.Windows.Threading.DispatcherPriority.Loaded);
        });
    }

    private void TuneList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowTune(TuneList.SelectedItem as GameTune);

    private void ShowTune(GameTune t)
    {
        if (t == null) { TuneDetail.Visibility = Visibility.Collapsed; return; }
        TuneDetail.Visibility = Visibility.Visible;
        TuneGame.Text = t.Game;
        TunePath.Text = t.ConfigPath != null ? $"{t.Engine} · {t.ConfigPath}" : t.Engine;
        TunePath.ToolTip = t.ConfigPath;
        TuneBasis.Text = t.CanEdit ? t.Basis : "";
        TuneBasis.Visibility = t.CanEdit ? Visibility.Visible : Visibility.Collapsed;
        TuneChanges.ItemsSource = t.Changes;
        TuneNote.Text = t.CanEdit
            ? ((t.Changes.Count == 0 ? "Всё, что программа проверяет в этой игре, уже настроено как надо. " : "") + t.Note).Trim()
            : t.Note;
        BtnTuneApply.Visibility = t.CanEdit && t.Changes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnTuneRestore.Visibility = t.HasBackup ? Visibility.Visible : Visibility.Collapsed;
        TuneButtons.Visibility = BtnTuneApply.Visibility == Visibility.Visible || t.HasBackup ? Visibility.Visible : Visibility.Collapsed;
        TuneResult.Text = "";
        Motion.Reveal(TuneDetail, 0, 6);
    }

    private async void BtnTuneApply_Click(object sender, RoutedEventArgs e)
    {
        if (TuneList.SelectedItem is not GameTune t) return;
        var chosen = t.Changes.Where(c => c.Selected).ToList();
        if (chosen.Count == 0) { TuneResult.Text = "Ничего не отмечено."; return; }
        BtnTuneApply.IsEnabled = false;
        BeginBusy();
        string result;
        try { result = await Task.Run(() => GameTuning.Apply(t, chosen)); }
        catch (Exception ex) { result = "Не удалось записать файл: " + ex.Message; }
        finally { EndBusy(); BtnTuneApply.IsEnabled = true; }
        // Файл перечитывается: рекомендации пересчитаются, итог появится под ними.
        tuneMessage = result;
        LoadTunes(notify: false);
    }

    private async void BtnTuneRestore_Click(object sender, RoutedEventArgs e)
    {
        if (TuneList.SelectedItem is not GameTune t) return;
        BeginBusy();
        string result;
        try { result = await Task.Run(() => GameTuning.Restore(t)); }
        catch (Exception ex) { result = "Не удалось восстановить файл из копии: " + ex.Message; }
        finally { EndBusy(); }
        tuneMessage = result;
        LoadTunes(notify: false);
    }
}
