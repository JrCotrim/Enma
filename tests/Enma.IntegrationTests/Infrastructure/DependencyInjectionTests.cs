using Enma.Application.Abstractions;
using Enma.Application.Organizations;
using Enma.Application.Users;
using Enma.Infrastructure;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Repositories;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Enma.IntegrationTests.Infrastructure;

[Collection(PostgreSqlCollection.Name)]
public sealed class DependencyInjectionTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task AddInfrastructure_RegistersRepositoriesAndSharedScopedUnitOfWork()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(fixture.ConnectionString);

        await using ServiceProvider serviceProvider = services.BuildServiceProvider();
        await using AsyncServiceScope firstScope = serviceProvider.CreateAsyncScope();

        IUserRepository firstUserRepository = firstScope.ServiceProvider
            .GetRequiredService<IUserRepository>();
        IUserCredentialRepository firstUserCredentialRepository =
            firstScope.ServiceProvider
                .GetRequiredService<IUserCredentialRepository>();
        IOrganizationMembershipRepository firstMembershipRepository =
            firstScope.ServiceProvider
                .GetRequiredService<IOrganizationMembershipRepository>();
        IOrganizationRepository firstOrganizationRepository = firstScope.ServiceProvider
            .GetRequiredService<IOrganizationRepository>();
        IUnitOfWork firstUnitOfWork = firstScope.ServiceProvider
            .GetRequiredService<IUnitOfWork>();
        EnmaDbContext firstDbContext = firstScope.ServiceProvider
            .GetRequiredService<EnmaDbContext>();

        Assert.IsType<UserRepository>(firstUserRepository);
        Assert.IsType<UserCredentialRepository>(firstUserCredentialRepository);
        Assert.IsType<OrganizationMembershipRepository>(firstMembershipRepository);
        Assert.IsType<OrganizationRepository>(firstOrganizationRepository);
        Assert.Same(
            firstUserRepository,
            firstScope.ServiceProvider.GetRequiredService<IUserRepository>());
        Assert.Same(
            firstUserCredentialRepository,
            firstScope.ServiceProvider
                .GetRequiredService<IUserCredentialRepository>());
        Assert.Same(
            firstMembershipRepository,
            firstScope.ServiceProvider
                .GetRequiredService<IOrganizationMembershipRepository>());
        Assert.Same(
            firstOrganizationRepository,
            firstScope.ServiceProvider.GetRequiredService<IOrganizationRepository>());
        Assert.Same(firstDbContext, firstUnitOfWork);

        await using AsyncServiceScope secondScope = serviceProvider.CreateAsyncScope();
        IUserRepository secondUserRepository = secondScope.ServiceProvider
            .GetRequiredService<IUserRepository>();
        IUserCredentialRepository secondUserCredentialRepository =
            secondScope.ServiceProvider
                .GetRequiredService<IUserCredentialRepository>();
        IOrganizationMembershipRepository secondMembershipRepository =
            secondScope.ServiceProvider
                .GetRequiredService<IOrganizationMembershipRepository>();
        IOrganizationRepository secondOrganizationRepository = secondScope.ServiceProvider
            .GetRequiredService<IOrganizationRepository>();
        EnmaDbContext secondDbContext = secondScope.ServiceProvider
            .GetRequiredService<EnmaDbContext>();

        Assert.NotSame(firstUserRepository, secondUserRepository);
        Assert.NotSame(
            firstUserCredentialRepository,
            secondUserCredentialRepository);
        Assert.NotSame(firstMembershipRepository, secondMembershipRepository);
        Assert.NotSame(firstOrganizationRepository, secondOrganizationRepository);
        Assert.NotSame(firstDbContext, secondDbContext);
        Assert.Same(
            secondDbContext,
            secondScope.ServiceProvider.GetRequiredService<IUnitOfWork>());
    }
}
