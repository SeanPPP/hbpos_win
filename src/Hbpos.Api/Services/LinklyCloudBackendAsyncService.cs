using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hbpos.Api.Data;
using Hbpos.Contracts.Linkly;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace Hbpos.Api.Services;

public interface ILinklyCloudBackendAsyncService
{
    Task<LinklyCloudBackendSessionResponse> StartTransactionAsync(
        string storeCode,
        string deviceCode,
        LinklyCloudBackendTransactionRequest request,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendSessionResponse?> GetStatusAsync(
        string storeCode,
        string deviceCode,
        string environment,
        string sessionId,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendSessionResponse?> GetActiveSessionAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendHealthResponse> GetHealthAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendTerminalCredentialResponse> UpsertTerminalCredentialAsync(
        string storeCode,
        string deviceCode,
        LinklyCloudBackendTerminalCredentialUpsertRequest request,
        string? updatedBy,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendSessionResponse> RecoverAsync(
        string storeCode,
        string deviceCode,
        string sessionId,
        LinklyCloudBackendRecoverRequest request,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendSessionResponse> SendKeyAsync(
        string storeCode,
        string deviceCode,
        string sessionId,
        LinklyCloudBackendSendKeyRequest request,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendSessionResponse> MarkReceiptPrintedAsync(
        string storeCode,
        string deviceCode,
        string sessionId,
        LinklyCloudBackendMarkReceiptPrintedRequest request,
        CancellationToken cancellationToken);

    Task ReceiveNotificationAsync(
        string environment,
        string sessionId,
        string type,
        string? authorizationHeader,
        JsonElement payload,
        CancellationToken cancellationToken);
}

public sealed class LinklyCloudBackendAsyncOptions
{
    public string? ProductionNotificationBearer { get; set; }

    public string? SandboxNotificationBearer { get; set; }

    public string? PublicNotificationBaseUrl { get; set; }

    public string ProductionAuthBaseUrl { get; set; } = "https://auth.cloud.pceftpos.com/v1/";

    public string SandboxAuthBaseUrl { get; set; } = "https://auth.sandbox.cloud.pceftpos.com/v1/";

    public string ProductionRestBaseUrl { get; set; } = "https://rest.pos.cloud.pceftpos.com/v1/";

    public string SandboxRestBaseUrl { get; set; } = "https://rest.pos.sandbox.cloud.pceftpos.com/v1/";

    public string PosName { get; set; } = "HotBargainPOS";

    public string PosVersion { get; set; } = "2026.5.1";

    public string? ProductionPosVendorId { get; set; }

    public string? SandboxPosVendorId { get; set; } = "11111111-1111-4111-8111-111111111111";
}

public class LinklyCloudBackendAsyncService(
    ILinklyCloudBackendAsyncRepository repository,
    ILinklyCloudBackendAsyncTransport transport,
    ILinklyCloudBackendTokenProvider tokenProvider,
    ILinklyCloudCredentialRepository credentialRepository,
    ILinklyCloudBackendTerminalCredentialRepository terminalCredentialRepository,
    IOptions<LinklyCloudBackendAsyncOptions> options) : ILinklyCloudBackendAsyncService
{
    private const string StatusPending = "Pending";
    private const string StatusCompleted = "Completed";
    private const string StatusNotSubmitted = "NotSubmitted";
    private const string StatusTokenRefreshRequired = "TokenRefreshRequired";
    private const string StatusFailed = "Failed";
    private const string RecoveryRetry = "Retry";
    private const string RecoveryRefreshToken = "RefreshToken";
    private const string RecoveryRetryNewSession = "RetryNewSession";
    private const int MaxTxnRefCreateAttempts = 5;

    public async Task<LinklyCloudBackendSessionResponse> StartTransactionAsync(
        string storeCode,
        string deviceCode,
        LinklyCloudBackendTransactionRequest request,
        CancellationToken cancellationToken)
    {
        var environment = NormalizeEnvironment(request.Environment);
        var normalizedStoreCode = NormalizeRequired(storeCode, "storeCode");
        var normalizedDeviceCode = NormalizeRequired(deviceCode, "deviceCode");
        var notificationBaseUri = GetPublicNotificationBaseUri();
        var activeSession = await repository.GetActiveSessionAsync(
            environment,
            normalizedStoreCode,
            normalizedDeviceCode,
            cancellationToken);
        if (activeSession is not null)
        {
            throw new LinklyCloudBackendActiveTransactionException(activeSession.SessionId);
        }

        var token = await tokenProvider.GetTokenAsync(
            environment,
            normalizedStoreCode,
            normalizedDeviceCode,
            cancellationToken);

        var session = await CreatePendingSessionWithUniqueTxnRefAsync(
            environment,
            normalizedStoreCode,
            normalizedDeviceCode,
            cancellationToken);

        var notification = BuildNotificationRequest(environment, session.SessionId, notificationBaseUri);
        var transportRequest = new LinklyCloudBackendTransportTransactionRequest(
            environment,
            token.RestBaseUrl,
            token.AccessToken,
            session.SessionId,
            NormalizeRequired(request.TxnType, "txnType"),
            request.AmtPurchase,
            session.TxnRef!,
            request.PurchaseAnalysisData,
            notification);

        var response = await SendWithRecoverableFailureAsync(
            () => transport.StartTransactionAsync(transportRequest, cancellationToken));
        ApplyTransportResponse(session, response);
        if (IsTransportRecoveryFailure(response.StatusCode))
        {
            session.RecoveryCount++;
        }

        var savedSession = await UpsertSessionAndReadLatestAsync(session, cancellationToken);
        return await BuildResponseAsync(savedSession, cancellationToken);
    }

    public async Task<LinklyCloudBackendSessionResponse?> GetStatusAsync(
        string storeCode,
        string deviceCode,
        string environment,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var session = await repository.GetSessionAsync(
            NormalizeEnvironment(environment),
            NormalizeRequired(storeCode, "storeCode"),
            NormalizeRequired(deviceCode, "deviceCode"),
            NormalizeRequired(sessionId, "sessionId"),
            cancellationToken);

        return session is null ? null : await BuildResponseAsync(session, cancellationToken);
    }

    public async Task<LinklyCloudBackendSessionResponse?> GetActiveSessionAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken)
    {
        var session = await repository.GetActiveSessionAsync(
            NormalizeEnvironment(environment),
            NormalizeRequired(storeCode, "storeCode"),
            NormalizeRequired(deviceCode, "deviceCode"),
            cancellationToken);

        return session is null ? null : await BuildResponseAsync(session, cancellationToken);
    }

    public async Task<LinklyCloudBackendSessionResponse> RecoverAsync(
        string storeCode,
        string deviceCode,
        string sessionId,
        LinklyCloudBackendRecoverRequest request,
        CancellationToken cancellationToken)
    {
        var session = await GetRequiredSessionAsync(storeCode, deviceCode, request.Environment, sessionId, cancellationToken);
        var token = await tokenProvider.GetTokenAsync(
            session.Environment,
            session.StoreCode,
            session.DeviceCode,
            cancellationToken);
        var transportRequest = new LinklyCloudBackendTransportSessionRequest(
            session.Environment,
            token.RestBaseUrl,
            token.AccessToken,
            session.SessionId);

        var response = await SendWithRecoverableFailureAsync(
            () => transport.RecoverTransactionAsync(transportRequest, cancellationToken));

        // 恢复响应只应用一次：408/5xx 保持 Pending，401 要求刷新 token，404 释放活动锁。
        ApplyTransportResponse(session, response);
        if (ShouldCountRecoveryResponse(response.StatusCode))
        {
            session.RecoveryCount++;
        }

        var savedSession = await UpsertSessionAndReadLatestAsync(session, cancellationToken);
        return await BuildResponseAsync(savedSession, cancellationToken);
    }

    public async Task<LinklyCloudBackendSessionResponse> SendKeyAsync(
        string storeCode,
        string deviceCode,
        string sessionId,
        LinklyCloudBackendSendKeyRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedKey = NormalizeSendKey(request.Key);
        var session = await GetRequiredSessionAsync(storeCode, deviceCode, request.Environment, sessionId, cancellationToken);
        var token = await tokenProvider.GetTokenAsync(
            session.Environment,
            session.StoreCode,
            session.DeviceCode,
            cancellationToken);
        var transportRequest = new LinklyCloudBackendTransportSendKeyRequest(
            session.Environment,
            token.RestBaseUrl,
            token.AccessToken,
            session.SessionId,
            normalizedKey,
            NormalizeOptional(request.Data));

        var response = await SendWithRecoverableFailureAsync(
            () => transport.SendKeyAsync(transportRequest, cancellationToken));
        ApplyTransportResponse(session, response);
        if (IsTransportRecoveryFailure(response.StatusCode))
        {
            session.RecoveryCount++;
        }

        var savedSession = await UpsertSessionAndReadLatestAsync(session, cancellationToken);
        return await BuildResponseAsync(savedSession, cancellationToken);
    }

    public async Task<LinklyCloudBackendSessionResponse> MarkReceiptPrintedAsync(
        string storeCode,
        string deviceCode,
        string sessionId,
        LinklyCloudBackendMarkReceiptPrintedRequest request,
        CancellationToken cancellationToken)
    {
        var session = await GetRequiredSessionAsync(storeCode, deviceCode, request.Environment, sessionId, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        // 收据通知只说明文本已到达；客户端确认进入打印交付链路后，才在这里标记已打印。
        session.ReceiptPrintedAt ??= now;
        session.UpdatedAt = now;
        var savedSession = await UpsertSessionAndReadLatestAsync(session, cancellationToken);
        return await BuildResponseAsync(savedSession, cancellationToken);
    }

    public async Task ReceiveNotificationAsync(
        string environment,
        string sessionId,
        string type,
        string? authorizationHeader,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironment = NormalizeEnvironment(environment);
        var expectedBearer = GetNotificationBearer(normalizedEnvironment);
        if (string.IsNullOrWhiteSpace(expectedBearer) ||
            !string.Equals(authorizationHeader?.Trim(), $"Bearer {expectedBearer}", StringComparison.Ordinal))
        {
            throw new LinklyCloudBackendNotificationUnauthorizedException();
        }

        var normalizedSessionId = NormalizeRequired(sessionId, "sessionId");
        var normalizedType = NormalizeRequired(type, "type");
        var payloadJson = payload.GetRawText();
        var now = DateTimeOffset.UtcNow;

        var session = await repository.GetSessionByEnvironmentSessionIdAsync(
            normalizedEnvironment,
            normalizedSessionId,
            cancellationToken);
        if (session is null)
        {
            return;
        }

        var existingNotifications = await repository.GetNotificationsAsync(
            normalizedEnvironment,
            session.StoreCode,
            session.DeviceCode,
            normalizedSessionId,
            cancellationToken);
        if (!existingNotifications.Any(notification =>
            string.Equals(notification.Type, normalizedType, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(notification.PayloadJson, payloadJson, StringComparison.Ordinal)))
        {
            await repository.AddNotificationAsync(new LinklyCloudBackendNotificationRecord
            {
                Environment = normalizedEnvironment,
                StoreCode = session.StoreCode,
                DeviceCode = session.DeviceCode,
                SessionId = normalizedSessionId,
                Type = normalizedType,
                PayloadJson = payloadJson,
                ReceivedAt = now
            }, cancellationToken);
        }

        if (string.Equals(normalizedType, "transaction", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsCompleted(session) || string.IsNullOrWhiteSpace(session.ResponseCode))
            {
                ApplyCompletedPayload(session, payloadJson);
            }
            else
            {
                session.UpdatedAt = now;
            }
        }
        else if (string.Equals(normalizedType, "display", StringComparison.OrdinalIgnoreCase))
        {
            ApplyDisplayPayload(session, payloadJson, now);
        }
        else if (string.Equals(normalizedType, "receipt", StringComparison.OrdinalIgnoreCase))
        {
            ApplyReceiptPayload(session, payloadJson, now);
        }
        else
        {
            session.UpdatedAt = now;
        }

        await repository.UpsertSessionAsync(session, cancellationToken);
    }

    public async Task<LinklyCloudBackendHealthResponse> GetHealthAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironment = NormalizeEnvironment(environment);
        var normalizedStoreCode = NormalizeRequired(storeCode, "storeCode");
        var normalizedDeviceCode = NormalizeRequired(deviceCode, "deviceCode");
        var checks = new List<LinklyCloudBackendHealthCheckDto>();

        var credential = await credentialRepository.GetByStoreCodeAsync(
            normalizedStoreCode,
            normalizedEnvironment,
            cancellationToken);
        var storeCredentialReady = credential is not null &&
            !string.IsNullOrWhiteSpace(credential.Username) &&
            !string.IsNullOrWhiteSpace(credential.Password);
        checks.Add(CreateHealthCheck(
            "STORE_CREDENTIAL",
            storeCredentialReady,
            "Linkly Cloud store credential is configured.",
            "Linkly Cloud store credential is missing for this store and environment."));

        var terminalCredential = await terminalCredentialRepository.GetByDeviceAsync(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            cancellationToken);
        var terminalSecretReady = terminalCredential is not null &&
            !string.IsNullOrWhiteSpace(terminalCredential.Secret);
        checks.Add(CreateHealthCheck(
            "TERMINAL_SECRET",
            terminalSecretReady,
            "Linkly Cloud terminal secret is configured.",
            "Linkly Cloud terminal secret is missing for this terminal."));

        var terminalPosIdReady = terminalCredential is not null &&
            !string.IsNullOrWhiteSpace(terminalCredential.PosId);
        checks.Add(CreateHealthCheck(
            "TERMINAL_POS_ID",
            terminalPosIdReady,
            "Linkly Cloud POS ID is configured.",
            "Linkly Cloud POS ID is missing for this terminal."));

        var notificationBearerReady = !string.IsNullOrWhiteSpace(GetNotificationBearer(normalizedEnvironment));
        checks.Add(CreateHealthCheck(
            "NOTIFICATION_BEARER",
            notificationBearerReady,
            "Linkly Cloud notification bearer is configured.",
            "Linkly Cloud notification bearer is missing for this environment."));

        var publicCallbackReady = TryGetPublicNotificationBaseUri(out var publicCallbackUri);
        checks.Add(CreateHealthCheck(
            "PUBLIC_CALLBACK_URL",
            publicCallbackReady,
            "Linkly Cloud notification callback URL is public HTTPS.",
            "Linkly Cloud notification callback URL must be public HTTPS."));

        // 健康检查只验证本地配置，不把 active session 的 404 当作配置可用。
        return new LinklyCloudBackendHealthResponse(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            checks.All(check => check.IsReady),
            publicCallbackUri?.AbsoluteUri,
            checks);
    }

    public async Task<LinklyCloudBackendTerminalCredentialResponse> UpsertTerminalCredentialAsync(
        string storeCode,
        string deviceCode,
        LinklyCloudBackendTerminalCredentialUpsertRequest request,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedEnvironment = NormalizeEnvironment(request.Environment);
        var normalizedStoreCode = NormalizeRequired(storeCode, "storeCode");
        var normalizedDeviceCode = NormalizeRequired(deviceCode, "deviceCode");
        var secret = NormalizeRequired(request.Secret, "secret");
        var posId = NormalizeUuidV4(request.PosId);
        var now = DateTime.UtcNow;

        var credential = await terminalCredentialRepository.UpsertAsync(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            secret,
            posId,
            now,
            NormalizeOptional(updatedBy),
            cancellationToken);

        return new LinklyCloudBackendTerminalCredentialResponse(
            credential.Environment ?? normalizedEnvironment,
            credential.StoreCode ?? normalizedStoreCode,
            credential.DeviceCode ?? normalizedDeviceCode,
            !string.IsNullOrWhiteSpace(credential.Secret),
            credential.PosId ?? posId,
            new DateTimeOffset(DateTime.SpecifyKind(credential.UpdatedAt ?? now, DateTimeKind.Utc)));
    }

    private async Task<LinklyCloudBackendSessionRecord> GetRequiredSessionAsync(
        string storeCode,
        string deviceCode,
        string environment,
        string sessionId,
        CancellationToken cancellationToken)
    {
        return await repository.GetSessionAsync(
            NormalizeEnvironment(environment),
            NormalizeRequired(storeCode, "storeCode"),
            NormalizeRequired(deviceCode, "deviceCode"),
            NormalizeRequired(sessionId, "sessionId"),
            cancellationToken) ?? throw new LinklyCloudBackendSessionNotFoundException();
    }

    private async Task<LinklyCloudBackendSessionRecord> CreatePendingSessionWithUniqueTxnRefAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxTxnRefCreateAttempts; attempt++)
        {
            var now = DateTimeOffset.UtcNow;
            var session = new LinklyCloudBackendSessionRecord
            {
                Environment = environment,
                StoreCode = storeCode,
                DeviceCode = deviceCode,
                SessionId = Guid.NewGuid().ToString("D"),
                Status = StatusPending,
                TxnRef = CreateTxnRef(),
                IsActive = true,
                UpdatedAt = now
            };

            // 先落活动会话锁，再调用 Linkly；若只是 TxnRef 唯一键碰撞，则换号重试。
            if (await repository.TryCreateSessionAsync(session, cancellationToken))
            {
                return session;
            }

            var active = await repository.GetActiveSessionAsync(environment, storeCode, deviceCode, cancellationToken);
            if (active is not null)
            {
                throw new LinklyCloudBackendActiveTransactionException(active.SessionId);
            }
        }

        throw new LinklyCloudBackendValidationException("Failed to allocate a unique Linkly Cloud transaction reference.");
    }

    private LinklyCloudBackendNotificationRequest BuildNotificationRequest(
        string environment,
        string sessionId,
        Uri notificationBaseUri)
    {
        var bearer = GetNotificationBearer(environment);
        if (string.IsNullOrWhiteSpace(bearer))
        {
            throw new LinklyCloudBackendValidationException("Linkly Cloud notification bearer is not configured.");
        }

        var relativePath = string.Join(
            '/',
            "api/v1/linkly/cloud-notifications",
            Uri.EscapeDataString(environment),
            Uri.EscapeDataString(sessionId),
            "{{type}}");
        return new LinklyCloudBackendNotificationRequest(
            notificationBaseUri.AbsoluteUri + relativePath,
            $"Bearer {bearer}");
    }

    private string? GetNotificationBearer(string environment)
    {
        return string.Equals(environment, "Sandbox", StringComparison.Ordinal)
            ? NormalizeOptional(options.Value.SandboxNotificationBearer)
            : NormalizeOptional(options.Value.ProductionNotificationBearer);
    }

    private Uri GetPublicNotificationBaseUri()
    {
        if (!TryGetPublicNotificationBaseUri(out var uri))
        {
            throw new LinklyCloudBackendValidationException(
                "Linkly Cloud public notification base URL must be configured as an HTTPS URL.");
        }

        return uri!;
    }

    private bool TryGetPublicNotificationBaseUri(out Uri? uri)
    {
        var configured = NormalizeOptional(options.Value.PublicNotificationBaseUrl);
        if (configured is null ||
            !Uri.TryCreate(configured, UriKind.Absolute, out var parsedUri) ||
            !string.Equals(parsedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !IsPublicHost(parsedUri.Host))
        {
            uri = null;
            return false;
        }

        uri = parsedUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? parsedUri
            : new Uri(parsedUri.AbsoluteUri + "/", UriKind.Absolute);
        return true;
    }

    private static bool IsPublicHost(string host)
    {
        var normalizedHost = host.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(normalizedHost) || IsClearlyNonPublicHostName(normalizedHost))
        {
            return false;
        }

        if (!IPAddress.TryParse(normalizedHost, out var address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !IsPrivateOrSpecialIpv4(bytes);
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 &&
            !address.Equals(IPAddress.IPv6Any) &&
            !address.IsIPv6LinkLocal &&
            !address.IsIPv6SiteLocal &&
            !address.IsIPv6Multicast &&
            !IsDocumentationIpv6(bytes) &&
            (bytes[0] & 0xfe) != 0xfc;
    }

    private static bool IsClearlyNonPublicHostName(string host)
    {
        // 非 IP 主机不做 DNS 解析，避免配置校验和测试依赖外网，只拦截显然不可公网回调的本地域名。
        return IsHostOrDomain(host, "localhost") ||
            IsHostOrDomain(host, "local") ||
            IsHostOrDomain(host, "internal") ||
            IsHostOrDomain(host, "lan");
    }

    private static bool IsHostOrDomain(string host, string domain)
    {
        return string.Equals(host, domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrivateOrSpecialIpv4(byte[] bytes)
    {
        return bytes[0] == 0 ||
            bytes[0] == 10 ||
            bytes[0] == 127 ||
            bytes[0] == 169 && bytes[1] == 254 ||
            bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
            bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0 ||
            bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2 ||
            bytes[0] == 192 && bytes[1] == 168 ||
            bytes[0] == 100 && bytes[1] is >= 64 and <= 127 ||
            bytes[0] == 198 && bytes[1] is 18 or 19 ||
            bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100 ||
            bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113 ||
            bytes[0] >= 224;
    }

    private static bool IsDocumentationIpv6(byte[] bytes)
    {
        return bytes[0] == 0x20 &&
            bytes[1] == 0x01 &&
            bytes[2] == 0x0d &&
            bytes[3] == 0xb8;
    }

    private async Task<LinklyCloudBackendSessionRecord> UpsertSessionAndReadLatestAsync(
        LinklyCloudBackendSessionRecord session,
        CancellationToken cancellationToken)
    {
        var latestBeforeWrite = await repository.GetSessionAsync(
            session.Environment,
            session.StoreCode,
            session.DeviceCode,
            session.SessionId,
            cancellationToken);
        if (ShouldKeepCompletedSession(latestBeforeWrite, session))
        {
            return latestBeforeWrite!;
        }

        await repository.UpsertSessionAsync(session, cancellationToken);

        var latestAfterWrite = await repository.GetSessionAsync(
            session.Environment,
            session.StoreCode,
            session.DeviceCode,
            session.SessionId,
            cancellationToken);
        return ShouldKeepCompletedSession(latestAfterWrite, session)
            ? latestAfterWrite!
            : latestAfterWrite ?? session;
    }

    private static bool ShouldKeepCompletedSession(
        LinklyCloudBackendSessionRecord? latest,
        LinklyCloudBackendSessionRecord incoming)
    {
        if (latest is null || !IsCompleted(latest))
        {
            return false;
        }

        if (!IsCompleted(incoming))
        {
            return true;
        }

        // 已完成后的 receipt/display 辅助字段可更新，但不同最终结果不能覆盖首次完成结果。
        return !SameOptional(latest.TxnRef, incoming.TxnRef) ||
            !SameOptional(latest.ResponseCode, incoming.ResponseCode) ||
            !SameOptional(latest.ResponseText, incoming.ResponseText);
    }

    private async Task<LinklyCloudBackendSessionResponse> BuildResponseAsync(
        LinklyCloudBackendSessionRecord session,
        CancellationToken cancellationToken)
    {
        var notifications = await repository.GetNotificationsAsync(
            session.Environment,
            session.StoreCode,
            session.DeviceCode,
            session.SessionId,
            cancellationToken);
        return new LinklyCloudBackendSessionResponse(
            session.Environment,
            session.StoreCode,
            session.DeviceCode,
            session.SessionId,
            session.Status,
            session.TxnRef,
            session.ResponseCode,
            session.ResponseText,
            session.RecoveryAction,
            session.DisplayText,
            session.CancelKeyFlag,
            session.OKKeyFlag,
            session.AcceptYesKeyFlag,
            session.DeclineNoKeyFlag,
            session.AuthoriseKeyFlag,
            session.InputType,
            session.GraphicCode,
            SplitDisplayLines(session.DisplayLines),
            session.ReceiptText,
            session.RecoveryCount,
            session.ReceiptPrintedAt,
            session.LastHttpStatus,
            notifications.Select(notification => new LinklyCloudBackendNotificationDto(
                notification.Type,
                notification.PayloadJson,
                notification.ReceivedAt)).ToArray());
    }

    private static async Task<LinklyCloudBackendTransportResponse> SendWithRecoverableFailureAsync(
        Func<Task<LinklyCloudBackendTransportResponse>> sendAsync)
    {
        try
        {
            return await sendAsync();
        }
        catch (HttpRequestException)
        {
            return new LinklyCloudBackendTransportResponse(HttpStatusCode.RequestTimeout, null);
        }
        catch (TaskCanceledException)
        {
            return new LinklyCloudBackendTransportResponse(HttpStatusCode.RequestTimeout, null);
        }
    }

    private static void ApplyTransportResponse(
        LinklyCloudBackendSessionRecord session,
        LinklyCloudBackendTransportResponse response)
    {
        session.UpdatedAt = DateTimeOffset.UtcNow;
        session.LastHttpStatus = (int)response.StatusCode;
        session.RecoveryAction = null;
        if (response.StatusCode == HttpStatusCode.OK)
        {
            ApplyCompletedPayload(session, response.Body);
            return;
        }

        var code = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            session.Status = StatusPending;
            session.IsActive = true;
            return;
        }

        if (IsTransportRecoveryFailure(response.StatusCode))
        {
            session.Status = StatusPending;
            session.RecoveryAction = RecoveryRetry;
            session.IsActive = true;
            return;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            session.Status = StatusTokenRefreshRequired;
            session.RecoveryAction = RecoveryRefreshToken;
            session.IsActive = true;
            return;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            session.Status = StatusNotSubmitted;
            session.RecoveryAction = RecoveryRetryNewSession;
            session.IsActive = false;
            return;
        }

        session.Status = StatusFailed;
        session.ResponseText = $"Linkly Cloud returned HTTP {code}.";
        session.IsActive = false;
    }

    private static void ApplyCompletedPayload(LinklyCloudBackendSessionRecord session, string? payloadJson)
    {
        session.Status = StatusCompleted;
        session.IsActive = false;
        session.RecoveryAction = null;
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return;
        }

        using var document = JsonDocument.Parse(payloadJson);
        var response = ReadResponse(document.RootElement);
        session.ResponseCode = ReadString(response, "ResponseCode");
        session.ResponseText = ReadString(response, "ResponseText");
    }

    private static void ApplyDisplayPayload(
        LinklyCloudBackendSessionRecord session,
        string payloadJson,
        DateTimeOffset receivedAt)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        var response = ReadResponse(root);
        var displayLines = ReadTextLines(root, "DisplayLines") ??
            ReadTextLines(response, "DisplayLines") ??
            ReadTextLines(root, "DisplayText") ??
            ReadTextLines(response, "DisplayText");
        var displayText = ReadText(root, "DisplayText") ??
            ReadText(response, "DisplayText") ??
            (displayLines is null ? null : string.Join(Environment.NewLine, displayLines));

        // display 通知代表当前终端提示快照；缺失字段要清空，避免沿用上一屏按键状态。
        session.DisplayText = displayText;
        session.DisplayLines = displayLines is null ? null : string.Join(Environment.NewLine, displayLines);
        session.CancelKeyFlag = ReadBool(root, "CancelKeyFlag") ?? ReadBool(response, "CancelKeyFlag") ?? false;
        session.OKKeyFlag = ReadBool(root, "OKKeyFlag") ?? ReadBool(response, "OKKeyFlag") ?? false;
        session.AcceptYesKeyFlag = ReadBool(root, "AcceptYesKeyFlag") ?? ReadBool(response, "AcceptYesKeyFlag") ?? false;
        session.DeclineNoKeyFlag = ReadBool(root, "DeclineNoKeyFlag") ?? ReadBool(response, "DeclineNoKeyFlag") ?? false;
        session.AuthoriseKeyFlag = ReadBool(root, "AuthoriseKeyFlag") ?? ReadBool(response, "AuthoriseKeyFlag") ?? false;
        session.InputType = ReadString(root, "InputType") ?? ReadString(response, "InputType");
        session.GraphicCode = ReadString(root, "GraphicCode") ?? ReadString(response, "GraphicCode");

        // 显示和小票通知只更新辅助字段，不改变已完成交易状态。
        session.UpdatedAt = receivedAt;
    }

    private static void ApplyReceiptPayload(
        LinklyCloudBackendSessionRecord session,
        string payloadJson,
        DateTimeOffset receivedAt)
    {
        var receiptText = ReadPayloadText(payloadJson, "ReceiptText");
        if (!string.IsNullOrWhiteSpace(receiptText))
        {
            session.ReceiptText = receiptText;
        }

        session.UpdatedAt = receivedAt;
    }

    private static bool IsTransportRecoveryFailure(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return statusCode == HttpStatusCode.RequestTimeout ||
            code is >= 500 and <= 599;
    }

    private static bool ShouldCountRecoveryResponse(HttpStatusCode statusCode)
    {
        // 202 是 Linkly 普通处理中，继续短轮询即可；其它 recover 响应代表一次恢复尝试已有结果。
        return statusCode != HttpStatusCode.Accepted;
    }

    private static string? ReadPayloadText(string payloadJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(payloadJson);
        return ReadText(document.RootElement, propertyName) ??
            ReadText(ReadResponse(document.RootElement), propertyName);
    }

    private static bool IsCompleted(LinklyCloudBackendSessionRecord session)
    {
        return string.Equals(session.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement ReadResponse(JsonElement root)
    {
        return TryGetProperty(root, "Response", out var response) && response.ValueKind == JsonValueKind.Object
            ? response
            : root;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Undefined ||
            !TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => NormalizeOptional(value.GetString()),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static string? ReadText(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Undefined ||
            !TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return NormalizeOptional(value.GetString());
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var lines = value
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => NormalizeOptional(item.GetString()))
            .Where(line => line is not null)
            .Select(line => line!)
            .ToArray();
        return lines.Length == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    private static IReadOnlyList<string>? ReadTextLines(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Undefined ||
            !TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = NormalizeOptional(value.GetString());
            return text is null ? null : [text];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var lines = value
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => NormalizeOptional(item.GetString()))
            .Where(line => line is not null)
            .Select(line => line!)
            .ToArray();
        return lines.Length == 0 ? null : lines;
    }

    private static bool? ReadBool(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Undefined ||
            !TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed != 0,
            JsonValueKind.Number when value.TryGetInt32(out var parsed) => parsed != 0,
            _ => null
        };
    }

    private static IReadOnlyList<string> SplitDisplayLines(string? displayLines)
    {
        return string.IsNullOrWhiteSpace(displayLines)
            ? []
            : displayLines
                .Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeOptional)
                .Where(line => line is not null)
                .Select(line => line!)
                .ToArray();
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string NormalizeEnvironment(string? environment)
    {
        return LinklyCloudCredentialService.NormalizeEnvironment(environment)
            ?? throw new LinklyCloudBackendValidationException("environment must be Production or Sandbox");
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        return NormalizeOptional(value)
            ?? throw new LinklyCloudBackendValidationException($"{fieldName} is required.");
    }

    private static string NormalizeSendKey(string? key)
    {
        // Linkly sendkey 只接受官方数字枚举；文字键仅作为旧客户端兼容入口。
        var normalized = NormalizeOptional(key)?.ToUpperInvariant();
        return normalized switch
        {
            "0" or "1" or "2" or "3" => normalized,
            "OK" or "CANCEL" => "0",
            "YES" => "1",
            "NO" => "2",
            "AUTH" => "3",
            _ => throw new LinklyCloudBackendValidationException("key must be one of 0, 1, 2, or 3.")
        };
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string NormalizeUuidV4(string? value)
    {
        var normalized = NormalizeRequired(value, "posId");
        if (!Guid.TryParseExact(normalized, "D", out var parsed))
        {
            throw new LinklyCloudBackendValidationException("posId must be UUID v4.");
        }

        normalized = parsed.ToString("D");
        var version = normalized[14];
        var variant = normalized[19];
        if (version != '4' || variant is not ('8' or '9' or 'a' or 'b'))
        {
            throw new LinklyCloudBackendValidationException("posId must be UUID v4.");
        }

        return normalized;
    }

    private static bool SameOptional(string? left, string? right)
    {
        return string.Equals(NormalizeOptional(left), NormalizeOptional(right), StringComparison.OrdinalIgnoreCase);
    }

    private static LinklyCloudBackendHealthCheckDto CreateHealthCheck(
        string code,
        bool isReady,
        string readyMessage,
        string notReadyMessage)
    {
        return new LinklyCloudBackendHealthCheckDto(
            code,
            isReady,
            isReady ? readyMessage : notReadyMessage);
    }

    private static string CreateTxnRef()
    {
        Span<byte> bytes = stackalloc byte[2];
        Random.Shared.NextBytes(bytes);
        return $"{DateTimeOffset.UtcNow:yyMMddHHmmss}{Convert.ToHexString(bytes)}";
    }
}

public sealed class LinklyCloudBackendValidationException(string message) : Exception(message);

public sealed class LinklyCloudBackendActiveTransactionException(string? activeSessionId)
    : Exception("An active Linkly Cloud transaction already exists for this terminal.")
{
    public string? ActiveSessionId { get; } = activeSessionId;
}

public sealed class LinklyCloudBackendSessionNotFoundException()
    : Exception("Linkly Cloud backend session was not found.");

public sealed class LinklyCloudBackendNotificationUnauthorizedException()
    : Exception("Linkly Cloud notification authorization is invalid.");

public interface ILinklyCloudBackendTokenProvider
{
    Task<LinklyCloudBackendToken> GetTokenAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken);
}

public sealed record LinklyCloudBackendToken(
    string RestBaseUrl,
    string AccessToken);

public sealed class HttpLinklyCloudBackendTokenProvider(
    ILinklyCloudCredentialRepository credentialRepository,
    ILinklyCloudBackendTerminalCredentialRepository terminalCredentialRepository,
    HttpClient httpClient,
    IOptions<LinklyCloudBackendAsyncOptions> options) : ILinklyCloudBackendTokenProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<LinklyCloudBackendToken> GetTokenAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironment = NormalizeEnvironment(environment);
        var normalizedStoreCode = NormalizeRequired(storeCode, "storeCode");
        var normalizedDeviceCode = NormalizeRequired(deviceCode, "deviceCode");

        var credential = await credentialRepository.GetByStoreCodeAsync(
            normalizedStoreCode,
            normalizedEnvironment,
            cancellationToken);
        if (credential is null ||
            string.IsNullOrWhiteSpace(credential.Username) ||
            string.IsNullOrWhiteSpace(credential.Password))
        {
            throw new LinklyCloudBackendValidationException(
                "Linkly Cloud credential is not configured for this store and environment.");
        }

        var terminalCredential = await terminalCredentialRepository.GetByDeviceAsync(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            cancellationToken);
        if (terminalCredential is null || string.IsNullOrWhiteSpace(terminalCredential.Secret))
        {
            throw new LinklyCloudBackendValidationException(
                "Linkly Cloud terminal secret is not configured for this terminal.");
        }

        if (string.IsNullOrWhiteSpace(terminalCredential.PosId))
        {
            throw new LinklyCloudBackendValidationException(
                "Linkly Cloud POS ID is not configured for this terminal.");
        }

        var authBaseUrl = NormalizeRequired(GetAuthBaseUrl(normalizedEnvironment), "authBaseUrl");
        var restBaseUrl = NormalizeRequired(GetRestBaseUrl(normalizedEnvironment), "restBaseUrl");
        var posVendorId = NormalizeRequired(GetPosVendorId(normalizedEnvironment), "posVendorId");

        // 服务端凭据是唯一可信来源；请求体里的 token/endpoint 字段即便存在也不会参与签发。
        using var response = await httpClient.PostAsJsonAsync(
            new Uri(GetBaseUri(authBaseUrl), "tokens/cloudpos"),
            new LinklyCloudBackendTokenRequest(
                terminalCredential.Secret.Trim(),
                NormalizeRequired(options.Value.PosName, "posName"),
                NormalizeRequired(options.Value.PosVersion, "posVersion"),
                terminalCredential.PosId.Trim(),
                posVendorId),
            JsonOptions,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new LinklyCloudBackendValidationException(
                $"Linkly Cloud token endpoint returned HTTP {(int)response.StatusCode}.");
        }

        using var document = JsonDocument.Parse(body);
        var token = ReadString(document.RootElement, "token");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new LinklyCloudBackendValidationException("Linkly Cloud token response was missing a token.");
        }

        return new LinklyCloudBackendToken(NormalizeBaseUrl(restBaseUrl), token);
    }

    private string GetAuthBaseUrl(string environment)
    {
        return string.Equals(environment, "Sandbox", StringComparison.Ordinal)
            ? options.Value.SandboxAuthBaseUrl
            : options.Value.ProductionAuthBaseUrl;
    }

    private string GetRestBaseUrl(string environment)
    {
        return string.Equals(environment, "Sandbox", StringComparison.Ordinal)
            ? options.Value.SandboxRestBaseUrl
            : options.Value.ProductionRestBaseUrl;
    }

    private string? GetPosVendorId(string environment)
    {
        return string.Equals(environment, "Sandbox", StringComparison.Ordinal)
            ? options.Value.SandboxPosVendorId
            : options.Value.ProductionPosVendorId;
    }

    private static string NormalizeEnvironment(string? environment)
    {
        return LinklyCloudCredentialService.NormalizeEnvironment(environment)
            ?? throw new LinklyCloudBackendValidationException("environment must be Production or Sandbox");
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new LinklyCloudBackendValidationException($"{fieldName} is required.")
            : value.Trim();
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim();
        return trimmed.EndsWith("/", StringComparison.Ordinal) ? trimmed : trimmed + "/";
    }

    private static Uri GetBaseUri(string baseUrl)
    {
        return new Uri(NormalizeBaseUrl(baseUrl), UriKind.Absolute);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? NormalizeRequired(value.GetString(), propertyName)
            : null;
    }

    private sealed record LinklyCloudBackendTokenRequest(
        [property: JsonPropertyName("secret")] string Secret,
        [property: JsonPropertyName("posName")] string PosName,
        [property: JsonPropertyName("posVersion")] string PosVersion,
        [property: JsonPropertyName("posId")] string PosId,
        [property: JsonPropertyName("posVendorId")] string PosVendorId);
}

public interface ILinklyCloudBackendTerminalCredentialRepository
{
    Task<LinklyCloudBackendTerminalCredentialRecord?> GetByDeviceAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendTerminalCredentialRecord> UpsertAsync(
        string environment,
        string storeCode,
        string deviceCode,
        string secret,
        string posId,
        DateTime updatedAt,
        string? updatedBy,
        CancellationToken cancellationToken);
}

public sealed class SqlSugarLinklyCloudBackendTerminalCredentialRepository(
    HbposSqlSugarContext dbContext) : ILinklyCloudBackendTerminalCredentialRepository
{
    internal const string UpsertSql = """
        MERGE [dbo].[POSM_LinklyCloudBackendTerminal] WITH (HOLDLOCK) AS target
        USING (
            SELECT @Environment AS [Environment],
                   @StoreCode AS [StoreCode],
                   @DeviceCode AS [DeviceCode]) AS source
        ON target.[Environment] = source.[Environment]
           AND target.[StoreCode] = source.[StoreCode]
           AND target.[DeviceCode] = source.[DeviceCode]
        WHEN MATCHED THEN
            UPDATE SET
                [Secret] = @Secret,
                [PosId] = @PosId,
                [UpdatedAt] = @UpdatedAt,
                [UpdatedBy] = @UpdatedBy
        WHEN NOT MATCHED THEN
            INSERT ([Environment], [StoreCode], [DeviceCode], [Secret], [PosId], [UpdatedAt], [UpdatedBy])
            VALUES (@Environment, @StoreCode, @DeviceCode, @Secret, @PosId, @UpdatedAt, @UpdatedBy);
        """;

    public async Task<LinklyCloudBackendTerminalCredentialRecord?> GetByDeviceAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                [Id],
                [Environment],
                [StoreCode],
                [DeviceCode],
                [Secret],
                [PosId],
                [UpdatedAt],
                [UpdatedBy]
            FROM [dbo].[POSM_LinklyCloudBackendTerminal]
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [DeviceCode] = @DeviceCode
              AND NULLIF(LTRIM(RTRIM([Secret])), '') IS NOT NULL
              AND NULLIF(LTRIM(RTRIM([PosId])), '') IS NOT NULL
            ORDER BY [UpdatedAt] DESC, [Id] DESC;
            """;

        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<LinklyCloudBackendTerminalCredentialRecord>(
            sql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@DeviceCode", deviceCode));
    }

    public async Task<LinklyCloudBackendTerminalCredentialRecord> UpsertAsync(
        string environment,
        string storeCode,
        string deviceCode,
        string secret,
        string posId,
        DateTime updatedAt,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        // 终端 secret 只作为数据库参数写入，接口响应始终只暴露 HasSecret。
        await dbContext.PosmDb.Ado.ExecuteCommandAsync(
            UpsertSql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@DeviceCode", deviceCode),
            new SugarParameter("@Secret", secret),
            new SugarParameter("@PosId", posId),
            new SugarParameter("@UpdatedAt", updatedAt),
            new SugarParameter("@UpdatedBy", updatedBy));

        return await GetByDeviceAsync(environment, storeCode, deviceCode, cancellationToken)
            ?? new LinklyCloudBackendTerminalCredentialRecord
            {
                Environment = environment,
                StoreCode = storeCode,
                DeviceCode = deviceCode,
                Secret = secret,
                PosId = posId,
                UpdatedAt = updatedAt,
                UpdatedBy = updatedBy
            };
    }
}

public sealed class LinklyCloudBackendTerminalCredentialRecord
{
    public long Id { get; set; }

    public string? Environment { get; set; }

    public string? StoreCode { get; set; }

    public string? DeviceCode { get; set; }

    public string? Secret { get; set; }

    public string? PosId { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public string? UpdatedBy { get; set; }
}

public interface ILinklyCloudBackendAsyncTransport
{
    Task<LinklyCloudBackendTransportResponse> StartTransactionAsync(
        LinklyCloudBackendTransportTransactionRequest request,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendTransportResponse> RecoverTransactionAsync(
        LinklyCloudBackendTransportSessionRequest request,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendTransportResponse> SendKeyAsync(
        LinklyCloudBackendTransportSendKeyRequest request,
        CancellationToken cancellationToken);
}

public sealed record LinklyCloudBackendTransportResponse(
    HttpStatusCode StatusCode,
    string? Body);

public sealed record LinklyCloudBackendNotificationRequest(
    string Uri,
    string AuthorizationHeader);

public sealed record LinklyCloudBackendTransportTransactionRequest(
    string Environment,
    string RestBaseUrl,
    string AccessToken,
    string SessionId,
    string TxnType,
    long AmtPurchase,
    string TxnRef,
    IReadOnlyDictionary<string, string>? PurchaseAnalysisData,
    LinklyCloudBackendNotificationRequest Notification);

public sealed record LinklyCloudBackendTransportSessionRequest(
    string Environment,
    string RestBaseUrl,
    string AccessToken,
    string SessionId);

public sealed record LinklyCloudBackendTransportSendKeyRequest(
    string Environment,
    string RestBaseUrl,
    string AccessToken,
    string SessionId,
    string Key,
    string? Data);

public sealed class HttpLinklyCloudBackendAsyncTransport(HttpClient httpClient) : ILinklyCloudBackendAsyncTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public Task<LinklyCloudBackendTransportResponse> StartTransactionAsync(
        LinklyCloudBackendTransportTransactionRequest request,
        CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, object?>
        {
            ["Merchant"] = "00",
            ["Application"] = "00",
            ["TxnType"] = request.TxnType,
            ["AmtPurchase"] = request.AmtPurchase,
            ["TxnRef"] = request.TxnRef,
            ["CurrencyCode"] = "AUD",
            ["CutReceipt"] = "0",
            ["ReceiptAutoPrint"] = "0"
        };
        if (request.PurchaseAnalysisData is { Count: > 0 })
        {
            fields["PurchaseAnalysisData"] = request.PurchaseAnalysisData;
        }

        return SendAsync(
            request.RestBaseUrl,
            request.AccessToken,
            request.SessionId,
            "transaction",
            HttpMethod.Post,
            new LinklyCloudBackendApiRequest(
                fields,
                new LinklyCloudBackendApiNotification(
                    request.Notification.Uri,
                    request.Notification.AuthorizationHeader)),
            cancellationToken);
    }

    public Task<LinklyCloudBackendTransportResponse> RecoverTransactionAsync(
        LinklyCloudBackendTransportSessionRequest request,
        CancellationToken cancellationToken)
    {
        return SendAsync(
            request.RestBaseUrl,
            request.AccessToken,
            request.SessionId,
            "transaction",
            HttpMethod.Get,
            body: null,
            cancellationToken);
    }

    public Task<LinklyCloudBackendTransportResponse> SendKeyAsync(
        LinklyCloudBackendTransportSendKeyRequest request,
        CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, object?>
        {
            ["Key"] = request.Key
        };
        if (!string.IsNullOrWhiteSpace(request.Data))
        {
            fields["Data"] = request.Data;
        }

        return SendAsync(
            request.RestBaseUrl,
            request.AccessToken,
            request.SessionId,
            "sendkey",
            HttpMethod.Post,
            new LinklyCloudBackendApiRequest(fields, Notification: null),
            cancellationToken);
    }

    private async Task<LinklyCloudBackendTransportResponse> SendAsync(
        string restBaseUrl,
        string accessToken,
        string sessionId,
        string requestType,
        HttpMethod method,
        LinklyCloudBackendApiRequest? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            method,
            new Uri(GetBaseUri(restBaseUrl), $"sessions/{Uri.EscapeDataString(sessionId)}/{requestType}?async=true"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        return new LinklyCloudBackendTransportResponse(response.StatusCode, responseBody);
    }

    private static Uri GetBaseUri(string baseUrl)
    {
        var trimmed = baseUrl.Trim();
        return new Uri(trimmed.EndsWith("/", StringComparison.Ordinal) ? trimmed : trimmed + "/", UriKind.Absolute);
    }

    private sealed record LinklyCloudBackendApiRequest(
        [property: JsonPropertyName("Request")] IReadOnlyDictionary<string, object?> Request,
        [property: JsonPropertyName("Notification")] LinklyCloudBackendApiNotification? Notification);

    private sealed record LinklyCloudBackendApiNotification(
        [property: JsonPropertyName("Uri")] string Uri,
        [property: JsonPropertyName("AuthorizationHeader")] string AuthorizationHeader);
}

public interface ILinklyCloudBackendAsyncRepository
{
    Task<bool> TryCreateSessionAsync(
        LinklyCloudBackendSessionRecord session,
        CancellationToken cancellationToken);

    Task UpsertSessionAsync(
        LinklyCloudBackendSessionRecord session,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendSessionRecord?> GetSessionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        string sessionId,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendSessionRecord?> GetSessionByEnvironmentSessionIdAsync(
        string environment,
        string sessionId,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendSessionRecord?> GetActiveSessionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken);

    Task AddNotificationAsync(
        LinklyCloudBackendNotificationRecord notification,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<LinklyCloudBackendNotificationRecord>> GetNotificationsAsync(
        string environment,
        string storeCode,
        string deviceCode,
        string sessionId,
        CancellationToken cancellationToken);
}

public sealed class InMemoryLinklyCloudBackendAsyncRepository : ILinklyCloudBackendAsyncRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LinklyCloudBackendSessionRecord> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LinklyCloudBackendNotificationRecord> _notifications = [];

    public Task<bool> TryCreateSessionAsync(
        LinklyCloudBackendSessionRecord session,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_sessions.Values.Any(existing =>
                existing.IsActive &&
                SameTerminal(existing, session.Environment, session.StoreCode, session.DeviceCode)))
            {
                return Task.FromResult(false);
            }

            for (var attempt = 0; attempt < 5 && _sessions.Values.Any(existing =>
                string.Equals(existing.Environment, session.Environment, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.StoreCode, session.StoreCode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.TxnRef, session.TxnRef, StringComparison.OrdinalIgnoreCase)); attempt++)
            {
                session.TxnRef = CreateTxnRef();
            }

            if (_sessions.Values.Any(existing =>
                string.Equals(existing.Environment, session.Environment, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.StoreCode, session.StoreCode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.TxnRef, session.TxnRef, StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromResult(false);
            }

            _sessions[SessionKey(session.Environment, session.StoreCode, session.DeviceCode, session.SessionId)] = Clone(session);
            return Task.FromResult(true);
        }
    }

    public Task UpsertSessionAsync(
        LinklyCloudBackendSessionRecord session,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var key = SessionKey(session.Environment, session.StoreCode, session.DeviceCode, session.SessionId);
            if (_sessions.TryGetValue(key, out var existing) &&
                IsCompleted(existing) &&
                !IsCompleted(session))
            {
                return Task.CompletedTask;
            }

            _sessions[key] = Clone(session);
            return Task.CompletedTask;
        }
    }

    public Task<LinklyCloudBackendSessionRecord?> GetSessionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        string sessionId,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<LinklyCloudBackendSessionRecord?>(_sessions.TryGetValue(SessionKey(environment, storeCode, deviceCode, sessionId), out var session)
                ? Clone(session)
                : null);
        }
    }

    public Task<LinklyCloudBackendSessionRecord?> GetSessionByEnvironmentSessionIdAsync(
        string environment,
        string sessionId,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var session = _sessions.Values.FirstOrDefault(existing =>
                string.Equals(existing.Environment, environment, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(session is null ? null : Clone(session));
        }
    }

    public Task<LinklyCloudBackendSessionRecord?> GetActiveSessionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var session = _sessions.Values.FirstOrDefault(existing =>
                existing.IsActive && SameTerminal(existing, environment, storeCode, deviceCode));
            return Task.FromResult(session is null ? null : Clone(session));
        }
    }

    public Task AddNotificationAsync(
        LinklyCloudBackendNotificationRecord notification,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_notifications.Any(existing =>
                string.Equals(existing.Environment, notification.Environment, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.StoreCode, notification.StoreCode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.DeviceCode, notification.DeviceCode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.SessionId, notification.SessionId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.Type, notification.Type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.PayloadJson, notification.PayloadJson, StringComparison.Ordinal)))
            {
                _notifications.Add(Clone(notification));
            }

            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<LinklyCloudBackendNotificationRecord>> GetNotificationsAsync(
        string environment,
        string storeCode,
        string deviceCode,
        string sessionId,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<LinklyCloudBackendNotificationRecord>>(
                _notifications
                    .Where(notification =>
                        string.Equals(notification.Environment, environment, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(notification.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(notification.DeviceCode, deviceCode, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(notification.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(notification => notification.ReceivedAt)
                    .Select(Clone)
                    .ToArray());
        }
    }

    private static bool SameTerminal(
        LinklyCloudBackendSessionRecord session,
        string environment,
        string storeCode,
        string deviceCode)
    {
        return string.Equals(session.Environment, environment, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(session.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(session.DeviceCode, deviceCode, StringComparison.OrdinalIgnoreCase);
    }

    private static string SessionKey(string environment, string storeCode, string deviceCode, string sessionId)
    {
        return string.Join('|', environment, storeCode, deviceCode, sessionId);
    }

    private static bool IsCompleted(LinklyCloudBackendSessionRecord session)
    {
        return string.Equals(session.Status, "Completed", StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateTxnRef()
    {
        Span<byte> bytes = stackalloc byte[2];
        Random.Shared.NextBytes(bytes);
        return $"{DateTimeOffset.UtcNow:yyMMddHHmmss}{Convert.ToHexString(bytes)}";
    }

    private static LinklyCloudBackendSessionRecord Clone(LinklyCloudBackendSessionRecord session)
    {
        return new LinklyCloudBackendSessionRecord
        {
            Id = session.Id,
            Environment = session.Environment,
            StoreCode = session.StoreCode,
            DeviceCode = session.DeviceCode,
            SessionId = session.SessionId,
            Status = session.Status,
            TxnRef = session.TxnRef,
            ResponseCode = session.ResponseCode,
            ResponseText = session.ResponseText,
            RecoveryAction = session.RecoveryAction,
            DisplayText = session.DisplayText,
            DisplayLines = session.DisplayLines,
            CancelKeyFlag = session.CancelKeyFlag,
            OKKeyFlag = session.OKKeyFlag,
            AcceptYesKeyFlag = session.AcceptYesKeyFlag,
            DeclineNoKeyFlag = session.DeclineNoKeyFlag,
            AuthoriseKeyFlag = session.AuthoriseKeyFlag,
            InputType = session.InputType,
            GraphicCode = session.GraphicCode,
            ReceiptText = session.ReceiptText,
            RecoveryCount = session.RecoveryCount,
            ReceiptPrintedAt = session.ReceiptPrintedAt,
            LastHttpStatus = session.LastHttpStatus,
            IsActive = session.IsActive,
            UpdatedAt = session.UpdatedAt
        };
    }

    private static LinklyCloudBackendNotificationRecord Clone(LinklyCloudBackendNotificationRecord notification)
    {
        return new LinklyCloudBackendNotificationRecord
        {
            Id = notification.Id,
            Environment = notification.Environment,
            StoreCode = notification.StoreCode,
            DeviceCode = notification.DeviceCode,
            SessionId = notification.SessionId,
            Type = notification.Type,
            PayloadJson = notification.PayloadJson,
            ReceivedAt = notification.ReceivedAt
        };
    }
}

public sealed class SqlSugarLinklyCloudBackendAsyncRepository(
    HbposSqlSugarContext dbContext) : ILinklyCloudBackendAsyncRepository
{
    internal const string TryCreateSessionSql = """
        SET XACT_ABORT ON;
        SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
        BEGIN TRANSACTION;

        IF NOT EXISTS (
            SELECT 1
            FROM [dbo].[POSM_LinklyCloudBackendSession] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [DeviceCode] = @DeviceCode
              AND [IsActive] = 1)
        AND NOT EXISTS (
            SELECT 1
            FROM [dbo].[POSM_LinklyCloudBackendSession] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [TxnRef] = @TxnRef)
        BEGIN
            INSERT INTO [dbo].[POSM_LinklyCloudBackendSession] (
                [Environment], [StoreCode], [DeviceCode], [SessionId], [Status], [TxnRef],
                [ResponseCode], [ResponseText], [RecoveryAction], [DisplayText], [DisplayLines],
                [CancelKeyFlag], [OKKeyFlag], [AcceptYesKeyFlag], [DeclineNoKeyFlag], [AuthoriseKeyFlag],
                [InputType], [GraphicCode], [ReceiptText],
                [RecoveryCount], [ReceiptPrintedAt], [LastHttpStatus], [IsActive], [UpdatedAt])
            VALUES (
                @Environment, @StoreCode, @DeviceCode, @SessionId, @Status, @TxnRef,
                @ResponseCode, @ResponseText, @RecoveryAction, @DisplayText, @DisplayLines,
                @CancelKeyFlag, @OKKeyFlag, @AcceptYesKeyFlag, @DeclineNoKeyFlag, @AuthoriseKeyFlag,
                @InputType, @GraphicCode, @ReceiptText,
                @RecoveryCount, @ReceiptPrintedAt, @LastHttpStatus, @IsActive, @UpdatedAt);
        END;

        COMMIT TRANSACTION;
        """;

    internal const string UpsertSessionSql = """
        MERGE [dbo].[POSM_LinklyCloudBackendSession] AS target
        USING (SELECT @Environment AS [Environment], @StoreCode AS [StoreCode], @DeviceCode AS [DeviceCode], @SessionId AS [SessionId]) AS source
        ON target.[Environment] = source.[Environment]
           AND target.[StoreCode] = source.[StoreCode]
           AND target.[DeviceCode] = source.[DeviceCode]
           AND target.[SessionId] = source.[SessionId]
        WHEN MATCHED
             AND NOT (
                target.[Status] = 'Completed'
                AND (
                    @Status <> 'Completed'
                    OR ISNULL(target.[TxnRef], N'') <> ISNULL(@TxnRef, N'')
                    OR ISNULL(target.[ResponseCode], N'') <> ISNULL(@ResponseCode, N'')
                    OR ISNULL(target.[ResponseText], N'') <> ISNULL(@ResponseText, N'')
                )) THEN
            UPDATE SET
                [Status] = @Status,
                [TxnRef] = @TxnRef,
                [ResponseCode] = @ResponseCode,
                [ResponseText] = @ResponseText,
                [RecoveryAction] = @RecoveryAction,
                [DisplayText] = @DisplayText,
                [DisplayLines] = @DisplayLines,
                [CancelKeyFlag] = @CancelKeyFlag,
                [OKKeyFlag] = @OKKeyFlag,
                [AcceptYesKeyFlag] = @AcceptYesKeyFlag,
                [DeclineNoKeyFlag] = @DeclineNoKeyFlag,
                [AuthoriseKeyFlag] = @AuthoriseKeyFlag,
                [InputType] = @InputType,
                [GraphicCode] = @GraphicCode,
                [ReceiptText] = @ReceiptText,
                [RecoveryCount] = @RecoveryCount,
                [ReceiptPrintedAt] = @ReceiptPrintedAt,
                [LastHttpStatus] = @LastHttpStatus,
                [IsActive] = @IsActive,
                [UpdatedAt] = @UpdatedAt
        WHEN NOT MATCHED THEN
            INSERT (
                [Environment], [StoreCode], [DeviceCode], [SessionId], [Status], [TxnRef],
                [ResponseCode], [ResponseText], [RecoveryAction], [DisplayText], [DisplayLines],
                [CancelKeyFlag], [OKKeyFlag], [AcceptYesKeyFlag], [DeclineNoKeyFlag], [AuthoriseKeyFlag],
                [InputType], [GraphicCode], [ReceiptText],
                [RecoveryCount], [ReceiptPrintedAt], [LastHttpStatus], [IsActive], [UpdatedAt])
            VALUES (
                @Environment, @StoreCode, @DeviceCode, @SessionId, @Status, @TxnRef,
                @ResponseCode, @ResponseText, @RecoveryAction, @DisplayText, @DisplayLines,
                @CancelKeyFlag, @OKKeyFlag, @AcceptYesKeyFlag, @DeclineNoKeyFlag, @AuthoriseKeyFlag,
                @InputType, @GraphicCode, @ReceiptText,
                @RecoveryCount, @ReceiptPrintedAt, @LastHttpStatus, @IsActive, @UpdatedAt);
        """;

    public async Task<bool> TryCreateSessionAsync(
        LinklyCloudBackendSessionRecord session,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var affected = await dbContext.PosmDb.Ado.ExecuteCommandAsync(TryCreateSessionSql, ToSessionParameters(session));
                if (affected > 0)
                {
                    return true;
                }
            }
            catch (Exception ex) when (IsUniqueConstraintViolation(ex))
            {
                var activeAfterConflict = await GetActiveSessionAsync(session.Environment, session.StoreCode, session.DeviceCode, cancellationToken);
                if (activeAfterConflict is not null ||
                    ex.ToString().Contains("UX_POSM_LinklyCloudBackendSession_ActiveTerminal", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                session.TxnRef = CreateTxnRef();
                continue;
            }

            var active = await GetActiveSessionAsync(session.Environment, session.StoreCode, session.DeviceCode, cancellationToken);
            if (active is not null)
            {
                return false;
            }

            session.TxnRef = CreateTxnRef();
        }

        return false;
    }

    private static bool IsUniqueConstraintViolation(Exception ex)
    {
        var text = ex.ToString();
        return text.Contains("2601", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("2627", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("UX_POSM_LinklyCloudBackendSession", StringComparison.OrdinalIgnoreCase);
    }

    public Task UpsertSessionAsync(
        LinklyCloudBackendSessionRecord session,
        CancellationToken cancellationToken)
    {
        return dbContext.PosmDb.Ado.ExecuteCommandAsync(UpsertSessionSql, ToSessionParameters(session));
    }

    public async Task<LinklyCloudBackendSessionRecord?> GetSessionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        string sessionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                [Id], [Environment], [StoreCode], [DeviceCode], [SessionId], [Status], [TxnRef],
                [ResponseCode], [ResponseText], [RecoveryAction], [DisplayText], [DisplayLines],
                [CancelKeyFlag], [OKKeyFlag], [AcceptYesKeyFlag], [DeclineNoKeyFlag], [AuthoriseKeyFlag],
                [InputType], [GraphicCode], [ReceiptText],
                [RecoveryCount], [ReceiptPrintedAt], [LastHttpStatus], [IsActive], [UpdatedAt]
            FROM [dbo].[POSM_LinklyCloudBackendSession]
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [DeviceCode] = @DeviceCode
              AND [SessionId] = @SessionId;
            """;

        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<LinklyCloudBackendSessionRecord>(
            sql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@DeviceCode", deviceCode),
            new SugarParameter("@SessionId", sessionId));
    }

    public async Task<LinklyCloudBackendSessionRecord?> GetSessionByEnvironmentSessionIdAsync(
        string environment,
        string sessionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                [Id], [Environment], [StoreCode], [DeviceCode], [SessionId], [Status], [TxnRef],
                [ResponseCode], [ResponseText], [RecoveryAction], [DisplayText], [DisplayLines],
                [CancelKeyFlag], [OKKeyFlag], [AcceptYesKeyFlag], [DeclineNoKeyFlag], [AuthoriseKeyFlag],
                [InputType], [GraphicCode], [ReceiptText],
                [RecoveryCount], [ReceiptPrintedAt], [LastHttpStatus], [IsActive], [UpdatedAt]
            FROM [dbo].[POSM_LinklyCloudBackendSession]
            WHERE [Environment] = @Environment
              AND [SessionId] = @SessionId
            ORDER BY [UpdatedAt] DESC, [Id] DESC;
            """;

        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<LinklyCloudBackendSessionRecord>(
            sql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@SessionId", sessionId));
    }

    public async Task<LinklyCloudBackendSessionRecord?> GetActiveSessionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                [Id], [Environment], [StoreCode], [DeviceCode], [SessionId], [Status], [TxnRef],
                [ResponseCode], [ResponseText], [RecoveryAction], [DisplayText], [DisplayLines],
                [CancelKeyFlag], [OKKeyFlag], [AcceptYesKeyFlag], [DeclineNoKeyFlag], [AuthoriseKeyFlag],
                [InputType], [GraphicCode], [ReceiptText],
                [RecoveryCount], [ReceiptPrintedAt], [LastHttpStatus], [IsActive], [UpdatedAt]
            FROM [dbo].[POSM_LinklyCloudBackendSession]
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [DeviceCode] = @DeviceCode
              AND [IsActive] = 1
            ORDER BY [UpdatedAt] DESC, [Id] DESC;
            """;

        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<LinklyCloudBackendSessionRecord>(
            sql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@DeviceCode", deviceCode));
    }

    public Task AddNotificationAsync(
        LinklyCloudBackendNotificationRecord notification,
        CancellationToken cancellationToken)
    {
        const string sql = """
            IF NOT EXISTS (
                SELECT 1
                FROM [dbo].[POSM_LinklyCloudBackendNotification] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Environment] = @Environment
                  AND [StoreCode] = @StoreCode
                  AND [DeviceCode] = @DeviceCode
                  AND [SessionId] = @SessionId
                  AND [Type] = @Type
                  AND [PayloadJson] = @PayloadJson)
            BEGIN
                INSERT INTO [dbo].[POSM_LinklyCloudBackendNotification] (
                    [Environment], [StoreCode], [DeviceCode], [SessionId], [Type], [PayloadJson], [ReceivedAt])
                VALUES (
                    @Environment, @StoreCode, @DeviceCode, @SessionId, @Type, @PayloadJson, @ReceivedAt);
            END;
            """;

        return dbContext.PosmDb.Ado.ExecuteCommandAsync(
            sql,
            new SugarParameter("@Environment", notification.Environment),
            new SugarParameter("@StoreCode", notification.StoreCode),
            new SugarParameter("@DeviceCode", notification.DeviceCode),
            new SugarParameter("@SessionId", notification.SessionId),
            new SugarParameter("@Type", notification.Type),
            new SugarParameter("@PayloadJson", notification.PayloadJson),
            new SugarParameter("@ReceivedAt", notification.ReceivedAt.UtcDateTime));
    }

    public async Task<IReadOnlyList<LinklyCloudBackendNotificationRecord>> GetNotificationsAsync(
        string environment,
        string storeCode,
        string deviceCode,
        string sessionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                [Id], [Environment], [StoreCode], [DeviceCode], [SessionId], [Type], [PayloadJson], [ReceivedAt]
            FROM [dbo].[POSM_LinklyCloudBackendNotification]
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [DeviceCode] = @DeviceCode
              AND [SessionId] = @SessionId
            ORDER BY [ReceivedAt] ASC, [Id] ASC;
            """;

        return await dbContext.PosmDb.Ado.SqlQueryAsync<LinklyCloudBackendNotificationRecord>(
            sql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@DeviceCode", deviceCode),
            new SugarParameter("@SessionId", sessionId));
    }

    private static SugarParameter[] ToSessionParameters(LinklyCloudBackendSessionRecord session)
    {
        return
        [
            new SugarParameter("@Environment", session.Environment),
            new SugarParameter("@StoreCode", session.StoreCode),
            new SugarParameter("@DeviceCode", session.DeviceCode),
            new SugarParameter("@SessionId", session.SessionId),
            new SugarParameter("@Status", session.Status),
            new SugarParameter("@TxnRef", session.TxnRef),
            new SugarParameter("@ResponseCode", session.ResponseCode),
            new SugarParameter("@ResponseText", session.ResponseText),
            new SugarParameter("@RecoveryAction", session.RecoveryAction),
            new SugarParameter("@DisplayText", session.DisplayText),
            new SugarParameter("@DisplayLines", session.DisplayLines),
            new SugarParameter("@CancelKeyFlag", session.CancelKeyFlag),
            new SugarParameter("@OKKeyFlag", session.OKKeyFlag),
            new SugarParameter("@AcceptYesKeyFlag", session.AcceptYesKeyFlag),
            new SugarParameter("@DeclineNoKeyFlag", session.DeclineNoKeyFlag),
            new SugarParameter("@AuthoriseKeyFlag", session.AuthoriseKeyFlag),
            new SugarParameter("@InputType", session.InputType),
            new SugarParameter("@GraphicCode", session.GraphicCode),
            new SugarParameter("@ReceiptText", session.ReceiptText),
            new SugarParameter("@RecoveryCount", session.RecoveryCount),
            new SugarParameter("@ReceiptPrintedAt", session.ReceiptPrintedAt?.UtcDateTime),
            new SugarParameter("@LastHttpStatus", session.LastHttpStatus),
            new SugarParameter("@IsActive", session.IsActive),
            new SugarParameter("@UpdatedAt", session.UpdatedAt.UtcDateTime)
        ];
    }

    private static string CreateTxnRef()
    {
        Span<byte> bytes = stackalloc byte[2];
        Random.Shared.NextBytes(bytes);
        return $"{DateTimeOffset.UtcNow:yyMMddHHmmss}{Convert.ToHexString(bytes)}";
    }
}

public sealed class LinklyCloudBackendSessionRecord
{
    public long Id { get; set; }

    public string Environment { get; set; } = string.Empty;

    public string StoreCode { get; set; } = string.Empty;

    public string DeviceCode { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? TxnRef { get; set; }

    public string? ResponseCode { get; set; }

    public string? ResponseText { get; set; }

    public string? RecoveryAction { get; set; }

    public string? DisplayText { get; set; }

    public string? DisplayLines { get; set; }

    public bool CancelKeyFlag { get; set; }

    public bool OKKeyFlag { get; set; }

    public bool AcceptYesKeyFlag { get; set; }

    public bool DeclineNoKeyFlag { get; set; }

    public bool AuthoriseKeyFlag { get; set; }

    public string? InputType { get; set; }

    public string? GraphicCode { get; set; }

    public string? ReceiptText { get; set; }

    public int RecoveryCount { get; set; }

    public DateTimeOffset? ReceiptPrintedAt { get; set; }

    public int? LastHttpStatus { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class LinklyCloudBackendNotificationRecord
{
    public long Id { get; set; }

    public string Environment { get; set; } = string.Empty;

    public string StoreCode { get; set; } = string.Empty;

    public string DeviceCode { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = string.Empty;

    public DateTimeOffset ReceivedAt { get; set; }
}
