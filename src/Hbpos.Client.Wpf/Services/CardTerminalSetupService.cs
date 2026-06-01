using Hbpos.Client.Wpf.Localization;

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

    Task<LinklyConnectionTestResult> PairLinklyCloudAsync(
        CardTerminalEnvironment environment,
        string pairCode,
        string? username,
        string? password,
        CancellationToken cancellationToken = default);

    Task<LinklyCloudCredentialSettings> LoadLinklyCloudCredentialAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);

    Task SaveLinklyCloudCredentialAsync(
        CardTerminalEnvironment environment,
        string username,
        string password,
        CancellationToken cancellationToken = default);

    Task<LinklyConnectionTestResult> TestLinklyCloudConnectionAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);

    Task<LinklyConnectionTestResult> TestLinklyCloudBackendConnectionAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);

    Task<bool> HasLinklyCloudSecretAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);

    Task SaveLinklyCloudAsync(
        CardTerminalConfiguration configuration,
        CancellationToken cancellationToken = default);
}

public sealed class CardTerminalSetupService(
    ICardTerminalSettingsStore settingsStore,
    ISquareTerminalSetupClient squareSetupClient,
    ILinklyTerminalClient linklyTerminalClient,
    ILinklyCloudApiClient? linklyCloudApiClient = null,
    ILinklyCloudTerminalClient? linklyCloudTerminalClient = null,
    ILinklyBackendTerminalClient? linklyBackendTerminalClient = null,
    DeviceAuthorizationState? deviceAuthorizationState = null,
    ILocalizationService? localization = null) : ICardTerminalSetupService
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
        return settingsStore.SaveAsync(configuration with { LinklyConnectionMode = LinklyConnectionMode.Local }, squareAccessToken: null, cancellationToken);
    }

    public Task<LinklyConnectionTestResult> TestLinklyConnectionAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        return linklyTerminalClient.TestConnectionAsync(host, port, timeout, cancellationToken);
    }

    public async Task<LinklyConnectionTestResult> PairLinklyCloudAsync(
        CardTerminalEnvironment environment,
        string pairCode,
        string? username,
        string? password,
        CancellationToken cancellationToken = default)
    {
        if (linklyCloudApiClient is null)
        {
            LogLinklyCloudSetup($"pair blocked environment={environment} reason=missing-dependencies");
            return new LinklyConnectionTestResult(false, T("settings.linklyCloud.unavailable", "Linkly Cloud setup is unavailable."));
        }

        if (string.IsNullOrWhiteSpace(pairCode))
        {
            LogLinklyCloudSetup($"pair blocked environment={environment} reason=missing-pair-code");
            return new LinklyConnectionTestResult(false, T("settings.linklyCloud.pairCodeRequired", "Pair code is required."));
        }

        var savedCredential = await settingsStore.GetLinklyCloudCredentialAsync(environment, cancellationToken);
        var resolvedUsername = ResolveCredentialPart(username, savedCredential.Username);
        var resolvedPassword = ResolveCredentialPart(password, savedCredential.Password);
        if (string.IsNullOrWhiteSpace(resolvedUsername) || string.IsNullOrWhiteSpace(resolvedPassword))
        {
            LogLinklyCloudSetup($"pair blocked environment={environment} reason=missing-local-credential hasUsername={!string.IsNullOrWhiteSpace(resolvedUsername)} hasPassword={!string.IsNullOrWhiteSpace(resolvedPassword)}");
            return new LinklyConnectionTestResult(false, T("settings.linklyCloud.localCredentialMissing", "Save the Linkly Cloud API username and password first."));
        }

        try
        {
            LogLinklyCloudSetup($"pair start environment={environment} hasPairCode=true credentialSource=local hasUsername=true");
            var authBaseUrl = CardTerminalSettings.ResolveLinklyCloudAuthBaseUrl(environment);
            var authValidationMessage = CardTerminalSettings.ValidateLinklyCloudAuthBaseUrl(environment, authBaseUrl);
            if (!string.IsNullOrWhiteSpace(authValidationMessage))
            {
                LogLinklyCloudSetup($"pair blocked environment={environment} reason=invalid-auth-endpoint");
                return new LinklyConnectionTestResult(false, authValidationMessage);
            }

            var secret = await linklyCloudApiClient.PairAsync(
                authBaseUrl,
                resolvedUsername,
                resolvedPassword,
                pairCode,
                cancellationToken);
            await settingsStore.SaveLinklyCloudSecretAsync(environment, secret, cancellationToken);
            LogLinklyCloudSetup($"pair succeeded environment={environment} secretSaved=true");
            return new LinklyConnectionTestResult(true, T("settings.linklyCloud.paired", "Linkly Cloud terminal paired."));
        }
        catch (LinklyCloudApiException ex)
        {
            LogLinklyCloudSetup($"pair failed environment={environment} source=linkly authFailure={ex.IsAuthenticationFailure} error={ex.GetType().Name}");
            return new LinklyConnectionTestResult(false, ex.IsAuthenticationFailure
                ? "Linkly Cloud pairing failed. Check the pair code."
                : ex.Message);
        }
    }

    public async Task<LinklyCloudCredentialSettings> LoadLinklyCloudCredentialAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        var credential = await settingsStore.GetLinklyCloudCredentialAsync(environment, cancellationToken);
        LogLinklyCloudSetup($"load local credential environment={environment} hasUsername={!string.IsNullOrWhiteSpace(credential.Username)} hasPassword={credential.HasProtectedPassword}");
        return credential with { Password = null };
    }

    public Task SaveLinklyCloudCredentialAsync(
        CardTerminalEnvironment environment,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        LogLinklyCloudSetup($"save local credential requested environment={environment} hasUsername={!string.IsNullOrWhiteSpace(username)} hasPassword={!string.IsNullOrWhiteSpace(password)}");
        return settingsStore.SaveLinklyCloudCredentialAsync(environment, username, password, cancellationToken);
    }

    public async Task<LinklyConnectionTestResult> TestLinklyCloudConnectionAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        if (linklyCloudTerminalClient is null || deviceAuthorizationState?.Current is null)
        {
            LogLinklyCloudSetup($"test blocked environment={environment} reason=missing-dependencies-or-device-state");
            return new LinklyConnectionTestResult(false, T("settings.linklyCloud.unavailable", "Linkly Cloud setup is unavailable."));
        }

        LogLinklyCloudSetup($"test start environment={environment} store={LogValue(deviceAuthorizationState.Current.StoreCode)} device={LogValue(deviceAuthorizationState.Current.DeviceCode)}");
        var authBaseUrl = CardTerminalSettings.ResolveLinklyCloudAuthBaseUrl(environment);
        var restBaseUrl = CardTerminalSettings.ResolveLinklyCloudRestBaseUrl(environment);
        var authValidationMessage = CardTerminalSettings.ValidateLinklyCloudAuthBaseUrl(environment, authBaseUrl);
        if (!string.IsNullOrWhiteSpace(authValidationMessage))
        {
            LogLinklyCloudSetup($"test blocked environment={environment} reason=invalid-auth-endpoint");
            return new LinklyConnectionTestResult(false, authValidationMessage);
        }

        var restValidationMessage = CardTerminalSettings.ValidateLinklyCloudRestBaseUrl(environment, restBaseUrl);
        if (!string.IsNullOrWhiteSpace(restValidationMessage))
        {
            LogLinklyCloudSetup($"test blocked environment={environment} reason=invalid-rest-endpoint");
            return new LinklyConnectionTestResult(false, restValidationMessage);
        }

        var settings = await settingsStore.GetSettingsAsync(cancellationToken);
        var secret = await settingsStore.GetLinklyCloudSecretAsync(environment, cancellationToken);
        settings = settings with
        {
            Environment = environment,
            LinklyConnectionMode = LinklyConnectionMode.Cloud,
            LinklyCloudSecret = secret,
            // 统一使用按环境解析后的 endpoint，避免调用链里新旧变量优先级不一致。
            LinklyCloudAuthBaseUrl = authBaseUrl,
            LinklyCloudRestBaseUrl = restBaseUrl,
            LinklyPosVendorId = CardTerminalSettings.ResolveLinklyPosVendorId(
                environment,
                settings.LinklyPosVendorId)
        };
        var result = await linklyCloudTerminalClient.TestConnectionAsync(
            settings,
            deviceAuthorizationState.Current.StoreCode,
            deviceAuthorizationState.Current.DeviceCode,
            cancellationToken);
        LogLinklyCloudSetup($"test completed environment={environment} store={LogValue(deviceAuthorizationState.Current.StoreCode)} device={LogValue(deviceAuthorizationState.Current.DeviceCode)} success={result.Succeeded}");
        return result;
    }

    public async Task<LinklyConnectionTestResult> TestLinklyCloudBackendConnectionAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        if (linklyBackendTerminalClient is null)
        {
            LogLinklyCloudSetup($"backend test blocked environment={environment} reason=missing-backend-client");
            return new LinklyConnectionTestResult(false, T("settings.linklyCloud.unavailable", "Linkly Cloud setup is unavailable."));
        }

        LogLinklyCloudSetup($"backend test start environment={environment}");
        var result = await linklyBackendTerminalClient.TestConnectionAsync(environment, cancellationToken);
        LogLinklyCloudSetup($"backend test completed environment={environment} success={result.Succeeded}");
        return result;
    }

    public async Task<bool> HasLinklyCloudSecretAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        var hasSecret = !string.IsNullOrWhiteSpace(await settingsStore.GetLinklyCloudSecretAsync(
            environment,
            cancellationToken));
        LogLinklyCloudSetup($"secret status environment={environment} hasSecret={hasSecret}");
        return hasSecret;
    }

    public Task SaveLinklyCloudAsync(
        CardTerminalConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var normalizedMode = CardTerminalSettings.NormalizeLinklyConnectionMode(configuration.LinklyConnectionMode);
        var cloudMode = normalizedMode == LinklyConnectionMode.CloudBackendAsync
            ? LinklyConnectionMode.CloudBackendAsync
            : LinklyConnectionMode.CloudDirectSync;
        LogLinklyCloudSetup($"save configuration environment={configuration.Environment} mode={cloudMode}");
        return settingsStore.SaveAsync(configuration with { LinklyConnectionMode = cloudMode }, squareAccessToken: null, cancellationToken);
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

    private static void LogLinklyCloudSetup(string message)
    {
        ConsoleLog.Write("LinklyCloud", $"settings {message}");
    }

    private static string DescribeTokenSource(string? accessToken)
    {
        return string.IsNullOrWhiteSpace(accessToken) ? "stored" : "provided";
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value;
    }

    private static string? ResolveCredentialPart(string? currentValue, string? savedValue)
    {
        return string.IsNullOrWhiteSpace(currentValue) ? savedValue?.Trim() : currentValue.Trim();
    }

    private static bool IsUuidV4(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return Guid.TryParse(trimmed, out _) &&
            trimmed.Length == 36 &&
            trimmed[14] == '4' &&
            trimmed[19] is '8' or '9' or 'a' or 'A' or 'b' or 'B';
    }

    private string T(string key, string fallback)
    {
        return localization?.T(key) ?? fallback;
    }
}
