using System.Globalization;
using System.Windows.Data;
using Hbpos.Client.Wpf.Localization;

namespace Hbpos.Client.Wpf.Converters;

/// <summary>
/// 将本地化键（resx key）解析为本地化文本。
/// 通过 LocalizationResourceProvider.Instance 索引器按当前文化查找翻译。
/// 注意：转换器只在绑定源（如 TitleKey）变化时重新执行；
/// 不会因文化变更而自动刷新（实际场景中运行时文化极少切换）。
/// </summary>
public sealed class LocalizationKeyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || string.IsNullOrWhiteSpace(key))
            return string.Empty;

        return LocalizationResourceProvider.Instance[key];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
