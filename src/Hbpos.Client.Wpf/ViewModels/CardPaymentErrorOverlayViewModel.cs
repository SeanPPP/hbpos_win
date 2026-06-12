using CommunityToolkit.Mvvm.ComponentModel;

namespace Hbpos.Client.Wpf.ViewModels;

public partial class CardPaymentErrorOverlayViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _titleKey = string.Empty;

    [ObservableProperty]
    private string _messageKey = string.Empty;

    [ObservableProperty]
    private string _buttonTextKey = "payment.card.error.overlay.close";

    [ObservableProperty]
    private bool _hasPrimaryAction;

    [ObservableProperty]
    private string _primaryButtonTextKey = string.Empty;

    public static CardPaymentErrorOverlayViewModel ConnectionFailed()
        => new()
        {
            TitleKey = "payment.card.error.overlay.connectionFailed.title",
            MessageKey = "payment.card.error.overlay.connectionFailed.message"
        };

    public static CardPaymentErrorOverlayViewModel CloudCommunicationFailed()
        => new()
        {
            TitleKey = "payment.card.error.overlay.cloudCommunicationFailed.title",
            MessageKey = "payment.card.error.overlay.cloudCommunicationFailed.message"
        };

    public static CardPaymentErrorOverlayViewModel Timeout()
        => new()
        {
            TitleKey = "payment.card.error.overlay.timeout.title",
            MessageKey = "payment.card.error.overlay.timeout.message"
        };

    public static CardPaymentErrorOverlayViewModel SquareCommunicationFailed()
        => new()
        {
            TitleKey = "payment.card.error.overlay.squareCommunicationFailed.title",
            MessageKey = "payment.card.error.overlay.squareCommunicationFailed.message"
        };

    public static CardPaymentErrorOverlayViewModel Unexpected()
        => new()
        {
            TitleKey = "payment.card.error.overlay.unexpected.title",
            MessageKey = "payment.card.error.overlay.unexpected.message"
        };

    public static CardPaymentErrorOverlayViewModel ActiveSessionRequiresRecovery()
        => new()
        {
            TitleKey = "payment.card.error.overlay.activeSession.title",
            MessageKey = "payment.card.error.overlay.activeSession.message",
            ButtonTextKey = "payment.card.error.overlay.activeSession.close",
            HasPrimaryAction = true,
            PrimaryButtonTextKey = "payment.card.error.overlay.activeSession.recover"
        };
}
