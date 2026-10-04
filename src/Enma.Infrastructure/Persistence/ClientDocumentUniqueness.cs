using Enma.Domain.Clients;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence;

internal static class ClientDocumentUniqueness
{
    public const string CpfConstraint = "ux_clients_organization_id_cpf";
    public const string CnpjConstraint = "ux_clients_organization_id_cnpj";

    // Callers must hold the organization row lock so that same-organization
    // client writes are serialized; the unique indexes remain the fallback.
    public static Task<bool> HasConflictAsync(
        EnmaDbContext dbContext,
        Client client,
        CancellationToken cancellationToken)
    {
        string? cpf = client.Cpf;
        string? cnpj = client.Cnpj;

        if (cpf is null && cnpj is null)
        {
            return Task.FromResult(false);
        }

        return dbContext.Clients
            .AsNoTracking()
            .AnyAsync(
                candidate =>
                    candidate.OrganizationId == client.OrganizationId &&
                    candidate.Id != client.Id &&
                    ((cpf != null && candidate.Cpf == cpf) ||
                        (cnpj != null && candidate.Cnpj == cnpj)),
                cancellationToken);
    }

    public static bool IsUniqueViolation(Exception exception)
    {
        return PostgreSqlConstraintViolations.IsUniqueViolation(
                exception,
                CpfConstraint) ||
            PostgreSqlConstraintViolations.IsUniqueViolation(
                exception,
                CnpjConstraint);
    }
}
