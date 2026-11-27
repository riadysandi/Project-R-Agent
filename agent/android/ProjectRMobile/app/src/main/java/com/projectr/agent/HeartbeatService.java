package com.projectr.agent;

import android.app.AlarmManager;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.os.Build;
import android.os.IBinder;
import android.util.Log;
import androidx.core.app.NotificationCompat;
import org.json.JSONArray;
import org.json.JSONObject;

import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public class HeartbeatService extends Service {
    private static final String TAG = "ProjectRHeartbeat";
    private static final String CHANNEL_ID = "project_r_guardian_silent_v2";
    public static final String ACTION_SYNC_NOW = "com.projectr.agent.ACTION_SYNC_NOW";
    public static final String PREF_LAST_HEARTBEAT_ATTEMPT_MS = "last_heartbeat_attempt_ms";
    public static final String PREF_LAST_HEARTBEAT_SUCCESS_MS = "last_heartbeat_success_ms";
    public static final String PREF_HEARTBEAT_INTERVAL_SEC = "heartbeat_interval_sec";

    private static volatile boolean serviceRunning = false;
    private static final Object HEARTBEAT_LOCK = new Object();

    private boolean isRunning = false;
    private Thread workerThread;
    private final ExecutorService syncExecutor = Executors.newSingleThreadExecutor();
    private volatile int heartbeatIntervalSec = 60;
    private volatile int inventoryIntervalSec = 3600;

    public static boolean isServiceRunning() {
        return serviceRunning;
    }

    @Override
    public void onCreate() {
        super.onCreate();
        serviceRunning = true;
        createNotificationChannel();
        Notification notification = new NotificationCompat.Builder(this, CHANNEL_ID)
                .setContentTitle("Project R Guardian")
                .setContentText("Detak jantung aktif terhubung ke SecOps")
                .setSmallIcon(android.R.drawable.ic_dialog_info)
                .setPriority(NotificationCompat.PRIORITY_MIN)
                .setOngoing(false)
                .setOnlyAlertOnce(true)
                .setAutoCancel(false)
                .build();
        startForeground(1337, notification);
        LocationHelper.ensureLocationUpdates(this);
        MdmPolicyManager.applyCachedPolicies(this);
        WatchdogJobService.schedulePeriodic(this);
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        serviceRunning = true;
        RemoteRelayClient.getInstance(this).ensureConnected();
        if (!isRunning) {
            isRunning = true;
            startHeartbeatLoop();
        }
        if (intent != null && ACTION_SYNC_NOW.equals(intent.getAction())) {
            syncExecutor.execute(this::sendHeartbeat);
        }
        return START_STICKY;
    }

    public static void triggerImmediateSync(Context context) {
        if (context == null) return;
        try {
            Intent intent = new Intent(context, HeartbeatService.class);
            intent.setAction(ACTION_SYNC_NOW);
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                context.startForegroundService(intent);
            } else {
                context.startService(intent);
            }
        } catch (Exception e) {
            Log.w(TAG, "triggerImmediateSync FGS blocked/failed, scheduling watchdog fallback: " + e.getMessage());
            WatchdogJobService.scheduleRestart(context);
        }
    }

    private void startHeartbeatLoop() {
        workerThread = new Thread(() -> {
            while (isRunning) {
                try {
                    int nextInterval = sendHeartbeat();
                    if (nextInterval >= 15 && nextInterval <= 600) {
                        heartbeatIntervalSec = nextInterval;
                    }
                    long sleepMs = Math.max(15, heartbeatIntervalSec) * 1000L;
                    Thread.sleep(sleepMs);
                } catch (InterruptedException e) {
                    break;
                } catch (Exception e) {
                    Log.w(TAG, "Heartbeat loop iteration error: " + e.getMessage());
                }
            }
        });
        workerThread.start();
    }

    public int sendHeartbeat() {
        return executeHeartbeatCycle(this);
    }

    /**
     * Thread-safe single heartbeat execution shared between HeartbeatService and WatchdogJobService fallback.
     * Returns the server-configured interval in seconds (or current cached interval).
     */
    public static int executeHeartbeatCycle(Context context) {
        synchronized (HEARTBEAT_LOCK) {
            SharedPreferences prefs = context.getSharedPreferences(MdmPolicyManager.PREFS_NAME, Context.MODE_PRIVATE);
            int currentInterval = prefs.getInt(PREF_HEARTBEAT_INTERVAL_SEC, 60);
            try {
                boolean isEnrolled = prefs.getBoolean("is_enrolled", false);
                if (!isEnrolled) return currentInterval;

                String baseUrl = prefs.getString("server_url", "").trim();
                String token = prefs.getString("token", NetworkHelper.DEFAULT_TOKEN).trim();
                String tag = prefs.getString("asset_tag", "").trim();
                if (baseUrl.isEmpty() || token.isEmpty() || tag.isEmpty()) return currentInterval;

                prefs.edit().putLong(PREF_LAST_HEARTBEAT_ATTEMPT_MS, System.currentTimeMillis()).apply();
                RemoteRelayClient.getInstance(context).ensureConnected();

                String previousPoliciesRaw = prefs.getString(MdmPolicyManager.PREF_LAST_POLICIES, "");
                JSONArray preEvaluations = MdmPolicyManager.buildEvaluations(context);
                JSONObject resp = NetworkHelper.sendHeartbeat(context, baseUrl, token, tag, preEvaluations);
                if (resp != null) {
                    prefs.edit().putLong(PREF_LAST_HEARTBEAT_SUCCESS_MS, System.currentTimeMillis()).apply();

                    int srvInterval = resp.optInt("interval", 60);
                    if (srvInterval >= 15 && srvInterval <= 600) {
                        currentInterval = srvInterval;
                        prefs.edit().putInt(PREF_HEARTBEAT_INTERVAL_SEC, srvInterval).apply();
                    }

                    // 1. Handle pending remote actions (e.g. remote_unenroll from web portal)
                    if (MdmPolicyManager.handlePendingActions(context, resp.optJSONArray("pending_actions"))) {
                        try {
                            NetworkHelper.sendHeartbeat(context, baseUrl, token, tag, null, true);
                        } catch (Exception ackEx) {
                            Log.w(TAG, "Failed sending remote_unenroll_ack heartbeat: " + ackEx.getMessage());
                        }
                        return currentInterval;
                    }

                    // 2. Skip re-enforcing MDM policies if device was remotely unenrolled and has no active admin/owner
                    if (MdmPolicyManager.isMdmUnenrolledAndUnprivileged(context)) {
                        Log.d(TAG, "Perangkat dalam status remote_unenroll (aplikasi biasa); melewati penerapan kebijakan MDM.");
                        return currentInterval;
                    }

                    if (resp.has("policies")) {
                        JSONObject newPolicies = resp.getJSONObject("policies");
                        String newPoliciesRaw = newPolicies.toString();
                        JSONArray postEvaluations = MdmPolicyManager.applyAndEvaluatePolicies(context, newPolicies);

                        // Report updated compliance immediately when policy payload changed or first evaluation occurred
                        boolean policiesChanged = !newPoliciesRaw.equals(previousPoliciesRaw);
                        boolean firstEvaluation = preEvaluations.length() == 0 && postEvaluations.length() > 0;
                        if ((policiesChanged || firstEvaluation) && postEvaluations.length() > 0) {
                            try {
                                NetworkHelper.sendHeartbeat(context, baseUrl, token, tag, postEvaluations);
                            } catch (Exception evalEx) {
                                Log.w(TAG, "Follow-up evaluation heartbeat failed: " + evalEx.getMessage());
                            }
                        }
                    }

                    // Silent OTA Auto-Update Check
                    String serverVer = resp.optString("android_version", resp.optString("version", ""));
                    String apkUrl = resp.optString("apk_download_url", "");
                    if (!serverVer.isEmpty()) {
                        AutoUpdateManager.checkAndTriggerUpdate(context, baseUrl, serverVer, apkUrl);
                    }
                }
            } catch (Exception e) {
                Log.e(TAG, "Error sending heartbeat: " + e.getMessage(), e);
            }
            return currentInterval;
        }
    }

    public static void applyPolicies(Context context, JSONObject policies) {
        MdmPolicyManager.applyAndEvaluatePolicies(context, policies);
    }

    private void createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationChannel channel = new NotificationChannel(
                    CHANNEL_ID,
                    "Project R Guardian Service",
                    NotificationManager.IMPORTANCE_MIN
            );
            channel.setDescription("Layanan latar belakang detak jantung dan keamanan aset");
            NotificationManager manager = getSystemService(NotificationManager.class);
            if (manager != null) {
                manager.createNotificationChannel(channel);
            }
        }
    }

    @Override
    public void onTaskRemoved(Intent rootIntent) {
        Log.i(TAG, "Task dihapus dari Recents; menjadwalkan pemulihan watchdog...");
        scheduleRestartWatchdog();
        super.onTaskRemoved(rootIntent);
    }

    @Override
    public void onDestroy() {
        serviceRunning = false;
        isRunning = false;
        RemoteRelayClient.getInstance(this).disconnect();
        if (workerThread != null) {
            workerThread.interrupt();
        }
        syncExecutor.shutdownNow();
        scheduleRestartWatchdog();
        super.onDestroy();
    }

    private void scheduleRestartWatchdog() {
        try {
            SharedPreferences prefs = getSharedPreferences(MdmPolicyManager.PREFS_NAME, Context.MODE_PRIVATE);
            if (!prefs.getBoolean("is_enrolled", false)) {
                return;
            }

            // 1. Native JobScheduler watchdog fallback (works without SCHEDULE_EXACT_ALARM on Android 12-15)
            WatchdogJobService.schedulePeriodic(getApplicationContext());
            WatchdogJobService.scheduleRestart(getApplicationContext());

            // 2. Non-exact AlarmManager fallback (does not throw SecurityException on targetSdk 35)
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
                long triggerAt = System.currentTimeMillis() + 5000L;
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                    am.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, triggerAt, pIntent);
                } else {
                    am.set(AlarmManager.RTC_WAKEUP, triggerAt, pIntent);
                }
            }
        } catch (Exception e) {
            Log.w(TAG, "scheduleRestartWatchdog error: " + e.getMessage());
        }
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
