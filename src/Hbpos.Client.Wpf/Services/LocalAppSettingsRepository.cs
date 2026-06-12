namespace Hbpos.Client.Wpf.Services;

public interface ILocalAppSettingsRepository
{
    Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default);

    Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default);

    Task DeleteValueAsync(string key, CancellationToken cancellationToken = default);
}

public sealed class LocalAppSettingsRepository(LocalSqliteStore store) : ILocalAppSettingsRepository
{
    public async Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await EnsureAppSettingsTableAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Value
            FROM AppSettings
            WHERE Key = $Key;
            """;
        command.Parameters.AddWithValue("$Key", key);

        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await EnsureAppSettingsTableAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AppSettings (Key, Value, UpdatedAt)
            VALUES ($Key, $Value, $UpdatedAt)
            ON CONFLICT(Key) DO UPDATE SET
                Value = excluded.Value,
                UpdatedAt = excluded.UpdatedAt;
            """;
        command.Parameters.AddWithValue("$Key", key);
        command.Parameters.AddWithValue("$Value", value);
        command.Parameters.AddWithValue("$UpdatedAt", DateTimeOffset.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await EnsureAppSettingsTableAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM AppSettings
            WHERE Key = $Key;
            """;
        command.Parameters.AddWithValue("$Key", key);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureAppSettingsTableAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS AppSettings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            """;

        // 设置读取可能早于完整本地库初始化，先保证自己的轻量配置表存在。
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
