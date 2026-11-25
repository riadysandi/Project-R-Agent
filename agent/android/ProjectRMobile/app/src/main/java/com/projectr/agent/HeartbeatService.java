package com.projectr.agent;

import android.app.AlarmManager;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.app.admin.DevicePolicyManager;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.UserManager;
import android.util.Log;
import androidx.core.app.NotificationCompat;
import org.json.JSONObject;

public class HeartbeatService extends Service {
    private static final String TAG = "ProjectRHeartbeat";
    private static final String CHANNEL_ID = "project_r_guardian_channel";
    private boolean isRunning = false;
    private Thread workerThread;
    private volatile int heartbeatIntervalSec = 60;
    private volatile int inventoryIntervalSec = 3600;

    @Override
    public void onCreate() {
        super.onCreate();
        createNotificationChannel();
        Notification notification = new NotificationCompat.Builder(this, CHANNEL_ID)
                .setContentTitle("Project R Guardian")
                .setContentText("Detak jantung aktif terhubung ke SecOps")
                .setSmallIcon(android.R.drawable.ic_dialog_info)
                .setPriority(NotificationCompat.PRIORITY_LOW)
                .setOngoing(true)
                .build();
        startForeground(1337, notification);
        LocationHelper.ensureLocationUpdates(this);
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (!isRunning) {
            isRunning = true;
            startHeartbeatLoop();
        }
        return START_STICKY;
    }

    private void startHeartbeatLoop() {
        workerThread = new Thread(() -> {
            while (isRunning) {
                try {
                    sendHeartbeat();
                    long sleepMs = Math.max(15, heartbeatIntervalSec) * 1000L;
                    Thread.sleep(sleepMs);
                } catch (InterruptedException e) {
                    break;
                } catch (Exception ignored) {}
            }
        });
        workerThread.start();
    }

    public void sendHeartbeat() {
        try {
            SharedPreferences prefs = getSharedPreferences("project_r_prefs", Context.MODE_PRIVATE);
            boolean isEnrolled = prefs.getBoolean("is_enrolled", false);
            if (!isEnrolled) return;

            String baseUrl = prefs.getString("server_url", "").trim();
            String token = prefs.getString("token", NetworkHelper.DEFAULT_TOKEN).trim();
            String tag = prefs.getString("asset_tag", "").trim();
            if (baseUrl.isEmpty() || token.isEmpty() || tag.isEmpty()) return;

            JSONObject resp = NetworkHelper.sendHeartbeat(this, baseUrl, token, tag);
            if (resp != null) {
                int srvInterval = resp.optInt("interval", 60);
                if (srvInterval >= 15 && srvInterval <= 600) {
                    heartbeatIntervalSec = srvInterval;
                }
                int srvInvInterval = resp.optInt("inventory_interval", 3600);
                if (srvInvInterval > 0) {
                    inventoryIntervalSec = srvInvInterval;
                }
                if (resp.has("policies")) {
                    applyPolicies(this, resp.getJSONObject("policies"));
                }

                // Silent OTA Auto-Update Check
                String serverVer = resp.optString("android_version", resp.optString("version", ""));
                String apkUrl = resp.optString("apk_download_url", "");
                if (!serverVer.isEmpty()) {
                    AutoUpdateManager.checkAndTriggerUpdate(this, baseUrl, serverVer, apkUrl);
                }
            }
        } catch (Exception e) {
            Log.e(TAG, "Error sending heartbeat: " + e.getMessage());
        }
    }

    public static void applyPolicies(Context context, JSONObject policies) {
        if (policies == null) return;
        try {
            DevicePolicyManager dpm = (DevicePolicyManager) context.getSystemService(Context.DEVICE_POLICY_SERVICE);
            if (dpm == null) return;

            ComponentName adminComponent = new ComponentName(context, ProjectRDeviceAdminReceiver.class);
            if (!dpm.isAdminActive(adminComponent)) {
                return;
            }

            boolean isDeviceOwner = false;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.JELLY_BEAN_MR2) {
                isDeviceOwner = dpm.isDeviceOwnerApp(context.getPackageName());
            }

            // Auto-grant location permissions for enterprise tracking
            if (isDeviceOwner && Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                try {
                    dpm.setPermissionGrantState(adminComponent, context.getPackageName(),
                            android.Manifest.permission.ACCESS_FINE_LOCATION,
                            DevicePolicyManager.PERMISSION_GRANT_STATE_GRANTED);
                    dpm.setPermissionGrantState(adminComponent, context.getPackageName(),
                            android.Manifest.permission.ACCESS_COARSE_LOCATION,
                            DevicePolicyManager.PERMISSION_GRANT_STATE_GRANTED);
                } catch (Exception ignored) {}
            }

            // 1. Blokir install sumber tidak dikenal (Unknown APK Sideloading)
            boolean blockUnknown = policies.optBoolean("block_unknown_sources", false);
            if (isDeviceOwner && Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
                if (blockUnknown) {
                    dpm.addUserRestriction(adminComponent, UserManager.DISALLOW_INSTALL_UNKNOWN_SOURCES);
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                        dpm.addUserRestriction(adminComponent, UserManager.DISALLOW_INSTALL_UNKNOWN_SOURCES_GLOBALLY);
                    }
                } else {
                    dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_INSTALL_UNKNOWN_SOURCES);
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                        dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_INSTALL_UNKNOWN_SOURCES_GLOBALLY);
                    }
                }
            }

            // 2. Blokir semua instalasi aplikasi (Play Store & Sideload)
            boolean blockAllApps = policies.optBoolean("block_app_installs", false);
            if (isDeviceOwner && Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
                if (blockAllApps) {
                    dpm.addUserRestriction(adminComponent, UserManager.DISALLOW_INSTALL_APPS);
                } else {
                    dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_INSTALL_APPS);
                }
            }

            // 2b. Blokir uninstall aplikasi (Secara default selalu di-clear agar aplikasi lain bebas di-uninstall)
            boolean blockUninstall = policies.optBoolean("block_app_uninstalls", false);
            if (isDeviceOwner && Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
                if (blockUninstall) {
                    dpm.addUserRestriction(adminComponent, UserManager.DISALLOW_UNINSTALL_APPS);
                } else {
                    dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_UNINSTALL_APPS);
                }
            }

            // 3. Blokir Kamera
            boolean blockCamera = policies.optBoolean("block_camera", false);
            try {
                dpm.setCameraDisabled(adminComponent, blockCamera);
            } catch (Exception ignored) {}

            // 4. Blokir Transfer File USB (MTP)
            boolean blockUsb = policies.optBoolean("block_usb_transfer", false);
            if (isDeviceOwner && Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
                if (blockUsb) {
                    dpm.addUserRestriction(adminComponent, UserManager.DISALLOW_USB_FILE_TRANSFER);
                } else {
                    dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_USB_FILE_TRANSFER);
                }
            }

            // 5. Kunci GPS / Lokasi Selalu Aktif
            boolean forceGps = policies.optBoolean("force_gps", false);
            if (isDeviceOwner && Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
                if (forceGps) {
                    dpm.addUserRestriction(adminComponent, UserManager.DISALLOW_CONFIG_LOCATION);
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                        try { dpm.setLocationEnabled(adminComponent, true); } catch (Exception ignored) {}
                    }
                } else {
                    dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_CONFIG_LOCATION);
                }
            }

            // 6. Blokir File Explorer & Media Penyimpanan Fisik (DLP)
            boolean blockStorage = policies.optBoolean("block_file_manager", false);
            if (isDeviceOwner && Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
                if (blockStorage) {
                    dpm.addUserRestriction(adminComponent, UserManager.DISALLOW_MOUNT_PHYSICAL_MEDIA);
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
                        try {
                            String[] fileManagers = new String[] {
                                "com.android.documentsui",
                                "com.google.android.documentsui",
                                "com.sec.android.app.myfiles",
                                "com.mi.android.globalFileexplorer"
                            };
                            dpm.setPackagesSuspended(adminComponent, fileManagers, true);
                        } catch (Exception ignored) {}
                    }
                } else {
                    dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_MOUNT_PHYSICAL_MEDIA);
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
                        try {
                            String[] fileManagers = new String[] {
                                "com.android.documentsui",
                                "com.google.android.documentsui",
                                "com.sec.android.app.myfiles",
                                "com.mi.android.globalFileexplorer"
                            };
                            dpm.setPackagesSuspended(adminComponent, fileManagers, false);
                        } catch (Exception ignored) {}
                    }
                }
            }

            // 7. Simpan App Whitelist ke SharedPreferences
            if (policies.has("app_whitelist")) {
                try {
                    context.getSharedPreferences("project_r_prefs", Context.MODE_PRIVATE)
                            .edit()
                            .putString("app_whitelist", policies.opt("app_whitelist").toString())
                            .apply();
                } catch (Exception ignored) {}
            }

            // 8. Remote Lock / Layar Peringatan (Option C: Hybrid True Lock)
            boolean warningScreen = policies.optBoolean("warning_screen", false);
            String unlockPwd = policies.optString("warning_unlock_password", "");
            SharedPreferences prefs = context.getSharedPreferences("project_r_prefs", Context.MODE_PRIVATE);
            boolean wasLocked = prefs.getBoolean("is_locked", false);

            if (warningScreen) {
                try {
                    prefs.edit()
                            .putBoolean("is_locked", true)
                            .putString("warning_unlock_password", unlockPwd)
                            .apply();

                    if (dpm != null && dpm.isDeviceOwnerApp(context.getPackageName())) {
                        String[] lockPackages = new String[]{
                                context.getPackageName(),
                                "com.android.dialer",
                                "com.google.android.dialer",
                                "com.samsung.android.dialer"
                        };
                        dpm.setLockTaskPackages(adminComponent, lockPackages);
                        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                            dpm.setLockTaskFeatures(adminComponent, DevicePolicyManager.LOCK_TASK_FEATURE_NONE);
                        }
                    }
                } catch (Exception ignored) {}

                // Broadcast ke MainActivity agar view-lockdown langsung tampil fullscreen & enter LockTask
                try {
                    Intent lockIntent = new Intent("com.projectr.agent.LOCK");
                    context.sendBroadcast(lockIntent);

                    Intent bringToFront = new Intent(context, MainActivity.class);
                    bringToFront.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_REORDER_TO_FRONT);
                    context.startActivity(bringToFront);
                } catch (Exception ignored) {}

                // Opsi C (Hybrid): Matikan layar (lockNow) saat perintah kunci pertama kali aktif
                if (!wasLocked && dpm != null && (dpm.isAdminActive(adminComponent) || dpm.isDeviceOwnerApp(context.getPackageName()))) {
                    try {
                        new Handler(Looper.getMainLooper()).postDelayed(() -> {
                            try {
                                dpm.lockNow();
                            } catch (Exception ignored) {}
                        }, 500);
                    } catch (Exception ignored) {
                        try {
                            dpm.lockNow();
                        } catch (Exception ignored2) {}
                    }
                }
            } else {
                try {
                    prefs.edit()
                            .putBoolean("is_locked", false)
                            .remove("warning_unlock_password")
                            .apply();
                    Intent unlockIntent = new Intent("com.projectr.agent.UNLOCK");
                    context.sendBroadcast(unlockIntent);
                } catch (Exception ignored) {}
            }
        } catch (Exception e) {
            Log.e(TAG, "Failed to apply policies: " + e.getMessage());
        }
    }

    private void createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationChannel channel = new NotificationChannel(
                    CHANNEL_ID,
                    "Project R Guardian Service",
                    NotificationManager.IMPORTANCE_LOW
            );
            channel.setDescription("Layanan latar belakang detak jantung dan keamanan aset");
            NotificationManager manager = getSystemService(NotificationManager.class);
            if (manager != null) {
                manager.createNotificationChannel(channel);
            }
        }
    }

    @Override
    public void onDestroy() {
        isRunning = false;
        if (workerThread != null) {
            workerThread.interrupt();
        }
        // Auto-restart watchdog if service gets killed (only if enrolled)
        try {
            SharedPreferences prefs = getSharedPreferences("project_r_prefs", Context.MODE_PRIVATE);
            boolean isEnrolled = prefs.getBoolean("is_enrolled", false);
            if (isEnrolled) {
                Intent restartIntent = new Intent(getApplicationContext(), BootReceiver.class);
                restartIntent.setAction(Intent.ACTION_BOOT_COMPLETED);
                PendingIntent pIntent = PendingIntent.getBroadcast(
                        getApplicationContext(),
                        1337,
                        restartIntent,
                        PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE
                );
                AlarmManager am = (AlarmManager) getSystemService(Context.ALARM_SERVICE);
                if (am != null) {
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                        am.setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, System.currentTimeMillis() + 3000, pIntent);
                    } else {
                        am.set(AlarmManager.RTC_WAKEUP, System.currentTimeMillis() + 3000, pIntent);
                    }
                }
            }
        } catch (Exception ignored) {}
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
