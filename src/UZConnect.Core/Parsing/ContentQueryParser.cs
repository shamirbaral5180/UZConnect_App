using System.Globalization;
using System.Text.RegularExpressions;
using UZConnect.Core.Models;

namespace UZConnect.Core.Parsing;

public sealed record ContentRow(IReadOnlyDictionary<string, string> Values, string Raw)
{
    public string? Get(string key) => Values.TryGetValue(key, out var value) ? value : null;

    public long? GetLong(string key)
        => long.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    public int? GetInt(string key)
        => int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    public bool GetBool(string key)
    {
        var value = Get(key);
        return value is "1" or "true" or "TRUE";
    }
}

/// <summary>
/// Parses the output of Android's `content query` shell command. The command is
/// available on the stock firmware and is used without third-party binaries.
/// </summary>
public static partial class ContentQueryParser
{
    [GeneratedRegex(@"^Row:\s*\d+\s+(?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex RowRegex();

    [GeneratedRegex(@", (?=[A-Za-z_][A-Za-z0-9_]*=)", RegexOptions.CultureInvariant)]
    private static partial Regex FieldSeparatorRegex();

    public static IReadOnlyList<ContentRow> Parse(string? output)
    {
        var rows = new List<ContentRow>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return rows;
        }

        foreach (var line in output.Replace("\r\n", "\n").Split('\n'))
        {
            var match = RowRegex().Match(line.TrimEnd());
            if (!match.Success)
            {
                continue;
            }

            var rest = match.Groups["rest"].Value;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in FieldSeparatorRegex().Split(rest))
            {
                var equals = field.IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }

                var key = field[..equals].Trim();
                var value = field[(equals + 1)..];
                values[key] = value;
            }

            rows.Add(new ContentRow(values, line));
        }

        return rows;
    }

    /// <summary>
    /// Extracts a single column from a single-row query. Used for message bodies so
    /// commas, quotes and newlines in the body are preserved exactly.
    /// </summary>
    public static string? ExtractSingleValue(string? output, string column)
    {
        if (string.IsNullOrWhiteSpace(output) ||
            output.Contains("No result found", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalized = output.Replace("\r\n", "\n");
        var marker = column + "=";
        var index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var value = normalized[(index + marker.Length)..];
        if (value.EndsWith('\n'))
        {
            value = value[..^1];
        }

        return value;
    }

    public static SmsMessage ToMessage(
        ContentRow row,
        string body,
        string deviceSerial,
        SmsFolder folder,
        DateTimeOffset cachedAt)
    {
        var id = row.GetLong("_id") ?? 0;
        var dateMillis = row.GetLong("date") ?? 0;
        var timestamp = dateMillis > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(dateMillis)
            : DateTimeOffset.UnixEpoch;
        var read = row.GetBool("read") || folder is SmsFolder.Sent or SmsFolder.Outbox;

        return new SmsMessage(
            deviceSerial,
            id,
            folder,
            row.Get("address") ?? string.Empty,
            body,
            timestamp,
            read,
            row.GetLong("thread_id") ?? 0,
            row.GetInt("status") ?? 0,
            row.GetInt("error_code"),
            row.Get("service_center"),
            cachedAt);
    }

    public static SimInfo ToSimInfo(IReadOnlyList<ContentRow> rows, string state, string? operatorFallback)
    {
        if (rows.Count == 0)
        {
            return new SimInfo(
                !string.Equals(state, "ABSENT", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(state, "UNKNOWN", StringComparison.OrdinalIgnoreCase),
                state,
                null,
                operatorFallback,
                null,
                null,
                null,
                "Not available");
        }

        var row = rows[0];
        var number = row.Get("number");
        var carrier = row.Get("carrier_name");
        var display = row.Get("display_name");
        var icc = row.Get("icc_id");

        return new SimInfo(
            true,
            state,
            string.IsNullOrWhiteSpace(number) ? null : number,
            string.IsNullOrWhiteSpace(carrier) ? operatorFallback : carrier,
            string.IsNullOrWhiteSpace(icc) ? null : icc,
            row.Get("country_iso"),
            string.IsNullOrWhiteSpace(display) ? null : display,
            "Not available");
    }

    public static IReadOnlyList<SimContact> ToSimContacts(IReadOnlyList<ContentRow> rows)
    {
        var contacts = new List<SimContact>();
        var index = 0;
        foreach (var row in rows)
        {
            var number = row.Get("number") ?? string.Empty;
            var name = row.Get("name") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(number) && string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            contacts.Add(new SimContact(
                row.Get("_id") ?? index.ToString(CultureInfo.InvariantCulture),
                name,
                number,
                row.Get("emails"),
                "SIM",
                index));
            index++;
        }

        return contacts;
    }

    public static IReadOnlyList<ApnConfig> ToApns(IReadOnlyList<ContentRow> rows)
    {
        return rows.Select(row =>
        {
            var port = row.GetInt("port");
            return new ApnConfig(
                row.Get("_id") ?? string.Empty,
                row.Get("name") ?? string.Empty,
                row.Get("apn") ?? string.Empty,
                row.Get("type") ?? string.Empty,
                NullIfEmpty(row.Get("mmsc")),
                NullIfEmpty(row.Get("proxy")),
                port,
                row.GetBool("current"),
                NullIfEmpty(row.Get("numeric")),
                false);
        }).Where(a => !string.IsNullOrWhiteSpace(a.Apn) || !string.IsNullOrWhiteSpace(a.Name)).ToList();
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
