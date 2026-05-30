using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public interface IInstallmentOrderService
{
    Task<IReadOnlyList<InstallmentOrderSummary>> GetOrdersAsync(PosSessionState session, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InstallmentOrderSummary>> SearchAsync(PosSessionState session, string? keyword, CancellationToken cancellationToken = default);

    Task<LocalInstallmentOrder?> GetLocalOrderAsync(Guid installmentGuid, CancellationToken cancellationToken = default);

    Task<InstallmentWriteResult<InstallmentCreateResponse>> CreateAsync(PosSessionState session, InstallmentCreateRequest request, CancellationToken cancellationToken = default);

    Task<InstallmentWriteResult<InstallmentAppendPaymentResponse>> AppendPaymentAsync(PosSessionState session, InstallmentAppendPaymentRequest request, CancellationToken cancellationToken = default);

    Task<InstallmentWriteResult<InstallmentConfirmPickupResponse>> ConfirmPickupAsync(PosSessionState session, InstallmentConfirmPickupRequest request, CancellationToken cancellationToken = default);

    Task<InstallmentWriteResult<InstallmentCancelResponse>> CancelWithRefundAsync(PosSessionState session, InstallmentCancelRequest request, CancellationToken cancellationToken = default);

    Task<InstallmentWriteResult<InstallmentVoidResponse>> VoidCancelAsync(PosSessionState session, InstallmentVoidRequest request, CancellationToken cancellationToken = default);

    Task<InstallmentOrderCreateResult> CreateOrderAsync(InstallmentOrderCreateRequest request, CancellationToken cancellationToken = default);

    Task<InstallmentOrderActionResult> AddRepaymentAsync(InstallmentOrderRepaymentRequest request, CancellationToken cancellationToken = default);

    Task<InstallmentOrderActionResult> CancelWithRefundAsync(Guid orderId, PosSessionState session, CancellationToken cancellationToken = default);

    Task<InstallmentOrderActionResult> VoidCancelAsync(Guid orderId, PosSessionState session, string? reason = null, CancellationToken cancellationToken = default);

    Task<InstallmentOrderActionResult> ConfirmPickupAsync(Guid orderId, PosSessionState session, CancellationToken cancellationToken = default);
}

public interface IInstallmentApiClient
{
    Task<InstallmentCreateResponse> CreateAsync(InstallmentCreateRequest request, CancellationToken cancellationToken = default);

    Task<InstallmentAppendPaymentResponse> AppendPaymentAsync(InstallmentAppendPaymentRequest request, CancellationToken cancellationToken = default);

    Task<InstallmentConfirmPickupResponse> ConfirmPickupAsync(InstallmentConfirmPickupRequest request, CancellationToken cancellationToken = default);

    Task<InstallmentCancelResponse> CancelAsync(InstallmentCancelRequest request, CancellationToken cancellationToken = default);

    Task<InstallmentVoidResponse> VoidAsync(InstallmentVoidRequest request, CancellationToken cancellationToken = default);
}

public sealed class InstallmentOrderService(
    ILocalInstallmentOrderRepository localRepository,
    IInstallmentApiClient apiClient,
    PosCartService? cart = null) : IInstallmentOrderService
{
    public async Task<IReadOnlyList<InstallmentOrderSummary>> GetOrdersAsync(PosSessionState session, CancellationToken cancellationToken = default)
    {
        var orders = await localRepository.GetRecentByStoreAsync(session.StoreCode, cancellationToken: cancellationToken);
        return orders.Select(MapSummary).ToList();
    }

    public async Task<IReadOnlyList<InstallmentOrderSummary>> SearchAsync(PosSessionState session, string? keyword, CancellationToken cancellationToken = default)
    {
        var orders = await localRepository.GetRecentByStoreAsync(session.StoreCode, 200, cancellationToken);
        var normalized = string.IsNullOrWhiteSpace(keyword) ? string.Empty : keyword.Trim();
        return orders
            .Where(order => string.IsNullOrWhiteSpace(normalized) ||
                order.InstallmentNumber.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                order.CustomerName.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                order.CustomerPhone.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .Select(MapSummary)
            .ToList();
    }

    public Task<LocalInstallmentOrder?> GetLocalOrderAsync(Guid installmentGuid, CancellationToken cancellationToken = default)
    {
        return localRepository.GetAsync(installmentGuid, cancellationToken);
    }

    public async Task<InstallmentWriteResult<InstallmentCreateResponse>> CreateAsync(PosSessionState session, InstallmentCreateRequest request, CancellationToken cancellationToken = default)
    {
        if (!session.IsOnline)
        {
            return InstallmentWriteResult<InstallmentCreateResponse>.OnlineRequired("OnlineRequired");
        }

        var response = await apiClient.CreateAsync(request, cancellationToken);
        var localOrder = await SaveSnapshotAsync(response.Details, cancellationToken);
        cart?.Clear();
        return InstallmentWriteResult<InstallmentCreateResponse>.Success(response, localOrder, response.Message);
    }

    public async Task<InstallmentWriteResult<InstallmentAppendPaymentResponse>> AppendPaymentAsync(PosSessionState session, InstallmentAppendPaymentRequest request, CancellationToken cancellationToken = default)
    {
        if (!session.IsOnline)
        {
            return InstallmentWriteResult<InstallmentAppendPaymentResponse>.OnlineRequired("OnlineRequired");
        }

        var response = await apiClient.AppendPaymentAsync(request, cancellationToken);
        var localOrder = await SaveSnapshotAsync(response.Details, cancellationToken);
        return InstallmentWriteResult<InstallmentAppendPaymentResponse>.Success(response, localOrder, response.Message);
    }

    public async Task<InstallmentWriteResult<InstallmentConfirmPickupResponse>> ConfirmPickupAsync(PosSessionState session, InstallmentConfirmPickupRequest request, CancellationToken cancellationToken = default)
    {
        if (!session.IsOnline)
        {
            return InstallmentWriteResult<InstallmentConfirmPickupResponse>.OnlineRequired("OnlineRequired");
        }

        var response = await apiClient.ConfirmPickupAsync(request, cancellationToken);
        var localOrder = await SaveSnapshotAsync(response.Details, cancellationToken);
        return InstallmentWriteResult<InstallmentConfirmPickupResponse>.Success(response, localOrder);
    }

    public async Task<InstallmentWriteResult<InstallmentCancelResponse>> CancelWithRefundAsync(PosSessionState session, InstallmentCancelRequest request, CancellationToken cancellationToken = default)
    {
        if (!session.IsOnline)
        {
            return InstallmentWriteResult<InstallmentCancelResponse>.OnlineRequired("OnlineRequired");
        }

        var response = await apiClient.CancelAsync(request, cancellationToken);
        var localOrder = await SaveSnapshotAsync(response.Details, cancellationToken);
        return InstallmentWriteResult<InstallmentCancelResponse>.Success(response, localOrder, response.Message);
    }

    public async Task<InstallmentWriteResult<InstallmentVoidResponse>> VoidCancelAsync(PosSessionState session, InstallmentVoidRequest request, CancellationToken cancellationToken = default)
    {
        if (!session.IsOnline)
        {
            return InstallmentWriteResult<InstallmentVoidResponse>.OnlineRequired("OnlineRequired");
        }

        var response = await apiClient.VoidAsync(request, cancellationToken);
        var localOrder = await SaveSnapshotAsync(response.Details, cancellationToken);
        return InstallmentWriteResult<InstallmentVoidResponse>.Success(response, localOrder, response.Message);
    }

    public async Task<InstallmentOrderCreateResult> CreateOrderAsync(InstallmentOrderCreateRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.Session.IsOnline)
        {
            return new InstallmentOrderCreateResult(false, "OnlineRequired");
        }

        var installmentGuid = Guid.NewGuid();
        var payment = request.DownPayment with
        {
            Amount = Math.Min(request.DownPayment.Amount, request.CartSnapshot.ActualAmount),
            IdempotencyKey = EnsureIdempotencyKey(request.DownPayment.IdempotencyKey, installmentGuid)
        };
        var apiRequest = new InstallmentCreateRequest(
            installmentGuid,
            request.Session.StoreCode,
            request.Session.DeviceCode,
            request.Session.CashierId,
            request.Session.CashierName,
            DateTimeOffset.Now,
            request.CartSnapshot.ActualAmount,
            payment.Amount,
            request.CartSnapshot.Lines.Select(line => new InstallmentLineDto(
                Guid.NewGuid(),
                line.ProductCode,
                line.ReferenceCode,
                line.DisplayName,
                line.LookupCode,
                line.Quantity,
                line.UnitPrice,
                line.DiscountAmount,
                line.ActualAmount,
                line.ItemNumber)).ToList(),
            new InstallmentPaymentCommandDto(payment.PaymentGuid, payment.Method, payment.Amount, payment.Reference, payment.ReservationToken, payment.CardTransactions, payment.IdempotencyKey),
            request.CustomerName.Trim(),
            request.CustomerPhone.Trim(),
            request.Note.Trim());
        var result = await CreateAsync(request.Session, apiRequest, cancellationToken);
        return result.Status == InstallmentWriteStatus.Succeeded && result.LocalOrder is not null
            ? new InstallmentOrderCreateResult(true, result.Message ?? $"已创建分期单 {result.LocalOrder.InstallmentNumber}。", MapSummary(result.LocalOrder))
            : new InstallmentOrderCreateResult(false, result.Message ?? result.Status.ToString());
    }

    public async Task<InstallmentOrderActionResult> AddRepaymentAsync(InstallmentOrderRepaymentRequest request, CancellationToken cancellationToken = default)
    {
        var local = await localRepository.GetAsync(request.InstallmentGuid, cancellationToken);
        if (local is null)
        {
            return new InstallmentOrderActionResult(false, "未找到本机缓存的分期单。");
        }

        var apiRequest = new InstallmentAppendPaymentRequest(
            request.InstallmentGuid,
            request.Payment.PaymentGuid,
            request.Session.StoreCode,
            request.Session.DeviceCode,
            request.Session.CashierId,
            request.Session.CashierName,
            Math.Min(request.Payment.Amount, local.BalanceAmount),
            request.Payment.Method,
            request.Payment.Reference,
            request.Payment.ReservationToken,
            request.Payment.CardTransactions,
            EnsureIdempotencyKey(request.Payment.IdempotencyKey, request.InstallmentGuid));
        var result = await AppendPaymentAsync(request.Session, apiRequest, cancellationToken);
        return new InstallmentOrderActionResult(result.Status == InstallmentWriteStatus.Succeeded, result.Message ?? "补款已记录。", result.LocalOrder is null ? null : MapSummary(result.LocalOrder));
    }

    public Task<InstallmentOrderActionResult> AddRepaymentAsync(
        Guid orderId,
        PosSessionState session,
        decimal amount,
        PaymentMethodKind method,
        string? reference,
        string? reservationToken,
        CancellationToken cancellationToken = default)
    {
        return AddRepaymentAsync(new InstallmentOrderRepaymentRequest(orderId, session, new InstallmentPaymentDraft(Guid.NewGuid(), method, amount, reference, reservationToken)), cancellationToken);
    }

    public async Task<InstallmentOrderActionResult> CancelWithRefundAsync(Guid orderId, PosSessionState session, CancellationToken cancellationToken = default)
    {
        var local = await localRepository.GetAsync(orderId, cancellationToken);
        if (local is null)
        {
            return new InstallmentOrderActionResult(false, "未找到本机缓存的分期单。");
        }

        var refunds = local.Payments
            .Where(payment => payment.Status == InstallmentPaymentStatus.Recorded && payment.Amount > 0m)
            .Select(payment => new InstallmentRefundPaymentCommandDto(Guid.NewGuid(), payment.Method, payment.Amount, payment.Reference, payment.CardTransactions, $"{local.InstallmentGuid:D}:refund:{payment.PaymentGuid:D}"))
            .ToList();
        var result = await CancelWithRefundAsync(
            session,
            new InstallmentCancelRequest(local.InstallmentGuid, session.StoreCode, session.DeviceCode, session.CashierId, session.CashierName, DateTimeOffset.Now, refunds, "取消分期并退款", $"{local.InstallmentGuid:D}:cancel"),
            cancellationToken);
        return new InstallmentOrderActionResult(result.Status == InstallmentWriteStatus.Succeeded, result.Message ?? "分期单已取消并退款。", result.LocalOrder is null ? null : MapSummary(result.LocalOrder));
    }

    public Task<InstallmentOrderActionResult> CancelWithRefundAsync(Guid orderId, PosSessionState session, string? reason, CancellationToken cancellationToken = default)
    {
        return CancelWithRefundAsync(orderId, session, cancellationToken);
    }

    public async Task<InstallmentOrderActionResult> VoidCancelAsync(Guid orderId, PosSessionState session, string? reason = null, CancellationToken cancellationToken = default)
    {
        var result = await VoidCancelAsync(
            session,
            new InstallmentVoidRequest(orderId, session.StoreCode, session.DeviceCode, session.CashierId, session.CashierName, DateTimeOffset.Now, string.IsNullOrWhiteSpace(reason) ? "作废分期单" : reason.Trim(), $"{orderId:D}:void"),
            cancellationToken);
        return new InstallmentOrderActionResult(result.Status == InstallmentWriteStatus.Succeeded, result.Message ?? "分期单已作废。", result.LocalOrder is null ? null : MapSummary(result.LocalOrder));
    }

    public Task<InstallmentOrderActionResult> VoidAsync(Guid orderId, PosSessionState session, string? reason = null, CancellationToken cancellationToken = default)
    {
        return VoidCancelAsync(orderId, session, reason, cancellationToken);
    }

    public async Task<InstallmentOrderActionResult> ConfirmPickupAsync(Guid orderId, PosSessionState session, CancellationToken cancellationToken = default)
    {
        var result = await ConfirmPickupAsync(session, new InstallmentConfirmPickupRequest(orderId, session.StoreCode, session.DeviceCode, session.CashierId, session.CashierName, DateTimeOffset.Now), cancellationToken);
        return new InstallmentOrderActionResult(result.Status == InstallmentWriteStatus.Succeeded, result.Message ?? result.Status.ToString(), result.LocalOrder is null ? null : MapSummary(result.LocalOrder));
    }

    private async Task<LocalInstallmentOrder> SaveSnapshotAsync(InstallmentDetailsDto details, CancellationToken cancellationToken)
    {
        var localOrder = new LocalInstallmentOrder(details.InstallmentGuid, details.InstallmentGuid, details.InstallmentNumber, details.StoreCode, details.DeviceCode, details.CashierId, details.CashierName, details.CustomerName, details.CustomerPhone, details.CreatedAt, DateTimeOffset.UtcNow, details.TotalAmount, details.MinimumDownPayment, details.DownPaymentAmount, details.PaidAmount, details.BalanceAmount, details.Status, details.Lines, details.Payments, details.PickupInfo, details.Note, details.CancellationInfo);
        await localRepository.UpsertAsync(localOrder, cancellationToken);
        return localOrder;
    }

    private static InstallmentOrderSummary MapSummary(LocalInstallmentOrder order)
    {
        return new InstallmentOrderSummary(order.InstallmentGuid, order.InstallmentNumber, order.CustomerName, order.CustomerPhone, order.TotalAmount, order.DownPaymentAmount, order.PaidAmount, order.BalanceAmount, 0, order.Status == InstallmentStatus.Active && order.BalanceAmount > 0m, order.Status == InstallmentStatus.PaidOff, order.Status == InstallmentStatus.Active && order.BalanceAmount > 0m, order.Status == InstallmentStatus.Active && order.BalanceAmount > 0m, order.Status.ToString(), order.DeviceCode, order.UpdatedAt);
    }

    private static string EnsureIdempotencyKey(string? value, Guid scope) => string.IsNullOrWhiteSpace(value) ? $"{scope:D}:{Guid.NewGuid():D}" : value.Trim();
}

public sealed record InstallmentOrderSummary(Guid OrderId, string OrderNumber, string CustomerName, string CustomerPhone, decimal TotalAmount, decimal DownPaymentAmount, decimal PaidAmount, decimal OutstandingAmount, int InstallmentMonths, bool CanAddRepayment, bool CanConfirmPickup, bool CanCancelRefund, bool CanVoid, string Status, string DeviceCode, DateTimeOffset UpdatedAt)
{
    public bool CanCancelWithRefund => CanCancelRefund;

    public bool CanVoidCancel => CanVoid;

    public string DownPaymentMethod => string.Empty;
}

public sealed record InstallmentPaymentDraft(Guid PaymentGuid, PaymentMethodKind Method, decimal Amount, string? Reference = null, string? ReservationToken = null, IReadOnlyList<CardTransactionDto>? CardTransactions = null, string? IdempotencyKey = null);

public sealed record InstallmentOrderCreateRequest(PosSessionState Session, PosCartServiceSnapshot CartSnapshot, string CustomerName, string CustomerPhone, decimal DownPaymentAmount, InstallmentPaymentDraft DownPayment, string Note)
{
    public InstallmentOrderCreateRequest(PosSessionState session, PosCartServiceSnapshot cartSnapshot, string customerName, string customerPhone, int installmentMonths, decimal downPaymentAmount, PaymentMethodKind method, string? reference, string? reservationToken, string note)
        : this(session, cartSnapshot, customerName, customerPhone, downPaymentAmount, new InstallmentPaymentDraft(Guid.NewGuid(), method, downPaymentAmount, reference, reservationToken), note)
    {
    }
}

public sealed record InstallmentOrderRepaymentRequest(Guid InstallmentGuid, PosSessionState Session, InstallmentPaymentDraft Payment);

public sealed record InstallmentOrderCreateResult(bool Succeeded, string Message, InstallmentOrderSummary? Order = null);

public sealed record InstallmentOrderActionResult(bool Succeeded, string Message, InstallmentOrderSummary? Order = null);

public sealed class InstallmentApiClient(HttpClient httpClient) : IInstallmentApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<InstallmentCreateResponse> CreateAsync(InstallmentCreateRequest request, CancellationToken cancellationToken = default) => PostAsync<InstallmentCreateRequest, InstallmentCreateResponse>("api/v1/installments", request, cancellationToken);

    public Task<InstallmentAppendPaymentResponse> AppendPaymentAsync(InstallmentAppendPaymentRequest request, CancellationToken cancellationToken = default) => PostAsync<InstallmentAppendPaymentRequest, InstallmentAppendPaymentResponse>($"api/v1/installments/{request.InstallmentGuid:D}/payments", request, cancellationToken);

    public Task<InstallmentConfirmPickupResponse> ConfirmPickupAsync(InstallmentConfirmPickupRequest request, CancellationToken cancellationToken = default) => PostAsync<InstallmentConfirmPickupRequest, InstallmentConfirmPickupResponse>($"api/v1/installments/{request.InstallmentGuid:D}/pickup", request, cancellationToken);

    public Task<InstallmentCancelResponse> CancelAsync(InstallmentCancelRequest request, CancellationToken cancellationToken = default) => PostAsync<InstallmentCancelRequest, InstallmentCancelResponse>($"api/v1/installments/{request.InstallmentGuid:D}/cancel", request, cancellationToken);

    public Task<InstallmentVoidResponse> VoidAsync(InstallmentVoidRequest request, CancellationToken cancellationToken = default) => PostAsync<InstallmentVoidRequest, InstallmentVoidResponse>($"api/v1/installments/{request.InstallmentGuid:D}/void", request, cancellationToken);

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(path, request, JsonOptions, cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<ApiResult<TResponse>>(JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode || payload?.Success != true || payload.Data is null)
        {
            throw new CatalogApiException(payload?.Message ?? $"Installment API request failed with HTTP {(int)response.StatusCode}.", response.StatusCode, payload?.ErrorCode);
        }

        return payload.Data;
    }
}

public sealed class NoopInstallmentOrderService : IInstallmentOrderService
{
    public static NoopInstallmentOrderService Instance { get; } = new();

    private NoopInstallmentOrderService() { }

    public Task<IReadOnlyList<InstallmentOrderSummary>> GetOrdersAsync(PosSessionState session, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<InstallmentOrderSummary>>([]);
    public Task<IReadOnlyList<InstallmentOrderSummary>> SearchAsync(PosSessionState session, string? keyword, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<InstallmentOrderSummary>>([]);
    public Task<LocalInstallmentOrder?> GetLocalOrderAsync(Guid installmentGuid, CancellationToken cancellationToken = default) => Task.FromResult<LocalInstallmentOrder?>(null);
    public Task<InstallmentWriteResult<InstallmentCreateResponse>> CreateAsync(PosSessionState session, InstallmentCreateRequest request, CancellationToken cancellationToken = default) => Task.FromResult(InstallmentWriteResult<InstallmentCreateResponse>.OnlineRequired(session.IsOnline ? "分期服务尚未接入。" : "OnlineRequired"));
    public Task<InstallmentWriteResult<InstallmentAppendPaymentResponse>> AppendPaymentAsync(PosSessionState session, InstallmentAppendPaymentRequest request, CancellationToken cancellationToken = default) => Task.FromResult(InstallmentWriteResult<InstallmentAppendPaymentResponse>.OnlineRequired(session.IsOnline ? "分期服务尚未接入。" : "OnlineRequired"));
    public Task<InstallmentWriteResult<InstallmentConfirmPickupResponse>> ConfirmPickupAsync(PosSessionState session, InstallmentConfirmPickupRequest request, CancellationToken cancellationToken = default) => Task.FromResult(InstallmentWriteResult<InstallmentConfirmPickupResponse>.OnlineRequired(session.IsOnline ? "分期服务尚未接入。" : "OnlineRequired"));
    public Task<InstallmentWriteResult<InstallmentCancelResponse>> CancelWithRefundAsync(PosSessionState session, InstallmentCancelRequest request, CancellationToken cancellationToken = default) => Task.FromResult(InstallmentWriteResult<InstallmentCancelResponse>.OnlineRequired(session.IsOnline ? "分期服务尚未接入。" : "OnlineRequired"));
    public Task<InstallmentWriteResult<InstallmentVoidResponse>> VoidCancelAsync(PosSessionState session, InstallmentVoidRequest request, CancellationToken cancellationToken = default) => Task.FromResult(InstallmentWriteResult<InstallmentVoidResponse>.OnlineRequired(session.IsOnline ? "分期服务尚未接入。" : "OnlineRequired"));
    public Task<InstallmentOrderCreateResult> CreateOrderAsync(InstallmentOrderCreateRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new InstallmentOrderCreateResult(false, "分期服务尚未接入。"));
    public Task<InstallmentOrderActionResult> AddRepaymentAsync(InstallmentOrderRepaymentRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new InstallmentOrderActionResult(false, "分期服务尚未接入。"));
    public Task<InstallmentOrderActionResult> CancelWithRefundAsync(Guid orderId, PosSessionState session, CancellationToken cancellationToken = default) => Task.FromResult(new InstallmentOrderActionResult(false, "分期服务尚未接入。"));
    public Task<InstallmentOrderActionResult> VoidCancelAsync(Guid orderId, PosSessionState session, string? reason = null, CancellationToken cancellationToken = default) => Task.FromResult(new InstallmentOrderActionResult(false, "分期服务尚未接入。"));
    public Task<InstallmentOrderActionResult> ConfirmPickupAsync(Guid orderId, PosSessionState session, CancellationToken cancellationToken = default) => Task.FromResult(new InstallmentOrderActionResult(false, "分期服务尚未接入。"));
}
