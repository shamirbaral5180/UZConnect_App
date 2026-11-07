namespace UZConnect.Core.Models;

/// <summary>
/// Operator-specific defaults for the tested Nepali networks. Detection uses the
/// SIM operator name reported by Android and the numeric MNC where available.
/// </summary>
public static class OperatorDefaults
{
    public static bool IsNtc(string? operatorName)
    {
        var value = operatorName ?? string.Empty;
        return value.Contains("Nepal Telecom", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Namaste", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("NTC", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("42901", StringComparison.Ordinal);
    }

    public static bool IsNcell(string? operatorName)
    {
        var value = operatorName ?? string.Empty;
        return value.Contains("Ncell", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("42902", StringComparison.Ordinal);
    }

    public static string DefaultBalanceCode(string? operatorName)
        => IsNtc(operatorName) ? "*400#" : IsNcell(operatorName) ? "*901#" : "*123#";

    public static IReadOnlyList<(string Name, string Code)> UssdCommands(string? operatorName)
    {
        if (IsNtc(operatorName))
        {
            return new[]
            {
                ("NTC Balance", "*400#"),
                ("NTC Data packs", "*1415#"),
                ("NTC Own number", "*9#")
            };
        }

        if (IsNcell(operatorName))
        {
            return new[]
            {
                ("Ncell Balance", "*901#"),
                ("Ncell Data packs", "*101#"),
                ("Ncell Own number", "*903#")
            };
        }

        return Array.Empty<(string Name, string Code)>();
    }

    public static (string Name, string Apn)? DefaultApn(string? operatorName)
        => IsNtc(operatorName)
            ? ("NTC", "ntc")
            : IsNcell(operatorName)
                ? ("Ncell", "web")
                : null;

    public static bool ApnMatches(string? apn, string? operatorName)
    {
        var value = apn ?? string.Empty;
        if (IsNtc(operatorName))
        {
            return value.Equals("ntc", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("ntnet", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("ntwap", StringComparison.OrdinalIgnoreCase);
        }

        if (IsNcell(operatorName))
        {
            return value.Equals("web", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("ncellnet", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public static bool ProfileNameMatches(string? name, string? operatorName)
    {
        var value = name ?? string.Empty;
        if (IsNtc(operatorName))
        {
            return value.Contains("NTC", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("Nepal", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("Namaste", StringComparison.OrdinalIgnoreCase);
        }

        if (IsNcell(operatorName))
        {
            return value.Contains("Ncell", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
