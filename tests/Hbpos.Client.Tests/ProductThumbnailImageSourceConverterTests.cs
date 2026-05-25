using System.Globalization;
using System.Windows.Media.Imaging;
using Hbpos.Client.Wpf.Converters;

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
    public void Convert_resolves_root_relative_path_with_api_base_url()
    {
        const string variableName = "HBPOS_API_BASE_URL";
        var originalValue = Environment.GetEnvironmentVariable(variableName);
        var converter = new ProductThumbnailImageSourceConverter();

        try
        {
            Environment.SetEnvironmentVariable(variableName, "https://cdn.example.test/tenant-a");

            var result = converter.Convert("/images/product.png", typeof(BitmapSource), null, CultureInfo.InvariantCulture);

            var image = Assert.IsType<BitmapImage>(result);
            Assert.Equal("https://cdn.example.test/tenant-a/images/product.png", image.UriSource?.AbsoluteUri);
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
        File.WriteAllBytes(filePath, Convert.FromBase64String(OnePixelPngBase64));
        return filePath;
    }
}
