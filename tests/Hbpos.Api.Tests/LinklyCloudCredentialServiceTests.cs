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

    private sealed class FakeLinklyCloudCredentialRepository(
        Func<string, string, Task<LinklyCloudCredentialRecord?>> getByStoreCodeAsync) : ILinklyCloudCredentialRepository
    {
        public Task<LinklyCloudCredentialRecord?> GetByStoreCodeAsync(
            string storeCode,
            string environment,
            CancellationToken cancellationToken)
        {
            return getByStoreCodeAsync(storeCode, environment);
        }
    }
}
