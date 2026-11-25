using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;

namespace ProjectR.Agent.Common
{
    public class LockState
    {
        public bool IsLocked { get; set; }
        public string UnlockPassword { get; set; }
        public string LockedAt { get; set; }
        public bool IsCmdDisabled { get; set; }
        public bool IsTaskMgrDisabled { get; set; }

        public LockState()
        {
            IsLocked = false;
            UnlockPassword = "";
            LockedAt = "";
            IsCmdDisabled = false;
            IsTaskMgrDisabled = false;
        }
    }

    public class AgentRuntimeStatus
    {
        public bool IsConnected { get; set; }
        public string LastHeartbeatAt { get; set; }
        public string LastError { get; set; }
        public int ConsecutiveFailures { get; set; }

        public AgentRuntimeStatus()
        {
            IsConnected = false;
            LastHeartbeatAt = "";
            LastError = "";
            ConsecutiveFailures = 0;
        }
    }

    public class AgentConfig
    {
        public const string CurrentVersion = "2.4.7";

        public string ServerUrl { get; set; }
        public string Token { get; set; }
        public string Tag { get; set; }
        public string Version { get; set; }
        public int HeartbeatIntervalSeconds { get; set; }
        public string InstalledAt { get; set; }

        public AgentConfig()
        {
            ServerUrl = "";
            Token = "";
            Tag = "";
            Version = CurrentVersion;
            HeartbeatIntervalSeconds = 60;
            InstalledAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        public static string GetConfigDirectory()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProjectR", "Agent");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            try
            {
                // Ensure Users have Write/Modify permissions on the config folder
                DirectoryInfo dInfo = new DirectoryInfo(dir);
                DirectorySecurity dSecurity = dInfo.GetAccessControl();
                SecurityIdentifier usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
                dSecurity.AddAccessRule(new FileSystemAccessRule(
                    usersSid,
                    FileSystemRights.Modify,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow
                ));
                dInfo.SetAccessControl(dSecurity);
            }
            catch { }

            return dir;
        }

        public static string GetConfigFilePath()
        {
            return Path.Combine(GetConfigDirectory(), "agent.json");
        }

        public static string GetTagFilePath()
        {
            return Path.Combine(GetConfigDirectory(), "asset.tag");
        }

        public static string GetLockFilePath()
        {
            return Path.Combine(GetConfigDirectory(), "lock.json");
        }

        public static string GetPoliciesFilePath()
        {
            return Path.Combine(GetConfigDirectory(), "policies.json");
        }

        public static PolicyData LoadPolicies()
        {
            string path = GetPoliciesFilePath();
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path, Encoding.UTF8);
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    PolicyData pol = ser.Deserialize<PolicyData>(json);
                    if (pol != null) return pol;
                }
                catch { }
            }
            return null;
        }

        public static void SavePolicies(PolicyData pol)
        {
            if (pol == null) return;
            try
            {
                string path = GetPoliciesFilePath();
                JavaScriptSerializer ser = new JavaScriptSerializer();
                string json = ser.Serialize(pol);
                File.WriteAllText(path, json, Encoding.UTF8);
            }
            catch { }
        }

        private static LockState _lastValidLockState = null;
        private static readonly object _lockFileSync = new object();

        public static LockState LoadLockState()
        {
            lock (_lockFileSync)
            {
                string path = GetLockFilePath();
                if (File.Exists(path))
                {
                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        try
                        {
                            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                            {
                                string json = sr.ReadToEnd();
                                if (!string.IsNullOrEmpty(json) && json.Length > 10)
                                {
                                    JavaScriptSerializer ser = new JavaScriptSerializer();
                                    LockState state = ser.Deserialize<LockState>(json);
                                    if (state != null)
                                    {
                                        _lastValidLockState = state;
                                        return state;
                                    }
                                }
                            }
                        }
                        catch
                        {
                            System.Threading.Thread.Sleep(20);
                        }
                    }

                    if (_lastValidLockState != null)
                    {
                        return _lastValidLockState;
                    }
                }
                else
                {
                    _lastValidLockState = new LockState { IsLocked = false };
                    return _lastValidLockState;
                }

                return _lastValidLockState ?? new LockState();
            }
        }

        public static void SaveLockState(LockState state)
        {
            lock (_lockFileSync)
            {
                string path = GetLockFilePath();
                if (state == null || (!state.IsLocked && !state.IsCmdDisabled && !state.IsTaskMgrDisabled))
                {
                    if (File.Exists(path))
                    {
                        try { File.Delete(path); } catch { }
                    }
                    _lastValidLockState = new LockState { IsLocked = false };
                    return;
                }

                _lastValidLockState = state;

                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        JavaScriptSerializer ser = new JavaScriptSerializer();
                        string json = ser.Serialize(state);
                        string tmpPath = path + ".tmp";

                        using (FileStream fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                        using (StreamWriter sw = new StreamWriter(fs, Encoding.UTF8))
                        {
                            sw.Write(json);
                            sw.Flush();
                        }

                        if (File.Exists(path))
                        {
                            File.Copy(tmpPath, path, true);
                            try { File.Delete(tmpPath); } catch { }
                        }
                        else
                        {
                            File.Move(tmpPath, path);
                        }
                        return;
                    }
                    catch
                    {
                        System.Threading.Thread.Sleep(25);
                    }
                }
            }
        }

        public static string GetStatusFilePath()
        {
            return Path.Combine(GetConfigDirectory(), "status.json");
        }

        public static AgentRuntimeStatus LoadStatus()
        {
            string path = GetStatusFilePath();
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path, Encoding.UTF8);
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    AgentRuntimeStatus st = ser.Deserialize<AgentRuntimeStatus>(json);
                    if (st != null) return st;
                }
                catch { }
            }
            return new AgentRuntimeStatus();
        }

        public static void SaveStatus(AgentRuntimeStatus status)
        {
            try
            {
                string path = GetStatusFilePath();
                JavaScriptSerializer ser = new JavaScriptSerializer();
                string json = ser.Serialize(status ?? new AgentRuntimeStatus());
                File.WriteAllText(path, json, Encoding.UTF8);
            }
            catch { }
        }

        public static AgentConfig Load()
        {
            string path = GetConfigFilePath();
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path, Encoding.UTF8);
                    JavaScriptSerializer serializer = new JavaScriptSerializer();
                    AgentConfig cfg = serializer.Deserialize<AgentConfig>(json);
                    if (cfg != null)
                    {
                        if (cfg.Version != CurrentVersion)
                        {
                            cfg.Version = CurrentVersion;
                            cfg.Save();
                        }
                        string tagFile = GetTagFilePath();
                        if (File.Exists(tagFile))
                        {
                            cfg.Tag = File.ReadAllText(tagFile, Encoding.UTF8).Trim();
                        }
                        return cfg;
                    }
                }
                catch { }
            }
            return new AgentConfig();
        }

        public void Save()
        {
            string dir = GetConfigDirectory();
            string path = GetConfigFilePath();

            if (File.Exists(path))
            {
                try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
            }

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(this);
            File.WriteAllText(path, json, Encoding.UTF8);

            string tagPath = GetTagFilePath();
            if (!string.IsNullOrEmpty(Tag))
            {
                if (File.Exists(tagPath))
                {
                    try { File.SetAttributes(tagPath, FileAttributes.Normal); } catch { }
                }
                File.WriteAllText(tagPath, Tag.Trim(), Encoding.UTF8);
            }
        }

        public static bool IsInstalled()
        {
            return File.Exists(GetConfigFilePath());
        }
    }
}
