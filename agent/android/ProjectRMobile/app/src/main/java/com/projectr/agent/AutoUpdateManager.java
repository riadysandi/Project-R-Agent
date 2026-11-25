package com.projectr.agent;

import android.app.PendingIntent;
import android.app.admin.DevicePolicyManager;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageInfo;
import android.content.pm.PackageInstaller;
import android.content.pm.PackageManager;
import android.os.Build;
import android.util.Log;

import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;

public class AutoUpdateManager {
    private static final String TAG = "ProjectROTA";
    private static volatile boolean isUpdating = false;
    private static long lastAttemptTime = 0;

    public static synchronized boolean isUpdating() {
        return isUpdating;
    }

    public static synchronized void setUpdating(boolean updating) {
        isUpdating = updating;
    }

    public static boolean isVersionNewer(String serverVerStr, String currentVerStr) {
        if (serverVerStr == null || serverVerStr.trim().isEmpty() ||
            currentVerStr == null || currentVerStr.trim().isEmpty()) {
            return false;
        }

        try {
            String cleanServer = serverVerStr.trim().replaceAll("^[vV]", "");
            String cleanCurrent = currentVerStr.trim().replaceAll("^[vV]", "");

            String[] sParts = cleanServer.split("\\.");
            String[] cParts = cleanCurrent.split("\\.");

            int maxLen = Math.max(sParts.length, cParts.length);
            for (int i = 0; i < maxLen; i++) {
                int sNum = (i < sParts.length) ? Integer.parseInt(sParts[i].replaceAll("\\D+", "")) : 0;
                int cNum = (i < cParts.length) ? Integer.parseInt(cParts[i].replaceAll("\\D+", "")) : 0;
                if (sNum > cNum) return true;
                if (sNum < cNum) return false;
            }
            return false;
        } catch (Exception e) {
            return !serverVerStr.trim().equalsIgnoreCase(currentVerStr.trim());
        }
    }

    public static void checkAndTriggerUpdate(Context context, String serverUrl, String serverVersion, String apkDownloadUrl) {
        if (isUpdating()) {
            Log.d(TAG, "Proses update sedang berjalan, lewati pengecekan.");
            return;
        }

        // Throttle: minimal 2 menit antar percobaan
        long now = System.currentTimeMillis();
        if (now - lastAttemptTime < 120_000) {
            return;
        }

        String currentVersion = "2.3.0";
        try {
            PackageInfo pInfo = context.getPackageManager().getPackageInfo(context.getPackageName(), 0);
            currentVersion = pInfo.versionName;
        } catch (PackageManager.NameNotFoundException ignored) {}

        if (!isVersionNewer(serverVersion, currentVersion)) {
            return;
        }

        Log.i(TAG, "Terdeteksi rilis APK baru di server: v" + serverVersion + " (versi saat ini: v" + currentVersion + "). Memulai Silent OTA Auto-Update...");
        lastAttemptTime = now;
        setUpdating(true);

        new Thread(() -> {
            try {
                String downloadUrl = apkDownloadUrl;
                if (downloadUrl == null || downloadUrl.trim().isEmpty()) {
                    downloadUrl = serverUrl.replaceAll("/+$", "") + "/api/agent/download/apk";
                }

                performSilentInstall(context, downloadUrl);
            } catch (Exception e) {
                Log.e(TAG, "Silent OTA Auto-update gagal: " + e.getMessage(), e);
                setUpdating(false);
            }
        }).start();
    }

    private static void performSilentInstall(Context context, String downloadEndpoint) throws Exception {
        Log.i(TAG, "Mengunduh APK pembaruan dari: " + downloadEndpoint);
        URL url = new URL(downloadEndpoint);
        HttpURLConnection conn = (HttpURLConnection) url.openConnection();
        conn.setRequestMethod("GET");
        conn.setConnectTimeout(15000);
        conn.setReadTimeout(45000);
        conn.setInstanceFollowRedirects(true);

        int code = conn.getResponseCode();
        if (code != HttpURLConnection.HTTP_OK) {
            throw new Exception("HTTP error code: " + code + " saat mengunduh APK");
        }

        int contentLength = conn.getContentLength();
        Log.i(TAG, "Ukuran paket pembaruan: " + contentLength + " bytes. Membuka PackageInstaller Session...");

        PackageManager pm = context.getPackageManager();
        PackageInstaller installer = pm.getPackageInstaller();
        PackageInstaller.SessionParams params = new PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL);
        params.setAppPackageName(context.getPackageName());

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            params.setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED);
        }

        int sessionId = installer.createSession(params);
        PackageInstaller.Session session = installer.openSession(sessionId);

        try (InputStream in = conn.getInputStream();
             OutputStream out = session.openWrite("projectr_ota_update", 0, contentLength > 0 ? contentLength : -1)) {
            byte[] buffer = new byte[65536];
            int read;
            long totalRead = 0;
            while ((read = in.read(buffer)) != -1) {
                out.write(buffer, 0, read);
                totalRead += read;
            }
            session.fsync(out);
            Log.i(TAG, "File APK berhasil diunduh (" + totalRead + " bytes). Mengirim commit session ke sistem Android...");
        } catch (Exception e) {
            session.abandon();
            throw e;
        }

        Intent statusIntent = new Intent(context, PackageInstallerStatusReceiver.class);
        int flags = PendingIntent.FLAG_UPDATE_CURRENT;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            flags |= PendingIntent.FLAG_MUTABLE;
        }
        PendingIntent pendingIntent = PendingIntent.getBroadcast(context, sessionId, statusIntent, flags);

        session.commit(pendingIntent.getIntentSender());
        session.close();
        Log.i(TAG, "Session instalasi commit berhasil dikirim. Android OS sedang memasang pembaruan di latar belakang...");
    }
}
