using System.Text.Json;
using UZConnect.Core.Models;
using Xunit;

namespace UZConnect.Tests;

public sealed class WifiManagementTests
{
    [Fact]
    public void ProcessAloneDoesNotConfirmOn()
    {
        var snapshot = WifiSnapshot.Parse("""{"version":1,"ok":true,"hostapd":true,"apState":11}""");
        Assert.Equal("Not confirmed", snapshot.Status);
        Assert.False(snapshot.ConfigurationAvailable);
        Assert.False(snapshot.ClientsAvailable);
        Assert.Null(snapshot.PasswordConfigured);
    }

    [Fact]
    public void ParsesAssociatedClientsWithoutCreatingClientsFromLeases()
    {
        var snapshot = WifiSnapshot.Parse("""
            {"version":1,"ok":true,"apState":13,"hostapd":true,"clientsAvailable":true,
             "channel":11,"hidden":true,"passwordConfigured":true,
             "clients":[{"name":"Test\nDevice","ip":"","mac":"02:00:00:00:00:01"},
                        {"name":"Duplicate","ip":"192.168.100.101","mac":"02:00:00:00:00:01"},
                        {"name":"Invalid","ip":"bad","mac":"invalid"}],
             "leases":[{"mac":"02:00:00:00:00:02"}],"pwd":"TEST_SECRET_DO_NOT_COPY"}
            """);
        Assert.Single(snapshot.Clients);
        Assert.Equal("TestDevice", snapshot.Clients[0].Name);
        Assert.Equal("Not available", snapshot.Clients[0].IpAddress);
        Assert.Equal(11, snapshot.Channel);
        Assert.True(snapshot.HiddenSsid);
        Assert.DoesNotContain("TEST_SECRET_DO_NOT_COPY", JsonSerializer.Serialize(snapshot));
    }

    [Theory]
    [InlineData("{\"version\":1,\"ok\":false}")]
    [InlineData("{\"version\":2,\"ok\":true}")]
    public void RejectsFailedOrUnknownHelperProtocol(string json)
        => Assert.Throws<InvalidDataException>(() => WifiSnapshot.Parse(json));

    [Fact]
    public void ValidatesSsidBytesRatherThanCharacters()
    {
        Assert.Null(new WifiBasicSettings(new string('é', 16), 10).ValidationError);
        Assert.NotNull(new WifiBasicSettings(new string('é', 17), 10).ValidationError);
        Assert.NotNull(new WifiBasicSettings("bad\nname", 10).ValidationError);
        Assert.NotNull(new WifiBasicSettings("Valid", 11).ValidationError);
        Assert.NotNull(new WifiBasicSettings(" ", 5).ValidationError);
    }

    [Fact]
    public async Task BackupFailureNeverSendsSettings()
    {
        var fake = new Device { FailBackup = true };
        var result = await new WifiBasicChange(fake).ApplyAsync(new("Temporary", 5));
        Assert.Equal(WifiChangeOutcome.Rejected, result.Outcome);
        Assert.Empty(fake.Saves);
    }

    [Fact]
    public async Task InconsistentInitialStateNeverBacksUpOrChanges()
    {
        var fake = new Device { FailInitialVerification = true };
        var result = await new WifiBasicChange(fake).ApplyAsync(new("Temporary", 5));
        Assert.Equal(WifiChangeOutcome.Rejected, result.Outcome);
        Assert.False(fake.BackedUp);
        Assert.Empty(fake.Saves);
    }

    [Fact]
    public async Task SuccessfulAcknowledgementWithWrongStateRollsBack()
    {
        var fake = new Device { FailNewVerification = true };
        var result = await new WifiBasicChange(fake).ApplyAsync(new("Temporary", 5));
        Assert.Equal(WifiChangeOutcome.RolledBack, result.Outcome);
        Assert.Equal(fake.Original, fake.Current);
        Assert.Equal(2, fake.Restarts);
        Assert.True(fake.BackedUp);
    }

    [Fact]
    public async Task LostSaveResponseStillRollsBack()
    {
        var fake = new Device { ThrowAfterSave = true };
        var result = await new WifiBasicChange(fake).ApplyAsync(new("Temporary", 5));
        Assert.Equal(WifiChangeOutcome.RolledBack, result.Outcome);
        Assert.Equal(fake.Original, fake.Current);
        Assert.DoesNotContain("SENSITIVE", result.Message);
    }

    [Fact]
    public async Task CancelledOperationUsesIndependentRecoveryToken()
    {
        using var cancellation = new CancellationTokenSource();
        var fake = new Device { CancelAfterSave = cancellation };
        var result = await new WifiBasicChange(fake).ApplyAsync(new("Temporary", 5), cancellation.Token);
        Assert.Equal(WifiChangeOutcome.RolledBack, result.Outcome);
        Assert.Equal(fake.Original, fake.Current);
    }

    [Fact]
    public async Task FailedRollbackNeverClaimsRecovery()
    {
        var fake = new Device { FailNewVerification = true, FailRecovery = true };
        var result = await new WifiBasicChange(fake).ApplyAsync(new("Temporary", 5));
        Assert.Equal(WifiChangeOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal("backup", result.BackupId);
    }

    [Fact]
    public async Task SuccessRequiresVerificationAfterRestart()
    {
        var fake = new Device();
        var result = await new WifiBasicChange(fake).ApplyAsync(new("Temporary", 5));
        Assert.Equal(WifiChangeOutcome.Verified, result.Outcome);
        Assert.Equal(2, fake.Verifications);
        Assert.Equal(1, fake.Restarts);
    }

    private sealed class Device : IWifiBasicChangeDevice
    {
        public WifiBasicSettings Original { get; } = new("Original", 10);
        public WifiBasicSettings Current { get; private set; } = new("Original", 10);
        public List<WifiBasicSettings> Saves { get; } = new();
        public bool FailBackup, FailNewVerification, FailInitialVerification, ThrowAfterSave, FailRecovery, BackedUp;
        public CancellationTokenSource? CancelAfterSave;
        public int Restarts, Verifications;
        public Task<WifiBasicSettings> ReadSavedAsync(CancellationToken cancellationToken) => Task.FromResult(Original);
        public Task<string> BackupAsync(CancellationToken cancellationToken)
        {
            if (FailBackup) throw new IOException("SENSITIVE");
            BackedUp = true;
            return Task.FromResult("backup");
        }
        public Task SaveAsync(WifiBasicSettings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(BackedUp);
            Saves.Add(settings);
            if (Saves.Count > 1 && FailRecovery) throw new IOException("SENSITIVE");
            Current = settings;
            if (Saves.Count == 1)
            {
                CancelAfterSave?.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                if (ThrowAfterSave) throw new IOException("SENSITIVE");
            }
            return Task.CompletedTask;
        }
        public Task RestartAccessPointAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Restarts++;
            return Task.CompletedTask;
        }
        public Task<bool> VerifyAsync(WifiBasicSettings settings, CancellationToken cancellationToken)
        {
            Verifications++;
            return Task.FromResult(!(Verifications == 1 && FailInitialVerification) &&
                !(settings != Original && FailNewVerification) && Current == settings);
        }
    }
}
