using Hbpos.Api.Services;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;

namespace Hbpos.Api.Tests;

public sealed class InstallmentServiceTests
{
    [Fact]
    public async Task Create_rejects_down_payment_below_minimum()
    {
        var service = CreateService();
        var request = CreateRequest(totalAmount: 100m, downPaymentAmount: 19.99m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(request, CancellationToken.None));

        Assert.Contains("at least $20", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_allows_small_order_when_paid_off()
    {
        var service = CreateService();
        var request = CreateRequest(totalAmount: 12m, downPaymentAmount: 12m);

        var response = await service.CreateAsync(request, CancellationToken.None);

        Assert.Equal(InstallmentStatus.PaidOff, response.Status);
        Assert.Equal(12m, response.PaidAmount);
        Assert.Equal(0m, response.BalanceAmount);
    }

    [Fact]
    public async Task Create_returns_existing_installment_idempotently()
    {
        var service = CreateService();
        var request = CreateRequest();

        await service.CreateAsync(request, CancellationToken.None);
        var duplicate = await service.CreateAsync(request, CancellationToken.None);

        Assert.True(duplicate.AlreadyExists);
        Assert.Equal("AlreadyExists", duplicate.Message);
    }

    [Fact]
    public async Task Append_payment_records_once_and_marks_paid_off()
    {
        var service = CreateService();
        var request = CreateRequest(totalAmount: 60m, downPaymentAmount: 20m);
        var created = await service.CreateAsync(request, CancellationToken.None);
        var paymentGuid = Guid.NewGuid();

        var response = await service.AppendPaymentAsync(
            CreatePayment(created.InstallmentGuid, paymentGuid, amount: 40m),
            CancellationToken.None);
        var duplicate = await service.AppendPaymentAsync(
            CreatePayment(created.InstallmentGuid, paymentGuid, amount: 40m),
            CancellationToken.None);

        Assert.Equal(InstallmentStatus.PaidOff, response.Status);
        Assert.Equal(60m, response.PaidAmount);
        Assert.Equal(0m, response.BalanceAmount);
        Assert.True(duplicate.AlreadyRecorded);
        Assert.Equal(60m, duplicate.PaidAmount);
    }

    [Fact]
    public async Task Append_payment_is_idempotent_by_idempotency_key()
    {
        var service = CreateService();
        var created = await service.CreateAsync(CreateRequest(totalAmount: 60m, downPaymentAmount: 20m), CancellationToken.None);
        var idempotencyKey = "INSTALLMENT-1:PAY-2";
        var firstPaymentGuid = Guid.NewGuid();

        await service.AppendPaymentAsync(
            CreatePayment(created.InstallmentGuid, firstPaymentGuid, amount: 10m, idempotencyKey: idempotencyKey),
            CancellationToken.None);
        var duplicate = await service.AppendPaymentAsync(
            CreatePayment(created.InstallmentGuid, Guid.NewGuid(), amount: 10m, idempotencyKey: idempotencyKey),
            CancellationToken.None);

        Assert.True(duplicate.AlreadyRecorded);
        Assert.Equal(firstPaymentGuid, duplicate.PaymentGuid);
        Assert.Equal(30m, duplicate.PaidAmount);
    }

    [Fact]
    public async Task Append_payment_rejects_device_scope_mismatch()
    {
        var service = CreateService();
        var created = await service.CreateAsync(CreateRequest(totalAmount: 60m, downPaymentAmount: 20m), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AppendPaymentAsync(
                CreatePayment(created.InstallmentGuid, Guid.NewGuid(), amount: 10m) with { StoreCode = "S02" },
                CancellationToken.None));

        Assert.Contains("this store", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Confirm_pickup_rejects_device_scope_mismatch()
    {
        var service = CreateService();
        var created = await service.CreateAsync(CreateRequest(totalAmount: 20m, downPaymentAmount: 20m), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConfirmPickupAsync(CreatePickup(created.InstallmentGuid) with { DeviceCode = "POS02" }, CancellationToken.None));

        Assert.Contains("this device", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_rejects_line_total_mismatch()
    {
        var service = CreateService();
        var request = CreateRequest(totalAmount: 100m, downPaymentAmount: 20m) with
        {
            Lines =
            [
                new InstallmentLineDto(
                    Guid.NewGuid(),
                    "SKU-001",
                    null,
                    "Tea",
                    "9300001",
                    1m,
                    99m,
                    0m,
                    99m)
            ]
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(request, CancellationToken.None));

        Assert.Contains("Line total", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Confirm_pickup_requires_paid_off_installment()
    {
        var service = CreateService();
        var created = await service.CreateAsync(CreateRequest(totalAmount: 80m, downPaymentAmount: 20m), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConfirmPickupAsync(CreatePickup(created.InstallmentGuid), CancellationToken.None));

        Assert.Contains("paid off", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Confirm_pickup_is_idempotent_after_success()
    {
        var service = CreateService();
        var created = await service.CreateAsync(CreateRequest(totalAmount: 20m, downPaymentAmount: 20m), CancellationToken.None);
        var first = await service.ConfirmPickupAsync(CreatePickup(created.InstallmentGuid), CancellationToken.None);

        var second = await service.ConfirmPickupAsync(CreatePickup(created.InstallmentGuid), CancellationToken.None);

        Assert.Equal(InstallmentStatus.PickedUp, first.Status);
        Assert.True(second.AlreadyConfirmed);
    }

    [Fact]
    public async Task Query_history_matches_trimmed_keyword_against_summary_fields()
    {
        var service = CreateService();
        var aliceRequest = CreateRequest() with
        {
            InstallmentGuid = Guid.NewGuid(),
            CustomerName = "Alice Zhang",
            CustomerPhone = "0400111222"
        };
        var bobRequest = CreateRequest() with
        {
            InstallmentGuid = Guid.NewGuid(),
            CustomerName = "Bob Li",
            CustomerPhone = "0499888777"
        };

        var alice = await service.CreateAsync(aliceRequest, CancellationToken.None);
        await service.CreateAsync(bobRequest, CancellationToken.None);

        var byName = await service.QueryAsync(
            new InstallmentHistoryQueryRequest(" S01 ", Keyword: "  Alice  "),
            CancellationToken.None);
        var byNumber = await service.QueryAsync(
            new InstallmentHistoryQueryRequest("S01", Keyword: $"  {alice.InstallmentNumber}  "),
            CancellationToken.None);

        Assert.Equal(alice.InstallmentGuid, Assert.Single(byName.Orders).InstallmentGuid);
        Assert.Equal(alice.InstallmentGuid, Assert.Single(byNumber.Orders).InstallmentGuid);
    }

    [Fact]
    public async Task Voucher_payment_requires_valid_reservation()
    {
        var reservation = new FakeReservationService();
        var service = CreateService(reservation);
        var request = CreateRequest(
            totalAmount: 50m,
            downPaymentAmount: 20m,
            method: PaymentMethodKind.Voucher,
            reference: "VOUCHER-1",
            reservationToken: "missing-token");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task Cancel_with_refund_marks_active_installment_cancelled()
    {
        var service = CreateService();
        var created = await service.CreateAsync(CreateRequest(totalAmount: 80m, downPaymentAmount: 20m), CancellationToken.None);

        var response = await service.CancelAsync(
            CreateCancel(created.InstallmentGuid, [new InstallmentRefundPaymentCommandDto(Guid.NewGuid(), PaymentMethodKind.Cash, 20m, "CASH-REFUND")]),
            CancellationToken.None);
        var duplicate = await service.CancelAsync(
            CreateCancel(created.InstallmentGuid, [new InstallmentRefundPaymentCommandDto(Guid.NewGuid(), PaymentMethodKind.Cash, 20m, "CASH-REFUND-2")]),
            CancellationToken.None);

        Assert.Equal(InstallmentStatus.Cancelled, response.Status);
        Assert.NotNull(response.Details.CancellationInfo);
        Assert.Equal(InstallmentCancellationKind.RefundCancel, response.Details.CancellationInfo!.Kind);
        Assert.Equal(0m, response.Details.PaidAmount);
        Assert.True(duplicate.AlreadyCancelled);
    }

    [Fact]
    public async Task Void_marks_active_installment_cancelled_without_refund_payment()
    {
        var service = CreateService();
        var created = await service.CreateAsync(CreateRequest(totalAmount: 80m, downPaymentAmount: 20m), CancellationToken.None);

        var response = await service.VoidAsync(CreateVoid(created.InstallmentGuid), CancellationToken.None);
        var duplicate = await service.VoidAsync(CreateVoid(created.InstallmentGuid), CancellationToken.None);

        Assert.Equal(InstallmentStatus.Cancelled, response.Status);
        Assert.NotNull(response.Details.CancellationInfo);
        Assert.Equal(InstallmentCancellationKind.VoidCancel, response.Details.CancellationInfo!.Kind);
        Assert.Equal(20m, response.Details.PaidAmount);
        Assert.Single(response.Details.Payments);
        Assert.True(duplicate.AlreadyVoided);
    }

    [Fact]
    public async Task Cancel_and_void_reject_paid_off_installment()
    {
        var service = CreateService();
        var created = await service.CreateAsync(CreateRequest(totalAmount: 20m, downPaymentAmount: 20m), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CancelAsync(
                CreateCancel(created.InstallmentGuid, [new InstallmentRefundPaymentCommandDto(Guid.NewGuid(), PaymentMethodKind.Cash, 20m, "CASH-REFUND")]),
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.VoidAsync(CreateVoid(created.InstallmentGuid), CancellationToken.None));
    }

    [Fact]
    public async Task Cancel_then_void_returns_conflict()
    {
        var service = CreateService();
        var created = await service.CreateAsync(CreateRequest(totalAmount: 80m, downPaymentAmount: 20m), CancellationToken.None);
        await service.CancelAsync(
            CreateCancel(created.InstallmentGuid, [new InstallmentRefundPaymentCommandDto(Guid.NewGuid(), PaymentMethodKind.Cash, 20m, "CASH-REFUND")]),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.VoidAsync(CreateVoid(created.InstallmentGuid), CancellationToken.None));

        Assert.Contains("conflicts", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static InstallmentService CreateService(FakeReservationService? reservation = null)
    {
        return new InstallmentService(new InMemoryInstallmentRepository(), reservation ?? new FakeReservationService());
    }

    private static InstallmentCreateRequest CreateRequest(
        decimal totalAmount = 100m,
        decimal downPaymentAmount = 20m,
        PaymentMethodKind method = PaymentMethodKind.Cash,
        string? reference = null,
        string? reservationToken = null)
    {
        return new InstallmentCreateRequest(
            Guid.NewGuid(),
            "S01",
            "POS01",
            "C01",
            "Cashier",
            DateTimeOffset.Parse("2026-05-21T10:00:00Z"),
            totalAmount,
            downPaymentAmount,
            [
                new InstallmentLineDto(
                    Guid.NewGuid(),
                    "SKU-001",
                    null,
                    "Tea",
                    "9300001",
                    1m,
                    totalAmount,
                    0m,
                    totalAmount)
            ],
            new InstallmentPaymentCommandDto(Guid.NewGuid(), method, downPaymentAmount, reference, reservationToken),
            "Alice",
            "0400000000");
    }

    private static InstallmentAppendPaymentRequest CreatePayment(
        Guid installmentGuid,
        Guid paymentGuid,
        decimal amount,
        PaymentMethodKind method = PaymentMethodKind.Cash,
        string? idempotencyKey = null)
    {
        return new InstallmentAppendPaymentRequest(
            installmentGuid,
            paymentGuid,
            "S01",
            "POS01",
            "C01",
            "Cashier",
            amount,
            method,
            null,
            IdempotencyKey: idempotencyKey);
    }

    private static InstallmentConfirmPickupRequest CreatePickup(Guid installmentGuid)
    {
        return new InstallmentConfirmPickupRequest(
            installmentGuid,
            "S01",
            "POS01",
            "C01",
            "Cashier",
            DateTimeOffset.Parse("2026-05-22T10:00:00Z"));
    }

    private static InstallmentCancelRequest CreateCancel(
        Guid installmentGuid,
        IReadOnlyList<InstallmentRefundPaymentCommandDto> refunds)
    {
        return new InstallmentCancelRequest(
            installmentGuid,
            "S01",
            "POS01",
            "C01",
            "Cashier",
            DateTimeOffset.Parse("2026-05-22T11:00:00Z"),
            refunds,
            "Customer cancelled");
    }

    private static InstallmentVoidRequest CreateVoid(Guid installmentGuid)
    {
        return new InstallmentVoidRequest(
            installmentGuid,
            "S01",
            "POS01",
            "C01",
            "Cashier",
            DateTimeOffset.Parse("2026-05-22T11:30:00Z"),
            "Void without refund");
    }

    private sealed class InMemoryInstallmentRepository : IInstallmentRepository
    {
        private readonly Dictionary<Guid, InstallmentDetailsDto> details = [];
        private readonly Dictionary<Guid, Guid> paymentIndex = [];

        public Task CreateAsync(InstallmentDetailsDto details, CancellationToken cancellationToken)
        {
            this.details[details.InstallmentGuid] = details;
            foreach (var payment in details.Payments)
            {
                paymentIndex[payment.PaymentGuid] = details.InstallmentGuid;
            }

            return Task.CompletedTask;
        }

        public Task<InstallmentDetailsDto> AppendPaymentAsync(
            Guid installmentGuid,
            InstallmentPaymentDto payment,
            CancellationToken cancellationToken)
        {
            var current = details[installmentGuid];
            if (!paymentIndex.ContainsKey(payment.PaymentGuid))
            {
                paymentIndex[payment.PaymentGuid] = installmentGuid;
                var paidAmount = current.PaidAmount + payment.Amount;
                var balanceAmount = Math.Max(0m, current.TotalAmount - paidAmount);
                current = current with
                {
                    PaidAmount = paidAmount,
                    BalanceAmount = balanceAmount,
                    Status = balanceAmount == 0m ? InstallmentStatus.PaidOff : InstallmentStatus.Active,
                    Payments = current.Payments.Concat([payment]).ToList()
                };
                details[installmentGuid] = current;
            }

            return Task.FromResult(current);
        }

        public Task<InstallmentDetailsDto> ConfirmPickupAsync(
            Guid installmentGuid,
            DateTimeOffset pickedUpAt,
            string pickedUpBy,
            string? note,
            CancellationToken cancellationToken)
        {
            var current = details[installmentGuid] with
            {
                Status = InstallmentStatus.PickedUp,
                PickupInfo = new InstallmentPickupInfoDto(pickedUpAt, pickedUpBy, note)
            };
            details[installmentGuid] = current;
            return Task.FromResult(current);
        }

        public Task<InstallmentDetailsDto> CancelWithRefundAsync(
            Guid installmentGuid,
            IReadOnlyList<InstallmentPaymentDto> refunds,
            InstallmentCancellationInfoDto cancellationInfo,
            CancellationToken cancellationToken)
        {
            var current = details[installmentGuid];
            foreach (var refund in refunds)
            {
                paymentIndex[refund.PaymentGuid] = installmentGuid;
            }

            var payments = current.Payments.Concat(refunds).ToList();
            var paidAmount = payments.Where(payment => payment.Status == InstallmentPaymentStatus.Recorded).Sum(payment => payment.Amount);
            current = current with
            {
                Status = InstallmentStatus.Cancelled,
                PaidAmount = paidAmount,
                BalanceAmount = 0m,
                Payments = payments,
                CancellationInfo = cancellationInfo
            };
            details[installmentGuid] = current;
            return Task.FromResult(current);
        }

        public Task<InstallmentDetailsDto> VoidAsync(
            Guid installmentGuid,
            InstallmentCancellationInfoDto cancellationInfo,
            CancellationToken cancellationToken)
        {
            var current = details[installmentGuid] with
            {
                Status = InstallmentStatus.Cancelled,
                CancellationInfo = cancellationInfo
            };
            details[installmentGuid] = current;
            return Task.FromResult(current);
        }

        public Task<InstallmentPaymentLookup?> FindPaymentAsync(Guid paymentGuid, CancellationToken cancellationToken)
        {
            if (!paymentIndex.TryGetValue(paymentGuid, out var installmentGuid))
            {
                return Task.FromResult<InstallmentPaymentLookup?>(null);
            }

            var payment = details[installmentGuid].Payments.Single(x => x.PaymentGuid == paymentGuid);
            return Task.FromResult<InstallmentPaymentLookup?>(new InstallmentPaymentLookup(installmentGuid, payment));
        }

        public Task<InstallmentPaymentLookup?> FindPaymentByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken)
        {
            var match = details.Values
                .SelectMany(order => order.Payments.Select(payment => new { order.InstallmentGuid, Payment = payment }))
                .FirstOrDefault(x => string.Equals(x.Payment.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));
            return Task.FromResult(match is null
                ? null
                : new InstallmentPaymentLookup(match.InstallmentGuid, match.Payment));
        }

        public Task<InstallmentHistoryQueryResponse> QueryAsync(
            InstallmentHistoryQueryRequest request,
            CancellationToken cancellationToken)
        {
            var query = details.Values
                .Where(order => string.Equals(order.StoreCode, request.StoreCode, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(request.DeviceCode))
            {
                query = query.Where(order => string.Equals(order.DeviceCode, request.DeviceCode, StringComparison.OrdinalIgnoreCase));
            }

            if (request.CreatedFrom is not null)
            {
                query = query.Where(order => order.CreatedAt >= request.CreatedFrom.Value);
            }

            if (request.CreatedTo is not null)
            {
                query = query.Where(order => order.CreatedAt <= request.CreatedTo.Value);
            }

            if (request.Status is not null)
            {
                query = query.Where(order => order.Status == request.Status.Value);
            }

            if (!string.IsNullOrWhiteSpace(request.Keyword))
            {
                var keyword = request.Keyword.Trim();
                query = query.Where(order =>
                    order.InstallmentGuid.ToString("D").Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    order.InstallmentNumber.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    order.CustomerName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    order.CustomerPhone.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            }

            var orders = query
                .OrderByDescending(order => order.CreatedAt)
                .Take(Math.Clamp(request.Take, 1, 200))
                .Select(order => new InstallmentSummaryDto(
                    order.InstallmentGuid,
                    order.InstallmentNumber,
                    order.StoreCode,
                    order.DeviceCode,
                    order.CashierName,
                    order.CustomerName,
                    order.CustomerPhone,
                    order.CreatedAt,
                    order.TotalAmount,
                    order.DownPaymentAmount,
                    order.PaidAmount,
                    order.BalanceAmount,
                    order.Status,
                    order.CreatedAt))
                .ToList();
            return Task.FromResult(new InstallmentHistoryQueryResponse(orders));
        }

        public Task<InstallmentDetailsDto?> GetDetailsAsync(Guid installmentGuid, CancellationToken cancellationToken)
        {
            details.TryGetValue(installmentGuid, out var value);
            return Task.FromResult(value);
        }
    }

    private sealed class FakeReservationService : IStoreVoucherReservationService
    {
        private readonly Dictionary<string, StoreVoucherReservation> reservations = [];

        public void Add(StoreVoucherReservation reservation)
        {
            reservations[reservation.Token] = reservation;
        }

        public Task<StoreVoucherReservation?> GetAsync(string token, CancellationToken cancellationToken)
        {
            reservations.TryGetValue(token, out var reservation);
            return Task.FromResult(reservation);
        }

        public Task<StoreVoucherReservation> ReserveAsync(
            string storeCode,
            string voucherCode,
            decimal requestedAmount,
            decimal currentRemainingAmount,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task ConsumeAsync(string token, CancellationToken cancellationToken)
        {
            reservations.Remove(token);
            return Task.CompletedTask;
        }
    }
}
