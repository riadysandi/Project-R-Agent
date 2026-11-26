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
    private static final String CHANNEL_ID = "project_r_guardian_channel";
    public static final String ACTION_SYNC_NOW = "com.projectr.agent.ACTION_SYNC_NOW";

    private boolean isRunning = false;
    private Thread workerThread;
    private final ExecutorService syncExecutor = Executors.newSingleThreadExecutor();
    private final Object heartbeatLock = new Object();
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
        MdmPolicyManager.applyCachedPolicies(this);
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
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
            Log.w(TAG, "triggerImmediateSync failed: " + e.getMessage());
        }
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
                } catch (Exception e) {
                    Log.w(TAG, "Heartbeat loop iteration error: " + e.getMessage());
                }
            }
        });
        workerThread.start();
    }

    public void sendHeartbeat() {
        synchronized (heartbeatLock) {
            try {
                SharedPreferences prefs = getSharedPreferences(MdmPolicyManager.PREFS_NAME, Context.MODE_PRIVATE);
                boolean isEnrolled = prefs.getBoolean("is_enrolled", false);
                if (!isEnrolled) return;

                String baseUrl = prefs.getString("server_url", "").trim();
                String token = prefs.getString("token", NetworkHelper.DEFAULT_TOKEN).trim();
                String tag = prefs.getString("asset_tag", "").trim();
                if (baseUrl.isEmpty() || token.isEmpty() || tag.isEmpty()) return;

                RemoteRelayClient.getInstance(this).ensureConnected();

                String previousPoliciesRaw = prefs.getString(MdmPolicyManager.PREF_LAST_POLICIES, "");
                JSONArray preEvaluations = MdmPolicyManager.buildEvaluations(this);
                JSONObject resp = NetworkHelper.sendHeartbeat(this, baseUrl, token, tag, preEvaluations);
                if (resp != null) {
                    int srvInterval = resp.optInt("interval", 60);
                    if (srvInterval >= 15 && srvInterval <= 600) {
                        heartbeatIntervalSec = srvInterval;
                    }
                    int srvInvInterval = resp.optInt("inventory_interval", 3600);
                    if (srvInvInterval > 0) {
                        inventoryIntervalSec = srvInvInterval;
                    }

                    // 1. Handle pending remote actions (e.g. remote_unenroll from web portal)
                    if (MdmPolicyManager.handlePendingActions(this, resp.optJSONArray("pending_actions"))) {
                        try {
                            NetworkHelper.sendHeartbeat(this, baseUrl, token, tag, null, true);
                        } catch (Exception ackEx) {
                            Log.w(TAG, "Failed sending remote_unenroll_ack heartbeat: " + ackEx.getMessage());
                        }
                        return;
                    }

                    // 2. Skip re-enforcing MDM policies if device was remotely unenrolled and has no active admin/owner
                    if (MdmPolicyManager.isMdmUnenrolledAndUnprivileged(this)) {
                        Log.d(TAG, "Perangkat dalam status remote_unenroll (aplikasi biasa); melewati penerapan kebijakan MDM.");
                        return;
                    }

                    if (resp.has("policies")) {
                        JSONObject newPolicies = resp.getJSONObject("policies");
                        String newPoliciesRaw = newPolicies.toString();
                        JSONArray postEvaluations = MdmPolicyManager.applyAndEvaluatePolicies(this, newPolicies);

                        // Report updated compliance immediately when policy payload changed or first evaluation occurred
                        boolean policiesChanged = !newPoliciesRaw.equals(previousPoliciesRaw);
                        boolean firstEvaluation = preEvaluations.length() == 0 && postEvaluations.length() > 0;
                        if ((policiesChanged || firstEvaluation) && postEvaluations.length() > 0) {
                            try {
                                NetworkHelper.sendHeartbeat(this, baseUrl, token, tag, postEvaluations);
                            } catch (Exception evalEx) {
                                Log.w(TAG, "Follow-up evaluation heartbeat failed: " + evalEx.getMessage());
                            }
                        }
                    }

                    // Silent OTA Auto-Update Check
                    String serverVer = resp.optString("android_version", resp.optString("version", ""));
                    String apkUrl = resp.optString("apk_download_url", "");
                    if (!serverVer.isEmpty()) {
                        AutoUpdateManager.checkAndTriggerUpdate(this, baseUrl, serverVer, apkUrl);
                    }
                }
            } catch (Exception e) {
                Log.e(TAG, "Error sending heartbeat: " + e.getMessage(), e);
            }
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
        RemoteRelayClient.getInstance(this).disconnect();
        if (workerThread != null) {
            workerThread.interrupt();
        }
        syncExecutor.shutdownNow();
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
