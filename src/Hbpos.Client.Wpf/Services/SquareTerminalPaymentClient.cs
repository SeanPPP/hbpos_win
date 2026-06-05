using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Hbpos.Client.Wpf.Services;

public sealed record SquareCheckoutStatusResult(
    string CheckoutId,
    string Status,
    long? AmountCents,
    string? Currency,
    IReadOnlyList<string> PaymentIds,
    string? CancelReason);

public sealed record SquarePaymentStatusResult(
    string PaymentId,
    string Status,
    long AmountCents,
    string Currency);

public interface ISquareTerminalPaymentClient
{
    Task<SquareCheckoutStatusResult> GetCheckoutAsync(
        CardTerminalSettings settings,
        string checkoutId,
        CancellationToken cancellationToken = default);

    Task<SquarePaymentStatusResult> GetPaymentAsync(
        CardTerminalSettings settings,
        string paymentId,
        CancellationToken cancellationToken = default);
}

public sealed class SquareTerminalPaymentClient(
    HttpClient httpClient,
    ISquareAccessTokenProvider? squareAccessTokenProvider = null) : ISquareTerminalPaymentClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SquareCheckoutStatusResult> GetCheckoutAsync(
        CardTerminalSettings settings,
        string checkoutId,
        CancellationToken cancellationToken = default)
    {
        var body = await SendSquareAsync(
            settings,
            HttpMethod.Get,
            $"terminals/checkouts/{Uri.EscapeDataString(checkoutId)}",
            cancellationToken);
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("checkout", out var checkout) ||
            checkout.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Square response is missing required object 'checkout'.");
        }

        return new SquareCheckoutStatusResult(
            ReadRequiredString(checkout, "id"),
            ReadRequiredString(checkout, "status"),
            TryReadMoney(checkout, "amount_money", out var amountCents, out var currency) ? amountCents : null,
            currency,
            ReadPaymentIds(checkout),
            ReadOptionalString(checkout, "cancel_reason"));
    }

    public async Task<SquarePaymentStatusResult> GetPaymentAsync(
        CardTerminalSettings settings,
        string paymentId,
        CancellationToken cancellationToken = default)
    {
        var body = await SendSquareAsync(
            settings,
            HttpMethod.Get,
            $"payments/{Uri.EscapeDataString(paymentId)}",
            cancellationToken);
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("payment", out var payment) ||
            payment.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Square response is missing required object 'payment'.");
        }

        if (!TryReadMoney(payment, "amount_money", out var amountCents, out var currency))
        {
            throw new JsonException("Square payment is missing amount_money.");
        }

        return new SquarePaymentStatusResult(
            ReadRequiredString(payment, "id"),
            ReadRequiredString(payment, "status"),
            amountCents,
            currency!);
    }

    private async Task<string> SendSquareAsync(
        CardTerminalSettings settings,
        HttpMethod method,
        string relativeUrl,
        CancellationToken cancellationToken)
    {
        var response = await SendSquareOnceAsync(settings, method, relativeUrl, cancellationToken);
        if (IsSquareAuthenticationFailure(response) && squareAccessTokenProvider is not null)
        {
            response.Dispose();
            var refreshedToken = await squareAccessTokenProvider.GetSquareAccessTokenAsync(
                settings.Environment,
                forceRefresh: true,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(refreshedToken))
            {
                response = await SendSquareOnceAsync(settings with { SquareAccessToken = refreshedToken }, method, relativeUrl, cancellationToken);
            }
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(ReadSquareErrorMessage(body) ?? $"Square request failed with HTTP {(int)response.StatusCode}.");
            }

            return body;
        }
    }

    private async Task<HttpResponseMessage> SendSquareOnceAsync(
        CardTerminalSettings settings,
        HttpMethod method,
        string relativeUrl,
        CancellationToken cancellationToken)
    {
        var baseUri = settings.SquareApiBaseUrl.EndsWith("/")
            ? new Uri(settings.SquareApiBaseUrl, UriKind.Absolute)
            : new Uri(settings.SquareApiBaseUrl + "/", UriKind.Absolute);
        using var request = new HttpRequestMessage(method, new Uri(baseUri, $"v2/{relativeUrl}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.SquareAccessToken);
        request.Headers.Add("Square-Version", CardTerminalSettings.SquareVersion);
        return await httpClient.SendAsync(request, cancellationToken);
    }

    private static IReadOnlyList<string> ReadPaymentIds(JsonElement checkout)
    {
        if (!checkout.TryGetProperty("payment_ids", out var paymentIds) ||
            paymentIds.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return paymentIds
            .EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.String)
            .Select(element => element.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }

    private static bool TryReadMoney(JsonElement element, string propertyName, out long amountCents, out string? currency)
    {
        amountCents = 0;
        currency = null;
        if (!element.TryGetProperty(propertyName, out var money) || money.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!money.TryGetProperty("amount", out var amount) || !amount.TryGetInt64(out amountCents))
        {
            return false;
        }

        currency = ReadOptionalString(money, "currency");
        return !string.IsNullOrWhiteSpace(currency);
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        var value = ReadOptionalString(element, propertyName);
        return string.IsNullOrWhiteSpace(value)
            ? throw new JsonException($"Square response is missing required property '{propertyName}'.")
            : value;
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var propertyElement) &&
            propertyElement.ValueKind == JsonValueKind.String
                ? propertyElement.GetString()
                : null;
    }

    private static string? ReadSquareErrorMessage(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            var response = JsonSerializer.Deserialize<SquareErrorResponse>(responseBody, JsonOptions);
            var error = response?.Errors?.FirstOrDefault();
            if (error is null)
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(error.Code)
                ? error.Detail
                : string.IsNullOrWhiteSpace(error.Detail)
                    ? error.Code
                    : $"{error.Code}: {error.Detail}";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsSquareAuthenticationFailure(HttpResponseMessage response)
    {
        return response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden;
    }

    private sealed record SquareErrorResponse(IReadOnlyList<SquareError>? Errors);

    private sealed record SquareError(string? Code, string? Detail);
}
