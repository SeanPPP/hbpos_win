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
using Hbpos.Contracts.Square;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Tests;

public sealed class SquareControllerTests
{
    private const string BackendToken = "opaque-api-square-token";

    [Fact]
    public void SquareTokenEndpoint_KeepsExpectedRouteAndAuthorization()
    {
        Assert.Equal("token", typeof(SquareController)
            .GetMethod(nameof(SquareController.GetToken))?
            .GetCustomAttributes(typeof(HttpGetAttribute), inherit: false)
            .Cast<HttpGetAttribute>()
            .Single()
            .Template);
        Assert.NotNull(typeof(SquareController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .SingleOrDefault());
    }

    [Fact]
    public async Task GetToken_RequiresAuthentication()
    {
        await using var factory = new SquareApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/square/token?environment=Production");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetToken_ReturnsWrappedTokenForEnvironment()
    {
        var expected = new SquareTokenResponse(
            "Production",
            BackendToken,
            new DateTimeOffset(2026, 5, 26, 4, 0, 0, TimeSpan.Zero));
        string? requestedEnvironment = null;

        await using var factory = new SquareApiFactory(new StubSquareTokenService(
            responseFactory: environment =>
            {
                requestedEnvironment = environment;
                return Task.FromResult<SquareTokenResponse?>(expected);
            }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.GetAsync("/api/v1/square/token?environment=production");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<SquareTokenResponse>>();
        Assert.NotNull(apiResult);
        Assert.True(apiResult.Success);
        Assert.Equal(expected.Environment, apiResult.Data?.Environment);
        Assert.Equal(expected.UpdatedAt, apiResult.Data?.UpdatedAt);
        Assert.True(
            string.Equals(BackendToken, apiResult.Data?.AccessToken, StringComparison.Ordinal),
            "successful token response should include the configured token");
        Assert.Equal("Production", requestedEnvironment);
    }

    [Fact]
    public async Task GetToken_ReturnsBadRequestForInvalidEnvironment()
    {
        await using var factory = new SquareApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.GetAsync("/api/v1/square/token?environment=staging");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<SquareTokenResponse>>();
        Assert.NotNull(apiResult);
        Assert.False(apiResult.Success);
        Assert.Equal("SQUARE_ENVIRONMENT_INVALID", apiResult.ErrorCode);
    }

    [Fact]
    public async Task GetToken_ReturnsStableNotFoundWhenTokenIsMissing()
    {
        await using var factory = new SquareApiFactory(new StubSquareTokenService(
            responseFactory: _ => Task.FromResult<SquareTokenResponse?>(null)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.GetAsync("/api/v1/square/token?environment=Sandbox");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<SquareTokenResponse>>();
        Assert.NotNull(apiResult);
        Assert.False(apiResult.Success);
        Assert.Equal("SQUARE_TOKEN_NOT_CONFIGURED", apiResult.ErrorCode);
        Assert.Equal("Square token is not configured for this environment.", apiResult.Message);
    }

    [Fact]
    public async Task GetToken_ReturnsSanitizedServerErrorWhenServiceThrows()
    {
        await using var factory = new SquareApiFactory(new StubSquareTokenService(
            exceptionFactory: _ => new InvalidOperationException($"SQL timeout from POSM_SquareToken on server db01 for token {BackendToken}")));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        using var response = await client.GetAsync("/api/v1/square/token?environment=Production");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var apiResult = await response.Content.ReadFromJsonAsync<ApiResult<SquareTokenResponse>>();
        Assert.NotNull(apiResult);
        var result = apiResult!;
        Assert.False(result.Success);
        Assert.Equal("SQUARE_TOKEN_READ_FAILED", result.ErrorCode);
        var message = result.Message ?? string.Empty;
        Assert.Equal("Failed to load Square token configuration.", message);
        Assert.DoesNotContain("SQL", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("POSM_SquareToken", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("db01", message, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            !message.Contains(BackendToken, StringComparison.Ordinal),
            "sanitized API error should not include token values");
    }

    [Fact]
    public void Startup_FailsWhenSquareTokenSchemaInitializerThrows()
    {
        using var factory = new SquareApiFactory(
            schemaInitializer: new ThrowingSquareTokenSchemaInitializer(
                new InvalidOperationException("schema bootstrap failed without token values")));

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("schema bootstrap failed", exception.Message);
        Assert.DoesNotContain(BackendToken, exception.Message, StringComparison.Ordinal);
    }

    private sealed class SquareApiFactory(
        ISquareTokenService? squareTokenService = null,
        ISquareTokenSchemaInitializer? schemaInitializer = null)
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

                services.RemoveAll<ISquareTokenService>();
                services.AddSingleton(squareTokenService ?? new StubSquareTokenService());

                services.RemoveAll<ISquareTokenSchemaInitializer>();
                services.AddSingleton(schemaInitializer ?? new NoOpSquareTokenSchemaInitializer());
            });
        }
    }

    private sealed class NoOpSquareTokenSchemaInitializer : ISquareTokenSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSquareTokenSchemaInitializer(Exception exception) : ISquareTokenSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            throw exception;
        }
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "SquareTestAuth";

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

    private sealed class StubSquareTokenService(
        Func<string, Task<SquareTokenResponse?>>? responseFactory = null,
        Func<string, Exception>? exceptionFactory = null) : ISquareTokenService
    {
        public Task<SquareTokenResponse?> GetActiveTokenAsync(
            string environment,
            CancellationToken cancellationToken)
        {
            if (exceptionFactory is not null)
            {
                throw exceptionFactory(environment);
            }

            if (responseFactory is not null)
            {
                return responseFactory(environment);
            }

            return Task.FromResult<SquareTokenResponse?>(null);
        }
    }
}
