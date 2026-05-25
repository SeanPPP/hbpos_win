using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public sealed class CashCheckoutService
{
    public CashCheckoutResult CreateCashOrder(PosCartService cart, PosSessionState session, decimal tenderedAmount)
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

        if (tenderedAmount < cart.ActualAmount)
        {
            throw new InvalidOperationException("Tendered amount cannot be less than amount due.");
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
            [new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Cash, cart.ActualAmount, null)]);

        return new CashCheckoutResult(
            order,
            tenderedAmount,
            decimal.Round(tenderedAmount - cart.ActualAmount, 2, MidpointRounding.AwayFromZero));
    }
}
