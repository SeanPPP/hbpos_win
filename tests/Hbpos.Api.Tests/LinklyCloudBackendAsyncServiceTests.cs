using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hbpos.Api.Services;
using Hbpos.Contracts.Linkly;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Tests;

    public sealed class LinklyCloudBackendAsyncServiceTests
    {
    [Fact]
    public void Sql_repository_create_session_uses_serializable_lock_hints()
    {
        Assert.Contains("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE", SqlSugarLinklyCloudBackendAsyncRepository.TryCreateSessionSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WITH (UPDLOCK, HOLDLOCK)", SqlSugarLinklyCloudBackendAsyncRepository.TryCreateSessionSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartTransactionAsync_limits_txn_ref_and_locks_active_terminal_session()
    {
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var tokenProvider = new CapturingLinklyCloudBackendTokenProvider();
        var service = CreateService(transport, tokenProvider);
        var request = CreateTransactionRequest();

        var response = await service.StartTransactionAsync(
            "S01",
            "POS-01",
            request,
            CancellationToken.None);

        Assert.Equal("Pending", response.Status);
        Assert.Equal("Sandbox", response.Environment);
        Assert.Equal("S01", response.StoreCode);
        Assert.Equal("POS-01", response.DeviceCode);
        Assert.NotNull(response.TxnRef);
        Assert.True(response.TxnRef!.Length <= 16);
        Assert.Equal(response.TxnRef, transport.LastTransaction?.TxnRef);
        Assert.Equal("https://server-rest.example/POS-01/", transport.LastTransaction?.RestBaseUrl);
        Assert.Equal("server-token-POS-01", transport.LastTransaction?.AccessToken);
        Assert.Equal([("Sandbox", "S01", "POS-01")], tokenProvider.Calls);
        Assert.NotNull(transport.LastNotification);
        Assert.Equal("Bearer sandbox-notify", transport.LastNotification!.AuthorizationHeader);
        Assert.Equal(
            $"https://public.example/callback/api/v1/linkly/cloud-notifications/Sandbox/{response.SessionId}/{{{{type}}}}",
            transport.LastNotification.Uri);
        Assert.Equal(202, response.LastHttpStatus);

        var activeException = await Assert.ThrowsAsync<LinklyCloudBackendActiveTransactionException>(() =>
            service.StartTransactionAsync(
                "S01",
                "POS-01",
                request,
                CancellationToken.None));
        Assert.Equal(response.SessionId, activeException.ActiveSessionId);

        var secondTerminal = await service.StartTransactionAsync(
            "S01",
            "POS-02",
            request,
            CancellationToken.None);
        Assert.True(secondTerminal.TxnRef?.Length <= 16);
        Assert.Equal("https://server-rest.example/POS-02/", transport.LastTransaction?.RestBaseUrl);
        Assert.Equal("server-token-POS-02", transport.LastTransaction?.AccessToken);
        Assert.Equal(
            [("Sandbox", "S01", "POS-01"), ("Sandbox", "S01", "POS-02")],
            tokenProvider.Calls);
    }

    [Fact]
    public async Task StartTransactionAsync_retries_when_txn_ref_collides_without_active_session()
    {
        var repository = new TxnRefCollisionOnceRepository();
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var service = CreateService(transport, new CapturingLinklyCloudBackendTokenProvider(), repository: repository);

        var response = await service.StartTransactionAsync(
            "S01",
            "POS-01",
            CreateTransactionRequest(),
            CancellationToken.None);

        Assert.Equal("Pending", response.Status);
        Assert.Equal(2, repository.TryCreateCallCount);
        Assert.NotNull(transport.LastTransaction);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://public.example/callback/")]
    [InlineData("https://localhost/callback/")]
    [InlineData("https://127.0.0.1/callback/")]
    [InlineData("https://10.1.2.3/callback/")]
    [InlineData("https://172.16.2.3/callback/")]
    [InlineData("https://192.168.2.3/callback/")]
    [InlineData("https://169.254.2.3/callback/")]
    [InlineData("https://pos.local/callback/")]
    [InlineData("https://terminal.internal/callback/")]
    [InlineData("https://store.lan/callback/")]
    [InlineData("https://192.0.0.8/callback/")]
    [InlineData("https://192.0.2.10/callback/")]
    [InlineData("https://198.18.0.1/callback/")]
    [InlineData("https://198.51.100.10/callback/")]
    [InlineData("https://203.0.113.10/callback/")]
    [InlineData("https://[::1]/callback/")]
    [InlineData("https://[fc00::1]/callback/")]
    [InlineData("https://[fe80::1]/callback/")]
    public async Task StartTransactionAsync_requires_configured_https_public_notification_url(
        string? publicNotificationBaseUrl)
    {
        var tokenProvider = new CapturingLinklyCloudBackendTokenProvider();
        var service = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted),
            tokenProvider,
            publicNotificationBaseUrl);

        var exception = await Assert.ThrowsAsync<LinklyCloudBackendValidationException>(() =>
            service.StartTransactionAsync(
                "S01",
                "POS-01",
                CreateTransactionRequest(),
                CancellationToken.None));

        Assert.Equal("Linkly Cloud public notification base URL must be configured as an HTTPS URL.", exception.Message);
        Assert.Empty(tokenProvider.Calls);
    }

    [Fact]
    public void Sql_repository_upsert_does_not_overwrite_completed_with_non_completed()
    {
        Assert.Contains("target.[Status] = 'Completed'", SqlSugarLinklyCloudBackendAsyncRepository.UpsertSessionSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@Status <> 'Completed'", SqlSugarLinklyCloudBackendAsyncRepository.UpsertSessionSql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, "Pending", "Retry")]
    [InlineData(HttpStatusCode.InternalServerError, "Pending", "Retry")]
    [InlineData((HttpStatusCode)599, "Pending", "Retry")]
    [InlineData(HttpStatusCode.Unauthorized, "TokenRefreshRequired", "RefreshToken")]
    [InlineData(HttpStatusCode.NotFound, "NotSubmitted", "RetryNewSession")]
    public async Task RecoverAsync_maps_linkly_status_codes_to_recoverable_outcomes(
        HttpStatusCode statusCode,
        string expectedStatus,
        string expectedRecoveryAction)
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var transport = new CapturingLinklyCloudBackendAsyncTransport(statusCode);
        var service = CreateService(transport);
        await service.Repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = sessionId,
            Status = "Pending",
            TxnRef = "TXN-1",
            IsActive = true,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        var response = await service.RecoverAsync(
            "S01",
            "POS-01",
            sessionId,
            new LinklyCloudBackendRecoverRequest("Sandbox"),
            CancellationToken.None);

        Assert.Equal(expectedStatus, response.Status);
        Assert.Equal(expectedRecoveryAction, response.RecoveryAction);
        Assert.Equal(sessionId, response.SessionId);
        Assert.Equal(1, response.RecoveryCount);
        Assert.Equal((int)statusCode, response.LastHttpStatus);
    }

    [Fact]
    public async Task RecoverAsync_treats_202_as_short_pending_without_recovery_increment()
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var service = CreateService(transport);
        await service.Repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = sessionId,
            Status = "Pending",
            TxnRef = "TXN-202",
            RecoveryCount = 3,
            IsActive = true,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        var response = await service.RecoverAsync(
            "S01",
            "POS-01",
            sessionId,
            new LinklyCloudBackendRecoverRequest("Sandbox"),
            CancellationToken.None);

        Assert.Equal("Pending", response.Status);
        Assert.Null(response.RecoveryAction);
        Assert.Equal(3, response.RecoveryCount);
        Assert.Equal(202, response.LastHttpStatus);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task StartTransactionAsync_counts_transport_recovery_failures(HttpStatusCode statusCode)
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(statusCode));

        var response = await service.StartTransactionAsync(
            "S01",
            "POS-01",
            CreateTransactionRequest(),
            CancellationToken.None);

        Assert.Equal("Pending", response.Status);
        Assert.Equal("Retry", response.RecoveryAction);
        Assert.Equal(1, response.RecoveryCount);
        Assert.Equal((int)statusCode, response.LastHttpStatus);
    }

    [Fact]
    public async Task StartTransactionAsync_keeps_202_as_plain_pending_without_recovery_count()
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));

        var response = await service.StartTransactionAsync(
            "S01",
            "POS-01",
            CreateTransactionRequest(),
            CancellationToken.None);

        Assert.Equal("Pending", response.Status);
        Assert.Null(response.RecoveryAction);
        Assert.Equal(0, response.RecoveryCount);
        Assert.Equal(202, response.LastHttpStatus);
    }

    [Fact]
    public async Task GetActiveSessionAsync_returns_current_claim_scoped_active_session()
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        var active = await service.StartTransactionAsync(
            "S01",
            "POS-01",
            CreateTransactionRequest(),
            CancellationToken.None);

        var response = await service.GetActiveSessionAsync("S01", "POS-01", "sandbox", CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(active.SessionId, response!.SessionId);
        Assert.Equal("S01", response.StoreCode);
        Assert.Equal("POS-01", response.DeviceCode);
        Assert.Null(await service.GetActiveSessionAsync("S01", "POS-02", "Sandbox", CancellationToken.None));
    }

    [Fact]
    public async Task SendKeyAsync_uses_server_token_scope_for_existing_session()
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var tokenProvider = new CapturingLinklyCloudBackendTokenProvider();
        var service = CreateService(transport, tokenProvider);
        await service.Repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = sessionId,
            Status = "Pending",
            TxnRef = "TXN-1",
            IsActive = true,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        await service.SendKeyAsync(
            "S01",
            "POS-01",
            sessionId,
            new LinklyCloudBackendSendKeyRequest("Sandbox", "OK", null),
            CancellationToken.None);

        Assert.Equal("https://server-rest.example/POS-01/", transport.LastSendKey?.RestBaseUrl);
        Assert.Equal("server-token-POS-01", transport.LastSendKey?.AccessToken);
        Assert.Equal([("Sandbox", "S01", "POS-01")], tokenProvider.Calls);
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("1", "1")]
    [InlineData("2", "2")]
    [InlineData("3", "3")]
    [InlineData("OK", "0")]
    [InlineData("CANCEL", "0")]
    [InlineData("YES", "1")]
    [InlineData("NO", "2")]
    [InlineData("AUTH", "3")]
    [InlineData(" auth ", "3")]
    public async Task SendKeyAsync_normalizes_supported_linkly_keys_to_official_numeric_enum(
        string key,
        string expectedKey)
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var service = CreateService(transport);
        await SeedPendingSessionAsync(service.Repository, sessionId);

        await service.SendKeyAsync(
            "S01",
            "POS-01",
            sessionId,
            new LinklyCloudBackendSendKeyRequest("Sandbox", key, null),
            CancellationToken.None);

        Assert.Equal(expectedKey, transport.LastSendKey?.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("4")]
    [InlineData("-1")]
    [InlineData("OKAY")]
    public async Task SendKeyAsync_rejects_keys_outside_official_linkly_enum(string? key)
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var transport = new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted);
        var tokenProvider = new CapturingLinklyCloudBackendTokenProvider();
        var service = CreateService(transport, tokenProvider);
        await SeedPendingSessionAsync(service.Repository, sessionId);

        var exception = await Assert.ThrowsAsync<LinklyCloudBackendValidationException>(() =>
            service.SendKeyAsync(
                "S01",
                "POS-01",
                sessionId,
                new LinklyCloudBackendSendKeyRequest("Sandbox", key!, null),
                CancellationToken.None));

        Assert.Equal("key must be one of 0, 1, 2, or 3.", exception.Message);
        Assert.Empty(tokenProvider.Calls);
        Assert.Null(transport.LastSendKey);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task SendKeyAsync_marks_408_and_5xx_as_recovery_retry(HttpStatusCode statusCode)
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var transport = new CapturingLinklyCloudBackendAsyncTransport(statusCode);
        var service = CreateService(transport);
        await service.Repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = sessionId,
            Status = "Pending",
            TxnRef = "TXN-SENDKEY",
            IsActive = true,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        var response = await service.SendKeyAsync(
            "S01",
            "POS-01",
            sessionId,
            new LinklyCloudBackendSendKeyRequest("Sandbox", "OK", null),
            CancellationToken.None);

        Assert.Equal("Pending", response.Status);
        Assert.Equal("Retry", response.RecoveryAction);
        Assert.Equal(1, response.RecoveryCount);
        Assert.Equal((int)statusCode, response.LastHttpStatus);
    }

    [Theory]
    [InlineData("start", HttpStatusCode.Accepted)]
    [InlineData("recover", HttpStatusCode.RequestTimeout)]
    [InlineData("sendkey", HttpStatusCode.InternalServerError)]
    public async Task Transport_stale_responses_do_not_rollback_completed_sessions(
        string operation,
        HttpStatusCode staleStatusCode)
    {
        var repository = new InMemoryLinklyCloudBackendAsyncRepository();
        var transport = new CompletingBeforeResponseTransport(repository, staleStatusCode);
        var service = CreateService(transport, repository: repository);
        var sessionId = Guid.NewGuid().ToString("D");

        if (!string.Equals(operation, "start", StringComparison.Ordinal))
        {
            await repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
            {
                Environment = "Sandbox",
                StoreCode = "S01",
                DeviceCode = "POS-01",
                SessionId = sessionId,
                Status = "Pending",
                TxnRef = "TXN-PENDING",
                IsActive = true,
                UpdatedAt = DateTimeOffset.UtcNow
            }, CancellationToken.None);
        }

        var response = operation switch
        {
            "start" => await service.StartTransactionAsync(
                "S01",
                "POS-01",
                CreateTransactionRequest(),
                CancellationToken.None),
            "recover" => await service.RecoverAsync(
                "S01",
                "POS-01",
                sessionId,
                new LinklyCloudBackendRecoverRequest("Sandbox"),
                CancellationToken.None),
            "sendkey" => await service.SendKeyAsync(
                "S01",
                "POS-01",
                sessionId,
                new LinklyCloudBackendSendKeyRequest("Sandbox", "OK", null),
                CancellationToken.None),
            _ => throw new InvalidOperationException(operation)
        };

        var persisted = await service.GetStatusAsync(
            "S01",
            "POS-01",
            "Sandbox",
            response.SessionId,
            CancellationToken.None);

        Assert.Equal("Completed", response.Status);
        Assert.Equal("Completed", persisted?.Status);
        Assert.Equal("00", persisted?.ResponseCode);
        Assert.Null(await repository.GetActiveSessionAsync("Sandbox", "S01", "POS-01", CancellationToken.None));
    }

    [Fact]
    public async Task StartTransactionAsync_returns_clear_error_when_terminal_secret_is_missing()
    {
        var service = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted),
            new ThrowingLinklyCloudBackendTokenProvider(
                "Linkly Cloud terminal secret is not configured for this terminal."));

        var exception = await Assert.ThrowsAsync<LinklyCloudBackendValidationException>(() =>
            service.StartTransactionAsync(
            "S01",
            "POS-01",
            CreateTransactionRequest(),
            CancellationToken.None));

        Assert.Equal("Linkly Cloud terminal secret is not configured for this terminal.", exception.Message);
    }

    [Fact]
    public async Task TokenProvider_uses_environment_store_device_terminal_secret_and_pos_id()
    {
        var credentialRepository = new CapturingCredentialRepository(new LinklyCloudCredentialRecord
        {
            StoreCode = "S01",
            Environment = "Sandbox",
            Username = "merchant-user",
            Password = "merchant-password",
            UpdatedAt = DateTime.UtcNow
        });
        var terminalRepository = new CapturingTerminalCredentialRepository(
            new LinklyCloudBackendTerminalCredentialRecord
            {
                Environment = "Sandbox",
                StoreCode = "S01",
                DeviceCode = "POS-01",
                Secret = "secret-pos-01",
                PosId = "11111111-1111-4111-8111-111111111111"
            },
            new LinklyCloudBackendTerminalCredentialRecord
            {
                Environment = "Sandbox",
                StoreCode = "S01",
                DeviceCode = "POS-02",
                Secret = "secret-pos-02",
                PosId = "22222222-2222-4222-8222-222222222222"
            });
        var handler = new CapturingTokenHttpMessageHandler();
        var provider = new HttpLinklyCloudBackendTokenProvider(
            credentialRepository,
            terminalRepository,
            new HttpClient(handler),
            Options.Create(new LinklyCloudBackendAsyncOptions
            {
                SandboxAuthBaseUrl = "https://auth.sandbox.example/v1/",
                SandboxRestBaseUrl = "https://rest.sandbox.example/v1/",
                SandboxPosVendorId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                PosName = "HBPOS",
                PosVersion = "2026.5.1"
            }));

        var pos01Token = await provider.GetTokenAsync("Sandbox", "S01", "POS-01", CancellationToken.None);
        var pos02Token = await provider.GetTokenAsync("Sandbox", "S01", "POS-02", CancellationToken.None);

        Assert.Equal("https://rest.sandbox.example/v1/", pos01Token.RestBaseUrl);
        Assert.Equal("token-for-111111111111", pos01Token.AccessToken);
        Assert.Equal("token-for-222222222222", pos02Token.AccessToken);
        Assert.Equal(
            [("S01", "Sandbox"), ("S01", "Sandbox")],
            credentialRepository.Calls);
        Assert.Equal(
            [("Sandbox", "S01", "POS-01"), ("Sandbox", "S01", "POS-02")],
            terminalRepository.Calls);
        Assert.Equal("secret-pos-01", handler.RequestBodies[0].RootElement.GetProperty("secret").GetString());
        Assert.Equal("11111111-1111-4111-8111-111111111111", handler.RequestBodies[0].RootElement.GetProperty("posId").GetString());
        Assert.Equal("secret-pos-02", handler.RequestBodies[1].RootElement.GetProperty("secret").GetString());
        Assert.Equal("22222222-2222-4222-8222-222222222222", handler.RequestBodies[1].RootElement.GetProperty("posId").GetString());
    }

    [Fact]
    public async Task TokenProvider_returns_clear_error_when_store_credential_is_missing()
    {
        var provider = new HttpLinklyCloudBackendTokenProvider(
            new CapturingCredentialRepository(null),
            new CapturingTerminalCredentialRepository(new LinklyCloudBackendTerminalCredentialRecord
            {
                Environment = "Sandbox",
                StoreCode = "S01",
                DeviceCode = "POS-01",
                Secret = "secret-pos-01",
                PosId = "11111111-1111-4111-8111-111111111111"
            }),
            new HttpClient(new CapturingTokenHttpMessageHandler()),
            Options.Create(new LinklyCloudBackendAsyncOptions
            {
                SandboxAuthBaseUrl = "https://auth.sandbox.example/v1/",
                SandboxRestBaseUrl = "https://rest.sandbox.example/v1/",
                SandboxPosVendorId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
            }));

        var exception = await Assert.ThrowsAsync<LinklyCloudBackendValidationException>(() =>
            provider.GetTokenAsync("Sandbox", "S01", "POS-01", CancellationToken.None));

        Assert.Equal("Linkly Cloud credential is not configured for this store and environment.", exception.Message);
    }

    [Fact]
    public async Task GetHealthAsync_reports_ready_when_store_terminal_bearer_and_public_callback_are_configured()
    {
        var credentialRepository = new CapturingCredentialRepository(new LinklyCloudCredentialRecord
        {
            StoreCode = "S01",
            Environment = "Sandbox",
            Username = "merchant-user",
            Password = "merchant-password",
            UpdatedAt = DateTime.UtcNow
        });
        var terminalRepository = new CapturingTerminalCredentialRepository(new LinklyCloudBackendTerminalCredentialRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            Secret = "secret-pos-01",
            PosId = "11111111-1111-4111-8111-111111111111"
        });
        var service = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted),
            credentialRepository: credentialRepository,
            terminalCredentialRepository: terminalRepository);

        var response = await service.GetHealthAsync("S01", "POS-01", "sandbox", CancellationToken.None);

        Assert.True(response.IsReady);
        Assert.Equal("Sandbox", response.Environment);
        Assert.Equal("S01", response.StoreCode);
        Assert.Equal("POS-01", response.DeviceCode);
        Assert.All(response.Checks, check => Assert.True(check.IsReady));
        Assert.Contains(response.Checks, check => check.Code == "STORE_CREDENTIAL");
        Assert.Contains(response.Checks, check => check.Code == "TERMINAL_SECRET");
        Assert.Contains(response.Checks, check => check.Code == "TERMINAL_POS_ID");
        Assert.Contains(response.Checks, check => check.Code == "NOTIFICATION_BEARER");
        Assert.Contains(response.Checks, check => check.Code == "PUBLIC_CALLBACK_URL");
        Assert.Equal([("S01", "Sandbox")], credentialRepository.Calls);
        Assert.Equal([("Sandbox", "S01", "POS-01")], terminalRepository.Calls);
    }

    [Fact]
    public async Task GetHealthAsync_reports_not_ready_without_calling_active_session_when_configuration_is_incomplete()
    {
        var service = CreateService(
            new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted),
            publicNotificationBaseUrl: "https://localhost/callback/",
            credentialRepository: new CapturingCredentialRepository(null),
            terminalCredentialRepository: new CapturingTerminalCredentialRepository());

        var response = await service.GetHealthAsync("S01", "POS-01", "Sandbox", CancellationToken.None);

        Assert.False(response.IsReady);
        Assert.Contains(response.Checks, check => check.Code == "STORE_CREDENTIAL" && !check.IsReady);
        Assert.Contains(response.Checks, check => check.Code == "TERMINAL_SECRET" && !check.IsReady);
        Assert.Contains(response.Checks, check => check.Code == "TERMINAL_POS_ID" && !check.IsReady);
        Assert.Contains(response.Checks, check => check.Code == "PUBLIC_CALLBACK_URL" && !check.IsReady);
    }

    [Fact]
    public async Task NotificationAsync_validates_environment_bearer_and_keeps_status_scoped()
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        using var unknownDisplay = JsonDocument.Parse("""{ "Response": { "DisplayText": ["IGNORED"] } }""");
        await service.ReceiveNotificationAsync(
            "Sandbox",
            "unknown-session",
            "display",
            "Bearer sandbox-notify",
            unknownDisplay.RootElement,
            CancellationToken.None);
        Assert.Null(await service.GetStatusAsync("S01", "POS-01", "Sandbox", "unknown-session", CancellationToken.None));

        var session = await service.StartTransactionAsync(
            "S01",
            "POS-01",
            CreateTransactionRequest(),
            CancellationToken.None);
        using var display = JsonDocument.Parse("""{ "sessionId": "ignored", "responseType": "display", "Response": { "DisplayText": ["PRESENT CARD"] } }""");
        using var receipt = JsonDocument.Parse("""{ "Response": { "ReceiptText": ["MERCHANT RECEIPT", "APPROVED"] } }""");

        await Assert.ThrowsAsync<LinklyCloudBackendNotificationUnauthorizedException>(() =>
            service.ReceiveNotificationAsync(
                "Sandbox",
                session.SessionId,
                "display",
                "Bearer wrong",
                display.RootElement,
                CancellationToken.None));

        await service.ReceiveNotificationAsync(
            "Sandbox",
            session.SessionId,
            "display",
            "Bearer sandbox-notify",
            display.RootElement,
            CancellationToken.None);
        await service.ReceiveNotificationAsync(
            "Sandbox",
            session.SessionId,
            "display",
            "Bearer sandbox-notify",
            display.RootElement,
            CancellationToken.None);

        using var approved = JsonDocument.Parse("""{ "Response": { "TxnRef": "TXN-APPROVED", "ResponseCode": "00", "ResponseText": "APPROVED" } }""");
        using var staleDeclined = JsonDocument.Parse("""{ "Response": { "TxnRef": "TXN-DECLINED", "ResponseCode": "05", "ResponseText": "DECLINED" } }""");
        await service.ReceiveNotificationAsync(
            "Sandbox",
            session.SessionId,
            "transaction",
            "Bearer sandbox-notify",
            approved.RootElement,
            CancellationToken.None);
        await service.ReceiveNotificationAsync(
            "Sandbox",
            session.SessionId,
            "transaction",
            "Bearer sandbox-notify",
            approved.RootElement,
            CancellationToken.None);
        await service.ReceiveNotificationAsync(
            "Sandbox",
            session.SessionId,
            "receipt",
            "Bearer sandbox-notify",
            receipt.RootElement,
            CancellationToken.None);
        await service.ReceiveNotificationAsync(
            "Sandbox",
            session.SessionId,
            "receipt",
            "Bearer sandbox-notify",
            receipt.RootElement,
            CancellationToken.None);
        await service.ReceiveNotificationAsync(
            "Sandbox",
            session.SessionId,
            "transaction",
            "Bearer sandbox-notify",
            staleDeclined.RootElement,
            CancellationToken.None);

        var scopedStatus = await service.GetStatusAsync(
            "S01",
            "POS-01",
            "Sandbox",
            session.SessionId,
            CancellationToken.None);
        Assert.NotNull(scopedStatus);
        Assert.Equal("Completed", scopedStatus!.Status);
        Assert.Equal(session.TxnRef, scopedStatus.TxnRef);
        Assert.Equal("00", scopedStatus.ResponseCode);
        Assert.Equal("PRESENT CARD", scopedStatus.DisplayText);
        Assert.Contains("MERCHANT RECEIPT", scopedStatus.ReceiptText, StringComparison.Ordinal);
        Assert.Equal(4, scopedStatus.Notifications.Count);
        Assert.Equal("display", scopedStatus.Notifications[0].Type);
        Assert.Null(scopedStatus.ReceiptPrintedAt);
        Assert.Null(await service.GetStatusAsync("S01", "POS-02", "Sandbox", session.SessionId, CancellationToken.None));
        Assert.Null(await service.GetStatusAsync("S01", "POS-01", "Production", session.SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task Display_notifications_persist_metadata_for_status_and_active_and_replace_current_prompt()
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        var session = await service.StartTransactionAsync(
            "S01",
            "POS-01",
            CreateTransactionRequest(),
            CancellationToken.None);
        using var firstDisplay = JsonDocument.Parse("""
            {
              "Response": {
                "DisplayText": ["PRESENT CARD"],
                "DisplayLines": ["PRESENT CARD", "TOTAL $10.00"],
                "CancelKeyFlag": "0",
                "OKKeyFlag": "1",
                "AcceptYesKeyFlag": "1",
                "DeclineNoKeyFlag": "0",
                "AuthoriseKeyFlag": "3",
                "InputType": "2",
                "GraphicCode": "7"
              }
            }
            """);

        await service.ReceiveNotificationAsync(
            "Sandbox",
            session.SessionId,
            "display",
            "Bearer sandbox-notify",
            firstDisplay.RootElement,
            CancellationToken.None);

        var status = await service.GetStatusAsync("S01", "POS-01", "Sandbox", session.SessionId, CancellationToken.None);
        var active = await service.GetActiveSessionAsync("S01", "POS-01", "Sandbox", CancellationToken.None);

        Assert.NotNull(status);
        Assert.NotNull(active);
        Assert.Equal("PRESENT CARD", status!.DisplayText);
        Assert.False(status.CancelKeyFlag);
        Assert.True(status.OKKeyFlag);
        Assert.True(status.AcceptYesKeyFlag);
        Assert.False(status.DeclineNoKeyFlag);
        Assert.True(status.AuthoriseKeyFlag);
        Assert.Equal("2", status.InputType);
        Assert.Equal("7", status.GraphicCode);
        Assert.Equal(["PRESENT CARD", "TOTAL $10.00"], status.DisplayLines);
        Assert.Equal(status.DisplayLines, active!.DisplayLines);
        Assert.Equal(status.OKKeyFlag, active.OKKeyFlag);

        using var nextDisplay = JsonDocument.Parse("""
            {
              "Response": {
                "DisplayText": ["ENTER PIN"],
                "DisplayLines": ["ENTER PIN"],
                "DeclineNoKeyFlag": "1",
                "InputType": "1"
              }
            }
            """);
        await service.ReceiveNotificationAsync(
            "Sandbox",
            session.SessionId,
            "display",
            "Bearer sandbox-notify",
            nextDisplay.RootElement,
            CancellationToken.None);

        status = await service.GetStatusAsync("S01", "POS-01", "Sandbox", session.SessionId, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal("ENTER PIN", status!.DisplayText);
        Assert.Equal(["ENTER PIN"], status.DisplayLines);
        Assert.False(status.CancelKeyFlag);
        Assert.False(status.OKKeyFlag);
        Assert.False(status.AcceptYesKeyFlag);
        Assert.True(status.DeclineNoKeyFlag);
        Assert.False(status.AuthoriseKeyFlag);
        Assert.Equal("1", status.InputType);
        Assert.Null(status.GraphicCode);

        using var approved = JsonDocument.Parse("""{ "Response": { "TxnRef": "TXN-APPROVED", "ResponseCode": "00", "ResponseText": "APPROVED" } }""");
        using var staleDisplay = JsonDocument.Parse("""{ "Response": { "DisplayText": ["OLD PROMPT"], "OKKeyFlag": "1" } }""");
        await service.ReceiveNotificationAsync(
            "Sandbox",
            session.SessionId,
            "transaction",
            "Bearer sandbox-notify",
            approved.RootElement,
            CancellationToken.None);
        await service.ReceiveNotificationAsync(
            "Sandbox",
            session.SessionId,
            "display",
            "Bearer sandbox-notify",
            staleDisplay.RootElement,
            CancellationToken.None);

        status = await service.GetStatusAsync("S01", "POS-01", "Sandbox", session.SessionId, CancellationToken.None);
        Assert.Equal("Completed", status?.Status);
        Assert.Equal("00", status?.ResponseCode);
        Assert.Null(await service.GetActiveSessionAsync("S01", "POS-01", "Sandbox", CancellationToken.None));
    }

    [Fact]
    public async Task MarkReceiptPrintedAsync_sets_printed_time_after_receipt_arrives()
    {
        var service = CreateService(new CapturingLinklyCloudBackendAsyncTransport(HttpStatusCode.Accepted));
        var session = await service.StartTransactionAsync(
            "S01",
            "POS-01",
            CreateTransactionRequest(),
            CancellationToken.None);
        using var receipt = JsonDocument.Parse("""{ "Response": { "ReceiptText": ["MERCHANT RECEIPT", "APPROVED"] } }""");
        await service.ReceiveNotificationAsync(
            "Sandbox",
            session.SessionId,
            "receipt",
            "Bearer sandbox-notify",
            receipt.RootElement,
            CancellationToken.None);

        var beforeMark = await service.GetStatusAsync(
            "S01",
            "POS-01",
            "Sandbox",
            session.SessionId,
            CancellationToken.None);
        Assert.NotNull(beforeMark);
        Assert.Contains("MERCHANT RECEIPT", beforeMark!.ReceiptText, StringComparison.Ordinal);
        Assert.Null(beforeMark.ReceiptPrintedAt);

        var afterMark = await service.MarkReceiptPrintedAsync(
            "S01",
            "POS-01",
            session.SessionId,
            new LinklyCloudBackendMarkReceiptPrintedRequest("Sandbox"),
            CancellationToken.None);

        Assert.NotNull(afterMark.ReceiptPrintedAt);
        Assert.Contains("MERCHANT RECEIPT", afterMark.ReceiptText, StringComparison.Ordinal);
    }

    private static TestableLinklyCloudBackendAsyncService CreateService(
        ILinklyCloudBackendAsyncTransport transport,
        ILinklyCloudBackendTokenProvider? tokenProvider = null,
        string? publicNotificationBaseUrl = "https://public.example/callback/",
        ILinklyCloudBackendAsyncRepository? repository = null,
        ILinklyCloudCredentialRepository? credentialRepository = null,
        ILinklyCloudBackendTerminalCredentialRepository? terminalCredentialRepository = null)
    {
        repository ??= new InMemoryLinklyCloudBackendAsyncRepository();
        credentialRepository ??= new CapturingCredentialRepository(new LinklyCloudCredentialRecord
        {
            StoreCode = "S01",
            Environment = "Sandbox",
            Username = "merchant-user",
            Password = "merchant-password",
            UpdatedAt = DateTime.UtcNow
        });
        terminalCredentialRepository ??= new CapturingTerminalCredentialRepository(new LinklyCloudBackendTerminalCredentialRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            Secret = "secret-pos-01",
            PosId = "11111111-1111-4111-8111-111111111111"
        });
        return new TestableLinklyCloudBackendAsyncService(
            repository,
            transport,
            tokenProvider ?? new CapturingLinklyCloudBackendTokenProvider(),
            credentialRepository,
            terminalCredentialRepository,
            Options.Create(new LinklyCloudBackendAsyncOptions
            {
                ProductionNotificationBearer = "production-notify",
                SandboxNotificationBearer = "sandbox-notify",
                PublicNotificationBaseUrl = publicNotificationBaseUrl
            }));
    }

    private static LinklyCloudBackendTransactionRequest CreateTransactionRequest()
    {
        return new LinklyCloudBackendTransactionRequest(
            "Sandbox",
            "P",
            1000,
            null);
    }

    private static Task SeedPendingSessionAsync(
        ILinklyCloudBackendAsyncRepository repository,
        string sessionId)
    {
        return repository.UpsertSessionAsync(new LinklyCloudBackendSessionRecord
        {
            Environment = "Sandbox",
            StoreCode = "S01",
            DeviceCode = "POS-01",
            SessionId = sessionId,
            Status = "Pending",
            TxnRef = "TXN-SENDKEY",
            IsActive = true,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
    }

    private sealed class TestableLinklyCloudBackendAsyncService(
        ILinklyCloudBackendAsyncRepository repository,
        ILinklyCloudBackendAsyncTransport transport,
        ILinklyCloudBackendTokenProvider tokenProvider,
        ILinklyCloudCredentialRepository credentialRepository,
        ILinklyCloudBackendTerminalCredentialRepository terminalCredentialRepository,
        IOptions<LinklyCloudBackendAsyncOptions> options)
        : LinklyCloudBackendAsyncService(repository, transport, tokenProvider, credentialRepository, terminalCredentialRepository, options)
    {
        public ILinklyCloudBackendAsyncRepository Repository { get; } = repository;
    }

    private sealed class CapturingLinklyCloudBackendAsyncTransport(
        HttpStatusCode responseStatusCode) : ILinklyCloudBackendAsyncTransport
    {
        public LinklyCloudBackendNotificationRequest? LastNotification { get; private set; }

        public LinklyCloudBackendTransportTransactionRequest? LastTransaction { get; private set; }

        public LinklyCloudBackendTransportSessionRequest? LastRecover { get; private set; }

        public LinklyCloudBackendTransportSendKeyRequest? LastSendKey { get; private set; }

        public Task<LinklyCloudBackendTransportResponse> StartTransactionAsync(
            LinklyCloudBackendTransportTransactionRequest request,
            CancellationToken cancellationToken)
        {
            LastTransaction = request;
            LastNotification = request.Notification;
            return Task.FromResult(new LinklyCloudBackendTransportResponse(responseStatusCode, null));
        }

        public Task<LinklyCloudBackendTransportResponse> RecoverTransactionAsync(
            LinklyCloudBackendTransportSessionRequest request,
            CancellationToken cancellationToken)
        {
            LastRecover = request;
            return Task.FromResult(new LinklyCloudBackendTransportResponse(responseStatusCode, null));
        }

        public Task<LinklyCloudBackendTransportResponse> SendKeyAsync(
            LinklyCloudBackendTransportSendKeyRequest request,
            CancellationToken cancellationToken)
        {
            LastSendKey = request;
            return Task.FromResult(new LinklyCloudBackendTransportResponse(responseStatusCode, null));
        }
    }

    private sealed class CompletingBeforeResponseTransport(
        ILinklyCloudBackendAsyncRepository repository,
        HttpStatusCode responseStatusCode) : ILinklyCloudBackendAsyncTransport
    {
        public async Task<LinklyCloudBackendTransportResponse> StartTransactionAsync(
            LinklyCloudBackendTransportTransactionRequest request,
            CancellationToken cancellationToken)
        {
            await CompleteAsync(request.Environment, request.SessionId, cancellationToken);
            return new LinklyCloudBackendTransportResponse(responseStatusCode, null);
        }

        public async Task<LinklyCloudBackendTransportResponse> RecoverTransactionAsync(
            LinklyCloudBackendTransportSessionRequest request,
            CancellationToken cancellationToken)
        {
            await CompleteAsync(request.Environment, request.SessionId, cancellationToken);
            return new LinklyCloudBackendTransportResponse(responseStatusCode, null);
        }

        public async Task<LinklyCloudBackendTransportResponse> SendKeyAsync(
            LinklyCloudBackendTransportSendKeyRequest request,
            CancellationToken cancellationToken)
        {
            await CompleteAsync(request.Environment, request.SessionId, cancellationToken);
            return new LinklyCloudBackendTransportResponse(responseStatusCode, null);
        }

        private async Task CompleteAsync(
            string environment,
            string sessionId,
            CancellationToken cancellationToken)
        {
            var current = await repository.GetSessionAsync(environment, "S01", "POS-01", sessionId, cancellationToken);
            Assert.NotNull(current);
            current!.Status = "Completed";
            current.ResponseCode = "00";
            current.ResponseText = "APPROVED";
            current.RecoveryAction = null;
            current.IsActive = false;
            current.LastHttpStatus = 200;
            current.UpdatedAt = DateTimeOffset.UtcNow;
            await repository.UpsertSessionAsync(current, cancellationToken);
        }
    }

    private sealed class TxnRefCollisionOnceRepository : ILinklyCloudBackendAsyncRepository
    {
        private readonly InMemoryLinklyCloudBackendAsyncRepository _inner = new();
        private bool _collided;

        public int TryCreateCallCount { get; private set; }

        public Task<bool> TryCreateSessionAsync(
            LinklyCloudBackendSessionRecord session,
            CancellationToken cancellationToken)
        {
            TryCreateCallCount++;
            if (!_collided)
            {
                _collided = true;
                return Task.FromResult(false);
            }

            return _inner.TryCreateSessionAsync(session, cancellationToken);
        }

        public Task UpsertSessionAsync(
            LinklyCloudBackendSessionRecord session,
            CancellationToken cancellationToken)
        {
            return _inner.UpsertSessionAsync(session, cancellationToken);
        }

        public Task<LinklyCloudBackendSessionRecord?> GetSessionAsync(
            string environment,
            string storeCode,
            string deviceCode,
            string sessionId,
            CancellationToken cancellationToken)
        {
            return _inner.GetSessionAsync(environment, storeCode, deviceCode, sessionId, cancellationToken);
        }

        public Task<LinklyCloudBackendSessionRecord?> GetSessionByEnvironmentSessionIdAsync(
            string environment,
            string sessionId,
            CancellationToken cancellationToken)
        {
            return _inner.GetSessionByEnvironmentSessionIdAsync(environment, sessionId, cancellationToken);
        }

        public Task<LinklyCloudBackendSessionRecord?> GetActiveSessionAsync(
            string environment,
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken)
        {
            return _inner.GetActiveSessionAsync(environment, storeCode, deviceCode, cancellationToken);
        }

        public Task AddNotificationAsync(
            LinklyCloudBackendNotificationRecord notification,
            CancellationToken cancellationToken)
        {
            return _inner.AddNotificationAsync(notification, cancellationToken);
        }

        public Task<IReadOnlyList<LinklyCloudBackendNotificationRecord>> GetNotificationsAsync(
            string environment,
            string storeCode,
            string deviceCode,
            string sessionId,
            CancellationToken cancellationToken)
        {
            return _inner.GetNotificationsAsync(environment, storeCode, deviceCode, sessionId, cancellationToken);
        }
    }

    private sealed class CapturingLinklyCloudBackendTokenProvider : ILinklyCloudBackendTokenProvider
    {
        public List<(string Environment, string StoreCode, string DeviceCode)> Calls { get; } = [];

        public Task<LinklyCloudBackendToken> GetTokenAsync(
            string environment,
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken)
        {
            Calls.Add((environment, storeCode, deviceCode));
            return Task.FromResult(new LinklyCloudBackendToken(
                $"https://server-rest.example/{deviceCode}/",
                $"server-token-{deviceCode}"));
        }
    }

    private sealed class ThrowingLinklyCloudBackendTokenProvider(string message) : ILinklyCloudBackendTokenProvider
    {
        public Task<LinklyCloudBackendToken> GetTokenAsync(
            string environment,
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken)
        {
            throw new LinklyCloudBackendValidationException(message);
        }
    }

    private sealed class CapturingCredentialRepository(
        LinklyCloudCredentialRecord? credential) : ILinklyCloudCredentialRepository
    {
        public List<(string StoreCode, string Environment)> Calls { get; } = [];

        public Task<LinklyCloudCredentialRecord?> GetByStoreCodeAsync(
            string storeCode,
            string environment,
            CancellationToken cancellationToken)
        {
            Calls.Add((storeCode, environment));
            return Task.FromResult(credential);
        }
    }

    private sealed class CapturingTerminalCredentialRepository(
        params LinklyCloudBackendTerminalCredentialRecord[] credentials) : ILinklyCloudBackendTerminalCredentialRepository
    {
        public List<(string Environment, string StoreCode, string DeviceCode)> Calls { get; } = [];

        public Task<LinklyCloudBackendTerminalCredentialRecord?> GetByDeviceAsync(
            string environment,
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken)
        {
            Calls.Add((environment, storeCode, deviceCode));
            return Task.FromResult(credentials.SingleOrDefault(credential =>
                string.Equals(credential.Environment, environment, StringComparison.Ordinal) &&
                string.Equals(credential.StoreCode, storeCode, StringComparison.Ordinal) &&
                string.Equals(credential.DeviceCode, deviceCode, StringComparison.Ordinal)));
        }
    }

    private sealed class CapturingTokenHttpMessageHandler : HttpMessageHandler
    {
        public List<JsonDocument> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(JsonDocument.Parse(body));
            var posId = RequestBodies[^1].RootElement.GetProperty("posId").GetString() ?? string.Empty;
            var suffix = posId.Split('-').Last();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    token = $"token-for-{suffix}",
                    expirySeconds = 300
                })
            };
        }
    }
}
