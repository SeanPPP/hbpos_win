using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public interface ILinklyBackendTerminalClient
{
    Task<LinklyConnectionTestResult> TestConnectionAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);

    Task<PaymentAuthorizationResult> PurchaseAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default);

    Task<PaymentAuthorizationResult> RefundAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        CancellationToken cancellationToken = default);

    Task<LinklyCloudBackendSessionResponse?> GetResumableSessionAsync(
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default);

    Task<LinklyCloudBackendSessionResponse> RecoverSessionAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<LinklyCloudBackendSessionResponse> GetSessionStatusAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken = default);

    Task AcknowledgeSessionAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken = default);
}

public sealed class LinklyBackendTerminalClient(
    HttpClient httpClient,
    ILinklyTerminalDialogService dialogService,
    TimeSpan? pollInterval = null,
    Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
    ILocalizationService? localization = null) : ILinklyBackendTerminalClient
{
    private const string ProcessorName = "ANZ";
    private const string StatusCompleted = "Completed";
    private const string StatusFailed = "Failed";
    private const string StatusNotSubmitted = "NotSubmitted";
    private const string StatusTokenRefreshRequired = "TokenRefreshRequired";
    private const string RecoveryRetry = "Retry";
    private const string RecoveryRefreshToken = "RefreshToken";
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly TimeSpan _pollInterval = pollInterval.GetValueOrDefault(DefaultPollInterval);
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync = delayAsync ?? Task.Delay;

    public async Task<LinklyConnectionTestResult> TestConnectionAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Log($"health request start environment={environment} componentVersion={GetComponentVersion()}");
            using var response = await httpClient.GetAsync(
                $"api/v1/linkly/cloud-backend/health?environment={Uri.EscapeDataString(environment.ToString())}",
                cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var health = ReadHealthResult(content);
                var failedCodes = GetFailedHealthChecks(health)
                    .Select(check => check.Code)
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .ToArray();
                Log(
                    $"health request completed environment={environment} ready={health.IsReady} failedChecks={(failedCodes.Length == 0 ? "<none>" : string.Join(",", failedCodes))}");
                return health.IsReady
                    ? new LinklyConnectionTestResult(true, T("linkly.backend.configValid", "ANZ Linkly Cloud backend configuration is valid."))
                    : new LinklyConnectionTestResult(false, FormatHealthFailure(health));
            }

            var message = TryReadApiMessage(content) ??
                string.Format(
                    CultureInfo.InvariantCulture,
                    T("linkly.backend.configTestHttpFailed", "ANZ Linkly Cloud backend configuration test failed with HTTP {0}."),
                    (int)response.StatusCode);
            Log($"health request failed environment={environment} http={(int)response.StatusCode}");
            return new LinklyConnectionTestResult(false, message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log($"health request failed environment={environment} error={ex.GetType().Name}");
            return new LinklyConnectionTestResult(false, T("linkly.backend.communicationFailed", "ANZ Linkly Cloud backend communication failed."));
        }
    }

    public Task<PaymentAuthorizationResult> PurchaseAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default)
    {
        return RunAsync("P", amount, session, settings, refundReference: null, cancellationToken);
    }

    public async Task<PaymentAuthorizationResult> RefundAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        CancellationToken cancellationToken = default)
    {
        var refundReference = TryParseRefundReference(originalReference);
        Log(
            $"refund reference resolved originalReference={LogValue(originalReference)} refundReference={LogValue(refundReference)} " +
            $"hasRefundReference={!string.IsNullOrWhiteSpace(refundReference)}");
        if (string.IsNullOrWhiteSpace(refundReference))
        {
            refundReference = await TryResolveOriginalBackendRefundReferenceAsync(settings, originalReference, cancellationToken);
        }

        return string.IsNullOrWhiteSpace(refundReference)
            ? new PaymentAuthorizationResult(false, null, T("linkly.backend.refundMissingReference", "Linkly Cloud refund requires an original RFN reference."))
            : await RunAsync("R", amount, session, settings, refundReference, cancellationToken);
    }

    public Task<LinklyCloudBackendSessionResponse?> GetResumableSessionAsync(
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default)
    {
        return GetResumableSessionCoreAsync(settings, cancellationToken);
    }

    public Task<LinklyCloudBackendSessionResponse> RecoverSessionAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        return RecoverAsync(settings, sessionId, cancellationToken);
    }

    public Task<LinklyCloudBackendSessionResponse> GetSessionStatusAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        return GetStatusAsync(settings, sessionId, cancellationToken);
    }

    public async Task AcknowledgeSessionAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var relativeUrl = $"api/v1/linkly/cloud-backend/transactions/{Uri.EscapeDataString(sessionId)}/acknowledge";
        var request = new LinklyCloudBackendAcknowledgeRequest(settings.Environment.ToString());
        LogHttpRequest(
            "acknowledge",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            txnRef: null,
            bodyJson: SerializeDebugJson(request));
        using var response = await httpClient.PostAsJsonAsync(
            relativeUrl,
            request,
            JsonOptions,
            cancellationToken);
        _ = await ReadApiResultAsync(
            response,
            "acknowledge",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            stopwatch,
            cancellationToken);
    }

    private async Task<PaymentAuthorizationResult> RunAsync(
        string txnType,
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? refundReference,
        CancellationToken cancellationToken)
    {
        if (amount <= 0m)
        {
            return new PaymentAuthorizationResult(false, null, T("linkly.backend.amountMustBePositive", "Card amount must be greater than zero."));
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(settings.TerminalTimeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(180) : settings.TerminalTimeout);
        var keepDialogOpen = false;
        Log($"transaction request start txnType={txnType} environment={settings.Environment} componentVersion={GetComponentVersion()}");

        try
        {
            var fallbackTxnRef = BuildTxnRef(session);
            var activeStatus = await GetActiveSessionAsync(settings, timeoutCts.Token);
            if (activeStatus is not null)
            {
                var recoveredStatus = await ResumeActiveSessionAsync(settings, activeStatus, timeoutCts.Token);
                var recoveredResult = ToAuthorizationResult(recoveredStatus, amount, fallbackTxnRef, suppressPrintedReceipt: true);
                keepDialogOpen = !recoveredResult.Approved;
                return recoveredResult;
            }

            var request = new LinklyCloudBackendTransactionRequest(
                settings.Environment.ToString(),
                txnType,
                ToMinorUnits(amount),
                BuildPurchaseAnalysisData(amount, session, refundReference));

            LinklyCloudBackendSessionResponse status;
            try
            {
                status = await StartTransactionAsync(request, timeoutCts.Token);
            }
            catch (LinklyBackendHttpException ex) when (ex.HttpStatus == HttpStatusCode.Conflict)
            {
                // 409 代表终端已有 active session，不能把它折叠成普通通讯失败。
                activeStatus = await GetActiveSessionAsync(settings, timeoutCts.Token);
                if (activeStatus is null)
                {
                    var message = T("linkly.backend.activeSessionUnavailable", "Current terminal has an unfinished card transaction, but no recoverable active session was returned. Try again later.");
                    await PresentFinalFailureAsync("backend-active-unavailable", message, CancellationToken.None);
                    keepDialogOpen = true;
                    return new PaymentAuthorizationResult(false, null, message);
                }

                var recoveredStatus = await ResumeActiveSessionAsync(settings, activeStatus, timeoutCts.Token);
                var recoveredResult = ToAuthorizationResult(recoveredStatus, amount, fallbackTxnRef, suppressPrintedReceipt: true);
                keepDialogOpen = !recoveredResult.Approved;
                return recoveredResult;
            }

            status = await PollUntilFinalAsync(settings, status, timeoutCts.Token);
            var result = ToAuthorizationResult(status, amount, fallbackTxnRef, suppressPrintedReceipt: false);
            keepDialogOpen = !result.Approved;
            return result;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var message = T("linkly.backend.timeout", "ANZ Linkly Cloud transaction timed out.");
            await PresentFinalFailureAsync("backend-timeout", message, CancellationToken.None);
            keepDialogOpen = true;
            return new PaymentAuthorizationResult(false, null, message);
        }
        catch (HttpRequestException)
        {
            var message = T("linkly.backend.communicationFailed", "ANZ Linkly Cloud backend communication failed.");
            await PresentFinalFailureAsync("backend-http-error", message, CancellationToken.None);
            keepDialogOpen = true;
            return new PaymentAuthorizationResult(false, null, message);
        }
        catch (JsonException)
        {
            var message = T("linkly.backend.invalidResponse", "ANZ Linkly Cloud backend returned an invalid response.");
            await PresentFinalFailureAsync("backend-json-error", message, CancellationToken.None);
            keepDialogOpen = true;
            return new PaymentAuthorizationResult(false, null, message);
        }
        finally
        {
            // 成功交易自动收起页面弹窗；失败最终状态保留给收银员确认。
            if (!keepDialogOpen)
            {
                await dialogService.CloseAsync(CancellationToken.None);
            }
        }
    }

    private async Task<LinklyCloudBackendSessionResponse> ResumeActiveSessionAsync(
        CardTerminalSettings settings,
        LinklyCloudBackendSessionResponse activeStatus,
        CancellationToken cancellationToken)
    {
        var status = await PresentStatusAsync(
            settings,
            activeStatus,
            T("linkly.backend.activeSessionResume", "Current terminal has an unfinished card transaction. Continuing to poll/recover that session."),
            cancellationToken);
        if (!IsFinal(status))
        {
            status = await RecoverAsync(settings, status.SessionId, cancellationToken);
        }

        return await PollUntilFinalAsync(settings, status, cancellationToken);
    }

    private async Task<LinklyCloudBackendSessionResponse> PollUntilFinalAsync(
        CardTerminalSettings settings,
        LinklyCloudBackendSessionResponse status,
        CancellationToken cancellationToken)
    {
        status = await PresentStatusAsync(settings, status, message: null, cancellationToken);
        var shouldRefreshImmediately = !IsFinal(status) && !RequiresRecovery(status);
        while (!IsFinal(status))
        {
            if (shouldRefreshImmediately)
            {
                LogStatusSnapshot("poll loop immediate refresh", status);
                shouldRefreshImmediately = false;
            }
            else
            {
                LogStatusSnapshot("poll loop before delay", status);
                await DelayBeforeNextPollAsync(status, cancellationToken);
            }

            status = RequiresRecovery(status)
                ? await RecoverAsync(settings, status.SessionId, cancellationToken)
                : await GetStatusAsync(settings, status.SessionId, cancellationToken);
            status = await PresentStatusAsync(settings, status, message: null, cancellationToken);
        }

        if (string.Equals(status.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase) &&
            !HasReceipt(status))
        {
            for (var attempt = 0; attempt < 3 && !HasReceipt(status); attempt++)
            {
                await DelayAsync(_pollInterval, cancellationToken);

                status = await GetStatusAsync(settings, status.SessionId, cancellationToken);
                status = await PresentStatusAsync(settings, status, message: null, cancellationToken);
            }
        }

        return status;
    }

    private async Task<LinklyCloudBackendSessionResponse> PresentStatusAsync(
        CardTerminalSettings settings,
        LinklyCloudBackendSessionResponse status,
        string? message,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var dialogState = ToDialogState(status, message);
            var updateStopwatch = Stopwatch.StartNew();
            var action = await dialogService.UpdateAsync(dialogState, cancellationToken);
            updateStopwatch.Stop();
            Log(
                $"dialog update completed sessionId={status.SessionId} elapsedMs={updateStopwatch.ElapsedMilliseconds} " +
                $"status={status.Status} display=\"{LogValue(TruncateForLog(dialogState.DisplayText, 80))}\" " +
                $"buttons={(dialogState.DisplayButtons?.Count ?? 0)} message=\"{LogValue(TruncateForLog(message, 80))}\"");
            if (IsFinal(status) || action is null || string.IsNullOrWhiteSpace(action.Key))
            {
                return status;
            }

            // 刷卡机按键由独立对话服务翻译，支付流程只发送后端 sendkey 契约。
            try
            {
                status = await SendKeyAsync(settings, status.SessionId, action, cancellationToken);
                LogStatusSnapshot("manual sendkey completed", status);
                message = null;
            }
            catch (HttpRequestException)
            {
                message = T("linkly.backend.sendKeyFailed", "Card terminal action failed. Try again or recover the transaction.");
                try
                {
                    status = await GetStatusAsync(settings, status.SessionId, cancellationToken);
                }
                catch (HttpRequestException)
                {
                    // 状态刷新失败时继续保留当前会话轮询，不能把一次无效按键升级成最终失败。
                }

                continue;
            }
        }
    }

    private Task PresentFinalFailureAsync(
        string sessionId,
        string message,
        CancellationToken cancellationToken)
    {
        return dialogService.UpdateAsync(
            new LinklyTerminalDialogState(
                sessionId,
                StatusFailed,
                message,
                ReceiptText: null,
                ResponseText: message,
                RecoveryCount: 0,
                LastHttpStatus: null,
                Message: null,
                IsInteractive: false,
                IsFinal: true,
                DisplayButtons: []),
            cancellationToken);
    }

    private static LinklyTerminalDialogState ToDialogState(
        LinklyCloudBackendSessionResponse status,
        string? message)
    {
        var isFinal = IsFinal(status);
        var responseText = NormalizeOptional(status.ResponseText);
        // 最终态不继续显示旧刷卡提示，避免盖住批准/失败结果。
        var displayText = isFinal
            ? responseText ?? NormalizeOptional(status.Status)
            : IsAutoContinueDisplay(status)
                ? "Waiting for card terminal result..."
            : NormalizeOptional(status.DisplayText);

        return new LinklyTerminalDialogState(
            status.SessionId,
            status.Status,
            displayText,
            ReadReceiptText(status),
            responseText,
            status.RecoveryCount,
            status.LastHttpStatus,
            NormalizeOptional(message),
            LinklyTerminalDialogMode.CloudBackendInteractive,
            IsInteractive: !isFinal,
            IsFinal: isFinal,
            DisplayButtons: BuildDisplayButtons(status),
            InputType: NormalizeOptional(status.InputType),
            GraphicCode: NormalizeOptional(status.GraphicCode));
    }

    private static IReadOnlyList<LinklyTerminalDialogButton> BuildDisplayButtons(
        LinklyCloudBackendSessionResponse status)
    {
        if (IsFinal(status))
        {
            return [];
        }

        var buttons = new List<LinklyTerminalDialogButton>();
        if (!HasDisplayNotification(status))
        {
            return buttons;
        }

        if (IsCardTerminalWaitDisplay(status))
        {
            return buttons;
        }

        // Linkly 官方 REST sendkey 将 OK 与 CANCEL 都映射为 Key=0，且一次只能显示一个。
        if (status.OKKeyFlag && status.CancelKeyFlag)
        {
            buttons.Add(new LinklyTerminalDialogButton("linkly.backend.dialog.button.okCancel", LinklyTerminalDialogKeys.OkCancel));
        }
        else if (status.OKKeyFlag)
        {
            buttons.Add(new LinklyTerminalDialogButton("linkly.backend.dialog.button.ok", LinklyTerminalDialogKeys.OkCancel));
        }

        if (status.AcceptYesKeyFlag)
        {
            buttons.Add(new LinklyTerminalDialogButton("linkly.backend.dialog.button.yesApproved", LinklyTerminalDialogKeys.Yes));
        }

        if (status.DeclineNoKeyFlag)
        {
            buttons.Add(new LinklyTerminalDialogButton("linkly.backend.dialog.button.noDeclined", LinklyTerminalDialogKeys.No, IsDestructive: true));
        }

        if (status.AuthoriseKeyFlag)
        {
            // 签名授权按官方 sendkey AUTH=3 发送，不能退化成普通确认。
            buttons.Add(new LinklyTerminalDialogButton("linkly.backend.dialog.button.authoriseSignature", LinklyTerminalDialogKeys.Auth));
        }

        if (!status.OKKeyFlag && status.CancelKeyFlag)
        {
            buttons.Add(CreateCancelButton());
        }

        return buttons;
    }

    private static LinklyTerminalDialogButton CreateCancelButton()
    {
        return new LinklyTerminalDialogButton(
            "linkly.backend.dialog.button.cancel",
            LinklyTerminalDialogKeys.OkCancel,
            IsDestructive: true);
    }

    private static bool HasDisplayNotification(LinklyCloudBackendSessionResponse status)
    {
        return (status.Notifications ?? [])
            .Any(notification => string.Equals(notification.Type, "display", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCardTerminalWaitDisplay(LinklyCloudBackendSessionResponse status)
    {
        var display = NormalizeDisplayPrompt(status.DisplayText);
        if (IsCardTerminalWaitPrompt(display))
        {
            return true;
        }

        return (status.DisplayLines ?? [])
            .Select(NormalizeDisplayPrompt)
            .Any(IsCardTerminalWaitPrompt);
    }

    private static bool IsAutoContinueDisplay(LinklyCloudBackendSessionResponse status)
    {
        var display = NormalizeDisplayPrompt(status.DisplayText);
        if (IsAutoContinuePrompt(display))
        {
            return true;
        }

        return (status.DisplayLines ?? [])
            .Select(NormalizeDisplayPrompt)
            .Any(IsAutoContinuePrompt);
    }

    private static bool IsAutoContinuePrompt(string? value)
    {
        return string.Equals(value, "TAP OK TO CONTINUE", StringComparison.Ordinal);
    }

    private static bool IsCardTerminalWaitPrompt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("SWIPE CARD", StringComparison.Ordinal) ||
            value.Contains("PRESENT CARD", StringComparison.Ordinal) ||
            value.Contains("INSERT CARD", StringComparison.Ordinal) ||
            value.Contains("TAP CARD", StringComparison.Ordinal) ||
            value.Contains("TAP OK TO CONTINUE", StringComparison.Ordinal) ||
            value.Contains("WAITING FOR CARD", StringComparison.Ordinal);
    }

    private static string? NormalizeDisplayPrompt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Join(' ', value.Trim().ToUpperInvariant().Split(
            [' ', '\r', '\n', '\t'],
            StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task<LinklyCloudBackendSessionResponse> StartTransactionAsync(
        LinklyCloudBackendTransactionRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        const string relativeUrl = "api/v1/linkly/cloud-backend/transactions";
        LogHttpRequest(
            "start transaction",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            request.TxnType,
            txnRef: null,
            bodyJson: SerializeDebugJson(request));
        using var response = await httpClient.PostAsJsonAsync(
            relativeUrl,
            request,
            JsonOptions,
            cancellationToken);
        var status = await ReadApiResultAsync(
            response,
            "start transaction",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            request.TxnType,
            stopwatch,
            cancellationToken);
        return status;
    }

    private async Task<LinklyCloudBackendSessionResponse?> GetActiveSessionAsync(
        CardTerminalSettings settings,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var relativeUrl = $"api/v1/linkly/cloud-backend/transactions/active?environment={Uri.EscapeDataString(settings.Environment.ToString())}";
        LogHttpRequest(
            "active session",
            HttpMethod.Get,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            txnRef: null,
            bodyJson: null);
        using var response = await httpClient.GetAsync(
            relativeUrl,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();
            LogHttpResponse(
                "active session",
                HttpMethod.Get,
                FormatRequestUrl(relativeUrl),
                response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                txnType: null,
                txnRef: null,
                body);
            return null;
        }

        var status = await ReadApiResultAsync(
            response,
            "active session",
            HttpMethod.Get,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            stopwatch,
            cancellationToken);
        return status;
    }

    private async Task<LinklyCloudBackendSessionResponse?> GetResumableSessionCoreAsync(
        CardTerminalSettings settings,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var relativeUrl = $"api/v1/linkly/cloud-backend/transactions/resumable?environment={Uri.EscapeDataString(settings.Environment.ToString())}";
        LogHttpRequest(
            "resumable session",
            HttpMethod.Get,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            txnRef: null,
            bodyJson: null);
        using var response = await httpClient.GetAsync(
            relativeUrl,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();
            LogHttpResponse(
                "resumable session",
                HttpMethod.Get,
                FormatRequestUrl(relativeUrl),
                response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                txnType: null,
                txnRef: null,
                body);
            return null;
        }

        var status = await ReadApiResultAsync(
            response,
            "resumable session",
            HttpMethod.Get,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            stopwatch,
            cancellationToken);
        return status;
    }

    private async Task<LinklyCloudBackendSessionResponse> GetStatusAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var relativeUrl = $"api/v1/linkly/cloud-backend/transactions/{Uri.EscapeDataString(sessionId)}/status?environment={Uri.EscapeDataString(settings.Environment.ToString())}";
        LogHttpRequest(
            "status",
            HttpMethod.Get,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            txnRef: null,
            bodyJson: null);
        using var response = await httpClient.GetAsync(
            relativeUrl,
            cancellationToken);
        var status = await ReadApiResultAsync(
            response,
            "status",
            HttpMethod.Get,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            stopwatch,
            cancellationToken);
        return status;
    }

    private async Task<LinklyCloudBackendSessionResponse> RecoverAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var relativeUrl = $"api/v1/linkly/cloud-backend/transactions/{Uri.EscapeDataString(sessionId)}/recover";
        var request = new LinklyCloudBackendRecoverRequest(settings.Environment.ToString());
        LogHttpRequest(
            "recover",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            txnRef: null,
            bodyJson: SerializeDebugJson(request));
        using var response = await httpClient.PostAsJsonAsync(
            relativeUrl,
            request,
            JsonOptions,
            cancellationToken);
        var status = await ReadApiResultAsync(
            response,
            "recover",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            stopwatch,
            cancellationToken);
        return status;
    }

    private async Task<LinklyCloudBackendSessionResponse> SendKeyAsync(
        CardTerminalSettings settings,
        string sessionId,
        LinklyTerminalDialogAction action,
        CancellationToken cancellationToken)
    {
        var normalizedKey = LinklyTerminalDialogKeys.Normalize(action.Key);
        var stopwatch = Stopwatch.StartNew();
        var relativeUrl = $"api/v1/linkly/cloud-backend/transactions/{Uri.EscapeDataString(sessionId)}/sendkey";
        var request = new LinklyCloudBackendSendKeyRequest(
            settings.Environment.ToString(),
            normalizedKey,
            NormalizeOptional(action.Data));
        LogHttpRequest(
            "sendkey",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            txnRef: null,
            bodyJson: SerializeDebugJson(request));
        using var response = await httpClient.PostAsJsonAsync(
            relativeUrl,
            request,
            JsonOptions,
            cancellationToken);
        var status = await ReadApiResultAsync(
            response,
            "sendkey",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            stopwatch,
            cancellationToken);
        return status;
    }

    private async Task<string?> TryResolveOriginalBackendRefundReferenceAsync(
        CardTerminalSettings settings,
        string? originalReference,
        CancellationToken cancellationToken)
    {
        if (!LinklyBackendPaymentReference.TryGetPrintMarker(originalReference, out var environment, out var sessionId) ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            Log($"refund reference recovery skipped reason=no-backend-session originalReference={LogValue(originalReference)}");
            return null;
        }

        if (!string.Equals(environment, settings.Environment.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            Log(
                $"refund reference recovery skipped reason=environment-mismatch referenceEnvironment={LogValue(environment)} " +
                $"settingsEnvironment={settings.Environment}");
            return null;
        }

        try
        {
            var status = await GetStatusAsync(settings, sessionId, cancellationToken);
            var refundReference = TryReadRefundReference(status, originalReference) ??
                TryReadOriginalTxnRef(originalReference);
            Log(
                $"refund reference recovery completed sessionId={sessionId} status={status.Status} " +
                $"notifications={status.Notifications?.Count ?? 0} refundReference={LogValue(refundReference)} " +
                $"transactionPayloads={BuildRefundReferenceRecoverySnapshot(status)}");
            return refundReference;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"refund reference recovery failed sessionId={sessionId} error={ex.GetType().Name}");
            return null;
        }
    }

    private static async Task<LinklyCloudBackendSessionResponse> ReadApiResultAsync(
        HttpResponseMessage response,
        string operation,
        HttpMethod method,
        string url,
        string? txnType,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        ApiResult<LinklyCloudBackendSessionResponse>? result = null;
        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                result = JsonSerializer.Deserialize<ApiResult<LinklyCloudBackendSessionResponse>>(content, JsonOptions);
            }
            catch (JsonException) when (!response.IsSuccessStatusCode)
            {
                result = null;
            }
        }
        stopwatch.Stop();
        LogHttpResponse(
            operation,
            method,
            url,
            response.StatusCode,
            stopwatch.ElapsedMilliseconds,
            txnType,
            result?.Data?.TxnRef,
            content);

        if (!response.IsSuccessStatusCode)
        {
            throw new LinklyBackendHttpException(
                result?.Message ?? $"Linkly backend request failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode);
        }

        if (result?.Success != true || result.Data is null)
        {
            throw new LinklyBackendHttpException(
                result?.Message ?? "Linkly backend returned a failure response.",
                response.StatusCode);
        }

        if (string.IsNullOrWhiteSpace(result.Data.SessionId))
        {
            throw new JsonException("Linkly backend response is missing session id.");
        }

        LogStatusSnapshot($"{operation} http response elapsedMs={stopwatch.ElapsedMilliseconds} http={(int)response.StatusCode}", result.Data);
        return result.Data;
    }

    private static string? TryReadApiMessage(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            var result = JsonSerializer.Deserialize<ApiResult<LinklyCloudBackendSessionResponse>>(content, JsonOptions);
            return NormalizeOptional(result?.Message);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static LinklyCloudBackendHealthResponse ReadHealthResult(string content)
    {
        var result = JsonSerializer.Deserialize<ApiResult<LinklyCloudBackendHealthResponse>>(content, JsonOptions);
        if (result?.Success != true || result.Data is null)
        {
            throw new JsonException("Linkly backend health response is invalid.");
        }

        return result.Data;
    }

    private string FormatHealthFailure(LinklyCloudBackendHealthResponse health)
    {
        var failedMessages = GetFailedHealthChecks(health)
            .Select(check => NormalizeOptional(check.Message))
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return failedMessages.Length == 0
            ? T("linkly.backend.configIncomplete", "ANZ Linkly Cloud backend configuration is incomplete.")
            : string.Join(Environment.NewLine, failedMessages!);
    }

    private static IReadOnlyList<LinklyCloudBackendHealthCheckDto> GetFailedHealthChecks(
        LinklyCloudBackendHealthResponse health)
    {
        return (health.Checks ?? [])
            .Where(check => !check.IsReady)
            .ToArray();
    }

    private PaymentAuthorizationResult ToAuthorizationResult(
        LinklyCloudBackendSessionResponse status,
        decimal requestedAmount,
        string requestedTxnRef,
        bool suppressPrintedReceipt)
    {
        if (string.Equals(status.Status, StatusNotSubmitted, StringComparison.OrdinalIgnoreCase))
        {
            return new PaymentAuthorizationResult(false, null, T("linkly.backend.notSubmitted", "Linkly Cloud transaction was not submitted. Retry the payment."));
        }

        var transactionResult = ReadTransactionResult(status, requestedAmount, requestedTxnRef);
        var amount = transactionResult.Amount ?? requestedAmount;
        var receiptText = ReadReceiptText(status, suppressPrintedReceipt);
        var transaction = ToCardTransaction(transactionResult, amount, receiptText);
        var approved = string.Equals(status.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase) &&
            transactionResult.Succeeded &&
            string.Equals(transactionResult.ResponseCode?.Trim(), "00", StringComparison.OrdinalIgnoreCase);
        var reference = LinklyBackendPaymentReference.Format(
            transaction.TxnRef ?? transactionResult.SessionId,
            transactionResult.SessionId,
            status.Environment,
            transactionResult.RefundReference);

        return approved
            ? new PaymentAuthorizationResult(
                true,
                reference,
                "ANZ Linkly Cloud",
                amount,
                [transaction],
                ProcessorName,
                status.Environment,
                LinklyConnectionMode.CloudBackendAsync.ToString(),
                null,
                status.SessionId,
                transaction.TxnRef,
                transaction.ResponseCode,
                transaction.ResponseText)
            : new PaymentAuthorizationResult(
                false,
                reference,
                FormatResponseMessage(transactionResult.ResponseText, transactionResult.ResponseCode),
                amount,
                [transaction],
                ProcessorName,
                status.Environment,
                LinklyConnectionMode.CloudBackendAsync.ToString(),
                null,
                status.SessionId,
                transaction.TxnRef,
                transaction.ResponseCode,
                transaction.ResponseText);
    }

    private static CardTransactionDto ToCardTransaction(
        LinklyCloudTransactionResult response,
        decimal amount,
        string? receiptText)
    {
        return new CardTransactionDto(
            ProcessorName,
            NormalizeOptional(response.TxnRef) ?? response.SessionId,
            NormalizeOptional(response.AuthCode),
            NormalizeOptional(response.CardType),
            int.TryParse(response.CardName, out var cardName) && cardName > 0 ? cardName : null,
            MaskCardNumber(response.Pan),
            NormalizeOptional(response.Caid),
            NormalizeOptional(response.ResponseCode),
            NormalizeOptional(response.ResponseText),
            NormalizeOptional(response.Stan),
            null,
            decimal.Round(amount, 2, MidpointRounding.AwayFromZero),
            NormalizeOptional(receiptText),
            NormalizeOptional(response.RefundReference));
    }

    private static LinklyCloudTransactionResult ReadTransactionResult(
        LinklyCloudBackendSessionResponse status,
        decimal requestedAmount,
        string requestedTxnRef)
    {
        var protectedResponseCode = NormalizeOptional(status.ResponseCode);
        var protectedResponseText = NormalizeOptional(status.ResponseText);
        var notifications = status.Notifications ?? [];
        var fallbackRefundReference = TryReadRefundReference(status, null);
        var transactionNotification = string.IsNullOrWhiteSpace(protectedResponseCode)
            ? notifications.LastOrDefault(IsTransactionNotification)
            : notifications.LastOrDefault(notification =>
                IsTransactionNotification(notification) &&
                TransactionNotificationMatchesProtectedResult(notification, protectedResponseCode, protectedResponseText));
        if (transactionNotification is null || string.IsNullOrWhiteSpace(transactionNotification.PayloadJson))
        {
            return new LinklyCloudTransactionResult(
                status.SessionId,
                string.Equals(protectedResponseCode, "00", StringComparison.OrdinalIgnoreCase),
                NormalizeOptional(status.TxnRef) ?? requestedTxnRef,
                null,
                null,
                null,
                null,
                null,
                protectedResponseCode,
                protectedResponseText,
                null,
                requestedAmount,
                fallbackRefundReference);
        }

        using var document = JsonDocument.Parse(transactionNotification.PayloadJson);
        var response = ReadResponse(document.RootElement);
        var purchaseAnalysisData = ReadObject(response, "PurchaseAnalysisData");
        var notificationRefundReference = TryReadRefundReference(document.RootElement, out _);
        return new LinklyCloudTransactionResult(
            status.SessionId,
            string.IsNullOrWhiteSpace(protectedResponseCode)
                ? ReadBool(response, "Success") == true
                : string.Equals(protectedResponseCode, "00", StringComparison.OrdinalIgnoreCase),
            NormalizeOptional(status.TxnRef) ?? requestedTxnRef,
            ReadString(response, "AuthCode"),
            ReadString(response, "CardType"),
            ReadString(response, "CardName"),
            ReadString(response, "Pan"),
            ReadString(response, "Caid"),
            protectedResponseCode ?? ReadString(response, "ResponseCode"),
            protectedResponseText ?? ReadString(response, "ResponseText"),
            ReadString(response, "Stan"),
            ReadDecimal(response, "AmtPurchase") ?? requestedAmount,
            ReadString(purchaseAnalysisData, "RFN") ?? notificationRefundReference ?? fallbackRefundReference);
    }

    private static string? TryReadRefundReference(
        LinklyCloudBackendSessionResponse status,
        string? fallbackReference)
    {
        var backendReference = LinklyBackendPaymentReference.TryGetRefundReference(fallbackReference);
        if (!string.IsNullOrWhiteSpace(backendReference))
        {
            return backendReference;
        }

        foreach (var notification in (status.Notifications ?? []).Where(IsTransactionNotification).Reverse())
        {
            if (string.IsNullOrWhiteSpace(notification.PayloadJson))
            {
                continue;
            }

            using var document = JsonDocument.Parse(notification.PayloadJson);
            var refundReference = TryReadRefundReference(document.RootElement, out _);
            if (!string.IsNullOrWhiteSpace(refundReference))
            {
                return refundReference;
            }
        }

        return null;
    }

    private static string BuildRefundReferenceRecoverySnapshot(LinklyCloudBackendSessionResponse status)
    {
        var transactionNotifications = (status.Notifications ?? [])
            .Where(IsTransactionNotification)
            .TakeLast(5)
            .ToArray();
        if (transactionNotifications.Length == 0)
        {
            return "<none>";
        }

        var parts = transactionNotifications.Select((notification, index) =>
        {
            if (string.IsNullOrWhiteSpace(notification.PayloadJson))
            {
                return $"#{index + 1}:empty";
            }

            try
            {
                using var document = JsonDocument.Parse(notification.PayloadJson);
                var root = document.RootElement;
                var response = ReadResponse(root);
                var purchaseAnalysisData = ReadValue(response, "PurchaseAnalysisData");
                var refundReference = TryReadRefundReference(root, out var source);
                return $"#{index + 1}:bytes={notification.PayloadJson.Length},rootKeys={DescribeKeys(root)},responseKeys={DescribeKeys(response)}," +
                    $"padKind={DescribeKind(purchaseAnalysisData)},padKeys={DescribeKeys(purchaseAnalysisData)},rfnSource={LogValue(source)},rfn={LogValue(refundReference)}";
            }
            catch (JsonException ex)
            {
                return $"#{index + 1}:invalid-json:{ex.GetType().Name}";
            }
        });

        return string.Join(" | ", parts);
    }

    private static string? TryReadRefundReference(JsonElement root, out string? source)
    {
        var response = ReadResponse(root);
        var purchaseAnalysisData = ReadValue(response, "PurchaseAnalysisData");
        // Linkly backend 的 PAD 在不同通知里可能是对象、键值数组或字符串，退款恢复必须宽解析 RFN。
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
            JsonValueKind.Number => null,
            JsonValueKind.True => null,
            JsonValueKind.False => null,
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

    private static JsonElement ReadValue(JsonElement root, string propertyName)
    {
        return TryGetProperty(root, propertyName, out var value) ? value : default;
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

    private static bool IsTransactionNotification(LinklyCloudBackendNotificationDto notification)
    {
        return string.Equals(notification.Type, "transaction", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TransactionNotificationMatchesProtectedResult(
        LinklyCloudBackendNotificationDto notification,
        string protectedResponseCode,
        string? protectedResponseText)
    {
        if (string.IsNullOrWhiteSpace(notification.PayloadJson))
        {
            return false;
        }

        using var document = JsonDocument.Parse(notification.PayloadJson);
        var response = ReadResponse(document.RootElement);
        if (!string.Equals(ReadString(response, "ResponseCode"), protectedResponseCode, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var responseText = ReadString(response, "ResponseText");
        return string.IsNullOrWhiteSpace(protectedResponseText) ||
            string.IsNullOrWhiteSpace(responseText) ||
            string.Equals(responseText, protectedResponseText, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadReceiptText(LinklyCloudBackendSessionResponse status)
    {
        return ReadReceiptText(status, suppressPrintedReceipt: false);
    }

    private static string? ReadReceiptText(
        LinklyCloudBackendSessionResponse status,
        bool suppressPrintedReceipt)
    {
        if (suppressPrintedReceipt && status.ReceiptPrintedAt is not null)
        {
            return null;
        }

        return NormalizeOptional(status.ReceiptText) ?? ReadReceiptText(status.Notifications ?? []);
    }

    private static string? ReadReceiptText(IReadOnlyList<LinklyCloudBackendNotificationDto> notifications)
    {
        var receipts = notifications
            .Where(notification => string.Equals(notification.Type, "receipt", StringComparison.OrdinalIgnoreCase))
            .Select(notification => ReadReceiptNotification(notification.PayloadJson))
            .Where(receipt => !string.IsNullOrWhiteSpace(receipt))
            .Select(receipt => receipt!)
            .ToArray();
        return receipts.Length == 0 ? null : string.Join(Environment.NewLine + Environment.NewLine, receipts);
    }

    private static bool HasReceipt(LinklyCloudBackendSessionResponse status)
    {
        return !string.IsNullOrWhiteSpace(status.ReceiptText) ||
            (status.Notifications ?? []).Any(notification =>
                string.Equals(notification.Type, "receipt", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(ReadReceiptNotification(notification.PayloadJson)));
    }

    private static string? ReadReceiptNotification(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(payloadJson);
        return ReadReceiptText(document.RootElement) ?? ReadReceiptText(ReadResponse(document.RootElement));
    }

    private static string? ReadReceiptText(JsonElement element)
    {
        if (!TryGetProperty(element, "ReceiptText", out var receipt))
        {
            return null;
        }

        if (receipt.ValueKind == JsonValueKind.String)
        {
            return NormalizeOptional(receipt.GetString());
        }

        if (receipt.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var lines = receipt
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => NormalizeOptional(item.GetString()))
            .Where(line => line is not null)
            .Select(line => line!)
            .ToArray();
        return lines.Length == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    private static bool IsFinal(LinklyCloudBackendSessionResponse status)
    {
        return string.Equals(status.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status.Status, StatusFailed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status.Status, StatusNotSubmitted, StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresRecovery(LinklyCloudBackendSessionResponse status)
    {
        return string.Equals(status.Status, StatusTokenRefreshRequired, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status.RecoveryAction, RecoveryRefreshToken, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(status.RecoveryAction, RecoveryRetry, StringComparison.OrdinalIgnoreCase) &&
                IsRecoveryHttpStatus(status.LastHttpStatus));
    }

    private async Task DelayBeforeNextPollAsync(
        LinklyCloudBackendSessionResponse status,
        CancellationToken cancellationToken)
    {
        var delay = GetNextPollDelay(status);
        Log($"poll delay start sessionId={status.SessionId} delayMs={delay.TotalMilliseconds:0} lastHttp={status.LastHttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "<null>"}");
        var stopwatch = Stopwatch.StartNew();
        await DelayAsync(delay, cancellationToken);
        stopwatch.Stop();
        Log($"poll delay completed sessionId={status.SessionId} elapsedMs={stopwatch.ElapsedMilliseconds}");
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay > TimeSpan.Zero)
        {
            await _delayAsync(delay, cancellationToken);
        }
    }

    private TimeSpan GetNextPollDelay(LinklyCloudBackendSessionResponse status)
    {
        if (!IsRecoveryHttpStatus(status.LastHttpStatus))
        {
            return _pollInterval;
        }

        var exponent = Math.Clamp(status.RecoveryCount, 0, 6);
        var multiplier = 1 << exponent;
        var milliseconds = Math.Min(
            _pollInterval.TotalMilliseconds * multiplier,
            TimeSpan.FromSeconds(30).TotalMilliseconds);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static bool IsRecoveryHttpStatus(int? httpStatus)
    {
        return httpStatus == (int)HttpStatusCode.RequestTimeout ||
            httpStatus is >= 500 and <= 599;
    }

    private static IReadOnlyDictionary<string, string>? BuildPurchaseAnalysisData(
        decimal amount,
        PosSessionState session,
        string? refundReference)
    {
        if (string.IsNullOrWhiteSpace(refundReference))
        {
            return null;
        }

        return new Dictionary<string, string>
        {
            ["RFN"] = refundReference.Trim(),
            ["OPR"] = $"{session.CashierId}|{session.CashierName}",
            ["AMT"] = ToMinorUnits(amount).ToString("D9", CultureInfo.InvariantCulture),
            ["PCM"] = "0000"
        };
    }

    private static long ToMinorUnits(decimal amount)
    {
        return decimal.ToInt64(decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));
    }

    private static string BuildTxnRef(PosSessionState session)
    {
        var device = new string(session.DeviceCode.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(device))
        {
            device = "POS";
        }

        return Limit($"{device}{DateTimeOffset.UtcNow:yyMMddHHmmss}", 16);
    }

    private static string? TryParseRefundReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var backendRefundReference = LinklyBackendPaymentReference.TryGetRefundReference(reference);
        if (!string.IsNullOrWhiteSpace(backendRefundReference))
        {
            return backendRefundReference;
        }

        var parts = reference.Trim().Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 &&
            string.Equals(parts[0], "ANZCLOUD", StringComparison.OrdinalIgnoreCase)
                ? parts[2]
                : null;
    }

    private static string? TryReadOriginalTxnRef(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var parts = reference.Trim().Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 &&
            string.Equals(parts[0], LinklyBackendPaymentReference.Prefix, StringComparison.OrdinalIgnoreCase)
                ? Uri.UnescapeDataString(parts[1])
                : null;
    }

    private string FormatResponseMessage(string? responseText, string? responseCode)
    {
        var text = NormalizeOptional(responseText);
        var code = NormalizeOptional(responseCode);
        if (text is null && code is null)
        {
            return T("linkly.backend.declined", "ANZ Linkly Cloud transaction was declined.");
        }

        return code is null ? text! : $"{text ?? T("linkly.backend.declined", "ANZ Linkly Cloud transaction was declined.")} ({code})";
    }

    private static JsonElement ReadResponse(JsonElement root)
    {
        return TryGetProperty(root, "Response", out var response) && response.ValueKind == JsonValueKind.Object
            ? response
            : root;
    }

    private static JsonElement ReadObject(JsonElement root, string propertyName)
    {
        return TryGetProperty(root, propertyName, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : default;
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

    private static bool? ReadBool(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static decimal? ReadDecimal(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var decimalValue))
        {
            return decimalValue / 100m;
        }

        return value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), out var parsed)
            ? parsed / 100m
            : null;
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

    private static string? MaskCardNumber(string? pan)
    {
        var value = NormalizeOptional(pan);
        if (value is null)
        {
            return null;
        }

        if (value.Contains('*', StringComparison.Ordinal) || value.Contains('X', StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length <= 4 ? digits : $"****{digits[^4..]}";
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string Limit(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static void Log(string message)
    {
        LinklyJsonLog.WriteMessage("LinklyBackend", "backend-terminal", message);
    }

    private string FormatRequestUrl(string relativeUrl)
    {
        return httpClient.BaseAddress is null
            ? relativeUrl
            : new Uri(httpClient.BaseAddress, relativeUrl).ToString();
    }

    private static string SerializeDebugJson<T>(T value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static void LogHttpRequest(
        string operation,
        HttpMethod method,
        string url,
        string? txnType,
        string? txnRef,
        string? bodyJson)
    {
        LinklyJsonLog.Write(
            "LinklyBackend",
            "backend-terminal",
            operation,
            "request",
            direction: "request",
            request: new
            {
                method = method.Method,
                url,
                body = RawJsonBody(bodyJson)
            },
            details: new
            {
                timestamp = DateTimeOffset.Now,
                txnType,
                txnRef
            });
    }

    private static void LogHttpResponse(
        string operation,
        HttpMethod method,
        string url,
        HttpStatusCode statusCode,
        long elapsedMs,
        string? txnType,
        string? txnRef,
        string? bodyJson)
    {
        LinklyJsonLog.Write(
            "LinklyBackend",
            "backend-terminal",
            operation,
            "response",
            direction: "response",
            httpStatus: statusCode,
            success: (int)statusCode is >= 200 and < 300,
            elapsedMs: elapsedMs,
            response: new
            {
                method = method.Method,
                url,
                body = RawJsonBody(bodyJson)
            },
            details: new
            {
                timestamp = DateTimeOffset.Now,
                txnType,
                txnRef
            });
    }

    private static void LogStatusSnapshot(string prefix, LinklyCloudBackendSessionResponse status)
    {
        Log(
            $"{prefix} sessionId={status.SessionId} status={status.Status} lastHttp={status.LastHttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "<null>"} " +
            $"txnRef={LogValue(status.TxnRef)} " +
            $"display=\"{LogValue(TruncateForLog(status.DisplayText, 80))}\" " +
            $"flags=cancel:{status.CancelKeyFlag},ok:{status.OKKeyFlag},yes:{status.AcceptYesKeyFlag},no:{status.DeclineNoKeyFlag},auth:{status.AuthoriseKeyFlag} " +
            $"notifications={status.Notifications?.Count ?? 0}");
    }

    private static string LogJsonBody(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<none>" : value.Trim();
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

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value.Trim();
    }

    private static string? TruncateForLog(string? value, int maxLength)
    {
        var normalized = NormalizeOptional(value);
        if (normalized is null || normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..maxLength];
    }

    private static string GetComponentVersion()
    {
        var assembly = typeof(LinklyBackendTerminalClient).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
            assembly.GetName().Version?.ToString() ??
            "unknown";
    }

    private string T(string key, string fallback)
    {
        var value = localization?.T(key) ?? LocalizationResourceProvider.Instance[key];
        return string.IsNullOrWhiteSpace(value) || value.StartsWith("[[", StringComparison.Ordinal)
            ? fallback
            : value;
    }

    private sealed class LinklyBackendHttpException(
        string message,
        HttpStatusCode httpStatus) : HttpRequestException(message)
    {
        public HttpStatusCode HttpStatus { get; } = httpStatus;
    }
}
