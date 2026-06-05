using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Wpf.Services;

public enum LocalSquarePaymentAttemptStatus
{
    Pending,
    CheckoutCreated,
    Recovering,
    CheckoutCompleted,
    PaymentVerified,
    Canceled,
    TimedOut,
    Failed,
    Unknown,
    OrderCompleted,
    Abandoned
}

public sealed record LocalSquarePaymentAttempt(
    Guid AttemptGuid,
    string? CheckoutId,
    string IdempotencyKey,
    string DeviceId,
    string LocationId,
    string Environment,
    decimal Amount,
    long AmountCents,
    string Currency,
    LocalSquarePaymentAttemptStatus Status,
    string? CheckoutStatus,
    string? CancelReason,
    string OrderDraftJson,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string? PaymentId,
    string? PaymentStatus,
    string? ResponseCode,
    string? ResponseText,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? OrderCompletedAt,
    DateTimeOffset? ResolvedAt);

public sealed record SquarePaymentAttemptContext(
    Guid AttemptGuid,
    string IdempotencyKey);

public interface ISquarePaymentAttemptContextAccessor
{
    SquarePaymentAttemptContext? Current { get; }

    IDisposable Begin(SquarePaymentAttemptContext context);
}

public sealed class SquarePaymentAttemptContextAccessor : ISquarePaymentAttemptContextAccessor
{
    private readonly AsyncLocal<SquarePaymentAttemptContext?> _current = new();

    public SquarePaymentAttemptContext? Current => _current.Value;

    public IDisposable Begin(SquarePaymentAttemptContext context)
    {
        var previous = _current.Value;
        _current.Value = context;
        return new Scope(this, previous);
    }

    private sealed class Scope(
        SquarePaymentAttemptContextAccessor owner,
        SquarePaymentAttemptContext? previous) : IDisposable
    {
        public void Dispose()
        {
            owner._current.Value = previous;
        }
    }
}

public interface ILocalSquarePaymentAttemptRepository
{
    Task CreateAsync(LocalSquarePaymentAttempt attempt, CancellationToken cancellationToken = default);

    Task MarkCheckoutCreatedAsync(
        Guid attemptGuid,
        string checkoutId,
        string? checkoutStatus,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task MarkRecoveringAsync(Guid attemptGuid, DateTimeOffset updatedAt, CancellationToken cancellationToken = default);

    Task UpdateCheckoutStatusAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus status,
        string? checkoutStatus,
        string? cancelReason,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task MarkPaymentVerifiedAsync(
        Guid attemptGuid,
        string paymentId,
        string paymentStatus,
        string? responseCode,
        string? responseText,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default);

    Task MarkFailedAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus status,
        string? checkoutStatus,
        string? paymentStatus,
        string? responseCode,
        string? responseText,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default);

    Task MarkOrderCompletedAsync(Guid attemptGuid, DateTimeOffset completedAt, CancellationToken cancellationToken = default);

    Task<LocalSquarePaymentAttempt?> GetLatestOpenAttemptAsync(
        string storeCode,
        string deviceCode,
        string cashierId,
        string environment,
        CancellationToken cancellationToken = default);

    Task<LocalSquarePaymentAttempt?> GetAttemptAsync(Guid attemptGuid, CancellationToken cancellationToken = default);
}

public sealed class LocalSquarePaymentAttemptRepository(LocalSqliteStore store) : ILocalSquarePaymentAttemptRepository
{
    private static readonly string[] TerminalStatuses =
    [
        LocalSquarePaymentAttemptStatus.Canceled.ToString(),
        LocalSquarePaymentAttemptStatus.TimedOut.ToString(),
        LocalSquarePaymentAttemptStatus.Failed.ToString(),
        LocalSquarePaymentAttemptStatus.OrderCompleted.ToString(),
        LocalSquarePaymentAttemptStatus.Abandoned.ToString()
    ];

    public async Task CreateAsync(LocalSquarePaymentAttempt attempt, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO LocalSquarePaymentAttempts
            (
                AttemptGuid, CheckoutId, IdempotencyKey, DeviceId, LocationId, Environment,
                Amount, AmountCents, Currency, Status, CheckoutStatus, CancelReason,
                OrderDraftJson, StoreCode, DeviceCode, CashierId, PaymentId, PaymentStatus,
                ResponseCode, ResponseText, CreatedAt, UpdatedAt, CompletedAt, OrderCompletedAt, ResolvedAt
            )
            VALUES
            (
                $AttemptGuid, $CheckoutId, $IdempotencyKey, $DeviceId, $LocationId, $Environment,
                $Amount, $AmountCents, $Currency, $Status, $CheckoutStatus, $CancelReason,
                $OrderDraftJson, $StoreCode, $DeviceCode, $CashierId, $PaymentId, $PaymentStatus,
                $ResponseCode, $ResponseText, $CreatedAt, $UpdatedAt, $CompletedAt, $OrderCompletedAt, $ResolvedAt
            );
            """;
        AddAttemptParameters(command, attempt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkCheckoutCreatedAsync(
        Guid attemptGuid,
        string checkoutId,
        string? checkoutStatus,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        await ExecuteUpdateAsync(
            """
            UPDATE LocalSquarePaymentAttempts
            SET CheckoutId = $CheckoutId,
                CheckoutStatus = $CheckoutStatus,
                Status = $Status,
                UpdatedAt = $UpdatedAt
            WHERE AttemptGuid = $AttemptGuid;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());
                command.Parameters.AddWithValue("$CheckoutId", checkoutId);
                command.Parameters.AddWithValue("$CheckoutStatus", (object?)checkoutStatus ?? DBNull.Value);
                command.Parameters.AddWithValue("$Status", LocalSquarePaymentAttemptStatus.CheckoutCreated.ToString());
                command.Parameters.AddWithValue("$UpdatedAt", updatedAt.ToString("O"));
            },
            cancellationToken);
    }

    public Task MarkRecoveringAsync(Guid attemptGuid, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
    {
        return UpdateCheckoutStatusAsync(
            attemptGuid,
            LocalSquarePaymentAttemptStatus.Recovering,
            checkoutStatus: null,
            cancelReason: null,
            updatedAt,
            cancellationToken);
    }

    public async Task UpdateCheckoutStatusAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus status,
        string? checkoutStatus,
        string? cancelReason,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        await ExecuteUpdateAsync(
            """
            UPDATE LocalSquarePaymentAttempts
            SET Status = $Status,
                CheckoutStatus = COALESCE($CheckoutStatus, CheckoutStatus),
                CancelReason = COALESCE($CancelReason, CancelReason),
                UpdatedAt = $UpdatedAt
            WHERE AttemptGuid = $AttemptGuid;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());
                command.Parameters.AddWithValue("$Status", status.ToString());
                command.Parameters.AddWithValue("$CheckoutStatus", (object?)checkoutStatus ?? DBNull.Value);
                command.Parameters.AddWithValue("$CancelReason", (object?)cancelReason ?? DBNull.Value);
                command.Parameters.AddWithValue("$UpdatedAt", updatedAt.ToString("O"));
            },
            cancellationToken);
    }

    public async Task MarkPaymentVerifiedAsync(
        Guid attemptGuid,
        string paymentId,
        string paymentStatus,
        string? responseCode,
        string? responseText,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        await ExecuteUpdateAsync(
            """
            UPDATE LocalSquarePaymentAttempts
            SET Status = $Status,
                PaymentId = $PaymentId,
                PaymentStatus = $PaymentStatus,
                ResponseCode = $ResponseCode,
                ResponseText = $ResponseText,
                CompletedAt = $CompletedAt,
                UpdatedAt = $CompletedAt
            WHERE AttemptGuid = $AttemptGuid;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());
                command.Parameters.AddWithValue("$Status", LocalSquarePaymentAttemptStatus.PaymentVerified.ToString());
                command.Parameters.AddWithValue("$PaymentId", paymentId);
                command.Parameters.AddWithValue("$PaymentStatus", paymentStatus);
                command.Parameters.AddWithValue("$ResponseCode", (object?)responseCode ?? DBNull.Value);
                command.Parameters.AddWithValue("$ResponseText", (object?)responseText ?? DBNull.Value);
                command.Parameters.AddWithValue("$CompletedAt", completedAt.ToString("O"));
            },
            cancellationToken);
    }

    public async Task MarkFailedAsync(
        Guid attemptGuid,
        LocalSquarePaymentAttemptStatus status,
        string? checkoutStatus,
        string? paymentStatus,
        string? responseCode,
        string? responseText,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default)
    {
        await ExecuteUpdateAsync(
            """
            UPDATE LocalSquarePaymentAttempts
            SET Status = $Status,
                CheckoutStatus = COALESCE($CheckoutStatus, CheckoutStatus),
                PaymentStatus = COALESCE($PaymentStatus, PaymentStatus),
                ResponseCode = $ResponseCode,
                ResponseText = $ResponseText,
                ResolvedAt = $ResolvedAt,
                UpdatedAt = $ResolvedAt
            WHERE AttemptGuid = $AttemptGuid;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());
                command.Parameters.AddWithValue("$Status", status.ToString());
                command.Parameters.AddWithValue("$CheckoutStatus", (object?)checkoutStatus ?? DBNull.Value);
                command.Parameters.AddWithValue("$PaymentStatus", (object?)paymentStatus ?? DBNull.Value);
                command.Parameters.AddWithValue("$ResponseCode", (object?)responseCode ?? DBNull.Value);
                command.Parameters.AddWithValue("$ResponseText", (object?)responseText ?? DBNull.Value);
                command.Parameters.AddWithValue("$ResolvedAt", resolvedAt.ToString("O"));
            },
            cancellationToken);
    }

    public async Task MarkOrderCompletedAsync(Guid attemptGuid, DateTimeOffset completedAt, CancellationToken cancellationToken = default)
    {
        await ExecuteUpdateAsync(
            """
            UPDATE LocalSquarePaymentAttempts
            SET Status = $Status,
                OrderCompletedAt = $CompletedAt,
                UpdatedAt = $CompletedAt
            WHERE AttemptGuid = $AttemptGuid;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());
                command.Parameters.AddWithValue("$Status", LocalSquarePaymentAttemptStatus.OrderCompleted.ToString());
                command.Parameters.AddWithValue("$CompletedAt", completedAt.ToString("O"));
            },
            cancellationToken);
    }

    public async Task<LocalSquarePaymentAttempt?> GetLatestOpenAttemptAsync(
        string storeCode,
        string deviceCode,
        string cashierId,
        string environment,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT *
            FROM LocalSquarePaymentAttempts
            WHERE StoreCode = $StoreCode
              AND DeviceCode = $DeviceCode
              AND CashierId = $CashierId
              AND Environment = $Environment
              AND Status NOT IN ($TerminalStatus1, $TerminalStatus2, $TerminalStatus3, $TerminalStatus4, $TerminalStatus5)
            ORDER BY UpdatedAt DESC, CreatedAt DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$StoreCode", storeCode);
        command.Parameters.AddWithValue("$DeviceCode", deviceCode);
        command.Parameters.AddWithValue("$CashierId", cashierId);
        command.Parameters.AddWithValue("$Environment", environment);
        for (var i = 0; i < TerminalStatuses.Length; i++)
        {
            command.Parameters.AddWithValue($"$TerminalStatus{i + 1}", TerminalStatuses[i]);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAttempt(reader) : null;
    }

    public async Task<LocalSquarePaymentAttempt?> GetAttemptAsync(Guid attemptGuid, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM LocalSquarePaymentAttempts WHERE AttemptGuid = $AttemptGuid;";
        command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAttempt(reader) : null;
    }

    private async Task ExecuteUpdateAsync(
        string sql,
        Action<SqliteCommand> configure,
        CancellationToken cancellationToken)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddAttemptParameters(SqliteCommand command, LocalSquarePaymentAttempt attempt)
    {
        command.Parameters.AddWithValue("$AttemptGuid", attempt.AttemptGuid.ToString());
        command.Parameters.AddWithValue("$CheckoutId", (object?)attempt.CheckoutId ?? DBNull.Value);
        command.Parameters.AddWithValue("$IdempotencyKey", attempt.IdempotencyKey);
        command.Parameters.AddWithValue("$DeviceId", attempt.DeviceId);
        command.Parameters.AddWithValue("$LocationId", attempt.LocationId);
        command.Parameters.AddWithValue("$Environment", attempt.Environment);
        command.Parameters.AddWithValue("$Amount", attempt.Amount);
        command.Parameters.AddWithValue("$AmountCents", attempt.AmountCents);
        command.Parameters.AddWithValue("$Currency", attempt.Currency);
        command.Parameters.AddWithValue("$Status", attempt.Status.ToString());
        command.Parameters.AddWithValue("$CheckoutStatus", (object?)attempt.CheckoutStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$CancelReason", (object?)attempt.CancelReason ?? DBNull.Value);
        // Square attempt 的草稿与 Linkly 分开存，避免 checkout_id 被误当 Linkly session 使用。
        command.Parameters.AddWithValue("$OrderDraftJson", attempt.OrderDraftJson);
        command.Parameters.AddWithValue("$StoreCode", attempt.StoreCode);
        command.Parameters.AddWithValue("$DeviceCode", attempt.DeviceCode);
        command.Parameters.AddWithValue("$CashierId", attempt.CashierId);
        command.Parameters.AddWithValue("$PaymentId", (object?)attempt.PaymentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$PaymentStatus", (object?)attempt.PaymentStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$ResponseCode", (object?)attempt.ResponseCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$ResponseText", (object?)attempt.ResponseText ?? DBNull.Value);
        command.Parameters.AddWithValue("$CreatedAt", attempt.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$UpdatedAt", attempt.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$CompletedAt", attempt.CompletedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$OrderCompletedAt", attempt.OrderCompletedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$ResolvedAt", attempt.ResolvedAt?.ToString("O") ?? (object)DBNull.Value);
    }

    private static LocalSquarePaymentAttempt ReadAttempt(SqliteDataReader reader)
    {
        return new LocalSquarePaymentAttempt(
            ReadGuid(reader, "AttemptGuid"),
            ReadNullableString(reader, "CheckoutId"),
            ReadString(reader, "IdempotencyKey"),
            ReadString(reader, "DeviceId"),
            ReadString(reader, "LocationId"),
            ReadString(reader, "Environment"),
            ReadDecimal(reader, "Amount"),
            ReadInt64(reader, "AmountCents"),
            ReadString(reader, "Currency"),
            Enum.Parse<LocalSquarePaymentAttemptStatus>(ReadString(reader, "Status")),
            ReadNullableString(reader, "CheckoutStatus"),
            ReadNullableString(reader, "CancelReason"),
            ReadString(reader, "OrderDraftJson"),
            ReadString(reader, "StoreCode"),
            ReadString(reader, "DeviceCode"),
            ReadString(reader, "CashierId"),
            ReadNullableString(reader, "PaymentId"),
            ReadNullableString(reader, "PaymentStatus"),
            ReadNullableString(reader, "ResponseCode"),
            ReadNullableString(reader, "ResponseText"),
            ReadDateTimeOffset(reader, "CreatedAt"),
            ReadDateTimeOffset(reader, "UpdatedAt"),
            ReadNullableDateTimeOffset(reader, "CompletedAt"),
            ReadNullableDateTimeOffset(reader, "OrderCompletedAt"),
            ReadNullableDateTimeOffset(reader, "ResolvedAt"));
    }

    private static Guid ReadGuid(SqliteDataReader reader, string name) => Guid.Parse(ReadString(reader, name));

    private static string ReadString(SqliteDataReader reader, string name) => reader.GetString(reader.GetOrdinal(name));

    private static string? ReadNullableString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static decimal ReadDecimal(SqliteDataReader reader, string name)
    {
        var value = reader.GetValue(reader.GetOrdinal(name));
        return value switch
        {
            decimal decimalValue => decimalValue,
            double doubleValue => Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture),
            long longValue => longValue,
            int intValue => intValue,
            string stringValue => decimal.Parse(stringValue, CultureInfo.InvariantCulture),
            _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture)
        };
    }

    private static long ReadInt64(SqliteDataReader reader, string name)
    {
        var value = reader.GetValue(reader.GetOrdinal(name));
        return value switch
        {
            long longValue => longValue,
            int intValue => intValue,
            string stringValue => long.Parse(stringValue, CultureInfo.InvariantCulture),
            _ => Convert.ToInt64(value, CultureInfo.InvariantCulture)
        };
    }

    private static DateTimeOffset ReadDateTimeOffset(SqliteDataReader reader, string name)
    {
        return DateTimeOffset.Parse(ReadString(reader, name), CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(SqliteDataReader reader, string name)
    {
        var value = ReadNullableString(reader, name);
        return string.IsNullOrWhiteSpace(value)
            ? null
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    }
}
