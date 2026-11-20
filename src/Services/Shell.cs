using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace PulsePC.Services;

/// <summary>Запуск системных утилит без окна и с захватом вывода.</summary>
public static class Shell
{
    public static bool IsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// env — переменные окружения для запущенной программы. Так в команду PowerShell передаются данные (имена адаптеров,
    /// пути, значения из копий «как было»): сценарий читает их как $env:ИМЯ, и они никогда не становятся частью текста команды.
    /// </summary>
    public static (int Code, string Output) Run(string exe, string args, int timeoutMs = 60000, Encoding encoding = null,
                                                IReadOnlyDictionary<string, string> env = null)
    {
        try
        {
            var psi = new ProcessStartInfo(Resolve(exe), args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            if (encoding != null) { psi.StandardOutputEncoding = encoding; psi.StandardErrorEncoding = encoding; }
            if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v ?? "";

            using var p = Process.Start(psi);
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return (-1, "команда не завершилась вовремя");
            }
            return (p.ExitCode, stdout.Result + stderr.Result);
        }
        catch (Exception ex)
        {
            return (-2, ex.Message);
        }
    }

    /// <summary>
    /// Системная утилита по короткому имени («powercfg.exe») — полным путём из папки Windows. Программа работает
    /// с правами администратора, и поиск по текущей папке и PATH мог бы подсунуть чужой файл с тем же именем.
    /// </summary>
    private static string Resolve(string exe)
    {
        try
        {
            if (string.IsNullOrEmpty(exe) || exe.IndexOfAny(new[] { '\\', '/', ':' }) >= 0) return exe;
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (exe.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
            {
                string ps = Path.Combine(sys, @"WindowsPowerShell\v1.0\powershell.exe");
                return File.Exists(ps) ? ps : exe;
            }
            string full = Path.Combine(sys, exe);
            return File.Exists(full) ? full : exe;
        }
        catch { return exe; }
    }

    /// <summary>
    /// Выполнить фрагмент PowerShell. Команда передаётся в кодировке Base64, вывод читается как UTF-8.
    /// Данные — только через env (см. Run), не вставкой в текст сценария.
    /// </summary>
    public static (int Code, string Output) PowerShell(string script, int timeoutMs = 120000, IReadOnlyDictionary<string, string> env = null)
    {
        string full = "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $ProgressPreference='SilentlyContinue'; " + script;
        string b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        return Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + b64, timeoutMs, Encoding.UTF8, env);
    }

    /// <summary>
    /// Строка в одинарных кавычках PowerShell — на случай, когда текст всё же приходится вставить в сценарий.
    /// PowerShell считает кавычкой не только ', но и ‘ ’ ‚ ‛: все они удваиваются. Управляющие символы отбрасываются.
    /// Данные из файлов этим не передавать — только через env.
    /// </summary>
    public static string PsLiteral(string text)
    {
        var sb = new StringBuilder("'");
        foreach (char c in text ?? "")
        {
            if (char.IsControl(c)) continue;
            if (c is '\'' or '‘' or '’' or '‚' or '‛') sb.Append(c);
            sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }

    // ------------------------------------------------------------------ запуск от имени обычного пользователя

    /// <summary>
    /// Запустить программу без прав администратора — с правами оболочки Windows (Проводника) текущего пользователя —
    /// и дождаться её. Для утилит из папок пользователя (uv и т. п.): их файл может подменить любая программа без прав,
    /// поэтому от администратора они не запускаются никогда. exe — полный путь. Если оболочка сама работает с правами
    /// администратора (контроль учётных записей выключен) или запустить не удалось — возвращается (-2, причина), и
    /// ничего не запускается. Нужна служба «Вторичный вход в систему» (seclogon), как для «Запуск от имени».
    /// </summary>
    public static (int Code, string Error) RunAsUser(string exe, string args, int timeoutMs = 300000)
    {
        if (string.IsNullOrEmpty(exe) || !Path.IsPathRooted(exe) || !File.Exists(exe)) return (-2, "файл не найден");
        IntPtr process = IntPtr.Zero, shellToken = IntPtr.Zero, token = IntPtr.Zero;
        var pi = default(ProcessInformation);
        try
        {
            IntPtr shell = GetShellWindow();
            if (shell == IntPtr.Zero) return (-2, "оболочка Windows не запущена");
            GetWindowThreadProcessId(shell, out uint pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId) return (-2, "оболочка Windows не найдена");
            process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (process == IntPtr.Zero) return (-2, "нет доступа к оболочке Windows");
            if (!OpenProcessToken(process, 0x0002 | 0x0008 /* DUPLICATE | QUERY */, out shellToken)) return (-2, "нет доступа к оболочке Windows");
            if (IsElevated(shellToken)) return (-2, "оболочка Windows работает с правами администратора");
            // QUERY | DUPLICATE | ASSIGN_PRIMARY | ADJUST_DEFAULT | ADJUST_SESSIONID; SecurityImpersonation, TokenPrimary.
            if (!DuplicateTokenEx(shellToken, 0x0008 | 0x0002 | 0x0001 | 0x0080 | 0x0100, IntPtr.Zero, 2, 1, out token)) return (-2, "ошибка Windows " + Marshal.GetLastWin32Error());

            var si = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
            var cmd = new StringBuilder("\"" + exe + "\"" + (string.IsNullOrEmpty(args) ? "" : " " + args));
            string dir = Path.GetDirectoryName(exe);
            if (!CreateProcessWithTokenW(token, 0, exe, cmd, 0x08000000 /* CREATE_NO_WINDOW */, IntPtr.Zero, dir, ref si, out pi))
                return (-2, "ошибка Windows " + Marshal.GetLastWin32Error());
            if (WaitForSingleObject(pi.hProcess, (uint)Math.Max(0, timeoutMs)) != 0)
            {
                TerminateProcess(pi.hProcess, 1);
                return (-1, "команда не завершилась вовремя");
            }
            return GetExitCodeProcess(pi.hProcess, out uint code) ? ((int)code, "") : (-2, "код завершения не прочитан");
        }
        catch (Exception ex) { return (-2, ex.Message); }
        finally
        {
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            if (token != IntPtr.Zero) CloseHandle(token);
            if (shellToken != IntPtr.Zero) CloseHandle(shellToken);
            if (process != IntPtr.Zero) CloseHandle(process);
        }
    }

    private static bool IsElevated(IntPtr token)
    {
        IntPtr buffer = Marshal.AllocHGlobal(4);
        try { return !GetTokenInformation(token, 20 /* TokenElevation */, buffer, 4, out _) || Marshal.ReadInt32(buffer) != 0; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attributes, int level, int type, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr buffer, int length, out int needed);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string application, StringBuilder commandLine, uint creationFlags,
                                                       IntPtr environment, string currentDirectory, ref StartupInfo startup, out ProcessInformation info);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
