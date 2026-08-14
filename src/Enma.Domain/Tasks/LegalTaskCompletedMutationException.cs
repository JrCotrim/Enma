namespace Enma.Domain.Tasks;

public sealed class LegalTaskCompletedMutationException
    : InvalidOperationException
{
    public LegalTaskCompletedMutationException()
        : base(LegalTaskErrors.CompletedTaskCannotChange)
    {
    }
}
