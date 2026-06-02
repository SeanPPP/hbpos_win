using Hbpos.Api.Data;
using Hbpos.Api.Services;

namespace Hbpos.Api;

public static class ServiceRegistration
{
    public static IServiceCollection AddHbposApiServices(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        if (configuration is not null)
        {
            services.Configure<LinklyCloudBackendAsyncOptions>(options =>
            {
                // 旧配置只作为兜底来源；新配置后应用，且空字符串不能覆盖已有有效值。
                ApplyLinklyCloudBackendAsyncOptionsSection(
                    options,
                    configuration.GetSection("LinklyCloudBackend"));
                ApplyLinklyCloudBackendAsyncOptionsSection(
                    options,
                    configuration.GetSection("LinklyCloudBackendAsync"));
            });
        }

        services.AddScoped<HbposSqlSugarContext>();
        services.AddScoped<IDeviceRegistrationRepository, SqlSugarDeviceRegistrationRepository>();
        services.AddScoped<IDeviceService, DeviceService>();
        services.AddScoped<IDeviceAuthorizationService, DeviceAuthorizationService>();
        services.AddScoped<ICashierService, CashierService>();
        services.AddScoped<ICatalogService, CatalogService>();
        services.AddScoped<IAdvertisementPlaybackService, AdvertisementPlaybackService>();
        services.AddScoped<IOrderRepository, SqlSugarOrderRepository>();
        services.AddScoped<IOrderSyncService, OrderSyncService>();
        services.AddScoped<IOrderHistoryRepository, SqlSugarOrderHistoryRepository>();
        services.AddScoped<IOrderHistoryService, OrderHistoryService>();
        services.AddScoped<IOrderReturnRepository, SqlSugarOrderReturnRepository>();
        services.AddScoped<IOrderReturnService, OrderReturnService>();
        services.AddScoped<IInstallmentRepository, SqlSugarInstallmentRepository>();
        services.AddScoped<InstallmentService>();
        services.AddScoped<IInstallmentService>(sp => sp.GetRequiredService<InstallmentService>());
        services.AddScoped<IInstallmentHistoryService>(sp => sp.GetRequiredService<InstallmentService>());
        services.AddScoped<IStoreVoucherRepository, SqlSugarStoreVoucherRepository>();
        services.AddScoped<IStoreVoucherService, StoreVoucherService>();
        services.AddScoped<ILinklyCloudCredentialRepository, SqlSugarLinklyCloudCredentialRepository>();
        services.AddScoped<ILinklyCloudCredentialService, LinklyCloudCredentialService>();
        services.AddScoped<ILinklyCloudCredentialSchemaSqlExecutor, SqlSugarLinklyCloudCredentialSchemaSqlExecutor>();
        services.AddScoped<ILinklyCloudCredentialSchemaInitializer, SqlSugarLinklyCloudCredentialSchemaInitializer>();
        services.AddScoped<ILinklyCloudBackendAsyncRepository, SqlSugarLinklyCloudBackendAsyncRepository>();
        services.AddScoped<ILinklyCloudBackendTerminalCredentialRepository, SqlSugarLinklyCloudBackendTerminalCredentialRepository>();
        services.AddHttpClient<ILinklyCloudBackendAsyncTransport, HttpLinklyCloudBackendAsyncTransport>();
        services.AddHttpClient<ILinklyCloudBackendTokenProvider, HttpLinklyCloudBackendTokenProvider>();
        services.AddScoped<ILinklyCloudBackendAsyncService, LinklyCloudBackendAsyncService>();
        services.AddScoped<ILinklyCloudBackendAsyncSchemaSqlExecutor, SqlSugarLinklyCloudBackendAsyncSchemaSqlExecutor>();
        services.AddScoped<ILinklyCloudBackendAsyncSchemaInitializer, SqlSugarLinklyCloudBackendAsyncSchemaInitializer>();
        services.AddScoped<ISquareTokenRepository, SqlSugarSquareTokenRepository>();
        services.AddScoped<ISquareTokenService, SquareTokenService>();
        services.AddScoped<ISquareTokenSchemaSqlExecutor, SqlSugarSquareTokenSchemaSqlExecutor>();
        services.AddScoped<ISquareTokenSchemaInitializer, SqlSugarSquareTokenSchemaInitializer>();
        services.AddSingleton<ICatalogIndexCache, CatalogIndexCache>();
        services.AddSingleton<IPriceIndexBuilder, PriceIndexBuilder>();
        services.AddSingleton<IOrderSyncPlanner, OrderSyncPlanner>();
        services.AddScoped<IStoreVoucherReservationService, SqlSugarStoreVoucherReservationService>();

        return services;
    }

    private static void ApplyLinklyCloudBackendAsyncOptionsSection(
        LinklyCloudBackendAsyncOptions options,
        IConfigurationSection section)
    {
        AssignNonEmpty(
            section,
            nameof(LinklyCloudBackendAsyncOptions.ProductionNotificationBearer),
            value => options.ProductionNotificationBearer = value);
        AssignNonEmpty(
            section,
            nameof(LinklyCloudBackendAsyncOptions.SandboxNotificationBearer),
            value => options.SandboxNotificationBearer = value);
        AssignNonEmpty(
            section,
            nameof(LinklyCloudBackendAsyncOptions.PublicNotificationBaseUrl),
            value => options.PublicNotificationBaseUrl = value);
        AssignNonEmpty(
            section,
            nameof(LinklyCloudBackendAsyncOptions.ProductionAuthBaseUrl),
            value => options.ProductionAuthBaseUrl = value);
        AssignNonEmpty(
            section,
            nameof(LinklyCloudBackendAsyncOptions.SandboxAuthBaseUrl),
            value => options.SandboxAuthBaseUrl = value);
        AssignNonEmpty(
            section,
            nameof(LinklyCloudBackendAsyncOptions.ProductionRestBaseUrl),
            value => options.ProductionRestBaseUrl = value);
        AssignNonEmpty(
            section,
            nameof(LinklyCloudBackendAsyncOptions.SandboxRestBaseUrl),
            value => options.SandboxRestBaseUrl = value);
        AssignNonEmpty(
            section,
            nameof(LinklyCloudBackendAsyncOptions.PosName),
            value => options.PosName = value);
        AssignNonEmpty(
            section,
            nameof(LinklyCloudBackendAsyncOptions.PosVersion),
            value => options.PosVersion = value);
        AssignNonEmpty(
            section,
            nameof(LinklyCloudBackendAsyncOptions.ProductionPosVendorId),
            value => options.ProductionPosVendorId = value);
        AssignNonEmpty(
            section,
            nameof(LinklyCloudBackendAsyncOptions.SandboxPosVendorId),
            value => options.SandboxPosVendorId = value);
    }

    private static void AssignNonEmpty(
        IConfiguration section,
        string key,
        Action<string> assign)
    {
        var value = section[key];
        if (!string.IsNullOrWhiteSpace(value))
        {
            assign(value.Trim());
        }
    }
}
