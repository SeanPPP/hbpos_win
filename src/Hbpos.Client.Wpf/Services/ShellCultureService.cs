using Hbpos.Client.Wpf.Localization;

namespace Hbpos.Client.Wpf.Services;

public interface IShellCultureService
{
    Task<string> RestoreAsync(
        AppStartupOptions startupOptions,
        bool schemaReady,
        CancellationToken cancellationToken = default);

    Task<string> ApplyAsync(
        string cultureName,
        bool persist,
        bool schemaReady,
        CancellationToken cancellationToken = default);

    Task<string> ToggleAsync(bool schemaReady, CancellationToken cancellationToken = default);
}

public sealed class ShellCultureService(
    ILocalizationService localization,
    ILocalAppSettingsRepository settingsRepository) : IShellCultureService
{
    private const string LanguageSettingKey = "Language";

    public async Task<string> RestoreAsync(
        AppStartupOptions startupOptions,
        bool schemaReady,
        CancellationToken cancellationToken = default)
    {
        if (startupOptions.PreviewMode)
        {
            return await ApplyAsync(
                startupOptions.InitialCulture ?? LocalizationService.DefaultCultureName,
                persist: false,
                schemaReady,
                cancellationToken);
        }

        var cultureName = startupOptions.InitialCulture;
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            cultureName = await settingsRepository.GetValueAsync(LanguageSettingKey, cancellationToken)
                ?? LocalizationService.DefaultCultureName;
        }

        return await ApplyAsync(
            cultureName,
            persist: startupOptions.InitialCulture is not null,
            schemaReady,
            cancellationToken);
    }

    public async Task<string> ApplyAsync(
        string cultureName,
        bool persist,
        bool schemaReady,
        CancellationToken cancellationToken = default)
    {
        try
        {
            localization.SetCulture(cultureName);
        }
        catch (ArgumentException)
        {
            localization.SetCulture(LocalizationService.DefaultCultureName);
        }

        if (persist && schemaReady)
        {
            await settingsRepository.SetValueAsync(
                LanguageSettingKey,
                localization.CurrentCulture.Name,
                cancellationToken);
        }

        return localization.CurrentCulture.Name;
    }

    public Task<string> ToggleAsync(bool schemaReady, CancellationToken cancellationToken = default)
    {
        var nextCultureName = string.Equals(
            localization.CurrentCulture.Name,
            LocalizationService.ChineseCultureName,
            StringComparison.OrdinalIgnoreCase)
            ? LocalizationService.DefaultCultureName
            : LocalizationService.ChineseCultureName;

        return ApplyAsync(nextCultureName, persist: true, schemaReady, cancellationToken);
    }
}
