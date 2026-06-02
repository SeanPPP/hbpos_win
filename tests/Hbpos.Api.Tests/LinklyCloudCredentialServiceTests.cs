using Hbpos.Api.Services;
using Hbpos.Contracts.Linkly;

namespace Hbpos.Api.Tests;

public sealed class LinklyCloudCredentialServiceTests
{
    [Fact]
    public async Task GetByStoreCodeAsync_UsesTrimmedStoreCodeAndMapsCredential()
    {
        string? requestedStoreCode = null;
        string? requestedEnvironment = null;
        var updatedAt = new DateTime(2026, 5, 28, 1, 2, 3, DateTimeKind.Utc);
        var service = new LinklyCloudCredentialService(new FakeLinklyCloudCredentialRepository(
            getByStoreCodeAsync: (storeCode, environment) =>
            {
                requestedStoreCode = storeCode;
                requestedEnvironment = environment;
                return Task.FromResult<LinklyCloudCredentialRecord?>(new LinklyCloudCredentialRecord
                {
                    StoreCode = "S01",
                    Environment = "Sandbox",
                    Username = "merchant-user",
                    Password = "merchant-password",
                    UpdatedAt = updatedAt
                });
            }));

        var response = await service.GetByStoreCodeAsync("  S01  ", " sandbox ", CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("S01", response!.StoreCode);
        Assert.Equal("Sandbox", response.Environment);
        Assert.Equal("merchant-user", response.Username);
        Assert.Equal("merchant-password", response.Password);
        Assert.Equal(new DateTimeOffset(updatedAt), response.UpdatedAt);
        Assert.Equal("S01", requestedStoreCode);
        Assert.Equal("Sandbox", requestedEnvironment);
    }

    [Fact]
    public async Task GetByStoreCodeAsync_ReturnsNullWhenCredentialPayloadIsIncomplete()
    {
        var service = new LinklyCloudCredentialService(new FakeLinklyCloudCredentialRepository(
            getByStoreCodeAsync: (_, _) => Task.FromResult<LinklyCloudCredentialRecord?>(new LinklyCloudCredentialRecord
            {
                StoreCode = "S01",
                Environment = "Production",
                Username = "merchant-user",
                Password = " "
            })));

        var response = await service.GetByStoreCodeAsync("S01", "Production", CancellationToken.None);

        Assert.Null(response);
    }

    [Fact]
    public async Task GetByStoreCodeAsync_ReturnsNullWhenEnvironmentIsInvalid()
    {
        var repositoryCalled = false;
        var service = new LinklyCloudCredentialService(new FakeLinklyCloudCredentialRepository(
            getByStoreCodeAsync: (_, _) =>
            {
                repositoryCalled = true;
                return Task.FromResult<LinklyCloudCredentialRecord?>(null);
            }));

        var response = await service.GetByStoreCodeAsync("S01", "staging", CancellationToken.None);

        Assert.Null(response);
        Assert.False(repositoryCalled);
    }

    [Fact]
    public async Task UpsertAsync_trims_scope_and_returns_sanitized_response()
    {
        LinklyCloudCredentialRecord? saved = null;
        var service = new LinklyCloudCredentialService(new FakeLinklyCloudCredentialRepository(
            getByStoreCodeAsync: (_, _) => Task.FromResult(saved),
            upsertAsync: (storeCode, environment, username, password, updatedAt, updatedBy) =>
            {
                saved = new LinklyCloudCredentialRecord
                {
                    StoreCode = storeCode,
                    Environment = environment,
                    Username = username,
                    Password = password,
                    UpdatedAt = updatedAt,
                    UpdatedBy = updatedBy
                };
                return Task.FromResult(saved);
            }));

        var response = await service.UpsertAsync(
            " S01 ",
            new LinklyCloudCredentialUpsertRequest(" sandbox ", " merchant-user ", " merchant-password "),
            " device:POS-01 ",
            CancellationToken.None);

        Assert.Equal("S01", response.StoreCode);
        Assert.Equal("Sandbox", response.Environment);
        Assert.Equal("merchant-user", response.Username);
        Assert.True(response.HasPassword);
        Assert.Equal("merchant-password", saved?.Password);
        Assert.Equal("device:POS-01", saved?.UpdatedBy);
    }

    [Theory]
    [InlineData("staging", "merchant-user", "merchant-password", "environment must be Production or Sandbox")]
    [InlineData("Sandbox", "", "merchant-password", "username is required.")]
    [InlineData("Sandbox", "merchant-user", "", "password is required.")]
    public async Task UpsertAsync_rejects_invalid_input_without_exposing_secret_values(
        string environment,
        string username,
        string password,
        string expectedMessage)
    {
        var service = new LinklyCloudCredentialService(new FakeLinklyCloudCredentialRepository(
            getByStoreCodeAsync: (_, _) => Task.FromResult<LinklyCloudCredentialRecord?>(null)));

        var exception = await Assert.ThrowsAsync<LinklyCloudCredentialValidationException>(() =>
            service.UpsertAsync(
                "S01",
                new LinklyCloudCredentialUpsertRequest(environment, username, password),
                "device:POS-01",
                CancellationToken.None));

        Assert.Equal(expectedMessage, exception.Message);
        Assert.DoesNotContain("merchant-password", exception.Message, StringComparison.Ordinal);
    }

    private sealed class FakeLinklyCloudCredentialRepository(
        Func<string, string, Task<LinklyCloudCredentialRecord?>> getByStoreCodeAsync,
        Func<string, string, string, string, DateTime, string?, Task<LinklyCloudCredentialRecord>>? upsertAsync = null) : ILinklyCloudCredentialRepository
    {
        public Task<LinklyCloudCredentialRecord?> GetByStoreCodeAsync(
            string storeCode,
            string environment,
            CancellationToken cancellationToken)
        {
            return getByStoreCodeAsync(storeCode, environment);
        }

        public Task<LinklyCloudCredentialRecord> UpsertAsync(
            string storeCode,
            string environment,
            string username,
            string password,
            DateTime updatedAt,
            string? updatedBy,
            CancellationToken cancellationToken)
        {
            if (upsertAsync is not null)
            {
                return upsertAsync(storeCode, environment, username, password, updatedAt, updatedBy);
            }

            return Task.FromResult(new LinklyCloudCredentialRecord
            {
                StoreCode = storeCode,
                Environment = environment,
                Username = username,
                Password = password,
                UpdatedAt = updatedAt,
                UpdatedBy = updatedBy
            });
        }
    }
}
