using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Hbpos.Client.Wpf.Converters;

public sealed class ProductThumbnailImageSourceConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private const string DataImagePrefix = "data:image/";

    public int DecodePixelWidth { get; set; } = 72;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string uriText || string.IsNullOrWhiteSpace(uriText))
        {
            return null;
        }

        var sourceText = uriText.Trim();
        var imageRequest = TryCreateImageRequest(sourceText);
        if (imageRequest is null)
        {
            return null;
        }

        var cacheKey = $"{Math.Max(1, DecodePixelWidth)}|{imageRequest.CacheKey}";
        if (Cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var imageSource = CreateImageSource(imageRequest);
        if (imageSource is not null)
        {
            Cache.TryAdd(cacheKey, imageSource);
        }

        return imageSource;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private ImageSource? CreateImageSource(ImageRequest imageRequest)
    {
        try
        {
            if (imageRequest.ImageBytes is not null)
            {
                using var stream = new MemoryStream(imageRequest.ImageBytes, writable: false);
                var streamImage = new BitmapImage();
                streamImage.BeginInit();
                streamImage.StreamSource = stream;
                streamImage.CacheOption = BitmapCacheOption.OnLoad;
                streamImage.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                streamImage.EndInit();
                if (streamImage.CanFreeze)
                {
                    streamImage.Freeze();
                }

                return streamImage;
            }

            if (imageRequest.Uri is null)
            {
                return null;
            }

            var uriImage = new BitmapImage();
            uriImage.BeginInit();
            uriImage.UriSource = imageRequest.Uri;
            uriImage.DecodePixelWidth = Math.Max(1, DecodePixelWidth);
            uriImage.CreateOptions = BitmapCreateOptions.DelayCreation;
            uriImage.CacheOption = BitmapCacheOption.Default;
            uriImage.EndInit();
            if (uriImage.CanFreeze)
            {
                uriImage.Freeze();
            }

            return uriImage;
        }
        catch
        {
            return null;
        }
    }

    private static ImageRequest? TryCreateImageRequest(string sourceText)
    {
        if (TryCreateDataImageRequest(sourceText, out var dataRequest))
        {
            return dataRequest;
        }

        if (TryCreateLocalFileRequest(sourceText, out var fileRequest))
        {
            return fileRequest;
        }

        if (Uri.TryCreate(sourceText, UriKind.Absolute, out var absoluteUri))
        {
            if (absoluteUri.IsFile)
            {
                return TryCreateLocalFileRequest(absoluteUri.LocalPath, out fileRequest)
                    ? fileRequest
                    : null;
            }

            if (string.Equals(absoluteUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(absoluteUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return new ImageRequest(absoluteUri.AbsoluteUri, absoluteUri, null);
            }

            return null;
        }

        if (sourceText.StartsWith("/", StringComparison.Ordinal) &&
            TryResolveRelativeApiPath(sourceText, out var relativeUri))
        {
            return new ImageRequest(relativeUri.AbsoluteUri, relativeUri, null);
        }

        return null;
    }

    private static bool TryCreateDataImageRequest(string sourceText, out ImageRequest? imageRequest)
    {
        imageRequest = null;
        if (!sourceText.StartsWith(DataImagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var commaIndex = sourceText.IndexOf(',');
        if (commaIndex <= 0)
        {
            return false;
        }

        var metadata = sourceText[..commaIndex];
        if (!metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var bytes = System.Convert.FromBase64String(sourceText[(commaIndex + 1)..]);
            imageRequest = new ImageRequest(sourceText, null, bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryCreateLocalFileRequest(string sourceText, out ImageRequest? imageRequest)
    {
        imageRequest = null;
        if (!Path.IsPathRooted(sourceText) || !File.Exists(sourceText))
        {
            return false;
        }

        try
        {
            imageRequest = new ImageRequest(Path.GetFullPath(sourceText), null, File.ReadAllBytes(sourceText));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryResolveRelativeApiPath(string sourceText, out Uri resolvedUri)
    {
        resolvedUri = default!;
        var configuredBaseUrl = Environment.GetEnvironmentVariable("HBPOS_API_BASE_URL");
        if (string.IsNullOrWhiteSpace(configuredBaseUrl))
        {
            return false;
        }

        var normalizedBaseUrl = configuredBaseUrl.Trim();
        if (!normalizedBaseUrl.EndsWith('/'))
        {
            normalizedBaseUrl += "/";
        }

        if (!Uri.TryCreate(normalizedBaseUrl, UriKind.Absolute, out var baseUri))
        {
            return false;
        }

        if (!Uri.TryCreate(baseUri, sourceText.TrimStart(['/']), out var createdUri) || createdUri is null)
        {
            return false;
        }

        resolvedUri = createdUri;
        return true;
    }

    private sealed record ImageRequest(string CacheKey, Uri? Uri, byte[]? ImageBytes);
}
