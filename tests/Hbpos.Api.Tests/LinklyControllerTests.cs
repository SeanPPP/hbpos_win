using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Hbpos.Api;
using Hbpos.Api.Controllers;
using Hbpos.Api.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.Linkly;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Tests;

public sealed class LinklyControllerTests
{
    [Fact]
    public void LinklyCloudCredentialEndpoint_KeepsExpectedRouteAndAuthorization()
    {
        Assert.Equal("cloud-credential", typeof(LinklyController)
            .GetMethod(nameof(LinklyController.GetCloudCredential))?
            .GetCustomAttributes(typeof(HttpGetAttribute), inherit: false)
            .Cast<HttpGetAttribute>()
            .Single()
            .Template);
        Assert.NotNull(typeof(LinklyController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .SingleOrDefault());
    }

    [Fact]
    public async Task GetCloudCredential_RequiresAuthentication()
    {
        await using var factory = new LinklyApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/linkly/cloud-credential");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetCloudCredential_UsesAuthenticatedStoreCodeOnly()
    {
        string? requestedStoreCode = null;
        var expected = new LinklyCloudCredentialResponse(
            "S01",
            "merchant-user",
            "merchant-password",
            new DateTimeOffset(2026, 5, 28, 4, 0, 0, TimeSpan.Zero));

        await using var factory = new LinklyApiFactory(new StubLinklyCloudCredentialService(
            responseFactory: storeCode =>
            {
                requestedStoreCode = storeCode;
                return Task.FromResult<LinklyCloudCredentialResponse?>(expected);
            }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.GetAsync("/api/v1/linkly/cloud-credential?storeCode=S99");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<LinklyCloudCredentialResponse>>();
        Assert.NotNull(apiResult);
        Assert.True(apiResult!.Success);
        Assert.Equal("S01", requestedStoreCode);
        Assert.Equal(expected.StoreCode, apiResult.Data?.StoreCode);
        Assert.Equal(expected.Username, apiResult.Data?.Username);
        Assert.Equal(expected.Password, apiResult.Data?.Password);
    }

    [Fact]
    public async Task GetCloudCredential_ReturnsStableNotFoundWhenCredentialIsMissing()
    {
        await using var factory = new LinklyApiFactory(new StubLinklyCloudCredentialService(
            responseFactory: _ => Task.FromResult<LinklyCloudCredentialResponse?>(null)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.GetAsync("/api/v1/linkly/cloud-credential");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<LinklyCloudCredentialResponse>>();
        Assert.NotNull(apiResult);
        Assert.False(apiResult!.Success);
        Assert.Equal("LINKLY_CLOUD_CREDENTIAL_NOT_CONFIGURED", apiResult.ErrorCode);
        Assert.Equal("Linkly Cloud credential is not configured for this store.", apiResult.Message);
    }

    [Fact]
    public async Task GetCloudCredential_ReturnsSanitizedServerErrorWhenServiceThrows()
    {
        const string secretPassword = "merchant-password";
        await using var factory = new LinklyApiFactory(new StubLinklyCloudCredentialService(
            exceptionFactory: _ => new InvalidOperationException($"SQL timeout from POSM_LinklyCloudCredential on db01 for password {secretPassword}")));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.GetAsync("/api/v1/linkly/cloud-credential");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<LinklyCloudCredentialResponse>>();
        Assert.NotNull(apiResult);
        Assert.False(apiResult!.Success);
        Assert.Equal("LINKLY_CLOUD_CREDENTIAL_READ_FAILED", apiResult.ErrorCode);
        var message = apiResult.Message ?? string.Empty;
        Assert.Equal("Failed to load Linkly Cloud credential configuration.", message);
        Assert.DoesNotContain("SQL", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("POSM_LinklyCloudCredential", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("db01", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secretPassword, message, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_FailsWhenLinklyCloudCredentialSchemaInitializerThrows()
    {
        using var factory = new LinklyApiFactory(
            schemaInitializer: new ThrowingLinklyCloudCredentialSchemaInitializer(
                new InvalidOperationException("linkly cloud schema bootstrap failed")));

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("schema bootstrap failed", exception.Message);
    }

    private sealed class LinklyApiFactory(
        ILinklyCloudCredentialService? linklyCloudCredentialService = null,
        ILinklyCloudCredentialSchemaInitializer? schemaInitializer = null)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<AuthenticationOptions>(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    options.DefaultScheme = TestAuthHandler.SchemeName;
                });

                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.SchemeName,
                        _ => { });

                services.RemoveAll<ILinklyCloudCredentialService>();
                services.AddSingleton(linklyCloudCredentialService ?? new StubLinklyCloudCredentialService());

                services.RemoveAll<ILinklyCloudCredentialSchemaInitializer>();
                services.AddSingleton(schemaInitializer ?? new NoOpLinklyCloudCredentialSchemaInitializer());

                services.RemoveAll<ISquareTokenSchemaInitializer>();
                services.AddSingleton<ISquareTokenSchemaInitializer>(new NoOpSquareTokenSchemaInitializer());
            });
        }
    }

    private sealed class NoOpLinklyCloudCredentialSchemaInitializer : ILinklyCloudCredentialSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingLinklyCloudCredentialSchemaInitializer(Exception exception) : ILinklyCloudCredentialSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            throw exception;
        }
    }

    private sealed class NoOpSquareTokenSchemaInitializer : ISquareTokenSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "LinklyTestAuth";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            if (string.IsNullOrWhiteSpace(header) || !string.Equals(header, "Test", StringComparison.Ordinal))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new[]
            {
                new Claim(DeviceAuthConstants.DeviceCodeClaim, "POS-01"),
                new Claim(DeviceAuthConstants.StoreCodeClaim, "S01"),
                new Claim(DeviceAuthConstants.HardwareIdClaim, "HW-001")
            };
            var identity = new ClaimsIdentity(claims, SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed class StubLinklyCloudCredentialService(
        Func<string, Task<LinklyCloudCredentialResponse?>>? responseFactory = null,
        Func<string, Exception>? exceptionFactory = null) : ILinklyCloudCredentialService
    {
        public Task<LinklyCloudCredentialResponse?> GetByStoreCodeAsync(
            string storeCode,
            CancellationToken cancellationToken)
        {
            if (exceptionFactory is not null)
            {
                throw exceptionFactory(storeCode);
            }

            if (responseFactory is not null)
            {
                return responseFactory(storeCode);
            }

            return Task.FromResult<LinklyCloudCredentialResponse?>(null);
        }
    }
}
