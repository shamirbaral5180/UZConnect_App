using System.Globalization;
using System.Text.RegularExpressions;

namespace UZConnect.Core.Models;

public enum ModemProviderKind
{
    Adb,
    SerialAt,
    Diagnostic
}

public enum UsbInterfaceRole
{
    Unknown,
    Adb,
    Rndis,
    Modem,
    Diagnostics,
    Serial,
    MassStorage,
    Audio
}

/// <summary>
/// One USB interface (child) of a composite device, as enumerated by Windows.
/// Identity values are discovered at runtime; none are assumed by the application.
/// </summary>
public sealed record UsbInterfaceInfo(
    string Name,
    string InstanceId,
    string? Service,
    string? DeviceClass,
    ushort? VendorId,
    ushort? ProductId,
    string HardwareId,
    string? ComPort,
    UsbInterfaceRole Role)
{
    public bool IsAdb => Role == UsbInterfaceRole.Adb;
    public bool IsRndis => Role == UsbInterfaceRole.Rndis;
    public bool IsModem => Role == UsbInterfaceRole.Modem;
    public bool IsDiagnostics => Role == UsbInterfaceRole.Diagnostics;

    public string RoleLabel => Role switch
    {
        UsbInterfaceRole.Adb => "ADB",
        UsbInterfaceRole.Rndis => "RNDIS",
        UsbInterfaceRole.Modem => "Modem",
        UsbInterfaceRole.Diagnostics => "Diagnostics",
        UsbInterfaceRole.Serial => "Serial",
        UsbInterfaceRole.MassStorage => "Mass storage",
        UsbInterfaceRole.Audio => "Audio",
        _ => "Interface"
    };
}

/// <summary>
/// A composite USB device discovered from Windows PnP enumeration.
/// </summary>
public sealed record UsbDeviceInfo(
    string InstanceId,
    string DisplayName,
    ushort? VendorId,
    ushort? ProductId,
    string? SerialNumber,
    string? Manufacturer,
    string? BusReportedDescription,
    string? Service,
    IReadOnlyList<string> HardwareIds,
    IReadOnlyList<UsbInterfaceInfo> Interfaces)
{
    public string VidPid => VendorId is not null && ProductId is not null
        ? string.Create(CultureInfo.InvariantCulture, $"VID_{VendorId:X4} PID_{ProductId:X4}")
        : "VID/PID not available";

    public bool HasAdb => Interfaces.Any(i => i.IsAdb);
    public bool HasRndis => Interfaces.Any(i => i.IsRndis);
    public bool HasModem => Interfaces.Any(i => i.IsModem);
    public UsbInterfaceInfo? AdbInterface => Interfaces.FirstOrDefault(i => i.IsAdb);
    public UsbInterfaceInfo? RndisInterface => Interfaces.FirstOrDefault(i => i.IsRndis);

    public IEnumerable<string> ComPorts =>
        Interfaces.Where(i => i.ComPort is not null).Select(i => i.ComPort!);
}

/// <summary>
/// A modem endpoint exposed by a provider. ADB and serial providers both produce this.
/// </summary>
public sealed record ModemDevice(
    string Id,
    string DisplayName,
    ModemProviderKind ProviderKind,
    ushort? VendorId,
    ushort? ProductId,
    string? SerialNumber,
    string? AdbSerial,
    string? Manufacturer,
    string? Model,
    string? FirmwareVersion,
    string? ComPort,
    string? CompositeInstanceId,
    IReadOnlyList<string> HardwareIds,
    IReadOnlyList<UsbInterfaceInfo> Interfaces,
    bool IsAuthorized = true)
{
    public string VidPid => VendorId is not null && ProductId is not null
        ? string.Create(CultureInfo.InvariantCulture, $"VID_{VendorId:X4} PID_{ProductId:X4}")
        : "VID/PID not available";

    public string ProviderLabel => ProviderKind switch
    {
        ModemProviderKind.Adb => "ADB",
        ModemProviderKind.SerialAt => "Serial / AT",
        _ => "Diagnostic"
    };

    public bool HasVerifiedTransport => ProviderKind == ModemProviderKind.Adb && IsAuthorized;

    public static string MakeId(ModemProviderKind kind, string identity) =>
        $"{kind.ToString().ToLowerInvariant()}:{identity}";
}

public static partial class UsbIdParser
{
    [GeneratedRegex(@"VID_([0-9A-Fa-f]{4}).*?PID_([0-9A-Fa-f]{4})", RegexOptions.CultureInvariant)]
    private static partial Regex VidPidRegex();

    public static (ushort? Vid, ushort? Pid) Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        var match = VidPidRegex().Match(value);
        if (!match.Success)
        {
            return (null, null);
        }

        return (
            ushort.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            ushort.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }
}
