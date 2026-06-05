using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hbpos.Client.Wpf.Services;

internal static class LinklyJsonLog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string Build(
        string source,
        string operation,
        string phase,
        string? direction = null,
        CardTerminalEnvironment? environment = null,
        string? sessionId = null,
        int? httpStatus = null,
        bool? success = null,
        string? reason = null,
        long? elapsedMs = null,
        object? request = null,
        object? response = null,
        object? details = null)
    {
        // Linkly 诊断要求保留原始字段，helper 只负责统一 JSON 外形，不做脱敏。
        return JsonSerializer.Serialize(
            new LinklyLogEvent(
                source,
                operation,
                phase,
                direction,
                environment?.ToString(),
                sessionId,
                httpStatus,
                success,
                reason,
                elapsedMs,
                request,
                response,
                details),
            JsonOptions);
    }

    public static void Write(
        string category,
        string source,
        string operation,
        string phase,
        string? direction = null,
        CardTerminalEnvironment? environment = null,
        string? sessionId = null,
        HttpStatusCode? httpStatus = null,
        bool? success = null,
        string? reason = null,
        long? elapsedMs = null,
        object? request = null,
        object? response = null,
        object? details = null)
    {
        ConsoleLog.Write(
            category,
            Build(
                source,
                operation,
                phase,
                direction,
                environment,
                sessionId,
                httpStatus.HasValue ? (int)httpStatus.Value : null,
                success,
                reason,
                elapsedMs,
                request,
                response,
                details));
    }

    public static void WriteMessage(string category, string source, string message)
    {
        var operation = InferOperation(message);
        var phase = InferPhase(message);
        Write(
            category,
            source,
            operation,
            phase,
            details: new
            {
                message
            });
    }

    private static string InferOperation(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "linkly";
        }

        var trimmed = message.Trim();
        var index = trimmed.IndexOf(' ', StringComparison.Ordinal);
        return index <= 0 ? trimmed : trimmed[..index];
    }

    private static string InferPhase(string message)
    {
        if (message.Contains("blocked", StringComparison.OrdinalIgnoreCase))
        {
            return "blocked";
        }

        if (message.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            return "failed";
        }

        if (message.Contains("succeeded", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("completed", StringComparison.OrdinalIgnoreCase))
        {
            return "succeeded";
        }

        if (message.Contains("response", StringComparison.OrdinalIgnoreCase))
        {
            return "response";
        }

        if (message.Contains("request", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("start", StringComparison.OrdinalIgnoreCase))
        {
            return "request";
        }

        return "event";
    }

    private sealed record LinklyLogEvent(
        string Source,
        string Operation,
        string Phase,
        string? Direction,
        string? Environment,
        string? SessionId,
        int? HttpStatus,
        bool? Success,
        string? Reason,
        long? ElapsedMs,
        object? Request,
        object? Response,
        object? Details);
}
