using Hbpos.Api.Auth;
using Hbpos.Api.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Installments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hbpos.Api.Controllers;

[ApiController]
[Route("api/v1/installments")]
public sealed class InstallmentsController(
    IInstallmentService installmentService,
    IInstallmentHistoryService historyService) : ControllerBase
{
    [Authorize]
    [HttpPost]
    public async Task<ActionResult<ApiResult<InstallmentCreateResponse>>> Create(
        [FromBody] InstallmentCreateRequest request,
        CancellationToken cancellationToken)
    {
        if (!this.IsDeviceScopeAllowed(request.StoreCode, request.DeviceCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<InstallmentCreateResponse>("Device is not authorized for this store.");
        }

        try
        {
            var response = await installmentService.CreateAsync(request, cancellationToken);
            return Ok(ApiResult<InstallmentCreateResponse>.Ok(response));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResult<InstallmentCreateResponse>.Fail("INSTALLMENT_CREATE_INVALID", ex.Message));
        }
    }

    [Authorize]
    [HttpPost("{installmentGuid:guid}/payments")]
    public async Task<ActionResult<ApiResult<InstallmentAppendPaymentResponse>>> AppendPayment(
        Guid installmentGuid,
        [FromBody] InstallmentAppendPaymentRequest request,
        CancellationToken cancellationToken)
    {
        if (installmentGuid != request.InstallmentGuid)
        {
            return BadRequest(ApiResult<InstallmentAppendPaymentResponse>.Fail("INSTALLMENT_GUID_MISMATCH", "Installment id does not match the route."));
        }

        if (!this.IsDeviceScopeAllowed(request.StoreCode, request.DeviceCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<InstallmentAppendPaymentResponse>("Device is not authorized for this store.");
        }

        try
        {
            var response = await installmentService.AppendPaymentAsync(request, cancellationToken);
            return Ok(ApiResult<InstallmentAppendPaymentResponse>.Ok(response));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResult<InstallmentAppendPaymentResponse>.Fail("INSTALLMENT_PAYMENT_INVALID", ex.Message));
        }
    }

    [Authorize]
    [HttpPost("{installmentGuid:guid}/pickup")]
    public async Task<ActionResult<ApiResult<InstallmentConfirmPickupResponse>>> ConfirmPickup(
        Guid installmentGuid,
        [FromBody] InstallmentConfirmPickupRequest request,
        CancellationToken cancellationToken)
    {
        if (installmentGuid != request.InstallmentGuid)
        {
            return BadRequest(ApiResult<InstallmentConfirmPickupResponse>.Fail("INSTALLMENT_GUID_MISMATCH", "Installment id does not match the route."));
        }

        if (!this.IsDeviceScopeAllowed(request.StoreCode, request.DeviceCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<InstallmentConfirmPickupResponse>("Device is not authorized for this store.");
        }

        try
        {
            var response = await installmentService.ConfirmPickupAsync(request, cancellationToken);
            return Ok(ApiResult<InstallmentConfirmPickupResponse>.Ok(response));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResult<InstallmentConfirmPickupResponse>.Fail("INSTALLMENT_PICKUP_INVALID", ex.Message));
        }
    }

    [Authorize]
    [HttpPost("{installmentGuid:guid}/cancel")]
    public async Task<ActionResult<ApiResult<InstallmentCancelResponse>>> Cancel(
        Guid installmentGuid,
        [FromBody] InstallmentCancelRequest request,
        CancellationToken cancellationToken)
    {
        if (installmentGuid != request.InstallmentGuid)
        {
            return BadRequest(ApiResult<InstallmentCancelResponse>.Fail("INSTALLMENT_GUID_MISMATCH", "Installment id does not match the route."));
        }

        if (!this.IsDeviceScopeAllowed(request.StoreCode, request.DeviceCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<InstallmentCancelResponse>("Device is not authorized for this store.");
        }

        try
        {
            var response = await installmentService.CancelAsync(request, cancellationToken);
            return Ok(ApiResult<InstallmentCancelResponse>.Ok(response));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResult<InstallmentCancelResponse>.Fail("INSTALLMENT_CANCEL_INVALID", ex.Message));
        }
    }

    [Authorize]
    [HttpPost("{installmentGuid:guid}/void")]
    public async Task<ActionResult<ApiResult<InstallmentVoidResponse>>> Void(
        Guid installmentGuid,
        [FromBody] InstallmentVoidRequest request,
        CancellationToken cancellationToken)
    {
        if (installmentGuid != request.InstallmentGuid)
        {
            return BadRequest(ApiResult<InstallmentVoidResponse>.Fail("INSTALLMENT_GUID_MISMATCH", "Installment id does not match the route."));
        }

        if (!this.IsDeviceScopeAllowed(request.StoreCode, request.DeviceCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<InstallmentVoidResponse>("Device is not authorized for this store.");
        }

        try
        {
            var response = await installmentService.VoidAsync(request, cancellationToken);
            return Ok(ApiResult<InstallmentVoidResponse>.Ok(response));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResult<InstallmentVoidResponse>.Fail("INSTALLMENT_VOID_INVALID", ex.Message));
        }
    }

    [Authorize]
    [HttpGet("history")]
    public async Task<ActionResult<ApiResult<InstallmentHistoryQueryResponse>>> History(
        [FromQuery] string storeCode,
        [FromQuery] string? deviceCode,
        [FromQuery] DateTimeOffset? createdFrom,
        [FromQuery] DateTimeOffset? createdTo,
        [FromQuery] string? keyword,
        [FromQuery] InstallmentStatus? status,
        [FromQuery] int take,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return BadRequest(ApiResult<InstallmentHistoryQueryResponse>.Fail("STORE_CODE_REQUIRED", "storeCode is required."));
        }

        if (!this.IsDeviceScopeAllowed(storeCode, deviceCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<InstallmentHistoryQueryResponse>("Device is not authorized for this store.");
        }

        var response = await historyService.QueryAsync(
            new InstallmentHistoryQueryRequest(storeCode, deviceCode, createdFrom, createdTo, keyword, status, take <= 0 ? 100 : take),
            cancellationToken);
        return Ok(ApiResult<InstallmentHistoryQueryResponse>.Ok(response));
    }

    [Authorize]
    [HttpGet("{installmentGuid:guid}")]
    public async Task<ActionResult<ApiResult<InstallmentDetailsDto?>>> Details(
        Guid installmentGuid,
        CancellationToken cancellationToken)
    {
        var details = await historyService.GetDetailsAsync(installmentGuid, cancellationToken);
        if (details is not null && !this.IsDeviceScopeAllowed(details.StoreCode, details.DeviceCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<InstallmentDetailsDto?>("Device is not authorized for this store.");
        }

        return Ok(ApiResult<InstallmentDetailsDto?>.Ok(details));
    }
}
