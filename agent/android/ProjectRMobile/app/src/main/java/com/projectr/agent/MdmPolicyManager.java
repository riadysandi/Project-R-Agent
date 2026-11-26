package com.projectr.agent;

import android.app.admin.DevicePolicyManager;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.location.LocationManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.UserManager;
import android.util.Log;

import org.json.JSONArray;
import org.json.JSONObject;

/**
 * Enforces Android MDM policies via DevicePolicyManager and produces
 * Desired-vs-Observed compliance evaluations for the backend and local UI.
 */
public class MdmPolicyManager {
    private static final String TAG = "ProjectRMDM";
    public static final String PREFS_NAME = "project_r_prefs";
    public static final String PREF_LAST_POLICIES = "last_policies_json";
    public static final String PREF_LAST_EVALUATIONS = "last_policy_evaluations";
    public static final String PREF_LAST_POLICY_STATUS = "last_policy_status";
    public static final String PREF_MDM_UNENROLLED = "mdm_unenrolled";
    public static final String PREF_MDM_UNENROLL_IN_PROGRESS = "mdm_unenroll_in_progress";
    public static final String ACTION_POLICY_UPDATED = "com.projectr.agent.POLICY_UPDATED";

    private static final String[] FILE_MANAGER_PACKAGES = new String[] {
            "com.android.documentsui",
            "com.google.android.documentsui",
            "com.sec.android.app.myfiles",
            "com.mi.android.globalFileexplorer"
    };

    private MdmPolicyManager() {}

    public static boolean isAdminActive(DevicePolicyManager dpm, ComponentName admin) {
        if (dpm == null || admin == null) return false;
        try {
            return dpm.isAdminActive(admin);
        } catch (Exception e) {
            Log.w(TAG, "Failed checking isAdminActive: " + e.getMessage());
            return false;
        }
    }

    public static boolean isDeviceOwner(DevicePolicyManager dpm, String packageName) {
        if (dpm == null || packageName == null) return false;
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.JELLY_BEAN_MR2) return false;
        try {
            return dpm.isDeviceOwnerApp(packageName);
        } catch (Exception e) {
            Log.w(TAG, "Failed checking isDeviceOwnerApp: " + e.getMessage());
            return false;
        }
    }

    public static boolean isProfileOwner(DevicePolicyManager dpm, String packageName) {
        if (dpm == null || packageName == null) return false;
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.LOLLIPOP) return false;
        try {
            return dpm.isProfileOwnerApp(packageName);
        } catch (Exception e) {
            Log.w(TAG, "Failed checking isProfileOwnerApp: " + e.getMessage());
            return false;
        }
    }

    public static void applyCachedPolicies(Context context) {
        try {
            SharedPreferences prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
            if (prefs.getBoolean(PREF_MDM_UNENROLLED, false)) {
                DevicePolicyManager dpm = (DevicePolicyManager) context.getSystemService(Context.DEVICE_POLICY_SERVICE);
                ComponentName admin = new ComponentName(context, ProjectRDeviceAdminReceiver.class);
                if (!isAdminActive(dpm, admin) && !isDeviceOwner(dpm, context.getPackageName())) {
                    return;
                }
            }
            String raw = prefs.getString(PREF_LAST_POLICIES, "");
            if (raw != null && !raw.trim().isEmpty()) {
                applyAndEvaluatePolicies(context, new JSONObject(raw));
            }
        } catch (Exception e) {
            Log.e(TAG, "Failed to apply cached policies: " + e.getMessage(), e);
        }
    }

    /**
     * Executes remote unenroll requested by the web portal:
     * restores all restrictions, clears Device Owner and Device Admin privileges,
     * clears cached MDM policies, unlocks any warning screen, and notifies the UI.
     */
    public static boolean performRemoteUnenroll(Context context) {
        if (context == null) return false;
        Log.i(TAG, "Memproses remote_unenroll: memulihkan pembatasan dan melepas hak MDM...");

        DevicePolicyManager dpm = (DevicePolicyManager) context.getSystemService(Context.DEVICE_POLICY_SERVICE);
        ComponentName adminComponent = new ComponentName(context, ProjectRDeviceAdminReceiver.class);
        String pkg = context.getPackageName();

        SharedPreferences prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
        prefs.edit().putBoolean(PREF_MDM_UNENROLL_IN_PROGRESS, true).apply();

        // 1. Kembalikan seluruh pembatasan sistem dan buka blokir uninstall
        ProjectRDeviceAdminReceiver.restoreAllRestrictions(context);

        // 2. Lepas status Device Owner jika aktif
        if (dpm != null && isDeviceOwner(dpm, pkg) && Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
            try {
                dpm.clearDeviceOwnerApp(pkg);
                Log.i(TAG, "clearDeviceOwnerApp(" + pkg + ") berhasil dipanggil.");
            } catch (Exception e) {
                Log.e(TAG, "Gagal clearDeviceOwnerApp: " + e.getMessage(), e);
            }
        }

        // 3. Lepas status Device Admin jika masih aktif (removeActiveAdmin bersifat asinkron)
        if (dpm != null && isAdminActive(dpm, adminComponent)) {
            try {
                dpm.removeActiveAdmin(adminComponent);
                Log.i(TAG, "removeActiveAdmin berhasil dipanggil.");
            } catch (Exception e) {
                Log.e(TAG, "Gagal removeActiveAdmin: " + e.getMessage(), e);
            }
        }

        // 4. Verifikasi status terbaru dengan retry polling hingga 500ms karena removeActiveAdmin asinkron
        boolean stillOwner = isDeviceOwner(dpm, pkg);
        boolean stillAdmin = isAdminActive(dpm, adminComponent);
        for (int attempt = 0; attempt < 10 && (stillOwner || stillAdmin); attempt++) {
            try {
                Thread.sleep(50L);
            } catch (InterruptedException ie) {
                Thread.currentThread().interrupt();
                break;
            }
            stillOwner = isDeviceOwner(dpm, pkg);
            stillAdmin = isAdminActive(dpm, adminComponent);
        }
        boolean success = !stillOwner && !stillAdmin;

        if (!success) {
            Log.w(TAG, "Verifikasi remote_unenroll belum sepenuhnya bersih setelah 500ms: isOwner=" + stillOwner + ", isAdmin=" + stillAdmin);
        } else {
            Log.i(TAG, "Verifikasi remote_unenroll sukses: Device Owner=false, Device Admin=false.");
        }

        // 5. Bersihkan cache kebijakan & kunci layar agar tidak diterapkan ulang
        boolean wasLocked = prefs.getBoolean("is_locked", false);
        prefs.edit()
                .remove(PREF_LAST_POLICIES)
                .remove(PREF_LAST_EVALUATIONS)
                .remove(PREF_LAST_POLICY_STATUS)
                .remove("app_whitelist")
                .remove("warning_unlock_password")
                .putBoolean("is_locked", false)
                .putBoolean(PREF_MDM_UNENROLLED, success)
                .putBoolean(PREF_MDM_UNENROLL_IN_PROGRESS, !success)
                .apply();

        // 6. Beritahu MainActivity / WebView untuk melepas layar kunci dan memperbarui status UI
        try {
            if (wasLocked) {
                Intent unlockIntent = new Intent("com.projectr.agent.UNLOCK");
                unlockIntent.setPackage(pkg);
                context.sendBroadcast(unlockIntent);
            }
            Intent policyUpdatedIntent = new Intent(ACTION_POLICY_UPDATED);
            policyUpdatedIntent.setPackage(pkg);
            context.sendBroadcast(policyUpdatedIntent);
        } catch (Exception e) {
            Log.w(TAG, "Broadcast setelah remote_unenroll gagal: " + e.getMessage());
        }

        return success;
    }

    /**
     * Inspects server pending_actions array and executes supported remote actions (e.g., remote_unenroll).
     * Returns true if remote_unenroll completed so caller can send remote_unenroll_ack immediately.
     */
    public static boolean handlePendingActions(Context context, JSONArray pendingActions) {
        if (context == null || pendingActions == null || pendingActions.length() == 0) {
            return false;
        }
        boolean unenrolled = false;
        for (int i = 0; i < pendingActions.length(); i++) {
            JSONObject action = pendingActions.optJSONObject(i);
            if (action == null) continue;
            String type = action.optString("type", "").trim();
            if ("remote_unenroll".equalsIgnoreCase(type)) {
                Log.i(TAG, "Menerima pending action remote_unenroll (id=" + action.optString("id", "-") + ")");
                if (performRemoteUnenroll(context)) {
                    unenrolled = true;
                }
            }
        }
        return unenrolled;
    }

    public static boolean isMdmUnenrolledAndUnprivileged(Context context) {
        if (context == null) return false;
        SharedPreferences prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
        if (!prefs.getBoolean(PREF_MDM_UNENROLLED, false)) {
            return false;
        }
        DevicePolicyManager dpm = (DevicePolicyManager) context.getSystemService(Context.DEVICE_POLICY_SERVICE);
        ComponentName admin = new ComponentName(context, ProjectRDeviceAdminReceiver.class);
        boolean stillPrivileged = isAdminActive(dpm, admin) || isDeviceOwner(dpm, context.getPackageName());
        if (stillPrivileged) {
            prefs.edit().putBoolean(PREF_MDM_UNENROLLED, false).apply();
            return false;
        }
        return true;
    }

    /**
     * Applies all policies from the server payload, logs any enforcement errors,
     * verifies observed device state, persists evaluations, and broadcasts UI update.
     */
    public static JSONArray applyAndEvaluatePolicies(Context context, JSONObject policies) {
        if (policies == null) return new JSONArray();

        SharedPreferences prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
        prefs.edit().putString(PREF_LAST_POLICIES, policies.toString()).apply();

        DevicePolicyManager dpm = (DevicePolicyManager) context.getSystemService(Context.DEVICE_POLICY_SERVICE);
        ComponentName adminComponent = new ComponentName(context, ProjectRDeviceAdminReceiver.class);
        String pkg = context.getPackageName();

        boolean isAdmin = isAdminActive(dpm, adminComponent);
        boolean isDeviceOwner = isDeviceOwner(dpm, pkg);
        boolean isProfileOwner = isProfileOwner(dpm, pkg);
        boolean hasOwnerPrivilege = isDeviceOwner || isProfileOwner;

        if (!isAdmin) {
            Log.w(TAG, "Device Admin belum aktif; kebijakan MDM tidak dapat ditegakkan hingga perangkat diprovisioning.");
        } else if (!hasOwnerPrivilege) {
            Log.w(TAG, "Aplikasi hanya berstatus Device Admin (bukan Device Owner). Pada Android 10+ (targetSdk 35), setCameraDisabled dan UserRestrictions memerlukan Device Owner.");
        }

        // Auto-grant location permissions when Device Owner
        if (isDeviceOwner && Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            try {
                dpm.setPermissionGrantState(adminComponent, pkg,
                        android.Manifest.permission.ACCESS_FINE_LOCATION,
                        DevicePolicyManager.PERMISSION_GRANT_STATE_GRANTED);
                dpm.setPermissionGrantState(adminComponent, pkg,
                        android.Manifest.permission.ACCESS_COARSE_LOCATION,
                        DevicePolicyManager.PERMISSION_GRANT_STATE_GRANTED);
            } catch (Exception e) {
                Log.w(TAG, "Failed auto-granting location permissions: " + e.getMessage());
            }
        }

        // Enforce UserRestrictions (Device Owner / Profile Owner only)
        String unknownSourcesErr = applyUserRestriction(dpm, adminComponent, isDeviceOwner,
                UserManager.DISALLOW_INSTALL_UNKNOWN_SOURCES,
                policies.optBoolean("block_unknown_sources", false));
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            String globalUnknownErr = applyUserRestriction(dpm, adminComponent, isDeviceOwner,
                    UserManager.DISALLOW_INSTALL_UNKNOWN_SOURCES_GLOBALLY,
                    policies.optBoolean("block_unknown_sources", false));
            if (unknownSourcesErr == null) unknownSourcesErr = globalUnknownErr;
        }

        String installAppsErr = applyUserRestriction(dpm, adminComponent, isDeviceOwner,
                UserManager.DISALLOW_INSTALL_APPS,
                policies.optBoolean("block_app_installs", false));

        String uninstallAppsErr = applyUserRestriction(dpm, adminComponent, isDeviceOwner,
                UserManager.DISALLOW_UNINSTALL_APPS,
                policies.optBoolean("block_app_uninstalls", false));

        String usbTransferErr = applyUserRestriction(dpm, adminComponent, isDeviceOwner,
                UserManager.DISALLOW_USB_FILE_TRANSFER,
                policies.optBoolean("block_usb_transfer", false));

        boolean forceGps = policies.optBoolean("force_gps", false);
        String forceGpsErr = applyUserRestriction(dpm, adminComponent, isDeviceOwner,
                UserManager.DISALLOW_CONFIG_LOCATION,
                forceGps);
        if (forceGps && isDeviceOwner && Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            try {
                dpm.setLocationEnabled(adminComponent, true);
            } catch (Exception e) {
                Log.w(TAG, "setLocationEnabled(true) failed: " + e.getMessage());
                if (forceGpsErr == null) forceGpsErr = e.getMessage();
            }
        }

        // Enforce Camera Block (block_camera)
        boolean blockCamera = policies.optBoolean("block_camera", false);
        String cameraApplyError = applyCameraPolicy(dpm, adminComponent, isAdmin, isDeviceOwner, hasOwnerPrivilege, blockCamera);

        // Enforce File Manager / Physical Media DLP
        boolean blockStorage = policies.optBoolean("block_file_manager", false);
        applyUserRestriction(dpm, adminComponent, isDeviceOwner,
                UserManager.DISALLOW_MOUNT_PHYSICAL_MEDIA,
                blockStorage);
        if (isDeviceOwner && Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
            try {
                dpm.setPackagesSuspended(adminComponent, FILE_MANAGER_PACKAGES, blockStorage);
            } catch (Exception e) {
                Log.w(TAG, "setPackagesSuspended(" + blockStorage + ") failed: " + e.getMessage());
            }
        }

        // Save App Whitelist
        if (policies.has("app_whitelist")) {
            try {
                prefs.edit()
                        .putString("app_whitelist", policies.opt("app_whitelist").toString())
                        .apply();
            } catch (Exception e) {
                Log.w(TAG, "Failed saving app_whitelist: " + e.getMessage());
            }
        }

        // Remote Lock / Warning Screen
        applyWarningScreenPolicy(context, dpm, adminComponent, isAdmin, isDeviceOwner, policies, prefs);

        // Build & persist evaluations from real observed OS state
        JSONArray evaluations = evaluateDeviceState(
                context, dpm, adminComponent, isAdmin, isDeviceOwner, hasOwnerPrivilege,
                policies, cameraApplyError, unknownSourcesErr, installAppsErr,
                uninstallAppsErr, usbTransferErr, forceGpsErr
        );

        prefs.edit().putString(PREF_LAST_EVALUATIONS, evaluations.toString()).apply();

        try {
            Intent policyUpdatedIntent = new Intent(ACTION_POLICY_UPDATED);
            policyUpdatedIntent.setPackage(pkg);
            context.sendBroadcast(policyUpdatedIntent);
        } catch (Exception ignored) {}

        return evaluations;
    }

    private static String applyCameraPolicy(
            DevicePolicyManager dpm,
            ComponentName adminComponent,
            boolean isAdmin,
            boolean isDeviceOwner,
            boolean hasOwnerPrivilege,
            boolean blockCamera
    ) {
        if (dpm == null) {
            return "DevicePolicyManager unavailable";
        }
        if (!isAdmin) {
            Log.w(TAG, "block_camera=" + blockCamera + " dilewati: Device Admin belum aktif.");
            return "Device Admin belum aktif";
        }

        // On Android 10+ (API 29+) with targetSdk >= 29, setCameraDisabled requires Device Owner or Profile Owner.
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q && !hasOwnerPrivilege) {
            if (!blockCamera) {
                boolean currentlyDisabled = false;
                try {
                    currentlyDisabled = dpm.getCameraDisabled(null);
                } catch (Exception ignored) {}
                if (!currentlyDisabled) {
                    return null;
                }
            }
            String reason = "Android " + Build.VERSION.RELEASE + " (API " + Build.VERSION.SDK_INT
                    + ") mewajibkan Device Owner untuk setCameraDisabled; status saat ini hanya Device Admin.";
            Log.e(TAG, "Gagal menerapkan block_camera=" + blockCamera + ": " + reason);
            return reason;
        }

        try {
            dpm.setCameraDisabled(adminComponent, blockCamera);
            boolean actual = dpm.getCameraDisabled(adminComponent);
            Log.i(TAG, "dpm.setCameraDisabled(" + blockCamera + ") -> observed getCameraDisabled=" + actual);

            // Prevent Android 12+ Quick Settings camera privacy toggle from conflicting when Device Owner
            if (isDeviceOwner && Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                try {
                    if (blockCamera) {
                        dpm.addUserRestriction(adminComponent, UserManager.DISALLOW_CAMERA_TOGGLE);
                    } else {
                        dpm.clearUserRestriction(adminComponent, UserManager.DISALLOW_CAMERA_TOGGLE);
                    }
                } catch (Exception toggleEx) {
                    Log.w(TAG, "DISALLOW_CAMERA_TOGGLE update warning: " + toggleEx.getMessage());
                }
            }

            if (actual != blockCamera) {
                String mismatch = "setCameraDisabled(" + blockCamera + ") dipanggil tetapi getCameraDisabled mengembalikan " + actual;
                Log.e(TAG, mismatch);
                return mismatch;
            }
            return null;
        } catch (SecurityException se) {
            String msg = "SecurityException pada setCameraDisabled(" + blockCamera + "): " + se.getMessage();
            Log.e(TAG, msg, se);
            return msg;
        } catch (Exception e) {
            String msg = e.getClass().getSimpleName() + " pada setCameraDisabled(" + blockCamera + "): " + e.getMessage();
            Log.e(TAG, msg, e);
            return msg;
        }
    }

    private static String applyUserRestriction(
            DevicePolicyManager dpm,
            ComponentName adminComponent,
            boolean isDeviceOwner,
            String restriction,
            boolean enable
    ) {
        if (!isDeviceOwner || dpm == null || Build.VERSION.SDK_INT < Build.VERSION_CODES.LOLLIPOP) {
            return null;
        }
        try {
            if (enable) {
                dpm.addUserRestriction(adminComponent, restriction);
            } else {
                dpm.clearUserRestriction(adminComponent, restriction);
            }
            return null;
        } catch (Exception e) {
            Log.e(TAG, "UserRestriction " + restriction + "=" + enable + " failed: " + e.getMessage(), e);
            return e.getClass().getSimpleName() + ": " + e.getMessage();
        }
    }

    private static void applyWarningScreenPolicy(
            Context context,
            DevicePolicyManager dpm,
            ComponentName adminComponent,
            boolean isAdmin,
            boolean isDeviceOwner,
            JSONObject policies,
            SharedPreferences prefs
    ) {
        boolean warningScreen = policies.optBoolean("warning_screen", false);
        String unlockPwd = policies.optString("warning_unlock_password", "");
        boolean wasLocked = prefs.getBoolean("is_locked", false);

        if (warningScreen) {
            try {
                prefs.edit()
                        .putBoolean("is_locked", true)
                        .putString("warning_unlock_password", unlockPwd)
                        .apply();

                if (dpm != null && isDeviceOwner) {
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
            } catch (Exception e) {
                Log.w(TAG, "Warning lock setup error: " + e.getMessage());
            }

            try {
                Intent lockIntent = new Intent("com.projectr.agent.LOCK");
                lockIntent.setPackage(context.getPackageName());
                context.sendBroadcast(lockIntent);

                Intent bringToFront = new Intent(context, MainActivity.class);
                bringToFront.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_REORDER_TO_FRONT);
                context.startActivity(bringToFront);
            } catch (Exception e) {
                Log.w(TAG, "Failed launching warning lock UI: " + e.getMessage());
            }

            if (!wasLocked && dpm != null && (isAdmin || isDeviceOwner)) {
                try {
                    new Handler(Looper.getMainLooper()).postDelayed(() -> {
                        try {
                            dpm.lockNow();
                        } catch (Exception e) {
                            Log.w(TAG, "Delayed dpm.lockNow failed: " + e.getMessage());
                        }
                    }, 500);
                } catch (Exception e) {
                    try {
                        dpm.lockNow();
                    } catch (Exception ignored) {}
                }
            }
        } else {
            try {
                prefs.edit()
                        .putBoolean("is_locked", false)
                        .remove("warning_unlock_password")
                        .apply();
                Intent unlockIntent = new Intent("com.projectr.agent.UNLOCK");
                unlockIntent.setPackage(context.getPackageName());
                context.sendBroadcast(unlockIntent);
            } catch (Exception e) {
                Log.w(TAG, "Failed unlocking warning screen: " + e.getMessage());
            }
        }
    }

    /**
     * Evaluates the live observed device state against the latest cached desired policies.
     * Safe to call right before sending a heartbeat.
     */
    public static JSONArray buildEvaluations(Context context) {
        try {
            SharedPreferences prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
            String raw = prefs.getString(PREF_LAST_POLICIES, "");
            if (raw == null || raw.trim().isEmpty()) {
                return new JSONArray();
            }
            JSONObject policies = new JSONObject(raw);
            DevicePolicyManager dpm = (DevicePolicyManager) context.getSystemService(Context.DEVICE_POLICY_SERVICE);
            ComponentName adminComponent = new ComponentName(context, ProjectRDeviceAdminReceiver.class);
            String pkg = context.getPackageName();

            boolean isAdmin = isAdminActive(dpm, adminComponent);
            boolean isDeviceOwner = isDeviceOwner(dpm, pkg);
            boolean isProfileOwner = isProfileOwner(dpm, pkg);
            boolean hasOwnerPrivilege = isDeviceOwner || isProfileOwner;

            JSONArray evaluations = evaluateDeviceState(
                    context, dpm, adminComponent, isAdmin, isDeviceOwner, hasOwnerPrivilege,
                    policies, null, null, null, null, null, null
            );
            prefs.edit().putString(PREF_LAST_EVALUATIONS, evaluations.toString()).apply();
            return evaluations;
        } catch (Exception e) {
            Log.e(TAG, "Error building evaluations: " + e.getMessage(), e);
            return new JSONArray();
        }
    }

    private static JSONArray evaluateDeviceState(
            Context context,
            DevicePolicyManager dpm,
            ComponentName adminComponent,
            boolean isAdmin,
            boolean isDeviceOwner,
            boolean hasOwnerPrivilege,
            JSONObject policies,
            String cameraApplyError,
            String unknownSourcesErr,
            String installAppsErr,
            String uninstallAppsErr,
            String usbTransferErr,
            String forceGpsErr
    ) {
        JSONArray list = new JSONArray();

        String privilegeLabel = isDeviceOwner
                ? "Device Owner"
                : (hasOwnerPrivilege ? "Profile Owner" : (isAdmin ? "Device Admin only" : "Unprivileged (Admin inactive)"));

        Bundle restrictions = null;
        if (dpm != null && hasOwnerPrivilege && Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
            try {
                restrictions = dpm.getUserRestrictions(adminComponent);
            } catch (Exception e) {
                Log.w(TAG, "getUserRestrictions failed: " + e.getMessage());
            }
        }

        // 1. block_camera
        boolean blockCameraDesired = policies.optBoolean("block_camera", false);
        boolean observedCameraDisabled = false;
        String cameraReadError = null;
        if (dpm != null) {
            try {
                observedCameraDisabled = isAdmin
                        ? dpm.getCameraDisabled(adminComponent)
                        : dpm.getCameraDisabled(null);
                if (!observedCameraDisabled) {
                    observedCameraDisabled = dpm.getCameraDisabled(null);
                }
            } catch (Exception e) {
                cameraReadError = e.getMessage();
                Log.w(TAG, "getCameraDisabled failed: " + cameraReadError);
            }
        }

        String camDesiredStr = blockCameraDesired ? "blocked" : "allowed";
        String camObservedStr;
        boolean camCompliant;
        String camEvidence;

        if (cameraApplyError != null && blockCameraDesired && !observedCameraDisabled) {
            camObservedStr = "allowed";
            camCompliant = false;
            camEvidence = cameraApplyError + " (" + privilegeLabel + ")";
        } else if (cameraReadError != null) {
            camObservedStr = "error";
            camCompliant = false;
            camEvidence = "getCameraDisabled error: " + cameraReadError + " (" + privilegeLabel + ")";
        } else {
            camObservedStr = observedCameraDisabled ? "blocked" : "allowed";
            camCompliant = (observedCameraDisabled == blockCameraDesired);
            if (camCompliant) {
                camEvidence = "getCameraDisabled=" + observedCameraDisabled + " (" + privilegeLabel + ")";
            } else if (blockCameraDesired && !hasOwnerPrivilege && Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                camEvidence = "getCameraDisabled=false; Android " + Build.VERSION.RELEASE
                        + " memerlukan Device Owner untuk setCameraDisabled (status: " + privilegeLabel + ")";
            } else {
                camEvidence = "getCameraDisabled=" + observedCameraDisabled
                        + " != target " + blockCameraDesired + " (" + privilegeLabel + ")";
            }
        }

        addEvaluation(list, "block_camera", camDesiredStr, camObservedStr, camCompliant, camEvidence);

        // Save detailed local policy status for WebView UI
        saveLocalStatusSummary(context, privilegeLabel, isAdmin, isDeviceOwner,
                camDesiredStr, camObservedStr, camCompliant, camEvidence);

        // 2. block_unknown_sources
        boolean blockUnknown = policies.optBoolean("block_unknown_sources", false);
        boolean unknownObserved = restrictions != null && (
                restrictions.getBoolean(UserManager.DISALLOW_INSTALL_UNKNOWN_SOURCES, false)
                || (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q
                    && restrictions.getBoolean(UserManager.DISALLOW_INSTALL_UNKNOWN_SOURCES_GLOBALLY, false)));
        addRestrictionEvaluation(list, "block_unknown_sources", blockUnknown, unknownObserved,
                isDeviceOwner, privilegeLabel, unknownSourcesErr,
                "DISALLOW_INSTALL_UNKNOWN_SOURCES=" + unknownObserved);

        // 3. block_app_installs
        boolean blockInstalls = policies.optBoolean("block_app_installs", false);
        boolean installsObserved = restrictions != null
                && restrictions.getBoolean(UserManager.DISALLOW_INSTALL_APPS, false);
        addRestrictionEvaluation(list, "block_app_installs", blockInstalls, installsObserved,
                isDeviceOwner, privilegeLabel, installAppsErr,
                "DISALLOW_INSTALL_APPS=" + installsObserved);

        // 4. block_app_uninstalls
        boolean blockUninstalls = policies.optBoolean("block_app_uninstalls", false);
        boolean uninstallsObserved = restrictions != null
                && restrictions.getBoolean(UserManager.DISALLOW_UNINSTALL_APPS, false);
        addRestrictionEvaluation(list, "block_app_uninstalls", blockUninstalls, uninstallsObserved,
                isDeviceOwner, privilegeLabel, uninstallAppsErr,
                "DISALLOW_UNINSTALL_APPS=" + uninstallsObserved);

        // 5. block_usb_transfer
        boolean blockUsb = policies.optBoolean("block_usb_transfer", false);
        boolean usbObserved = restrictions != null
                && restrictions.getBoolean(UserManager.DISALLOW_USB_FILE_TRANSFER, false);
        addRestrictionEvaluation(list, "block_usb_transfer", blockUsb, usbObserved,
                isDeviceOwner, privilegeLabel, usbTransferErr,
                "DISALLOW_USB_FILE_TRANSFER=" + usbObserved);

        // 6. force_gps
        boolean forceGps = policies.optBoolean("force_gps", false);
        boolean gpsConfigLocked = restrictions != null
                && restrictions.getBoolean(UserManager.DISALLOW_CONFIG_LOCATION, false);
        boolean gpsEnabled = isLocationEnabled(context);
        boolean gpsObserved = forceGps ? (gpsConfigLocked && gpsEnabled) : gpsConfigLocked;
        String gpsEvidence = "DISALLOW_CONFIG_LOCATION=" + gpsConfigLocked + ", locationEnabled=" + gpsEnabled;
        addRestrictionEvaluation(list, "force_gps", forceGps, gpsObserved,
                isDeviceOwner, privilegeLabel, forceGpsErr, gpsEvidence);

        return list;
    }

    private static void addRestrictionEvaluation(
            JSONArray list,
            String controlKey,
            boolean desired,
            boolean observed,
            boolean isDeviceOwner,
            String privilegeLabel,
            String applyError,
            String rawEvidence
    ) {
        String desiredStr = desired ? "blocked" : "allowed";
        String observedStr = observed ? "blocked" : "allowed";
        if ("force_gps".equals(controlKey)) {
            desiredStr = desired ? "enforced" : "optional";
            observedStr = observed ? "enforced" : "optional";
        }

        boolean compliant;
        String evidence;
        if (applyError != null) {
            observedStr = "error";
            compliant = false;
            evidence = applyError + " (" + privilegeLabel + ")";
        } else if (!isDeviceOwner && desired) {
            compliant = false;
            evidence = rawEvidence + "; memerlukan Device Owner (status: " + privilegeLabel + ")";
        } else {
            compliant = (desired == observed);
            evidence = rawEvidence + " (" + privilegeLabel + ")";
        }
        addEvaluation(list, controlKey, desiredStr, observedStr, compliant, evidence);
    }

    private static void addEvaluation(
            JSONArray list,
            String control,
            String desired,
            String observed,
            boolean compliant,
            String evidence
    ) {
        try {
            JSONObject item = new JSONObject();
            item.put("control", control);
            item.put("desired", desired);
            item.put("observed", observed);
            item.put("compliant", compliant);
            item.put("evidence", evidence);
            list.put(item);
        } catch (Exception ignored) {}
    }

    private static boolean isLocationEnabled(Context context) {
        try {
            LocationManager lm = (LocationManager) context.getSystemService(Context.LOCATION_SERVICE);
            if (lm == null) return false;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                return lm.isLocationEnabled();
            }
            return lm.isProviderEnabled(LocationManager.GPS_PROVIDER)
                    || lm.isProviderEnabled(LocationManager.NETWORK_PROVIDER);
        } catch (Exception e) {
            return false;
        }
    }

    private static void saveLocalStatusSummary(
            Context context,
            String privilegeLabel,
            boolean isAdmin,
            boolean isDeviceOwner,
            String camDesired,
            String camObserved,
            boolean camCompliant,
            String camEvidence
    ) {
        try {
            JSONObject summary = new JSONObject();
            summary.put("privilegeMode", privilegeLabel);
            summary.put("isAdminActive", isAdmin);
            summary.put("isOwnerActive", isDeviceOwner);
            summary.put("cameraDesired", camDesired);
            summary.put("cameraObserved", camObserved);
            summary.put("cameraCompliant", camCompliant);
            summary.put("cameraEvidence", camEvidence);
            summary.put("evaluatedAt", System.currentTimeMillis());

            context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
                    .edit()
                    .putString(PREF_LAST_POLICY_STATUS, summary.toString())
                    .apply();
        } catch (Exception ignored) {}
    }

    public static JSONObject getPolicyStatusJson(Context context) {
        try {
            SharedPreferences prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
            String raw = prefs.getString(PREF_LAST_POLICY_STATUS, "");
            if (raw != null && !raw.trim().isEmpty()) {
                return new JSONObject(raw);
            }
        } catch (Exception ignored) {}

        // Fallback live privilege check before first policy sync
        JSONObject fallback = new JSONObject();
        try {
            DevicePolicyManager dpm = (DevicePolicyManager) context.getSystemService(Context.DEVICE_POLICY_SERVICE);
            ComponentName admin = new ComponentName(context, ProjectRDeviceAdminReceiver.class);
            boolean isAdmin = isAdminActive(dpm, admin);
            boolean isOwner = isDeviceOwner(dpm, context.getPackageName());
            fallback.put("privilegeMode", isOwner ? "Device Owner" : (isAdmin ? "Device Admin only" : "Aplikasi Biasa (MDM Tidak Aktif)"));
            fallback.put("isAdminActive", isAdmin);
            fallback.put("isOwnerActive", isOwner);
            fallback.put("cameraDesired", "unknown");
            boolean disabled = false;
            try {
                if (dpm != null) disabled = dpm.getCameraDisabled(null);
            } catch (Exception ignored) {}
            fallback.put("cameraObserved", disabled ? "blocked" : "allowed");
            fallback.put("cameraCompliant", true);
            fallback.put("cameraEvidence", (!isAdmin && !isOwner)
                    ? "Mode aplikasi biasa (non-MDM). Perangkat bebas dari pembatasan dan siap di-uninstall."
                    : "Menunggu sinkronisasi kebijakan pertama dari server.");
        } catch (Exception ignored) {}
        return fallback;
    }
}
