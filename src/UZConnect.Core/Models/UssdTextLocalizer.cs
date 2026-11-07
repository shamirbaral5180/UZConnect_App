using System.Text;
using System.Text.RegularExpressions;

namespace UZConnect.Core.Models;

/// <summary>
/// The tested modem's Android locale is Chinese, so the stock dialer's USSD/MMI
/// dialog arrives in Chinese. Translate known system dialog strings; USSD
/// payload text from the network is left untouched.
/// </summary>
public static partial class UssdTextLocalizer
{
    private static readonly (string Chinese, string English)[] Replacements =
    {
        ("\u51FA\u73B0\u8FDE\u63A5\u95EE\u9898\u6216 MMI \u7801\u65E0\u6548\u3002", "Connection problem or invalid MMI code."),
        ("\u51FA\u73B0\u8FDE\u63A5\u95EE\u9898\u6216MMI\u7801\u65E0\u6548\u3002", "Connection problem or invalid MMI code."),
        ("\u51FA\u73B0\u8FDE\u63A5\u95EE\u9898\u6216 MMI \u7801\u65E0\u6548", "Connection problem or invalid MMI code."),
        ("\u51FA\u73B0\u8FDE\u63A5\u95EE\u9898\u6216MMI\u7801\u65E0\u6548", "Connection problem or invalid MMI code."),
        ("\u65E0\u6548\u7684 MMI \u7801", "Invalid MMI code."),
        ("\u65E0\u6548MMI\u7801", "Invalid MMI code."),
        ("\u624B\u673A\u4E2D\u6CA1\u6709 UIM \u5361", "No SIM card in the modem."),
        ("\u624B\u673A\u4E2D\u6CA1\u6709UIM\u5361", "No SIM card in the modem."),
        ("\u8BF7\u63D2\u5165 UIM \u5361", "Insert a SIM card."),
        ("\u8BF7\u63D2\u5165UIM\u5361", "Insert a SIM card."),
        ("\u7F51\u7EDC\u65E0\u54CD\u5E94", "No response from the network."),
        ("USSD \u7801\u65E0\u6548", "Invalid USSD code."),
        ("USSD\u7801\u65E0\u6548", "Invalid USSD code."),
        ("\u64CD\u4F5C\u5931\u8D25", "Operation failed."),
        ("\u6B63\u5728\u5904\u7406", "Processing..."),
        ("\u5DF2\u53D6\u6D88", "Cancelled."),
        ("\u8FDE\u63A5\u95EE\u9898", "Connection problem.")
    };

    private static readonly string[] ButtonLabels =
    {
        "\u786E\u5B9A", // OK
        "\u53D6\u6D88", // Cancel
        "\u5173\u95ED", // Close
        "\u7EE7\u7EED", // Continue
        "\u8FD4\u56DE"  // Back
    };

    private static readonly HashSet<string> DialerChrome = new(StringComparer.Ordinal)
    {
        "\u62E8\u53F7",       // Dial
        "\u901A\u8BDD",       // Call log
        "\u8054\u7CFB\u4EBA", // Contacts
        "\u6536\u85CF",       // Favorites
        "\u6700\u8FD1",       // Recent
        "\u952E\u76D8",       // Keypad
        "\u6302\u65AD",       // End call
        "\u77ED\u4FE1",       // Messages
        "\u66F4\u591A",       // More
        "\u547C\u53EB",       // Call
        "\u6B63\u5728\u547C\u53EB", // Calling
        "\u6B63\u5728\u8FD0\u884C USSD \u4EE3\u7801" // Running USSD code
    };

    private static readonly HashSet<string> StandaloneButtonLabels = new(StringComparer.Ordinal)
    {
        "OK",
        "Cancel",
        "Close",
        "Continue",
        "Back"
    };

    public static string Localize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            // On this firmware the dialog text and the "OK" label often arrive in
            // the same accessibility node, e.g. "出现连接问题或 MMI 码无效。确定".
            foreach (var label in ButtonLabels)
            {
                line = line.Replace(label, string.Empty, StringComparison.Ordinal);
            }

            line = MmiErrorPattern().Replace(line, "Connection problem or invalid MMI code.");
            line = StandaloneInvalidMmiPattern().Replace(line, "Invalid MMI code.");
            line = StandaloneInvalidUssdPattern().Replace(line, "Invalid USSD code.");
            line = FullWidthPunctuationPattern().Replace(line, match => match.Value switch
            {
                "\u3002" => ".",
                "\uFF0C" => ",",
                "\uFF1A" => ":",
                "\uFF1B" => ";",
                _ => match.Value
            }).Trim();

            if (line.Length == 0 || StandaloneButtonLabels.Contains(line) || DialerChrome.Contains(line))
            {
                continue;
            }

            foreach (var (chinese, english) in Replacements)
            {
                line = line.Replace(chinese, english, StringComparison.Ordinal);
            }

            if (ContainsChinese(line))
            {
                var systemText = TranslateSystemLine(line);
                if (systemText is not null)
                {
                    line = systemText;
                }
            }

            line = line.Trim();
            if (line.Length == 0 || !seen.Add(line))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(line);
        }

        return builder.ToString();
    }

    /// <summary>
    /// True when the captured dialog is a dialer/MMI system message rather than a
    /// network USSD payload. Used to mark the request as rejected instead of
    /// treating the system message as a balance result.
    /// </summary>
    public static bool IsFailure(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return text.Contains("MMI", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("not ready", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("not available", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("no service", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("no response", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("unable", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("rejected", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("connection problem", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ContainsChinese(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return text.Any(c => c is >= '\u4e00' and <= '\u9fff');
    }

    private static string? TranslateSystemLine(string line)
    {
        // Only translate known dialer/MMI dialog lines. Unknown Chinese text may be
        // the operator's USSD menu and must be shown unchanged.
        if (line.Contains("MMI", StringComparison.Ordinal) ||
            line.Contains("USSD", StringComparison.OrdinalIgnoreCase))
        {
            return "Connection problem or invalid MMI code.";
        }

        if (line.Contains("UIM", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("SIM", StringComparison.OrdinalIgnoreCase) ||
            line.Contains('\u5361')) // 卡 (card)
        {
            return "No SIM card, or the SIM is not ready.";
        }

        if (line.Contains("\u7F51\u7EDC")) // 网络
        {
            return "No response from the network.";
        }

        if (line.Contains("\u8FDE\u63A5")) // 连接
        {
            return "Connection problem.";
        }

        if (line.Contains("\u5931\u8D25")) // 失败
        {
            return "Operation failed.";
        }

        if (line.Contains("\u53D6\u6D88")) // 取消
        {
            return "Cancelled.";
        }

        return null;
    }

    [GeneratedRegex(@"\s*(?:\u51FA\u73B0)?\u8FDE\u63A5\u95EE\u9898\u6216\s*MMI\s*\u7801\u65E0\u6548\s*[\u3002.]?", RegexOptions.CultureInvariant)]
    private static partial Regex MmiErrorPattern();

    [GeneratedRegex(@"^\s*MMI\s*\u7801\s*\u65E0\u6548\s*[\u3002.]?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex StandaloneInvalidMmiPattern();

    [GeneratedRegex(@"^\s*USSD\s*(?:\u4EE3\u7801|\u7801)?\s*\u65E0\u6548\s*[\u3002.]?\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StandaloneInvalidUssdPattern();

    [GeneratedRegex(@"[\u3002\uFF0C\uFF1A\uFF1B]", RegexOptions.CultureInvariant)]
    private static partial Regex FullWidthPunctuationPattern();
}
