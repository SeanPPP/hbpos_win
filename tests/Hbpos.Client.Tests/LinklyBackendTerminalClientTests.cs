using System.Net;
using System.Text;
using System.Text.Json;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Linkly;

namespace Hbpos.Client.Tests;

public sealed class LinklyBackendTerminalClientTests
{
    [Fact]
    public async Task PurchaseAsync_uses_localized_backend_message_for_invalid_amount()
    {
        var localization = new LocalizationService();
        localization.SetCulture("zh-CN");
        var client = CreateClient(
            new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP should not be called.")),
            new FakeLinklyTerminalDialogService(),
            localization);

        var result = await client.PurchaseAsync(0m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Equal("刷卡金额必须大于零。", result.Message);
    }

    [Fact]
    public async Task TestConnectionAsync_uses_backend_health_config_endpoint_without_local_linkly_token()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return JsonResponse(
                """
                {
                  "success": true,
                  "data": {
                    "environment": "Sandbox",
                    "storeCode": "S01",
                    "deviceCode": "TERM-1",
                    "isReady": true,
                    "publicNotificationBaseUrl": "https://public.example/callback/",
                    "checks": []
                  }
                }
                """);
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.TestConnectionAsync(CardTerminalEnvironment.Sandbox);

        Assert.True(result.Succeeded);
        Assert.Contains("backend", result.Message, StringComparison.OrdinalIgnoreCase);
        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            "https://api.example/api/v1/linkly/cloud-backend/health?environment=Sandbox",
            request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task TestConnectionAsync_returns_failed_when_backend_health_is_not_ready()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse(
            """
            {
              "success": true,
              "data": {
                "environment": "Sandbox",
                "storeCode": "S01",
                "deviceCode": "TERM-1",
                "isReady": false,
                "checks": [
                  {
                    "code": "TERMINAL_SECRET",
                    "isReady": false,
                    "message": "Linkly Cloud terminal secret is missing for this terminal."
                  }
                ]
              }
            }
            """));
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.TestConnectionAsync(CardTerminalEnvironment.Sandbox);

        Assert.False(result.Succeeded);
        Assert.Equal("Linkly Cloud terminal secret is missing for this terminal.", result.Message);
    }

    [Fact]
    public async Task TestConnectionAsync_combines_multiple_backend_health_failures_and_logs_failed_codes()
    {
        using var logs = new ConsoleLogCapture();
        var handler = new StubHttpMessageHandler(_ => JsonResponse(
            """
            {
              "success": true,
              "data": {
                "environment": "Sandbox",
                "storeCode": "S01",
                "deviceCode": "TERM-1",
                "isReady": false,
                "checks": [
                  {
                    "code": "STORE_CREDENTIAL",
                    "isReady": false,
                    "message": "Linkly Cloud store credential is missing for this store."
                  },
                  {
                    "code": "TERMINAL_SECRET",
                    "isReady": false,
                    "message": "Linkly Cloud terminal secret is missing for this terminal."
                  }
                ]
              }
            }
            """));
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.TestConnectionAsync(CardTerminalEnvironment.Sandbox);

        Assert.False(result.Succeeded);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "Linkly Cloud store credential is missing for this store.",
                "Linkly Cloud terminal secret is missing for this terminal."),
            result.Message);
        Assert.Contains(
            logs.Lines,
            line => line.Contains("failedChecks=STORE_CREDENTIAL,TERMINAL_SECRET", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TestConnectionAsync_does_not_treat_missing_health_config_endpoint_as_success()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.TestConnectionAsync(CardTerminalEnvironment.Sandbox);

        Assert.False(result.Succeeded);
        Assert.Contains("404", result.Message, StringComparison.Ordinal);
        Assert.Equal(
            "https://api.example/api/v1/linkly/cloud-backend/health?environment=Sandbox",
            Assert.Single(requests).RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task PurchaseAsync_uses_backend_contract_without_client_secret_payload()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "backend-session-1",
                        "status": "Pending",
                        "txnRef": "260601120001",
                        "displayText": "PRESENT CARD",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "backend-session-1",
                        "status": "Completed",
                        "txnRef": "260601120001",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "displayText": "APPROVED",
                        "receiptText": "MERCHANT RECEIPT\nAPPROVED",
                        "recoveryCount": 0,
                        "receiptPrintedAt": "2026-06-01T02:00:00Z",
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """)
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal(
            "ANZBACKEND:260601120001:session=backend-session-1:environment=Sandbox",
            result.Reference);
        Assert.True(LinklyBackendPaymentReference.TryGetPrintMarker(result.Reference, out var environment, out var sessionId));
        Assert.Equal("Sandbox", environment);
        Assert.Equal("backend-session-1", sessionId);
        Assert.Collection(
            requests,
            active =>
            {
                Assert.Equal(HttpMethod.Get, active.Method);
                Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/active?environment=Sandbox", active.RequestUri!.AbsoluteUri);
            },
            create =>
            {
                Assert.Equal(HttpMethod.Post, create.Method);
                Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions", create.RequestUri!.AbsoluteUri);
            },
            status =>
            {
                Assert.Equal(HttpMethod.Get, status.Method);
                Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/backend-session-1/status?environment=Sandbox", status.RequestUri!.AbsoluteUri);
            });
        var createBody = await requests[1].Content!.ReadAsStringAsync();
        Assert.Equal("Sandbox", ReadJsonString(createBody, "environment"));
        Assert.Equal("P", ReadJsonString(createBody, "txnType"));
        Assert.Equal("1000", ReadJsonString(createBody, "amtPurchase"));
        Assert.Null(TryReadJsonString(createBody, "accessToken"));
        Assert.Null(TryReadJsonString(createBody, "restBaseUrl"));
        Assert.Null(TryReadJsonString(createBody, "storeCode"));
        Assert.Null(TryReadJsonString(createBody, "deviceCode"));
        Assert.Null(TryReadJsonString(createBody, "txnRef"));
        Assert.Contains(dialog.States, state => state.DisplayText == "PRESENT CARD");
        Assert.Contains(dialog.States, state => state.ReceiptText == "MERCHANT RECEIPT\nAPPROVED");
        Assert.Equal("MERCHANT RECEIPT\nAPPROVED", Assert.Single(result.CardTransactions!).ReceiptText);
        Assert.Equal(1, dialog.CloseCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_keeps_final_dialog_open_when_backend_declines()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            return request.RequestUri!.AbsolutePath.EndsWith("/active", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "declined-session-1",
                        "status": "Completed",
                        "txnRef": "260601120012",
                        "responseCode": "05",
                        "responseText": "DECLINED",
                        "displayText": "DECLINED",
                        "receiptText": "DECLINED RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": "2026-06-01T02:00:00Z",
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """);
        });
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Equal(0, dialog.CloseCallCount);
        var finalState = Assert.Single(dialog.States.Where(state => state.IsFinal));
        Assert.Equal("DECLINED", finalState.DisplayText);
        Assert.Equal("DECLINED", finalState.ResponseText);
    }

    [Fact]
    public async Task PurchaseAsync_final_dialog_state_uses_response_text_instead_of_stale_display_prompt()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            return request.RequestUri!.AbsolutePath.EndsWith("/active", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "final-display-session-1",
                        "status": "Completed",
                        "txnRef": "260601120011",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "displayText": "PRESENT CARD",
                        "receiptText": "FINAL RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": "2026-06-01T02:00:00Z",
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """);
        });
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        var finalState = Assert.Single(dialog.States.Where(state => state.IsFinal));
        Assert.Equal("APPROVED", finalState.DisplayText);
        Assert.Equal("APPROVED", finalState.ResponseText);
        Assert.DoesNotContain(dialog.States, state =>
            state.IsFinal &&
            state.DisplayText == "PRESENT CARD");
    }

    [Fact]
    public async Task PurchaseAsync_waits_briefly_for_receipt_after_transaction_completion()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "backend-session-2",
                        "status": "Pending",
                        "txnRef": "260601120002",
                        "displayText": "PRESENT CARD",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """),
                3 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "backend-session-2",
                        "status": "Completed",
                        "txnRef": "260601120002",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "displayText": "APPROVED",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "backend-session-2",
                        "status": "Completed",
                        "txnRef": "260601120002",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "displayText": "APPROVED",
                        "receiptText": "CUSTOMER RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": "2026-06-01T02:00:01Z",
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """)
            };
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal(4, requests.Count);
        Assert.Equal("CUSTOMER RECEIPT", Assert.Single(result.CardTransactions!).ReceiptText);
    }

    [Fact]
    public async Task PurchaseAsync_uses_protected_session_result_when_later_transaction_notification_conflicts()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            return request.RequestUri!.AbsolutePath.EndsWith("/active", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "protected-session-1",
                        "status": "Completed",
                        "txnRef": "260601120099",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "displayText": "APPROVED",
                        "receiptText": "APPROVED RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": [
                          {
                            "type": "transaction",
                            "payloadJson": "{\"Response\":{\"Success\":true,\"ResponseCode\":\"00\",\"ResponseText\":\"APPROVED\",\"AuthCode\":\"AUTH1\"}}",
                            "receivedAt": "2026-06-01T02:00:00Z"
                          },
                          {
                            "type": "transaction",
                            "payloadJson": "{\"Response\":{\"Success\":false,\"ResponseCode\":\"05\",\"ResponseText\":\"DECLINED\",\"AuthCode\":\"BAD\"}}",
                            "receivedAt": "2026-06-01T02:00:01Z"
                          }
                        ]
                      }
                    }
                    """);
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        var transaction = Assert.Single(result.CardTransactions!);
        Assert.Equal("00", transaction.ResponseCode);
        Assert.Equal("APPROVED", transaction.ResponseText);
        Assert.Equal("AUTH1", transaction.AuthCode);
    }

    [Fact]
    public async Task PurchaseAsync_recovers_existing_active_session_before_starting_new_transaction()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "active-session-1",
                        "status": "Pending",
                        "txnRef": "260601120010",
                        "displayText": "REMOVE CARD",
                        "receiptText": null,
                        "recoveryCount": 1,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 409,
                        "notifications": []
                      }
                    }
                    """),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "active-session-1",
                        "status": "Pending",
                        "txnRef": "260601120010",
                        "displayText": "PROCESSING",
                        "receiptText": null,
                        "recoveryCount": 2,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "active-session-1",
                        "status": "Completed",
                        "txnRef": "260601120010",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "displayText": "APPROVED",
                        "receiptText": "ACTIVE RECEIPT",
                        "recoveryCount": 2,
                        "receiptPrintedAt": "2026-06-01T02:00:02Z",
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """)
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.DoesNotContain(requests, request =>
            request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsolutePath.EndsWith("/transactions", StringComparison.Ordinal));
        Assert.Collection(
            requests,
            active => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/active?environment=Sandbox", active.RequestUri!.AbsoluteUri),
            recover => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/active-session-1/recover", recover.RequestUri!.AbsoluteUri),
            status => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/active-session-1/status?environment=Sandbox", status.RequestUri!.AbsoluteUri));
        var recoverBody = await requests[1].Content!.ReadAsStringAsync();
        Assert.Equal("Sandbox", ReadJsonString(recoverBody, "environment"));
        Assert.Null(TryReadJsonString(recoverBody, "accessToken"));
        Assert.Null(TryReadJsonString(recoverBody, "restBaseUrl"));
        Assert.Contains(dialog.States, state =>
            state.Message?.Contains("unfinished card transaction", StringComparison.Ordinal) == true &&
            state.SessionId == "active-session-1");
    }

    [Fact]
    public async Task PurchaseAsync_recovers_active_session_after_conflict_without_generic_backend_failure()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": false,
                      "message": "Active session exists."
                    }
                    """,
                    HttpStatusCode.Conflict),
                3 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "conflict-session-1",
                        "status": "Pending",
                        "txnRef": "260601120020",
                        "displayText": "WAITING",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 409,
                        "notifications": []
                      }
                    }
                    """),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "conflict-session-1",
                        "status": "Completed",
                        "txnRef": "260601120020",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "displayText": "APPROVED",
                        "receiptText": "CONFLICT RECEIPT",
                        "recoveryCount": 1,
                        "receiptPrintedAt": "2026-06-01T02:00:03Z",
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """)
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.NotEqual("ANZ Linkly Cloud backend communication failed.", result.Message);
        Assert.Collection(
            requests,
            activeBeforeStart => Assert.Equal(HttpMethod.Get, activeBeforeStart.Method),
            start => Assert.Equal(HttpMethod.Post, start.Method),
            activeAfterConflict => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/active?environment=Sandbox", activeAfterConflict.RequestUri!.AbsoluteUri),
            recover => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/conflict-session-1/recover", recover.RequestUri!.AbsoluteUri));
        Assert.Contains(dialog.States, state =>
            state.Message?.Contains("unfinished card transaction", StringComparison.Ordinal) == true &&
            state.SessionId == "conflict-session-1");
    }

    [Fact]
    public async Task PurchaseAsync_sends_dialog_key_during_status_polling()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "key-session-1",
                        "status": "Pending",
                        "txnRef": "260601120030",
                        "displayText": "PRESS OK",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """),
                3 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "key-session-1",
                        "status": "Pending",
                        "txnRef": "260601120030",
                        "displayText": "PROCESSING",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "key-session-1",
                        "status": "Completed",
                        "txnRef": "260601120030",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "displayText": "APPROVED",
                        "receiptText": "KEY RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": "2026-06-01T02:00:04Z",
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """)
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        dialog.EnqueueAction(new LinklyTerminalDialogAction("OK", null));
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Collection(
            requests,
            active => Assert.Equal(HttpMethod.Get, active.Method),
            start => Assert.Equal(HttpMethod.Post, start.Method),
            sendKey => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/key-session-1/sendkey", sendKey.RequestUri!.AbsoluteUri),
            status => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/key-session-1/status?environment=Sandbox", status.RequestUri!.AbsoluteUri));
        var sendKeyBody = await requests[2].Content!.ReadAsStringAsync();
        Assert.Equal("Sandbox", ReadJsonString(sendKeyBody, "environment"));
        Assert.Equal("0", ReadJsonString(sendKeyBody, "key"));
        Assert.Null(TryReadJsonString(sendKeyBody, "data"));
        Assert.Null(TryReadJsonString(sendKeyBody, "accessToken"));
        Assert.Contains(dialog.States, state => state.DisplayText == "PRESS OK");
    }

    [Fact]
    public async Task PurchaseAsync_keeps_polling_current_session_when_sendkey_request_fails()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "key-failure-session-1",
                        "status": "Pending",
                        "txnRef": "260601120031",
                        "displayText": "PRESS OK",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "okKeyFlag": true,
                        "notifications": []
                      }
                    }
                    """),
                3 => JsonResponse(
                    """
                    {
                      "success": false,
                      "message": "Send key failed."
                    }
                    """,
                    HttpStatusCode.InternalServerError),
                4 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "key-failure-session-1",
                        "status": "Pending",
                        "txnRef": "260601120031",
                        "displayText": "WAITING FOR CARD",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "key-failure-session-1",
                        "status": "Completed",
                        "txnRef": "260601120031",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "displayText": "APPROVED",
                        "receiptText": "RECOVERED RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": "2026-06-01T02:00:04Z",
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """)
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        dialog.EnqueueAction(new LinklyTerminalDialogAction("OK", null));
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Collection(
            requests,
            active => Assert.Equal(HttpMethod.Get, active.Method),
            start => Assert.Equal(HttpMethod.Post, start.Method),
            sendKey => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/key-failure-session-1/sendkey", sendKey.RequestUri!.AbsoluteUri),
            status => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/key-failure-session-1/status?environment=Sandbox", status.RequestUri!.AbsoluteUri),
            finalStatus => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/key-failure-session-1/status?environment=Sandbox", finalStatus.RequestUri!.AbsoluteUri));

        // sendkey 失败后应继续展示当前会话，并提示用户重试或恢复交易。
        Assert.Contains(dialog.States, state =>
            state.SessionId == "key-failure-session-1" &&
            state.Message == "Card terminal action failed. Try again or recover the transaction." &&
            state.DisplayText == "PRESS OK");
        Assert.Contains(dialog.States, state =>
            state.SessionId == "key-failure-session-1" &&
            state.DisplayText == "WAITING FOR CARD");
        Assert.Equal("RECOVERED RECEIPT", Assert.Single(result.CardTransactions!).ReceiptText);
    }

    [Fact]
    public async Task PurchaseAsync_builds_single_ok_cancel_display_button_when_ok_and_cancel_flags_are_both_true()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "ok-cancel-session-1",
                        "status": "Pending",
                        "txnRef": "260601120032",
                        "displayText": "CHOOSE ACTION",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "okKeyFlag": true,
                        "cancelKeyFlag": true,
                        "notifications": []
                      }
                    }
                    """),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "ok-cancel-session-1",
                        "status": "Completed",
                        "txnRef": "260601120032",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "displayText": "APPROVED",
                        "receiptText": "OK CANCEL RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": "2026-06-01T02:00:05Z",
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """)
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        var dialogState = Assert.Single(dialog.States.Where(state => state.Status == "Pending"));
        var button = Assert.Single(dialogState.DisplayButtons!);
        Assert.Equal("linkly.backend.dialog.button.okCancel", button.TextResourceKey);
        Assert.Equal(LinklyTerminalDialogKeys.OkCancel, button.Key);
    }

    [Fact]
    public async Task PurchaseAsync_treats_202_retry_hint_as_short_status_poll()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "short-poll-session",
                        "status": "Pending",
                        "txnRef": "260601120040",
                        "recoveryAction": "Retry",
                        "displayText": "PROCESSING",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 202,
                        "notifications": []
                      }
                    }
                    """),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "short-poll-session",
                        "status": "Completed",
                        "txnRef": "260601120040",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "displayText": "APPROVED",
                        "receiptText": "SHORT POLL RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """)
            };
        });
        var delays = new List<TimeSpan>();
        var client = CreateClient(
            handler,
            new FakeLinklyTerminalDialogService(),
            TimeSpan.FromMilliseconds(100),
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal([TimeSpan.FromMilliseconds(100)], delays);
        Assert.Equal(
            "https://api.example/api/v1/linkly/cloud-backend/transactions/short-poll-session/status?environment=Sandbox",
            requests[2].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task PurchaseAsync_uses_exponential_backoff_for_408_and_5xx_recovery()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "backoff-session",
                        "status": "Pending",
                        "txnRef": "260601120050",
                        "recoveryAction": "Retry",
                        "displayText": "RECOVERING",
                        "receiptText": null,
                        "recoveryCount": 1,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 408,
                        "notifications": []
                      }
                    }
                    """),
                3 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "backoff-session",
                        "status": "Pending",
                        "txnRef": "260601120050",
                        "recoveryAction": "Retry",
                        "displayText": "RECOVERING",
                        "receiptText": null,
                        "recoveryCount": 2,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 500,
                        "notifications": []
                      }
                    }
                    """),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "backoff-session",
                        "status": "Completed",
                        "txnRef": "260601120050",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "displayText": "APPROVED",
                        "receiptText": "BACKOFF RECEIPT",
                        "recoveryCount": 2,
                        "receiptPrintedAt": "2026-06-01T02:00:05Z",
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """)
            };
        });
        var delays = new List<TimeSpan>();
        var client = CreateClient(
            handler,
            new FakeLinklyTerminalDialogService(),
            TimeSpan.FromMilliseconds(100),
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal([TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400)], delays);
        Assert.Equal(
            "https://api.example/api/v1/linkly/cloud-backend/transactions/backoff-session/recover",
            requests[2].RequestUri!.AbsoluteUri);
        Assert.Equal(
            "https://api.example/api/v1/linkly/cloud-backend/transactions/backoff-session/recover",
            requests[3].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task PurchaseAsync_does_not_mark_receipt_printed_before_print_service_confirms()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "mark-receipt-session",
                        "status": "Completed",
                        "txnRef": "260601120060",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "displayText": "APPROVED",
                        "receiptText": "MARK RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """),
                _ => throw new InvalidOperationException("PurchaseAsync must not call receipt/printed before the receipt is actually printed.")
            };
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal("MARK RECEIPT", Assert.Single(result.CardTransactions!).ReceiptText);
        Assert.Collection(
            requests,
            active => Assert.Equal(HttpMethod.Get, active.Method),
            start => Assert.Equal(HttpMethod.Post, start.Method));
        Assert.DoesNotContain(requests, request =>
            request.RequestUri!.AbsolutePath.EndsWith("/receipt/printed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PurchaseAsync_suppresses_receipt_text_when_restored_session_was_already_printed()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return JsonResponse(
                """
                {
                  "success": true,
                  "data": {
                    "environment": "Sandbox",
                    "storeCode": "S01",
                    "deviceCode": "TERM-1",
                    "sessionId": "printed-session",
                    "status": "Completed",
                    "txnRef": "260601120070",
                    "responseCode": "00",
                    "responseText": "APPROVED",
                    "displayText": "APPROVED",
                    "receiptText": "ALREADY PRINTED RECEIPT",
                    "recoveryCount": 1,
                    "receiptPrintedAt": "2026-06-01T02:00:07Z",
                    "lastHttpStatus": 200,
                    "notifications": []
                  }
                }
                """);
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Null(Assert.Single(result.CardTransactions!).ReceiptText);
        Assert.Single(requests);
    }

    private static LinklyBackendTerminalClient CreateClient(
        StubHttpMessageHandler handler,
        FakeLinklyTerminalDialogService dialog)
    {
        return CreateClient(handler, dialog, TimeSpan.Zero, null, null);
    }

    private static LinklyBackendTerminalClient CreateClient(
        StubHttpMessageHandler handler,
        FakeLinklyTerminalDialogService dialog,
        ILocalizationService localization)
    {
        return CreateClient(handler, dialog, TimeSpan.Zero, null, localization);
    }

    private static LinklyBackendTerminalClient CreateClient(
        StubHttpMessageHandler handler,
        FakeLinklyTerminalDialogService dialog,
        TimeSpan pollInterval,
        Func<TimeSpan, CancellationToken, Task>? delayAsync)
    {
        return CreateClient(handler, dialog, pollInterval, delayAsync, null);
    }

    private static LinklyBackendTerminalClient CreateClient(
        StubHttpMessageHandler handler,
        FakeLinklyTerminalDialogService dialog,
        TimeSpan pollInterval,
        Func<TimeSpan, CancellationToken, Task>? delayAsync,
        ILocalizationService? localization)
    {
        return new LinklyBackendTerminalClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.example/") },
            dialog,
            pollInterval,
            delayAsync,
            localization);
    }

    private static PosSessionState CreateSession()
    {
        return new PosSessionState(
            "HB POS",
            "S01",
            "Main",
            "TERM-1",
            "C001",
            "Cashier",
            true,
            0);
    }

    private static CardTerminalSettings CreateSettings()
    {
        return CardTerminalSettings.FromEnvironment() with
        {
            Processor = CardProcessorKind.Linkly,
            Environment = CardTerminalEnvironment.Sandbox,
            LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync,
            TerminalTimeout = TimeSpan.FromSeconds(5)
        };
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static HttpRequestMessage CloneRequestWithBody(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        if (request.Content is not null)
        {
            clone.Content = new StringContent(
                request.Content.ReadAsStringAsync().GetAwaiter().GetResult(),
                Encoding.UTF8,
                "application/json");
        }

        return clone;
    }

    private static string? ReadJsonString(string json, string propertyName)
    {
        return TryReadJsonString(json, propertyName)
            ?? throw new InvalidOperationException($"Missing JSON property {propertyName}.");
    }

    private static string? TryReadJsonString(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            _ => value.ToString()
        };
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }

    private sealed class FakeLinklyTerminalDialogService : ILinklyTerminalDialogService
    {
        private readonly Queue<LinklyTerminalDialogAction?> _actions = new();

        public List<LinklyTerminalDialogState> States { get; } = [];

        public int CloseCallCount { get; private set; }

        public void EnqueueAction(LinklyTerminalDialogAction? action)
        {
            _actions.Enqueue(action);
        }

        public Task<LinklyTerminalDialogAction?> UpdateAsync(
            LinklyTerminalDialogState state,
            CancellationToken cancellationToken)
        {
            States.Add(state);
            return Task.FromResult(_actions.Count == 0 ? null : _actions.Dequeue());
        }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            CloseCallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ConsoleLogCapture : IDisposable
    {
        public List<string> Lines { get; } = [];

        public ConsoleLogCapture()
        {
            ConsoleLog.LineWritten += OnLineWritten;
        }

        public void Dispose()
        {
            ConsoleLog.LineWritten -= OnLineWritten;
        }

        private void OnLineWritten(string line)
        {
            Lines.Add(line);
        }
    }
}
