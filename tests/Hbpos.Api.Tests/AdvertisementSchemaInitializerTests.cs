using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Shared.Models.HBweb;
using Hbpos.Api.Data;
using Hbpos.Api.Services;
using SqlSugar;

namespace Hbpos.Api.Tests;

public sealed class AdvertisementSchemaInitializerTests
{
    [Fact]
    public async Task InitializeAsync_CreatesAdvertisementTablesInMainDatabase()
    {
        await using var fixture = AdvertisementSchemaFixture.Create();
        var initializer = new SqlSugarAdvertisementSchemaInitializer(fixture.DbContext);

        Assert.False(await fixture.TableExistsAsync("Advertisement"));
        Assert.False(await fixture.TableExistsAsync("AdvertisementStore"));

        await initializer.InitializeAsync();

        Assert.True(await fixture.TableExistsAsync("Advertisement"));
        Assert.True(await fixture.TableExistsAsync("AdvertisementStore"));
        Assert.Equal(0, await fixture.MainDb.Queryable<Advertisement>().CountAsync());
        Assert.Equal(0, await fixture.MainDb.Queryable<AdvertisementStore>().CountAsync());
    }

    private sealed class AdvertisementSchemaFixture : IAsyncDisposable
    {
        private readonly string mainDatabasePath = Path.Combine(
            Path.GetTempPath(),
            $"hbpos-ad-schema-main-{Guid.NewGuid():N}.db");

        private AdvertisementSchemaFixture()
        {
            MainDb = CreateClient(mainDatabasePath);
            PosmDb = CreateUnusablePosmClient();
            DbContext = CreateDbContext(MainDb, PosmDb);
        }

        public ISqlSugarClient MainDb { get; }

        public ISqlSugarClient PosmDb { get; }

        public HbposSqlSugarContext DbContext { get; }

        public static AdvertisementSchemaFixture Create()
        {
            return new AdvertisementSchemaFixture();
        }

        public async Task<bool> TableExistsAsync(string tableName)
        {
            var count = await MainDb.Ado.GetIntAsync(
                "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = @tableName",
                new SugarParameter("@tableName", tableName));
            return count > 0;
        }

        public ValueTask DisposeAsync()
        {
            MainDb.Dispose();
            PosmDb.Dispose();
            DeleteIfExists(mainDatabasePath);
            return ValueTask.CompletedTask;
        }

        private static ISqlSugarClient CreateClient(string databasePath)
        {
            return new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = $"Data Source={databasePath}",
                DbType = DbType.Sqlite,
                InitKeyType = InitKeyType.Attribute,
                IsAutoCloseConnection = true
            });
        }

        private static ISqlSugarClient CreateUnusablePosmClient()
        {
            return new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = "Server=127.0.0.1,1;Database=hbpos_unreachable;User Id=invalid;Password=invalid;TrustServerCertificate=True;Connection Timeout=1",
                DbType = DbType.SqlServer,
                InitKeyType = InitKeyType.Attribute,
                IsAutoCloseConnection = true
            });
        }

        private static HbposSqlSugarContext CreateDbContext(ISqlSugarClient mainDb, ISqlSugarClient posmDb)
        {
            var context = (HbposSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HbposSqlSugarContext));
            SetAutoProperty(context, nameof(HbposSqlSugarContext.MainDb), mainDb);
            SetAutoProperty(context, nameof(HbposSqlSugarContext.PosmDb), posmDb);
            return context;
        }

        private static void SetAutoProperty(HbposSqlSugarContext context, string propertyName, ISqlSugarClient value)
        {
            var backingField = typeof(HbposSqlSugarContext).GetField(
                $"<{propertyName}>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(backingField);
            backingField!.SetValue(context, value);
        }

        private static void DeleteIfExists(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // SQLite 可能短暂占用测试库文件，不影响断言结果。
            }
        }
    }
}
