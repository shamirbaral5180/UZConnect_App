using UZConnect.Core.Models;

namespace UZConnect.Core.Abstractions;

/// <summary>
/// Common modem transport contract. A provider is only advertised when it has been
/// verified against the connected hardware; experimental transports are marked as such.
/// </summary>
public interface IModemProvider
{
    string Id { get; }
    string DisplayName { get; }
    ModemProviderKind Kind { get; }

    /// <summary>True when the transport has been verified against this device/firmware.</summary>
    bool IsVerified { get; }

    bool IsConnected { get; }
    ModemDevice? ActiveDevice { get; }

    Task<IReadOnlyList<ModemDevice>> DetectDevicesAsync(CancellationToken cancellationToken = default);
    Task ConnectAsync(ModemDevice device, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<DeviceInformation> GetDeviceInformationAsync(CancellationToken cancellationToken = default);
    Task<ModemCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
    Task<CommandResult> SendCommandAsync(string command, CancellationToken cancellationToken = default);
    Task<ConnectionStatus> GetConnectionStatusAsync(CancellationToken cancellationToken = default);
}

public interface ISmsCapable
{
    Task<IReadOnlyList<SmsMessage>> GetMessagesAsync(SmsFolder folder, CancellationToken cancellationToken = default);
    Task MarkReadAsync(long deviceMessageId, bool isRead, CancellationToken cancellationToken = default);
    Task DeleteMessageAsync(long deviceMessageId, CancellationToken cancellationToken = default);
    Task<SmsSendResult> SendSmsAsync(SmsSendRequest request, CancellationToken cancellationToken = default);
    Task<bool> IsSmsSendReadyAsync(CancellationToken cancellationToken = default);
    Task InstallSmsAgentAsync(CancellationToken cancellationToken = default);
    Task<bool> IsSmsAgentInstalledAsync(CancellationToken cancellationToken = default);
}

public interface ISimCapable
{
    Task<SimInfo> GetSimInfoAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SimContact>> GetSimContactsAsync(CancellationToken cancellationToken = default);
    Task AddSimContactAsync(string name, string number, CancellationToken cancellationToken = default);
    Task DeleteSimContactAsync(string deviceId, CancellationToken cancellationToken = default);
}

public interface INetworkCapable
{
    Task<NetworkInfo> GetNetworkInfoAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ApnConfig>> GetApnsAsync(CancellationToken cancellationToken = default);
}

public interface IConnectionCapable
{
    Task SetMobileDataAsync(bool enabled, CancellationToken cancellationToken = default);
}

public interface IUsbDeviceService
{
    event EventHandler<UsbDevicesChangedEventArgs>? DevicesChanged;

    Task<IReadOnlyList<UsbDeviceInfo>> EnumerateAsync(CancellationToken cancellationToken = default);

    /// <summary>All serial ports installed on the machine, with PnP identity when available.</summary>
    Task<IReadOnlyList<SerialPortInfo>> GetSerialPortsAsync(CancellationToken cancellationToken = default);
}

public sealed record SerialPortInfo(
    string PortName,
    string? Description,
    string? InstanceId,
    string? HardwareId,
    ushort? VendorId,
    ushort? ProductId,
    bool IsInUse);

public sealed class UsbDevicesChangedEventArgs(IReadOnlyList<UsbDeviceInfo> devices, string reason) : EventArgs
{
    public IReadOnlyList<UsbDeviceInfo> Devices { get; } = devices;
    public string Reason { get; } = reason;
}

public interface IMessageStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<SmsSyncResult> ReplaceFolderAsync(
        string deviceSerial,
        SmsFolder folder,
        IReadOnlyList<SmsMessage> messages,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SmsMessage>> GetMessagesAsync(
        string deviceSerial,
        SmsFolder folder,
        int limit = 1000,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SmsFolderCount>> GetFolderCountsAsync(
        string deviceSerial,
        CancellationToken cancellationToken = default);
    Task MarkReadAsync(string deviceSerial, long deviceMessageId, bool isRead, CancellationToken cancellationToken = default);
    Task DeleteAsync(string deviceSerial, long deviceMessageId, CancellationToken cancellationToken = default);
    Task DeleteFromFolderAsync(string deviceSerial, long deviceMessageId, SmsFolder folder, CancellationToken cancellationToken = default);
    Task MoveToRecycleAsync(string deviceSerial, IReadOnlyList<SmsMessage> messages, CancellationToken cancellationToken = default);
    Task<int> EmptyRecycleAsync(string deviceSerial, CancellationToken cancellationToken = default);
    Task<SmsMessage> SaveDraftAsync(string deviceSerial, string to, string body, CancellationToken cancellationToken = default);
    Task DeleteDraftAsync(string deviceSerial, long deviceMessageId, CancellationToken cancellationToken = default);
    Task<long> AddSendHistoryAsync(
        string deviceSerial,
        string to,
        string body,
        int parts,
        string status,
        string? error,
        CancellationToken cancellationToken = default);
    Task UpdateSendHistoryAsync(
        long id,
        string status,
        string? error,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SmsSendHistoryEntry>> GetSendHistoryAsync(
        string deviceSerial,
        int limit = 200,
        CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
    Task<string> GetDatabasePathAsync();
}

public interface ISettingsStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string? value, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default);
}

public interface IUssdCommandStore
{
    Task<IReadOnlyList<UssdCommand>> GetUssdCommandsAsync(CancellationToken cancellationToken = default);
    Task<UssdCommand> SaveUssdCommandAsync(long id, string name, string code, CancellationToken cancellationToken = default);
    Task DeleteUssdCommandAsync(long id, CancellationToken cancellationToken = default);
}

public interface IDiagnosticsLog
{
    event EventHandler<DiagnosticEntry>? EntryAdded;

    IReadOnlyList<DiagnosticEntry> Entries { get; }
    DateTimeOffset? LastSuccessfulCommunication { get; }
    string LogFilePath { get; }

    void Trace(string category, string message);
    void Info(string category, string message);
    void Warning(string category, string message);
    void Error(string category, string message, Exception? exception = null);
    void MarkCommunicationSuccess(string category, string message);
    Task ExportAsync(string path, DiagnosticReportContext context, CancellationToken cancellationToken = default);
}
