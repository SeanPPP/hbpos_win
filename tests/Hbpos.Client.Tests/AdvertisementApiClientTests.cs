using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Advertisements;
using Hbpos.Contracts.Common;

namespace Hbpos.Client.Tests;

public sealed class AdvertisementApiClientTests
{
    [Fact]
    public async Task GetActiveAsync_builds_expected_request_and_unwraps_api_result()
    {
        HttpRequestMessage? capturedRequest = null;
        var expected = new AdvertisementPlaybackResponse(
            "S001",
            DateTimeOffset.UtcNow,
            [CreateImageAdvertisement("ad-1")]);
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = request;
            return JsonResponse(ApiResult<AdvertisementPlaybackResponse>.Ok(expected));
        });
        var client = new AdvertisementApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        });

        var response = await client.GetActiveAsync(" S001 ", take: 12);

        Assert.Equal(HttpMethod.Get, capturedRequest?.Method);
        Assert.Equal(
            "http://localhost:5000/api/v1/advertisements/active?storeCode=S001&take=12",
            capturedRequest?.RequestUri?.ToString());
        Assert.Equal("S001", response.StoreCode);
        Assert.Equal("ad-1", Assert.Single(response.Items).Id);
    }

    [Fact]
    public async Task GetActiveAsync_throws_when_api_returns_failure()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            JsonResponse(
                ApiResult<AdvertisementPlaybackResponse>.Fail("STORE_CODE_REQUIRED", "storeCode is required"),
                HttpStatusCode.BadRequest));
        var client = new AdvertisementApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        });

        var ex = await Assert.ThrowsAsync<AdvertisementApiException>(() =>
            client.GetActiveAsync("S001"));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Equal("STORE_CODE_REQUIRED", ex.ErrorCode);
    }

    private static AdvertisementPlaybackItemDto CreateImageAdvertisement(string id)
    {
        return new AdvertisementPlaybackItemDto(
            id,
            $"Ad {id}",
            $"Description {id}",
            "image",
            $"https://cdn.example.com/{id}.png",
            null,
            $"object/{id}",
            $"{id}.png",
            "image/png",
            1024,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddMinutes(5),
            1);
    }

    private static HttpResponseMessage JsonResponse<T>(T payload, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
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
