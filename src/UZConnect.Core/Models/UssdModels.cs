namespace UZConnect.Core.Models;

public sealed record UssdCommand(long Id, string Name, string Code)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Code : Name;
}

public sealed record UssdResult(bool Success, string Output, string? Error, bool Unsupported, bool NeedsReply = false)
{
    public static UssdResult Failed(string error, bool unsupported = false)
        => new(false, string.Empty, error, unsupported);
}
