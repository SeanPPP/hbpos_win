using BlazorApp.Shared.Models.HBweb;
using Hbpos.Api.Data;

namespace Hbpos.Api.Services;

public interface IAdvertisementSchemaInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public sealed class SqlSugarAdvertisementSchemaInitializer(HbposSqlSugarContext dbContext)
    : IAdvertisementSchemaInitializer
{
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        /* 广告播放读取管理主库，启动时确保表存在，避免客显拉取广告时才暴露缺表错误。 */
        dbContext.MainDb.CodeFirst.InitTables<Advertisement, AdvertisementStore>();
        return Task.CompletedTask;
    }
}
