namespace UZConnect.Core.Models;

public sealed record DeviceInformation(
    string Model,
    string Manufacturer,
    string HardwareRevision,
    string HardwareRevisionSource,
    string FirmwareVersion,
    string AndroidVersion,
    string BuildId,
    string BuildDescription,
    string Platform,
    string SerialNumber,
    string Imei,
    string BasebandVersion,
    int ApiLevel,
    string ProductName,
    string BoardName)
{
    public static DeviceInformation Unknown { get; } = new(
        "Not available", "Not available", "Not available", "Not available", "Not available",
        "Not available", "Not available", "Not available", "Not available", "Not available",
        "Not available", "Not available", 0, "Not available", "Not available");

    public string MaskedImei => string.IsNullOrWhiteSpace(Imei) || Imei == "Not available"
        ? "Not available"
        : Masking.MaskIdentifier(Imei);
}

public sealed record SimInfo(
    bool IsPresent,
    string State,
    string? PhoneNumber,
    string? Operator,
    string? IccId,
    string? CountryIso,
    string? DisplayName,
    string StorageSummary)
{
    public static SimInfo NotAvailable { get; } = new(
        false, "Not available", null, null, null, null, null, "Not available");

    public string PhoneNumberOrNotAvailable =>
        string.IsNullOrWhiteSpace(PhoneNumber) ? "Not available" : PhoneNumber!;
}

public sealed record SimContact(
    string DeviceId,
    string Name,
    string Number,
    string? Email,
    string Storage,
    int Index)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Number : Name;
}

public sealed record ContactStorageInfo(
    int Used,
    int Capacity,
    string Summary)
{
    public static ContactStorageInfo NotAvailable { get; } = new(0, 0, "Not available");
}

public sealed record ApnConfig(
    string Id,
    string Name,
    string Apn,
    string Type,
    string? Mmsc,
    string? Proxy,
    int? Port,
    bool IsCurrent,
    string? Numeric,
    bool IsEditable)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Apn : Name;
}

public sealed record ConnectionStatus(
    bool MobileDataEnabled,
    bool DataConnected,
    string State,
    string? Apn,
    string? IpAddress,
    long? DurationSeconds,
    long? RxBytes,
    long? TxBytes)
{
    public static ConnectionStatus Unknown { get; } = new(
        false, false, "Not available", null, null, null, null, null);
}

public sealed record NetworkInfo(
    string RegistrationState,
    bool IsRegistered,
    string Operator,
    string NetworkType,
    int? SignalDbm,
    int? SignalLevel,
    string SignalSource,
    string? ApnName,
    string? Apn,
    string DataState,
    bool MobileDataEnabled,
    string? IpAddress,
    string? NetworkCountryIso,
    string? Roaming)
{
    public static NetworkInfo NotAvailable { get; } = new(
        "Not available", false, "Not available", "Not available", null, null, "Not available",
        null, null, "Not available", false, null, null, null);

    public string SignalDisplay => SignalDbm is null ? "Not available" : $"{SignalDbm} dBm";

    public string SignalLevelDisplay => SignalLevel switch
    {
        0 => "None",
        1 => "Weak",
        2 => "Fair",
        3 => "Good",
        4 => "Excellent",
        _ => "Not available"
    };
}

public sealed record ModemCapabilities(
    string ProviderName,
    bool IsVerified,
    bool CanReadSms,
    bool CanSendSms,
    bool CanDeleteSms,
    bool CanReadSimContacts,
    bool CanWriteSimContacts,
    bool CanControlData,
    bool CanReadSignal,
    bool CanReadApn,
    bool CanReadDataUsage,
    IReadOnlyList<string> Notes)
{
    public static ModemCapabilities None { get; } = new(
        "None", false, false, false, false, false, false, false, false, false, false,
        Array.Empty<string>());
}

public sealed record CommandResult(bool Success, string Command, string Output, string? Error, TimeSpan Duration)
{
    public static CommandResult Failed(string command, string error, TimeSpan duration) =>
        new(false, command, string.Empty, error, duration);
}

public static class Masking
{
    public static string MaskIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Not available";
        }

        var trimmed = value.Trim();
        if (trimmed.Length <= 4)
        {
            return new string('*', trimmed.Length);
        }

        var visible = trimmed.Length >= 8 ? 4 : 2;
        return string.Concat(trimmed.AsSpan(0, visible), new string('*', Math.Min(trimmed.Length - visible, 10)));
    }

    public static string MaskPhoneNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Not available";
        }

        var trimmed = value.Trim();
        if (trimmed.Length <= 4)
        {
            return new string('*', trimmed.Length);
        }

        return string.Concat(new string('*', trimmed.Length - 4), trimmed.AsSpan(trimmed.Length - 4));
    }
}
