using System.Text.RegularExpressions;
using System.Xml.Linq;
using UZConnect.Core.Abstractions;
using UZConnect.Core.Models;
using UZConnect.Windows.Adb;

namespace UZConnect.Windows.Ussd;

/// <summary>
/// Experimental USSD transport. The stock firmware has no USSD shell command; this
/// invokes the platform dialer's MMI handling and reads the modal result with
/// uiautomator. If the firmware rejects the MMI code, the result is reported as
/// unsupported instead of being treated as a successful balance check.
/// </summary>
public sealed partial class UssdService
{
    private readonly AdbClient _adb;
    private readonly IDiagnosticsLog _log;

    public UssdService(AdbClient adb, IDiagnosticsLog log)
    {
        _adb = adb;
        _log = log;
    }

    public async Task<UssdResult> SendAsync(string serial, string code, CancellationToken cancellationToken = default)
    {
        code = (code ?? string.Empty).Trim();
        if (code.Length == 0 || !CodeRegex().IsMatch(code))
        {
            return UssdResult.Failed("Enter a USSD code such as *123#.");
        }

        var encoded = code.Replace("#", "%23", StringComparison.Ordinal);
        var start = await _adb.ShellAsync(
            serial,
            $"am start -a android.intent.action.CALL -d 'tel:{encoded}'",
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        if (!start.Success)
        {
            var error = FirstLine(start.StandardError);
            return UssdResult.Failed(error.Length > 0 ? error : "The dialer could not start the USSD request.");
        }

        await Task.Delay(3000, cancellationToken).ConfigureAwait(false);

        try
        {
            var dump = await _adb.ShellAsync(
                serial,
                "uiautomator dump /sdcard/uzconnect-ussd.xml",
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
            var xml = dump.Success
                ? await _adb.ShellTextAsync(serial, "cat /sdcard/uzconnect-ussd.xml", TimeSpan.FromSeconds(10), cancellationToken)
                    .ConfigureAwait(false)
                : string.Empty;

            var output = UssdTextLocalizer.Localize(ExtractText(xml));
            _log.Info("USSD", $"USSD request completed; captured {output.Length} character(s).");

            await DismissAsync(serial, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(output))
            {
                return UssdResult.Failed("No USSD response was captured. The stock firmware may not support USSD.", true);
            }

            var unsupported = UssdTextLocalizer.IsFailure(output) ||
                              output.Contains("UIM", StringComparison.OrdinalIgnoreCase);

            if (UssdTextLocalizer.ContainsChinese(output))
            {
                // Never expose untranslated dialer/system text in the normal UI.
                output = unsupported
                    ? "Connection problem or invalid MMI code."
                    : "The modem returned a system message instead of a USSD result. Check the code and try again.";
                unsupported = true;
            }

            return unsupported
                ? new UssdResult(false, output, "The modem rejected the USSD code. Check the code and try again.", true)
                : new UssdResult(true, output, null, false);
        }
        catch (Exception ex)
        {
            _log.Error("USSD", "USSD result capture failed.", ex);
            return UssdResult.Failed(ex.Message);
        }
    }

    private async Task DismissAsync(string serial, CancellationToken cancellationToken)
    {
        // Close the modal result. These are ordinary key events; no call is placed.
        await _adb.ShellAsync(serial, "input keyevent 66", TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        await _adb.ShellAsync(serial, "input keyevent 4", TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
    }

    private static string ExtractText(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return string.Empty;
        }

        try
        {
            var document = XDocument.Parse(xml);
            var lines = document.Descendants()
                .Attributes("text")
                .Select(a => a.Value.Trim())
                .Where(v => v.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return string.Join(Environment.NewLine, lines);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FirstLine(string value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Split('\n')[0].Trim();

    [GeneratedRegex(@"^[*#0-9+]{2,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodeRegex();
}

