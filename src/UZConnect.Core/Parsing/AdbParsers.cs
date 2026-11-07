using System.Globalization;
using System.Text.RegularExpressions;

namespace UZConnect.Core.Parsing;

public sealed record AdbDeviceEntry(
    string Serial,
    string State,
    string? Product,
    string? Model,
    string? Device,
    int? TransportId)
{
    public bool IsReady => string.Equals(State, "device", StringComparison.OrdinalIgnoreCase);
    public bool IsUnauthorized => string.Equals(State, "unauthorized", StringComparison.OrdinalIgnoreCase);
    public bool IsOffline => string.Equals(State, "offline", StringComparison.OrdinalIgnoreCase);
}

public static partial class AdbDevicesParser
{
    public static IReadOnlyList<AdbDeviceEntry> Parse(string? output)
    {
        var result = new List<AdbDeviceEntry>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return result;
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 ||
                line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("* daemon", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("adb server", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var serial = parts[0];
            var state = parts[1];
            string? product = null, model = null, device = null;
            int? transportId = null;

            for (var i = 2; i < parts.Length; i++)
            {
                var token = parts[i];
                var colon = token.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                var key = token[..colon];
                var value = token[(colon + 1)..];
                switch (key)
                {
                    case "product": product = value; break;
                    case "model": model = value; break;
                    case "device": device = value; break;
                    case "transport_id" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id):
                        transportId = id;
                        break;
                }
            }

            result.Add(new AdbDeviceEntry(serial, state, product, model, device, transportId));
        }

        return result;
    }
}

public static partial class GetPropParser
{
    [GeneratedRegex(@"^\[(?<key>[^\]]+)\]:\s*\[(?<value>.*)\]\s*$", RegexOptions.Multiline)]
    private static partial Regex PropRegex();

    public static IReadOnlyDictionary<string, string> Parse(string? output)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(output))
        {
            return result;
        }

        foreach (Match match in PropRegex().Matches(output))
        {
            result[match.Groups["key"].Value] = match.Groups["value"].Value;
        }

        return result;
    }

    public static string Value(IReadOnlyDictionary<string, string> props, string key, string fallback = "Not available")
        => props.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
}
