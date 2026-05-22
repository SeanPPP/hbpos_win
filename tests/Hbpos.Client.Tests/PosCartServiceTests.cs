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

    private static SellableItemDto CreateItem(
        string storeCode = "S001",
        string productCode = "SKU-001",
        string lookupCode = "690001",
        string displayName = "Milk 1L",
        string? itemNumber = null,
        decimal price = 10m,
        PriceSourceKind priceSource = PriceSourceKind.StoreRetailPrice,
        string? productImage = null)
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
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow,
            ProductImage: productImage);
    }
}
