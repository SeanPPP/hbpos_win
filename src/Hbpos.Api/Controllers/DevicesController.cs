using Hbpos.Api.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Hbpos.Api.Controllers;

[ApiController]
[Route("api/v1/devices")]
public sealed class DevicesController(IDeviceService deviceService) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<ActionResult<ApiResult<DeviceRegisterResponse>>> Register(
        [FromBody] DeviceRegisterRequest request,
        CancellationToken cancellationToken)
    {
        var response = await deviceService.RegisterAsync(request, cancellationToken);
        return Ok(ApiResult<DeviceRegisterResponse>.Ok(response));
    }

    [AllowAnonymous]
    [HttpPost("verify")]
    public async Task<ActionResult<ApiResult<DeviceVerifyResponse>>> Verify(
        [FromBody] DeviceVerifyRequest request,
        CancellationToken cancellationToken)
    {
        var response = await deviceService.VerifyAsync(request, cancellationToken);
        return Ok(ApiResult<DeviceVerifyResponse>.Ok(response));
    }

    [Authorize]
    [HttpPost("reregister")]
    public async Task<ActionResult<ApiResult<DeviceReregisterResponse>>> Reregister(
        [FromBody] DeviceReregisterRequest request,
        CancellationToken cancellationToken)
    {
        var deviceCode = User?.FindFirstValue(DeviceAuthConstants.DeviceCodeClaim);
        var storeCode = User?.FindFirstValue(DeviceAuthConstants.StoreCodeClaim);
        var hardwareId = User?.FindFirstValue(DeviceAuthConstants.HardwareIdClaim);
        if (string.IsNullOrWhiteSpace(deviceCode)
            || string.IsNullOrWhiteSpace(storeCode)
            || string.IsNullOrWhiteSpace(hardwareId))
        {
            return Unauthorized(ApiResult<DeviceReregisterResponse>.Fail(
                "DEVICE_AUTH_REQUIRED",
                "Device authorization is required."));
        }

        var response = await deviceService.ReregisterAsync(
            request,
            new DeviceReregisterContext(deviceCode, storeCode, hardwareId),
            cancellationToken);
        return Ok(ApiResult<DeviceReregisterResponse>.Ok(response));
    }
}
