package com.projectr.agent;

import android.app.admin.DevicePolicyManager;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.net.Uri;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.webkit.JavascriptInterface;
import org.json.JSONObject;

public class WebAppInterface {
    private final MainActivity activity;

    public WebAppInterface(MainActivity activity) {
        this.activity = activity;
    }

    @JavascriptInterface
    public String getAgentConfig() {
        try {
            JSONObject obj = new JSONObject();
            SharedPreferences prefs = activity.getSharedPreferences("project_r_prefs", Context.MODE_PRIVATE);

            String tag = prefs.getString("asset_tag", "").trim();
            boolean isEnrolled = prefs.getBoolean("is_enrolled", false) && !tag.isEmpty();
            String serverUrl = prefs.getString("server_url", "").trim();
            String token = prefs.getString("token", "").trim();

            obj.put("isEnrolled", isEnrolled);
            obj.put("serverUrl", serverUrl);
            obj.put("token", token);
            obj.put("assetTag", tag);
            obj.put("deviceId", NetworkHelper.getDeviceId(activity));
            obj.put("hostname", NetworkHelper.getHostname());
            obj.put("model", Build.MANUFACTURER.toUpperCase() + " " + Build.MODEL);
            obj.put("androidVersion", "Android " + Build.VERSION.RELEASE);
            obj.put("version", NetworkHelper.getAppVersion(activity));
            obj.put("isAdminActive", isDeviceAdminActive());
            obj.put("isOwnerActive", isDeviceOwnerActive());
            obj.put("isLocked", prefs.getBoolean("is_locked", false));

            return obj.toString();
        } catch (Exception e) {
            return "{}";
        }
    }

    @JavascriptInterface
    public void enroll(String serverUrl, String tag) {
        SharedPreferences prefs = activity.getSharedPreferences("project_r_prefs", Context.MODE_PRIVATE);
        String token = prefs.getString("token", NetworkHelper.DEFAULT_TOKEN);
        if (token == null || token.trim().isEmpty()) {
            token = NetworkHelper.DEFAULT_TOKEN;
        }
        enroll(serverUrl, token, tag);
    }

    @JavascriptInterface
    public void enroll(String serverUrl, String token, String tag) {
        new Thread(() -> {
            try {
                if (serverUrl == null || serverUrl.trim().isEmpty()) {
                    callback("onEnrollComplete", false, "URL Endpoint Server tidak boleh kosong.");
                    return;
                }
                if (token == null || token.trim().isEmpty()) {
                    callback("onEnrollComplete", false, "Deployment Token tidak boleh kosong.");
                    return;
                }
                if (tag == null || tag.trim().isEmpty()) {
                    callback("onEnrollComplete", false, "Nomor Asset (TAG) tidak boleh kosong.");
                    return;
                }

                String cleanToken = token.trim();
                String cleanUrl = serverUrl.trim().replaceAll("/+$", "");
                String cleanTag = tag.trim();

                JSONObject resp = NetworkHelper.registerDevice(activity, cleanUrl, cleanToken, cleanTag);
                String status = resp.optString("status", "");

                if ("success".equalsIgnoreCase(status) || "ok".equalsIgnoreCase(status) || resp.has("device_id")) {
                    SharedPreferences prefs = activity.getSharedPreferences("project_r_prefs", Context.MODE_PRIVATE);
                    prefs.edit()
                            .putBoolean("is_enrolled", true)
                            .putString("server_url", cleanUrl)
                            .putString("token", cleanToken)
                            .putString("asset_tag", cleanTag)
                            .apply();

                    callback("onEnrollComplete", true, "Perangkat berhasil didaftarkan ke server Project R!");
                } else {
                    String msg = resp.optString("message", resp.optString("response", "Gagal mendaftarkan perangkat. Periksa token dan URL server."));
                    callback("onEnrollComplete", false, msg);
                }
            } catch (Exception e) {
                callback("onEnrollComplete", false, "Koneksi gagal: " + e.getMessage());
            }
        }).start();
    }

    @JavascriptInterface
    public void syncNow() {
        new Thread(() -> {
            try {
                SharedPreferences prefs = activity.getSharedPreferences("project_r_prefs", Context.MODE_PRIVATE);
                String serverUrl = prefs.getString("server_url", "").trim();
                String token = prefs.getString("token", NetworkHelper.DEFAULT_TOKEN).trim();
                String tag = prefs.getString("asset_tag", "").trim();

                if (serverUrl.isEmpty() || token.isEmpty() || tag.isEmpty()) {
                    callback("onSyncComplete", false, "URL Server, Token, atau Asset TAG belum diisi di Pengaturan.");
                    return;
                }

                JSONObject resp = NetworkHelper.sendHeartbeat(activity, serverUrl, token, tag);
                String status = resp.optString("status", "");

                if (!"ok".equalsIgnoreCase(status) && !"success".equalsIgnoreCase(status)) {
                    // Auto-register device if unlinked or unrecognized
                    try {
                        JSONObject reg = NetworkHelper.registerDevice(activity, serverUrl, token, tag);
                        String regStatus = reg.optString("status", "");
                        if ("success".equalsIgnoreCase(regStatus) || "ok".equalsIgnoreCase(regStatus) || reg.has("device_id")) {
                            prefs.edit().putBoolean("is_enrolled", true).apply();
                            resp = NetworkHelper.sendHeartbeat(activity, serverUrl, token, tag);
                            status = resp.optString("status", "");
                        }
                    } catch (Exception ignored) {}
                }

                if ("ok".equalsIgnoreCase(status) || "success".equalsIgnoreCase(status)) {
                    if (resp.has("policies")) {
                        HeartbeatService.applyPolicies(activity, resp.getJSONObject("policies"));
                    }
                    callback("onSyncComplete", true, "Sinkronisasi berhasil! Status perangkat ONLINE di Web Portal.");
                } else {
                    String msg = resp.optString("response", resp.optString("message", "Server menolak sinkronisasi (Status: " + status + ")"));
                    callback("onSyncComplete", false, msg);
                }
            } catch (Exception e) {
                callback("onSyncComplete", false, "Koneksi ke server terputus: " + e.getMessage());
            }
        }).start();
    }

    @JavascriptInterface
    public void saveAssetTag(String newTag) {
        new Thread(() -> {
            try {
                if (newTag == null || newTag.trim().isEmpty()) {
                    callback("onSaveTagComplete", false, "Nomor Asset tidak boleh kosong.");
                    return;
                }

                SharedPreferences prefs = activity.getSharedPreferences("project_r_prefs", Context.MODE_PRIVATE);
                String serverUrl = prefs.getString("server_url", "").trim();
                String token = prefs.getString("token", "");
                if (serverUrl.isEmpty()) {
                    callback("onSaveTagComplete", false, "URL Endpoint Server belum dikonfigurasi.");
                    return;
                }
                String cleanTag = newTag.trim();

                JSONObject resp = NetworkHelper.updateTag(activity, serverUrl, token, cleanTag);
                String status = resp.optString("status", "");

                if ("success".equalsIgnoreCase(status) || "ok".equalsIgnoreCase(status)) {
                    prefs.edit().putString("asset_tag", cleanTag).apply();
                    callback("onSaveTagComplete", true, "Nomor Asset berhasil diperbarui di server!");
                } else {
                    String msg = resp.optString("message", "Gagal memperbarui TAG.");
                    callback("onSaveTagComplete", false, msg);
                }
            } catch (Exception e) {
                // If offline, save locally
                SharedPreferences prefs = activity.getSharedPreferences("project_r_prefs", Context.MODE_PRIVATE);
                prefs.edit().putString("asset_tag", newTag.trim()).apply();
                callback("onSaveTagComplete", true, "TAG disimpan lokal (Offline). Akan disinkronkan saat online.");
            }
        }).start();
    }

    @JavascriptInterface
    public void requestDeviceAdmin() {
        activity.runOnUiThread(() -> {
            try {
                DevicePolicyManager dpm = (DevicePolicyManager) activity.getSystemService(Context.DEVICE_POLICY_SERVICE);
                ComponentName adminComponent = new ComponentName(activity, ProjectRDeviceAdminReceiver.class);
                if (!dpm.isAdminActive(adminComponent)) {
                    Intent intent = new Intent(DevicePolicyManager.ACTION_ADD_DEVICE_ADMIN);
                    intent.putExtra(DevicePolicyManager.EXTRA_DEVICE_ADMIN, adminComponent);
                    intent.putExtra(DevicePolicyManager.EXTRA_ADD_EXPLANATION,
                            "Aktifkan Project R Device Admin untuk melindungi perangkat dari uninstalasi dan mengaktifkan penguncian darurat IT.");
                    activity.startActivity(intent);
                }
            } catch (Exception ignored) {}
        });
    }

    @JavascriptInterface
    public boolean isDeviceAdminActive() {
        try {
            DevicePolicyManager dpm = (DevicePolicyManager) activity.getSystemService(Context.DEVICE_POLICY_SERVICE);
            ComponentName adminComponent = new ComponentName(activity, ProjectRDeviceAdminReceiver.class);
            return dpm.isAdminActive(adminComponent);
        } catch (Exception e) {
            return false;
        }
    }

    @JavascriptInterface
    public boolean isDeviceOwnerActive() {
        try {
            DevicePolicyManager dpm = (DevicePolicyManager) activity.getSystemService(Context.DEVICE_POLICY_SERVICE);
            return dpm.isDeviceOwnerApp(activity.getPackageName());
        } catch (Exception e) {
            return false;
        }
    }

    @JavascriptInterface
    public void unenrollDevice(String token) {
        new Thread(() -> {
            try {
                if (token == null || token.trim().isEmpty()) {
                    callback("onUnenrollComplete", false, "Token uninstalasi wajib diisi.");
                    return;
                }

                SharedPreferences prefs = activity.getSharedPreferences("project_r_prefs", Context.MODE_PRIVATE);
                String serverUrl = prefs.getString("server_url", "").trim();
                if (serverUrl.isEmpty()) {
                    callback("onUnenrollComplete", false, "URL Endpoint Server belum dikonfigurasi.");
                    return;
                }
                String cleanToken = token.trim();

                JSONObject resp = NetworkHelper.verifyUninstall(activity, serverUrl, cleanToken);
                boolean authorized = resp.optBoolean("authorized", false);

                if (authorized) {
                    // 1. Kembalikan seluruh settingan pembatasan (Restore Policy Settings ke normal)
                    ProjectRDeviceAdminReceiver.restoreAllRestrictions(activity);

                    // 2. Lepas hak Device Owner & Device Admin
                    DevicePolicyManager dpm = (DevicePolicyManager) activity.getSystemService(Context.DEVICE_POLICY_SERVICE);
                    ComponentName adminComponent = new ComponentName(activity, ProjectRDeviceAdminReceiver.class);
                    if (dpm != null) {
                        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP && dpm.isDeviceOwnerApp(activity.getPackageName())) {
                            dpm.clearDeviceOwnerApp(activity.getPackageName());
                        }
                        if (dpm.isAdminActive(adminComponent)) {
                            dpm.removeActiveAdmin(adminComponent);
                        }
                    }

                    // 3. Matikan Background Heartbeat Service
                    activity.stopService(new Intent(activity, HeartbeatService.class));

                    // 4. Bersihkan data preferensi lokal
                    prefs.edit().clear().apply();

                    callback("onUnenrollComplete", true, "Otorisasi berhasil. Seluruh kebijakan blokir telah dinetralkan. Membuka dialog uninstal...");

                    // 5. Buka dialog uninstal bawaan Android
                    new Handler(Looper.getMainLooper()).postDelayed(() -> {
                        try {
                            Intent intent = new Intent(Intent.ACTION_DELETE);
                            intent.setData(Uri.parse("package:" + activity.getPackageName()));
                            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                            activity.startActivity(intent);
                        } catch (Exception ignored) {}
                    }, 1200);

                } else {
                    String msg = resp.optString("message", "Token uninstalasi ditolak oleh server.");
                    callback("onUnenrollComplete", false, msg);
                }
            } catch (Exception e) {
                callback("onUnenrollComplete", false, "Gagal memverifikasi token uninstalasi: " + e.getMessage());
            }
        }).start();
    }

    @JavascriptInterface
    public void unlockWithPin(String pin) {
        new Thread(() -> {
            try {
                if (pin == null || pin.trim().isEmpty()) {
                    callback("onUnlockAttemptResult", false, "PIN unlock tidak boleh kosong.");
                    return;
                }

                String cleanPin = pin.trim();
                SharedPreferences prefs = activity.getSharedPreferences("project_r_prefs", Context.MODE_PRIVATE);
                String serverUrl = prefs.getString("server_url", "").trim();
                String cachedPin = prefs.getString("warning_unlock_password", null);

                boolean unlocked = false;
                String responseMessage = "";

                if (!serverUrl.isEmpty()) {
                    try {
                        JSONObject resp = NetworkHelper.unlockScreen(activity, serverUrl, cleanPin);
                        String status = resp.optString("status", "");
                        if ("success".equalsIgnoreCase(status) || "ok".equalsIgnoreCase(status)) {
                            unlocked = true;
                            responseMessage = resp.optString("message", "Layar perangkat berhasil dibuka kuncinya.");
                            if (resp.has("policies")) {
                                HeartbeatService.applyPolicies(activity, resp.getJSONObject("policies"));
                            }
                        } else {
                            responseMessage = resp.optString("message", "PIN unlock atau token tidak valid.");
                        }
                    } catch (Exception netEx) {
                        if (cachedPin != null && cachedPin.equalsIgnoreCase(cleanPin)) {
                            unlocked = true;
                            responseMessage = "Layar berhasil dibuka (Otorisasi Offline Valid).";
                        } else {
                            responseMessage = "Gagal memverifikasi ke server: " + netEx.getMessage();
                        }
                    }
                } else if (cachedPin != null && cachedPin.equalsIgnoreCase(cleanPin)) {
                    unlocked = true;
                    responseMessage = "Layar berhasil dibuka (Otorisasi Offline Valid).";
                } else {
                    responseMessage = "URL Endpoint Server belum dikonfigurasi.";
                }

                if (unlocked) {
                    prefs.edit()
                            .putBoolean("is_locked", false)
                            .remove("warning_unlock_password")
                            .apply();

                    // Send unlock broadcast
                    Intent unlockIntent = new Intent("com.projectr.agent.UNLOCK");
                    activity.sendBroadcast(unlockIntent);

                    callback("onUnlockAttemptResult", true, responseMessage);
                } else {
                    callback("onUnlockAttemptResult", false, responseMessage);
                }
            } catch (Exception e) {
                callback("onUnlockAttemptResult", false, "Terjadi kesalahan: " + e.getMessage());
            }
        }).start();
    }

    @JavascriptInterface
    public void callHelpdesk(String phoneNumber) {
        try {
            Intent intent = new Intent(Intent.ACTION_DIAL);
            intent.setData(Uri.parse("tel:" + (phoneNumber != null ? phoneNumber : "+622150881234")));
            activity.startActivity(intent);
        } catch (Exception ignored) {}
    }

    private void callback(String funcName, boolean success, String message) {
        new Handler(Looper.getMainLooper()).post(() -> {
            String escapedMsg = message.replace("\\", "\\\\").replace("'", "\\'").replace("\n", " ");
            activity.runJs("if (typeof " + funcName + " === 'function') { " + funcName + "(" + success + ", '" + escapedMsg + "'); }");
        });
    }
}
