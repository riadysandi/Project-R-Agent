package com.projectr.agent;

import android.Manifest;
import android.annotation.SuppressLint;
import android.content.Context;
import android.content.pm.PackageManager;
import android.location.Location;
import android.location.LocationListener;
import android.location.LocationManager;
import android.net.DhcpInfo;
import android.net.wifi.WifiInfo;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Looper;
import android.util.Log;
import androidx.core.content.ContextCompat;
import org.json.JSONObject;

import java.net.InetAddress;
import java.net.NetworkInterface;
import java.util.Collections;
import java.util.List;

public class LocationHelper {
    private static final String TAG = "LocationHelper";
    private static Location lastKnownFix = null;
    private static long lastFixTime = 0;
    private static boolean isListening = false;

    private static final LocationListener locationListener = new LocationListener() {
        @Override
        public void onLocationChanged(Location location) {
            if (location != null) {
                lastKnownFix = location;
                lastFixTime = System.currentTimeMillis();
                Log.d(TAG, "Location updated via listener: " + location.getLatitude() + ", " + location.getLongitude()
                        + " (" + location.getProvider() + ", acc=" + location.getAccuracy() + "m)");
            }
        }

        @Override
        public void onStatusChanged(String provider, int status, Bundle extras) {}

        @Override
        public void onProviderEnabled(String provider) {}

        @Override
        public void onProviderDisabled(String provider) {}
    };

    /**
     * Start background location listener if permissions allow.
     */
    @SuppressLint("MissingPermission")
    public static void ensureLocationUpdates(Context context) {
        if (!hasLocationPermission(context)) {
            return;
        }

        try {
            LocationManager lm = (LocationManager) context.getSystemService(Context.LOCATION_SERVICE);
            if (lm == null || isListening) return;

            Looper looper = Looper.getMainLooper();
            if (lm.isProviderEnabled(LocationManager.GPS_PROVIDER)) {
                lm.requestLocationUpdates(LocationManager.GPS_PROVIDER, 30000, 10, locationListener, looper);
                isListening = true;
            }
            if (lm.isProviderEnabled(LocationManager.NETWORK_PROVIDER)) {
                lm.requestLocationUpdates(LocationManager.NETWORK_PROVIDER, 30000, 10, locationListener, looper);
                isListening = true;
            }
            if (lm.isProviderEnabled(LocationManager.PASSIVE_PROVIDER)) {
                lm.requestLocationUpdates(LocationManager.PASSIVE_PROVIDER, 30000, 10, locationListener, looper);
                isListening = true;
            }
        } catch (Exception e) {
            Log.w(TAG, "Failed to start location updates: " + e.getMessage());
        }
    }

    public static boolean hasLocationPermission(Context context) {
        return ContextCompat.checkSelfPermission(context, Manifest.permission.ACCESS_FINE_LOCATION) == PackageManager.PERMISSION_GRANTED
                || ContextCompat.checkSelfPermission(context, Manifest.permission.ACCESS_COARSE_LOCATION) == PackageManager.PERMISSION_GRANTED;
    }

    /**
     * Get best available location fix (GPS first, then Network, then Passive, then cached).
     */
    @SuppressLint("MissingPermission")
    public static Location getBestLocation(Context context) {
        ensureLocationUpdates(context);

        if (!hasLocationPermission(context)) {
            return lastKnownFix;
        }

        try {
            LocationManager lm = (LocationManager) context.getSystemService(Context.LOCATION_SERVICE);
            if (lm == null) return lastKnownFix;

            Location bestLocation = lastKnownFix;

            List<String> providers = lm.getAllProviders();
            for (String provider : providers) {
                try {
                    Location l = lm.getLastKnownLocation(provider);
                    if (l == null) continue;

                    if (bestLocation == null) {
                        bestLocation = l;
                    } else {
                        // Prioritize GPS over other providers
                        boolean isGps = LocationManager.GPS_PROVIDER.equalsIgnoreCase(l.getProvider());
                        boolean bestIsGps = LocationManager.GPS_PROVIDER.equalsIgnoreCase(bestLocation.getProvider());

                        if (isGps && !bestIsGps) {
                            bestLocation = l;
                        } else if (isGps == bestIsGps) {
                            // Compare freshness and accuracy
                            long timeDiff = l.getTime() - bestLocation.getTime();
                            if (timeDiff > 120000) { // More than 2 minutes newer
                                bestLocation = l;
                            } else if (timeDiff > -120000 && l.getAccuracy() < bestLocation.getAccuracy()) {
                                bestLocation = l;
                            }
                        }
                    }
                } catch (SecurityException ignored) {}
            }

            if (bestLocation != null) {
                lastKnownFix = bestLocation;
                lastFixTime = System.currentTimeMillis();
            }

            return bestLocation;
        } catch (Exception e) {
            Log.w(TAG, "Error getting best location: " + e.getMessage());
            return lastKnownFix;
        }
    }

    /**
     * Get Wi-Fi connection info (SSID, BSSID, Gateway).
     */
    @SuppressLint("MissingPermission")
    public static JSONObject getWifiAndNetworkInfo(Context context) {
        JSONObject res = new JSONObject();
        try {
            WifiManager wm = (WifiManager) context.getApplicationContext().getSystemService(Context.WIFI_SERVICE);
            if (wm != null) {
                WifiInfo info = wm.getConnectionInfo();
                if (info != null) {
                    String ssid = info.getSSID();
                    if (ssid != null) {
                        ssid = ssid.replace("\"", "");
                        if (!"<unknown ssid>".equalsIgnoreCase(ssid) && !ssid.isEmpty()) {
                            res.put("wifi_ssid", ssid);
                        }
                    }

                    String bssid = info.getBSSID();
                    if (bssid != null && !"02:00:00:00:00:00".equals(bssid) && !bssid.isEmpty()) {
                        res.put("wifi_bssid", bssid);
                    }
                    res.put("wifi_rssi", info.getRssi());
                }

                DhcpInfo dhcp = wm.getDhcpInfo();
                if (dhcp != null && dhcp.gateway != 0) {
                    String gatewayIp = formatIp(dhcp.gateway);
                    if (!gatewayIp.isEmpty()) {
                        res.put("default_gateway", gatewayIp);
                    }
                }
            }

            String localIp = getLocalIpAddress();
            if (!localIp.isEmpty()) {
                res.put("primary_ip", localIp);
            }
        } catch (Exception e) {
            Log.w(TAG, "Error reading wifi info: " + e.getMessage());
        }
        return res;
    }

    private static String formatIp(int ip) {
        return (ip & 0xFF) + "." +
                ((ip >> 8) & 0xFF) + "." +
                ((ip >> 16) & 0xFF) + "." +
                ((ip >> 24) & 0xFF);
    }

    public static String getLocalIpAddress() {
        try {
            List<NetworkInterface> interfaces = Collections.list(NetworkInterface.getNetworkInterfaces());
            for (NetworkInterface intf : interfaces) {
                if (intf.isLoopback() || !intf.isUp()) continue;
                List<InetAddress> addrs = Collections.list(intf.getInetAddresses());
                for (InetAddress addr : addrs) {
                    if (!addr.isLoopbackAddress()) {
                        String sAddr = addr.getHostAddress();
                        if (sAddr != null && sAddr.indexOf(':') < 0) { // IPv4
                            return sAddr;
                        }
                    }
                }
            }
        } catch (Exception ignored) {}
        return "";
    }

    /**
     * Attach all location & network information to the heartbeat/register payload.
     */
    public static void attachLocationData(Context context, JSONObject payload) {
        try {
            // 1. Wi-Fi & Network info
            JSONObject netInfo = getWifiAndNetworkInfo(context);
            if (netInfo.has("wifi_ssid")) payload.put("wifi_ssid", netInfo.getString("wifi_ssid"));
            if (netInfo.has("wifi_bssid")) payload.put("wifi_bssid", netInfo.getString("wifi_bssid"));
            if (netInfo.has("default_gateway")) payload.put("default_gateway", netInfo.getString("default_gateway"));
            if (netInfo.has("primary_ip") && !payload.has("primary_ip")) payload.put("primary_ip", netInfo.getString("primary_ip"));

            // 2. GPS / Hardware Location
            Location loc = getBestLocation(context);
            if (loc != null) {
                payload.put("has_gps", true);
                payload.put("latitude", loc.getLatitude());
                payload.put("longitude", loc.getLongitude());
                payload.put("location_accuracy", (double) loc.getAccuracy());
                payload.put("location_provider", loc.getProvider() != null ? loc.getProvider() : "gps");
                payload.put("location_timestamp", loc.getTime());
                Log.i(TAG, "Attached GPS coordinates to payload: " + loc.getLatitude() + ", " + loc.getLongitude() + " (" + loc.getProvider() + ")");
            } else {
                payload.put("has_gps", false);
                Log.d(TAG, "No GPS fix available yet for payload");
            }
        } catch (Exception e) {
            Log.e(TAG, "Failed attaching location data: " + e.getMessage());
        }
    }
}
