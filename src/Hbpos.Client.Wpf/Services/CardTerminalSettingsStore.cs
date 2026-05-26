namespace Hbpos.Client.Wpf.Services;

public sealed class CardTerminalSettingsStore(
    ILocalAppSettingsRepository settingsRepository,
    IDeviceAuthorizationProtector protector,
    ISquareTokenApiClient? squareTokenApiClient = null) : ICardTerminalSettingsStore
{
    private const string ProcessorKey = "CardTerminal:Processor";
    private const string EnvironmentKey = "CardTerminal:Environment";
    private const string LinklyHostKey = "CardTerminal:LinklyHost";
    private const string LinklyPortKey = "CardTerminal:LinklyPort";
    private const string LegacySquareTokenKey = "CardTerminal:SquareAccessTokenProtected";
    private const string SquareTokenKeyPrefix = "CardTerminal:SquareAccessTokenProtected:";
    private const string SquareLocationIdKey = "CardTerminal:SquareLocationId";
    private const string SquareDeviceIdKey = "CardTerminal:SquareDeviceId";
    private const string TimeoutSecondsKey = "CardTerminal:TimeoutSeconds";

    public async Task<CardTerminalConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        var environmentSettings = CardTerminalSettings.FromEnvironment();

        var processor = ParseProcessor(
            await settingsRepository.GetValueAsync(ProcessorKey, cancellationToken),
            environmentSettings.Processor);
        var terminalEnvironment = ParseEnvironment(
            await settingsRepository.GetValueAsync(EnvironmentKey, cancellationToken),
            environmentSettings.Environment);
        var linklyHost = NormalizeText(
            await settingsRepository.GetValueAsync(LinklyHostKey, cancellationToken),
            environmentSettings.LinklyHost);
        var linklyPort = ParsePort(
            await settingsRepository.GetValueAsync(LinklyPortKey, cancellationToken),
            environmentSettings.LinklyPort);
        var squareLocationId = NormalizeText(
            await settingsRepository.GetValueAsync(SquareLocationIdKey, cancellationToken),
            environmentSettings.SquareLocationId);
        var squareDeviceId = NormalizeText(
            await settingsRepository.GetValueAsync(SquareDeviceIdKey, cancellationToken),
            environmentSettings.SquareDeviceId);
        var timeoutSeconds = ParseTimeoutSeconds(
            await settingsRepository.GetValueAsync(TimeoutSecondsKey, cancellationToken),
            (int)Math.Max(1, environmentSettings.TerminalTimeout.TotalSeconds));
        var protectedToken = await ReadProtectedSquareAccessTokenAsync(terminalEnvironment, cancellationToken);

        return new CardTerminalConfiguration(
            processor,
            terminalEnvironment,
            linklyHost,
            linklyPort,
            squareLocationId,
            squareDeviceId,
            !string.IsNullOrWhiteSpace(protectedToken),
            timeoutSeconds);
    }

    public async Task SaveAsync(
        CardTerminalConfiguration configuration,
        string? squareAccessToken,
        CancellationToken cancellationToken = default)
    {
        await settingsRepository.SetValueAsync(ProcessorKey, configuration.Processor.ToString(), cancellationToken);
        await settingsRepository.SetValueAsync(EnvironmentKey, configuration.Environment.ToString(), cancellationToken);
        await settingsRepository.SetValueAsync(LinklyHostKey, NormalizeText(configuration.LinklyHost, CardTerminalConfiguration.Default.LinklyHost), cancellationToken);
        await settingsRepository.SetValueAsync(LinklyPortKey, NormalizePort(configuration.LinklyPort).ToString(), cancellationToken);
        await settingsRepository.SetValueAsync(SquareLocationIdKey, configuration.SquareLocationId?.Trim() ?? string.Empty, cancellationToken);
        await settingsRepository.SetValueAsync(SquareDeviceIdKey, configuration.SquareDeviceId?.Trim() ?? string.Empty, cancellationToken);
        await settingsRepository.SetValueAsync(TimeoutSecondsKey, NormalizeTimeoutSeconds(configuration.TerminalTimeoutSeconds).ToString(), cancellationToken);

        if (!string.IsNullOrWhiteSpace(squareAccessToken))
        {
            await SaveProtectedSquareAccessTokenAsync(
                configuration.Environment,
                squareAccessToken,
                cancellationToken);
        }
    }

    public async Task<string?> GetSquareAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await LoadAsync(cancellationToken);
        return await GetSquareAccessTokenAsync(
            configuration.Environment,
            forceRefresh: false,
            cancellationToken);
    }

    public Task<string?> GetTokenAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        return GetSquareAccessTokenAsync(environment, forceRefresh: false, cancellationToken);
    }

    public Task<string?> RefreshTokenAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        return GetSquareAccessTokenAsync(environment, forceRefresh: true, cancellationToken);
    }

    public async Task<string?> GetSquareAccessTokenAsync(
        CardTerminalEnvironment environment,
        bool forceRefresh,
        CancellationToken cancellationToken = default)
    {
        var localToken = await ReadLocalSquareAccessTokenAsync(environment, cancellationToken);
        if (!forceRefresh && !string.IsNullOrWhiteSpace(localToken))
        {
            return localToken;
        }

        if (squareTokenApiClient is not null)
        {
            try
            {
                var remoteToken = await squareTokenApiClient.GetTokenAsync(environment, cancellationToken);
                if (!string.IsNullOrWhiteSpace(remoteToken.AccessToken))
                {
                    await SaveProtectedSquareAccessTokenAsync(
                        environment,
                        remoteToken.AccessToken,
                        cancellationToken);
                    return remoteToken.AccessToken.Trim();
                }
            }
            catch
            {
                if (forceRefresh)
                {
                    return null;
                }
            }
        }

        return null;
    }

    private async Task<string?> ReadLocalSquareAccessTokenAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken)
    {
        var protectedToken = await ReadProtectedSquareAccessTokenAsync(environment, cancellationToken);
        if (!string.IsNullOrWhiteSpace(protectedToken))
        {
            return protector.Unprotect(protectedToken);
        }

        return null;
    }

    private async Task SaveProtectedSquareAccessTokenAsync(
        CardTerminalEnvironment environment,
        string squareAccessToken,
        CancellationToken cancellationToken)
    {
        var protectedToken = protector.Protect(squareAccessToken.Trim());
        if (string.IsNullOrWhiteSpace(protectedToken))
        {
            throw new InvalidOperationException("Square access token could not be protected.");
        }

        await settingsRepository.SetValueAsync(GetSquareTokenKey(environment), protectedToken, cancellationToken);
    }

    private async Task<string?> ReadProtectedSquareAccessTokenAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken)
    {
        var protectedToken = await settingsRepository.GetValueAsync(GetSquareTokenKey(environment), cancellationToken);
        if (!string.IsNullOrWhiteSpace(protectedToken))
        {
            return protectedToken;
        }

        return environment == CardTerminalEnvironment.Production
            ? await settingsRepository.GetValueAsync(LegacySquareTokenKey, cancellationToken)
            : null;
    }

    private static string GetSquareTokenKey(CardTerminalEnvironment environment)
    {
        return $"{SquareTokenKeyPrefix}{environment}";
    }

    public async Task<CardTerminalSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await LoadAsync(cancellationToken);
        var squareAccessToken = await GetSquareAccessTokenAsync(cancellationToken);

        return new CardTerminalSettings(
            configuration.Processor,
            configuration.Environment,
            configuration.LinklyHost,
            configuration.LinklyPort,
            squareAccessToken,
            configuration.SquareLocationId,
            configuration.SquareDeviceId,
            CardTerminalSettings.GetSquareApiBaseUrl(configuration.Environment),
            TimeSpan.FromSeconds(NormalizeTimeoutSeconds(configuration.TerminalTimeoutSeconds)));
    }

    private static CardProcessorKind ParseProcessor(string? value, CardProcessorKind fallback)
    {
        return Enum.TryParse<CardProcessorKind>(value, ignoreCase: true, out var processor)
            ? processor
            : fallback;
    }

    private static CardTerminalEnvironment ParseEnvironment(string? value, CardTerminalEnvironment fallback)
    {
        return Enum.TryParse<CardTerminalEnvironment>(value, ignoreCase: true, out var environment)
            ? environment
            : fallback;
    }

    private static string NormalizeText(string? value, string? fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback ?? string.Empty : value.Trim();
    }

    private static int ParsePort(string? value, int fallback)
    {
        return int.TryParse(value, out var port) ? NormalizePort(port) : NormalizePort(fallback);
    }

    private static int NormalizePort(int port)
    {
        return port is > 0 and <= 65535 ? port : 2011;
    }

    private static int ParseTimeoutSeconds(string? value, int fallback)
    {
        return int.TryParse(value, out var seconds) ? NormalizeTimeoutSeconds(seconds) : NormalizeTimeoutSeconds(fallback);
    }

    private static int NormalizeTimeoutSeconds(int seconds)
    {
        return seconds > 0 ? seconds : CardTerminalConfiguration.Default.TerminalTimeoutSeconds;
    }
}
