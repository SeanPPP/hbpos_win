using Hbpos.Api.Auth;
using Hbpos.Api.Services;
using Hbpos.Contracts.Advertisements;
using Hbpos.Contracts.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hbpos.Api.Controllers;

[ApiController]
[Route("api/v1/advertisements")]
[Authorize]
public sealed class AdvertisementsController(IAdvertisementPlaybackService advertisementService) : ControllerBase
{
    [HttpGet("active")]
    public async Task<ActionResult<ApiResult<AdvertisementPlaybackResponse>>> GetActive(
        [FromQuery] string storeCode,
        [FromQuery] int take = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return BadRequest(ApiResult<AdvertisementPlaybackResponse>.Fail(
                "STORE_CODE_REQUIRED",
                "storeCode is required"));
        }

        // 广告下发必须受设备授权门店约束，避免跨店拉取素材。
        if (!this.IsDeviceScopeAllowed(storeCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<AdvertisementPlaybackResponse>(
                "Device is not authorized for this store.");
        }

        var response = await advertisementService.GetActiveAsync(storeCode, take, cancellationToken);
        return Ok(ApiResult<AdvertisementPlaybackResponse>.Ok(response));
    }
}
