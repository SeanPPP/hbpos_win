using System.Windows;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.Views.Windows;

public partial class LinklyTerminalDialogWindow : Window
{
    public LinklyTerminalDialogWindow()
    {
        InitializeComponent();
    }

    public event EventHandler<LinklyTerminalDialogAction>? ActionRequested;

    public void ApplyText(
        string title,
        string statusLabel,
        string displayLabel,
        string receiptLabel,
        string confirmText,
        string cancelText)
    {
        Title = title;
        StatusLabelTextBlock.Text = statusLabel;
        DisplayLabelTextBlock.Text = displayLabel;
        ReceiptLabelTextBlock.Text = receiptLabel;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;
    }

    public void UpdateState(LinklyTerminalDialogState state)
    {
        MessageTextBlock.Text = state.Message ?? string.Empty;
        MessageTextBlock.Visibility = string.IsNullOrWhiteSpace(state.Message)
            ? Visibility.Collapsed
            : Visibility.Visible;
        StatusTextBlock.Text = FormatStatus(state);
        DisplayTextBlock.Text = string.IsNullOrWhiteSpace(state.DisplayText)
            ? state.ResponseText ?? state.Status
            : state.DisplayText;
        ReceiptTextBox.Text = state.ReceiptText ?? string.Empty;
    }

    private static string FormatStatus(LinklyTerminalDialogState state)
    {
        var details = new List<string> { state.Status };
        if (state.LastHttpStatus is { } httpStatus)
        {
            details.Add($"HTTP {httpStatus}");
        }

        if (state.RecoveryCount > 0)
        {
            details.Add($"Recovery {state.RecoveryCount}");
        }

        return string.Join(" · ", details);
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        ActionRequested?.Invoke(this, new LinklyTerminalDialogAction(LinklyTerminalDialogKeys.OkCancel, null));
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        ActionRequested?.Invoke(this, new LinklyTerminalDialogAction(LinklyTerminalDialogKeys.OkCancel, null));
    }
}
