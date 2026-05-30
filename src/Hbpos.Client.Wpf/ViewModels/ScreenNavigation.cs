using Hbpos.Client.Wpf.Models;

namespace Hbpos.Client.Wpf.ViewModels;

public interface IScreenNavigation
{
    void OpenCashPayment(PosCartServiceSnapshot cartSnapshot);

    void OpenInstallmentCenter(PosCartServiceSnapshot? cartSnapshot);

    void OpenInstallmentCreate(PosCartServiceSnapshot? cartSnapshot);

    void PaymentSuccess(LocalOrder order);
}

public sealed record PosCartServiceSnapshot(
    decimal TotalAmount,
    decimal DiscountAmount,
    decimal ActualAmount,
    IReadOnlyList<PosCartLineServiceSnapshot> Lines)
{
    public PosCartServiceSnapshot(decimal totalAmount, decimal discountAmount, decimal actualAmount)
        : this(totalAmount, discountAmount, actualAmount, [])
    {
    }
}

public sealed record PosCartLineServiceSnapshot(
    string ProductCode,
    string? ReferenceCode,
    string DisplayName,
    string LookupCode,
    string? ItemNumber,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal ActualAmount);

public sealed class PaymentCompletedEventArgs(LocalOrder order, decimal tenderedAmount, decimal changeAmount) : EventArgs
{
    public LocalOrder Order { get; } = order;

    public decimal TenderedAmount { get; } = tenderedAmount;

    public decimal ChangeAmount { get; } = changeAmount;
}
