namespace Enma.Application.Security;

public interface ICompromisedPasswordChecker
{
    Task<bool> IsCompromisedAsync(
        string password,
        CancellationToken cancellationToken = default);
}
