using System.Diagnostics;
using System.Globalization;
using System.Text;
using UZConnect.Core.Abstractions;
using UZConnect.Core.Models;
using UZConnect.Core.Parsing;

namespace UZConnect.Windows.Adb;

public sealed record AdbCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    TimeSpan Duration)
{
    public bool Success => !TimedOut && ExitCode == 0;
    public string CombinedOutput => string.IsNullOrWhiteSpace(StandardError)
        ? StandardOutput
        : StandardOutput + Environment.NewLine + StandardError;

    public static AdbCommandResult NotAvailable(string message) =>
        new(-1, string.Empty, message, false, TimeSpan.Zero);
}

/// <summary>
/// Thin, cancellable wrapper around the Android Debug Bridge. ADB is only a transport;
/// every command is explicit and observable in diagnostics.
/// </summary>
public sealed class AdbClient
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private readonly IDiagnosticsLog _log;

    public AdbClient(IDiagnosticsLog log)
    {
        _log = log;
        AdbPath = LocateAdb();
    }

    public string? AdbPath { get; private set; }
    public DateTimeOffset? LastSuccessUtc { get; private set; }
    public bool IsAvailable => !string.IsNullOrWhiteSpace(AdbPath) && File.Exists(AdbPath);

    public void Configure(string? adbPath)
    {
        if (!string.IsNullOrWhiteSpace(adbPath) && File.Exists(adbPath))
        {
            AdbPath = adbPath;
            _log.Info("ADB", $"ADB executable configured: {adbPath}");
        }
        else if (!string.IsNullOrWhiteSpace(adbPath))
        {
            _log.Warning("ADB", "Configured ADB path does not exist; keeping automatic detection.");
        }
    }

    public void RefreshLocation()
    {
        AdbPath = LocateAdb();
        _log.Info("ADB", IsAvailable
            ? $"ADB executable found: {AdbPath}"
            : "ADB executable was not found. Set the path in Settings or install Android platform-tools.");
    }

    private static string? LocateAdb()
    {
        var candidates = new List<string?>();

        var explicitPath = Environment.GetEnvironmentVariable("ADB_EXE");
        candidates.Add(explicitPath);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "adb.exe"));

        var sdkRoot = Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT")
                      ?? Environment.GetEnvironmentVariable("ANDROID_HOME");
        if (!string.IsNullOrWhiteSpace(sdkRoot))
        {
            candidates.Add(Path.Combine(sdkRoot, "platform-tools", "adb.exe"));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(localAppData, "Android", "Sdk", "platform-tools", "adb.exe"));

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        candidates.Add(Path.Combine(programFilesX86, "Android", "android-sdk", "platform-tools", "adb.exe"));

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                candidates.Add(Path.Combine(directory.Trim('"'), "adb.exe"));
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return candidates
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!)
            .FirstOrDefault(File.Exists);
    }

    public async Task<AdbCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return AdbCommandResult.NotAvailable("ADB executable not found.");
        }

        var stopwatch = Stopwatch.StartNew();
        var effectiveTimeout = timeout ?? DefaultTimeout;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = AdbPath!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _log.Error("ADB", "Failed to start adb.exe.", ex);
            return AdbCommandResult.NotAvailable(ex.Message);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        var waitTask = process.WaitForExitAsync(cancellationToken);
        var completed = await Task.WhenAny(waitTask, Task.Delay(effectiveTimeout, cancellationToken)).ConfigureAwait(false);
        var timedOut = completed != waitTask;
        if (timedOut)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Process may already be gone.
            }
        }

        string stdout = string.Empty;
        string stderr = string.Empty;
        try
        {
            stdout = await stdoutTask.ConfigureAwait(false);
            stderr = await stderrTask.ConfigureAwait(false);
            if (!timedOut)
            {
                await waitTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Propagate cancellation after recording the attempt.
        }

        stopwatch.Stop();
        var exitCode = timedOut ? -2 : process.ExitCode;
        if (!timedOut && exitCode == 0)
        {
            LastSuccessUtc = DateTimeOffset.Now;
        }
        else if (!timedOut)
        {
            _log.Warning("ADB", $"adb exited with code {exitCode}: {FirstLine(stderr)}");
        }

        return new AdbCommandResult(exitCode, stdout, stderr, timedOut, stopwatch.Elapsed);
    }

    public Task<AdbCommandResult> StartServerAsync(CancellationToken cancellationToken = default)
        => RunAsync(new[] { "start-server" }, TimeSpan.FromSeconds(20), cancellationToken);

    public Task<AdbCommandResult> KillServerAsync(CancellationToken cancellationToken = default)
        => RunAsync(new[] { "kill-server" }, TimeSpan.FromSeconds(10), cancellationToken);

    public async Task<IReadOnlyList<AdbDeviceEntry>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(new[] { "devices", "-l" }, TimeSpan.FromSeconds(15), cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            return Array.Empty<AdbDeviceEntry>();
        }

        return AdbDevicesParser.Parse(result.StandardOutput);
    }

    public async Task<string> GetStateAsync(string serial, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(new[] { "-s", serial, "get-state" }, TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);
        return result.StandardOutput.Trim();
    }

    public async Task<AdbCommandResult> ShellAsync(
        string serial,
        string command,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            new[] { "-s", serial, "shell", command },
            timeout ?? DefaultTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            _log.Trace("ADB", $"shell [{Redact(command)}] ok in {result.Duration.TotalMilliseconds:0} ms");
        }

        return result;
    }

    public async Task<string> ShellTextAsync(
        string serial,
        string command,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var result = await ShellAsync(serial, command, timeout, cancellationToken).ConfigureAwait(false);
        return result.Success ? result.StandardOutput : string.Empty;
    }

    public async Task<string> ExecOutAsync(
        string serial,
        string command,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            new[] { "-s", serial, "exec-out", command },
            timeout ?? DefaultTimeout,
            cancellationToken).ConfigureAwait(false);
        return result.Success ? result.StandardOutput : string.Empty;
    }

    public async Task<bool> InstallApkAsync(
        string serial,
        string apkPath,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            new[] { "-s", serial, "install", "-r", apkPath },
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);
        var success = result.Success && result.StandardOutput.Contains("Success", StringComparison.OrdinalIgnoreCase);
        if (success)
        {
            _log.Info("ADB", "SMS helper agent installed successfully.");
        }
        else
        {
            _log.Warning("ADB", $"SMS helper agent install failed: {FirstLine(result.CombinedOutput)}");
        }

        return success;
    }

    public static string Quote(string value)
    {
        // For remote shell commands. Values are single-quoted and embedded quotes escaped.
        return "'" + value.Replace("'", "'\\''") + "'";
    }

    private static string Redact(string command)
    {
        if (command.Contains("sms", StringComparison.OrdinalIgnoreCase))
        {
            return "content provider command";
        }

        if (command.Length > 120)
        {
            return command[..117] + "...";
        }

        return command;
    }

    private static string FirstLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var line = value.Split('\n')[0].Trim();
        return line.Length <= 160 ? line : line[..160];
    }
}
