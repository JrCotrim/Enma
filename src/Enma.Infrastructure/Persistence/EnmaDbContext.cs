using Enma.Application.Abstractions;
using Enma.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence;

public sealed class EnmaDbContext(DbContextOptions<EnmaDbContext> options)
    : DbContext(options), IUnitOfWork
{
    public DbSet<Organization> Organizations => Set<Organization>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EnmaDbContext).Assembly);
    }
}
