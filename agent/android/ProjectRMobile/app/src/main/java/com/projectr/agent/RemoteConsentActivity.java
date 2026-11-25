package com.projectr.agent;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.graphics.Color;
import android.graphics.drawable.GradientDrawable;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.os.Bundle;
import android.os.CountDownTimer;
import android.provider.Settings;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import androidx.appcompat.app.AppCompatActivity;

import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;

public class RemoteConsentActivity extends AppCompatActivity implements RemoteRelayClient.ChatListener {
    private static final int REQ_MEDIA_PROJECTION = 4401;

    private LinearLayout consentCard;
    private LinearLayout sessionCard;
    private LinearLayout chatPanel;
    private ScrollView chatScroll;
    private EditText inputChat;
    private TextView txtTimer;
    private TextView txtTouchStatus;
    private CountDownTimer countdownTimer;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        String adminName = getIntent() != null ? getIntent().getStringExtra("admin_name") : "Admin IT";
        String adminRole = getIntent() != null ? getIntent().getStringExtra("admin_role") : "Administrator";
        if (TextUtils.isEmpty(adminName)) adminName = "Admin IT";
        if (TextUtils.isEmpty(adminRole)) adminRole = "Administrator";

        buildUi(adminName, adminRole);

        RemoteRelayClient relay = RemoteRelayClient.getInstance(this);
        relay.setChatListener(this);

        if (relay.isConsentApproved() && relay.isStreaming()) {
            showSessionMode();
            for (RemoteRelayClient.ChatItem c : relay.getSessionChats()) {
                appendChatBubble(c);
            }
        } else {
            startCountdown();
        }
    }

    private void buildUi(String adminName, String adminRole) {
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(Color.parseColor("#09090b"));
        root.setPadding(40, 56, 40, 40);
        root.setGravity(Gravity.CENTER);

        // 1. CONSENT CARD
        consentCard = createCard();
        TextView badge = new TextView(this);
        badge.setText("PROJECT R REMOTE SUPPORT");
        badge.setTextColor(Color.parseColor("#38bdf8"));
        badge.setTextSize(11f);
        badge.setPadding(0, 0, 0, 10);
        consentCard.addView(badge);

        TextView title = new TextView(this);
        title.setText("Permintaan Izin Remote Layar");
        title.setTextColor(Color.WHITE);
        title.setTextSize(19f);
        title.setTypeface(null, android.graphics.Typeface.BOLD);
        consentCard.addView(title);

        TextView desc = new TextView(this);
        desc.setText(adminName + " (" + adminRole + ") meminta izin untuk melihat layar perangkat Android Anda guna membantu dukungan teknis.");
        desc.setTextColor(Color.parseColor("#cbd5e1"));
        desc.setTextSize(13.5f);
        desc.setPadding(0, 18, 0, 22);
        consentCard.addView(desc);

        txtTouchStatus = new TextView(this);
        boolean canTouch = ProjectRAccessibilityService.isAvailable();
        txtTouchStatus.setText(canTouch
                ? "Kontrol sentuh jarak jauh (Accessibility): AKTIF"
                : "Mode tampilan layar aktif. Untuk izinkan kontrol sentuh jarak jauh, aktifkan Aksesibilitas Project R.");
        txtTouchStatus.setTextColor(Color.parseColor(canTouch ? "#34d399" : "#fbbf24"));
        txtTouchStatus.setTextSize(12f);
        txtTouchStatus.setPadding(0, 0, 0, 16);
        consentCard.addView(txtTouchStatus);

        if (!canTouch) {
            Button btnAccess = makeButton("Aktifkan Kontrol Sentuh (Opsional)", "#1e293b", "#93c5fd");
            btnAccess.setOnClickListener(v -> {
                try {
                    startActivity(new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS));
                } catch (Exception ignored) {}
            });
            consentCard.addView(btnAccess, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
            addSpacer(consentCard, 18);
        }

        txtTimer = new TextView(this);
        txtTimer.setTextColor(Color.parseColor("#fbbf24"));
        txtTimer.setTextSize(12f);
        txtTimer.setPadding(0, 0, 0, 24);
        consentCard.addView(txtTimer);

        LinearLayout btnRow = new LinearLayout(this);
        btnRow.setOrientation(LinearLayout.HORIZONTAL);
        Button btnReject = makeButton("Tolak", "#450a0a", "#f87171");
        Button btnApprove = makeButton("Izinkan & Bagikan", "#065f46", "#ecfdf5");

        LinearLayout.LayoutParams lpLeft = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f);
        lpLeft.setMarginEnd(16);
        LinearLayout.LayoutParams lpRight = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1.3f);
        btnRow.addView(btnReject, lpLeft);
        btnRow.addView(btnApprove, lpRight);
        consentCard.addView(btnRow);

        btnReject.setOnClickListener(v -> {
            cancelTimer();
            RemoteRelayClient.getInstance(this).onConsentRejected("Pengguna menolak permintaan akses remote.");
            finish();
        });

        btnApprove.setOnClickListener(v -> {
            cancelTimer();
            MediaProjectionManager mpm = (MediaProjectionManager) getSystemService(Context.MEDIA_PROJECTION_SERVICE);
            if (mpm != null) {
                startActivityForResult(mpm.createScreenCaptureIntent(), REQ_MEDIA_PROJECTION);
            } else {
                Toast.makeText(this, "MediaProjection tidak didukung di perangkat ini.", Toast.LENGTH_LONG).show();
            }
        });

        root.addView(consentCard, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        // 2. ACTIVE SESSION + LIVE CHAT CARD
        sessionCard = createCard();
        sessionCard.setVisibility(View.GONE);

        TextView sTitle = new TextView(this);
        sTitle.setText("● Sesi Remote Aktif • " + adminName);
        sTitle.setTextColor(Color.parseColor("#34d399"));
        sTitle.setTextSize(15f);
        sTitle.setTypeface(null, android.graphics.Typeface.BOLD);
        sTitle.setPadding(0, 0, 0, 14);
        sessionCard.addView(sTitle);

        chatScroll = new ScrollView(this);
        chatPanel = new LinearLayout(this);
        chatPanel.setOrientation(LinearLayout.VERTICAL);
        chatScroll.addView(chatPanel);
        LinearLayout.LayoutParams scrollLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 520);
        sessionCard.addView(chatScroll, scrollLp);

        LinearLayout inputRow = new LinearLayout(this);
        inputRow.setOrientation(LinearLayout.HORIZONTAL);
        inputRow.setPadding(0, 16, 0, 16);

        inputChat = new EditText(this);
        inputChat.setHint("Balas pesan admin IT...");
        inputChat.setHintTextColor(Color.parseColor("#64748b"));
        inputChat.setTextColor(Color.WHITE);
        inputChat.setBackgroundColor(Color.parseColor("#09090b"));
        inputChat.setPadding(24, 20, 24, 20);
        inputChat.setTextSize(13f);

        Button btnSend = makeButton("Kirim", "#059669", "#ffffff");
        btnSend.setOnClickListener(v -> {
            String msg = inputChat.getText() != null ? inputChat.getText().toString().trim() : "";
            if (msg.isEmpty()) return;
            inputChat.setText("");
            String now = new SimpleDateFormat("HH:mm", Locale.getDefault()).format(new Date());
            RemoteRelayClient.getInstance(this).sendClientChatMessage(msg, now);
            appendChatBubble(new RemoteRelayClient.ChatItem("client", "Anda", msg, now));
        });

        LinearLayout.LayoutParams inpLp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f);
        inpLp.setMarginEnd(12);
        inputRow.addView(inputChat, inpLp);
        inputRow.addView(btnSend, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        sessionCard.addView(inputRow);

        Button btnEnd = makeButton("Akhiri Sesi Remote", "#7f1d1d", "#fecaca");
        btnEnd.setOnClickListener(v -> {
            RemoteRelayClient.getInstance(this).onClientEndedSession();
            finish();
        });
        sessionCard.addView(btnEnd, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        root.addView(sessionCard, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        setContentView(root);
    }

    @Override
    protected void onResume() {
        super.onResume();
        if (txtTouchStatus != null) {
            boolean canTouch = ProjectRAccessibilityService.isAvailable();
            txtTouchStatus.setText(canTouch
                    ? "Kontrol sentuh jarak jauh (Accessibility): AKTIF"
                    : "Mode tampilan layar aktif. Untuk izinkan kontrol sentuh jarak jauh, aktifkan Aksesibilitas Project R.");
            txtTouchStatus.setTextColor(Color.parseColor(canTouch ? "#34d399" : "#fbbf24"));
        }
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode == REQ_MEDIA_PROJECTION) {
            if (resultCode == Activity.RESULT_OK && data != null) {
                Intent svc = new Intent(this, RemoteScreenService.class);
                svc.putExtra(RemoteScreenService.EXTRA_RESULT_CODE, resultCode);
                svc.putExtra(RemoteScreenService.EXTRA_RESULT_DATA, data);
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                    startForegroundService(svc);
                } else {
                    startService(svc);
                }
                showSessionMode();
            } else {
                RemoteRelayClient.getInstance(this).onConsentRejected("Izin tangkapan layar Android dibatalkan pengguna.");
                finish();
            }
        }
    }

    private void showSessionMode() {
        cancelTimer();
        consentCard.setVisibility(View.GONE);
        sessionCard.setVisibility(View.VISIBLE);
    }

    private void startCountdown() {
        cancelTimer();
        countdownTimer = new CountDownTimer(120_000L, 1000L) {
            @Override
            public void onTick(long millisUntilFinished) {
                txtTimer.setText("Waktu konfirmasi tersisa: " + (millisUntilFinished / 1000) + " detik");
            }

            @Override
            public void onFinish() {
                RemoteRelayClient.getInstance(RemoteConsentActivity.this)
                        .onConsentRejected("Waktu konfirmasi habis tanpa respons.");
                finish();
            }
        }.start();
    }

    private void cancelTimer() {
        if (countdownTimer != null) {
            countdownTimer.cancel();
            countdownTimer = null;
        }
    }

    private void appendChatBubble(RemoteRelayClient.ChatItem item) {
        if (chatPanel == null) return;
        boolean isClient = "client".equalsIgnoreCase(item.sender);
        TextView tv = new TextView(this);
        tv.setText((isClient ? "Anda" : item.senderName) + " • " + item.timestamp + "\n" + item.text);
        tv.setTextColor(Color.WHITE);
        tv.setTextSize(12.5f);
        tv.setPadding(24, 16, 24, 16);

        GradientDrawable bg = new GradientDrawable();
        bg.setCornerRadius(16f);
        bg.setColor(Color.parseColor(isClient ? "#065f46" : "#27272a"));
        tv.setBackground(bg);

        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.gravity = isClient ? Gravity.END : Gravity.START;
        lp.bottomMargin = 14;
        chatPanel.addView(tv, lp);

        if (chatScroll != null) {
            chatScroll.post(() -> chatScroll.fullScroll(View.FOCUS_DOWN));
        }
    }

    @Override
    public void onChatMessage(RemoteRelayClient.ChatItem item) {
        if (sessionCard.getVisibility() == View.VISIBLE) {
            appendChatBubble(item);
        }
    }

    @Override
    public void onSessionEnded(String reason) {
        finish();
    }

    @Override
    protected void onDestroy() {
        cancelTimer();
        RemoteRelayClient.getInstance(this).setChatListener(null);
        super.onDestroy();
    }

    private LinearLayout createCard() {
        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(44, 44, 44, 44);
        GradientDrawable bg = new GradientDrawable();
        bg.setCornerRadius(24f);
        bg.setColor(Color.parseColor("#18181b"));
        bg.setStroke(2, Color.parseColor("#27272a"));
        card.setBackground(bg);
        return card;
    }

    private Button makeButton(String label, String bgHex, String textHex) {
        Button b = new Button(this);
        b.setText(label);
        b.setAllCaps(false);
        b.setTextColor(Color.parseColor(textHex));
        b.setTextSize(13f);
        GradientDrawable bg = new GradientDrawable();
        bg.setCornerRadius(16f);
        bg.setColor(Color.parseColor(bgHex));
        b.setBackground(bg);
        b.setPadding(24, 22, 24, 22);
        return b;
    }

    private void addSpacer(LinearLayout parent, int px) {
        View v = new View(this);
        parent.addView(v, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, px));
    }
}
