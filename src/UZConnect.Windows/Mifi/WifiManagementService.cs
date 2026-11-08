using System.Text.Json;
using UZConnect.Core.Models;
using UZConnect.Windows.Adb;

namespace UZConnect.Windows.Mifi;

/// <summary>Uses the root helper's allowlisted output. The current passphrase is returned only for local display.</summary>
public sealed class WifiManagementService(AdbClient adb, MifiWebClient mifi)
{
    private const string RemoteJar = "/data/local/tmp/uzconnect-wifi.jar";
    private readonly MifiWebClient _mifi = mifi;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<WifiSnapshot> ReadAsync(string serial, CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false))
            throw new TimeoutException("A previous Wi-Fi operation is still running.");
        try
        {
            await EnsureHelperAsync(serial, cancellationToken).ConfigureAwait(false);
            return await ReadInternalAsync(serial, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<WifiSnapshot> ReadInternalAsync(string serial, CancellationToken cancellationToken)
        => WifiSnapshot.Parse(await RunAsync(serial, "read", cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Turns the access point off or on using the verified vendor sequence. Off cancels the
    /// 20-second vendor refresh so it cannot rebind; on re-arms it and restarts the AP. Both
    /// paths require the state and the hostapd process to agree before reporting success.
    /// </summary>
    public async Task<bool> SetPowerAsync(string serial, bool on, CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false))
            throw new TimeoutException("A previous Wi-Fi operation is still running.");
        try
        {
            await EnsureHelperAsync(serial, cancellationToken).ConfigureAwait(false);
            if (on)
            {
                await ControlAsync(serial, "alarm-on", cancellationToken).ConfigureAwait(false);
                if (!await _mifi.RestartWifiAsync(serial, cancellationToken).ConfigureAwait(false))
                    await ControlAsync(serial, "ap-cycle", cancellationToken).ConfigureAwait(false);

                for (var attempt = 0; attempt < 3; attempt++)
                {
                    for (var check = 0; check < 4; check++)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
                        if (await IsOnAsync(serial, cancellationToken).ConfigureAwait(false))
                            return true;
                    }

                    await _mifi.RestartWifiAsync(serial, cancellationToken).ConfigureAwait(false);
                }

                return false;
            }

            await ControlAsync(serial, "alarm-off", cancellationToken).ConfigureAwait(false);
            await ControlAsync(serial, "ap-off", cancellationToken).ConfigureAwait(false);
            for (var check = 0; check < 5; check++)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                var snapshot = await ReadInternalAsync(serial, cancellationToken).ConfigureAwait(false);
                if (snapshot.ApState is 10 or 11 && !snapshot.HostapdRunning)
                    return true;
            }

            // Restore the access point if it would not stay off.
            await ControlAsync(serial, "alarm-on", cancellationToken).ConfigureAwait(false);
            await _mifi.RestartWifiAsync(serial, cancellationToken).ConfigureAwait(false);
            return false;
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> IsOnAsync(string serial, CancellationToken cancellationToken)
    {
        var snapshot = await ReadInternalAsync(serial, cancellationToken).ConfigureAwait(false);
        var status = await _mifi.GetWifiStatusAsync(serial, cancellationToken).ConfigureAwait(false);
        return snapshot.ApState == 13 && snapshot.HostapdRunning && snapshot.Channel is > 0 &&
               status is { Enabled: true };
    }

    private async Task ControlAsync(string serial, string action, CancellationToken cancellationToken)
    {
        var result = await adb.ShellAsync(serial,
            $"CLASSPATH={RemoteJar} app_process /system/bin WifiControl {action}",
            TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        if (!result.Success) throw new IOException("The Wi-Fi control helper did not respond.");
    }

    // A hardware-qualification caller can use the same transaction as the UI, without weakening its release gate.
    public async Task<WifiChangeResult> QualifyBasicChangeAsync(string serial, WifiBasicSettings settings,
        CancellationToken cancellationToken = default)
        => await QualifyAsync(serial, settings, injectVerificationFailure: false, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Hardware fault injection: the first check of the new settings is reported as failed so the
    /// real save, restart and restore path is exercised. Qualification harness only.
    /// </summary>
    public async Task<WifiChangeResult> QualifyRollbackAsync(string serial, WifiBasicSettings settings,
        CancellationToken cancellationToken = default)
        => await QualifyAsync(serial, settings, injectVerificationFailure: true, cancellationToken).ConfigureAwait(false);

    private async Task<WifiChangeResult> QualifyAsync(string serial, WifiBasicSettings settings,
        bool injectVerificationFailure, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false))
            throw new TimeoutException("A previous Wi-Fi operation is still running.");
        try
        {
            await EnsureHelperAsync(serial, cancellationToken).ConfigureAwait(false);
            return await new WifiBasicChange(new Device(this, serial, injectVerificationFailure))
                .ApplyAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public Task<WifiChangeResult> ApplyBasicAsync(string serial, WifiBasicSettings settings,
        CancellationToken cancellationToken = default) => WifiCapabilities.BasicChangesVerified
        ? QualifyBasicChangeAsync(serial, settings, cancellationToken)
        : Task.FromResult(new WifiChangeResult(WifiChangeOutcome.Rejected,
            "Saving is unavailable until modem verification and recovery checks are complete."));

    // Qualification-only: returns saved values so a harness can diagnose without printing secrets.
    public async Task<WifiBasicSettings> ReadSavedAsync(string serial, CancellationToken cancellationToken = default)
    {
        using var doc = JsonDocument.Parse(await RunAsync(serial, "saved", cancellationToken).ConfigureAwait(false));
        return new(doc.RootElement.GetProperty("ssid").GetString() ?? "",
            doc.RootElement.GetProperty("maximumClients").GetInt32());
    }

    public async Task<WifiBasicSettings> ReadBackupAsync(string serial, string backupId,
        CancellationToken cancellationToken = default)
    {
        if (backupId.Any(c => !Uri.IsHexDigit(c)) || backupId.Length != 32) throw new InvalidOperationException();
        using var doc = JsonDocument.Parse(await RunAsync(serial, "original " + backupId, cancellationToken).ConfigureAwait(false));
        return new(doc.RootElement.GetProperty("ssid").GetString() ?? "",
            doc.RootElement.GetProperty("maximumClients").GetInt32());
    }

    private async Task EnsureHelperAsync(string serial, CancellationToken cancellationToken)
    {
        var path = HelperFiles.Resolve("uzconnect-wifi.jar");
        // Re-push after every entry: reconnection/reboot and helper version changes must not reuse an unknown binary.
        var result = await adb.RunAsync(["-s", serial, "push", path, RemoteJar], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        if (!result.Success) throw new IOException("Wi-Fi information could not be read.");
    }

    private async Task<string> RunAsync(string serial, string arguments, CancellationToken cancellationToken)
    {
        var result = await adb.ShellAsync(serial,
            $"CLASSPATH={RemoteJar} app_process /system/bin WifiProbe {arguments}",
            TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.Success) throw new IOException("Wi-Fi information could not be read.");
        var json = result.StandardOutput.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith('{'));
        if (json is null) throw new IOException("Wi-Fi information could not be read.");
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new IOException("Wi-Fi information could not be read.");
        return json;
    }

    private sealed class Device(WifiManagementService owner, string serial, bool injectVerificationFailure = false)
        : IWifiBasicChangeDevice
    {
        private int _verifications;
        public async Task<WifiBasicSettings> ReadSavedAsync(CancellationToken cancellationToken)
        {
            using var doc = JsonDocument.Parse(await owner.RunAsync(serial, "saved", cancellationToken).ConfigureAwait(false));
            return new(doc.RootElement.GetProperty("ssid").GetString() ?? "",
                doc.RootElement.GetProperty("maximumClients").GetInt32());
        }

        public async Task<string> BackupAsync(CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid().ToString("N");
            using var doc = JsonDocument.Parse(await owner.RunAsync(serial, "backup " + id, cancellationToken).ConfigureAwait(false));
            if (doc.RootElement.GetProperty("backupId").GetString() != id) throw new IOException();
            return id;
        }

        public async Task SaveAsync(WifiBasicSettings settings, CancellationToken cancellationToken)
        {
            if (settings.ValidationError is not null) throw new IOException();
            await owner._mifi.SetWifiBasicAsync(serial, settings, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // A lost HTTP response is not proof of failure, just as a success flag is not proof of application.
            if (await ReadSavedAsync(cancellationToken).ConfigureAwait(false) != settings)
                throw new IOException("Saved settings did not match.");
        }

        public async Task RestartAccessPointAsync(CancellationToken cancellationToken)
        {
            await owner._mifi.RestartWifiAsync(serial, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // The restart may outlive the HTTP request. VerifyAsync decides the result from actual state.
        }

        public async Task<bool> VerifyAsync(WifiBasicSettings settings, CancellationToken cancellationToken)
        {
            _verifications++;
            if (injectVerificationFailure && _verifications == 2)
            {
                // Simulates a change that the modem acknowledged but did not apply.
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                return false;
            }

            // Require three stable observations across more than one watchdog interval.
            var stable = 0;
            for (var i = 0; i < 8; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var active = WifiSnapshot.Parse(await owner.RunAsync(serial, "read", cancellationToken).ConfigureAwait(false));
                var saved = await ReadSavedAsync(cancellationToken).ConfigureAwait(false);
                var status = await owner._mifi.GetWifiStatusAsync(serial, cancellationToken).ConfigureAwait(false);
                if (active.ApState == 13 && active.HostapdRunning && active.ConfigurationAvailable && active.RndisBridged &&
                    active.Ssid == settings.Ssid && active.MaximumClients == settings.MaximumClients && saved == settings &&
                    status is { Enabled: true } && status.Ssid == settings.Ssid && status.MaxClients == settings.MaximumClients)
                {
                    if (++stable == 3) return true;
                }
                else stable = 0;
                await Task.Delay(TimeSpan.FromSeconds(11), cancellationToken).ConfigureAwait(false);
            }
            return false;
        }
    }
}
