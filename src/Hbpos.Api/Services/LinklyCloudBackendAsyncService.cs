using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hbpos.Api.Data;
using Hbpos.Contracts.Linkly;
using Microsoft.Extensions.Logging;
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

    Task<LinklyCloudBackendSessionResponse?> GetResumableSessionAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendHealthResponse> GetHealthAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendLogonTestResponse> RunLogonTestAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendStatusTestResponse> RunStatusTestAsync(
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

    Task<LinklyCloudBackendSessionResponse> AcknowledgeSessionAsync(
        string storeCode,
        string deviceCode,
        string environment,
        string sessionId,
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
    IOptions<LinklyCloudBackendAsyncOptions> options,
    ILogger<LinklyCloudBackendAsyncService>? logger = null) : ILinklyCloudBackendAsyncService
{
    private static readonly JsonSerializerOptions ServiceJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

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
        Log(
            $"transaction start environment={LogValue(environment)} " +
            $"store={LogValue(normalizedStoreCode)} device={LogValue(normalizedDeviceCode)} " +
            $"componentVersion={GetComponentVersion()}");
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
        var txnType = NormalizeRequired(request.TxnType, "txnType");
        var purchaseAnalysisData = EnsurePurchaseAnalysisData(
            txnType,
            request.AmtPurchase,
            session.TxnRef!,
            request.PurchaseAnalysisData);
        var transportRequest = new LinklyCloudBackendTransportTransactionRequest(
            environment,
            token.RestBaseUrl,
            token.AccessToken,
            session.SessionId,
            txnType,
            request.AmtPurchase,
            session.TxnRef!,
            purchaseAnalysisData,
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

    public async Task<LinklyCloudBackendSessionResponse?> GetResumableSessionAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken)
    {
        var session = await repository.GetResumableSessionAsync(
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

        // 闁诡厹鍨归ˇ鏌ュ传瀹ュ懐瀹夐柛娆樹簻缁ㄦ煡鎮介妸銈囶伇婵炲棌妲勭�?08/5xx 濞ｅ洦绻冪€?Pending�?01 閻熸洑鐒﹂惇浼村礆闁垮鐓€ token�?04 闂佹彃锕ラ弬浣该虹拠鎻捫楅梺澶搁檷閳?
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
        ApplySendKeyTransportResponse(session, response);
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

        // 闁衡偓閼搁潧绁﹂梺顐ｆ皑閻擄繝宕ｉ鍥跺殯闁哄嫬瀛╅弸鍐嫉椤掆偓閸戯繝宕氶幏灞惧涧闁挎稒绋戦褰掑箣妞嬪寒浼傜痪顓у枦椤撶粯娼诲☉妯哄汲闁瑰灚鎸稿畵鍐╃閵堝嫮甯涢梺鐐礃閻箖宕ユ惔顖滅闁归潧绉村﹢顏呮交濞嗘挸娅￠柡宥呮穿椤斿洤顔忛崣澶娾叺闁告鍩囬埀?
        session.ReceiptPrintedAt ??= now;
        session.UpdatedAt = now;
        var savedSession = await UpsertSessionAndReadLatestAsync(session, cancellationToken);
        return await BuildResponseAsync(savedSession, cancellationToken);
    }

    public async Task<LinklyCloudBackendSessionResponse> AcknowledgeSessionAsync(
        string storeCode,
        string deviceCode,
        string environment,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var acknowledgedAt = DateTimeOffset.UtcNow;
        var session = await repository.AcknowledgeSessionAsync(
            NormalizeEnvironment(environment),
            NormalizeRequired(storeCode, "storeCode"),
            NormalizeRequired(deviceCode, "deviceCode"),
            NormalizeRequired(sessionId, "sessionId"),
            acknowledgedAt,
            cancellationToken);

        return session is null
            ? throw new LinklyCloudBackendSessionNotFoundException()
            : await BuildResponseAsync(session, cancellationToken);
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
        var authorizationMatches = !string.IsNullOrWhiteSpace(expectedBearer) &&
            string.Equals(authorizationHeader?.Trim(), $"Bearer {expectedBearer}", StringComparison.Ordinal);
        Log(
            $"notification received environment={LogValue(normalizedEnvironment)} " +
            $"sessionId={LogValue(sessionId)} type={LogValue(type)} " +
            $"authorizationPresent={!string.IsNullOrWhiteSpace(authorizationHeader)} authorizationMatches={authorizationMatches}");
        if (string.IsNullOrWhiteSpace(expectedBearer) ||
            !authorizationMatches)
        {
            Log(
                $"notification rejected environment={LogValue(normalizedEnvironment)} " +
                $"sessionId={LogValue(sessionId)} type={LogValue(type)} reason=invalid-authorization");
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
            Log(
                $"notification ignored environment={LogValue(normalizedEnvironment)} " +
                $"sessionId={LogValue(normalizedSessionId)} type={LogValue(normalizedType)} reason=session-not-found");
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
            LogTransactionNotificationSnapshot(normalizedSessionId, payloadJson);
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
        LogServiceJson(
            operation: "backend-health",
            phase: "request",
            direction: "request",
            environment: normalizedEnvironment,
            storeCode: normalizedStoreCode,
            deviceCode: normalizedDeviceCode,
            success: null,
            reason: null,
            request: new
            {
                environment = normalizedEnvironment,
                storeCode = normalizedStoreCode,
                deviceCode = normalizedDeviceCode
            },
            response: null,
            details: null);

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

        // 闂傚倸鍊搁崐鐑芥嚄閸洍鈧箓宕奸妷顔芥櫈闂佸憡鍔﹂崰鏍閸ф鈷戞い鎺嗗亾缂佸顕划濠氬冀椤愮喎浜炬鐐茬仢閸旀岸鏌熼崣澹濐亪鍩ユ径鎰潊闁绘﹢娼ф慨锔戒繆閻愵亜鈧牜鏁幒鏂哄亾濮樼厧澧撮柟閿嬪灴閹垽宕楅懖鈺佸妇濠电姰鍨煎▔娑㈡偋閸℃稑绀夋慨姗嗗幘缁犻箖鏌熼崘鎻掓Щ闁告碍锕㈠顕€宕奸悢铚傛睏闂備浇鍋愰埛鍫ュ礈濞戙垹绀夋い鏇楀亾婵﹦鍎ょ€电厧鈻庨幋鐘虫婵＄偑鍊х粻鎾愁焽瑜旈、姘舵晲婢跺浠洪梺鍛婃尭瀵爼寮查姀銈嗏拺闂侇偆鍋涢懟顖涙櫠椤曗偓閺屻劑寮村Ο铏逛紙濡ょ姷鍋為敃銏ゃ€佸▎鎾村仼閻忕偠妫勭粭宀勬⒑鐠囨煡顎楃紒鐘茬Ч瀹曟洟宕￠悘缁樻そ婵℃悂鍩℃担绋挎闂備胶顭堥惉濂稿磻閻愮儤鍋傞柕澶涘缁犻箖鏌熺€甸晲绱虫い蹇撶墑閳ь剙鍟埢搴ㄥ箣閻樼绱叉繝寰锋澘鈧劙宕戦幘娣簻闁挎梻鍋撻弳顒勬�?active session �?404 闂傚倷娴囧畷鐢稿窗閹邦喖鍨濋幖娣灪濞呯姵淇婇妶鍛櫣缂佺姵婢橀—鍐偓锝庝簼閹癸絿绱掗埀顒佺節閸ャ劎鍘搁梺鍛婂姂閸斿孩鏅跺☉銏＄厽闁圭虎鍨版禍楣冩⒑鐠囨煡顎楃紒鐘茬Ч瀹曟洟宕￠悘缁樻そ婵℃悂鍩℃担绋挎闂備胶顭堥惉濂稿磻閻愮儤鍋傞柡鍥╁枔缁犻箖鏌涢埄鍐ｆ（妞ゅ繐鐗婇崐鍫曟煥濠靛棭妲归柣鎾寸☉闇夐柨婵嗩槹濞懷囨煃瑜滈崜姘辨崲閸愨晝�?
        var response = new LinklyCloudBackendHealthResponse(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            checks.All(check => check.IsReady),
            publicCallbackUri?.AbsoluteUri,
            checks);
        LogServiceJson(
            operation: "backend-health",
            phase: "response",
            direction: "response",
            environment: normalizedEnvironment,
            storeCode: normalizedStoreCode,
            deviceCode: normalizedDeviceCode,
            success: response.IsReady,
            reason: response.IsReady ? null : "failed-health-checks",
            request: null,
            response: new
            {
                response.IsReady,
                response.PublicNotificationBaseUrl,
                FailedChecks = response.Checks
                    .Where(check => !check.IsReady)
                    .Select(check => check.Code)
                    .ToArray()
            },
            details: new
            {
                Checks = response.Checks.Select(check => new
                {
                    check.Code,
                    check.IsReady
                }).ToArray()
            });
        return response;
    }

    public async Task<LinklyCloudBackendStatusTestResponse> RunStatusTestAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironment = NormalizeEnvironment(environment);
        var normalizedStoreCode = NormalizeRequired(storeCode, "storeCode");
        var normalizedDeviceCode = NormalizeRequired(deviceCode, "deviceCode");
        var token = await tokenProvider.GetTokenAsync(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            cancellationToken);
        var requestedAt = DateTimeOffset.UtcNow;
        var sessionId = Guid.NewGuid().ToString("D");
        var transportRequest = new LinklyCloudBackendTransportStatusRequest(
            normalizedEnvironment,
            token.RestBaseUrl,
            token.AccessToken,
            sessionId);

        var transportResponse = await SendWithRecoverableFailureAsync(
            () => transport.SendStatusAsync(transportRequest, cancellationToken));
        var status = ParseStatusTestResponse(
            transportResponse.Body,
            out var responseCode,
            out var responseText,
            out var responseTxnRef,
            out var responseDate,
            out var responseTime);
        var succeeded = transportResponse.StatusCode == HttpStatusCode.OK &&
            status &&
            string.Equals(responseCode, "00", StringComparison.OrdinalIgnoreCase);
        var message = BuildStatusTestMessage(transportResponse.StatusCode, succeeded, responseCode, responseText);
        var result = new LinklyCloudBackendStatusTestResponse(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            sessionId,
            requestedAt,
            (int)transportResponse.StatusCode,
            succeeded,
            responseCode,
            responseText,
            responseTxnRef,
            responseDate,
            responseTime,
            message);

        LogServiceJson(
            operation: "transaction-status-test",
            phase: "response",
            direction: "response",
            environment: normalizedEnvironment,
            storeCode: normalizedStoreCode,
            deviceCode: normalizedDeviceCode,
            success: succeeded,
            reason: succeeded ? null : "status-test-failed",
            request: null,
            response: new
            {
                result.TransactionReference,
                result.HttpStatus,
                result.ResponseCode,
                result.ResponseText,
                result.ResponseTxnRef,
                result.ResponseDate,
                result.ResponseTime
            },
            details: new
            {
                result.RequestedAt
            });
        return result;
    }

    public async Task<LinklyCloudBackendLogonTestResponse> RunLogonTestAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironment = NormalizeEnvironment(environment);
        var normalizedStoreCode = NormalizeRequired(storeCode, "storeCode");
        var normalizedDeviceCode = NormalizeRequired(deviceCode, "deviceCode");
        var token = await tokenProvider.GetTokenAsync(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            cancellationToken);
        var requestedAt = DateTimeOffset.UtcNow;
        var sessionId = Guid.NewGuid().ToString("D");
        var transportRequest = new LinklyCloudBackendTransportSessionRequest(
            normalizedEnvironment,
            token.RestBaseUrl,
            token.AccessToken,
            sessionId);

        var transportResponse = await SendWithRecoverableFailureAsync(
            () => transport.SendLogonAsync(transportRequest, cancellationToken));
        var logon = ParseLogonTestResponse(
            transportResponse.Body,
            out var responseCode,
            out var responseText,
            out var catid,
            out var caid,
            out var pinPadVersion);
        var succeeded = transportResponse.StatusCode == HttpStatusCode.OK &&
            logon &&
            string.Equals(responseCode, "00", StringComparison.OrdinalIgnoreCase);
        var message = BuildLogonTestMessage(transportResponse.StatusCode, succeeded, responseCode, responseText);
        var result = new LinklyCloudBackendLogonTestResponse(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            sessionId,
            requestedAt,
            (int)transportResponse.StatusCode,
            succeeded,
            responseCode,
            responseText,
            catid,
            caid,
            pinPadVersion,
            message);

        LogServiceJson(
            operation: "logon-test",
            phase: "response",
            direction: "response",
            environment: normalizedEnvironment,
            storeCode: normalizedStoreCode,
            deviceCode: normalizedDeviceCode,
            success: succeeded,
            reason: succeeded ? null : "logon-test-failed",
            request: null,
            response: new
            {
                result.TransactionReference,
                result.HttpStatus,
                result.ResponseCode,
                result.ResponseText,
                result.Catid,
                result.Caid,
                result.PinPadVersion
            },
            details: new
            {
                result.RequestedAt
            });
        return result;
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
        LogServiceJson(
            operation: "terminal-credential",
            phase: "request",
            direction: "request",
            environment: normalizedEnvironment,
            storeCode: normalizedStoreCode,
            deviceCode: normalizedDeviceCode,
            success: null,
            reason: null,
            request: new
            {
                environment = normalizedEnvironment,
                storeCode = normalizedStoreCode,
                deviceCode = normalizedDeviceCode,
                posId,
                secret = DescribeSecret(secret)
            },
            response: null,
            details: new
            {
                updatedBy = NormalizeOptional(updatedBy)
            });

        var credential = await terminalCredentialRepository.UpsertAsync(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            secret,
            posId,
            now,
            NormalizeOptional(updatedBy),
            cancellationToken);

        var response = new LinklyCloudBackendTerminalCredentialResponse(
            credential.Environment ?? normalizedEnvironment,
            credential.StoreCode ?? normalizedStoreCode,
            credential.DeviceCode ?? normalizedDeviceCode,
            !string.IsNullOrWhiteSpace(credential.Secret),
            credential.PosId ?? posId,
            new DateTimeOffset(DateTime.SpecifyKind(credential.UpdatedAt ?? now, DateTimeKind.Utc)));
        LogServiceJson(
            operation: "terminal-credential",
            phase: "succeeded",
            direction: "response",
            environment: response.Environment,
            storeCode: response.StoreCode,
            deviceCode: response.DeviceCode,
            success: true,
            reason: null,
            request: null,
            response: new
            {
                response.Environment,
                response.StoreCode,
                response.DeviceCode,
                response.HasSecret,
                response.PosId,
                response.UpdatedAt
            },
            details: null);
        return response;
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

            // 闂傚倷鑳堕…鍫㈡崲閹扮増鍋嬮柟鐗堟緲缁犳岸鏌曢崼婵囧仾闁惧繗顫夌€氭岸鏌熺紒妯洪唶婵°倐鍋撻棁澶愭煕韫囨稒锛熷ù婧垮灲閺岋綁鏁傜捄銊х厯闂佸搫琚崝宥囩箔閻斿摜绡€闁告洦鍙庡Σ鐑芥⒒娴ｅ憡鍟為柛鏃€娲滅划鏃堟倻閼恒儱鍓梺缁樻煥閸氬宕?Linkly闂傚倷鐒︾€笛呯矙閹烘梻鐭欓柡宥庡亐閸嬫挸顫濋妷銉т紙閻庤娲╃紞渚€銆侀弮鍫濆窛�?TxnRef 闂傚倷绀侀幗婊堝磿閹版澘鍨傛い鏍ㄧ矌閸楁岸鏌熺紒銏犳灍闁绘挶鍎查妵鍕籍閸屾艾浠橀梺鍝勬娴滎剛妲愰幒妤€绠涙い蹇撴閻℃绱撴担浠嬪摵缂佽鐗嗛悾宄邦潩椤戣姤鐎婚梺褰掑亰閸撴瑧鑺遍妷鈺傗拺闁告稑锕﹂幊鍛存煕韫囨棑鑰跨€规洘濞婂畷顐﹀Ψ閿曗偓閻濅即姊洪崫鍕閼裤倝鏌?
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
        // �?IP 濠电偞鍨堕幐鍓у垝椤栨埃鏀﹂柍褜鍓熼弻娑橆潩椤掑倐锝嗕繆椤愮姴鈧洟鍩㈤弮鍫濆嵆闁绘柨鎲＄€氭娊姊洪棃鈺呮婵犮垺锕㈠畷褰掓偩鐏炵浜?DNS闂備焦瀵х粙鎴︽嚐椤栨縿浜圭憸鏃堢嵁韫囨稒鏅滈柦妯侯槺閸旂敻鏌ｉ姀鈺佺仭闁烩剝娲熼敐鐐哄幢濞戞瑥鍓梺鍛婃处閸ㄨ鲸鎱ㄥ鍫熺厱闊浄绲芥晶鎵磽瀹ュ拋妯€鐎规洏鍎查幆鏃堟晲閸モ晪绱遍梻?
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

        // 闂佽娴烽幊鎾诲嫉椤掑嫬鍨傛慨妯挎硾鐟欙箓骞栨潏鍓хɑ闁?receipt/display 濠电偞鍨堕幐鍝ョ矓鐎垫瓕濮抽柤纰卞墯閸熸椽鏌涢埄鍐噭缁惧彞鍗抽弻锟犲醇濮橆兛澹曠紓鍌氬€风粈渚€鎮ч崨顓ф僵闁挎洖鍊搁崣濠囨煙閹冾暢婵炲鍨介弻锛勨偓锝庡亞閻滆崵绱掓潏銊х疄婵﹣绮欐俊鑸靛緞鐎ｎ亶娼梺璇叉捣閹虫捇宕姘肩劷闁汇垹鎲￠崵濠冦亜韫囨挸顏╃紓宥嗩殔閳藉骞橀姘缂傚倷鐒﹂〃鍡涘蓟瑜嶉～婵嬫晝閸屾艾寮烽梺鍦亾婢у酣宕?
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
            session.ClientAcknowledgedAt,
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

    private static void ApplySendKeyTransportResponse(
        LinklyCloudBackendSessionRecord session,
        LinklyCloudBackendTransportResponse response)
    {
        session.UpdatedAt = DateTimeOffset.UtcNow;
        session.LastHttpStatus = (int)response.StatusCode;
        session.RecoveryAction = null;
        Log(
            $"sendkey response applied sessionId={LogValue(session.SessionId)} " +
            $"http={(int)response.StatusCode}");
        if (response.StatusCode == HttpStatusCode.OK)
        {
            ApplyCompletedPayload(session, response.Body);
            return;
        }

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

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            // sendkey 400 闁告瑯浜欓崬顒傛偘閵婏附鎷辨繛鍡忓墲鐎垫粓鏌ㄩ娆惧殲婵懓鍊瑰Λ銈夊极閸剛骞㈠ù婧垮€栧Σ妤冪磼閹惧浜ù鐘茬С�?async notification / recovery 濞戞挸鎼崳顖炲Υ?
            session.Status = StatusPending;
            session.IsActive = true;
            return;
        }

        var code = (int)response.StatusCode;
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

    private void LogTransactionNotificationSnapshot(string sessionId, string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            var response = ReadResponse(root);
            var purchaseAnalysisData = ReadValue(response, "PurchaseAnalysisData");
            var refundReference = TryReadRefundReference(root, out var source);
            Log(
                $"transaction notification snapshot sessionId={LogValue(sessionId)} bytes={payloadJson.Length} " +
                $"rootKeys={DescribeKeys(root)} responseKeys={DescribeKeys(response)} " +
                $"responseCode={LogValue(ReadString(response, "ResponseCode"))} responseText={LogValue(ReadString(response, "ResponseText"))} " +
                $"padKind={DescribeKind(purchaseAnalysisData)} padKeys={DescribeKeys(purchaseAnalysisData)} " +
                $"rfnSource={LogValue(source)} rfn={LogValue(MaskReference(refundReference))}");
        }
        catch (JsonException ex)
        {
            Log($"transaction notification snapshot failed sessionId={LogValue(sessionId)} error={ex.GetType().Name}");
        }
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

        // display 閫氱煡浠ｈ〃褰撳墠缁堢鎻愮ず蹇収锛涚己澶卞瓧娈佃娓呯┖锛岄伩鍏嶆部鐢ㄤ笂涓€灞忔寜閿姸鎬併�?
        session.DisplayText = displayText;
        session.DisplayLines = displayLines is null ? null : string.Join(Environment.NewLine, displayLines);
        session.CancelKeyFlag = ReadBool(root, "CancelKeyFlag") ?? ReadBool(response, "CancelKeyFlag") ?? false;
        session.OKKeyFlag = ReadBool(root, "OKKeyFlag") ?? ReadBool(response, "OKKeyFlag") ?? false;
        session.AcceptYesKeyFlag = ReadBool(root, "AcceptYesKeyFlag") ?? ReadBool(response, "AcceptYesKeyFlag") ?? false;
        session.DeclineNoKeyFlag = ReadBool(root, "DeclineNoKeyFlag") ?? ReadBool(response, "DeclineNoKeyFlag") ?? false;
        session.AuthoriseKeyFlag = ReadBool(root, "AuthoriseKeyFlag") ?? ReadBool(response, "AuthoriseKeyFlag") ?? false;
        session.InputType = ReadString(root, "InputType") ?? ReadString(response, "InputType");
        session.GraphicCode = ReadString(root, "GraphicCode") ?? ReadString(response, "GraphicCode");
        Log(
            $"display notification applied sessionId={LogValue(session.SessionId)} " +
            $"display=\"{LogValue(TruncateForLog(displayText, 80))}\" " +
            $"cancel={session.CancelKeyFlag} ok={session.OKKeyFlag} " +
            $"yes={session.AcceptYesKeyFlag} no={session.DeclineNoKeyFlag} auth={session.AuthoriseKeyFlag}");

        // 鏄剧ず鍜屽皬绁ㄩ€氱煡鍙洿鏂拌緟鍔╁瓧娈碉紝涓嶆敼鍙樺凡瀹屾垚浜ゆ槗鐘舵€併�?
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
        // 202 闂備浇宕甸崑鐐电矙韫囨稑绀夐幖娣妼妗?Linkly 闂佽楠稿﹢閬嶁€﹂崼婵愬殨闁告挷鐒﹂弳婊堟煙缂併垹鏋涢�?recover 闂備浇宕垫慨鏉懨洪銏犵哗闂侇剙绉甸崕鎴澝归悡搴ｆ憼闁哄拋鍓熼幃姗€鎮欑捄杞版睏闂佽崵鍠愮换鍫ュ箖鐟欏嫭濯存繛鏉戭儏娴滈箖鏌ょ喊鍗炲闁崇粯娲樼换婵嬪閳ュ啿濮哥紓渚囧枛婢т粙骞夐幘顔芥櫇闁稿本姘ㄩ悾鎶芥⒑闂堟稓绠冲┑顖ｅ幖鍗卞┑鐘崇閸婂爼鐓崶銊︹拻缂佺姵宀搁弻锝夋晜鐠囪尙浠告�?
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

    private static IReadOnlyDictionary<string, string>? EnsurePurchaseAnalysisData(
        string txnType,
        long amount,
        string txnRef,
        IReadOnlyDictionary<string, string>? purchaseAnalysisData)
    {
        var fields = purchaseAnalysisData is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(purchaseAnalysisData, StringComparer.OrdinalIgnoreCase);

        if (string.Equals(txnType, "P", StringComparison.OrdinalIgnoreCase) &&
            !fields.ContainsKey("RFN"))
        {
            // 后端异步链路�?TxnRef �?API 生成；写入原交易 RFN，后续退卡才能引用同一个原�?RFN�?
            fields["RFN"] = txnRef;
        }

        if (fields.Count == 0)
        {
            return null;
        }

        fields.TryAdd("AMT", amount.ToString("D9", CultureInfo.InvariantCulture));
        fields.TryAdd("PCM", "0000");
        return fields;
    }

    private static JsonElement ReadResponse(JsonElement root)
    {
        return TryGetProperty(root, "Response", out var response) && response.ValueKind == JsonValueKind.Object
            ? response
            : root;
    }

    private static bool ParseStatusTestResponse(
        string? bodyJson,
        out string? responseCode,
        out string? responseText,
        out string? responseTxnRef,
        out string? responseDate,
        out string? responseTime)
    {
        responseCode = null;
        responseText = null;
        responseTxnRef = null;
        responseDate = null;
        responseTime = null;
        if (string.IsNullOrWhiteSpace(bodyJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(bodyJson);
            var response = ReadResponse(document.RootElement);
            responseCode = ReadString(response, "ResponseCode");
            responseText = ReadString(response, "ResponseText");
            responseTxnRef = ReadString(response, "TxnRef");
            responseDate = ReadString(response, "Date");
            responseTime = ReadString(response, "Time");
            return ReadBool(response, "Success") == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ParseLogonTestResponse(
        string? bodyJson,
        out string? responseCode,
        out string? responseText,
        out string? catid,
        out string? caid,
        out string? pinPadVersion)
    {
        responseCode = null;
        responseText = null;
        catid = null;
        caid = null;
        pinPadVersion = null;
        if (string.IsNullOrWhiteSpace(bodyJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(bodyJson);
            var response = ReadResponse(document.RootElement);
            responseCode = ReadString(response, "ResponseCode");
            responseText = ReadString(response, "ResponseText");
            catid = ReadString(response, "Catid");
            caid = ReadString(response, "Caid");
            pinPadVersion = ReadString(response, "PinPadVersion");
            return ReadBool(response, "Success") == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string BuildStatusTestMessage(
        HttpStatusCode httpStatus,
        bool succeeded,
        string? responseCode,
        string? responseText)
    {
        if (succeeded)
        {
            return "ANZ Linkly Cloud transaction status test succeeded.";
        }

        if (!string.IsNullOrWhiteSpace(responseText))
        {
            return responseText.Trim();
        }

        if (!string.IsNullOrWhiteSpace(responseCode))
        {
            return $"ANZ Linkly Cloud transaction status test failed with response code {responseCode.Trim()}.";
        }

        return $"ANZ Linkly Cloud transaction status test failed with HTTP {(int)httpStatus}.";
    }

    private static string BuildLogonTestMessage(
        HttpStatusCode httpStatus,
        bool succeeded,
        string? responseCode,
        string? responseText)
    {
        if (succeeded)
        {
            return "ANZ Linkly Cloud logon succeeded.";
        }

        if (!string.IsNullOrWhiteSpace(responseText))
        {
            return responseText.Trim();
        }

        if (!string.IsNullOrWhiteSpace(responseCode))
        {
            return $"ANZ Linkly Cloud logon failed with response code {responseCode.Trim()}.";
        }

        return $"ANZ Linkly Cloud logon failed with HTTP {(int)httpStatus}.";
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

    private static JsonElement ReadValue(JsonElement root, string propertyName)
    {
        return TryGetProperty(root, propertyName, out var value) ? value : default;
    }

    private static string? TryReadRefundReference(JsonElement root, out string? source)
    {
        var response = ReadResponse(root);
        var purchaseAnalysisData = ReadValue(response, "PurchaseAnalysisData");
        // 闂傚倷绀侀幖顐︽偋閸℃蛋鍥敊閹规劕小?Linkly payload �?FN/PAD 闂傚倸鍊风欢锟犲磻閳ь剟鏌涚€ｎ偅灏扮紒缁樼洴瀹曞崬螣閸濆嫬袘缂傚倷鐒︽晶搴ㄥ疾閻樿绠栧ù鐘差儏鎯熼梺鎸庢婵倕顭块幒妤佲�?PurchaseAnalysisData 闂傚倷绀侀幉锟犲礉閺囥垹绠犵€光偓閸曨偆鍔?
        var refundReference = TryReadRefundReferenceValue(purchaseAnalysisData, "Response.PurchaseAnalysisData", out source);
        if (!string.IsNullOrWhiteSpace(refundReference))
        {
            return refundReference;
        }

        refundReference = TryReadRefundReferenceValue(response, "Response", out source);
        if (!string.IsNullOrWhiteSpace(refundReference))
        {
            return refundReference;
        }

        refundReference = TryReadRefundReferenceValue(root, "Root", out source);
        if (!string.IsNullOrWhiteSpace(refundReference))
        {
            return refundReference;
        }

        source = null;
        return null;
    }

    private static string? TryReadRefundReferenceValue(JsonElement element, string path, out string? source)
    {
        source = null;
        return element.ValueKind switch
        {
            JsonValueKind.Object => TryReadRefundReferenceObject(element, path, out source),
            JsonValueKind.Array => TryReadRefundReferenceArray(element, path, out source),
            JsonValueKind.String => TryReadRefundReferenceFromText(element.GetString(), path, out source),
            _ => null
        };
    }

    private static string? TryReadRefundReferenceObject(JsonElement element, string path, out string? source)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, "RFN", StringComparison.OrdinalIgnoreCase))
            {
                var value = ReadScalar(property.Value);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    source = $"{path}.{property.Name}";
                    return value;
                }
            }
        }

        var key = ReadString(element, "Key") ??
            ReadString(element, "Name") ??
            ReadString(element, "Tag") ??
            ReadString(element, "Code");
        if (string.Equals(key, "RFN", StringComparison.OrdinalIgnoreCase))
        {
            var value = ReadString(element, "Value") ??
                ReadString(element, "Data") ??
                ReadString(element, "Text");
            if (!string.IsNullOrWhiteSpace(value))
            {
                source = $"{path}[{key}]";
                return value;
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            var value = TryReadRefundReferenceValue(property.Value, $"{path}.{property.Name}", out source);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        source = null;
        return null;
    }

    private static string? TryReadRefundReferenceArray(JsonElement element, string path, out string? source)
    {
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var value = TryReadRefundReferenceValue(item, $"{path}[{index}]", out source);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            index++;
        }

        source = null;
        return null;
    }

    private static string? TryReadRefundReferenceFromText(string? text, string path, out string? source)
    {
        var value = NormalizeOptional(text);
        if (value is null)
        {
            source = null;
            return null;
        }

        var marker = value.IndexOf("RFN", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            source = null;
            return null;
        }

        var start = marker + 3;
        while (start < value.Length && (char.IsWhiteSpace(value[start]) || value[start] is ':' or '=' or '-'))
        {
            start++;
        }

        var end = start;
        while (end < value.Length && !char.IsWhiteSpace(value[end]) && value[end] is not ',' and not ';' and not '|')
        {
            end++;
        }

        var refundReference = NormalizeOptional(value[start..end]);
        source = refundReference is null ? null : path;
        return refundReference;
    }

    private static string? ReadScalar(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => NormalizeOptional(value.GetString()),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static string DescribeKind(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Undefined ? "<missing>" : element.ValueKind.ToString();
    }

    private static string DescribeKeys(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return DescribeKind(element);
        }

        var keys = element.EnumerateObject()
            .Select(property => property.Name)
            .Take(12)
            .ToArray();
        return keys.Length == 0 ? "<empty>" : string.Join(",", keys);
    }

    private static string? MaskReference(string? value)
    {
        var normalized = NormalizeOptional(value);
        if (normalized is null || normalized.Length <= 8)
        {
            return normalized;
        }

        return $"{normalized[..4]}...{normalized[^4..]}";
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
        // Linkly sendkey 闂備礁鎲￠悷顖涚濠靛牊鏆滈柟缁㈠枛閻淇婇妶鍌氫壕闂佷紮绲介妶绋款嚕椤掑倹鍠嗛柛鏇ㄥ墯椤斿秹鏌ｆ惔銏⑩姇妞ゃ劌锕顐︻敇閵忕姷锛欑紒缁㈠弮椤ユ挾绮堟径鎰厸闁稿本纰嶉幖鎰版煟閳瑥娲﹂悡銉︺亜閺冨洤鍚归柣顓炶嫰闇夐柨婵嗘閻棝鎮归幇顔兼灈鐎殿噮鍠涢ˇ鍙夈亜閹惧磭绉烘鐐差儐椤︾増鎯旈鑽ょ＝闂備胶顭堢换鎺楀储瑜旈、娆撳箛閺夎法顦ч梺闈涱槶閸庤京绱為幘缁樼�?
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

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<none>" : value.Trim();
    }

    private static string? TruncateForLog(string? value, int maxLength)
    {
        var normalized = NormalizeOptional(value);
        return normalized is null || normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength];
    }

    private static void Log(string message)
    {
        Console.WriteLine($"[HBPOS][Api][LinklyCloudBackend] {DateTimeOffset.Now:O} {message}");
    }

    private void LogServiceJson(
        string operation,
        string phase,
        string direction,
        string environment,
        string storeCode,
        string deviceCode,
        bool? success,
        string? reason,
        object? request,
        object? response,
        object? details)
    {
        logger?.LogInformation(
            "{LinklyJson}",
            BuildServiceJsonLog(
                operation,
                phase,
                direction,
                environment,
                storeCode,
                deviceCode,
                success,
                reason,
                request,
                response,
                details));
    }

    private static string BuildServiceJsonLog(
        string operation,
        string phase,
        string direction,
        string environment,
        string storeCode,
        string deviceCode,
        bool? success,
        string? reason,
        object? request,
        object? response,
        object? details)
    {
        return JsonSerializer.Serialize(
            new
            {
                source = "api-backend-service",
                operation,
                phase,
                direction,
                environment,
                sessionId = (string?)null,
                httpStatus = (int?)null,
                success,
                reason,
                elapsedMs = (long?)null,
                request,
                response,
                details = new
                {
                    timestamp = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                    storeCode,
                    deviceCode,
                    data = details
                }
            },
            ServiceJsonOptions);
    }

    private static object DescribeSecret(string secret)
    {
        var trimmed = secret.Trim();
        return new
        {
            hasSecret = trimmed.Length > 0,
            secretLength = trimmed.Length,
            secretPreview = trimmed.Length <= 8 ? "***" : $"{trimmed[..4]}...{trimmed[^4..]}"
        };
    }

    private static string GetComponentVersion()
    {
        var assembly = typeof(LinklyCloudBackendAsyncService).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
            assembly.GetName().Version?.ToString() ??
            "unknown";
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
    IOptions<LinklyCloudBackendAsyncOptions> options,
    ILogger<HttpLinklyCloudBackendTokenProvider>? logger = null) : ILinklyCloudBackendTokenProvider
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
        // 闂備礁鎲￠懝鎯归悜绛嬫�?token 闂佽崵濮村ú顓㈠绩闁秵鍎戦柣妤€鐗婃刊鎾偣閹帒濡介柡鍡楃箳缁辨挻鎷呴崫銉モ叡�?secret闂備線娼уΛ鏃傛導婵犵爤 identity 闂備礁鎲＄划宀勬儔婵傚摜宓侀柛鈩兩戝▍鐘绘煕閹扮數鍘涢柛銈囧█楠?endpoint�?
        var requestUri = new Uri(GetBaseUri(authBaseUrl), "tokens/cloudpos");
        var tokenRequest = new LinklyCloudBackendTokenRequest(
            terminalCredential.Secret.Trim(),
            NormalizeRequired(options.Value.PosName, "posName"),
            NormalizeRequired(options.Value.PosVersion, "posVersion"),
            terminalCredential.PosId.Trim(),
            posVendorId);
        LogTokenHttpRequest(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            requestUri,
            SerializeDebugJson(tokenRequest));

        var stopwatch = Stopwatch.StartNew();
        using var response = await httpClient.PostAsJsonAsync(
            requestUri,
            tokenRequest,
            JsonOptions,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        stopwatch.Stop();
        LogTokenHttpResponse(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            requestUri,
            response.StatusCode,
            stopwatch.ElapsedMilliseconds,
            body);
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

    private static string SerializeDebugJson<T>(T value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private void LogTokenHttpRequest(
        string environment,
        string storeCode,
        string deviceCode,
        Uri url,
        string bodyJson)
    {
        logger?.LogInformation(
            "{LinklyJson}",
            BuildTokenJsonLog(
                "token",
                "request",
                "request",
                environment,
                storeCode,
                deviceCode,
                url,
                httpStatus: null,
                elapsedMs: null,
                request: RawJsonBody(bodyJson),
                response: null));
    }

    private void LogTokenHttpResponse(
        string environment,
        string storeCode,
        string deviceCode,
        Uri url,
        HttpStatusCode statusCode,
        long elapsedMs,
        string? bodyJson)
    {
        logger?.LogInformation(
            "{LinklyJson}",
            BuildTokenJsonLog(
                "token",
                "response",
                "response",
                environment,
                storeCode,
                deviceCode,
                url,
                statusCode,
                elapsedMs,
                request: null,
                response: RawJsonBody(bodyJson)));
    }

    private static string LogJsonBody(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<none>" : value.Trim();
    }

    private static string BuildTokenJsonLog(
        string operation,
        string phase,
        string direction,
        string environment,
        string storeCode,
        string deviceCode,
        Uri url,
        HttpStatusCode? httpStatus,
        long? elapsedMs,
        object? request,
        object? response)
    {
        return JsonSerializer.Serialize(
            new
            {
                source = "api-backend-token-provider",
                operation,
                phase,
                direction,
                environment,
                sessionId = (string?)null,
                httpStatus = httpStatus.HasValue ? (int)httpStatus.Value : (int?)null,
                success = httpStatus.HasValue ? ((int)httpStatus.Value is >= 200 and < 300) : (bool?)null,
                reason = (string?)null,
                elapsedMs,
                request,
                response,
                details = new
                {
                    timestamp = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                    method = "POST",
                    url = url.ToString(),
                    storeCode,
                    deviceCode
                }
            },
            JsonOptions);
    }

    private static object? RawJsonBody(string? bodyJson)
    {
        if (string.IsNullOrWhiteSpace(bodyJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(bodyJson);
            return JsonSerializer.Deserialize<object>(document.RootElement.GetRawText(), JsonOptions);
        }
        catch (JsonException)
        {
            return bodyJson;
        }
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
        // secret 闂傚倸鍊风粈渚€骞夐敓鐘冲仭妞ゆ牗绋撻々鍙夌節婵犲倻澧曢柣銈庡櫍閺岀喖骞戦幇闈涙缂備讲鍋撻柛宀€鍋為悡蹇擃熆閼哥數娲存俊缁㈠枟缁绘盯宕煎┑鍫濈厽闂佸搫鐬奸崰鎾诲箯閻樿鐏抽柧蹇ｅ亞娴滎亞绱撻崒娆撴闁告梹鍨剁粋宥囨崉閾忚娈鹃梺鎸庣箓椤︻垳澹曢崗鑲╃闁瑰鍋熼幊鍕亜閺傝法效婵﹥妞介獮鎰償閿濆倹顫嶉梻浣稿悑濡炲潡宕归崼鏇犲祦闁哄稁鍘介悡銉╂倵閿濆骸浜滈柛鏂挎嚇濮婃椽宕崟顐熷亾閹间焦鍊舵繝闈涙濡插牏鎲搁弮鍫濊摕闁跨喓濮撮獮銏′繆椤栨繍鍤欓柛鐐差槸閳规垿鎮╅顫?HasSecret闂傚倸鍊烽悞锔锯偓绗涘懐鐭欓柟瀵稿仧闂勫嫰鏌￠崘銊モ偓鑽ょ不閺傛鐔嗛柤鎼佹涧婵洭鏌ｉ幇顒婅含闁哄矉缍侀獮鍥敇閻戝棙顥嬮梻浣虹帛閸旀洟鏁冮鍫濊摕闁跨喓濮寸粈瀣亜閹扳晛鐒洪柛鏂款樀濮婃椽宕崟闈涘壈闂佸摜鍠庨悺銊︾┍?
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

    Task<LinklyCloudBackendTransportResponse> SendLogonAsync(
        LinklyCloudBackendTransportSessionRequest request,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendTransportResponse> SendStatusAsync(
        LinklyCloudBackendTransportStatusRequest request,
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

public sealed record LinklyCloudBackendTransportStatusRequest(
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

public sealed class HttpLinklyCloudBackendAsyncTransport(
    HttpClient httpClient,
    ILogger<HttpLinklyCloudBackendAsyncTransport>? logger = null) : ILinklyCloudBackendAsyncTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly JsonSerializerOptions LogJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
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
            request.Environment,
            request.RestBaseUrl,
            request.AccessToken,
            request.SessionId,
            request.TxnType,
            request.TxnRef,
            operation: "transaction",
            endpointSegment: "transaction",
            asyncMode: true,
            method: HttpMethod.Post,
            body: new LinklyCloudBackendApiRequest(
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
            request.Environment,
            request.RestBaseUrl,
            request.AccessToken,
            request.SessionId,
            txnType: null,
            txnRef: null,
            operation: "transaction",
            endpointSegment: "transaction",
            asyncMode: true,
            method: HttpMethod.Get,
            body: null,
            cancellationToken);
    }

    public Task<LinklyCloudBackendTransportResponse> SendLogonAsync(
        LinklyCloudBackendTransportSessionRequest request,
        CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, object?>
        {
            ["Merchant"] = "00",
            ["LogonType"] = " ",
            ["Application"] = "00",
            ["ReceiptAutoPrint"] = "0",
            ["CutReceipt"] = "0"
        };

        return SendAsync(
            request.Environment,
            request.RestBaseUrl,
            request.AccessToken,
            request.SessionId,
            txnType: null,
            txnRef: null,
            operation: "logon-test",
            endpointSegment: "logon",
            asyncMode: false,
            method: HttpMethod.Post,
            body: new LinklyCloudBackendApiRequest(fields, Notification: null),
            cancellationToken);
    }

    public Task<LinklyCloudBackendTransportResponse> SendStatusAsync(
        LinklyCloudBackendTransportStatusRequest request,
        CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, object?>
        {
            ["Merchant"] = "00",
            ["Application"] = "00",
            ["StatusType"] = "0"
        };

        return SendAsync(
            request.Environment,
            request.RestBaseUrl,
            request.AccessToken,
            request.SessionId,
            txnType: null,
            txnRef: null,
            operation: "transaction-status-test",
            endpointSegment: "status",
            asyncMode: false,
            method: HttpMethod.Post,
            body: new LinklyCloudBackendApiRequest(fields, Notification: null),
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
            request.Environment,
            request.RestBaseUrl,
            request.AccessToken,
            request.SessionId,
            txnType: null,
            txnRef: null,
            operation: "sendkey",
            endpointSegment: "sendkey",
            asyncMode: true,
            method: HttpMethod.Post,
            body: new LinklyCloudBackendApiRequest(fields, Notification: null),
            cancellationToken);
    }

    private async Task<LinklyCloudBackendTransportResponse> SendAsync(
        string environment,
        string restBaseUrl,
        string accessToken,
        string sessionId,
        string? txnType,
        string? txnRef,
        string operation,
        string endpointSegment,
        bool asyncMode,
        HttpMethod method,
        LinklyCloudBackendApiRequest? body,
        CancellationToken cancellationToken)
    {
        var requestBodyJson = body is null ? null : SerializeDebugJson(body);
        using var request = new HttpRequestMessage(
            method,
            new Uri(GetBaseUri(restBaseUrl), $"sessions/{Uri.EscapeDataString(sessionId)}/{endpointSegment}?async={asyncMode.ToString().ToLowerInvariant()}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        LogLinklyHttpRequest(
            environment,
            sessionId,
            operation,
            method,
            request.RequestUri!,
            txnType,
            txnRef,
            requestBodyJson);
        var stopwatch = Stopwatch.StartNew();
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        stopwatch.Stop();
        LogLinklyHttpResponse(
            environment,
            sessionId,
            operation,
            method,
            request.RequestUri!,
            response.StatusCode,
            stopwatch.ElapsedMilliseconds,
            txnType,
            txnRef,
            responseBody);
        return new LinklyCloudBackendTransportResponse(response.StatusCode, responseBody);
    }

    private static Uri GetBaseUri(string baseUrl)
    {
        var trimmed = baseUrl.Trim();
        return new Uri(trimmed.EndsWith("/", StringComparison.Ordinal) ? trimmed : trimmed + "/", UriKind.Absolute);
    }

    private static string SerializeDebugJson<T>(T value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private void LogLinklyHttpRequest(
        string environment,
        string sessionId,
        string operation,
        HttpMethod method,
        Uri url,
        string? txnType,
        string? txnRef,
        string? bodyJson)
    {
        logger?.LogInformation(
            "{LinklyJson}",
            BuildTransportJsonLog(
                operation,
                "request",
                "request",
                environment,
                sessionId,
                method,
                url,
                httpStatus: null,
                elapsedMs: null,
                txnType,
                txnRef,
                transactionReference: sessionId,
                txnRefSpecified: !string.IsNullOrWhiteSpace(txnRef),
                requestJson: bodyJson,
                responseJson: null,
                request: RawJsonBody(bodyJson),
                response: null));
    }

    private void LogLinklyHttpResponse(
        string environment,
        string sessionId,
        string operation,
        HttpMethod method,
        Uri url,
        HttpStatusCode statusCode,
        long elapsedMs,
        string? txnType,
        string? txnRef,
        string? bodyJson)
    {
        logger?.LogInformation(
            "{LinklyJson}",
            BuildTransportJsonLog(
                operation,
                "response",
                "response",
                environment,
                sessionId,
                method,
                url,
                statusCode,
                elapsedMs,
                txnType,
                txnRef,
                transactionReference: sessionId,
                txnRefSpecified: !string.IsNullOrWhiteSpace(txnRef),
                requestJson: null,
                responseJson: bodyJson,
                request: null,
                response: RawJsonBody(bodyJson)));
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value.Trim();
    }

    private static string LogJsonBody(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<none>" : value.Trim();
    }

    private static string BuildTransportJsonLog(
        string operation,
        string phase,
        string direction,
        string environment,
        string sessionId,
        HttpMethod method,
        Uri url,
        HttpStatusCode? httpStatus,
        long? elapsedMs,
        string? txnType,
        string? txnRef,
        string transactionReference,
        bool txnRefSpecified,
        string? requestJson,
        string? responseJson,
        object? request,
        object? response)
    {
        var responseDetails = ReadTransportResponseDetails(responseJson);
        return JsonSerializer.Serialize(
            new
            {
                source = "api-backend-transport",
                operation,
                phase,
                direction,
                environment,
                sessionId,
                httpStatus = httpStatus.HasValue ? (int)httpStatus.Value : (int?)null,
                success = httpStatus.HasValue ? ((int)httpStatus.Value is >= 200 and < 300) : (bool?)null,
                reason = (string?)null,
                elapsedMs,
                request,
                response,
                details = new
                {
                    timestamp = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                    method = method.Method,
                    url = url.ToString(),
                    transactionReference,
                    txnType,
                    txnRef,
                    txnRefSpecified,
                    requestJson,
                    responseJson,
                    responseTxnRef = responseDetails.TxnRef,
                    responseDate = responseDetails.Date,
                    responseTime = responseDetails.Time,
                    responseCode = responseDetails.ResponseCode,
                    responseText = responseDetails.ResponseText
                }
            },
            LogJsonOptions);
    }

    private static LinklyTransportResponseDetails ReadTransportResponseDetails(string? responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return LinklyTransportResponseDetails.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(responseJson);
            var response = TryGetProperty(document.RootElement, "Response", out var nested) &&
                nested.ValueKind == JsonValueKind.Object
                ? nested
                : document.RootElement;
            return new LinklyTransportResponseDetails(
                ReadString(response, "TxnRef"),
                ReadString(response, "Date"),
                ReadString(response, "Time"),
                ReadString(response, "ResponseCode"),
                ReadString(response, "ResponseText"));
        }
        catch (JsonException)
        {
            return LinklyTransportResponseDetails.Empty;
        }
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }

    private static object? RawJsonBody(string? bodyJson)
    {
        if (string.IsNullOrWhiteSpace(bodyJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(bodyJson);
            return JsonSerializer.Deserialize<object>(document.RootElement.GetRawText(), JsonOptions);
        }
        catch (JsonException)
        {
            return bodyJson;
        }
    }

    private sealed record LinklyCloudBackendApiRequest(
        [property: JsonPropertyName("Request")] IReadOnlyDictionary<string, object?> Request,
        [property: JsonPropertyName("Notification")] LinklyCloudBackendApiNotification? Notification);

    private sealed record LinklyCloudBackendApiNotification(
        [property: JsonPropertyName("Uri")] string Uri,
        [property: JsonPropertyName("AuthorizationHeader")] string AuthorizationHeader);

    private sealed record LinklyTransportResponseDetails(
        string? TxnRef,
        string? Date,
        string? Time,
        string? ResponseCode,
        string? ResponseText)
    {
        public static LinklyTransportResponseDetails Empty { get; } = new(null, null, null, null, null);
    }
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

    Task<LinklyCloudBackendSessionRecord?> GetResumableSessionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendSessionRecord?> AcknowledgeSessionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        string sessionId,
        DateTimeOffset acknowledgedAt,
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

            var next = Clone(session);
            if (_sessions.TryGetValue(key, out existing) &&
                existing.ClientAcknowledgedAt is not null &&
                next.ClientAcknowledgedAt is null)
            {
                next.ClientAcknowledgedAt = existing.ClientAcknowledgedAt;
            }

            _sessions[key] = next;
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

    public Task<LinklyCloudBackendSessionRecord?> GetResumableSessionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var session = _sessions.Values
                .Where(existing =>
                    SameTerminal(existing, environment, storeCode, deviceCode) &&
                    (existing.IsActive ||
                        IsFinalForClientRecovery(existing) && existing.ClientAcknowledgedAt is null))
                .OrderBy(existing => existing.IsActive ? 0 : 1)
                .ThenByDescending(existing => existing.UpdatedAt)
                .ThenByDescending(existing => existing.Id)
                .FirstOrDefault();
            return Task.FromResult(session is null ? null : Clone(session));
        }
    }

    public Task<LinklyCloudBackendSessionRecord?> AcknowledgeSessionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        string sessionId,
        DateTimeOffset acknowledgedAt,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var key = SessionKey(environment, storeCode, deviceCode, sessionId);
            if (!_sessions.TryGetValue(key, out var session))
            {
                return Task.FromResult<LinklyCloudBackendSessionRecord?>(null);
            }

            var next = Clone(session);
            // 客户端恢复完成后写入确认时间，后�?resumable 查询不再返回这笔已完成会话�?            next.ClientAcknowledgedAt = acknowledgedAt;
            next.UpdatedAt = acknowledgedAt;
            _sessions[key] = Clone(next);
            return Task.FromResult<LinklyCloudBackendSessionRecord?>(next);
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

    private static bool IsFinalForClientRecovery(LinklyCloudBackendSessionRecord session)
    {
        return string.Equals(session.Status, "Completed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(session.Status, "Failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(session.Status, "NotSubmitted", StringComparison.OrdinalIgnoreCase);
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
            ClientAcknowledgedAt = session.ClientAcknowledgedAt,
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
                [RecoveryCount], [ReceiptPrintedAt], [ClientAcknowledgedAt], [LastHttpStatus], [IsActive], [UpdatedAt])
            VALUES (
                @Environment, @StoreCode, @DeviceCode, @SessionId, @Status, @TxnRef,
                @ResponseCode, @ResponseText, @RecoveryAction, @DisplayText, @DisplayLines,
                @CancelKeyFlag, @OKKeyFlag, @AcceptYesKeyFlag, @DeclineNoKeyFlag, @AuthoriseKeyFlag,
                @InputType, @GraphicCode, @ReceiptText,
                @RecoveryCount, @ReceiptPrintedAt, @ClientAcknowledgedAt, @LastHttpStatus, @IsActive, @UpdatedAt);
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
                [ClientAcknowledgedAt] = COALESCE(@ClientAcknowledgedAt, target.[ClientAcknowledgedAt]),
                [LastHttpStatus] = @LastHttpStatus,
                [IsActive] = @IsActive,
                [UpdatedAt] = @UpdatedAt
        WHEN NOT MATCHED THEN
            INSERT (
                [Environment], [StoreCode], [DeviceCode], [SessionId], [Status], [TxnRef],
                [ResponseCode], [ResponseText], [RecoveryAction], [DisplayText], [DisplayLines],
                [CancelKeyFlag], [OKKeyFlag], [AcceptYesKeyFlag], [DeclineNoKeyFlag], [AuthoriseKeyFlag],
                [InputType], [GraphicCode], [ReceiptText],
                [RecoveryCount], [ReceiptPrintedAt], [ClientAcknowledgedAt], [LastHttpStatus], [IsActive], [UpdatedAt])
            VALUES (
                @Environment, @StoreCode, @DeviceCode, @SessionId, @Status, @TxnRef,
                @ResponseCode, @ResponseText, @RecoveryAction, @DisplayText, @DisplayLines,
                @CancelKeyFlag, @OKKeyFlag, @AcceptYesKeyFlag, @DeclineNoKeyFlag, @AuthoriseKeyFlag,
                @InputType, @GraphicCode, @ReceiptText,
                @RecoveryCount, @ReceiptPrintedAt, @ClientAcknowledgedAt, @LastHttpStatus, @IsActive, @UpdatedAt);
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
                [RecoveryCount], [ReceiptPrintedAt], [ClientAcknowledgedAt], [LastHttpStatus], [IsActive], [UpdatedAt]
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
                [RecoveryCount], [ReceiptPrintedAt], [ClientAcknowledgedAt], [LastHttpStatus], [IsActive], [UpdatedAt]
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
                [RecoveryCount], [ReceiptPrintedAt], [ClientAcknowledgedAt], [LastHttpStatus], [IsActive], [UpdatedAt]
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

    public async Task<LinklyCloudBackendSessionRecord?> GetResumableSessionAsync(
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
                [RecoveryCount], [ReceiptPrintedAt], [ClientAcknowledgedAt], [LastHttpStatus], [IsActive], [UpdatedAt]
            FROM [dbo].[POSM_LinklyCloudBackendSession]
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [DeviceCode] = @DeviceCode
              AND (
                    [IsActive] = 1
                    OR ([Status] IN (N'Completed', N'Failed', N'NotSubmitted') AND [ClientAcknowledgedAt] IS NULL)
                  )
            ORDER BY
                CASE WHEN [IsActive] = 1 THEN 0 ELSE 1 END,
                [UpdatedAt] DESC,
                [Id] DESC;
            """;

        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<LinklyCloudBackendSessionRecord>(
            sql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@DeviceCode", deviceCode));
    }

    public async Task<LinklyCloudBackendSessionRecord?> AcknowledgeSessionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        string sessionId,
        DateTimeOffset acknowledgedAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [dbo].[POSM_LinklyCloudBackendSession]
            SET [ClientAcknowledgedAt] = @ClientAcknowledgedAt,
                [UpdatedAt] = @ClientAcknowledgedAt
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [DeviceCode] = @DeviceCode
              AND [SessionId] = @SessionId;
            """;

        var acknowledgedAtUtc = acknowledgedAt.UtcDateTime;
        var affected = await dbContext.PosmDb.Ado.ExecuteCommandAsync(
            sql,
            new SugarParameter("@ClientAcknowledgedAt", acknowledgedAtUtc),
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@DeviceCode", deviceCode),
            new SugarParameter("@SessionId", sessionId));
        return affected <= 0
            ? null
            : await GetSessionAsync(environment, storeCode, deviceCode, sessionId, cancellationToken);
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
            new SugarParameter("@ClientAcknowledgedAt", session.ClientAcknowledgedAt?.UtcDateTime),
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

    public DateTimeOffset? ClientAcknowledgedAt { get; set; }

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
