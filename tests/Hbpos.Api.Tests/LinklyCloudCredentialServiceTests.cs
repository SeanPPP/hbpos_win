using Hbpos.Api.Services;
using Hbpos.Contracts.Linkly;

namespace Hbpos.Api.Tests;

public sealed class LinklyCloudCredentialServiceTests
{
    [Fact]
    public async Task GetByStoreCodeAsync_UsesTrimmedStoreCodeAndMapsCredential()
    {
        string? requestedStoreCode = null;
        var updatedAt = new DateTime(2026, 5, 28, 1, 2, 3, DateTimeKind.Utc);
        var service = new LinklyCloudCredentialService(new FakeLinklyCloudCredentialRepository(
            getByStoreCodeAsync: storeCode =>
            {
                requestedStoreCode = storeCode;
                return Task.FromResult<LinklyCloudCredentialRecord?>(new LinklyCloudCredentialRecord
                {
                    StoreCode = "S01",
                    Username = "merchant-user",
                    Password = "merchant-password",
                    UpdatedAt = updatedAt
                });
            }));

        var response = await service.GetByStoreCodeAsync("  S01  ", CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("S01", response!.StoreCode);
        Assert.Equal("merchant-user", response.Username);
        Assert.Equal("merchant-password", response.Password);
        Assert.Equal(new DateTimeOffset(updatedAt), response.UpdatedAt);
        Assert.Equal("S01", requestedStoreCode);
    }

    [Fact]
    public async Task GetByStoreCodeAsync_ReturnsNullWhenCredentialPayloadIsIncomplete()
    {
        var service = new LinklyCloudCredentialService(new FakeLinklyCloudCredentialRepository(
            getByStoreCodeAsync: _ => Task.FromResult<LinklyCloudCredentialRecord?>(new LinklyCloudCredentialRecord
            {
                StoreCode = "S01",
                Username = "merchant-user",
                Password = " "
            })));

        var response = await service.GetByStoreCodeAsync("S01", CancellationToken.None);

        Assert.Null(response);
    }

    private sealed class FakeLinklyCloudCredentialRepository(
        Func<string, Task<LinklyCloudCredentialRecord?>> getByStoreCodeAsync) : ILinklyCloudCredentialRepository
    {
        public Task<LinklyCloudCredentialRecord?> GetByStoreCodeAsync(
            string storeCode,
            CancellationToken cancellationToken)
        {
            return getByStoreCodeAsync(storeCode);
        }
    }
}
