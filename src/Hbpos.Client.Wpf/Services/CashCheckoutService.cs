using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public sealed class CashCheckoutService
{
    public CashCheckoutResult CreateCashOrder(PosCartService cart, PosSessionState session, decimal tenderedAmount)
    {
        if (cart.IsEmpty)
        {
            throw new InvalidOperationException("购物车为空，不能收款。");
        }

        if (cart.HasNonIntegerQuantity)
        {
            throw new InvalidOperationException("商品数量必须为正整数。");
        }

        if (cart.HasZeroPriceLine)
        {
            throw new InvalidOperationException("购物车存在价格为 0 的商品，不能收款。");
        }

        if (tenderedAmount < cart.ActualAmount)
        {
            throw new InvalidOperationException("实收金额不能小于应收金额。");
        }

        var lines = cart.Lines
            .Select(line => new LocalOrderLine(
                Guid.NewGuid(),
                line.ProductCode,
                line.ReferenceCode,
                line.DisplayName,
                line.LookupCode,
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

        return new CashCheckoutResult(order, tenderedAmount, decimal.Round(tenderedAmount - cart.ActualAmount, 2, MidpointRounding.AwayFromZero));
    }
}
