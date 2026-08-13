using Enma.Application.Authorization;
using Enma.Application.Processes;
using Enma.Application.Processes.Update;
using Enma.Application.Validation;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;

namespace Enma.UnitTests.Application.Processes.Update;

public sealed class UpdateLegalProcessUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "64ee6b20-ad4e-4af8-9887-9c5161364dda");

    private static readonly Guid OrganizationId = Guid.Parse(
        "51c3f69f-1aef-44b7-9516-9ea838f5d4e3");

    private static readonly Guid ProcessId = Guid.Parse(
        "eb80340a-ef78-443d-9244-12ab19f9a92d");

    private static readonly Guid ClientId = Guid.Parse(
        "a8237b0f-19c2-4a6b-a46d-dd5789ac51fc");

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        13,
        18,
        0,
        0,
        TimeSpan.Zero);

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task ExecuteAsync_WithAuthorizedRole_UpdatesOnlyContextualTitle(
        OrganizationRole role)
    {
        var persistence = new FakeLegalProcessMutationPersistence();
        UpdateLegalProcessUseCase useCase = CreateUseCase(role, persistence);

        UpdateLegalProcessResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ProcessId,
            "  Novo título  ");

        Assert.Equal(UpdateLegalProcessResultStatus.Updated, result.Status);
        Assert.Equal(ProcessId, persistence.ProcessId);
        Assert.Equal(OrganizationId, persistence.OrganizationId);
        Assert.Equal("Novo título", persistence.LegalProcess.Title);
        Assert.Equal(OrganizationId, persistence.LegalProcess.OrganizationId);
        Assert.Equal(ClientId, persistence.LegalProcess.ClientId);
        Assert.Equal(CreatedAt, persistence.LegalProcess.CreatedAt);
    }

    [Fact]
    public async Task ExecuteAsync_WithMemberRole_DeniesWithoutPersistence()
    {
        var persistence = new FakeLegalProcessMutationPersistence();
        UpdateLegalProcessUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            persistence);

        UpdateLegalProcessResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ProcessId,
            "   ");

        Assert.Equal(UpdateLegalProcessResultStatus.AccessDenied, result.Status);
        Assert.Equal(0, persistence.UpdateCallCount);
        Assert.Equal("Initial title", persistence.LegalProcess.Title);
    }

    [Fact]
    public async Task ExecuteAsync_WithDeniedOrganizationAccess_DeniesWithoutPersistence()
    {
        var persistence = new FakeLegalProcessMutationPersistence();
        UpdateLegalProcessUseCase useCase = CreateUseCase(
            (OrganizationRole?)null,
            persistence);

        UpdateLegalProcessResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ProcessId,
            "Updated title");

        Assert.Equal(UpdateLegalProcessResultStatus.AccessDenied, result.Status);
        Assert.Equal(0, persistence.UpdateCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithAuthorizedMissingProcess_ReturnsNotFound()
    {
        var persistence = new FakeLegalProcessMutationPersistence(
            LegalProcessMutationPersistenceResult.NotFound);
        UpdateLegalProcessUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        UpdateLegalProcessResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ProcessId,
            "Updated title");

        Assert.Same(UpdateLegalProcessResult.NotFound, result);
        Assert.Equal(1, persistence.UpdateCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithCrossTenantShape_ReturnsSameNotFoundContract()
    {
        var persistence = new FakeLegalProcessMutationPersistence(
            LegalProcessMutationPersistenceResult.NotFound);
        UpdateLegalProcessUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            persistence);

        UpdateLegalProcessResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ProcessId,
            "Updated title");

        Assert.Same(UpdateLegalProcessResult.NotFound, result);
        Assert.Equal(OrganizationId, persistence.OrganizationId);
        Assert.Equal(ProcessId, persistence.ProcessId);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyProcessId_ReturnsNotFoundWithoutPersistence()
    {
        var persistence = new FakeLegalProcessMutationPersistence();
        UpdateLegalProcessUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        UpdateLegalProcessResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            Guid.Empty,
            "Updated title");

        Assert.Same(UpdateLegalProcessResult.NotFound, result);
        Assert.Equal(0, persistence.UpdateCallCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_WithInvalidTitle_TranslatesDomainValidation(
        string title)
    {
        var persistence = new FakeLegalProcessMutationPersistence();
        UpdateLegalProcessUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(() =>
                useCase.ExecuteAsync(
                    UserId,
                    OrganizationId,
                    ProcessId,
                    title));

        Assert.Contains(LegalProcessErrors.TitleRequired, exception.Message);
        Assert.Equal(1, persistence.UpdateCallCount);
        Assert.Equal("Initial title", persistence.LegalProcess.Title);
    }

    [Fact]
    public async Task ExecuteAsync_WithTitleBeyondMaximum_TranslatesDomainValidation()
    {
        var persistence = new FakeLegalProcessMutationPersistence();
        UpdateLegalProcessUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            persistence);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(() =>
                useCase.ExecuteAsync(
                    UserId,
                    OrganizationId,
                    ProcessId,
                    new string('a', 151)));

        Assert.Contains(LegalProcessErrors.TitleTooLong, exception.Message);
        Assert.Equal("Initial title", persistence.LegalProcess.Title);
    }

    [Fact]
    public async Task ExecuteAsync_WithTitleAtMaximum_UpdatesTitle()
    {
        var persistence = new FakeLegalProcessMutationPersistence();
        UpdateLegalProcessUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);
        string title = new('a', 150);

        UpdateLegalProcessResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ProcessId,
            title);

        Assert.Equal(UpdateLegalProcessResultStatus.Updated, result.Status);
        Assert.Equal(title, persistence.LegalProcess.Title);
    }

    [Fact]
    public async Task ExecuteAsync_WithOwnerElsewhereAndMemberInContext_DeniesContextualUpdate()
    {
        Guid otherOrganizationId = Guid.Parse(
            "b691e216-522d-4d02-ab8f-2df2be14b647");
        var lookup = new ContextualOrganizationAccessLookup(
            OrganizationId,
            OrganizationRole.Member,
            otherOrganizationId,
            OrganizationRole.Owner);
        var persistence = new FakeLegalProcessMutationPersistence();
        UpdateLegalProcessUseCase useCase = CreateUseCase(lookup, persistence);

        UpdateLegalProcessResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ProcessId,
            "Updated title");

        Assert.Equal(UpdateLegalProcessResultStatus.AccessDenied, result.Status);
        Assert.Equal(0, persistence.UpdateCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithContext_ForwardsCancellationAndTenantInputs()
    {
        var lookup = new ContextualOrganizationAccessLookup(
            OrganizationId,
            OrganizationRole.Owner);
        var persistence = new FakeLegalProcessMutationPersistence();
        UpdateLegalProcessUseCase useCase = CreateUseCase(lookup, persistence);
        using var cancellationTokenSource = new CancellationTokenSource();

        await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ProcessId,
            "Updated title",
            cancellationTokenSource.Token);

        Assert.Equal(cancellationTokenSource.Token, lookup.CancellationToken);
        Assert.Equal(cancellationTokenSource.Token, persistence.CancellationToken);
        Assert.Equal(OrganizationId, persistence.OrganizationId);
        Assert.Equal(ProcessId, persistence.ProcessId);
    }

    private static UpdateLegalProcessUseCase CreateUseCase(
        OrganizationRole? role,
        FakeLegalProcessMutationPersistence persistence)
    {
        return CreateUseCase(
            new ContextualOrganizationAccessLookup(OrganizationId, role),
            persistence);
    }

    private static UpdateLegalProcessUseCase CreateUseCase(
        IOrganizationAccessLookup lookup,
        FakeLegalProcessMutationPersistence persistence)
    {
        return new UpdateLegalProcessUseCase(
            new ProcessActionAuthorization(
                new OrganizationAccessAuthorization(lookup)),
            persistence);
    }

    private sealed class ContextualOrganizationAccessLookup(
        Guid firstOrganizationId,
        OrganizationRole? firstRole,
        Guid? secondOrganizationId = null,
        OrganizationRole? secondRole = null) : IOrganizationAccessLookup
    {
        public CancellationToken CancellationToken { get; private set; }

        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CancellationToken = cancellationToken;

            OrganizationRole? role = organizationId == firstOrganizationId
                ? firstRole
                : organizationId == secondOrganizationId
                    ? secondRole
                    : null;

            return Task.FromResult(role);
        }
    }

    private sealed class FakeLegalProcessMutationPersistence(
        LegalProcessMutationPersistenceResult result =
            LegalProcessMutationPersistenceResult.Updated)
        : ILegalProcessMutationPersistence
    {
        public LegalProcess LegalProcess { get; } = new(
            UpdateLegalProcessUseCaseTests.OrganizationId,
            UpdateLegalProcessUseCaseTests.ClientId,
            "Initial title",
            UpdateLegalProcessUseCaseTests.CreatedAt);

        public int UpdateCallCount { get; private set; }

        public Guid ProcessId { get; private set; }

        public Guid OrganizationId { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<LegalProcessMutationPersistenceResult> UpdateTitleAsync(
            Guid processId,
            Guid organizationId,
            string title,
            CancellationToken cancellationToken = default)
        {
            UpdateCallCount++;
            ProcessId = processId;
            OrganizationId = organizationId;
            CancellationToken = cancellationToken;

            if (result == LegalProcessMutationPersistenceResult.Updated)
            {
                LegalProcess.ChangeTitle(title);
            }

            return Task.FromResult(result);
        }
    }
}
