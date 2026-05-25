using System.Globalization;
using Hbpos.Client.Wpf.Models;

namespace Hbpos.Client.Wpf.Services;

public interface ICashPaymentWorkflowService
{
    bool TryParseTenderedAmount(string? amountTenderedText, out decimal tenderedAmount);

    decimal CalculateChange(string? amountTenderedText, decimal actualAmount);

    Task<CashPaymentWorkflowResult> CompleteAsync(
        PosCartService cart,
        PosSessionState session,
        string? amountTenderedText,
        CancellationToken cancellationToken = default);
}

public sealed class CashPaymentWorkflowService(
    CashCheckoutService checkout,
    ILocalOrderRepository orderRepository,
    ISyncQueueRepository syncQueueRepository) : ICashPaymentWorkflowService
{
    public bool TryParseTenderedAmount(string? amountTenderedText, out decimal tenderedAmount)
    {
        if (string.IsNullOrWhiteSpace(amountTenderedText))
        {
            tenderedAmount = 0m;
            return false;
        }

        return decimal.TryParse(amountTenderedText, NumberStyles.Number, CultureInfo.CurrentCulture, out tenderedAmount)
            || decimal.TryParse(amountTenderedText, NumberStyles.Number, CultureInfo.InvariantCulture, out tenderedAmount);
    }

    public decimal CalculateChange(string? amountTenderedText, decimal actualAmount)
    {
        return TryParseTenderedAmount(amountTenderedText, out var tenderedAmount)
            ? decimal.Round(tenderedAmount - actualAmount, 2, MidpointRounding.AwayFromZero)
            : 0m;
    }

    public async Task<CashPaymentWorkflowResult> CompleteAsync(
        PosCartService cart,
        PosSessionState session,
        string? amountTenderedText,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseTenderedAmount(amountTenderedText, out var tenderedAmount))
        {
            throw new InvalidOperationException("Tendered amount is invalid.");
        }

        var result = checkout.CreateCashOrder(cart, session, tenderedAmount);
        await orderRepository.SavePendingOrderAsync(result.Order, cancellationToken);

        cart.Clear();

        var pendingSyncCount = await syncQueueRepository.CountPendingAsync(cancellationToken);
        var updatedSession = session with { PendingSyncCount = pendingSyncCount };

        return new CashPaymentWorkflowResult(
            result.Order,
            result.TenderedAmount,
            result.ChangeAmount,
            pendingSyncCount,
            updatedSession);
    }
}

public sealed record CashPaymentWorkflowResult(
    LocalOrder Order,
    decimal TenderedAmount,
    decimal ChangeAmount,
    int PendingSyncCount,
    PosSessionState UpdatedSession);
