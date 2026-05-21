using System.Diagnostics;
using Hbpos.Api.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Common;
using Microsoft.AspNetCore.Mvc;

namespace Hbpos.Api.Controllers;

[ApiController]
[Route("api/v1/catalog")]
public sealed class CatalogController(ICatalogService catalogService) : ControllerBase
{
    private const int MaxPageSize = 1000;

    [HttpGet("stores")]
    public async Task<ActionResult<ApiResult<IReadOnlyList<StoreDto>>>> GetStores(
        CancellationToken cancellationToken)
    {
        var stores = await catalogService.GetStoresAsync(cancellationToken);
        return Ok(ApiResult<IReadOnlyList<StoreDto>>.Ok(stores));
    }

    [HttpGet("sellable-items")]
    public async Task<ActionResult<ApiResult<SellableItemsResponse>>> GetSellableItems(
        [FromQuery] string storeCode,
        [FromQuery] DateTimeOffset? since,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return BadRequest(ApiResult<SellableItemsResponse>.Fail("STORE_CODE_REQUIRED", "storeCode 不能为空"));
        }

        var response = await catalogService.GetSellableItemsAsync(storeCode, since, cancellationToken);
        return response is null
            ? NotFound(ApiResult<SellableItemsResponse>.Fail("STORE_NOT_FOUND", "门店不存在或已停用"))
            : Ok(ApiResult<SellableItemsResponse>.Ok(response));
    }

    [HttpGet("sellable-items/page")]
    public async Task<ActionResult<ApiResult<CatalogSyncPageResponse>>> GetSellableItemsPage(
        [FromQuery] string storeCode,
        [FromQuery] DateTimeOffset? since,
        [FromQuery] string? cursor,
        [FromQuery] int pageSize = 500,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return BadRequest(ApiResult<CatalogSyncPageResponse>.Fail("STORE_CODE_REQUIRED", "storeCode is required"));
        }

        if (pageSize <= 0 || pageSize > MaxPageSize)
        {
            return BadRequest(ApiResult<CatalogSyncPageResponse>.Fail("PAGE_SIZE_INVALID", $"pageSize must be between 1 and {MaxPageSize}"));
        }

        var stopwatch = Stopwatch.StartNew();
        Log($"page request store={storeCode} cursor={cursor ?? "<start>"} pageSize={pageSize}");
        var response = await catalogService.GetSellableItemsPageAsync(
            storeCode,
            since,
            cursor,
            pageSize,
            cancellationToken);
        stopwatch.Stop();
        Log(response is null
            ? $"page response store={storeCode} status=404 elapsedMs={stopwatch.ElapsedMilliseconds}"
            : $"page response store={response.StoreCode} status=200 items={response.Items.Count} deletedLookups={response.DeletedLookups.Count} hasMore={response.HasMore} next={response.NextCursor ?? "<end>"} elapsedMs={stopwatch.ElapsedMilliseconds}");

        return response is null
            ? NotFound(ApiResult<CatalogSyncPageResponse>.Fail("STORE_NOT_FOUND", "store was not found or inactive"))
            : Ok(ApiResult<CatalogSyncPageResponse>.Ok(response));
    }

    [HttpPost("sellable-items/compare")]
    public async Task<ActionResult<ApiResult<CatalogCompareResponse>>> CompareSellableItems(
        [FromBody] CatalogCompareRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(ApiResult<CatalogCompareResponse>.Fail("COMPARE_REQUEST_REQUIRED", "request body is required"));
        }

        if (string.IsNullOrWhiteSpace(request.StoreCode))
        {
            return BadRequest(ApiResult<CatalogCompareResponse>.Fail("STORE_CODE_REQUIRED", "storeCode is required"));
        }

        var stopwatch = Stopwatch.StartNew();
        Log($"compare request store={request.StoreCode} localLookups={request.LocalLookups.Count}");
        var response = await catalogService.CompareSellableItemsAsync(request, cancellationToken);
        stopwatch.Stop();
        Log(response is null
            ? $"compare response store={request.StoreCode} status=404 elapsedMs={stopwatch.ElapsedMilliseconds}"
            : $"compare response store={response.StoreCode} status=200 upsertedLookups={response.UpsertedLookups.Count} deletedLookups={response.DeletedLookups.Count} hasMore={response.HasMore} elapsedMs={stopwatch.ElapsedMilliseconds}");
        return response is null
            ? NotFound(ApiResult<CatalogCompareResponse>.Fail("STORE_NOT_FOUND", "store was not found or inactive"))
            : Ok(ApiResult<CatalogCompareResponse>.Ok(response));
    }

    [HttpGet("sellable-items/lookup")]
    public async Task<ActionResult<ApiResult<CatalogLookupResponse>>> LookupSellableItem(
        [FromQuery] string storeCode,
        [FromQuery] string? lookupCode,
        [FromQuery] string? lookupCodeNormalized,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return BadRequest(ApiResult<CatalogLookupResponse>.Fail("STORE_CODE_REQUIRED", "storeCode is required"));
        }

        if (string.IsNullOrWhiteSpace(lookupCode) && string.IsNullOrWhiteSpace(lookupCodeNormalized))
        {
            return BadRequest(ApiResult<CatalogLookupResponse>.Fail("LOOKUP_CODE_REQUIRED", "lookupCode or lookupCodeNormalized is required"));
        }

        var stopwatch = Stopwatch.StartNew();
        Log($"lookup request store={storeCode} lookupCode={lookupCode ?? "<null>"} lookupCodeNormalized={lookupCodeNormalized ?? "<null>"}");
        var response = await catalogService.LookupSellableItemAsync(
            storeCode,
            lookupCode,
            lookupCodeNormalized,
            cancellationToken);
        stopwatch.Stop();
        Log(response is null
            ? $"lookup response store={storeCode} status=404 elapsedMs={stopwatch.ElapsedMilliseconds}"
            : $"lookup response store={response.StoreCode} status=200 found={response.Found} elapsedMs={stopwatch.ElapsedMilliseconds}");

        return response is null
            ? NotFound(ApiResult<CatalogLookupResponse>.Fail("STORE_NOT_FOUND", "store was not found or inactive"))
            : Ok(ApiResult<CatalogLookupResponse>.Ok(response));
    }

    private static void Log(string message)
    {
        Console.WriteLine($"[HBPOS][Api][Catalog] {DateTimeOffset.Now:O} {message}");
    }
}
