using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ProjectR.Agent.Common
{
    public class BiosInfo
    {
        public string bversion { get; set; }
        public string bdate { get; set; }
        public string manufacturer { get; set; }
        public string model { get; set; }
        public string serial { get; set; }
        public string asset_tag { get; set; }

        public BiosInfo()
        {
            bversion = "";
            bdate = "";
            manufacturer = "";
            model = "";
            serial = "";
            asset_tag = "";
        }
    }

    public class MonitorItem
    {
        public string caption { get; set; }
        public string manufacturer { get; set; }
        public string model { get; set; }
        public string serial { get; set; }
        public string description { get; set; }
    }

    public class AntivirusItem
    {
        public string name { get; set; }
        public string version { get; set; }
        public string publisher { get; set; }
        public string status { get; set; }
        public string source { get; set; }
    }

    public class SoftwareItem
    {
        public string name { get; set; }
        public string version { get; set; }
        public string publisher { get; set; }
        public string install_date { get; set; }
    }

    public class DiskItem
    {
        public string name { get; set; }
        public string model { get; set; }
        public string serial_number { get; set; }
        public string type { get; set; }
        public int size_mb { get; set; }
    }

    public class NetworkItem
    {
        public string name { get; set; }
        public string mac { get; set; }
        public string ip_address { get; set; }
        public bool active { get; set; }
    }

    public class BatteryInfo
    {
        public bool present { get; set; }
        public int percent { get; set; }
        public bool charging { get; set; }
        public string status { get; set; }
        public string name { get; set; }
        public string manufacturer { get; set; }
        public string serial { get; set; }
        public string chemistry { get; set; }

        public BatteryInfo()
        {
            status = "AC";
            name = "";
            manufacturer = "";
            serial = "";
            chemistry = "Lithium-ion";
        }
    }

    public class DeviceInventoryData
    {
        public string token { get; set; }
        public string platform { get; set; }
        public string device_id { get; set; }
        public string hostname { get; set; }
        public string tag { get; set; }
        public string uuid { get; set; }
        public string serial_number { get; set; }
        public string manufacturer { get; set; }
        public string model { get; set; }
        public string chassis_type { get; set; }
        public string chassis_name { get; set; }
        public string operating_system { get; set; }
        public string os_version { get; set; }
        public string cpu_name { get; set; }
        public int memory_mb { get; set; }
        public string last_user { get; set; }
        public string primary_ip { get; set; }
        public string primary_mac { get; set; }
        public string default_gateway { get; set; }
        public string agent_version { get; set; }
        public string bitlocker_key { get; set; }
        public BiosInfo bios { get; set; }
        public BatteryInfo battery { get; set; }
        public List<DiskItem> disks { get; set; }
        public List<NetworkItem> networks { get; set; }
        public List<MonitorItem> monitors { get; set; }
        public List<AntivirusItem> antivirus { get; set; }
        public List<SoftwareItem> softwares { get; set; }
        public string timestamp { get; set; }
        public string nonce { get; set; }

        public DeviceInventoryData()
        {
            platform = "windows";
            timestamp = DateTime.UtcNow.ToString("o");
            nonce = Guid.NewGuid().ToString("N");
            bios = new BiosInfo();
            battery = new BatteryInfo();
            disks = new List<DiskItem>();
            networks = new List<NetworkItem>();
            monitors = new List<MonitorItem>();
            antivirus = new List<AntivirusItem>();
            softwares = new List<SoftwareItem>();
        }
    }

    public static class SystemInfo
    {
        public static DeviceInventoryData Collect(string token, string tag, string version)
        {
            DeviceInventoryData data = new DeviceInventoryData();
            data.token = token;
            data.tag = tag;
            data.agent_version = version;
            data.hostname = Environment.MachineName;

            // 1. BIOS & System Enclosure
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Manufacturer, SMBIOSBIOSVersion, Version, ReleaseDate, SerialNumber FROM Win32_BIOS"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        data.serial_number = CleanString(o["SerialNumber"]);
                        data.bios.serial = data.serial_number;
                        data.bios.manufacturer = CleanString(o["Manufacturer"]);
                        data.bios.bversion = CleanString(o["SMBIOSBIOSVersion"]);
                        if (string.IsNullOrEmpty(data.bios.bversion))
                        {
                            data.bios.bversion = CleanString(o["Version"]);
                        }
                        data.bios.bdate = FormatWmiDate(CleanString(o["ReleaseDate"]));
                        break;
                    }
                }
            }
            catch { }

            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT UUID, IdentifyingNumber FROM Win32_ComputerSystemProduct"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        data.uuid = CleanString(o["UUID"]);
                        if (string.IsNullOrEmpty(data.serial_number))
                        {
                            data.serial_number = CleanString(o["IdentifyingNumber"]);
                            data.bios.serial = data.serial_number;
                        }
                        break;
                    }
                }
            }
            catch { }

            // Device ID resolution
            if (!string.IsNullOrEmpty(data.uuid) && !data.uuid.StartsWith("00000000") && !data.uuid.StartsWith("FFFFFFFF"))
            {
                data.device_id = data.uuid;
            }
            else if (!string.IsNullOrEmpty(data.serial_number) && data.serial_number.ToLower() != "none" && data.serial_number.ToLower() != "null")
            {
                data.device_id = data.serial_number;
            }
            else
            {
                data.device_id = data.hostname;
            }

            // 2. Computer System (Manufacturer, Model, Total RAM)
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        data.manufacturer = CleanString(o["Manufacturer"]);
                        data.model = CleanString(o["Model"]);
                        data.bios.model = data.model;
                        if (string.IsNullOrEmpty(data.bios.manufacturer))
                        {
                            data.bios.manufacturer = data.manufacturer;
                        }
                        ulong totalBytes;
                        if (ulong.TryParse(CleanString(o["TotalPhysicalMemory"]), out totalBytes))
                        {
                            data.memory_mb = (int)(totalBytes / (1024 * 1024));
                        }
                        break;
                    }
                }
            }
            catch { }

            // 3. Operating System (Caption, Version)
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Caption, Version FROM Win32_OperatingSystem"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        data.operating_system = CleanString(o["Caption"]);
                        data.os_version = CleanString(o["Version"]);
                        break;
                    }
                }
            }
            catch { }

            // 4. Processor (CPU Name)
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        data.cpu_name = CleanString(o["Name"]);
                        break;
                    }
                }
            }
            catch { }

            // 5. Chassis Type & Asset Tag
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT ChassisTypes, SMBIOSAssetTag FROM Win32_SystemEnclosure"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        ushort[] types = (ushort[])o["ChassisTypes"];
                        if (types != null && types.Length > 0)
                        {
                            data.chassis_type = types[0].ToString();
                            data.chassis_name = GetChassisName(data.chassis_type);
                        }
                        data.bios.asset_tag = CleanString(o["SMBIOSAssetTag"]);
                        break;
                    }
                }
            }
            catch { }

            if (string.IsNullOrEmpty(data.chassis_name))
            {
                data.chassis_name = "Desktop";
            }

            // 6. Network Adapters & Primary IP/MAC
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Description, MACAddress, IPAddress, DefaultIPGateway FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        string desc = CleanString(o["Description"]);
                        string mac = CleanString(o["MACAddress"]);
                        string[] ips = (string[])o["IPAddress"];
                        string ip = (ips != null && ips.Length > 0) ? ips[0] : "";
                        string[] gw = (string[])o["DefaultIPGateway"];

                        NetworkItem net = new NetworkItem
                        {
                            name = desc,
                            mac = mac,
                            ip_address = ip,
                            active = true
                        };
                        data.networks.Add(net);

                        if (gw != null && gw.Length > 0 && string.IsNullOrEmpty(data.primary_ip))
                        {
                            data.primary_ip = ip;
                            data.primary_mac = mac;
                            data.default_gateway = gw[0];
                        }
                    }
                }
            }
            catch { }

            if (string.IsNullOrEmpty(data.primary_ip) && data.networks.Count > 0)
            {
                data.primary_ip = data.networks[0].ip_address;
                data.primary_mac = data.networks[0].mac;
            }

            // 7. Disks List
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Caption, Model, SerialNumber, MediaType, Size FROM Win32_DiskDrive"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        ulong sizeBytes;
                        ulong.TryParse(CleanString(o["Size"]), out sizeBytes);

                        DiskItem disk = new DiskItem
                        {
                            name = CleanString(o["Caption"]),
                            model = CleanString(o["Model"]),
                            serial_number = CleanString(o["SerialNumber"]),
                            type = CleanString(o["MediaType"]),
                            size_mb = (int)(sizeBytes / (1024 * 1024))
                        };
                        data.disks.Add(disk);
                    }
                }
            }
            catch { }

            // 8. Battery Information
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Name, DeviceID, EstimatedChargeRemaining, BatteryStatus, Chemistry FROM Win32_Battery"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        data.battery.present = true;
                        data.battery.name = CleanString(o["Name"]);
                        data.battery.serial = CleanString(o["DeviceID"]);
                        data.battery.manufacturer = data.manufacturer;

                        ushort charge;
                        if (ushort.TryParse(CleanString(o["EstimatedChargeRemaining"]), out charge))
                        {
                            data.battery.percent = (int)charge;
                        }
                        ushort status;
                        if (ushort.TryParse(CleanString(o["BatteryStatus"]), out status))
                        {
                            data.battery.charging = (status == 2);
                            data.battery.status = (status == 2) ? "Charging" : ((status == 1) ? "Discharging" : "AC / Idle");
                        }
                        ushort chem;
                        if (ushort.TryParse(CleanString(o["Chemistry"]), out chem))
                        {
                            data.battery.chemistry = (chem == 2) ? "Lithium-ion" : "Li-Polymer";
                        }
                        break;
                    }
                }
            }
            catch { }

            // 9. Monitors
            data.monitors = GetMonitors();

            // 10. Antivirus
            data.antivirus = GetAntivirus();

            // 11. Installed Applications (Softwares)
            data.softwares = GetInstalledSoftwares();

            // 12. Logged-in User
            data.last_user = GetLoggedInUser();

            // 13. BitLocker Recovery Key (if permitted/encrypted)
            data.bitlocker_key = GetBitLockerRecoveryKey();

            return data;
        }

        public static List<MonitorItem> GetMonitors()
        {
            List<MonitorItem> list = new List<MonitorItem>();
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(@"root\wmi", "SELECT ManufacturerName, UserFriendlyName, SerialNumberID, YearOfManufacture FROM WmiMonitorID"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        string mfg = DecodeUshortArray(o["ManufacturerName"]);
                        string model = DecodeUshortArray(o["UserFriendlyName"]);
                        string serial = DecodeUshortArray(o["SerialNumberID"]);
                        string year = CleanString(o["YearOfManufacture"]);

                        if (!string.IsNullOrEmpty(model) || !string.IsNullOrEmpty(mfg))
                        {
                            MonitorItem m = new MonitorItem();
                            m.manufacturer = mfg;
                            m.model = model;
                            m.serial = serial;
                            m.caption = string.IsNullOrEmpty(mfg) ? model : (mfg + " " + model).Trim();
                            m.description = "Display " + (string.IsNullOrEmpty(year) ? "" : "(" + year + ")");
                            list.Add(m);
                        }
                    }
                }
            }
            catch { }

            if (list.Count == 0)
            {
                try
                {
                    using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Caption, MonitorManufacturer, MonitorType, Description FROM Win32_DesktopMonitor"))
                    {
                        foreach (ManagementObject o in s.Get())
                        {
                            string caption = CleanString(o["Caption"]);
                            if (!string.IsNullOrEmpty(caption))
                            {
                                MonitorItem m = new MonitorItem();
                                m.caption = caption;
                                m.manufacturer = CleanString(o["MonitorManufacturer"]);
                                m.model = CleanString(o["MonitorType"]);
                                m.serial = "-";
                                m.description = CleanString(o["Description"]);
                                list.Add(m);
                            }
                        }
                    }
                }
                catch { }
            }

            return list;
        }

        public static List<AntivirusItem> GetAntivirus()
        {
            List<AntivirusItem> list = new List<AntivirusItem>();
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName, pathToSignedProductExe, productState FROM AntiVirusProduct"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        string name = CleanString(o["displayName"]);
                        if (string.IsNullOrEmpty(name)) continue;

                        string exePath = CleanString(o["pathToSignedProductExe"]);
                        string stateStr = CleanString(o["productState"]);
                        int state = 0;
                        int.TryParse(stateStr, out state);

                        bool isActive = (state & 0x1000) != 0 || (state & 0x10000) != 0 || (state & 0x40000) != 0;
                        string status = isActive ? "Aktif (Terlindungi)" : "Nonaktif";

                        string publisher = ResolveAvPublisher(name, exePath);
                        string version = "-";
                        try
                        {
                            string cleanPath = exePath.Replace(@"\\?\", "");
                            if (!string.IsNullOrEmpty(cleanPath) && File.Exists(cleanPath))
                            {
                                FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(cleanPath);
                                if (!string.IsNullOrEmpty(fvi.FileVersion)) version = fvi.FileVersion;
                                if (string.IsNullOrEmpty(publisher) && !string.IsNullOrEmpty(fvi.CompanyName)) publisher = fvi.CompanyName;
                            }
                        }
                        catch { }

                        AntivirusItem av = new AntivirusItem();
                        av.name = name;
                        av.version = version;
                        av.publisher = publisher;
                        av.status = status;
                        av.source = "Windows Security Center";
                        list.Add(av);
                    }
                }
            }
            catch { }
            return list;
        }

        private static string ResolveAvPublisher(string name, string exePath)
        {
            string lower = name.ToLower();
            if (lower.Contains("defender")) return "Microsoft Corporation";
            if (lower.Contains("mcafee")) return "McAfee, LLC";
            if (lower.Contains("crowdstrike")) return "CrowdStrike, Inc.";
            if (lower.Contains("kaspersky")) return "Kaspersky Lab";
            if (lower.Contains("symantec") || lower.Contains("norton")) return "Symantec Corporation";
            if (lower.Contains("sophos")) return "Sophos Ltd.";
            if (lower.Contains("eset")) return "ESET, spol. s r.o.";
            if (lower.Contains("bitdefender")) return "Bitdefender";
            if (lower.Contains("sentinelone")) return "SentinelOne";
            if (lower.Contains("trend micro")) return "Trend Micro Inc.";
            if (lower.Contains("avast") || lower.Contains("avg")) return "Gen Digital Inc.";
            if (lower.Contains("malwarebytes")) return "Malwarebytes Inc.";
            return "Security Vendor";
        }

        public static List<SoftwareItem> GetInstalledSoftwares()
        {
            List<SoftwareItem> list = new List<SoftwareItem>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            ScanRegistryUninstall(RegistryHive.LocalMachine, RegistryView.Registry64, list, seen);
            ScanRegistryUninstall(RegistryHive.LocalMachine, RegistryView.Registry32, list, seen);
            ScanRegistryUninstall(RegistryHive.CurrentUser, RegistryView.Default, list, seen);

            list.Sort(delegate(SoftwareItem a, SoftwareItem b)
            {
                return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
            });

            return list;
        }

        private static void ScanRegistryUninstall(RegistryHive hive, RegistryView view, List<SoftwareItem> list, Dictionary<string, bool> seen)
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey subKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (subKey == null) return;
                    foreach (string subName in subKey.GetSubKeyNames())
                    {
                        try
                        {
                            using (RegistryKey appKey = subKey.OpenSubKey(subName))
                            {
                                if (appKey == null) continue;

                                object sysVal = appKey.GetValue("SystemComponent");
                                if (sysVal != null && sysVal.ToString() == "1") continue;

                                object parentVal = appKey.GetValue("ParentKeyName");
                                if (parentVal != null && !string.IsNullOrEmpty(parentVal.ToString().Trim())) continue;

                                object nameVal = appKey.GetValue("DisplayName");
                                if (nameVal == null || string.IsNullOrEmpty(nameVal.ToString().Trim())) continue;

                                string name = nameVal.ToString().Trim();
                                object verVal = appKey.GetValue("DisplayVersion");
                                string ver = verVal != null ? verVal.ToString().Trim() : "";

                                string key = name + "|" + ver;
                                if (seen.ContainsKey(key)) continue;
                                seen[key] = true;

                                object pubVal = appKey.GetValue("Publisher");
                                string pub = pubVal != null ? pubVal.ToString().Trim() : "";

                                object dateVal = appKey.GetValue("InstallDate");
                                string dateStr = dateVal != null ? dateVal.ToString().Trim() : "";
                                string formattedDate = "";
                                if (dateStr.Length == 8 && Regex.IsMatch(dateStr, @"^\d{8}$"))
                                {
                                    formattedDate = dateStr.Substring(0, 4) + "-" + dateStr.Substring(4, 2) + "-" + dateStr.Substring(6, 2);
                                }
                                else if (!string.IsNullOrEmpty(dateStr))
                                {
                                    DateTime dt;
                                    if (DateTime.TryParse(dateStr, out dt))
                                    {
                                        formattedDate = dt.ToString("yyyy-MM-dd");
                                    }
                                }

                                SoftwareItem item = new SoftwareItem();
                                item.name = name;
                                item.version = ver;
                                item.publisher = pub;
                                item.install_date = formattedDate;
                                list.Add(item);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static string GetChassisName(string typeStr)
        {
            int t;
            if (!int.TryParse(typeStr, out t)) return "Desktop";
            switch (t)
            {
                case 8:
                case 9:
                case 10:
                case 14:
                    return "Notebook";
                case 30:
                case 31:
                case 32:
                    return "Tablet";
                case 17:
                case 23:
                    return "Server";
                case 3:
                case 4:
                case 5:
                case 6:
                case 7:
                case 13:
                case 15:
                case 16:
                case 24:
                    return "Desktop";
                default:
                    return "PC";
            }
        }

        private static string FormatWmiDate(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            if (raw.Length >= 8 && Regex.IsMatch(raw.Substring(0, 8), @"^\d{8}$"))
            {
                return raw.Substring(0, 4) + "-" + raw.Substring(4, 2) + "-" + raw.Substring(6, 2);
            }
            DateTime dt;
            if (DateTime.TryParse(raw, out dt))
            {
                return dt.ToString("yyyy-MM-dd");
            }
            return raw;
        }

        private static string DecodeUshortArray(object val)
        {
            if (val == null) return "";
            ushort[] arr = val as ushort[];
            if (arr == null) return "";
            StringBuilder sb = new StringBuilder();
            foreach (ushort u in arr)
            {
                if (u == 0) break;
                sb.Append((char)u);
            }
            return sb.ToString().Trim();
        }

        public static string GetLoggedInUser()
        {
            try
            {
                Process[] procs = Process.GetProcessesByName("explorer");
                if (procs.Length > 0)
                {
                    using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT * FROM Win32_Process WHERE ProcessId = " + procs[0].Id))
                    {
                        foreach (ManagementObject o in s.Get())
                        {
                            object[] outParams = new object[2];
                            uint res = (uint)o.InvokeMethod("GetOwner", outParams);
                            if (res == 0 && outParams[0] != null)
                            {
                                string user = outParams[0].ToString();
                                string domain = outParams[1] != null ? outParams[1].ToString() : "";
                                return string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
                            }
                        }
                    }
                }
            }
            catch { }
            return Environment.UserName;
        }

        public static string GetBitLockerRecoveryKey()
        {
            try
            {
                ManagementScope scope = new ManagementScope(@"\\.\root\CIMv2\Security\MicrosoftVolumeEncryption");
                scope.Connect();
                SelectQuery query = new SelectQuery("Win32_EncryptableVolume", "DriveLetter = 'C:'");
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(scope, query))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        ManagementBaseObject inParams = o.GetMethodParameters("GetKeyProtectors");
                        inParams["KeyProtectorType"] = 3; // 3 = RecoveryPassword
                        ManagementBaseObject outParams = o.InvokeMethod("GetKeyProtectors", inParams, null);
                        string[] ids = (string[])outParams["VolumeKeyProtectorID"];
                        if (ids != null && ids.Length > 0)
                        {
                            ManagementBaseObject inNum = o.GetMethodParameters("GetKeyProtectorNumericalPassword");
                            inNum["VolumeKeyProtectorID"] = ids[0];
                            ManagementBaseObject outNum = o.InvokeMethod("GetKeyProtectorNumericalPassword", inNum, null);
                            if (outNum["NumericalPassword"] != null)
                            {
                                return outNum["NumericalPassword"].ToString();
                            }
                        }
                        break;
                    }
                }
            }
            catch { }
            return "";
        }

        public static string GetHardwareUuid()
        {
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT UUID FROM Win32_ComputerSystemProduct"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        return CleanString(o["UUID"]);
                    }
                }
            }
            catch { }
            return "";
        }

        private static string CleanString(object val)
        {
            if (val == null) return "";
            return val.ToString().Trim();
        }
    }
}
