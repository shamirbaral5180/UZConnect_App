using System.Globalization;
using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using UZConnect.Core.Abstractions;
using UZConnect.Core.Models;

namespace UZConnect.Windows.Usb;

/// <summary>
/// Enumerates USB devices from Windows PnP and watches for arrival/removal.
/// Identity is discovered from the machine; no VID/PID is hard-coded.
/// </summary>
public sealed partial class UsbDeviceService : IUsbDeviceService, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly IDiagnosticsLog _log;
    private readonly object _gate = new();
    private readonly Timer _pollTimer;
    private ManagementEventWatcher? _creationWatcher;
    private ManagementEventWatcher? _removalWatcher;
    private string _lastFingerprint = string.Empty;
    private bool _disposed;

    public UsbDeviceService(IDiagnosticsLog log)
    {
        _log = log;
        _pollTimer = new Timer(_ => _ = PollAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event EventHandler<UsbDevicesChangedEventArgs>? DevicesChanged;

    public void Start()
    {
        try
        {
            _creationWatcher = new ManagementEventWatcher(
                new WqlEventQuery("SELECT * FROM __InstanceCreationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_PnPEntity'"));
            _creationWatcher.EventArrived += (_, args) => OnWmiEvent(args, "arrival");
            _creationWatcher.Start();

            _removalWatcher = new ManagementEventWatcher(
                new WqlEventQuery("SELECT * FROM __InstanceDeletionEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_PnPEntity'"));
            _removalWatcher.EventArrived += (_, args) => OnWmiEvent(args, "removal");
            _removalWatcher.Start();

            _log.Info("USB", "Windows PnP arrival/removal watchers started.");
        }
        catch (Exception ex)
        {
            _log.Error("USB", "WMI device watchers could not be started; polling remains active.", ex);
        }

        _pollTimer.Change(TimeSpan.Zero, PollInterval);
    }

    private void OnWmiEvent(EventArrivedEventArgs args, string reason)
    {
        try
        {
            var target = (ManagementBaseObject)args.NewEvent["TargetInstance"];
            var deviceId = target["DeviceID"] as string;
            if (!string.IsNullOrWhiteSpace(deviceId) && deviceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
            {
                _log.Trace("USB", $"PnP {reason}: {deviceId}");
            }
        }
        catch
        {
            // Event payload shape is not important; a refresh follows.
        }

        _ = PollAsync(reason);
    }

    private async Task PollAsync(string reason = "poll")
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var devices = await EnumerateAsync().ConfigureAwait(false);
            var fingerprint = string.Join("|", devices.Select(d => d.InstanceId).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            lock (_gate)
            {
                if (fingerprint == _lastFingerprint)
                {
                    return;
                }

                _lastFingerprint = fingerprint;
            }

            DevicesChanged?.Invoke(this, new UsbDevicesChangedEventArgs(devices, reason));
        }
        catch (Exception ex)
        {
            _log.Error("USB", "USB enumeration failed.", ex);
        }
    }

    public Task<IReadOnlyList<UsbDeviceInfo>> EnumerateAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => EnumerateCore(cancellationToken), cancellationToken);

    private IReadOnlyList<UsbDeviceInfo> EnumerateCore(CancellationToken cancellationToken)
    {
        var entities = new List<PnpEntity>();
        using (var searcher = new ManagementObjectSearcher(
                   "SELECT DeviceID, Name, Description, Manufacturer, Service, PNPClass, HardwareID, Status, ConfigManagerErrorCode FROM Win32_PnPEntity"))
        using (var collection = searcher.Get())
        {
            foreach (ManagementObject item in collection)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (item)
                {
                    var deviceId = item["DeviceID"] as string;
                    if (string.IsNullOrWhiteSpace(deviceId) ||
                        !deviceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var errorCode = TryInt(item["ConfigManagerErrorCode"]);
                    if (errorCode is > 0 and not 24 and not 45)
                    {
                        // Keep devices with a driver problem visible in diagnostics.
                    }

                    entities.Add(new PnpEntity(
                        deviceId,
                        item["Name"] as string ?? item["Description"] as string ?? deviceId,
                        item["Description"] as string,
                        item["Manufacturer"] as string,
                        item["Service"] as string,
                        item["PNPClass"] as string,
                        ToStringArray(item["HardwareID"]),
                        item["Status"] as string,
                        errorCode));
                }
            }
        }

        var composites = entities
            .Where(e => !e.DeviceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => e.DeviceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var result = new List<UsbDeviceInfo>();
        foreach (var composite in composites)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (vid, pid) = UsbIdParser.Parse(composite.DeviceId);
            var serial = composite.DeviceId.Split('\\').LastOrDefault();
            var parentPrefix = ReadRegistryValue(composite.DeviceId, "ParentIdPrefix");
            var busReported = ReadRegistryValue(composite.DeviceId, "BusReportedDeviceDesc");

            IEnumerable<PnpEntity> children;
            if (!string.IsNullOrWhiteSpace(parentPrefix))
            {
                children = entities.Where(e =>
                    e.DeviceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase) &&
                    e.DeviceId.Split('\\').LastOrDefault()?.StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase) == true);
            }
            else
            {
                children = entities.Where(e =>
                    e.DeviceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase) &&
                    UsbIdParser.Parse(e.DeviceId) == (vid, pid));
            }

            var interfaces = children
                .OrderBy(e => e.DeviceId, StringComparer.OrdinalIgnoreCase)
                .Select(e => ToInterface(e, vid, pid))
                .ToList();

            result.Add(new UsbDeviceInfo(
                composite.DeviceId,
                composite.Name,
                vid,
                pid,
                string.IsNullOrWhiteSpace(serial) ? null : serial,
                composite.Manufacturer,
                busReported,
                composite.Service,
                composite.HardwareIds,
                interfaces));
        }

        // A USB device that exposes interfaces but no enumerable composite parent is still
        // useful in diagnostics; add any orphan interfaces under a synthetic parent.
        var groupedChildIds = result.SelectMany(r => r.Interfaces).Select(i => i.InstanceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var orphan in entities.Where(e =>
                     e.DeviceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase) &&
                     !groupedChildIds.Contains(e.DeviceId)))
        {
            var (vid, pid) = UsbIdParser.Parse(orphan.DeviceId);
            result.Add(new UsbDeviceInfo(
                orphan.DeviceId,
                orphan.Name,
                vid,
                pid,
                null,
                orphan.Manufacturer,
                ReadRegistryValue(orphan.DeviceId, "BusReportedDeviceDesc"),
                orphan.Service,
                orphan.HardwareIds,
                new[] { ToInterface(orphan, vid, pid) }));
        }

        return result.OrderBy(d => d.VendorId).ThenBy(d => d.ProductId).ThenBy(d => d.InstanceId).ToList();
    }

    private static UsbInterfaceInfo ToInterface(PnpEntity entity, ushort? vid, ushort? pid)
    {
        var name = entity.Name;
        var service = entity.Service;
        var pnpClass = entity.PnpClass;
        var role = DetectRole(name, service, pnpClass, entity.DeviceId);
        var comPort = ComPortRegex().Match(name).Groups["port"].Value;
        return new UsbInterfaceInfo(
            name,
            entity.DeviceId,
            service,
            pnpClass,
            vid,
            pid,
            entity.HardwareIds.FirstOrDefault() ?? entity.DeviceId,
            string.IsNullOrWhiteSpace(comPort) ? null : comPort,
            role);
    }

    private static UsbInterfaceRole DetectRole(string name, string? service, string? pnpClass, string deviceId)
    {
        if (name.Contains("ADB", StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(service, "WINUSB", StringComparison.OrdinalIgnoreCase) && deviceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase)))
        {
            return UsbInterfaceRole.Adb;
        }

        if (name.Contains("Diagnostics", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(service, "qcusbdiag", StringComparison.OrdinalIgnoreCase))
        {
            return UsbInterfaceRole.Diagnostics;
        }

        if (name.Contains("RNDIS", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(service, "usbrndis6", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(service, "rndismpx", StringComparison.OrdinalIgnoreCase))
        {
            return UsbInterfaceRole.Rndis;
        }

        if (string.Equals(pnpClass, "Modem", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Modem", StringComparison.OrdinalIgnoreCase))
        {
            return UsbInterfaceRole.Modem;
        }

        if (ComPortRegex().IsMatch(name))
        {
            return UsbInterfaceRole.Serial;
        }

        if (pnpClass is "USBDevice")
        {
            return UsbInterfaceRole.Unknown;
        }

        return UsbInterfaceRole.Unknown;
    }

    public async Task<IReadOnlyList<SerialPortInfo>> GetSerialPortsAsync(CancellationToken cancellationToken = default)
    {
        var wmi = await Task.Run(() => GetSerialPortsCore(cancellationToken), cancellationToken).ConfigureAwait(false);
        var result = new List<SerialPortInfo>();
        foreach (var name in SerialPort.GetPortNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var match = wmi.FirstOrDefault(p => string.Equals(p.PortName, name, StringComparison.OrdinalIgnoreCase));
            result.Add(match ?? new SerialPortInfo(name, null, null, null, null, null, false));
        }

        return result;
    }

    private static List<SerialPortInfo> GetSerialPortsCore(CancellationToken cancellationToken)
    {
        var result = new List<SerialPortInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name, Description, PNPDeviceID, HardwareID FROM Win32_SerialPort");
            using var collection = searcher.Get();
            foreach (ManagementObject item in collection)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (item)
                {
                    var port = item["DeviceID"] as string;
                    if (string.IsNullOrWhiteSpace(port))
                    {
                        continue;
                    }

                    var instance = item["PNPDeviceID"] as string;
                    var hardwareId = ToStringArray(item["HardwareID"]).FirstOrDefault();
                    var (vid, pid) = UsbIdParser.Parse(instance ?? hardwareId);
                    result.Add(new SerialPortInfo(
                        port,
                        item["Name"] as string ?? item["Description"] as string,
                        instance,
                        hardwareId,
                        vid,
                        pid,
                        false));
                }
            }
        }
        catch
        {
            // Fall back to SerialPort.GetPortNames in the caller.
        }

        return result;
    }

    private static string? ReadRegistryValue(string deviceId, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\" + deviceId, writable: false);
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }

    private static string[] ToStringArray(object? value)
    {
        return value switch
        {
            string[] array => array,
            object[] array => array.OfType<string>().ToArray(),
            string single => new[] { single },
            _ => Array.Empty<string>()
        };
    }

    private static int? TryInt(object? value)
    {
        try
        {
            return value is null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"\((COM\d+)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComPortRegex();

    public void Dispose()
    {
        _disposed = true;
        _pollTimer.Dispose();
        _creationWatcher?.Stop();
        _creationWatcher?.Dispose();
        _removalWatcher?.Stop();
        _removalWatcher?.Dispose();
    }

    private sealed record PnpEntity(
        string DeviceId,
        string Name,
        string? Description,
        string? Manufacturer,
        string? Service,
        string? PnpClass,
        string[] HardwareIds,
        string? Status,
        int? ConfigManagerErrorCode);
}
