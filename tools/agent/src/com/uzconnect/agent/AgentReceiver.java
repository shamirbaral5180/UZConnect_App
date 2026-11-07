package com.uzconnect.agent;

import android.app.Activity;
import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.ContentResolver;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.database.Cursor;
import android.net.Uri;
import android.os.Build;
import android.os.Environment;
import android.telephony.SmsManager;
import android.telephony.TelephonyManager;
import android.util.Base64;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.File;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;

/**
 * One-way command receiver for the UZConnect desktop app. It has no UI and no background
 * polling. The desktop app invokes it explicitly over ADB and reads a small JSON result file.
 */
public class AgentReceiver extends BroadcastReceiver {

    private static final String ACTION_SENT = "com.uzconnect.agent.SENT";
    private static final String ACTION_DELIVERED = "com.uzconnect.agent.DELIVERED";
    private static final String PREFS = "uzconnect_agent";
    private static final Map<String, SendInfo> PENDING = new ConcurrentHashMap<>();

    private static final class SendInfo {
        final String to;
        final String text;
        final long started;
        final int parts;

        SendInfo(String to, String text, long started, int parts) {
            this.to = to;
            this.text = text;
            this.started = started;
            this.parts = parts;
        }
    }

    @Override
    public void onReceive(Context context, Intent intent) {
        String action = intent.getStringExtra("action");
        if (action == null) {
            action = intent.getAction();
        }

        try {
            if ("send".equals(action)) {
                handleSend(context, intent);
            } else if ("ping".equals(action)) {
                handlePing(context, intent);
            } else if ("dump-sms".equals(action)) {
                handleDumpSms(context, intent);
            } else if ("ussd".equals(action)) {
                handleUssd(context, intent);
            } else if (ACTION_SENT.equals(action)) {
                handleSent(context, intent);
            } else if (ACTION_DELIVERED.equals(action)) {
                handleDelivered(context, intent);
            } else {
                writeResult(context, intent.getStringExtra("id"),
                        result("failed", "unknown action", 0, 0, null));
            }
        } catch (Throwable t) {
            writeResult(context, intent.getStringExtra("id"),
                    result("failed", t.getClass().getSimpleName() + ": " + t.getMessage(), 0, 0, null));
        }
    }

    private void handlePing(Context context, Intent intent) {
        String id = intent.getStringExtra("id");
        if (id == null || id.length() == 0) {
            id = "ping";
        }
        try {
            JSONObject json = result("ok", null, 0, 0, null);
            json.put("version", 2);
            json.put("sdk", Build.VERSION.SDK_INT);
            json.put("agent", "UZConnect Agent");
            writeResult(context, id, json);
        } catch (Exception e) {
            writeResult(context, id, result("failed", e.toString(), 0, 0, null));
        }
    }

    private void handleSend(Context context, Intent intent) throws Exception {
        String id = intent.getStringExtra("id");
        String to = intent.getStringExtra("to");
        String textB64 = intent.getStringExtra("text_b64");
        if (id == null || to == null || textB64 == null) {
            writeResult(context, id, result("failed", "missing send parameter", 0, 0, null));
            return;
        }

        String text = new String(Base64.decode(textB64, Base64.DEFAULT), StandardCharsets.UTF_8);
        long started = System.currentTimeMillis();
        SmsManager manager = SmsManager.getDefault();
        ArrayList<String> parts = manager.divideMessage(text);
        int partCount = parts == null ? 1 : parts.size();

        PENDING.put(id, new SendInfo(to, text, started, partCount));

        JSONObject queued = result("queued", null, partCount, 0, null);
        writeResult(context, id, queued);

        SharedPreferences prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        prefs.edit()
                .putInt("parts_" + id, partCount)
                .putInt("sent_" + id, 0)
                .putInt("failed_" + id, 0)
                .remove("done_" + id)
                .apply();

        if (partCount == 1) {
            PendingIntent sent = sentIntent(context, id, 0, partCount);
            manager.sendTextMessage(to, null, text, sent, null);
        } else {
            ArrayList<PendingIntent> sentIntents = new ArrayList<PendingIntent>();
            for (int i = 0; i < partCount; i++) {
                sentIntents.add(sentIntent(context, id, i, partCount));
            }
            manager.sendMultipartTextMessage(to, null, parts, sentIntents, null);
        }

        scheduleSentFolderCheck(context, id, to, text, started, partCount);
    }

    private PendingIntent sentIntent(Context context, String id, int part, int parts) {
        Intent intent = new Intent(context, AgentReceiver.class);
        intent.setAction(ACTION_SENT);
        intent.putExtra("id", id);
        intent.putExtra("part", part);
        intent.putExtra("parts", parts);
        int requestCode = (id.hashCode() * 31) + part;
        return PendingIntent.getBroadcast(context, requestCode, intent, PendingIntent.FLAG_UPDATE_CURRENT);
    }

    private void handleSent(Context context, Intent intent) {
        final String id = intent.getStringExtra("id");
        if (id == null) {
            return;
        }

        int part = intent.getIntExtra("part", 0);
        int parts = intent.getIntExtra("parts", 1);
        int resultCode = getResultCode();
        SharedPreferences prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        final int sentSoFar = prefs.getInt("sent_" + id, 0);

        if (resultCode == Activity.RESULT_OK) {
            int sent = sentSoFar + 1;
            prefs.edit().putInt("sent_" + id, sent).apply();
            if (sent >= parts) {
                writeTerminal(context, id, result("sent", null, parts, sent, null));
                PENDING.remove(id);
            }
            return;
        }

        // Android 4.4 frequently returns RESULT_ERROR_GENERIC_FAILURE through
        // the SmsManager PendingIntent even though the message is actually
        // written to the device's Sent folder. Confirm against the provider
        // before reporting a failure.
        final SendInfo info = PENDING.get(id);
        final int partsFinal = parts;
        final String error = describeResult(resultCode);
        new Thread(new Runnable() {
            @Override
            public void run() {
                boolean confirmed = info != null &&
                        confirmInSentFolder(context, info.to, info.text, info.started);
                if (confirmed) {
                    writeTerminal(context, id, result("sent", null, partsFinal, partsFinal,
                            "confirmed in device Sent folder"));
                } else {
                    writeTerminal(context, id, result("failed", error, partsFinal, sentSoFar, null));
                }
                PENDING.remove(id);
            }
        }).start();
    }

    private static void scheduleSentFolderCheck(
            final Context context,
            final String id,
            final String to,
            final String text,
            final long started,
            final int parts) {
        new Thread(new Runnable() {
            @Override
            public void run() {
                try {
                    Thread.sleep(5000);
                } catch (InterruptedException ignored) {
                }

                if (confirmInSentFolder(context, to, text, started)) {
                    writeTerminal(context, id, result("sent", null, parts, parts,
                            "confirmed in device Sent folder"));
                }
                PENDING.remove(id);
            }
        }).start();
    }

    private static boolean confirmInSentFolder(Context context, String to, String text, long started) {
        Cursor cursor = null;
        try {
            cursor = context.getContentResolver().query(
                    Uri.parse("content://sms/sent"),
                    new String[]{"address", "body", "date"},
                    "date>=?",
                    new String[]{String.valueOf(started - 2000)},
                    "date DESC");
            if (cursor != null && cursor.moveToFirst()) {
                do {
                    if (matchesSentRow(cursor, to, text, started)) {
                        return true;
                    }
                } while (cursor.moveToNext());
            }
        } catch (Throwable ignored) {
        } finally {
            if (cursor != null) {
                cursor.close();
            }
        }

        // Some 4.4 providers ignore the date selection; scan the newest rows.
        cursor = null;
        try {
            cursor = context.getContentResolver().query(
                    Uri.parse("content://sms/sent"),
                    new String[]{"address", "body", "date"},
                    null,
                    null,
                    "date DESC");
            int checked = 0;
            while (cursor != null && cursor.moveToNext() && checked < 40) {
                checked++;
                if (matchesSentRow(cursor, to, text, started)) {
                    return true;
                }
            }
        } catch (Throwable ignored) {
        } finally {
            if (cursor != null) {
                cursor.close();
            }
        }

        return false;
    }

    private static boolean matchesSentRow(Cursor cursor, String to, String text, long started) {
        String address = cursor.getString(0);
        String body = cursor.getString(1);
        long date = cursor.getLong(2);
        if (date > 0 && date < started - 5000) {
            return false;
        }
        if (!sameRecipient(address, to)) {
            return false;
        }
        return body == null || body.length() == 0 || sameMessageBody(body, text);
    }

    private static boolean sameRecipient(String left, String right) {
        String a = digitsOnly(left);
        String b = digitsOnly(right);
        if (a.length() == 0 || b.length() == 0) {
            return false;
        }
        String aTail = a.length() > 9 ? a.substring(a.length() - 9) : a;
        String bTail = b.length() > 9 ? b.substring(b.length() - 9) : b;
        return aTail.equals(bTail);
    }

    private static boolean sameMessageBody(String left, String right) {
        String a = normalizeBody(left);
        String b = normalizeBody(right);
        return a.length() == 0 || b.length() == 0 || a.equals(b) || a.contains(b) || b.contains(a);
    }

    private static String digitsOnly(String value) {
        if (value == null) {
            return "";
        }
        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < value.length(); i++) {
            char c = value.charAt(i);
            if (Character.isDigit(c)) {
                builder.append(c);
            }
        }
        return builder.toString();
    }

    private static String normalizeBody(String value) {
        return value == null ? "" : value.replace("\r\n", "\n").replace('\r', '\n').trim();
    }

    private static void writeTerminal(Context context, String id, JSONObject json) {
        if (id == null) {
            return;
        }

        SharedPreferences prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        String done = prefs.getString("done_" + id, null);
        String status = json.optString("status");
        if ("sent".equals(done)) {
            return;
        }
        if ("failed".equals(done) && !"sent".equals(status)) {
            return;
        }

        prefs.edit().putString("done_" + id, status).apply();
        writeResult(context, id, json);
    }

    private void handleDelivered(Context context, Intent intent) {
        String id = intent.getStringExtra("id");
        if (id == null) {
            return;
        }

        try {
            JSONObject json = result("delivered", null,
                    intent.getIntExtra("parts", 1),
                    intent.getIntExtra("parts", 1), null);
            writeResult(context, id + "-delivered", json);
        } catch (Exception ignored) {
        }
    }

    private void handleUssd(Context context, Intent intent) throws Exception {
        String id = intent.getStringExtra("id");
        String code = intent.getStringExtra("code");
        if (code == null || code.length() == 0) {
            writeResult(context, id, result("failed", "missing USSD code", 0, 0, null));
            return;
        }

        // TelephonyManager.handlePinMmi(String) is present on this stock Android
        // 4.4.4 image. It submits the USSD/MMI request to the phone process without
        // opening the dialer or placing a voice call.
        TelephonyManager manager = (TelephonyManager) context.getSystemService(Context.TELEPHONY_SERVICE);
        boolean accepted;
        try {
            java.lang.reflect.Method method = TelephonyManager.class.getMethod("handlePinMmi", String.class);
            Object value = method.invoke(manager, code);
            accepted = value instanceof Boolean && (Boolean) value;
        } catch (Throwable first) {
            try {
                java.lang.reflect.Method getter = TelephonyManager.class.getDeclaredMethod("getITelephony");
                getter.setAccessible(true);
                Object telephony = getter.invoke(manager);
                java.lang.reflect.Method handle = telephony.getClass().getMethod("handlePinMmi", String.class);
                Object value = handle.invoke(telephony, code);
                accepted = value instanceof Boolean && (Boolean) value;
            } catch (Throwable second) {
                JSONObject error = result("failed", describe(second), 0, 0, null);
                error.put("code", code);
                writeResult(context, id, error);
                return;
            }
        }

        JSONObject json = result(accepted ? "accepted" : "rejected",
                accepted ? null : "The phone process rejected the USSD/MMI code.",
                0, 0, null);
        json.put("code", code);
        writeResult(context, id, json);
    }

    private static String describe(Throwable throwable) {
        Throwable cause = throwable;
        while (cause instanceof java.lang.reflect.InvocationTargetException && cause.getCause() != null) {
            cause = cause.getCause();
        }
        String message = cause.getMessage();
        return cause.getClass().getSimpleName() + (message == null ? "" : ": " + message);
    }

    private void handleDumpSms(Context context, Intent intent) throws Exception {
        String id = intent.getStringExtra("id");
        if (id == null || id.length() == 0) {
            id = "dump";
        }

        JSONObject manifest = new JSONObject();
        manifest.put("ok", true);
        manifest.put("timestamp", System.currentTimeMillis());

        String[] folders = new String[]{"inbox", "sent", "draft", "outbox", "failed", "queued"};
        for (String folder : folders) {
            JSONArray messages = readFolder(context, folder);
            manifest.put(folder, messages.length());
            writeTextFile(context, "sms-" + folder + ".json", messages.toString());
        }

        writeTextFile(context, "sms-dump.json", manifest.toString());
        writeResult(context, id, result("ok", null, 0, 0, null));
    }

    private JSONArray readFolder(Context context, String folder) {
        JSONArray array = new JSONArray();
        ContentResolver resolver = context.getContentResolver();
        Cursor cursor = null;
        try {
            cursor = resolver.query(Uri.parse("content://sms/" + folder), null, null, null, "date DESC");
            if (cursor == null) {
                return array;
            }

            while (cursor.moveToNext()) {
                JSONObject item = new JSONObject();
                item.put("_id", cursor.getLong(cursor.getColumnIndexOrThrow("_id")));
                item.put("thread_id", cursor.getLong(cursor.getColumnIndexOrThrow("thread_id")));
                item.put("address", stringOrEmpty(cursor, "address"));
                item.put("date", cursor.getLong(cursor.getColumnIndexOrThrow("date")));
                item.put("read", cursor.getInt(cursor.getColumnIndexOrThrow("read")));
                item.put("status", cursor.getInt(cursor.getColumnIndexOrThrow("status")));
                item.put("type", cursor.getInt(cursor.getColumnIndexOrThrow("type")));
                item.put("body", stringOrEmpty(cursor, "body"));
                item.put("service_center", stringOrEmpty(cursor, "service_center"));
                array.put(item);
            }
        } catch (Throwable ignored) {
            // A missing/empty provider or permission denial yields an empty array.
        } finally {
            if (cursor != null) {
                cursor.close();
            }
        }
        return array;
    }

    private static String stringOrEmpty(Cursor cursor, String column) {
        int index = cursor.getColumnIndex(column);
        if (index < 0 || cursor.isNull(index)) {
            return "";
        }
        return cursor.getString(index);
    }

    private static JSONObject result(String status, String error, int parts, int completed, String extra) {
        try {
            JSONObject json = new JSONObject();
            json.put("status", status);
            if (error != null) {
                json.put("error", error);
            }
            json.put("parts", parts);
            json.put("completed", completed);
            json.put("timestamp", System.currentTimeMillis());
            if (extra != null) {
                json.put("extra", extra);
            }
            return json;
        } catch (Exception e) {
            return new JSONObject();
        }
    }

    private static String describeResult(int resultCode) {
        switch (resultCode) {
            case SmsManager.RESULT_ERROR_GENERIC_FAILURE:
                return "SMS generic failure (no SIM, no service, or rejected by network)";
            case SmsManager.RESULT_ERROR_RADIO_OFF:
                return "Radio is off";
            case SmsManager.RESULT_ERROR_NULL_PDU:
                return "Null PDU";
            case SmsManager.RESULT_ERROR_NO_SERVICE:
                return "No service";
            default:
                return "SMS send failed (result code " + resultCode + ")";
        }
    }

    private static void writeResult(Context context, String id, JSONObject json) {
        if (id == null || id.length() == 0) {
            id = "unknown-" + System.currentTimeMillis();
        }
        writeTextFile(context, "results/" + id + ".json", json.toString());
    }

    private static void writeTextFile(Context context, String relativePath, String content) {
        File root = new File(Environment.getExternalStorageDirectory(), "uzconnect");
        if (!ensureDirectory(root)) {
            root = context.getFilesDir();
        }

        File target = new File(root, relativePath);
        File parent = target.getParentFile();
        if (parent != null && !ensureDirectory(parent)) {
            return;
        }

        File temp = new File(target.getAbsolutePath() + ".tmp");
        FileOutputStream stream = null;
        try {
            stream = new FileOutputStream(temp);
            stream.write(content.getBytes(StandardCharsets.UTF_8));
            stream.flush();
            stream.close();
            stream = null;
            if (target.exists() && !target.delete()) {
                // Fall through; rename may still replace on some filesystems.
            }
            temp.renameTo(target);
        } catch (Throwable ignored) {
            try {
                if (stream != null) {
                    stream.close();
                }
            } catch (Throwable ignored2) {
            }
        } finally {
            if (temp.exists() && !target.exists()) {
                temp.delete();
            }
        }
    }

    private static boolean ensureDirectory(File directory) {
        return directory.exists() || directory.mkdirs();
    }
}
