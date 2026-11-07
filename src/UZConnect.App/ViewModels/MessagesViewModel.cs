using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Win32;
using System.Text;
using System.IO;
using UZConnect.Core.Models;
using UZConnect.Windows.Services;

namespace UZConnect.App.ViewModels;

public sealed class MessagesViewModel : ObservableObject, IPageViewModel
{
    private readonly ModemSession _session;
    private SmsFolderItem? _selectedFolder;
    private MessageRow? _selectedMessage;
    private bool _isComposing;
    private bool _isBusy;
    private bool _isSending;
    private bool _isInstalling;
    private bool _agentInstalled = true;
    private bool _showAgentPrompt;
    private bool _showDeletePrompt;
    private bool _showHistory;
    private string _composeTo = string.Empty;
    private string _composeBody = string.Empty;
    private string _composeTitle = "New text message";
    private string _statusText = "Select a folder to load messages.";
    private string _sendStatus = string.Empty;
    private bool _sendFailed;
    private string _deletePromptText = string.Empty;
    private IReadOnlyList<MessageRow> _pendingDelete = Array.Empty<MessageRow>();
    private readonly HashSet<MessageRow> _selectedRows = new();
    private int _unreadTotal;
    private int _loadVersion;
    private bool _suppressAutoLoad;

    public MessagesViewModel(ModemSession session)
    {
        _session = session;
        Folders = new ObservableCollection<SmsFolderItem>
        {
            new(SmsFolder.Inbox, "Inbox", "\uE8B7"),
            new(SmsFolder.Sent, "Outbox", "\uE724"),
            new(SmsFolder.Drafts, "Draft", "\uE70F"),
            new(SmsFolder.Recycle, "Recycle", "\uE74D")
        };

        RefreshCommand = new AsyncRelayCommand(() => LoadFolderAsync(SelectedFolder, sync: true), onError: ReportError);
        NewMessageCommand = new RelayCommand(() => StartCompose());
        ReplyCommand = new RelayCommand(() => StartCompose(SelectedMessage?.Sender, string.Empty, "New text message"), () => SelectedMessage is not null);
        ForwardCommand = new RelayCommand(() => StartCompose(null, "Fwd: " + (SelectedMessage?.Message.Body ?? string.Empty), "New text message"), () => SelectedMessage is not null);
        SaveDraftCommand = new AsyncRelayCommand(SaveDraftAsync, () => CanSend || !string.IsNullOrWhiteSpace(ComposeBody), ReportError);
        EmptyRecycleCommand = new AsyncRelayCommand(EmptyRecycleAsync, () => SelectedFolder?.Folder == SmsFolder.Recycle, ReportError);
        ExportCommand = new RelayCommand(ExportFolder);
        SendCommand = new AsyncRelayCommand(SendAsync, () => CanSend, ReportError);
        CancelComposeCommand = new RelayCommand(() => IsComposing = false);
        MarkReadCommand = new AsyncRelayCommand(() => SetReadAsync(true), () => SelectedMessage is not null && !IsBusy, ReportError);
        MarkUnreadCommand = new AsyncRelayCommand(() => SetReadAsync(false), () => SelectedMessage is not null && !IsBusy, ReportError);
        RequestDeleteSelectedCommand = new RelayCommand(RequestDeleteSelected, CanDeleteSelected);
        RequestDeleteCheckedCommand = new RelayCommand(RequestDeleteChecked, () => Messages.Any(m => m.IsChecked));
        ConfirmDeleteCommand = new AsyncRelayCommand(DeleteConfirmedAsync, () => _pendingDelete.Count > 0 && !IsBusy, ReportError);
        CancelDeleteCommand = new RelayCommand(() => ShowDeletePrompt = false);
        RequestInstallAgentCommand = new RelayCommand(() => ShowAgentPrompt = true, () => IsConnected);
        ConfirmInstallAgentCommand = new AsyncRelayCommand(InstallAgentAsync, () => IsConnected && !IsInstalling, ReportError);
        CancelAgentPromptCommand = new RelayCommand(() => ShowAgentPrompt = false);
        ShowHistoryCommand = new AsyncRelayCommand(OpenHistoryAsync, onError: ReportError);
        CloseHistoryCommand = new RelayCommand(() => ShowHistory = false);
        SelectFolderCommand = new RelayCommand(parameter =>
        {
            if (parameter is SmsFolderItem folder)
            {
                SelectedFolder = folder;
            }
        });
    }

    public string Title => "Messages";
    public string Subtitle => "Read, compose and manage SMS through the modem's own Android SMS service.";

    public ObservableCollection<SmsFolderItem> Folders { get; }
    public ObservableCollection<MessageRow> Messages { get; } = new();
    public ObservableCollection<SmsSendHistoryEntry> SendHistory { get; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand NewMessageCommand { get; }
    public ICommand ReplyCommand { get; }
    public ICommand ForwardCommand { get; }
    public ICommand SaveDraftCommand { get; }
    public ICommand EmptyRecycleCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand SendCommand { get; }
    public ICommand CancelComposeCommand { get; }
    public ICommand MarkReadCommand { get; }
    public ICommand MarkUnreadCommand { get; }
    public ICommand RequestDeleteSelectedCommand { get; }
    public ICommand RequestDeleteCheckedCommand { get; }
    public ICommand ConfirmDeleteCommand { get; }
    public ICommand CancelDeleteCommand { get; }
    public ICommand RequestInstallAgentCommand { get; }
    public ICommand ConfirmInstallAgentCommand { get; }
    public ICommand CancelAgentPromptCommand { get; }
    public ICommand ShowHistoryCommand { get; }
    public ICommand CloseHistoryCommand { get; }
    public ICommand SelectFolderCommand { get; }

    public bool IsConnected => _session.IsConnected;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(ShowEmpty));
            }
        }
    }

    public bool IsSending
    {
        get => _isSending;
        private set
        {
            if (SetProperty(ref _isSending, value))
            {
                OnPropertyChanged(nameof(CanSend));
            }
        }
    }

    public bool IsInstalling
    {
        get => _isInstalling;
        private set
        {
            if (SetProperty(ref _isInstalling, value))
            {
                OnPropertyChanged(nameof(AgentButtonText));
            }
        }
    }

    public bool AgentInstalled
    {
        get => _agentInstalled;
        private set
        {
            if (SetProperty(ref _agentInstalled, value))
            {
                OnPropertyChanged(nameof(ShowAgentBanner));
                OnPropertyChanged(nameof(AgentButtonText));
            }
        }
    }

    public bool ShowAgentBanner => IsConnected && !AgentInstalled;
    public string AgentButtonText => IsInstalling ? "Installing…" : "Install helper";

    public bool ShowAgentPrompt
    {
        get => _showAgentPrompt;
        private set => SetProperty(ref _showAgentPrompt, value);
    }

    public bool ShowDeletePrompt
    {
        get => _showDeletePrompt;
        private set => SetProperty(ref _showDeletePrompt, value);
    }

    public bool ShowHistory
    {
        get => _showHistory;
        private set => SetProperty(ref _showHistory, value);
    }

    public SmsFolderItem? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (!SetProperty(ref _selectedFolder, value) || value is null)
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedFolderLabel));
            OnPropertyChanged(nameof(FolderTitle));
            OnPropertyChanged(nameof(IsRecycleFolder));
            if (!_suppressAutoLoad)
            {
                _ = LoadFolderAsync(value, sync: true);
            }
        }
    }

    public string SelectedFolderLabel => SelectedFolder?.Label ?? "Messages";
    public string FolderTitle => SelectedFolder?.Label ?? "Messages";
    public bool IsRecycleFolder => SelectedFolder?.Folder == SmsFolder.Recycle;

    public MessageRow? SelectedMessage
    {
        get => _selectedMessage;
        set
        {
            if (!SetProperty(ref _selectedMessage, value))
            {
                return;
            }

            Raise(nameof(HasSelectedMessage), nameof(ShowPreview), nameof(ShowMessagePlaceholder), nameof(PreviewSender), nameof(PreviewBody), nameof(PreviewDate),
                nameof(PreviewInitial), nameof(PreviewStatus), nameof(PreviewAddress), nameof(PreviewName),
                nameof(PreviewNumber), nameof(PreviewDateTime));
            if (value is { IsUnread: true } && IsConnected && !IsComposing)
            {
                _ = MarkSelectedReadAsync(value);
            }
        }
    }

    public bool HasSelectedMessage => SelectedMessage is not null;
    public bool ShowPreview => HasSelectedMessage && !IsComposing;
    public bool ShowMessagePlaceholder => !IsComposing && !HasSelectedMessage;
    public bool IsComposing
    {
        get => _isComposing;
        private set
        {
            if (SetProperty(ref _isComposing, value))
            {
                Raise(nameof(ShowPreview), nameof(ShowMessagePlaceholder));
            }
        }
    }

    public string MessageCountText => Messages.Count == 1 ? "1 message" : $"{Messages.Count} messages";

    public string PreviewSender => SelectedMessage?.Sender ?? string.Empty;
    public string PreviewBody => SelectedMessage?.Message.Body ?? string.Empty;
    public string PreviewDate => SelectedMessage?.FullTimeDisplay ?? string.Empty;
    public string PreviewInitial => SelectedMessage?.Initial ?? "?";
    public string PreviewAddress => SelectedMessage?.Sender ?? string.Empty;
    public string PreviewStatus => SelectedMessage?.StatusText ?? string.Empty;
    public string PreviewName => SelectedMessage?.Sender ?? string.Empty;
    public string PreviewNumber => SelectedMessage?.Sender ?? string.Empty;
    public string PreviewDateTime => SelectedMessage?.FullTimeDisplay ?? string.Empty;

    public string ComposeTitle
    {
        get => _composeTitle;
        private set => SetProperty(ref _composeTitle, value);
    }

    public string ComposeTo
    {
        get => _composeTo;
        set
        {
            if (SetProperty(ref _composeTo, value))
            {
                OnPropertyChanged(nameof(CanSend));
                OnPropertyChanged(nameof(ComposeRecipientCountText));
            }
        }
    }

    public string ComposeBody
    {
        get => _composeBody;
        set
        {
            if (SetProperty(ref _composeBody, value))
            {
                UpdateComposeStats();
                OnPropertyChanged(nameof(CanSend));
            }
        }
    }

    public string ComposeCountText { get; private set; } = "0 characters · 0 segments";
    public string ComposeEncodingText { get; private set; } = "GSM 7-bit · 160 characters per segment";
    public string ComposeRecipientCountText => string.IsNullOrWhiteSpace(ComposeTo) ? "0/100" : "1/100";
    public string ComposeSegmentText
    {
        get
        {
            var (units, segments, _) = SmsSegmentCalculator.Describe(ComposeBody);
            return $"({units})/{segments}/10";
        }
    }

    public bool CanSend => IsConnected && !IsSending;

    public string SendStatus
    {
        get => _sendStatus;
        private set
        {
            if (SetProperty(ref _sendStatus, value))
            {
                OnPropertyChanged(nameof(HasSendStatus));
            }
        }
    }

    public bool SendFailed
    {
        get => _sendFailed;
        private set => SetProperty(ref _sendFailed, value);
    }

    public bool HasSendStatus => !string.IsNullOrWhiteSpace(SendStatus);

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string DeletePromptText
    {
        get => _deletePromptText;
        private set => SetProperty(ref _deletePromptText, value);
    }

    public bool ShowEmpty => !IsBusy && Messages.Count == 0;
    public string EmptyText => IsConnected
        ? $"No messages in {SelectedFolderLabel}."
        : "No cached messages. Connect the modem to sync.";
    public string ConnectionText => IsConnected
        ? $"Connected · {Display.OrNotAvailable(_session.DeviceInfo.Model)}"
        : "Offline · showing cached messages";

    public int UnreadTotal
    {
        get => _unreadTotal;
        private set => SetProperty(ref _unreadTotal, value);
    }

    public async Task RefreshUnreadCountAsync(CancellationToken cancellationToken = default)
        => await RefreshCountsAsync(cancellationToken);

    public async Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedFolder is null)
        {
            _suppressAutoLoad = true;
            SelectedFolder = Folders[0];
            _suppressAutoLoad = false;
        }

        await LoadFolderAsync(SelectedFolder, sync: IsConnected, cancellationToken);

        await CheckAgentAsync(cancellationToken);
        await RefreshCountsAsync(cancellationToken);
        Raise(nameof(IsConnected), nameof(ShowAgentBanner), nameof(ConnectionText), nameof(CanSend));
    }

    public async Task RefreshFromSessionAsync(CancellationToken cancellationToken = default)
    {
        Raise(nameof(IsConnected), nameof(ShowAgentBanner), nameof(ConnectionText), nameof(CanSend),
            nameof(EmptyText));
        if (IsConnected)
        {
            await CheckAgentAsync(cancellationToken);
        }
    }

    private async Task LoadFolderAsync(SmsFolderItem? folder, bool sync, CancellationToken cancellationToken = default)
    {
        if (folder is null)
        {
            return;
        }

        var version = Interlocked.Increment(ref _loadVersion);
        IsBusy = true;
        StatusText = $"Loading {folder.Label.ToLowerInvariant()}…";
        try
        {
            var cached = await _session.GetCachedMessagesAsync(folder.Folder, cancellationToken);
            if (version != _loadVersion)
            {
                return;
            }

            ReplaceMessages(cached);
            StatusText = cached.Count == 0 ? $"No cached messages in {folder.Label}." : $"{cached.Count} cached message(s).";

            if (sync && IsConnected && folder.Folder is SmsFolder.Inbox or SmsFolder.Sent)
            {
                StatusText = $"Syncing {folder.Label.ToLowerInvariant()} with the device…";
                try
                {
                    var result = await _session.SyncMessagesAsync(folder.Folder, cancellationToken);
                    var refreshed = await _session.GetCachedMessagesAsync(folder.Folder, cancellationToken);
                    if (version != _loadVersion)
                    {
                        return;
                    }

                    ReplaceMessages(refreshed);
                    StatusText = $"Synced {folder.Label.ToLowerInvariant()}: {result.Added} new, {result.Updated} updated, {result.Removed} removed.";
                }
                catch (Exception ex)
                {
                    StatusText = $"Showing cached messages. Sync failed: {ex.Message}";
                }
            }

            await RefreshCountsAsync(cancellationToken);
            Raise(nameof(ShowEmpty), nameof(EmptyText), nameof(ConnectionText));
        }
        catch (Exception ex)
        {
            StatusText = $"Could not load messages: {ex.Message}";
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsBusy = false;
                Raise(nameof(ShowEmpty));
            }
        }
    }

    private void ReplaceMessages(IReadOnlyList<SmsMessage> messages)
    {
        var previousId = SelectedMessage?.Message.DeviceMessageId;
        foreach (var row in Messages)
        {
            row.PropertyChanged -= OnMessageRowPropertyChanged;
        }

        Messages.Clear();
        foreach (var message in messages)
        {
            var row = new MessageRow(message);
            row.PropertyChanged += OnMessageRowPropertyChanged;
            Messages.Add(row);
        }

        SelectedMessage = previousId is null
            ? Messages.FirstOrDefault()
            : Messages.FirstOrDefault(m => m.Message.DeviceMessageId == previousId) ?? Messages.FirstOrDefault();
        Raise(nameof(SelectAll), nameof(CheckedCount));
    }

    private void OnMessageRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MessageRow.IsChecked))
        {
            OnPropertyChanged(nameof(SelectAll));
            OnPropertyChanged(nameof(CheckedCount));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task MarkSelectedReadAsync(MessageRow row)
    {
        try
        {
            await _session.MarkReadAsync(row.Message, true);
            row.MarkReadLocal();
            await RefreshCountsAsync();
        }
        catch
        {
            // Keep the message visible even if the device update fails.
        }
    }

    private async Task SetReadAsync(bool isRead)
    {
        var row = SelectedMessage;
        if (row is null || !IsConnected)
        {
            StatusText = "Connect the modem to change message state on the device.";
            return;
        }

        await _session.MarkReadAsync(row.Message, isRead);
        if (isRead)
        {
            row.MarkReadLocal();
        }
        else
        {
            row.MarkUnreadLocal();
        }

        await RefreshCountsAsync();
        StatusText = isRead ? "Message marked read." : "Message marked unread.";
    }

    private void StartCompose(string? to = null, string? body = null, string title = "New text message")
    {
        ComposeTitle = title;
        IsComposing = true;
        ComposeTo = to ?? string.Empty;
        ComposeBody = body ?? string.Empty;
        SendStatus = IsConnected ? string.Empty : "Connect the modem before sending a message.";
        SendFailed = !IsConnected;
        ShowAgentPrompt = false;
        UpdateComposeStats();
    }

    private async Task SendAsync()
    {
        if (!IsConnected)
        {
            SendFailed = true;
            SendStatus = "Connect the modem before sending a message.";
            return;
        }

        if (string.IsNullOrWhiteSpace(ComposeTo))
        {
            SendFailed = true;
            SendStatus = "Enter a recipient number or choose one with Send to...";
            return;
        }

        if (string.IsNullOrWhiteSpace(ComposeBody))
        {
            SendFailed = true;
            SendStatus = "Enter the message text before sending.";
            return;
        }

        if (!AgentInstalled)
        {
            // The package check can race the first device refresh. Ask once more
            // before showing the install prompt.
            try
            {
                AgentInstalled = await _session.IsSmsAgentInstalledAsync();
            }
            catch
            {
                AgentInstalled = false;
            }
        }

        if (!AgentInstalled)
        {
            ShowAgentPrompt = true;
            SendFailed = true;
            SendStatus = "Install the one-time SMS helper, then send again.";
            return;
        }

        IsSending = true;
        SendFailed = false;
        SendStatus = "Sending…";
        try
        {
            var result = await _session.SendSmsAsync(ComposeTo.Trim(), ComposeBody);
            if (result.Success)
            {
                SendStatus = $"Sent in {result.Parts} segment(s).";
                SendFailed = false;
                ComposeBody = string.Empty;
                IsComposing = false;
                await SelectSentFolderAsync();
                StatusText = "Message sent and recorded in history.";
            }
            else
            {
                SendFailed = true;
                SendStatus = string.IsNullOrWhiteSpace(result.Error)
                    ? $"Send failed ({result.Status})."
                    : $"Send failed: {result.Error}";
            }
        }
        catch (Exception ex)
        {
            SendFailed = true;
            SendStatus = $"Send failed: {ex.Message}";
        }
        finally
        {
            IsSending = false;
        }
    }

    private async Task SelectSentFolderAsync()
    {
        var sent = Folders.FirstOrDefault(f => f.Folder == SmsFolder.Sent);
        if (sent is not null)
        {
            _suppressAutoLoad = true;
            SelectedFolder = sent;
            _suppressAutoLoad = false;
            await LoadFolderAsync(sent, sync: true);
        }
    }

    private void UpdateComposeStats()
    {
        var (units, segments, isGsm) = SmsSegmentCalculator.Describe(ComposeBody);
        ComposeCountText = $"{units} character{(units == 1 ? string.Empty : "s")} · {segments} segment{(segments == 1 ? string.Empty : "s")}";
        ComposeEncodingText = isGsm ? "GSM 7-bit" : "Unicode";
        OnPropertyChanged(nameof(ComposeCountText));
        OnPropertyChanged(nameof(ComposeEncodingText));
        OnPropertyChanged(nameof(ComposeSegmentText));
        OnPropertyChanged(nameof(ComposeRecipientCountText));
    }

    private bool CanDeleteSelected()
        => !IsBusy &&
           (Messages.Any(m => m.IsChecked) || _selectedRows.Count > 0 || SelectedMessage is not null);

    public bool SelectAll
    {
        get => Messages.Count > 0 && Messages.All(m => m.IsChecked);
        set
        {
            foreach (var row in Messages)
            {
                row.IsChecked = value;
            }

            OnPropertyChanged(nameof(SelectAll));
            OnPropertyChanged(nameof(CheckedCount));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public int CheckedCount => Messages.Count(m => m.IsChecked);

    public void SetSelectedRows(IEnumerable<MessageRow> rows)
    {
        _selectedRows.Clear();
        foreach (var row in rows)
        {
            _selectedRows.Add(row);
        }

        CommandManager.InvalidateRequerySuggested();
    }

    public void OpenMessage(MessageRow row)
    {
        SelectedMessage = row;
        StartCompose(row.Sender, row.Message.Body, "Edit");
    }

    private void RequestDeleteSelected()
    {
        var rows = Messages.Where(m => m.IsChecked).ToList();
        foreach (var row in _selectedRows)
        {
            if (!rows.Contains(row))
            {
                rows.Add(row);
            }
        }

        if (rows.Count == 0 && SelectedMessage is not null)
        {
            rows.Add(SelectedMessage);
        }

        if (rows.Count == 0)
        {
            StatusText = "Select or tick at least one message first.";
            return;
        }

        PrepareDelete(rows);
    }

    private void RequestDeleteChecked()
    {
        var checkedRows = Messages.Where(m => m.IsChecked).ToList();
        if (checkedRows.Count == 0)
        {
            return;
        }

        PrepareDelete(checkedRows);
    }

    private void PrepareDelete(IReadOnlyList<MessageRow> rows)
    {
        _pendingDelete = rows;
        DeletePromptText = rows.Count == 1
            ? "Delete this message from the modem? This cannot be undone."
            : $"Delete {rows.Count} messages from the modem? This cannot be undone.";
        ShowDeletePrompt = true;
    }

    private async Task DeleteConfirmedAsync()
    {
        ShowDeletePrompt = false;
        if (_pendingDelete.Count == 0)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var deleted = await _session.DeleteMessagesAsync(_pendingDelete.Select(r => r.Message));
            foreach (var row in _pendingDelete)
            {
                Messages.Remove(row);
            }

            if (SelectedMessage is not null && _pendingDelete.Contains(SelectedMessage))
            {
                SelectedMessage = Messages.FirstOrDefault();
            }

            _pendingDelete = Array.Empty<MessageRow>();
            _selectedRows.Clear();
            await RefreshCountsAsync();
            Raise(nameof(SelectAll), nameof(CheckedCount));
            StatusText = $"{deleted} message(s) deleted.";
        }
        finally
        {
            IsBusy = false;
            Raise(nameof(ShowEmpty));
        }
    }

    private async Task CheckAgentAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            AgentInstalled = false;
            return;
        }

        try
        {
            AgentInstalled = await _session.IsSmsAgentInstalledAsync(cancellationToken);
        }
        catch
        {
            AgentInstalled = false;
        }

        Raise(nameof(ShowAgentBanner), nameof(CanSend));
    }

    private async Task InstallAgentAsync()
    {
        IsInstalling = true;
        try
        {
            await _session.InstallSmsAgentAsync();
            AgentInstalled = await _session.IsSmsAgentInstalledAsync();
            ShowAgentPrompt = false;
            SendFailed = false;
            SendStatus = "SMS helper installed. You can send messages now.";
            StatusText = "One-time SMS helper installed.";
        }
        catch (Exception ex)
        {
            SendFailed = true;
            SendStatus = $"Helper install failed: {ex.Message}";
        }
        finally
        {
            IsInstalling = false;
        }
    }

    private async Task OpenHistoryAsync()
    {
        SendHistory.Clear();
        var history = await _session.GetSendHistoryAsync();
        foreach (var entry in history)
        {
            SendHistory.Add(entry);
        }

        ShowHistory = true;
    }

    public Task<IReadOnlyList<SimContact>> GetContactsForComposeAsync(CancellationToken cancellationToken = default)
        => _session.GetSimContactsAsync(cancellationToken);

    private async Task SaveDraftAsync()
    {
        if (string.IsNullOrWhiteSpace(ComposeBody))
        {
            SendFailed = true;
            SendStatus = "Enter a message before saving a draft.";
            return;
        }

        try
        {
            await _session.SaveDraftAsync(ComposeTo.Trim(), ComposeBody);
            SendFailed = false;
            SendStatus = "Saved to Draft.";
            IsComposing = false;
            var drafts = Folders.FirstOrDefault(f => f.Folder == SmsFolder.Drafts);
            if (drafts is not null)
            {
                _suppressAutoLoad = true;
                SelectedFolder = drafts;
                _suppressAutoLoad = false;
                await LoadFolderAsync(drafts, sync: false);
            }
        }
        catch (Exception ex)
        {
            SendFailed = true;
            SendStatus = $"Could not save draft: {ex.Message}";
        }
    }

    private async Task EmptyRecycleAsync()
    {
        if (SelectedFolder?.Folder != SmsFolder.Recycle)
        {
            return;
        }

        var count = await _session.EmptyRecycleAsync();
        Messages.Clear();
        SelectedMessage = null;
        await RefreshCountsAsync();
        StatusText = $"{count} message(s) permanently deleted.";
        Raise(nameof(ShowEmpty));
    }

    private void ExportFolder()
    {
        var dialog = new SaveFileDialog
        {
            Title = "SMS Export",
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"uzconnect-sms-{SelectedFolder?.Label ?? "messages"}.csv"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var builder = new StringBuilder();
            builder.AppendLine("Name/Number,Content,Date/Time,Folder");
            foreach (var row in Messages)
            {
                builder.AppendLine(string.Join(",",
                    Csv(row.Message.Address),
                    Csv(row.Message.Body),
                    Csv(row.FullTimeDisplay),
                    Csv(row.Message.FolderLabel)));
            }

            File.WriteAllText(dialog.FileName, builder.ToString(), Encoding.UTF8);
            StatusText = $"Exported {Messages.Count} message(s) to {dialog.FileName}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    private static string Csv(string? value)
    {
        var text = value ?? string.Empty;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    private async Task RefreshCountsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var counts = await _session.GetFolderCountsAsync(cancellationToken);
            foreach (var folder in Folders)
            {
                var count = counts.FirstOrDefault(c => c.Folder == folder.Folder);
                folder.Count = count?.Count ?? 0;
                folder.Unread = count?.Unread ?? 0;
            }

            UnreadTotal = Folders.Sum(f => f.Unread);
        }
        catch
        {
            // Counts are cosmetic; keep the list usable.
        }
    }

    private void ReportError(Exception ex)
    {
        SendFailed = true;
        SendStatus = $"Operation failed: {ex.Message}";
        StatusText = $"Operation failed: {ex.Message}";
    }
}











