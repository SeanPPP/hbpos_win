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

    Task<IReadOnlyList<SquareDeviceCodeOption>> ListSquareDeviceCodesAsync(
        string? accessToken,
        CardTerminalEnvironment environment,
        string locationId,
        CancellationToken cancellationToken = default);

    Task<SquareDeviceCodeOption> CreateSquareDeviceCodeAsync(
        string? accessToken,
        CardTerminalEnvironment environment,
        string locationId,
        string name,
        CancellationToken cancellationToken = default);

    Task<SquareDeviceCodeOption> GetSquareDeviceCodeAsync(
        string? accessToken,
        CardTerminalEnvironment environment,
        string deviceCodeId,
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
        LogSquareSetup("load configuration requested");
        return settingsStore.LoadAsync(cancellationToken);
    }

    public Task<string?> GetSquareAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        LogSquareSetup("get cached square token requested");
        return settingsStore.GetSquareAccessTokenAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SquareLocationOption>> ListSquareLocationsAsync(
        string? accessToken,
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        LogSquareSetup($"list locations requested environment={environment} tokenSource={DescribeTokenSource(accessToken)}");
        var resolvedToken = await ResolveSquareAccessTokenAsync(accessToken, environment, cancellationToken);
        try
        {
            var locations = await squareSetupClient.ListLocationsAsync(resolvedToken, environment, cancellationToken);
            LogSquareSetup($"list locations succeeded environment={environment} count={locations.Count}");
            return locations;
        }
        catch (SquareApiException ex) when (ex.IsAuthenticationFailure && string.IsNullOrWhiteSpace(accessToken))
        {
            LogSquareSetup($"list locations authentication failure environment={environment}; refreshing token");
            var refreshedToken = await settingsStore.GetSquareAccessTokenAsync(
                environment,
                forceRefresh: true,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(refreshedToken))
            {
                LogSquareSetup($"list locations token refresh failed environment={environment}");
                throw new InvalidOperationException("Square token refresh failed.", ex);
            }

            var locations = await squareSetupClient.ListLocationsAsync(refreshedToken, environment, cancellationToken);
            LogSquareSetup($"list locations succeeded after refresh environment={environment} count={locations.Count}");
            return locations;
        }
    }

    public async Task<IReadOnlyList<SquareDeviceOption>> ListSquareDevicesAsync(
        string? accessToken,
        CardTerminalEnvironment environment,
        string locationId,
        CancellationToken cancellationToken = default)
    {
        LogSquareSetup($"list devices requested environment={environment} locationId={LogValue(locationId)} tokenSource={DescribeTokenSource(accessToken)}");
        var resolvedToken = await ResolveSquareAccessTokenAsync(accessToken, environment, cancellationToken);
        try
        {
            var devices = await squareSetupClient.ListDevicesAsync(resolvedToken, environment, locationId, cancellationToken);
            LogSquareSetup($"list devices succeeded environment={environment} locationId={LogValue(locationId)} count={devices.Count}");
            return devices;
        }
        catch (SquareApiException ex) when (ex.IsAuthenticationFailure && string.IsNullOrWhiteSpace(accessToken))
        {
            LogSquareSetup($"list devices authentication failure environment={environment} locationId={LogValue(locationId)}; refreshing token");
            var refreshedToken = await settingsStore.GetSquareAccessTokenAsync(
                environment,
                forceRefresh: true,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(refreshedToken))
            {
                LogSquareSetup($"list devices token refresh failed environment={environment} locationId={LogValue(locationId)}");
                throw new InvalidOperationException("Square token refresh failed.", ex);
            }

            var devices = await squareSetupClient.ListDevicesAsync(refreshedToken, environment, locationId, cancellationToken);
            LogSquareSetup($"list devices succeeded after refresh environment={environment} locationId={LogValue(locationId)} count={devices.Count}");
            return devices;
        }
    }

    public Task<IReadOnlyList<SquareDeviceCodeOption>> ListSquareDeviceCodesAsync(
        string? accessToken,
        CardTerminalEnvironment environment,
        string locationId,
        CancellationToken cancellationToken = default)
    {
        EnsureDeviceCodesSupported(environment);
        return ExecuteSquareWithRetryAsync(
            accessToken,
            environment,
            token => squareSetupClient.ListDeviceCodesAsync(token, environment, locationId, cancellationToken),
            cancellationToken);
    }

    public Task<SquareDeviceCodeOption> CreateSquareDeviceCodeAsync(
        string? accessToken,
        CardTerminalEnvironment environment,
        string locationId,
        string name,
        CancellationToken cancellationToken = default)
    {
        EnsureDeviceCodesSupported(environment);
        return ExecuteSquareWithRetryAsync(
            accessToken,
            environment,
            token => squareSetupClient.CreateDeviceCodeAsync(token, environment, locationId, name, cancellationToken),
            cancellationToken);
    }

    public Task<SquareDeviceCodeOption> GetSquareDeviceCodeAsync(
        string? accessToken,
        CardTerminalEnvironment environment,
        string deviceCodeId,
        CancellationToken cancellationToken = default)
    {
        EnsureDeviceCodesSupported(environment);
        return ExecuteSquareWithRetryAsync(
            accessToken,
            environment,
            token => squareSetupClient.GetDeviceCodeAsync(token, environment, deviceCodeId, cancellationToken),
            cancellationToken);
    }

    public async Task SaveSquareAsync(
        CardTerminalConfiguration configuration,
        string? squareAccessToken,
        CancellationToken cancellationToken = default)
    {
        var normalizedConfiguration = configuration with
        {
            SquareDeviceId = SquareDeviceIdNormalizer.NormalizeForTerminalCheckout(configuration.SquareDeviceId)
        };

        LogSquareSetup(
            $"save square requested environment={normalizedConfiguration.Environment} locationId={LogValue(normalizedConfiguration.SquareLocationId)} storedDeviceId={LogValue(configuration.SquareDeviceId)} savedDeviceId={LogValue(normalizedConfiguration.SquareDeviceId)} tokenSource={DescribeTokenSource(squareAccessToken)}");
        await settingsStore.SaveAsync(normalizedConfiguration, squareAccessToken, cancellationToken);
        LogSquareSetup(
            $"save square succeeded environment={normalizedConfiguration.Environment} locationId={LogValue(normalizedConfiguration.SquareLocationId)} savedDeviceId={LogValue(normalizedConfiguration.SquareDeviceId)}");
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
            LogSquareSetup($"resolve token using provided token environment={environment}");
            return accessToken.Trim();
        }

        LogSquareSetup($"resolve token using stored token environment={environment}");
        var storedToken = await settingsStore.GetSquareAccessTokenAsync(
            environment,
            forceRefresh: false,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(storedToken))
        {
            LogSquareSetup($"resolve token failed environment={environment} reason=missing");
            throw new InvalidOperationException("Square access token is unavailable.");
        }

        LogSquareSetup($"resolve token succeeded environment={environment} source=stored");
        return storedToken;
    }

    private async Task<T> ExecuteSquareWithRetryAsync<T>(
        string? accessToken,
        CardTerminalEnvironment environment,
        Func<string, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        LogSquareSetup($"execute square operation requested environment={environment} tokenSource={DescribeTokenSource(accessToken)}");
        var resolvedToken = await ResolveSquareAccessTokenAsync(accessToken, environment, cancellationToken);
        try
        {
            var result = await operation(resolvedToken);
            LogSquareSetup($"execute square operation succeeded environment={environment}");
            return result;
        }
        catch (SquareApiException ex) when (ex.IsAuthenticationFailure && string.IsNullOrWhiteSpace(accessToken))
        {
            LogSquareSetup($"execute square operation authentication failure environment={environment}; refreshing token");
            var refreshedToken = await settingsStore.GetSquareAccessTokenAsync(
                environment,
                forceRefresh: true,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(refreshedToken))
            {
                LogSquareSetup($"execute square operation token refresh failed environment={environment}");
                throw new InvalidOperationException("Square token refresh failed.", ex);
            }

            var result = await operation(refreshedToken);
            LogSquareSetup($"execute square operation succeeded after refresh environment={environment}");
            return result;
        }
    }

    private static void EnsureDeviceCodesSupported(CardTerminalEnvironment environment)
    {
        if (environment == CardTerminalEnvironment.Sandbox)
        {
            throw new InvalidOperationException("Square Device Codes are only supported in Production.");
        }
    }

    private static void LogSquareSetup(string message)
    {
        ConsoleLog.Write("Square", $"settings {message}");
    }

    private static string DescribeTokenSource(string? accessToken)
    {
        return string.IsNullOrWhiteSpace(accessToken) ? "stored" : "provided";
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value;
    }
}
