package com.projectr.agent;

import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.util.DisplayMetrics;
import android.util.Log;
import android.view.WindowManager;

import androidx.core.app.NotificationCompat;

import org.json.JSONObject;

import java.net.URLEncoder;
import java.nio.ByteBuffer;
import java.security.SecureRandom;
import java.security.cert.X509Certificate;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;

import javax.net.ssl.SSLContext;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;
import okhttp3.WebSocket;
import okhttp3.WebSocketListener;
import okio.ByteString;

public class RemoteRelayClient {
    private static final String TAG = "ProjectRRemote";
    private static final String ALERT_CHANNEL_ID = "project_r_remote_alerts";
    private static final int ALERT_NOTIF_ID = 2401;
    private static final int CHAT_NOTIF_ID = 2402;

    public static final String ACTION_CONSENT_CLOSED = "com.projectr.agent.REMOTE_CONSENT_CLOSED";
    public static final String ACTION_CHAT_RECEIVED = "com.projectr.agent.REMOTE_CHAT_RECEIVED";
    public static final String ACTION_SESSION_ENDED = "com.projectr.agent.REMOTE_SESSION_ENDED";

    private static volatile RemoteRelayClient instance;

    private final Context appContext;
    private final OkHttpClient httpClient;
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private final Object lock = new Object();

    private WebSocket webSocket;
    private boolean started = false;
    private boolean reconnecting = false;
    private String currentWsUrl = "";

    private final AtomicBoolean consentApproved = new AtomicBoolean(false);
    private final AtomicBoolean promptActive = new AtomicBoolean(false);
    private final AtomicBoolean streaming = new AtomicBoolean(false);
    private final AtomicBoolean waitingAck = new AtomicBoolean(false);
    private final AtomicInteger frameSeq = new AtomicInteger(0);

    private volatile long lastFrameSentMs = 0L;
    private volatile int targetFps = 15;
    private volatile int jpegQuality = 60;

    private volatile int physicalWidth = 1080;
    private volatile int physicalHeight = 1920;
    private volatile int captureWidth = 540;
    private volatile int captureHeight = 960;

    private final List<ChatItem> sessionChats = Collections.synchronizedList(new ArrayList<>());
    private volatile ChatListener chatListener;

    public interface ChatListener {
        void onChatMessage(ChatItem item);
        void onSessionEnded(String reason);
    }

    public static class ChatItem {
        public final String sender;
        public final String senderName;
        public final String text;
        public final String timestamp;

        public ChatItem(String sender, String senderName, String text, String timestamp) {
            this.sender = sender;
            this.senderName = senderName;
            this.text = text;
            this.timestamp = timestamp;
        }
    }

    private RemoteRelayClient(Context context) {
        this.appContext = context.getApplicationContext();
        this.httpClient = buildOkHttpClient();
        refreshMetrics();
    }

    public static RemoteRelayClient getInstance(Context context) {
        if (instance == null) {
            synchronized (RemoteRelayClient.class) {
                if (instance == null) {
                    instance = new RemoteRelayClient(context);
                }
            }
        }
        return instance;
    }

    private OkHttpClient buildOkHttpClient() {
        OkHttpClient.Builder builder = new OkHttpClient.Builder()
                .connectTimeout(10, TimeUnit.SECONDS)
                .readTimeout(0, TimeUnit.MILLISECONDS)
                .pingInterval(25, TimeUnit.SECONDS);

        try {
            TrustManager[] trustAllCerts = new TrustManager[]{
                    new X509TrustManager() {
                        @Override public void checkClientTrusted(X509Certificate[] chain, String authType) {}
                        @Override public void checkServerTrusted(X509Certificate[] chain, String authType) {}
                        @Override public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
                    }
            };
            SSLContext sslContext = SSLContext.getInstance("TLS");
            sslContext.init(null, trustAllCerts, new SecureRandom());
            builder.sslSocketFactory(sslContext.getSocketFactory(), (X509TrustManager) trustAllCerts[0]);
            builder.hostnameVerifier((hostname, session) -> true);
        } catch (Exception ignored) {}

        return builder.build();
    }

    private void refreshMetrics() {
        try {
            WindowManager wm = (WindowManager) appContext.getSystemService(Context.WINDOW_SERVICE);
            if (wm != null) {
                DisplayMetrics dm = new DisplayMetrics();
                wm.getDefaultDisplay().getRealMetrics(dm);
                if (dm.widthPixels > 0 && dm.heightPixels > 0) {
                    physicalWidth = dm.widthPixels;
                    physicalHeight = dm.heightPixels;
                    computeCaptureSize();
                }
            }
        } catch (Exception ignored) {}
    }

    private void computeCaptureSize() {
        int maxDim = 960;
        int w = physicalWidth;
        int h = physicalHeight;
        if (w <= maxDim && h <= maxDim) {
            captureWidth = Math.max(2, w - (w % 2));
            captureHeight = Math.max(2, h - (h % 2));
            return;
        }
        float scale = (w >= h) ? ((float) maxDim / w) : ((float) maxDim / h);
        captureWidth = Math.max(2, Math.round(w * scale) & ~1);
        captureHeight = Math.max(2, Math.round(h * scale) & ~1);
    }

    public int getPhysicalWidth() { return physicalWidth; }
    public int getPhysicalHeight() { return physicalHeight; }
    public int getCaptureWidth() { return captureWidth; }
    public int getCaptureHeight() { return captureHeight; }
    public int getTargetFps() { return targetFps; }
    public int getJpegQuality() { return jpegQuality; }
    public boolean isStreaming() { return streaming.get(); }
    public boolean isConsentApproved() { return consentApproved.get(); }

    public void setChatListener(ChatListener listener) {
        this.chatListener = listener;
    }

    public List<ChatItem> getSessionChats() {
        synchronized (sessionChats) {
            return new ArrayList<>(sessionChats);
        }
    }

    public void ensureConnected() {
        SharedPreferences prefs = appContext.getSharedPreferences("project_r_prefs", Context.MODE_PRIVATE);
        boolean isEnrolled = prefs.getBoolean("is_enrolled", false);
        String serverUrl = prefs.getString("server_url", "").trim();
        String token = prefs.getString("token", "").trim();
        String tag = prefs.getString("asset_tag", "").trim();

        if (!isEnrolled || serverUrl.isEmpty() || token.isEmpty() || tag.isEmpty()) {
            disconnect();
            return;
        }

        String wsUrl = buildRelayWsUrl(serverUrl, NetworkHelper.getDeviceId(appContext), token);
        synchronized (lock) {
            if (started && webSocket != null && wsUrl.equals(currentWsUrl)) {
                return;
            }
            started = true;
            currentWsUrl = wsUrl;
            connectInternal();
        }
    }

    public void disconnect() {
        synchronized (lock) {
            started = false;
            currentWsUrl = "";
            if (webSocket != null) {
                try { webSocket.close(1000, "Client shutdown"); } catch (Exception ignored) {}
                webSocket = null;
            }
        }
        stopRemoteSessionLocally("Sesi dihentikan.");
    }

    private String buildRelayWsUrl(String serverUrl, String deviceId, String token) {
        String wsBase = serverUrl.trim().replaceAll("/+$", "");
        if (wsBase.startsWith("https://")) {
            wsBase = "wss://" + wsBase.substring(8);
        } else if (wsBase.startsWith("http://")) {
            wsBase = "ws://" + wsBase.substring(7);
        } else if (!wsBase.startsWith("ws://") && !wsBase.startsWith("wss://")) {
            wsBase = "wss://" + wsBase;
        }
        try {
            return wsBase + "/remote/ws/agent?device_id="
                    + URLEncoder.encode(deviceId, "UTF-8")
                    + "&token=" + URLEncoder.encode(token, "UTF-8");
        } catch (Exception e) {
            return wsBase + "/remote/ws/agent?device_id=" + deviceId + "&token=" + token;
        }
    }

    private void connectInternal() {
        String url;
        synchronized (lock) {
            if (!started || currentWsUrl.isEmpty()) return;
            if (webSocket != null) {
                try { webSocket.cancel(); } catch (Exception ignored) {}
                webSocket = null;
            }
            url = currentWsUrl;
        }

        Log.i(TAG, "Dialing Go remote relay: " + url.replaceAll("token=[^&]+", "token=***"));
        Request req = new Request.Builder().url(url).build();
        WebSocket ws = httpClient.newWebSocket(req, new WebSocketListener() {
            @Override
            public void onOpen(WebSocket ws, Response response) {
                Log.i(TAG, "Connected to Project R Go Remote Relay.");
                sendInit();
            }

            @Override
            public void onMessage(WebSocket ws, String text) {
                handleCommand(text);
            }

            @Override
            public void onClosing(WebSocket ws, int code, String reason) {
                ws.close(1000, null);
                scheduleReconnect();
            }

            @Override
            public void onClosed(WebSocket ws, int code, String reason) {
                scheduleReconnect();
            }

            @Override
            public void onFailure(WebSocket ws, Throwable t, Response response) {
                Log.w(TAG, "Relay connection failed: " + (t != null ? t.getMessage() : "unknown"));
                scheduleReconnect();
            }
        });

        synchronized (lock) {
            webSocket = ws;
        }
    }

    private void scheduleReconnect() {
        synchronized (lock) {
            if (!started || reconnecting) return;
            reconnecting = true;
            webSocket = null;
        }
        mainHandler.postDelayed(() -> {
            synchronized (lock) {
                reconnecting = false;
                if (!started) return;
            }
            connectInternal();
        }, 5000L);
    }

    private void sendInit() {
        try {
            refreshMetrics();
            JSONObject payload = new JSONObject();
            payload.put("device_id", NetworkHelper.getDeviceId(appContext));
            payload.put("screen_width", captureWidth);
            payload.put("screen_height", captureHeight);
            payload.put("physical_width", physicalWidth);
            payload.put("physical_height", physicalHeight);
            payload.put("version", NetworkHelper.getAppVersion(appContext));

            JSONObject env = new JSONObject();
            env.put("type", "init");
            env.put("payload", payload);
            sendJson(env);
        } catch (Exception ignored) {}
    }

    public boolean sendJson(JSONObject obj) {
        WebSocket ws;
        synchronized (lock) {
            ws = this.webSocket;
        }
        if (ws == null) return false;
        return ws.send(obj.toString());
    }

    public boolean shouldSkipFrameForBackpressure() {
        if (!streaming.get()) return true;
        if (waitingAck.get() && (System.currentTimeMillis() - lastFrameSentMs) < 180L) {
            return true;
        }
        return false;
    }

    public boolean sendJpegFrame(byte[] jpegBytes) {
        if (jpegBytes == null || jpegBytes.length == 0 || !streaming.get()) return false;
        WebSocket ws;
        synchronized (lock) {
            ws = this.webSocket;
        }
        if (ws == null) return false;

        int seq = frameSeq.incrementAndGet();
        ByteBuffer buf = ByteBuffer.allocate(7 + jpegBytes.length);
        buf.put((byte) 0x01);
        buf.putInt(seq);
        buf.putShort((short) 1); // Format 1 = JPEG
        buf.put(jpegBytes);

        waitingAck.set(true);
        lastFrameSentMs = System.currentTimeMillis();
        return ws.send(ByteString.of(buf.array()));
    }

    public void onConsentApproved() {
        consentApproved.set(true);
        promptActive.set(false);
        streaming.set(true);
        waitingAck.set(false);
        try {
            JSONObject obj = new JSONObject();
            obj.put("type", "consent_response");
            obj.put("approved", true);
            sendJson(obj);
        } catch (Exception ignored) {}
    }

    public void onConsentRejected(String reason) {
        consentApproved.set(false);
        promptActive.set(false);
        streaming.set(false);
        try {
            JSONObject obj = new JSONObject();
            obj.put("type", "consent_response");
            obj.put("approved", false);
            obj.put("reason", reason != null ? reason : "Pengguna menolak permintaan akses remote.");
            sendJson(obj);
        } catch (Exception ignored) {}
    }

    public void onClientEndedSession() {
        stopRemoteSessionLocally("Sesi diakhiri oleh pengguna di perangkat Android.");
        try {
            JSONObject obj = new JSONObject();
            obj.put("type", "session_ended");
            obj.put("reason", "Sesi diakhiri oleh pengguna di perangkat Android.");
            sendJson(obj);
        } catch (Exception ignored) {}
    }

    public void sendClientChatMessage(String text, String timestamp) {
        if (text == null || text.trim().isEmpty()) return;
        ChatItem item = new ChatItem("client", "Anda", text.trim(), timestamp);
        sessionChats.add(item);
        try {
            JSONObject obj = new JSONObject();
            obj.put("type", "chat_message");
            obj.put("sender", "client");
            obj.put("sender_name", "Pengguna Android");
            obj.put("text", text.trim());
            obj.put("timestamp", timestamp);
            sendJson(obj);
        } catch (Exception ignored) {}
    }

    private void handleCommand(String raw) {
        try {
            JSONObject env = new JSONObject(raw);
            String type = env.optString("type", "");
            JSONObject payload = env.optJSONObject("payload");

            switch (type) {
                case "frame_ack":
                    waitingAck.set(false);
                    break;

                case "consent_request": {
                    JSONObject p = payload != null ? payload : env;
                    String adminName = p.optString("admin_name", "Admin IT");
                    String adminRole = p.optString("admin_role", "Administrator");
                    triggerConsentPrompt(adminName, adminRole);
                    break;
                }

                case "status": {
                    if (payload == null) break;
                    String code = payload.optString("code", "");
                    if ("viewer_joined".equals(code)) {
                        triggerConsentPrompt("Admin IT", "Administrator");
                    } else if ("no_viewers".equals(code)) {
                        stopRemoteSessionLocally("Semua viewer telah keluar.");
                    }
                    break;
                }

                case "quality": {
                    if (payload == null) break;
                    int fps = payload.optInt("fps", 0);
                    int q = payload.optInt("quality", 0);
                    if (fps > 0 && fps <= 30) targetFps = fps;
                    if (q >= 15 && q <= 95) jpegQuality = q;
                    break;
                }

                case "request_keyframe":
                case "refresh":
                    waitingAck.set(false);
                    break;

                case "chat_message": {
                    JSONObject p = payload != null ? payload : env;
                    String sender = p.optString("sender", "admin");
                    String senderName = p.optString("sender_name", "Admin IT");
                    String text = p.optString("text", "");
                    String ts = p.optString("timestamp", "");
                    if (!text.isEmpty()) {
                        ChatItem item = new ChatItem(sender, senderName, text, ts);
                        sessionChats.add(item);
                        ChatListener listener = this.chatListener;
                        if (listener != null) {
                            mainHandler.post(() -> listener.onChatMessage(item));
                        }
                        showChatNotification(senderName, text);
                    }
                    break;
                }

                case "clipboard": {
                    JSONObject p = payload != null ? payload : env;
                    String clipText = p.optString("text", "");
                    if (!clipText.isEmpty()) {
                        mainHandler.post(() -> {
                            try {
                                ClipboardManager cm = (ClipboardManager) appContext.getSystemService(Context.CLIPBOARD_SERVICE);
                                if (cm != null) {
                                    cm.setPrimaryClip(ClipData.newPlainText("ProjectR", clipText));
                                }
                            } catch (Exception ignored) {}
                        });
                    }
                    break;
                }

                case "mouse_down":
                    if (consentApproved.get() && payload != null) {
                        float mx = mapX(payload.optInt("x", 0));
                        float my = mapY(payload.optInt("y", 0));
                        ProjectRAccessibilityService.handleMouseDown(mx, my);
                    }
                    break;

                case "mouse_up":
                    if (consentApproved.get() && payload != null) {
                        float mx = mapX(payload.optInt("x", 0));
                        float my = mapY(payload.optInt("y", 0));
                        ProjectRAccessibilityService.handleMouseUp(mx, my, physicalWidth, physicalHeight);
                    }
                    break;

                case "wheel":
                    if (consentApproved.get() && payload != null) {
                        int dy = payload.optInt("delta_y", 0);
                        ProjectRAccessibilityService.handleWheel(dy, physicalWidth, physicalHeight);
                    }
                    break;

                case "key_down":
                    if (consentApproved.get() && payload != null) {
                        ProjectRAccessibilityService.handleKeyDown(payload.optString("key", ""));
                    }
                    break;

                case "end_session":
                    stopRemoteSessionLocally("Sesi diakhiri oleh administrator.");
                    break;
            }
        } catch (Exception e) {
            Log.w(TAG, "Error handling command: " + e.getMessage());
        }
    }

    private float mapX(int x) {
        if (captureWidth <= 0) return x;
        return ((float) x * physicalWidth) / captureWidth;
    }

    private float mapY(int y) {
        if (captureHeight <= 0) return y;
        return ((float) y * physicalHeight) / captureHeight;
    }

    private void triggerConsentPrompt(String adminName, String adminRole) {
        if (consentApproved.get() && streaming.get()) {
            try {
                JSONObject obj = new JSONObject();
                obj.put("type", "consent_response");
                obj.put("approved", true);
                sendJson(obj);
            } catch (Exception ignored) {}
            return;
        }

        if (!promptActive.compareAndSet(false, true)) {
            return;
        }

        sessionChats.clear();
        streaming.set(false);
        consentApproved.set(false);

        Intent intent = new Intent(appContext, RemoteConsentActivity.class);
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_SINGLE_TOP | Intent.FLAG_ACTIVITY_CLEAR_TOP);
        intent.putExtra("admin_name", adminName);
        intent.putExtra("admin_role", adminRole);

        showConsentNotification(adminName, adminRole, intent);
        mainHandler.post(() -> {
            try {
                appContext.startActivity(intent);
            } catch (Exception e) {
                Log.w(TAG, "Direct startActivity blocked by Android background rules; relying on notification: " + e.getMessage());
            }
        });
    }

    public void stopRemoteSessionLocally(String reason) {
        promptActive.set(false);
        consentApproved.set(false);
        streaming.set(false);
        waitingAck.set(false);

        try {
            appContext.stopService(new Intent(appContext, RemoteScreenService.class));
        } catch (Exception ignored) {}

        NotificationManager nm = (NotificationManager) appContext.getSystemService(Context.NOTIFICATION_SERVICE);
        if (nm != null) {
            nm.cancel(ALERT_NOTIF_ID);
        }

        ChatListener listener = this.chatListener;
        if (listener != null) {
            mainHandler.post(() -> listener.onSessionEnded(reason));
        }
    }

    private void ensureAlertChannel(NotificationManager nm) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && nm != null) {
            NotificationChannel ch = new NotificationChannel(
                    ALERT_CHANNEL_ID,
                    "Project R Remote Support",
                    NotificationManager.IMPORTANCE_HIGH
            );
            nm.createNotificationChannel(ch);
        }
    }

    private void showConsentNotification(String adminName, String adminRole, Intent activityIntent) {
        NotificationManager nm = (NotificationManager) appContext.getSystemService(Context.NOTIFICATION_SERVICE);
        if (nm == null) return;
        ensureAlertChannel(nm);

        int flags = PendingIntent.FLAG_UPDATE_CURRENT | (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M ? PendingIntent.FLAG_IMMUTABLE : 0);
        PendingIntent pi = PendingIntent.getActivity(appContext, ALERT_NOTIF_ID, activityIntent, flags);

        NotificationCompat.Builder b = new NotificationCompat.Builder(appContext, ALERT_CHANNEL_ID)
                .setSmallIcon(android.R.drawable.ic_menu_view)
                .setContentTitle("Permintaan Remote Desktop Project R")
                .setContentText(adminName + " (" + adminRole + ") meminta izin akses layar.")
                .setPriority(NotificationCompat.PRIORITY_HIGH)
                .setCategory(NotificationCompat.CATEGORY_CALL)
                .setAutoCancel(true)
                .setContentIntent(pi)
                .setFullScreenIntent(pi, true);

        nm.notify(ALERT_NOTIF_ID, b.build());
    }

    private void showChatNotification(String senderName, String text) {
        NotificationManager nm = (NotificationManager) appContext.getSystemService(Context.NOTIFICATION_SERVICE);
        if (nm == null) return;
        ensureAlertChannel(nm);

        Intent intent = new Intent(appContext, RemoteConsentActivity.class);
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_SINGLE_TOP);
        int flags = PendingIntent.FLAG_UPDATE_CURRENT | (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M ? PendingIntent.FLAG_IMMUTABLE : 0);
        PendingIntent pi = PendingIntent.getActivity(appContext, CHAT_NOTIF_ID, intent, flags);

        NotificationCompat.Builder b = new NotificationCompat.Builder(appContext, ALERT_CHANNEL_ID)
                .setSmallIcon(android.R.drawable.stat_notify_chat)
                .setContentTitle("Pesan IT (" + senderName + ")")
                .setContentText(text)
                .setStyle(new NotificationCompat.BigTextStyle().bigText(text))
                .setPriority(NotificationCompat.PRIORITY_HIGH)
                .setAutoCancel(true)
                .setContentIntent(pi);

        nm.notify(CHAT_NOTIF_ID, b.build());
    }
}
