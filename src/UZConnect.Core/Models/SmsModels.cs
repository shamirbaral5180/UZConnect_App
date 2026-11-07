namespace UZConnect.Core.Models;

public enum SmsFolder
{
    Inbox = 0,
    Sent = 1,
    Drafts = 2,
    Outbox = 3,
    Failed = 4,
    Queued = 5,
    Recycle = 6
}

public sealed record SmsMessage(
    string DeviceSerial,
    long DeviceMessageId,
    SmsFolder Folder,
    string Address,
    string Body,
    DateTimeOffset Timestamp,
    bool IsRead,
    long ThreadId,
    int Status,
    int? ErrorCode,
    string? ServiceCenter,
    DateTimeOffset CachedAt)
{
    public string AddressOrDefault => string.IsNullOrWhiteSpace(Address) ? "Unknown sender" : Address;

    public string Preview
    {
        get
        {
            var singleLine = Body.Replace("\r", " ").Replace("\n", " ").Trim();
            return singleLine.Length <= 120 ? singleLine : singleLine[..117] + "…";
        }
    }

    public string FolderLabel => Folder switch
    {
        SmsFolder.Inbox => "Inbox",
        SmsFolder.Sent => "Sent",
        SmsFolder.Drafts => "Drafts",
        SmsFolder.Outbox => "Outbox",
        SmsFolder.Failed => "Failed",
        SmsFolder.Queued => "Queued",
        SmsFolder.Recycle => "Recycle",
        _ => Folder.ToString()
    };
}

public sealed record SmsSendRequest(string To, string Body);

public sealed record SmsSendResult(
    bool Success,
    string Status,
    string? Error,
    string? AgentMessageId,
    int Parts,
    DateTimeOffset CompletedAt)
{
    public static SmsSendResult Failed(string status, string? error, int parts = 0) =>
        new(false, status, error, null, parts, DateTimeOffset.Now);
}

public sealed record SmsSendHistoryEntry(
    long Id,
    string DeviceSerial,
    string To,
    string Body,
    int Parts,
    string Status,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SmsFolderCount(SmsFolder Folder, int Count, int Unread);

public sealed record SmsSyncResult(
    int Retrieved,
    int Added,
    int Updated,
    int Removed,
    string? Warning)
{
    public static SmsSyncResult Empty { get; } = new(0, 0, 0, 0, null);
}

public static class SmsSegmentCalculator
{
    // Conservative GSM 03.38 limits. Non-GSM text is measured in UTF-16 code units by
    // the device, so we report a range instead of pretending there is one exact count.
    public const int GsmSingleLimit = 160;
    public const int GsmMultipartLimit = 153;
    public const int UnicodeSingleLimit = 70;
    public const int UnicodeMultipartLimit = 67;

    public static bool IsGsmCompatible(string text)
    {
        const string gsm =
            "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?" +
            "¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà" +
            "^{}\\[~]|€";

        return text.All(c => gsm.Contains(c, StringComparison.Ordinal));
    }

    public static int CountSegments(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var gsm = IsGsmCompatible(text);
        var extended = text.Count(c => c is '^' or '{' or '}' or '\\' or '[' or '~' or ']' or '|' or '€');
        var length = (gsm ? text.Length : text.Length) + (gsm ? extended : 0);

        if (length <= (gsm ? GsmSingleLimit : UnicodeSingleLimit))
        {
            return 1;
        }

        var perPart = gsm ? GsmMultipartLimit : UnicodeMultipartLimit;
        return (int)Math.Ceiling(length / (double)perPart);
    }

    public static (int Units, int Segments, bool IsGsm) Describe(string? text)
    {
        text ??= string.Empty;
        var gsm = IsGsmCompatible(text);
        return (text.Length, CountSegments(text), gsm);
    }
}


