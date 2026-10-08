using Enma.Application.Security;

namespace Enma.E2ETests.Infrastructure;

// Approved substitute (D1): the real checker calls the public Pwned Passwords
// API, which would make journeys depend on internet access. Screening itself
// is covered by unit and integration tests.
internal sealed class FakeCompromisedPasswordChecker : ICompromisedPasswordChecker
{
    public Task<bool> IsCompromisedAsync(
        string password,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }
}
