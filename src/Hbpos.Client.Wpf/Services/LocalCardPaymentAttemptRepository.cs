using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Wpf.Services;

public enum LocalCardPaymentAttemptStatus
{
    Pending,
    SessionStarted,
    Recovering,
    Approved,
    RequiresReview,
    Declined,
    TimedOut,
    Cancelled,
    Failed,
    OrderCompleted,
    Abandoned
}

public sealed record LocalCardPaymentAttempt(
    Guid AttemptGuid,
    string? SessionId,
    string? TxnRef,
    string Processor,
    string Environment,
    string ConnectionMode,
    string TxnType,
    decimal Amount,
    LocalCardPaymentAttemptStatus Status,
    string OrderDraftJson,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string? ResponseCode,
    string? ResponseText,
    string? PaymentReference,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? AcknowledgedAt);

public sealed record LinklyPaymentAttemptContext(
    Guid AttemptGuid,
    Func<string, string?, DateTimeOffset, CancellationToken, Task> BindSessionAsync);

public interface ILinklyPaymentAttemptContextAccessor
{
    LinklyPaymentAttemptContext? Current { get; }

    IDisposable Begin(LinklyPaymentAttemptContext context);
}

public sealed class LinklyPaymentAttemptContextAccessor : ILinklyPaymentAttemptContextAccessor
{
    private readonly AsyncLocal<LinklyPaymentAttemptContext?> _current = new();

    public LinklyPaymentAttemptContext? Current => _current.Value;

    public IDisposable Begin(LinklyPaymentAttemptContext context)
    {
        var previous = _current.Value;
        _current.Value = context;
        return new Scope(this, previous);
    }

    private sealed class Scope(
        LinklyPaymentAttemptContextAccessor owner,
        LinklyPaymentAttemptContext? previous) : IDisposable
    {
        public void Dispose()
        {
            owner._current.Value = previous;
        }
    }
}

public interface ILocalCardPaymentAttemptRepository
{
    Task CreateAsync(LocalCardPaymentAttempt attempt, CancellationToken cancellationToken = default);

    Task UpdateSessionAsync(
        Guid attemptGuid,
        string sessionId,
        string? txnRef,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task UpdateOutcomeAsync(
        Guid attemptGuid,
        LocalCardPaymentAttemptStatus status,
        string? responseCode,
        string? responseText,
        string? paymentReference,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default);

    Task MarkOrderCompletedAsync(
        Guid attemptGuid,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default);

    Task MarkAcknowledgedAsync(
        Guid attemptGuid,
        DateTimeOffset acknowledgedAt,
        CancellationToken cancellationToken = default);

    Task MarkRecoveringAsync(
        Guid attemptGuid,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<LocalCardPaymentAttempt?> GetLatestOpenAttemptAsync(
        string storeCode,
        string deviceCode,
        string cashierId,
        string environment,
        CancellationToken cancellationToken = default);

    Task<LocalCardPaymentAttempt?> GetAttemptAsync(Guid attemptGuid, CancellationToken cancellationToken = default);
}

public sealed class LocalCardPaymentAttemptRepository(LocalSqliteStore store) : ILocalCardPaymentAttemptRepository
{
    private static readonly string[] TerminalStatuses =
    [
        LocalCardPaymentAttemptStatus.Declined.ToString(),
        LocalCardPaymentAttemptStatus.TimedOut.ToString(),
        LocalCardPaymentAttemptStatus.Cancelled.ToString(),
        LocalCardPaymentAttemptStatus.Failed.ToString(),
        LocalCardPaymentAttemptStatus.OrderCompleted.ToString(),
        LocalCardPaymentAttemptStatus.Abandoned.ToString()
    ];

    public async Task CreateAsync(LocalCardPaymentAttempt attempt, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO LocalCardPaymentAttempts
            (
                AttemptGuid,
                SessionId,
                TxnRef,
                Processor,
                Environment,
                ConnectionMode,
                TxnType,
                Amount,
                Status,
                OrderDraftJson,
                StoreCode,
                DeviceCode,
                CashierId,
                ResponseCode,
                ResponseText,
                PaymentReference,
                CreatedAt,
                UpdatedAt,
                CompletedAt,
                AcknowledgedAt
            )
            VALUES
            (
                $AttemptGuid,
                $SessionId,
                $TxnRef,
                $Processor,
                $Environment,
                $ConnectionMode,
                $TxnType,
                $Amount,
                $Status,
                $OrderDraftJson,
                $StoreCode,
                $DeviceCode,
                $CashierId,
                $ResponseCode,
                $ResponseText,
                $PaymentReference,
                $CreatedAt,
                $UpdatedAt,
                $CompletedAt,
                $AcknowledgedAt
            );
            """;
        AddAttemptParameters(command, attempt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateSessionAsync(
        Guid attemptGuid,
        string sessionId,
        string? txnRef,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE LocalCardPaymentAttempts
            SET
                SessionId = $SessionId,
                TxnRef = $TxnRef,
                Status = $Status,
                UpdatedAt = $UpdatedAt
            WHERE AttemptGuid = $AttemptGuid;
            """;
        command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());
        command.Parameters.AddWithValue("$SessionId", sessionId);
        command.Parameters.AddWithValue("$TxnRef", (object?)txnRef ?? DBNull.Value);
        command.Parameters.AddWithValue("$Status", LocalCardPaymentAttemptStatus.SessionStarted.ToString());
        command.Parameters.AddWithValue("$UpdatedAt", updatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateOutcomeAsync(
        Guid attemptGuid,
        LocalCardPaymentAttemptStatus status,
        string? responseCode,
        string? responseText,
        string? paymentReference,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE LocalCardPaymentAttempts
            SET
                Status = $Status,
                ResponseCode = $ResponseCode,
                ResponseText = $ResponseText,
                PaymentReference = $PaymentReference,
                CompletedAt = $CompletedAt,
                UpdatedAt = $UpdatedAt
            WHERE AttemptGuid = $AttemptGuid;
            """;
        command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());
        command.Parameters.AddWithValue("$Status", status.ToString());
        command.Parameters.AddWithValue("$ResponseCode", (object?)responseCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$ResponseText", (object?)responseText ?? DBNull.Value);
        command.Parameters.AddWithValue("$PaymentReference", (object?)paymentReference ?? DBNull.Value);
        command.Parameters.AddWithValue("$CompletedAt", completedAt.ToString("O"));
        command.Parameters.AddWithValue("$UpdatedAt", completedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkOrderCompletedAsync(
        Guid attemptGuid,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE LocalCardPaymentAttempts
            SET
                Status = $Status,
                CompletedAt = COALESCE(CompletedAt, $CompletedAt),
                UpdatedAt = $CompletedAt
            WHERE AttemptGuid = $AttemptGuid;
            """;
        command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());
        command.Parameters.AddWithValue("$Status", LocalCardPaymentAttemptStatus.OrderCompleted.ToString());
        command.Parameters.AddWithValue("$CompletedAt", completedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkAcknowledgedAsync(
        Guid attemptGuid,
        DateTimeOffset acknowledgedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE LocalCardPaymentAttempts
            SET
                AcknowledgedAt = $AcknowledgedAt,
                UpdatedAt = $AcknowledgedAt
            WHERE AttemptGuid = $AttemptGuid;
            """;
        command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());
        command.Parameters.AddWithValue("$AcknowledgedAt", acknowledgedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkRecoveringAsync(
        Guid attemptGuid,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE LocalCardPaymentAttempts
            SET
                Status = $Status,
                UpdatedAt = $UpdatedAt
            WHERE AttemptGuid = $AttemptGuid;
            """;
        command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());
        command.Parameters.AddWithValue("$Status", LocalCardPaymentAttemptStatus.Recovering.ToString());
        command.Parameters.AddWithValue("$UpdatedAt", updatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<LocalCardPaymentAttempt?> GetLatestOpenAttemptAsync(
        string storeCode,
        string deviceCode,
        string cashierId,
        string environment,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                AttemptGuid,
                SessionId,
                TxnRef,
                Processor,
                Environment,
                ConnectionMode,
                TxnType,
                Amount,
                Status,
                OrderDraftJson,
                StoreCode,
                DeviceCode,
                CashierId,
                ResponseCode,
                ResponseText,
                PaymentReference,
                CreatedAt,
                UpdatedAt,
                CompletedAt,
                AcknowledgedAt
            FROM LocalCardPaymentAttempts
            WHERE StoreCode = $StoreCode
              AND DeviceCode = $DeviceCode
              AND CashierId = $CashierId
              AND Environment = $Environment
              AND (
                    Status NOT IN ($TerminalStatus1, $TerminalStatus2, $TerminalStatus3, $TerminalStatus4, $TerminalStatus5, $TerminalStatus6)
                    OR (Status = $OrderCompletedStatus AND AcknowledgedAt IS NULL AND SessionId IS NOT NULL)
                  )
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
        command.Parameters.AddWithValue("$OrderCompletedStatus", LocalCardPaymentAttemptStatus.OrderCompleted.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadAttempt(reader)
            : null;
    }

    public async Task<LocalCardPaymentAttempt?> GetAttemptAsync(
        Guid attemptGuid,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                AttemptGuid,
                SessionId,
                TxnRef,
                Processor,
                Environment,
                ConnectionMode,
                TxnType,
                Amount,
                Status,
                OrderDraftJson,
                StoreCode,
                DeviceCode,
                CashierId,
                ResponseCode,
                ResponseText,
                PaymentReference,
                CreatedAt,
                UpdatedAt,
                CompletedAt,
                AcknowledgedAt
            FROM LocalCardPaymentAttempts
            WHERE AttemptGuid = $AttemptGuid;
            """;
        command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadAttempt(reader)
            : null;
    }

    private static void AddAttemptParameters(SqliteCommand command, LocalCardPaymentAttempt attempt)
    {
        command.Parameters.AddWithValue("$AttemptGuid", attempt.AttemptGuid.ToString());
        command.Parameters.AddWithValue("$SessionId", (object?)attempt.SessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$TxnRef", (object?)attempt.TxnRef ?? DBNull.Value);
        command.Parameters.AddWithValue("$Processor", attempt.Processor);
        command.Parameters.AddWithValue("$Environment", attempt.Environment);
        command.Parameters.AddWithValue("$ConnectionMode", attempt.ConnectionMode);
        command.Parameters.AddWithValue("$TxnType", attempt.TxnType);
        command.Parameters.AddWithValue("$Amount", attempt.Amount);
        command.Parameters.AddWithValue("$Status", attempt.Status.ToString());
        // OrderDraftJson 这里只做原样落盘，业务草稿结构由上层支付流程决定。
        command.Parameters.AddWithValue("$OrderDraftJson", attempt.OrderDraftJson);
        command.Parameters.AddWithValue("$StoreCode", attempt.StoreCode);
        command.Parameters.AddWithValue("$DeviceCode", attempt.DeviceCode);
        command.Parameters.AddWithValue("$CashierId", attempt.CashierId);
        command.Parameters.AddWithValue("$ResponseCode", (object?)attempt.ResponseCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$ResponseText", (object?)attempt.ResponseText ?? DBNull.Value);
        command.Parameters.AddWithValue("$PaymentReference", (object?)attempt.PaymentReference ?? DBNull.Value);
        command.Parameters.AddWithValue("$CreatedAt", attempt.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$UpdatedAt", attempt.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$CompletedAt", attempt.CompletedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$AcknowledgedAt", attempt.AcknowledgedAt?.ToString("O") ?? (object)DBNull.Value);
    }

    private static LocalCardPaymentAttempt ReadAttempt(SqliteDataReader reader)
    {
        return new LocalCardPaymentAttempt(
            ReadGuid(reader, "AttemptGuid"),
            ReadNullableString(reader, "SessionId"),
            ReadNullableString(reader, "TxnRef"),
            ReadString(reader, "Processor"),
            ReadString(reader, "Environment"),
            ReadString(reader, "ConnectionMode"),
            ReadString(reader, "TxnType"),
            ReadDecimal(reader, "Amount"),
            Enum.Parse<LocalCardPaymentAttemptStatus>(ReadString(reader, "Status")),
            ReadString(reader, "OrderDraftJson"),
            ReadString(reader, "StoreCode"),
            ReadString(reader, "DeviceCode"),
            ReadString(reader, "CashierId"),
            ReadNullableString(reader, "ResponseCode"),
            ReadNullableString(reader, "ResponseText"),
            ReadNullableString(reader, "PaymentReference"),
            ReadDateTimeOffset(reader, "CreatedAt"),
            ReadDateTimeOffset(reader, "UpdatedAt"),
            ReadNullableDateTimeOffset(reader, "CompletedAt"),
            ReadNullableDateTimeOffset(reader, "AcknowledgedAt"));
    }

    private static Guid ReadGuid(SqliteDataReader reader, string name)
    {
        return Guid.Parse(ReadString(reader, name));
    }

    private static string ReadString(SqliteDataReader reader, string name)
    {
        return reader.GetString(reader.GetOrdinal(name));
    }

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
