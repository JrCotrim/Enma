using Enma.Application.Abstractions;
using Enma.Application.Authentication;
using Enma.Application.Organizations;
using Enma.Application.Security;
using Enma.Application.Users;
using Enma.Infrastructure;
using Enma.Infrastructure.Email;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Enma.Infrastructure.Persistence.Repositories;
using Enma.Infrastructure.Security;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MailKit.Security;
using MicrosoftPasswordHasher = Microsoft.AspNetCore.Identity.IPasswordHasher<object>;

namespace Enma.IntegrationTests.Infrastructure;

[Collection(PostgreSqlCollection.Name)]
public sealed class DependencyInjectionTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task AddInfrastructure_RegistersCompromisedPasswordCheckerAsTypedHttpClient()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(fixture.ConnectionString, CreateConfiguration());

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
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddLogging();
        services.AddInfrastructure(fixture.ConnectionString, CreateConfiguration());

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
        IEmailVerificationUserLookup firstEmailVerificationUserLookup =
            firstScope.ServiceProvider
                .GetRequiredService<IEmailVerificationUserLookup>();
        IAuthenticationSessionRepository firstAuthenticationSessionRepository =
            firstScope.ServiceProvider
                .GetRequiredService<IAuthenticationSessionRepository>();
        IAuthenticationSessionIssuancePersistence
            firstAuthenticationSessionIssuancePersistence = firstScope.ServiceProvider
                .GetRequiredService<IAuthenticationSessionIssuancePersistence>();
        IEmailVerificationChallengePersistence
            firstEmailVerificationChallengePersistence = firstScope.ServiceProvider
                .GetRequiredService<IEmailVerificationChallengePersistence>();
        IAuthenticationSessionHandleService firstAuthenticationSessionHandleService =
            firstScope.ServiceProvider
                .GetRequiredService<IAuthenticationSessionHandleService>();
        IEmailVerificationTokenService firstEmailVerificationTokenService =
            firstScope.ServiceProvider
                .GetRequiredService<IEmailVerificationTokenService>();
        IEmailVerificationDelivery firstEmailVerificationDelivery =
            firstScope.ServiceProvider
                .GetRequiredService<IEmailVerificationDelivery>();
        EmailVerificationLinkBuilder firstEmailVerificationLinkBuilder =
            firstScope.ServiceProvider
                .GetRequiredService<EmailVerificationLinkBuilder>();
        RequestEmailVerificationUseCase firstRequestUseCase = firstScope.ServiceProvider
            .GetRequiredService<RequestEmailVerificationUseCase>();
        VerifyEmailUseCase firstVerifyUseCase = firstScope.ServiceProvider
            .GetRequiredService<VerifyEmailUseCase>();
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
        Assert.IsType<EmailVerificationUserLookup>(firstEmailVerificationUserLookup);
        Assert.IsType<AuthenticationSessionRepository>(
            firstAuthenticationSessionRepository);
        Assert.IsType<AuthenticationSessionIssuancePersistence>(
            firstAuthenticationSessionIssuancePersistence);
        Assert.IsType<EmailVerificationChallengePersistence>(
            firstEmailVerificationChallengePersistence);
        Assert.IsType<CryptographicAuthenticationSessionHandleService>(
            firstAuthenticationSessionHandleService);
        Assert.IsType<CryptographicEmailVerificationTokenService>(
            firstEmailVerificationTokenService);
        Assert.IsType<BudgetedEmailVerificationDelivery>(
            firstEmailVerificationDelivery);
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
            firstEmailVerificationUserLookup,
            firstScope.ServiceProvider
                .GetRequiredService<IEmailVerificationUserLookup>());
        Assert.Same(
            firstAuthenticationSessionRepository,
            firstScope.ServiceProvider
                .GetRequiredService<IAuthenticationSessionRepository>());
        Assert.Same(
            firstAuthenticationSessionIssuancePersistence,
            firstScope.ServiceProvider
                .GetRequiredService<IAuthenticationSessionIssuancePersistence>());
        Assert.Same(
            firstEmailVerificationChallengePersistence,
            firstScope.ServiceProvider
                .GetRequiredService<IEmailVerificationChallengePersistence>());
        Assert.Same(
            firstAuthenticationSessionHandleService,
            firstScope.ServiceProvider
                .GetRequiredService<IAuthenticationSessionHandleService>());
        Assert.Same(
            firstEmailVerificationTokenService,
            firstScope.ServiceProvider
                .GetRequiredService<IEmailVerificationTokenService>());
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
        Assert.Same(
            firstEmailVerificationDelivery,
            firstScope.ServiceProvider.GetRequiredService<IEmailVerificationDelivery>());
        Assert.Same(
            firstEmailVerificationLinkBuilder,
            firstScope.ServiceProvider.GetRequiredService<EmailVerificationLinkBuilder>());
        Assert.Same(
            firstRequestUseCase,
            firstScope.ServiceProvider.GetRequiredService<RequestEmailVerificationUseCase>());
        Assert.Same(
            firstVerifyUseCase,
            firstScope.ServiceProvider.GetRequiredService<VerifyEmailUseCase>());

        await using AsyncServiceScope secondScope = serviceProvider.CreateAsyncScope();
        IUserRepository secondUserRepository = secondScope.ServiceProvider
            .GetRequiredService<IUserRepository>();
        IUserCredentialRepository secondUserCredentialRepository =
            secondScope.ServiceProvider
                .GetRequiredService<IUserCredentialRepository>();
        IAuthenticationIdentityLookup secondAuthenticationIdentityLookup =
            secondScope.ServiceProvider
                .GetRequiredService<IAuthenticationIdentityLookup>();
        IEmailVerificationUserLookup secondEmailVerificationUserLookup =
            secondScope.ServiceProvider
                .GetRequiredService<IEmailVerificationUserLookup>();
        IAuthenticationSessionRepository secondAuthenticationSessionRepository =
            secondScope.ServiceProvider
                .GetRequiredService<IAuthenticationSessionRepository>();
        IAuthenticationSessionIssuancePersistence
            secondAuthenticationSessionIssuancePersistence = secondScope.ServiceProvider
                .GetRequiredService<IAuthenticationSessionIssuancePersistence>();
        IEmailVerificationChallengePersistence
            secondEmailVerificationChallengePersistence = secondScope.ServiceProvider
                .GetRequiredService<IEmailVerificationChallengePersistence>();
        IAuthenticationSessionHandleService secondAuthenticationSessionHandleService =
            secondScope.ServiceProvider
                .GetRequiredService<IAuthenticationSessionHandleService>();
        IEmailVerificationTokenService secondEmailVerificationTokenService =
            secondScope.ServiceProvider
                .GetRequiredService<IEmailVerificationTokenService>();
        IEmailVerificationDelivery secondEmailVerificationDelivery =
            secondScope.ServiceProvider
                .GetRequiredService<IEmailVerificationDelivery>();
        EmailVerificationLinkBuilder secondEmailVerificationLinkBuilder =
            secondScope.ServiceProvider
                .GetRequiredService<EmailVerificationLinkBuilder>();
        RequestEmailVerificationUseCase secondRequestUseCase = secondScope.ServiceProvider
            .GetRequiredService<RequestEmailVerificationUseCase>();
        VerifyEmailUseCase secondVerifyUseCase = secondScope.ServiceProvider
            .GetRequiredService<VerifyEmailUseCase>();
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
            firstEmailVerificationUserLookup,
            secondEmailVerificationUserLookup);
        Assert.NotSame(
            firstAuthenticationSessionRepository,
            secondAuthenticationSessionRepository);
        Assert.NotSame(
            firstAuthenticationSessionIssuancePersistence,
            secondAuthenticationSessionIssuancePersistence);
        Assert.NotSame(
            firstEmailVerificationChallengePersistence,
            secondEmailVerificationChallengePersistence);
        Assert.Same(
            firstAuthenticationSessionHandleService,
            secondAuthenticationSessionHandleService);
        Assert.Same(
            firstEmailVerificationTokenService,
            secondEmailVerificationTokenService);
        Assert.NotSame(
            firstEmailVerificationDelivery,
            secondEmailVerificationDelivery);
        Assert.Same(
            firstEmailVerificationLinkBuilder,
            secondEmailVerificationLinkBuilder);
        Assert.NotSame(firstRequestUseCase, secondRequestUseCase);
        Assert.NotSame(firstVerifyUseCase, secondVerifyUseCase);
        Assert.NotSame(firstDbContext, firstAuthenticationSessionHandleService);
        Assert.NotSame(firstDbContext, firstEmailVerificationTokenService);
        Assert.NotSame(firstDbContext, firstEmailVerificationChallengePersistence);
        Assert.NotSame(firstDbContext, firstEmailVerificationUserLookup);
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

    [Fact]
    public async Task AddInfrastructure_InsecureSmtpConfiguration_RejectsDeliveryWithoutSecretDisclosure()
    {
        const string syntheticPassword = "synthetic-smtp-password";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(
            fixture.ConnectionString,
            CreateConfiguration(
                SecureSocketOptions.None,
                syntheticPassword));

        await using ServiceProvider serviceProvider = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => serviceProvider.GetRequiredService<IEmailVerificationDelivery>());

        Assert.Contains(
            exception.Failures,
            failure => failure.Contains("SmtpSecurity", StringComparison.Ordinal));
        Assert.DoesNotContain(
            syntheticPassword,
            exception.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "smtp-user",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddInfrastructure_InMemoryDeliverySection_BindsExactOptions()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(fixture.ConnectionString, CreateConfiguration());

        await using ServiceProvider serviceProvider = services.BuildServiceProvider();
        EmailVerificationDeliveryOptions options = serviceProvider
            .GetRequiredService<IOptions<EmailVerificationDeliveryOptions>>()
            .Value;

        Assert.Equal("https://app.example/verify-email", options.VerificationPageUrl);
        Assert.Equal("ENMA", options.SenderName);
        Assert.Equal("no-reply@example.test", options.SenderAddress);
        Assert.Equal("smtp.example.test", options.SmtpHost);
        Assert.Equal(587, options.SmtpPort);
        Assert.Equal(SecureSocketOptions.StartTls, options.SmtpSecurity);
        Assert.Equal("smtp-user", options.SmtpUsername);
        Assert.Equal("synthetic-smtp-password", options.SmtpPassword);

        EmailVerificationSendBudgetOptions budgetOptions = serviceProvider
            .GetRequiredService<IOptions<EmailVerificationSendBudgetOptions>>()
            .Value;
        Assert.Equal(321, budgetOptions.GlobalHourlyLimit);
        Assert.Equal(9, budgetOptions.DestinationDailyLimit);
    }

    [Fact]
    public async Task AddInfrastructure_EmailDeliveryGraph_UsesSafeLifetimes()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddLogging();
        services.AddInfrastructure(fixture.ConnectionString, CreateConfiguration());

        await using ServiceProvider serviceProvider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
        await using AsyncServiceScope firstScope = serviceProvider.CreateAsyncScope();

        IEmailVerificationDelivery firstDelivery = firstScope.ServiceProvider
            .GetRequiredService<IEmailVerificationDelivery>();
        IEmailVerificationSendBudget firstBudget = firstScope.ServiceProvider
            .GetRequiredService<IEmailVerificationSendBudget>();
        MailKitEmailVerificationDelivery firstSmtp = firstScope.ServiceProvider
            .GetRequiredService<MailKitEmailVerificationDelivery>();
        RequestEmailVerificationUseCase firstUseCase = firstScope.ServiceProvider
            .GetRequiredService<RequestEmailVerificationUseCase>();

        Assert.IsType<BudgetedEmailVerificationDelivery>(firstDelivery);
        Assert.IsType<PostgreSqlEmailVerificationSendBudget>(firstBudget);
        Assert.NotNull(firstSmtp);
        Assert.NotNull(firstUseCase);
        Assert.Same(
            firstDelivery,
            firstScope.ServiceProvider.GetRequiredService<IEmailVerificationDelivery>());
        Assert.Same(
            firstBudget,
            firstScope.ServiceProvider.GetRequiredService<IEmailVerificationSendBudget>());

        await using AsyncServiceScope secondScope = serviceProvider.CreateAsyncScope();

        Assert.NotSame(
            firstDelivery,
            secondScope.ServiceProvider.GetRequiredService<IEmailVerificationDelivery>());
        Assert.NotSame(
            firstBudget,
            secondScope.ServiceProvider.GetRequiredService<IEmailVerificationSendBudget>());
        Assert.Same(
            firstSmtp,
            secondScope.ServiceProvider
                .GetRequiredService<MailKitEmailVerificationDelivery>());
        Assert.NotSame(
            firstUseCase,
            secondScope.ServiceProvider
                .GetRequiredService<RequestEmailVerificationUseCase>());
    }

    private static IConfiguration CreateConfiguration(
        SecureSocketOptions smtpSecurity = SecureSocketOptions.StartTls,
        string smtpPassword = "synthetic-smtp-password",
        int globalHourlyLimit = 321,
        int destinationDailyLimit = 9)
    {
        string section = EmailVerificationDeliveryOptions.SectionName;
        var values = new Dictionary<string, string?>
        {
            [$"{section}:VerificationPageUrl"] =
                "https://app.example/verify-email",
            [$"{section}:SenderName"] = "ENMA",
            [$"{section}:SenderAddress"] = "no-reply@example.test",
            [$"{section}:SmtpHost"] = "smtp.example.test",
            [$"{section}:SmtpPort"] = "587",
            [$"{section}:SmtpSecurity"] = smtpSecurity.ToString(),
            [$"{section}:SmtpUsername"] = "smtp-user",
            [$"{section}:SmtpPassword"] = smtpPassword,
            [$"{EmailVerificationSendBudgetOptions.SectionName}:GlobalHourlyLimit"] =
                globalHourlyLimit.ToString(),
            [$"{EmailVerificationSendBudgetOptions.SectionName}:DestinationDailyLimit"] =
                destinationDailyLimit.ToString()
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
