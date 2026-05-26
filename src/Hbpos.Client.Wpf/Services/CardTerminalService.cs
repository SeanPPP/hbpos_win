using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
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

public sealed record CardTerminalConfiguration(
    CardProcessorKind Processor,
    CardTerminalEnvironment Environment,
    string LinklyHost,
    int LinklyPort,
    string? SquareLocationId,
    string? SquareDeviceId,
    bool HasProtectedSquareAccessToken,
    int TerminalTimeoutSeconds)
{
    public static CardTerminalConfiguration Default { get; } = new(
        CardProcessorKind.None,
        CardTerminalEnvironment.Production,
        "127.0.0.1",
        2011,
        null,
        null,
        false,
        90);
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
    TimeSpan TerminalTimeout)
{
    public const string SquareVersion = "2026-01-22";

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
                    : 90));
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

public interface ICardTerminalSettingsStore : ICardTerminalSettingsProvider, ISquareTokenResolver
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

    private readonly ICardTerminalSettingsProvider _settingsProvider;
    private readonly HttpClient _httpClient;
    private readonly ILinklyTerminalClient? _linklyTerminalClient;
    private readonly ISquareAccessTokenProvider? _squareAccessTokenProvider;

    public ConfiguredCardTerminalClient(
        ICardTerminalSettingsProvider settingsProvider,
        HttpClient httpClient,
        ILinklyTerminalClient? linklyTerminalClient = null,
        ISquareAccessTokenProvider? squareAccessTokenProvider = null)
    {
        _settingsProvider = settingsProvider;
        _httpClient = httpClient;
        _linklyTerminalClient = linklyTerminalClient;
        _squareAccessTokenProvider = squareAccessTokenProvider;
    }

    public async Task<PaymentAuthorizationResult> AuthorizeAsync(
        decimal amount,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        if (amount <= 0m)
        {
            return new PaymentAuthorizationResult(false, null, "Card amount must be greater than zero.");
        }

        var settings = await _settingsProvider.GetSettingsAsync(cancellationToken);
        return settings.Processor switch
        {
            CardProcessorKind.Linkly => _linklyTerminalClient is null
                ? new PaymentAuthorizationResult(false, null, "ANZ Linkly terminal adapter is unavailable.")
                : await _linklyTerminalClient.PurchaseAsync(amount, session, settings, cancellationToken),
            CardProcessorKind.Square => await AuthorizeSquareAsync(settings, amount, session, cancellationToken),
            _ => new PaymentAuthorizationResult(false, null, "Card terminal is not configured.")
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
            return new PaymentAuthorizationResult(false, null, "Square terminal configuration is incomplete.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(settings.TerminalTimeout);

        var currentSettings = settings;
        var hasRefreshedToken = false;
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
                    device_id = settings.SquareDeviceId
                },
                location_id = settings.SquareLocationId,
                reference_id = reference,
                note = Limit($"HBPOS {session.StoreCode} {session.DeviceCode}", 500)
            }
        };

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
            return new PaymentAuthorizationResult(false, null, $"Square checkout failed with HTTP {(int)createResponse.StatusCode}.");
        }

        using var createDocument = JsonDocument.Parse(createBody);
        var checkout = createDocument.RootElement.GetProperty("checkout");
        var checkoutId = checkout.GetProperty("id").GetString();
        if (string.IsNullOrWhiteSpace(checkoutId))
        {
            return new PaymentAuthorizationResult(false, null, "Square checkout did not return an id.");
        }

        while (true)
        {
            timeoutCts.Token.ThrowIfCancellationRequested();
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
                return new PaymentAuthorizationResult(false, null, $"Square checkout status failed with HTTP {(int)getResponse.StatusCode}.");
            }

            using var getDocument = JsonDocument.Parse(getBody);
            var currentCheckout = getDocument.RootElement.GetProperty("checkout");
            var status = currentCheckout.GetProperty("status").GetString();
            if (string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
            {
                var authorizedAmount = ReadAmount(currentCheckout) ?? amount;
                if (authorizedAmount != amount)
                {
                    return new PaymentAuthorizationResult(false, null, "Square authorized amount did not match the requested amount.");
                }

                var paymentId = ReadFirstPaymentId(currentCheckout) ?? checkoutId;
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
                return new PaymentAuthorizationResult(false, null, "Square checkout was canceled.");
            }

            await Task.Delay(SquarePollInterval, timeoutCts.Token);
        }
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

        response.Dispose();
        var refreshedToken = await _squareAccessTokenProvider.GetSquareAccessTokenAsync(
            settings.Environment,
            forceRefresh: true,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(refreshedToken))
        {
            return new SquareSendResult(
                new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized),
                settings,
                false);
        }

        var refreshedSettings = settings with { SquareAccessToken = refreshedToken };
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

    private static string Limit(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static bool IsSquareAuthenticationFailure(HttpResponseMessage response)
    {
        return response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden;
    }

    private sealed record SquareSendResult(
        HttpResponseMessage Response,
        CardTerminalSettings Settings,
        bool Refreshed);
}
