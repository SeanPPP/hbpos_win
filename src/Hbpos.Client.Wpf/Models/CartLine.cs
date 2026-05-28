using CommunityToolkit.Mvvm.ComponentModel;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Models;

public enum CartLineKind
{
    Sale = 0,
    Return = 1,
    OpenItem = 2
}

public sealed record ReturnCartLineRequest(
    string StoreCode,
    string ProductCode,
    string? ReferenceCode,
    string DisplayName,
    string LookupCode,
    string? ItemNumber,
    string? ProductImage,
    decimal Quantity,
    decimal UnitPrice,
    PriceSourceKind PriceSource,
    string PriceSourceLabel,
    string ReturnSourceKey,
    Guid? OriginalOrderGuid,
    Guid? OriginalOrderLineGuid,
    string? ReturnReason = null);

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
    private CartLineKind _kind = CartLineKind.Sale;
    private string _returnSourceKey = string.Empty;
    private Guid? _originalOrderGuid;
    private Guid? _originalOrderLineGuid;
    private string? _returnReason;

    public CartLine(SellableItemDto item)
        : this(item, CartLineKind.Sale, item.RetailPrice)
    {
    }

    public CartLine(SellableItemDto item, CartLineKind kind, decimal unitPrice)
    {
        if (kind == CartLineKind.Return)
        {
            throw new InvalidOperationException("Return cart lines must be created from a return request.");
        }

        if (!IsPositiveIntegerQuantity(item.QuantityFactor))
        {
            throw new InvalidOperationException("Cart line quantity must be a positive integer.");
        }

        Kind = kind;
        Quantity = item.QuantityFactor;
        UpdateFrom(item);
        UnitPrice = unitPrice;
    }

    public CartLine(ReturnCartLineRequest request)
    {
        if (!IsPositiveIntegerQuantity(request.Quantity))
        {
            throw new InvalidOperationException("Cart line quantity must be a positive integer.");
        }

        Kind = CartLineKind.Return;
        StoreCode = request.StoreCode;
        ProductCode = request.ProductCode;
        ReferenceCode = request.ReferenceCode;
        ProductImage = request.ProductImage;
        DisplayName = request.DisplayName;
        LookupCode = request.LookupCode;
        LookupCodeNormalized = NormalizeLookupCode(request.LookupCode);
        Quantity = request.Quantity;
        UnitPrice = request.UnitPrice;
        PriceSource = request.PriceSource;
        PriceSourceLabel = request.PriceSourceLabel;
        ReturnSourceKey = request.ReturnSourceKey;
        OriginalOrderGuid = request.OriginalOrderGuid;
        OriginalOrderLineGuid = request.OriginalOrderLineGuid;
        ReturnReason = request.ReturnReason;
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
                OnPropertyChanged(nameof(HasZeroUnitPrice));
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

    public decimal GrossAmount => SignedAmount(PositiveGrossAmount);

    public decimal ActualAmount => SignedAmount(PositiveActualAmount);

    public bool HasDiscount => DiscountAmount > 0m && PositiveGrossAmount > 0m;

    public bool HasZeroUnitPrice => UnitPrice == 0m;

    public string DiscountRateText
    {
        get
        {
            if (!HasDiscount)
            {
                return string.Empty;
            }

            var rate = DiscountAmount / PositiveGrossAmount;
            return $"-{rate * 100m:0.##}%";
        }
    }

    public decimal? DiscountPercent => _discountPercent;

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

    public CartLineKind Kind
    {
        get => _kind;
        private set
        {
            if (SetProperty(ref _kind, value))
            {
                OnPropertyChanged(nameof(IsReturnLine));
                OnPropertyChanged(nameof(IsOpenItem));
                OnPropertyChanged(nameof(IsLocked));
                OnAmountPropertiesChanged();
            }
        }
    }

    public bool IsReturnLine => Kind == CartLineKind.Return;

    public bool IsOpenItem => Kind == CartLineKind.OpenItem;

    public bool IsLocked => IsReturnLine;

    public decimal SignedQuantity => IsReturnLine ? -Quantity : Quantity;

    public string ReturnSourceKey
    {
        get => _returnSourceKey;
        private set => SetProperty(ref _returnSourceKey, value);
    }

    public Guid? OriginalOrderGuid
    {
        get => _originalOrderGuid;
        private set => SetProperty(ref _originalOrderGuid, value);
    }

    public Guid? OriginalOrderLineGuid
    {
        get => _originalOrderLineGuid;
        private set => SetProperty(ref _originalOrderLineGuid, value);
    }

    public string? ReturnReason
    {
        get => _returnReason;
        private set => SetProperty(ref _returnReason, value);
    }

    public void Increase(decimal quantity)
    {
        ThrowIfLocked();
        Quantity += Math.Max(1m, quantity);
    }

    public bool Decrease(decimal quantity)
    {
        ThrowIfLocked();
        var decreaseBy = Math.Max(1m, quantity);
        if (Quantity <= decreaseBy)
        {
            return false;
        }

        Quantity -= decreaseBy;
        return true;
    }

    public void IncreaseReturnQuantity(decimal quantity)
    {
        if (!IsReturnLine)
        {
            throw new InvalidOperationException("Only return lines can use return quantity merging.");
        }

        Quantity += Math.Max(1m, quantity);
    }

    public void SetQuantity(decimal quantity)
    {
        ThrowIfLocked();
        if (!IsPositiveIntegerQuantity(quantity))
        {
            throw new InvalidOperationException("Cart line quantity must be a positive integer.");
        }

        Quantity = quantity;
    }

    public void SetUnitPrice(decimal unitPrice)
    {
        ThrowIfLocked();
        UnitPrice = unitPrice;
    }

    public void SetDiscountAmount(decimal discountAmount)
    {
        ThrowIfLocked();
        _discountPercent = null;
        DiscountAmount = ClampDiscountAmount(discountAmount);
    }

    public void SetDiscountPercent(decimal discountPercent)
    {
        ThrowIfLocked();
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

    private static bool IsPositiveIntegerQuantity(decimal quantity)
    {
        return quantity > 0m && decimal.Truncate(quantity) == quantity;
    }

    private void OnAmountPropertiesChanged()
    {
        OnPropertyChanged(nameof(SignedQuantity));
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
        return ClampDiscountAmount(decimal.Round(PositiveGrossAmount * discountPercent / 100m, 2, MidpointRounding.AwayFromZero));
    }

    private decimal ClampDiscountAmount(decimal discountAmount)
    {
        return Math.Clamp(decimal.Round(discountAmount, 2, MidpointRounding.AwayFromZero), 0m, PositiveGrossAmount);
    }

    private decimal PositiveGrossAmount => decimal.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);

    private decimal PositiveActualAmount => decimal.Round((Quantity * UnitPrice) - DiscountAmount, 2, MidpointRounding.AwayFromZero);

    private decimal SignedAmount(decimal amount)
    {
        return IsReturnLine ? -amount : amount;
    }

    private void ThrowIfLocked()
    {
        if (IsLocked)
        {
            throw new InvalidOperationException("Locked cart lines cannot be edited.");
        }
    }
}
