using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hbpos.Client.Wpf.Services;

public interface ILinklyCloudApiClient
{
    Task<string> PairAsync(
        string authBaseUrl,
        string username,
        string password,
        string pairCode,
        CancellationToken cancellationToken = default);

    Task<LinklyCloudToken> GetTokenAsync(
        CardTerminalSettings settings,
        string posId,
        CancellationToken cancellationToken = default);

    Task<LinklyCloudStatusResult> SendStatusAsync(
        CardTerminalSettings settings,
        string token,
        CancellationToken cancellationToken = default);

    Task<LinklyCloudLogonResult> SendLogonAsync(
        CardTerminalSettings settings,
        string token,
        CancellationToken cancellationToken = default);

    Task<LinklyCloudTransactionResult> SendTransactionAsync(
        CardTerminalSettings settings,
        string token,
        LinklyCloudTransactionRequest request,
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<LinklyCloudTransactionResult> GetTransactionAsync(
        CardTerminalSettings settings,
        string token,
        string sessionId,
        CancellationToken cancellationToken = default);

    Task SendKeyAsync(
        CardTerminalSettings settings,
        string token,
        string sessionId,
        string key,
        string? data,
        CancellationToken cancellationToken = default);
}

public sealed class LinklyCloudApiClient(HttpClient httpClient) : ILinklyCloudApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions LinklyRequestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<string> PairAsync(
        string authBaseUrl,
        string username,
        string password,
        string pairCode,
        CancellationToken cancellationToken = default)
    {
        var requestBody = new LinklyCloudPairingRequest(username.Trim(), password.Trim(), pairCode.Trim());
        LogEvent(
            "pair",
            "request",
            direction: "request",
            request: requestBody,
            details: new
            {
                authBaseUrl,
                authHost = LogHost(authBaseUrl),
                username,
                password,
                pairCode
            });
        using var response = await httpClient.PostAsJsonAsync(
            new Uri(GetBaseUri(authBaseUrl), "pairing/cloudpos"),
            requestBody,
            JsonOptions,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        LogEvent(
            "pair",
            "response",
            direction: "response",
            httpStatus: response.StatusCode,
            success: response.IsSuccessStatusCode,
            response: RawJsonBody(body),
            details: new { authBaseUrl });
        EnsureSuccess(response, body, "Linkly Cloud pairing");

        using var document = JsonDocument.Parse(body);
        var secret = ReadString(document.RootElement, "secret");
        if (string.IsNullOrWhiteSpace(secret))
        {
            LogEvent(
                "pair",
                "invalid",
                direction: "response",
                httpStatus: response.StatusCode,
                success: false,
                reason: "missing-secret",
                response: RawJsonBody(body));
            throw new LinklyCloudApiException("Linkly Cloud pairing returned no secret.", response.StatusCode);
        }

        LogEvent(
            "pair",
            "succeeded",
            direction: "response",
            httpStatus: response.StatusCode,
            success: true,
            response: new
            {
                secret = secret.Trim(),
                body = RawJsonBody(body)
            });
        return secret.Trim();
    }

    public async Task<LinklyCloudToken> GetTokenAsync(
        CardTerminalSettings settings,
        string posId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.LinklyCloudSecret))
        {
            LogEvent("token", "blocked", environment: settings.Environment, success: false, reason: "missing-secret");
            throw new LinklyCloudApiException("Linkly Cloud secret is missing.");
        }

        if (string.IsNullOrWhiteSpace(settings.LinklyPosVendorId))
        {
            LogEvent("token", "blocked", environment: settings.Environment, success: false, reason: "missing-pos-vendor-id");
            throw new LinklyCloudApiException("Linkly POS vendor id is not configured.");
        }

        if (!IsUuidV4(posId))
        {
            LogEvent("token", "blocked", environment: settings.Environment, success: false, reason: "invalid-pos-id", details: new { posId });
            throw new LinklyCloudApiException("Linkly POS id must be a UUID v4.");
        }

        if (!IsUuidV4(settings.LinklyPosVendorId))
        {
            LogEvent("token", "blocked", environment: settings.Environment, success: false, reason: "invalid-pos-vendor-id", details: new { settings.LinklyPosVendorId });
            throw new LinklyCloudApiException("Linkly POS vendor id must be a UUID v4.");
        }

        var requestBody = new LinklyCloudTokenRequest(
            settings.LinklyCloudSecret.Trim(),
            settings.LinklyPosName,
            settings.LinklyPosVersion,
            posId,
            settings.LinklyPosVendorId.Trim());
        LogEvent(
            "token",
            "request",
            direction: "request",
            environment: settings.Environment,
            request: requestBody,
            details: new
            {
                authBaseUrl = settings.LinklyCloudAuthBaseUrl,
                authHost = LogHost(settings.LinklyCloudAuthBaseUrl),
                posId
            });
        using var response = await httpClient.PostAsJsonAsync(
            new Uri(GetBaseUri(settings.LinklyCloudAuthBaseUrl), "tokens/cloudpos"),
            requestBody,
            JsonOptions,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        LogEvent(
            "token",
            "response",
            direction: "response",
            environment: settings.Environment,
            httpStatus: response.StatusCode,
            success: response.IsSuccessStatusCode,
            response: RawJsonBody(body),
            details: new { posId });
        EnsureSuccess(response, body, "Linkly Cloud token request");

        using var document = JsonDocument.Parse(body);
        var token = ReadString(document.RootElement, "token");
        if (string.IsNullOrWhiteSpace(token))
        {
            LogEvent("token", "invalid", direction: "response", environment: settings.Environment, httpStatus: response.StatusCode, success: false, reason: "missing-token", response: RawJsonBody(body), details: new { posId });
            throw new LinklyCloudApiException("Linkly Cloud token response was missing a token.", response.StatusCode);
        }

        var expirySeconds = ReadInt(document.RootElement, "expirySeconds") ?? 0;
        LogEvent(
            "token",
            "succeeded",
            direction: "response",
            environment: settings.Environment,
            httpStatus: response.StatusCode,
            success: true,
            response: new
            {
                token = token.Trim(),
                expirySeconds = Math.Max(0, expirySeconds),
                body = RawJsonBody(body)
            },
            details: new { posId });
        return new LinklyCloudToken(
            token.Trim(),
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, expirySeconds)));
    }

    public async Task<LinklyCloudStatusResult> SendStatusAsync(
        CardTerminalSettings settings,
        string token,
        CancellationToken cancellationToken = default)
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var requestBody = new LinklyCloudApiRequest(new Dictionary<string, object?>
        {
            ["Merchant"] = "00",
            ["StatusType"] = "0"
        });
        LogEvent(
            "status",
            "request",
            direction: "request",
            environment: settings.Environment,
            sessionId: sessionId,
            request: requestBody,
            details: new
            {
                restBaseUrl = settings.LinklyCloudRestBaseUrl,
                token
            });
        using var response = await SendLinklyRequestAsync(
            settings,
            token,
            "status",
            HttpMethod.Post,
            requestBody,
            sessionId,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        LogEvent("status", "response", direction: "response", environment: settings.Environment, sessionId: sessionId, httpStatus: response.StatusCode, success: response.IsSuccessStatusCode, response: RawJsonBody(body));
        EnsureSuccess(response, body, "Linkly Cloud status request");

        using var document = JsonDocument.Parse(body);
        var result = ReadResponse(document.RootElement);
        var status = new LinklyCloudStatusResult(
            ReadBool(result, "Success") == true,
            ReadString(result, "ResponseCode"),
            ReadString(result, "ResponseText"),
            ReadBool(result, "LoggedOn"),
            ReadString(result, "Catid"),
            ReadString(result, "Caid"),
            ReadString(result, "PinPadSerialNumber"),
            ReadString(result, "PinPadVersion"));
        LogEvent("status", "parsed", direction: "response", environment: settings.Environment, sessionId: sessionId, success: status.Succeeded, response: status);
        return status;
    }

    public async Task<LinklyCloudLogonResult> SendLogonAsync(
        CardTerminalSettings settings,
        string token,
        CancellationToken cancellationToken = default)
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var requestBody = new LinklyCloudApiRequest(new Dictionary<string, object?>
        {
            ["Merchant"] = "00",
            ["LogonType"] = " ",
            ["Application"] = "00",
            ["ReceiptAutoPrint"] = "0",
            ["CutReceipt"] = "0"
        });
        LogEvent(
            "logon",
            "request",
            direction: "request",
            environment: settings.Environment,
            sessionId: sessionId,
            request: requestBody,
            details: new
            {
                restBaseUrl = settings.LinklyCloudRestBaseUrl,
                token
            });
        using var response = await SendLinklyRequestAsync(
            settings,
            token,
            "logon",
            HttpMethod.Post,
            requestBody,
            sessionId,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        LogEvent("logon", "response", direction: "response", environment: settings.Environment, sessionId: sessionId, httpStatus: response.StatusCode, success: response.IsSuccessStatusCode, response: RawJsonBody(body));
        EnsureSuccess(response, body, "Linkly Cloud logon request");

        using var document = JsonDocument.Parse(body);
        var result = ReadResponse(document.RootElement);
        var logon = new LinklyCloudLogonResult(
            ReadBool(result, "Success") == true,
            ReadString(result, "ResponseCode"),
            ReadString(result, "ResponseText"),
            ReadString(result, "Catid"),
            ReadString(result, "Caid"),
            ReadString(result, "PinPadVersion"));
        LogEvent("logon", "parsed", direction: "response", environment: settings.Environment, sessionId: sessionId, success: logon.Succeeded, response: logon);
        return logon;
    }

    public async Task<LinklyCloudTransactionResult> SendTransactionAsync(
        CardTerminalSettings settings,
        string token,
        LinklyCloudTransactionRequest request,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response;
        var requestBody = new LinklyCloudApiRequest(request.ToFields());
        LogEvent("transaction", "request", direction: "request", environment: settings.Environment, sessionId: sessionId, request: requestBody, details: new { token, request.TxnType, request.TxnRef, request.AmtPurchase });
        try
        {
            response = await SendLinklyRequestAsync(
                settings,
                token,
                "transaction",
                HttpMethod.Post,
                requestBody,
                sessionId,
                cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            LogEvent("transaction", "pending", direction: "request", environment: settings.Environment, sessionId: sessionId, success: false, reason: "http-request-exception", details: new { error = ex.GetType().Name, ex.Message });
            return PendingTransaction(sessionId);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Accepted ||
                response.StatusCode == HttpStatusCode.RequestTimeout ||
                (int)response.StatusCode >= 500)
            {
                LogEvent("transaction", "pending", direction: "response", environment: settings.Environment, sessionId: sessionId, httpStatus: response.StatusCode, success: response.IsSuccessStatusCode);
                return PendingTransaction(sessionId);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            LogEvent("transaction", "response", direction: "response", environment: settings.Environment, sessionId: sessionId, httpStatus: response.StatusCode, success: response.IsSuccessStatusCode, response: RawJsonBody(body));
            EnsureSuccess(response, body, "Linkly Cloud transaction request");
            var result = ParseTransactionResult(sessionId, body);
            LogEvent("transaction", "parsed", direction: "response", environment: settings.Environment, sessionId: sessionId, success: result.Succeeded, response: result);
            return result;
        }
    }

    public async Task<LinklyCloudTransactionResult> GetTransactionAsync(
        CardTerminalSettings settings,
        string token,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        LogEvent("transaction-status", "request", direction: "request", environment: settings.Environment, sessionId: sessionId, details: new { token });
        using var response = await SendLinklyRequestAsync(
            settings,
            token,
            "transaction",
            HttpMethod.Get,
            body: null,
            sessionId,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.Accepted ||
            response.StatusCode == HttpStatusCode.RequestTimeout ||
            (int)response.StatusCode >= 500)
        {
            LogEvent("transaction-status", "pending", direction: "response", environment: settings.Environment, sessionId: sessionId, httpStatus: response.StatusCode, success: response.IsSuccessStatusCode);
            return PendingTransaction(sessionId);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            LogEvent("transaction-status", "not-submitted", direction: "response", environment: settings.Environment, sessionId: sessionId, httpStatus: response.StatusCode, success: false);
            return NotSubmittedTransaction(sessionId);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        LogEvent("transaction-status", "response", direction: "response", environment: settings.Environment, sessionId: sessionId, httpStatus: response.StatusCode, success: response.IsSuccessStatusCode, response: RawJsonBody(body));
        EnsureSuccess(response, body, "Linkly Cloud transaction status request");
        var result = ParseTransactionResult(sessionId, body);
        LogEvent("transaction-status", "parsed", direction: "response", environment: settings.Environment, sessionId: sessionId, success: result.Succeeded, response: result);
        return result;
    }

    public async Task SendKeyAsync(
        CardTerminalSettings settings,
        string token,
        string sessionId,
        string key,
        string? data,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = LinklyTerminalDialogKeys.Normalize(key);
        var requestBody = new LinklyCloudApiRequest(new Dictionary<string, object?>
        {
            ["Key"] = normalizedKey,
            ["Data"] = string.IsNullOrWhiteSpace(data) ? null : data.Trim()
        });
        LogEvent("sendkey", "request", direction: "request", environment: settings.Environment, sessionId: sessionId, request: requestBody, details: new { token, key = normalizedKey, data });
        using var response = await SendLinklyRequestAsync(
            settings,
            token,
            "sendkey",
            HttpMethod.Post,
            requestBody,
            sessionId,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        LogEvent("sendkey", "response", direction: "response", environment: settings.Environment, sessionId: sessionId, httpStatus: response.StatusCode, success: response.IsSuccessStatusCode, response: RawJsonBody(body));
        EnsureSuccess(response, body, "Linkly Cloud sendkey request");
    }

    private async Task<HttpResponseMessage> SendLinklyRequestAsync(
        CardTerminalSettings settings,
        string token,
        string requestType,
        HttpMethod method,
        LinklyCloudApiRequest? body,
        string sessionId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            method,
            new Uri(GetBaseUri(settings.LinklyCloudRestBaseUrl), $"sessions/{Uri.EscapeDataString(sessionId)}/{requestType}?async=false"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: LinklyRequestJsonOptions);
        }

        return await httpClient.SendAsync(request, cancellationToken);
    }

    private static LinklyCloudTransactionResult PendingTransaction(string sessionId)
    {
        return new LinklyCloudTransactionResult(
            sessionId,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null)
        {
            Outcome = LinklyCloudTransactionOutcome.Pending
        };
    }

    private static LinklyCloudTransactionResult NotSubmittedTransaction(string sessionId)
    {
        return new LinklyCloudTransactionResult(
            sessionId,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null)
        {
            Outcome = LinklyCloudTransactionOutcome.NotSubmitted
        };
    }

    private static LinklyCloudTransactionResult ParseTransactionResult(string fallbackSessionId, string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var response = ReadResponse(root);
        var sessionId = ReadString(root, "SessionId") ?? ReadString(root, "sessionId") ?? fallbackSessionId;
        var amount = ReadDecimal(response, "AmtPurchase");
        var purchaseAnalysisData = ReadObject(response, "PurchaseAnalysisData");

        return new LinklyCloudTransactionResult(
            sessionId,
            ReadBool(response, "Success") == true,
            ReadString(response, "TxnRef"),
            ReadString(response, "AuthCode"),
            ReadString(response, "CardType"),
            ReadString(response, "CardName"),
            ReadString(response, "Pan"),
            ReadString(response, "Caid"),
            ReadString(response, "ResponseCode"),
            ReadString(response, "ResponseText"),
            ReadString(response, "Stan"),
            amount,
            ReadString(purchaseAnalysisData, "RFN"));
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
            JsonValueKind.String => Normalize(value.GetString()),
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

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var parsed) => parsed,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
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

    private static void EnsureSuccess(HttpResponseMessage response, string? body, string operation)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw new LinklyCloudApiException(
            $"{operation} failed with HTTP {(int)response.StatusCode}.",
            response.StatusCode,
            ReadErrorMessage(body));
    }

    private static string? ReadErrorMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return ReadString(document.RootElement, "message") ??
                ReadString(document.RootElement, "error") ??
                ReadString(document.RootElement, "detail");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Uri GetBaseUri(string baseUrl)
    {
        var value = string.IsNullOrWhiteSpace(baseUrl) ? CardTerminalSettings.GetLinklyCloudAuthBaseUrl(CardTerminalEnvironment.Production) : baseUrl.Trim();
        return new Uri(value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/", UriKind.Absolute);
    }

    private static void Log(string message)
    {
        LinklyJsonLog.WriteMessage("LinklyCloud", "cloud-api", message);
    }

    private static void LogEvent(
        string operation,
        string phase,
        string? direction = null,
        CardTerminalEnvironment? environment = null,
        string? sessionId = null,
        HttpStatusCode? httpStatus = null,
        bool? success = null,
        string? reason = null,
        object? request = null,
        object? response = null,
        object? details = null)
    {
        LinklyJsonLog.Write(
            "LinklyCloud",
            "cloud-api",
            operation,
            phase,
            direction,
            environment,
            sessionId,
            httpStatus,
            success,
            reason,
            request: request,
            response: response,
            details: details);
    }

    private static object? RawJsonBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return JsonSerializer.Deserialize<object>(document.RootElement.GetRawText(), JsonOptions);
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value.Trim();
    }

    private static string LogHost(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) ||
            !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return "<invalid>";
        }

        return uri.Host;
    }

    private static string ShortId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<null>";
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 8 ? trimmed : $"{trimmed[..8]}...";
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool IsUuidV4(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return Guid.TryParse(trimmed, out _) &&
            trimmed.Length == 36 &&
            trimmed[14] == '4' &&
            trimmed[19] is '8' or '9' or 'a' or 'A' or 'b' or 'B';
    }

    private sealed record LinklyCloudPairingRequest(
        [property: JsonPropertyName("username")] string Username,
        [property: JsonPropertyName("password")] string Password,
        [property: JsonPropertyName("pairCode")] string PairCode);

    private sealed record LinklyCloudTokenRequest(
        [property: JsonPropertyName("secret")] string Secret,
        [property: JsonPropertyName("posName")] string PosName,
        [property: JsonPropertyName("posVersion")] string PosVersion,
        [property: JsonPropertyName("posId")] string PosId,
        [property: JsonPropertyName("posVendorId")] string PosVendorId);
}

public sealed record LinklyCloudToken(string Token, DateTimeOffset ExpiresAt);

public sealed record LinklyCloudStatusResult(
    bool Succeeded,
    string? ResponseCode,
    string? ResponseText,
    bool? LoggedOn,
    string? Catid,
    string? Caid,
    string? PinPadSerialNumber,
    string? PinPadVersion);

public sealed record LinklyCloudLogonResult(
    bool Succeeded,
    string? ResponseCode,
    string? ResponseText,
    string? Catid,
    string? Caid,
    string? PinPadVersion);

public sealed record LinklyCloudTransactionRequest(
    string TxnType,
    long AmtPurchase,
    string TxnRef,
    IReadOnlyDictionary<string, string>? PurchaseAnalysisData = null)
{
    public Dictionary<string, object?> ToFields()
    {
        var fields = new Dictionary<string, object?>
        {
            ["Merchant"] = "00",
            ["Application"] = "00",
            ["TxnType"] = TxnType,
            ["AmtPurchase"] = AmtPurchase,
            ["TxnRef"] = TxnRef,
            ["CurrencyCode"] = "AUD",
            ["CutReceipt"] = "0",
            ["ReceiptAutoPrint"] = "0"
        };

        if (PurchaseAnalysisData is { Count: > 0 })
        {
            fields["PurchaseAnalysisData"] = PurchaseAnalysisData;
        }

        return fields;
    }
}

public sealed record LinklyCloudApiRequest(
    [property: JsonPropertyName("Request")] IReadOnlyDictionary<string, object?> Request);

public sealed record LinklyCloudTransactionResult(
    string SessionId,
    bool Succeeded,
    string? TxnRef,
    string? AuthCode,
    string? CardType,
    string? CardName,
    string? Pan,
    string? Caid,
    string? ResponseCode,
    string? ResponseText,
    string? Stan,
    decimal? Amount,
    string? RefundReference)
{
    public LinklyCloudTransactionOutcome Outcome { get; init; } = LinklyCloudTransactionOutcome.Completed;
}

public enum LinklyCloudTransactionOutcome
{
    Completed,
    Pending,
    NotSubmitted
}

public sealed class LinklyCloudApiException : Exception
{
    public LinklyCloudApiException(
        string message,
        HttpStatusCode? statusCode = null,
        string? detail = null)
        : base(string.IsNullOrWhiteSpace(detail) ? message : $"{message} {detail}")
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }

    public bool IsAuthenticationFailure => StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}
