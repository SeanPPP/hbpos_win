using System.Diagnostics;
using System.Security.Claims;
using Hbpos.Api.Auth;
using Hbpos.Api.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.Linkly;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hbpos.Api.Controllers;

[ApiController]
[Route("api/v1/linkly")]
[Authorize]
public sealed class LinklyController(
    ILinklyCloudCredentialService linklyCloudCredentialService) : ControllerBase
{
    private const string CloudCredentialReadFailedCode = "LINKLY_CLOUD_CREDENTIAL_READ_FAILED";
    private const string CloudCredentialReadFailedMessage = "Failed to load Linkly Cloud credential configuration.";

    [HttpGet("cloud-credential")]
    public async Task<ActionResult<ApiResult<LinklyCloudCredentialResponse>>> GetCloudCredential(
        CancellationToken cancellationToken)
    {
        var storeCode = User.FindFirstValue(DeviceAuthConstants.StoreCodeClaim);
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            Log("cloud credential request rejected reason=missing-store-claim");
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<LinklyCloudCredentialResponse>(
                "Device store scope is unavailable.");
        }

        var stopwatch = Stopwatch.StartNew();
        Log($"cloud credential request store={LogValue(storeCode)}");
        try
        {
            var credential = await linklyCloudCredentialService.GetByStoreCodeAsync(storeCode, cancellationToken);
            stopwatch.Stop();
            if (credential is null)
            {
                Log($"cloud credential response store={LogValue(storeCode)} status=404 elapsedMs={stopwatch.ElapsedMilliseconds}");
                return NotFound(ApiResult<LinklyCloudCredentialResponse>.Fail(
                    "LINKLY_CLOUD_CREDENTIAL_NOT_CONFIGURED",
                    "Linkly Cloud credential is not configured for this store."));
            }

            Log($"cloud credential response store={LogValue(storeCode)} status=200 updatedAt={credential.UpdatedAt:O} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return Ok(ApiResult<LinklyCloudCredentialResponse>.Ok(credential));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log($"cloud credential response store={LogValue(storeCode)} status=500 error={ex.GetType().Name} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResult<LinklyCloudCredentialResponse>.Fail(
                    CloudCredentialReadFailedCode,
                    CloudCredentialReadFailedMessage));
        }
    }

    private static void Log(string message)
    {
        Console.WriteLine($"[HBPOS][Api][LinklyCloud] {DateTimeOffset.Now:O} {message}");
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value.Trim();
    }
}
