using System.Globalization;
using System.Windows.Media.Imaging;
using Hbpos.Client.Wpf.Converters;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class ProductThumbnailImageSourceConverterTests
{
    private const string OnePixelPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4////fwAJ+wP9KobjigAAAABJRU5ErkJggg==";

    [Fact]
    public void Convert_returns_bitmap_for_data_image_base64()
    {
        var converter = new ProductThumbnailImageSourceConverter();

        var result = converter.Convert(
            $"data:image/png;base64,{OnePixelPngBase64}",
            typeof(BitmapSource),
            null,
            CultureInfo.InvariantCulture);

        var image = Assert.IsType<BitmapImage>(result);
        Assert.Equal(1, image.PixelWidth);
        Assert.Equal(1, image.PixelHeight);
    }

    [Fact]
    public void Convert_returns_bitmap_for_absolute_file_path()
    {
        var converter = new ProductThumbnailImageSourceConverter();
        var filePath = CreateTempImageFile();

        try
        {
            var result = converter.Convert(filePath, typeof(BitmapSource), null, CultureInfo.InvariantCulture);

            Assert.IsType<BitmapImage>(result);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Convert_returns_bitmap_for_file_uri()
    {
        var converter = new ProductThumbnailImageSourceConverter();
        var filePath = CreateTempImageFile();

        try
        {
            var result = converter.Convert(new Uri(filePath).AbsoluteUri, typeof(BitmapSource), null, CultureInfo.InvariantCulture);

            Assert.IsType<BitmapImage>(result);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Convert_returns_null_for_missing_absolute_file_path()
    {
        var converter = new ProductThumbnailImageSourceConverter();
        var filePath = Path.Combine(Path.GetTempPath(), $"hbpos-thumbnail-missing-{Guid.NewGuid():N}.png");

        var result = converter.Convert(filePath, typeof(BitmapSource), null, CultureInfo.InvariantCulture);

        Assert.Null(result);
    }

    [Fact]
    public void Convert_returns_bitmap_for_http_uri()
    {
        var converter = new ProductThumbnailImageSourceConverter();
        using var remoteImages = ProductThumbnailImageSourceConverter.UseRemoteImageBytesLoaderForTests(
            (_, _) => Task.FromResult(OnePixelPngBytes()));

        var result = converter.Convert(
            $"https://cdn.example.test/images/{Guid.NewGuid():N}/product.png",
            typeof(BitmapSource),
            null,
            CultureInfo.InvariantCulture);

        var image = Assert.IsType<BitmapImage>(result);
        Assert.Equal(72, image.PixelWidth);
        Assert.Equal(72, image.PixelHeight);
    }

    [Fact]
    public void Convert_logs_remote_uri_diagnostics_when_url_contains_unescaped_hash()
    {
        var converter = new ProductThumbnailImageSourceConverter();
        var imageUrl = $"https://cdn.example.test/images/{Guid.NewGuid():N}/#0065-6759#XRU.jpg";
        using var remoteImages = ProductThumbnailImageSourceConverter.UseRemoteImageBytesLoaderForTests(
            (_, _) => Task.FromResult(OnePixelPngBytes()));

        var logs = CaptureProductImageLogs(() => converter.Convert(imageUrl, typeof(BitmapSource), null, CultureInfo.InvariantCulture));

        var line = logs.Single(item => item.Contains("image uri parsed", StringComparison.Ordinal));
        Assert.Contains("image uri parsed", line);
        Assert.Contains("sourceKind=http", line);
        Assert.Contains("containsUnescapedHash=true", line);
        Assert.Contains("hasFragment=true", line);
        Assert.Contains("resolvedUri=\"https://cdn.example.test/images/", line);
    }

    [Fact]
    public void Convert_escapes_unescaped_hash_in_http_image_file_name()
    {
        var converter = new ProductThumbnailImageSourceConverter();
        Uri? requestedUri = null;
        using var remoteImages = ProductThumbnailImageSourceConverter.UseRemoteImageBytesLoaderForTests((uri, _) =>
        {
            requestedUri = uri;
            return Task.FromResult(OnePixelPngBytes());
        });

        var result = converter.Convert(
            "https://cdn.example.test/images/#0065-6759#XRU.jpg",
            typeof(BitmapSource),
            null,
            CultureInfo.InvariantCulture);

        Assert.IsType<BitmapImage>(result);
        Assert.NotNull(requestedUri);
        Assert.Empty(requestedUri.Fragment);
        Assert.Equal(
            "https://cdn.example.test/images/%230065-6759%23XRU.jpg",
            requestedUri.AbsoluteUri);
    }

    [Fact]
    public void Convert_logs_missing_local_file_once()
    {
        var converter = new ProductThumbnailImageSourceConverter();
        var filePath = Path.Combine(Path.GetTempPath(), $"hbpos-thumbnail-missing-{Guid.NewGuid():N}.png");

        var logs = CaptureProductImageLogs(() =>
        {
            converter.Convert(filePath, typeof(BitmapSource), null, CultureInfo.InvariantCulture);
            converter.Convert(filePath, typeof(BitmapSource), null, CultureInfo.InvariantCulture);
        });

        var line = Assert.Single(logs);
        Assert.Contains("image request rejected", line);
        Assert.Contains("reason=file-missing", line);
        Assert.Contains("sourceKind=file", line);
    }

    [Fact]
    public void Convert_logs_invalid_data_image_without_full_payload()
    {
        var converter = new ProductThumbnailImageSourceConverter();
        var payload = $"not-base64-{Guid.NewGuid():N}";

        var logs = CaptureProductImageLogs(() =>
            converter.Convert($"data:image/png;base64,{payload}", typeof(BitmapSource), null, CultureInfo.InvariantCulture));

        var line = Assert.Single(logs);
        Assert.Contains("reason=invalid-data-base64", line);
        Assert.Contains("sourceKind=data", line);
        Assert.Contains("dataLength=", line);
        Assert.DoesNotContain(payload, line);
    }

    [Fact]
    public void Convert_logs_rejected_unsupported_source_once()
    {
        var converter = new ProductThumbnailImageSourceConverter();
        var source = $"unsupported://image/{Guid.NewGuid():N}";

        var logs = CaptureProductImageLogs(() =>
        {
            converter.Convert(source, typeof(BitmapSource), null, CultureInfo.InvariantCulture);
            converter.Convert(source, typeof(BitmapSource), null, CultureInfo.InvariantCulture);
        });

        var line = Assert.Single(logs);
        Assert.Contains("image request rejected", line);
        Assert.Contains("reason=unsupported-uri-scheme", line);
        Assert.Contains("sourceKind=unsupported", line);
    }

    [Fact]
    public void Convert_downloads_http_bitmap_without_caching()
    {
        var converter = new ProductThumbnailImageSourceConverter();
        var imageUrl = $"https://cdn.example.test/images/{Guid.NewGuid():N}/product.png";
        var loadCount = 0;
        using var remoteImages = ProductThumbnailImageSourceConverter.UseRemoteImageBytesLoaderForTests((_, _) =>
        {
            loadCount++;
            return Task.FromResult(OnePixelPngBytes());
        });

        var first = converter.Convert(imageUrl, typeof(BitmapSource), null, CultureInfo.InvariantCulture);
        var second = converter.Convert(imageUrl, typeof(BitmapSource), null, CultureInfo.InvariantCulture);

        var firstImage = Assert.IsType<BitmapImage>(first);
        var secondImage = Assert.IsType<BitmapImage>(second);
        Assert.True(firstImage.IsFrozen);
        Assert.True(secondImage.IsFrozen);
        Assert.NotSame(firstImage, secondImage);
        Assert.Equal(2, loadCount);
    }

    [Fact]
    public void Convert_returns_bitmap_for_pack_uri()
    {
        var converter = new ProductThumbnailImageSourceConverter();

        var result = converter.Convert(
            "pack://application:,,,/Hbpos.Client.Wpf;component/Resources/AppIcon.ico",
            typeof(BitmapSource),
            null,
            CultureInfo.InvariantCulture);

        var image = Assert.IsType<BitmapImage>(result);
        Assert.True(image.PixelWidth > 0);
        Assert.True(image.PixelHeight > 0);
    }

    [Fact]
    public void Convert_resolves_root_relative_path_with_default_api_base_url()
    {
        const string variableName = "HBPOS_API_BASE_URL";
        var originalValue = Environment.GetEnvironmentVariable(variableName);
        var converter = new ProductThumbnailImageSourceConverter();
        Uri? requestedUri = null;
        using var remoteImages = ProductThumbnailImageSourceConverter.UseRemoteImageBytesLoaderForTests((uri, _) =>
        {
            requestedUri = uri;
            return Task.FromResult(OnePixelPngBytes());
        });

        try
        {
            Environment.SetEnvironmentVariable(variableName, null);

            var result = converter.Convert("/images/product.png", typeof(BitmapSource), null, CultureInfo.InvariantCulture);

            Assert.IsType<BitmapImage>(result);
            Assert.Equal("http://localhost:5159/images/product.png", requestedUri?.AbsoluteUri);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, originalValue);
        }
    }

    [Fact]
    public void Convert_resolves_root_relative_path_with_api_base_url()
    {
        const string variableName = "HBPOS_API_BASE_URL";
        var originalValue = Environment.GetEnvironmentVariable(variableName);
        var converter = new ProductThumbnailImageSourceConverter();
        Uri? requestedUri = null;
        using var remoteImages = ProductThumbnailImageSourceConverter.UseRemoteImageBytesLoaderForTests((uri, _) =>
        {
            requestedUri = uri;
            return Task.FromResult(OnePixelPngBytes());
        });

        try
        {
            Environment.SetEnvironmentVariable(variableName, "https://cdn.example.test/tenant-a");

            var result = converter.Convert("/images/product.png", typeof(BitmapSource), null, CultureInfo.InvariantCulture);

            Assert.IsType<BitmapImage>(result);
            Assert.Equal("https://cdn.example.test/tenant-a/images/product.png", requestedUri?.AbsoluteUri);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, originalValue);
        }
    }

    [Fact]
    public void Convert_resolves_relative_path_without_leading_slash_with_api_base_url()
    {
        const string variableName = "HBPOS_API_BASE_URL";
        var originalValue = Environment.GetEnvironmentVariable(variableName);
        var converter = new ProductThumbnailImageSourceConverter();
        Uri? requestedUri = null;
        using var remoteImages = ProductThumbnailImageSourceConverter.UseRemoteImageBytesLoaderForTests((uri, _) =>
        {
            requestedUri = uri;
            return Task.FromResult(OnePixelPngBytes());
        });

        try
        {
            Environment.SetEnvironmentVariable(variableName, "https://cdn.example.test/tenant-a");

            var result = converter.Convert("images/product.png", typeof(BitmapSource), null, CultureInfo.InvariantCulture);

            Assert.IsType<BitmapImage>(result);
            Assert.Equal("https://cdn.example.test/tenant-a/images/product.png", requestedUri?.AbsoluteUri);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, originalValue);
        }
    }

    [Fact]
    public void Convert_returns_null_for_invalid_data_image()
    {
        var converter = new ProductThumbnailImageSourceConverter();

        var result = converter.Convert("data:image/png;base64,not-base64", typeof(BitmapSource), null, CultureInfo.InvariantCulture);

        Assert.Null(result);
    }

    private static string CreateTempImageFile()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"hbpos-thumbnail-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(filePath, OnePixelPngBytes());
        return filePath;
    }

    private static byte[] OnePixelPngBytes()
    {
        return Convert.FromBase64String(OnePixelPngBase64);
    }

    private static List<string> CaptureProductImageLogs(Action action)
    {
        var lines = new List<string>();
        void Handler(string line)
        {
            if (line.Contains("[ProductImage]", StringComparison.Ordinal))
            {
                lines.Add(line);
            }
        }

        ConsoleLog.LineWritten += Handler;
        try
        {
            action();
        }
        finally
        {
            ConsoleLog.LineWritten -= Handler;
        }

        return lines;
    }
}
