namespace UZConnect.Core.Models;

/// <summary>Candidate transaction for hardware qualification, not enabled in the released UI.</summary>
public interface IWifiBasicChangeDevice
{
    Task<WifiBasicSettings> ReadSavedAsync(CancellationToken cancellationToken);
    Task<string> BackupAsync(CancellationToken cancellationToken);
    Task SaveAsync(WifiBasicSettings settings, CancellationToken cancellationToken);
    Task RestartAccessPointAsync(CancellationToken cancellationToken);
    // Must compare active AND saved settings and control connectivity; never just the API flag.
    Task<bool> VerifyAsync(WifiBasicSettings settings, CancellationToken cancellationToken);
}

public enum WifiChangeOutcome { Verified, Rejected, RolledBack, RecoveryRequired }
public sealed record WifiChangeResult(WifiChangeOutcome Outcome, string Message, string? BackupId = null);

public sealed class WifiBasicChange(IWifiBasicChangeDevice device)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<WifiChangeResult> ApplyAsync(WifiBasicSettings requested, CancellationToken cancellationToken = default)
    {
        if (requested.ValidationError is { } error) return new(WifiChangeOutcome.Rejected, error);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        WifiBasicSettings? original = null;
        string? backup = null;
        var attempted = false;
        try
        {
            original = await device.ReadSavedAsync(cancellationToken).ConfigureAwait(false);
            if (original.ValidationError is not null ||
                !await device.VerifyAsync(original, cancellationToken).ConfigureAwait(false))
                return new(WifiChangeOutcome.Rejected, "Current settings and connectivity could not be confirmed.");
            backup = await device.BackupAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(backup)) throw new InvalidOperationException();
            cancellationToken.ThrowIfCancellationRequested();
            attempted = true; // A timed-out save may still have changed the device.
            await device.SaveAsync(requested, cancellationToken).ConfigureAwait(false);
            await device.RestartAccessPointAsync(cancellationToken).ConfigureAwait(false);
            if (await device.VerifyAsync(requested, cancellationToken).ConfigureAwait(false))
                return new(WifiChangeOutcome.Verified, "Active and saved settings confirmed. Reconnection and reboot checks remain separate.", backup);
            throw new InvalidOperationException();
        }
        catch
        {
            // Never surface transport exceptions: these can contain request/response data.
            if (!attempted || original is null)
                return new(WifiChangeOutcome.Rejected, "No changes were sent. The initial checks or backup did not complete.", backup);
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try
            {
                await device.SaveAsync(original, recovery.Token).ConfigureAwait(false);
                await device.RestartAccessPointAsync(recovery.Token).ConfigureAwait(false);
                if (await device.VerifyAsync(original, recovery.Token).ConfigureAwait(false))
                    return new(WifiChangeOutcome.RolledBack, "The change was not confirmed. Original settings were restored and checked.", backup);
            }
            catch { }
            return new(WifiChangeOutcome.RecoveryRequired, "Recovery could not be confirmed. Keep the modem connected and do not retry changes.", backup);
        }
        finally { _gate.Release(); }
    }
}
