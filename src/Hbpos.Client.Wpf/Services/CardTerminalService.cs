using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public enum CardProcessorKind
{
    None,
    Linkly,
    Square
}

public enum CardTerminalEnvironment
{
    Production,
    Sandbox
}

public enum LinklyConnectionMode
{
    Local,
    Cloud
}

public sealed record CardTerminalConfiguration(
    CardProcessorKind Processor,
    CardTerminalEnvironment Environment,
    string LinklyHost,
    int LinklyPort,
    string? SquareLocationId,
    string? SquareDeviceId,
    bool HasProtectedSquareAccessToken,
    int TerminalTimeoutSeconds,
    LinklyConnectionMode LinklyConnectionMode = LinklyConnectionMode.Local,
    bool HasProtectedLinklyCloudSecret = false)
{
    public static CardTerminalConfiguration Default { get; } = new(
        CardProcessorKind.None,
        CardTerminalEnvironment.Production,
        "127.0.0.1",
        2011,
        null,
        null,
        false,
        90,
        LinklyConnectionMode.Local,
        false);
}

public sealed record CardTerminalSettings(
    CardProcessorKind Processor,
    CardTerminalEnvironment Environment,
    string LinklyHost,
    int LinklyPort,
    string? SquareAccessToken,
    string? SquareLocationId,
    string? SquareDeviceId,
    string SquareApiBaseUrl,
    TimeSpan TerminalTimeout,
    LinklyConnectionMode LinklyConnectionMode = LinklyConnectionMode.Local,
    string? LinklyCloudSecret = null,
    string LinklyCloudAuthBaseUrl = "https://auth.cloud.pceftpos.com/v1/",
    string LinklyCloudRestBaseUrl = "https://rest.pos.cloud.pceftpos.com/v1/",
    string LinklyPosName = "HBPOS",
    string LinklyPosVersion = "1.0.0",
    string? LinklyPosVendorId = null)
{
    public const string SquareVersion = "2026-01-22";
    public const string DefaultLinklyPosName = "HotBargainPOS";
    public const string DefaultLinklyPosVersion = "2026.5.1";

    public static CardTerminalSettings FromEnvironment()
    {
        var processorText = System.Environment.GetEnvironmentVariable("HBPOS_CARD_PROCESSOR") ?? string.Empty;
        var processor = processorText.Trim().ToUpperInvariant() switch
        {
            "LINKLY" or "ANZ" => CardProcessorKind.Linkly,
            "SQUARE" => CardProcessorKind.Square,
            _ => CardProcessorKind.None
        };

        var terminalEnvironment = ReadEnvironment();
        var apiBase = System.Environment.GetEnvironmentVariable("HBPOS_SQUARE_API_BASE_URL")?.Trim();
        var linklyAuthBase = System.Environment.GetEnvironmentVariable("HBPOS_LINKLY_CLOUD_AUTH_BASE_URL")?.Trim();
        var linklyRestBase = System.Environment.GetEnvironmentVariable("HBPOS_LINKLY_CLOUD_REST_BASE_URL")?.Trim();

        return new CardTerminalSettings(
            processor,
            terminalEnvironment,
            System.Environment.GetEnvironmentVariable("HBPOS_LINKLY_HOST")?.Trim() is { Length: > 0 } host ? host : "127.0.0.1",
            int.TryParse(System.Environment.GetEnvironmentVariable("HBPOS_LINKLY_PORT"), out var port) ? port : 2011,
            null,
            System.Environment.GetEnvironmentVariable("HBPOS_SQUARE_LOCATION_ID"),
            System.Environment.GetEnvironmentVariable("HBPOS_SQUARE_DEVICE_ID"),
            string.IsNullOrWhiteSpace(apiBase)
                ? GetSquareApiBaseUrl(terminalEnvironment)
                : NormalizeSquareApiBaseUrl(apiBase),
            TimeSpan.FromSeconds(
                int.TryParse(System.Environment.GetEnvironmentVariable("HBPOS_CARD_TERMINAL_TIMEOUT_SECONDS"), out var timeoutSeconds) && timeoutSeconds > 0
                    ? timeoutSeconds
                    : 90),
            ReadLinklyConnectionMode(),
            null,
            string.IsNullOrWhiteSpace(linklyAuthBase)
                ? GetLinklyCloudAuthBaseUrl(terminalEnvironment)
                : NormalizeBaseUrl(linklyAuthBase),
            string.IsNullOrWhiteSpace(linklyRestBase)
                ? GetLinklyCloudRestBaseUrl(terminalEnvironment)
                : NormalizeBaseUrl(linklyRestBase),
            ReadText("HBPOS_LINKLY_POS_NAME", DefaultLinklyPosName),
            ReadText("HBPOS_LINKLY_POS_VERSION", DefaultLinklyPosVersion),
            NormalizeOptional(System.Environment.GetEnvironmentVariable("HBPOS_LINKLY_POS_VENDOR_ID")));
    }

    public static string GetSquareApiBaseUrl(CardTerminalEnvironment environment)
    {
        return environment == CardTerminalEnvironment.Sandbox
            ? "https://connect.squareupsandbox.com/v2/"
            : "https://connect.squareup.com/v2/";
    }

    public static string NormalizeSquareApiBaseUrl(string apiBaseUrl)
    {
        var trimmed = apiBaseUrl.Trim();
        if (trimmed.Length == 0)
        {
            return GetSquareApiBaseUrl(CardTerminalEnvironment.Production);
        }

        trimmed = trimmed.TrimEnd('/');
        if (!trimmed.EndsWith("/v2", StringComparison.OrdinalIgnoreCase))
        {
            trimmed += "/v2";
        }

        return trimmed + "/";
    }

    public static string GetLinklyCloudAuthBaseUrl(CardTerminalEnvironment environment)
    {
        return environment == CardTerminalEnvironment.Sandbox
            ? "https://auth.sandbox.cloud.pceftpos.com/v1/"
            : "https://auth.cloud.pceftpos.com/v1/";
    }

    public static string GetLinklyCloudRestBaseUrl(CardTerminalEnvironment environment)
    {
        return environment == CardTerminalEnvironment.Sandbox
            ? "https://rest.pos.sandbox.cloud.pceftpos.com/v1/"
            : "https://rest.pos.cloud.pceftpos.com/v1/";
    }

    private static CardTerminalEnvironment ReadEnvironment()
    {
        var environmentText = System.Environment.GetEnvironmentVariable("HBPOS_CARD_TERMINAL_ENVIRONMENT") ??
            System.Environment.GetEnvironmentVariable("HBPOS_SQUARE_ENVIRONMENT") ??
            System.Environment.GetEnvironmentVariable("SQUARE_ENVIRONMENT") ??
            string.Empty;

        return environmentText.Trim().ToUpperInvariant() switch
        {
            "SANDBOX" or "TEST" => CardTerminalEnvironment.Sandbox,
            _ => CardTerminalEnvironment.Production
        };
    }

    private static LinklyConnectionMode ReadLinklyConnectionMode()
    {
        var modeText = System.Environment.GetEnvironmentVariable("HBPOS_LINKLY_CONNECTION_MODE") ??
            System.Environment.GetEnvironmentVariable("HBPOS_LINKLY_MODE") ??
            string.Empty;

        return modeText.Trim().ToUpperInvariant() switch
        {
            "CLOUD" => LinklyConnectionMode.Cloud,
            _ => LinklyConnectionMode.Local
        };
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim();
        return trimmed.EndsWith("/", StringComparison.Ordinal) ? trimmed : trimmed + "/";
    }

    private static string ReadText(string key, string fallback)
    {
        var value = System.Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

public interface ICardTerminalSettingsProvider
{
    Task<CardTerminalSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
}

public interface ISquareAccessTokenProvider
{
    Task<string?> GetSquareAccessTokenAsync(
        CardTerminalEnvironment environment,
        bool forceRefresh,
        CancellationToken cancellationToken = default);
}

public interface ISquareTokenResolver : ISquareAccessTokenProvider
{
    Task<string?> GetTokenAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);

    Task<string?> RefreshTokenAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);
}

public interface ILinklyCloudSecretStore
{
    Task<string?> GetLinklyCloudSecretAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);

    Task SaveLinklyCloudSecretAsync(
        CardTerminalEnvironment environment,
        string secret,
        CancellationToken cancellationToken = default);

    Task<string> GetOrCreateLinklyCloudPosIdAsync(
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken = default);
}

public interface ICardTerminalSettingsStore : ICardTerminalSettingsProvider, ISquareTokenResolver, ILinklyCloudSecretStore
{
    Task<CardTerminalConfiguration> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        CardTerminalConfiguration configuration,
        string? squareAccessToken,
        CancellationToken cancellationToken = default);

    Task<string?> GetSquareAccessTokenAsync(CancellationToken cancellationToken = default);
}

public sealed class StaticCardTerminalSettingsProvider(CardTerminalSettings settings) : ICardTerminalSettingsProvider
{
    public Task<CardTerminalSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(settings);
    }
}

public sealed class ConfiguredCardTerminalClient : ICardTerminalClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan SquarePollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SquareCleanupTimeout = TimeSpan.FromSeconds(5);

    private readonly ICardTerminalSettingsProvider _settingsProvider;
    private readonly HttpClient _httpClient;
    private readonly ILinklyTerminalClient? _linklyTerminalClient;
    private readonly ISquareAccessTokenProvider? _squareAccessTokenProvider;
    private readonly ILocalizationService? _localization;
    private readonly ConcurrentDictionary<SquareRefundAttemptKey, string> _squareRefundIdempotencyKeys = new();

    public ConfiguredCardTerminalClient(
        ICardTerminalSettingsProvider settingsProvider,
        HttpClient httpClient,
        ILinklyTerminalClient? linklyTerminalClient = null,
        ISquareAccessTokenProvider? squareAccessTokenProvider = null,
        ILocalizationService? localization = null)
    {
        _settingsProvider = settingsProvider;
        _httpClient = httpClient;
        _linklyTerminalClient = linklyTerminalClient;
        _squareAccessTokenProvider = squareAccessTokenProvider;
        _localization = localization;
    }

    public async Task<PaymentAuthorizationResult> AuthorizeAsync(
        decimal amount,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        if (amount <= 0m)
        {
            return new PaymentAuthorizationResult(false, null, T("payment.card.amountMustBePositive", "Card amount must be greater than zero."));
        }

        var settings = await _settingsProvider.GetSettingsAsync(cancellationToken);
        return settings.Processor switch
        {
            CardProcessorKind.Linkly => _linklyTerminalClient is null
                ? new PaymentAuthorizationResult(false, null, T("payment.card.linklyUnavailable", "ANZ Linkly terminal adapter is unavailable."))
                : await _linklyTerminalClient.PurchaseAsync(amount, session, settings, cancellationToken),
            CardProcessorKind.Square => await AuthorizeSquareAsync(settings, amount, session, cancellationToken),
            _ => new PaymentAuthorizationResult(false, null, T("payment.card.status.notConfigured", "Card terminal is not configured."))
        };
    }

    public async Task<PaymentAuthorizationResult> RefundAsync(
        decimal amount,
        PosSessionState session,
        string? originalReference,
        CancellationToken cancellationToken = default)
    {
        if (amount <= 0m)
        {
            return new PaymentAuthorizationResult(false, null, T("payment.card.amountMustBePositive", "Card amount must be greater than zero."));
        }

        var settings = await _settingsProvider.GetSettingsAsync(cancellationToken);
        return settings.Processor switch
        {
            CardProcessorKind.Linkly => _linklyTerminalClient is null
                ? new PaymentAuthorizationResult(false, null, T("payment.card.linklyUnavailable", "ANZ Linkly terminal adapter is unavailable."))
                : await _linklyTerminalClient.RefundAsync(amount, session, settings, originalReference, cancellationToken),
            CardProcessorKind.Square => await RefundSquareAsync(settings, amount, originalReference, cancellationToken),
            _ => new PaymentAuthorizationResult(false, null, T("payment.card.status.notConfigured", "Card terminal is not configured."))
        };
    }

    private async Task<PaymentAuthorizationResult> AuthorizeSquareAsync(
        CardTerminalSettings settings,
        decimal amount,
        PosSessionState session,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.SquareAccessToken) ||
            string.IsNullOrWhiteSpace(settings.SquareLocationId) ||
            string.IsNullOrWhiteSpace(settings.SquareDeviceId))
        {
            return new PaymentAuthorizationResult(false, null, T("payment.card.squareConfigIncomplete", "Square terminal configuration is incomplete."));
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(settings.TerminalTimeout);

        var currentSettings = settings;
        var checkoutDeviceId = SquareDeviceIdNormalizer.NormalizeForTerminalCheckout(settings.SquareDeviceId);
        if (string.IsNullOrWhiteSpace(checkoutDeviceId))
        {
            return new PaymentAuthorizationResult(false, null, T("payment.card.squareConfigIncomplete", "Square terminal configuration is incomplete."));
        }

        var hasRefreshedToken = false;
        string? checkoutId = null;
        var pollCount = 0;
        string? lastLoggedStatus = null;
        var reference = Limit($"{session.DeviceCode}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}", 40);
        var createRequest = new
        {
            idempotency_key = Guid.NewGuid().ToString("N"),
            checkout = new
            {
                amount_money = new
                {
                    amount = ToMinorUnits(amount),
                    currency = "AUD"
                },
                device_options = new
                {
                    device_id = checkoutDeviceId
                },
                location_id = settings.SquareLocationId,
                reference_id = reference,
                note = Limit($"HBPOS {session.StoreCode} {session.DeviceCode}", 500)
            }
        };

        LogSquare(
            $"authorize start storeCode={session.StoreCode} deviceCode={session.DeviceCode} amount={amount:0.00} environment={settings.Environment} locationId={LogValue(settings.SquareLocationId)} storedSquareDeviceId={LogValue(settings.SquareDeviceId)} checkoutDeviceId={LogValue(checkoutDeviceId)} timeoutSeconds={(int)settings.TerminalTimeout.TotalSeconds}");

        try
        {
            var createResult = await SendSquareWithTokenRefreshAsync(
                currentSettings,
                HttpMethod.Post,
                "terminals/checkouts",
                createRequest,
                allowRefresh: !hasRefreshedToken,
                cancellationToken: timeoutCts.Token);
            currentSettings = createResult.Settings;
            hasRefreshedToken = hasRefreshedToken || createResult.Refreshed;
            using var createResponse = createResult.Response;
            var createBody = await createResponse.Content.ReadAsStringAsync(timeoutCts.Token);
            if (!createResponse.IsSuccessStatusCode)
            {
                LogSquare($"checkout create failed http={(int)createResponse.StatusCode} detail={LogValue(ReadSquareErrorMessage(createBody))}");
                return FailSquareRequest("checkout", createResponse.StatusCode, createBody);
            }

            using (var createDocument = JsonDocument.Parse(createBody))
            {
                var checkout = ReadRequiredCheckout(createDocument.RootElement);
                checkoutId = ReadRequiredString(checkout, "id");
                lastLoggedStatus = ReadOptionalString(checkout, "status");
                if (string.IsNullOrWhiteSpace(checkoutId))
                {
                    LogSquare("checkout create returned empty checkout id");
                    return new PaymentAuthorizationResult(false, null, T("payment.card.squareMissingCheckoutId", "Square checkout did not return an id."));
                }
            }

            LogSquare($"checkout create succeeded checkoutId={checkoutId} status={LogValue(lastLoggedStatus)}");

            while (true)
            {
                timeoutCts.Token.ThrowIfCancellationRequested();
                pollCount++;
                var getResult = await SendSquareWithTokenRefreshAsync(
                    currentSettings,
                    HttpMethod.Get,
                    $"terminals/checkouts/{Uri.EscapeDataString(checkoutId)}",
                    body: null,
                    allowRefresh: !hasRefreshedToken,
                    cancellationToken: timeoutCts.Token);
                currentSettings = getResult.Settings;
                hasRefreshedToken = hasRefreshedToken || getResult.Refreshed;
                using var getResponse = getResult.Response;
                var getBody = await getResponse.Content.ReadAsStringAsync(timeoutCts.Token);
                if (!getResponse.IsSuccessStatusCode)
                {
                    LogSquare($"checkout poll failed checkoutId={checkoutId} poll={pollCount} http={(int)getResponse.StatusCode} detail={LogValue(ReadSquareErrorMessage(getBody))}");
                    return FailSquareRequest("checkout status", getResponse.StatusCode, getBody);
                }

                using var getDocument = JsonDocument.Parse(getBody);
                var currentCheckout = ReadRequiredCheckout(getDocument.RootElement);
                var status = ReadRequiredString(currentCheckout, "status");
                if (!string.Equals(lastLoggedStatus, status, StringComparison.OrdinalIgnoreCase))
                {
                    LogSquare($"checkout status checkoutId={checkoutId} poll={pollCount} status={status}");
                    lastLoggedStatus = status;
                }

                if (string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
                {
                    var authorizedAmount = ReadAmount(currentCheckout) ?? amount;
                    if (authorizedAmount != amount)
                    {
                        LogSquare($"checkout amount mismatch checkoutId={checkoutId} requested={amount:0.00} authorized={authorizedAmount:0.00}");
                        return new PaymentAuthorizationResult(false, null, T("payment.card.squareAmountMismatch", "Square authorized amount did not match the requested amount."));
                    }

                    var paymentId = ReadFirstPaymentId(currentCheckout) ?? checkoutId;
                    LogSquare($"checkout completed checkoutId={checkoutId} paymentId={paymentId} amount={authorizedAmount:0.00}");
                    return new PaymentAuthorizationResult(
                        true,
                        $"SQ:{paymentId}",
                        "Square",
                        authorizedAmount,
                        [
                            new CardTransactionDto(
                                "Square",
                                paymentId,
                                null,
                                null,
                                null,
                                null,
                                null,
                                null,
                                status,
                                null,
                                DateTimeOffset.UtcNow,
                                authorizedAmount,
                                null)
                        ]);
                }

                if (string.Equals(status, "CANCELED", StringComparison.OrdinalIgnoreCase))
                {
                    LogSquare($"checkout canceled checkoutId={checkoutId} poll={pollCount}");
                    return new PaymentAuthorizationResult(false, null, T("payment.card.squareCanceled", "Square checkout was canceled."));
                }

                if (!IsSquarePendingStatus(status))
                {
                    LogSquare($"checkout entered unexpected status checkoutId={checkoutId} poll={pollCount} status={status}");
                    return new PaymentAuthorizationResult(
                        false,
                        null,
                        string.Format(
                            _localization?.CurrentCulture ?? CultureInfo.CurrentCulture,
                            T("payment.card.squareUnexpectedStatus", "Square checkout entered unexpected status '{0}'."),
                            status));
                }

                await Task.Delay(SquarePollInterval, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            var cancellationReason = cancellationToken.IsCancellationRequested ? "caller-cancelled" : "local-timeout";
            LogSquare($"authorize canceled checkoutId={LogValue(checkoutId)} reason={cancellationReason}; starting cleanup");
            await CleanupSquareCheckoutBestEffortAsync(
                currentSettings,
                checkoutId,
                allowRefresh: !hasRefreshedToken);
            throw;
        }
        catch (HttpRequestException ex)
        {
            LogSquare($"authorize network failure checkoutId={LogValue(checkoutId)} message={LogValue(ex.Message)}");
            return new PaymentAuthorizationResult(false, null, T("payment.card.squareCommunicationFailed", "Square terminal communication failed."));
        }
        catch (JsonException ex)
        {
            LogSquare($"authorize invalid response checkoutId={LogValue(checkoutId)} message={LogValue(ex.Message)}");
            return new PaymentAuthorizationResult(false, null, T("payment.card.squareInvalidResponse", "Square terminal returned an invalid response."));
        }
    }

    private async Task<PaymentAuthorizationResult> RefundSquareAsync(
        CardTerminalSettings settings,
        decimal amount,
        string? originalReference,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.SquareAccessToken))
        {
            return new PaymentAuthorizationResult(false, null, T("payment.card.squareConfigIncomplete", "Square terminal configuration is incomplete."));
        }

        var paymentId = TryParseSquarePaymentId(originalReference);
        if (string.IsNullOrWhiteSpace(paymentId))
        {
            return new PaymentAuthorizationResult(false, null, T("payment.card.squareRefundMissingReference", "Square refund requires an original Square payment reference."));
        }

        try
        {
            var minorAmount = ToMinorUnits(amount);
            var refundAttemptKey = new SquareRefundAttemptKey(settings.Environment, paymentId, minorAmount);
            var request = new
            {
                idempotency_key = _squareRefundIdempotencyKeys.GetOrAdd(refundAttemptKey, _ => Guid.NewGuid().ToString("N")),
                payment_id = paymentId,
                amount_money = new
                {
                    amount = minorAmount,
                    currency = "AUD"
                }
            };

            var sendResult = await SendSquareWithTokenRefreshAsync(
                settings,
                HttpMethod.Post,
                "refunds",
                request,
                allowRefresh: true,
                cancellationToken);
            using var response = sendResult.Response;
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _squareRefundIdempotencyKeys.TryRemove(refundAttemptKey, out _);
                return FailSquareRequest("refund", response.StatusCode, body);
            }

            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("refund", out var refundElement) ||
                refundElement.ValueKind != JsonValueKind.Object)
            {
                return new PaymentAuthorizationResult(false, null, T("payment.card.squareInvalidResponse", "Square terminal returned an invalid response."));
            }

            var refundId = ReadRequiredString(refundElement, "id");
            var status = ReadRequiredString(refundElement, "status");
            if (string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "REJECTED", StringComparison.OrdinalIgnoreCase))
            {
                _squareRefundIdempotencyKeys.TryRemove(refundAttemptKey, out _);
                return new PaymentAuthorizationResult(
                    false,
                    $"SQRF:{refundId}",
                    string.Format(
                        _localization?.CurrentCulture ?? CultureInfo.CurrentCulture,
                        T("payment.card.squareRefundStatus", "Square refund status is {0}."),
                        status));
            }

            _squareRefundIdempotencyKeys.TryRemove(refundAttemptKey, out _);
            return new PaymentAuthorizationResult(
                true,
                $"SQRF:{refundId}",
                status,
                amount,
                [
                    new CardTransactionDto(
                        "Square",
                        refundId,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        status,
                        null,
                        DateTimeOffset.UtcNow,
                        amount,
                        null)
                ]);
        }
        catch (HttpRequestException)
        {
            return new PaymentAuthorizationResult(false, null, T("payment.card.squareCommunicationFailed", "Square terminal communication failed."));
        }
        catch (JsonException)
        {
            return new PaymentAuthorizationResult(false, null, T("payment.card.squareInvalidResponse", "Square terminal returned an invalid response."));
        }
    }

    private string T(string key, string fallback)
    {
        return _localization?.T(key) ?? fallback;
    }

    private async Task<SquareSendResult> SendSquareWithTokenRefreshAsync(
        CardTerminalSettings settings,
        HttpMethod method,
        string relativeUrl,
        object? body,
        bool allowRefresh,
        CancellationToken cancellationToken)
    {
        var response = await SendSquareAsync(settings, method, relativeUrl, body, cancellationToken);
        if (!allowRefresh || !IsSquareAuthenticationFailure(response) || _squareAccessTokenProvider is null)
        {
            return new SquareSendResult(response, settings, false);
        }

        LogSquare($"auth refresh requested method={method.Method} path={relativeUrl} environment={settings.Environment}");
        response.Dispose();
        var refreshedToken = await _squareAccessTokenProvider.GetSquareAccessTokenAsync(
            settings.Environment,
            forceRefresh: true,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(refreshedToken))
        {
            LogSquare($"auth refresh failed method={method.Method} path={relativeUrl} reason=empty-token");
            return new SquareSendResult(
                new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized),
                settings,
                false);
        }

        var refreshedSettings = settings with { SquareAccessToken = refreshedToken };
        LogSquare($"auth refresh succeeded method={method.Method} path={relativeUrl}");
        return new SquareSendResult(
            await SendSquareAsync(refreshedSettings, method, relativeUrl, body, cancellationToken),
            refreshedSettings,
            true);
    }

    private async Task<HttpResponseMessage> SendSquareAsync(
        CardTerminalSettings settings,
        HttpMethod method,
        string relativeUrl,
        object? body,
        CancellationToken cancellationToken)
    {
        var baseUri = settings.SquareApiBaseUrl.EndsWith("/")
            ? new Uri(settings.SquareApiBaseUrl, UriKind.Absolute)
            : new Uri(settings.SquareApiBaseUrl + "/", UriKind.Absolute);
        using var request = new HttpRequestMessage(method, new Uri(baseUri, relativeUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.SquareAccessToken);
        request.Headers.Add("Square-Version", CardTerminalSettings.SquareVersion);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        return await _httpClient.SendAsync(request, cancellationToken);
    }

    private async Task CleanupSquareCheckoutBestEffortAsync(
        CardTerminalSettings settings,
        string? checkoutId,
        bool allowRefresh)
    {
        if (string.IsNullOrWhiteSpace(checkoutId))
        {
            LogSquare("cleanup skipped because checkout id is empty");
            return;
        }

        LogSquare($"cleanup start checkoutId={checkoutId} allowRefresh={allowRefresh}");
        using var cleanupCts = new CancellationTokenSource(SquareCleanupTimeout);
        var cancelResult = await CancelSquareCheckoutBestEffortAsync(
            settings,
            checkoutId,
            allowRefresh,
            cleanupCts.Token);
        if (cancelResult.ShouldDismiss)
        {
            LogSquare($"cleanup dismiss required checkoutId={checkoutId}");
            await DismissSquareCheckoutBestEffortAsync(
                cancelResult.Settings,
                checkoutId,
                allowRefresh: allowRefresh && !cancelResult.Refreshed,
                cleanupCts.Token);
        }
        else
        {
            LogSquare($"cleanup finished without dismiss checkoutId={checkoutId}");
        }
    }

    private async Task<SquareCleanupResult> CancelSquareCheckoutBestEffortAsync(
        CardTerminalSettings settings,
        string checkoutId,
        bool allowRefresh,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await SendSquareWithTokenRefreshAsync(
                settings,
                HttpMethod.Post,
                $"terminals/checkouts/{Uri.EscapeDataString(checkoutId)}/cancel",
                body: null,
                allowRefresh,
                cancellationToken);
            var updatedSettings = result.Settings;
            var refreshed = result.Refreshed;
            using var response = result.Response;
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogSquare($"checkout cancel failed checkoutId={checkoutId} http={(int)response.StatusCode} detail={LogValue(ReadSquareErrorMessage(body))}");
                return new SquareCleanupResult(updatedSettings, refreshed, true);
            }

            var status = TryReadSquareCheckoutStatus(body);
            LogSquare($"checkout cancel result checkoutId={checkoutId} status={LogValue(status)} shouldDismiss={!IsSquareTerminalStatusFinal(status)}");
            return new SquareCleanupResult(
                updatedSettings,
                refreshed,
                !IsSquareTerminalStatusFinal(status));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            LogSquare($"checkout cancel exception checkoutId={checkoutId} message={LogValue(ex.Message)}");
            return new SquareCleanupResult(settings, Refreshed: false, ShouldDismiss: true);
        }
    }

    private async Task DismissSquareCheckoutBestEffortAsync(
        CardTerminalSettings settings,
        string checkoutId,
        bool allowRefresh,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await SendSquareWithTokenRefreshAsync(
                settings,
                HttpMethod.Post,
                $"terminals/checkouts/{Uri.EscapeDataString(checkoutId)}/dismiss",
                body: null,
                allowRefresh,
                cancellationToken);
            using var response = result.Response;
            _ = await response.Content.ReadAsStringAsync(cancellationToken);
            LogSquare($"checkout dismiss result checkoutId={checkoutId} http={(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            LogSquare($"checkout dismiss exception checkoutId={checkoutId} message={LogValue(ex.Message)}");
        }
    }

    private static PaymentAuthorizationResult FailSquareRequest(
        string operation,
        System.Net.HttpStatusCode statusCode,
        string? responseBody)
    {
        var detail = ReadSquareErrorMessage(responseBody);
        var message = string.IsNullOrWhiteSpace(detail)
            ? $"Square {operation} failed with HTTP {(int)statusCode}."
            : $"Square {operation} failed with HTTP {(int)statusCode}: {detail}";
        return new PaymentAuthorizationResult(false, null, message);
    }

    private static string? ReadSquareErrorMessage(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("errors", out var errorsElement) ||
                errorsElement.ValueKind != JsonValueKind.Array ||
                errorsElement.GetArrayLength() == 0)
            {
                return null;
            }

            var error = errorsElement[0];
            var code = error.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String
                ? codeElement.GetString()
                : null;
            var detail = error.TryGetProperty("detail", out var detailElement) && detailElement.ValueKind == JsonValueKind.String
                ? detailElement.GetString()
                : null;

            return string.IsNullOrWhiteSpace(code)
                ? detail
                : string.IsNullOrWhiteSpace(detail)
                    ? code
                    : $"{code}: {detail}";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryReadSquareCheckoutStatus(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            return document.RootElement.TryGetProperty("checkout", out var checkoutElement)
                ? ReadOptionalString(checkoutElement, "status")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement ReadRequiredCheckout(JsonElement root)
    {
        if (!root.TryGetProperty("checkout", out var checkoutElement) || checkoutElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Square response is missing required object 'checkout'.");
        }

        return checkoutElement;
    }

    private static long ToMinorUnits(decimal amount)
    {
        return decimal.ToInt64(decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));
    }

    private static decimal? ReadAmount(JsonElement checkout)
    {
        return checkout.TryGetProperty("amount_money", out var money) &&
            money.TryGetProperty("amount", out var amount) &&
            amount.TryGetInt64(out var minorUnits)
                ? minorUnits / 100m
                : null;
    }

    private static string? ReadFirstPaymentId(JsonElement checkout)
    {
        return checkout.TryGetProperty("payment_ids", out var paymentIds) &&
            paymentIds.ValueKind == JsonValueKind.Array &&
            paymentIds.GetArrayLength() > 0
                ? paymentIds[0].GetString()
                : null;
    }

    private static string? TryParseSquarePaymentId(string? originalReference)
    {
        if (string.IsNullOrWhiteSpace(originalReference))
        {
            return null;
        }

        var trimmed = originalReference.Trim();
        return trimmed.StartsWith("SQ:", StringComparison.OrdinalIgnoreCase)
            ? trimmed[3..].Trim()
            : null;
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        var value = ReadOptionalString(element, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new JsonException($"Square response is missing required property '{propertyName}'.");
        }

        return value;
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var propertyElement) &&
            propertyElement.ValueKind == JsonValueKind.String
                ? propertyElement.GetString()
                : null;
    }

    private static string Limit(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static bool IsSquarePendingStatus(string status)
    {
        return string.Equals(status, "PENDING", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "IN_PROGRESS", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "CANCEL_REQUESTED", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSquareTerminalStatusFinal(string? status)
    {
        return string.Equals(status, "CANCELED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSquareAuthenticationFailure(HttpResponseMessage response)
    {
        return response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden;
    }

    private static void LogSquare(string message)
    {
        ConsoleLog.Write("Square", message);
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value;
    }

    private sealed record SquareSendResult(
        HttpResponseMessage Response,
        CardTerminalSettings Settings,
        bool Refreshed);

    private sealed record SquareRefundAttemptKey(
        CardTerminalEnvironment Environment,
        string PaymentId,
        long MinorAmount);

    private sealed record SquareCleanupResult(
        CardTerminalSettings Settings,
        bool Refreshed,
        bool ShouldDismiss);
}
