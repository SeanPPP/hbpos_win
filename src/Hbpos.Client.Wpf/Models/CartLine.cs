using CommunityToolkit.Mvvm.ComponentModel;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Models;

public sealed class CartLine : ObservableObject
{
    private string _storeCode = string.Empty;
    private string _productCode = string.Empty;
    private string? _itemNumber;
    private string? _referenceCode;
    private string? _productImage;
    private string _displayName = string.Empty;
    private string _lookupCode = string.Empty;
    private string _lookupCodeNormalized = string.Empty;
    private decimal _quantity;
    private decimal _unitPrice;
    private decimal _discountAmount;
    private decimal? _discountPercent;
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

    public string? ProductImage
    {
        get => _productImage;
        private set => SetProperty(ref _productImage, value);
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
                RefreshDiscountForGrossChange();
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
                RefreshDiscountForGrossChange();
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
            return $"-{rate * 100m:0.##}%";
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

    public bool Decrease(decimal quantity)
    {
        var decreaseBy = Math.Max(1m, quantity);
        if (Quantity <= decreaseBy)
        {
            return false;
        }

        Quantity -= decreaseBy;
        return true;
    }

    public void SetQuantity(decimal quantity)
    {
        Quantity = quantity;
    }

    public void SetUnitPrice(decimal unitPrice)
    {
        UnitPrice = unitPrice;
    }

    public void SetDiscountAmount(decimal discountAmount)
    {
        _discountPercent = null;
        DiscountAmount = ClampDiscountAmount(discountAmount);
    }

    public void SetDiscountPercent(decimal discountPercent)
    {
        _discountPercent = Math.Clamp(discountPercent, 0m, 100m);
        DiscountAmount = CalculateDiscountAmount(_discountPercent.Value);
    }

    public void UpdateFrom(SellableItemDto item)
    {
        StoreCode = item.StoreCode;
        ProductCode = item.ProductCode;
        ItemNumber = item.ItemNumber;
        ReferenceCode = item.ReferenceCode;
        ProductImage = item.ProductImage;
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

    private void RefreshDiscountForGrossChange()
    {
        DiscountAmount = _discountPercent is decimal discountPercent
            ? CalculateDiscountAmount(discountPercent)
            : ClampDiscountAmount(DiscountAmount);
    }

    private decimal CalculateDiscountAmount(decimal discountPercent)
    {
        return ClampDiscountAmount(decimal.Round(GrossAmount * discountPercent / 100m, 2, MidpointRounding.AwayFromZero));
    }

    private decimal ClampDiscountAmount(decimal discountAmount)
    {
        return Math.Clamp(decimal.Round(discountAmount, 2, MidpointRounding.AwayFromZero), 0m, GrossAmount);
    }
}
