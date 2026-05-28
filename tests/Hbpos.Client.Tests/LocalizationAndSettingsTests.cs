using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Tests;

public sealed class LocalizationAndSettingsTests
{
    [Fact]
    public void Localization_defaults_to_en_us_and_returns_english_text()
    {
        var localization = new LocalizationService();

        Assert.Equal("en-US", localization.CurrentCulture.Name);
        Assert.Contains(localization.AvailableCultures, culture => culture.Name == "en-US");
        Assert.Contains(localization.AvailableCultures, culture => culture.Name == "zh-CN");
        Assert.Equal("POS Terminal", localization.T("PosTerminal"));
    }

    [Fact]
    public void Localization_switches_to_zh_cn_and_notifies_consumers()
    {
        var localization = new LocalizationService();
        var notificationCount = 0;
        localization.CultureChanged += (_, _) => notificationCount++;

        localization.SetCulture("zh-CN");

        Assert.Equal("zh-CN", localization.CurrentCulture.Name);
        Assert.Equal("POS \u6536\u94F6\u53F0", localization.T("PosTerminal"));
        Assert.Equal(1, notificationCount);
    }

    [Fact]
    public void Localization_has_startup_and_shell_control_text()
    {
        var localization = new LocalizationService();

        Assert.Equal("Preparing point of sale...", localization.T("startup.loading"));
        Assert.Equal("Scanner", localization.T("shell.scannerBinding"));

        localization.SetCulture("zh-CN");

        Assert.Equal("\u6B63\u5728\u51C6\u5907\u6536\u94F6\u7CFB\u7EDF...", localization.T("startup.loading"));
        Assert.Equal("\u626B\u7801\u67AA", localization.T("shell.scannerBinding"));
        Assert.Equal("\u91CD\u65B0\u5B66\u4E60\u626B\u7801\u67AA", localization.T("shell.scannerBinding.resetTooltip"));
    }

    [Fact]
    public void Localization_has_settings_text()
    {
        var localization = new LocalizationService();

        Assert.Equal("Settings", localization.T("settings.title"));
        Assert.Equal("Data Download", localization.T("settings.section.dataDownload.title"));

        localization.SetCulture("zh-CN");

        Assert.Equal("\u8BBE\u7F6E", localization.T("settings.title"));
        Assert.Equal("\u6570\u636E\u4E0B\u8F7D", localization.T("settings.section.dataDownload.title"));
        Assert.Equal("\u66F4\u6362\u5206\u5E97\u91CD\u65B0\u6CE8\u518C", localization.T("settings.deviceRegistration.action"));
    }

    [Fact]
    public void Localization_has_linkly_sop_text()
    {
        string[] keys =
        [
            "settings.linkly.sop.title",
            "settings.linkly.sop.note",
            "settings.linkly.sop.cloud.title",
            "settings.linkly.sop.cloud.step1",
            "settings.linkly.sop.cloud.step8",
            "settings.linkly.sop.repair.title",
            "settings.linkly.sop.repair.step1",
            "settings.linkly.sop.repair.warning",
            "settings.linkly.sop.local.title",
            "settings.linkly.sop.local.step6",
            "settings.linkly.sop.local.step10",
            "settings.linkly.sop.mode.title",
            "settings.linkly.sop.mode.cloud",
            "settings.linkly.sop.mode.local"
        ];
        var localization = new LocalizationService();

        foreach (var key in keys)
        {
            var english = localization.T(key);
            Assert.False(english.StartsWith("[[", StringComparison.Ordinal), key);
            Assert.DoesNotContain("1111 2227", english, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("pp.cloud.pceftpos.com", english, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PCEFTPOS", english, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("Terminal setup steps", localization.T("settings.linkly.sop.title"), StringComparison.Ordinal);
        Assert.Contains("Async DLE 9600", localization.T("settings.linkly.sop.local.step6"), StringComparison.Ordinal);

        localization.SetCulture("zh-CN");

        foreach (var key in keys)
        {
            var chinese = localization.T(key);
            Assert.False(chinese.StartsWith("[[", StringComparison.Ordinal), key);
            Assert.DoesNotContain("1111 2227", chinese, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("pp.cloud.pceftpos.com", chinese, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PCEFTPOS", chinese, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("\u5237\u5361\u673A\u64CD\u4F5C\u6B65\u9AA4", localization.T("settings.linkly.sop.title"), StringComparison.Ordinal);
        Assert.Contains("Async DLE 9600", localization.T("settings.linkly.sop.local.step6"), StringComparison.Ordinal);
    }

    [Fact]
    public void Localization_has_navigation_and_history_text()
    {
        var localization = new LocalizationService();

        Assert.Equal("Back to POS", localization.T("shell.backToPos"));
        Assert.Equal("History", localization.T("shell.page.history"));
        Assert.Equal("Local", localization.T("history.source.local"));
        Assert.Equal("Pending recall", localization.T("history.status.pendingRecall"));
        Assert.Equal("History", localization.T("pos.terminal.actions.history"));
        Assert.Equal("Settings", localization.T("pos.terminal.actions.settings"));
        Assert.Equal("Customer Display", localization.T("pos.terminal.actions.customerDisplay"));

        localization.SetCulture("zh-CN");

        Assert.Equal("\u8FD4\u56DE\u6536\u94F6", localization.T("shell.backToPos"));
        Assert.Equal("\u5386\u53F2", localization.T("shell.page.history"));
        Assert.Equal("\u672C\u5730", localization.T("history.source.local"));
        Assert.Equal("\u5F85\u53D6\u56DE", localization.T("history.status.pendingRecall"));
        Assert.Equal("\u5386\u53F2", localization.T("pos.terminal.actions.history"));
        Assert.Equal("\u8BBE\u7F6E", localization.T("pos.terminal.actions.settings"));
        Assert.Equal("\u5BA2\u663E", localization.T("pos.terminal.actions.customerDisplay"));
    }

    [Fact]
    public void Localization_returns_placeholder_for_missing_key()
    {
        var localization = new LocalizationService();

        Assert.Equal("[[DefinitelyMissingKey]]", localization.T("DefinitelyMissingKey"));
    }

    [Fact]
    public async Task App_settings_store_and_restore_language()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var settings = new LocalAppSettingsRepository(store);
            await schema.InitializeAsync();

            await settings.SetValueAsync("Language", "zh-CN");

            Assert.Equal("zh-CN", await settings.GetValueAsync("Language"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Scanner_binding_service_clears_saved_device_path()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var settings = new LocalAppSettingsRepository(store);
            var binding = new ScannerBindingService(settings);
            await schema.InitializeAsync();

            await binding.SetBoundDevicePathAsync("scanner-device");
            Assert.Equal("scanner-device", await binding.GetBoundDevicePathAsync());

            await binding.ClearBoundDevicePathAsync();

            Assert.Null(await binding.GetBoundDevicePathAsync());
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Local_schema_creates_app_settings_table()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            await schema.InitializeAsync();

            await using var connection = await store.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table' AND name = 'AppSettings';
                """;

            var count = (long)(await command.ExecuteScalarAsync() ?? 0L);
            Assert.Equal(1L, count);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    private static string CreateTempDatabasePath()
    {
        return Path.Combine(Path.GetTempPath(), $"hbpos-client-{Guid.NewGuid():N}.db");
    }

    private static void DeleteTempDatabase(string databasePath)
    {
        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }
    }
}
