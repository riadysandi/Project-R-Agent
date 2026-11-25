package com.projectr.agent;

import android.app.Activity;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.graphics.Bitmap;
import android.graphics.PixelFormat;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.Image;
import android.media.ImageReader;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.IBinder;
import android.util.DisplayMetrics;
import android.util.Log;

import androidx.core.app.NotificationCompat;

import java.io.ByteArrayOutputStream;
import java.nio.ByteBuffer;

public class RemoteScreenService extends Service {
    private static final String TAG = "ProjectRScreen";
    private static final String CHANNEL_ID = "project_r_screen_cast";
    private static final int NOTIF_ID = 2403;

    public static final String EXTRA_RESULT_CODE = "result_code";
    public static final String EXTRA_RESULT_DATA = "result_data";

    private MediaProjection mediaProjection;
    private VirtualDisplay virtualDisplay;
    private ImageReader imageReader;
    private HandlerThread captureThread;
    private Handler captureHandler;
    private long lastCapturedMs = 0L;

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        startForegroundNotification();

        if (intent == null) {
            stopSelf();
            return START_NOT_STICKY;
        }

        int resultCode = intent.getIntExtra(EXTRA_RESULT_CODE, Activity.RESULT_CANCELED);
        Intent resultData = intent.getParcelableExtra(EXTRA_RESULT_DATA);

        if (resultCode != Activity.RESULT_OK || resultData == null) {
            stopSelf();
            return START_NOT_STICKY;
        }

        startCapture(resultCode, resultData);
        return START_NOT_STICKY;
    }

    private void startForegroundNotification() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationManager nm = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
            if (nm != null) {
                nm.createNotificationChannel(new NotificationChannel(
                        CHANNEL_ID,
                        "Project R Screen Sharing",
                        NotificationManager.IMPORTANCE_LOW
                ));
            }
        }

        Notification notification = new NotificationCompat.Builder(this, CHANNEL_ID)
                .setSmallIcon(android.R.drawable.ic_menu_view)
                .setContentTitle("Project R Remote Aktif")
                .setContentText("Berbagi layar ke administrator IT sedang berlangsung.")
                .setOngoing(true)
                .build();

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(NOTIF_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION);
        } else {
            startForeground(NOTIF_ID, notification);
        }
    }

    private void startCapture(int resultCode, Intent resultData) {
        cleanupCapture();

        try {
            MediaProjectionManager mpm = (MediaProjectionManager) getSystemService(Context.MEDIA_PROJECTION_SERVICE);
            if (mpm == null) {
                stopSelf();
                return;
            }
            mediaProjection = mpm.getMediaProjection(resultCode, resultData);
            if (mediaProjection == null) {
                stopSelf();
                return;
            }

            RemoteRelayClient relay = RemoteRelayClient.getInstance(this);
            int width = relay.getCaptureWidth();
            int height = relay.getCaptureHeight();
            DisplayMetrics dm = getResources().getDisplayMetrics();

            captureThread = new HandlerThread("ProjectRScreenCapture");
            captureThread.start();
            captureHandler = new Handler(captureThread.getLooper());

            mediaProjection.registerCallback(new MediaProjection.Callback() {
                @Override
                public void onStop() {
                    cleanupCapture();
                    RemoteRelayClient.getInstance(RemoteScreenService.this).onClientEndedSession();
                    stopSelf();
                }
            }, captureHandler);

            imageReader = ImageReader.newInstance(width, height, PixelFormat.RGBA_8888, 2);
            imageReader.setOnImageAvailableListener(reader -> {
                Image image = null;
                try {
                    image = reader.acquireLatestImage();
                    if (image == null) return;

                    RemoteRelayClient rc = RemoteRelayClient.getInstance(this);
                    if (rc.shouldSkipFrameForBackpressure()) return;

                    int fps = Math.max(1, rc.getTargetFps());
                    long minInterval = 1000L / fps;
                    long now = System.currentTimeMillis();
                    if (now - lastCapturedMs < minInterval) return;

                    byte[] jpeg = encodeImageToJpeg(image, width, height, rc.getJpegQuality());
                    if (jpeg != null && rc.sendJpegFrame(jpeg)) {
                        lastCapturedMs = now;
                    }
                } catch (Exception e) {
                    Log.w(TAG, "Frame capture error: " + e.getMessage());
                } finally {
                    if (image != null) {
                        try { image.close(); } catch (Exception ignored) {}
                    }
                }
            }, captureHandler);

            virtualDisplay = mediaProjection.createVirtualDisplay(
                    "ProjectRRemoteDisplay",
                    width,
                    height,
                    dm.densityDpi,
                    DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
                    imageReader.getSurface(),
                    null,
                    captureHandler
            );

            relay.onConsentApproved();
        } catch (Exception e) {
            Log.e(TAG, "Failed to start MediaProjection capture: " + e.getMessage());
            stopSelf();
        }
    }

    private byte[] encodeImageToJpeg(Image image, int width, int height, int quality) {
        Image.Plane[] planes = image.getPlanes();
        if (planes == null || planes.length == 0) return null;

        ByteBuffer buffer = planes[0].getBuffer();
        int pixelStride = planes[0].getPixelStride();
        int rowStride = planes[0].getRowStride();
        int rowPadding = rowStride - pixelStride * width;

        int bitmapWidth = width + (rowPadding / pixelStride);
        Bitmap rawBitmap = Bitmap.createBitmap(bitmapWidth, height, Bitmap.Config.ARGB_8888);
        rawBitmap.copyPixelsFromBuffer(buffer);

        Bitmap cleanBitmap = (bitmapWidth == width)
                ? rawBitmap
                : Bitmap.createBitmap(rawBitmap, 0, 0, width, height);

        ByteArrayOutputStream out = new ByteArrayOutputStream();
        cleanBitmap.compress(Bitmap.CompressFormat.JPEG, quality, out);

        if (cleanBitmap != rawBitmap) cleanBitmap.recycle();
        rawBitmap.recycle();
        return out.toByteArray();
    }

    private void cleanupCapture() {
        try { if (virtualDisplay != null) { virtualDisplay.release(); virtualDisplay = null; } } catch (Exception ignored) {}
        try { if (imageReader != null) { imageReader.close(); imageReader = null; } } catch (Exception ignored) {}
        try { if (mediaProjection != null) { mediaProjection.stop(); mediaProjection = null; } } catch (Exception ignored) {}
        try { if (captureThread != null) { captureThread.quitSafely(); captureThread = null; } } catch (Exception ignored) {}
    }

    @Override
    public void onDestroy() {
        cleanupCapture();
        super.onDestroy();
    }
}
