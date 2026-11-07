using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using UZConnect.Core.Abstractions;
using UZConnect.Core.Models;

namespace UZConnect.Windows.Services;

/// <summary>
/// Keeps a bounded in-memory log and appends a redacted line-oriented log to disk.
/// SMS bodies, phone numbers and IMEIs must never be written here.
/// </summary>
public sealed partial class DiagnosticsLog : IDiagnosticsLog, IDisposable
{
    private const int MaxEntries = 2000;
    private readonly object _gate = new();
    private readonly List<DiagnosticEntry> _entries = new();
    private readonly StreamWriter? _writer;
    private DateTimeOffset? _lastSuccess;

    public DiagnosticsLog()
    {
        LogFilePath = Path.Combine(AppPaths.LogsDirectory, $"uzconnect-{DateTime.Now:yyyyMMdd}.log");
        try
        {
            _writer = new StreamWriter(new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true
            };
        }
        catch
        {
            _writer = null;
        }

        Info("App", "Diagnostics log started.");
    }

    public event EventHandler<DiagnosticEntry>? EntryAdded;

    public IReadOnlyList<DiagnosticEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public DateTimeOffset? LastSuccessfulCommunication
    {
        get
        {
            lock (_gate)
            {
                return _lastSuccess;
            }
        }
    }

    public string LogFilePath { get; }

    public void Trace(string category, string message) => Add(DiagnosticLevel.Trace, category, message);
    public void Info(string category, string message) => Add(DiagnosticLevel.Info, category, message);
    public void Warning(string category, string message) => Add(DiagnosticLevel.Warning, category, message);
    public void Error(string category, string message, Exception? exception = null)
        => Add(DiagnosticLevel.Error, category, exception is null ? message : $"{message} {exception.GetType().Name}: {exception.Message}");

    public void MarkCommunicationSuccess(string category, string message)
    {
        lock (_gate)
        {
            _lastSuccess = DateTimeOffset.Now;
        }

        Info(category, message);
    }

    private void Add(DiagnosticLevel level, string category, string message)
    {
        var entry = new DiagnosticEntry(DateTimeOffset.Now, level, category, Redact(message));
        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
            }

            try
            {
                _writer?.WriteLine($"{entry.Timestamp:O} {entry.LevelLabel,-5} [{entry.Category}] {entry.Message}");
            }
            catch
            {
                // Logging must never take down the app.
            }
        }

        EntryAdded?.Invoke(this, entry);
    }

    public async Task ExportAsync(string path, DiagnosticReportContext context, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var safeDevice = context.Device is null
            ? null
            : new
            {
                context.Device.Model,
                context.Device.Manufacturer,
                context.Device.HardwareRevision,
                context.Device.HardwareRevisionSource,
                context.Device.FirmwareVersion,
                context.Device.AndroidVersion,
                context.Device.BuildId,
                context.Device.Platform,
                context.Device.SerialNumber,
                Imei = context.Device.MaskedImei,
                context.Device.BasebandVersion,
                context.Device.ApiLevel
            };

        var report = new
        {
            report = "UZConnect diagnostic report",
            context.AppVersion,
            GeneratedAt = context.GeneratedAt,
            Note = "SMS contents, phone numbers and full IMEIs are intentionally excluded. Review before sharing.",
            UsbDevice = context.UsbDevice,
            ActiveDevice = context.ActiveDevice is null ? null : new
            {
                context.ActiveDevice.DisplayName,
                context.ActiveDevice.ProviderKind,
                context.ActiveDevice.VidPid,
                context.ActiveDevice.SerialNumber,
                context.ActiveDevice.AdbSerial,
                context.ActiveDevice.Model,
                context.ActiveDevice.FirmwareVersion,
                context.ActiveDevice.ComPort,
                context.ActiveDevice.Interfaces
            },
            Device = safeDevice,
            Capabilities = context.Capabilities,
            UsbDevices = context.UsbDevices,
            context.ComPorts,
            Adb = new { context.AdbFound, context.AdbPath, context.AdbAuthorized, context.AdbState },
            context.AtProbeResults,
            LastSuccessfulCommunication = LastSuccessfulCommunication,
            Entries = context.Entries.Select(e => new
            {
                e.Timestamp,
                Level = e.LevelLabel,
                e.Category,
                e.Message
            })
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var json = JsonSerializer.Serialize(report, options);
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
        Info("Diagnostics", $"Diagnostic report exported to {path}.");
    }

    private static string Redact(string message)
    {
        // IMEI-like 15 digit sequences and long digit runs are masked.
        var redacted = LongDigitsRegex().Replace(message, match =>
        {
            var value = match.Value;
            return value.Length <= 4 ? new string('*', value.Length) : value[..4] + new string('*', Math.Min(value.Length - 4, 10));
        });

        // Mask common phone-number shapes when they appear in diagnostic text.
        return PhoneRegex().Replace(redacted, match =>
        {
            var digits = new string(match.Value.Where(char.IsDigit).ToArray());
            if (digits.Length < 6)
            {
                return match.Value;
            }

            return new string('*', Math.Max(0, digits.Length - 4)) + digits[^4..];
        });
    }

    [GeneratedRegex(@"\b\d{15,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex LongDigitsRegex();

    [GeneratedRegex(@"(?<![\w])(?:\+\d[\d\s\-()]{6,}\d)(?![\w])", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneRegex();

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
        }
    }
}
