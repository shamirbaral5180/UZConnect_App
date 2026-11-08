import java.io.*;
import java.net.*;
import java.lang.reflect.*;
import java.util.*;
import java.security.MessageDigest;
import org.json.*;

/** Root read/backup helper. No secret values, exception details or raw files leave this process. */
public final class WifiProbe {
    private static final String CONFIG = "/data/mificonfig.json";
    private static final String AP = "/data/misc/wifi/hostapd.conf";
    private static final String BACKUPS = "/data/local/tmp/uzconnect-wifi-backups";

    public static void main(String[] args) {
        Thread deadline = new Thread(new Runnable() {
            public void run() {
                try { Thread.sleep(35000); } catch (InterruptedException ignored) { }
                Runtime.getRuntime().halt(2); // A stuck Binder must not leave a root helper process behind.
            }
        });
        deadline.setDaemon(true);
        deadline.start();
        try {
            JSONObject result;
            if (args.length == 1 && "read".equals(args[0])) result = read();
            else if (args.length == 1 && "saved".equals(args[0])) {
                JSONObject config = new JSONObject(text(CONFIG));
                result = base().put("ssid", config.getString("WIFIssid"))
                    .put("maximumClients", config.getInt("WIFImaxSta"));
            } else if (args.length == 2 && "backup".equals(args[0])) result = backup(args[1]);
            else if (args.length == 2 && "original".equals(args[0])) result = original(args[1]);
            else throw new IOException();
            System.out.println(result.toString());
        } catch (Throwable ignored) {
            System.out.println("{\"version\":1,\"ok\":false}");
        }
        System.out.flush();
        Runtime.getRuntime().halt(0);
    }

    private static JSONObject base() throws JSONException {
        return new JSONObject().put("version", 1).put("ok", true);
    }

    private static byte[] bytes(String path) throws IOException {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        try (InputStream in = new FileInputStream(path)) {
            byte[] buffer = new byte[4096]; int count;
            while ((count = in.read(buffer)) != -1) {
                if (out.size() + count > 1024 * 1024) throw new IOException();
                out.write(buffer, 0, count);
            }
        }
        return out.toByteArray();
    }

    private static String text(String path) throws IOException { return new String(bytes(path), "UTF-8"); }

    private static Object service(String name, String stub) throws Exception {
        Object binder = Class.forName("android.os.ServiceManager").getMethod("getService", String.class).invoke(null, name);
        return Class.forName(stub).getMethod("asInterface", Class.forName("android.os.IBinder")).invoke(null, binder);
    }

    private static JSONObject read() throws Exception {
        JSONObject result = base();
        boolean hostapd = false;
        JSONArray ranges = new JSONArray();
        File[] procs = new File("/proc").listFiles();
        if (procs == null) throw new IOException();
        for (File proc : procs) {
            if (!proc.getName().matches("[0-9]+")) continue;
            try {
                String[] argv = text(proc + "/cmdline").split("\u0000");
                if (argv.length == 0) continue;
                if ("/system/bin/hostapd".equals(argv[0])) hostapd = true;
                if ("/system/bin/dnsmasq".equals(argv[0])) for (String arg : argv) {
                    if (!arg.startsWith("--dhcp-range=")) continue;
                    String[] range = arg.substring(13).split(",");
                    if (range.length >= 3) ranges.put(new JSONObject().put("start", range[0])
                        .put("end", range[1]).put("lease", range[range.length - 1]));
                }
            } catch (IOException ignored) { }
        }
        result.put("hostapd", hostapd).put("ranges", ranges);
        try {
            Object wifi = service("wifi", "android.net.wifi.IWifiManager$Stub");
            result.put("apState", wifi.getClass().getMethod("getWifiApEnabledState").invoke(wifi));
        } catch (Exception ignored) { }

        // Never serialize the source map: hostapd.conf contains a PSK/passphrase.
        try {
            Map<String, String> config = new HashMap<String, String>();
            for (String line : text(AP).split("\n")) {
                line = line.trim();
                if (line.startsWith("#")) continue;
                int at = line.indexOf('=');
                if (at > 0) config.put(line.substring(0, at), line.substring(at + 1));
            }
            result.put("configurationAvailable", config.containsKey("interface"));
            if (config.containsKey("ssid")) result.put("ssid", config.get("ssid"));
            else if (config.containsKey("ssid2")) result.put("ssid", decodeHex(config.get("ssid2")));
            number(result, "channel", config.get("channel"));
            number(result, "maximumClients", config.get("max_num_sta"));
            if (config.containsKey("ignore_broadcast_ssid"))
                result.put("hidden", !"0".equals(config.get("ignore_broadcast_ssid")));
            result.put("passwordConfigured", nonempty(config.get("wpa_passphrase")) || nonempty(config.get("wpa_psk")));
            // The current passphrase is shown locally, read-only. It is never logged or sent
            // anywhere: the caller only renders it and drops it when the page is refreshed away.
            String passphrase = config.get("wpa_passphrase");
            if (passphrase != null && passphrase.length() >= 8 && passphrase.length() <= 63) {
                result.put("password", passphrase);
            }
            String security = "Not available";
            if ("2".equals(config.get("wpa")) && "CCMP".equals(config.get("rsn_pairwise"))) security = "WPA2 / AES";
            else if ("0".equals(config.get("wpa"))) security = "Open";
            else if (config.containsKey("wpa")) security = "Other (not verified)";
            result.put("security", security);
        } catch (Exception ignored) { result.put("configurationAvailable", false); }

        NetworkInterface bridge = NetworkInterface.getByName("br0");
        if (bridge != null) {
            Enumeration<InetAddress> addresses = bridge.getInetAddresses();
            while (addresses.hasMoreElements()) {
                InetAddress addr = addresses.nextElement();
                if (addr instanceof Inet4Address) result.put("gateway", addr.getHostAddress());
            }
        }
        result.put("rndisBridged", new File("/sys/class/net/br0/brif/rndis0").exists());
        JSONArray clients = new JSONArray();
        try {
            Object connectivity = service("connectivity", "android.net.IConnectivityManager$Stub");
            List<?> stations = (List<?>) connectivity.getClass().getMethod("getTetherConnectedSta").invoke(connectivity);
            if (stations == null) throw new IOException();
            Map<String, String> ips = new HashMap<String, String>();
            try {
                for (String line : text("/proc/net/arp").split("\n")) {
                    String[] fields = line.trim().split(" +");
                    if (fields.length >= 6 && "0x2".equals(fields[2]) &&
                        ("br0".equals(fields[5]) || "wlan0".equals(fields[5]))) ips.put(fields[3].toLowerCase(Locale.US), fields[0]);
                }
            } catch (IOException ignored) { }
            for (Object station : stations) {
                Class<?> type = station.getClass();
                String mac = (String) type.getField("deviceAddress").get(station);
                String name = (String) type.getField("deviceName").get(station);
                if (mac == null) continue;
                String ip = ips.get(mac.toLowerCase(Locale.US));
                clients.put(new JSONObject().put("mac", mac).put("name", name == null ? "" : name)
                    .put("ip", ip == null ? "" : ip));
            }
            result.put("clientsAvailable", true);
        } catch (Exception ignored) { result.put("clientsAvailable", false); }
        return result.put("clients", clients);
    }

    private static void number(JSONObject result, String name, String value) throws JSONException {
        if (value != null) try { result.put(name, Integer.parseInt(value)); } catch (NumberFormatException ignored) { }
    }
    private static boolean nonempty(String value) { return value != null && value.length() > 0; }
    private static String decodeHex(String value) throws IOException {
        if (value.length() % 2 != 0 || !value.matches("[0-9a-fA-F]+")) throw new IOException();
        byte[] decoded = new byte[value.length() / 2];
        for (int i = 0; i < decoded.length; i++) decoded[i] = (byte) Integer.parseInt(value.substring(i * 2, i * 2 + 2), 16);
        return new String(decoded, "UTF-8");
    }

    private static void mode(File path, String mode) throws Exception {
        if (Runtime.getRuntime().exec(new String[]{"/system/bin/chmod", mode, path.getPath()}).waitFor() != 0) throw new IOException();
    }
    // Reads only non-secret fields from a previously verified backup.
    private static JSONObject original(String token) throws Exception {
        if (!token.matches("[0-9a-f]{32}")) throw new IOException();
        File dir = new File(BACKUPS, token);
        if (!dir.getCanonicalPath().startsWith(BACKUPS + "/")) throw new IOException();
        JSONObject config = new JSONObject(text(new File(dir, "mificonfig.json").getPath()));
        return base().put("ssid", config.getString("WIFIssid"))
            .put("maximumClients", config.getInt("WIFImaxSta"));
    }

    private static JSONObject backup(String token) throws Exception {        if (!token.matches("[0-9a-f]{32}")) throw new IOException();
        File parent = new File(BACKUPS);
        if (!parent.getCanonicalPath().equals(BACKUPS)) throw new IOException();
        if (!parent.exists() && !parent.mkdir()) throw new IOException();
        mode(parent, "700");
        File dir = new File(parent, token);
        if (!dir.mkdir()) throw new IOException();
        mode(dir, "700");
        JSONObject hashes = new JSONObject();
        for (String source : new String[]{CONFIG, AP, "/data/misc/wifi/softap.conf"}) {
            byte[] content = bytes(source);
            File dest = new File(dir, new File(source).getName());
            if (!dest.createNewFile()) throw new IOException();
            mode(dest, "600");
            try (FileOutputStream out = new FileOutputStream(dest)) { out.write(content); out.getFD().sync(); }
            if (!Arrays.equals(content, bytes(dest.getPath()))) throw new IOException();
            hashes.put(dest.getName(), hex(MessageDigest.getInstance("SHA-256").digest(content)));
            Arrays.fill(content, (byte) 0);
        }
        File manifest = new File(dir, "manifest.json");
        try (FileOutputStream out = new FileOutputStream(manifest)) {
            out.write(hashes.toString().getBytes("UTF-8")); out.getFD().sync();
        }
        mode(manifest, "600");
        return base().put("backupId", token);
    }
    private static String hex(byte[] bytes) {
        StringBuilder result = new StringBuilder();
        for (byte b : bytes) result.append(String.format(Locale.US, "%02x", b & 255));
        return result.toString();
    }
}
