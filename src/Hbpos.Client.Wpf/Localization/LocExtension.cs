using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows.Data;
using System.Windows.Markup;

namespace Hbpos.Client.Wpf.Localization;

public sealed class LocalizationResourceProvider : INotifyPropertyChanged
{
    public static LocalizationResourceProvider Instance { get; } = new();

    private static readonly ResourceManager[] FallbackResourceManagers =
    [
        new("Hbpos.Client.Wpf.Resources.Strings", typeof(LocalizationResourceProvider).Assembly),
        new("Hbpos.Client.Wpf.Resources.SettingsStrings", typeof(LocalizationResourceProvider).Assembly)
    ];

    private ILocalizationService? _localization;

    private LocalizationResourceProvider()
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CultureInfo CurrentCulture => _localization?.CurrentCulture ?? CultureInfo.GetCultureInfo(LocalizationService.DefaultCultureName);

    public string this[string key] =>
        _localization?.T(key) ?? GetFallbackString(key) ?? $"[[{key}]]";

    public void Configure(ILocalizationService localization)
    {
        if (_localization is not null)
        {
            _localization.CultureChanged -= OnCultureChanged;
        }

        _localization = localization;
        _localization.CultureChanged += OnCultureChanged;
        NotifyCultureChanged();
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        NotifyCultureChanged();
    }

    private void NotifyCultureChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentCulture)));
    }

    private string? GetFallbackString(string key)
    {
        foreach (var resourceManager in FallbackResourceManagers)
        {
            try
            {
                var value = resourceManager.GetString(key, CurrentCulture);
                if (value is not null)
                {
                    return value;
                }
            }
            catch (MissingManifestResourceException)
            {
            }
        }

        return null;
    }
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationResourceProvider.Instance,
            Mode = BindingMode.OneWay
        };

        return binding.ProvideValue(serviceProvider);
    }
}
