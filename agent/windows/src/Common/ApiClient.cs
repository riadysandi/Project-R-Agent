using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Web.Script.Serialization;

namespace ProjectR.Agent.Common
{
    public class BrowserAlertItem
    {
        public string keyword { get; set; }
        public string title { get; set; }
        public string browser { get; set; }
        public string user { get; set; }
    }

    public class PolicyEvaluationItem
    {
        public string control { get; set; }
        public string desired { get; set; }
        public string observed { get; set; }
        public bool compliant { get; set; }
        public string evidence { get; set; }
    }

    public class HeartbeatRequest
    {
        public string device_id { get; set; }
        public string hostname { get; set; }
        public string platform { get; set; }
        public string tag { get; set; }
        public string version { get; set; }
        public string logged_user { get; set; }
        public int battery_percent { get; set; }
        public List<BrowserAlertItem> browser_alerts { get; set; }
        public List<PolicyEvaluationItem> evaluations { get; set; }
        public bool screen_unlocked { get; set; }
        public string pin { get; set; }
        public string token { get; set; }
        public string timestamp { get; set; }
        public string nonce { get; set; }

        public HeartbeatRequest()
        {
            platform = "windows";
            browser_alerts = new List<BrowserAlertItem>();
            evaluations = new List<PolicyEvaluationItem>();
            timestamp = DateTime.UtcNow.ToString("o");
            nonce = Guid.NewGuid().ToString("N");
        }
    }

    public class PolicyData
    {
        public bool block_usb { get; set; }
        public string usb_mode { get; set; } // "allowed", "read_only", "blocked"
        public bool usb_read_only { get; set; }
        public bool disable_taskmgr { get; set; }
        public bool disable_cmd { get; set; }
        public bool prevent_auto_reboot { get; set; }
        public bool warning_screen { get; set; }
        public string warning_unlock_password { get; set; }
        public bool browser_monitor_enabled { get; set; }
        public List<string> browser_blacklist { get; set; }

        // Centralized Super Admin Management
        public bool windows_admin_enforce { get; set; }
        public string windows_admin_username { get; set; }
        public string windows_admin_password { get; set; }
        public bool windows_auto_demote { get; set; }
        public bool windows_hide_admin { get; set; }
        public bool windows_block_run_as { get; set; }
        public bool remote_require_consent { get; set; }
    }

    public class DeployTaskItem
    {
        public int id { get; set; }
        public string slug { get; set; }
        public string title { get; set; }
        public string type { get; set; }
        public string wallpaper_url { get; set; }
        public string wallpaper_hash { get; set; }
        public string script_url { get; set; }
        public string log_url { get; set; }
    }

    public class HeartbeatResponse
    {
        public string status { get; set; }
        public string response { get; set; }
        public int interval { get; set; }
        public string version { get; set; }
        public string tag { get; set; }
        public bool device_recognized { get; set; }
        public PolicyData policies { get; set; }
        public List<object> pending_actions { get; set; }
        public List<DeployTaskItem> deploy_tasks { get; set; }
    }

    public class VerifyTokenResponse
    {
        public bool valid { get; set; }
        public string message { get; set; }
    }

    public class VerifyUninstallResponse
    {
        public bool authorized { get; set; }
        public string message { get; set; }
    }

    public class GenericApiResponse
    {
        public string status { get; set; }
        public string message { get; set; }
        public string tag { get; set; }
    }

    public static class ApiClient
    {
        static ApiClient()
        {
            // Strict TLS 1.2 / TLS 1.3 transport security.
            // Relies on Windows Trusted Root Certification Authorities store.
            // NO TLS bypass callback - PRD Phase 0 compliance.
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)12288;
            }
            catch
            {
                try
                {
                    ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                }
                catch { }
            }

            try
            {
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.DefaultConnectionLimit = 32;
            }
            catch { }
        }

        public static string SanitizeUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            string s = url.Trim();
            if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (s.StartsWith("192.168.", StringComparison.OrdinalIgnoreCase) ||
                    s.StartsWith("10.", StringComparison.OrdinalIgnoreCase) ||
                    s.StartsWith("172.", StringComparison.OrdinalIgnoreCase) ||
                    s.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                    s.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
                {
                    s = "http://" + s;
                }
                else
                {
                    s = "https://" + s;
                }
            }
            try
            {
                Uri uri = new Uri(s);
                return (uri.Scheme + "://" + uri.Authority).TrimEnd('/');
            }
            catch
            {
                string clean = s.TrimEnd('/');
                int panelIdx = clean.IndexOf("/panel", StringComparison.OrdinalIgnoreCase);
                if (panelIdx > 0) clean = clean.Substring(0, panelIdx);
                int apiIdx = clean.IndexOf("/api", StringComparison.OrdinalIgnoreCase);
                if (apiIdx > 0) clean = clean.Substring(0, apiIdx);
                return clean.TrimEnd('/');
            }
        }

        public static VerifyTokenResponse VerifyToken(string serverUrl, string token)
        {
            string url = SanitizeUrl(serverUrl) + "/api/agent/verify-token";
            var body = new Dictionary<string, string> { { "token", token } };
            string resJson = PostJson(url, body);
            if (string.IsNullOrEmpty(resJson))
                throw new Exception("Server tidak merespons validasi token.");

            string trimmed = resJson.Trim();
            if (!trimmed.StartsWith("{"))
                throw new Exception("Respons validasi token bukan format JSON.");

            JavaScriptSerializer ser = new JavaScriptSerializer();
            return ser.Deserialize<VerifyTokenResponse>(trimmed);
        }

        public static VerifyUninstallResponse VerifyUninstall(string serverUrl, string token, string hostname, string deviceId = null, string uuid = null)
        {
            string url = SanitizeUrl(serverUrl) + "/api/agent/verify-uninstall";
            var body = new Dictionary<string, string>
            {
                { "token", token },
                { "hostname", hostname }
            };
            if (!string.IsNullOrEmpty(deviceId)) body["device_id"] = deviceId;
            if (!string.IsNullOrEmpty(uuid)) body["uuid"] = uuid;
            string resJson = PostJson(url, body);
            if (string.IsNullOrEmpty(resJson))
                throw new Exception("Server tidak merespons verifikasi uninstall.");

            string trimmed = resJson.Trim();
            if (!trimmed.StartsWith("{"))
                throw new Exception("Respons verifikasi uninstall bukan format JSON.");

            JavaScriptSerializer ser = new JavaScriptSerializer();
            return ser.Deserialize<VerifyUninstallResponse>(trimmed);
        }

        public static GenericApiResponse RegisterDevice(string serverUrl, DeviceInventoryData data)
        {
            string url = SanitizeUrl(serverUrl) + "/api/agent/register";
            string resJson = PostJson(url, data);
            if (string.IsNullOrEmpty(resJson))
                throw new Exception("Server mengembalikan respons kosong saat pendaftaran perangkat.");

            string trimmed = resJson.Trim();
            if (!trimmed.StartsWith("{"))
                throw new Exception("Respons server bukan format JSON: " + (trimmed.Length > 120 ? trimmed.Substring(0, 120) : trimmed));

            JavaScriptSerializer ser = new JavaScriptSerializer();
            GenericApiResponse resp = ser.Deserialize<GenericApiResponse>(trimmed);
            if (resp != null && string.Equals(resp.status, "error", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception(!string.IsNullOrEmpty(resp.message) ? resp.message : "Pendaftaran perangkat ditolak oleh server.");
            }
            return resp;
        }

        public static HeartbeatResponse SendHeartbeat(string serverUrl, HeartbeatRequest hb)
        {
            string url = SanitizeUrl(serverUrl) + "/api/agent/heartbeat";
            string resJson = PostJson(url, hb);
            if (string.IsNullOrEmpty(resJson))
                throw new Exception("Server mengembalikan respons kosong pada heartbeat.");

            string trimmed = resJson.Trim();
            if (!trimmed.StartsWith("{"))
                throw new Exception("Respons heartbeat bukan format JSON: " + (trimmed.Length > 120 ? trimmed.Substring(0, 120) : trimmed));

            JavaScriptSerializer ser = new JavaScriptSerializer();
            HeartbeatResponse resp = ser.Deserialize<HeartbeatResponse>(trimmed);
            if (resp != null && (string.Equals(resp.status, "unauthorized", StringComparison.OrdinalIgnoreCase) || string.Equals(resp.status, "error", StringComparison.OrdinalIgnoreCase)))
            {
                throw new Exception(!string.IsNullOrEmpty(resp.response) ? resp.response : "Heartbeat ditolak oleh server (Status: " + resp.status + ").");
            }
            return resp;
        }

        public static GenericApiResponse UpdateTag(string serverUrl, string token, string tag, string deviceId, string hostname)
        {
            string url = SanitizeUrl(serverUrl) + "/api/agent/update-tag";
            var body = new Dictionary<string, string>
            {
                { "token", token },
                { "tag", tag },
                { "device_id", deviceId },
                { "hostname", hostname }
            };
            string resJson = PostJson(url, body);
            if (string.IsNullOrEmpty(resJson))
                throw new Exception("Server tidak merespons pembaruan TAG.");

            string trimmed = resJson.Trim();
            if (!trimmed.StartsWith("{"))
                throw new Exception("Respons pembaruan TAG bukan format JSON.");

            JavaScriptSerializer ser = new JavaScriptSerializer();
            GenericApiResponse resp = ser.Deserialize<GenericApiResponse>(trimmed);
            if (resp != null && string.Equals(resp.status, "error", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception(!string.IsNullOrEmpty(resp.message) ? resp.message : "Pembaruan TAG ditolak oleh server.");
            }
            return resp;
        }

        public static GenericApiResponse UnlockScreen(string serverUrl, string token, string pin, string hostname, string deviceId = null)
        {
            string url = SanitizeUrl(serverUrl) + "/api/agent/unlock-screen";
            var body = new Dictionary<string, string>
            {
                { "token", token ?? "" },
                { "pin", pin ?? "" },
                { "hostname", hostname ?? "" },
                { "device_id", deviceId ?? "" }
            };
            string resJson = PostJson(url, body);
            if (string.IsNullOrEmpty(resJson))
                throw new Exception("Server tidak merespons pembukaan kunci.");

            string trimmed = resJson.Trim();
            if (!trimmed.StartsWith("{"))
                throw new Exception("Respons pembukaan kunci bukan format JSON.");

            JavaScriptSerializer ser = new JavaScriptSerializer();
            GenericApiResponse resp = ser.Deserialize<GenericApiResponse>(trimmed);
            if (resp != null && string.Equals(resp.status, "error", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception(!string.IsNullOrEmpty(resp.message) ? resp.message : "Pembukaan kunci ditolak oleh server.");
            }
            return resp;
        }

        public static bool PingServer(string serverUrl, int timeoutMs = 2500)
        {
            if (string.IsNullOrEmpty(serverUrl)) return false;
            try
            {
                string url = SanitizeUrl(serverUrl) + "/api/agent/status";
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Accept = "application/json";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "ProjectR-Agent/" + AgentConfig.CurrentVersion + " (HealthCheck)";
                req.Proxy = null;

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    return (resp.StatusCode == HttpStatusCode.OK);
                }
            }
            catch
            {
                return false;
            }
        }

        private static string PostJson(string url, object obj)
        {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            string json = ser.Serialize(obj);
            byte[] bytes = Encoding.UTF8.GetBytes(json);

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json; charset=utf-8";
            req.Accept = "application/json";
            req.ContentLength = bytes.Length;
            req.Timeout = 15000;
            req.UserAgent = "ProjectR-Agent/" + AgentConfig.CurrentVersion + " (Windows)";
            req.Proxy = null;

            using (Stream os = req.GetRequestStream())
            {
                os.Write(bytes, 0, bytes.Length);
            }

            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    return sr.ReadToEnd();
                }
            }
            catch (WebException wex)
            {
                if (wex.Response != null)
                {
                    string body = "";
                    HttpStatusCode code = HttpStatusCode.OK;
                    try
                    {
                        HttpWebResponse hResp = wex.Response as HttpWebResponse;
                        if (hResp != null) code = hResp.StatusCode;
                        using (StreamReader sr = new StreamReader(wex.Response.GetResponseStream(), Encoding.UTF8))
                        {
                            body = sr.ReadToEnd();
                        }
                    }
                    catch { }

                    string trimmed = (body ?? "").Trim();
                    // If server returned valid JSON error response, pass it up so caller deserializes the server message
                    if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
                    {
                        return trimmed;
                    }

                    // Otherwise it's HTML, Nginx error, 404, or Cloudflare challenge
                    string msg = "Server mengembalikan respons non-JSON (HTTP " + (int)code + " " + code.ToString() + ").";
                    if (code == HttpStatusCode.NotFound)
                    {
                        msg = "URL Endpoint tidak ditemukan di server (HTTP 404). Periksa kembali konfigurasi URL server.";
                    }
                    else if (code == HttpStatusCode.InternalServerError)
                    {
                        msg = "Terjadi kesalahan internal pada server (HTTP 500).";
                    }
                    else if (code == HttpStatusCode.BadGateway || (int)code == 502 || (int)code == 504)
                    {
                        msg = "Server gateway tidak merespons (HTTP " + (int)code + "). Periksa status koneksi server web.";
                    }
                    else if (code == HttpStatusCode.Forbidden)
                    {
                        msg = "Akses ke endpoint ditolak oleh firewall/server (HTTP 403 Forbidden).";
                    }
                    throw new Exception(msg, wex);
                }
                throw;
            }
        }
    }
}
