using System.Security.Claims;
using Hbpos.Api.Auth;
using Hbpos.Api.Controllers;
using Hbpos.Api.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.Vouchers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Hbpos.Api.Tests;

public sealed class VouchersControllerTests
{
    [Fact]
    public void VoucherEndpoints_KeepExpectedRoutes()
    {
        Assert.Equal("{voucherCode}", GetHttpGetTemplate(nameof(VouchersController.Get)));
        Assert.Equal("lock", GetHttpPostTemplate(nameof(VouchersController.Lock)));
        Assert.Equal("refund", GetHttpPostTemplate(nameof(VouchersController.IssueRefund)));
        Assert.Equal("issue", GetHttpPostTemplate(nameof(VouchersController.Issue)));
        Assert.NotNull(typeof(VouchersController)
            .GetCustomAttributes(typeof(ApiControllerAttribute), inherit: false)
            .SingleOrDefault());
        Assert.NotNull(typeof(VouchersController)
            .GetMethod(nameof(VouchersController.Get))?
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .SingleOrDefault());
    }

    [Fact]
    public async Task Get_ReturnsWrappedVoucherResponse()
    {
        var service = new FakeStoreVoucherService
        {
            QueryResponse = new StoreVoucherQueryResponse(
                true,
                new StoreVoucherDto("V001", "S01", 3, 20m, 12m, "1", DateTimeOffset.UtcNow.AddDays(1), null, 0m, null))
        };
        var controller = new VouchersController(service);
        SetAuthenticatedDevice(controller, "S01", "POS-01");

        var result = await controller.Get("V001", "S01", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<StoreVoucherQueryResponse>>(ok.Value);
        Assert.True(apiResult.Success);
        Assert.NotNull(apiResult.Data);
        Assert.Equal("V001", apiResult.Data.Voucher?.VoucherCode);
    }

    [Fact]
    public async Task Lock_ReturnsForbiddenWhenDeviceStoreDoesNotMatch()
    {
        var controller = new VouchersController(new FakeStoreVoucherService());
        SetAuthenticatedDevice(controller, "S02", "POS-02");

        var result = await controller.Lock(new StoreVoucherLockRequest("S01", "V001", 5m), CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        var apiResult = Assert.IsType<ApiResult<StoreVoucherLockResponse>>(forbidden.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("DEVICE_SCOPE_FORBIDDEN", apiResult.ErrorCode);
    }

    [Fact]
    public async Task Lock_ReturnsBadRequestWhenRequestedAmountInvalid()
    {
        var controller = new VouchersController(new FakeStoreVoucherService());
        SetAuthenticatedDevice(controller, "S01", "POS-01");

        var result = await controller.Lock(new StoreVoucherLockRequest("S01", "V001", 0m), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<StoreVoucherLockResponse>>(badRequest.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("REQUESTED_AMOUNT_INVALID", apiResult.ErrorCode);
    }

    [Fact]
    public async Task IssueRefund_ReturnsWrappedVoucherResponse()
    {
        var controller = new VouchersController(new FakeStoreVoucherService());
        SetAuthenticatedDevice(controller, "S01", "POS-01");

        var result = await controller.IssueRefund(
            new StoreVoucherIssueRefundRequest("S01", 15m, "C001", IdempotencyKey: "ORDER-1:PAY-1", OrderReference: "ORDER-1", Reason: "Refund"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<StoreVoucherIssueRefundResponse>>(ok.Value);
        Assert.True(apiResult.Success);
        Assert.NotNull(apiResult.Data);
        Assert.Equal("RF001", apiResult.Data.VoucherCode);
    }

    [Fact]
    public async Task IssueRefund_ReturnsBadRequestWhenIdempotencyKeyMissing()
    {
        var controller = new VouchersController(new FakeStoreVoucherService());
        SetAuthenticatedDevice(controller, "S01", "POS-01");

        var result = await controller.IssueRefund(
            new StoreVoucherIssueRefundRequest("S01", 15m, "C001", OrderReference: "ORDER-1", Reason: "Refund"),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<StoreVoucherIssueRefundResponse>>(badRequest.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", apiResult.ErrorCode);
    }

    [Fact]
    public async Task Issue_ReturnsWrappedVoucherResponse()
    {
        var controller = new VouchersController(new FakeStoreVoucherService());
        SetAuthenticatedDevice(controller, "S01", "POS-01");

        var result = await controller.Issue(
            new StoreVoucherIssueRequest("S01", 20m, "C001", "ISSUE-1", CustomerCode: "CUS001", Reason: "Manual issue"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<StoreVoucherIssueResponse>>(ok.Value);
        Assert.True(apiResult.Success);
        Assert.NotNull(apiResult.Data);
        Assert.Equal("VC001", apiResult.Data.VoucherCode);
        Assert.Equal("CUS001", apiResult.Data.CustomerCode);
    }

    [Fact]
    public async Task Issue_ReturnsForbiddenWhenDeviceStoreDoesNotMatch()
    {
        var controller = new VouchersController(new FakeStoreVoucherService());
        SetAuthenticatedDevice(controller, "S02", "POS-02");

        var result = await controller.Issue(
            new StoreVoucherIssueRequest("S01", 20m, "C001", "ISSUE-1"),
            CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        var apiResult = Assert.IsType<ApiResult<StoreVoucherIssueResponse>>(forbidden.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("DEVICE_SCOPE_FORBIDDEN", apiResult.ErrorCode);
    }

    [Fact]
    public async Task Issue_ReturnsBadRequestWhenIdempotencyKeyMissing()
    {
        var controller = new VouchersController(new FakeStoreVoucherService());
        SetAuthenticatedDevice(controller, "S01", "POS-01");

        var result = await controller.Issue(
            new StoreVoucherIssueRequest("S01", 20m, "C001", string.Empty),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<StoreVoucherIssueResponse>>(badRequest.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", apiResult.ErrorCode);
    }

    [Fact]
    public async Task Issue_ReturnsBadRequestWhenStoreCodeMissing()
    {
        var controller = new VouchersController(new FakeStoreVoucherService());
        SetAuthenticatedDevice(controller, "S01", "POS-01");

        var result = await controller.Issue(
            new StoreVoucherIssueRequest(string.Empty, 20m, "C001", "ISSUE-1"),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<StoreVoucherIssueResponse>>(badRequest.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("STORE_CODE_REQUIRED", apiResult.ErrorCode);
    }

    private static string? GetHttpGetTemplate(string methodName)
    {
        return typeof(VouchersController)
            .GetMethod(methodName)?
            .GetCustomAttributes(typeof(HttpGetAttribute), inherit: false)
            .Cast<HttpGetAttribute>()
            .Single()
            .Template;
    }

    private static string? GetHttpPostTemplate(string methodName)
    {
        return typeof(VouchersController)
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

    private sealed class FakeStoreVoucherService : IStoreVoucherService
    {
        public StoreVoucherQueryResponse QueryResponse { get; init; } = new(false, null, "VoucherNotFound");

        public StoreVoucherLockResponse LockResponse { get; init; } =
            new("V001", 5m, "token-1", DateTimeOffset.UtcNow.AddMinutes(5));

        public StoreVoucherIssueRefundResponse RefundResponse { get; init; } =
            new("RF001", 15m, 15m, "1", DateTimeOffset.UtcNow.AddMonths(12));

        public StoreVoucherIssueResponse IssueResponse { get; init; } =
            new("VC001", 20m, 20m, "1", DateTimeOffset.UtcNow.AddMonths(12), "S01", "CUS001");

        public Task<StoreVoucherQueryResponse> QueryAsync(
            string storeCode,
            string voucherCode,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(QueryResponse);
        }

        public Task<StoreVoucherLockResponse> LockAsync(
            StoreVoucherLockRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(LockResponse);
        }

        public Task<StoreVoucherIssueRefundResponse> IssueRefundAsync(
            StoreVoucherIssueRefundRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(RefundResponse);
        }

        public Task<StoreVoucherIssueResponse> IssueAsync(
            StoreVoucherIssueRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(IssueResponse);
        }
    }
}
