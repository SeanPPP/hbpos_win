using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public sealed class PosCartService
{
    private readonly List<CartLine> _lines = [];
    private readonly List<OrderReturnPaymentCapacityDto> _returnPaymentCapacities = [];

    public IReadOnlyList<CartLine> Lines => _lines;

    public IReadOnlyList<OrderReturnPaymentCapacityDto> ReturnPaymentCapacities => _returnPaymentCapacities;

    public decimal TotalAmount => decimal.Round(_lines.Sum(line => line.GrossAmount), 2, MidpointRounding.AwayFromZero);

    public decimal DiscountAmount => decimal.Round(_lines.Sum(line => line.DiscountAmount), 2, MidpointRounding.AwayFromZero);

    public decimal ActualAmount => decimal.Round(_lines.Sum(line => line.ActualAmount), 2, MidpointRounding.AwayFromZero);

    public bool IsEmpty => _lines.Count == 0;

    public bool HasZeroPriceLine => _lines.Any(line => line.HasZeroUnitPrice);

    public bool HasNonIntegerQuantity => _lines.Any(line => !IsPositiveIntegerQuantity(line.Quantity));

    public bool HasReturnLine => _lines.Any(line => line.IsReturnLine);

    public event EventHandler? CartChanged;

    public CartLine AddItem(SellableItemDto item)
    {
        if (!IsPositiveIntegerQuantity(item.QuantityFactor))
        {
            throw new InvalidOperationException("Cart item quantity must be a positive integer.");
        }

        var existing = FindLineByLookupCode(item.StoreCode, item.LookupCode);

        if (existing is not null)
        {
            if (!IsPositiveIntegerQuantity(existing.Quantity))
            {
                throw new InvalidOperationException("Cart line quantity must be a positive integer.");
            }

            existing.Increase(item.QuantityFactor);
            OnCartChanged();
            return existing;
        }

        var line = new CartLine(item);
        _lines.Add(line);
        OnCartChanged();
        return line;
    }

    public CartLine AddOpenItem(SellableItemDto item, decimal unitPrice)
    {
        if (!IsPositiveIntegerQuantity(item.QuantityFactor))
        {
            throw new InvalidOperationException("Cart item quantity must be a positive integer.");
        }

        if (unitPrice < 0m)
        {
            throw new InvalidOperationException("Open item price must be zero or greater.");
        }

        var line = new CartLine(item, CartLineKind.OpenItem, unitPrice);
        _lines.Add(line);
        OnCartChanged();
        return line;
    }

    public CartLine AddReturnLine(ReturnCartLineRequest request)
    {
        if (!IsPositiveIntegerQuantity(request.Quantity))
        {
            throw new InvalidOperationException("Return cart line quantity must be a positive integer.");
        }

        var existing = FindReturnLineBySourceKey(request.ReturnSourceKey);
        if (existing is not null)
        {
            existing.IncreaseReturnQuantity(request.Quantity);
            OnCartChanged();
            return existing;
        }

        var line = new CartLine(request);
        _lines.Add(line);
        OnCartChanged();
        return line;
    }

    public void AddReturnPaymentCapacities(IEnumerable<OrderReturnPaymentCapacityDto> capacities)
    {
        var capacityList = capacities
            .Where(capacity => capacity.RemainingAmount > 0m)
            .ToList();
        if (capacityList.Count == 0)
        {
            return;
        }

        var changed = false;
        foreach (var capacity in capacityList)
        {
            var existingIndex = _returnPaymentCapacities.FindIndex(existing =>
                existing.Method == capacity.Method &&
                string.Equals(existing.Reference ?? string.Empty, capacity.Reference ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                existing.OriginalOrderGuid == capacity.OriginalOrderGuid);
            if (existingIndex >= 0)
            {
                _returnPaymentCapacities[existingIndex] = capacity;
            }
            else
            {
                _returnPaymentCapacities.Add(capacity);
            }

            changed = true;
        }

        if (changed)
        {
            OnCartChanged();
        }
    }

    public CartLine? FindLineByLookupCode(string storeCode, string lookupCode)
    {
        var normalizedLookupCode = CartLine.NormalizeLookupCode(lookupCode);
        return _lines.FirstOrDefault(line =>
            !line.IsReturnLine &&
            !line.IsOpenItem &&
            string.Equals(line.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase) &&
            line.LookupCodeNormalized == normalizedLookupCode);
    }

    public CartLine? FindReturnLineBySourceKey(string returnSourceKey)
    {
        return _lines.FirstOrDefault(line =>
            line.IsReturnLine &&
            string.Equals(line.ReturnSourceKey, returnSourceKey, StringComparison.OrdinalIgnoreCase));
    }

    public bool UpdateLineFromRemote(SellableItemDto item)
    {
        return UpdateLineFromRemote(item.StoreCode, item.LookupCode, item);
    }

    public bool UpdateLineFromRemote(string storeCode, string lookupCode, SellableItemDto item)
    {
        var line = FindLineByLookupCode(storeCode, lookupCode);

        if (line is null)
        {
            return false;
        }

        line.UpdateFrom(item);
        OnCartChanged();
        return true;
    }

    public bool UpdateLineFromRemote(CartLine line, SellableItemDto item)
    {
        if (!_lines.Contains(line))
        {
            return false;
        }

        line.UpdateFrom(item);
        OnCartChanged();
        return true;
    }

    public bool RemoveLineByLookupCode(string storeCode, string lookupCode)
    {
        var line = FindLineByLookupCode(storeCode, lookupCode);

        if (line is null)
        {
            return false;
        }

        _lines.Remove(line);
        OnCartChanged();
        return true;
    }

    public bool RemoveLine(CartLine line)
    {
        if (!_lines.Remove(line))
        {
            return false;
        }

        if (line.IsReturnLine && !_lines.Any(existing => existing.IsReturnLine))
        {
            _returnPaymentCapacities.Clear();
        }

        OnCartChanged();
        return true;
    }

    public bool IncreaseLine(CartLine? line)
    {
        if (line is null || line.IsLocked || !_lines.Contains(line) || !IsPositiveIntegerQuantity(line.Quantity))
        {
            return false;
        }

        line.Increase(1m);
        OnCartChanged();
        return true;
    }

    public bool DecreaseLine(CartLine? line)
    {
        if (line is null || line.IsLocked || !_lines.Contains(line) || !IsPositiveIntegerQuantity(line.Quantity))
        {
            return false;
        }

        if (!line.Decrease(1m))
        {
            _lines.Remove(line);
            if (line.IsReturnLine && !_lines.Any(existing => existing.IsReturnLine))
            {
                _returnPaymentCapacities.Clear();
            }
        }

        OnCartChanged();
        return true;
    }

    public bool SetLineQuantity(CartLine? line, decimal quantity)
    {
        if (line is null || line.IsLocked || !_lines.Contains(line) || !IsPositiveIntegerQuantity(quantity))
        {
            return false;
        }

        line.SetQuantity(quantity);
        OnCartChanged();
        return true;
    }

    public bool SetLineUnitPrice(CartLine? line, decimal unitPrice)
    {
        if (line is null || line.IsLocked || !_lines.Contains(line) || unitPrice < 0m)
        {
            return false;
        }

        line.SetUnitPrice(unitPrice);
        OnCartChanged();
        return true;
    }

    public bool SetLineDiscountAmount(CartLine? line, decimal discountAmount)
    {
        if (line is null || line.IsLocked || !_lines.Contains(line) || discountAmount < 0m || discountAmount > line.GrossAmount)
        {
            return false;
        }

        line.SetDiscountAmount(discountAmount);
        OnCartChanged();
        return true;
    }

    public bool SetLineDiscountPercent(CartLine? line, decimal discountPercent)
    {
        if (line is null || line.IsLocked || !_lines.Contains(line) || discountPercent < 0m || discountPercent > 100m)
        {
            return false;
        }

        line.SetDiscountPercent(discountPercent);
        OnCartChanged();
        return true;
    }

    public bool SetOrderDiscountAmount(decimal discountAmount)
    {
        if (_lines.Count == 0 || HasReturnLine || discountAmount < 0m || discountAmount > TotalAmount)
        {
            return false;
        }

        ApplyOrderDiscountAmount(discountAmount);
        OnCartChanged();
        return true;
    }

    public bool SetOrderDiscountPercent(decimal discountPercent)
    {
        if (_lines.Count == 0 || HasReturnLine || discountPercent < 0m || discountPercent > 100m)
        {
            return false;
        }

        var discountAmount = decimal.Round(TotalAmount * discountPercent / 100m, 2, MidpointRounding.AwayFromZero);
        ApplyOrderDiscountAmount(discountAmount);
        OnCartChanged();
        return true;
    }

    public void Clear()
    {
        if (_lines.Count == 0)
        {
            if (_returnPaymentCapacities.Count > 0)
            {
                _returnPaymentCapacities.Clear();
                OnCartChanged();
            }

            return;
        }

        _lines.Clear();
        _returnPaymentCapacities.Clear();
        OnCartChanged();
    }

    public PosCartSnapshot CreateSnapshot()
    {
        return new PosCartSnapshot(_lines
            .Select(line => new PosCartLineSnapshot(
                line.StoreCode,
                line.ProductCode,
                line.ReferenceCode,
                line.DisplayName,
                line.LookupCode,
                line.ItemNumber,
                line.ProductImage,
                line.Quantity,
                line.UnitPrice,
                line.DiscountAmount,
                line.DiscountPercent,
                line.PriceSource,
                line.PriceSourceLabel,
                line.Kind,
                line.ReturnSourceKey,
                line.OriginalOrderGuid,
                line.OriginalOrderLineGuid,
                line.ReturnReason))
            .ToArray());
    }

    public void RestoreSnapshot(PosCartSnapshot snapshot)
    {
        _lines.Clear();
        _returnPaymentCapacities.Clear();
        foreach (var snapshotLine in snapshot.Lines)
        {
            if (!IsPositiveIntegerQuantity(snapshotLine.Quantity))
            {
                throw new InvalidOperationException("Cart line quantity must be a positive integer.");
            }

            CartLine line;
            if (snapshotLine.Kind == CartLineKind.Return)
            {
                line = new CartLine(new ReturnCartLineRequest(
                    snapshotLine.StoreCode,
                    snapshotLine.ProductCode,
                    snapshotLine.ReferenceCode,
                    snapshotLine.DisplayName,
                    snapshotLine.LookupCode,
                    snapshotLine.ItemNumber,
                    snapshotLine.ProductImage,
                    snapshotLine.Quantity,
                    snapshotLine.UnitPrice,
                    snapshotLine.PriceSource,
                    snapshotLine.PriceSourceLabel,
                    snapshotLine.ReturnSourceKey,
                    snapshotLine.OriginalOrderGuid,
                    snapshotLine.OriginalOrderLineGuid,
                    snapshotLine.ReturnReason));
            }
            else if (snapshotLine.Kind == CartLineKind.OpenItem)
            {
                var item = CreateSnapshotItem(snapshotLine);
                line = new CartLine(item, CartLineKind.OpenItem, snapshotLine.UnitPrice);
                line.SetQuantity(snapshotLine.Quantity);
                if (snapshotLine.DiscountPercent is decimal discountPercent)
                {
                    line.SetDiscountPercent(discountPercent);
                }
                else
                {
                    line.SetDiscountAmount(snapshotLine.DiscountAmount);
                }
            }
            else
            {
                var item = CreateSnapshotItem(snapshotLine);
                line = new CartLine(item);
                line.SetQuantity(snapshotLine.Quantity);
                line.SetUnitPrice(snapshotLine.UnitPrice);
                if (snapshotLine.DiscountPercent is decimal discountPercent)
                {
                    line.SetDiscountPercent(discountPercent);
                }
                else
                {
                    line.SetDiscountAmount(snapshotLine.DiscountAmount);
                }
            }

            _lines.Add(line);
        }

        OnCartChanged();
    }

    private static SellableItemDto CreateSnapshotItem(PosCartLineSnapshot snapshotLine)
    {
        return new SellableItemDto(
            snapshotLine.StoreCode,
            snapshotLine.ProductCode,
            snapshotLine.ReferenceCode,
            snapshotLine.DisplayName,
            snapshotLine.LookupCode,
            snapshotLine.ItemNumber,
            snapshotLine.LookupCode,
            snapshotLine.UnitPrice,
            snapshotLine.PriceSource,
            snapshotLine.PriceSourceLabel,
            1m,
            null,
            snapshotLine.ProductImage);
    }

    private void OnCartChanged()
    {
        CartChanged?.Invoke(this, EventArgs.Empty);
    }

    public static bool IsPositiveIntegerQuantity(decimal quantity)
    {
        return quantity > 0m && decimal.Truncate(quantity) == quantity;
    }

    private void ApplyOrderDiscountAmount(decimal discountAmount)
    {
        var totalGrossAmount = TotalAmount;
        var remainingDiscount = Math.Clamp(
            decimal.Round(discountAmount, 2, MidpointRounding.AwayFromZero),
            0m,
            totalGrossAmount);
        var discountableLines = _lines.Where(line => line.GrossAmount > 0m).ToList();

        if (discountableLines.Count == 0)
        {
            return;
        }

        for (var i = 0; i < discountableLines.Count; i++)
        {
            var line = discountableLines[i];
            var lineDiscount = i == discountableLines.Count - 1
                ? remainingDiscount
                : decimal.Round(discountAmount * line.GrossAmount / totalGrossAmount, 2, MidpointRounding.AwayFromZero);

            lineDiscount = Math.Clamp(lineDiscount, 0m, Math.Min(line.GrossAmount, remainingDiscount));
            line.SetDiscountAmount(lineDiscount);
            remainingDiscount -= lineDiscount;
        }
    }
}

public sealed record PosCartSnapshot(IReadOnlyList<PosCartLineSnapshot> Lines);

public sealed record PosCartLineSnapshot(
    string StoreCode,
    string ProductCode,
    string? ReferenceCode,
    string DisplayName,
    string LookupCode,
    string? ItemNumber,
    string? ProductImage,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal? DiscountPercent,
    PriceSourceKind PriceSource,
    string PriceSourceLabel,
    CartLineKind Kind = CartLineKind.Sale,
    string ReturnSourceKey = "",
    Guid? OriginalOrderGuid = null,
    Guid? OriginalOrderLineGuid = null,
    string? ReturnReason = null);
