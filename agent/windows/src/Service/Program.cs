using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Timers;
using ProjectR.Agent.Common;

namespace ProjectR.Agent.Service
{
    public class ProjectRService : ServiceBase
    {
        public const string ServiceNameConst = "ProjectRAgent";
        public const string ServiceDisplayName = "Project R Asset & Security Agent";
        public const string ServiceDescription = "Provides autonomous asset inventory, 60s telemetry heartbeat, and GPO policy enforcement for Project R.";

        private System.Timers.Timer _timer;
        private DateTime _lastFullInventory = DateTime.MinValue;
        private bool _isBusy = false;
        private static Mutex _daemonMutex;
        private static PolicyData _lastPolicies;
        private static string _cachedDeviceId = "";
        private static Thread _protectionThread = null;
        private static bool _protectionRunning = false;
        private static readonly object _alertLock = new object();
        private static readonly List<BrowserAlertItem> _pendingBrowserAlerts = new List<BrowserAlertItem>();

        public ProjectRService()
        {
            this.ServiceName = ServiceNameConst;
            this.CanStop = true;
            this.CanPauseAndContinue = false;
            this.AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            StartAgentCore();
        }

        protected override void OnStop()
        {
            StopAgentCore();
        }

        public bool StartAgentCore()
        {
            // Daemon Mutex (Guarantees only 1 background service or runner executes simultaneously)
            bool createdNew;
            _daemonMutex = new Mutex(true, "Global\\ProjectR_Agent_Daemon_Mutex", out createdNew);
            if (!createdNew)
            {
                Log("[WARNING] Another background daemon instance of Project R Agent is already active.");
                return false;
            }

            Log("Project R Agent Service starting...");

            // Run initial sync in background thread
            ThreadPool.QueueUserWorkItem(delegate
            {
                RunCycle(true);
            });

            // Start 60-second periodic timer (1 minute heartbeat)
            _timer = new System.Timers.Timer(60000);
            _timer.Elapsed += delegate
            {
                RunCycle(false);
            };
            _timer.AutoReset = true;
            _timer.Start();

            // Start active kernel/service-level process protection watcher (500ms loop)
            _lastPolicies = AgentConfig.LoadPolicies();
            if (_lastPolicies == null)
            {
                LockState initialLock = AgentConfig.LoadLockState();
                _lastPolicies = new PolicyData
                {
                    disable_cmd = initialLock.IsCmdDisabled,
                    disable_taskmgr = initialLock.IsTaskMgrDisabled,
                    warning_screen = initialLock.IsLocked
                };
            }
            StartActiveProtectionWatcher();

            Log("Project R Agent Service started successfully. Heartbeat active (60s), full inventory every 1 hour.");
            return true;
        }

        public void StopAgentCore()
        {
            Log("Project R Agent Service stopping...");
            _protectionRunning = false;
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
                _timer = null;
            }

            if (_daemonMutex != null)
            {
                try
                {
                    _daemonMutex.ReleaseMutex();
                    _daemonMutex.Close();
                }
                catch { }
                _daemonMutex = null;
            }

            Log("Project R Agent Service stopped.");
        }

        private static void StartActiveProtectionWatcher()
        {
            if (_protectionRunning) return;
            _protectionRunning = true;
            _protectionThread = new Thread(() =>
            {
                int browserScanTick = 0;
                int daemonCheckTick = 0;
                while (_protectionRunning)
                {
                    try
                    {
                        // Periodically ensure user session daemons (ProjectRManager & ProjectRRemoteAgent) are running
                        daemonCheckTick++;
                        if (daemonCheckTick >= 120) // Every 60 seconds (throttled fallback)
                        {
                            daemonCheckTick = 0;
                            EnsureUserSessionDaemons();
                        }
                        if (_lastPolicies != null)
                        {
                            // 1. Enforce CMD & PowerShell restrictions
                            if (_lastPolicies.disable_cmd)
                            {
                                string[] termApps = new string[] { "WindowsTerminal", "wt", "OpenConsole" };
                                foreach (string term in termApps)
                                {
                                    foreach (Process p in Process.GetProcessesByName(term))
                                    {
                                        try { p.Kill(); } catch { }
                                    }
                                }

                                string[] shells = new string[] { "powershell", "pwsh", "powershell_ise", "cmd" };
                                foreach (string sh in shells)
                                {
                                    foreach (Process p in Process.GetProcessesByName(sh))
                                    {
                                        try
                                        {
                                            // Terminate any interactive user shell (SessionId > 0)
                                            if (p.SessionId > 0)
                                            {
                                                p.Kill();
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }

                            // 2. Enforce Task Manager restriction
                            if (_lastPolicies.disable_taskmgr)
                            {
                                foreach (Process p in Process.GetProcessesByName("Taskmgr"))
                                {
                                    try { p.Kill(); } catch { }
                                }
                            }

                            // 3. Periodic Browser Window Inspection (every 10 seconds: 20 * 500ms)
                            if (_lastPolicies.browser_monitor_enabled &&
                                _lastPolicies.browser_blacklist != null &&
                                _lastPolicies.browser_blacklist.Count > 0)
                            {
                                browserScanTick++;
                                if (browserScanTick >= 20)
                                {
                                    browserScanTick = 0;
                                    PerformBrowserScan();
                                }
                            }
                            else
                            {
                                browserScanTick = 0;
                            }
                        }
                    }
                    catch { }

                    Thread.Sleep(500);
                }
            });
            _protectionThread.IsBackground = true;
            _protectionThread.Start();
        }

        private static readonly object _daemonLock = new object();
        private static void EnsureUserSessionDaemons()
        {
            lock (_daemonLock)
            {
                try
                {
                    string managerExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProjectRManager.exe");
                    if (File.Exists(managerExe))
                    {
                        SessionLauncher.EnsureWatcherRunning(managerExe, Log);
                    }

                    string remoteAgentExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProjectRRemoteAgent.exe");
                    if (File.Exists(remoteAgentExe))
                    {
                        AgentConfig cfg = AgentConfig.Load();
                        string devId = _cachedDeviceId;
                        if (string.IsNullOrEmpty(devId))
                        {
                            try { devId = SystemInfo.Collect(cfg.Token, cfg.Tag, cfg.Version).device_id; _cachedDeviceId = devId; } catch { }
                        }
                        SessionLauncher.EnsureRemoteAgentRunning(remoteAgentExe, cfg.ServerUrl, devId, cfg.Token, Log);
                    }
                }
                catch (Exception ex)
                {
                    Log("EnsureUserSessionDaemons error: " + ex.Message);
                }
            }
        }

        private static void PerformBrowserScan()
        {
            try
            {
                if (_lastPolicies == null || !_lastPolicies.browser_monitor_enabled) return;

                if (!BrowserMonitor.HasAnyBrowserRunning()) return;

                string loggedUser = null;
                try { loggedUser = SystemInfo.GetLoggedInUser(); } catch { }

                List<BrowserAlertItem> detected = BrowserMonitor.ScanActiveBrowserAlerts(_lastPolicies.browser_blacklist, loggedUser);
                if (detected != null && detected.Count > 0)
                {
                    lock (_alertLock)
                    {
                        foreach (var item in detected)
                        {
                            bool exists = false;
                            foreach (var p in _pendingBrowserAlerts)
                            {
                                if (string.Equals(p.keyword, item.keyword, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(p.title, item.title, StringComparison.OrdinalIgnoreCase))
                                {
                                    exists = true;
                                    break;
                                }
                            }
                            if (!exists)
                            {
                                _pendingBrowserAlerts.Add(item);
                                Log("[BROWSER ALERT] Deteksi akses situs terlarang: [" + item.keyword + "] di judul jendela '" + item.title + "' (" + item.browser + ") oleh " + item.user);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("[BROWSER MONITOR ERROR] " + ex.Message);
            }
        }

        public void RunCycle(bool forceInventory)
        {
            if (_isBusy) return;
            _isBusy = true;

            try
            {
                // Ensure legacy tasks and offending scripts from MONASys prototype are eliminated
                PolicyEngine.CleanLegacyArtifacts();

                AgentConfig cfg = AgentConfig.Load();
                if (string.IsNullOrEmpty(cfg.ServerUrl))
                {
                    Log("Warning: Server URL is not configured.");
                    return;
                }

                // 1. Full Inventory: on start, manual force, or every 1 hour
                if (forceInventory || DateTime.Now >= _lastFullInventory.AddHours(1))
                {
                    try
                    {
                        Log("Submitting hardware, software, monitors, antivirus, and OS inventory to " + cfg.ServerUrl + "...");
                        DeviceInventoryData inv = SystemInfo.Collect(cfg.Token, cfg.Tag, cfg.Version);
                        GenericApiResponse resp = ApiClient.RegisterDevice(cfg.ServerUrl, inv);
                        if (resp != null && resp.status == "success")
                        {
                            _lastFullInventory = DateTime.Now;
                            Log("Inventory registered successfully. (" + inv.softwares.Count + " apps, " + inv.monitors.Count + " monitors, " + inv.antivirus.Count + " AV)");
                        }
                        else
                        {
                            Log("Inventory response: " + (resp != null ? resp.message : "No response"));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("Inventory error: " + ex.Message);
                    }
                }

                // 2. Heartbeat & Policy Sync (every 60 seconds)
                try
                {
                    DeviceInventoryData quickInfo = SystemInfo.Collect(cfg.Token, cfg.Tag, cfg.Version);
                    _cachedDeviceId = quickInfo.device_id;
                    HeartbeatRequest hb = new HeartbeatRequest();
                    hb.device_id = quickInfo.device_id;
                    hb.hostname = Environment.MachineName;
                    hb.tag = cfg.Tag;
                    hb.version = cfg.Version;
                    hb.logged_user = quickInfo.last_user;
                    hb.battery_percent = quickInfo.battery.percent;
                    hb.token = cfg.Token;

                    // Screen unlock sync: check if user unlocked warning screen locally with PIN
                    string unlockFlagPath = Path.Combine(AgentConfig.GetConfigDirectory(), "screen_unlocked.flag");
                    if (File.Exists(unlockFlagPath))
                    {
                        try
                        {
                            string flagContent = File.ReadAllText(unlockFlagPath, Encoding.UTF8).Trim();
                            string[] parts = flagContent.Split('|');
                            hb.screen_unlocked = true;
                            if (parts.Length > 1) hb.pin = parts[1];
                        }
                        catch { }
                    }

                    // Evaluate actual observed compliance against desired policies (PRD Phase 1)
                    hb.evaluations = PolicyEngine.EvaluateCompliance(_lastPolicies ?? new PolicyData());

                    // Perform immediate browser check right before sending heartbeat to catch anything open right now
                    if (_lastPolicies != null && _lastPolicies.browser_monitor_enabled)
                    {
                        PerformBrowserScan();
                    }

                    // Attach pending browser alerts to heartbeat
                    lock (_alertLock)
                    {
                        // Drain alerts from user-session watcher (ProjectRManager IPC queue)
                        List<BrowserAlertItem> queuedAlerts = BrowserMonitor.DrainQueue();
                        foreach (var q in queuedAlerts)
                        {
                            bool exists = false;
                            foreach (var p in _pendingBrowserAlerts)
                            {
                                if (string.Equals(p.keyword, q.keyword, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(p.title, q.title, StringComparison.OrdinalIgnoreCase))
                                {
                                    exists = true;
                                    break;
                                }
                            }
                            if (!exists)
                            {
                                _pendingBrowserAlerts.Add(q);
                            }
                        }

                        if (_pendingBrowserAlerts.Count > 0)
                        {
                            hb.browser_alerts = new List<BrowserAlertItem>(_pendingBrowserAlerts);
                            _pendingBrowserAlerts.Clear();
                            Log("Melampirkan " + hb.browser_alerts.Count + " log peringatan browser ke heartbeat payload.");
                        }
                    }

                    HeartbeatResponse hbResp = ApiClient.SendHeartbeat(cfg.ServerUrl, hb);
                    if (hbResp != null)
                    {
                        if (File.Exists(unlockFlagPath))
                        {
                            try { File.Delete(unlockFlagPath); } catch { }
                        }

                        try
                        {
                            AgentRuntimeStatus st = new AgentRuntimeStatus
                            {
                                IsConnected = true,
                                LastHeartbeatAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                                LastError = "",
                                ConsecutiveFailures = 0
                            };
                            AgentConfig.SaveStatus(st);
                        }
                        catch { }

                        if (hbResp.policies != null)
                        {
                            _lastPolicies = hbResp.policies;
                            AgentConfig.SavePolicies(_lastPolicies);
                            PolicyEngine.ApplyPolicies(hbResp.policies);
                            HandleWarningLock(hbResp.policies.warning_screen, hbResp.policies.warning_unlock_password, hbResp.policies.disable_cmd, hbResp.policies.disable_taskmgr);
                        }

                        if (!string.IsNullOrEmpty(hbResp.tag) && hbResp.tag != cfg.Tag)
                        {
                            Log("Updating local TAG to " + hbResp.tag + " per server request.");
                            cfg.Tag = hbResp.tag;
                            cfg.Save();
                        }

                        // Silent OTA Auto-Update check (PRD Phase 1)
                        if (!string.IsNullOrEmpty(hbResp.version))
                        {
                            AutoUpdateManager.CheckAndTriggerUpdate(cfg.ServerUrl, hbResp.version, AgentConfig.CurrentVersion, Log);
                        }

                        // Deploy Tasks Execution (Wallpaper, scripts, etc.)
                        if (hbResp.deploy_tasks != null && hbResp.deploy_tasks.Count > 0)
                        {
                            Log("Menerima " + hbResp.deploy_tasks.Count + " tugas deployment dari server. Memulai eksekusi...");
                            DeployExecutor.ExecuteTasks(hbResp.deploy_tasks, cfg, Log);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("Heartbeat error: " + ex.Message);
                    try
                    {
                        AgentRuntimeStatus prev = AgentConfig.LoadStatus();
                        AgentRuntimeStatus st = new AgentRuntimeStatus
                        {
                            IsConnected = false,
                            LastHeartbeatAt = prev != null ? prev.LastHeartbeatAt : "",
                            LastError = ex.Message,
                            ConsecutiveFailures = (prev != null ? prev.ConsecutiveFailures : 0) + 1
                        };
                        AgentConfig.SaveStatus(st);
                    }
                    catch { }
                }
            }
            finally
            {
                _isBusy = false;
            }
        }

        private static void Log(string msg)
        {
            try
            {
                string logFile = Path.Combine(AgentConfig.GetConfigDirectory(), "agent.log");
                string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + msg + Environment.NewLine;
                File.AppendAllText(logFile, line);
                Console.WriteLine(line.TrimEnd());
            }
            catch { }
        }

        public static int Main(string[] args)
        {
            if (args.Length > 0)
            {
                string cmd = args[0].ToLower().Trim();
                if (cmd == "--install-service" || cmd == "/install")
                {
                    return InstallService();
                }
                else if (cmd == "--uninstall-service" || cmd == "/uninstall")
                {
                    return UninstallService();
                }
                else if (cmd == "--run" || cmd == "/run")
                {
                    Console.WriteLine("Starting Project R Agent in interactive console mode...");
                    ProjectRService s = new ProjectRService();
                    if (!s.StartAgentCore())
                    {
                        Console.WriteLine("[ERROR] Another daemon instance is already running.");
                        return 1;
                    }
                    Console.WriteLine("Press ENTER to exit.");
                    Console.ReadLine();
                    s.StopAgentCore();
                    return 0;
                }
                else if (cmd == "--inventory" || cmd == "/inventory")
                {
                    Console.WriteLine("Executing immediate inventory push...");
                    ProjectRService s = new ProjectRService();
                    s.RunCycle(true);
                    Console.WriteLine("Inventory completed.");
                    return 0;
                }
                else if (cmd == "--verify-token" || cmd == "/verify-token")
                {
                    if (args.Length < 3)
                    {
                        Console.WriteLine("[ERROR] Usage: ProjectRAgent.exe --verify-token <server_url> <token>");
                        WriteVerifyStatus("ERROR", "Parameter verifikasi tidak lengkap.");
                        return 1;
                    }
                    string srv = ApiClient.SanitizeUrl(args[1]);
                    string tok = args[2].Trim();

                    try
                    {
                        VerifyTokenResponse resp = ApiClient.VerifyToken(srv, tok);
                        if (resp != null && resp.valid)
                        {
                            Console.WriteLine("[SUCCESS] Token is valid.");
                            WriteVerifyStatus("SUCCESS", "Token deployment valid.");
                            return 0;
                        }
                        else
                        {
                            string msg = (resp != null && !string.IsNullOrEmpty(resp.message)) ? resp.message : "Deployment token ditolak oleh server Project R.";
                            Console.WriteLine("[ERROR] " + msg);
                            WriteVerifyStatus("TOKEN_REJECTED", msg);
                            return 1;
                        }
                    }
                    catch (WebException wex)
                    {
                        string netMsg;
                        if (wex.Status == WebExceptionStatus.NameResolutionFailure)
                            netMsg = "Domain/URL server tidak dapat ditemukan (DNS resolve gagal). Periksa apakah URL server benar.";
                        else if (wex.Status == WebExceptionStatus.Timeout)
                            netMsg = "Koneksi ke server timeout (waktu habis). Pastikan server aktif dan dapat diakses.";
                        else if (wex.Status == WebExceptionStatus.ConnectFailure)
                            netMsg = "Koneksi ke server ditolak atau port server tidak dapat diakses.";
                        else if (wex.Status == WebExceptionStatus.TrustFailure || wex.Status == WebExceptionStatus.SecureChannelFailure)
                            netMsg = "Sertifikat keamanan SSL/TLS tidak dapat diverifikasi oleh sistem operasi Windows.";
                        else
                            netMsg = wex.Message;

                        Console.WriteLine("[ERROR] Network error: " + netMsg);
                        WriteVerifyStatus("NETWORK_ERROR", netMsg);
                        return 2;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[ERROR] Verification failed: " + ex.Message);
                        WriteVerifyStatus("ERROR", ex.Message);
                        return 2;
                    }
                }
                else if (cmd == "--verify-uninstall" || cmd == "/verify-uninstall")
                {
                    string uninstToken = (args.Length > 1) ? args[1].Trim() : "";
                    if (string.IsNullOrEmpty(uninstToken))
                    {
                        Console.WriteLine("[ERROR] Missing uninstall token argument.");
                        WriteVerifyStatus("ERROR", "Token uninstalasi wajib diisi.");
                        return 1;
                    }

                    try
                    {
                        AgentConfig cfg = AgentConfig.Load();
                        if (string.IsNullOrEmpty(cfg.ServerUrl))
                        {
                            Console.WriteLine("[ERROR] Server URL is not configured.");
                            WriteVerifyStatus("ERROR", "URL server belum dikonfigurasi.");
                            return 1;
                        }

                        string srv = ApiClient.SanitizeUrl(cfg.ServerUrl);
                        string deviceId = "";
                        try { deviceId = SystemInfo.Collect(cfg.Token, cfg.Tag, cfg.Version).device_id; } catch { }
                        string uuid = "";
                        try { uuid = SystemInfo.GetHardwareUuid(); } catch { }

                        VerifyUninstallResponse vResp = ApiClient.VerifyUninstall(srv, uninstToken, Environment.MachineName, deviceId, uuid);
                        if (vResp != null && vResp.authorized)
                        {
                            Console.WriteLine("[SUCCESS] Uninstallation authorized by server. Device removed from inventory.");
                            WriteVerifyStatus("SUCCESS", "Otorisasi uninstalasi diterima.");
                            return 0;
                        }
                        else
                        {
                            string msg = (vResp != null && !string.IsNullOrEmpty(vResp.message)) ? vResp.message : "Token uninstalasi tidak valid atau ditolak server.";
                            Console.WriteLine("[ERROR] " + msg);
                            WriteVerifyStatus("TOKEN_REJECTED", msg);
                            return 1;
                        }
                    }
                    catch (WebException wex)
                    {
                        string netMsg;
                        if (wex.Status == WebExceptionStatus.NameResolutionFailure)
                            netMsg = "Domain/URL server tidak dapat ditemukan (DNS resolve gagal).";
                        else if (wex.Status == WebExceptionStatus.Timeout)
                            netMsg = "Koneksi ke server timeout.";
                        else if (wex.Status == WebExceptionStatus.TrustFailure || wex.Status == WebExceptionStatus.SecureChannelFailure)
                            netMsg = "Sertifikat keamanan SSL/TLS tidak dapat diverifikasi.";
                        else
                            netMsg = wex.Message;

                        Console.WriteLine("[ERROR] Verification error: " + netMsg);
                        WriteVerifyStatus("NETWORK_ERROR", netMsg);
                        return 2;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[ERROR] Verification error: " + ex.Message);
                        WriteVerifyStatus("ERROR", ex.Message);
                        return 2;
                    }
                }
            }

            if (Environment.UserInteractive && args.Length == 0)
            {
                try
                {
                    string dir = AppDomain.CurrentDomain.BaseDirectory;
                    string setupExe = Path.Combine(dir, "ProjectR-Agent-Setup-x64.exe");
                    if (!File.Exists(setupExe)) setupExe = Path.Combine(dir, "ProjectR-Agent-Setup-x86.exe");
                    if (File.Exists(setupExe))
                    {
                        Process.Start(setupExe);
                        return 0;
                    }

                    string managerExe = Path.Combine(dir, "ProjectRManager.exe");
                    if (File.Exists(managerExe))
                    {
                        Process.Start(managerExe);
                        return 0;
                    }

                    string installBat = Path.Combine(dir, "install.bat");
                    if (File.Exists(installBat))
                    {
                        Process.Start(installBat);
                        return 0;
                    }
                }
                catch { }

                ShowInteractiveHelp();
                return 0;
            }

            // Normal Windows Service Run
            ServiceBase.Run(new ProjectRService());
            return 0;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

        private static void ShowInteractiveHelp()
        {
            MessageBox(
                IntPtr.Zero,
                "ProjectRAgent.exe adalah background service untuk Windows.\n\n" +
                "Untuk memasang Project R Agent di komputer ini:\n" +
                "• Jalankan 'ProjectR-Agent-Setup-x64.exe' (Wizard Instalasi Setup)\n" +
                "• Atau jalankan 'ProjectRManager.exe' (Manager GUI)\n" +
                "• Atau jalankan 'install.bat'",
                "Project R Agent",
                0x00000040 // MB_ICONINFORMATION
            );
        }

        private static void WriteVerifyStatus(string status, string message)
        {
            try
            {
                string tempDir = Path.GetTempPath();
                string statusFile = Path.Combine(tempDir, "projectr_verify_status.txt");
                File.WriteAllText(statusFile, status + "\r\n" + message, Encoding.UTF8);
            }
            catch { }
        }

        private static void HandleWarningLock(bool isWarning, string unlockPassword, bool disableCmd, bool disableTaskMgr)
        {
            try
            {
                LockState newState = new LockState
                {
                    IsLocked = isWarning,
                    UnlockPassword = unlockPassword ?? "",
                    LockedAt = isWarning ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") : "",
                    IsCmdDisabled = disableCmd,
                    IsTaskMgrDisabled = disableTaskMgr
                };
                AgentConfig.SaveLockState(newState);

                if (isWarning || disableCmd || disableTaskMgr)
                {
                    bool isProcessRunning = false;
                    foreach (Process p in Process.GetProcessesByName("ProjectRManager"))
                    {
                        isProcessRunning = true;
                        break;
                    }

                    if (!isProcessRunning)
                    {
                        LaunchInteractiveLockScreen();
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Warning lock handling error: " + ex.Message);
            }
        }

        private static void LaunchInteractiveLockScreen()
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                string managerExe = Path.Combine(dir, "ProjectRManager.exe");
                if (!File.Exists(managerExe)) return;

                // Create runner batch file in config directory to avoid space-escaping issues with schtasks
                string configDir = AgentConfig.GetConfigDirectory();
                string runnerCmd = Path.Combine(configDir, "run_lock.cmd");
                string cmdBody = "@echo off\r\nstart \"\" \"" + managerExe + "\" --watcher\r\n";
                File.WriteAllText(runnerCmd, cmdBody, Encoding.ASCII);

                // Ensure scheduled task exists to launch interactively in active user session
                Process pCreate = new Process();
                pCreate.StartInfo.FileName = "schtasks.exe";
                pCreate.StartInfo.Arguments = "/create /tn \"ProjectR-WarningLock\" /tr \"" + runnerCmd + "\" /sc onlogon /f /rl highest";
                pCreate.StartInfo.UseShellExecute = false;
                pCreate.StartInfo.CreateNoWindow = true;
                pCreate.Start();
                pCreate.WaitForExit(3000);

                // Run task now in user session
                Process pRun = new Process();
                pRun.StartInfo.FileName = "schtasks.exe";
                pRun.StartInfo.Arguments = "/run /tn \"ProjectR-WarningLock\"";
                pRun.StartInfo.UseShellExecute = false;
                pRun.StartInfo.CreateNoWindow = true;
                pRun.Start();
                pRun.WaitForExit(3000);
            }
            catch (Exception ex)
            {
                Log("LaunchInteractiveLockScreen error: " + ex.Message);
            }
        }

        private static int InstallService()
        {
            Console.WriteLine("[*] Registering Project R Windows Service...");
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                string binPath = "\"" + exePath + "\"";

                Process p = new Process();
                p.StartInfo.FileName = "sc.exe";
                p.StartInfo.Arguments = "create " + ServiceNameConst + " binPath= " + binPath + " start= auto DisplayName= \"" + ServiceDisplayName + "\"";
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.Start();
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                Console.WriteLine(output);

                // Set description
                Process pDesc = new Process();
                pDesc.StartInfo.FileName = "sc.exe";
                pDesc.StartInfo.Arguments = "description " + ServiceNameConst + " \"" + ServiceDescription + "\"";
                pDesc.StartInfo.UseShellExecute = false;
                pDesc.Start();
                pDesc.WaitForExit();

                // Set auto-recovery on failure
                Process pRec = new Process();
                pRec.StartInfo.FileName = "sc.exe";
                pRec.StartInfo.Arguments = "failure " + ServiceNameConst + " reset= 86400 actions= restart/60000/restart/60000/restart/60000";
                pRec.StartInfo.UseShellExecute = false;
                pRec.Start();
                pRec.WaitForExit();

                // Start service
                Process pStart = new Process();
                pStart.StartInfo.FileName = "net.exe";
                pStart.StartInfo.Arguments = "start " + ServiceNameConst;
                pStart.StartInfo.UseShellExecute = false;
                pStart.Start();
                pStart.WaitForExit();

                Console.WriteLine("[SUCCESS] Project R Agent Service installed and started.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ERROR] Failed to install service: " + ex.Message);
                return 1;
            }
        }

        private static int UninstallService()
        {
            Console.WriteLine("[*] Removing Project R Windows Service...");
            try
            {
                Process pStop = new Process();
                pStop.StartInfo.FileName = "net.exe";
                pStop.StartInfo.Arguments = "stop " + ServiceNameConst;
                pStop.StartInfo.UseShellExecute = false;
                pStop.Start();
                pStop.WaitForExit();

                Process p = new Process();
                p.StartInfo.FileName = "sc.exe";
                p.StartInfo.Arguments = "delete " + ServiceNameConst;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.Start();
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                Console.WriteLine(output);

                PolicyEngine.RestoreDefaultPolicies();

                // Clean up ProgramData configuration folder completely (Zero Traces)
                try
                {
                    string cfgDir = AgentConfig.GetConfigDirectory();
                    if (Directory.Exists(cfgDir))
                    {
                        Directory.Delete(cfgDir, true);
                    }
                    string parent = Path.GetDirectoryName(cfgDir);
                    if (Directory.Exists(parent) && Directory.GetFileSystemEntries(parent).Length == 0)
                    {
                        Directory.Delete(parent);
                    }
                }
                catch { }

                Console.WriteLine("[SUCCESS] Project R Agent Service removed.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ERROR] Failed to uninstall service: " + ex.Message);
                return 1;
            }
        }
    }
}
