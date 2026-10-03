using Enma.Application.Validation;
using Enma.Domain.Finance;

namespace Enma.Application.Finance;

internal static class PaymentReversalReasonParser
{
    public static PaymentReversalReason Parse(string? value)
    {
        return value switch
        {
            "registeredByMistake" => PaymentReversalReason.RegisteredByMistake,
            "wrongInstallment" => PaymentReversalReason.WrongInstallment,
            "paymentNotCompleted" => PaymentReversalReason.PaymentNotCompleted,
            "other" => PaymentReversalReason.Other,
            _ => throw new RequestValidationException(
                "Reason must be 'registeredByMistake', 'wrongInstallment', " +
                "'paymentNotCompleted', or 'other'.")
        };
    }
}
