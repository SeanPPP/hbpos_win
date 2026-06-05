using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class ConfiguredCardTerminalClientTests
{
    private const string InitialToken = "opaque-initial-square-token";
    private const string RefreshedToken = "opaque-refreshed-square-token";

    [Fact]
    public async Task AuthorizeAsync_fails_when_card_terminal_is_not_configured()
    {
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CardTerminalSettings.FromEnvironment() with { Processor = CardProcessorKind.None }),
            new HttpClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))));

        var result = await client.AuthorizeAsync(10m, CreateSession());

        Assert.False(result.Approved);
        Assert.Equal("Card terminal is not configured.", result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_fails_closed_for_linkly_when_adapter_is_unavailable()
    {
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CardTerminalSettings.FromEnvironment() with { Processor = CardProcessorKind.Linkly }),
            new HttpClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))));

        var result = await client.AuthorizeAsync(10m, CreateSession());

        Assert.False(result.Approved);
        Assert.Contains("Linkly terminal adapter is unavailable", result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_delegates_linkly_purchase_to_adapter()
    {
        var settings = CardTerminalSettings.FromEnvironment() with { Processor = CardProcessorKind.Linkly };
        var linkly = new StubLinklyTerminalClient(new PaymentAuthorizationResult(
            true,
            "ANZ:TXN-1",
            "ANZ Linkly",
            10m,
            [new CardTransactionDto("ANZ", "TXN-1", "123456", "VISA", 4, "****1234", "MID", "00", "APPROVED", "42", DateTimeOffset.UtcNow, 10m, "receipt")]));
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(settings),
            new HttpClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))),
            linkly);

        var result = await client.AuthorizeAsync(10m, CreateSession());

        Assert.True(result.Approved);
        Assert.Equal("ANZ:TXN-1", result.Reference);
        Assert.Equal(10m, linkly.LastAmount);
        Assert.Equal("TXN-1", Assert.Single(result.CardTransactions!).TxnRef);
    }

    [Fact]
    public async Task RefundAsync_delegates_linkly_refund_to_adapter()
    {
        var settings = CardTerminalSettings.FromEnvironment() with { Processor = CardProcessorKind.Linkly };
        var linkly = new StubLinklyTerminalClient(new PaymentAuthorizationResult(true, "ANZ:REFUND-1", AuthorizedAmount: 6m));
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(settings),
            new HttpClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))),
            linkly);

        var result = await client.RefundAsync(6m, CreateSession(), "ANZ:ORIGINAL-1");

        Assert.True(result.Approved);
        Assert.Equal("ANZ:REFUND-1", result.Reference);
        Assert.Equal(6m, linkly.LastRefundAmount);
        Assert.Equal("ANZ:ORIGINAL-1", linkly.LastOriginalReference);
    }

    [Fact]
    public async Task AuthorizeAsync_preserves_linkly_backend_async_mode_for_configured_adapter()
    {
        var settings = CardTerminalSettings.FromEnvironment() with
        {
            Processor = CardProcessorKind.Linkly,
            LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync
        };
        var linkly = new StubLinklyTerminalClient(new PaymentAuthorizationResult(true, "ANZBACKEND:TXN-1", AuthorizedAmount: 10m));
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(settings),
            new HttpClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))),
            linkly);

        var result = await client.AuthorizeAsync(10m, CreateSession());

        Assert.True(result.Approved);
        Assert.Equal(LinklyConnectionMode.CloudBackendAsync, linkly.LastSettings?.LinklyConnectionMode);
    }

    [Fact]
    public async Task RefundAsync_posts_square_refund_request()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = CloneRequestWithBody(request);
            return JsonResponse(
                """
                {
                  "refund": {
                    "id": "refund-1",
                    "status": "PENDING"
                  }
                }
                """);
        });
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            new HttpClient(handler));

        var result = await client.RefundAsync(12.34m, CreateSession(), "SQ:payment-1");

        Assert.True(result.Approved);
        Assert.Equal("SQRF:refund-1", result.Reference);
        Assert.NotNull(capturedRequest);
        Assert.Equal("https://connect.squareup.com/v2/refunds", capturedRequest!.RequestUri!.AbsoluteUri);
        var body = await capturedRequest.Content!.ReadAsStringAsync();
        Assert.Contains("\"payment_id\":\"payment-1\"", body, StringComparison.Ordinal);
        Assert.Contains("\"amount\":1234", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefundAsync_reuses_square_refund_idempotency_key_after_network_failure()
    {
        var capturedRequests = new List<HttpRequestMessage>();
        var callCount = 0;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequests.Add(CloneRequestWithBody(request));
            callCount++;
            if (callCount == 1)
            {
                throw new HttpRequestException("Network dropped after Square accepted the refund.");
            }

            return JsonResponse(
                """
                {
                  "refund": {
                    "id": "refund-retry-1",
                    "status": "PENDING"
                  }
                }
                """);
        });
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            new HttpClient(handler));

        var first = await client.RefundAsync(12.34m, CreateSession(), "SQ:payment-1");
        var second = await client.RefundAsync(12.34m, CreateSession(), "SQ:payment-1");

        Assert.False(first.Approved);
        Assert.True(second.Approved);
        Assert.Equal(2, capturedRequests.Count);
        var firstKey = ReadJsonString(await capturedRequests[0].Content!.ReadAsStringAsync(), "idempotency_key");
        var secondKey = ReadJsonString(await capturedRequests[1].Content!.ReadAsStringAsync(), "idempotency_key");
        Assert.Equal(firstKey, secondKey);
    }

    [Fact]
    public async Task RefundAsync_rejects_missing_square_payment_reference()
    {
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            new HttpClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))));

        var result = await client.RefundAsync(5m, CreateSession(), null);

        Assert.False(result.Approved);
        Assert.Equal("Square refund requires an original Square payment reference.", result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_completes_square_checkout_and_returns_payment_reference()
    {
        using var logs = new ConsoleLogCapture();
        var requests = new List<HttpRequestMessage>();
        var statusPollCount = 0;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            requests.Add(CloneRequest(request));
            if (request.Method == HttpMethod.Post)
            {
                requests[^1] = CloneRequestWithBody(request);
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-1",
                        "status": "PENDING"
                      }
                    }
                    """);
            }

            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsolutePath.Contains("/v2/payments/", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """
                    {
                      "payment": {
                        "id": "payment-1",
                        "status": "COMPLETED",
                        "amount_money": { "amount": 1099, "currency": "AUD" }
                      }
                    }
                    """);
            }

            statusPollCount++;
            return JsonResponse(
                statusPollCount == 1
                    ? """
                      {
                        "checkout": {
                          "id": "checkout-1",
                          "status": "IN_PROGRESS"
                        }
                      }
                      """
                    : """
                      {
                        "checkout": {
                          "id": "checkout-1",
                          "status": "COMPLETED",
                          "amount_money": { "amount": 1099, "currency": "AUD" },
                          "payment_ids": [ "payment-1" ]
                        }
                      }
                      """);
        });
        var settings = CreateSquareSettings();
        var client = new ConfiguredCardTerminalClient(new StaticCardTerminalSettingsProvider(settings), new HttpClient(handler));

        var result = await client.AuthorizeAsync(10.99m, CreateSession());

        Assert.True(result.Approved);
        Assert.Equal("SQ:payment-1", result.Reference);
        Assert.Equal(10.99m, result.AuthorizedAmount);
        var cardTransaction = Assert.Single(result.CardTransactions!);
        Assert.Equal("Square", cardTransaction.Processor);
        Assert.Equal("payment-1", cardTransaction.TxnRef);
        Assert.Collection(
            requests,
            create =>
            {
                Assert.Equal(HttpMethod.Post, create.Method);
                Assert.Equal("https://connect.squareup.com/v2/terminals/checkouts", create.RequestUri!.AbsoluteUri);
                Assert.True(HasBearerToken(create, InitialToken), "create request should use the configured Square token");
                Assert.Equal("DEV-1", ReadCheckoutDeviceId(create));
            },
            firstStatus =>
            {
                Assert.Equal(HttpMethod.Get, firstStatus.Method);
                Assert.Equal("https://connect.squareup.com/v2/terminals/checkouts/checkout-1", firstStatus.RequestUri!.AbsoluteUri);
            },
            secondStatus =>
            {
                Assert.Equal(HttpMethod.Get, secondStatus.Method);
                Assert.Equal("https://connect.squareup.com/v2/terminals/checkouts/checkout-1", secondStatus.RequestUri!.AbsoluteUri);
            },
            payment =>
            {
                Assert.Equal(HttpMethod.Get, payment.Method);
                Assert.Equal("https://connect.squareup.com/v2/payments/payment-1", payment.RequestUri!.AbsoluteUri);
            });
        AssertContainsLogLine(logs.Lines, "authorize start");
        AssertContainsLogLine(logs.Lines, "storedSquareDeviceId=DEV-1 checkoutDeviceId=DEV-1");
        AssertContainsLogLine(logs.Lines, "checkout create succeeded checkoutId=checkout-1 status=PENDING");
        AssertContainsLogLine(logs.Lines, "checkout status checkoutId=checkout-1 poll=1 status=IN_PROGRESS");
        AssertContainsLogLine(logs.Lines, "checkout status checkoutId=checkout-1 poll=2 status=COMPLETED");
        AssertContainsLogLine(logs.Lines, "checkout completed checkoutId=checkout-1 paymentId=payment-1 amount=10.99");
        AssertContainsLogLine(logs.Lines, "payment verified checkoutId=checkout-1 paymentId=payment-1 status=COMPLETED amount=10.99 currency=AUD");
        AssertNoSensitiveTokenLogged(logs.Lines);
    }

    [Fact]
    public async Task AuthorizeAsync_rejects_completed_square_checkout_without_payment_id()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-no-payment",
                        "status": "PENDING"
                      }
                    }
                    """);
            }

            return JsonResponse(
                """
                {
                  "checkout": {
                    "id": "checkout-no-payment",
                    "status": "COMPLETED",
                    "amount_money": { "amount": 500, "currency": "AUD" },
                    "payment_ids": []
                  }
                }
                """);
        });
        var client = new ConfiguredCardTerminalClient(new StaticCardTerminalSettingsProvider(CreateSquareSettings()), new HttpClient(handler));

        var result = await client.AuthorizeAsync(5m, CreateSession());

        Assert.False(result.Approved);
        Assert.Equal("Square checkout did not return a payment id.", result.Message);
    }

    [Theory]
    [InlineData("APPROVED")]
    [InlineData("PENDING")]
    [InlineData("CANCELED")]
    [InlineData("FAILED")]
    public async Task AuthorizeAsync_rejects_square_payment_status_that_is_not_completed(string paymentStatus)
    {
        var handler = CreateCompletedCheckoutThenPaymentHandler(
            """
            {
              "payment": {
                "id": "payment-1",
                "status": "%STATUS%",
                "amount_money": { "amount": 500, "currency": "AUD" }
              }
            }
            """.Replace("%STATUS%", paymentStatus, StringComparison.Ordinal));
        var client = new ConfiguredCardTerminalClient(new StaticCardTerminalSettingsProvider(CreateSquareSettings()), new HttpClient(handler));

        var result = await client.AuthorizeAsync(5m, CreateSession());

        Assert.False(result.Approved);
        Assert.Equal($"Square payment status is {paymentStatus}.", result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_rejects_square_payment_amount_mismatch()
    {
        var handler = CreateCompletedCheckoutThenPaymentHandler(
            """
            {
              "payment": {
                "id": "payment-1",
                "status": "COMPLETED",
                "amount_money": { "amount": 499, "currency": "AUD" }
              }
            }
            """);
        var client = new ConfiguredCardTerminalClient(new StaticCardTerminalSettingsProvider(CreateSquareSettings()), new HttpClient(handler));

        var result = await client.AuthorizeAsync(5m, CreateSession());

        Assert.False(result.Approved);
        Assert.Equal("Square payment amount did not match the requested amount.", result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_rejects_square_payment_currency_mismatch()
    {
        var handler = CreateCompletedCheckoutThenPaymentHandler(
            """
            {
              "payment": {
                "id": "payment-1",
                "status": "COMPLETED",
                "amount_money": { "amount": 500, "currency": "USD" }
              }
            }
            """);
        var client = new ConfiguredCardTerminalClient(new StaticCardTerminalSettingsProvider(CreateSquareSettings()), new HttpClient(handler));

        var result = await client.AuthorizeAsync(5m, CreateSession());

        Assert.False(result.Approved);
        Assert.Equal("Square payment currency did not match the requested currency.", result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_returns_square_error_detail_for_failed_payment_lookup()
    {
        var handler = CreateCompletedCheckoutThenPaymentHandler(
            HttpStatusCode.BadRequest,
            """
            {
              "errors": [
                {
                  "code": "BAD_REQUEST",
                  "detail": "Payment is unavailable."
                }
              ]
            }
            """);
        var client = new ConfiguredCardTerminalClient(new StaticCardTerminalSettingsProvider(CreateSquareSettings()), new HttpClient(handler));

        var result = await client.AuthorizeAsync(5m, CreateSession());

        Assert.False(result.Approved);
        Assert.Equal("Square payment failed with HTTP 400: BAD_REQUEST: Payment is unavailable.", result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_returns_invalid_response_when_square_payment_payload_is_missing_payment()
    {
        var handler = CreateCompletedCheckoutThenPaymentHandler(
            """
            {
              "not_payment": {
                "id": "payment-1"
              }
            }
            """);
        var client = new ConfiguredCardTerminalClient(new StaticCardTerminalSettingsProvider(CreateSquareSettings()), new HttpClient(handler));

        var result = await client.AuthorizeAsync(5m, CreateSession());

        Assert.False(result.Approved);
        Assert.Equal("Square terminal returned an invalid response.", result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_returns_invalid_response_when_square_payment_is_missing_amount_money()
    {
        var handler = CreateCompletedCheckoutThenPaymentHandler(
            """
            {
              "payment": {
                "id": "payment-1",
                "status": "COMPLETED"
              }
            }
            """);
        var client = new ConfiguredCardTerminalClient(new StaticCardTerminalSettingsProvider(CreateSquareSettings()), new HttpClient(handler));

        var result = await client.AuthorizeAsync(5m, CreateSession());

        Assert.False(result.Approved);
        Assert.Equal("Square terminal returned an invalid response.", result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_refreshes_square_token_once_when_payment_lookup_is_unauthorized()
    {
        var requests = new List<HttpRequestMessage>();
        var paymentLookupCount = 0;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            requests.Add(CloneRequest(request));
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-payment-refresh",
                        "status": "PENDING"
                      }
                    }
                    """);
            }

            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsolutePath.Contains("/v2/payments/", StringComparison.Ordinal))
            {
                paymentLookupCount++;
                if (paymentLookupCount == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        ReasonPhrase = "Unauthorized"
                    };
                }

                return JsonResponse(
                    """
                    {
                      "payment": {
                        "id": "payment-1",
                        "status": "COMPLETED",
                        "amount_money": { "amount": 500, "currency": "AUD" }
                      }
                    }
                    """);
            }

            return JsonResponse(
                """
                {
                  "checkout": {
                    "id": "checkout-payment-refresh",
                    "status": "COMPLETED",
                    "amount_money": { "amount": 500, "currency": "AUD" },
                    "payment_ids": [ "payment-1" ]
                  }
                }
                """);
        });
        var tokenProvider = new FakeSquareAccessTokenProvider(RefreshedToken);
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            new HttpClient(handler),
            squareAccessTokenProvider: tokenProvider);

        var result = await client.AuthorizeAsync(5m, CreateSession());

        Assert.True(result.Approved);
        Assert.Equal(1, tokenProvider.ForceRefreshCount);
        Assert.Collection(
            requests,
            create => Assert.True(HasBearerToken(create, InitialToken), "create request should use the cached Square token"),
            status => Assert.True(HasBearerToken(status, InitialToken), "checkout status request should use the cached Square token"),
            failedPayment => Assert.True(HasBearerToken(failedPayment, InitialToken), "first payment request should use the cached Square token"),
            retriedPayment => Assert.True(HasBearerToken(retriedPayment, RefreshedToken), "retried payment request should use the refreshed Square token"));
    }

    [Fact]
    public async Task AuthorizeAsync_normalizes_devices_api_id_for_square_checkout()
    {
        using var logs = new ConsoleLogCapture();
        HttpRequestMessage? createRequest = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                createRequest = CloneRequestWithBody(request);
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-normalized",
                        "status": "PENDING"
                      }
                    }
                    """);
            }

            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsolutePath.Contains("/v2/payments/", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """
                    {
                      "payment": {
                        "id": "payment-normalized",
                        "status": "COMPLETED",
                        "amount_money": { "amount": 500, "currency": "AUD" }
                      }
                    }
                    """);
            }

            return JsonResponse(
                """
                {
                  "checkout": {
                    "id": "checkout-normalized",
                    "status": "COMPLETED",
                    "amount_money": { "amount": 500, "currency": "AUD" },
                    "payment_ids": [ "payment-normalized" ]
                  }
                }
                """);
        });
        var settings = CreateSquareSettings() with { SquareDeviceId = "device:533CS145C3000413" };
        var client = new ConfiguredCardTerminalClient(new StaticCardTerminalSettingsProvider(settings), new HttpClient(handler));

        var result = await client.AuthorizeAsync(5m, CreateSession());

        Assert.True(result.Approved);
        Assert.NotNull(createRequest);
        Assert.Equal("533CS145C3000413", ReadCheckoutDeviceId(createRequest!));
        AssertContainsLogLine(logs.Lines, "storedSquareDeviceId=device:533CS145C3000413 checkoutDeviceId=533CS145C3000413");
        AssertNoSensitiveTokenLogged(logs.Lines);
    }

    [Fact]
    public async Task AuthorizeAsync_refreshes_square_token_once_when_checkout_is_unauthorized()
    {
        using var logs = new ConsoleLogCapture();
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            requests.Add(CloneRequest(request));
            if (requests.Count == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    ReasonPhrase = "Unauthorized"
                };
            }

            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-1",
                        "status": "PENDING"
                      }
                    }
                    """);
            }

            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsolutePath.Contains("/v2/payments/", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """
                    {
                      "payment": {
                        "id": "payment-1",
                        "status": "COMPLETED",
                        "amount_money": { "amount": 500, "currency": "AUD" }
                      }
                    }
                    """);
            }

            return JsonResponse(
                """
                {
                  "checkout": {
                    "id": "checkout-1",
                    "status": "COMPLETED",
                    "amount_money": { "amount": 500, "currency": "AUD" },
                    "payment_ids": [ "payment-1" ]
                  }
                }
                """);
        });
        var settings = CreateSquareSettings();
        var tokenProvider = new FakeSquareAccessTokenProvider(RefreshedToken);
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(settings),
            new HttpClient(handler),
            squareAccessTokenProvider: tokenProvider);

        var result = await client.AuthorizeAsync(5m, CreateSession());

        Assert.True(result.Approved);
        Assert.Equal(1, tokenProvider.ForceRefreshCount);
        Assert.Collection(
            requests,
            first => Assert.True(HasBearerToken(first, InitialToken), "first checkout request should use the cached Square token"),
            retry => Assert.True(HasBearerToken(retry, RefreshedToken), "retry checkout request should use the refreshed Square token"),
            status => Assert.True(HasBearerToken(status, RefreshedToken), "status request should use the refreshed Square token"),
            payment => Assert.True(HasBearerToken(payment, RefreshedToken), "payment request should use the refreshed Square token"));
        AssertContainsLogLine(logs.Lines, "auth refresh requested method=POST path=terminals/checkouts environment=Production");
        AssertContainsLogLine(logs.Lines, "auth refresh succeeded method=POST path=terminals/checkouts");
        AssertNoSensitiveTokenLogged(logs.Lines);
    }

    [Fact]
    public async Task AuthorizeAsync_returns_failure_when_square_checkout_is_canceled_after_cancel_requested()
    {
        var requests = new List<HttpRequestMessage>();
        var statusPollCount = 0;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            requests.Add(CloneRequest(request));
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-2",
                        "status": "PENDING"
                      }
                    }
                    """);
            }

            statusPollCount++;
            return JsonResponse(
                statusPollCount == 1
                    ? """
                      {
                        "checkout": {
                          "id": "checkout-2",
                          "status": "CANCEL_REQUESTED"
                        }
                      }
                      """
                    : """
                      {
                        "checkout": {
                          "id": "checkout-2",
                          "status": "CANCELED"
                        }
                      }
                      """);
        });
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            new HttpClient(handler));

        var result = await client.AuthorizeAsync(10m, CreateSession());

        Assert.False(result.Approved);
        Assert.Equal("Square checkout was canceled.", result.Message);
        Assert.Equal(3, requests.Count);
    }

    [Fact]
    public async Task AuthorizeAsync_returns_square_error_detail_for_failed_checkout_request()
    {
        using var logs = new ConsoleLogCapture();
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            return JsonResponse(
                HttpStatusCode.BadRequest,
                """
                {
                  "errors": [
                    {
                      "code": "BAD_REQUEST",
                      "detail": "Device is offline."
                    }
                  ]
                }
                """);
        });
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            new HttpClient(handler));

        var result = await client.AuthorizeAsync(10m, CreateSession());

        Assert.False(result.Approved);
        Assert.Equal("Square checkout failed with HTTP 400: BAD_REQUEST: Device is offline.", result.Message);
        AssertContainsLogLine(logs.Lines, "checkout create failed http=400 detail=BAD_REQUEST: Device is offline.");
        AssertNoSensitiveTokenLogged(logs.Lines);
    }

    [Fact]
    public async Task AuthorizeAsync_returns_failure_for_unexpected_square_status()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-3",
                        "status": "PENDING"
                      }
                    }
                    """);
            }

            return JsonResponse(
                """
                {
                  "checkout": {
                    "id": "checkout-3",
                    "status": "BROKEN"
                  }
                }
                """);
        });
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            new HttpClient(handler));

        var result = await client.AuthorizeAsync(10m, CreateSession());

        Assert.False(result.Approved);
        Assert.Equal("Square checkout entered unexpected status 'BROKEN'.", result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_returns_invalid_response_when_square_payload_is_missing_checkout()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            return JsonResponse(
                """
                {
                  "not_checkout": {
                    "id": "checkout-4"
                  }
                }
                """);
        });
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            new HttpClient(handler));

        var result = await client.AuthorizeAsync(10m, CreateSession());

        Assert.False(result.Approved);
        Assert.Equal("Square terminal returned an invalid response.", result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_cleans_up_square_checkout_on_manual_cancel()
    {
        using var logs = new ConsoleLogCapture();
        var requests = new List<HttpRequestMessage>();
        var getStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            lock (requests)
            {
                requests.Add(CloneRequest(request));
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsolutePath.EndsWith("/cancel", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-cancel",
                        "status": "CANCEL_REQUESTED"
                      }
                    }
                    """);
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsolutePath.EndsWith("/dismiss", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-cancel",
                        "status": "CANCELED"
                      }
                    }
                    """);
            }

            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-cancel",
                        "status": "PENDING"
                      }
                    }
                    """);
            }

            getStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable after cancellation.");
        });
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            new HttpClient(handler));
        using var cancellationTokenSource = new CancellationTokenSource();
        var authorizeTask = client.AuthorizeAsync(10m, CreateSession(), cancellationTokenSource.Token);

        await getStarted.Task;
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authorizeTask);
        Assert.Collection(
            requests,
            create => Assert.Equal("https://connect.squareup.com/v2/terminals/checkouts", create.RequestUri!.AbsoluteUri),
            status => Assert.Equal("https://connect.squareup.com/v2/terminals/checkouts/checkout-cancel", status.RequestUri!.AbsoluteUri),
            cancel => Assert.Equal("https://connect.squareup.com/v2/terminals/checkouts/checkout-cancel/cancel", cancel.RequestUri!.AbsoluteUri),
            dismiss => Assert.Equal("https://connect.squareup.com/v2/terminals/checkouts/checkout-cancel/dismiss", dismiss.RequestUri!.AbsoluteUri));
        AssertContainsLogLine(logs.Lines, "authorize canceled checkoutId=checkout-cancel reason=caller-cancelled; starting cleanup");
        AssertContainsLogLine(logs.Lines, "cleanup start checkoutId=checkout-cancel allowRefresh=True");
        AssertContainsLogLine(logs.Lines, "checkout cancel result checkoutId=checkout-cancel status=CANCEL_REQUESTED shouldDismiss=True");
        AssertContainsLogLine(logs.Lines, "cleanup dismiss required checkoutId=checkout-cancel");
        AssertContainsLogLine(logs.Lines, "checkout dismiss result checkoutId=checkout-cancel http=200");
        AssertNoSensitiveTokenLogged(logs.Lines);
    }

    [Fact]
    public async Task AuthorizeAsync_cleans_up_square_checkout_on_timeout()
    {
        var requests = new List<HttpRequestMessage>();
        var getStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            lock (requests)
            {
                requests.Add(CloneRequest(request));
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsolutePath.EndsWith("/cancel", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-timeout",
                        "status": "CANCEL_REQUESTED"
                      }
                    }
                    """);
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsolutePath.EndsWith("/dismiss", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-timeout",
                        "status": "CANCELED"
                      }
                    }
                    """);
            }

            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-timeout",
                        "status": "PENDING"
                      }
                    }
                    """);
            }

            getStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable after timeout.");
        });
        var settings = CreateSquareSettings(terminalTimeout: TimeSpan.FromMilliseconds(50));
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(settings),
            new HttpClient(handler));

        var authorizeTask = client.AuthorizeAsync(10m, CreateSession());
        await getStarted.Task;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authorizeTask);
        Assert.Collection(
            requests,
            create => Assert.Equal("https://connect.squareup.com/v2/terminals/checkouts", create.RequestUri!.AbsoluteUri),
            status => Assert.Equal("https://connect.squareup.com/v2/terminals/checkouts/checkout-timeout", status.RequestUri!.AbsoluteUri),
            cancel => Assert.Equal("https://connect.squareup.com/v2/terminals/checkouts/checkout-timeout/cancel", cancel.RequestUri!.AbsoluteUri),
            dismiss => Assert.Equal("https://connect.squareup.com/v2/terminals/checkouts/checkout-timeout/dismiss", dismiss.RequestUri!.AbsoluteUri));
    }

    [Fact]
    public async Task AuthorizeAsync_refreshes_square_token_once_when_cancel_cleanup_is_unauthorized()
    {
        using var logs = new ConsoleLogCapture();
        var requests = new List<HttpRequestMessage>();
        var getStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            lock (requests)
            {
                requests.Add(CloneRequest(request));
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsolutePath.EndsWith("/dismiss", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-refresh-cancel",
                        "status": "CANCELED"
                      }
                    }
                    """);
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsolutePath.EndsWith("/cancel", StringComparison.Ordinal))
            {
                if (HasBearerToken(request, InitialToken))
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }

                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-refresh-cancel",
                        "status": "CANCEL_REQUESTED"
                      }
                    }
                    """);
            }

            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-refresh-cancel",
                        "status": "PENDING"
                      }
                    }
                    """);
            }

            getStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable after cancellation.");
        });
        var tokenProvider = new FakeSquareAccessTokenProvider(RefreshedToken);
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            new HttpClient(handler),
            squareAccessTokenProvider: tokenProvider);
        using var cancellationTokenSource = new CancellationTokenSource();
        var authorizeTask = client.AuthorizeAsync(10m, CreateSession(), cancellationTokenSource.Token);

        await getStarted.Task;
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authorizeTask);
        Assert.Equal(1, tokenProvider.ForceRefreshCount);
        Assert.Collection(
            requests,
            create => Assert.True(HasBearerToken(create, InitialToken)),
            status => Assert.True(HasBearerToken(status, InitialToken)),
            cancel => Assert.True(HasBearerToken(cancel, InitialToken)),
            cancelRetry => Assert.True(HasBearerToken(cancelRetry, RefreshedToken)),
            dismiss => Assert.True(HasBearerToken(dismiss, RefreshedToken)));
        AssertContainsLogLine(logs.Lines, "auth refresh requested method=POST path=terminals/checkouts/checkout-refresh-cancel/cancel environment=Production");
        AssertContainsLogLine(logs.Lines, "auth refresh succeeded method=POST path=terminals/checkouts/checkout-refresh-cancel/cancel");
        AssertContainsLogLine(logs.Lines, "checkout cancel result checkoutId=checkout-refresh-cancel status=CANCEL_REQUESTED shouldDismiss=True");
        AssertContainsLogLine(logs.Lines, "checkout dismiss result checkoutId=checkout-refresh-cancel http=200");
        AssertNoSensitiveTokenLogged(logs.Lines);
    }

    private static PosSessionState CreateSession()
    {
        return new PosSessionState(
            "HB POS",
            "1001",
            "Main",
            "TERM-1",
            "C001",
            "Cashier",
            true,
            0);
    }

    private static CardTerminalSettings CreateSquareSettings(TimeSpan? terminalTimeout = null)
    {
        return new CardTerminalSettings(
            CardProcessorKind.Square,
            CardTerminalEnvironment.Production,
            "127.0.0.1",
            2011,
            InitialToken,
            "LOC-1",
            "DEV-1",
            CardTerminalSettings.GetSquareApiBaseUrl(CardTerminalEnvironment.Production),
            terminalTimeout ?? TimeSpan.FromSeconds(10));
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return JsonResponse(HttpStatusCode.OK, json);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static StubHttpMessageHandler CreateCompletedCheckoutThenPaymentHandler(string paymentJson)
    {
        return CreateCompletedCheckoutThenPaymentHandler(HttpStatusCode.OK, paymentJson);
    }

    private static StubHttpMessageHandler CreateCompletedCheckoutThenPaymentHandler(HttpStatusCode paymentStatusCode, string paymentJson)
    {
        return new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-verified",
                        "status": "PENDING"
                      }
                    }
                    """);
            }

            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsolutePath.Contains("/v2/payments/", StringComparison.Ordinal))
            {
                return JsonResponse(paymentStatusCode, paymentJson);
            }

            return JsonResponse(
                """
                {
                  "checkout": {
                    "id": "checkout-verified",
                    "status": "COMPLETED",
                    "amount_money": { "amount": 500, "currency": "AUD" },
                    "payment_ids": [ "payment-1" ]
                  }
                }
                """);
        });
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        clone.Headers.Authorization = request.Headers.Authorization;
        foreach (var header in request.Headers.Where(header => header.Key != "Authorization"))
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    private static HttpRequestMessage CloneRequestWithBody(HttpRequestMessage request)
    {
        var clone = CloneRequest(request);
        if (request.Content is not null)
        {
            var body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            clone.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return clone;
    }

    private static string? ReadCheckoutDeviceId(HttpRequestMessage request)
    {
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        using var document = System.Text.Json.JsonDocument.Parse(body);
        return document.RootElement
            .GetProperty("checkout")
            .GetProperty("device_options")
            .GetProperty("device_id")
            .GetString();
    }

    private static bool HasBearerToken(HttpRequestMessage request, string expectedToken)
    {
        return request.Headers.Authorization?.Scheme == "Bearer" &&
            string.Equals(request.Headers.Authorization.Parameter, expectedToken, StringComparison.Ordinal);
    }

    private static string ReadJsonString(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(propertyName).GetString()
            ?? throw new InvalidOperationException($"Missing JSON property '{propertyName}'.");
    }

    private static void AssertContainsLogLine(IEnumerable<string> lines, string expectedMessageFragment)
    {
        Assert.Contains(
            lines,
            line => line.Contains("[HBPOS][Client][Square]", StringComparison.Ordinal) &&
                line.Contains(expectedMessageFragment, StringComparison.Ordinal));
    }

    private static void AssertNoSensitiveTokenLogged(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            Assert.DoesNotContain(InitialToken, line, StringComparison.Ordinal);
            Assert.DoesNotContain(RefreshedToken, line, StringComparison.Ordinal);
        }
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
            : this((request, cancellationToken) => Task.FromResult(handler(request, cancellationToken)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return handler(request, cancellationToken);
        }
    }

    private sealed class StubLinklyTerminalClient(PaymentAuthorizationResult result) : ILinklyTerminalClient
    {
        public decimal LastAmount { get; private set; }

        public decimal LastRefundAmount { get; private set; }

        public string? LastOriginalReference { get; private set; }

        public CardTerminalSettings? LastSettings { get; private set; }

        public Task<LinklyConnectionTestResult> TestConnectionAsync(
            string host,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LinklyConnectionTestResult(true));
        }

        public Task<PaymentAuthorizationResult> PurchaseAsync(
            decimal amount,
            PosSessionState session,
            CardTerminalSettings settings,
            CancellationToken cancellationToken = default)
        {
            LastAmount = amount;
            LastSettings = settings;
            return Task.FromResult(result);
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            CardTerminalSettings settings,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            LastRefundAmount = amount;
            LastOriginalReference = originalReference;
            LastSettings = settings;
            return Task.FromResult(result);
        }

        public Task<PaymentAuthorizationResult> VoidAsync(
            decimal amount,
            PosSessionState session,
            CardTerminalSettings settings,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeSquareAccessTokenProvider(string token) : ISquareAccessTokenProvider
    {
        public int ForceRefreshCount { get; private set; }

        public Task<string?> GetSquareAccessTokenAsync(
            CardTerminalEnvironment environment,
            bool forceRefresh,
            CancellationToken cancellationToken = default)
        {
            if (forceRefresh)
            {
                ForceRefreshCount++;
            }

            return Task.FromResult<string?>(token);
        }
    }

    private sealed class ConsoleLogCapture : IDisposable
    {
        private readonly List<string> _lines = [];

        public ConsoleLogCapture()
        {
            ConsoleLog.LineWritten += OnLineWritten;
        }

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return _lines.ToArray();
                }
            }
        }

        public void Dispose()
        {
            ConsoleLog.LineWritten -= OnLineWritten;
        }

        private void OnLineWritten(string line)
        {
            lock (_lines)
            {
                _lines.Add(line);
            }
        }
    }
}
