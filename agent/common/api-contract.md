# Unified Multi-Platform Agent API Specification (v1)
**Project R Enterprise Asset & MDM Management System**

This specification defines the universal protocol used by all Project R client agents across all supported operating systems:
- **Windows** (PC, Laptop, Server)
- **Android** (Smartphone, Tablet, Handheld Terminal) - Android Enterprise Device Owner
- **macOS** (MacBook, Mac Mini, iMac)
- **iOS / iPadOS** (iPhone, iPad) - Apple MDM Supervision

---

## 1. Core Architecture Principles
1. **Universal Protocol**: All platforms communicate with the same backend endpoints using identical JSON data contracts.
2. **Platform-Agnostic Storage**: The backend stores shared attributes in core columns (`device_id`, `tag`, `hostname`, `manufacturer`, `model`, `serial_number`, `uuid`, `operating_system`, `primary_ip`, `status`) and platform-specific attributes in `raw_summary` (JSON) and `gpo_policies` (JSON).
3. **Resilient Offline-First Design**: If the network is unavailable, agents cache events and retry on next connectivity without failing or freezing the host device.
4. **Anti-Tamper & Security**: Installation, tag modification, and uninstallation require cryptographic token authorization.

---

## 2. API Endpoints Overview

| Method | Endpoint | Description |
|---|---|---|
| `POST` | `/api/agent/v1/verify-token` | Verifies the deployment security token prior to installation. |
| `POST` | `/api/agent/v1/register` | Registers device and submits full hardware/OS/disk/network inventory. |
| `POST` | `/api/agent/v1/heartbeat` | 60-second periodic heartbeat ping, telemetry, and policy synchronization. |
| `POST` | `/api/agent/v1/update-tag` | Updates Asset TAG on client and server. |
| `POST` | `/api/agent/v1/verify-uninstall` | Authenticates IT authorization token before allowing uninstallation. |
| `POST` | `/api/agent/v1/actions/ack` | Reports execution outcome of remote commands (lock, wipe, kiosk, message). |

---

## 3. Universal Data Contracts

### 3.1 Device Registration (`POST /api/agent/v1/register`)
Sent upon initial installation and periodically (e.g., every 24 hours or on system boot).

#### Request Schema
```json
{
  "token": "PR-AGT-XXXXXXXXXX",
  "platform": "windows | android | macos | ios",
  "device_id": "string (unique hardware ID)",
  "hostname": "string (e.g. LAPTOP-IT-01 or Pixel-7-Office)",
  "tag": "string (Asset TAG, e.g. 4200001562)",
  "uuid": "string (Hardware UUID or Android ID or Apple Vendor ID)",
  "serial_number": "string (BIOS / Hardware Serial)",
  "manufacturer": "string (e.g. Lenovo, Dell, Samsung, Apple)",
  "model": "string (e.g. ThinkPad T14, Galaxy Tab S9, MacBook Pro 14)",
  "chassis_type": "string (Desktop | Notebook | Smartphone | Tablet | Server)",
  "operating_system": "string (e.g. Windows 11 Pro, Android 14, macOS Sonoma, iOS 17.5)",
  "os_version": "string (Build / version number)",
  "cpu_name": "string (e.g. Intel Core i7-13700H, Snapdragon 8 Gen 2, Apple M3)",
  "memory_mb": 16384,
  "storage_total_mb": 512000,
  "storage_free_mb": 348000,
  "battery": {
    "present": true,
    "percent": 88,
    "charging": false,
    "health": "good"
  },
  "last_user": "domain\\username or android_user_email",
  "primary_ip": "192.168.1.105",
  "primary_mac": "00:1A:2B:3C:4D:5E",
  "agent_version": "2.1.0",
  "security_status": {
    "encryption_enabled": true,
    "encryption_type": "BitLocker | FileVault | Android-FBE | iOS-DataProtection",
    "recovery_key": "string (if available & permitted)",
    "antivirus": "Windows Defender | Samsung Knox | XProtect",
    "firewall": true,
    "is_rooted_or_jailbroken": false,
    "screen_lock_enforced": true
  },
  "disks": [
    {
      "name": "NVMe Samsung 980 500GB",
      "model": "Samsung SSD 980 500GB",
      "serial_number": "S64DNC0T123456",
      "type": "Fixed hard disk media",
      "size_mb": 500107
    }
  ],
  "networks": [
    {
      "name": "Intel Wi-Fi 6E AX211",
      "mac": "00:1A:2B:3C:4D:5E",
      "ip_address": "192.168.1.105",
      "active": true
    }
  ]
}
```

---

### 3.2 Periodic Heartbeat (`POST /api/agent/v1/heartbeat`)
Sent every 60 seconds (configurable from portal settings).

#### Request Schema
```json
{
  "device_id": "string",
  "hostname": "string",
  "platform": "windows | android | macos | ios",
  "tag": "string",
  "agent_version": "2.1.0",
  "uptime_seconds": 128450,
  "logged_user": "string",
  "battery_percent": 88,
  "telemetry": {
    "cpu_load_percent": 14,
    "memory_used_mb": 8200
  },
  "browser_alerts": [
    {
      "keyword": "facebook.com",
      "title": "Facebook – Masuk atau Daftar",
      "browser": "Google Chrome",
      "timestamp": "2026-11-14T01:30:00Z"
    }
  ]
}
```

#### Response Schema (Server to Client)
```json
{
  "status": "ok",
  "response": "heartbeat accepted by Project R",
  "server_time": "2026-11-14T01:30:00+07:00",
  "interval": 60,
  "version": "2.1.0",
  "device_recognized": true,
  "tag": "4200001562",
  "policies": {
    "block_usb": true,
    "disable_taskmgr": false,
    "disable_cmd": false,
    "prevent_auto_reboot": true,
    "block_camera": false,
    "enforce_screen_lock": true,
    "block_factory_reset": true,
    "kiosk_mode": false,
    "browser_monitor_enabled": true,
    "browser_blacklist": ["youtube.com", "facebook.com", "tiktok.com", "instagram.com"]
  },
  "pending_actions": [
    {
      "id": "act-9921",
      "type": "lock_screen | remote_wipe | display_message | restart | trigger_inventory",
      "payload": {
        "message": "Perangkat ini dikelola oleh IT Infrastructure. Harap hubungi IT Support."
      }
    }
  ]
}
```

---

## 4. Cross-Platform Policy Mapping Matrix

| Capability / Policy | Windows Enforcement | Android MDM (Device Owner) | macOS MDM | iOS MDM |
|---|---|---|---|---|
| **USB Storage Blocking** | HKLM Registry `USBSTOR\Start = 4` & GPO `RemovableStorageDevices` | `setUsbDataSignalingEnabled(false)` (Android 12+) | Configuration Profile `com.apple.systempolicy.control` | Disallow USB Accessories in Lock Mode |
| **Camera Disable** | Device Manager disable via WMI / Registry | `setCameraDisabled(adminComponent, true)` | Configuration Profile `cameraEnabled = false` | Restriction `allowCamera = false` |
| **Enforce Screen Lock** | Local Security Policy (Password complexity) | `setPasswordQuality(admin, PASSWORD_QUALITY_COMPLEX)` | Passcode payload in Configuration Profile | Passcode payload in Configuration Profile |
| **Factory Reset Protection** | BitLocker encryption + BIOS password | `addUserRestriction(DISALLOW_FACTORY_RESET)` | Recovery Lock / Activation Lock | Activation Lock / Supervision |
| **Kiosk / Single-App Mode** | Windows Assigned Access / Shell Launcher | `setLockTaskPackages()` & `startLockTask()` | macOS Kiosk Profile | Autonomous Single App Mode (ASAM) |
| **App Blacklisting** | Software Restriction Policies / AppLocker | `setApplicationHidden(admin, packageName, true)` | Managed App Restrictions | Managed App Restrictions |
| **Remote Screen Lock** | `LockWorkStation()` Win32 API | `lockNow()` DevicePolicyManager | Remote Lock MDM command | Remote Lock MDM command |
| **Remote Wipe** | Encrypt drive & BitLocker secure erase | `wipeData(WIPE_EXTERNAL_STORAGE)` | EraseDevice MDM command | EraseDevice MDM command |

---

## 5. Security & Verification
- **Install Verification**: Every installer sends `POST /api/agent/v1/verify-token` before performing any write operations.
- **Uninstall Verification**: Uninstaller prompts for IT Authorization Token and verifies via `POST /api/agent/v1/verify-uninstall`.
- **Single Instance Guarantee**: Windows agents employ a named system mutex `Global\ProjectR_Agent_Mutex` to ensure only one worker instance runs at any given time.
- **Process Orphan Cleanup**: On startup, each agent cleans up any hung worker processes to prevent recursive process stacking (fork bombs).
