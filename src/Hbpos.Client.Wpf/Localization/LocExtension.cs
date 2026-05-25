using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows.Data;
using System.Windows.Markup;

namespace Hbpos.Client.Wpf.Localization;

public sealed class LocalizationResourceProvider : INotifyPropertyChanged
{
    public static LocalizationResourceProvider Instance { get; } = new();

    private static readonly ResourceManager FallbackResourceManager = new(
        "Hbpos.Client.Wpf.Resources.Strings",
        typeof(LocalizationResourceProvider).Assembly);

    private ILocalizationService? _localization;

    private LocalizationResourceProvider()
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CultureInfo CurrentCulture => _localization?.CurrentCulture ?? CultureInfo.GetCultureInfo(LocalizationService.DefaultCultureName);

    public string this[string key] =>
        _localization?.T(key) ?? FallbackResourceManager.GetString(key, CurrentCulture) ?? $"[[{key}]]";

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
