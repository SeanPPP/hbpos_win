using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
            new HttpClient(new StubHttpMessageHandler((_, _) => throw new InvalidOperationException("HTTP should not be called."))));

        var result = await client.AuthorizeAsync(10m, CreateSession());

        Assert.False(result.Approved);
        Assert.Equal("Card terminal is not configured.", result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_fails_closed_for_linkly_when_adapter_is_unavailable()
    {
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CardTerminalSettings.FromEnvironment() with { Processor = CardProcessorKind.Linkly }),
            new HttpClient(new StubHttpMessageHandler((_, _) => throw new InvalidOperationException("HTTP should not be called."))));

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
            new HttpClient(new StubHttpMessageHandler((_, _) => throw new InvalidOperationException("HTTP should not be called."))),
            linkly);

        var result = await client.AuthorizeAsync(10m, CreateSession());

        Assert.True(result.Approved);
        Assert.Equal("ANZ:TXN-1", result.Reference);
        Assert.Equal(10m, linkly.LastAmount);
        Assert.Equal("TXN-1", Assert.Single(result.CardTransactions!).TxnRef);
    }

    [Fact]
    public async Task AuthorizeAsync_completes_square_checkout_and_returns_payment_reference()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            requests.Add(CloneRequest(request));
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

            return JsonResponse(
                """
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
        var settings = new CardTerminalSettings(
            CardProcessorKind.Square,
            CardTerminalEnvironment.Production,
            "127.0.0.1",
            2011,
            InitialToken,
            "LOC-1",
            "DEV-1",
            CardTerminalSettings.GetSquareApiBaseUrl(CardTerminalEnvironment.Production),
            TimeSpan.FromSeconds(10));
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
            },
            status =>
            {
                Assert.Equal(HttpMethod.Get, status.Method);
                Assert.Equal("https://connect.squareup.com/v2/terminals/checkouts/checkout-1", status.RequestUri!.AbsoluteUri);
            });
    }

    [Fact]
    public async Task AuthorizeAsync_refreshes_square_token_once_when_checkout_is_unauthorized()
    {
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
        var settings = new CardTerminalSettings(
            CardProcessorKind.Square,
            CardTerminalEnvironment.Production,
            "127.0.0.1",
            2011,
            InitialToken,
            "LOC-1",
            "DEV-1",
            CardTerminalSettings.GetSquareApiBaseUrl(CardTerminalEnvironment.Production),
            TimeSpan.FromSeconds(10));
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
            status => Assert.True(HasBearerToken(status, RefreshedToken), "status request should use the refreshed Square token"));
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

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
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

    private static bool HasBearerToken(HttpRequestMessage request, string expectedToken)
    {
        return request.Headers.Authorization?.Scheme == "Bearer" &&
            string.Equals(request.Headers.Authorization.Parameter, expectedToken, StringComparison.Ordinal);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request, cancellationToken));
        }
    }

    private sealed class StubLinklyTerminalClient(PaymentAuthorizationResult result) : ILinklyTerminalClient
    {
        public decimal LastAmount { get; private set; }

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
            return Task.FromResult(result);
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            CardTerminalSettings settings,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
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
}
