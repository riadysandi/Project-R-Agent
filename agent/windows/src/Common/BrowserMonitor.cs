using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace ProjectR.Agent.Common
{
    public static class BrowserMonitor
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private static readonly object _queueLock = new object();

        private static readonly Regex BrowserSuffixRegex = new Regex(
            @"\s*[-—–|•]\s*(Google Chrome|Microsoft Edge|Mozilla Firefox|Brave|Opera|Vivaldi)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SearchEngineSuffixRegex = new Regex(
            @"([-—–|•]\s*(Penelusuran Google|Google Search|Google Suche|Google Zoeken|Recherche Google|Buscar con Google|Carian Google|Google 検索|Google Images|Google Gambar|Penelusuran Gambar Google|Google Maps|Google Terjemahan|Google Translate|Google News|Google Berita|Google Cendekia|Google Scholar|Google|Bing Search|Microsoft Bing|Bing Images|Bing Video|Bing|Yahoo Search Results|Yahoo Search|Pencarian Yahoo|Yahoo! Search|Yahoo!|DuckDuckGo Search|DuckDuckGo|Yandex Search|Yandex|Baidu Search|Baidu|Startpage Search|Startpage|Brave Search|Ecosia|Perplexity AI|Perplexity|Qwant|Kagi Search|Kagi|Ask\.com|AOL Search|Search)$)|(\s+at DuckDuckGo$)|(_\u767e\u5ea6\u641c\u7d22$)|(:\s*[\d\s,.]+\s*(hasil ditemukan|results?)\s*(di|on|in)?\s*Yandex$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex NewsOrReferenceRegex = new Regex(
            @"[-—–|•]\s*(Wikipedia(\s+bahasa\s+[^\-—–|•]+)?|Wikidata|Wiktionary|detikcom|detikNews|detikFinance|detikInet|detikHot|detikSport|detikHealth|detikTravel|detikOto|detikFood|detikEdu|detikPop|Kompas\.com|Kompasiana|CNN Indonesia|CNN|Tribunnews|Tribun\s+[^\-—–|•]+|Liputan6(\.com)?|Merdeka\.com|Kumparan|Tempo\.co|Koran Tempo|Antara News|Antara|Sindonews|CNBC Indonesia|CNBC|Tirto\.id|IDN Times|Bisnis\.com|KapanLagi|VIVA\.co\.id|Suara\.com|JawaPos\.com|iNews\.id|BBC News|BBC Indonesia|BBC|The Verge|TechCrunch|Wired|Engadget|CNET|ZDNet|Ars Technica|Reuters|Bloomberg|The New York Times|The Guardian|Forbes|Wall Street Journal|WSJ|Reddit|Quora|Stack Overflow|GitHub|GitLab|Medium|Dev\.to|Substack|Blogspot|WordPress|ChatGPT|Claude|Gemini|Copilot|Microsoft Copilot)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Dictionary<string, string> SupportedBrowsers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "chrome", "Google Chrome" },
            { "msedge", "Microsoft Edge" },
            { "firefox", "Mozilla Firefox" },
            { "brave", "Brave" },
            { "opera", "Opera" },
            { "vivaldi", "Vivaldi" }
        };

        /// <summary>
        /// Lightweight check (sub-millisecond) to verify if any supported web browser process is running.
        /// </summary>
        public static bool HasAnyBrowserRunning()
        {
            foreach (var kvp in SupportedBrowsers)
            {
                try
                {
                    Process[] procs = Process.GetProcessesByName(kvp.Key);
                    if (procs != null && procs.Length > 0)
                    {
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        /// <summary>
        /// High-speed native Win32 foreground window probe (executed inside interactive user desktop session via ProjectRManager).
        /// Takes ~0.001 ms, immune to Session 0 isolation, zero CPU impact.
        /// </summary>
        public static BrowserAlertItem ScanForegroundWindow(List<string> blacklist, string fallbackUser = null)
        {
            if (blacklist == null || blacklist.Count == 0) return null;

            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return null;

                uint pid;
                GetWindowThreadProcessId(fg, out pid);
                if (pid == 0) return null;

                string procName = "";
                try
                {
                    using (Process proc = Process.GetProcessById((int)pid))
                    {
                        procName = proc.ProcessName.ToLowerInvariant();
                    }
                }
                catch { return null; }

                string browserDisplayName = null;
                if (!SupportedBrowsers.TryGetValue(procName, out browserDisplayName))
                {
                    return null;
                }

                StringBuilder sb = new StringBuilder(512);
                int len = GetWindowText(fg, sb, 512);
                if (len <= 0) return null;

                string title = sb.ToString().Trim();
                if (!IsValidWindowTitle(title)) return null;

                string cleanTitle = StripBrowserSuffix(title);
                if (IsSearchEngineTab(cleanTitle)) return null;

                bool isNewsOrRef = IsNewsOrReferencePortal(cleanTitle);

                foreach (string rawKw in blacklist)
                {
                    string kw = (rawKw ?? "").Trim();
                    if (string.IsNullOrEmpty(kw)) continue;

                    if (IsMatchBlacklist(cleanTitle, kw, isNewsOrRef))
                    {
                        return new BrowserAlertItem
                        {
                            keyword = kw,
                            title = title,
                            browser = browserDisplayName,
                            user = !string.IsNullOrEmpty(fallbackUser) ? fallbackUser : Environment.UserName
                        };
                    }
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Atomically enqueues an alert into the shared local IPC file for the background Windows Service to consume.
        /// </summary>
        public static void EnqueueAlert(BrowserAlertItem alert)
        {
            if (alert == null) return;

            try
            {
                string dir = AgentConfig.GetConfigDirectory();
                string queuePath = Path.Combine(dir, "browser_queue.json");

                lock (_queueLock)
                {
                    List<BrowserAlertItem> list = LoadQueue(queuePath);

                    bool exists = false;
                    foreach (var item in list)
                    {
                        if (string.Equals(item.keyword, alert.keyword, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(item.title, alert.title, StringComparison.OrdinalIgnoreCase))
                        {
                            exists = true;
                            break;
                        }
                    }

                    if (!exists)
                    {
                        list.Add(alert);
                        SaveQueue(queuePath, list);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Drains and clears all alerts from the shared local IPC queue file.
        /// </summary>
        public static List<BrowserAlertItem> DrainQueue()
        {
            List<BrowserAlertItem> result = new List<BrowserAlertItem>();

            try
            {
                string dir = AgentConfig.GetConfigDirectory();
                string queuePath = Path.Combine(dir, "browser_queue.json");

                lock (_queueLock)
                {
                    if (File.Exists(queuePath))
                    {
                        result = LoadQueue(queuePath);
                        try { File.Delete(queuePath); } catch { }
                    }
                }
            }
            catch { }

            return result;
        }

        private static List<BrowserAlertItem> LoadQueue(string path)
        {
            List<BrowserAlertItem> list = new List<BrowserAlertItem>();
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path, Encoding.UTF8);
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    BrowserAlertItem[] items = ser.Deserialize<BrowserAlertItem[]>(json);
                    if (items != null)
                    {
                        list.AddRange(items);
                    }
                }
                catch { }
            }
            return list;
        }

        private static void SaveQueue(string path, List<BrowserAlertItem> list)
        {
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                string json = ser.Serialize(list ?? new List<BrowserAlertItem>());
                File.WriteAllText(path, json, Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>
        /// Inspects running browser window titles across all user desktop sessions (via tasklist /v /fo csv)
        /// and evaluates them against the blacklist keywords.
        /// </summary>
        public static List<BrowserAlertItem> ScanActiveBrowserAlerts(List<string> blacklist, string fallbackUser = null)
        {
            List<BrowserAlertItem> results = new List<BrowserAlertItem>();

            if (blacklist == null || blacklist.Count == 0)
            {
                return results;
            }

            // Clean blacklist keywords
            List<string> activeKeywords = new List<string>();
            foreach (string rawKw in blacklist)
            {
                string kw = (rawKw ?? "").Trim();
                if (!string.IsNullOrEmpty(kw))
                {
                    activeKeywords.Add(kw);
                }
            }

            if (activeKeywords.Count == 0)
            {
                return results;
            }

            // Determine which supported browsers are currently running
            List<KeyValuePair<string, string>> runningBrowsers = new List<KeyValuePair<string, string>>();
            foreach (var kvp in SupportedBrowsers)
            {
                try
                {
                    Process[] procs = Process.GetProcessesByName(kvp.Key);
                    if (procs != null && procs.Length > 0)
                    {
                        runningBrowsers.Add(kvp);
                    }
                }
                catch { }
            }

            if (runningBrowsers.Count == 0)
            {
                return results;
            }

            // 1. Primary Probe: Query Terminal Services session titles via tasklist /v /fo csv
            string tasklistPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tasklist.exe");
            if (!File.Exists(tasklistPath))
            {
                tasklistPath = "tasklist.exe";
            }

            foreach (var browser in runningBrowsers)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = tasklistPath,
                        Arguments = "/fi \"imagename eq " + browser.Key + ".exe\" /v /fo csv",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8
                    };

                    using (Process p = Process.Start(psi))
                    {
                        if (p != null)
                        {
                            string output = p.StandardOutput.ReadToEnd();
                            p.WaitForExit(3000);

                            if (!string.IsNullOrEmpty(output))
                            {
                                string[] lines = output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                                for (int i = 0; i < lines.Length; i++)
                                {
                                    if (i == 0 && lines[i].StartsWith("\"Image Name\"", StringComparison.OrdinalIgnoreCase))
                                    {
                                        continue; // Skip CSV header
                                    }

                                    List<string> parts = ParseCsvLine(lines[i]);
                                    if (parts.Count >= 9)
                                    {
                                        string userCol = parts[6].Trim();
                                        string titleCol = parts[8].Trim();

                                        if (IsValidWindowTitle(titleCol))
                                        {
                                            string finalUser = ResolveUser(userCol, fallbackUser);
                                            MatchAndAddAlert(results, activeKeywords, titleCol, browser.Value, finalUser);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }

                // 2. Secondary Fast Path: Process.MainWindowTitle (works when agent runs in user interactive session)
                try
                {
                    Process[] procs = Process.GetProcessesByName(browser.Key);
                    foreach (var proc in procs)
                    {
                        try
                        {
                            string mTitle = proc.MainWindowTitle;
                            if (IsValidWindowTitle(mTitle))
                            {
                                MatchAndAddAlert(results, activeKeywords, mTitle, browser.Value, fallbackUser ?? Environment.UserName);
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            return results;
        }

        private static bool IsValidWindowTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return false;
            string t = title.Trim();
            if (t.Length == 0) return false;

            if (t.Equals("N/A", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("OleMainThreadWndName", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("OLEChannelWnd", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("Default IME", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("MSCTFIME", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Chrome Legacy Window", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("New Tab", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Tab Baru", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static string ResolveUser(string userFromTasklist, string fallbackUser)
        {
            string u = (userFromTasklist ?? "").Trim();
            if (string.IsNullOrEmpty(u) || u.Equals("N/A", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrEmpty(fallbackUser) ? fallbackUser : Environment.UserName;
            }

            // Strip domain prefix e.g. "COMPANY\john" -> "john"
            if (u.Contains("\\"))
            {
                u = u.Substring(u.IndexOf('\\') + 1);
            }

            return u;
        }

        private static void MatchAndAddAlert(List<BrowserAlertItem> results, List<string> keywords, string title, string browserName, string userName)
        {
            string cleanTitle = StripBrowserSuffix(title);
            if (IsSearchEngineTab(cleanTitle)) return;

            bool isNewsOrRef = IsNewsOrReferencePortal(cleanTitle);

            foreach (string rawKw in keywords)
            {
                string kw = (rawKw ?? "").Trim();
                if (string.IsNullOrEmpty(kw)) continue;

                if (IsMatchBlacklist(cleanTitle, kw, isNewsOrRef))
                {
                    // Check if already in list
                    bool exists = false;
                    foreach (var item in results)
                    {
                        if (string.Equals(item.keyword, kw, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(item.title, title, StringComparison.OrdinalIgnoreCase))
                        {
                            exists = true;
                            break;
                        }
                    }

                    if (!exists)
                    {
                        results.Add(new BrowserAlertItem
                        {
                            keyword = kw,
                            title = title,
                            browser = browserName,
                            user = string.IsNullOrEmpty(userName) ? "User" : userName
                        });
                    }
                    break; // Matched first keyword for this title
                }
            }
        }

        public static string StripBrowserSuffix(string title)
        {
            if (string.IsNullOrEmpty(title)) return "";
            return BrowserSuffixRegex.Replace(title.Trim(), "").Trim();
        }

        public static bool IsSearchEngineTab(string pageTitle)
        {
            if (string.IsNullOrEmpty(pageTitle)) return false;
            string t = pageTitle.Trim();

            if (t.Equals("Google", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Google Search", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Penelusuran Google", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Bing", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Microsoft Bing", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("DuckDuckGo", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("DuckDuckGo — Privacy, simplified.", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Yahoo", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Yahoo!", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Baidu", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Yandex", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Ecosia", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Brave Search", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Startpage", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Perplexity", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return SearchEngineSuffixRegex.IsMatch(t);
        }

        public static bool IsNewsOrReferencePortal(string pageTitle)
        {
            if (string.IsNullOrEmpty(pageTitle)) return false;
            return NewsOrReferenceRegex.IsMatch(pageTitle.Trim());
        }

        public static bool IsMatchBlacklist(string cleanTitle, string rawKw, bool isNewsOrRef)
        {
            if (string.IsNullOrEmpty(cleanTitle) || string.IsNullOrEmpty(rawKw)) return false;

            string cleanKw = rawKw.Trim().ToLowerInvariant();
            if (cleanKw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) cleanKw = cleanKw.Substring(7);
            if (cleanKw.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) cleanKw = cleanKw.Substring(8);
            if (cleanKw.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) cleanKw = cleanKw.Substring(4);
            int slashIdx = cleanKw.IndexOf('/');
            if (slashIdx >= 0) cleanKw = cleanKw.Substring(0, slashIdx);

            if (string.IsNullOrEmpty(cleanKw)) return false;

            string brand = cleanKw;
            int dotIdx = cleanKw.IndexOf('.');
            if (dotIdx > 0)
            {
                brand = cleanKw.Substring(0, dotIdx);
            }

            // 1. Explicit domain in title (e.g. "youtube.com", "slot88.com", "tiktok.com")
            if (cleanKw.Contains(".") && cleanTitle.IndexOf(cleanKw, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            // 2. If it's a news/reference portal or discussion forum, do not match general brand anchors
            // unless the blacklist keyword itself targets that portal
            if (isNewsOrRef)
            {
                if (cleanKw.Equals("wikipedia", StringComparison.OrdinalIgnoreCase) ||
                    cleanKw.Equals("detik", StringComparison.OrdinalIgnoreCase) ||
                    cleanKw.Equals("kompas", StringComparison.OrdinalIgnoreCase) ||
                    cleanKw.Equals("reddit", StringComparison.OrdinalIgnoreCase))
                {
                    return cleanTitle.IndexOf(brand, StringComparison.OrdinalIgnoreCase) >= 0;
                }
                return false;
            }

            // 3. Site anchor matching:
            // A. Suffix anchor: e.g. "Taylor Swift - Blank Space - YouTube", "Home | Facebook", "Stranger Things - Netflix"
            string suffixPattern = @"[-—–|•:]\s*" + Regex.Escape(brand) + @"\b";
            if (Regex.IsMatch(cleanTitle, suffixPattern, RegexOptions.IgnoreCase))
            {
                return true;
            }

            // B. Prefix anchor: e.g. "YouTube", "(1) YouTube", "YouTube - Broadcast Yourself", "Facebook - Log In", "TikTok - Make Your Day"
            string prefixPattern = @"^\(?\d*\)?\s*" + Regex.Escape(brand) + @"\b";
            if (Regex.IsMatch(cleanTitle, prefixPattern, RegexOptions.IgnoreCase))
            {
                return true;
            }

            // C. Standalone brand word match (for brand length >= 4):
            // e.g. "YouTube Studio", "Watch YouTube", "Instagram"
            if (brand.Length >= 4)
            {
                string wordPattern = @"\b" + Regex.Escape(brand) + @"\b";
                if (Regex.IsMatch(cleanTitle, wordPattern, RegexOptions.IgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<string> ParseCsvLine(string line)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrEmpty(line)) return result;

            StringBuilder sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++; // Skip escaped double quote
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    result.Add(sb.ToString());
                    sb.Length = 0;
                }
                else
                {
                    sb.Append(c);
                }
            }
            result.Add(sb.ToString());
            return result;
        }
    }
}
