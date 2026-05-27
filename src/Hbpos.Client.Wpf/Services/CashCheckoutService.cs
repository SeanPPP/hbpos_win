using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public sealed class CashCheckoutService
{
    private readonly CashRoundingPolicy _cashRoundingPolicy = new();

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

        if (tenders.Any(tender => tender.Amount <= 0m))
        {
            throw new InvalidOperationException("Payment tender amounts must be greater than zero.");
        }

        var normalizedTenders = tenders
            .Select(NormalizeTender)
            .ToList();
        var paymentTotal = RoundCurrency(normalizedTenders.Sum(tender => tender.Amount));
        var nonCashTotal = RoundCurrency(normalizedTenders
            .Where(tender => tender.Method != PaymentMethodKind.Cash)
            .Sum(tender => tender.Amount));
        if (nonCashTotal > cart.ActualAmount)
        {
            throw new InvalidOperationException("Non-cash payments cannot exceed amount due.");
        }

        var cashTenderedTotal = RoundCurrency(normalizedTenders
            .Where(tender => tender.Method == PaymentMethodKind.Cash)
            .Sum(tender => tender.Amount));
        var hasCashTender = cashTenderedTotal > 0m;
        var roundedCashDue = hasCashTender
            ? _cashRoundingPolicy.CalculateRoundedCashDue(cart.ActualAmount, nonCashTotal)
            : 0m;
        var requiredPaymentTotal = hasCashTender
            ? RoundCurrency(nonCashTotal + roundedCashDue)
            : cart.ActualAmount;
        if (paymentTotal < requiredPaymentTotal)
        {
            throw new InvalidOperationException("Payment amount cannot be less than amount due.");
        }

        var normalizedCashTenderedAmount = hasCashTender
            ? cashTenderedTotal
            : _cashRoundingPolicy.NormalizeCashTender(cashTenderedAmount);
        var changeAmount = RoundCurrency(paymentTotal - requiredPaymentTotal);
        if (changeAmount > normalizedCashTenderedAmount)
        {
            throw new InvalidOperationException("Only cash payments can exceed amount due.");
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
            AllocatePayments(cart.ActualAmount, BuildOrderTendersForAllocation(cart.ActualAmount, normalizedTenders)));

        return new PaymentCheckoutResult(
            order,
            normalizedCashTenderedAmount,
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

    private PaymentTender NormalizeTender(PaymentTender tender)
    {
        var normalizedAmount = tender.Method == PaymentMethodKind.Cash
            ? _cashRoundingPolicy.NormalizeCashTender(tender.Amount)
            : RoundCurrency(tender.Amount);
        return tender with { Amount = normalizedAmount };
    }

    private static IReadOnlyList<PaymentTender> BuildOrderTendersForAllocation(
        decimal actualAmount,
        IReadOnlyList<PaymentTender> normalizedTenders)
    {
        var tenderTotal = RoundCurrency(normalizedTenders.Sum(tender => tender.Amount));
        var shortfall = RoundCurrency(actualAmount - tenderTotal);
        if (shortfall <= 0m)
        {
            return normalizedTenders;
        }

        var allocationTenders = normalizedTenders.ToList();
        var cashTenderIndex = allocationTenders.FindLastIndex(tender => tender.Method == PaymentMethodKind.Cash);
        if (cashTenderIndex < 0)
        {
            return normalizedTenders;
        }

        var cashTender = allocationTenders[cashTenderIndex];
        allocationTenders[cashTenderIndex] = cashTender with
        {
            Amount = RoundCurrency(cashTender.Amount + shortfall)
        };
        return allocationTenders;
    }

    private static decimal RoundCurrency(decimal amount)
    {
        return decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
    }
}
