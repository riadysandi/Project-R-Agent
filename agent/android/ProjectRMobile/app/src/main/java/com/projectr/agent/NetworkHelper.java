package com.projectr.agent;

import android.content.Context;
import android.os.Build;
import android.provider.Settings;
import org.json.JSONArray;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;

public class NetworkHelper {

    public static String getDeviceId(Context context) {
        try {
            String id = Settings.Secure.getString(context.getContentResolver(), Settings.Secure.ANDROID_ID);
            if (id != null && !id.trim().isEmpty()) {
                return "AND-" + id.toUpperCase();
            }
        } catch (Exception ignored) {}
        String fallbackSerial = (Build.SERIAL != null && !Build.SERIAL.trim().isEmpty() && !Build.UNKNOWN.equalsIgnoreCase(Build.SERIAL))
                ? Build.SERIAL.trim()
                : "DEV-" + Build.MODEL.hashCode();
        return "AND-" + fallbackSerial;
    }

    public static String getHostname() {
        return Build.MANUFACTURER.toUpperCase() + "-" + Build.MODEL.replace(" ", "-");
    }

    public static final String DEFAULT_TOKEN = "";

    public static String getAppVersion(Context context) {
        try {
            return context.getPackageManager().getPackageInfo(context.getPackageName(), 0).versionName;
        } catch (Exception ignored) {
            return "2.4.5";
        }
    }

    private static String postJson(String endpointUrl, String jsonBody, String token) throws Exception {
        URL url = new URL(endpointUrl);
        HttpURLConnection conn = (HttpURLConnection) url.openConnection();
        conn.setRequestMethod("POST");
        conn.setRequestProperty("Content-Type", "application/json");
        conn.setRequestProperty("Accept", "application/json");
        
        String cleanToken = (token != null && !token.trim().isEmpty()) ? token.trim() : DEFAULT_TOKEN;
        conn.setRequestProperty("X-Agent-Token", cleanToken);
        conn.setConnectTimeout(8000);
        conn.setReadTimeout(12000);
        conn.setDoOutput(true);

        try (OutputStream os = conn.getOutputStream()) {
            byte[] input = jsonBody.getBytes(StandardCharsets.UTF_8);
            os.write(input, 0, input.length);
        }

        int code = conn.getResponseCode();
        InputStream is = (code >= 200 && code < 400) ? conn.getInputStream() : conn.getErrorStream();
        if (is == null) {
            return "{\"status\":\"error\",\"message\":\"HTTP error code: " + code + "\"}";
        }

        BufferedReader br = new BufferedReader(new InputStreamReader(is, StandardCharsets.UTF_8));
        StringBuilder sb = new StringBuilder();
        String line;
        while ((line = br.readLine()) != null) {
            sb.append(line);
        }
        return sb.toString();
    }

    public static JSONObject registerDevice(Context context, String serverUrl, String token, String tag) throws Exception {
        String cleanUrl = serverUrl.replaceAll("/+$", "") + "/api/agent/register";
        String cleanToken = (token != null && !token.trim().isEmpty()) ? token.trim() : DEFAULT_TOKEN;

        JSONObject payload = new JSONObject();
        payload.put("token", cleanToken);
        payload.put("tag", tag);
        payload.put("device_id", getDeviceId(context));
        payload.put("hostname", getHostname());
        payload.put("manufacturer", Build.MANUFACTURER);
        payload.put("model", Build.MODEL);
        payload.put("operating_system", "Android " + Build.VERSION.RELEASE);
        payload.put("os_version", Build.DISPLAY);
        payload.put("platform", "android");
        payload.put("chassis_type", "Smartphone");
        payload.put("status", "online");
        payload.put("agent_version", getAppVersion(context));
        LocationHelper.attachLocationData(context, payload);

        String resp = postJson(cleanUrl, payload.toString(), cleanToken);
        return new JSONObject(resp);
    }

    public static JSONObject sendHeartbeat(Context context, String serverUrl, String token, String tag) throws Exception {
        return sendHeartbeat(context, serverUrl, token, tag, MdmPolicyManager.buildEvaluations(context), false);
    }

    public static JSONObject sendHeartbeat(Context context, String serverUrl, String token, String tag, JSONArray evaluations) throws Exception {
        return sendHeartbeat(context, serverUrl, token, tag, evaluations, false);
    }

    public static JSONObject sendHeartbeat(Context context, String serverUrl, String token, String tag, JSONArray evaluations, boolean remoteUnenrollAck) throws Exception {
        String cleanUrl = serverUrl.replaceAll("/+$", "") + "/api/agent/heartbeat";
        String cleanToken = (token != null && !token.trim().isEmpty()) ? token.trim() : DEFAULT_TOKEN;

        JSONObject payload = new JSONObject();
        payload.put("token", cleanToken);
        payload.put("tag", tag);
        payload.put("device_id", getDeviceId(context));
        payload.put("hostname", getHostname());
        payload.put("manufacturer", Build.MANUFACTURER);
        payload.put("model", Build.MODEL);
        payload.put("operating_system", "Android " + Build.VERSION.RELEASE);
        payload.put("platform", "android");
        payload.put("chassis_type", "Smartphone");
        payload.put("version", getAppVersion(context));
        payload.put("status", "online");
        if (evaluations != null && evaluations.length() > 0) {
            payload.put("evaluations", evaluations);
        }
        if (remoteUnenrollAck) {
            payload.put("remote_unenroll_ack", true);
        }
        LocationHelper.attachLocationData(context, payload);

        String resp = postJson(cleanUrl, payload.toString(), cleanToken);
        return new JSONObject(resp);
    }

    public static JSONObject updateTag(Context context, String serverUrl, String token, String newTag) throws Exception {
        String cleanUrl = serverUrl.replaceAll("/+$", "") + "/api/agent/update-tag";
        String cleanToken = (token != null && !token.trim().isEmpty()) ? token.trim() : DEFAULT_TOKEN;

        JSONObject payload = new JSONObject();
        payload.put("token", cleanToken);
        payload.put("tag", newTag);
        payload.put("device_id", getDeviceId(context));
        payload.put("hostname", getHostname());

        String resp = postJson(cleanUrl, payload.toString(), cleanToken);
        return new JSONObject(resp);
    }

    public static JSONObject verifyUninstall(Context context, String serverUrl, String token) throws Exception {
        String cleanUrl = serverUrl.replaceAll("/+$", "") + "/api/agent/verify-uninstall";

        JSONObject payload = new JSONObject();
        payload.put("token", token);
        payload.put("device_id", getDeviceId(context));
        payload.put("hostname", getHostname());

        String resp = postJson(cleanUrl, payload.toString(), token);
        return new JSONObject(resp);
    }

    public static JSONObject unlockScreen(Context context, String serverUrl, String pin) throws Exception {
        String cleanUrl = serverUrl.replaceAll("/+$", "") + "/api/agent/unlock-screen";

        JSONObject payload = new JSONObject();
        payload.put("pin", pin);
        payload.put("device_id", getDeviceId(context));
        payload.put("hostname", getHostname());

        String resp = postJson(cleanUrl, payload.toString(), null);
        return new JSONObject(resp);
    }
}
