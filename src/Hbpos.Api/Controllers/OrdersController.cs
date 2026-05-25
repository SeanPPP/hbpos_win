using Hbpos.Api.Auth;
using Hbpos.Api.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Orders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hbpos.Api.Controllers;

[ApiController]
[Route("api/v1/orders")]
public sealed class OrdersController(
    IOrderSyncService orderSyncService,
    IOrderHistoryService orderHistoryService) : ControllerBase
{
    [Authorize]
    [HttpPost("sync")]
    public async Task<ActionResult<ApiResult<OrderSyncResponse>>> Sync(
        [FromBody] OrderSyncRequest request,
        CancellationToken cancellationToken)
    {
        if (!this.IsDeviceScopeAllowed(request.StoreCode, request.DeviceCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<OrderSyncResponse>("Device is not authorized for this store.");
        }

        if (request.Lines.Count == 0)
        {
            return BadRequest(ApiResult<OrderSyncResponse>.Fail("ORDER_LINES_REQUIRED", "订单明细不能为空"));
        }

        if (request.Payments.Count == 0)
        {
            return BadRequest(ApiResult<OrderSyncResponse>.Fail("ORDER_PAYMENTS_REQUIRED", "订单付款不能为空"));
        }

        var response = await orderSyncService.SyncAsync(request, cancellationToken);
        return Ok(ApiResult<OrderSyncResponse>.Ok(response));
    }

    [Authorize]
    [HttpGet("history")]
    public async Task<ActionResult<ApiResult<OrderHistoryQueryResponse>>> History(
        [FromQuery] string storeCode,
        [FromQuery] string? deviceCode,
        [FromQuery] DateTimeOffset? soldFrom,
        [FromQuery] DateTimeOffset? soldTo,
        [FromQuery] string? keyword,
        [FromQuery] int take,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return BadRequest(ApiResult<OrderHistoryQueryResponse>.Fail("STORE_CODE_REQUIRED", "Store code is required."));
        }

        if (!this.IsDeviceScopeAllowed(storeCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<OrderHistoryQueryResponse>("Device is not authorized for this store.");
        }

        var response = await orderHistoryService.QueryAsync(
            new OrderHistoryQueryRequest(storeCode, deviceCode, soldFrom, soldTo, keyword, take <= 0 ? 100 : take),
            cancellationToken);
        return Ok(ApiResult<OrderHistoryQueryResponse>.Ok(response));
    }

    [Authorize]
    [HttpGet("history/{orderGuid:guid}")]
    public async Task<ActionResult<ApiResult<OrderHistoryDetailsDto?>>> HistoryDetails(
        Guid orderGuid,
        CancellationToken cancellationToken)
    {
        var details = await orderHistoryService.GetDetailsAsync(orderGuid, cancellationToken);
        if (details is null)
        {
            return Ok(ApiResult<OrderHistoryDetailsDto?>.Ok(null));
        }

        if (!this.IsDeviceScopeAllowed(details.StoreCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<OrderHistoryDetailsDto?>("Device is not authorized for this store.");
        }

        return Ok(ApiResult<OrderHistoryDetailsDto?>.Ok(details));
    }
}
