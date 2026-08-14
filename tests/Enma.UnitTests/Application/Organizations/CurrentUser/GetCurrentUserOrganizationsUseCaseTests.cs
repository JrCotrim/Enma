using System.Reflection;
using Enma.Application.Organizations.CurrentUser;
using Enma.Application.Validation;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Organizations.CurrentUser;

public sealed class GetCurrentUserOrganizationsUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "a8c77fb4-0997-443f-bbd5-d22749f9c4da");

    [Fact]
    public async Task ExecuteAsync_WithAuthenticatedUser_QueriesOnlySuppliedUser()
    {
        CurrentUserOrganizationReadModel[] expected =
        [
            new(
                Guid.Parse("8f511878-39b1-4130-a41a-60767a75951f"),
                "Enma Legal",
                OrganizationRole.Administrator,
                Guid.Parse("23a8c178-12e7-46fd-aac7-a8172dfbda7d"))
        ];
        var queries = new FakeCurrentUserOrganizationQueries(expected);
        var useCase = new GetCurrentUserOrganizationsUseCase(queries);
        using var cancellationTokenSource = new CancellationTokenSource();

        IReadOnlyList<CurrentUserOrganizationReadModel> result =
            await useCase.ExecuteAsync(
                UserId,
                cancellationTokenSource.Token);

        Assert.Equal(expected, result);
        Assert.Equal(UserId, queries.UserId);
        Assert.Equal(cancellationTokenSource.Token, queries.CancellationToken);
        Assert.Equal(1, queries.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoAccessibleOrganizations_ReturnsEmptyCollection()
    {
        var queries = new FakeCurrentUserOrganizationQueries([]);
        var useCase = new GetCurrentUserOrganizationsUseCase(queries);

        IReadOnlyList<CurrentUserOrganizationReadModel> result =
            await useCase.ExecuteAsync(UserId);

        Assert.Empty(result);
        Assert.Equal(1, queries.CallCount);
    }

    [Fact]
    public void ReadModel_CurrentScope_ContainsOnlyNavigationFields()
    {
        string[] propertyNames = typeof(CurrentUserOrganizationReadModel)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(
            [
                nameof(CurrentUserOrganizationReadModel.MembershipId),
                nameof(CurrentUserOrganizationReadModel.Name),
                nameof(CurrentUserOrganizationReadModel.OrganizationId),
                nameof(CurrentUserOrganizationReadModel.Role)
            ],
            propertyNames);
    }

    [Fact]
    public void ExecuteAsync_Contract_HasNoOrganizationContextInput()
    {
        MethodInfo? executeAsync = typeof(GetCurrentUserOrganizationsUseCase)
            .GetMethod(nameof(GetCurrentUserOrganizationsUseCase.ExecuteAsync));
        Assert.NotNull(executeAsync);

        Type[] parameterTypes = executeAsync
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Equal([typeof(Guid), typeof(CancellationToken)], parameterTypes);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyUserId_RejectsBeforeQuery()
    {
        var queries = new FakeCurrentUserOrganizationQueries([]);
        var useCase = new GetCurrentUserOrganizationsUseCase(queries);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => useCase.ExecuteAsync(Guid.Empty));

        Assert.Equal("User id cannot be empty.", exception.Message);
        Assert.Equal(0, queries.CallCount);
    }

    private sealed class FakeCurrentUserOrganizationQueries(
        IReadOnlyList<CurrentUserOrganizationReadModel> organizations)
        : ICurrentUserOrganizationQueries
    {
        public int CallCount { get; private set; }

        public Guid UserId { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<IReadOnlyList<CurrentUserOrganizationReadModel>>
            ListAccessibleAsync(
                Guid userId,
                CancellationToken cancellationToken = default)
        {
            CallCount++;
            UserId = userId;
            CancellationToken = cancellationToken;
            return Task.FromResult(organizations);
        }
    }
}
