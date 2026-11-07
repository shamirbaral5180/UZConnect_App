using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using UZConnect.Core.Abstractions;
using UZConnect.Core.Models;

namespace UZConnect.Windows.Serial;

/// <summary>
/// Sends USSD/MMI requests through the modem's AT command port using AT+CUSD.
/// This is the path used by the stock connection manager and works even though
/// the Android dialer's MMI handler rejects every code on this firmware.
///
/// A response with status 1 leaves the USSD menu session open. In that state a
/// digits-only code (for example "1") is sent as the menu reply; a new code
/// beginning with * or # starts a fresh session. The caller restores the
/// network mode only when the session has finished.
/// </summary>
public sealed class SerialUssdService
{
    private readonly IDiagnosticsLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _sessionActive;

    public SerialUssdService(IDiagnosticsLog log)
    {
        _log = log;
    }

    public bool SessionActive => _sessionActive;

    public async Task<UssdResult> SendAsync(ModemDevice device, string code, CancellationToken cancellationToken = default)
    {
        code = (code ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            return UssdResult.Failed("Enter a USSD code such as *901#.");
        }

        var isReply = code.All(char.IsDigit);
        if (isReply && !_sessionActive)
        {
            return UssdResult.Failed("There is no active USSD menu. Send the USSD code again first.");
        }

        var portName = ModemAtPortLocator.Find(device.VendorId, device.ProductId);
        if (string.IsNullOrWhiteSpace(portName))
        {
            return UssdResult.Failed("The modem command port could not be found.", true);
        }

        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false))
        {
            return UssdResult.Failed("Another USSD request is still running.");
        }

        try
        {
            var result = await Task.Run(
                () => SendCore(portName, code, isReply, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                _log.Info("USSD", $"USSD {(isReply ? "reply" : "request")} completed over the modem command port ({result.Output.Length} characters).");
            }
            else
            {
                _log.Warning("USSD", $"USSD request failed: {result.Error}");
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("USSD", "USSD request failed on the modem command port.", ex);
            return UssdResult.Failed($"The modem command port could not be used: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CancelAsync(ModemDevice device, CancellationToken cancellationToken = default)
    {
        var portName = ModemAtPortLocator.Find(device.VendorId, device.ProductId);
        if (string.IsNullOrWhiteSpace(portName))
        {
            _sessionActive = false;
            return;
        }

        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false))
        {
            _sessionActive = false;
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                SerialPort? port = null;
                try
                {
                    port = Open(portName);
                    port.Write("AT+CUSD=2\r");
                    ReadFor(port, 500, cancellationToken, stopOnCusd: false);
                }
                catch
                {
                    // The session is being abandoned; the network will time it out.
                }
                finally
                {
                    Close(port);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sessionActive = false;
            _gate.Release();
        }
    }

    private UssdResult SendCore(string portName, string code, bool isReply, CancellationToken cancellationToken)
    {
        SerialPort? port = null;
        try
        {
            port = Open(portName);

            port.Write("AT\r");
            var handshake = ReadFor(port, 1500, cancellationToken, stopOnCusd: false);
            if (!handshake.Contains("OK", StringComparison.OrdinalIgnoreCase))
            {
                _sessionActive = false;
                return UssdResult.Failed("The modem command port did not answer.", true);
            }

            var response = SendCusd(port, code, isReply, cancellationToken);
            var parsed = UssdCusdParser.Parse(response);
            if (parsed is null || !parsed.IsSuccess || HasModemError(response))
            {
                // The modem occasionally reports a transient CME error right
                // after a network-mode change or a previous session. Reset and
                // retry once before reporting a failure.
                if (!isReply)
                {
                    port.DiscardInBuffer();
                    port.Write("AT+CUSD=2\r");
                    ReadFor(port, 800, cancellationToken, stopOnCusd: false);
                }

                Thread.Sleep(1200);
                response = SendCusd(port, code, isReply, cancellationToken);
                parsed = UssdCusdParser.Parse(response);
            }

            if (parsed is null)
            {
                _sessionActive = false;
                var errorLine = FirstError(response);
                if (!string.IsNullOrWhiteSpace(errorLine))
                {
                    return UssdResult.Failed($"The modem rejected the USSD request: {errorLine}");
                }

                return UssdResult.Failed(
                    "No USSD response was received. Check the signal and try again.",
                    true);
            }

            if (!parsed.IsSuccess)
            {
                _sessionActive = false;
                return new UssdResult(
                    false,
                    parsed.Text,
                    UssdCusdParser.DescribeFailure(parsed.Status),
                    parsed.Status == 4);
            }

            _sessionActive = parsed.NeedsReply;
            return new UssdResult(true, parsed.Text, null, false, parsed.NeedsReply);
        }
        finally
        {
            Close(port);
        }
    }

    private static string SendCusd(
        SerialPort port,
        string code,
        bool isReply,
        CancellationToken cancellationToken)
    {
        if (!isReply)
        {
            // Start a fresh session. A reply keeps the current menu alive.
            port.DiscardInBuffer();
            port.Write("AT+CUSD=2\r");
            ReadFor(port, 800, cancellationToken, stopOnCusd: false);
        }

        port.DiscardInBuffer();
        port.Write($"AT+CUSD=1,\"{code}\",15\r");
        return ReadFor(port, 12000, cancellationToken, stopOnCusd: true);
    }

    private static bool HasModemError(string response)
        => response.Contains("+CME ERROR", StringComparison.OrdinalIgnoreCase) ||
           response.Contains("+CMS ERROR", StringComparison.OrdinalIgnoreCase);

    private static SerialPort Open(string portName)
    {
        var port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 300,
            WriteTimeout = 2000,
            DtrEnable = true,
            RtsEnable = true,
            NewLine = "\r",
            Encoding = Encoding.ASCII
        };
        port.Open();
        Thread.Sleep(250);
        port.DiscardInBuffer();
        port.DiscardOutBuffer();
        return port;
    }

    private static void Close(SerialPort? port)
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

    private static string ReadFor(
        SerialPort port,
        int milliseconds,
        CancellationToken cancellationToken,
        bool stopOnCusd)
    {
        var builder = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < milliseconds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var chunk = port.ReadExisting();
                if (!string.IsNullOrEmpty(chunk))
                {
                    builder.Append(chunk);
                    if (stopOnCusd && UssdCusdParser.Parse(builder.ToString()) is not null)
                    {
                        break;
                    }

                    if (builder.ToString().Contains("+CME ERROR", StringComparison.OrdinalIgnoreCase) ||
                        builder.ToString().Contains("+CMS ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
            }
            catch (TimeoutException)
            {
                // No data in this slice; keep waiting.
            }

            Thread.Sleep(120);
        }

        return builder.ToString();
    }

    private static string? FirstError(string response)
    {
        foreach (var line in response.Replace("\r", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("+CME ERROR", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("+CMS ERROR", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }
        }

        return null;
    }
}
