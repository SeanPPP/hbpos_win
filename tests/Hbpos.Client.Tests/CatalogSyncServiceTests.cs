using System.Net;
using System.Text;
using System.Text.Json;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Health;
using Hbpos.Contracts.Devices;

namespace Hbpos.Client.Tests;

public sealed class CatalogSyncServiceTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public async Task FullSyncAsync_WhenCompareFails_DoesNotDeleteLocalCatalog()
    {
        var repository = new FakeLocalCatalogRepository();
        repository.ComparePages.Enqueue(
        [
            new LocalSellableItemCompareRow("S01", "LOCAL-CODE", "local-hash", Timestamp)
        ]);
        var apiClient = new FakeCatalogApiClient
        {
            CompareException = new CatalogApiException("remote compare failed")
        };
        var service = new LocalCatalogSyncService(repository, apiClient);

        await Assert.ThrowsAsync<CatalogApiException>(() => service.FullSyncAsync("S01"));

        Assert.Empty(repository.UpsertedBatches);
        Assert.Empty(repository.DeleteCalls);
    }

    [Fact]
    public async Task RefreshLookupAsync_WhenRemoteReturnsNotFound_DeletesOnlyThatLookup()
    {
        var repository = new FakeLocalCatalogRepository();
        var apiClient = new FakeCatalogApiClient
        {
            LookupResponse = new CatalogLookupResponse("S01", " abc ", "ABC", Found: false, Item: null)
        };
        var service = new RemoteLookupRefreshService(repository, apiClient);

        var result = await service.RefreshLookupAsync("S01", " abc ");

        Assert.False(result.Found);
        Assert.True(result.Deleted);
        Assert.Empty(repository.UpsertedBatches);
        var deleteCall = Assert.Single(repository.DeleteCalls);
        Assert.Equal("S01", deleteCall.StoreCode);
        Assert.Equal(["ABC"], deleteCall.LookupCodes);
    }

    [Fact]
    public async Task RefreshLookupAsync_WhenRemoteFails_DoesNotDeleteLocalCatalog()
    {
        var repository = new FakeLocalCatalogRepository();
        var apiClient = new FakeCatalogApiClient
        {
            LookupException = new CatalogApiException("store route missing", HttpStatusCode.NotFound, "STORE_NOT_FOUND")
        };
        var service = new RemoteLookupRefreshService(repository, apiClient);

        await Assert.ThrowsAsync<CatalogApiException>(() => service.RefreshLookupAsync("S01", "abc"));

        Assert.Empty(repository.UpsertedBatches);
        Assert.Empty(repository.DeleteCalls);
    }

    [Fact]
    public async Task RefreshLookupAsync_WhenRemoteLookupIsCanceled_DoesNotDeleteLocalCatalog()
    {
        var repository = new FakeLocalCatalogRepository();
        var apiClient = new FakeCatalogApiClient
        {
            LookupException = new OperationCanceledException("lookup timed out")
        };
        var service = new RemoteLookupRefreshService(repository, apiClient);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.RefreshLookupAsync("S01", "abc"));

        Assert.Empty(repository.UpsertedBatches);
        Assert.Empty(repository.DeleteCalls);
    }

    [Fact]
    public async Task FullSyncAsync_AppliesCompareAndRemotePageUpsertsAndDeletes()
    {
        var repository = new FakeLocalCatalogRepository();
        repository.ComparePages.Enqueue(
        [
            new LocalSellableItemCompareRow("S01", "LOCAL-CODE", "local-hash", Timestamp)
        ]);
        repository.ComparePages.Enqueue([]);

        var apiClient = new FakeCatalogApiClient();
        apiClient.CompareResponses.Enqueue(new CatalogCompareResponse(
            "S01",
            Timestamp,
            [CreateLookupItem("CMP-001", "cmp-code", "CMP-REF-001")],
            [CreateDeletedLookup("old-code")],
            NextCursor: null,
            HasMore: false));
        apiClient.PageResponses.Enqueue(new CatalogSyncPageResponse(
            "S01",
            Timestamp,
            Cursor: null,
            [CreateLookupItem("PAGE-001", "page-code", "PAGE-REF-001")],
            [CreateDeletedLookup("gone-code")],
            NextCursor: null,
            HasMore: false,
            TotalCount: 1));
        var service = new LocalCatalogSyncService(repository, apiClient);

        var result = await service.FullSyncAsync("S01");

        Assert.Equal(new LocalCatalogSyncResult("S01", ComparePages: 1, RemotePages: 1, UpsertedCount: 2, DeletedCount: 2), result);
        Assert.Equal(2, repository.UpsertedBatches.Count);
        var compareUpsert = Assert.Single(repository.UpsertedBatches[0]);
        var pageUpsert = Assert.Single(repository.UpsertedBatches[1]);
        Assert.Equal("CMP-001", compareUpsert.ProductCode);
        Assert.Equal("CMP-REF-001", compareUpsert.ReferenceCode);
        Assert.Equal("https://images.example/CMP-001.jpg", compareUpsert.ProductImage);
        Assert.Equal(0.2m, compareUpsert.DiscountRate);
        Assert.Equal("PAGE-001", pageUpsert.ProductCode);
        Assert.Equal("PAGE-REF-001", pageUpsert.ReferenceCode);
        Assert.Equal("https://images.example/PAGE-001.jpg", pageUpsert.ProductImage);
        Assert.Equal(0.2m, pageUpsert.DiscountRate);
        Assert.Equal(["OLD-CODE"], repository.DeleteCalls[0].LookupCodes);
        Assert.Equal(["GONE-CODE"], repository.DeleteCalls[1].LookupCodes);
        Assert.NotNull(apiClient.LastCompareRequest);
        var localVersion = Assert.Single(apiClient.LastCompareRequest.LocalLookups);
        Assert.Equal("LOCAL-CODE", localVersion.LookupCodeNormalized);
        Assert.Equal("local-hash", localVersion.RowVersion);
    }

    [Fact]
    public async Task FullSyncAsync_ReportsDownloadProgressWithPercentAndTotals()
    {
        var repository = new FakeLocalCatalogRepository();
        repository.ComparePages.Enqueue([]);
        var apiClient = new FakeCatalogApiClient();
        apiClient.PageResponses.Enqueue(new CatalogSyncPageResponse(
            "S01",
            Timestamp,
            Cursor: null,
            [CreateLookupItem("PAGE-001", "page-code-1")],
            [],
            NextCursor: "PAGE-CODE-1",
            HasMore: true,
            TotalCount: 2));
        apiClient.PageResponses.Enqueue(new CatalogSyncPageResponse(
            "S01",
            Timestamp,
            Cursor: "PAGE-CODE-1",
            [CreateLookupItem("PAGE-002", "page-code-2")],
            [CreateDeletedLookup("gone-code")],
            NextCursor: null,
            HasMore: false,
            TotalCount: 2));
        var service = new LocalCatalogSyncService(repository, apiClient);
        var progressReports = new List<CatalogSyncProgress>();
        var progress = new CapturingProgress<CatalogSyncProgress>(progressReports);

        await service.FullSyncAsync("S01", progress: progress);

        Assert.Contains(progressReports, report =>
            report.Stage == CatalogSyncProgressStage.Downloading &&
            report.DownloadedCount == 1 &&
            report.TotalCount == 2 &&
            report.Percent == 50);
        var completed = Assert.Single(progressReports.Where(report => report.Stage == CatalogSyncProgressStage.Completed));
        Assert.Equal(100, completed.Percent);
        Assert.Equal(2, completed.DownloadedCount);
        Assert.Equal(2, completed.TotalCount);
        Assert.Equal(2, completed.RemotePages);
        Assert.Equal(2, completed.UpsertedCount);
        Assert.Equal(1, completed.DeletedCount);
    }

    [Fact]
    public async Task FullSyncAsync_RequestsRemotePagesWithMaxBatchSize()
    {
        var repository = new FakeLocalCatalogRepository();
        repository.ComparePages.Enqueue([]);
        var apiClient = new FakeCatalogApiClient();
        apiClient.PageResponses.Enqueue(new CatalogSyncPageResponse(
            "S01",
            Timestamp,
            Cursor: null,
            [],
            [],
            NextCursor: null,
            HasMore: false,
            TotalCount: 0));
        var service = new LocalCatalogSyncService(repository, apiClient);

        await service.FullSyncAsync("S01");

        var pageRequest = Assert.Single(apiClient.PageRequests);
        Assert.Equal(("S01", null, 1000), pageRequest);
    }

    [Fact]
    public async Task CatalogApiClient_CompareSellableItemsAsync_PostsJsonAndUnwrapsApiResult()
    {
        HttpRequestMessage? capturedRequest = null;
        var expected = new CatalogCompareResponse(
            "S01",
            Timestamp,
            [CreateLookupItem("CMP-001", "cmp-code")],
            [],
            NextCursor: null,
            HasMore: false);
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = request;
            return JsonResponse(ApiResult<CatalogCompareResponse>.Ok(expected));
        });
        var client = new CatalogApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        });

        var response = await client.CompareSellableItemsAsync(new CatalogCompareRequest("S01", []));

        Assert.Equal(HttpMethod.Post, capturedRequest?.Method);
        Assert.Equal("http://localhost:5000/api/v1/catalog/sellable-items/compare", capturedRequest?.RequestUri?.ToString());
        Assert.Equal("S01", response.StoreCode);
        Assert.Equal("CMP-001", Assert.Single(response.UpsertedLookups).ProductCode);
    }

    [Fact]
    public async Task CatalogApiClient_MarkSpecialProductAsync_PostsJsonAndUnwrapsApiResult()
    {
        HttpRequestMessage? capturedRequest = null;
        var expected = new CatalogSpecialProductMarkResponse(
            "S01",
            "P01",
            true,
            Timestamp,
            [CreateLookupItem("P01", "p01-code")]);
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = request;
            return JsonResponse(ApiResult<CatalogSpecialProductMarkResponse>.Ok(expected));
        });
        var client = new CatalogApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        });

        var response = await client.MarkSpecialProductAsync(new CatalogSpecialProductMarkRequest("S01", "P01", true));

        Assert.Equal(HttpMethod.Post, capturedRequest?.Method);
        Assert.Equal("http://localhost:5000/api/v1/catalog/special-products/mark", capturedRequest?.RequestUri?.ToString());
        Assert.Equal("P01", response.ProductCode);
        Assert.True(response.IsSpecialProduct);
        Assert.Equal("P01", Assert.Single(response.Items).ProductCode);
    }

    [Fact]
    public async Task DeviceAuthorizationMessageHandler_AddsBearerAndDeviceHeaders()
    {
        HttpRequestMessage? capturedRequest = null;
        var state = new DeviceAuthorizationState();
        state.Set(new DeviceAuthorizationContext("POS-001", "S01", "HW-001", "AUTH-001"));
        var handler = new DeviceAuthorizationMessageHandler(state)
        {
            InnerHandler = new StubHttpMessageHandler((request, _) =>
            {
                capturedRequest = request;
                return JsonResponse(ApiResult<CatalogSyncPageResponse>.Ok(new CatalogSyncPageResponse(
                    "S01",
                    Timestamp,
                    Cursor: null,
                    [],
                    [],
                    NextCursor: null,
                    HasMore: false,
                    TotalCount: 0)));
            })
        };
        var client = new CatalogApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        });

        await client.GetSellableItemsPageAsync("S01", cursor: null, pageSize: 100);

        Assert.Equal("Bearer", capturedRequest?.Headers.Authorization?.Scheme);
        Assert.Equal("AUTH-001", capturedRequest?.Headers.Authorization?.Parameter);
        Assert.Equal("POS-001", capturedRequest?.Headers.GetValues(DeviceAuthConstants.DeviceCodeHeader).Single());
        Assert.Equal("S01", capturedRequest?.Headers.GetValues(DeviceAuthConstants.StoreCodeHeader).Single());
        Assert.Equal("HW-001", capturedRequest?.Headers.GetValues(DeviceAuthConstants.HardwareIdHeader).Single());
    }

    [Fact]
    public async Task CatalogApiClient_LookupSellableItemAsync_ThrowsForStoreNotFound404()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            JsonResponse(
                ApiResult<CatalogLookupResponse>.Fail("STORE_NOT_FOUND", "store was not found or inactive"),
                HttpStatusCode.NotFound));
        var client = new CatalogApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        });

        var ex = await Assert.ThrowsAsync<CatalogApiException>(() =>
            client.LookupSellableItemAsync("S01", "abc"));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal("STORE_NOT_FOUND", ex.ErrorCode);
    }

    [Fact]
    public async Task CatalogApiClient_LookupSellableItemAsync_ReturnsNullOnlyForLookupNotFound404()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            JsonResponse(
                ApiResult<CatalogLookupResponse>.Fail("LOOKUP_NOT_FOUND", "lookup was not found"),
                HttpStatusCode.NotFound));
        var client = new CatalogApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        });

        var response = await client.LookupSellableItemAsync("S01", "abc");

        Assert.Null(response);
    }

    [Fact]
    public async Task CatalogApiClient_LookupSellableItemAsync_PropagatesCancellation()
    {
        var handler = new StubHttpMessageHandler((_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return JsonResponse(ApiResult<CatalogLookupResponse>.Ok(new CatalogLookupResponse("S01", "abc", "ABC", true, null)));
        });
        var client = new CatalogApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.LookupSellableItemAsync("S01", "abc", cts.Token));
    }

    [Fact]
    public async Task ConnectivityApiClient_CheckOnlineAsync_ReturnsTrueWhenHealthEndpointSucceeds()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = request;
            return JsonResponse(ApiResult<HealthCheckResponse>.Ok(
                new HealthCheckResponse(true, DateTimeOffset.UnixEpoch, "ok")));
        });
        var client = new ConnectivityApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        });

        var isOnline = await client.CheckOnlineAsync();

        Assert.True(isOnline);
        Assert.Equal(HttpMethod.Get, capturedRequest?.Method);
        Assert.Equal("http://localhost:5000/api/v1/health", capturedRequest?.RequestUri?.ToString());
    }

    [Fact]
    public async Task ConnectivityApiClient_CheckOnlineAsync_ReturnsFalseWhenHealthEndpointFails()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var client = new ConnectivityApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        });

        var isOnline = await client.CheckOnlineAsync();

        Assert.False(isOnline);
    }

    private static CatalogLookupItemDto CreateLookupItem(string productCode, string lookupCode, string? referenceCode = null)
    {
        var normalizedLookupCode = lookupCode.Trim().ToUpperInvariant();
        return new CatalogLookupItemDto(
            "S01",
            productCode,
            ReferenceCode: referenceCode,
            $"{productCode} item",
            lookupCode,
            normalizedLookupCode,
            ItemNumber: productCode,
            Barcode: lookupCode,
            RetailPrice: 12.34m,
            PriceSourceKind.StoreRetailPrice,
            "store-retail",
            QuantityFactor: 1m,
            UpdatedAt: Timestamp,
            RowVersion: $"row-{productCode}",
            ProductImage: $"https://images.example/{productCode}.jpg",
            DiscountRate: 0.2m);
    }

    private static DeletedLookupDto CreateDeletedLookup(string lookupCode)
    {
        return new DeletedLookupDto(
            "S01",
            lookupCode,
            lookupCode.Trim().ToUpperInvariant(),
            Timestamp);
    }

    private static HttpResponseMessage JsonResponse<T>(T value, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Encoding.UTF8,
                "application/json")
        };
    }

    private sealed class FakeLocalCatalogRepository : ILocalCatalogRepository
    {
        public Queue<IReadOnlyList<LocalSellableItemCompareRow>> ComparePages { get; } = new();

        public List<IReadOnlyList<SellableItemDto>> UpsertedBatches { get; } = [];

        public List<(string StoreCode, string[] LookupCodes)> DeleteCalls { get; } = [];

        public Task ReplaceSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default)
        {
            return UpsertSellableItemsAsync(items, cancellationToken);
        }

        public Task UpsertSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default)
        {
            UpsertedBatches.Add(items.ToArray());
            return Task.CompletedTask;
        }

        public Task<int> DeleteByLookupCodesAsync(
            string storeCode,
            IEnumerable<string> lookupCodes,
            CancellationToken cancellationToken = default)
        {
            var materializedCodes = lookupCodes.ToArray();
            DeleteCalls.Add((storeCode, materializedCodes));
            return Task.FromResult(materializedCodes.Length);
        }

        public Task<SellableItemDto?> FindByLookupCodeAsync(
            string storeCode,
            string lookupCode,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SellableItemDto?>(null);
        }

        public Task<IReadOnlyList<SellableItemDto>> LoadSpecialProductItemsAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SellableItemDto>>([]);
        }

        public Task SaveSpecialProductOrderAsync(
            string storeCode,
            IEnumerable<string> productCodes,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<int> UpdateSpecialProductFlagAsync(
            string storeCode,
            string productCode,
            bool isSpecialProduct,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<IReadOnlyList<LocalSellableItemCompareRow>> LoadSellableItemComparePageAsync(
            string storeCode,
            string? afterLookupCodeNormalized,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ComparePages.Count == 0 ? [] : ComparePages.Dequeue());
        }

        public Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SellableItemDto>>([]);
        }
    }

    private sealed class FakeCatalogApiClient : ICatalogApiClient
    {
        public Queue<CatalogSyncPageResponse> PageResponses { get; } = new();

        public Queue<CatalogCompareResponse> CompareResponses { get; } = new();

        public List<(string StoreCode, string? Cursor, int PageSize)> PageRequests { get; } = [];

        public Exception? CompareException { get; init; }

        public Exception? LookupException { get; init; }

        public CatalogLookupResponse? LookupResponse { get; init; }

        public CatalogCompareRequest? LastCompareRequest { get; private set; }

        public Task<CatalogSyncPageResponse> GetSellableItemsPageAsync(
            string storeCode,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            PageRequests.Add((storeCode, cursor, pageSize));
            return Task.FromResult(PageResponses.Dequeue());
        }

        public Task<CatalogCompareResponse> CompareSellableItemsAsync(
            CatalogCompareRequest request,
            CancellationToken cancellationToken = default)
        {
            LastCompareRequest = request;
            return CompareException is not null
                ? Task.FromException<CatalogCompareResponse>(CompareException)
                : Task.FromResult(CompareResponses.Dequeue());
        }

        public Task<CatalogLookupResponse?> LookupSellableItemAsync(
            string storeCode,
            string lookupCode,
            CancellationToken cancellationToken = default)
        {
            return LookupException is not null
                ? Task.FromException<CatalogLookupResponse?>(LookupException)
                : Task.FromResult(LookupResponse);
        }

        public Task<CatalogSpecialProductMarkResponse> MarkSpecialProductAsync(
            CatalogSpecialProductMarkRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CatalogSpecialProductMarkResponse(
                request.StoreCode,
                request.ProductCode,
                request.IsSpecialProduct,
                DateTimeOffset.UtcNow,
                []));
        }
    }

    private sealed class CapturingProgress<T>(ICollection<T> reports) : IProgress<T>
    {
        public void Report(T value)
        {
            reports.Add(value);
        }
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request, cancellationToken));
        }
    }
}
