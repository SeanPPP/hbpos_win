using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Vouchers;

namespace Hbpos.Client.Wpf.Services;

public interface IVoucherApiClient : IVoucherTenderClient
{
    Task<StoreVoucherQueryResponse> QueryAsync(
        string storeCode,
        string voucherCode,
        CancellationToken cancellationToken = default);

    Task<StoreVoucherLockResponse> LockAsync(
        StoreVoucherLockRequest request,
        CancellationToken cancellationToken = default);

    Task<StoreVoucherIssueRefundResponse> IssueRefundVoucherAsync(
        StoreVoucherIssueRefundRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class VoucherApiClient(HttpClient httpClient, ILocalizationService? localization = null) : IVoucherApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<StoreVoucherQueryResponse> QueryAsync(
        string storeCode,
        string voucherCode,
        CancellationToken cancellationToken = default)
    {
        var path = $"api/v1/vouchers/{Uri.EscapeDataString(voucherCode)}?storeCode={Uri.EscapeDataString(storeCode)}";
        return GetAsync<StoreVoucherQueryResponse>(path, cancellationToken);
    }

    public Task<StoreVoucherLockResponse> LockAsync(
        StoreVoucherLockRequest request,
        CancellationToken cancellationToken = default)
    {
        return PostAsync<StoreVoucherLockRequest, StoreVoucherLockResponse>("api/v1/vouchers/lock", request, cancellationToken);
    }

    public Task<StoreVoucherIssueRefundResponse> IssueRefundVoucherAsync(
        StoreVoucherIssueRefundRequest request,
        CancellationToken cancellationToken = default)
    {
        return PostAsync<StoreVoucherIssueRefundRequest, StoreVoucherIssueRefundResponse>(
            "api/v1/vouchers/refund",
            request,
            cancellationToken);
    }

    public async Task<PaymentAuthorizationResult> RedeemAsync(
        decimal amount,
        PosSessionState session,
        string? voucherCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(voucherCode))
        {
            return new PaymentAuthorizationResult(false, null, T("payment.voucher.codeRequired", "Voucher code is required."));
        }

        var query = await QueryAsync(session.StoreCode, voucherCode.Trim(), cancellationToken);
        if (!query.Found || query.Voucher is null)
        {
            return new PaymentAuthorizationResult(false, null, query.Message ?? T("payment.voucher.status.unavailable", "Voucher is unavailable."));
        }

        var lockAmount = Math.Min(amount, query.Voucher.RemainingAmount);
        var locked = await LockAsync(
            new StoreVoucherLockRequest(session.StoreCode, query.Voucher.VoucherCode, lockAmount),
            cancellationToken);
        return new PaymentAuthorizationResult(
            true,
            $"VOUCHER:{locked.VoucherCode}:{locked.ReservationToken}",
            locked.VoucherCode,
            locked.LockedAmount);
    }

    public async Task<PaymentAuthorizationResult> IssueRefundAsync(
        decimal amount,
        PosSessionState session,
        string orderReference,
        string idempotencyKey,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (amount <= 0m)
        {
            return new PaymentAuthorizationResult(false, null, T("payment.voucher.refundAmountMustBePositive", "Voucher refund amount must be greater than zero."));
        }

        if (string.IsNullOrWhiteSpace(orderReference))
        {
            return new PaymentAuthorizationResult(false, null, T("payment.voucher.refundOrderReferenceRequired", "Voucher refund order reference is required."));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return new PaymentAuthorizationResult(false, null, T("payment.voucher.refundIdempotencyKeyRequired", "Voucher refund idempotency key is required."));
        }

        var issued = await IssueRefundVoucherAsync(
            new StoreVoucherIssueRefundRequest(
                session.StoreCode,
                amount,
                session.CashierId,
                IdempotencyKey: idempotencyKey.Trim(),
                OrderReference: orderReference.Trim(),
                Reason: reason),
            cancellationToken);
        return new PaymentAuthorizationResult(
            true,
            $"VOUCHER_REFUND:{issued.VoucherCode}",
            issued.VoucherCode,
            issued.Amount);
    }

    private async Task<TResponse> GetAsync<TResponse>(
        string path,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(path, cancellationToken);
        return await ReadAsync<TResponse>(response, cancellationToken);
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string path,
        TRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(path, request, JsonOptions, cancellationToken);
        return await ReadAsync<TResponse>(response, cancellationToken);
    }

    private static async Task<TResponse> ReadAsync<TResponse>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        ApiResult<TResponse>? result = string.IsNullOrWhiteSpace(content)
            ? null
            : JsonSerializer.Deserialize<ApiResult<TResponse>>(content, JsonOptions);

        if (!response.IsSuccessStatusCode || result is null || !result.Success || result.Data is null)
        {
            throw new CatalogApiException(
                result?.Message ?? $"Voucher API request failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode,
                result?.ErrorCode);
        }

        return result.Data;
    }

    private string T(string key, string fallback)
    {
        return localization?.T(key) ?? fallback;
    }
}
