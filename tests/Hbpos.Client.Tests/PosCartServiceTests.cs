using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

public sealed class PosCartServiceTests
{
    [Fact]
    public void AddItem_merges_same_store_lookup_with_different_price_and_preserves_quantity()
    {
        var cart = new PosCartService();

        cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "abc-001", price: 10m));
        cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: " ABC-001 ", price: 12m));

        var line = Assert.Single(cart.Lines);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal("ABC-001", line.LookupCodeNormalized);
    }

    [Fact]
    public void UpdateLineFromRemote_refreshes_display_price_source_and_totals()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem(
            productCode: "SKU-001",
            lookupCode: "690001",
            displayName: "Old Milk",
            price: 10m,
            priceSource: PriceSourceKind.ProductBase,
            productImage: "https://images.example/old-milk.jpg"));
        cart.AddItem(CreateItem(
            productCode: "SKU-001",
            lookupCode: "690001",
            displayName: "Old Milk",
            price: 10m,
            priceSource: PriceSourceKind.ProductBase));

        var updated = CreateItem(
            productCode: "SKU-001",
            lookupCode: "REMOTE-690001",
            displayName: "Fresh Milk",
            price: 12.5m,
            priceSource: PriceSourceKind.StoreClearancePrice,
            productImage: "https://images.example/fresh-milk.jpg");

        Assert.True(cart.UpdateLineFromRemote("S001", "690001", updated));

        var line = Assert.Single(cart.Lines);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal("Fresh Milk", line.DisplayName);
        Assert.Equal("https://images.example/fresh-milk.jpg", line.ProductImage);
        Assert.Equal("REMOTE-690001", line.LookupCodeNormalized);
        Assert.Equal(12.5m, line.UnitPrice);
        Assert.Equal(PriceSourceKind.StoreClearancePrice, line.PriceSource);
        Assert.Equal(nameof(PriceSourceKind.StoreClearancePrice), line.PriceSourceLabel);
        Assert.Equal(25m, cart.TotalAmount);
        Assert.Equal(25m, cart.ActualAmount);
    }

    [Fact]
    public void RemoveLineByLookupCode_removes_only_matching_store_lookup()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem(storeCode: "S001", productCode: "SKU-001", lookupCode: "690001", price: 10m));
        cart.AddItem(CreateItem(storeCode: "S001", productCode: "SKU-002", lookupCode: "690002", price: 20m));
        cart.AddItem(CreateItem(storeCode: "S002", productCode: "SKU-003", lookupCode: "690001", price: 30m));

        Assert.True(cart.RemoveLineByLookupCode("S001", " 690001 "));

        Assert.Equal(2, cart.Lines.Count);
        Assert.Null(cart.FindLineByLookupCode("S001", "690001"));
        Assert.NotNull(cart.FindLineByLookupCode("S001", "690002"));
        Assert.NotNull(cart.FindLineByLookupCode("S002", "690001"));
        Assert.Equal(50m, cart.TotalAmount);
    }

    [Fact]
    public void RemoveLine_removes_only_the_given_cart_line()
    {
        var cart = new PosCartService();
        var first = cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "690001", price: 10m));
        var second = cart.AddItem(CreateItem(productCode: "SKU-002", lookupCode: "690002", price: 20m));

        Assert.True(cart.RemoveLine(first));

        var line = Assert.Single(cart.Lines);
        Assert.Same(second, line);
        Assert.Equal(20m, cart.TotalAmount);
    }

    [Fact]
    public void RemoveLine_removes_the_entire_line_when_quantity_is_greater_than_one()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "690001", price: 10m));
        cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "690001", price: 10m));

        Assert.Equal(2m, line.Quantity);
        Assert.True(cart.RemoveLine(line));

        Assert.Empty(cart.Lines);
        Assert.Equal(0m, cart.TotalAmount);
    }

    [Fact]
    public void AddItem_does_not_merge_same_product_with_different_lookup()
    {
        var cart = new PosCartService();

        cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "690001", price: 10m));
        cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "690002", price: 10m));

        Assert.Equal(2, cart.Lines.Count);
        Assert.Contains(cart.Lines, line => line.LookupCodeNormalized == "690001");
        Assert.Contains(cart.Lines, line => line.LookupCodeNormalized == "690002");
    }

    [Fact]
    public void IncreaseLine_adds_one_unit_and_recalculates_totals()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(price: 10m));

        Assert.True(cart.IncreaseLine(line));

        Assert.Equal(2m, line.Quantity);
        Assert.Equal(20m, cart.TotalAmount);
        Assert.Equal(20m, cart.ActualAmount);
    }

    [Fact]
    public void DecreaseLine_removes_one_unit_and_recalculates_totals()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(price: 10m));
        cart.IncreaseLine(line);

        Assert.True(cart.DecreaseLine(line));

        line = Assert.Single(cart.Lines);
        Assert.Equal(1m, line.Quantity);
        Assert.Equal(10m, cart.TotalAmount);
        Assert.Equal(10m, cart.ActualAmount);
    }

    [Fact]
    public void DecreaseLine_removes_the_line_when_quantity_reaches_zero()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(price: 10m));

        Assert.True(cart.DecreaseLine(line));

        Assert.Empty(cart.Lines);
        Assert.Equal(0m, cart.TotalAmount);
        Assert.Equal(0m, cart.ActualAmount);
    }

    [Fact]
    public void SetLineQuantity_and_unit_price_recalculate_totals()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(price: 10m));

        Assert.True(cart.SetLineQuantity(line, 2m));
        Assert.True(cart.SetLineUnitPrice(line, 4.2m));

        Assert.Equal(2m, line.Quantity);
        Assert.Equal(4.2m, line.UnitPrice);
        Assert.Equal(8.4m, line.GrossAmount);
        Assert.Equal(8.4m, cart.TotalAmount);
        Assert.Equal(8.4m, cart.ActualAmount);
    }

    [Fact]
    public void Cart_detects_zero_price_lines_and_updates_after_price_edit()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(price: 0m));

        Assert.True(line.HasZeroUnitPrice);
        Assert.True(cart.HasZeroPriceLine);

        Assert.True(cart.SetLineUnitPrice(line, 4.2m));

        Assert.False(line.HasZeroUnitPrice);
        Assert.False(cart.HasZeroPriceLine);
    }

    [Fact]
    public void SetLineQuantity_rejects_non_integer_quantity()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(price: 10m));

        Assert.False(cart.SetLineQuantity(line, 2.5m));

        Assert.Equal(1m, line.Quantity);
        Assert.False(cart.HasNonIntegerQuantity);
    }

    [Fact]
    public void AddItem_rejects_non_integer_quantity_factor()
    {
        var cart = new PosCartService();

        Assert.Throws<InvalidOperationException>(() => cart.AddItem(CreateItem(quantityFactor: 1.5m)));

        Assert.Empty(cart.Lines);
    }

    [Fact]
    public void SetLineDiscountAmount_clamps_to_current_gross_amount()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(price: 10m));
        cart.SetLineQuantity(line, 2m);

        Assert.True(cart.SetLineDiscountAmount(line, 5m));
        Assert.Equal(5m, line.DiscountAmount);
        Assert.Equal(15m, line.ActualAmount);

        Assert.True(cart.SetLineUnitPrice(line, 2m));

        Assert.Equal(4m, line.GrossAmount);
        Assert.Equal(4m, line.DiscountAmount);
        Assert.Equal(0m, line.ActualAmount);
        Assert.Equal(4m, cart.DiscountAmount);
        Assert.Equal(0m, cart.ActualAmount);
    }

    [Fact]
    public void SetLineDiscountPercent_recalculates_after_quantity_changes()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(price: 10m));
        cart.SetLineQuantity(line, 2m);

        Assert.True(cart.SetLineDiscountPercent(line, 8.5m));

        Assert.Equal(1.70m, line.DiscountAmount);
        Assert.Equal(18.30m, line.ActualAmount);
        Assert.Equal("-8.5%", line.DiscountRateText);

        Assert.True(cart.SetLineQuantity(line, 3m));

        Assert.Equal(2.55m, line.DiscountAmount);
        Assert.Equal(27.45m, line.ActualAmount);
        Assert.Equal(2.55m, cart.DiscountAmount);
        Assert.Equal(27.45m, cart.ActualAmount);
    }

    [Fact]
    public void Line_edit_methods_reject_zero_quantity_and_unreasonable_discounts()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem(price: 10m));

        Assert.False(cart.SetLineQuantity(line, 0m));
        Assert.False(cart.SetLineDiscountAmount(line, 10.01m));
        Assert.False(cart.SetLineDiscountPercent(line, 100.01m));

        Assert.Equal(1m, line.Quantity);
        Assert.Equal(0m, line.DiscountAmount);
        Assert.Equal(10m, cart.ActualAmount);
    }

    [Fact]
    public void SetOrderDiscountAmount_prorates_one_time_discount_across_lines()
    {
        var cart = new PosCartService();
        var first = cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "690001", price: 10m));
        var second = cart.AddItem(CreateItem(productCode: "SKU-002", lookupCode: "690002", price: 30m));

        Assert.True(cart.SetOrderDiscountAmount(8m));

        Assert.Equal(2m, first.DiscountAmount);
        Assert.Equal(6m, second.DiscountAmount);
        Assert.Equal(8m, cart.DiscountAmount);
        Assert.Equal(32m, cart.ActualAmount);
    }

    [Fact]
    public void SetOrderDiscountPercent_applies_one_time_line_amount_discounts()
    {
        var cart = new PosCartService();
        var first = cart.AddItem(CreateItem(productCode: "SKU-001", lookupCode: "690001", price: 10m));
        var second = cart.AddItem(CreateItem(productCode: "SKU-002", lookupCode: "690002", price: 30m));

        Assert.True(cart.SetOrderDiscountPercent(10m));

        Assert.Equal(1m, first.DiscountAmount);
        Assert.Equal(3m, second.DiscountAmount);
        Assert.Equal(36m, cart.ActualAmount);

        Assert.True(cart.SetLineUnitPrice(first, 20m));

        Assert.Equal(1m, first.DiscountAmount);
        Assert.Equal(3m, second.DiscountAmount);
        Assert.Equal(4m, cart.DiscountAmount);
        Assert.Equal(46m, cart.ActualAmount);
    }

    [Fact]
    public void Order_discount_methods_reject_unreasonable_discounts()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem(price: 10m));

        Assert.False(cart.SetOrderDiscountAmount(10.01m));
        Assert.False(cart.SetOrderDiscountPercent(100.01m));

        var line = Assert.Single(cart.Lines);
        Assert.Equal(0m, line.DiscountAmount);
        Assert.Equal(10m, cart.ActualAmount);
    }

    private static SellableItemDto CreateItem(
        string storeCode = "S001",
        string productCode = "SKU-001",
        string lookupCode = "690001",
        string displayName = "Milk 1L",
        string? itemNumber = null,
        decimal price = 10m,
        PriceSourceKind priceSource = PriceSourceKind.StoreRetailPrice,
        string? productImage = null,
        decimal quantityFactor = 1m)
    {
        return new SellableItemDto(
            StoreCode: storeCode,
            ProductCode: productCode,
            ReferenceCode: null,
            DisplayName: displayName,
            LookupCode: lookupCode,
            ItemNumber: itemNumber ?? productCode,
            Barcode: lookupCode.Trim(),
            RetailPrice: price,
            PriceSource: priceSource,
            PriceSourceLabel: priceSource.ToString(),
            QuantityFactor: quantityFactor,
            UpdatedAt: DateTimeOffset.UtcNow,
            ProductImage: productImage);
    }
}
