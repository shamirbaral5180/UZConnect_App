using System.Net;
using System.Text;
using System.Text.Json;

namespace UZConnect.Core.Models;

// Deliberately contains no password, PSK, full configuration, or device serial.
public sealed record WifiClientInfo(string Name, string IpAddress, string MacAddress);
public sealed record WifiDhcpRange(string Start, string End, string Lease);
public sealed record WifiSnapshot(
    int? ApState, bool HostapdRunning, string? Ssid, int? Channel, int? MaximumClients,
    bool? HiddenSsid, bool? PasswordConfigured, string Security,
    string? Gateway, IReadOnlyList<WifiDhcpRange> DhcpRanges,
    IReadOnlyList<WifiClientInfo> Clients, bool ClientsAvailable,
    bool ConfigurationAvailable, bool RndisBridged, DateTimeOffset ObservedAt)
{
    public string Status => ApState switch
    {
        13 when HostapdRunning => "On (reported by modem)",
        11 when !HostapdRunning => "Off (reported by modem)",
        10 => "Turning off", 12 => "Turning on", 14 => "Unavailable",
        _ => "Not confirmed"
    };

    public static WifiSnapshot Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("version").GetInt32() != 1 ||
            !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("Wi-Fi information is unavailable.");
        string? Text(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
        int? Number(string key) => root.TryGetProperty(key, out var v) && v.TryGetInt32(out var n) ? n : null;
        bool? Flag(string key) => root.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;
        var clients = new List<WifiClientInfo>();
        if (root.TryGetProperty("clients", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray().Take(128))
            {
                var mac = row.GetProperty("mac").GetString() ?? "";
                if (!IsMac(mac)) continue;
                var ip = row.GetProperty("ip").GetString() ?? "";
                clients.Add(new WifiClientInfo(Clean(row.GetProperty("name").GetString(), 128),
                    IPAddress.TryParse(ip, out _) ? ip : "Not available", mac.ToUpperInvariant()));
            }
        }
        var ranges = new List<WifiDhcpRange>();
        if (root.TryGetProperty("ranges", out var rangeRows) && rangeRows.ValueKind == JsonValueKind.Array)
            foreach (var row in rangeRows.EnumerateArray().Take(32))
            {
                var start = row.GetProperty("start").GetString() ?? "";
                var end = row.GetProperty("end").GetString() ?? "";
                if (IPAddress.TryParse(start, out _) && IPAddress.TryParse(end, out _))
                    ranges.Add(new(start, end, Clean(row.GetProperty("lease").GetString(), 32)));
            }
        return new(Number("apState"), Flag("hostapd") == true, Text("ssid"), Number("channel"),
            Number("maximumClients"), Flag("hidden"), Flag("passwordConfigured"), Text("security") ?? "Not available",
            Text("gateway"), ranges, clients.DistinctBy(c => c.MacAddress).ToArray(), Flag("clientsAvailable") == true,
            Flag("configurationAvailable") == true, Flag("rndisBridged") == true, DateTimeOffset.UtcNow);
    }

    private static string Clean(string? value, int limit) => string.IsNullOrWhiteSpace(value) ? "Not available"
        : new string(value.Where(c => !char.IsControl(c)).Take(limit).ToArray());

    private static bool IsMac(string value) => value.Length == 17 && value.Split(':') is { Length: 6 } parts &&
        parts.All(p => p.Length == 2 && p.All(Uri.IsHexDigit));
}

public sealed record WifiBasicSettings(string Ssid, int MaximumClients)
{
    public string? ValidationError => string.IsNullOrWhiteSpace(Ssid) || Encoding.UTF8.GetByteCount(Ssid) > 32 ||
        Ssid.Any(char.IsControl) ? "Enter a network name of 1–32 UTF-8 bytes without control characters."
        : MaximumClients is < 1 or > 10 ? "Choose a maximum of 1–10 devices." : null;
}

public sealed record WifiFeature(string Name, string Status, string Reason);

public static class WifiCapabilities
{
    // Release gate for the verified transaction. Enabled only after the modem qualification run:
    // apply, active/saved agreement, restart, reboot persistence and hardware rollback all passed.
    public static bool BasicChangesVerified => true;
    public static IReadOnlyList<WifiFeature> Features { get; } = new WifiFeature[]
    {
        new("Network name / maximum devices", "Available", "Changes are backed up, applied, confirmed in active and saved state, and restored if verification fails."),
        new("Wi-Fi on / off", "Available", "Turns the access point off or on, then verifies the access-point state and the running service before reporting success."),
        new("Password changes", "Unavailable", "The stock networking service writes the passphrase to the Android log during every access-point restart."),
        new("Security mode", "Read only", "Only the current security configuration has been verified."),
        new("Channel selection", "Read only", "The modem's channel-changing control does not apply changes."),
        new("Hide network name", "Read only", "The modem service overwrites the hidden flag from its in-memory configuration on every restart."),
        new("Allow / block devices", "Unavailable", "Device filtering enforcement has not been verified."),
        new("Reserved addresses", "Unavailable", "No verified persistent reservation mechanism is available."),
        new("DHCP / gateway / DNS", "Read only", "Working configuration controls are not available; changes may interrupt USB and Wi-Fi."),
    };
}
