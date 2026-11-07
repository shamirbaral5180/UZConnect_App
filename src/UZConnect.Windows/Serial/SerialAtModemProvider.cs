using System.Diagnostics;
using System.IO.Ports;
using UZConnect.Core.Abstractions;
using UZConnect.Core.Models;

namespace UZConnect.Windows.Serial;

/// <summary>
/// Experimental serial/AT transport. It never runs automatically: the user explicitly
/// probes a port from Diagnostics. On the tested stock firmware the only Qualcomm serial
/// endpoint is a diagnostics channel that did not answer AT, so no device is advertised.
/// </summary>
public sealed class SerialAtModemProvider : IModemProvider
{
    private readonly IUsbDeviceService _usb;
    private readonly IDiagnosticsLog _log;
    private readonly List<AtProbeResult> _probes = new();
    private ModemDevice? _activeDevice;

    public SerialAtModemProvider(IUsbDeviceService usb, IDiagnosticsLog log)
    {
        _usb = usb;
        _log = log;
    }

    public string Id => "serial-at";
    public string DisplayName => "Serial / AT commands";
    public ModemProviderKind Kind => ModemProviderKind.SerialAt;
    public bool IsVerified => _activeDevice is not null;
    public bool IsConnected => _activeDevice is not null;
    public ModemDevice? ActiveDevice => _activeDevice;
    public IReadOnlyList<AtProbeResult> Probes => _probes;

    public Task<IReadOnlyList<ModemDevice>> DetectDevicesAsync(CancellationToken cancellationToken = default)
    {
        // Deliberately no probing here. A COM port appearing is not evidence that it
        // speaks AT or that it belongs to the modem data path.
        return Task.FromResult<IReadOnlyList<ModemDevice>>(Array.Empty<ModemDevice>());
    }

    public async Task<IReadOnlyList<AtProbeResult>> ProbeAllAsync(CancellationToken cancellationToken = default)
    {
        _probes.Clear();
        var ports = await _usb.GetSerialPortsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var port in ports)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ProbeAsync(port.PortName, cancellationToken).ConfigureAwait(false);
            _probes.Add(result);
            if (result.Success && _activeDevice is null)
            {
                _activeDevice = new ModemDevice(
                    ModemDevice.MakeId(ModemProviderKind.SerialAt, port.PortName),
                    $"Serial modem on {port.PortName}",
                    ModemProviderKind.SerialAt,
                    port.VendorId,
                    port.ProductId,
                    null,
                    null,
                    port.Description,
                    "Not available",
                    null,
                    port.PortName,
                    port.InstanceId,
                    port.HardwareId is null ? Array.Empty<string>() : new[] { port.HardwareId },
                    Array.Empty<UsbInterfaceInfo>(),
                    true);
            }
        }

        return _probes;
    }

    public async Task<AtProbeResult> ProbeAsync(string portName, CancellationToken cancellationToken = default)
    {
        foreach (var baud in new[] { 115200, 9600 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await Task.Run(() => ProbeCore(portName, baud, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            if (result.Success)
            {
                _log.Info("AT", $"AT probe succeeded on {portName} at {baud}.");
                return result;
            }
        }

        _log.Info("AT", $"AT probe found no command channel on {portName}.");
        return new AtProbeResult(portName, 115200, false, string.Empty, "No AT response", TimeSpan.Zero);
    }

    private static AtProbeResult ProbeCore(string portName, int baud, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        SerialPort? port = null;
        try
        {
            port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 1200,
                WriteTimeout = 1200,
                DtrEnable = true,
                RtsEnable = true,
                NewLine = "\r"
            };
            port.Open();
            port.DiscardInBuffer();
            port.DiscardOutBuffer();
            port.Write("AT\r");
            Thread.Sleep(350);
            cancellationToken.ThrowIfCancellationRequested();
            var response = port.ReadExisting();
            if (!response.Contains("OK", StringComparison.OrdinalIgnoreCase))
            {
                port.Write("ATI\r");
                Thread.Sleep(350);
                response += port.ReadExisting();
            }

            stopwatch.Stop();
            var success = response.Contains("OK", StringComparison.OrdinalIgnoreCase);
            return new AtProbeResult(portName, baud, success, response.Trim(), success ? null : "No AT response", stopwatch.Elapsed);
        }
        catch (UnauthorizedAccessException ex)
        {
            stopwatch.Stop();
            return new AtProbeResult(portName, baud, false, string.Empty, "Port is in use", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new AtProbeResult(portName, baud, false, string.Empty, ex.Message, stopwatch.Elapsed);
        }
        finally
        {
            try
            {
                if (port?.IsOpen == true)
                {
                    port.Close();
                }
            }
            catch
            {
                // Ignore close failures.
            }

            port?.Dispose();
        }
    }

    public Task ConnectAsync(ModemDevice device, CancellationToken cancellationToken = default)
    {
        if (device.ProviderKind != ModemProviderKind.SerialAt || string.IsNullOrWhiteSpace(device.ComPort))
        {
            throw new InvalidOperationException("Not a verified serial device.");
        }

        _activeDevice = device;
        _log.Info("AT", $"Serial provider selected {device.ComPort}. Command support is experimental.");
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _activeDevice = null;
        return Task.CompletedTask;
    }

    public Task<DeviceInformation> GetDeviceInformationAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(DeviceInformation.Unknown);

    public Task<ModemCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new ModemCapabilities(
            "Serial / AT", false, false, false, false, false, false, false, false, false, false,
            new[] { "Experimental: AT transport has not been verified on stock V2.3.11." }));

    public Task<CommandResult> SendCommandAsync(string command, CancellationToken cancellationToken = default)
        => Task.FromResult(CommandResult.Failed(command, "The serial/AT transport has not been verified.", TimeSpan.Zero));

    public Task<ConnectionStatus> GetConnectionStatusAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(ConnectionStatus.Unknown);
}
