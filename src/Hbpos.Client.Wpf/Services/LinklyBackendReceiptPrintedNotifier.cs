using System.Net.Http;
using System.Net.Http.Json;
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
        using var response = await httpClient.PostAsJsonAsync(
            $"api/v1/linkly/cloud-backend/transactions/{Uri.EscapeDataString(sessionId)}/receipt/printed",
            request,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
