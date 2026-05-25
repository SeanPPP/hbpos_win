using System.Windows.Input;
using Hbpos.Client.Wpf;

namespace Hbpos.Client.Tests;

public sealed class KeyboardScannerFallbackBufferTests
{
    [Fact]
    public void Process_CompletesFastScannerInputOnEnter()
    {
        var buffer = new KeyboardScannerFallbackBuffer();
        var now = DateTimeOffset.UtcNow;

        Assert.Null(buffer.Process(Key.D9, now));
        Assert.Null(buffer.Process(Key.D3, now.AddMilliseconds(10)));
        Assert.Null(buffer.Process(Key.D0, now.AddMilliseconds(20)));
        var barcode = buffer.Process(Key.Enter, now.AddMilliseconds(30));

        Assert.Equal("930", barcode);
    }

    [Fact]
    public void Process_DropsSlowInputBeforeEnter()
    {
        var buffer = new KeyboardScannerFallbackBuffer();
        var now = DateTimeOffset.UtcNow;

        Assert.Null(buffer.Process(Key.D9, now));
        Assert.Null(buffer.Process(Key.D3, now.AddMilliseconds(150)));
        var barcode = buffer.Process(Key.Enter, now.AddMilliseconds(160));

        Assert.Null(barcode);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void ShouldBlockKeyboardScannerFallback_blocks_only_visible_text_input_focus(
        bool isTextInputFocused,
        bool isFocusedElementVisible,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainWindow.ShouldBlockKeyboardScannerFallback(isTextInputFocused, isFocusedElementVisible));
    }
}
