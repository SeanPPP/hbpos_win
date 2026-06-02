namespace Hbpos.Contracts.Linkly;

public static class LinklyBackendPaymentReference
{
    public const string Prefix = "ANZBACKEND";

    private const string SessionPart = "session=";
    private const string EnvironmentPart = "environment=";

    public static string Format(
        string txnRef,
        string sessionId,
        string environment,
        string? refundReference)
    {
        var reference = $"{Prefix}:{Encode(txnRef)}";
        if (!string.IsNullOrWhiteSpace(refundReference))
        {
            reference += $":{Encode(refundReference.Trim())}";
        }

        return $"{reference}:{SessionPart}{Encode(sessionId)}:{EnvironmentPart}{Encode(environment)}";
    }

    public static bool TryGetPrintMarker(
        string? reference,
        out string environment,
        out string sessionId)
    {
        environment = string.Empty;
        sessionId = string.Empty;
        if (!TryReadParts(reference, out var parts))
        {
            return false;
        }

        foreach (var part in parts.Skip(2))
        {
            if (part.StartsWith(SessionPart, StringComparison.OrdinalIgnoreCase))
            {
                sessionId = Decode(part[SessionPart.Length..]);
            }
            else if (part.StartsWith(EnvironmentPart, StringComparison.OrdinalIgnoreCase))
            {
                environment = Decode(part[EnvironmentPart.Length..]);
            }
        }

        return !string.IsNullOrWhiteSpace(environment) &&
            !string.IsNullOrWhiteSpace(sessionId);
    }

    public static string? TryGetRefundReference(string? reference)
    {
        if (!TryReadParts(reference, out var parts) || parts.Length < 3)
        {
            return null;
        }

        return parts
            .Skip(2)
            .Where(part =>
                !part.StartsWith(SessionPart, StringComparison.OrdinalIgnoreCase) &&
                !part.StartsWith(EnvironmentPart, StringComparison.OrdinalIgnoreCase))
            .Select(Decode)
            .FirstOrDefault(part => !string.IsNullOrWhiteSpace(part));
    }

    public static string? GetDisplayReference(string? reference)
    {
        if (!TryReadParts(reference, out var parts))
        {
            return reference;
        }

        var display = $"{Prefix}:{Decode(parts[1])}";
        var refundReference = TryGetRefundReference(reference);
        return string.IsNullOrWhiteSpace(refundReference)
            ? display
            : $"{display}:{refundReference}";
    }

    private static bool TryReadParts(
        string? reference,
        out string[] parts)
    {
        parts = [];
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        parts = reference.Trim().Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 &&
            string.Equals(parts[0], Prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string Encode(string value)
    {
        return Uri.EscapeDataString(value.Trim());
    }

    private static string Decode(string value)
    {
        return Uri.UnescapeDataString(value);
    }
}

public sealed record LinklyCloudBackendTransactionRequest(
    string Environment,
    string TxnType,
    long AmtPurchase,
    IReadOnlyDictionary<string, string>? PurchaseAnalysisData);

public sealed record LinklyCloudBackendRecoverRequest(
    string Environment);

public sealed record LinklyCloudBackendSendKeyRequest(
    string Environment,
    string Key,
    string? Data);

public sealed record LinklyCloudBackendMarkReceiptPrintedRequest(
    string Environment);

public sealed record LinklyCloudBackendSessionResponse(
    string Environment,
    string StoreCode,
    string DeviceCode,
    string SessionId,
    string Status,
    string? TxnRef,
    string? ResponseCode,
    string? ResponseText,
    string? RecoveryAction,
    string? DisplayText,
    bool CancelKeyFlag,
    bool OKKeyFlag,
    bool AcceptYesKeyFlag,
    bool DeclineNoKeyFlag,
    bool AuthoriseKeyFlag,
    string? InputType,
    string? GraphicCode,
    IReadOnlyList<string>? DisplayLines,
    string? ReceiptText,
    int RecoveryCount,
    DateTimeOffset? ReceiptPrintedAt,
    int? LastHttpStatus,
    IReadOnlyList<LinklyCloudBackendNotificationDto> Notifications);

public sealed record LinklyCloudBackendNotificationDto(
    string Type,
    string PayloadJson,
    DateTimeOffset ReceivedAt);

public sealed record LinklyCloudBackendHealthResponse(
    string Environment,
    string StoreCode,
    string DeviceCode,
    bool IsReady,
    string? PublicNotificationBaseUrl,
    IReadOnlyList<LinklyCloudBackendHealthCheckDto> Checks);

public sealed record LinklyCloudBackendHealthCheckDto(
    string Code,
    bool IsReady,
    string Message);
