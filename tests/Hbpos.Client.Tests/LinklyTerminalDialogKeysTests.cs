using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class LinklyTerminalDialogKeysTests
{
    [Theory]
    [InlineData("OK", LinklyTerminalDialogKeys.OkCancel)]
    [InlineData("Cancel", LinklyTerminalDialogKeys.OkCancel)]
    [InlineData(" ok/cancel ", LinklyTerminalDialogKeys.OkCancel)]
    [InlineData("YES", LinklyTerminalDialogKeys.Yes)]
    [InlineData(" no ", LinklyTerminalDialogKeys.No)]
    [InlineData("AUTH", LinklyTerminalDialogKeys.Auth)]
    [InlineData("7", "7")]
    public void Normalize_maps_dialog_button_keys_to_linkly_sendkey_values(string input, string expected)
    {
        // 兼容旧弹窗文本按钮，确保发送给 Linkly 后端的始终是官方 sendkey 枚举。
        var normalized = LinklyTerminalDialogKeys.Normalize(input);

        Assert.Equal(expected, normalized);
    }
}
