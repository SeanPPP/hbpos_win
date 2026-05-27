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
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
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
            });
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
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
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
            });
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
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                ReasonPhrase = "Unauthorized",
                Content = new StringContent("{ \"errors\": [{ \"detail\": \"bad token " + accessToken + "\" }] }", Encoding.UTF8, "application/json")
            }));

        var client = new SquareTerminalSetupClient(new HttpClient(handler));
        var exception = await Assert.ThrowsAsync<SquareApiException>(() =>
            client.ListLocationsAsync(accessToken, CardTerminalEnvironment.Production));

        Assert.Contains("401", exception.Message);
        Assert.True(
            !exception.Message.Contains(accessToken, StringComparison.Ordinal),
            "Square error messages should not include access tokens");
    }

    [Fact]
    public async Task ListDeviceCodesAsync_UsesProductionEndpointAndParsesDeviceCodes()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "device_codes": [
                        {
                          "id": "DC-1",
                          "name": "Counter 2",
                          "code": "ABC123",
                          "status": "UNPAIRED",
                          "location_id": "LOC-1",
                          "pair_by": "2026-05-27T10:05:00Z",
                          "created_at": "2026-05-27T10:00:00Z"
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            });
        });

        var client = new SquareTerminalSetupClient(new HttpClient(handler));

        var result = await client.ListDeviceCodesAsync(TestAccessToken, CardTerminalEnvironment.Production, "LOC-1");

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Get, capturedRequest!.Method);
        Assert.Equal("https://connect.squareup.com/v2/devices/codes?location_id=LOC-1&product_type=TERMINAL_API", capturedRequest.RequestUri!.AbsoluteUri);
        Assert.True(HasBearerToken(capturedRequest, TestAccessToken));
        var deviceCode = Assert.Single(result);
        Assert.Equal("DC-1", deviceCode.Id);
        Assert.Equal("Counter 2", deviceCode.Name);
        Assert.Equal("ABC123", deviceCode.Code);
        Assert.Equal("UNPAIRED", deviceCode.Status);
        Assert.Equal("LOC-1", deviceCode.LocationId);
    }

    [Fact]
    public async Task CreateDeviceCodeAsync_PostsExpectedRequestBody()
    {
        HttpRequestMessage? capturedRequest = null;
        string? requestBody = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = request;
            return CreateResponseAsync();

            async Task<HttpResponseMessage> CreateResponseAsync()
            {
                requestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "device_code": {
                            "id": "DC-1",
                            "name": "Counter 2",
                            "code": "ABC123",
                            "status": "UNPAIRED",
                            "location_id": "LOC-1",
                            "pair_by": "2026-05-27T10:05:00Z",
                            "created_at": "2026-05-27T10:00:00Z"
                          }
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            }
        });

        var client = new SquareTerminalSetupClient(new HttpClient(handler));

        var result = await client.CreateDeviceCodeAsync(TestAccessToken, CardTerminalEnvironment.Production, "LOC-1", "Counter 2");

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("https://connect.squareup.com/v2/devices/codes", capturedRequest.RequestUri!.AbsoluteUri);
        Assert.True(HasBearerToken(capturedRequest, TestAccessToken));
        Assert.NotNull(requestBody);
        Assert.Contains("\"location_id\":\"LOC-1\"", requestBody, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"Counter 2\"", requestBody, StringComparison.Ordinal);
        Assert.Contains("\"product_type\":\"TERMINAL_API\"", requestBody, StringComparison.Ordinal);
        Assert.Contains("\"idempotency_key\":", requestBody, StringComparison.Ordinal);
        Assert.Equal("ABC123", result.Code);
    }

    [Fact]
    public async Task GetDeviceCodeAsync_ParsesPairedDeviceId()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "device_code": {
                        "id": "DC-1",
                        "name": "Counter 2",
                        "code": "ABC123",
                        "status": "PAIRED",
                        "location_id": "LOC-1",
                        "device_id": "DEV-2",
                        "pair_by": "2026-05-27T10:05:00Z",
                        "created_at": "2026-05-27T10:00:00Z"
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            }));

        var client = new SquareTerminalSetupClient(new HttpClient(handler));

        var result = await client.GetDeviceCodeAsync(TestAccessToken, CardTerminalEnvironment.Production, "DC-1");

        Assert.Equal("PAIRED", result.Status);
        Assert.Equal("DEV-2", result.DeviceId);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return await handler(request, cancellationToken);
        }
    }

    private static bool HasBearerToken(HttpRequestMessage request, string expectedToken)
    {
        return request.Headers.Authorization?.Scheme == "Bearer" &&
            string.Equals(request.Headers.Authorization.Parameter, expectedToken, StringComparison.Ordinal);
    }
}
