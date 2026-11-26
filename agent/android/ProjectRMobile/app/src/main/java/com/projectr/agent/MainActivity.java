package com.projectr.agent;

import android.annotation.SuppressLint;
import android.app.ActivityManager;
import android.app.admin.DevicePolicyManager;
import android.content.BroadcastReceiver;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.SharedPreferences;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.PowerManager;
import android.provider.Settings;
import android.util.Log;
import android.view.KeyEvent;
import android.view.WindowManager;
import android.webkit.WebChromeClient;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import androidx.appcompat.app.AppCompatActivity;

public class MainActivity extends AppCompatActivity {
    private WebView webView;
    private long lastResumeSyncMs = 0L;
    private final BroadcastReceiver lockReceiver = new BroadcastReceiver() {
        @Override
        public void onReceive(Context context, Intent intent) {
            String action = intent.getAction();
            if ("com.projectr.agent.LOCK".equals(action)) {
                setLockState(true);
            } else if ("com.projectr.agent.UNLOCK".equals(action)) {
                setLockState(false);
            } else if (MdmPolicyManager.ACTION_POLICY_UPDATED.equals(action)) {
                runJs("if (typeof loadConfig === 'function') { loadConfig(); }");
            }
        }
    };

    @SuppressLint("SetJavaScriptEnabled")
    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        // Ensure default preferences
        SharedPreferences prefs = getSharedPreferences("project_r_prefs", Context.MODE_PRIVATE);

        // Read intent extras if passed via ADB (e.g. am start --es server_url ... --es tag ...)
        Intent startIntent = getIntent();
        if (startIntent != null) {
            SharedPreferences.Editor editor = prefs.edit();
            boolean changed = false;
            if (startIntent.hasExtra("server_url")) {
                editor.putString("server_url", startIntent.getStringExtra("server_url"));
                changed = true;
            }
            if (startIntent.hasExtra("token")) {
                editor.putString("token", startIntent.getStringExtra("token"));
                changed = true;
            }
            if (startIntent.hasExtra("asset_tag") || startIntent.hasExtra("tag")) {
                String t = startIntent.hasExtra("asset_tag") ? startIntent.getStringExtra("asset_tag") : startIntent.getStringExtra("tag");
                if (t != null && !t.trim().isEmpty()) {
                    editor.putString("asset_tag", t.trim());
                    editor.putBoolean("is_enrolled", true);
                    changed = true;
                }
            }
            if (changed) {
                editor.apply();
            }
        }

        webView = new WebView(this);
        setContentView(webView);

        webView.getSettings().setJavaScriptEnabled(true);
        webView.getSettings().setDomStorageEnabled(true);
        webView.getSettings().setAllowFileAccess(true);
        webView.setWebViewClient(new WebViewClient() {
            @Override
            public void onPageFinished(WebView view, String url) {
                super.onPageFinished(view, url);
                view.evaluateJavascript("if (typeof loadConfig === 'function') { loadConfig(); }", null);
            }
        });
        webView.setWebChromeClient(new WebChromeClient());

        webView.addJavascriptInterface(new WebAppInterface(this), "AndroidBridge");
        webView.loadUrl("file:///android_asset/www/index.html");

        // Register Lock/Unlock & Policy Broadcasts
        IntentFilter filter = new IntentFilter();
        filter.addAction("com.projectr.agent.LOCK");
        filter.addAction("com.projectr.agent.UNLOCK");
        filter.addAction(MdmPolicyManager.ACTION_POLICY_UPDATED);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            registerReceiver(lockReceiver, filter, Context.RECEIVER_EXPORTED);
        } else {
            registerReceiver(lockReceiver, filter);
        }

        // Ensure Device Owner uninstall blocking & auto-grant permissions
        try {
            android.app.admin.DevicePolicyManager dpm = (android.app.admin.DevicePolicyManager) getSystemService(Context.DEVICE_POLICY_SERVICE);
            if (dpm != null && dpm.isDeviceOwnerApp(getPackageName())) {
                android.content.ComponentName admin = new android.content.ComponentName(this, ProjectRDeviceAdminReceiver.class);
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
                    dpm.setUninstallBlocked(admin, getPackageName(), true);
                }
                dpm.setPermissionGrantState(admin, getPackageName(), android.Manifest.permission.ACCESS_FINE_LOCATION, android.app.admin.DevicePolicyManager.PERMISSION_GRANT_STATE_GRANTED);
                dpm.setPermissionGrantState(admin, getPackageName(), android.Manifest.permission.ACCESS_COARSE_LOCATION, android.app.admin.DevicePolicyManager.PERMISSION_GRANT_STATE_GRANTED);
            }
        } catch (Exception ignored) {}

        LocationHelper.ensureLocationUpdates(this);

        // Schedule persistent JobScheduler watchdog & start Background Heartbeat Service
        if (prefs.getBoolean("is_enrolled", false)) {
            WatchdogJobService.schedulePeriodic(this);
            maybeRequestBatteryOptimizationExemption(prefs);
        }
        WatchdogJobService.tryStartHeartbeatService(this);
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        if (intent != null) {
            SharedPreferences prefs = getSharedPreferences("project_r_prefs", Context.MODE_PRIVATE);
            SharedPreferences.Editor editor = prefs.edit();
            boolean changed = false;
            if (intent.hasExtra("server_url")) {
                editor.putString("server_url", intent.getStringExtra("server_url"));
                changed = true;
            }
            if (intent.hasExtra("token")) {
                editor.putString("token", intent.getStringExtra("token"));
                changed = true;
            }
            if (intent.hasExtra("asset_tag") || intent.hasExtra("tag")) {
                String t = intent.hasExtra("asset_tag") ? intent.getStringExtra("asset_tag") : intent.getStringExtra("tag");
                if (t != null && !t.trim().isEmpty()) {
                    editor.putString("asset_tag", t.trim());
                    editor.putBoolean("is_enrolled", true);
                    changed = true;
                }
            }
            if (changed) {
                editor.apply();
                if (prefs.getBoolean("is_enrolled", false)) {
                    WatchdogJobService.schedulePeriodic(this);
                    HeartbeatService.triggerImmediateSync(this);
                }
                runJs("if (typeof loadConfig === 'function') { loadConfig(); }");
            }
        }
    }

    @Override
    protected void onResume() {
        super.onResume();
        SharedPreferences prefs = getSharedPreferences(MdmPolicyManager.PREFS_NAME, Context.MODE_PRIVATE);
        boolean isLocked = prefs.getBoolean("is_locked", false);
        if (isLocked) {
            setLockState(true);
        }
        if (prefs.getBoolean("is_enrolled", false)) {
            WatchdogJobService.schedulePeriodic(this);
            maybeRequestBatteryOptimizationExemption(prefs);
            long now = System.currentTimeMillis();
            if (now - lastResumeSyncMs > 15000L) {
                lastResumeSyncMs = now;
                HeartbeatService.triggerImmediateSync(this);
            }
        }
        runJs("if (typeof loadConfig === 'function') { loadConfig(); }");
    }

    private void maybeRequestBatteryOptimizationExemption(SharedPreferences prefs) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.M) return;
        if (prefs.getBoolean("battery_opt_prompted", false)) return;

        try {
            DevicePolicyManager dpm = (DevicePolicyManager) getSystemService(Context.DEVICE_POLICY_SERVICE);
            if (MdmPolicyManager.isDeviceOwner(dpm, getPackageName())) {
                return;
            }
            PowerManager pm = (PowerManager) getSystemService(Context.POWER_SERVICE);
            if (pm != null && !pm.isIgnoringBatteryOptimizations(getPackageName())) {
                prefs.edit().putBoolean("battery_opt_prompted", true).apply();
                Intent intent = new Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS);
                intent.setData(Uri.parse("package:" + getPackageName()));
                startActivity(intent);
            }
        } catch (Exception e) {
            Log.w("MainActivity", "Failed requesting battery optimization exemption: " + e.getMessage());
        }
    }

    @Override
    public void onBackPressed() {
        SharedPreferences prefs = getSharedPreferences("project_r_prefs", Context.MODE_PRIVATE);
        boolean isLocked = prefs.getBoolean("is_locked", false);
        if (isLocked) {
            // Abaikan tombol back saat akses layar sedang dikunci IT
            return;
        }
        super.onBackPressed();
    }

    @Override
    public boolean onKeyDown(int keyCode, KeyEvent event) {
        SharedPreferences prefs = getSharedPreferences("project_r_prefs", Context.MODE_PRIVATE);
        boolean isLocked = prefs.getBoolean("is_locked", false);
        if (isLocked) {
            if (keyCode == KeyEvent.KEYCODE_BACK
                    || keyCode == KeyEvent.KEYCODE_HOME
                    || keyCode == KeyEvent.KEYCODE_APP_SWITCH
                    || keyCode == KeyEvent.KEYCODE_MENU
                    || keyCode == KeyEvent.KEYCODE_SEARCH) {
                return true; // Blokir total aksi tombol navigasi fisik & virtual
            }
        }
        return super.onKeyDown(keyCode, event);
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        SharedPreferences prefs = getSharedPreferences("project_r_prefs", Context.MODE_PRIVATE);
        boolean isLocked = prefs.getBoolean("is_locked", false);
        if (isLocked && !hasFocus) {
            try {
                Intent closeDialogs = new Intent(Intent.ACTION_CLOSE_SYSTEM_DIALOGS);
                sendBroadcast(closeDialogs);
            } catch (Exception ignored) {}

            try {
                Intent bringToFront = new Intent(this, MainActivity.class);
                bringToFront.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_REORDER_TO_FRONT);
                startActivity(bringToFront);
            } catch (Exception ignored) {}
        }
    }

    @Override
    protected void onPause() {
        super.onPause();
        SharedPreferences prefs = getSharedPreferences("project_r_prefs", Context.MODE_PRIVATE);
        boolean isLocked = prefs.getBoolean("is_locked", false);
        if (isLocked) {
            try {
                Intent bringToFront = new Intent(this, MainActivity.class);
                bringToFront.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_REORDER_TO_FRONT);
                startActivity(bringToFront);
            } catch (Exception ignored) {}
        }
    }

    @Override
    protected void onUserLeaveHint() {
        super.onUserLeaveHint();
        SharedPreferences prefs = getSharedPreferences("project_r_prefs", Context.MODE_PRIVATE);
        boolean isLocked = prefs.getBoolean("is_locked", false);
        if (isLocked) {
            // Paksa kembali ke depan seketika jika ada upaya meninggalkan aplikasi
            try {
                Intent bringToFront = new Intent(this, MainActivity.class);
                bringToFront.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_REORDER_TO_FRONT);
                startActivity(bringToFront);
            } catch (Exception ignored) {}
        }
    }

    public void runJs(String script) {
        new Handler(Looper.getMainLooper()).post(() -> {
            if (webView != null) {
                webView.evaluateJavascript(script, null);
            }
        });
    }

    public void setLockState(boolean isLocked) {
        new Handler(Looper.getMainLooper()).post(() -> {
            if (webView != null) {
                webView.evaluateJavascript("if (typeof setLockdownMode === 'function') { setLockdownMode(" + isLocked + "); }", null);
            }

            try {
                DevicePolicyManager dpm = (DevicePolicyManager) getSystemService(Context.DEVICE_POLICY_SERVICE);
                ComponentName admin = new ComponentName(this, ProjectRDeviceAdminReceiver.class);
                ActivityManager am = (ActivityManager) getSystemService(Context.ACTIVITY_SERVICE);

                if (isLocked) {
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O_MR1) {
                        setShowWhenLocked(true);
                        setTurnScreenOn(true);
                    } else {
                        getWindow().addFlags(WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED
                                | WindowManager.LayoutParams.FLAG_DISMISS_KEYGUARD
                                | WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON);
                    }
                    getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);

                    if (dpm != null && dpm.isDeviceOwnerApp(getPackageName())) {
                        String[] lockPackages = new String[]{
                                getPackageName(),
                                "com.android.dialer",
                                "com.google.android.dialer",
                                "com.samsung.android.dialer"
                        };
                        dpm.setLockTaskPackages(admin, lockPackages);
                        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                            // Matikan tombol Home, Recent Apps, dan notification shade atas total
                            dpm.setLockTaskFeatures(admin, DevicePolicyManager.LOCK_TASK_FEATURE_NONE);
                        }
                    }

                    if (am != null && am.getLockTaskModeState() == ActivityManager.LOCK_TASK_MODE_NONE) {
                        try {
                            startLockTask();
                            Log.i("MainActivity", "True Kiosk Lock Task Mode activated successfully.");
                        } catch (Exception e) {
                            Log.e("MainActivity", "startLockTask error: " + e.getMessage());
                        }
                    }
                } else {
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O_MR1) {
                        setShowWhenLocked(false);
                        setTurnScreenOn(false);
                    } else {
                        getWindow().clearFlags(WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED
                                | WindowManager.LayoutParams.FLAG_DISMISS_KEYGUARD
                                | WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON);
                    }
                    getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);

                    if (am != null && am.getLockTaskModeState() != ActivityManager.LOCK_TASK_MODE_NONE) {
                        try {
                            stopLockTask();
                            Log.i("MainActivity", "Lock Task Mode deactivated successfully.");
                        } catch (Exception e) {
                            Log.e("MainActivity", "stopLockTask error: " + e.getMessage());
                        }
                    }
                }
            } catch (Exception e) {
                Log.e("MainActivity", "setLockState error: " + e.getMessage());
            }
        });
    }

    public void syncNow() {
        new Thread(() -> {
            try {
                // Heartbeat sync
                Thread.sleep(800);
            } catch (Exception ignored) {}

            new Handler(Looper.getMainLooper()).post(() -> {
                if (webView != null) {
                    webView.evaluateJavascript("if (typeof onNativeSyncComplete === 'function') { onNativeSyncComplete(true); }", null);
                }
            });
        }).start();
    }

    @Override
    protected void onDestroy() {
        try {
            unregisterReceiver(lockReceiver);
        } catch (Exception ignored) {}
        super.onDestroy();
    }
}
