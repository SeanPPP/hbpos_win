using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Shared.Models.POSM;
using Hbpos.Api.Data;
using Hbpos.Api.Services;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;
using SqlSugar;

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
    public async Task Append_payment_allows_same_idempotency_key_on_different_installments()
    {
        var service = CreateService();
        var first = await service.CreateAsync(CreateRequest(totalAmount: 60m, downPaymentAmount: 20m), CancellationToken.None);
        var second = await service.CreateAsync(CreateRequest(totalAmount: 70m, downPaymentAmount: 20m), CancellationToken.None);
        var idempotencyKey = "SHARED-PAYMENT-KEY";
        var firstPaymentGuid = Guid.NewGuid();
        var secondPaymentGuid = Guid.NewGuid();

        await service.AppendPaymentAsync(
            CreatePayment(first.InstallmentGuid, firstPaymentGuid, amount: 10m, idempotencyKey: idempotencyKey),
            CancellationToken.None);

        var response = await service.AppendPaymentAsync(
            CreatePayment(second.InstallmentGuid, secondPaymentGuid, amount: 15m, idempotencyKey: idempotencyKey),
            CancellationToken.None);

        Assert.False(response.AlreadyRecorded);
        Assert.Equal(second.InstallmentGuid, response.InstallmentGuid);
        Assert.Equal(secondPaymentGuid, response.PaymentGuid);
        Assert.Equal(35m, response.PaidAmount);
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
    public async Task Create_with_voucher_payment_redeems_voucher_and_consumes_reservation()
    {
        await using var fixture = await InstallmentSqliteFixture.CreateAsync();
        var timeProvider = new MutableFakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z"));
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 30m));
        var reservationService = new SqlSugarStoreVoucherReservationService(fixture.DbContext, timeProvider);
        var reservation = await reservationService.ReserveAsync("S01", "V001", 20m, 30m, CancellationToken.None);
        var service = new InstallmentService(
            new InMemoryInstallmentRepository(fixture.DbContext),
            reservationService,
            timeProvider);

        var response = await service.CreateAsync(
            CreateRequest(
                totalAmount: 50m,
                downPaymentAmount: 20m,
                method: PaymentMethodKind.Voucher,
                reference: "V001",
                reservationToken: reservation.Token),
            CancellationToken.None);

        var voucher = await fixture.GetVoucherAsync("V001");
        var storedReservation = await fixture.GetReservationEntityAsync(reservation.Token);
        Assert.Equal(20m, response.PaidAmount);
        Assert.NotNull(voucher);
        Assert.Equal(10m, voucher!.RemainingAmount);
        Assert.Equal("consumed", storedReservation?.Status);
        Assert.Null(await reservationService.GetAsync(reservation.Token, CancellationToken.None));
    }

    [Fact]
    public async Task Append_with_voucher_payment_redeems_voucher_and_consumes_reservation()
    {
        await using var fixture = await InstallmentSqliteFixture.CreateAsync();
        var timeProvider = new MutableFakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z"));
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 50m));
        var reservationService = new SqlSugarStoreVoucherReservationService(fixture.DbContext, timeProvider);
        var service = new InstallmentService(
            new InMemoryInstallmentRepository(fixture.DbContext),
            reservationService,
            timeProvider);
        var created = await service.CreateAsync(
            CreateRequest(totalAmount: 60m, downPaymentAmount: 20m),
            CancellationToken.None);
        var reservation = await reservationService.ReserveAsync("S01", "V001", 30m, 50m, CancellationToken.None);

        var response = await service.AppendPaymentAsync(
            CreatePayment(
                created.InstallmentGuid,
                Guid.NewGuid(),
                amount: 30m,
                method: PaymentMethodKind.Voucher,
                reference: "V001",
                reservationToken: reservation.Token),
            CancellationToken.None);

        var voucher = await fixture.GetVoucherAsync("V001");
        var storedReservation = await fixture.GetReservationEntityAsync(reservation.Token);
        Assert.Equal(50m, response.PaidAmount);
        Assert.Equal(10m, response.BalanceAmount);
        Assert.NotNull(voucher);
        Assert.Equal(20m, voucher!.RemainingAmount);
        Assert.Equal("consumed", storedReservation?.Status);
        Assert.Null(await reservationService.GetAsync(reservation.Token, CancellationToken.None));
    }

    [Fact]
    public async Task Append_with_voucher_payment_rejects_reused_reservation_token()
    {
        await using var fixture = await InstallmentSqliteFixture.CreateAsync();
        var timeProvider = new MutableFakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z"));
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 50m));
        var reservationService = new SqlSugarStoreVoucherReservationService(fixture.DbContext, timeProvider);
        var service = new InstallmentService(
            new InMemoryInstallmentRepository(fixture.DbContext),
            reservationService,
            timeProvider);
        var created = await service.CreateAsync(
            CreateRequest(totalAmount: 60m, downPaymentAmount: 20m),
            CancellationToken.None);
        var reservation = await reservationService.ReserveAsync("S01", "V001", 15m, 50m, CancellationToken.None);

        await service.AppendPaymentAsync(
            CreatePayment(
                created.InstallmentGuid,
                Guid.NewGuid(),
                amount: 15m,
                method: PaymentMethodKind.Voucher,
                reference: "V001",
                reservationToken: reservation.Token),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AppendPaymentAsync(
                CreatePayment(
                    created.InstallmentGuid,
                    Guid.NewGuid(),
                    amount: 15m,
                    method: PaymentMethodKind.Voucher,
                    reference: "V001",
                    reservationToken: reservation.Token),
                CancellationToken.None));
        var voucher = await fixture.GetVoucherAsync("V001");
        var storedReservation = await fixture.GetReservationEntityAsync(reservation.Token);
        Assert.Contains("Voucher reservation token", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(voucher);
        Assert.Equal(35m, voucher!.RemainingAmount);
        Assert.Equal("consumed", storedReservation?.Status);
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
        string? idempotencyKey = null,
        string? reference = null,
        string? reservationToken = null)
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
            reference,
            reservationToken,
            IdempotencyKey: idempotencyKey);
    }

    private static StoreVoucher CreateVoucher(decimal remainingAmount)
    {
        return new StoreVoucher
        {
            ID = 1,
            StoreCode = "S01",
            VoucherCode = "V001",
            VoucherType = 3,
            Amount = remainingAmount,
            RemainingAmount = remainingAmount,
            Status = "1",
            ExpiredDate = DateTime.UtcNow.AddDays(3),
            DiscountRate = 0m,
            IsDelete = false
        };
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

    private sealed class InMemoryInstallmentRepository(HbposSqlSugarContext? dbContext = null) : IInstallmentRepository
    {
        private readonly Dictionary<Guid, InstallmentDetailsDto> details = [];
        private readonly Dictionary<Guid, Guid> paymentIndex = [];

        public async Task CreateAsync(InstallmentDetailsDto details, CancellationToken cancellationToken)
        {
            if (dbContext is not null)
            {
                await RedeemVoucherPaymentsAsync(details.StoreCode, details.CashierId, details.Payments, cancellationToken);
            }

            this.details[details.InstallmentGuid] = details;
            foreach (var payment in details.Payments)
            {
                paymentIndex[payment.PaymentGuid] = details.InstallmentGuid;
            }
        }

        public async Task<InstallmentDetailsDto> AppendPaymentAsync(
            Guid installmentGuid,
            InstallmentPaymentDto payment,
            CancellationToken cancellationToken)
        {
            var current = details[installmentGuid];
            if (!paymentIndex.ContainsKey(payment.PaymentGuid))
            {
                if (dbContext is not null && payment.Method == PaymentMethodKind.Voucher)
                {
                    await RedeemVoucherPaymentsAsync(current.StoreCode, payment.CashierId, [payment], cancellationToken);
                }

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

            return current;
        }

        private async Task RedeemVoucherPaymentsAsync(
            string storeCode,
            string cashierId,
            IReadOnlyList<InstallmentPaymentDto> payments,
            CancellationToken cancellationToken)
        {
            var voucherPayments = payments
                .Where(payment => payment.Method == PaymentMethodKind.Voucher)
                .ToList();
            if (voucherPayments.Count == 0 || dbContext is null)
            {
                return;
            }

            await dbContext.PosmDb.Ado.BeginTranAsync();
            try
            {
                foreach (var payment in voucherPayments)
                {
                    await SqlSugarStoreVoucherReservationService.ClaimInsideTransactionAsync(
                        dbContext.PosmDb,
                        payment.ReservationToken ?? string.Empty,
                        storeCode,
                        payment.Reference ?? string.Empty,
                        payment.Amount,
                        payment.PaymentGuid.ToString("D"),
                        payment.RecordedAt,
                        cancellationToken);
                    await SqlSugarStoreVoucherRepository.RedeemInsideTransactionAsync(
                        dbContext.PosmDb,
                        storeCode,
                        payment.Reference ?? string.Empty,
                        payment.Amount,
                        cashierId,
                        cancellationToken);
                }

                await dbContext.PosmDb.Ado.CommitTranAsync();
            }
            catch
            {
                await dbContext.PosmDb.Ado.RollbackTranAsync();
                throw;
            }
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

        public Task<InstallmentPaymentLookup?> FindPaymentByIdempotencyKeyAsync(
            Guid installmentGuid,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            var match = details.Values
                .Where(order => order.InstallmentGuid == installmentGuid)
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

        public Task<StoreVoucherReservation> ClaimAsync(
            string token,
            string storeCode,
            string voucherCode,
            decimal amount,
            string? consumedByReference,
            CancellationToken cancellationToken)
        {
            if (!reservations.TryGetValue(token, out var reservation) ||
                !string.Equals(reservation.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(reservation.VoucherCode, voucherCode, StringComparison.OrdinalIgnoreCase) ||
                reservation.LockedAmount < amount)
            {
                throw new InvalidOperationException("Voucher reservation token is invalid, expired, or already claimed.");
            }

            reservations.Remove(token);
            return Task.FromResult(reservation);
        }

        public Task ConsumeAsync(string token, CancellationToken cancellationToken)
        {
            reservations.Remove(token);
            return Task.CompletedTask;
        }
    }

    private sealed class MutableFakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class InstallmentSqliteFixture : IAsyncDisposable
    {
        private readonly string databasePath = Path.Combine(
            Path.GetTempPath(),
            $"hbpos-installment-voucher-tests-{Guid.NewGuid():N}.db");
        private readonly SqlSugarClient client;

        private InstallmentSqliteFixture()
        {
            client = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = $"Data Source={databasePath}",
                DbType = DbType.Sqlite,
                InitKeyType = InitKeyType.Attribute,
                IsAutoCloseConnection = true
            });
            client.CodeFirst.InitTables<StoreVoucher, StoreVoucherReservationEntity>();
            DbContext = CreateDbContext(client);
        }

        public HbposSqlSugarContext DbContext { get; }

        public static Task<InstallmentSqliteFixture> CreateAsync()
        {
            return Task.FromResult(new InstallmentSqliteFixture());
        }

        public Task SeedVoucherAsync(StoreVoucher voucher)
        {
            return client.Insertable(voucher).ExecuteCommandAsync();
        }

        public async Task<StoreVoucher?> GetVoucherAsync(string voucherCode)
        {
            return await client.Queryable<StoreVoucher>()
                .Where(x => x.VoucherCode == voucherCode)
                .FirstAsync();
        }

        public async Task<StoreVoucherReservationEntity?> GetReservationEntityAsync(string token)
        {
            return await client.Queryable<StoreVoucherReservationEntity>()
                .Where(x => x.Token == token)
                .FirstAsync();
        }

        public ValueTask DisposeAsync()
        {
            client.Dispose();
            if (File.Exists(databasePath))
            {
                try
                {
                    File.Delete(databasePath);
                }
                catch (IOException)
                {
                    // SQLite 可能短暂占用测试数据库文件，不影响断言结果。
                }
            }

            return ValueTask.CompletedTask;
        }

        private static HbposSqlSugarContext CreateDbContext(ISqlSugarClient posmDb)
        {
            var context = (HbposSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HbposSqlSugarContext));
            SetAutoProperty(context, nameof(HbposSqlSugarContext.MainDb), posmDb);
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
    }
}
