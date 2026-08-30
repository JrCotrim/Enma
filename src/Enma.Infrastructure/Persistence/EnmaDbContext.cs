using Enma.Application.Abstractions;
using Enma.Application.Organizations.Create;
using Enma.Application.Users;
using Enma.Domain.Authentication;
using Enma.Domain.Auditing;
using Enma.Domain.CalendarEvents;
using Enma.Domain.Clients;
using Enma.Domain.Deadlines;
using Enma.Domain.Documents;
using Enma.Domain.Notifications;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Tasks;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Enma.Infrastructure.Persistence;

public sealed class EnmaDbContext(DbContextOptions<EnmaDbContext> options)
    : DbContext(options), IUnitOfWork
{
    private const string OrganizationSlugUniqueConstraint = "ux_organizations_slug";
    private const string UserEmailUniqueConstraint = "ux_users_email";

    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();

    public DbSet<Client> Clients => Set<Client>();

    public DbSet<LegalDocument> LegalDocuments => Set<LegalDocument>();

    public DbSet<LegalDeadline> LegalDeadlines => Set<LegalDeadline>();

    public DbSet<LegalProcess> LegalProcesses => Set<LegalProcess>();

    public DbSet<LegalTask> LegalTasks => Set<LegalTask>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<User> Users => Set<User>();

    public DbSet<UserCredential> UserCredentials => Set<UserCredential>();

    public DbSet<AuthenticationSession> AuthenticationSessions =>
        Set<AuthenticationSession>();

    public DbSet<EmailVerificationChallenge> EmailVerificationChallenges =>
        Set<EmailVerificationChallenge>();

    public DbSet<EmailVerificationSendBudget> EmailVerificationSendBudgets =>
        Set<EmailVerificationSendBudget>();

    public DbSet<OrganizationMembership> OrganizationMemberships =>
        Set<OrganizationMembership>();

    public DbSet<OrganizationInvitation> OrganizationInvitations =>
        Set<OrganizationInvitation>();

    async Task<int> IUnitOfWork.SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation
            })
        {
            var postgresException = (PostgresException)exception.InnerException;

            switch (postgresException.ConstraintName)
            {
                case OrganizationSlugUniqueConstraint:
                    Organization? organization = exception.Entries
                        .Select(entry => entry.Entity)
                        .OfType<Organization>()
                        .FirstOrDefault();

                    if (organization is null)
                    {
                        throw;
                    }

                    throw new OrganizationSlugAlreadyExistsException(
                        organization.Slug,
                        exception);

                case UserEmailUniqueConstraint:
                    User? user = GetSingleConflictingUser(exception);

                    if (user is null)
                    {
                        throw;
                    }

                    throw new UserEmailAlreadyExistsException(
                        user.Email,
                        exception);

                default:
                    throw;
            }
        }
    }

    public override int SaveChanges()
    {
        return SaveChanges(acceptAllChangesOnSuccess: true);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureAuditLogsAreAppendOnly();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        return SaveChangesAsync(
            acceptAllChangesOnSuccess: true,
            cancellationToken);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        EnsureAuditLogsAreAppendOnly();
        return base.SaveChangesAsync(
            acceptAllChangesOnSuccess,
            cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EnmaDbContext).Assembly);
    }

    private static User? GetSingleConflictingUser(
        DbUpdateException exception)
    {
        User[] users = exception.Entries
            .Select(entry => entry.Entity)
            .OfType<User>()
            .Take(2)
            .ToArray();

        return users.Length == 1 ? users[0] : null;
    }

    private void EnsureAuditLogsAreAppendOnly()
    {
        bool hasMutation = ChangeTracker
            .Entries<AuditLog>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted);

        if (hasMutation)
        {
            throw new InvalidOperationException(
                "Audit logs are append-only and cannot be modified or deleted.");
        }
    }
}
