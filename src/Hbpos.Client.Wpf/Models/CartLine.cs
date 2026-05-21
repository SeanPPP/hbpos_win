using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Models;

public sealed class CartLine
{
    public CartLine(SellableItemDto item)
    {
        Quantity = Math.Max(1m, item.QuantityFactor);
        UpdateFrom(item);
    }

    public string StoreCode { get; private set; } = string.Empty;

    public string ProductCode { get; private set; } = string.Empty;

    public string? ReferenceCode { get; private set; }

    public string DisplayName { get; private set; } = string.Empty;

    public string LookupCode { get; private set; } = string.Empty;

    public string LookupCodeNormalized { get; private set; } = string.Empty;

    public decimal Quantity { get; private set; }

    public decimal UnitPrice { get; private set; }

    public decimal DiscountAmount { get; private set; }

    public decimal GrossAmount => decimal.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);

    public decimal ActualAmount => decimal.Round((Quantity * UnitPrice) - DiscountAmount, 2, MidpointRounding.AwayFromZero);

    public bool HasDiscount => DiscountAmount > 0m && GrossAmount > 0m;

    public string DiscountRateText
    {
        get
        {
            if (!HasDiscount)
            {
                return string.Empty;
            }

            var rate = DiscountAmount / GrossAmount;
            return $"-{rate:P0}";
        }
    }

    public PriceSourceKind PriceSource { get; private set; }

    public string PriceSourceLabel { get; private set; } = string.Empty;

    public void Increase(decimal quantity)
    {
        Quantity += Math.Max(1m, quantity);
    }

    public void UpdateFrom(SellableItemDto item)
    {
        StoreCode = item.StoreCode;
        ProductCode = item.ProductCode;
        ReferenceCode = item.ReferenceCode;
        DisplayName = item.DisplayName;
        LookupCode = item.LookupCode;
        LookupCodeNormalized = NormalizeLookupCode(item.LookupCode);
        UnitPrice = item.RetailPrice;
        PriceSource = item.PriceSource;
        PriceSourceLabel = item.PriceSourceLabel;
    }

    public static string NormalizeLookupCode(string? lookupCode)
    {
        return (lookupCode ?? string.Empty).Trim().ToUpperInvariant();
    }
}
