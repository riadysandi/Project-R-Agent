using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ProjectR.Agent.Common
{
    public static class DeployExecutor
    {
        private static readonly object _deployLock = new object();

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);

        private const int SPI_SETDESKWALLPAPER = 20;
        private const int SPIF_UPDATEINIFILE = 0x01;
        private const int SPIF_SENDCHANGE = 0x02;

        public static void ExecuteTasks(List<DeployTaskItem> tasks, AgentConfig cfg, Action<string> logger = null)
        {
            if (tasks == null || tasks.Count == 0) return;

            ThreadPool.QueueUserWorkItem(delegate
            {
                lock (_deployLock)
                {
                    foreach (var task in tasks)
                    {
                        try
                        {
                            if (task == null) continue;

                            if (string.Equals(task.type, "wallpaper", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(task.slug, "wallpaper", StringComparison.OrdinalIgnoreCase))
                            {
                                ExecuteWallpaperDeploy(task, cfg, logger);
                            }
                            else if (string.Equals(task.type, "powershell_script", StringComparison.OrdinalIgnoreCase) &&
                                     !string.IsNullOrEmpty(task.script_url))
                            {
                                ExecuteScriptDeploy(task, cfg, logger);
                            }
                        }
                        catch (Exception ex)
                        {
                            if (logger != null) logger("Deploy task error [" + (task != null ? task.slug : "") + "]: " + ex.Message);
                        }
                    }
                }
            });
        }

        private static void ExecuteWallpaperDeploy(DeployTaskItem task, AgentConfig cfg, Action<string> logger)
        {
            if (string.IsNullOrEmpty(task.wallpaper_url)) return;

            if (logger != null) logger("[DEPLOY] Menjalankan Wallpaper Enforcer dari: " + task.wallpaper_url);

            string destDir = @"C:\Windows\Web\Wallpaper";
            try
            {
                if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
            }
            catch
            {
                destDir = AgentConfig.GetConfigDirectory();
            }

            string destFile = Path.Combine(destDir, "company_wallpaper.jpg");
            bool shouldDownload = true;

            // 1. Check MD5 hash to skip redundant download
            if (File.Exists(destFile) && !string.IsNullOrEmpty(task.wallpaper_hash) && !task.wallpaper_hash.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            {
                string localHash = GetMd5Hash(destFile);
                if (string.Equals(localHash, task.wallpaper_hash, StringComparison.OrdinalIgnoreCase))
                {
                    shouldDownload = false;
                    if (logger != null) logger("[DEPLOY] Hash wallpaper cocok (" + localHash + "). Melewati pengunduhan ulang.");
                }
            }

            // 2. Download wallpaper file
            if (shouldDownload)
            {
                try
                {
                    using (WebClient wc = new WebClient())
                    {
                        try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { } // TLS 1.2
                        wc.DownloadFile(task.wallpaper_url, destFile);
                    }
                    if (logger != null) logger("[DEPLOY] Berhasil mengunduh wallpaper baru ke: " + destFile);
                }
                catch (Exception ex)
                {
                    if (logger != null) logger("[DEPLOY] Gagal mengunduh wallpaper: " + ex.Message);
                    SendDeployLog(task.log_url, cfg != null ? cfg.Tag : null, "failed", "Gagal mengunduh file: " + ex.Message);
                    return;
                }
            }

            // 3. Enforce GPO & Personalization Sync Registry
            try
            {
                using (RegistryKey syncKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\SettingSync"))
                {
                    if (syncKey != null) syncKey.SetValue("DisablePersonalizationSync", 2, RegistryValueKind.DWord);
                }
            }
            catch { }

            // 4. Enforce for all active user profiles in HKEY_USERS
            try
            {
                string[] sids = Registry.Users.GetSubKeyNames();
                if (sids != null)
                {
                    foreach (string sid in sids)
                    {
                        if (sid.StartsWith("S-1-5-21-") && !sid.EndsWith("_Classes"))
                        {
                            try
                            {
                                // System Policy
                                using (RegistryKey pol = Registry.Users.CreateSubKey(sid + @"\Software\Microsoft\Windows\CurrentVersion\Policies\System"))
                                {
                                    if (pol != null)
                                    {
                                        pol.SetValue("Wallpaper", destFile, RegistryValueKind.String);
                                        pol.SetValue("WallpaperStyle", "2", RegistryValueKind.String); // 2 = Stretch
                                    }
                                }

                                // Desktop Control Panel
                                using (RegistryKey cp = Registry.Users.CreateSubKey(sid + @"\Control Panel\Desktop"))
                                {
                                    if (cp != null)
                                    {
                                        cp.SetValue("Wallpaper", destFile, RegistryValueKind.String);
                                        cp.SetValue("WallpaperStyle", "2", RegistryValueKind.String);
                                    }
                                }

                                // Clear Windows wallpaper theme cache
                                try
                                {
                                    using (RegistryKey profKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid))
                                    {
                                        if (profKey != null)
                                        {
                                            string profPath = profKey.GetValue("ProfileImagePath") as string;
                                            if (!string.IsNullOrEmpty(profPath))
                                            {
                                                string transcoded = Path.Combine(profPath, @"AppData\Roaming\Microsoft\Windows\Themes\TranscodedWallpaper");
                                                if (File.Exists(transcoded)) try { File.Delete(transcoded); } catch { }
                                                string cachedDir = Path.Combine(profPath, @"AppData\Roaming\Microsoft\Windows\Themes\CachedFiles");
                                                if (Directory.Exists(cachedDir))
                                                {
                                                    foreach (var f in Directory.GetFiles(cachedDir)) { try { File.Delete(f); } catch { } }
                                                }
                                            }
                                        }
                                    }
                                }
                                catch { }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            // 5. Enforce for Default User (New Users)
            try
            {
                ProcessStartInfo loadPsi = new ProcessStartInfo("reg.exe", "load \"HKU\\DefaultUser\" \"C:\\Users\\Default\\NTUSER.DAT\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using (Process regLoad = Process.Start(loadPsi))
                {
                    if (regLoad != null)
                    {
                        regLoad.WaitForExit(3000);
                        using (RegistryKey defPol = Registry.Users.CreateSubKey(@"DefaultUser\Software\Microsoft\Windows\CurrentVersion\Policies\System"))
                        {
                            if (defPol != null)
                            {
                                defPol.SetValue("Wallpaper", destFile, RegistryValueKind.String);
                                defPol.SetValue("WallpaperStyle", "2", RegistryValueKind.String);
                            }
                        }
                        using (RegistryKey defCp = Registry.Users.CreateSubKey(@"DefaultUser\Control Panel\Desktop"))
                        {
                            if (defCp != null)
                            {
                                defCp.SetValue("Wallpaper", destFile, RegistryValueKind.String);
                                defCp.SetValue("WallpaperStyle", "2", RegistryValueKind.String);
                            }
                        }
                        ProcessStartInfo unloadPsi = new ProcessStartInfo("reg.exe", "unload \"HKU\\DefaultUser\"")
                        {
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };
                        using (Process regUnload = Process.Start(unloadPsi))
                        {
                            if (regUnload != null) regUnload.WaitForExit(3000);
                        }
                    }
                }
            }
            catch { }

            // 6. Force immediate desktop refresh in interactive user session
            try
            {
                SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, destFile, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            }
            catch { }

            try
            {
                ProcessStartInfo rfPsi = new ProcessStartInfo("RUNDLL32.EXE", "user32.dll,UpdatePerUserSystemParameters")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using (Process p = Process.Start(rfPsi))
                {
                    if (p != null) p.WaitForExit(3000);
                }
            }
            catch { }

            // 7. Send Deploy Log back to server
            string msg = shouldDownload ? "Wallpaper diunduh dan dipasang secara sukses." : "Hash wallpaper cocok, kebijakan wallpaper diperbarui.";
            string devId = Environment.MachineName;
            SendDeployLog(task.log_url, devId, "success", msg);
            if (logger != null) logger("[DEPLOY] Sukses menerapkan wallpaper! Laporan dikirim ke server.");
        }

        private static void ExecuteScriptDeploy(DeployTaskItem task, AgentConfig cfg, Action<string> logger)
        {
            if (logger != null) logger("[DEPLOY] Menjalankan Script Deployment: " + task.title + " (" + task.slug + ")");

            string tempPs1 = Path.Combine(Path.GetTempPath(), "projectr_deploy_" + task.slug + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".ps1");
            try
            {
                using (WebClient wc = new WebClient())
                {
                    try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }
                    wc.DownloadFile(task.script_url, tempPs1);
                }

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "powershell.exe";
                psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + tempPs1 + "\"";
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;

                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(180000); // 3 minutes max
                    int exitCode = p.ExitCode;
                    string devId = Environment.MachineName;
                    if (exitCode == 0)
                    {
                        SendDeployLog(task.log_url, devId, "success", "Script " + task.slug + " berhasil dieksekusi.");
                        if (logger != null) logger("[DEPLOY] Script " + task.slug + " selesai dieksekusi dengan sukses.");
                    }
                    else
                    {
                        SendDeployLog(task.log_url, devId, "failed", "Script " + task.slug + " keluar dengan kode error: " + exitCode);
                        if (logger != null) logger("[DEPLOY] Script " + task.slug + " gagal dengan kode error: " + exitCode);
                    }
                }
            }
            catch (Exception ex)
            {
                if (logger != null) logger("[DEPLOY] Eksekusi script gagal: " + ex.Message);
                string devId = Environment.MachineName;
                SendDeployLog(task.log_url, devId, "failed", "Eksekusi script gagal: " + ex.Message);
            }
            finally
            {
                if (File.Exists(tempPs1))
                {
                    try { File.Delete(tempPs1); } catch { }
                }
            }
        }

        private static void SendDeployLog(string logUrl, string deviceId, string status, string message)
        {
            if (string.IsNullOrEmpty(logUrl)) return;
            try
            {
                string json = "{\"device_id\":\"" + (deviceId ?? Environment.MachineName) + "\",\"status\":\"" + status + "\",\"message\":\"" + (message ?? "").Replace("\"", "'") + "\"}";
                byte[] bytes = Encoding.UTF8.GetBytes(json);

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(logUrl);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = 8000;
                req.ContentLength = bytes.Length;

                using (Stream stream = req.GetRequestStream())
                {
                    stream.Write(bytes, 0, bytes.Length);
                }

                using (WebResponse resp = req.GetResponse()) { }
            }
            catch { }
        }

        private static string GetMd5Hash(string filePath)
        {
            try
            {
                using (MD5 md5 = MD5.Create())
                {
                    using (FileStream stream = File.OpenRead(filePath))
                    {
                        byte[] hash = md5.ComputeHash(stream);
                        StringBuilder sb = new StringBuilder();
                        for (int i = 0; i < hash.Length; i++)
                        {
                            sb.Append(hash[i].ToString("x2"));
                        }
                        return sb.ToString();
                    }
                }
            }
            catch
            {
                return "";
            }
        }
    }
}
