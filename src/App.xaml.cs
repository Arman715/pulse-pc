using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using PulsePC.Services;

namespace PulsePC;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Служебные переменные PULSEPC_* (папка данных, переносной режим, подставные данные) у программы с правами
        // администратора — только заданные для этого процесса, а не постоянные переменные пользователя (HKCU\Environment):
        // их может записать любая программа без прав, и автозапуск с правами взял бы данные из её папки.
        DropUserOverrides();
        // Переносная версия (portable.txt рядом с программой): история и настройки — в папке Data рядом с ней.
        // Самым первым: до всего, что читает настройки или пишет журнал. Установщик и удаление так не запускаются.
        if (Services.SetupRequest.Parse(e.Args, Environment.ProcessPath) == null) Services.Portable.Apply(Environment.ProcessPath);
        // Любая необработанная ошибка пишется в журнал; окно с ней — не чаще раза в 10 минут, программа продолжает работать.
        DispatcherUnhandledException += OnUiException;
        // Ошибка в фоновом потоке завершает процесс: успеть вернуть вентиляторы драйверу и «игровые» настройки системе.
        AppDomain.CurrentDomain.UnhandledException += OnFatalException;
        // Забытая ошибка задачи процесс не завершает — только в журнал, чтобы не терялась молча.
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            Log.Error("необработанная ошибка в фоновой задаче", ev.Exception?.GetBaseException());
            ev.SetObserved();
        };
        // Выход из Windows: сначала сохранить идущую сессию, вернуть «игровые» настройки и вентиляторы видеокарты —
        // до того, как процесс завершат.
        SessionEnding += (_, _) =>
        {
            try { (MainWindow as PulsePC.MainWindow)?.FlushForShutdown(resume: true); }
            catch (Exception ex) { Log.Error("выход из Windows", ex); }
            try { FanControl.Shutdown(); } catch { }
        };
        base.OnStartup(e);

        // Язык интерфейса: выбор из настроек («Система» → «Язык») или язык Windows. До первого окна, в том числе установщика.
        bool selfTest = e.Args.Any(a => a is "--selftest" or "--feature-test");
        try { L.Init(selfTest ? "ru" : Services.AppSettings.LanguageSetting()); } catch (Exception ex) { Log.Error("язык интерфейса", ex); }
        L.HookWindows();
        // Тема: до первого окна, чтобы все стили и чертежи взяли её цвета.
        try { Theme.Init(selfTest ? "light" : Services.AppSettings.ThemeSetting()); Theme.Apply(Resources); }
        catch (Exception ex) { Log.Error("тема оформления", ex); }

        // Установка и удаление: тот же файл программы. Имя «PulsePC-Setup…» или --setup — установщик, --uninstall — удаление.
        // Решается до всего остального и без «одного экземпляра»: установщик должен уметь закрыть работающую программу.
        var setup = Services.SetupRequest.Parse(e.Args, Environment.ProcessPath);
        if (setup != null) { Setup.SetupWindow.Run(this, setup); return; }

        // Служебный режим: отрисовка чертежей в PNG для проверки (без окна программы).
        if (Blueprint.RenderHarness.TryRun(e.Args)) { Shutdown(); return; }

        // Служебные режимы: снимок одного раздела вкладки «Система» и самопроверка одной службы.
        if (Panels.PanelHarness.TryRun(e.Args)) { Shutdown(); return; }
        // Служебный режим: снимки окна программы с подставными данными (страница проекта, справка).
        if (Panels.WindowShots.TryRun(e.Args, this)) return;

        // Служебный режим: проверка безопасного удаления в песочнице, без окна.
        int i = Array.IndexOf(e.Args, "--selftest");
        if (i >= 0)
        {
            string report = i + 1 < e.Args.Length ? e.Args[i + 1] : Path.Combine(Path.GetTempPath(), "PulsePC-selftest.txt");
            Shutdown(Services.SelfTest.Run(report));
            return;
        }

        // Один экземпляр: если программа уже работает (например, свёрнута к часам), повторный запуск просто показывает её окно.
        bool admin = Services.Shell.IsAdmin();
        string suffix = admin ? "Admin" : "User";
        bool first = OpenSingle("PulsePC.Single." + suffix, admin);
        if (!first)
        {
            try { EventWaitHandle.OpenExisting("PulsePC.Show." + suffix).Set(); } catch { }
            Shutdown();
            return;
        }

        var window = new MainWindow();
        // Автозапуск при входе в Windows: --minimized — сразу к часам, окно покажется по значку.
        if (!e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase))) window.Show();

        var showSignal = OpenSignal("PulsePC.Show." + suffix, admin);
        if (showSignal != null)
        {
            new Thread(() =>
            {
                while (true)
                {
                    showSignal.WaitOne();
                    Dispatcher.BeginInvoke(new Action(window.ShowFromTray));
                }
            }) { IsBackground = true }.Start();
        }

        // Установщик и удаление просят работающую программу выйти (обновление файла, удаление папки).
        var exitSignal = OpenSignal("PulsePC.Exit." + suffix, admin);
        if (exitSignal != null)
        {
            new Thread(() =>
            {
                exitSignal.WaitOne();
                Dispatcher.BeginInvoke(new Action(() => { (MainWindow as PulsePC.MainWindow)?.PrepareExit(); Shutdown(); }));
            }) { IsBackground = true }.Start();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // После удаления: запущенный файл удаляется уже после выхода.
        Services.Installer.RunPendingCleanup();
        if (restart)
        {
            // Смена языка: новая копия запускается, когда эта отпустила «один экземпляр».
            try { single?.ReleaseMutex(); } catch { }
            try { single?.Dispose(); single = null; } catch { }
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false })?.Dispose(); }
            catch (Exception ex) { Log.Error("перезапуск программы", ex); }
        }
        base.OnExit(e);
    }

    private static bool restart;

    /// <summary>Перезапустить программу (после смены языка): идущая сессия сохраняется, временные изменения возвращаются, как при выходе.</summary>
    public static void Restart()
    {
        restart = true;
        try { (Current.MainWindow as PulsePC.MainWindow)?.PrepareExit(); } catch (Exception ex) { Log.Error("перезапуск: подготовка к выходу", ex); }
        Current.Shutdown();
    }

    private static Mutex single;

    /// <summary>
    /// Программа с правами: переменные PULSEPC_*, значение которых пришло из постоянных переменных пользователя, убираются
    /// из окружения процесса (до переносного режима и до первого обращения к папкам данных).
    /// </summary>
    private static void DropUserOverrides()
    {
        try
        {
            if (!Services.Shell.IsAdmin()) return;
            var dropped = new List<string>();
            foreach (System.Collections.DictionaryEntry v in Environment.GetEnvironmentVariables(EnvironmentVariableTarget.User))
            {
                if (v.Key is not string name || !name.StartsWith("PULSEPC_", StringComparison.OrdinalIgnoreCase)) continue;
                if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } now && now == v.Value as string)
                {
                    Environment.SetEnvironmentVariable(name, null);
                    dropped.Add(name);
                }
            }
            if (dropped.Count > 0) Log.Info("постоянные переменные пользователя не действуют на программу с правами: " + string.Join(", ", dropped));
        }
        catch (Exception ex) { Log.Error("служебные переменные окружения", ex); }
    }

    // ------------------------------------------------------------------ один экземпляр
    // Имена «PulsePC.*.Admin» общие для всего сеанса: программа без прав могла бы создать их заранее и тем самым
    // не дать программе запуститься (мьютекс «уже занят») или закрыть её (событие выхода). Поэтому объекты создаются
    // с доступом только для администраторов и системы, а уже существующий проверяется: чей он.

    private static SecurityIdentifier Admins => new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static SecurityIdentifier SystemSid => new(WellKnownSidType.LocalSystemSid, null);

    /// <summary>Владелец объекта — администраторы или система (так создаёт объект программа, запущенная с правами).</summary>
    private static bool OwnedByAdmins(ObjectSecurity sec)
    {
        try
        {
            var owner = sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return owner != null && (owner == Admins || owner == SystemSid);
        }
        catch { return false; }
    }

    /// <summary>
    /// Мьютекс «один экземпляр». true — эта копия первая (или чужой объект с тем же именем не в счёт).
    /// Отказ в доступе или чужой владелец пишется в журнал, а программа запускается, а не закрывается молча.
    /// </summary>
    private static bool OpenSingle(string name, bool admin)
    {
        try
        {
            if (!admin)
            {
                single = new Mutex(true, name, out bool created);
                return created;
            }
            var sec = new MutexSecurity();
            foreach (var sid in new[] { Admins, SystemSid })
                sec.AddAccessRule(new MutexAccessRule(sid, MutexRights.FullControl, AccessControlType.Allow));
            single = MutexAcl.Create(true, name, out bool createdNew, sec);
            if (createdNew) return true;
            if (OwnedByAdmins(single.GetAccessControl())) return false;     // настоящая работающая копия
            Log.Error($"объект «{name}» создан не администратором — не считаю его работающей копией программы");
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Error($"объект «{name}» занят без доступа для администраторов — запускаюсь без проверки второго экземпляра", ex);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("проверка второго экземпляра", ex);
            return true;
        }
    }

    /// <summary>
    /// Событие «показать окно» / «выйти» с доступом только для администраторов и системы. null — слушать нельзя:
    /// объект с этим именем создал кто-то без прав (тогда он мог бы закрывать программу) или он недоступен.
    /// </summary>
    private static EventWaitHandle OpenSignal(string name, bool admin)
    {
        try
        {
            if (!admin) return new EventWaitHandle(false, EventResetMode.AutoReset, name);
            var sec = new EventWaitHandleSecurity();
            foreach (var sid in new[] { Admins, SystemSid })
                sec.AddAccessRule(new EventWaitHandleAccessRule(sid, EventWaitHandleRights.FullControl, AccessControlType.Allow));
            var ev = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, name, out bool createdNew, sec);
            if (createdNew || OwnedByAdmins(ev.GetAccessControl())) return ev;
            Log.Error($"событие «{name}» создано не администратором — не слушаю его");
            ev.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"событие «{name}» недоступно", ex);
            return null;
        }
    }

    // ------------------------------------------------------------------ ошибки
    private static DateTime lastErrorBox = DateTime.MinValue;

    private static void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("необработанная ошибка в окне", e.Exception);
        e.Handled = true;
        // Ошибка на каждом такте таймера (раз в 1,5 с) завалила бы экран окнами — показываем не чаще раза в 10 минут.
        if ((DateTime.UtcNow - lastErrorBox).TotalMinutes < 10) return;
        lastErrorBox = DateTime.UtcNow;
        MessageBox.Show(L.T("Что-то пошло не так, но программа продолжает работать. Если ошибка повторяется, перезапустите её.") + "\n\n" + e.Exception.Message, L.T("Пульс ПК"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Процесс сейчас завершится: записать причину и вернуть системе то, что программа меняла.</summary>
    private static void OnFatalException(object sender, UnhandledExceptionEventArgs e)
    {
        try { Log.Error("программа завершается из-за ошибки", e.ExceptionObject as Exception); } catch { }
        try { FanControl.Shutdown(); } catch { }
        try { DpcLatency.StopActive(); } catch { }        // сеанс трассировки ядра («Задержки драйверов») не оставлять работать
        try { GameHooks.RaiseEnded(wait: true); } catch { }
    }
}
