using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ProjectR.Agent.Common
{
    public static class SessionLauncher
    {
        [DllImport("kernel32.dll")]
        private static extern uint WTSGetActiveConsoleSessionId();

        public static uint GetActiveConsoleSessionId()
        {
            return WTSGetActiveConsoleSessionId();
        }

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool DuplicateTokenEx(
            IntPtr hExistingToken,
            uint dwDesiredAccess,
            IntPtr lpTokenAttributes,
            int ImpersonationLevel,
            int TokenType,
            out IntPtr phNewToken);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessAsUserW")]
        private static extern bool CreateProcessAsUser(
            IntPtr hToken,
            string lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OSVERSIONINFOW
        {
            public int dwOSVersionInfoSize;
            public int dwMajorVersion;
            public int dwMinorVersion;
            public int dwBuildNumber;
            public int dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
        }

        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        private static extern int RtlGetVersion(ref OSVERSIONINFOW versionInfo);

        private static bool? _isWin10OrGreater;
        private static bool _loggedLegacySkip;

        /// <summary>
        /// Uses kernel RtlGetVersion (immune to GetVersionEx compatibility shims and WOW64 redirection)
        /// to verify Windows 10 / Server 2016+ (Major >= 10), which is required by Golang 1.21+ runtime.
        /// </summary>
        public static bool IsWindows10OrGreater()
        {
            if (_isWin10OrGreater.HasValue) return _isWin10OrGreater.Value;
            try
            {
                OSVERSIONINFOW osvi = new OSVERSIONINFOW();
                osvi.dwOSVersionInfoSize = Marshal.SizeOf(typeof(OSVERSIONINFOW));
                if (RtlGetVersion(ref osvi) == 0)
                {
                    _isWin10OrGreater = osvi.dwMajorVersion >= 10;
                    return _isWin10OrGreater.Value;
                }
            }
            catch { }
            _isWin10OrGreater = Environment.OSVersion.Version.Major >= 10;
            return _isWin10OrGreater.Value;
        }

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public uint dwX;
            public uint dwY;
            public uint dwXSize;
            public uint dwYSize;
            public uint dwXCountChars;
            public uint dwYCountChars;
            public uint dwFillAttribute;
            public uint dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        private const uint MAXIMUM_ALLOWED = 0x02000000;
        private const int SecurityImpersonation = 2;
        private const int TokenPrimary = 1;
        private const uint CREATE_NO_WINDOW = 0x08000000;
        private const uint NORMAL_PRIORITY_CLASS = 0x00000020;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

        /// <summary>
        /// Cleans up any stale processes in Session 0, and ensures the target process is running
        /// inside the active interactive user console session.
        /// </summary>
        public static bool EnsureProcessRunningInSession(string exePath, string arguments, string processName, Action<string> logger = null)
        {
            try
            {
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return false;

                // 1. Check if process is already running in ANY user session
                try
                {
                    Process[] procs = Process.GetProcessesByName(processName);
                    bool alreadyRunning = false;
                    foreach (var p in procs)
                    {
                        try
                        {
                            if (p.SessionId == 0)
                            {
                                p.Kill(); // Clean up accidental Session 0 instance
                            }
                            else
                            {
                                alreadyRunning = true;
                            }
                        }
                        catch
                        {
                            alreadyRunning = true;
                        }
                    }

                    if (alreadyRunning)
                    {
                        return true; // Already running in user session! Do not touch or spawn duplicates!
                    }
                }
                catch { }

                // 2. Query active user console session
                uint activeSessionId = WTSGetActiveConsoleSessionId();
                if (activeSessionId == 0xFFFFFFFF || activeSessionId == 0)
                {
                    return false;
                }

                // 4. Launch process into activeSessionId using CreateProcessAsUser
                IntPtr hUserToken = IntPtr.Zero;
                IntPtr hPrimaryToken = IntPtr.Zero;

                try
                {
                    if (!WTSQueryUserToken(activeSessionId, out hUserToken))
                    {
                        if (logger != null) logger("WTSQueryUserToken failed for Session " + activeSessionId + " (Error: " + Marshal.GetLastWin32Error() + ")");
                        return false;
                    }

                    if (!DuplicateTokenEx(hUserToken, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out hPrimaryToken))
                    {
                        hPrimaryToken = hUserToken;
                    }

                    STARTUPINFO si = new STARTUPINFO();
                    si.cb = Marshal.SizeOf(si);
                    si.lpDesktop = @"winsta0\default";

                    PROCESS_INFORMATION pi = new PROCESS_INFORMATION();
                    string cmd = "\"" + exePath + "\"" + (string.IsNullOrEmpty(arguments) ? "" : " " + arguments);

                    IntPtr lpEnv = IntPtr.Zero;
                    try
                    {
                        CreateEnvironmentBlock(out lpEnv, hPrimaryToken, false);
                    }
                    catch { }

                    bool ok = false;
                    try
                    {
                        ok = CreateProcessAsUser(
                            hPrimaryToken,
                            null,
                            cmd,
                            IntPtr.Zero,
                            IntPtr.Zero,
                            false,
                            NORMAL_PRIORITY_CLASS | (lpEnv != IntPtr.Zero ? CREATE_UNICODE_ENVIRONMENT : 0),
                            lpEnv,
                            Path.GetDirectoryName(exePath),
                            ref si,
                            out pi);
                    }
                    finally
                    {
                        if (lpEnv != IntPtr.Zero)
                        {
                            try { DestroyEnvironmentBlock(lpEnv); } catch { }
                        }
                    }

                    if (ok)
                    {
                        if (logger != null) logger(processName + " successfully launched in User Session " + activeSessionId + " (PID: " + pi.dwProcessId + ").");
                        if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
                        if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                        return true;
                    }
                    else
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (logger != null) logger("CreateProcessAsUser for " + processName + " failed with Win32 error code: " + err);
                    }
                }
                finally
                {
                    if (hPrimaryToken != IntPtr.Zero) CloseHandle(hPrimaryToken);
                    if (hUserToken != IntPtr.Zero) CloseHandle(hUserToken);
                }
            }
            catch (Exception ex)
            {
                if (logger != null) logger("EnsureProcessRunningInSession (" + processName + ") exception: " + ex.Message);
            }

            return false;
        }

        public static bool EnsureWatcherRunning(string managerExe, Action<string> logger = null)
        {
            return EnsureProcessRunningInSession(managerExe, "--watcher", "ProjectRManager", logger);
        }

        public static bool EnsureRemoteAgentRunning(string remoteAgentExe, Action<string> logger = null)
        {
            return EnsureRemoteAgentRunning(remoteAgentExe, "", "", "", logger);
        }

        public static bool EnsureRemoteAgentRunning(string remoteAgentExe, string serverUrl, string deviceId, string token, Action<string> logger = null)
        {
            if (!IsWindows10OrGreater())
            {
                if (!_loggedLegacySkip)
                {
                    _loggedLegacySkip = true;
                    if (logger != null) logger("[COMPAT] OS Windows lawas (< Windows 10) terdeteksi. Melewati ProjectRRemoteAgent (Golang membutuhkan Windows 10+). Fitur MDM, Inventory & Blokir Website tetap aktif.");
                }
                return false;
            }

            string args = "";
            if (!string.IsNullOrEmpty(serverUrl) || !string.IsNullOrEmpty(deviceId))
            {
                args = string.Format("--server \"{0}\" --device-id \"{1}\" --token \"{2}\"", serverUrl ?? "", deviceId ?? "", token ?? "");
            }
            return EnsureProcessRunningInSession(remoteAgentExe, args, "ProjectRRemoteAgent", logger);
        }
    }
}
