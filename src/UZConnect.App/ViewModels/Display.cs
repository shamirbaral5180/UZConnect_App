namespace UZConnect.App.ViewModels;

public static class Display
{
    public static string OrNotAvailable(string? value)
        => string.IsNullOrWhiteSpace(value) || string.Equals(value, "null", StringComparison.OrdinalIgnoreCase)
            ? "Not available"
            : value!;

    public static string Bytes(long? bytes)
    {
        if (bytes is null)
        {
            return "Not available";
        }

        var value = bytes.Value;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.#} {units[unit]}";
    }

    public static string Duration(long? seconds)
    {
        if (seconds is null or <= 0)
        {
            return "Not available";
        }

        var span = TimeSpan.FromSeconds(seconds.Value);
        return span.TotalDays >= 1
            ? $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m"
            : span.TotalHours >= 1
                ? $"{(int)span.TotalHours}h {span.Minutes}m"
                : $"{span.Minutes}m {span.Seconds}s";
    }

    public static string MessageTime(DateTimeOffset value)
    {
        if (value.Year < 2000)
        {
            return "Device date not set";
        }

        var local = value.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date
            ? local.ToString("HH:mm")
            : local.ToString("dd MMM HH:mm");
    }

    public static string FullTime(DateTimeOffset value)
    {
        if (value.Year < 2000)
        {
            return "Device date not set";
        }

        return value.ToLocalTime().ToString("dd MMM yyyy, HH:mm");
    }

    public static string Timestamp(DateTimeOffset? value)
        => value is null ? "No successful communication" : value.Value.ToLocalTime().ToString("dd MMM yyyy, HH:mm:ss");

    public static string MaskPhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Not available";
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 4 ? new string('*', trimmed.Length) : new string('*', trimmed.Length - 4) + trimmed[^4..];
    }

    public static string Initial(string? name)
    {
        var value = string.IsNullOrWhiteSpace(name) ? "?" : name.Trim();
        return value[..1].ToUpperInvariant();
    }
}
