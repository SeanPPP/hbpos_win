using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Hbpos.Api.Auth;
using Hbpos.Api.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.Linkly;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Hbpos.Api.Controllers;

[ApiController]
[Route("api/v1/linkly")]
[Authorize]
public sealed class LinklyController(
    ILinklyCloudCredentialService linklyCloudCredentialService,
    ILinklyCloudBackendAsyncService linklyCloudBackendAsyncService,
    ILogger<LinklyController>? logger = null) : ControllerBase
{
    private const string CloudCredentialEnvironmentInvalidCode = "LINKLY_CLOUD_CREDENTIAL_ENVIRONMENT_INVALID";
    private const string CloudCredentialInvalidCode = "LINKLY_CLOUD_CREDENTIAL_REQUEST_INVALID";
    private const string CloudCredentialReadFailedCode = "LINKLY_CLOUD_CREDENTIAL_READ_FAILED";
    private const string CloudCredentialWriteFailedCode = "LINKLY_CLOUD_CREDENTIAL_WRITE_FAILED";
    private const string CloudCredentialReadFailedMessage = "Failed to load Linkly Cloud credential configuration.";
    private const string CloudCredentialWriteFailedMessage = "Failed to save Linkly Cloud credential configuration.";
    private const string CloudBackendInvalidCode = "LINKLY_CLOUD_BACKEND_REQUEST_INVALID";
    private const string CloudBackendActiveCode = "LINKLY_CLOUD_BACKEND_ACTIVE_TRANSACTION";
    private const string CloudBackendNotFoundCode = "LINKLY_CLOUD_BACKEND_SESSION_NOT_FOUND";
    private const string CloudBackendFailedCode = "LINKLY_CLOUD_BACKEND_FAILED";

    [HttpGet("cloud-credential")]
    public async Task<ActionResult<ApiResult<LinklyCloudCredentialResponse>>> GetCloudCredential(
        [FromQuery] string? environment,
        CancellationToken cancellationToken)
    {
        var storeCode = User.FindFirstValue(DeviceAuthConstants.StoreCodeClaim);
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            Log("cloud credential request rejected reason=missing-store-claim");
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<LinklyCloudCredentialResponse>(
                "Device store scope is unavailable.");
        }

        var normalizedEnvironment = LinklyCloudCredentialService.NormalizeEnvironment(environment);
        if (normalizedEnvironment is null)
        {
            return BadRequest(ApiResult<LinklyCloudCredentialResponse>.Fail(
                CloudCredentialEnvironmentInvalidCode,
                "environment must be Production or Sandbox"));
        }

        var stopwatch = Stopwatch.StartNew();
        Log($"cloud credential request store={LogValue(storeCode)} environment={normalizedEnvironment}");
        try
        {
            var credential = await linklyCloudCredentialService.GetByStoreCodeAsync(
                storeCode,
                normalizedEnvironment,
                cancellationToken);
            stopwatch.Stop();
            if (credential is null)
            {
                Log($"cloud credential response store={LogValue(storeCode)} environment={normalizedEnvironment} status=404 elapsedMs={stopwatch.ElapsedMilliseconds}");
                return NotFound(ApiResult<LinklyCloudCredentialResponse>.Fail(
                    "LINKLY_CLOUD_CREDENTIAL_NOT_CONFIGURED",
                    "Linkly Cloud credential is not configured for this store."));
            }

            Log($"cloud credential response store={LogValue(storeCode)} environment={normalizedEnvironment} status=200 updatedAt={credential.UpdatedAt:O} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return Ok(ApiResult<LinklyCloudCredentialResponse>.Ok(credential));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log($"cloud credential response store={LogValue(storeCode)} environment={normalizedEnvironment} status=500 error={ex.GetType().Name} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResult<LinklyCloudCredentialResponse>.Fail(
                    CloudCredentialReadFailedCode,
                    CloudCredentialReadFailedMessage));
        }
    }

    [HttpPut("cloud-credential")]
    public async Task<ActionResult<ApiResult<LinklyCloudCredentialUpsertResponse>>> UpsertCloudCredential(
        [FromBody] LinklyCloudCredentialUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var storeCode = User.FindFirstValue(DeviceAuthConstants.StoreCodeClaim);
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            Log("cloud credential upsert rejected reason=missing-store-claim");
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<LinklyCloudCredentialUpsertResponse>(
                "Device store scope is unavailable.");
        }

        if (request is null)
        {
            return BadRequest(ApiResult<LinklyCloudCredentialUpsertResponse>.Fail(
                CloudCredentialInvalidCode,
                "request body is required."));
        }

        try
        {
            var normalizedEnvironment = LinklyCloudCredentialService.NormalizeEnvironment(request.Environment);
            if (normalizedEnvironment is null)
            {
                return BadRequest(ApiResult<LinklyCloudCredentialUpsertResponse>.Fail(
                    CloudCredentialEnvironmentInvalidCode,
                    "environment must be Production or Sandbox"));
            }

            Log($"cloud credential upsert request store={LogValue(storeCode)} environment={normalizedEnvironment}");
            var response = await linklyCloudCredentialService.UpsertAsync(
                storeCode,
                request,
                GetUpdatedByClaim(),
                cancellationToken);
            Log($"cloud credential upsert response store={LogValue(storeCode)} environment={response.Environment} status=200 updatedAt={response.UpdatedAt:O}");
            return Ok(ApiResult<LinklyCloudCredentialUpsertResponse>.Ok(response));
        }
        catch (LinklyCloudCredentialValidationException ex)
        {
            return BadRequest(ApiResult<LinklyCloudCredentialUpsertResponse>.Fail(
                CloudCredentialInvalidCode,
                ex.Message));
        }
        catch (Exception ex)
        {
            Log($"cloud credential upsert failed store={LogValue(storeCode)} error={ex.GetType().Name}");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResult<LinklyCloudCredentialUpsertResponse>.Fail(
                    CloudCredentialWriteFailedCode,
                    CloudCredentialWriteFailedMessage));
        }
    }

    [HttpPost("cloud-backend/transactions")]
    public async Task<ActionResult<ApiResult<LinklyCloudBackendSessionResponse>>> StartCloudBackendTransaction(
        [FromBody] LinklyCloudBackendTransactionRequest request,
        CancellationToken cancellationToken)
    {
        var scope = GetAuthenticatedDeviceScope<LinklyCloudBackendSessionResponse>();
        if (scope.Result is not null)
        {
            return scope.Result;
        }

        try
        {
            var response = await linklyCloudBackendAsyncService.StartTransactionAsync(
                scope.StoreCode!,
                scope.DeviceCode!,
                request,
                cancellationToken);
            return Ok(ApiResult<LinklyCloudBackendSessionResponse>.Ok(response));
        }
        catch (LinklyCloudBackendActiveTransactionException ex)
        {
            return Conflict(ApiResult<LinklyCloudBackendSessionResponse>.Fail(
                CloudBackendActiveCode,
                string.IsNullOrWhiteSpace(ex.ActiveSessionId)
                    ? "An active Linkly Cloud transaction already exists for this terminal."
                    : $"An active Linkly Cloud transaction already exists for this terminal: {ex.ActiveSessionId}."));
        }
        catch (LinklyCloudBackendValidationException ex)
        {
            return BadRequest(ApiResult<LinklyCloudBackendSessionResponse>.Fail(
                CloudBackendInvalidCode,
                ex.Message));
        }
        catch (Exception ex)
        {
            Log($"cloud backend transaction failed error={ex.GetType().Name}");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResult<LinklyCloudBackendSessionResponse>.Fail(
                    CloudBackendFailedCode,
                    "Failed to start Linkly Cloud backend transaction."));
        }
    }

    [HttpPut("cloud-backend/terminal")]
    public async Task<ActionResult<ApiResult<LinklyCloudBackendTerminalCredentialResponse>>> UpsertCloudBackendTerminalCredential(
        [FromBody] LinklyCloudBackendTerminalCredentialUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var scope = GetAuthenticatedDeviceScope<LinklyCloudBackendTerminalCredentialResponse>();
        if (scope.Result is not null)
        {
            return scope.Result;
        }

        if (request is null)
        {
            return BadRequest(ApiResult<LinklyCloudBackendTerminalCredentialResponse>.Fail(
                CloudBackendInvalidCode,
                "request body is required."));
        }

        try
        {
            var response = await linklyCloudBackendAsyncService.UpsertTerminalCredentialAsync(
                scope.StoreCode!,
                scope.DeviceCode!,
                request,
                GetUpdatedByClaim(),
                cancellationToken);
            return Ok(ApiResult<LinklyCloudBackendTerminalCredentialResponse>.Ok(response));
        }
        catch (LinklyCloudBackendValidationException ex)
        {
            return BadRequest(ApiResult<LinklyCloudBackendTerminalCredentialResponse>.Fail(
                CloudBackendInvalidCode,
                ex.Message));
        }
        catch (Exception ex)
        {
            Log($"cloud backend terminal upsert failed error={ex.GetType().Name}");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResult<LinklyCloudBackendTerminalCredentialResponse>.Fail(
                    CloudBackendFailedCode,
                    "Failed to save Linkly Cloud backend terminal credential."));
        }
    }

    [HttpGet("cloud-backend/transactions/active")]
    public async Task<ActionResult<ApiResult<LinklyCloudBackendSessionResponse>>> GetActiveCloudBackendTransaction(
        [FromQuery] string? environment,
        CancellationToken cancellationToken)
    {
        var scope = GetAuthenticatedDeviceScope<LinklyCloudBackendSessionResponse>();
        if (scope.Result is not null)
        {
            return scope.Result;
        }

        try
        {
            var response = await linklyCloudBackendAsyncService.GetActiveSessionAsync(
                scope.StoreCode!,
                scope.DeviceCode!,
                environment ?? string.Empty,
                cancellationToken);
            return response is null
                ? NotFound(ApiResult<LinklyCloudBackendSessionResponse>.Fail(
                    CloudBackendNotFoundCode,
                    "Linkly Cloud backend session was not found."))
                : Ok(ApiResult<LinklyCloudBackendSessionResponse>.Ok(response));
        }
        catch (LinklyCloudBackendValidationException ex)
        {
            return BadRequest(ApiResult<LinklyCloudBackendSessionResponse>.Fail(
                CloudBackendInvalidCode,
                ex.Message));
        }
    }

    [HttpGet("cloud-backend/transactions/resumable")]
    public async Task<ActionResult<ApiResult<LinklyCloudBackendSessionResponse>>> GetResumableCloudBackendTransaction(
        [FromQuery] string? environment,
        CancellationToken cancellationToken)
    {
        var scope = GetAuthenticatedDeviceScope<LinklyCloudBackendSessionResponse>();
        if (scope.Result is not null)
        {
            return scope.Result;
        }

        try
        {
            var response = await linklyCloudBackendAsyncService.GetResumableSessionAsync(
                scope.StoreCode!,
                scope.DeviceCode!,
                environment ?? string.Empty,
                cancellationToken);
            return response is null
                ? NotFound(ApiResult<LinklyCloudBackendSessionResponse>.Fail(
                    CloudBackendNotFoundCode,
                    "Linkly Cloud backend session was not found."))
                : Ok(ApiResult<LinklyCloudBackendSessionResponse>.Ok(response));
        }
        catch (LinklyCloudBackendValidationException ex)
        {
            return BadRequest(ApiResult<LinklyCloudBackendSessionResponse>.Fail(
                CloudBackendInvalidCode,
                ex.Message));
        }
    }

    [HttpGet("cloud-backend/health")]
    public async Task<ActionResult<ApiResult<LinklyCloudBackendHealthResponse>>> GetCloudBackendHealth(
        [FromQuery] string? environment,
        CancellationToken cancellationToken)
    {
        var scope = GetAuthenticatedDeviceScope<LinklyCloudBackendHealthResponse>();
        if (scope.Result is not null)
        {
            return scope.Result;
        }

        try
        {
            var response = await linklyCloudBackendAsyncService.GetHealthAsync(
                scope.StoreCode!,
                scope.DeviceCode!,
                environment ?? string.Empty,
                cancellationToken);
            return Ok(ApiResult<LinklyCloudBackendHealthResponse>.Ok(response));
        }
        catch (LinklyCloudBackendValidationException ex)
        {
            return BadRequest(ApiResult<LinklyCloudBackendHealthResponse>.Fail(
                CloudBackendInvalidCode,
                ex.Message));
        }
    }

    [HttpPost("cloud-backend/status-test")]
    public async Task<ActionResult<ApiResult<LinklyCloudBackendStatusTestResponse>>> RunCloudBackendStatusTest(
        [FromQuery] string? environment,
        CancellationToken cancellationToken)
    {
        var scope = GetAuthenticatedDeviceScope<LinklyCloudBackendStatusTestResponse>();
        if (scope.Result is not null)
        {
            return scope.Result;
        }

        try
        {
            var response = await linklyCloudBackendAsyncService.RunStatusTestAsync(
                scope.StoreCode!,
                scope.DeviceCode!,
                environment ?? string.Empty,
                cancellationToken);
            return Ok(ApiResult<LinklyCloudBackendStatusTestResponse>.Ok(response));
        }
        catch (LinklyCloudBackendValidationException ex)
        {
            return BadRequest(ApiResult<LinklyCloudBackendStatusTestResponse>.Fail(
                CloudBackendInvalidCode,
                ex.Message));
        }
        catch (Exception ex)
        {
            Log($"cloud-backend status-test error={ex.GetType().Name} message={ex.Message}");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResult<LinklyCloudBackendStatusTestResponse>.Fail(
                    CloudBackendFailedCode,
                    "An unexpected error occurred."));
        }
    }

    [HttpPost("cloud-backend/logon-test")]
    public async Task<ActionResult<ApiResult<LinklyCloudBackendLogonTestResponse>>> RunCloudBackendLogonTest(
        [FromQuery] string? environment,
        CancellationToken cancellationToken)
    {
        var scope = GetAuthenticatedDeviceScope<LinklyCloudBackendLogonTestResponse>();
        if (scope.Result is not null)
        {
            return scope.Result;
        }

        try
        {
            var response = await linklyCloudBackendAsyncService.RunLogonTestAsync(
                scope.StoreCode!,
                scope.DeviceCode!,
                environment ?? string.Empty,
                cancellationToken);
            return Ok(ApiResult<LinklyCloudBackendLogonTestResponse>.Ok(response));
        }
        catch (LinklyCloudBackendValidationException ex)
        {
            return BadRequest(ApiResult<LinklyCloudBackendLogonTestResponse>.Fail(
                CloudBackendInvalidCode,
                ex.Message));
        }
        catch (Exception ex)
        {
            Log($"cloud-backend logon-test error={ex.GetType().Name} message={ex.Message}");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResult<LinklyCloudBackendLogonTestResponse>.Fail(
                    CloudBackendFailedCode,
                    "An unexpected error occurred."));
        }
    }

    [HttpGet("cloud-backend/transactions/{sessionId}/status")]
    public async Task<ActionResult<ApiResult<LinklyCloudBackendSessionResponse>>> GetCloudBackendTransactionStatus(
        string sessionId,
        [FromQuery] string? environment,
        CancellationToken cancellationToken)
    {
        var scope = GetAuthenticatedDeviceScope<LinklyCloudBackendSessionResponse>();
        if (scope.Result is not null)
        {
            return scope.Result;
        }

        try
        {
            var response = await linklyCloudBackendAsyncService.GetStatusAsync(
                scope.StoreCode!,
                scope.DeviceCode!,
                environment ?? string.Empty,
                sessionId,
                cancellationToken);
            return response is null
                ? NotFound(ApiResult<LinklyCloudBackendSessionResponse>.Fail(
                    CloudBackendNotFoundCode,
                    "Linkly Cloud backend session was not found."))
                : Ok(ApiResult<LinklyCloudBackendSessionResponse>.Ok(response));
        }
        catch (LinklyCloudBackendValidationException ex)
        {
            return BadRequest(ApiResult<LinklyCloudBackendSessionResponse>.Fail(
                CloudBackendInvalidCode,
                ex.Message));
        }
    }

    [HttpPost("cloud-backend/transactions/{sessionId}/acknowledge")]
    public async Task<ActionResult<ApiResult<LinklyCloudBackendSessionResponse>>> AcknowledgeCloudBackendTransaction(
        string sessionId,
        [FromQuery] string? environment,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] LinklyCloudBackendAcknowledgeRequest? request,
        CancellationToken cancellationToken)
    {
        var scope = GetAuthenticatedDeviceScope<LinklyCloudBackendSessionResponse>();
        if (scope.Result is not null)
        {
            return scope.Result;
        }

        try
        {
            var response = await linklyCloudBackendAsyncService.AcknowledgeSessionAsync(
                scope.StoreCode!,
                scope.DeviceCode!,
                request?.Environment ?? environment ?? string.Empty,
                sessionId,
                cancellationToken);
            return Ok(ApiResult<LinklyCloudBackendSessionResponse>.Ok(response));
        }
        catch (LinklyCloudBackendSessionNotFoundException)
        {
            return NotFound(ApiResult<LinklyCloudBackendSessionResponse>.Fail(
                CloudBackendNotFoundCode,
                "Linkly Cloud backend session was not found."));
        }
        catch (LinklyCloudBackendValidationException ex)
        {
            return BadRequest(ApiResult<LinklyCloudBackendSessionResponse>.Fail(
                CloudBackendInvalidCode,
                ex.Message));
        }
    }

    [HttpPost("cloud-backend/transactions/{sessionId}/recover")]
    public async Task<ActionResult<ApiResult<LinklyCloudBackendSessionResponse>>> RecoverCloudBackendTransaction(
        string sessionId,
        [FromBody] LinklyCloudBackendRecoverRequest request,
        CancellationToken cancellationToken)
    {
        var scope = GetAuthenticatedDeviceScope<LinklyCloudBackendSessionResponse>();
        if (scope.Result is not null)
        {
            return scope.Result;
        }

        try
        {
            var response = await linklyCloudBackendAsyncService.RecoverAsync(
                scope.StoreCode!,
                scope.DeviceCode!,
                sessionId,
                request,
                cancellationToken);
            return Ok(ApiResult<LinklyCloudBackendSessionResponse>.Ok(response));
        }
        catch (LinklyCloudBackendSessionNotFoundException)
        {
            return NotFound(ApiResult<LinklyCloudBackendSessionResponse>.Fail(
                CloudBackendNotFoundCode,
                "Linkly Cloud backend session was not found."));
        }
        catch (LinklyCloudBackendValidationException ex)
        {
            return BadRequest(ApiResult<LinklyCloudBackendSessionResponse>.Fail(
                CloudBackendInvalidCode,
                ex.Message));
        }
    }

    [HttpPost("cloud-backend/transactions/{sessionId}/sendkey")]
    public async Task<ActionResult<ApiResult<LinklyCloudBackendSessionResponse>>> SendCloudBackendKey(
        string sessionId,
        [FromBody] LinklyCloudBackendSendKeyRequest request,
        CancellationToken cancellationToken)
    {
        var scope = GetAuthenticatedDeviceScope<LinklyCloudBackendSessionResponse>();
        if (scope.Result is not null)
        {
            return scope.Result;
        }

        try
        {
            var response = await linklyCloudBackendAsyncService.SendKeyAsync(
                scope.StoreCode!,
                scope.DeviceCode!,
                sessionId,
                request,
                cancellationToken);
            if (response.LastHttpStatus == StatusCodes.Status400BadRequest)
            {
                return BadRequest(ApiResult<LinklyCloudBackendSessionResponse>.Fail(
                    CloudBackendInvalidCode,
                    "Linkly Cloud rejected the terminal action. Continue waiting for the transaction result."));
            }

            return Ok(ApiResult<LinklyCloudBackendSessionResponse>.Ok(response));
        }
        catch (LinklyCloudBackendSessionNotFoundException)
        {
            return NotFound(ApiResult<LinklyCloudBackendSessionResponse>.Fail(
                CloudBackendNotFoundCode,
                "Linkly Cloud backend session was not found."));
        }
        catch (LinklyCloudBackendValidationException ex)
        {
            return BadRequest(ApiResult<LinklyCloudBackendSessionResponse>.Fail(
                CloudBackendInvalidCode,
                ex.Message));
        }
    }

    [HttpPost("cloud-backend/transactions/{sessionId}/receipt/printed")]
    public async Task<ActionResult<ApiResult<LinklyCloudBackendSessionResponse>>> MarkCloudBackendReceiptPrinted(
        string sessionId,
        [FromBody] LinklyCloudBackendMarkReceiptPrintedRequest request,
        CancellationToken cancellationToken)
    {
        var scope = GetAuthenticatedDeviceScope<LinklyCloudBackendSessionResponse>();
        if (scope.Result is not null)
        {
            return scope.Result;
        }

        try
        {
            var response = await linklyCloudBackendAsyncService.MarkReceiptPrintedAsync(
                scope.StoreCode!,
                scope.DeviceCode!,
                sessionId,
                request,
                cancellationToken);
            return Ok(ApiResult<LinklyCloudBackendSessionResponse>.Ok(response));
        }
        catch (LinklyCloudBackendSessionNotFoundException)
        {
            return NotFound(ApiResult<LinklyCloudBackendSessionResponse>.Fail(
                CloudBackendNotFoundCode,
                "Linkly Cloud backend session was not found."));
        }
        catch (LinklyCloudBackendValidationException ex)
        {
            return BadRequest(ApiResult<LinklyCloudBackendSessionResponse>.Fail(
                CloudBackendInvalidCode,
                ex.Message));
        }
    }

    [AllowAnonymous]
    [HttpPost("cloud-notifications/{environment}/{sessionId}/{type}")]
    public async Task<ActionResult<ApiResult<string>>> ReceiveCloudBackendNotification(
        string environment,
        string sessionId,
        string type,
        [FromBody] JsonElement payload,
        CancellationToken cancellationToken)
    {
        LogNotification(
            "request",
            "request",
            environment,
            sessionId,
            type,
            statusCode: null,
            request: new
            {
                authorization = Request.Headers.Authorization.ToString(),
                payload
            },
            response: null);
        try
        {
            await linklyCloudBackendAsyncService.ReceiveNotificationAsync(
                environment,
                sessionId,
                type,
                Request.Headers.Authorization.ToString(),
                payload,
                cancellationToken);
            var accepted = ApiResult<string>.Ok("accepted");
            LogNotification(
                "response",
                "response",
                environment,
                sessionId,
                type,
                StatusCodes.Status200OK,
                request: null,
                response: accepted);
            return Ok(accepted);
        }
        catch (LinklyCloudBackendNotificationUnauthorizedException)
        {
            var unauthorized = ApiResult<string>.Fail(
                "LINKLY_CLOUD_BACKEND_NOTIFICATION_UNAUTHORIZED",
                "Linkly Cloud notification authorization is invalid.");
            LogNotification(
                "response",
                "response",
                environment,
                sessionId,
                type,
                StatusCodes.Status401Unauthorized,
                request: null,
                response: unauthorized);
            return Unauthorized(unauthorized);
        }
        catch (LinklyCloudBackendValidationException ex)
        {
            var badRequest = ApiResult<string>.Fail(
                CloudBackendInvalidCode,
                ex.Message);
            LogNotification(
                "response",
                "response",
                environment,
                sessionId,
                type,
                StatusCodes.Status400BadRequest,
                request: null,
                response: badRequest);
            return BadRequest(badRequest);
        }
    }

    private (string? StoreCode, string? DeviceCode, ActionResult<ApiResult<T>>? Result) GetAuthenticatedDeviceScope<T>()
    {
        var storeCode = User.FindFirstValue(DeviceAuthConstants.StoreCodeClaim);
        var deviceCode = User.FindFirstValue(DeviceAuthConstants.DeviceCodeClaim);
        if (string.IsNullOrWhiteSpace(storeCode) || string.IsNullOrWhiteSpace(deviceCode))
        {
            Log("cloud backend request rejected reason=missing-device-claims");
            return (null, null, DeviceAuthorizationExtensions.DeviceScopeForbidden<T>(
                "Device store and terminal scope are unavailable."));
        }

        // CloudBackendAsync 所有设备 scope 只信任认证 claim，忽略 query/body 中任何门店或设备字段。
        return (storeCode.Trim(), deviceCode.Trim(), null);
    }

    private string? GetUpdatedByClaim()
    {
        var deviceCode = User.FindFirstValue(DeviceAuthConstants.DeviceCodeClaim);
        return string.IsNullOrWhiteSpace(deviceCode) ? null : $"device:{deviceCode.Trim()}";
    }

    private void Log(string message)
    {
        LogJson(BuildJsonLog(
            source: "api-linkly-controller",
            operation: InferOperation(message),
            phase: InferPhase(message),
            direction: null,
            environment: null,
            sessionId: null,
            httpStatus: null,
            request: null,
            response: null,
            details: new
            {
                message
            }));
    }

    private void LogNotification(
        string phase,
        string direction,
        string environment,
        string sessionId,
        string type,
        int? statusCode,
        object? request,
        object? response)
    {
        LogJson(BuildJsonLog(
            source: "api-linkly-controller",
            operation: $"notification-{type}",
            phase: phase,
            direction: direction,
            environment: environment,
            sessionId: sessionId,
            httpStatus: statusCode,
            request: request,
            response: response,
            details: new
            {
                type,
                timestamp = DateTimeOffset.Now.ToString("O")
            }));
    }

    private void LogJson(string json)
    {
        Console.WriteLine($"[HBPOS][Api][LinklyCloud] {DateTimeOffset.Now:O} {json}");
        logger?.LogInformation("[HBPOS][Api][LinklyCloud] {Message}", json);
    }

    private static string BuildJsonLog(
        string source,
        string operation,
        string phase,
        string? direction,
        string? environment,
        string? sessionId,
        int? httpStatus,
        object? request,
        object? response,
        object? details)
    {
        return JsonSerializer.Serialize(new
        {
            source,
            operation,
            phase,
            direction,
            environment,
            sessionId,
            httpStatus,
            success = httpStatus.HasValue ? httpStatus.Value is >= 200 and < 300 : (bool?)null,
            reason = (string?)null,
            elapsedMs = (long?)null,
            request,
            response,
            details
        });
    }

    private static string InferOperation(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "linkly";
        }

        var trimmed = message.Trim();
        var index = trimmed.IndexOf(' ', StringComparison.Ordinal);
        return index <= 0 ? trimmed : trimmed[..index];
    }

    private static string InferPhase(string message)
    {
        if (message.Contains("rejected", StringComparison.OrdinalIgnoreCase))
        {
            return "rejected";
        }

        if (message.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            return "failed";
        }

        if (message.Contains("response", StringComparison.OrdinalIgnoreCase))
        {
            return "response";
        }

        if (message.Contains("request", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("upsert", StringComparison.OrdinalIgnoreCase))
        {
            return "request";
        }

        return "event";
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value.Trim();
    }
}
