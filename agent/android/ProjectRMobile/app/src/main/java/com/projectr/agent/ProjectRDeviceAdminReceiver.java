package com.projectr.agent;

import android.app.admin.DeviceAdminReceiver;
import android.app.admin.DevicePolicyManager;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.os.Build;
import android.os.UserManager;
import android.util.Log;
import android.widget.Toast;

public class ProjectRDeviceAdminReceiver extends DeviceAdminReceiver {
    private static final String TAG = "ProjectRAdminReceiver";

    @Override
    public void onEnabled(Context context, Intent intent) {
        super.onEnabled(context, intent);
        try {
            DevicePolicyManager dpm = (DevicePolicyManager) context.getSystemService(Context.DEVICE_POLICY_SERVICE);
            ComponentName adminComponent = new ComponentName(context, ProjectRDeviceAdminReceiver.class);
            if (dpm != null && Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP && dpm.isDeviceOwnerApp(context.getPackageName())) {
                dpm.setUninstallBlocked(adminComponent, context.getPackageName(), true);
                dpm.addUserRestriction(adminComponent, UserManager.DISALLOW_UNINSTALL_APPS);
            }
        } catch (Exception ignored) {}
        Toast.makeText(context, "Project R: Device Owner aktif & Anti-Uninstall terkunci", Toast.LENGTH_SHORT).show();
        startGuardian(context);
    }

    @Override
    public void onProfileProvisioningComplete(Context context, Intent intent) {
        super.onProfileProvisioningComplete(context, intent);
        startGuardian(context);
    }

    @Override
    public void onDisabled(Context context, Intent intent) {
        super.onDisabled(context, intent);
        Log.i(TAG, "Device Admin dinonaktifkan, mengembalikan seluruh pembatasan kebijakan ke normal...");
        restoreAllRestrictions(context);
    }

    private void startGuardian(Context context) {
        try {
            Intent serviceIntent = new Intent(context, HeartbeatService.class);
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                context.startForegroundService(serviceIntent);
            } else {
                context.startService(serviceIntent);
            }
        } catch (Exception ignored) {}
    }

    /**
     * Mengembalikan seluruh pembatasan sistem Android ke kondisi normal (seperti uninstaller Windows)
     */
    public static void restoreAllRestrictions(Context context) {
        try {
            DevicePolicyManager dpm = (DevicePolicyManager) context.getSystemService(Context.DEVICE_POLICY_SERVICE);
            if (dpm == null) return;

            ComponentName adminComponent = new ComponentName(context, ProjectRDeviceAdminReceiver.class);

            // 1. Kembalikan User Restrictions & Buka blokir uninstall
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
                try { dpm.setUninstallBlocked(adminComponent, context.getPackageName(), false); } catch (Exception ignored) {}
                try { dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_INSTALL_UNKNOWN_SOURCES); } catch (Exception ignored) {}
                try { dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_INSTALL_APPS); } catch (Exception ignored) {}
                try { dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_UNINSTALL_APPS); } catch (Exception ignored) {}
                try { dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_USB_FILE_TRANSFER); } catch (Exception ignored) {}
                try { dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_CONFIG_LOCATION); } catch (Exception ignored) {}
                try { dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_MOUNT_PHYSICAL_MEDIA); } catch (Exception ignored) {}
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                    try { dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_INSTALL_UNKNOWN_SOURCES_GLOBALLY); } catch (Exception ignored) {}
                }
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

            // 2. Kembalikan Sensor Kamera ke AKTIF
            try {
                dpm.setCameraDisabled(adminComponent, false);
            } catch (Exception ignored) {}

            Log.i(TAG, "Seluruh pembatasan MDM Android (APK, Kamera, USB) berhasil dinetralkan ke normal!");
        } catch (Exception e) {
            Log.e(TAG, "Gagal mengembalikan pembatasan: " + e.getMessage());
        }
    }
}
