using Hbpos.Api.Data;
using Hbpos.Api.Services;

namespace Hbpos.Api;

public static class ServiceRegistration
{
    public static IServiceCollection AddHbposApiServices(this IServiceCollection services)
    {
        services.AddScoped<HbposSqlSugarContext>();
        services.AddScoped<IDeviceService, DeviceService>();
        services.AddScoped<IDeviceAuthorizationService, DeviceAuthorizationService>();
        services.AddScoped<ICashierService, CashierService>();
        services.AddScoped<ICatalogService, CatalogService>();
        services.AddScoped<IOrderRepository, SqlSugarOrderRepository>();
        services.AddScoped<IOrderSyncService, OrderSyncService>();
        services.AddScoped<IOrderHistoryRepository, SqlSugarOrderHistoryRepository>();
        services.AddScoped<IOrderHistoryService, OrderHistoryService>();
        services.AddScoped<IOrderReturnRepository, SqlSugarOrderReturnRepository>();
        services.AddScoped<IOrderReturnService, OrderReturnService>();
        services.AddScoped<IStoreVoucherRepository, SqlSugarStoreVoucherRepository>();
        services.AddScoped<IStoreVoucherService, StoreVoucherService>();
        services.AddScoped<ISquareTokenRepository, SqlSugarSquareTokenRepository>();
        services.AddScoped<ISquareTokenService, SquareTokenService>();
        services.AddScoped<ISquareTokenSchemaSqlExecutor, SqlSugarSquareTokenSchemaSqlExecutor>();
        services.AddScoped<ISquareTokenSchemaInitializer, SqlSugarSquareTokenSchemaInitializer>();
        services.AddSingleton<ICatalogIndexCache, CatalogIndexCache>();
        services.AddSingleton<IPriceIndexBuilder, PriceIndexBuilder>();
        services.AddSingleton<IOrderSyncPlanner, OrderSyncPlanner>();
        services.AddSingleton<IStoreVoucherReservationService>(new InMemoryStoreVoucherReservationService(TimeProvider.System));

        return services;
    }
}
