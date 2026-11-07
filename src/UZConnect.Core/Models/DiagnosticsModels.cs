namespace UZConnect.Core.Models;

public enum DiagnosticLevel
{
    Trace,
    Info,
    Warning,
    Error
}

public sealed record DiagnosticEntry(
    DateTimeOffset Timestamp,
    DiagnosticLevel Level,
    string Category,
    string Message)
{
    public string TimeDisplay => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");

    public string LevelLabel => Level switch
    {
        DiagnosticLevel.Trace => "TRACE",
        DiagnosticLevel.Info => "INFO",
        DiagnosticLevel.Warning => "WARN",
        DiagnosticLevel.Error => "ERROR",
        _ => "INFO"
    };
}

public sealed record DiagnosticReportContext(
    string AppVersion,
    DateTimeOffset GeneratedAt,
    ModemDevice? UsbDevice,
    ModemDevice? ActiveDevice,
    DeviceInformation? Device,
    ModemCapabilities? Capabilities,
    IReadOnlyList<UsbDeviceInfo> UsbDevices,
    IReadOnlyList<string> ComPorts,
    bool AdbFound,
    string? AdbPath,
    bool AdbAuthorized,
    string? AdbState,
    IReadOnlyList<string> AtProbeResults,
    IReadOnlyList<DiagnosticEntry> Entries);

public sealed record ThemePreference(string Value)
{
    public static ThemePreference System { get; } = new("System");
}

public sealed record AtProbeResult(
    string PortName,
    int BaudRate,
    bool Success,
    string Response,
    string? Error,
    TimeSpan Duration)
{
    public string Summary => Success
        ? $"{PortName} @ {BaudRate}: OK ({Response.Replace("\r", " ").Replace("\n", " ").Trim()})"
        : $"{PortName} @ {BaudRate}: {Error ?? "No AT response"}";
}
