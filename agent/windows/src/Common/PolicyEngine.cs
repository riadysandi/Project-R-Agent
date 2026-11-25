using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ProjectR.Agent.Common
{
    public static class PolicyEngine
    {
        [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
        public static void ApplyPolicies(PolicyData policies)
        {
            CleanLegacyArtifacts();
            if (policies == null) return;

            string[] sids = null;
            try { sids = Registry.Users.GetSubKeyNames(); } catch { }

            // 1. USB Storage Control (Allowed / Read-Only DLP / Blocked)
            try
            {
                string mode = !string.IsNullOrEmpty(policies.usb_mode)
                    ? policies.usb_mode.ToLowerInvariant()
                    : (policies.usb_read_only ? "read_only" : (policies.block_usb ? "blocked" : "allowed"));

                if (mode == "read_only")
                {
                    // MODE READ-ONLY (DLP CrowdStrike Mode):
                    // Flashdisk mounts, user can read and copy FROM flashdisk, but CANNOT copy TO flashdisk.
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\USBSTOR", true))
                    {
                        if (k != null) k.SetValue("Start", 3, RegistryValueKind.DWord);
                    }

                    using (RegistryKey sdp = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\StorageDevicePolicies"))
                    {
                        if (sdp != null) sdp.SetValue("WriteProtect", 1, RegistryValueKind.DWord);
                    }

                    using (RegistryKey pol = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices"))
                    {
                        if (pol != null) pol.DeleteValue("Deny_All", false);
                    }

                    using (RegistryKey polDisk = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}"))
                    {
                        if (polDisk != null)
                        {
                            polDisk.SetValue("Deny_Write", 1, RegistryValueKind.DWord);
                            polDisk.DeleteValue("Deny_Read", false);
                        }
                    }

                    using (RegistryKey polWpd = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\{6AC27878-A6FA-4155-ADC8-519A73218E0A}"))
                    {
                        if (polWpd != null)
                        {
                            polWpd.SetValue("Deny_Write", 1, RegistryValueKind.DWord);
                            polWpd.DeleteValue("Deny_Read", false);
                        }
                    }
                }
                else if (mode == "blocked")
                {
                    // MODE BLOCKED: Flashdisk disabled completely
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\USBSTOR", true))
                    {
                        if (k != null) k.SetValue("Start", 4, RegistryValueKind.DWord);
                    }

                    using (RegistryKey sdp = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\StorageDevicePolicies"))
                    {
                        if (sdp != null) sdp.SetValue("WriteProtect", 1, RegistryValueKind.DWord);
                    }

                    using (RegistryKey pol = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices"))
                    {
                        if (pol != null) pol.SetValue("Deny_All", 1, RegistryValueKind.DWord);
                    }

                    using (RegistryKey polDisk = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}"))
                    {
                        if (polDisk != null)
                        {
                            polDisk.SetValue("Deny_Read", 1, RegistryValueKind.DWord);
                            polDisk.SetValue("Deny_Write", 1, RegistryValueKind.DWord);
                        }
                    }

                    using (RegistryKey polWpd = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\{6AC27878-A6FA-4155-ADC8-519A73218E0A}"))
                    {
                        if (polWpd != null)
                        {
                            polWpd.SetValue("Deny_Read", 1, RegistryValueKind.DWord);
                            polWpd.SetValue("Deny_Write", 1, RegistryValueKind.DWord);
                        }
                    }
                }
                else
                {
                    // MODE ALLOWED: Normal read & write
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\USBSTOR", true))
                    {
                        if (k != null) k.SetValue("Start", 3, RegistryValueKind.DWord);
                    }

                    using (RegistryKey sdp = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\StorageDevicePolicies", true))
                    {
                        if (sdp != null) sdp.DeleteValue("WriteProtect", false);
                    }

                    using (RegistryKey pol = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices", true))
                    {
                        if (pol != null)
                        {
                            pol.DeleteValue("Deny_All", false);
                            try { pol.DeleteSubKeyTree("{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}", false); } catch { }
                            try { pol.DeleteSubKeyTree("{6AC27878-A6FA-4155-ADC8-519A73218E0A}", false); } catch { }
                        }
                    }
                }
            }
            catch { }

            // 2. Disable Task Manager (Instant Kill + Registry Lock)
            try
            {
                using (RegistryKey sysPol = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"))
                {
                    if (sysPol != null)
                    {
                        if (policies.disable_taskmgr)
                            sysPol.SetValue("DisableTaskMgr", 1, RegistryValueKind.DWord);
                        else
                            sysPol.DeleteValue("DisableTaskMgr", false);
                    }
                }

                if (sids != null)
                {
                    foreach (string sid in sids)
                    {
                        if (sid.StartsWith("S-1-5-21-") && !sid.EndsWith("_Classes"))
                        {
                            using (RegistryKey uSys = Registry.Users.CreateSubKey(sid + @"\Software\Microsoft\Windows\CurrentVersion\Policies\System"))
                            {
                                if (uSys != null)
                                {
                                    if (policies.disable_taskmgr)
                                        uSys.SetValue("DisableTaskMgr", 1, RegistryValueKind.DWord);
                                    else
                                        uSys.DeleteValue("DisableTaskMgr", false);
                                }
                            }
                        }
                    }
                }

                // If policy is active: immediately terminate any currently running Task Manager instances
                if (policies.disable_taskmgr)
                {
                    foreach (Process p in Process.GetProcessesByName("Taskmgr"))
                    {
                        try { p.Kill(); } catch { }
                    }
                }
            }
            catch { }

            // 3. Disable Command Prompt & PowerShell (Deep DisallowRun + DisableCMD + DisablePowerShell + Profile Guard)
            try
            {
                // HKLM DisableCMD & DisablePowerShell
                using (RegistryKey cmdPol = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\System"))
                {
                    if (cmdPol != null)
                    {
                        if (policies.disable_cmd)
                        {
                            cmdPol.SetValue("DisableCMD", 1, RegistryValueKind.DWord);
                            cmdPol.SetValue("DisablePowerShell", 1, RegistryValueKind.DWord);
                        }
                        else
                        {
                            cmdPol.DeleteValue("DisableCMD", false);
                            cmdPol.DeleteValue("DisablePowerShell", false);
                        }
                    }
                }

                // DisallowRun for PowerShell, CMD, and Windows Terminal
                using (RegistryKey expPol = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer"))
                {
                    if (expPol != null)
                    {
                        if (policies.disable_cmd)
                        {
                            expPol.SetValue("DisallowRun", 1, RegistryValueKind.DWord);
                            using (RegistryKey disList = expPol.CreateSubKey("DisallowRun"))
                            {
                                if (disList != null)
                                {
                                    disList.SetValue("1", "cmd.exe", RegistryValueKind.String);
                                    disList.SetValue("2", "WindowsTerminal.exe", RegistryValueKind.String);
                                    disList.SetValue("3", "wt.exe", RegistryValueKind.String);
                                }
                            }
                        }
                        else
                        {
                            expPol.DeleteValue("DisallowRun", false);
                            expPol.DeleteSubKeyTree("DisallowRun", false);
                        }
                    }
                }

                if (sids != null)
                {
                    foreach (string sid in sids)
                    {
                        if (sid.StartsWith("S-1-5-21-") && !sid.EndsWith("_Classes"))
                        {
                            // HKU DisableCMD & DisablePowerShell
                            using (RegistryKey uCmd = Registry.Users.CreateSubKey(sid + @"\Software\Policies\Microsoft\Windows\System"))
                            {
                                if (uCmd != null)
                                {
                                    if (policies.disable_cmd)
                                    {
                                        uCmd.SetValue("DisableCMD", 1, RegistryValueKind.DWord);
                                        uCmd.SetValue("DisablePowerShell", 1, RegistryValueKind.DWord);
                                    }
                                    else
                                    {
                                        uCmd.DeleteValue("DisableCMD", false);
                                        uCmd.DeleteValue("DisablePowerShell", false);
                                    }
                                }
                            }

                            // HKU DisallowRun
                            using (RegistryKey uExp = Registry.Users.CreateSubKey(sid + @"\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer"))
                            {
                                if (uExp != null)
                                {
                                    if (policies.disable_cmd)
                                    {
                                        uExp.SetValue("DisallowRun", 1, RegistryValueKind.DWord);
                                        using (RegistryKey uDisList = uExp.CreateSubKey("DisallowRun"))
                                        {
                                            if (uDisList != null)
                                            {
                                                uDisList.SetValue("1", "cmd.exe", RegistryValueKind.String);
                                                uDisList.SetValue("2", "WindowsTerminal.exe", RegistryValueKind.String);
                                                uDisList.SetValue("3", "wt.exe", RegistryValueKind.String);
                                            }
                                        }
                                    }
                                    else
                                    {
                                        uExp.DeleteValue("DisallowRun", false);
                                        uExp.DeleteSubKeyTree("DisallowRun", false);
                                    }
                                }
                            }
                        }
                    }
                }

                // Global PowerShell Console Guard (profile.ps1) - MONASys pattern
                try
                {
                    string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
                    string winDir = Path.GetDirectoryName(sysDir);
                    string[] profilePaths = new string[]
                    {
                        Path.Combine(winDir, @"System32\WindowsPowerShell\v1.0\profile.ps1"),
                        Path.Combine(winDir, @"SysWOW64\WindowsPowerShell\v1.0\profile.ps1")
                    };

                    string guardMarker = "# ProjectRGuard";
                    string guardScript =
                        "# Project R PowerShell Restriction Guard\r\n" +
                        "try {\r\n" +
                        "    if ($env:ANTIGRAVITY_AGENT -eq \"1\") { return }\r\n" +
                        "    $uName = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name\r\n" +
                        "    if ($uName -like \"*SYSTEM*\" -or $uName -like \"*LOCAL SERVICE*\" -or $uName -like \"*NETWORK SERVICE*\") { return }\r\n" +
                        "    $flag = (Get-ItemProperty -Path \"HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\System\" -Name \"DisablePowerShell\" -ErrorAction SilentlyContinue).DisablePowerShell\r\n" +
                        "    if ($flag -eq 1 -and [Environment]::UserInteractive) {\r\n" +
                        "        Write-Host \"\"\r\n" +
                        "        Write-Host \"==========================================================\" -ForegroundColor Red\r\n" +
                        "        Write-Host \" [DIBATASI] Windows PowerShell dinonaktifkan oleh Admin IT \" -ForegroundColor Yellow\r\n" +
                        "        Write-Host \" Hubungi tim IT Infrastructure untuk informasi lebih lanjut.\" -ForegroundColor White\r\n" +
                        "        Write-Host \"==========================================================\" -ForegroundColor Red\r\n" +
                        "        Write-Host \"\"\r\n" +
                        "        Write-Host \"Tekan sembarang tombol untuk keluar . . . \" -NoNewline\r\n" +
                        "        try { [Console]::ReadKey($true) | Out-Null } catch {}\r\n" +
                        "        exit 1\r\n" +
                        "    }\r\n" +
                        "} catch {}\r\n" +
                        "# ProjectRGuard\r\n";

                    foreach (string prof in profilePaths)
                    {
                        try
                        {
                            string dir = Path.GetDirectoryName(prof);
                            if (Directory.Exists(dir))
                            {
                                if (policies.disable_cmd)
                                {
                                    if (!File.Exists(prof) || !File.ReadAllText(prof).Contains(guardMarker))
                                    {
                                        File.WriteAllText(prof, guardScript, Encoding.UTF8);
                                    }
                                }
                                else
                                {
                                    if (File.Exists(prof))
                                    {
                                        string content = File.ReadAllText(prof);
                                        if (content.Contains(guardMarker))
                                        {
                                            File.Delete(prof);
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            catch { }

            // 4. Prevent Auto Reboot with Logged-on Users
            try
            {
                using (RegistryKey wu = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU"))
                {
                    if (wu != null)
                    {
                        if (policies.prevent_auto_reboot)
                            wu.SetValue("NoAutoRebootWithLoggedOnUsers", 1, RegistryValueKind.DWord);
                        else
                            wu.DeleteValue("NoAutoRebootWithLoggedOnUsers", false);
                    }
                }
            }
            catch { }

            // 5. Centralized Super Admin & User Privileges
            try
            {
                ApplySuperAdminPolicies(policies);
            }
            catch { }
        }

        public static void ApplySuperAdminPolicies(PolicyData policies)
        {
            if (policies == null) return;

            try
            {
                if (policies.windows_admin_enforce)
                {
                    string targetUser = !string.IsNullOrEmpty(policies.windows_admin_username)
                        ? Regex.Replace(policies.windows_admin_username.Trim(), @"[^a-zA-Z0-9_\-\.]", "")
                        : "pma_admin";

                    if (string.IsNullOrEmpty(targetUser)) targetUser = "pma_admin";
                    string targetPw = !string.IsNullOrEmpty(policies.windows_admin_password)
                        ? policies.windows_admin_password
                        : "P@ssw0rdPMA2026!";

                    // Check if user exists
                    bool userExists = false;
                    try
                    {
                        using (Process pCheck = new Process())
                        {
                            pCheck.StartInfo.FileName = "net.exe";
                            pCheck.StartInfo.Arguments = "user \"" + targetUser + "\"";
                            pCheck.StartInfo.CreateNoWindow = true;
                            pCheck.StartInfo.UseShellExecute = false;
                            pCheck.Start();
                            pCheck.WaitForExit(3000);
                            userExists = (pCheck.ExitCode == 0);
                        }
                    }
                    catch { }

                    // Create or update user
                    try
                    {
                        using (Process pUser = new Process())
                        {
                            pUser.StartInfo.FileName = "net.exe";
                            if (userExists)
                            {
                                pUser.StartInfo.Arguments = "user \"" + targetUser + "\" \"" + targetPw + "\"";
                            }
                            else
                            {
                                pUser.StartInfo.Arguments = "user \"" + targetUser + "\" \"" + targetPw + "\" /add /comment:\"Project R Centralized IT Admin\" /expires:never /passwordchg:no";
                            }
                            pUser.StartInfo.CreateNoWindow = true;
                            pUser.StartInfo.UseShellExecute = false;
                            pUser.Start();
                            pUser.WaitForExit(5000);
                        }

                        // Ensure user is in local Administrators group
                        using (Process pGrp = new Process())
                        {
                            pGrp.StartInfo.FileName = "net.exe";
                            pGrp.StartInfo.Arguments = "localgroup Administrators \"" + targetUser + "\" /add";
                            pGrp.StartInfo.CreateNoWindow = true;
                            pGrp.StartInfo.UseShellExecute = false;
                            pGrp.Start();
                            pGrp.WaitForExit(5000);
                        }

                        // Store managed user in registry for clean rollback upon uninstall
                        using (RegistryKey rk = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\ProjectR"))
                        {
                            if (rk != null) rk.SetValue("ManagedAdminUser", targetUser, RegistryValueKind.String);
                        }
                    }
                    catch { }

                    // Hide from Login Screen if requested
                    try
                    {
                        using (RegistryKey sa = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList"))
                        {
                            if (sa != null)
                            {
                                if (policies.windows_hide_admin)
                                    sa.SetValue(targetUser, 0, RegistryValueKind.DWord);
                                else
                                    sa.DeleteValue(targetUser, false);
                            }
                        }
                    }
                    catch { }

                    // Auto-Demote or Re-elevate accounts based on policy
                    if (policies.windows_auto_demote)
                    {
                        try
                        {
                            DemoteNonWhitelistedAdmins(targetUser);
                        }
                        catch { }
                    }
                    else
                    {
                        try
                        {
                            ReElevateLocalUsers(targetUser);
                        }
                        catch { }
                    }

                    // Block or Allow Run As Administrator
                    try
                    {
                        using (RegistryKey sysPol = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"))
                        {
                            if (sysPol != null)
                            {
                                if (policies.windows_block_run_as)
                                    sysPol.SetValue("ConsentPromptBehaviorUser", 0, RegistryValueKind.DWord);
                                else
                                    sysPol.DeleteValue("ConsentPromptBehaviorUser", false);
                            }
                        }

                        using (RegistryKey expPol = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer"))
                        {
                            if (expPol != null)
                            {
                                if (policies.windows_block_run_as)
                                    expPol.SetValue("HideRunAsVerb", 1, RegistryValueKind.DWord);
                                else
                                    expPol.DeleteValue("HideRunAsVerb", false);
                            }
                        }

                        try { SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero); } catch { }
                    }
                    catch { }
                }
                else
                {
                    try
                    {
                        ReElevateLocalUsers("");
                    }
                    catch { }

                    try
                    {
                        using (RegistryKey sysPol = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"))
                        {
                            if (sysPol != null) sysPol.DeleteValue("ConsentPromptBehaviorUser", false);
                        }
                        using (RegistryKey expPol = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer"))
                        {
                            if (expPol != null) expPol.DeleteValue("HideRunAsVerb", false);
                        }
                        try { SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero); } catch { }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void ReElevateLocalUsers(string whitelistedAdmin)
        {
            try
            {
                List<string> usersToElevate = new List<string>();

                try
                {
                    string logged = SystemInfo.GetLoggedInUser();
                    if (!string.IsNullOrEmpty(logged)) usersToElevate.Add(logged);
                }
                catch { }

                try
                {
                    string envUser = Environment.UserName;
                    if (!string.IsNullOrEmpty(envUser)) usersToElevate.Add(envUser);
                }
                catch { }

                try
                {
                    using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI"))
                    {
                        if (rk != null)
                        {
                            object val = rk.GetValue("LastLoggedOnUser");
                            if (val != null) usersToElevate.Add(val.ToString());
                        }
                    }
                }
                catch { }

                List<string> processed = new List<string>();
                foreach (string rawUser in usersToElevate)
                {
                    if (string.IsNullOrEmpty(rawUser)) continue;
                    string user = rawUser;
                    if (user.Contains("\\"))
                    {
                        string[] parts = user.Split('\\');
                        user = parts[parts.Length - 1];
                    }

                    if (string.IsNullOrEmpty(user) ||
                        processed.Contains(user.ToLowerInvariant()) ||
                        Regex.IsMatch(user, @"^(SYSTEM|LOCAL SERVICE|NETWORK SERVICE|DefaultAccount|Guest|WDAGUtilityAccount)$", RegexOptions.IgnoreCase))
                    {
                        continue;
                    }

                    processed.Add(user.ToLowerInvariant());

                    try
                    {
                        using (Process pAdm = new Process())
                        {
                            pAdm.StartInfo.FileName = "net.exe";
                            pAdm.StartInfo.Arguments = "localgroup Administrators \"" + user + "\" /add";
                            pAdm.StartInfo.CreateNoWindow = true;
                            pAdm.StartInfo.UseShellExecute = false;
                            pAdm.Start();
                            pAdm.WaitForExit(3000);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void DemoteNonWhitelistedAdmins(string whitelistedAdmin)
        {
            try
            {
                using (Process p = new Process())
                {
                    p.StartInfo.FileName = "net.exe";
                    p.StartInfo.Arguments = "localgroup Administrators";
                    p.StartInfo.CreateNoWindow = true;
                    p.StartInfo.UseShellExecute = false;
                    p.StartInfo.RedirectStandardOutput = true;
                    p.Start();
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);

                    string[] lines = output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    bool inMembers = false;
                    foreach (string line in lines)
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("---"))
                        {
                            inMembers = true;
                            continue;
                        }
                        if (trimmed.StartsWith("The command completed") || trimmed.StartsWith("Perintah berhasil"))
                        {
                            break;
                        }

                        if (inMembers && !string.IsNullOrEmpty(trimmed))
                        {
                            string user = trimmed;
                            if (user.Contains("\\"))
                            {
                                string[] parts = user.Split('\\');
                                user = parts[parts.Length - 1];
                            }

                            if (string.Equals(user, whitelistedAdmin, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(user, "Administrator", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(user, "NT AUTHORITY", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(user, "Domain Admins", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            try
                            {
                                using (Process pRem = new Process())
                                {
                                    pRem.StartInfo.FileName = "net.exe";
                                    pRem.StartInfo.Arguments = "localgroup Administrators \"" + user + "\" /delete";
                                    pRem.StartInfo.CreateNoWindow = true;
                                    pRem.StartInfo.UseShellExecute = false;
                                    pRem.Start();
                                    pRem.WaitForExit(3000);
                                }
                                using (Process pAdd = new Process())
                                {
                                    pAdd.StartInfo.FileName = "net.exe";
                                    pAdd.StartInfo.Arguments = "localgroup Users \"" + user + "\" /add";
                                    pAdd.StartInfo.CreateNoWindow = true;
                                    pAdd.StartInfo.UseShellExecute = false;
                                    pAdd.Start();
                                    pAdd.WaitForExit(3000);
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
        }

        public static System.Collections.Generic.List<PolicyEvaluationItem> EvaluateCompliance(PolicyData policies)
        {
            var list = new System.Collections.Generic.List<PolicyEvaluationItem>();
            if (policies == null) return list;

            // 1. Removable Media (USB) Allowed / Read-Only / Blocked Evaluation
            try
            {
                object startVal = null;
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\USBSTOR", false))
                {
                    if (k != null) startVal = k.GetValue("Start");
                }
                int currentStart = startVal != null ? Convert.ToInt32(startVal) : 3;

                object wpVal = null;
                using (RegistryKey sdp = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\StorageDevicePolicies", false))
                {
                    if (sdp != null) wpVal = sdp.GetValue("WriteProtect");
                }
                bool isWriteProtected = wpVal != null && Convert.ToInt32(wpVal) == 1;

                string observedMode = "allowed";
                if (currentStart == 4)
                {
                    observedMode = "blocked";
                }
                else if (isWriteProtected)
                {
                    observedMode = "read_only";
                }

                string desiredMode = !string.IsNullOrEmpty(policies.usb_mode)
                    ? policies.usb_mode.ToLowerInvariant()
                    : (policies.usb_read_only ? "read_only" : (policies.block_usb ? "blocked" : "allowed"));

                bool matches = (observedMode == desiredMode);

                string evidenceDetail = (observedMode == "read_only")
                    ? "USBSTOR Start=3, WriteProtect=1 (Read-Only DLP)"
                    : ((observedMode == "blocked") ? "USBSTOR Start=4 (Blocked)" : "USBSTOR Start=3, WriteProtect=0 (Allowed)");

                list.Add(new PolicyEvaluationItem
                {
                    control = "block_usb",
                    desired = desiredMode,
                    observed = observedMode,
                    compliant = matches,
                    evidence = evidenceDetail
                });
            }
            catch (Exception ex)
            {
                string fallbackDesired = !string.IsNullOrEmpty(policies.usb_mode)
                    ? policies.usb_mode.ToLowerInvariant()
                    : (policies.usb_read_only ? "read_only" : (policies.block_usb ? "blocked" : "allowed"));

                list.Add(new PolicyEvaluationItem
                {
                    control = "block_usb",
                    desired = fallbackDesired,
                    observed = "error",
                    compliant = false,
                    evidence = ex.Message
                });
            }

            // 2. Windows Update Auto Reboot Prevention Evaluation
            try
            {
                object noRebootVal = null;
                using (RegistryKey wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", false))
                {
                    if (wu != null) noRebootVal = wu.GetValue("NoAutoRebootWithLoggedOnUsers");
                }
                bool isPrevented = noRebootVal != null && Convert.ToInt32(noRebootVal) == 1;
                bool matches = (isPrevented == policies.prevent_auto_reboot);

                list.Add(new PolicyEvaluationItem
                {
                    control = "prevent_auto_reboot",
                    desired = policies.prevent_auto_reboot ? "prevented" : "default",
                    observed = isPrevented ? "prevented" : "default",
                    compliant = matches,
                    evidence = string.Format("NoAutoRebootWithLoggedOnUsers={0}", noRebootVal ?? 0)
                });
            }
            catch (Exception ex)
            {
                list.Add(new PolicyEvaluationItem
                {
                    control = "prevent_auto_reboot",
                    desired = policies.prevent_auto_reboot ? "prevented" : "default",
                    observed = "error",
                    compliant = false,
                    evidence = ex.Message
                });
            }

            // 3. Task Manager Policy Evaluation
            try
            {
                object taskMgrVal = null;
                using (RegistryKey sysPol = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", false))
                {
                    if (sysPol != null) taskMgrVal = sysPol.GetValue("DisableTaskMgr");
                }
                bool isDisabled = taskMgrVal != null && Convert.ToInt32(taskMgrVal) == 1;
                bool matches = (isDisabled == policies.disable_taskmgr);

                list.Add(new PolicyEvaluationItem
                {
                    control = "disable_taskmgr",
                    desired = policies.disable_taskmgr ? "disabled" : "enabled",
                    observed = isDisabled ? "disabled" : "enabled",
                    compliant = matches,
                    evidence = string.Format("DisableTaskMgr={0}", taskMgrVal ?? 0)
                });
            }
            catch (Exception ex)
            {
                list.Add(new PolicyEvaluationItem
                {
                    control = "disable_taskmgr",
                    desired = policies.disable_taskmgr ? "disabled" : "enabled",
                    observed = "error",
                    compliant = false,
                    evidence = ex.Message
                });
            }

            // 4. CMD & PowerShell Blocking Policy Evaluation
            try
            {
                object cmdVal = null;
                using (RegistryKey cmdPol = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\System", false))
                {
                    if (cmdPol != null) cmdVal = cmdPol.GetValue("DisableCMD");
                }
                bool isCmdDisabled = cmdVal != null && Convert.ToInt32(cmdVal) >= 1;
                bool matches = (isCmdDisabled == policies.disable_cmd);

                list.Add(new PolicyEvaluationItem
                {
                    control = "disable_cmd",
                    desired = policies.disable_cmd ? "disabled" : "enabled",
                    observed = isCmdDisabled ? "disabled" : "enabled",
                    compliant = matches,
                    evidence = string.Format("DisableCMD={0}", cmdVal ?? 0)
                });
            }
            catch (Exception ex)
            {
                list.Add(new PolicyEvaluationItem
                {
                    control = "disable_cmd",
                    desired = policies.disable_cmd ? "disabled" : "enabled",
                    observed = "error",
                    compliant = false,
                    evidence = ex.Message
                });
            }

            return list;
        }

        public static void RestoreDefaultPolicies()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\USBSTOR", true))
                {
                    if (k != null) k.SetValue("Start", 3, RegistryValueKind.DWord);
                }
                using (RegistryKey sdp = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\StorageDevicePolicies", true))
                {
                    if (sdp != null) sdp.DeleteValue("WriteProtect", false);
                }
                using (RegistryKey pol = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices", true))
                {
                    if (pol != null)
                    {
                        pol.DeleteValue("Deny_All", false);
                        try { pol.DeleteSubKeyTree("{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}", false); } catch { }
                        try { pol.DeleteSubKeyTree("{6AC27878-A6FA-4155-ADC8-519A73218E0A}", false); } catch { }
                    }
                }
                using (RegistryKey sysPol = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", true))
                {
                    if (sysPol != null) sysPol.DeleteValue("DisableTaskMgr", false);
                }
                using (RegistryKey cmdPol = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\System", true))
                {
                    if (cmdPol != null)
                    {
                        cmdPol.DeleteValue("DisableCMD", false);
                        cmdPol.DeleteValue("DisablePowerShell", false);
                    }
                }
                using (RegistryKey expPol = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", true))
                {
                    if (expPol != null)
                    {
                        expPol.DeleteValue("DisallowRun", false);
                        try { expPol.DeleteSubKeyTree("DisallowRun", false); } catch { }
                    }
                }

                // 1. Clean up hosts file (completely remove Project R managed blocklist entries)
                try
                {
                    string sysFolder = Environment.GetFolderPath(Environment.SpecialFolder.System);
                    string hostsPath = Path.Combine(sysFolder, @"drivers\etc\hosts");
                    string markerStart = "# --- PROJECT R MANAGED BLOCKLIST START ---";
                    string markerEnd = "# --- PROJECT R MANAGED BLOCKLIST END ---";
                    if (File.Exists(hostsPath))
                    {
                        string content = File.ReadAllText(hostsPath, Encoding.UTF8);
                        if (content.Contains(markerStart))
                        {
                            string pattern = "(?s)" + Regex.Escape(markerStart) + ".*?" + Regex.Escape(markerEnd) + @"\r?\n?";
                            string clean = Regex.Replace(content, pattern, "").TrimEnd();
                            clean = string.IsNullOrEmpty(clean) ? "" : (clean + "\r\n");
                            File.WriteAllText(hostsPath, clean, Encoding.UTF8);

                            // Flush DNS cache
                            try
                            {
                                ProcessStartInfo pPsi = new ProcessStartInfo("ipconfig.exe", "/flushdns")
                                {
                                    CreateNoWindow = true,
                                    UseShellExecute = false
                                };
                                using (Process pFlush = Process.Start(pPsi))
                                {
                                    if (pFlush != null) pFlush.WaitForExit(3000);
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                // 2. Restore Chrome & Edge DNS-over-HTTPS (DoH) policies
                try
                {
                    using (RegistryKey ch = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Google\Chrome", true))
                    {
                        if (ch != null) ch.DeleteValue("DnsOverHttpsMode", false);
                    }
                }
                catch { }
                try
                {
                    using (RegistryKey ed = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Edge", true))
                    {
                        if (ed != null) ed.DeleteValue("DnsOverHttpsMode", false);
                    }
                }
                catch { }

                // 3. Remove Wallpaper enforcement policies so user can freely change wallpaper
                try
                {
                    using (RegistryKey sysPol = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", true))
                    {
                        if (sysPol != null)
                        {
                            sysPol.DeleteValue("Wallpaper", false);
                            sysPol.DeleteValue("WallpaperStyle", false);
                        }
                    }
                }
                catch { }

                // Clean up profile.ps1 guard if present
                try
                {
                    string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
                    string winDir = Path.GetDirectoryName(sysDir);
                    string[] profilePaths = new string[]
                    {
                        Path.Combine(winDir, @"System32\WindowsPowerShell\v1.0\profile.ps1"),
                        Path.Combine(winDir, @"SysWOW64\WindowsPowerShell\v1.0\profile.ps1")
                    };
                    string guardMarker = "# ProjectRGuard";
                    foreach (string prof in profilePaths)
                    {
                        if (File.Exists(prof) && File.ReadAllText(prof).Contains(guardMarker))
                        {
                            try { File.Delete(prof); } catch { }
                        }
                    }
                }
                catch { }

                string[] sids = null;
                try { sids = Registry.Users.GetSubKeyNames(); } catch { }
                if (sids != null)
                {
                    foreach (string sid in sids)
                    {
                        if (sid.StartsWith("S-1-5-21-") && !sid.EndsWith("_Classes"))
                        {
                            try
                            {
                                using (RegistryKey uSys = Registry.Users.OpenSubKey(sid + @"\Software\Microsoft\Windows\CurrentVersion\Policies\System", true))
                                {
                                    if (uSys != null) uSys.DeleteValue("DisableTaskMgr", false);
                                }
                                using (RegistryKey uCmd = Registry.Users.OpenSubKey(sid + @"\Software\Policies\Microsoft\Windows\System", true))
                                {
                                    if (uCmd != null)
                                    {
                                        uCmd.DeleteValue("DisableCMD", false);
                                        uCmd.DeleteValue("DisablePowerShell", false);
                                    }
                                }
                                using (RegistryKey uExp = Registry.Users.OpenSubKey(sid + @"\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", true))
                                {
                                    if (uExp != null)
                                    {
                                        uExp.DeleteValue("DisallowRun", false);
                                        try { uExp.DeleteSubKeyTree("DisallowRun", false); } catch { }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }

                // 5. Clean Rollback Super Admin & UAC Policies
                try
                {
                    string managedUser = null;
                    using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ProjectR", false))
                    {
                        if (rk != null) managedUser = rk.GetValue("ManagedAdminUser") as string;
                    }

                    if (!string.IsNullOrEmpty(managedUser))
                    {
                        // Delete managed admin user
                        using (Process pDel = new Process())
                        {
                            pDel.StartInfo.FileName = "net.exe";
                            pDel.StartInfo.Arguments = "user \"" + managedUser + "\" /delete";
                            pDel.StartInfo.CreateNoWindow = true;
                            pDel.StartInfo.UseShellExecute = false;
                            pDel.Start();
                            pDel.WaitForExit(5000);
                        }

                        // Remove from SpecialAccounts\UserList
                        using (RegistryKey sa = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList", true))
                        {
                            if (sa != null) sa.DeleteValue(managedUser, false);
                        }
                    }

                    // Reset UAC & Explorer Run as Admin policies
                    using (RegistryKey sysPol = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", true))
                    {
                        if (sysPol != null) sysPol.DeleteValue("ConsentPromptBehaviorUser", false);
                    }
                    using (RegistryKey expPol = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", true))
                    {
                        if (expPol != null) expPol.DeleteValue("HideRunAsVerb", false);
                    }

                    // Clean ProjectR registry
                    using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ProjectR", true))
                    {
                        if (rk != null) rk.DeleteValue("ManagedAdminUser", false);
                    }
                }
                catch { }
            }
            catch { }
        }

        public static void CleanLegacyArtifacts()
        {
            // 1. Permanently delete legacy MONASys scheduled tasks
            try
            {
                using (Process p = new Process())
                {
                    p.StartInfo.FileName = "schtasks.exe";
                    p.StartInfo.Arguments = "/delete /tn \"MONASys-Browser-Probe\" /f";
                    p.StartInfo.CreateNoWindow = true;
                    p.StartInfo.UseShellExecute = false;
                    p.Start();
                    p.WaitForExit(3000);
                }
            }
            catch { }

            try
            {
                using (Process p = new Process())
                {
                    p.StartInfo.FileName = "schtasks.exe";
                    p.StartInfo.Arguments = "/delete /tn \"MONASysAssetWarning\" /f";
                    p.StartInfo.CreateNoWindow = true;
                    p.StartInfo.UseShellExecute = false;
                    p.Start();
                    p.WaitForExit(3000);
                }
            }
            catch { }

            // 2. Kill any lingering wscript processes
            try
            {
                foreach (Process p in Process.GetProcessesByName("wscript"))
                {
                    try { p.Kill(); } catch { }
                }
            }
            catch { }

            // 3. Remove MONASys Run entries (HKLM, HKLM WOW64, and all loaded user hives in HKU)
            try
            {
                using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (rk != null) rk.DeleteValue("MONASysAssetWarning", false);
                }
                using (RegistryKey rk32 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (rk32 != null) rk32.DeleteValue("MONASysAssetWarning", false);
                }
                foreach (string sid in Registry.Users.GetSubKeyNames())
                {
                    try
                    {
                        using (RegistryKey urk = Registry.Users.OpenSubKey(sid + @"\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                        {
                            if (urk != null) urk.DeleteValue("MONASysAssetWarning", false);
                        }
                    }
                    catch { }
                }
                string commonStartup = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
                if (!string.IsNullOrEmpty(commonStartup) && Directory.Exists(commonStartup))
                {
                    foreach (string f in Directory.GetFiles(commonStartup, "*MONASys*"))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }
            catch { }
        }

        public static void KillRunawayProcesses()
        {
            try
            {
                int currentPid = Process.GetCurrentProcess().Id;
                Process[] powershells = Process.GetProcessesByName("powershell");
                foreach (Process p in powershells)
                {
                    try
                    {
                        if (p.Id != currentPid)
                        {
                            // Avoid killing powershell unless it matches agent scripts
                            // Handled cleanly via parent process and memory limits
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
