using System.Security.Claims;
using Hbpos.Api.Auth;
using Hbpos.Api.Controllers;
using Hbpos.Api.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.Orders;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Hbpos.Api.Tests;

public sealed class OrdersControllerTests
{
    [Fact]
    public async Task Sync_AllowsZeroActualAmountWithoutPayments()
    {
        var service = new FakeOrderSyncService();
        var controller = new OrdersController(service, new FakeOrderHistoryService(), new FakeOrderReturnService());
        SetAuthenticatedDevice(controller, "S01", "POS01");
        var request = CreateRequest(actualAmount: 0m, payments: []);

        var result = await controller.Sync(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<OrderSyncResponse>>(ok.Value);
        Assert.True(apiResult.Success);
        Assert.NotNull(service.LastRequest);
        Assert.Empty(service.LastRequest!.Payments);
    }

    [Fact]
    public async Task Sync_RejectsNonZeroActualAmountWithoutPayments()
    {
        var controller = new OrdersController(new FakeOrderSyncService(), new FakeOrderHistoryService(), new FakeOrderReturnService());
        SetAuthenticatedDevice(controller, "S01", "POS01");
        var request = CreateRequest(actualAmount: 1m, payments: []);

        var result = await controller.Sync(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<OrderSyncResponse>>(badRequest.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("ORDER_PAYMENTS_REQUIRED", apiResult.ErrorCode);
    }

    private static OrderSyncRequest CreateRequest(decimal actualAmount, IReadOnlyList<PaymentSyncDto> payments)
    {
        return new OrderSyncRequest(
            Guid.NewGuid(),
            "S01",
            "POS01",
            "C01",
            "Cashier",
            DateTimeOffset.Parse("2026-05-21T10:00:00Z"),
            10m,
            0m,
            actualAmount,
            [
                new OrderLineSyncDto(
                    Guid.NewGuid(),
                    "P01",
                    "SOURCE-GUID-01",
                    "Apple",
                    "BAR01",
                    1m,
                    10m,
                    0m,
                    10m,
                    PriceSourceKind.StoreRetailPrice)
            ],
            payments);
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

    private sealed class FakeOrderSyncService : IOrderSyncService
    {
        public OrderSyncRequest? LastRequest { get; private set; }

        public Task<OrderSyncResponse> SyncAsync(OrderSyncRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new OrderSyncResponse(request.OrderGuid, true, false, "Synced"));
        }
    }

    private sealed class FakeOrderHistoryService : IOrderHistoryService
    {
        public Task<OrderHistoryQueryResponse> QueryAsync(OrderHistoryQueryRequest request, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<OrderHistoryDetailsDto?> GetDetailsAsync(Guid orderGuid, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeOrderReturnService : IOrderReturnService
    {
        public Task<OrderReturnContextDto?> GetReturnContextAsync(Guid orderGuid, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<OrderReturnRecordCreateResponse> CreateRecordsAsync(OrderReturnRecordCreateRequest request, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }
}
