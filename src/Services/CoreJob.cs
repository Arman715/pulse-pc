using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PulsePC.Services;

/// <summary>
/// Объект задания Windows для дочерних процессов (nvidia-smi, PresentMon): закрылась программа — даже аварийно или
/// снятая из Диспетчера задач — Windows сама завершает их. Иначе после падения они оставались бы висеть в системе.
/// </summary>
public static class ChildJob
{
    private static readonly object gate = new();
    private static IntPtr job;          // держится открытым до конца процесса: закрытие описателя и завершает детей
    private static bool failed;

    /// <summary>Добавить запущенный процесс в задание. Не вышло — не страшно: процесс просто останется сам по себе.</summary>
    public static void Add(Process p)
    {
        if (p == null) return;
        try
        {
            IntPtr h = Handle();
            if (h == IntPtr.Zero) return;
            if (!AssignProcessToJobObject(h, p.Handle))
                Log.Error($"задание для дочерних процессов: не удалось добавить {SafeName(p)}, код {Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex) { Log.Error("задание для дочерних процессов", ex); }
    }

    private static string SafeName(Process p)
    {
        try { return p.ProcessName; } catch { return "процесс"; }
    }

    private static IntPtr Handle()
    {
        lock (gate)
        {
            if (job != IntPtr.Zero || failed) return job;
            IntPtr h = CreateJobObject(IntPtr.Zero, null);
            if (h == IntPtr.Zero) { failed = true; Log.Error($"задание для дочерних процессов не создано, код {Marshal.GetLastWin32Error()}"); return IntPtr.Zero; }
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = KillOnJobClose;
            int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buf, false);
                if (!SetInformationJobObject(h, ExtendedLimitInformation, buf, (uint)size))
                {
                    failed = true;
                    Log.Error($"задание для дочерних процессов: не задан режим завершения, код {Marshal.GetLastWin32Error()}");
                    CloseHandle(h);
                    return IntPtr.Zero;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            job = h;
            return job;
        }
    }

    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);
}
