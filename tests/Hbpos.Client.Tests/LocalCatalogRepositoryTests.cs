using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

public sealed class LocalCatalogRepositoryTests
{
    [Fact]
    public async Task UpsertSellableItemsAsync_inserts_and_updates_by_store_and_normalized_lookup_code()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            var original = CreateItem("S001", "SKU-001", " abc ", "Original name", 1.25m, "https://images.example/original.jpg", "REF-001");
            var updated = CreateItem("S001", "SKU-001B", "ABC", "Updated name", 2.50m, "https://images.example/updated.jpg", "REF-002");

            await repository.UpsertSellableItemsAsync([original]);
            await repository.UpsertSellableItemsAsync([updated]);

            var items = await repository.LoadSellableItemsAsync();
            var saved = Assert.Single(items);
            Assert.Equal("SKU-001B", saved.ProductCode);
            Assert.Equal("Updated name", saved.DisplayName);
            Assert.Equal("REF-002", saved.ReferenceCode);
            Assert.Equal(2.50m, saved.RetailPrice);
            Assert.Equal("https://images.example/updated.jpg", saved.ProductImage);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task UpsertSellableItemsAsync_inserts_and_updates_discount_rate()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            var original = CreateItem("S001", "SKU-001", " abc ", "Original name", 1.25m, discountRate: 0.2m);
            var updated = CreateItem("S001", "SKU-001B", "ABC", "Updated name", 2.50m, discountRate: 0.35m);

            await repository.UpsertSellableItemsAsync([original]);
            await repository.UpsertSellableItemsAsync([updated]);

            var items = await repository.LoadSellableItemsAsync();
            var saved = Assert.Single(items);
            Assert.Equal(0.35m, saved.DiscountRate);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task UpsertSellableItemsAsync_persists_special_flag_and_changes_compare_hash()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.UpsertSellableItemsAsync(
            [
                CreateItem("S001", "SKU-001", "abc", "Original name", 1.25m, isSpecialProduct: false)
            ]);
            var before = Assert.Single(await repository.LoadSellableItemComparePageAsync("S001", null, 10));

            await repository.UpsertSellableItemsAsync(
            [
                CreateItem("S001", "SKU-001", "abc", "Original name", 1.25m, isSpecialProduct: true)
            ]);

            var saved = Assert.Single(await repository.LoadSellableItemsAsync());
            var after = Assert.Single(await repository.LoadSellableItemComparePageAsync("S001", null, 10));
            Assert.True(saved.IsSpecialProduct);
            Assert.NotEqual(before.ContentHash, after.ContentHash);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task LoadSpecialProductItemsAsync_uses_local_sort_and_keeps_images()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.UpsertSellableItemsAsync(
            [
                CreateItem("S001", "SKU-001", "abc", "Alpha", 1m, "https://images.example/alpha.jpg", isSpecialProduct: true),
                CreateItem("S001", "SKU-002", "def", "Beta", 2m, "https://images.example/beta.jpg", isSpecialProduct: true),
                CreateItem("S001", "SKU-003", "ghi", "Gamma", 3m, isSpecialProduct: false)
            ]);
            await repository.SaveSpecialProductOrderAsync("S001", ["SKU-002", "SKU-001"]);

            var specialItems = await repository.LoadSpecialProductItemsAsync("S001");

            Assert.Equal(["SKU-002", "SKU-001"], specialItems.Select(x => x.ProductCode).ToArray());
            Assert.All(specialItems, item => Assert.True(item.IsSpecialProduct));
            Assert.Equal("https://images.example/beta.jpg", specialItems[0].ProductImage);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task UpdateSpecialProductFlagAsync_removes_item_from_special_list_and_sort_order()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.UpsertSellableItemsAsync(
            [
                CreateItem("S001", "SKU-001", "abc", "Alpha", 1m, isSpecialProduct: true),
                CreateItem("S001", "SKU-002", "def", "Beta", 2m, isSpecialProduct: true)
            ]);
            await repository.SaveSpecialProductOrderAsync("S001", ["SKU-002", "SKU-001"]);

            var updated = await repository.UpdateSpecialProductFlagAsync("S001", "SKU-002", false);
            var specialItems = await repository.LoadSpecialProductItemsAsync("S001");

            Assert.Equal(1, updated);
            var item = Assert.Single(specialItems);
            Assert.Equal("SKU-001", item.ProductCode);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ClearSpecialProductFlagsExceptAsync_unmarks_missing_products_and_keeps_requested_sort_order()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.UpsertSellableItemsAsync(
            [
                CreateItem("S001", "SKU-001", "abc", "Alpha", 1m, isSpecialProduct: true),
                CreateItem("S001", "SKU-002", "def", "Beta", 2m, isSpecialProduct: true),
                CreateItem("S001", "SKU-003", "ghi", "Gamma", 3m, isSpecialProduct: true)
            ]);
            await repository.SaveSpecialProductOrderAsync("S001", ["SKU-003", "SKU-002", "SKU-001"]);

            var updated = await repository.ClearSpecialProductFlagsExceptAsync("S001", ["SKU-002"]);
            var specialItems = await repository.LoadSpecialProductItemsAsync("S001");

            Assert.Equal(2, updated);
            var item = Assert.Single(specialItems);
            Assert.Equal("SKU-002", item.ProductCode);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task DeleteByLookupCodesAsync_deletes_only_matching_store_and_normalized_lookup_codes()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.UpsertSellableItemsAsync(
            [
                CreateItem("S001", "SKU-001", "abc", "S001 ABC", 1m),
                CreateItem("S001", "SKU-002", "def", "S001 DEF", 2m),
                CreateItem("S002", "SKU-003", "ABC", "S002 ABC", 3m)
            ]);

            var deleted = await repository.DeleteByLookupCodesAsync("S001", [" ABC "]);

            Assert.Equal(1, deleted);
            Assert.Null(await repository.FindByLookupCodeAsync("S001", "abc"));
            Assert.NotNull(await repository.FindByLookupCodeAsync("S001", "def"));
            Assert.NotNull(await repository.FindByLookupCodeAsync("S002", "abc"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task LoadSellableItemsAsync_WithStoreCode_ReturnsOnlyThatStore()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.UpsertSellableItemsAsync(
            [
                CreateItem("S001", "SKU-001", "abc", "S001 ABC", 1m),
                CreateItem("S002", "SKU-002", "abc", "S002 ABC", 2m)
            ]);

            var items = await repository.LoadSellableItemsAsync("S002");

            var item = Assert.Single(items);
            Assert.Equal("S002", item.StoreCode);
            Assert.Equal("SKU-002", item.ProductCode);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task LoadSellableItemComparePageAsync_pages_by_store_and_normalized_lookup_code()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.UpsertSellableItemsAsync(
            [
                CreateItem("S001", "SKU-B", "b-code", "B item", 2m),
                CreateItem("S001", "SKU-A", "a-code", "A item", 1m),
                CreateItem("S001", "SKU-C", "c-code", "C item", 3m),
                CreateItem("S002", "SKU-D", "aa-code", "Other store item", 4m)
            ]);

            var firstPage = await repository.LoadSellableItemComparePageAsync("S001", afterLookupCodeNormalized: null, pageSize: 2);
            var secondPage = await repository.LoadSellableItemComparePageAsync("S001", firstPage[^1].LookupCodeNormalized, pageSize: 2);

            Assert.Equal(["A-CODE", "B-CODE"], firstPage.Select(row => row.LookupCodeNormalized).ToArray());
            var finalRow = Assert.Single(secondPage);
            Assert.Equal("C-CODE", finalRow.LookupCodeNormalized);
            Assert.All(firstPage.Concat(secondPage), row =>
            {
                Assert.Equal("S001", row.StoreCode);
                Assert.False(string.IsNullOrWhiteSpace(row.ContentHash));
                Assert.NotNull(row.SyncedAt);
            });
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task FindByLookupCodeAsync_matches_normalized_lookup_within_store()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.UpsertSellableItemsAsync(
            [
                CreateItem("S001", "SKU-001", "  lookup-1 ", "Lookup item", 1m),
                CreateItem("S002", "SKU-002", "LOOKUP-1", "Other store item", 2m)
            ]);

            var found = await repository.FindByLookupCodeAsync("S001", "lookup-1");

            Assert.NotNull(found);
            Assert.Equal("SKU-001", found.ProductCode);
            Assert.Equal("Lookup item", found.DisplayName);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    private static async Task<LocalCatalogRepository> CreateRepositoryAsync(string databasePath)
    {
        var store = new LocalSqliteStore(databasePath);
        var schema = new LocalSchemaService(store);
        await schema.InitializeAsync();
        return new LocalCatalogRepository(store);
    }

    private static SellableItemDto CreateItem(
        string storeCode,
        string productCode,
        string lookupCode,
        string displayName,
        decimal retailPrice,
        string? productImage = null,
        string? referenceCode = null,
        decimal? discountRate = null,
        bool isSpecialProduct = false)
    {
        return new SellableItemDto(
            StoreCode: storeCode,
            ProductCode: productCode,
            ReferenceCode: referenceCode,
            DisplayName: displayName,
            LookupCode: lookupCode,
            ItemNumber: productCode,
            Barcode: lookupCode,
            RetailPrice: retailPrice,
            PriceSource: PriceSourceKind.StoreRetailPrice,
            PriceSourceLabel: PriceSourceKind.StoreRetailPrice.ToString(),
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow,
            ProductImage: productImage,
            DiscountRate: discountRate,
            IsSpecialProduct: isSpecialProduct);
    }

    private static string CreateTempDatabasePath()
    {
        return Path.Combine(Path.GetTempPath(), $"hbpos-catalog-{Guid.NewGuid():N}.db");
    }

    private static void DeleteTempDatabase(string databasePath)
    {
        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }
    }
}
