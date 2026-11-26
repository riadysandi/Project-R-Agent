package com.projectr.agent;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.os.Build;
import android.util.Log;

public class BootReceiver extends BroadcastReceiver {
    private static final String TAG = "ProjectRBootReceiver";

    @Override
    public void onReceive(Context context, Intent intent) {
        if (intent == null) return;
        String action = intent.getAction();
        Log.i(TAG, "Menerima broadcast sistem: " + action);

        if (Intent.ACTION_BOOT_COMPLETED.equals(action)
                || Intent.ACTION_LOCKED_BOOT_COMPLETED.equals(action)
                || "android.intent.action.QUICKBOOT_POWERON".equals(action)
                || "com.htc.intent.action.QUICKBOOT_POWERON".equals(action)
                || Intent.ACTION_MY_PACKAGE_REPLACED.equals(action)) {

            SharedPreferences prefs = context.getSharedPreferences(MdmPolicyManager.PREFS_NAME, Context.MODE_PRIVATE);
            boolean isEnrolled = prefs.getBoolean("is_enrolled", false);

            Log.i(TAG, "Perangkat booting/update selesai. Status terdaftar: " + isEnrolled);

            if (isEnrolled) {
                WatchdogJobService.schedulePeriodic(context);
                boolean started = WatchdogJobService.tryStartHeartbeatService(context);
                if (started) {
                    Log.i(TAG, "HeartbeatService berhasil dinyalakan otomatis setelah boot/update.");
                } else {
                    Log.w(TAG, "Start foreground service ditolak OS; menjadwalkan WatchdogJobService restart fallback.");
                    WatchdogJobService.scheduleRestart(context);
                }
            }

            // Jika status sedang dikunci saat HP dihidupkan ulang, seketika buka layar kunci Kiosk
            boolean isLocked = prefs.getBoolean("is_locked", false);
            if (isLocked) {
                try {
                    Intent lockIntent = new Intent(context, MainActivity.class);
                    lockIntent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_REORDER_TO_FRONT);
                    context.startActivity(lockIntent);
                    Log.i(TAG, "Status terkunci terdeteksi pada boot. MainActivity diluncurkan.");
                } catch (Exception e) {
                    Log.e(TAG, "Gagal meluncurkan MainActivity pada boot: " + e.getMessage());
                }
            }
        }
    }
}
