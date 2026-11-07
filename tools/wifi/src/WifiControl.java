import android.app.AlarmManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.os.Looper;
import android.os.SystemClock;

import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.security.MessageDigest;
import java.util.Locale;

/**
 * Privileged Wi-Fi control used by UZConnect. It never prints a passphrase, SSID or key;
 * results are limited to booleans, counts and hashes supplied by the caller.
 *
 * Actions:
 *   info                       read-only field shape of the framework AP configuration
 *   set-hidden 0|1             framework hidden flag + vendor configuration
 *   set-password               passphrase read from stdin, framework + vendor configuration
 *   verify <sha256>            compare framework key and hostapd passphrase with a hash
 *   alarm-off | alarm-on       cancel or restore the vendor 20-second refresh alarm
 */
public final class WifiControl {
    private static final String CONFIG = "/data/mificonfig.json";

    public static void main(String[] args) {
        try {
            String action = args.length > 0 ? args[0] : "info";
            JSONObject result;
            if ("info".equals(action)) result = info();
            else if ("set-hidden".equals(action) && args.length == 2) result = setHidden("1".equals(args[1]));
            else if ("set-password".equals(action) && args.length == 2) result = setPasswordFromFile(args[1]);
            else if ("verify".equals(action) && args.length == 2) result = verify(args[1]);
            else if ("alarm-off".equals(action)) result = alarm(false);
            else if ("alarm-on".equals(action)) result = alarm(true);
            else if ("ap-off".equals(action)) result = apEnabled(false);
            else if ("ap-on".equals(action)) result = apEnabled(true);
            else if ("ap-cycle".equals(action)) result = apCycle();
            else throw new IOException();
            System.out.println(result.toString());
        } catch (Throwable error) {
            try {
                System.out.println(new JSONObject().put("ok", false)
                    .put("error", error.getClass().getSimpleName()).toString());
            } catch (Exception ignored) {
                System.out.println("{\"ok\":false}");
            }
        }
        System.out.flush();
        Runtime.getRuntime().halt(0);
    }

    private static JSONObject base() throws Exception {
        return new JSONObject().put("ok", true);
    }

    private static Object service(String name, String stub) throws Exception {
        Object binder = Class.forName("android.os.ServiceManager").getMethod("getService", String.class).invoke(null, name);
        return Class.forName(stub).getMethod("asInterface", Class.forName("android.os.IBinder")).invoke(null, binder);
    }

    private static Object wifi() throws Exception {
        return service("wifi", "android.net.wifi.IWifiManager$Stub");
    }

    private static Object apConfig(Object wifi) throws Exception {
        return wifi.getClass().getMethod("getWifiApConfiguration").invoke(wifi);
    }

    private static void saveApConfig(Object wifi, Object config) throws Exception {
        Class<?> type = Class.forName("android.net.wifi.WifiConfiguration");
        Method set = wifi.getClass().getMethod("setWifiApConfiguration", type);
        set.invoke(wifi, config);
    }

    private static String unquote(String value) {
        if (value == null) return "";
        if (value.length() >= 2 && value.startsWith("\"") && value.endsWith("\"")) return value.substring(1, value.length() - 1);
        return value;
    }

    private static String sha256(String value) throws Exception {
        byte[] digest = MessageDigest.getInstance("SHA-256").digest(value.getBytes("UTF-8"));
        StringBuilder text = new StringBuilder();
        for (byte b : digest) text.append(String.format(Locale.US, "%02x", b & 255));
        return text.toString();
    }

    private static JSONObject info() throws Exception {
        Object wifi = wifi();
        Object config = apConfig(wifi);
        Class<?> type = Class.forName("android.net.wifi.WifiConfiguration");
        String key = (String) type.getField("preSharedKey").get(config);
        boolean hidden = type.getField("hiddenSSID").getBoolean(config);
        return base().put("hidden", hidden).put("keyConfigured", key != null && key.length() > 0)
            .put("keyQuoted", key != null && key.startsWith("\"") && key.endsWith("\""))
            .put("keyLength", unquote(key).length());
    }

    private static JSONObject setHidden(boolean hidden) throws Exception {
        Object wifi = wifi();
        Object config = apConfig(wifi);
        Class<?> type = Class.forName("android.net.wifi.WifiConfiguration");
        type.getField("hiddenSSID").setBoolean(config, hidden);
        saveApConfig(wifi, config);
        boolean saved = type.getField("hiddenSSID").getBoolean(apConfig(wifi));
        if (!saved) return new JSONObject().put("ok", false);
        updateVendor("WIFIssidhidden", hidden);
        return base().put("hidden", saved);
    }

    private static JSONObject setPassword(String secret) throws Exception {
        if (secret == null || secret.length() < 8 || secret.length() > 63) return new JSONObject().put("ok", false);
        for (int i = 0; i < secret.length(); i++) {
            char c = secret.charAt(i);
            if (c < 32 || c > 126) return new JSONObject().put("ok", false);
        }
        String expected = sha256(secret);
        Object wifi = wifi();
        Object config = apConfig(wifi);
        Class<?> type = Class.forName("android.net.wifi.WifiConfiguration");
        type.getField("preSharedKey").set(config, secret);
        saveApConfig(wifi, config);
        String saved = unquote((String) type.getField("preSharedKey").get(apConfig(wifi)));
        if (!sha256(saved).equals(expected)) return new JSONObject().put("ok", false);
        updateVendor("WIFIpassword", secret);
        return base().put("changed", true);
    }

    private static JSONObject verify(String expected) throws Exception {
        boolean framework = false;
        try {
            Object config = apConfig(wifi());
            String key = unquote((String) Class.forName("android.net.wifi.WifiConfiguration")
                .getField("preSharedKey").get(config));
            framework = sha256(key).equals(expected);
        } catch (Exception ignored) { }
        boolean hostapd = false;
        try {
            String passphrase = readValue("/data/misc/wifi/hostapd.conf", "wpa_passphrase");
            hostapd = passphrase != null && sha256(passphrase).equals(expected);
        } catch (Exception ignored) { }
        return base().put("framework", framework).put("hostapd", hostapd);
    }

    private static void updateVendor(String key, Object value) throws Exception {
        byte[] raw = readAll(CONFIG);
        JSONObject json = new JSONObject(new String(raw, "UTF-8"));
        json.put(key, value);
        File target = new File(CONFIG);
        File temp = new File(CONFIG + ".tmp");
        try (FileOutputStream out = new FileOutputStream(temp)) {
            out.write(json.toString().getBytes("UTF-8"));
            out.getFD().sync();
        }
        chmod(temp.getPath(), "600");
        chown(temp.getPath());
        if (!temp.renameTo(target)) throw new IOException();
        JSONObject check = new JSONObject(new String(readAll(CONFIG), "UTF-8"));
        if (!String.valueOf(value).equals(String.valueOf(check.get(key)))) throw new IOException();
    }

    private static void chmod(String path, String mode) throws Exception {
        if (Runtime.getRuntime().exec(new String[]{"/system/bin/chmod", mode, path}).waitFor() != 0) throw new IOException();
    }

    private static void chown(String path) throws Exception {
        Runtime.getRuntime().exec(new String[]{"/system/bin/chown", "system:system", path}).waitFor();
    }

    private static byte[] readAll(String path) throws IOException {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        try (InputStream in = new FileInputStream(path)) {
            byte[] buffer = new byte[4096];
            int count;
            while ((count = in.read(buffer)) != -1) out.write(buffer, 0, count);
        }
        return out.toByteArray();
    }

    private static String readValue(String path, String key) throws IOException {
        String text = new String(readAll(path), "UTF-8");
        for (String line : text.split("\n")) {
            if (line.startsWith(key + "=")) return line.substring(key.length() + 1);
        }
        return null;
    }

    private static JSONObject setPasswordFromFile(String path) throws Exception {
        File file = new File(path);
        if (!path.startsWith("/data/local/tmp/")) return new JSONObject().put("ok", false);
        String secret = "";
        try {
            byte[] raw = readAll(path);
            secret = new String(raw, "UTF-8").split("\n")[0].trim();
            java.util.Arrays.fill(raw, (byte) 0);
        } finally {
            file.delete();
        }
        if (file.exists()) return new JSONObject().put("ok", false);
        return setPassword(secret);
    }

    private static JSONObject apEnabled(boolean enable) throws Exception {
        Object wifi = wifi();
        Object config = enable ? apConfig(wifi) : null;
        Class<?> type = Class.forName("android.net.wifi.WifiConfiguration");
        Method set = wifi.getClass().getMethod("setWifiApEnabled", type, boolean.class);
        set.invoke(wifi, config, enable);
        Thread.sleep(1500);
        int state = (Integer) wifi.getClass().getMethod("getWifiApEnabledState").invoke(wifi);
        return base().put("state", state);
    }

    private static JSONObject apCycle() throws Exception {
        Object wifi = wifi();
        Class<?> type = Class.forName("android.net.wifi.WifiConfiguration");
        Method set = wifi.getClass().getMethod("setWifiApEnabled", type, boolean.class);
        set.invoke(wifi, null, false);
        for (int i = 0; i < 40; i++) {
            Thread.sleep(500);
            int state = (Integer) wifi.getClass().getMethod("getWifiApEnabledState").invoke(wifi);
            if (state == 11) break;
        }
        Thread.sleep(3000);
        Object config = apConfig(wifi);
        set.invoke(wifi, config, true);
        for (int i = 0; i < 40; i++) {
            Thread.sleep(500);
            int state = (Integer) wifi.getClass().getMethod("getWifiApEnabledState").invoke(wifi);
            if (state == 13) break;
        }
        int finalState = (Integer) wifi.getClass().getMethod("getWifiApEnabledState").invoke(wifi);
        return base().put("state", finalState);
    }

    private static JSONObject alarm(boolean restore) throws Exception {        if (Looper.myLooper() == null) Looper.prepareMainLooper();
        Object thread = Class.forName("android.app.ActivityThread").getMethod("systemMain").invoke(null);
        Context system = (Context) thread.getClass().getMethod("getSystemContext").invoke(thread);
        Context context = system;
        try {
            Class.forName("android.os.Process").getMethod("setUid", int.class).invoke(null, 1000);
            context = system.createPackageContext("com.mifiservice.hello",
                Context.CONTEXT_INCLUDE_CODE | Context.CONTEXT_IGNORE_SECURITY);
        } catch (Throwable ignored) {
            context = system;
        }
        Intent intent = new Intent("com.mifiservice.TIMER_UPDATE");
        PendingIntent pending = PendingIntent.getBroadcast(context, 0, intent, 0);
        AlarmManager manager = (AlarmManager) context.getSystemService(Context.ALARM_SERVICE);
        if (restore) {
            manager.setRepeating(AlarmManager.ELAPSED_REALTIME_WAKEUP,
                SystemClock.elapsedRealtime() + 20000L, 20000L, pending);
            return base().put("alarm", true);
        }
        manager.cancel(pending);
        return base().put("alarm", false);
    }
}
