using System.Net;
using System.Text;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class LinklyCloudCredentialApiClientTests
{
    [Fact]
    public async Task GetCredentialAsync_sends_requested_environment_to_backend()
    {
        HttpRequestMessage? capturedRequest = null;
        var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return JsonResponse(
                """
                {
                  "success": true,
                  "data": {
                    "storeCode": "S01",
                    "environment": "Sandbox",
                    "username": "store-user",
                    "password": "store-password",
                    "updatedAt": "2026-05-28T04:00:00Z"
                  }
                }
                """);
        }))
        {
            BaseAddress = new Uri("https://pos.example/")
        };
        var client = new LinklyCloudCredentialApiClient(httpClient);

        var credential = await client.GetCredentialAsync(CardTerminalEnvironment.Sandbox);

        Assert.NotNull(capturedRequest);
        Assert.Equal(
            "https://pos.example/api/v1/linkly/cloud-credential?environment=Sandbox",
            capturedRequest!.RequestUri!.AbsoluteUri);
        Assert.Equal("S01", credential.StoreCode);
        Assert.Equal("Sandbox", credential.Environment);
        Assert.Equal("store-user", credential.Username);
        Assert.Equal("store-password", credential.Password);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
