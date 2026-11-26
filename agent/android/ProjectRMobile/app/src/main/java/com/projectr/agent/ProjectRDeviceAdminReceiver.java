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
        boolean isDeviceOwner = false;
        try {
            DevicePolicyManager dpm = (DevicePolicyManager) context.getSystemService(Context.DEVICE_POLICY_SERVICE);
            ComponentName adminComponent = new ComponentName(context, ProjectRDeviceAdminReceiver.class);
            isDeviceOwner = dpm != null && Build.VERSION.SDK_INT >= Build.VERSION_CODES.JELLY_BEAN_MR2
                    && dpm.isDeviceOwnerApp(context.getPackageName());
            if (isDeviceOwner && Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
                dpm.setUninstallBlocked(adminComponent, context.getPackageName(), true);
                dpm.addUserRestriction(adminComponent, UserManager.DISALLOW_UNINSTALL_APPS);
            } else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                Log.w(TAG, "Device Admin diaktifkan tanpa status Device Owner. Kebijakan Android 10+ (seperti block_camera) memerlukan Device Owner.");
            }
        } catch (Exception e) {
            Log.e(TAG, "Error pada onEnabled: " + e.getMessage(), e);
        }

        if (isDeviceOwner) {
            Toast.makeText(context, "Project R: Device Owner aktif & Anti-Uninstall terkunci", Toast.LENGTH_SHORT).show();
        } else {
            Toast.makeText(context, "Project R: Device Admin aktif (Mode Terbatas — butuh Device Owner untuk blokir kamera)", Toast.LENGTH_LONG).show();
        }

        context.getSharedPreferences(MdmPolicyManager.PREFS_NAME, Context.MODE_PRIVATE)
                .edit()
                .putBoolean(MdmPolicyManager.PREF_MDM_UNENROLLED, false)
                .apply();

        MdmPolicyManager.applyCachedPolicies(context);
        startGuardian(context);
        HeartbeatService.triggerImmediateSync(context);
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

            // 1. Kembalikan User Restrictions, LockTask & Buka blokir uninstall
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
                try { dpm.setUninstallBlocked(adminComponent, context.getPackageName(), false); } catch (Exception ignored) {}
                try { dpm.setLockTaskPackages(adminComponent, new String[0]); } catch (Exception ignored) {}
                try { dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_INSTALL_UNKNOWN_SOURCES); } catch (Exception ignored) {}
                try { dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_INSTALL_APPS); } catch (Exception ignored) {}
                try { dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_UNINSTALL_APPS); } catch (Exception ignored) {}
                try { dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_USB_FILE_TRANSFER); } catch (Exception ignored) {}
                try { dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_CONFIG_LOCATION); } catch (Exception ignored) {}
                try { dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_MOUNT_PHYSICAL_MEDIA); } catch (Exception ignored) {}
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                    try { dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_INSTALL_UNKNOWN_SOURCES_GLOBALLY); } catch (Exception ignored) {}
                }
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                    try { dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_CAMERA_TOGGLE); } catch (Exception ignored) {}
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
            } catch (Exception e) {
                Log.w(TAG, "Gagal mengembalikan setCameraDisabled(false): " + e.getMessage());
            }

            Log.i(TAG, "Seluruh pembatasan MDM Android (APK, Kamera, USB) berhasil dinetralkan ke normal!");
        } catch (Exception e) {
            Log.e(TAG, "Gagal mengembalikan pembatasan: " + e.getMessage());
        }
    }
}
