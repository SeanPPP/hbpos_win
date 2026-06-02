using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hbpos.Client.Wpf.Localization;
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
        string receiptLabel)
    {
        Title = title;
        StatusLabelTextBlock.Text = statusLabel;
        DisplayLabelTextBlock.Text = displayLabel;
        ReceiptLabelTextBlock.Text = receiptLabel;
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
        UpdateButtons(state);
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

        return string.Join(" / ", details);
    }

    private void UpdateButtons(LinklyTerminalDialogState state)
    {
        ActionButtonsPanel.Children.Clear();

        if (state.IsFinal)
        {
            var closeButton = new Button
            {
                Content = LocalizationResourceProvider.Instance["linkly.backend.dialog.close"],
                MinWidth = 96,
                Margin = new Thickness(10, 0, 0, 0)
            };
            closeButton.Click += CloseButton_Click;
            ActionButtonsPanel.Visibility = Visibility.Visible;
            ActionButtonsPanel.Children.Add(closeButton);
            return;
        }

        var buttons = state.IsInteractive && !state.IsFinal
            ? state.DisplayButtons ?? []
            : [];
        if (buttons.Count == 0)
        {
            ActionButtonsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ActionButtonsPanel.Visibility = Visibility.Visible;
        foreach (var actionButton in buttons)
        {
            var button = new Button
            {
                Content = LocalizationResourceProvider.Instance[actionButton.TextResourceKey],
                MinWidth = 96,
                Margin = new Thickness(10, 0, 0, 0),
                Tag = actionButton
            };
            if (actionButton.IsDestructive)
            {
                button.Foreground = Brushes.DarkRed;
            }

            // Linkly 按键由后端 display flags 决定，窗口只把用户点击转换成 sendkey 动作。
            button.Click += ActionButton_Click;
            ActionButtonsPanel.Children.Add(button);
        }
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LinklyTerminalDialogButton button })
        {
            ActionRequested?.Invoke(this, new LinklyTerminalDialogAction(button.Key, button.Data));
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
