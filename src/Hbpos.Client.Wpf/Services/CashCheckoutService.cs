using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public sealed class CashCheckoutService
{
    private readonly CashRoundingPolicy _cashRoundingPolicy = new();

    public CashCheckoutResult CreateCashOrder(PosCartService cart, PosSessionState session, decimal tenderedAmount)
    {
        var signedTenderAmount = cart.ActualAmount < 0m ? -tenderedAmount : tenderedAmount;
        var result = CreatePaymentOrder(
            cart,
            session,
            [new PaymentTender(PaymentMethodKind.Cash, signedTenderAmount)],
            signedTenderAmount);

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

        if (cart.HasZeroPriceLine)
        {
            throw new InvalidOperationException("Cart contains a zero-price item.");
        }

        var normalizedTenders = tenders
            .Select(NormalizeTender)
            .ToList();
        var actualAmount = RoundCurrency(cart.ActualAmount);
        var isRefund = actualAmount < 0m;
        if (actualAmount == 0m)
        {
            if (normalizedTenders.Count > 0)
            {
                throw new InvalidOperationException("Zero-total orders cannot include payment tenders.");
            }

            return CreateResult(cart, session, [], tenderedAmount: 0m, changeAmount: 0m);
        }

        if (normalizedTenders.Count == 0)
        {
            throw new InvalidOperationException("At least one payment tender is required.");
        }

        if (isRefund)
        {
            if (normalizedTenders.Any(tender => tender.Amount >= 0m))
            {
                throw new InvalidOperationException("Refund tender amounts must be less than zero.");
            }
        }
        else if (normalizedTenders.Any(tender => tender.Amount <= 0m))
        {
            throw new InvalidOperationException("Payment tender amounts must be greater than zero.");
        }

        var paymentTotal = RoundCurrency(normalizedTenders.Sum(tender => tender.Amount));
        var nonCashTotal = RoundCurrency(normalizedTenders
            .Where(tender => tender.Method != PaymentMethodKind.Cash)
            .Sum(tender => tender.Amount));
        var cashTenderedTotal = RoundCurrency(normalizedTenders
            .Where(tender => tender.Method == PaymentMethodKind.Cash)
            .Sum(tender => tender.Amount));
        var hasCashTender = isRefund ? cashTenderedTotal < 0m : cashTenderedTotal > 0m;

        decimal requiredPaymentTotal;
        decimal normalizedCashTenderedAmount;
        decimal changeAmount;

        if (isRefund)
        {
            var refundAmount = Math.Abs(actualAmount);
            var nonCashRefundTotal = Math.Abs(nonCashTotal);
            if (nonCashRefundTotal > refundAmount)
            {
                throw new InvalidOperationException("Non-cash refunds cannot exceed amount refundable.");
            }

            var roundedCashRefund = hasCashTender
                ? _cashRoundingPolicy.CalculateRoundedCashDue(refundAmount, nonCashRefundTotal)
                : 0m;
            requiredPaymentTotal = hasCashTender
                ? -RoundCurrency(nonCashRefundTotal + roundedCashRefund)
                : actualAmount;
            if (paymentTotal != requiredPaymentTotal)
            {
                throw new InvalidOperationException("Refund amount must equal the amount refundable.");
            }

            normalizedCashTenderedAmount = hasCashTender
                ? cashTenderedTotal
                : -_cashRoundingPolicy.NormalizeCashTender(Math.Abs(cashTenderedAmount));
            changeAmount = 0m;
        }
        else
        {
            if (nonCashTotal > actualAmount)
            {
                throw new InvalidOperationException("Non-cash payments cannot exceed amount due.");
            }

            var roundedCashDue = hasCashTender
                ? _cashRoundingPolicy.CalculateRoundedCashDue(actualAmount, nonCashTotal)
                : 0m;
            requiredPaymentTotal = hasCashTender
                ? RoundCurrency(nonCashTotal + roundedCashDue)
                : actualAmount;
            if (paymentTotal < requiredPaymentTotal)
            {
                throw new InvalidOperationException("Payment amount cannot be less than amount due.");
            }

            normalizedCashTenderedAmount = hasCashTender
                ? cashTenderedTotal
                : _cashRoundingPolicy.NormalizeCashTender(cashTenderedAmount);
            changeAmount = RoundCurrency(paymentTotal - requiredPaymentTotal);
            if (changeAmount > normalizedCashTenderedAmount)
            {
                throw new InvalidOperationException("Only cash payments can exceed amount due.");
            }
        }

        return CreateResult(
            cart,
            session,
            AllocatePayments(actualAmount, BuildOrderTendersForAllocation(actualAmount, normalizedTenders)),
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
            if ((actualAmount > 0m && remainingAmount <= 0m) ||
                (actualAmount < 0m && remainingAmount >= 0m))
            {
                break;
            }

            var appliedAmount = actualAmount < 0m
                ? Math.Max(remainingAmount, tender.Amount)
                : Math.Min(remainingAmount, tender.Amount);
            if ((actualAmount > 0m && appliedAmount <= 0m) ||
                (actualAmount < 0m && appliedAmount >= 0m))
            {
                continue;
            }

            payments.Add(new LocalPayment(
                Guid.NewGuid(),
                tender.Method,
                decimal.Round(appliedAmount, 2, MidpointRounding.AwayFromZero),
                tender.Reference,
                tender.CardTransactions,
                tender.IdempotencyKey));
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
        if (actualAmount < 0m)
        {
            return normalizedTenders;
        }

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

    private PaymentCheckoutResult CreateResult(
        PosCartService cart,
        PosSessionState session,
        IReadOnlyList<LocalPayment> payments,
        decimal tenderedAmount,
        decimal changeAmount)
    {
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
                line.PriceSource,
                line.IsReturnLine ? OrderLineKind.Return : OrderLineKind.Sale,
                string.IsNullOrWhiteSpace(line.ReturnSourceKey) ? null : line.ReturnSourceKey,
                line.OriginalOrderGuid,
                line.OriginalOrderLineGuid))
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
            payments,
            // 将本次收款与找零固化到订单头，供支付成功页和历史小票复用。
            tenderedAmount,
            changeAmount);

        return new PaymentCheckoutResult(order, tenderedAmount, changeAmount);
    }

    private static decimal RoundCurrency(decimal amount)
    {
        return decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
    }
}
