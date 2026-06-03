using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;

namespace Hbpos.Client.Wpf.Services;

public enum LinklyTerminalDialogMode
{
    CloudBackendInteractive,
    CloudDirectStatus
}

public sealed record LinklyTerminalDialogButton(
    string TextResourceKey,
    string Key,
    string? Data = null,
    bool IsDestructive = false);

public sealed class LinklyTerminalDialogButtonViewModel : ObservableObject
{
    private string _text;

    public LinklyTerminalDialogButtonViewModel(
        LinklyTerminalDialogButton source,
        string text)
    {
        Source = source;
        _text = text;
    }

    public LinklyTerminalDialogButton Source { get; }

    public string Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    public string Key => Source.Key;

    public string? Data => Source.Data;

    public bool IsDestructive => Source.IsDestructive;

    public void RefreshText(ILocalizationService localization)
    {
        Text = localization.T(Source.TextResourceKey);
    }
}

public sealed record LinklyTerminalDialogState(
    string SessionId,
    string Status,
    string? DisplayText,
    string? ReceiptText,
    string? ResponseText,
    int RecoveryCount,
    int? LastHttpStatus,
    string? Message,
    LinklyTerminalDialogMode Mode = LinklyTerminalDialogMode.CloudBackendInteractive,
    bool IsInteractive = true,
    bool IsFinal = false,
    IReadOnlyList<LinklyTerminalDialogButton>? DisplayButtons = null,
    string? InputType = null,
    string? GraphicCode = null);

public sealed record LinklyTerminalDialogAction(
    string Key,
    string? Data);

public static class LinklyTerminalDialogKeys
{
    public const string OkCancel = "0";
    public const string Yes = "1";
    public const string No = "2";
    public const string Auth = "3";

    public static string Normalize(string key)
    {
        // Linkly REST sendkey 只接受官方数字键值；这里兼容旧窗口遗留的文本动作。
        var normalized = key.Trim();
        return normalized.ToUpperInvariant() switch
        {
            "OK" or "CANCEL" or "OKCANCEL" or "OK/CANCEL" => OkCancel,
            "YES" => Yes,
            "NO" => No,
            "AUTH" => Auth,
            _ => normalized
        };
    }
}

public interface ILinklyTerminalDialogService
{
    Task<LinklyTerminalDialogAction?> UpdateAsync(
        LinklyTerminalDialogState state,
        CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);
}

public interface ILinklyTerminalDialogPresenter
{
    bool IsOpen { get; }

    string TitleText { get; }

    string StatusLabelText { get; }

    string DisplayLabelText { get; }

    string ReceiptLabelText { get; }

    string? SessionId { get; }

    string StatusText { get; }

    string? DisplayText { get; }

    string? ReceiptText { get; }

    string? ResponseText { get; }

    string? MessageText { get; }

    bool IsInteractive { get; }

    bool IsFinal { get; }

    bool IsCloseButtonVisible { get; }

    bool IsCancelPaymentVisible { get; }

    string CancelPaymentText { get; }

    ObservableCollection<LinklyTerminalDialogButtonViewModel> DisplayButtons { get; }

    IRelayCommand<LinklyTerminalDialogButtonViewModel> SubmitActionCommand { get; }

    IRelayCommand CancelPaymentCommand { get; }

    IAsyncRelayCommand CloseCommand { get; }
}

public sealed class WpfLinklyTerminalDialogService :
    ObservableObject,
    ILinklyTerminalDialogService,
    ILinklyTerminalDialogPresenter
{
    private readonly ILocalizationService _localization;
    private LinklyTerminalDialogAction? _pendingAction;
    private bool _isOpen;
    private string? _sessionId;
    private string _statusText = string.Empty;
    private string? _displayText;
    private string? _receiptText;
    private string? _responseText;
    private string? _messageText;
    private bool _isInteractive;
    private bool _isFinal;
    private LinklyTerminalDialogMode _mode = LinklyTerminalDialogMode.CloudBackendInteractive;

    public WpfLinklyTerminalDialogService(ILocalizationService localization)
    {
        _localization = localization;
        SubmitActionCommand = new RelayCommand<LinklyTerminalDialogButtonViewModel>(
            SubmitAction,
            button => IsOpen && button is not null);
        CancelPaymentCommand = new RelayCommand(
            SubmitCancelPayment,
            () => IsCancelPaymentVisible);
        CloseCommand = new AsyncRelayCommand(CloseOrCancelAsync);
        _localization.CultureChanged += (_, _) => RaiseLocalizedTextChanged();
        RaiseLocalizedTextChanged();
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set
        {
            if (SetProperty(ref _isOpen, value))
            {
                RaiseDialogButtonStateChanged();
            }
        }
    }

    public string TitleText => _localization.T("linkly.backend.dialog.title");

    public string StatusLabelText => _localization.T("linkly.backend.dialog.status");

    public string DisplayLabelText => _localization.T("linkly.backend.dialog.display");

    public string ReceiptLabelText => _localization.T("linkly.backend.dialog.receipt");

    public string? SessionId
    {
        get => _sessionId;
        private set => SetProperty(ref _sessionId, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string? DisplayText
    {
        get => _displayText;
        private set => SetProperty(ref _displayText, value);
    }

    public string? ReceiptText
    {
        get => _receiptText;
        private set => SetProperty(ref _receiptText, value);
    }

    public string? ResponseText
    {
        get => _responseText;
        private set => SetProperty(ref _responseText, value);
    }

    public string? MessageText
    {
        get => _messageText;
        private set => SetProperty(ref _messageText, value);
    }

    public bool IsInteractive
    {
        get => _isInteractive;
        private set
        {
            if (SetProperty(ref _isInteractive, value))
            {
                RaiseDialogButtonStateChanged();
            }
        }
    }

    public bool IsFinal
    {
        get => _isFinal;
        private set
        {
            if (SetProperty(ref _isFinal, value))
            {
                RaiseDialogButtonStateChanged();
            }
        }
    }

    public bool IsCloseButtonVisible => IsOpen;

    public bool IsCancelPaymentVisible =>
        IsOpen &&
        _mode == LinklyTerminalDialogMode.CloudDirectStatus &&
        IsInteractive &&
        !IsFinal;

    public string CancelPaymentText => _localization.T("linkly.backend.dialog.button.cancelPayment");

    public ObservableCollection<LinklyTerminalDialogButtonViewModel> DisplayButtons { get; } = [];

    public IRelayCommand<LinklyTerminalDialogButtonViewModel> SubmitActionCommand { get; }

    public IRelayCommand CancelPaymentCommand { get; }

    public IAsyncRelayCommand CloseCommand { get; }

    public Task<LinklyTerminalDialogAction?> UpdateAsync(
        LinklyTerminalDialogState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return Task.FromResult(UpdateOnUiThread(state));
        }

        return dispatcher.InvokeAsync(() => UpdateOnUiThread(state)).Task;
    }

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            CloseOnUiThread();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(CloseOnUiThread).Task;
    }

    private LinklyTerminalDialogAction? UpdateOnUiThread(LinklyTerminalDialogState state)
    {
        _mode = state.Mode;
        IsOpen = true;
        SessionId = state.SessionId;
        StatusText = state.Status;
        DisplayText = state.DisplayText;
        ReceiptText = state.ReceiptText;
        ResponseText = state.ResponseText;
        MessageText = state.Message;
        IsInteractive = state.IsInteractive;
        IsFinal = state.IsFinal;

        DisplayButtons.Clear();
        foreach (var button in state.DisplayButtons ?? [])
        {
            DisplayButtons.Add(new LinklyTerminalDialogButtonViewModel(
                button,
                _localization.T(button.TextResourceKey)));
        }

        RaiseDialogButtonStateChanged();

        // 页面弹窗和轮询线程之间只传递一次待发送按键，避免重复 sendkey。
        var action = _pendingAction;
        _pendingAction = null;
        SubmitActionCommand.NotifyCanExecuteChanged();
        return action;
    }

    private void SubmitAction(LinklyTerminalDialogButtonViewModel? button)
    {
        if (button is null || string.IsNullOrWhiteSpace(button.Key))
        {
            return;
        }

        _pendingAction = new LinklyTerminalDialogAction(button.Key, button.Data);
    }

    private void SubmitCancelPayment()
    {
        if (!IsCancelPaymentVisible)
        {
            return;
        }

        // 只有 direct 模式保留独立取消；backend async 取消按钮必须来自 Linkly display flag。
        _pendingAction = new LinklyTerminalDialogAction(LinklyTerminalDialogKeys.OkCancel, null);
    }

    private Task CloseOrCancelAsync()
    {
        if (IsCancelPaymentVisible)
        {
            // direct 模式沿用 OK/CANCEL sendkey；backend async 关闭只收起本地弹窗，不误发 Key=0。
            SubmitCancelPayment();
            return Task.CompletedTask;
        }

        return CloseAsync(CancellationToken.None);
    }

    private void CloseOnUiThread()
    {
        _pendingAction = null;
        _mode = LinklyTerminalDialogMode.CloudBackendInteractive;
        IsOpen = false;
        SessionId = null;
        StatusText = string.Empty;
        DisplayText = null;
        ReceiptText = null;
        ResponseText = null;
        MessageText = null;
        IsInteractive = false;
        IsFinal = false;
        DisplayButtons.Clear();
        SubmitActionCommand.NotifyCanExecuteChanged();
        RaiseDialogButtonStateChanged();
    }

    private void RaiseLocalizedTextChanged()
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(StatusLabelText));
        OnPropertyChanged(nameof(DisplayLabelText));
        OnPropertyChanged(nameof(ReceiptLabelText));
        OnPropertyChanged(nameof(CancelPaymentText));
        foreach (var button in DisplayButtons)
        {
            button.RefreshText(_localization);
        }
    }

    private void RaiseDialogButtonStateChanged()
    {
        OnPropertyChanged(nameof(IsCloseButtonVisible));
        OnPropertyChanged(nameof(IsCancelPaymentVisible));
        CancelPaymentCommand.NotifyCanExecuteChanged();
    }
}
