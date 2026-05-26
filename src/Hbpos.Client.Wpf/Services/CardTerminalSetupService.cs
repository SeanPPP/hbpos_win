namespace Hbpos.Client.Wpf.Services;

public interface ICardTerminalSetupService
{
    Task<CardTerminalConfiguration> LoadConfigurationAsync(CancellationToken cancellationToken = default);

    Task<string?> GetSquareAccessTokenAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SquareLocationOption>> ListSquareLocationsAsync(
        string? accessToken,
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SquareDeviceOption>> ListSquareDevicesAsync(
        string? accessToken,
        CardTerminalEnvironment environment,
        string locationId,
        CancellationToken cancellationToken = default);

    Task SaveSquareAsync(
        CardTerminalConfiguration configuration,
        string? squareAccessToken,
        CancellationToken cancellationToken = default);

    Task SaveLinklyAsync(
        CardTerminalConfiguration configuration,
        CancellationToken cancellationToken = default);

    Task<LinklyConnectionTestResult> TestLinklyConnectionAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class CardTerminalSetupService(
    ICardTerminalSettingsStore settingsStore,
    ISquareTerminalSetupClient squareSetupClient,
    ILinklyTerminalClient linklyTerminalClient) : ICardTerminalSetupService
{
    public Task<CardTerminalConfiguration> LoadConfigurationAsync(CancellationToken cancellationToken = default)
    {
        return settingsStore.LoadAsync(cancellationToken);
    }

    public Task<string?> GetSquareAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        return settingsStore.GetSquareAccessTokenAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SquareLocationOption>> ListSquareLocationsAsync(
        string? accessToken,
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        var resolvedToken = await ResolveSquareAccessTokenAsync(accessToken, environment, cancellationToken);
        try
        {
            return await squareSetupClient.ListLocationsAsync(resolvedToken, environment, cancellationToken);
        }
        catch (SquareApiException ex) when (ex.IsAuthenticationFailure && string.IsNullOrWhiteSpace(accessToken))
        {
            var refreshedToken = await settingsStore.GetSquareAccessTokenAsync(
                environment,
                forceRefresh: true,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(refreshedToken))
            {
                throw new InvalidOperationException("Square token refresh failed.", ex);
            }

            return await squareSetupClient.ListLocationsAsync(refreshedToken, environment, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<SquareDeviceOption>> ListSquareDevicesAsync(
        string? accessToken,
        CardTerminalEnvironment environment,
        string locationId,
        CancellationToken cancellationToken = default)
    {
        var resolvedToken = await ResolveSquareAccessTokenAsync(accessToken, environment, cancellationToken);
        try
        {
            return await squareSetupClient.ListDevicesAsync(resolvedToken, environment, locationId, cancellationToken);
        }
        catch (SquareApiException ex) when (ex.IsAuthenticationFailure && string.IsNullOrWhiteSpace(accessToken))
        {
            var refreshedToken = await settingsStore.GetSquareAccessTokenAsync(
                environment,
                forceRefresh: true,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(refreshedToken))
            {
                throw new InvalidOperationException("Square token refresh failed.", ex);
            }

            return await squareSetupClient.ListDevicesAsync(refreshedToken, environment, locationId, cancellationToken);
        }
    }

    public Task SaveSquareAsync(
        CardTerminalConfiguration configuration,
        string? squareAccessToken,
        CancellationToken cancellationToken = default)
    {
        return settingsStore.SaveAsync(configuration, squareAccessToken, cancellationToken);
    }

    public Task SaveLinklyAsync(
        CardTerminalConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        return settingsStore.SaveAsync(configuration, squareAccessToken: null, cancellationToken);
    }

    public Task<LinklyConnectionTestResult> TestLinklyConnectionAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        return linklyTerminalClient.TestConnectionAsync(host, port, timeout, cancellationToken);
    }

    private async Task<string> ResolveSquareAccessTokenAsync(
        string? accessToken,
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            return accessToken.Trim();
        }

        var storedToken = await settingsStore.GetSquareAccessTokenAsync(
            environment,
            forceRefresh: false,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(storedToken))
        {
            throw new InvalidOperationException("Square access token is unavailable.");
        }

        return storedToken;
    }
}
