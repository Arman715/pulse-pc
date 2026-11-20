namespace PulsePC.Services;

/// <summary>
/// «Игра началась» и «игра закончилась» для функций, которые что-то меняют только на время игры
/// (схема питания, обновления Windows, клавиша Win). Окно программы сообщает сюда, службы подписываются.
/// Обработчики вызываются в фоновом потоке, по одному, каждый в своём try/catch. Все события идут через одну
/// очередь: если игра упала через секунду после запуска, обработчики «закончилась» всё равно выполнятся после
/// обработчиков «началась», а не раньше них (иначе перехват клавиши Win остался бы стоять после игры).
/// </summary>
public static class GameHooks
{
    /// <summary>Название игры и номер её процесса.</summary>
    public static event Action<string, int> Started;
    public static event Action Ended;

    public static bool InGame { get; private set; }
    public static string Game { get; private set; } = "";
    public static int Pid { get; private set; }
    /// <summary>Когда началась текущая (или последняя) игра.</summary>
    public static DateTime StartedAt { get; private set; } = DateTime.MinValue;

    private static readonly object Gate = new();
    private static Task tail = Task.CompletedTask;     // последняя задача очереди; новые встают за ней

    /// <summary>
    /// Игра с этим номером процесса всё ещё идёт. Обработчики «началась» проверяют это перед изменением системы:
    /// если игра уже закончилась, пока очередь дошла до них, менять нечего (возврат уже стоит в очереди следом).
    /// </summary>
    public static bool IsCurrent(int pid)
    {
        lock (Gate) return InGame && Pid == pid;
    }

    // Поставить работу в общую очередь. Вызывается под Gate: порядок в очереди совпадает с порядком событий.
    private static Task Enqueue(Action work)
    {
        tail = tail.ContinueWith(_ => { try { work(); } catch { } }, CancellationToken.None,
            TaskContinuationOptions.None, TaskScheduler.Default);
        return tail;
    }

    public static void RaiseStarted(string game, int pid)
    {
        lock (Gate)
        {
            if (InGame) return;
            StartedAt = DateTime.Now; InGame = true; Game = game ?? ""; Pid = pid;
            var handlers = Started?.GetInvocationList() ?? Array.Empty<Delegate>();
            string name = game ?? "";
            Enqueue(() =>
            {
                foreach (Action<string, int> h in handlers) { try { h(name, pid); } catch { } }
            });
        }
    }

    /// <summary>wait — дождаться обработчиков (при выходе из программы: всё, что менялось на время игры, должно вернуться).</summary>
    public static void RaiseEnded(bool wait = false)
    {
        Task task;
        lock (Gate)
        {
            if (!InGame) return;
            InGame = false; Game = ""; Pid = 0;
            var handlers = Ended?.GetInvocationList() ?? Array.Empty<Delegate>();
            task = Enqueue(() =>
            {
                foreach (Action h in handlers) { try { h(); } catch { } }
            });
        }
        if (wait) { try { task.Wait(15000); } catch { } }
    }
}
