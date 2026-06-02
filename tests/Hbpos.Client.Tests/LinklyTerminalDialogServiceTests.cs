using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class LinklyTerminalDialogServiceTests
{
    [Fact]
    public async Task UpdateAsync_opens_page_presenter_and_maps_state()
    {
        var service = new WpfLinklyTerminalDialogService(new LocalizationService());
        var button = new LinklyTerminalDialogButton("linkly.backend.dialog.button.ok", LinklyTerminalDialogKeys.OkCancel);

        var action = await service.UpdateAsync(
            new LinklyTerminalDialogState(
                "session-1",
                "Pending",
                "PRESENT CARD",
                "MERCHANT RECEIPT",
                "APPROVED",
                1,
                200,
                "Waiting",
                DisplayButtons: [button]),
            CancellationToken.None);

        Assert.Null(action);
        Assert.True(service.IsOpen);
        Assert.Equal("session-1", service.SessionId);
        Assert.Equal("Pending", service.StatusText);
        Assert.Equal("PRESENT CARD", service.DisplayText);
        Assert.Equal("MERCHANT RECEIPT", service.ReceiptText);
        Assert.Equal("APPROVED", service.ResponseText);
        Assert.Equal("Waiting", service.MessageText);
        var displayButton = Assert.Single(service.DisplayButtons);
        Assert.Equal(button, displayButton.Source);
        Assert.Equal("OK", displayButton.Text);
        Assert.True(service.IsCloseButtonVisible);
        Assert.True(service.IsCancelPaymentVisible);
        Assert.Equal("Cancel payment", service.CancelPaymentText);
    }

    [Fact]
    public async Task UpdateAsync_shows_close_button_for_cloud_direct_status()
    {
        var service = new WpfLinklyTerminalDialogService(new LocalizationService());

        await service.UpdateAsync(
            new LinklyTerminalDialogState(
                "direct-session-1",
                "Pending",
                "WAITING",
                null,
                null,
                0,
                null,
                "Cloud direct status",
                Mode: LinklyTerminalDialogMode.CloudDirectStatus,
                IsInteractive: true,
                IsFinal: false),
            CancellationToken.None);

        Assert.True(service.IsCloseButtonVisible);
        Assert.True(service.IsCancelPaymentVisible);
    }

    [Fact]
    public async Task UpdateAsync_consumes_cloud_direct_cancel_payment_action_once()
    {
        var service = new WpfLinklyTerminalDialogService(new LocalizationService());
        var state = new LinklyTerminalDialogState(
            "direct-session-1",
            "Pending",
            "WAITING",
            null,
            null,
            0,
            null,
            "Cloud direct status",
            Mode: LinklyTerminalDialogMode.CloudDirectStatus,
            IsInteractive: true,
            IsFinal: false);

        await service.UpdateAsync(state, CancellationToken.None);
        service.CancelPaymentCommand.Execute(null);

        var action = await service.UpdateAsync(state, CancellationToken.None);
        var nextAction = await service.UpdateAsync(state, CancellationToken.None);

        Assert.NotNull(action);
        Assert.Equal(LinklyTerminalDialogKeys.OkCancel, action.Key);
        Assert.Null(action.Data);
        Assert.Null(nextAction);
    }

    [Fact]
    public async Task UpdateAsync_hides_cancel_payment_for_final_cloud_direct_status()
    {
        var service = new WpfLinklyTerminalDialogService(new LocalizationService());

        await service.UpdateAsync(
            new LinklyTerminalDialogState(
                "direct-session-final",
                "Completed",
                "APPROVED",
                null,
                "APPROVED",
                0,
                null,
                null,
                Mode: LinklyTerminalDialogMode.CloudDirectStatus,
                IsInteractive: false,
                IsFinal: true),
            CancellationToken.None);

        Assert.True(service.IsCloseButtonVisible);
        Assert.False(service.IsCancelPaymentVisible);
    }

    [Fact]
    public async Task UpdateAsync_consumes_submitted_action_once()
    {
        var service = new WpfLinklyTerminalDialogService(new LocalizationService());
        var button = new LinklyTerminalDialogButton("linkly.backend.dialog.button.ok", LinklyTerminalDialogKeys.OkCancel);
        var state = new LinklyTerminalDialogState(
            "session-1",
            "Pending",
            "PRESS OK",
            null,
            null,
            0,
            null,
            null,
            DisplayButtons: [button]);

        await service.UpdateAsync(state, CancellationToken.None);
        service.SubmitActionCommand.Execute(Assert.Single(service.DisplayButtons));

        var action = await service.UpdateAsync(state, CancellationToken.None);
        var nextAction = await service.UpdateAsync(state, CancellationToken.None);

        Assert.Equal(LinklyTerminalDialogKeys.OkCancel, action?.Key);
        Assert.Null(nextAction);
    }

    [Fact]
    public async Task UpdateAsync_consumes_cancel_payment_action_once()
    {
        var service = new WpfLinklyTerminalDialogService(new LocalizationService());
        var state = new LinklyTerminalDialogState(
            "session-1",
            "Pending",
            "PRESENT CARD",
            null,
            null,
            0,
            null,
            null,
            Mode: LinklyTerminalDialogMode.CloudBackendInteractive,
            IsInteractive: true,
            IsFinal: false);

        await service.UpdateAsync(state, CancellationToken.None);
        service.CancelPaymentCommand.Execute(null);

        var action = await service.UpdateAsync(state, CancellationToken.None);
        var nextAction = await service.UpdateAsync(state, CancellationToken.None);

        Assert.Equal(LinklyTerminalDialogKeys.OkCancel, action?.Key);
        Assert.Null(nextAction);
    }

    [Fact]
    public async Task CloseCommand_sends_cancel_action_while_dialog_is_interactive()
    {
        var service = new WpfLinklyTerminalDialogService(new LocalizationService());
        var state = new LinklyTerminalDialogState(
            "session-1",
            "Pending",
            "PRESENT CARD",
            null,
            null,
            0,
            null,
            null,
            Mode: LinklyTerminalDialogMode.CloudBackendInteractive,
            IsInteractive: true,
            IsFinal: false);

        await service.UpdateAsync(state, CancellationToken.None);
        await service.CloseCommand.ExecuteAsync(null);

        var action = await service.UpdateAsync(state, CancellationToken.None);
        var nextAction = await service.UpdateAsync(state, CancellationToken.None);

        Assert.True(service.IsOpen);
        Assert.Equal(LinklyTerminalDialogKeys.OkCancel, action?.Key);
        Assert.Null(nextAction);
    }

    [Fact]
    public async Task CloseCommand_hides_dialog_when_state_is_final()
    {
        var service = new WpfLinklyTerminalDialogService(new LocalizationService());
        await service.UpdateAsync(
            new LinklyTerminalDialogState(
                "session-final",
                "Completed",
                "APPROVED",
                null,
                "APPROVED",
                0,
                null,
                null,
                IsInteractive: false,
                IsFinal: true),
            CancellationToken.None);

        await service.CloseCommand.ExecuteAsync(null);

        Assert.False(service.IsOpen);
        Assert.Null(service.SessionId);
        Assert.False(service.IsCloseButtonVisible);
        Assert.False(service.IsCancelPaymentVisible);
    }

    [Fact]
    public async Task CultureChanged_refreshes_dialog_labels_and_buttons()
    {
        var localization = new LocalizationService();
        var service = new WpfLinklyTerminalDialogService(localization);
        await service.UpdateAsync(
            new LinklyTerminalDialogState(
                "session-1",
                "Pending",
                "PRESENT CARD",
                null,
                null,
                0,
                null,
                null,
                DisplayButtons: [new LinklyTerminalDialogButton("linkly.backend.dialog.button.authoriseSignature", LinklyTerminalDialogKeys.Auth)]),
            CancellationToken.None);

        localization.SetCulture("zh-CN");

        Assert.Equal("ANZ Linkly 刷卡机", service.TitleText);
        Assert.Equal("刷卡机显示", service.DisplayLabelText);
        Assert.Equal("取消刷卡", service.CancelPaymentText);
        Assert.Equal("授权/签名通过", Assert.Single(service.DisplayButtons).Text);
    }

    [Fact]
    public async Task CloseAsync_hides_presenter_and_clears_state()
    {
        var service = new WpfLinklyTerminalDialogService(new LocalizationService());
        await service.UpdateAsync(
            new LinklyTerminalDialogState(
                "session-1",
                "Pending",
                "PRESENT CARD",
                null,
                null,
                0,
                null,
                null,
                DisplayButtons: [new LinklyTerminalDialogButton("linkly.backend.dialog.button.ok", LinklyTerminalDialogKeys.OkCancel)]),
            CancellationToken.None);

        await service.CloseAsync(CancellationToken.None);

        Assert.False(service.IsOpen);
        Assert.Null(service.SessionId);
        Assert.Empty(service.StatusText);
        Assert.Empty(service.DisplayButtons);
        Assert.False(service.IsCloseButtonVisible);
        Assert.False(service.IsCancelPaymentVisible);
    }
}
