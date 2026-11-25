package com.projectr.agent;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageInstaller;
import android.util.Log;

public class PackageInstallerStatusReceiver extends BroadcastReceiver {
    private static final String TAG = "ProjectRInstaller";

    @Override
    public void onReceive(Context context, Intent intent) {
        if (intent == null) return;

        int status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE);
        String message = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE);

        switch (status) {
            case PackageInstaller.STATUS_PENDING_USER_ACTION:
                Log.i(TAG, "Install pending user action. Mengarahkan konfirmasi ke user...");
                Intent confirmIntent = intent.getParcelableExtra(Intent.EXTRA_INTENT);
                if (confirmIntent != null) {
                    confirmIntent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                    context.startActivity(confirmIntent);
                }
                break;

            case PackageInstaller.STATUS_SUCCESS:
                Log.i(TAG, "OTA Auto-update berhasil dipasang secara senyap oleh Device Owner!");
                AutoUpdateManager.setUpdating(false);
                break;

            default:
                Log.e(TAG, "OTA Auto-update gagal. Status: " + status + ", pesan: " + message);
                AutoUpdateManager.setUpdating(false);
                break;
        }
    }
}
