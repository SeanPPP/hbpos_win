using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public sealed class CashRoundingPolicy
{
    public const decimal CashIncrement = 0.05m;

    public decimal NormalizeCashTender(decimal amount)
    {
        return RoundToNearestIncrement(amount);
    }

    public decimal CalculateRoundedCashDue(decimal amountDue)
    {
        return RoundToNearestIncrement(Math.Max(0m, amountDue));
    }

    public decimal CalculateRoundedCashDue(decimal actualAmount, decimal nonCashAmount)
    {
        var remainingAmount = RoundCurrency(actualAmount - Math.Min(actualAmount, Math.Max(0m, nonCashAmount)));
        return CalculateRoundedCashDue(remainingAmount);
    }

    public decimal CalculateChange(decimal cashTenderedAmount, decimal roundedCashDue)
    {
        return Math.Max(0m, RoundCurrency(cashTenderedAmount - roundedCashDue));
    }

    public static decimal GetCashPayableAmount(decimal actualAmount, IReadOnlyList<PaymentTender> tenders)
    {
        var policy = new CashRoundingPolicy();
        var normalizedTenders = tenders
            .Select(tender => NormalizeTender(tender, policy))
            .ToList();
        var nonCashTotal = RoundCurrency(normalizedTenders
            .Where(tender => tender.Method != PaymentMethodKind.Cash)
            .Sum(tender => tender.Amount));
        var cashTotal = RoundCurrency(normalizedTenders
            .Where(tender => tender.Method == PaymentMethodKind.Cash)
            .Sum(tender => tender.Amount));
        var roundedCashDue = policy.CalculateRoundedCashDue(actualAmount, nonCashTotal);
        return RoundCurrency(Math.Max(0m, roundedCashDue - cashTotal));
    }

    public static decimal CalculateCashChange(
        decimal actualAmount,
        IReadOnlyList<PaymentTender> tenders,
        decimal cashTenderedAmount)
    {
        var normalizedCashTenderedAmount = new CashRoundingPolicy().NormalizeCashTender(cashTenderedAmount);
        var cashPayableAmount = GetCashPayableAmount(actualAmount, tenders);
        return Math.Max(0m, RoundCurrency(normalizedCashTenderedAmount - cashPayableAmount));
    }

    private static decimal RoundToNearestIncrement(decimal amount)
    {
        var steps = decimal.Round(amount / CashIncrement, 0, MidpointRounding.AwayFromZero);
        return RoundCurrency(steps * CashIncrement);
    }

    private static decimal RoundCurrency(decimal amount)
    {
        return decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
    }

    private static PaymentTender NormalizeTender(PaymentTender tender, CashRoundingPolicy policy)
    {
        var normalizedAmount = tender.Method == PaymentMethodKind.Cash
            ? policy.NormalizeCashTender(tender.Amount)
            : RoundCurrency(tender.Amount);
        return tender with { Amount = normalizedAmount };
    }
}
