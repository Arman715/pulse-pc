namespace PulsePC.Services;

/// <summary>
/// Сторож: раз в сутки проверяет, не сбросились ли игровые настройки (обновления Windows это делают)
/// и не появились ли с прошлой проверки новые серьёзные события в журнале.
/// </summary>
public static class Watchdog
{
    public static bool IsDue(AppSettings settings) => (DateTime.Now - settings.LastWatch).TotalHours >= 24;

    /// <summary>Выполнить проверку. Возвращает сообщения для пользователя (пусто — всё спокойно).</summary>
    /// <param name="gaming">Идёт игра: настройки, которые обрывают сеть, сейчас не возвращаются.</param>
    public static List<string> Run(AppSettings settings, bool gaming = false)
    {
        var messages = new List<string>();
        bool firstRun = settings.LastWatch == DateTime.MinValue;

        // --- Настройки: что было применено в прошлый раз и перестало быть ---
        // Схема питания переехала из «Оптимизации» в раздел «Питание»: прежний выбор переносится в режим раздела.
        PowerAuto.Migrate(settings);
        // Не только переключатели «Оптимизации», но и перенесённые в разделы «Системы» (ускорение мыши, схема «всегда максимальная»).
        var all = Tweaks.Watched(settings);
        var titles = all.Select(t => t.Title).ToHashSet();
        // Настройка, которую программа больше не ведёт (например, схема питания в режиме «только в игре»), сбросом не считается.
        settings.KnownApplied = (settings.KnownApplied ?? new()).Where(titles.Contains).ToList();
        settings.RevertedTweaks = (settings.RevertedTweaks ?? new()).Where(titles.Contains).ToList();
        var applied = new List<string>();
        foreach (var tweak in all)
        {
            try { if (tweak.Check()) applied.Add(tweak.Title); } catch { }
        }
        if (!firstRun)
        {
            var reverted = settings.KnownApplied.Where(t => !applied.Contains(t)).ToList();
            if (reverted.Count > 0)
            {
                // Автовозврат: применяется заново то, что пользователь уже применял и что сбросила Windows.
                var restored = new List<string>();
                if (settings.AutoRestoreTweaks && Shell.IsAdmin())
                {
                    foreach (var tweak in all.Where(t => reverted.Contains(t.Title)))
                    {
                        if (gaming && tweak.DropsNetwork) continue;
                        try { tweak.Apply(); if (tweak.Check()) { restored.Add(tweak.Title); applied.Add(tweak.Title); } } catch { }
                    }
                }
                var left = reverted.Except(restored).ToList();
                if (restored.Count > 0)
                    messages.Add(L.F("Обновление Windows сбросило настройки: {0}. Программа их вернула.", string.Join("; ", restored.Select(L.T))));
                if (left.Count > 0)
                {
                    int inSystem = left.Count(t => Tweaks.HomeOf(t) != "");
                    string where = inSystem == 0 ? L.T("на вкладке «Оптимизация»")
                        : inSystem == left.Count ? L.T("в разделах вкладки «Система»")
                        : L.T("на вкладке «Оптимизация» и в разделах вкладки «Система»");
                    messages.Add(L.F("Сбросились настройки: {0}. Вернуть их можно {1}.", string.Join("; ", left.Select(L.T)), where));
                }
                settings.RevertedTweaks = left;
                // Невозвращённые остаются «известными», чтобы напоминать о них, пока их не вернут или не выключат вручную.
                applied.AddRange(left);
            }
        }
        settings.KnownApplied = applied.Distinct().ToList();

        // --- Журнал: новые серьёзные события с прошлой проверки ---
        // Те же правила, что в «Диагностике»; отмеченное там как просмотренное сторож тоже не считает.
        if (!firstRun)
        {
            void Check(string kind, string one, string few, string many)
            {
                DateTime since = settings.LastWatch;
                if (LogMarks.Seen(kind, settings) is DateTime seen && seen > since) since = seen;
                int n = Diagnostics.CountSince(kind, since);
                if (n > 0) messages.Add(L.F("{0} с прошлой проверки.", Fmt.Plural(n, one, few, many)));
            }
            Check(LogMarks.Bsod, "синий экран", "синих экрана", "синих экранов");
            Check(LogMarks.Gpu, "сбой драйвера видеокарты", "сбоя драйвера видеокарты", "сбоев драйвера видеокарты");
            Check(LogMarks.Whea, "аппаратная ошибка", "аппаратные ошибки", "аппаратных ошибок");
            Check(LogMarks.Shutdown, "аварийное выключение", "аварийных выключения", "аварийных выключений");
        }

        // --- Диски: ухудшение состояния или новые ошибки чтения с прошлой проверки ---
        // Во время игры не опрашиваются: PowerShell с опросом дисков нагружает процессор и будит уснувшие жёсткие диски.
        // Проверка дисков не пропадает — она будет на следующей проверке сторожа.
        if (!gaming)
        {
            try { messages.AddRange(CheckDisks(settings)); } catch (Exception ex) { Log.Error("сторож: проверка дисков", ex); }
        }

        settings.LastWatch = DateTime.Now;
        settings.LastWatchSummary = messages.Count == 0 ? "изменений нет" : string.Join(" ", messages);
        settings.Save();
        return messages;
    }

    /// <summary>
    /// Сравнивает состояние каждого диска с прошлым разом. Первое знакомство с диском только запоминает его:
    /// предупреждение — когда стало хуже, а не за старые ошибки, о которых уже известно.
    /// </summary>
    public static List<string> CheckDisks(AppSettings settings)
    {
        var messages = new List<string>();
        var disks = Metrics.QueryDisks();
        if (disks.Count == 0) return messages;     // без прав администратора Windows этих данных не отдаёт

        foreach (var d in disks)
        {
            string key = d.Name;
            double errors = d.ReadErrors ?? 0;
            string name = d.Name.Replace("WDC ", "WD ").Replace("Samsung SSD ", "Samsung ").Trim();
            DiskState was;
            lock (AppSettings.SyncRoot) settings.DiskStates.TryGetValue(key, out was);
            if (was != null)
            {
                if (d.Health != "Healthy" && was.Health == "Healthy")
                    messages.Add(L.F("Диск {0}: Windows сменила его состояние на «{1}». Скопируйте важное на другой диск.", name, d.Health));
                else if (errors > was.ReadErrors)
                    messages.Add(L.F("Диск {0}: новые ошибки чтения ({1:0} → {2:0}). Диск изнашивается: сделайте копию и не храните на нём важное.", name, was.ReadErrors, errors));
            }
            // Под замком настроек: их в это же время может сохранять другой поток.
            lock (AppSettings.SyncRoot) settings.DiskStates[key] = new DiskState { Health = d.Health, ReadErrors = errors, Hours = d.Hours ?? 0, Checked = DateTime.Now };
        }
        return messages;
    }

}
