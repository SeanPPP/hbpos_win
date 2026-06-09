using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Hbpos.Contracts.Linkly;

namespace Hbpos.Client.Wpf.Services;

public sealed class LinklyBackendReceiptPrintedNotifier(IHttpClientFactory httpClientFactory) : ICardReceiptPrintedNotifier
{
    public const string HttpClientName = nameof(LinklyBackendReceiptPrintedNotifier);

    public async Task MarkReceiptPrintedAsync(
        string environment,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var httpClient = httpClientFactory.CreateClient(HttpClientName);
        var request = new LinklyCloudBackendMarkReceiptPrintedRequest(environment);
        const string operation = "receipt/printed";
        var relativeUrl = $"api/v1/linkly/cloud-backend/transactions/{Uri.EscapeDataString(sessionId)}/receipt/printed";
        var absoluteUrl = httpClient.BaseAddress is null
            ? relativeUrl
            : new Uri(httpClient.BaseAddress, relativeUrl).ToString();
        var requestJson = JsonSerializer.Serialize(request);
        var evidenceEnvironment = Enum.TryParse<CardTerminalEnvironment>(environment, ignoreCase: true, out var parsedEnvironment)
            ? parsedEnvironment
            : (CardTerminalEnvironment?)null;
        LinklyJsonLog.Write(
            "LinklyBackend",
            "backend-terminal",
            operation,
            "request",
            direction: "request",
            environment: evidenceEnvironment,
            sessionId: sessionId,
            request: new
            {
                method = HttpMethod.Post.Method,
                url = absoluteUrl,
                body = request
            },
            details: new
            {
                timestamp = DateTimeOffset.Now,
                certCase = "4.1.3",
                method = HttpMethod.Post.Method,
                url = absoluteUrl,
                transactionReference = sessionId,
                requestJson,
                responseJson = (string?)null
            });

        using var response = await httpClient.PostAsJsonAsync(
            relativeUrl,
            request,
            cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        LinklyJsonLog.Write(
            "LinklyBackend",
            "backend-terminal",
            operation,
            "response",
            direction: "response",
            environment: evidenceEnvironment,
            sessionId: sessionId,
            httpStatus: response.StatusCode,
            success: response.IsSuccessStatusCode,
            reason: response.IsSuccessStatusCode ? null : "receipt-printed-marker-failed",
            response: new
            {
                method = HttpMethod.Post.Method,
                url = absoluteUrl,
                body = string.IsNullOrWhiteSpace(responseJson) ? null : responseJson
            },
            details: new
            {
                timestamp = DateTimeOffset.Now,
                certCase = "4.1.3",
                method = HttpMethod.Post.Method,
                url = absoluteUrl,
                transactionReference = sessionId,
                requestJson = (string?)null,
                responseJson
            });
        response.EnsureSuccessStatusCode();
    }
}
