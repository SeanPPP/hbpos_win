using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class SquareTerminalSetupClientTests
{
    private const string TestAccessToken = "opaque-square-setup-token";

    [Fact]
    public async Task ListLocationsAsync_UsesProductionEndpointAndParsesLocations()
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
                      "locations": [
                        { "id": "LOC-1", "name": "Main Store", "status": "ACTIVE" },
                        { "id": "LOC-2", "name": "Outlet", "status": "INACTIVE" }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var client = new SquareTerminalSetupClient(new HttpClient(handler));

        var result = await client.ListLocationsAsync(TestAccessToken, CardTerminalEnvironment.Production);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Get, capturedRequest!.Method);
        Assert.Equal("https://connect.squareup.com/v2/locations", capturedRequest.RequestUri!.AbsoluteUri);
        Assert.True(HasBearerToken(capturedRequest, TestAccessToken), "locations request should include a Square bearer token");
        Assert.Equal([CardTerminalSettings.SquareVersion], capturedRequest.Headers.GetValues("Square-Version"));
        Assert.Collection(
            result,
            location =>
            {
                Assert.Equal("LOC-1", location.Id);
                Assert.Equal("Main Store", location.Name);
            },
            location =>
            {
                Assert.Equal("LOC-2", location.Id);
                Assert.Equal("Outlet", location.Name);
            });
    }

    [Fact]
    public async Task ListDevicesAsync_UsesSandboxEndpointAndParsesDevices()
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
                      "devices": [
                        {
                          "id": "device:123",
                          "attributes": {
                            "name": "Counter Terminal",
                            "manufacturer": "Square",
                            "model": "T2",
                            "type": "TERMINAL"
                          },
                          "status": {
                            "category": "AVAILABLE"
                          }
                        },
                        {
                          "id": "device:456",
                          "attributes": {
                            "name": "Spare Terminal",
                            "manufacturer": "Square",
                            "model": "Handheld",
                            "type": "HANDHELD"
                          },
                          "status": {
                            "category": "OFFLINE"
                          }
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var client = new SquareTerminalSetupClient(new HttpClient(handler));

        var result = await client.ListDevicesAsync(TestAccessToken, CardTerminalEnvironment.Sandbox, "LOC/ 01");

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Get, capturedRequest!.Method);
        Assert.Equal("https://connect.squareupsandbox.com/v2/devices?location_id=LOC%2F%2001", capturedRequest.RequestUri!.AbsoluteUri);
        Assert.True(HasBearerToken(capturedRequest, TestAccessToken), "devices request should include a Square bearer token");
        Assert.Equal([CardTerminalSettings.SquareVersion], capturedRequest.Headers.GetValues("Square-Version"));
        Assert.Collection(
            result,
            device =>
            {
                Assert.Equal("device:123", device.Id);
                Assert.Equal("Counter Terminal", device.Name);
                Assert.Equal("AVAILABLE", device.Status);
            },
            device =>
            {
                Assert.Equal("device:456", device.Id);
                Assert.Equal("Spare Terminal", device.Name);
                Assert.Equal("OFFLINE", device.Status);
            });
    }

    [Fact]
    public async Task ListLocationsAsync_SanitizesErrorMessages()
    {
        const string accessToken = TestAccessToken;
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            ReasonPhrase = "Unauthorized",
            Content = new StringContent("{ \"errors\": [{ \"detail\": \"bad token " + accessToken + "\" }] }", Encoding.UTF8, "application/json")
        });

        var client = new SquareTerminalSetupClient(new HttpClient(handler));
        var exception = await Assert.ThrowsAsync<SquareApiException>(() =>
            client.ListLocationsAsync(accessToken, CardTerminalEnvironment.Production));

        Assert.Contains("401", exception.Message);
        Assert.True(
            !exception.Message.Contains(accessToken, StringComparison.Ordinal),
            "Square error messages should not include access tokens");
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

    private static bool HasBearerToken(HttpRequestMessage request, string expectedToken)
    {
        return request.Headers.Authorization?.Scheme == "Bearer" &&
            string.Equals(request.Headers.Authorization.Parameter, expectedToken, StringComparison.Ordinal);
    }
}
