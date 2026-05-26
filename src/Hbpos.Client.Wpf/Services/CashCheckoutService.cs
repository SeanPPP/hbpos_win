using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public sealed class CashCheckoutService
{
    public CashCheckoutResult CreateCashOrder(PosCartService cart, PosSessionState session, decimal tenderedAmount)
    {
        var result = CreatePaymentOrder(
            cart,
            session,
            [new PaymentTender(PaymentMethodKind.Cash, tenderedAmount)],
            tenderedAmount);

        return new CashCheckoutResult(result.Order, result.TenderedAmount, result.ChangeAmount);
    }

    public PaymentCheckoutResult CreatePaymentOrder(
        PosCartService cart,
        PosSessionState session,
        IReadOnlyList<PaymentTender> tenders,
        decimal cashTenderedAmount)
    {
        if (cart.IsEmpty)
        {
            throw new InvalidOperationException("Cart is empty.");
        }

        if (cart.HasNonIntegerQuantity)
        {
            throw new InvalidOperationException("Cart item quantity must be a positive integer.");
        }

        if (cart.HasReturnLine)
        {
            throw new InvalidOperationException("Refund checkout is not implemented yet.");
        }

        if (cart.HasZeroPriceLine)
        {
            throw new InvalidOperationException("Cart contains a zero-price item.");
        }

        if (tenders.Count == 0)
        {
            throw new InvalidOperationException("At least one payment tender is required.");
        }

        var paymentTotal = decimal.Round(tenders.Sum(tender => tender.Amount), 2, MidpointRounding.AwayFromZero);
        if (paymentTotal < cart.ActualAmount)
        {
            throw new InvalidOperationException("Payment amount cannot be less than amount due.");
        }

        var nonCashTotal = tenders
            .Where(tender => tender.Method != PaymentMethodKind.Cash)
            .Sum(tender => tender.Amount);
        if (nonCashTotal > cart.ActualAmount)
        {
            throw new InvalidOperationException("Non-cash payments cannot exceed amount due.");
        }

        var changeAmount = decimal.Round(paymentTotal - cart.ActualAmount, 2, MidpointRounding.AwayFromZero);
        if (changeAmount > cashTenderedAmount)
        {
            throw new InvalidOperationException("Only cash payments can exceed amount due.");
        }

        if (tenders.Any(tender => tender.Amount <= 0m))
        {
            throw new InvalidOperationException("Payment tender amounts must be greater than zero.");
        }

        var lines = cart.Lines
            .Select(line => new LocalOrderLine(
                Guid.NewGuid(),
                line.ProductCode,
                line.ReferenceCode,
                line.DisplayName,
                line.LookupCode,
                line.ItemNumber,
                line.Quantity,
                line.UnitPrice,
                line.DiscountAmount,
                line.ActualAmount,
                line.PriceSource))
            .ToList();

        var order = new LocalOrder(
            Guid.NewGuid(),
            session.StoreCode,
            session.DeviceCode,
            session.CashierId,
            session.CashierName,
            DateTimeOffset.Now,
            cart.TotalAmount,
            cart.DiscountAmount,
            cart.ActualAmount,
            lines,
            AllocatePayments(cart.ActualAmount, tenders));

        return new PaymentCheckoutResult(
            order,
            cashTenderedAmount,
            changeAmount);
    }

    private static IReadOnlyList<LocalPayment> AllocatePayments(
        decimal actualAmount,
        IReadOnlyList<PaymentTender> tenders)
    {
        var remainingAmount = decimal.Round(actualAmount, 2, MidpointRounding.AwayFromZero);
        var payments = new List<LocalPayment>(tenders.Count);

        foreach (var tender in tenders)
        {
            if (remainingAmount <= 0m)
            {
                break;
            }

            var appliedAmount = Math.Min(remainingAmount, tender.Amount);
            if (appliedAmount <= 0m)
            {
                continue;
            }

            payments.Add(new LocalPayment(
                Guid.NewGuid(),
                tender.Method,
                decimal.Round(appliedAmount, 2, MidpointRounding.AwayFromZero),
                tender.Reference,
                tender.CardTransactions));
            remainingAmount = decimal.Round(remainingAmount - appliedAmount, 2, MidpointRounding.AwayFromZero);
        }

        return payments;
    }
}
