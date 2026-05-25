using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Resources;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.Converters;

public sealed class ProductThumbnailImageSourceConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> LoggedDiagnostics = new(StringComparer.Ordinal);
    private static readonly HttpClient RemoteImageHttpClient = CreateRemoteImageHttpClient();
    private static readonly object RemoteImageLoaderLock = new();
    private static readonly SemaphoreSlim RemoteImageDownloadGate = new(4);
    private static Func<Uri, CancellationToken, Task<byte[]>> RemoteImageBytesLoader = DownloadRemoteImageBytesAsync;
    private static int nextAsyncLoadVersion;
    private const string DataImagePrefix = "data:image/";
    private const int MaxLoggedValueLength = 300;
    private static readonly TimeSpan RemoteImageDownloadTimeout = TimeSpan.FromSeconds(4);

    public int DecodePixelWidth { get; set; } = 72;

    public static readonly DependencyProperty AsyncSourceTextProperty =
        DependencyProperty.RegisterAttached(
            "AsyncSourceText",
            typeof(string),
            typeof(ProductThumbnailImageSourceConverter),
            new PropertyMetadata(null, OnAsyncSourceTextChanged));

    public static readonly DependencyProperty AsyncDecodePixelWidthProperty =
        DependencyProperty.RegisterAttached(
            "AsyncDecodePixelWidth",
            typeof(int),
            typeof(ProductThumbnailImageSourceConverter),
            new PropertyMetadata(72, OnAsyncDecodePixelWidthChanged));

    private static readonly DependencyProperty AsyncLoadVersionProperty =
        DependencyProperty.RegisterAttached(
            "AsyncLoadVersion",
            typeof(int),
            typeof(ProductThumbnailImageSourceConverter),
            new PropertyMetadata(0));

    public static string? GetAsyncSourceText(DependencyObject element)
    {
        return (string?)element.GetValue(AsyncSourceTextProperty);
    }

    public static void SetAsyncSourceText(DependencyObject element, string? value)
    {
        element.SetValue(AsyncSourceTextProperty, value);
    }

    public static int GetAsyncDecodePixelWidth(DependencyObject element)
    {
        return (int)element.GetValue(AsyncDecodePixelWidthProperty);
    }

    public static void SetAsyncDecodePixelWidth(DependencyObject element, int value)
    {
        element.SetValue(AsyncDecodePixelWidthProperty, value);
    }

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
        if (imageRequest.CanCache && Cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var imageSource = CreateImageSource(imageRequest);
        if (imageRequest.CanCache && imageSource is not null)
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
                return CreateBitmapImageFromBytes(
                    imageRequest.ImageBytes,
                    applyDecodePixelWidth: false,
                    Math.Max(1, DecodePixelWidth));
            }

            if (imageRequest.Uri is null)
            {
                return null;
            }

            if (imageRequest.IsRemoteUri)
            {
                return TryDownloadRemoteImageBytes(imageRequest, out var remoteBytes)
                    ? CreateBitmapImageFromBytes(
                        remoteBytes,
                        applyDecodePixelWidth: true,
                        Math.Max(1, DecodePixelWidth))
                    : null;
            }

            if (string.Equals(imageRequest.Uri.Scheme, "pack", StringComparison.OrdinalIgnoreCase))
            {
                if (TryReadPackResourceBytes(imageRequest.Uri, out var packBytes))
                {
                    return CreateBitmapImageFromBytes(
                        packBytes,
                        applyDecodePixelWidth: false,
                        Math.Max(1, DecodePixelWidth));
                }
            }

            var uriImage = new BitmapImage();
            uriImage.BeginInit();
            uriImage.UriSource = imageRequest.Uri;
            uriImage.DecodePixelWidth = Math.Max(1, DecodePixelWidth);
            uriImage.CreateOptions = BitmapCreateOptions.DelayCreation;
            uriImage.CacheOption = BitmapCacheOption.Default;
            uriImage.EndInit();

            return uriImage;
        }
        catch (Exception ex)
        {
            LogImageDiagnosticOnce(
                $"create-failed|{imageRequest.SourceKind}|{imageRequest.CacheKey}",
                "image source create failed " +
                $"sourceKind={imageRequest.SourceKind} " +
                $"cacheKey={FormatLogValue(imageRequest.CacheKey)} " +
                $"exception={ex.GetType().Name}: {FormatLogValue(ex.Message)}");
            return null;
        }
    }

    private static void OnAsyncSourceTextChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        BeginAsyncImageLoad(target, e.NewValue as string);
    }

    private static void OnAsyncDecodePixelWidthChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        BeginAsyncImageLoad(target, GetAsyncSourceText(target));
    }

    internal static IDisposable UseRemoteImageBytesLoaderForTests(Func<Uri, CancellationToken, Task<byte[]>> loader)
    {
        ArgumentNullException.ThrowIfNull(loader);

        lock (RemoteImageLoaderLock)
        {
            var previous = RemoteImageBytesLoader;
            RemoteImageBytesLoader = loader;
            return new RemoteImageLoaderScope(previous);
        }
    }

    private static void BeginAsyncImageLoad(DependencyObject target, string? sourceText)
    {
        var version = Interlocked.Increment(ref nextAsyncLoadVersion);
        target.SetValue(AsyncLoadVersionProperty, version);
        SetTargetImageSource(target, null);

        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return;
        }

        var trimmedSourceText = sourceText.Trim();
        var imageRequest = TryCreateImageRequest(trimmedSourceText);
        if (imageRequest is null)
        {
            return;
        }

        var decodePixelWidth = Math.Max(1, GetAsyncDecodePixelWidth(target));
        if (imageRequest.IsRemoteUri)
        {
            _ = LoadRemoteImageForTargetAsync(target, imageRequest, trimmedSourceText, decodePixelWidth, version);
            return;
        }

        var converter = new ProductThumbnailImageSourceConverter { DecodePixelWidth = decodePixelWidth };
        SetTargetImageSource(target, converter.CreateImageSource(imageRequest));
    }

    private static async Task LoadRemoteImageForTargetAsync(
        DependencyObject target,
        ImageRequest imageRequest,
        string sourceText,
        int decodePixelWidth,
        int version)
    {
        ImageSource? imageSource = null;
        await RemoteImageDownloadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            imageSource = await Task.Run(() =>
            {
                return TryDownloadRemoteImageBytes(imageRequest, out var remoteBytes)
                    ? CreateBitmapImageFromBytes(
                        remoteBytes,
                        applyDecodePixelWidth: true,
                        decodePixelWidth)
                    : null;
            }).ConfigureAwait(false);
        }
        finally
        {
            RemoteImageDownloadGate.Release();
        }

        var dispatcher = target.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        await dispatcher.InvokeAsync(() =>
        {
            if ((int)target.GetValue(AsyncLoadVersionProperty) != version)
            {
                return;
            }

            if (!string.Equals(GetAsyncSourceText(target)?.Trim(), sourceText, StringComparison.Ordinal))
            {
                return;
            }

            SetTargetImageSource(target, imageSource);
        });
    }

    private static void SetTargetImageSource(DependencyObject target, ImageSource? imageSource)
    {
        switch (target)
        {
            case Image image:
                image.Source = imageSource;
                break;
            case ImageBrush imageBrush:
                imageBrush.ImageSource = imageSource;
                break;
        }
    }

    private static BitmapImage CreateBitmapImageFromBytes(byte[] imageBytes, bool applyDecodePixelWidth, int decodePixelWidth)
    {
        using var stream = new MemoryStream(imageBytes, writable: false);
        var streamImage = new BitmapImage();
        streamImage.BeginInit();
        streamImage.StreamSource = stream;
        streamImage.CacheOption = BitmapCacheOption.OnLoad;
        streamImage.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        if (applyDecodePixelWidth)
        {
            streamImage.DecodePixelWidth = decodePixelWidth;
        }

        streamImage.EndInit();
        if (streamImage.CanFreeze)
        {
            streamImage.Freeze();
        }

        return streamImage;
    }

    private static ImageRequest? TryCreateImageRequest(string sourceText)
    {
        if (TryCreateDataImageRequest(sourceText, out var dataRequest))
        {
            return dataRequest;
        }

        if (sourceText.StartsWith(DataImagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (TryCreatePackResourceRequest(sourceText, out var packRequest))
        {
            return packRequest;
        }

        if (TryCreateLocalFileRequest(sourceText, out var fileRequest))
        {
            return fileRequest;
        }

        if (Path.IsPathRooted(sourceText) && !IsRootRelativeApiPath(sourceText))
        {
            return null;
        }

        if (TryCreateHttpImageRequest(sourceText, out var httpRequest))
        {
            return httpRequest;
        }

        if (Uri.TryCreate(sourceText, UriKind.Absolute, out var absoluteUri))
        {
            if (absoluteUri.IsFile)
            {
                return TryCreateLocalFileRequest(absoluteUri.LocalPath, out fileRequest)
                    ? fileRequest
                    : null;
            }

            if (string.Equals(absoluteUri.Scheme, "pack", StringComparison.OrdinalIgnoreCase))
            {
                return new ImageRequest(absoluteUri.AbsoluteUri, absoluteUri, null, "pack");
            }

            LogRejectedRequest(sourceText, "unsupported-uri-scheme", "unsupported");
            return null;
        }

        if (TryResolveRelativeApiPath(sourceText, out var relativeUri))
        {
            LogImageDiagnosticOnce(
                $"relative-resolved|{sourceText}",
                "image request resolved " +
                "sourceKind=relative " +
                $"value={FormatLogValue(sourceText)} " +
                $"resolvedUri={FormatLogValue(relativeUri.AbsoluteUri)}");
            return new ImageRequest(relativeUri.AbsoluteUri, relativeUri, null, "relative");
        }

        LogRejectedRequest(sourceText, "unrecognized-source", InferSourceKind(sourceText));
        return null;
    }

    private static bool TryCreateHttpImageRequest(string sourceText, out ImageRequest? imageRequest)
    {
        imageRequest = null;
        if (!sourceText.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !sourceText.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalizedText = sourceText.Replace("#", "%23", StringComparison.Ordinal);
        if (!Uri.TryCreate(normalizedText, UriKind.Absolute, out var normalizedUri) ||
            !string.Equals(normalizedUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(normalizedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            LogRejectedRequest(sourceText, "invalid-http-uri", "http");
            return true;
        }

        Uri.TryCreate(sourceText, UriKind.Absolute, out var originalUri);
        LogRemoteUriDiagnostics(sourceText, normalizedUri, originalUri);
        imageRequest = new ImageRequest(normalizedUri.AbsoluteUri, normalizedUri, null, "http");
        return true;
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
            imageRequest = new ImageRequest(sourceText, null, bytes, "data");
            return true;
        }
        catch (FormatException)
        {
            var dataLength = sourceText.Length - commaIndex - 1;
            LogImageDiagnosticOnce(
                $"invalid-data-base64|{metadata}|{dataLength}",
                "image request rejected " +
                "sourceKind=data " +
                "reason=invalid-data-base64 " +
                $"metadata={FormatLogValue(metadata)} " +
                $"dataLength={dataLength}");
            return false;
        }
    }

    private static bool TryCreatePackResourceRequest(string sourceText, out ImageRequest? imageRequest)
    {
        imageRequest = null;
        if (!sourceText.StartsWith("pack://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (TryReadPackResourceBytes(sourceText, out var bytes))
        {
            imageRequest = new ImageRequest(sourceText, null, bytes, "pack");
            return true;
        }

        if (!Uri.TryCreate(sourceText, UriKind.Absolute, out var packUri) ||
            !string.Equals(packUri.Scheme, "pack", StringComparison.OrdinalIgnoreCase))
        {
            LogPackResourceFailure(sourceText, "invalid-pack-uri");
            return false;
        }

        LogPackResourceFailure(sourceText, "pack-resource-not-found");
        imageRequest = new ImageRequest(packUri.OriginalString, packUri, null, "pack");
        return true;
    }

    private static bool TryCreateLocalFileRequest(string sourceText, out ImageRequest? imageRequest)
    {
        imageRequest = null;
        if (!Path.IsPathRooted(sourceText) || !File.Exists(sourceText))
        {
            if (Path.IsPathRooted(sourceText) && !IsRootRelativeApiPath(sourceText))
            {
                LogRejectedRequest(sourceText, "file-missing", "file");
            }

            return false;
        }

        try
        {
            imageRequest = new ImageRequest(Path.GetFullPath(sourceText), null, File.ReadAllBytes(sourceText), "file");
            return true;
        }
        catch (IOException)
        {
            LogRejectedRequest(sourceText, "file-io-error", "file");
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            LogRejectedRequest(sourceText, "file-unauthorized", "file");
            return false;
        }
    }

    private static bool IsRootRelativeApiPath(string sourceText)
    {
        return sourceText.StartsWith("/", StringComparison.Ordinal) &&
                !sourceText.StartsWith("//", StringComparison.Ordinal) ||
            sourceText.StartsWith("\\", StringComparison.Ordinal) &&
                !sourceText.StartsWith("\\\\", StringComparison.Ordinal);
    }

    private static bool TryReadPackResourceBytes(Uri packUri, out byte[] bytes)
    {
        return TryReadPackResourceBytes(packUri.OriginalString, out bytes);
    }

    private static bool TryReadPackResourceBytes(string packUriText, out byte[] bytes)
    {
        bytes = [];
        try
        {
            if (Uri.TryCreate(packUriText, UriKind.Absolute, out var packUri))
            {
                var resourceStream = Application.GetResourceStream(packUri);
                if (resourceStream?.Stream is not null)
                {
                    using var stream = resourceStream.Stream;
                    bytes = ReadAllBytes(stream);
                    return bytes.Length > 0;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (NotSupportedException)
        {
        }

        return TryReadManifestPackResourceBytes(packUriText, out bytes);
    }

    private static bool TryReadManifestPackResourceBytes(string packUriText, out byte[] bytes)
    {
        bytes = [];
        if (!TryGetPackResourceParts(packUriText, out var assemblyName, out var resourcePath))
        {
            return false;
        }

        var assembly = ResolvePackResourceAssembly(assemblyName);
        if (assembly is null)
        {
            return false;
        }

        var resourceName = $"{assembly.GetName().Name}.g.resources";
        using var resourceStream = assembly.GetManifestResourceStream(resourceName);
        if (resourceStream is null)
        {
            return false;
        }

        var resourceKey = resourcePath.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
        using var reader = new ResourceReader(resourceStream);
        foreach (System.Collections.DictionaryEntry entry in reader)
        {
            if (!string.Equals(entry.Key as string, resourceKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bytes = entry.Value switch
            {
                byte[] byteArray => byteArray,
                Stream stream => ReadAllBytes(stream),
                _ => []
            };
            return bytes.Length > 0;
        }

        return false;
    }

    private static bool TryGetPackResourceParts(string packUriText, out string? assemblyName, out string resourcePath)
    {
        assemblyName = null;
        resourcePath = string.Empty;
        const string applicationPackPrefix = "pack://application:,,,/";
        if (!packUriText.StartsWith(applicationPackPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = Uri.UnescapeDataString(packUriText[applicationPackPrefix.Length..]).TrimStart('/');
        var componentIndex = path.IndexOf(";component/", StringComparison.OrdinalIgnoreCase);
        if (componentIndex >= 0)
        {
            assemblyName = path[..componentIndex];
            resourcePath = path[(componentIndex + ";component/".Length)..];
        }
        else
        {
            resourcePath = path;
        }

        return !string.IsNullOrWhiteSpace(resourcePath);
    }

    private static Assembly? ResolvePackResourceAssembly(string? assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
        {
            return Application.ResourceAssembly ?? typeof(ProductThumbnailImageSourceConverter).Assembly;
        }

        var loadedAssembly = AppDomain.CurrentDomain
            .GetAssemblies()
            .FirstOrDefault(assembly => string.Equals(
                assembly.GetName().Name,
                assemblyName,
                StringComparison.OrdinalIgnoreCase));
        if (loadedAssembly is not null)
        {
            return loadedAssembly;
        }

        try
        {
            return Assembly.Load(new AssemblyName(assemblyName));
        }
        catch
        {
            return null;
        }
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static bool TryResolveRelativeApiPath(string sourceText, out Uri resolvedUri)
    {
        resolvedUri = default!;
        var normalizedPath = sourceText.Trim();
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        if (normalizedPath.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        normalizedPath = normalizedPath.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        var baseUri = ServiceRegistration.GetApiBaseAddress();
        if (!Uri.TryCreate(baseUri, normalizedPath, out var createdUri) || createdUri is null)
        {
            return false;
        }

        resolvedUri = createdUri;
        return true;
    }

    private static void LogRemoteUriDiagnostics(string sourceText, Uri normalizedUri, Uri? originalUri)
    {
        var containsUnescapedHash = sourceText.Contains('#', StringComparison.Ordinal);
        var containsSpace = sourceText.Contains(' ', StringComparison.Ordinal);
        var hasFragment = !string.IsNullOrEmpty(originalUri?.Fragment);

        LogImageDiagnosticOnce(
            $"remote-uri|{sourceText}",
            "image uri parsed " +
            "sourceKind=http " +
            $"containsUnescapedHash={containsUnescapedHash.ToString().ToLowerInvariant()} " +
            $"containsSpace={containsSpace.ToString().ToLowerInvariant()} " +
            $"hasFragment={hasFragment.ToString().ToLowerInvariant()} " +
            $"value={FormatLogValue(sourceText)} " +
            $"resolvedUri={FormatLogValue(normalizedUri.AbsoluteUri)}");
    }

    private static bool TryDownloadRemoteImageBytes(ImageRequest imageRequest, out byte[] bytes)
    {
        bytes = [];
        if (imageRequest.Uri is null)
        {
            return false;
        }

        Func<Uri, CancellationToken, Task<byte[]>> loader;
        lock (RemoteImageLoaderLock)
        {
            loader = RemoteImageBytesLoader;
        }

        using var cancellation = new CancellationTokenSource(RemoteImageDownloadTimeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            bytes = loader(imageRequest.Uri, cancellation.Token).GetAwaiter().GetResult();
            if (bytes.Length <= 0)
            {
                LogRemoteDownloadFailure(imageRequest, "empty-response", null, stopwatch.ElapsedMilliseconds);
                return false;
            }

            LogRemoteDownloadSuccess(imageRequest, bytes.Length, stopwatch.ElapsedMilliseconds);
            return true;
        }
        catch (OperationCanceledException ex) when (cancellation.IsCancellationRequested)
        {
            LogRemoteDownloadFailure(imageRequest, "timeout", ex, stopwatch.ElapsedMilliseconds);
            return false;
        }
        catch (HttpRequestException ex)
        {
            var reason = ex.StatusCode is null
                ? "http-request-failed"
                : $"http-{(int)ex.StatusCode.Value}";
            LogRemoteDownloadFailure(imageRequest, reason, ex, stopwatch.ElapsedMilliseconds);
            return false;
        }
        catch (IOException ex)
        {
            LogRemoteDownloadFailure(imageRequest, "io-error", ex, stopwatch.ElapsedMilliseconds);
            return false;
        }
        catch (Exception ex)
        {
            LogRemoteDownloadFailure(imageRequest, "download-exception", ex, stopwatch.ElapsedMilliseconds);
            return false;
        }
    }

    private static async Task<byte[]> DownloadRemoteImageBytesAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await RemoteImageHttpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private static HttpClient CreateRemoteImageHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HBPOS-Client/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("image/*");
        return client;
    }

    private static void LogRemoteDownloadSuccess(ImageRequest imageRequest, int byteCount, long elapsedMilliseconds)
    {
        LogImageDiagnosticOnce(
            $"download-success|{imageRequest.CacheKey}",
            "image downloaded " +
            $"sourceKind={imageRequest.SourceKind} " +
            $"uri={FormatLogValue(imageRequest.CacheKey)} " +
            $"bytes={byteCount} " +
            $"elapsedMs={elapsedMilliseconds}");
    }

    private static void LogRemoteDownloadFailure(
        ImageRequest imageRequest,
        string reason,
        Exception? exception,
        long elapsedMilliseconds)
    {
        LogImageDiagnosticOnce(
            $"download-failed|{reason}|{imageRequest.CacheKey}|{exception?.GetType().Name}|{exception?.Message}",
            "image download failed " +
            $"sourceKind={imageRequest.SourceKind} " +
            $"reason={reason} " +
            $"uri={FormatLogValue(imageRequest.CacheKey)} " +
            $"elapsedMs={elapsedMilliseconds} " +
            $"exception={FormatLogValue(exception is null ? "<none>" : $"{exception.GetType().Name}: {exception.Message}")}");
    }

    private static void LogRejectedRequest(string sourceText, string reason, string sourceKind)
    {
        LogImageDiagnosticOnce(
            $"{reason}|{sourceText}",
            "image request rejected " +
            $"sourceKind={sourceKind} " +
            $"reason={reason} " +
            $"value={FormatLogValue(sourceText)}");
    }

    private static void LogPackResourceFailure(string sourceText, string reason)
    {
        var hasParts = TryGetPackResourceParts(sourceText, out var assemblyName, out var resourcePath);
        LogImageDiagnosticOnce(
            $"{reason}|{sourceText}",
            "image request rejected " +
            "sourceKind=pack " +
            $"reason={reason} " +
            $"assembly={FormatLogValue(hasParts ? assemblyName ?? "<application>" : "<unknown>")} " +
            $"resource={FormatLogValue(hasParts ? resourcePath : "<unknown>")} " +
            $"value={FormatLogValue(sourceText)}");
    }

    private static string InferSourceKind(string sourceText)
    {
        if (sourceText.StartsWith(DataImagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return "data";
        }

        if (sourceText.StartsWith("pack://", StringComparison.OrdinalIgnoreCase))
        {
            return "pack";
        }

        if (Uri.TryCreate(sourceText, UriKind.Absolute, out var uri))
        {
            return uri.Scheme switch
            {
                "http" or "https" => "http",
                "file" => "file",
                _ => "unsupported"
            };
        }

        return "unknown";
    }

    private static void LogImageDiagnosticOnce(string key, string message)
    {
        if (LoggedDiagnostics.TryAdd(key, 0))
        {
            ConsoleLog.Write("ProductImage", message);
        }
    }

    private static string FormatLogValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        var normalized = value.Replace(Environment.NewLine, " ", StringComparison.Ordinal);
        if (normalized.Length > MaxLoggedValueLength)
        {
            normalized = normalized[..MaxLoggedValueLength] + "...";
        }

        return $"\"{normalized}\"";
    }

    private sealed record ImageRequest(string CacheKey, Uri? Uri, byte[]? ImageBytes, string SourceKind)
    {
        public bool CanCache => ImageBytes is not null;

        public bool IsRemoteUri =>
            Uri is not null &&
            (string.Equals(Uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(Uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class RemoteImageLoaderScope(Func<Uri, CancellationToken, Task<byte[]>> previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            lock (RemoteImageLoaderLock)
            {
                RemoteImageBytesLoader = previous;
            }

            _disposed = true;
        }
    }
}
