using System.Text;

namespace UZConnect.Core.Models;

/// <summary>
/// Parses a GSM +CUSD response from the modem AT command channel.
/// Example:
///   +CUSD: 1,"Balance : Rs. 60.44 Expiry date: 2026-12-05.\r\n1 >Rs50 ...",15
/// </summary>
public static class UssdCusdParser
{
    public static UssdCusdResponse? Parse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        var marker = response.LastIndexOf("+CUSD:", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return null;
        }

        var span = response[(marker + 6)..].TrimStart();
        var comma = span.IndexOf(',');
        if (comma < 0 || !int.TryParse(span[..comma].Trim(), out var status))
        {
            return null;
        }

        var rest = span[(comma + 1)..].TrimStart();
        if (rest.Length == 0 || rest[0] != '"')
        {
            return null;
        }

        var text = new StringBuilder();
        var index = 1;
        var closed = false;
        for (; index < rest.Length; index++)
        {
            var value = rest[index];
            if (value == '\\' && index + 1 < rest.Length)
            {
                var escaped = rest[index + 1];
                switch (escaped)
                {
                    case '"':
                    case '\\':
                        text.Append(escaped);
                        index++;
                        continue;
                    case 'r':
                        text.Append('\r');
                        index++;
                        continue;
                    case 'n':
                        text.Append('\n');
                        index++;
                        continue;
                    default:
                        text.Append(escaped);
                        index++;
                        continue;
                }
            }

            if (value == '"')
            {
                if (index + 1 < rest.Length && rest[index + 1] == '"')
                {
                    text.Append('"');
                    index++;
                    continue;
                }

                closed = true;
                break;
            }

            text.Append(value);
        }

        if (!closed)
        {
            return null;
        }

        int? dcs = null;
        var tail = rest[(index + 1)..];
        var dcsComma = tail.IndexOf(',');
        if (dcsComma >= 0)
        {
            var dcsText = new string(tail[(dcsComma + 1)..]
                .TakeWhile(char.IsDigit)
                .ToArray());
            if (int.TryParse(dcsText, out var parsedDcs))
            {
                dcs = parsedDcs;
            }
        }

        return new UssdCusdResponse(status, text.ToString().Trim(), dcs);
    }

    public static string DescribeFailure(int status) => status switch
    {
        2 => "The USSD session was terminated by the network.",
        3 => "The USSD operation is not allowed by the network.",
        4 => "The network does not support USSD for this SIM.",
        5 => "The network did not answer the USSD request in time.",
        _ => $"The USSD request failed (status {status})."
    };
}

public sealed record UssdCusdResponse(int Status, string Text, int? Dcs)
{
    public bool IsSuccess => Status is 0 or 1;
    public bool NeedsReply => Status == 1;
}
