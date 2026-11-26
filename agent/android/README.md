# Project R Android MDM Agent (Architecture Blueprint)

## 1. Overview
The **Project R Android MDM Agent** is a native Android application designed to provide centralized enterprise fleet management, asset tracking, security enforcement (Mobile GPO), and remote actions for corporate Android devices (smartphones, tablets, and handheld barcode scanners).

---

## 2. Operating Mode: Device Owner (Android Enterprise)
Unlike regular Android apps that have restricted permissions, the Project R MDM Agent operates in **Device Owner** mode (`android.app.admin.DevicePolicyManager`).

### Provisioning Methods:
1. **NFC Provisioning**: Tap a newly unboxed device against a provisioning master device.
2. **QR Code Provisioning**: Tap the setup wizard screen 6 times on a factory-reset device $\rightarrow$ scan the Project R enrollment QR code containing the server URL and enrollment token.
3. **ADB Command (Developer / Staging Lab)**:
   ```bash
   adb shell dpm set-device-owner com.projectr.agent/.ProjectRDeviceAdminReceiver
   ```
   > **Note (Android 10+ / targetSdk 29+)**: Plain **Device Admin** cannot enforce `setCameraDisabled` (`SecurityException`) or `UserManager` restrictions. The agent must be provisioned as **Device Owner** (`isDeviceOwnerApp == true`).
4. **Zero-Touch Enrollment (ZTE) / Knox Mobile Enrollment (KME)**: Automated enrollment as soon as the device connects to Wi-Fi out of the box.

---

## 3. Key Capabilities & Mobile GPO Matrix

### A. Asset & Hardware Ingest
- **Hardware Identifiers**: IMEI 1, IMEI 2, MEID, Serial Number, Build Board, Manufacturer, Brand, Model.
- **Battery & Health**: Real-time battery percentage, charging state, temperature, battery cycle count, health status.
- **Storage & RAM**: Total internal flash storage, available storage, total RAM, used RAM.
- **Network Interfaces**: Primary Wi-Fi SSID, BSSID, Wi-Fi MAC Address, Cellular carrier name, SIM Slot state, Cellular signal strength, Local IP & Gateway.
- **Software Inventory**: List of all installed user & system packages, version names, install timestamps, APK signatures.

### B. Mobile GPO Policies (Enforced via `DevicePolicyManager`)
1. **Camera Blocking**:
   ```kotlin
   devicePolicyManager.setCameraDisabled(adminComponent, true)
   ```
2. **USB File Transfer (MTP) Blocking**:
   ```kotlin
   devicePolicyManager.setUsbDataSignalingEnabled(false) // Android 12+
   devicePolicyManager.addUserRestriction(adminComponent, UserManager.DISALLOW_USB_FILE_TRANSFER)
   ```
3. **Prevent Factory Reset (Anti-Theft)**:
   ```kotlin
   devicePolicyManager.addUserRestriction(adminComponent, UserManager.DISALLOW_FACTORY_RESET)
   ```
4. **Screen Lock & Complexity Policy**:
   ```kotlin
   devicePolicyManager.setPasswordQuality(adminComponent, DevicePolicyManager.PASSWORD_QUALITY_COMPLEX)
   devicePolicyManager.setPasswordMinimumLength(adminComponent, 6)
   devicePolicyManager.setMaximumTimeToLock(adminComponent, 120_000L) // 2 minutes
   ```
5. **Kiosk Mode (Single App / Dedicated Device)**:
   ```kotlin
   devicePolicyManager.setLockTaskPackages(adminComponent, arrayOf("com.projectr.pos", "com.projectr.agent"))
   activity.startLockTask()
   ```
6. **Block Screenshots & Screen Recording**:
   ```kotlin
   devicePolicyManager.setScreenCaptureDisabled(adminComponent, true)
   ```
7. **Disallow Safe Boot**:
   ```kotlin
   devicePolicyManager.addUserRestriction(adminComponent, UserManager.DISALLOW_SAFE_BOOT)
   ```

### C. Remote Management Actions
- **Remote Screen Lock**: `devicePolicyManager.lockNow()`
- **Remote Wipe / Factory Reset**: `devicePolicyManager.wipeData(DevicePolicyManager.WIPE_EXTERNAL_STORAGE)`
- **Change Password Remotely**: `devicePolicyManager.resetPasswordWithToken(...)`
- **Push IT Notification Banner**: Foreground notification with high priority sound & heads-up display.

---

## 4. Background Engine & Heartbeat
- **Persistent Service**: `HeartbeatService` (`com.projectr.agent.HeartbeatService`) running as an Android Foreground Service with a persistent, non-dismissible notification (`START_STICKY`), enforcing policies via `MdmPolicyManager` and reporting Observed State `evaluations` back on `/api/agent/heartbeat`.
- **Power Optimization**: Uses `WorkManager` with `PeriodicWorkRequestBuilder` (15-min background sync) + WebSocket / Firebase Cloud Messaging (FCM) high-priority push for instant 60s heartbeats and real-time remote commands.
- **Fail-Safe Offline Queue**: SQLite / Room database stores offline logs and telemetry until network connectivity is re-established.
