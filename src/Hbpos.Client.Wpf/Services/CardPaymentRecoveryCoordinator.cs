using Hbpos.Client.Wpf.Models;

namespace Hbpos.Client.Wpf.Services;

public sealed class CardPaymentRecoveryCoordinator(
    ICardTerminalSettingsProvider settingsProvider,
    CardPaymentRecoveryService linklyRecoveryService,
    ISquarePaymentRecoveryService squareRecoveryService) : ICardPaymentRecoveryService
{
    public async Task<CardPaymentRecoveryResult> RecoverLatestAsync(
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        return settings.Processor switch
        {
            CardProcessorKind.Linkly => await linklyRecoveryService.RecoverLatestAsync(cart, session, cancellationToken),
            CardProcessorKind.Square => await squareRecoveryService.RecoverLatestAsync(cart, session, cancellationToken),
            _ => CardPaymentRecoveryResult.None
        };
    }
}
