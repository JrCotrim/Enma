using Enma.Application.Abstractions;
using Enma.Application.Authentication;
using Enma.Application.Onboarding.RegisterOrganizationOwner;
using Enma.Application.Organizations;
using Enma.Application.Organizations.Create;
using Enma.Application.Security;
using Enma.Application.Users;
using Enma.Application.Validation;
using Enma.Domain.Authentication;
using Enma.Domain.Organizations;
using Enma.Domain.Users;

namespace Enma.UnitTests.Application.Onboarding;

public sealed class RegisterOrganizationOwnerHandlerTests
{
    private const string ValidPassword = "Synthetic!Owner42";
    private const string OpaquePasswordHash = "opaque-test-password-hash";
    private const string RawVerificationToken =
        "synthetic-email-verification-token";

    private static readonly DateTimeOffset FixedUtcNow =
        new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_WithValidCommand_ReturnsCreatedIdentifiers()
    {
        var dependencies = new TestDependencies();
        var handler = dependencies.CreateHandler();

        RegisterOrganizationOwnerResult result = await handler.HandleAsync(CreateValidCommand());

        Assert.NotEqual(Guid.Empty, result.OrganizationId);
        Assert.NotEqual(Guid.Empty, result.UserId);
        Assert.NotEqual(Guid.Empty, result.MembershipId);
        Assert.Equal(3, new[]
        {
            result.OrganizationId,
            result.UserId,
            result.MembershipId
        }.Distinct().Count());
        Assert.Equal(dependencies.OrganizationRepository.AddedOrganization?.Id, result.OrganizationId);
        Assert.Equal(dependencies.UserRepository.AddedUser?.Id, result.UserId);
        Assert.Equal(dependencies.MembershipRepository.AddedMembership?.Id, result.MembershipId);
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_ReturnsNormalizedValues()
    {
        var handler = new TestDependencies().CreateHandler();
        var command = new RegisterOrganizationOwnerCommand(
            "  Enma Advocacia  ",
            "  ENMA-ADVOCACIA  ",
            "  Ana Silva  ",
            "  ANA.SILVA@EXAMPLE.COM  ",
            ValidPassword);

        RegisterOrganizationOwnerResult result = await handler.HandleAsync(command);

        Assert.Equal("Enma Advocacia", result.OrganizationName);
        Assert.Equal("enma-advocacia", result.OrganizationSlug);
        Assert.Equal("Ana Silva", result.UserName);
        Assert.Equal("ana.silva@example.com", result.UserEmail);
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_ReturnsOwnerRole()
    {
        var handler = new TestDependencies().CreateHandler();

        RegisterOrganizationOwnerResult result = await handler.HandleAsync(CreateValidCommand());

        Assert.Equal(OrganizationRole.Owner, result.Role);
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_UsesSingleTimestampForAllEntities()
    {
        var dependencies = new TestDependencies();
        var handler = dependencies.CreateHandler();

        RegisterOrganizationOwnerResult result = await handler.HandleAsync(CreateValidCommand());

        Assert.Equal(FixedUtcNow, dependencies.OrganizationRepository.AddedOrganization?.CreatedAt);
        Assert.Equal(FixedUtcNow, dependencies.UserRepository.AddedUser?.CreatedAt);
        Assert.Equal(FixedUtcNow, dependencies.UserCredentialRepository.AddedCredential?.CreatedAt);
        Assert.Equal(
            FixedUtcNow,
            dependencies.UserCredentialRepository.AddedCredential?.PasswordChangedAt);
        Assert.Equal(FixedUtcNow, dependencies.MembershipRepository.AddedMembership?.CreatedAt);
        Assert.Equal(
            FixedUtcNow,
            dependencies.EmailVerificationChallengeRepository.AddedChallenge?.CreatedAt);
        Assert.Equal(
            FixedUtcNow.Add(EmailVerificationPolicy.TokenLifetime),
            dependencies.EmailVerificationChallengeRepository.AddedChallenge?.ExpiresAt);
        Assert.Equal(FixedUtcNow, result.CreatedAt);
        Assert.Equal(1, dependencies.TimeProvider.GetUtcNowCallCount);
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_CreatesMembershipForCreatedOrganizationAndUser()
    {
        var dependencies = new TestDependencies();
        var handler = dependencies.CreateHandler();

        await handler.HandleAsync(CreateValidCommand());

        Organization organization = Assert.IsType<Organization>(
            dependencies.OrganizationRepository.AddedOrganization);
        User user = Assert.IsType<User>(dependencies.UserRepository.AddedUser);
        OrganizationMembership membership = Assert.IsType<OrganizationMembership>(
            dependencies.MembershipRepository.AddedMembership);
        Assert.Equal(organization.Id, membership.OrganizationId);
        Assert.Equal(user.Id, membership.UserId);
        Assert.Equal(OrganizationRole.Owner, membership.Role);
        Assert.True(membership.IsActive);
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_AddsAllEntitiesAndSavesOnce()
    {
        var dependencies = new TestDependencies();
        var handler = dependencies.CreateHandler();

        await handler.HandleAsync(CreateValidCommand());

        Assert.Equal(1, dependencies.OrganizationRepository.AddCallCount);
        Assert.Equal(1, dependencies.UserRepository.AddCallCount);
        Assert.Equal(1, dependencies.UserCredentialRepository.AddCallCount);
        Assert.Equal(1, dependencies.MembershipRepository.AddCallCount);
        Assert.Equal(
            1,
            dependencies.EmailVerificationChallengeRepository.AddCallCount);
        Assert.Equal(1, dependencies.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_PerformsOperationsInRequiredOrder()
    {
        var dependencies = new TestDependencies();
        var handler = dependencies.CreateHandler();

        await handler.HandleAsync(CreateValidCommand());

        Assert.Equal(
            new[]
            {
                "password-policy",
                "organization-exists",
                "user-exists",
                "password-screen",
                "password-hash",
                "verification-token-generate",
                "organization-add",
                "user-add",
                "credential-add",
                "membership-add",
                "challenge-add",
                "save",
                "verification-deliver"
            },
            dependencies.Operations);
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_ForwardsCancellationToken()
    {
        var dependencies = new TestDependencies();
        var handler = dependencies.CreateHandler();
        using var cancellationTokenSource = new CancellationTokenSource();
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        await handler.HandleAsync(CreateValidCommand(), cancellationToken);

        Assert.Equal(cancellationToken, dependencies.OrganizationRepository.ExistsCancellationToken);
        Assert.Equal(cancellationToken, dependencies.UserRepository.ExistsCancellationToken);
        Assert.Equal(cancellationToken, dependencies.CompromisedPasswordChecker.CancellationToken);
        Assert.Equal(cancellationToken, dependencies.OrganizationRepository.AddCancellationToken);
        Assert.Equal(cancellationToken, dependencies.UserRepository.AddCancellationToken);
        Assert.Equal(cancellationToken, dependencies.UserCredentialRepository.AddCancellationToken);
        Assert.Equal(cancellationToken, dependencies.MembershipRepository.AddCancellationToken);
        Assert.Equal(
            cancellationToken,
            dependencies.EmailVerificationChallengeRepository.AddCancellationToken);
        Assert.Equal(cancellationToken, dependencies.UnitOfWork.CancellationToken);
        Assert.Equal(cancellationToken, dependencies.EmailVerificationDelivery.CancellationToken);
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_CreatesChallengeAndDeliversAfterCommit()
    {
        var dependencies = new TestDependencies();
        var handler = dependencies.CreateHandler();

        RegisterOrganizationOwnerResult result = await handler.HandleAsync(
            CreateValidCommand(ownerEmail: "  OWNER@EXAMPLE.COM  "));

        EmailVerificationChallenge challenge = Assert.IsType<
            EmailVerificationChallenge>(
                dependencies.EmailVerificationChallengeRepository.AddedChallenge);
        Assert.Equal(1, dependencies.EmailVerificationTokenService.GenerateCallCount);
        Assert.Equal(result.UserId, challenge.UserId);
        Assert.Equal(result.UserEmail, challenge.EmailAtIssue);
        Assert.Same(dependencies.EmailVerificationTokenService.TokenHash, challenge.TokenHash);
        Assert.Equal(FixedUtcNow, challenge.CreatedAt);
        Assert.Equal(
            EmailVerificationPolicy.TokenLifetime,
            challenge.ExpiresAt - challenge.CreatedAt);
        Assert.Equal(1, dependencies.EmailVerificationDelivery.CallCount);
        Assert.Equal(result.UserEmail, dependencies.EmailVerificationDelivery.Email);
        Assert.Equal(
            RawVerificationToken,
            dependencies.EmailVerificationDelivery.RawToken);
        Assert.True(
            dependencies.Operations.IndexOf("save") <
            dependencies.Operations.IndexOf("verification-deliver"));
    }

    [Fact]
    public async Task HandleAsync_WhenPersistenceFails_DoesNotDeliver()
    {
        var dependencies = new TestDependencies();
        var expectedException = new InvalidOperationException(
            "Synthetic persistence failure.");
        dependencies.UnitOfWork.ExceptionToThrow = expectedException;
        var handler = dependencies.CreateHandler();

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => handler.HandleAsync(CreateValidCommand()));

        Assert.Same(expectedException, exception);
        Assert.Equal(1, dependencies.UnitOfWork.SaveChangesCallCount);
        Assert.Equal(0, dependencies.EmailVerificationDelivery.CallCount);
    }

    [Fact]
    public async Task HandleAsync_WhenDeliveryFails_ReturnsSuccessfulOnboardingResult()
    {
        var dependencies = new TestDependencies();
        dependencies.EmailVerificationDelivery.Result =
            EmailVerificationDeliveryResult.Failed;
        var handler = dependencies.CreateHandler();

        RegisterOrganizationOwnerResult result = await handler.HandleAsync(
            CreateValidCommand());

        Assert.Equal(
            dependencies.OrganizationRepository.AddedOrganization?.Id,
            result.OrganizationId);
        Assert.Equal(1, dependencies.UnitOfWork.SaveChangesCallCount);
        Assert.Equal(1, dependencies.EmailVerificationDelivery.CallCount);
    }

    [Fact]
    public async Task HandleAsync_WhenDeliveryThrows_PropagatesAfterSingleCommit()
    {
        var dependencies = new TestDependencies();
        var expectedException = new InvalidOperationException(
            "Synthetic unexpected delivery failure.");
        dependencies.EmailVerificationDelivery.ExceptionToThrow = expectedException;
        var handler = dependencies.CreateHandler();

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => handler.HandleAsync(CreateValidCommand()));

        Assert.Same(expectedException, exception);
        Assert.Equal(1, dependencies.UnitOfWork.SaveChangesCallCount);
        Assert.Equal(1, dependencies.EmailVerificationDelivery.CallCount);
        Assert.Equal(1, dependencies.EmailVerificationChallengeRepository.AddCallCount);
        Assert.Equal("verification-deliver", dependencies.Operations[^1]);
    }

    [Fact]
    public async Task HandleAsync_WithInvalidOrganizationName_ThrowsBeforeRepositoryAccess()
    {
        var dependencies = new TestDependencies();
        var handler = dependencies.CreateHandler();
        RegisterOrganizationOwnerCommand command = CreateValidCommand(
            organizationName: "   ");

        await Assert.ThrowsAsync<RequestValidationException>(
            () => handler.HandleAsync(command));

        Assert.Empty(dependencies.Operations);
        Assert.Equal(0, dependencies.PasswordPolicy.CallCount);
        Assert.Equal(0, dependencies.CompromisedPasswordChecker.CallCount);
    }

    [Fact]
    public async Task HandleAsync_WithInvalidOrganizationSlug_ThrowsBeforeRepositoryAccess()
    {
        var dependencies = new TestDependencies();
        var handler = dependencies.CreateHandler();
        RegisterOrganizationOwnerCommand command = CreateValidCommand(
            organizationSlug: "enma_advocacia");

        await Assert.ThrowsAsync<RequestValidationException>(
            () => handler.HandleAsync(command));

        Assert.Empty(dependencies.Operations);
        Assert.Equal(0, dependencies.PasswordPolicy.CallCount);
        Assert.Equal(0, dependencies.CompromisedPasswordChecker.CallCount);
    }

    [Fact]
    public async Task HandleAsync_WithInvalidOwnerName_ThrowsBeforeRepositoryAccess()
    {
        var dependencies = new TestDependencies();
        var handler = dependencies.CreateHandler();
        RegisterOrganizationOwnerCommand command = CreateValidCommand(ownerName: "   ");

        await Assert.ThrowsAsync<RequestValidationException>(
            () => handler.HandleAsync(command));

        Assert.Empty(dependencies.Operations);
        Assert.Equal(0, dependencies.PasswordPolicy.CallCount);
        Assert.Equal(0, dependencies.CompromisedPasswordChecker.CallCount);
    }

    [Fact]
    public async Task HandleAsync_WithInvalidOwnerEmail_ThrowsBeforeRepositoryAccess()
    {
        var dependencies = new TestDependencies();
        var handler = dependencies.CreateHandler();
        RegisterOrganizationOwnerCommand command = CreateValidCommand(
            ownerEmail: "invalid email");

        await Assert.ThrowsAsync<RequestValidationException>(
            () => handler.HandleAsync(command));

        Assert.Empty(dependencies.Operations);
        Assert.Equal(0, dependencies.PasswordPolicy.CallCount);
        Assert.Equal(0, dependencies.CompromisedPasswordChecker.CallCount);
    }

    [Fact]
    public async Task HandleAsync_WithExistingOrganizationSlug_ThrowsOrganizationSlugAlreadyExistsException()
    {
        var dependencies = new TestDependencies();
        dependencies.OrganizationRepository.SlugExists = true;
        var handler = dependencies.CreateHandler();
        RegisterOrganizationOwnerCommand command = CreateValidCommand(
            organizationSlug: "  ENMA-ADVOCACIA  ");

        var exception = await Assert.ThrowsAsync<OrganizationSlugAlreadyExistsException>(
            () => handler.HandleAsync(command));

        Assert.Equal("enma-advocacia", exception.Slug);
        Assert.Equal(0, dependencies.UserRepository.ExistsCallCount);
        Assert.Equal(0, dependencies.OrganizationRepository.AddCallCount);
        Assert.Equal(0, dependencies.UserRepository.AddCallCount);
        Assert.Equal(0, dependencies.UserCredentialRepository.AddCallCount);
        Assert.Equal(0, dependencies.MembershipRepository.AddCallCount);
        Assert.Equal(0, dependencies.CompromisedPasswordChecker.CallCount);
        Assert.Equal(0, dependencies.PasswordHasher.CallCount);
        Assert.Equal(0, dependencies.UnitOfWork.SaveChangesCallCount);
        Assert.Equal(
            new[] { "password-policy", "organization-exists" },
            dependencies.Operations);
    }

    [Fact]
    public async Task HandleAsync_WithExistingUserEmail_ThrowsUserEmailAlreadyExistsException()
    {
        var dependencies = new TestDependencies();
        dependencies.UserRepository.EmailExists = true;
        var handler = dependencies.CreateHandler();
        RegisterOrganizationOwnerCommand command = CreateValidCommand(
            ownerEmail: "  OWNER@EXAMPLE.COM  ");

        var exception = await Assert.ThrowsAsync<UserEmailAlreadyExistsException>(
            () => handler.HandleAsync(command));

        Assert.Equal("owner@example.com", exception.Email);
        Assert.Equal("A user with the provided email already exists.", exception.Message);
        Assert.Equal(0, dependencies.OrganizationRepository.AddCallCount);
        Assert.Equal(0, dependencies.UserRepository.AddCallCount);
        Assert.Equal(0, dependencies.UserCredentialRepository.AddCallCount);
        Assert.Equal(0, dependencies.MembershipRepository.AddCallCount);
        Assert.Equal(0, dependencies.CompromisedPasswordChecker.CallCount);
        Assert.Equal(0, dependencies.PasswordHasher.CallCount);
        Assert.Equal(0, dependencies.UnitOfWork.SaveChangesCallCount);
        Assert.Equal(
            new[] { "password-policy", "organization-exists", "user-exists" },
            dependencies.Operations);
    }

    [Fact]
    public async Task HandleAsync_UsesNormalizedValuesForDuplicateChecks()
    {
        var dependencies = new TestDependencies();
        var handler = dependencies.CreateHandler();
        var command = new RegisterOrganizationOwnerCommand(
            "Enma Advocacia",
            "  ENMA-ADVOCACIA  ",
            "Ana Silva",
            "  OWNER@EXAMPLE.COM  ",
            ValidPassword);

        await handler.HandleAsync(command);

        Assert.Equal("enma-advocacia", dependencies.OrganizationRepository.CheckedSlug);
        Assert.Equal("owner@example.com", dependencies.UserRepository.CheckedEmail);
    }

    [Fact]
    public async Task HandleAsync_WithNullCommand_ThrowsArgumentNullException()
    {
        var dependencies = new TestDependencies();
        var handler = dependencies.CreateHandler();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => handler.HandleAsync(null!));

        Assert.Equal("command", exception.ParamName);
        Assert.Empty(dependencies.Operations);
        Assert.Equal(0, dependencies.TimeProvider.GetUtcNowCallCount);
    }

    [Fact]
    public void Constructor_WithNullRepositoryDependency_ThrowsArgumentNullException()
    {
        var dependencies = new TestDependencies();

        var organizationException = Assert.Throws<ArgumentNullException>(
            () => new RegisterOrganizationOwnerHandler(
                null!,
                dependencies.UserRepository,
                dependencies.UserCredentialRepository,
                dependencies.MembershipRepository,
                dependencies.EmailVerificationChallengeRepository,
                dependencies.PasswordPolicy,
                dependencies.CompromisedPasswordChecker,
                dependencies.PasswordHasher,
                dependencies.EmailVerificationTokenService,
                dependencies.EmailVerificationDelivery,
                dependencies.UnitOfWork,
                dependencies.TimeProvider));
        var userException = Assert.Throws<ArgumentNullException>(
            () => new RegisterOrganizationOwnerHandler(
                dependencies.OrganizationRepository,
                null!,
                dependencies.UserCredentialRepository,
                dependencies.MembershipRepository,
                dependencies.EmailVerificationChallengeRepository,
                dependencies.PasswordPolicy,
                dependencies.CompromisedPasswordChecker,
                dependencies.PasswordHasher,
                dependencies.EmailVerificationTokenService,
                dependencies.EmailVerificationDelivery,
                dependencies.UnitOfWork,
                dependencies.TimeProvider));
        var userCredentialException = Assert.Throws<ArgumentNullException>(
            () => new RegisterOrganizationOwnerHandler(
                dependencies.OrganizationRepository,
                dependencies.UserRepository,
                null!,
                dependencies.MembershipRepository,
                dependencies.EmailVerificationChallengeRepository,
                dependencies.PasswordPolicy,
                dependencies.CompromisedPasswordChecker,
                dependencies.PasswordHasher,
                dependencies.EmailVerificationTokenService,
                dependencies.EmailVerificationDelivery,
                dependencies.UnitOfWork,
                dependencies.TimeProvider));
        var membershipException = Assert.Throws<ArgumentNullException>(
            () => new RegisterOrganizationOwnerHandler(
                dependencies.OrganizationRepository,
                dependencies.UserRepository,
                dependencies.UserCredentialRepository,
                null!,
                dependencies.EmailVerificationChallengeRepository,
                dependencies.PasswordPolicy,
                dependencies.CompromisedPasswordChecker,
                dependencies.PasswordHasher,
                dependencies.EmailVerificationTokenService,
                dependencies.EmailVerificationDelivery,
                dependencies.UnitOfWork,
                dependencies.TimeProvider));
        var challengeException = Assert.Throws<ArgumentNullException>(
            () => new RegisterOrganizationOwnerHandler(
                dependencies.OrganizationRepository,
                dependencies.UserRepository,
                dependencies.UserCredentialRepository,
                dependencies.MembershipRepository,
                null!,
                dependencies.PasswordPolicy,
                dependencies.CompromisedPasswordChecker,
                dependencies.PasswordHasher,
                dependencies.EmailVerificationTokenService,
                dependencies.EmailVerificationDelivery,
                dependencies.UnitOfWork,
                dependencies.TimeProvider));

        Assert.Equal("organizationRepository", organizationException.ParamName);
        Assert.Equal("userRepository", userException.ParamName);
        Assert.Equal("userCredentialRepository", userCredentialException.ParamName);
        Assert.Equal("membershipRepository", membershipException.ParamName);
        Assert.Equal(
            "emailVerificationChallengeRepository",
            challengeException.ParamName);
    }

    [Fact]
    public void Constructor_WithNullServiceDependency_ThrowsArgumentNullException()
    {
        var dependencies = new TestDependencies();

        var unitOfWorkException = Assert.Throws<ArgumentNullException>(
            () => new RegisterOrganizationOwnerHandler(
                dependencies.OrganizationRepository,
                dependencies.UserRepository,
                dependencies.UserCredentialRepository,
                dependencies.MembershipRepository,
                dependencies.EmailVerificationChallengeRepository,
                dependencies.PasswordPolicy,
                dependencies.CompromisedPasswordChecker,
                dependencies.PasswordHasher,
                dependencies.EmailVerificationTokenService,
                dependencies.EmailVerificationDelivery,
                null!,
                dependencies.TimeProvider));
        var passwordPolicyException = Assert.Throws<ArgumentNullException>(
            () => new RegisterOrganizationOwnerHandler(
                dependencies.OrganizationRepository,
                dependencies.UserRepository,
                dependencies.UserCredentialRepository,
                dependencies.MembershipRepository,
                dependencies.EmailVerificationChallengeRepository,
                null!,
                dependencies.CompromisedPasswordChecker,
                dependencies.PasswordHasher,
                dependencies.EmailVerificationTokenService,
                dependencies.EmailVerificationDelivery,
                dependencies.UnitOfWork,
                dependencies.TimeProvider));
        var compromisedPasswordCheckerException = Assert.Throws<ArgumentNullException>(
            () => new RegisterOrganizationOwnerHandler(
                dependencies.OrganizationRepository,
                dependencies.UserRepository,
                dependencies.UserCredentialRepository,
                dependencies.MembershipRepository,
                dependencies.EmailVerificationChallengeRepository,
                dependencies.PasswordPolicy,
                null!,
                dependencies.PasswordHasher,
                dependencies.EmailVerificationTokenService,
                dependencies.EmailVerificationDelivery,
                dependencies.UnitOfWork,
                dependencies.TimeProvider));
        var passwordHasherException = Assert.Throws<ArgumentNullException>(
            () => new RegisterOrganizationOwnerHandler(
                dependencies.OrganizationRepository,
                dependencies.UserRepository,
                dependencies.UserCredentialRepository,
                dependencies.MembershipRepository,
                dependencies.EmailVerificationChallengeRepository,
                dependencies.PasswordPolicy,
                dependencies.CompromisedPasswordChecker,
                null!,
                dependencies.EmailVerificationTokenService,
                dependencies.EmailVerificationDelivery,
                dependencies.UnitOfWork,
                dependencies.TimeProvider));
        var tokenServiceException = Assert.Throws<ArgumentNullException>(
            () => new RegisterOrganizationOwnerHandler(
                dependencies.OrganizationRepository,
                dependencies.UserRepository,
                dependencies.UserCredentialRepository,
                dependencies.MembershipRepository,
                dependencies.EmailVerificationChallengeRepository,
                dependencies.PasswordPolicy,
                dependencies.CompromisedPasswordChecker,
                dependencies.PasswordHasher,
                null!,
                dependencies.EmailVerificationDelivery,
                dependencies.UnitOfWork,
                dependencies.TimeProvider));
        var deliveryException = Assert.Throws<ArgumentNullException>(
            () => new RegisterOrganizationOwnerHandler(
                dependencies.OrganizationRepository,
                dependencies.UserRepository,
                dependencies.UserCredentialRepository,
                dependencies.MembershipRepository,
                dependencies.EmailVerificationChallengeRepository,
                dependencies.PasswordPolicy,
                dependencies.CompromisedPasswordChecker,
                dependencies.PasswordHasher,
                dependencies.EmailVerificationTokenService,
                null!,
                dependencies.UnitOfWork,
                dependencies.TimeProvider));
        var timeProviderException = Assert.Throws<ArgumentNullException>(
            () => new RegisterOrganizationOwnerHandler(
                dependencies.OrganizationRepository,
                dependencies.UserRepository,
                dependencies.UserCredentialRepository,
                dependencies.MembershipRepository,
                dependencies.EmailVerificationChallengeRepository,
                dependencies.PasswordPolicy,
                dependencies.CompromisedPasswordChecker,
                dependencies.PasswordHasher,
                dependencies.EmailVerificationTokenService,
                dependencies.EmailVerificationDelivery,
                dependencies.UnitOfWork,
                null!));

        Assert.Equal("passwordPolicy", passwordPolicyException.ParamName);
        Assert.Equal(
            "compromisedPasswordChecker",
            compromisedPasswordCheckerException.ParamName);
        Assert.Equal("passwordHasher", passwordHasherException.ParamName);
        Assert.Equal(
            "emailVerificationTokenService",
            tokenServiceException.ParamName);
        Assert.Equal("emailVerificationDelivery", deliveryException.ParamName);
        Assert.Equal("unitOfWork", unitOfWorkException.ParamName);
        Assert.Equal("timeProvider", timeProviderException.ParamName);
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_ValidatesScreensAndHashesPasswordOnce()
    {
        var dependencies = new TestDependencies();
        var handler = dependencies.CreateHandler();

        await handler.HandleAsync(CreateValidCommand());

        Assert.Equal(1, dependencies.PasswordPolicy.CallCount);
        Assert.True(dependencies.PasswordPolicy.ReceivedExpectedPassword);
        Assert.Equal(1, dependencies.CompromisedPasswordChecker.CallCount);
        Assert.True(dependencies.CompromisedPasswordChecker.ReceivedExpectedPassword);
        Assert.Equal(1, dependencies.PasswordHasher.CallCount);
        Assert.True(dependencies.PasswordHasher.ReceivedExpectedPassword);
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_CreatesCredentialForCreatedUser()
    {
        var dependencies = new TestDependencies();
        var handler = dependencies.CreateHandler();

        await handler.HandleAsync(CreateValidCommand());

        User user = Assert.IsType<User>(dependencies.UserRepository.AddedUser);
        UserCredential credential = Assert.IsType<UserCredential>(
            dependencies.UserCredentialRepository.AddedCredential);
        Assert.Equal(1, dependencies.UserCredentialRepository.AddCallCount);
        Assert.Equal(user.Id, credential.UserId);
        Assert.Equal(OpaquePasswordHash, credential.PasswordHash);
        Assert.Equal(FixedUtcNow, credential.CreatedAt);
        Assert.Equal(FixedUtcNow, credential.PasswordChangedAt);
        Assert.NotEqual(ValidPassword, credential.PasswordHash);
    }

    [Fact]
    public async Task HandleAsync_WithInvalidPassword_ThrowsBeforeRepositoryAccessOrHashing()
    {
        var dependencies = new TestDependencies();
        var expectedException = new ArgumentException(
            PasswordPolicyErrors.PasswordTooShort,
            "password");
        dependencies.PasswordPolicy.ExceptionToThrow = expectedException;
        var handler = dependencies.CreateHandler();

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => handler.HandleAsync(CreateValidCommand()));

        Assert.Equal(expectedException.Message, exception.Message);
        Assert.Same(expectedException, exception.InnerException);
        Assert.Equal(1, dependencies.TimeProvider.GetUtcNowCallCount);
        Assert.Equal(1, dependencies.PasswordPolicy.CallCount);
        Assert.Equal(0, dependencies.OrganizationRepository.ExistsCallCount);
        Assert.Equal(0, dependencies.UserRepository.ExistsCallCount);
        Assert.Equal(0, dependencies.CompromisedPasswordChecker.CallCount);
        Assert.Equal(0, dependencies.PasswordHasher.CallCount);
        Assert.Equal(0, dependencies.OrganizationRepository.AddCallCount);
        Assert.Equal(0, dependencies.UserRepository.AddCallCount);
        Assert.Equal(0, dependencies.UserCredentialRepository.AddCallCount);
        Assert.Equal(0, dependencies.MembershipRepository.AddCallCount);
        Assert.Equal(0, dependencies.UnitOfWork.SaveChangesCallCount);
        Assert.Equal(new[] { "password-policy" }, dependencies.Operations);
    }

    [Fact]
    public async Task HandleAsync_WithCompromisedPassword_ThrowsBeforeHashingOrPersistence()
    {
        var dependencies = new TestDependencies();
        dependencies.CompromisedPasswordChecker.IsCompromised = true;
        var handler = dependencies.CreateHandler();

        CompromisedPasswordException exception =
            await Assert.ThrowsAsync<CompromisedPasswordException>(
                () => handler.HandleAsync(CreateValidCommand()));

        Assert.Equal(
            "The provided password has appeared in a known data breach and cannot be used.",
            exception.Message);
        Assert.DoesNotContain(ValidPassword, exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, dependencies.PasswordPolicy.CallCount);
        Assert.Equal(1, dependencies.OrganizationRepository.ExistsCallCount);
        Assert.Equal(1, dependencies.UserRepository.ExistsCallCount);
        Assert.Equal(1, dependencies.CompromisedPasswordChecker.CallCount);
        Assert.Equal(0, dependencies.PasswordHasher.CallCount);
        Assert.Equal(0, dependencies.OrganizationRepository.AddCallCount);
        Assert.Equal(0, dependencies.UserRepository.AddCallCount);
        Assert.Equal(0, dependencies.UserCredentialRepository.AddCallCount);
        Assert.Equal(0, dependencies.MembershipRepository.AddCallCount);
        Assert.Equal(0, dependencies.UnitOfWork.SaveChangesCallCount);
        Assert.Equal(
            new[]
            {
                "password-policy",
                "organization-exists",
                "user-exists",
                "password-screen"
            },
            dependencies.Operations);
    }

    [Fact]
    public async Task HandleAsync_WhenCompromisedPasswordCheckIsUnavailable_PropagatesAndDoesNotWrite()
    {
        var dependencies = new TestDependencies();
        var expectedException = new CompromisedPasswordCheckUnavailableException();
        dependencies.CompromisedPasswordChecker.ExceptionToThrow = expectedException;
        var handler = dependencies.CreateHandler();

        CompromisedPasswordCheckUnavailableException exception =
            await Assert.ThrowsAsync<CompromisedPasswordCheckUnavailableException>(
                () => handler.HandleAsync(CreateValidCommand()));

        Assert.Same(expectedException, exception);
        Assert.Equal(1, dependencies.CompromisedPasswordChecker.CallCount);
        Assert.Equal(0, dependencies.PasswordHasher.CallCount);
        Assert.Equal(0, dependencies.OrganizationRepository.AddCallCount);
        Assert.Equal(0, dependencies.UserRepository.AddCallCount);
        Assert.Equal(0, dependencies.UserCredentialRepository.AddCallCount);
        Assert.Equal(0, dependencies.MembershipRepository.AddCallCount);
        Assert.Equal(0, dependencies.UnitOfWork.SaveChangesCallCount);
        Assert.Equal(
            new[]
            {
                "password-policy",
                "organization-exists",
                "user-exists",
                "password-screen"
            },
            dependencies.Operations);
    }

    [Fact]
    public void Command_ToString_DoesNotExposePassword()
    {
        const string distinctivePassword = "Distinctive!Owner42";
        var command = new RegisterOrganizationOwnerCommand(
            "Distinctive Organization",
            "distinctive-organization",
            "Distinctive Owner",
            "distinctive.owner@example.com",
            distinctivePassword);

        string commandText = Assert.IsType<string>(command.ToString());

        Assert.DoesNotContain(distinctivePassword, commandText, StringComparison.Ordinal);
        Assert.DoesNotContain(command.OwnerEmail, commandText, StringComparison.Ordinal);
        Assert.DoesNotContain(command.OrganizationName, commandText, StringComparison.Ordinal);
        Assert.Equal(typeof(RegisterOrganizationOwnerCommand).FullName, commandText);
    }

    private static RegisterOrganizationOwnerCommand CreateValidCommand(
        string organizationName = "Enma Advocacia",
        string organizationSlug = "enma-advocacia",
        string ownerName = "Ana Silva",
        string ownerEmail = "owner@example.com",
        string password = ValidPassword)
    {
        return new RegisterOrganizationOwnerCommand(
            organizationName,
            organizationSlug,
            ownerName,
            ownerEmail,
            password);
    }

    private sealed class TestDependencies
    {
        public TestDependencies()
        {
            OrganizationRepository = new FakeOrganizationRepository(Operations);
            UserRepository = new FakeUserRepository(Operations);
            UserCredentialRepository = new FakeUserCredentialRepository(Operations);
            MembershipRepository = new FakeOrganizationMembershipRepository(Operations);
            EmailVerificationChallengeRepository =
                new FakeEmailVerificationChallengeRepository(Operations);
            PasswordPolicy = new FakePasswordPolicy(Operations);
            CompromisedPasswordChecker = new FakeCompromisedPasswordChecker(Operations);
            PasswordHasher = new FakePasswordHasher(Operations);
            EmailVerificationTokenService =
                new FakeEmailVerificationTokenService(Operations);
            EmailVerificationDelivery = new FakeEmailVerificationDelivery(Operations);
            UnitOfWork = new FakeUnitOfWork(Operations);
            TimeProvider = new FixedTimeProvider(FixedUtcNow);
        }

        public List<string> Operations { get; } = [];

        public FakeOrganizationRepository OrganizationRepository { get; }

        public FakeUserRepository UserRepository { get; }

        public FakeUserCredentialRepository UserCredentialRepository { get; }

        public FakeOrganizationMembershipRepository MembershipRepository { get; }

        public FakeEmailVerificationChallengeRepository
            EmailVerificationChallengeRepository { get; }

        public FakePasswordPolicy PasswordPolicy { get; }

        public FakeCompromisedPasswordChecker CompromisedPasswordChecker { get; }

        public FakePasswordHasher PasswordHasher { get; }

        public FakeEmailVerificationTokenService EmailVerificationTokenService
        { get; }

        public FakeEmailVerificationDelivery EmailVerificationDelivery { get; }

        public FakeUnitOfWork UnitOfWork { get; }

        public FixedTimeProvider TimeProvider { get; }

        public RegisterOrganizationOwnerHandler CreateHandler()
        {
            return new RegisterOrganizationOwnerHandler(
                OrganizationRepository,
                UserRepository,
                UserCredentialRepository,
                MembershipRepository,
                EmailVerificationChallengeRepository,
                PasswordPolicy,
                CompromisedPasswordChecker,
                PasswordHasher,
                EmailVerificationTokenService,
                EmailVerificationDelivery,
                UnitOfWork,
                TimeProvider);
        }
    }

    private sealed class FakeEmailVerificationChallengeRepository(
        List<string> operations) : IEmailVerificationChallengeRepository
    {
        public EmailVerificationChallenge? AddedChallenge { get; private set; }

        public int AddCallCount { get; private set; }

        public CancellationToken AddCancellationToken { get; private set; }

        public Task AddAsync(
            EmailVerificationChallenge challenge,
            CancellationToken cancellationToken = default)
        {
            operations.Add("challenge-add");
            AddCallCount++;
            AddedChallenge = challenge;
            AddCancellationToken = cancellationToken;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEmailVerificationTokenService(List<string> operations)
        : IEmailVerificationTokenService
    {
        public EmailVerificationTokenHash TokenHash { get; } =
            new(CreateHashBytes(17));

        public int GenerateCallCount { get; private set; }

        public string GenerateToken(out EmailVerificationTokenHash tokenHash)
        {
            operations.Add("verification-token-generate");
            GenerateCallCount++;
            tokenHash = TokenHash;
            return RawVerificationToken;
        }

        public bool TryHashToken(
            string? rawToken,
            out EmailVerificationTokenHash? tokenHash)
        {
            throw new InvalidOperationException(
                "TryHashToken must not be called by onboarding tests.");
        }
    }

    private sealed class FakeEmailVerificationDelivery(List<string> operations)
        : IEmailVerificationDelivery
    {
        public EmailVerificationDeliveryResult Result { get; set; } =
            EmailVerificationDeliveryResult.Delivered;

        public Exception? ExceptionToThrow { get; set; }

        public int CallCount { get; private set; }

        public string? Email { get; private set; }

        public string? RawToken { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<EmailVerificationDeliveryResult> DeliverAsync(
            string email,
            string rawToken,
            CancellationToken cancellationToken = default)
        {
            operations.Add("verification-deliver");
            CallCount++;
            Email = email;
            RawToken = rawToken;
            CancellationToken = cancellationToken;

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult(Result);
        }
    }

    private sealed class FakeUserCredentialRepository(List<string> operations)
        : IUserCredentialRepository
    {
        public UserCredential? AddedCredential { get; private set; }

        public int AddCallCount { get; private set; }

        public CancellationToken AddCancellationToken { get; private set; }

        public Task<UserCredential?> GetByUserIdAsync(
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "GetByUserIdAsync must not be called by onboarding tests.");
        }

        public Task AddAsync(
            UserCredential credential,
            CancellationToken cancellationToken = default)
        {
            operations.Add("credential-add");
            AddCallCount++;
            AddedCredential = credential;
            AddCancellationToken = cancellationToken;

            return Task.CompletedTask;
        }
    }

    private sealed class FakePasswordPolicy(List<string> operations) : IPasswordPolicy
    {
        public int CallCount { get; private set; }

        public bool ReceivedExpectedPassword { get; private set; }

        public Exception? ExceptionToThrow { get; set; }

        public void Validate(string password)
        {
            operations.Add("password-policy");
            CallCount++;
            ReceivedExpectedPassword = password == ValidPassword;
            Assert.True(ReceivedExpectedPassword);

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }
        }
    }

    private sealed class FakeCompromisedPasswordChecker(List<string> operations)
        : ICompromisedPasswordChecker
    {
        public int CallCount { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public bool ReceivedExpectedPassword { get; private set; }

        public bool IsCompromised { get; set; }

        public Exception? ExceptionToThrow { get; set; }

        public Task<bool> IsCompromisedAsync(
            string password,
            CancellationToken cancellationToken = default)
        {
            operations.Add("password-screen");
            CallCount++;
            CancellationToken = cancellationToken;
            ReceivedExpectedPassword = password == ValidPassword;
            Assert.True(ReceivedExpectedPassword);

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult(IsCompromised);
        }
    }

    private sealed class FakePasswordHasher(List<string> operations) : IPasswordHasher
    {
        public int CallCount { get; private set; }

        public bool ReceivedExpectedPassword { get; private set; }

        public string HashPassword(string password)
        {
            operations.Add("password-hash");
            CallCount++;
            ReceivedExpectedPassword = password == ValidPassword;
            Assert.True(ReceivedExpectedPassword);

            return OpaquePasswordHash;
        }

        public PasswordVerificationResult VerifyHashedPassword(
            string passwordHash,
            string providedPassword)
        {
            throw new InvalidOperationException(
                "VerifyHashedPassword must not be called by onboarding tests.");
        }
    }

    private sealed class FakeOrganizationRepository(List<string> operations)
        : IOrganizationRepository
    {
        public bool SlugExists { get; set; }

        public string? CheckedSlug { get; private set; }

        public Organization? AddedOrganization { get; private set; }

        public int ExistsCallCount { get; private set; }

        public int AddCallCount { get; private set; }

        public CancellationToken ExistsCancellationToken { get; private set; }

        public CancellationToken AddCancellationToken { get; private set; }

        public Task<Organization?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "GetByIdAsync must not be called by onboarding tests.");
        }

        public Task<bool> ExistsBySlugAsync(
            string slug,
            CancellationToken cancellationToken = default)
        {
            operations.Add("organization-exists");
            ExistsCallCount++;
            CheckedSlug = slug;
            ExistsCancellationToken = cancellationToken;

            return Task.FromResult(SlugExists);
        }

        public Task AddAsync(
            Organization organization,
            CancellationToken cancellationToken = default)
        {
            operations.Add("organization-add");
            AddCallCount++;
            AddedOrganization = organization;
            AddCancellationToken = cancellationToken;

            return Task.CompletedTask;
        }
    }

    private sealed class FakeUserRepository(List<string> operations) : IUserRepository
    {
        public bool EmailExists { get; set; }

        public string? CheckedEmail { get; private set; }

        public User? AddedUser { get; private set; }

        public int ExistsCallCount { get; private set; }

        public int AddCallCount { get; private set; }

        public CancellationToken ExistsCancellationToken { get; private set; }

        public CancellationToken AddCancellationToken { get; private set; }

        public Task<bool> ExistsByEmailAsync(
            string email,
            CancellationToken cancellationToken = default)
        {
            operations.Add("user-exists");
            ExistsCallCount++;
            CheckedEmail = email;
            ExistsCancellationToken = cancellationToken;

            return Task.FromResult(EmailExists);
        }

        public Task AddAsync(User user, CancellationToken cancellationToken = default)
        {
            operations.Add("user-add");
            AddCallCount++;
            AddedUser = user;
            AddCancellationToken = cancellationToken;

            return Task.CompletedTask;
        }
    }

    private sealed class FakeOrganizationMembershipRepository(List<string> operations)
        : IOrganizationMembershipRepository
    {
        public OrganizationMembership? AddedMembership { get; private set; }

        public int AddCallCount { get; private set; }

        public CancellationToken AddCancellationToken { get; private set; }

        public Task AddAsync(
            OrganizationMembership membership,
            CancellationToken cancellationToken = default)
        {
            operations.Add("membership-add");
            AddCallCount++;
            AddedMembership = membership;
            AddCancellationToken = cancellationToken;

            return Task.CompletedTask;
        }
    }

    private sealed class FakeUnitOfWork(List<string> operations) : IUnitOfWork
    {
        public int SaveChangesCallCount { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Exception? ExceptionToThrow { get; set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            operations.Add("save");
            SaveChangesCallCount++;
            CancellationToken = cancellationToken;

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult(5);
        }
    }

    private static byte[] CreateHashBytes(byte seed)
    {
        return Enumerable.Range(0, 32)
            .Select(index => (byte)(seed + index))
            .ToArray();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public int GetUtcNowCallCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            GetUtcNowCallCount++;

            return utcNow;
        }
    }
}
