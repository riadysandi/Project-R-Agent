package com.projectr.agent;

import android.app.ForegroundServiceStartNotAllowedException;
import android.app.job.JobInfo;
import android.app.job.JobParameters;
import android.app.job.JobScheduler;
import android.app.job.JobService;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.os.Build;
import android.util.Log;

import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Native Android JobScheduler watchdog (API 24+) that keeps HeartbeatService
 * alive across task removals, package updates, and Android 12-15 background limits.
 * Falls back to a direct single heartbeat cycle if FGS start is blocked by the OS.
 */
public class WatchdogJobService extends JobService {
    private static final String TAG = "ProjectRWatchdog";
    public static final int PERIODIC_JOB_ID = 1401;
    public static final int RESTART_JOB_ID = 1402;
    private static final long PERIODIC_INTERVAL_MS = 15L * 60L * 1000L;

    private final ExecutorService executor = Executors.newSingleThreadExecutor();

    @Override
    public boolean onStartJob(JobParameters params) {
        SharedPreferences prefs = getSharedPreferences(MdmPolicyManager.PREFS_NAME, Context.MODE_PRIVATE);
        if (!prefs.getBoolean("is_enrolled", false)) {
            return false;
        }

        executor.execute(() -> {
            try {
                boolean serviceHealthy = isHeartbeatHealthy(prefs);
                if (serviceHealthy) {
                    Log.d(TAG, "Watchdog check: HeartbeatService sehat.");
                    return;
                }

                Log.i(TAG, "Watchdog mendeteksi HeartbeatService tidak aktif/stale; mencoba menghidupkan ulang...");
                boolean startedService = tryStartHeartbeatService(this);
                if (!startedService || !HeartbeatService.isServiceRunning()) {
                    Log.i(TAG, "Menjalankan fallback sinkronisasi heartbeat langsung dari WatchdogJobService.");
                    HeartbeatService.executeHeartbeatCycle(this);
                }
            } catch (Exception e) {
                Log.e(TAG, "WatchdogJobService error: " + e.getMessage(), e);
            } finally {
                jobFinished(params, false);
            }
        });
        return true;
    }

    @Override
    public boolean onStopJob(JobParameters params) {
        return true;
    }

    @Override
    public void onDestroy() {
        executor.shutdownNow();
        super.onDestroy();
    }

    private static boolean isHeartbeatHealthy(SharedPreferences prefs) {
        if (!HeartbeatService.isServiceRunning()) {
            return false;
        }
        long lastAttemptMs = prefs.getLong(HeartbeatService.PREF_LAST_HEARTBEAT_ATTEMPT_MS, 0L);
        int intervalSec = prefs.getInt(HeartbeatService.PREF_HEARTBEAT_INTERVAL_SEC, 60);
        long staleThresholdMs = Math.max(120_000L, intervalSec * 2000L);
        return lastAttemptMs > 0L && (System.currentTimeMillis() - lastAttemptMs) < staleThresholdMs;
    }

    public static boolean tryStartHeartbeatService(Context context) {
        if (context == null) return false;
        try {
            Intent serviceIntent = new Intent(context, HeartbeatService.class);
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                context.startForegroundService(serviceIntent);
            } else {
                context.startService(serviceIntent);
            }
            return true;
        } catch (Exception e) {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S
                    && e instanceof ForegroundServiceStartNotAllowedException) {
                Log.w(TAG, "OS memblokir startForegroundService dari background (Android 12+): " + e.getMessage());
            } else {
                Log.w(TAG, "Gagal menyalakan HeartbeatService: " + e.getMessage());
            }
            return false;
        }
    }

    /**
     * Schedules the 15-minute persisted periodic JobScheduler watchdog if enrolled
     * and not already pending.
     */
    public static void schedulePeriodic(Context context) {
        if (context == null || Build.VERSION.SDK_INT < Build.VERSION_CODES.LOLLIPOP) return;
        try {
            SharedPreferences prefs = context.getSharedPreferences(MdmPolicyManager.PREFS_NAME, Context.MODE_PRIVATE);
            if (!prefs.getBoolean("is_enrolled", false)) {
                return;
            }

            JobScheduler scheduler = (JobScheduler) context.getSystemService(Context.JOB_SCHEDULER_SERVICE);
            if (scheduler == null) return;

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
                if (scheduler.getPendingJob(PERIODIC_JOB_ID) != null) {
                    return;
                }
            }

            ComponentName component = new ComponentName(context, WatchdogJobService.class);
            JobInfo jobInfo = new JobInfo.Builder(PERIODIC_JOB_ID, component)
                    .setPeriodic(PERIODIC_INTERVAL_MS)
                    .setPersisted(true)
                    .setRequiredNetworkType(JobInfo.NETWORK_TYPE_ANY)
                    .build();

            int res = scheduler.schedule(jobInfo);
            Log.i(TAG, "Watchdog periodic job scheduled (result=" + res + ")");
        } catch (Exception e) {
            Log.w(TAG, "schedulePeriodic failed: " + e.getMessage());
        }
    }

    /**
     * Schedules a short one-shot restart job (3s..15s) after service destruction,
     * task swipe, or blocked background FGS startup.
     */
    public static void scheduleRestart(Context context) {
        if (context == null || Build.VERSION.SDK_INT < Build.VERSION_CODES.LOLLIPOP) return;
        try {
            SharedPreferences prefs = context.getSharedPreferences(MdmPolicyManager.PREFS_NAME, Context.MODE_PRIVATE);
            if (!prefs.getBoolean("is_enrolled", false)) {
                return;
            }

            JobScheduler scheduler = (JobScheduler) context.getSystemService(Context.JOB_SCHEDULER_SERVICE);
            if (scheduler == null) return;

            ComponentName component = new ComponentName(context, WatchdogJobService.class);
            JobInfo jobInfo = new JobInfo.Builder(RESTART_JOB_ID, component)
                    .setMinimumLatency(3_000L)
                    .setOverrideDeadline(15_000L)
                    .setRequiredNetworkType(JobInfo.NETWORK_TYPE_NONE)
                    .build();

            int res = scheduler.schedule(jobInfo);
            Log.i(TAG, "Watchdog one-shot restart job scheduled (result=" + res + ")");
        } catch (Exception e) {
            Log.w(TAG, "scheduleRestart failed: " + e.getMessage());
        }
    }
}
