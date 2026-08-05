using Enma.Application.Abstractions;
using Enma.Application.Organizations.Create;
using Enma.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Enma.Infrastructure.Persistence;

public sealed class EnmaDbContext(DbContextOptions<EnmaDbContext> options)
    : DbContext(options), IUnitOfWork
{
    private const string OrganizationSlugUniqueConstraint = "ux_organizations_slug";

    public DbSet<Organization> Organizations => Set<Organization>();

    async Task<int> IUnitOfWork.SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await base.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            Organization? organization = GetOrganizationWithConflictingSlug(exception);

            if (organization is null)
            {
                throw;
            }

            throw new OrganizationSlugAlreadyExistsException(
                organization.Slug,
                exception);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EnmaDbContext).Assembly);
    }

    private static Organization? GetOrganizationWithConflictingSlug(
        DbUpdateException exception)
    {
        if (exception.InnerException is not PostgresException postgresException ||
            postgresException.SqlState != PostgresErrorCodes.UniqueViolation ||
            postgresException.ConstraintName != OrganizationSlugUniqueConstraint)
        {
            return null;
        }

        return exception.Entries
            .Select(entry => entry.Entity)
            .OfType<Organization>()
            .FirstOrDefault();
    }
}
