using System.Net;
using System.Text;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class SquareTokenApiClientTests
{
    private const string BackendToken = "opaque-backend-square-token";

    [Fact]
    public async Task GetTokenAsync_reads_wrapped_backend_token()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Production",
                        "accessToken": "opaque-backend-square-token",
                        "updatedAt": "2026-05-26T00:00:00Z"
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var client = new SquareTokenApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://hbpos.example/")
        });

        var token = await client.GetTokenAsync(CardTerminalEnvironment.Production);

        Assert.True(
            string.Equals(BackendToken, token.AccessToken, StringComparison.Ordinal),
            "Square token API client should return the backend token");
        Assert.NotNull(capturedRequest);
        Assert.Equal("https://hbpos.example/api/v1/square/token?environment=Production", capturedRequest!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task GetTokenAsync_throws_catalog_exception_for_failure_response()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                """
                {
                  "success": false,
                  "errorCode": "SQUARE_TOKEN_NOT_CONFIGURED",
                  "message": "Square token is not configured for this environment: opaque-backend-square-token."
                }
                """,
                Encoding.UTF8,
                "application/json")
        });
        var client = new SquareTokenApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://hbpos.example/")
        });

        var exception = await Assert.ThrowsAsync<CatalogApiException>(() =>
            client.GetTokenAsync(CardTerminalEnvironment.Sandbox));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Equal("SQUARE_TOKEN_NOT_CONFIGURED", exception.ErrorCode);
        Assert.True(
            !exception.Message.Contains(BackendToken, StringComparison.Ordinal),
            "Square token API exception should not include backend-provided token values");
    }

    [Fact]
    public async Task GetTokenAsync_sanitizes_success_false_message()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "success": false,
                  "errorCode": "SQUARE_TOKEN_READ_FAILED",
                  "message": "unexpected token detail: opaque-backend-square-token"
                }
                """,
                Encoding.UTF8,
                "application/json")
        });
        var client = new SquareTokenApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://hbpos.example/")
        });

        var exception = await Assert.ThrowsAsync<CatalogApiException>(() =>
            client.GetTokenAsync(CardTerminalEnvironment.Production));

        Assert.Equal("SQUARE_TOKEN_READ_FAILED", exception.ErrorCode);
        Assert.True(
            !exception.Message.Contains(BackendToken, StringComparison.Ordinal),
            "Square token API exception should not include backend-provided token values");
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request, cancellationToken));
        }
    }
}
