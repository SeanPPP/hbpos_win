using System.Net;
using System.Text;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Vouchers;

namespace Hbpos.Client.Tests;

public sealed class VoucherApiClientTests
{
    [Fact]
    public async Task IssueRefundAsync_returns_refund_voucher_reference()
    {
        HttpRequestMessage? capturedRequest = null;
        var client = new VoucherApiClient(new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = CloneRequestWithBody(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "success": true,
                      "data": {
                        "voucherCode": "RF123",
                        "amount": 9.50,
                        "remainingAmount": 9.50,
                        "status": "1",
                        "expiredAt": "2027-05-26T00:00:00Z"
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }))
        {
            BaseAddress = new Uri("http://localhost/")
        });

        var result = await client.IssueRefundAsync(
            9.5m,
            new PosSessionState("HB POS", "S001", "Main", "POS-01", "C001", "Alice", true, 0),
            "11111111-1111-1111-1111-111111111111",
            "11111111-1111-1111-1111-111111111111:22222222-2222-2222-2222-222222222222",
            "Refund reason");

        Assert.True(result.Approved);
        Assert.Equal("VOUCHER_REFUND:RF123", result.Reference);
        Assert.Equal(9.5m, result.AuthorizedAmount);
        Assert.NotNull(capturedRequest);
        Assert.Equal("http://localhost/api/v1/vouchers/refund", capturedRequest!.RequestUri!.AbsoluteUri);
        var body = await capturedRequest.Content!.ReadAsStringAsync();
        Assert.Contains("\"storeCode\":\"S001\"", body, StringComparison.Ordinal);
        Assert.Contains("\"cashierId\":\"C001\"", body, StringComparison.Ordinal);
        Assert.Contains("\"idempotencyKey\":\"11111111-1111-1111-1111-111111111111:22222222-2222-2222-2222-222222222222\"", body, StringComparison.Ordinal);
        Assert.Contains("\"orderReference\":\"11111111-1111-1111-1111-111111111111\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueVoucherAsync_posts_issue_request()
    {
        HttpRequestMessage? capturedRequest = null;
        var client = new VoucherApiClient(new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = CloneRequestWithBody(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "success": true,
                      "data": {
                        "voucherCode": "VC123",
                        "amount": 20.00,
                        "remainingAmount": 20.00,
                        "status": "1",
                        "expiredAt": "2027-05-26T00:00:00Z",
                        "storeCode": "S001",
                        "customerCode": "CUS001"
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }))
        {
            BaseAddress = new Uri("http://localhost/")
        });

        var result = await client.IssueVoucherAsync(
            new StoreVoucherIssueRequest("S001", 20m, "C001", "ISSUE-1", CustomerCode: "CUS001", Reason: "Manual issue"));

        Assert.Equal("VC123", result.VoucherCode);
        Assert.Equal(20m, result.RemainingAmount);
        Assert.Equal("S001", result.StoreCode);
        Assert.Equal("CUS001", result.CustomerCode);
        Assert.NotNull(capturedRequest);
        Assert.Equal("http://localhost/api/v1/vouchers/issue", capturedRequest!.RequestUri!.AbsoluteUri);
        var body = await capturedRequest.Content!.ReadAsStringAsync();
        Assert.Contains("\"storeCode\":\"S001\"", body, StringComparison.Ordinal);
        Assert.Contains("\"cashierId\":\"C001\"", body, StringComparison.Ordinal);
        Assert.Contains("\"idempotencyKey\":\"ISSUE-1\"", body, StringComparison.Ordinal);
        Assert.Contains("\"customerCode\":\"CUS001\"", body, StringComparison.Ordinal);
    }

    private static HttpRequestMessage CloneRequestWithBody(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        if (request.Content is not null)
        {
            var body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            clone.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return clone;
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
}
