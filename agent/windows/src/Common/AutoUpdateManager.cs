using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ProjectR.Agent.Common
{
    public static class AutoUpdateManager
    {
        private static bool _isUpdating = false;
        private static DateTime _lastAttempt = DateTime.MinValue;

        /// <summary>
        /// Compares two semver-like version strings (e.g., "2.1.5" vs "2.1.4").
        /// Returns true if serverVersion is strictly newer than currentVersion.
        /// </summary>
        public static bool IsVersionNewer(string serverVersion, string currentVersion)
        {
            if (string.IsNullOrEmpty(serverVersion) || string.IsNullOrEmpty(currentVersion))
            {
                return false;
            }

            try
            {
                Version sVer = ParseVersion(serverVersion);
                Version cVer = ParseVersion(currentVersion);
                return sVer > cVer;
            }
            catch
            {
                return !string.Equals(serverVersion.Trim(), currentVersion.Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }

        private static Version ParseVersion(string v)
        {
            string clean = (v ?? "").Trim().TrimStart('v', 'V');
            string[] parts = clean.Split('.');
            if (parts.Length == 1) clean += ".0.0";
            else if (parts.Length == 2) clean += ".0";
            return new Version(clean);
        }

        /// <summary>
        /// Evaluates server version vs client version. If newer, triggers a silent background update task.
        /// Throttled to maximum 1 attempt per 2 minutes to prevent aggressive loops if server has issues.
        /// </summary>
        public static void CheckAndTriggerUpdate(string serverUrl, string serverVersion, string currentVersion, Action<string> logger)
        {
            if (_isUpdating) return;

            if (DateTime.Now < _lastAttempt.AddMinutes(2)) return;

            if (!IsVersionNewer(serverVersion, currentVersion)) return;

            _isUpdating = true;
            _lastAttempt = DateTime.Now;

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (logger != null) logger("[AUTO-UPDATE] Terdeteksi versi baru di server: v" + serverVersion + " (versi lokal saat ini: v" + currentVersion + "). Memulai proses silent self-update...");
                    PerformSilentUpdate(serverUrl, serverVersion, logger);
                }
                catch (Exception ex)
                {
                    if (logger != null) logger("[AUTO-UPDATE ERROR] Gagal melakukan silent self-update: " + ex.Message);
                }
                finally
                {
                    _isUpdating = false;
                }
            });
        }

        /// <summary>
        /// Downloads the latest Inno Setup silent installer, validates SHA-256 integrity against manifest.json,
        /// and launches silent execution (/VERYSILENT /SUPPRESSMSGBOXES /NORESTART).
        /// </summary>
        public static void PerformSilentUpdate(string serverUrl, string targetVersion, Action<string> logger)
        {
            string baseUrl = ApiClient.SanitizeUrl(serverUrl);
            string arch = Environment.Is64BitOperatingSystem ? "x64" : "x86";
            string installerName = "ProjectR-Agent-Setup-" + arch + ".exe";
            string cacheBuster = "v=" + Uri.EscapeDataString(targetVersion) + "&t=" + DateTime.UtcNow.Ticks;
            string downloadUrl = baseUrl + "/downloads/agent/" + installerName + "?" + cacheBuster;
            string manifestUrl = baseUrl + "/downloads/agent/manifest.json?t=" + DateTime.UtcNow.Ticks;

            string updateDir = Path.Combine(AgentConfig.GetConfigDirectory(), "updates");
            if (!Directory.Exists(updateDir))
            {
                Directory.CreateDirectory(updateDir);
            }

            string targetInstallerPath = Path.Combine(updateDir, installerName);

            // Step 1: Download manifest.json for cryptographic SHA-256 hash
            string expectedSha256 = null;
            try
            {
                if (logger != null) logger("[AUTO-UPDATE] Mengunduh manifest rilis: " + manifestUrl);
                using (WebClient wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "ProjectR-Agent-Updater/" + AgentConfig.CurrentVersion;
                    wc.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
                    wc.Headers.Add("Pragma", "no-cache");
                    string manifestJson = wc.DownloadString(manifestUrl);
                    expectedSha256 = ExtractSha256FromManifest(manifestJson, installerName);
                }
            }
            catch (Exception ex)
            {
                if (logger != null) logger("[AUTO-UPDATE WARNING] Gagal mengunduh manifest.json: " + ex.Message);
            }

            // Step 2: Download installer binary into temporary staging file
            if (logger != null) logger("[AUTO-UPDATE] Mengunduh paket installer (" + arch + ") dari: " + downloadUrl);
            string tempDownloadPath = targetInstallerPath + ".tmp";
            if (File.Exists(tempDownloadPath))
            {
                try { File.Delete(tempDownloadPath); } catch { }
            }

            using (WebClient wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "ProjectR-Agent-Updater/" + AgentConfig.CurrentVersion;
                wc.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
                wc.Headers.Add("Pragma", "no-cache");
                wc.DownloadFile(downloadUrl, tempDownloadPath);
            }

            // Step 3: Validate integrity and size
            FileInfo fi = new FileInfo(tempDownloadPath);
            if (!fi.Exists || fi.Length < 1000000) // Must be valid installer at least ~1MB
            {
                throw new Exception("File installer yang diunduh tidak valid atau ukuran terlalu kecil (" + (fi.Exists ? fi.Length : 0) + " bytes).");
            }

            if (!string.IsNullOrEmpty(expectedSha256))
            {
                string actualSha256 = ComputeSha256(tempDownloadPath);
                if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(tempDownloadPath); } catch { }
                    throw new Exception("Integritas biner gagal: Checksum SHA-256 tidak cocok! Diharapkan: " + expectedSha256 + ", Diperoleh: " + actualSha256);
                }
                if (logger != null) logger("[AUTO-UPDATE] Verifikasi SHA-256 VALID (" + actualSha256.Substring(0, 12) + "...). Integritas terjamin.");
            }

            // Replace existing installer in updates folder
            if (File.Exists(targetInstallerPath))
            {
                try { File.Delete(targetInstallerPath); } catch { }
            }
            File.Move(tempDownloadPath, targetInstallerPath);

            if (logger != null) logger("[AUTO-UPDATE] Meluncurkan silent upgrade: " + targetInstallerPath + " /VERYSILENT /SUPPRESSMSGBOXES /NORESTART");

            // Step 4: Launch Inno Setup installer silently
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = targetInstallerPath,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Process proc = Process.Start(psi);
            if (proc != null)
            {
                if (logger != null) logger("[AUTO-UPDATE] Proses upgrade berjalan di latar belakang (PID: " + proc.Id + "). Service akan otomatis diperbarui ke v" + targetVersion + ".");
            }
        }

        private static string ComputeSha256(string filePath)
        {
            using (SHA256 sha = SHA256.Create())
            {
                using (FileStream stream = File.OpenRead(filePath))
                {
                    byte[] hash = sha.ComputeHash(stream);
                    StringBuilder sb = new StringBuilder(hash.Length * 2);
                    for (int i = 0; i < hash.Length; i++)
                    {
                        sb.Append(hash[i].ToString("x2"));
                    }
                    return sb.ToString();
                }
            }
        }

        private static string ExtractSha256FromManifest(string json, string fileName)
        {
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> dict = ser.Deserialize<Dictionary<string, object>>(json);
                if (dict != null && dict.ContainsKey("files"))
                {
                    IEnumerable files = dict["files"] as IEnumerable;
                    if (files != null)
                    {
                        foreach (object f in files)
                        {
                            Dictionary<string, object> fileDict = f as Dictionary<string, object>;
                            if (fileDict != null && fileDict.ContainsKey("name") && fileDict.ContainsKey("sha256"))
                            {
                                string name = fileDict["name"] != null ? fileDict["name"].ToString() : "";
                                if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                                {
                                    return fileDict["sha256"] != null ? fileDict["sha256"].ToString() : null;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
