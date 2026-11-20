using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PulsePC.Services;

/// <summary>
/// Имена сеансов трассировки Windows (ETW) для записи кадров (PresentMon) и сети игры. У каждого запуска — своё имя
/// «PulsePC-{номер процесса}-{вид}{n}»: раньше имя было общим, и остановка сеанса закончившейся игры (она идёт в фоне)
/// обрывала сеанс только что начавшейся. Сеансы, оставшиеся от упавших прошлых запусков, убираются при старте.
/// </summary>
public static class EtwSessions
{
    public const string Prefix = "PulsePC";
    private static int counter;

    /// <summary>Новое уникальное имя сеанса: «PulsePC-1234-Fps1», «PulsePC-1234-Net2».</summary>
    public static string NewName(string kind) => $"{Prefix}-{Environment.ProcessId}-{kind}{Interlocked.Increment(ref counter)}";

    /// <summary>Остановить сеанс по имени. 0 — остановлен, 4201 — такого нет.</summary>
    public static uint Stop(string name)
    {
        IntPtr props = Alloc();
        try { return ControlTraceW(0, name, props, ControlStop); }
        catch { return uint.MaxValue; }
        finally { Marshal.FreeHGlobal(props); }
    }

    /// <summary>Имена всех сеансов трассировки в системе (нужны права администратора, иначе — пусто).</summary>
    public static List<string> List()
    {
        var names = new List<string>();
        var bufs = new IntPtr[MaxSessions];
        try
        {
            for (int i = 0; i < bufs.Length; i++) bufs[i] = Alloc();
            uint err = QueryAllTracesW(bufs, (uint)bufs.Length, out uint count);
            if (err != 0 && err != ErrMoreData) return names;
            for (int i = 0; i < count && i < bufs.Length; i++)
            {
                int off = Marshal.ReadInt32(bufs[i], 116);
                if (off <= 0 || off >= BufSize) continue;
                string n = Marshal.PtrToStringUni(bufs[i] + off);
                if (!string.IsNullOrEmpty(n)) names.Add(n);
            }
        }
        catch (Exception ex) { Log.Error("список сеансов трассировки", ex); }
        finally { foreach (var b in bufs) if (b != IntPtr.Zero) Marshal.FreeHGlobal(b); }
        return names;
    }

    /// <summary>
    /// Остановить сеансы «PulsePC…», которые никому не принадлежат: от прежних версий (общие имена «PulsePC», «PulsePC-Net»)
    /// и от упавших запусков (процесс с номером из имени уже не работает). Сеансы живой копии программы не трогаются.
    /// </summary>
    public static void CleanupStale()
    {
        foreach (var name in List())
        {
            if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || OwnerAlive(name)) continue;
            uint err = Stop(name);
            Log.Info($"остановлен сеанс трассировки «{name}», оставшийся от прошлого запуска" + (err != 0 ? $" (код {err})" : ""));
        }
    }

    private static bool OwnerAlive(string name)
    {
        var parts = name.Split('-');
        if (parts.Length < 3 || !int.TryParse(parts[1], out int pid)) return false;
        if (pid == Environment.ProcessId) return true;
        try
        {
            using var p = Process.GetProcessById(pid);
            using var self = Process.GetCurrentProcess();
            return p.ProcessName.Equals(self.ProcessName, StringComparison.OrdinalIgnoreCase)
                   || p.ProcessName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // EVENT_TRACE_PROPERTIES (120 байт на x64), за ней — место под имя сеанса и имя файла.
    private const int PropsSize = 120, NameBytes = 1024 * 2, BufSize = PropsSize + 2 * NameBytes, MaxSessions = 64;
    private const uint ControlStop = 1, ErrMoreData = 234, WnodeFlagTracedGuid = 0x00020000;

    private static IntPtr Alloc()
    {
        IntPtr p = Marshal.AllocHGlobal(BufSize);
        for (int i = 0; i < BufSize; i += 8) Marshal.WriteInt64(p, i, 0);
        Marshal.WriteInt32(p, 0, BufSize);                                // Wnode.BufferSize
        Marshal.WriteInt32(p, 44, unchecked((int)WnodeFlagTracedGuid));   // Wnode.Flags
        Marshal.WriteInt32(p, 112, PropsSize + NameBytes);                // LogFileNameOffset
        Marshal.WriteInt32(p, 116, PropsSize);                            // LoggerNameOffset
        return p;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ControlTraceW(ulong sessionHandle, string sessionName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint QueryAllTracesW([In] IntPtr[] propertyArray, uint propertyArrayCount, out uint loggerCount);
}
