using System.Collections.ObjectModel;
using System.Globalization;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

public enum CardRecoveryResultSeverity
{
    Success,
    Warning,
    Error
}

public sealed class CardRecoveryResultDialogViewModel
{
    public CardRecoveryResultDialogViewModel(
        string title,
        string message,
        CardRecoveryResultSeverity severity,
        Guid? orderGuid,
        decimal? amount,
        string? sessionId,
        string? txnRef,
        string? responseCode,
        string? responseText,
        DateTimeOffset timestamp,
        IEnumerable<ReceiptPreviewRow>? receiptPreviewRows = null,
        bool canPrintReceipt = false,
        string printButtonText = "Print receipt")
    {
        Title = title;
        Message = message;
        Severity = severity;
        OrderGuid = orderGuid;
        Amount = amount;
        SessionId = Normalize(sessionId);
        TxnRef = Normalize(txnRef);
        ResponseCode = Normalize(responseCode);
        ResponseText = Normalize(responseText);
        Timestamp = timestamp;
        CanPrintReceipt = canPrintReceipt;
        PrintButtonText = printButtonText;
        ReceiptPreviewRows = new ObservableCollection<ReceiptPreviewRow>(receiptPreviewRows ?? []);
    }

    public string Title { get; }

    public string Message { get; }

    public CardRecoveryResultSeverity Severity { get; }

    public Guid? OrderGuid { get; }

    public decimal? Amount { get; }

    public string? SessionId { get; }

    public string? TxnRef { get; }

    public string? ResponseCode { get; }

    public string? ResponseText { get; }

    public DateTimeOffset Timestamp { get; }

    public ObservableCollection<ReceiptPreviewRow> ReceiptPreviewRows { get; }

    public bool CanPrintReceipt { get; }

    public string PrintButtonText { get; }

    public bool HasReceiptPreview => ReceiptPreviewRows.Count > 0;

    public bool HasOrderGuid => OrderGuid is not null;

    public bool HasAmount => Amount is not null;

    public bool HasSessionId => !string.IsNullOrWhiteSpace(SessionId);

    public bool HasTxnRef => !string.IsNullOrWhiteSpace(TxnRef);

    public bool HasResponseCode => !string.IsNullOrWhiteSpace(ResponseCode);

    public bool HasResponseText => !string.IsNullOrWhiteSpace(ResponseText);

    public string AmountDisplay => Amount?.ToString("C2", CultureInfo.CurrentCulture) ?? "-";

    public string OrderGuidDisplay => OrderGuid?.ToString("D") ?? "-";

    public string TimestampDisplay => Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.CurrentCulture);

    public bool IsSuccess => Severity == CardRecoveryResultSeverity.Success;

    public bool IsWarning => Severity == CardRecoveryResultSeverity.Warning;

    public bool IsError => Severity == CardRecoveryResultSeverity.Error;

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
