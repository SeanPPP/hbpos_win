using CommunityToolkit.Mvvm.ComponentModel;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Models;

public sealed class CartLine : ObservableObject
{
    private string _storeCode = string.Empty;
    private string _productCode = string.Empty;
    private string? _itemNumber;
    private string? _referenceCode;
    private string _displayName = string.Empty;
    private string _lookupCode = string.Empty;
    private string _lookupCodeNormalized = string.Empty;
    private decimal _quantity;
    private decimal _unitPrice;
    private decimal _discountAmount;
    private PriceSourceKind _priceSource;
    private string _priceSourceLabel = string.Empty;

    public CartLine(SellableItemDto item)
    {
        Quantity = Math.Max(1m, item.QuantityFactor);
        UpdateFrom(item);
    }

    public string StoreCode
    {
        get => _storeCode;
        private set => SetProperty(ref _storeCode, value);
    }

    public string ProductCode
    {
        get => _productCode;
        private set => SetProperty(ref _productCode, value);
    }

    public string? ItemNumber
    {
        get => _itemNumber;
        private set => SetProperty(ref _itemNumber, value);
    }

    public string? ReferenceCode
    {
        get => _referenceCode;
        private set => SetProperty(ref _referenceCode, value);
    }

    public string DisplayName
    {
        get => _displayName;
        private set => SetProperty(ref _displayName, value);
    }

    public string LookupCode
    {
        get => _lookupCode;
        private set => SetProperty(ref _lookupCode, value);
    }

    public string LookupCodeNormalized
    {
        get => _lookupCodeNormalized;
        private set => SetProperty(ref _lookupCodeNormalized, value);
    }

    public decimal Quantity
    {
        get => _quantity;
        private set
        {
            if (SetProperty(ref _quantity, value))
            {
                OnAmountPropertiesChanged();
            }
        }
    }

    public decimal UnitPrice
    {
        get => _unitPrice;
        private set
        {
            if (SetProperty(ref _unitPrice, value))
            {
                OnAmountPropertiesChanged();
            }
        }
    }

    public decimal DiscountAmount
    {
        get => _discountAmount;
        private set
        {
            if (SetProperty(ref _discountAmount, value))
            {
                OnAmountPropertiesChanged();
            }
        }
    }

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

    public PriceSourceKind PriceSource
    {
        get => _priceSource;
        private set => SetProperty(ref _priceSource, value);
    }

    public string PriceSourceLabel
    {
        get => _priceSourceLabel;
        private set => SetProperty(ref _priceSourceLabel, value);
    }

    public void Increase(decimal quantity)
    {
        Quantity += Math.Max(1m, quantity);
    }

    public void UpdateFrom(SellableItemDto item)
    {
        StoreCode = item.StoreCode;
        ProductCode = item.ProductCode;
        ItemNumber = item.ItemNumber;
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

    private void OnAmountPropertiesChanged()
    {
        OnPropertyChanged(nameof(GrossAmount));
        OnPropertyChanged(nameof(ActualAmount));
        OnPropertyChanged(nameof(HasDiscount));
        OnPropertyChanged(nameof(DiscountRateText));
    }
}
