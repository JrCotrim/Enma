namespace Enma.Domain.Finance;

/// <summary>
/// Numeric values are permanent. Only append new values; never reuse one.
/// </summary>
public enum PaymentReversalReason
{
    RegisteredByMistake = 1,
    WrongInstallment = 2,
    PaymentNotCompleted = 3,
    Other = 4
}
