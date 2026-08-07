using Enma.Application.Abstractions;
using Enma.Application.Authentication;
using Enma.Application.Organizations;
using Enma.Application.Security;
using Enma.Application.Users;
using Enma.Infrastructure;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Enma.Infrastructure.Persistence.Repositories;
using Enma.Infrastructure.Security;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MicrosoftPasswordHasher = Microsoft.AspNetCore.Identity.IPasswordHasher<object>;

namespace Enma.IntegrationTests.Infrastructure;

[Collection(PostgreSqlCollection.Name)]
public sealed class DependencyInjectionTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task AddInfrastructure_RegistersCompromisedPasswordCheckerAsTypedHttpClient()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(fixture.ConnectionString);

        await using ServiceProvider serviceProvider = services.BuildServiceProvider();
        await using AsyncServiceScope firstScope = serviceProvider.CreateAsyncScope();

        ICompromisedPasswordChecker firstChecker = firstScope.ServiceProvider
            .GetRequiredService<ICompromisedPasswordChecker>();
        ICompromisedPasswordChecker secondChecker = firstScope.ServiceProvider
            .GetRequiredService<ICompromisedPasswordChecker>();
        EnmaDbContext dbContext = firstScope.ServiceProvider
            .GetRequiredService<EnmaDbContext>();
        IUnitOfWork unitOfWork = firstScope.ServiceProvider
            .GetRequiredService<IUnitOfWork>();
        IOrganizationRepository organizationRepository = firstScope.ServiceProvider
            .GetRequiredService<IOrganizationRepository>();
        IUserRepository userRepository = firstScope.ServiceProvider
            .GetRequiredService<IUserRepository>();
        IUserCredentialRepository userCredentialRepository = firstScope.ServiceProvider
            .GetRequiredService<IUserCredentialRepository>();
        IOrganizationMembershipRepository membershipRepository = firstScope
            .ServiceProvider
            .GetRequiredService<IOrganizationMembershipRepository>();
        IPasswordHasher passwordHasher = firstScope.ServiceProvider
            .GetRequiredService<IPasswordHasher>();
        IPasswordPolicy passwordPolicy = firstScope.ServiceProvider
            .GetRequiredService<IPasswordPolicy>();

        Assert.IsType<PwnedPasswordsCompromisedPasswordChecker>(firstChecker);
        Assert.IsType<PwnedPasswordsCompromisedPasswordChecker>(secondChecker);
        Assert.NotSame(firstChecker, secondChecker);
        Assert.Same(dbContext, unitOfWork);
        Assert.Same(
            organizationRepository,
            firstScope.ServiceProvider.GetRequiredService<IOrganizationRepository>());
        Assert.Same(
            userRepository,
            firstScope.ServiceProvider.GetRequiredService<IUserRepository>());
        Assert.Same(
            userCredentialRepository,
            firstScope.ServiceProvider.GetRequiredService<IUserCredentialRepository>());
        Assert.Same(
            membershipRepository,
            firstScope.ServiceProvider
                .GetRequiredService<IOrganizationMembershipRepository>());
        Assert.Same(
            passwordHasher,
            firstScope.ServiceProvider.GetRequiredService<IPasswordHasher>());
        Assert.Same(
            passwordPolicy,
            firstScope.ServiceProvider.GetRequiredService<IPasswordPolicy>());

        await using AsyncServiceScope secondScope = serviceProvider.CreateAsyncScope();

        Assert.NotSame(
            passwordHasher,
            secondScope.ServiceProvider.GetRequiredService<IPasswordHasher>());
        Assert.Same(
            passwordPolicy,
            secondScope.ServiceProvider.GetRequiredService<IPasswordPolicy>());
    }

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
        IAuthenticationIdentityLookup firstAuthenticationIdentityLookup =
            firstScope.ServiceProvider
                .GetRequiredService<IAuthenticationIdentityLookup>();
        IAuthenticationSessionRepository firstAuthenticationSessionRepository =
            firstScope.ServiceProvider
                .GetRequiredService<IAuthenticationSessionRepository>();
        IAuthenticationSessionHandleService firstAuthenticationSessionHandleService =
            firstScope.ServiceProvider
                .GetRequiredService<IAuthenticationSessionHandleService>();
        IOrganizationMembershipRepository firstMembershipRepository =
            firstScope.ServiceProvider
                .GetRequiredService<IOrganizationMembershipRepository>();
        IOrganizationRepository firstOrganizationRepository = firstScope.ServiceProvider
            .GetRequiredService<IOrganizationRepository>();
        IUnitOfWork firstUnitOfWork = firstScope.ServiceProvider
            .GetRequiredService<IUnitOfWork>();
        EnmaDbContext firstDbContext = firstScope.ServiceProvider
            .GetRequiredService<EnmaDbContext>();
        IPasswordHasher firstPasswordHasher = firstScope.ServiceProvider
            .GetRequiredService<IPasswordHasher>();
        IPasswordPolicy firstPasswordPolicy = firstScope.ServiceProvider
            .GetRequiredService<IPasswordPolicy>();
        MicrosoftPasswordHasher firstMicrosoftPasswordHasher = firstScope.ServiceProvider
            .GetRequiredService<MicrosoftPasswordHasher>();

        Assert.IsType<UserRepository>(firstUserRepository);
        Assert.IsType<UserCredentialRepository>(firstUserCredentialRepository);
        Assert.IsType<AuthenticationIdentityLookup>(firstAuthenticationIdentityLookup);
        Assert.IsType<AuthenticationSessionRepository>(
            firstAuthenticationSessionRepository);
        Assert.IsType<CryptographicAuthenticationSessionHandleService>(
            firstAuthenticationSessionHandleService);
        Assert.IsType<OrganizationMembershipRepository>(firstMembershipRepository);
        Assert.IsType<OrganizationRepository>(firstOrganizationRepository);
        Assert.IsType<AspNetCorePasswordHasher>(firstPasswordHasher);
        Assert.IsType<DefaultPasswordPolicy>(firstPasswordPolicy);
        Assert.IsType<PasswordHasher<object>>(firstMicrosoftPasswordHasher);
        Assert.Same(
            firstUserRepository,
            firstScope.ServiceProvider.GetRequiredService<IUserRepository>());
        Assert.Same(
            firstUserCredentialRepository,
            firstScope.ServiceProvider
                .GetRequiredService<IUserCredentialRepository>());
        Assert.Same(
            firstAuthenticationIdentityLookup,
            firstScope.ServiceProvider
                .GetRequiredService<IAuthenticationIdentityLookup>());
        Assert.Same(
            firstAuthenticationSessionRepository,
            firstScope.ServiceProvider
                .GetRequiredService<IAuthenticationSessionRepository>());
        Assert.Same(
            firstAuthenticationSessionHandleService,
            firstScope.ServiceProvider
                .GetRequiredService<IAuthenticationSessionHandleService>());
        Assert.Same(
            firstMembershipRepository,
            firstScope.ServiceProvider
                .GetRequiredService<IOrganizationMembershipRepository>());
        Assert.Same(
            firstOrganizationRepository,
            firstScope.ServiceProvider.GetRequiredService<IOrganizationRepository>());
        Assert.Same(
            firstPasswordHasher,
            firstScope.ServiceProvider.GetRequiredService<IPasswordHasher>());
        Assert.Same(
            firstPasswordPolicy,
            firstScope.ServiceProvider.GetRequiredService<IPasswordPolicy>());
        Assert.Same(
            firstMicrosoftPasswordHasher,
            firstScope.ServiceProvider.GetRequiredService<MicrosoftPasswordHasher>());
        Assert.Same(firstDbContext, firstUnitOfWork);

        await using AsyncServiceScope secondScope = serviceProvider.CreateAsyncScope();
        IUserRepository secondUserRepository = secondScope.ServiceProvider
            .GetRequiredService<IUserRepository>();
        IUserCredentialRepository secondUserCredentialRepository =
            secondScope.ServiceProvider
                .GetRequiredService<IUserCredentialRepository>();
        IAuthenticationIdentityLookup secondAuthenticationIdentityLookup =
            secondScope.ServiceProvider
                .GetRequiredService<IAuthenticationIdentityLookup>();
        IAuthenticationSessionRepository secondAuthenticationSessionRepository =
            secondScope.ServiceProvider
                .GetRequiredService<IAuthenticationSessionRepository>();
        IAuthenticationSessionHandleService secondAuthenticationSessionHandleService =
            secondScope.ServiceProvider
                .GetRequiredService<IAuthenticationSessionHandleService>();
        IOrganizationMembershipRepository secondMembershipRepository =
            secondScope.ServiceProvider
                .GetRequiredService<IOrganizationMembershipRepository>();
        IOrganizationRepository secondOrganizationRepository = secondScope.ServiceProvider
            .GetRequiredService<IOrganizationRepository>();
        EnmaDbContext secondDbContext = secondScope.ServiceProvider
            .GetRequiredService<EnmaDbContext>();
        IPasswordHasher secondPasswordHasher = secondScope.ServiceProvider
            .GetRequiredService<IPasswordHasher>();
        IPasswordPolicy secondPasswordPolicy = secondScope.ServiceProvider
            .GetRequiredService<IPasswordPolicy>();
        MicrosoftPasswordHasher secondMicrosoftPasswordHasher = secondScope.ServiceProvider
            .GetRequiredService<MicrosoftPasswordHasher>();

        Assert.NotSame(firstUserRepository, secondUserRepository);
        Assert.NotSame(
            firstUserCredentialRepository,
            secondUserCredentialRepository);
        Assert.NotSame(
            firstAuthenticationIdentityLookup,
            secondAuthenticationIdentityLookup);
        Assert.NotSame(
            firstAuthenticationSessionRepository,
            secondAuthenticationSessionRepository);
        Assert.Same(
            firstAuthenticationSessionHandleService,
            secondAuthenticationSessionHandleService);
        Assert.NotSame(firstDbContext, firstAuthenticationSessionHandleService);
        Assert.NotSame(firstMembershipRepository, secondMembershipRepository);
        Assert.NotSame(firstOrganizationRepository, secondOrganizationRepository);
        Assert.NotSame(firstDbContext, secondDbContext);
        Assert.NotSame(firstPasswordHasher, secondPasswordHasher);
        Assert.Same(firstPasswordPolicy, secondPasswordPolicy);
        Assert.NotSame(
            firstMicrosoftPasswordHasher,
            secondMicrosoftPasswordHasher);
        Assert.Same(
            secondDbContext,
            secondScope.ServiceProvider.GetRequiredService<IUnitOfWork>());
    }
}
