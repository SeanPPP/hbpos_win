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
    private const string LinklyConnectionModeKey = "CardTerminal:LinklyConnectionMode";
    private const string LinklyConnectionModePriorityKey = "CardTerminal:LinklyConnectionModePriority";
    private const string LinklyCloudSecretKeyPrefix = "CardTerminal:LinklyCloudSecretProtected:";
    private const string LinklyCloudUsernameKeyPrefix = "CardTerminal:LinklyCloudUsername:";
    private const string LinklyCloudPasswordKeyPrefix = "CardTerminal:LinklyCloudPasswordProtected:";
    private const string LinklyCloudPosIdKeyPrefix = "CardTerminal:LinklyCloudPosId:";
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
        var linklyConnectionMode = ParseLinklyConnectionMode(
            await settingsRepository.GetValueAsync(LinklyConnectionModeKey, cancellationToken),
            environmentSettings.LinklyConnectionMode);
        var linklyConnectionModePriority = CardTerminalSettings.ParseLinklyConnectionModePriority(
            await settingsRepository.GetValueAsync(LinklyConnectionModePriorityKey, cancellationToken),
            linklyConnectionMode);
        linklyConnectionMode = linklyConnectionModePriority[0];
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
        var protectedLinklySecret = await ReadProtectedLinklyCloudSecretAsync(terminalEnvironment, cancellationToken);

        return new CardTerminalConfiguration(
            processor,
            terminalEnvironment,
            linklyHost,
            linklyPort,
            squareLocationId,
            squareDeviceId,
            !string.IsNullOrWhiteSpace(protectedToken),
            timeoutSeconds,
            linklyConnectionMode,
            !string.IsNullOrWhiteSpace(protectedLinklySecret),
            linklyConnectionModePriority);
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
        var linklyPriority = configuration.LinklyConnectionModePriority is null ||
            configuration.LinklyConnectionModePriority.Count == 0
            ? CardTerminalSettings.NormalizeLinklyConnectionModePriority(null, configuration.LinklyConnectionMode)
            : CardTerminalSettings.NormalizeLinklyConnectionModePriority(
                configuration.LinklyConnectionModePriority,
                configuration.LinklyConnectionModePriority[0]);
        var primaryLinklyMode = linklyPriority[0];
        await settingsRepository.SetValueAsync(
            LinklyConnectionModeKey,
            CardTerminalSettings.FormatLinklyConnectionMode(primaryLinklyMode),
            cancellationToken);
        await settingsRepository.SetValueAsync(
            LinklyConnectionModePriorityKey,
            CardTerminalSettings.FormatLinklyConnectionModePriority(linklyPriority),
            cancellationToken);
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

    public async Task<string?> GetLinklyCloudSecretAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        var protectedSecret = await ReadProtectedLinklyCloudSecretAsync(environment, cancellationToken);
        return string.IsNullOrWhiteSpace(protectedSecret)
            ? null
            : protector.Unprotect(protectedSecret);
    }

    public async Task SaveLinklyCloudSecretAsync(
        CardTerminalEnvironment environment,
        string secret,
        CancellationToken cancellationToken = default)
    {
        var protectedSecret = protector.Protect(secret.Trim());
        if (string.IsNullOrWhiteSpace(protectedSecret))
        {
            throw new InvalidOperationException("Linkly Cloud secret could not be protected.");
        }

        await settingsRepository.SetValueAsync(
            GetLinklyCloudSecretKey(environment),
            protectedSecret,
            cancellationToken);
        LogLinklyCloud($"protected secret saved environment={environment}");
    }

    public async Task<string> GetOrCreateLinklyCloudPosIdAsync(
        CardTerminalEnvironment environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken = default)
    {
        var key = GetLinklyCloudPosIdKey(environment, storeCode, deviceCode);
        var existing = await settingsRepository.GetValueAsync(key, cancellationToken);
        if (IsUuidV4(existing))
        {
            LogLinklyCloud($"posId reused environment={environment} store={LogValue(storeCode)} device={LogValue(deviceCode)} posId={ShortId(existing)}");
            return existing!.Trim();
        }

        // 仅生产环境兼容旧版无环境 key，读取成功后立即写入新 key，避免沙箱误用生产 POS ID。
        if (environment == CardTerminalEnvironment.Production)
        {
            var legacyKey = GetLegacyLinklyCloudPosIdKey(storeCode, deviceCode);
            var legacy = await settingsRepository.GetValueAsync(legacyKey, cancellationToken);
            if (IsUuidV4(legacy))
            {
                var migrated = legacy!.Trim();
                await settingsRepository.SetValueAsync(key, migrated, cancellationToken);
                LogLinklyCloud($"posId migrated environment={environment} store={LogValue(storeCode)} device={LogValue(deviceCode)} posId={ShortId(migrated)}");
                return migrated;
            }
        }

        var posId = Guid.NewGuid().ToString("D");
        await settingsRepository.SetValueAsync(key, posId, cancellationToken);
        LogLinklyCloud($"posId generated environment={environment} store={LogValue(storeCode)} device={LogValue(deviceCode)} posId={ShortId(posId)} replacedInvalid={!string.IsNullOrWhiteSpace(existing)}");
        return posId;
    }

    public async Task<LinklyCloudCredentialSettings> GetLinklyCloudCredentialAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        var username = NormalizeOptional(await settingsRepository.GetValueAsync(
            GetLinklyCloudUsernameKey(environment),
            cancellationToken));
        var protectedPassword = await settingsRepository.GetValueAsync(
            GetLinklyCloudPasswordKey(environment),
            cancellationToken);
        var password = string.IsNullOrWhiteSpace(protectedPassword)
            ? null
            : protector.Unprotect(protectedPassword);

        return new LinklyCloudCredentialSettings(
            username,
            password,
            !string.IsNullOrWhiteSpace(protectedPassword));
    }

    public async Task SaveLinklyCloudCredentialAsync(
        CardTerminalEnvironment environment,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new InvalidOperationException("Linkly Cloud username is required.");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Linkly Cloud password is required.");
        }

        var protectedPassword = protector.Protect(password.Trim());
        if (string.IsNullOrWhiteSpace(protectedPassword))
        {
            throw new InvalidOperationException("Linkly Cloud password could not be protected.");
        }

        await settingsRepository.SetValueAsync(
            GetLinklyCloudUsernameKey(environment),
            username.Trim(),
            cancellationToken);
        await settingsRepository.SetValueAsync(
            GetLinklyCloudPasswordKey(environment),
            protectedPassword,
            cancellationToken);
        LogLinklyCloud($"protected cloud api credential saved environment={environment} hasUsername=true");
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

    private Task<string?> ReadProtectedLinklyCloudSecretAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken)
    {
        return settingsRepository.GetValueAsync(GetLinklyCloudSecretKey(environment), cancellationToken);
    }

    private static string GetSquareTokenKey(CardTerminalEnvironment environment)
    {
        return $"{SquareTokenKeyPrefix}{environment}";
    }

    private static string GetLinklyCloudSecretKey(CardTerminalEnvironment environment)
    {
        return $"{LinklyCloudSecretKeyPrefix}{environment}";
    }

    private static string GetLinklyCloudUsernameKey(CardTerminalEnvironment environment)
    {
        return $"{LinklyCloudUsernameKeyPrefix}{environment}";
    }

    private static string GetLinklyCloudPasswordKey(CardTerminalEnvironment environment)
    {
        return $"{LinklyCloudPasswordKeyPrefix}{environment}";
    }

    private static string GetLinklyCloudPosIdKey(
        CardTerminalEnvironment environment,
        string storeCode,
        string deviceCode)
    {
        return $"{LinklyCloudPosIdKeyPrefix}{environment}:{NormalizeKeyPart(storeCode)}:{NormalizeKeyPart(deviceCode)}";
    }

    private static string GetLegacyLinklyCloudPosIdKey(string storeCode, string deviceCode)
    {
        return $"{LinklyCloudPosIdKeyPrefix}{NormalizeKeyPart(storeCode)}:{NormalizeKeyPart(deviceCode)}";
    }

    public async Task<CardTerminalSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await LoadAsync(cancellationToken);
        var squareAccessToken = await GetSquareAccessTokenAsync(cancellationToken);
        var linklyCloudSecret = await GetLinklyCloudSecretAsync(configuration.Environment, cancellationToken);
        var environmentSettings = CardTerminalSettings.FromEnvironment();

        return new CardTerminalSettings(
            configuration.Processor,
            configuration.Environment,
            configuration.LinklyHost,
            configuration.LinklyPort,
            squareAccessToken,
            configuration.SquareLocationId,
            configuration.SquareDeviceId,
            CardTerminalSettings.GetSquareApiBaseUrl(configuration.Environment),
            TimeSpan.FromSeconds(NormalizeTimeoutSeconds(configuration.TerminalTimeoutSeconds)),
            CardTerminalSettings.NormalizeLinklyConnectionMode(configuration.LinklyConnectionMode),
            linklyCloudSecret,
            CardTerminalSettings.ResolveLinklyCloudAuthBaseUrl(configuration.Environment),
            CardTerminalSettings.ResolveLinklyCloudRestBaseUrl(configuration.Environment),
            environmentSettings.LinklyPosName,
            environmentSettings.LinklyPosVersion,
            CardTerminalSettings.ResolveLinklyPosVendorId(
                configuration.Environment,
                environmentSettings.LinklyPosVendorId),
            CardTerminalSettings.NormalizeLinklyConnectionModePriority(
                configuration.LinklyConnectionModePriority,
                configuration.LinklyConnectionMode));
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

    private static LinklyConnectionMode ParseLinklyConnectionMode(string? value, LinklyConnectionMode fallback)
    {
        return CardTerminalSettings.NormalizeLinklyConnectionMode(value, fallback);
    }

    private static string NormalizeText(string? value, string? fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback ?? string.Empty : value.Trim();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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

    private static string NormalizeKeyPart(string value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        return string.Concat(trimmed.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
    }

    private static bool IsUuidV4(string? value)
    {
        return Guid.TryParse(value, out var parsed) &&
            parsed.ToString("D").Equals(value.Trim(), StringComparison.OrdinalIgnoreCase) &&
            ((parsed.ToByteArray()[7] >> 4) & 0x0F) == 4;
    }

    private static void LogLinklyCloud(string message)
    {
        ConsoleLog.Write("LinklyCloud", $"settings-store {message}");
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value.Trim();
    }

    private static string ShortId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<null>";
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 8 ? trimmed : $"{trimmed[..8]}...";
    }
}
