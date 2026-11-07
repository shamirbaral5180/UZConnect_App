using UZConnect.Core.Models;

namespace UZConnect.App.ViewModels;

public sealed class NavigationItem : ObservableObject
{
    private int _badge;

    public NavigationItem(string key, string label, string iconPath, IPageViewModel page)
    {
        Key = key;
        Label = label;
        IconPath = iconPath;
        Page = page;
    }

    public string Key { get; }
    public string Label { get; }
    public string IconPath { get; }
    public string Glyph { get; } = string.Empty;
    public IPageViewModel Page { get; }

    public int Badge
    {
        get => _badge;
        set
        {
            if (SetProperty(ref _badge, value))
            {
                OnPropertyChanged(nameof(HasBadge));
                OnPropertyChanged(nameof(BadgeText));
            }
        }
    }

    public bool HasBadge => Badge > 0;
    public string BadgeText => Badge > 99 ? "99+" : Badge.ToString();
}

public interface IPageViewModel
{
    string Title { get; }
    string Subtitle { get; }
    Task OnNavigatedToAsync(CancellationToken cancellationToken = default);
    Task RefreshFromSessionAsync(CancellationToken cancellationToken = default);
}

public sealed class InterfaceRow
{
    public InterfaceRow(UsbInterfaceInfo info)
    {
        Name = info.Name;
        Role = info.RoleLabel;
        ComPort = info.ComPort ?? string.Empty;
        Service = info.Service ?? string.Empty;
        Detail = string.Join(" · ", new[] { Role, ComPort, Service }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    public string Name { get; }
    public string Role { get; }
    public string ComPort { get; }
    public string Service { get; }
    public string Detail { get; }
}

public sealed class UsbDeviceRow
{
    public UsbDeviceRow(UsbDeviceInfo device)
    {
        Name = device.DisplayName;
        Identity = $"{device.VidPid} · {device.SerialNumber ?? "no serial"}";
        Description = device.BusReportedDescription ?? device.Manufacturer ?? "USB device";
        Interfaces = device.Interfaces.Select(i => new InterfaceRow(i)).ToArray();
        HasAdb = device.HasAdb;
    }

    public string Name { get; }
    public string Identity { get; }
    public string Description { get; }
    public IReadOnlyList<InterfaceRow> Interfaces { get; }
    public bool HasAdb { get; }
    public string InterfaceSummary => Interfaces.Count == 0 ? "No interfaces" : $"{Interfaces.Count} interface(s)";
}

public sealed class SmsFolderItem : ObservableObject
{
    private int _count;
    private int _unread;

    public SmsFolderItem(SmsFolder folder, string label, string glyph)
    {
        Folder = folder;
        Label = label;
        Glyph = glyph;
    }

    public SmsFolder Folder { get; }
    public string Label { get; }
    public string Glyph { get; }
    public string IconPath => Folder switch
    {
        SmsFolder.Inbox => "pack://application:,,,/UZConnect;component/Assets/icons/folder-inbox.png",
        SmsFolder.Sent => "pack://application:,,,/UZConnect;component/Assets/icons/folder-outbox.png",
        SmsFolder.Drafts => "pack://application:,,,/UZConnect;component/Assets/icons/folder-draft.png",
        SmsFolder.Recycle => "pack://application:,,,/UZConnect;component/Assets/icons/folder-recycle.png",
        _ => "pack://application:,,,/UZConnect;component/Assets/icons/folder-small.png"
    };

    public int Count
    {
        get => _count;
        set
        {
            if (SetProperty(ref _count, value))
            {
                OnPropertyChanged(nameof(CountText));
            }
        }
    }

    public int Unread
    {
        get => _unread;
        set
        {
            if (SetProperty(ref _unread, value))
            {
                OnPropertyChanged(nameof(HasUnread));
                OnPropertyChanged(nameof(UnreadText));
            }
        }
    }

    public string CountText => Count == 0 ? string.Empty : Count.ToString();
    public bool HasUnread => Unread > 0;
    public string UnreadText => Unread > 99 ? "99+" : Unread.ToString();
}

public sealed class MessageRow : ObservableObject
{
    private bool _isChecked;
    private SmsMessage _message;

    public MessageRow(SmsMessage message)
    {
        _message = message;
    }

    public SmsMessage Message
    {
        get => _message;
        set
        {
            if (SetProperty(ref _message, value))
            {
                Raise(nameof(Sender), nameof(Preview), nameof(TimeDisplay), nameof(FullTimeDisplay),
                    nameof(IsUnread), nameof(Initial), nameof(StatusText));
            }
        }
    }

    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }

    public string Sender => _message.AddressOrDefault;
    public string Preview => _message.Preview;
    public string TimeDisplay => Display.MessageTime(_message.Timestamp);
    public string FullTimeDisplay => Display.FullTime(_message.Timestamp);
    public bool IsUnread => !_message.IsRead;
    public string Initial => Display.Initial(_message.Address);
    public string StatusText => _message.Folder switch
    {
        SmsFolder.Sent => "Sent",
        SmsFolder.Drafts => "Draft",
        SmsFolder.Outbox => "Outbox",
        SmsFolder.Failed => "Failed",
        SmsFolder.Queued => "Queued",
        _ => _message.IsRead ? "Read" : "Unread"
    };

    public void MarkReadLocal()
    {
        if (!_message.IsRead)
        {
            Message = _message with { IsRead = true };
        }
    }

    public void MarkUnreadLocal()
    {
        if (_message.IsRead)
        {
            Message = _message with { IsRead = false };
        }
    }
}

public sealed class SimContactRow
{
    public SimContactRow(SimContact contact)
    {
        Contact = contact;
        Initial = Display.Initial(contact.DisplayName);
    }

    public SimContact Contact { get; }
    public string Name => Contact.DisplayName;
    public string Number => Contact.Number;
    public string Initial { get; }
}

public sealed class ApnRow
{
    public ApnRow(ApnConfig apn)
    {
        Apn = apn;
    }

    public ApnConfig Apn { get; }
    public string Name => Apn.DisplayName;
    public string Detail => string.Join(" · ", new[] { Apn.Apn, Apn.Type }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public bool IsCurrent => Apn.IsCurrent;
}

