using System.Security.Claims;
using Hbpos.Api.Auth;
using Hbpos.Api.Controllers;
using Hbpos.Api.Services;
using Hbpos.Contracts.Advertisements;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hbpos.Api.Tests;

public sealed class AdvertisementControllerTests
{
    [Fact]
    public void AdvertisementEndpoint_KeepsExpectedRouteAndAuthorization()
    {
        Assert.Equal("active", GetHttpGetTemplate(nameof(AdvertisementsController.GetActive)));
        Assert.NotNull(typeof(AdvertisementsController)
            .GetCustomAttributes(typeof(ApiControllerAttribute), inherit: false)
            .SingleOrDefault());
        Assert.NotNull(typeof(AdvertisementsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .SingleOrDefault());
    }

    [Fact]
    public async Task GetActive_ReturnsBadRequestWhenStoreCodeMissing()
    {
        var controller = new AdvertisementsController(new FakeAdvertisementPlaybackService());

        var result = await controller.GetActive(string.Empty, 20, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<AdvertisementPlaybackResponse>>(badRequest.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("STORE_CODE_REQUIRED", apiResult.ErrorCode);
    }

    [Fact]
    public async Task GetActive_ReturnsForbiddenWhenDeviceStoreDoesNotMatch()
    {
        var controller = new AdvertisementsController(new FakeAdvertisementPlaybackService());
        SetAuthenticatedDevice(controller, "S02", "POS-02");

        var result = await controller.GetActive("S01", 20, CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        var apiResult = Assert.IsType<ApiResult<AdvertisementPlaybackResponse>>(forbidden.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("DEVICE_SCOPE_FORBIDDEN", apiResult.ErrorCode);
    }

    [Fact]
    public async Task GetActive_ReturnsWrappedResponse()
    {
        var expected = new AdvertisementPlaybackResponse(
            "S01",
            DateTimeOffset.Parse("2026-05-31T09:00:00Z"),
            [
                new AdvertisementPlaybackItemDto(
                    "AD-001",
                    "Promo",
                    "desc",
                    "image",
                    "https://cdn.example.com/ad-001.jpg",
                    null,
                    "advertisements/ad-001.jpg",
                    "ad-001.jpg",
                    "image/jpeg",
                    1024,
                    DateTimeOffset.Parse("2026-05-30T00:00:00Z"),
                    DateTimeOffset.Parse("2026-06-30T00:00:00Z"),
                    1)
            ]);
        var service = new FakeAdvertisementPlaybackService
        {
            Response = expected
        };
        var controller = new AdvertisementsController(service);
        SetAuthenticatedDevice(controller, "S01", "POS-01");

        var result = await controller.GetActive("S01", 15, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<AdvertisementPlaybackResponse>>(ok.Value);
        Assert.True(apiResult.Success);
        Assert.Same(expected, apiResult.Data);
        Assert.Equal(("S01", 15), service.LastRequest);
    }

    [Fact]
    public void Startup_InitializesAdvertisementSchema()
    {
        var advertisementSchemaInitializer = new RecordingAdvertisementSchemaInitializer();
        using var factory = new AdvertisementApiFactory(advertisementSchemaInitializer);

        using var client = factory.CreateClient();

        Assert.Equal(1, advertisementSchemaInitializer.InitializeCallCount);
    }

    [Fact]
    public void Startup_FailsWhenAdvertisementSchemaInitializerThrows()
    {
        using var factory = new AdvertisementApiFactory(
            new ThrowingAdvertisementSchemaInitializer(
                new InvalidOperationException("advertisement schema bootstrap failed")));

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("advertisement schema bootstrap failed", exception.Message);
    }

    private static string? GetHttpGetTemplate(string methodName)
    {
        return typeof(AdvertisementsController)
            .GetMethod(methodName)?
            .GetCustomAttributes(typeof(HttpGetAttribute), inherit: false)
            .Cast<HttpGetAttribute>()
            .Single()
            .Template;
    }

    private static void SetAuthenticatedDevice(
        ControllerBase controller,
        string storeCode,
        string deviceCode)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(DeviceAuthConstants.StoreCodeClaim, storeCode),
            new Claim(DeviceAuthConstants.DeviceCodeClaim, deviceCode),
            new Claim(DeviceAuthConstants.HardwareIdClaim, "HW-001")
        ], DeviceAuthConstants.Scheme);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity)
            }
        };
    }

    private sealed class FakeAdvertisementPlaybackService : IAdvertisementPlaybackService
    {
        public AdvertisementPlaybackResponse Response { get; init; } =
            new("S01", DateTimeOffset.UnixEpoch, []);

        public (string StoreCode, int Take)? LastRequest { get; private set; }

        public Task<AdvertisementPlaybackResponse> GetActiveAsync(
            string storeCode,
            int take,
            CancellationToken cancellationToken)
        {
            LastRequest = (storeCode, take);
            return Task.FromResult(Response);
        }
    }

    private sealed class AdvertisementApiFactory(IAdvertisementSchemaInitializer advertisementSchemaInitializer)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAdvertisementSchemaInitializer>();
                services.AddSingleton(advertisementSchemaInitializer);

                services.RemoveAll<ILinklyCloudCredentialSchemaInitializer>();
                services.AddSingleton<ILinklyCloudCredentialSchemaInitializer>(
                    new NoOpLinklyCloudCredentialSchemaInitializer());

                services.RemoveAll<ILinklyCloudBackendAsyncSchemaInitializer>();
                services.AddSingleton<ILinklyCloudBackendAsyncSchemaInitializer>(
                    new NoOpLinklyCloudBackendAsyncSchemaInitializer());

                services.RemoveAll<ISquareTokenSchemaInitializer>();
                services.AddSingleton<ISquareTokenSchemaInitializer>(new NoOpSquareTokenSchemaInitializer());
            });
        }
    }

    private sealed class RecordingAdvertisementSchemaInitializer : IAdvertisementSchemaInitializer
    {
        public int InitializeCallCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            InitializeCallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingAdvertisementSchemaInitializer(Exception exception) : IAdvertisementSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            throw exception;
        }
    }

    private sealed class NoOpLinklyCloudCredentialSchemaInitializer : ILinklyCloudCredentialSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpLinklyCloudBackendAsyncSchemaInitializer : ILinklyCloudBackendAsyncSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpSquareTokenSchemaInitializer : ISquareTokenSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
