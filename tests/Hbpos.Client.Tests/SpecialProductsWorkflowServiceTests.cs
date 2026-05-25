using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

public sealed class SpecialProductsWorkflowServiceTests
{
    [Fact]
    public async Task PreloadAsync_then_EnsureLoadedAsync_reuses_loaded_special_product_cache()
    {
        var repository = new FakeCatalogRepository
        {
            SpecialItems = [CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true)]
        };
        var service = CreateWorkflowService(repository: repository);

        var preload = await service.PreloadAsync("S001");
        var ensured = await service.EnsureLoadedAsync("S001");

        Assert.Equal(1, repository.LoadSpecialProductItemsCallCount);
        Assert.Single(preload.SpecialItems);
        Assert.Single(ensured.SpecialItems);
    }

    [Fact]
    public async Task EnsureLoadedAsync_reuses_inflight_preload_for_same_store()
    {
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new FakeCatalogRepository
        {
            SpecialItems = [CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true)],
            BeforeLoadSpecialProductItemsAsync = () => releaseLoad.Task
        };
        var service = CreateWorkflowService(repository: repository);

        var preloadTask = service.PreloadAsync("S001");
        var ensureTask = service.EnsureLoadedAsync("S001");
        releaseLoad.SetResult();
        await Task.WhenAll(preloadTask, ensureTask);

        Assert.Equal(1, repository.LoadSpecialProductItemsCallCount);
    }

    [Fact]
    public void Search_returns_deduplicated_non_special_results_for_store()
    {
        var duplicateA = CreateItem("SKU-001", "Alpha", "930001");
        var duplicateB = duplicateA with { LookupCode = "ITEM-001", Barcode = "930001" };
        var alreadySpecial = CreateItem("SKU-002", "Beta", "930002", isSpecialProduct: true);
        var otherStore = duplicateA with { StoreCode = "S002" };
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([duplicateA, duplicateB, alreadySpecial, otherStore]);
        var service = CreateWorkflowService(index: index);

        var result = service.Search("S001", "930001");

        var item = Assert.Single(result.Items);
        Assert.Equal("SKU-001", item.ProductCode);
        Assert.False(item.IsSpecialProduct);
    }

    [Fact]
    public async Task DownloadAsync_refreshes_index_and_returns_special_items()
    {
        var downloaded = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var repository = new FakeCatalogRepository
        {
            SellableItems = [downloaded],
            SpecialItems = [downloaded]
        };
        var specialProductService = new FakeSpecialProductService
        {
            DownloadResult = new SpecialProductDownloadResult("S001", 1, 1, 1, 1, 0)
        };
        var index = new LocalSellableItemIndex();
        var service = CreateWorkflowService(index, repository, specialProductService);

        var result = await service.DownloadAsync("S001");

        Assert.Equal(1, specialProductService.DownloadCallCount);
        Assert.Single(result.SpecialItems);
        Assert.Contains(index.Search("S001", "930001"), item => item.ProductCode == "SKU-001" && item.IsSpecialProduct);
    }

    [Fact]
    public async Task ReorderAsync_saves_updated_special_product_order()
    {
        var first = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var second = CreateItem("SKU-002", "Beta", "930002", isSpecialProduct: true);
        var repository = new FakeCatalogRepository
        {
            SpecialItems = [first, second]
        };
        var service = CreateWorkflowService(repository: repository);

        var result = await service.ReorderAsync("S001", [first, second], "SKU-001", 1);

        Assert.NotNull(result);
        Assert.Equal(["SKU-002", "SKU-001"], result.SpecialItems.Select(item => item.ProductCode).ToArray());
        Assert.Equal(["SKU-002", "SKU-001"], Assert.Single(repository.SavedOrders));
    }

    private static SpecialProductsWorkflowService CreateWorkflowService(
        LocalSellableItemIndex? index = null,
        FakeCatalogRepository? repository = null,
        FakeSpecialProductService? specialProductService = null)
    {
        return CreateWorkflowServiceCore(
            index ?? new LocalSellableItemIndex(),
            repository ?? new FakeCatalogRepository(),
            specialProductService ?? new FakeSpecialProductService());
    }

    private static SpecialProductsWorkflowService CreateWorkflowServiceCore(
        LocalSellableItemIndex index,
        FakeCatalogRepository repository,
        FakeSpecialProductService specialProductService)
    {
        return new SpecialProductsWorkflowService(
            index,
            new PosCartService(),
            repository,
            specialProductService);
    }

    private static SellableItemDto CreateItem(
        string productCode,
        string displayName,
        string lookupCode,
        bool isSpecialProduct = false)
    {
        return new SellableItemDto(
            "S001",
            productCode,
            ReferenceCode: null,
            displayName,
            lookupCode,
            ItemNumber: productCode,
            Barcode: lookupCode,
            RetailPrice: 1.25m,
            PriceSourceKind.StoreRetailPrice,
            "store-retail",
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow,
            ProductImage: $"https://images.example/{productCode}.jpg",
            DiscountRate: null,
            IsSpecialProduct: isSpecialProduct);
    }

    private sealed class FakeCatalogRepository : ILocalCatalogRepository
    {
        public IReadOnlyList<SellableItemDto> SellableItems { get; set; } = [];

        public IReadOnlyList<SellableItemDto> SpecialItems { get; set; } = [];

        public List<string[]> SavedOrders { get; } = [];

        public int LoadSpecialProductItemsCallCount { get; private set; }

        public Func<Task>? BeforeLoadSpecialProductItemsAsync { get; init; }

        public Task ReplaceSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task UpsertSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<int> DeleteByLookupCodesAsync(string storeCode, IEnumerable<string> lookupCodes, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<SellableItemDto?> FindByLookupCodeAsync(string storeCode, string lookupCode, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SellableItemDto?>(null);
        }

        public Task<IReadOnlyList<SellableItemDto>> LoadSpecialProductItemsAsync(string storeCode, CancellationToken cancellationToken = default)
        {
            LoadSpecialProductItemsCallCount++;
            if (BeforeLoadSpecialProductItemsAsync is null)
            {
                return Task.FromResult(SpecialItems);
            }

            return LoadSpecialItemsCoreAsync();

            async Task<IReadOnlyList<SellableItemDto>> LoadSpecialItemsCoreAsync()
            {
                await BeforeLoadSpecialProductItemsAsync();
                return SpecialItems;
            }
        }

        public Task SaveSpecialProductOrderAsync(string storeCode, IEnumerable<string> productCodes, CancellationToken cancellationToken = default)
        {
            SavedOrders.Add(productCodes.ToArray());
            SpecialItems = productCodes
                .Select(code => SpecialItems.First(item => string.Equals(item.ProductCode, code, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            return Task.CompletedTask;
        }

        public Task<int> UpdateSpecialProductFlagAsync(string storeCode, string productCode, bool isSpecialProduct, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<int> ClearSpecialProductFlagsExceptAsync(string storeCode, IEnumerable<string> productCodesToKeep, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<IReadOnlyList<LocalSellableItemCompareRow>> LoadSellableItemComparePageAsync(
            string storeCode,
            string? afterLookupCodeNormalized,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalSellableItemCompareRow>>([]);
        }

        public Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SellableItems);
        }

        public Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(string storeCode, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SellableItemDto>>(
                SellableItems.Where(item => string.Equals(item.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase)).ToArray());
        }
    }

    private sealed class FakeSpecialProductService : ISpecialProductService
    {
        public int DownloadCallCount { get; private set; }

        public SpecialProductDownloadResult DownloadResult { get; init; } =
            new("S001", 0, 0, 0, 0, 0);

        public Task<IReadOnlyList<SellableItemDto>> MarkSpecialProductAsync(
            string storeCode,
            string productCode,
            bool isSpecialProduct,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SellableItemDto>>([]);
        }

        public Task<SpecialProductDownloadResult> DownloadSpecialProductsAsync(
            string storeCode,
            CancellationToken cancellationToken = default,
            IProgress<SpecialProductDownloadProgress>? progress = null)
        {
            DownloadCallCount++;
            return Task.FromResult(DownloadResult);
        }
    }
}
