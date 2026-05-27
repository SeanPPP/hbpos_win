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
        Assert.Equal("\u91CD\u65B0\u6CE8\u518C\u8BBE\u5907", localization.T("settings.deviceRegistration.action"));
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
