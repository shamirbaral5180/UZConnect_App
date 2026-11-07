using UZConnect.Core.Abstractions;
using UZConnect.Core.Models;
using UZConnect.Windows.Adb;
using UZConnect.Windows.Mifi;

// Explicit opt-in qualification harness. Prints no SSIDs, MACs, serials, credentials or raw exceptions.
var log = new QuietLog();
var adb = new AdbClient(log);
var devices = await adb.GetDevicesAsync();
var connected = devices.Where(d => d.State == "device").ToArray();
if (connected.Length != 1) { Console.WriteLine("Exactly one authorized modem is required."); return 2; }
var serial = connected[0].Serial;
using var mifi = new MifiWebClient(adb, log);
var wifi = new WifiManagementService(adb, mifi);
try
{
    var snapshot = await wifi.ReadAsync(serial);
    var web = await mifi.GetWifiStatusAsync(serial);
    Console.WriteLine($"Web status readable={web is not null}; SSID agrees={web?.Ssid == snapshot.Ssid}; limit agrees={web?.MaxClients == snapshot.MaximumClients}; web enabled={web?.Enabled}");
    Console.WriteLine($"AP state={snapshot.ApState}; process={snapshot.HostapdRunning}; config={snapshot.ConfigurationAvailable}; " +
        $"channel={snapshot.Channel}; security={snapshot.Security}; passwordConfigured={snapshot.PasswordConfigured}; " +
        $"clientsReadable={snapshot.ClientsAvailable}; clients={snapshot.Clients.Count}; ranges={snapshot.DhcpRanges.Count}; RNDIS bridged={snapshot.RndisBridged}");
    if (args.Length == 0) return 0;
    if (args.Length == 1 && args[0] == "--diagnose")
    {
        for (var i = 0; i < 4; i++)
        {
            var a = await wifi.ReadAsync(serial);
            var s = await wifi.ReadSavedAsync(serial);
            var w = await mifi.GetWifiStatusAsync(serial);
            Console.WriteLine($"obs{i}: apState={a.ApState} hostapd={a.HostapdRunning} config={a.ConfigurationAvailable} " +
                $"rndis={a.RndisBridged} active=saved:{a.Ssid == s.Ssid && a.MaximumClients == s.MaximumClients} " +
                $"active=web:{w is not null && a.Ssid == w.Ssid && a.MaximumClients == w.MaxClients} webEnabled={w?.Enabled}");
            await Task.Delay(TimeSpan.FromSeconds(11));
        }
        return 0;
    }
    if (args.Length is < 1 or > 2) return 2;
    // Fixed constants, never re-derived from the state under test.
    var temporary = new WifiBasicSettings("UZConnect-verification", 9);
    if (args[0] == "--persist-check")
    {
        var active = await wifi.ReadAsync(serial);
        var saved = await wifi.ReadSavedAsync(serial);
        var persisted = active.Ssid == temporary.Ssid && active.MaximumClients == temporary.MaximumClients &&
            saved == temporary;
        Console.WriteLine($"Temporary settings survived restart={persisted}; apState={active.ApState}; " +
            $"hostapd={active.HostapdRunning}; rndis={active.RndisBridged}; clients={active.Clients.Count}");
        return persisted ? 0 : 1;
    }
    if (args[0] == "--restore-from" && args.Length == 2)
    {
        var before = await wifi.ReadBackupAsync(serial, args[1]);
        var restored = await wifi.QualifyBasicChangeAsync(serial, before);
        Console.WriteLine($"Restore from backup: {restored.Outcome}; newBackup={restored.BackupId}");
        return restored.Outcome == WifiChangeOutcome.Verified ? 0 : 1;
    }
    if (args[0] == "--rollback-test")
    {
        var rollback = await wifi.QualifyRollbackAsync(serial, temporary);
        var active = await wifi.ReadAsync(serial);
        var saved = await wifi.ReadSavedAsync(serial);
        var backToStart = active.Ssid == snapshot.Ssid && active.MaximumClients == snapshot.MaximumClients &&
            saved.Ssid == snapshot.Ssid && saved.MaximumClients == snapshot.MaximumClients;
        Console.WriteLine($"Injected failure outcome={rollback.Outcome}; original restored on device={backToStart}; " +
            $"apState={active.ApState}; rndis={active.RndisBridged}");
        return rollback.Outcome == WifiChangeOutcome.RolledBack && backToStart ? 0 : 1;
    }
    if (args[0] == "--apply-temp")
    {
        var applied = await wifi.QualifyBasicChangeAsync(serial, temporary);
        Console.WriteLine($"Apply temporary: {applied.Outcome}; backup={applied.BackupId}");
        return applied.Outcome == WifiChangeOutcome.Verified ? 0 : 1;
    }
    if (args[0] != "--approved-basic-test") return 2;
    if (snapshot.Ssid is null || snapshot.MaximumClients is null) return 2;
    var original = new WifiBasicSettings(snapshot.Ssid, snapshot.MaximumClients.Value);
    var result = await wifi.QualifyBasicChangeAsync(serial, temporary);
    Console.WriteLine($"Temporary change: {result.Outcome}; backup={result.BackupId}");
    if (result.Outcome == WifiChangeOutcome.Verified)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var restored = await wifi.QualifyBasicChangeAsync(serial, original);
            Console.WriteLine($"Restore original settings: {restored.Outcome}; backup={restored.BackupId}");
            if (restored.Outcome == WifiChangeOutcome.Verified) return 0;
            if (restored.Outcome != WifiChangeOutcome.RolledBack) break;
        }
        return 1;
    }
    return result.Outcome == WifiChangeOutcome.RolledBack ? 1 : 2;
}
catch { Console.WriteLine("Qualification did not complete. No exception contents were logged."); return 1; }

sealed class QuietLog : IDiagnosticsLog
{
    public event EventHandler<DiagnosticEntry>? EntryAdded { add { } remove { } }
    public IReadOnlyList<DiagnosticEntry> Entries => Array.Empty<DiagnosticEntry>();
    public DateTimeOffset? LastSuccessfulCommunication => null;
    public string LogFilePath => "";
    public void Trace(string category, string message) { }
    public void Info(string category, string message) { }
    public void Warning(string category, string message) { }
    public void Error(string category, string message, Exception? exception = null) { }
    public void MarkCommunicationSuccess(string category, string message) { }
    public Task ExportAsync(string path, DiagnosticReportContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
