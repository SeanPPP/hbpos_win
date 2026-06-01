using System.Windows;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Views.Windows;

namespace Hbpos.Client.Wpf.Services;

public sealed record LinklyTerminalDialogState(
    string SessionId,
    string Status,
    string? DisplayText,
    string? ReceiptText,
    string? ResponseText,
    int RecoveryCount,
    int? LastHttpStatus,
    string? Message);

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
        // Linkly REST sendkey 只接受官方数字枚举，兼容旧弹窗残留的文本动作。
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

public sealed class WpfLinklyTerminalDialogService(
    ILocalizationService localization) : ILinklyTerminalDialogService
{
    private LinklyTerminalDialogWindow? _window;
    private LinklyTerminalDialogAction? _pendingAction;
    private bool _isClosingProgrammatically;

    public Task<LinklyTerminalDialogAction?> UpdateAsync(
        LinklyTerminalDialogState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return Task.FromResult<LinklyTerminalDialogAction?>(null);
        }

        return dispatcher.CheckAccess()
            ? Task.FromResult(UpdateOnUiThread(state))
            : dispatcher.InvokeAsync(() => UpdateOnUiThread(state)).Task;
    }

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return Task.CompletedTask;
        }

        if (dispatcher.CheckAccess())
        {
            CloseOnUiThread();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(CloseOnUiThread).Task;
    }

    private LinklyTerminalDialogAction? UpdateOnUiThread(LinklyTerminalDialogState state)
    {
        EnsureWindow();
        _window!.ApplyText(
            localization.T("linkly.backend.dialog.title"),
            localization.T("linkly.backend.dialog.status"),
            localization.T("linkly.backend.dialog.display"),
            localization.T("linkly.backend.dialog.receipt"),
            localization.T("linkly.backend.dialog.confirm"),
            localization.T("linkly.backend.dialog.cancel"));
        _window.UpdateState(state);

        // 按钮动作只消费一次，避免下一轮状态轮询重复发送 sendkey。
        var action = _pendingAction;
        _pendingAction = null;
        return action;
    }

    private void EnsureWindow()
    {
        if (_window is not null)
        {
            return;
        }

        _window = new LinklyTerminalDialogWindow();
        if (Application.Current?.MainWindow is { } owner &&
            !ReferenceEquals(owner, _window))
        {
            _window.Owner = owner;
        }

        _window.ActionRequested += OnActionRequested;
        _window.Closed += OnWindowClosed;
        _window.Show();
    }

    private void OnActionRequested(object? sender, LinklyTerminalDialogAction action)
    {
        _pendingAction = action;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (!_isClosingProgrammatically)
        {
            _pendingAction = new LinklyTerminalDialogAction(LinklyTerminalDialogKeys.OkCancel, null);
        }

        if (_window is not null)
        {
            _window.ActionRequested -= OnActionRequested;
            _window.Closed -= OnWindowClosed;
        }

        _window = null;
        _isClosingProgrammatically = false;
    }

    private void CloseOnUiThread()
    {
        if (_window is null)
        {
            return;
        }

        _isClosingProgrammatically = true;
        _pendingAction = null;
        _window.Close();
    }
}
