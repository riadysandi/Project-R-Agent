package com.projectr.agent;

import android.accessibilityservice.AccessibilityService;
import android.accessibilityservice.GestureDescription;
import android.graphics.Path;
import android.os.Build;
import android.view.accessibility.AccessibilityEvent;

public class ProjectRAccessibilityService extends AccessibilityService {
    private static volatile ProjectRAccessibilityService instance;
    private static float downX = -1f;
    private static float downY = -1f;

    @Override
    protected void onServiceConnected() {
        super.onServiceConnected();
        instance = this;
    }

    @Override
    public void onAccessibilityEvent(AccessibilityEvent event) {
    }

    @Override
    public void onInterrupt() {
    }

    @Override
    public void onDestroy() {
        super.onDestroy();
        if (instance == this) {
            instance = null;
        }
    }

    public static boolean isAvailable() {
        return instance != null;
    }

    public static void handleMouseDown(float x, float y) {
        downX = x;
        downY = y;
    }

    public static void handleMouseUp(float x, float y, int maxW, int maxH) {
        ProjectRAccessibilityService svc = instance;
        if (svc == null || Build.VERSION.SDK_INT < Build.VERSION_CODES.N) return;

        float sx = clamp(downX >= 0 ? downX : x, 0, Math.max(1, maxW - 1));
        float sy = clamp(downY >= 0 ? downY : y, 0, Math.max(1, maxH - 1));
        float ex = clamp(x, 0, Math.max(1, maxW - 1));
        float ey = clamp(y, 0, Math.max(1, maxH - 1));
        downX = -1f;
        downY = -1f;

        float dist = (float) Math.hypot(ex - sx, ey - sy);
        Path path = new Path();
        path.moveTo(sx, sy);
        long durationMs;
        if (dist < 18f) {
            durationMs = 55L;
        } else {
            path.lineTo(ex, ey);
            durationMs = 220L;
        }

        GestureDescription.StrokeDescription stroke = new GestureDescription.StrokeDescription(path, 0L, durationMs);
        svc.dispatchGesture(new GestureDescription.Builder().addStroke(stroke).build(), null, null);
    }

    public static void handleWheel(int deltaY, int maxW, int maxH) {
        ProjectRAccessibilityService svc = instance;
        if (svc == null || Build.VERSION.SDK_INT < Build.VERSION_CODES.N || deltaY == 0) return;

        float cx = maxW / 2f;
        float cy = maxH / 2f;
        float step = Math.min(350f, Math.max(140f, maxH * 0.25f));
        float toY = deltaY > 0 ? Math.max(20f, cy - step) : Math.min(maxH - 20f, cy + step);

        Path path = new Path();
        path.moveTo(cx, cy);
        path.lineTo(cx, toY);

        GestureDescription.StrokeDescription stroke = new GestureDescription.StrokeDescription(path, 0L, 180L);
        svc.dispatchGesture(new GestureDescription.Builder().addStroke(stroke).build(), null, null);
    }

    public static void handleKeyDown(String key) {
        ProjectRAccessibilityService svc = instance;
        if (svc == null || key == null) return;
        if ("Escape".equalsIgnoreCase(key) || "Backspace".equalsIgnoreCase(key) || "GoBack".equalsIgnoreCase(key)) {
            svc.performGlobalAction(GLOBAL_ACTION_BACK);
        } else if ("Home".equalsIgnoreCase(key)) {
            svc.performGlobalAction(GLOBAL_ACTION_HOME);
        }
    }

    private static float clamp(float v, float min, float max) {
        return Math.max(min, Math.min(max, v));
    }
}
