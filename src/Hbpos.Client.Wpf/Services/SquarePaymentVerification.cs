namespace Hbpos.Client.Wpf.Services;

public enum SquarePaymentVerificationFailure
{
    None,
    Status,
    Amount,
    Currency
}

public sealed record SquarePaymentVerificationOutcome(
    bool Verified,
    SquarePaymentVerificationFailure Failure,
    string? Message);

public static class SquarePaymentVerifier
{
    public static SquarePaymentVerificationOutcome Verify(
        string paymentStatus,
        long paymentAmountCents,
        string paymentCurrency,
        long requestedAmountCents,
        string requestedCurrency)
    {
        if (!string.Equals(paymentStatus, "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            return new SquarePaymentVerificationOutcome(
                false,
                SquarePaymentVerificationFailure.Status,
                $"Square payment status is {paymentStatus}.");
        }

        if (paymentAmountCents != requestedAmountCents)
        {
            return new SquarePaymentVerificationOutcome(
                false,
                SquarePaymentVerificationFailure.Amount,
                "Square payment amount did not match the requested amount.");
        }

        if (!string.Equals(paymentCurrency, requestedCurrency, StringComparison.OrdinalIgnoreCase))
        {
            return new SquarePaymentVerificationOutcome(
                false,
                SquarePaymentVerificationFailure.Currency,
                "Square payment currency did not match the requested currency.");
        }

        return new SquarePaymentVerificationOutcome(true, SquarePaymentVerificationFailure.None, null);
    }
}
