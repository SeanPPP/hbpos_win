using System.Security.Claims;
using Hbpos.Api.Auth;
using Hbpos.Api.Controllers;
using Hbpos.Api.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Hbpos.Api.Tests;

public sealed class CatalogControllerTests
{
    [Fact]
    public void SyncEndpoints_KeepExpectedRoutes()
    {
        Assert.Equal("sellable-items/page", GetHttpGetTemplate(nameof(CatalogController.GetSellableItemsPage)));
        Assert.Equal("sellable-items/lookup", GetHttpGetTemplate(nameof(CatalogController.LookupSellableItem)));
        Assert.Equal("sellable-items/compare", GetHttpPostTemplate(nameof(CatalogController.CompareSellableItems)));
        Assert.Equal("special-products/mark", GetHttpPostTemplate(nameof(CatalogController.MarkSpecialProduct)));
        Assert.Equal("sellable-items", GetHttpGetTemplate(nameof(CatalogController.GetSellableItems)));
    }

    [Fact]
    public void CatalogController_RequiresAuthorizationExceptStoreList()
    {
        Assert.NotNull(typeof(CatalogController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .SingleOrDefault());
        Assert.NotNull(typeof(CatalogController)
            .GetMethod(nameof(CatalogController.GetStores))?
            .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: false)
            .SingleOrDefault());
    }

    [Fact]
    public async Task GetSellableItemsPage_ReturnsWrappedServiceResponse()
    {
        var expected = new CatalogSyncPageResponse("S01", DateTimeOffset.UnixEpoch, null, [], [], null, false, 0);
        var service = new FakeCatalogService { PageResponse = expected };
        var controller = new CatalogController(service);

        var result = await controller.GetSellableItemsPage(
            "S01",
            DateTimeOffset.UnixEpoch,
            "cursor-1",
            100,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<CatalogSyncPageResponse>>(ok.Value);
        Assert.True(apiResult.Success);
        Assert.Same(expected, apiResult.Data);
        Assert.Equal(("S01", DateTimeOffset.UnixEpoch, "cursor-1", 100), service.LastPageRequest);
    }

    [Fact]
    public async Task CompareSellableItems_ReturnsBadRequestWhenStoreCodeMissing()
    {
        var controller = new CatalogController(new FakeCatalogService());

        var result = await controller.CompareSellableItems(
            new CatalogCompareRequest("", []),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<CatalogCompareResponse>>(badRequest.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("STORE_CODE_REQUIRED", apiResult.ErrorCode);
    }

    [Fact]
    public async Task LookupSellableItem_RequiresLookupCodeOrNormalizedKey()
    {
        var controller = new CatalogController(new FakeCatalogService());

        var result = await controller.LookupSellableItem("S01", null, null, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<CatalogLookupResponse>>(badRequest.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("LOOKUP_CODE_REQUIRED", apiResult.ErrorCode);
    }

    [Fact]
    public async Task LookupSellableItem_PassesLookupCodeAndNormalizedKey()
    {
        var expected = new CatalogLookupResponse("S01", " abc-01 ", "ABC01", true, null);
        var service = new FakeCatalogService { LookupResponse = expected };
        var controller = new CatalogController(service);

        var result = await controller.LookupSellableItem(
            "S01",
            " abc-01 ",
            "ABC01",
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<CatalogLookupResponse>>(ok.Value);
        Assert.True(apiResult.Success);
        Assert.Same(expected, apiResult.Data);
        Assert.Equal(("S01", " abc-01 ", "ABC01"), service.LastLookupRequest);
    }

    [Fact]
    public async Task LookupSellableItem_ReturnsStoreNotFoundWhenServiceHasNoStoreIndex()
    {
        var controller = new CatalogController(new FakeCatalogService());

        var result = await controller.LookupSellableItem("S01", "missing", null, CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<CatalogLookupResponse>>(notFound.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("STORE_NOT_FOUND", apiResult.ErrorCode);
    }

    [Fact]
    public async Task GetSellableItemsPage_ReturnsForbiddenWhenDeviceStoreDoesNotMatch()
    {
        var controller = new CatalogController(new FakeCatalogService());
        SetAuthenticatedDevice(controller, storeCode: "S02", deviceCode: "POS-02");

        var result = await controller.GetSellableItemsPage(
            "S01",
            since: null,
            cursor: null,
            pageSize: 100,
            CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        var apiResult = Assert.IsType<ApiResult<CatalogSyncPageResponse>>(forbidden.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("DEVICE_SCOPE_FORBIDDEN", apiResult.ErrorCode);
    }

    [Fact]
    public async Task MarkSpecialProduct_ReturnsBadRequestWhenProductCodeMissing()
    {
        var controller = new CatalogController(new FakeCatalogService());
        SetAuthenticatedDevice(controller, storeCode: "S01", deviceCode: "POS-01");

        var result = await controller.MarkSpecialProduct(
            new CatalogSpecialProductMarkRequest("S01", "", true),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<CatalogSpecialProductMarkResponse>>(badRequest.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("PRODUCT_CODE_REQUIRED", apiResult.ErrorCode);
    }

    [Fact]
    public async Task MarkSpecialProduct_ReturnsForbiddenWhenDeviceStoreDoesNotMatch()
    {
        var controller = new CatalogController(new FakeCatalogService());
        SetAuthenticatedDevice(controller, storeCode: "S02", deviceCode: "POS-02");

        var result = await controller.MarkSpecialProduct(
            new CatalogSpecialProductMarkRequest("S01", "P01", true),
            CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        var apiResult = Assert.IsType<ApiResult<CatalogSpecialProductMarkResponse>>(forbidden.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("DEVICE_SCOPE_FORBIDDEN", apiResult.ErrorCode);
    }

    [Fact]
    public async Task MarkSpecialProduct_ReturnsWrappedServiceResponse()
    {
        var service = new FakeCatalogService();
        var controller = new CatalogController(service);
        SetAuthenticatedDevice(controller, storeCode: "S01", deviceCode: "POS-01");

        var result = await controller.MarkSpecialProduct(
            new CatalogSpecialProductMarkRequest("S01", "P01", true),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<CatalogSpecialProductMarkResponse>>(ok.Value);
        Assert.True(apiResult.Success);
        Assert.NotNull(apiResult.Data);
        Assert.Equal("S01", apiResult.Data.StoreCode);
        Assert.Equal("P01", apiResult.Data.ProductCode);
        Assert.True(apiResult.Data.IsSpecialProduct);
        Assert.Equal("POS-01", service.LastSpecialProductUpdatedBy);
        Assert.Equal(new CatalogSpecialProductMarkRequest("S01", "P01", true), service.LastSpecialProductRequest);
    }

    private static string? GetHttpGetTemplate(string methodName)
    {
        return typeof(CatalogController)
            .GetMethod(methodName)?
            .GetCustomAttributes(typeof(HttpGetAttribute), inherit: false)
            .Cast<HttpGetAttribute>()
            .Single()
            .Template;
    }

    private static string? GetHttpPostTemplate(string methodName)
    {
        return typeof(CatalogController)
            .GetMethod(methodName)?
            .GetCustomAttributes(typeof(HttpPostAttribute), inherit: false)
            .Cast<HttpPostAttribute>()
            .Single()
            .Template;
    }

    private static void SetAuthenticatedDevice(
        ControllerBase controller,
        string storeCode,
        string deviceCode)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(DeviceAuthConstants.StoreCodeClaim, storeCode),
            new Claim(DeviceAuthConstants.DeviceCodeClaim, deviceCode),
            new Claim(DeviceAuthConstants.HardwareIdClaim, "HW-001")
        ], DeviceAuthConstants.Scheme);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity)
            }
        };
    }

    private sealed class FakeCatalogService : ICatalogService
    {
        public CatalogSyncPageResponse? PageResponse { get; init; }

        public CatalogCompareResponse? CompareResponse { get; init; }

        public CatalogLookupResponse? LookupResponse { get; init; }

        public (string StoreCode, DateTimeOffset? Since, string? Cursor, int PageSize)? LastPageRequest { get; private set; }

        public CatalogCompareRequest? LastCompareRequest { get; private set; }

        public (string StoreCode, string? LookupCode, string? LookupCodeNormalized)? LastLookupRequest { get; private set; }

        public CatalogSpecialProductMarkRequest? LastSpecialProductRequest { get; private set; }

        public string? LastSpecialProductUpdatedBy { get; private set; }

        public Task<IReadOnlyList<StoreDto>> GetStoresAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<StoreDto>>([]);
        }

        public Task<SellableItemsResponse?> GetSellableItemsAsync(
            string storeCode,
            DateTimeOffset? since,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<SellableItemsResponse?>(null);
        }

        public Task<CatalogSyncPageResponse?> GetSellableItemsPageAsync(
            string storeCode,
            DateTimeOffset? since,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken)
        {
            LastPageRequest = (storeCode, since, cursor, pageSize);
            return Task.FromResult(PageResponse);
        }

        public Task<CatalogCompareResponse?> CompareSellableItemsAsync(
            CatalogCompareRequest request,
            CancellationToken cancellationToken)
        {
            LastCompareRequest = request;
            return Task.FromResult(CompareResponse);
        }

        public Task<CatalogLookupResponse?> LookupSellableItemAsync(
            string storeCode,
            string? lookupCode,
            string? lookupCodeNormalized,
            CancellationToken cancellationToken)
        {
            LastLookupRequest = (storeCode, lookupCode, lookupCodeNormalized);
            return Task.FromResult(LookupResponse);
        }

        public Task<CatalogSpecialProductMarkServiceResult> MarkSpecialProductAsync(
            CatalogSpecialProductMarkRequest request,
            string updatedBy,
            CancellationToken cancellationToken)
        {
            LastSpecialProductRequest = request;
            LastSpecialProductUpdatedBy = updatedBy;
            return Task.FromResult(CatalogSpecialProductMarkServiceResult.Ok(new CatalogSpecialProductMarkResponse(
                request.StoreCode,
                request.ProductCode,
                request.IsSpecialProduct,
                DateTimeOffset.UnixEpoch,
                [])));
        }
    }
}
