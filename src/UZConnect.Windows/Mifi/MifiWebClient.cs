using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UZConnect.Core.Abstractions;
using UZConnect.Windows.Adb;

namespace UZConnect.Windows.Mifi;

public sealed record MifiStatus(string Operator, string NetworkType, string NetStatus, int Rssi)
{
    public bool IsConnected => string.Equals(NetStatus, "Connected", StringComparison.OrdinalIgnoreCase);
}

public sealed record MifiTraffic(long UpKb, long DownKb, int ClientCount, int MaxClients);

public sealed record MifiConnectionInfo(
    string? Ip,
    string? Mask,
    string? Dns1,
    string? Dns2,
    string? Ssid,
    string? WlanIp);

public sealed record MifiProfile(int Number, string Name, string Apn, string User, string Password, int Auth);

public sealed record MifiWifiStatus(bool Enabled, string Ssid, int Clients, int MaxClients, int Channel);

public sealed record WifiControlResult(bool Supported, bool Enabled, string Message);

/// <summary>
/// Stock UZ801 web control channel on the USB RNDIS interface. It is used only for
/// functions the ADB surface does not expose (connection state, APN profiles, Wi-Fi
/// status, traffic counters). USB detection and SMS never depend on this channel.
/// </summary>
public sealed partial class MifiWebClient : IDisposable
{
    private readonly AdbClient _adb;
    private readonly IDiagnosticsLog _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly Dictionary<string, string> _baseUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MifiWebClient(AdbClient adb, IDiagnosticsLog log)
    {
        _adb = adb;
        _log = log;
    }

    public bool LastRequestSucceeded { get; private set; }
    public string? LastError { get; private set; }

    public async Task<MifiStatus?> GetStatusAsync(string serial, CancellationToken cancellationToken = default)
    {
        using var doc = await PostAsync(serial, new { funcNo = 1001 }, cancellationToken).ConfigureAwait(false);
        var result = FirstResult(doc);
        if (result is null)
        {
            return null;
        }

        return new MifiStatus(
            JsonString(result.Value, "oper") ?? "Not available",
            JsonString(result.Value, "netmode") ?? "Not available",
            JsonString(result.Value, "netstatus") ?? "Not available",
            JsonInt(result.Value, "rssi") ?? 0);
    }

    public async Task<MifiConnectionInfo?> GetConnectionInfoAsync(string serial, CancellationToken cancellationToken = default)
    {
        using var doc = await PostAsync(serial, new { funcNo = 1002 }, cancellationToken).ConfigureAwait(false);
        var result = FirstResult(doc);
        if (result is null)
        {
            return null;
        }

        return new MifiConnectionInfo(
            JsonString(result.Value, "IP"),
            JsonString(result.Value, "mask"),
            JsonString(result.Value, "dns1"),
            JsonString(result.Value, "dns2"),
            JsonString(result.Value, "ssid"),
            JsonString(result.Value, "wlan_ip"));
    }

    public async Task<MifiTraffic?> GetTrafficAsync(string serial, CancellationToken cancellationToken = default)
    {
        using var doc = await PostAsync(serial, new { funcNo = 1003 }, cancellationToken).ConfigureAwait(false);
        var result = FirstResult(doc);
        if (result is null)
        {
            return null;
        }

        return new MifiTraffic(
            JsonLong(result.Value, "up_bytes") ?? 0,
            JsonLong(result.Value, "down_bytes") ?? 0,
            JsonInt(result.Value, "client_num") ?? 0,
            JsonInt(result.Value, "maxSta") ?? 0);
    }

    public async Task<bool> SetConnectionModeAsync(string serial, bool connect, CancellationToken cancellationToken = default)
    {
        using var doc = await PostAsync(serial, new { funcNo = 1004, conn_mode = connect ? "0" : "1" }, cancellationToken)
            .ConfigureAwait(false);
        return doc is not null && Success(doc);
    }

    public async Task<MifiWifiStatus?> GetWifiStatusAsync(string serial, CancellationToken cancellationToken = default)
    {
        using var doc = await PostAsync(serial, new { funcNo = 1006 }, cancellationToken).ConfigureAwait(false);
        var result = FirstResult(doc);
        if (result is null)
        {
            return null;
        }

        return new MifiWifiStatus(
            string.Equals(JsonString(result.Value, "wifi_status"), "1", StringComparison.Ordinal),
            JsonString(result.Value, "ssid") ?? "Not available",
            JsonInt(result.Value, "client_num") ?? 0,
            JsonInt(result.Value, "maxSta") ?? 0,
            JsonInt(result.Value, "channel") ?? 0);
    }

    public async Task<IReadOnlyList<MifiProfile>> GetProfilesAsync(string serial, CancellationToken cancellationToken = default)
    {
        using var doc = await PostAsync(serial, new { funcNo = 1016 }, cancellationToken).ConfigureAwait(false);
        var result = FirstResult(doc);
        var profiles = new List<MifiProfile>();
        if (result is null ||
            !result.Value.TryGetProperty("info_arr", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return profiles;
        }

        foreach (var item in array.EnumerateArray())
        {
            profiles.Add(new MifiProfile(
                JsonInt(item, "no") ?? 0,
                JsonString(item, "name") ?? string.Empty,
                JsonString(item, "apn") ?? string.Empty,
                JsonString(item, "user") ?? string.Empty,
                JsonString(item, "pwd") ?? string.Empty,
                JsonInt(item, "auth") ?? 0));
        }

        return profiles;
    }

    internal async Task<bool> SetWifiBasicAsync(string serial, UZConnect.Core.Models.WifiBasicSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.ValidationError is not null) return false;
        using var doc = await PostAsync(serial, new { funcNo = 1007, ssid = settings.Ssid, maxSta = settings.MaximumClients }, cancellationToken)
            .ConfigureAwait(false);
        return doc is not null && Success(doc);
    }

    internal async Task<bool> RestartWifiAsync(string serial, CancellationToken cancellationToken)
    {
        using var doc = await PostAsync(serial, new { funcNo = 1008 }, cancellationToken).ConfigureAwait(false);
        return doc is not null && Success(doc);
    }

    public async Task<bool> SaveProfileAsync(string serial, MifiProfile profile, CancellationToken cancellationToken = default)
    {
        using var doc = await PostAsync(serial, new
        {
            funcNo = 1017,
            no = profile.Number.ToString(CultureInfo.InvariantCulture),
            name = profile.Name,
            apn = profile.Apn,
            user = profile.User,
            pwd = profile.Password,
            auth = profile.Auth.ToString(CultureInfo.InvariantCulture)
        }, cancellationToken).ConfigureAwait(false);
        return doc is not null && Success(doc);
    }

    public async Task<bool> ConnectProfileAsync(string serial, int profileNumber, CancellationToken cancellationToken = default)
    {
        using var doc = await PostAsync(serial, new
        {
            funcNo = 1018,
            profile_num = profileNumber.ToString(CultureInfo.InvariantCulture)
        }, cancellationToken).ConfigureAwait(false);
        return doc is not null && Success(doc);
    }

    public async Task<bool> SetNetworkModeAsync(string serial, int mode, CancellationToken cancellationToken = default)
    {
        using var doc = await PostAsync(serial, new { funcNo = 1005, net_mode = mode.ToString(CultureInfo.InvariantCulture) },
            cancellationToken).ConfigureAwait(false);
        return doc is not null && Success(doc);
    }

    /// <summary>
    /// The stock firmware exposes Wi-Fi status but no verified ON/OFF API. We tested
    /// `svc wifi` and it does not stop hostapd, so the caller must show this as unsupported.
    /// </summary>
    public WifiControlResult WifiControlCapability()
        => new(false, false,
            "The stock V2.3.11 web interface reports Wi-Fi status but exposes no verified ON/OFF control. No software path is assumed.");

    private async Task<JsonDocument?> PostAsync(string serial, object payload, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var baseUrl = await ResolveBaseUrlAsync(serial, cancellationToken).ConfigureAwait(false);
            if (baseUrl is null)
            {
                LastRequestSucceeded = false;
                LastError = "The modem control address could not be discovered.";
                return null;
            }

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(baseUrl + "/ajax", content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LastRequestSucceeded = false;
                LastError = $"Stock web control returned HTTP {(int)response.StatusCode}.";
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var document = JsonDocument.Parse(body);
            LastRequestSucceeded = Success(document);
            LastError = LastRequestSucceeded ? null : ErrorInfo(document);
            return document;
        }
        catch (Exception ex)
        {
            LastRequestSucceeded = false;
            LastError = ex.Message;
            _log.Warning("WebControl", $"Stock web control request failed: {ex.Message}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> ResolveBaseUrlAsync(string serial, CancellationToken cancellationToken)
    {
        if (_baseUrls.TryGetValue(serial, out var cached))
        {
            return cached;
        }

        var ipOutput = await _adb.ShellTextAsync(serial, "ip -f inet addr show br0", TimeSpan.FromSeconds(5), cancellationToken)
            .ConfigureAwait(false);
        var match = IpRegex().Match(ipOutput);
        string? address = match.Success ? match.Groups["ip"].Value : null;

        if (string.IsNullOrWhiteSpace(address))
        {
            var netcfg = await _adb.ShellTextAsync(serial, "netcfg", TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
            var netcfgMatch = NetcfgRegex().Match(netcfg);
            if (netcfgMatch.Success)
            {
                address = netcfgMatch.Groups["ip"].Value;
            }
        }

        if (string.IsNullOrWhiteSpace(address) || !IsPrivate(address))
        {
            return null;
        }

        var url = "http://" + address;
        _baseUrls[serial] = url;
        _log.Info("WebControl", $"Stock web control discovered at {url} over the USB RNDIS interface.");
        return url;
    }

    private static bool IsPrivate(string address)
    {
        if (!IPAddress.TryParse(address, out var ip))
        {
            return false;
        }

        var bytes = ip.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }

    private static JsonElement? FirstResult(JsonDocument? document)
    {
        if (document is null ||
            !document.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in results.EnumerateArray())
        {
            return item;
        }

        return null;
    }

    private static bool Success(JsonDocument document)
        => document.RootElement.TryGetProperty("flag", out var flag) &&
           string.Equals(flag.GetString(), "1", StringComparison.Ordinal);

    private static string? ErrorInfo(JsonDocument document)
        => document.RootElement.TryGetProperty("error_info", out var error) ? error.GetString() : null;

    private static string? JsonString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            }
            : null;

    private static int? JsonInt(JsonElement element, string name)
    {
        var value = JsonString(element, name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static long? JsonLong(JsonElement element, string name)
    {
        var value = JsonString(element, name);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    [GeneratedRegex(@"inet\s+(?<ip>\d+\.\d+\.\d+\.\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex IpRegex();

    [GeneratedRegex(@"br0\s+UP\s+(?<ip>\d+\.\d+\.\d+\.\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex NetcfgRegex();

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}


