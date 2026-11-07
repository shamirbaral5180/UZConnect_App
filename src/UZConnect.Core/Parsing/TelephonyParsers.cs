using System.Globalization;
using System.Text.RegularExpressions;
using UZConnect.Core.Models;

namespace UZConnect.Core.Parsing;

public sealed record SignalReading(
    int? Dbm,
    int? Level,
    string Source,
    string Raw)
{
    public static SignalReading Unavailable(string raw) => new(null, null, "Not available", raw);
}

public sealed record TelephonySnapshot(
    string ServiceStateRaw,
    string? VoiceRegistrationCode,
    string? DataRegistrationCode,
    string RegistrationState,
    bool IsRegistered,
    bool IsEmergencyOnly,
    string? OperatorAlpha,
    string? OperatorNumeric,
    string? NetworkType,
    SignalReading Signal,
    int? DataConnectionState,
    string DataState,
    string? DataApn,
    bool DataPossible,
    string? DataReason)
{
    public static TelephonySnapshot Empty { get; } = new(
        "Not available", null, null, "Not available", false, false, null, null, null,
        SignalReading.Unavailable("Not available"), null, "Not available", null, false, null);
}

public static partial class TelephonyParser
{
    [GeneratedRegex(@"mServiceState=(?<value>.*)", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceStateRegex();

    [GeneratedRegex(@"mSignalStrength=(?<value>.*)", RegexOptions.CultureInvariant)]
    private static partial Regex SignalRegex();

    [GeneratedRegex(@"mDataConnectionState=(?<value>-?\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex DataStateRegex();

    [GeneratedRegex(@"mDataConnectionApn=(?<value>.*)", RegexOptions.CultureInvariant)]
    private static partial Regex DataApnRegex();

    [GeneratedRegex(@"mDataConnectionPossible=(?<value>true|false)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DataPossibleRegex();

    [GeneratedRegex(@"mDataConnectionReason=(?<value>.*)", RegexOptions.CultureInvariant)]
    private static partial Regex DataReasonRegex();

    public static TelephonySnapshot Parse(
        string? telephonyDump,
        IReadOnlyDictionary<string, string>? props = null)
    {
        if (string.IsNullOrWhiteSpace(telephonyDump))
        {
            return TelephonySnapshot.Empty;
        }

        var service = ServiceStateRegex().Match(telephonyDump);
        var serviceStateRaw = service.Success ? service.Groups["value"].Value.Trim() : "Not available";

        string? voiceCode = null;
        string? dataCode = null;
        var emergencyOnly = serviceStateRaw.Contains("EmergOnly=true", StringComparison.OrdinalIgnoreCase);
        var registration = "Not available";
        var registered = false;

        if (service.Success)
        {
            var stateTokens = serviceStateRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (stateTokens.Length >= 2)
            {
                voiceCode = stateTokens[0];
                dataCode = stateTokens[1];
                registered = voiceCode == "0" || dataCode == "0";
                registration = DescribeRegistration(voiceCode, dataCode, emergencyOnly);
            }
        }

        var signalMatch = SignalRegex().Match(telephonyDump);
        var signal = signalMatch.Success
            ? ParseSignal(signalMatch.Groups["value"].Value.Trim())
            : SignalReading.Unavailable("Not available");

        var dataStateMatch = DataStateRegex().Match(telephonyDump);
        int? dataStateCode = dataStateMatch.Success
            ? int.Parse(dataStateMatch.Groups["value"].Value, CultureInfo.InvariantCulture)
            : null;
        var dataState = dataStateCode switch
        {
            0 => "Disconnected",
            1 => "Connecting",
            2 => "Connected",
            3 => "Suspended",
            _ => "Not available"
        };

        var apnMatch = DataApnRegex().Match(telephonyDump);
        var apn = apnMatch.Success && !string.IsNullOrWhiteSpace(apnMatch.Groups["value"].Value)
            ? apnMatch.Groups["value"].Value.Trim()
            : null;

        var possibleMatch = DataPossibleRegex().Match(telephonyDump);
        var dataPossible = possibleMatch.Success &&
                           string.Equals(possibleMatch.Groups["value"].Value, "true", StringComparison.OrdinalIgnoreCase);

        var reasonMatch = DataReasonRegex().Match(telephonyDump);
        var reason = reasonMatch.Success && !string.IsNullOrWhiteSpace(reasonMatch.Groups["value"].Value)
            ? reasonMatch.Groups["value"].Value.Trim()
            : null;

        props ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var operatorAlpha = props.TryGetValue("gsm.operator.alpha", out var alpha) && !string.IsNullOrWhiteSpace(alpha)
            ? alpha
            : null;
        var operatorNumeric = props.TryGetValue("gsm.operator.numeric", out var numeric) && !string.IsNullOrWhiteSpace(numeric)
            ? numeric
            : null;
        var networkType = props.TryGetValue("gsm.network.type", out var netType) && !string.IsNullOrWhiteSpace(netType)
            ? netType
            : null;

        return new TelephonySnapshot(
            serviceStateRaw, voiceCode, dataCode, registration, registered, emergencyOnly,
            operatorAlpha, operatorNumeric, networkType, signal, dataStateCode, dataState,
            apn, dataPossible, reason);
    }

    private static string DescribeRegistration(string? voice, string? data, bool emergencyOnly)
    {
        var code = voice ?? data;
        return code switch
        {
            "0" => "Registered",
            "1" => "Out of service",
            "2" => emergencyOnly ? "Emergency only" : "Searching",
            "3" => "Radio off",
            _ => "Not available"
        };
    }

    /// <summary>
    /// Parses the unlabelled SignalStrength.toString() fields used by Android 4.4.
    /// Returns null when the device reports an unknown sentinel instead of inventing a value.
    /// </summary>
    public static SignalReading ParseSignal(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return SignalReading.Unavailable("Not available");
        }

        var value = raw.StartsWith("SignalStrength:", StringComparison.OrdinalIgnoreCase)
            ? raw["SignalStrength:".Length..].Trim()
            : raw.Trim();

        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rat = tokens.LastOrDefault(t => t.Contains('|') || t is "gsm" or "lte" or "cdma" or "evdo" or "wcdma" or "tdscdma")
                  ?? string.Empty;
        var numbers = new List<int>();
        foreach (var token in tokens)
        {
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                numbers.Add(number);
            }
            else if (numbers.Count > 0)
            {
                break;
            }
        }

        int Valid(int index, int min, int max) =>
            index < numbers.Count && numbers[index] >= min && numbers[index] <= max ? numbers[index] : int.MinValue;

        // Android 4.4 order: GsmSignalStrength, GsmBitErrorRate, CdmaDbm, CdmaEcio,
        // EvdoDbm, EvdoEcio, EvdoSnr, LteSignalStrength, LteRsrp, LteRsrq, LteRssnr,
        // LteCqi, TdScdma.
        var lteRsrp = Valid(8, -140, -44);
        var cdmaDbm = Valid(2, -120, -20);
        var evdoDbm = Valid(4, -120, -20);
        var gsm = Valid(0, 0, 31);

        int? dbm = null;
        var source = "Not available";

        if (lteRsrp != int.MinValue && (rat.Contains("lte", StringComparison.OrdinalIgnoreCase) || gsm == int.MinValue))
        {
            dbm = lteRsrp;
            source = "LTE RSRP";
        }
        else if (evdoDbm != int.MinValue && rat.Contains("evdo", StringComparison.OrdinalIgnoreCase))
        {
            dbm = evdoDbm;
            source = "EVDO";
        }
        else if (cdmaDbm != int.MinValue && rat.Contains("cdma", StringComparison.OrdinalIgnoreCase))
        {
            dbm = cdmaDbm;
            source = "CDMA";
        }
        else if (gsm != int.MinValue)
        {
            dbm = -113 + (2 * gsm);
            source = "GSM RSSI";
        }

        var level = dbm is null ? (int?)null : dbm switch
        {
            >= -65 => 4,
            >= -75 => 3,
            >= -85 => 2,
            >= -95 => 1,
            _ => 0
        };

        return dbm is null
            ? SignalReading.Unavailable(raw)
            : new SignalReading(dbm, level, source, raw);
    }
}

public static partial class ConnectivityParser
{
    [GeneratedRegex(@"Mobile data state:\s*(?<state>\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex MobileDataStateRegex();

    [GeneratedRegex(@"Data enabled:\s*user=(?<user>\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex DataEnabledRegex();

    [GeneratedRegex(@"\{(?<link>LinkAddresses: \[[^\]]*\])[^}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex LinkAddressRegex();

    public static (string? State, bool? DataEnabled, string? LinkAddress) Parse(string? connectivityDump)
    {
        if (string.IsNullOrWhiteSpace(connectivityDump))
        {
            return (null, null, null);
        }

        var state = MobileDataStateRegex().Match(connectivityDump) is { Success: true } stateMatch
            ? stateMatch.Groups["state"].Value
            : null;
        var enabled = DataEnabledRegex().Match(connectivityDump) is { Success: true } enabledMatch
            ? string.Equals(enabledMatch.Groups["user"].Value, "true", StringComparison.OrdinalIgnoreCase)
            : (bool?)null;
        var link = LinkAddressRegex().Match(connectivityDump) is { Success: true } linkMatch
            ? linkMatch.Groups["link"].Value
            : null;

        return (state, enabled, link);
    }
}

public static partial class DataUsageParser
{
    public static (long? RxBytes, long? TxBytes) ParseProcNetDev(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return (null, null);
        }

        long rx = 0;
        long tx = 0;
        var found = false;

        foreach (var line in output.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            if (!name.StartsWith("rmnet", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("wwan", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fields = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 9)
            {
                continue;
            }

            if (long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var inBytes) &&
                long.TryParse(fields[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var outBytes))
            {
                rx += inBytes;
                tx += outBytes;
                found = true;
            }
        }

        return found ? (rx, tx) : (null, null);
    }
}

public static partial class IphoneSubInfoParser
{
    [GeneratedRegex(@"Device ID\s*=\s*(?<id>[0-9A-Fa-f]+)", RegexOptions.CultureInvariant)]
    private static partial Regex ImeiRegex();

    public static string ParseImei(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return "Not available";
        }

        var match = ImeiRegex().Match(output);
        return match.Success ? match.Groups["id"].Value : "Not available";
    }
}

public static class SmsFolderUris
{
    public static string For(SmsFolder folder) => folder switch
    {
        SmsFolder.Inbox => "content://sms/inbox",
        SmsFolder.Sent => "content://sms/sent",
        SmsFolder.Drafts => "content://sms/draft",
        SmsFolder.Outbox => "content://sms/outbox",
        SmsFolder.Failed => "content://sms/failed",
        SmsFolder.Queued => "content://sms/queued",
        SmsFolder.Recycle => "content://sms/inbox",
        _ => "content://sms/inbox"
    };

    public static string Name(SmsFolder folder) => folder switch
    {
        SmsFolder.Inbox => "Inbox",
        SmsFolder.Sent => "Sent",
        SmsFolder.Drafts => "Drafts",
        SmsFolder.Outbox => "Outbox",
        SmsFolder.Failed => "Failed",
        SmsFolder.Queued => "Queued",
        SmsFolder.Recycle => "Recycle",
        _ => folder.ToString()
    };
}


