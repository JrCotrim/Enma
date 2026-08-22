using System.Data.Common;
using Enma.Application.Agenda;
using Enma.Application.Authorization;
using Enma.Domain.CalendarEvents;
using Enma.Domain.Clients;
using Enma.Domain.Deadlines;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Tasks;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Enma.IntegrationTests.Application.Agenda;

[Collection(PostgreSqlCollection.Name)]
public sealed class GetAgendaUseCasePersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.Parse(
        "2026-08-22T12:00:00Z");
    private static readonly DateTimeOffset From = DateTimeOffset.Parse(
        "2026-09-01T00:00:00-03:00");
    private static readonly DateTimeOffset To = DateTimeOffset.Parse(
        "2026-09-08T00:00:00-03:00");

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task ExecuteAsync_AllSupportedLiveRoles_ReadMixedAgenda(
        OrganizationRole role)
    {
        AgendaGraph graph = CreateGraph(role);
        await SeedAsync(graph.Entities);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        GetAgendaUseCase useCase = CreateUseCase(dbContext);

        GetAgendaResult result = await useCase.ExecuteAsync(new GetAgendaQuery(
            graph.User.Id,
            graph.Organization.Id,
            From,
            To));

        Assert.Equal(GetAgendaResultStatus.Succeeded, result.Status);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(
            [
                AgendaItemKind.Deadline,
                AgendaItemKind.Task,
                AgendaItemKind.CalendarEvent
            ],
            result.Items.Select(item => item.Kind));
        Assert.Equal(graph.Deadline.Id, result.Items[0].Id);
        Assert.Equal(graph.Task.Id, result.Items[1].Id);
        Assert.Equal(graph.CalendarEvent.Id, result.Items[2].Id);
    }

    [Theory]
    [InlineData(InactiveState.User)]
    [InlineData(InactiveState.Membership)]
    [InlineData(InactiveState.Organization)]
    public async Task ExecuteAsync_InactiveLiveAccess_DeniesBeforeSourceQueries(
        InactiveState inactiveState)
    {
        AgendaGraph graph = CreateGraph(OrganizationRole.Owner);

        switch (inactiveState)
        {
            case InactiveState.User:
                graph.User.Deactivate();
                break;
            case InactiveState.Membership:
                graph.Membership.Deactivate();
                break;
            case InactiveState.Organization:
                graph.Organization.Deactivate();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(inactiveState));
        }

        await SeedAsync(graph.Entities);
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext = CreateContext(interceptor);
        GetAgendaUseCase useCase = CreateUseCase(dbContext);

        GetAgendaResult result = await useCase.ExecuteAsync(new GetAgendaQuery(
            graph.User.Id,
            graph.Organization.Id,
            From,
            To));

        Assert.Same(GetAgendaResult.AccessDenied, result);
        Assert.Single(interceptor.CommandTexts);
        Assert.DoesNotContain(
            interceptor.CommandTexts,
            commandText =>
                commandText.Contains("legal_deadlines", StringComparison.Ordinal) ||
                commandText.Contains("legal_tasks", StringComparison.Ordinal) ||
                commandText.Contains("calendar_events", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_DifferentOffsets_PreservesLocalDateAndInstantWindows()
    {
        AgendaGraph graph = CreateGraph(OrganizationRole.Member);
        await SeedAsync(graph.Entities);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        GetAgendaUseCase useCase = CreateUseCase(dbContext);
        var from = new DateTimeOffset(
            2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(-3));
        var to = new DateTimeOffset(
            2026, 9, 8, 0, 0, 0, TimeSpan.FromHours(-2));

        GetAgendaResult result = await useCase.ExecuteAsync(new GetAgendaQuery(
            graph.User.Id,
            graph.Organization.Id,
            from,
            to));

        Assert.Equal(GetAgendaResultStatus.Succeeded, result.Status);
        Assert.Contains(result.Items, item => item.Id == graph.Deadline.Id);
        Assert.Contains(result.Items, item => item.Id == graph.Task.Id);
        Assert.Contains(result.Items, item => item.Id == graph.CalendarEvent.Id);
        Assert.Equal(
            new DateOnly(2026, 9, 1),
            result.Items.Single(item => item.Id == graph.Deadline.Id).Date);
    }

    private static GetAgendaUseCase CreateUseCase(EnmaDbContext dbContext)
    {
        return new GetAgendaUseCase(
            new OrganizationAccessAuthorization(
                new OrganizationAccessLookup(dbContext)),
            new AgendaReadQueries(dbContext));
    }

    private EnmaDbContext CreateContext(DbCommandInterceptor interceptor)
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .AddInterceptors(interceptor)
                .Options;
        return new EnmaDbContext(options);
    }

    private async Task SeedAsync(IEnumerable<object> entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private static AgendaGraph CreateGraph(OrganizationRole role)
    {
        string marker = Guid.NewGuid().ToString("N");
        var organization = new Organization(
            "Agenda Legal",
            $"agenda-{marker}",
            CreatedAt);
        var user = new User(
            "Agenda User",
            $"agenda-{marker}@example.test",
            CreatedAt);
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            role,
            CreatedAt);
        var client = new Client(
            organization.Id,
            "Agenda Client",
            CreatedAt);
        var legalProcess = new LegalProcess(
            organization.Id,
            client.Id,
            "Agenda Process",
            CreatedAt);
        var deadline = new LegalDeadline(
            organization.Id,
            legalProcess.Id,
            "Agenda Deadline",
            new DateOnly(2026, 9, 1),
            CreatedAt);
        var task = new LegalTask(
            organization.Id,
            "Agenda Task",
            null,
            new DateOnly(2026, 9, 2),
            legalProcess.Id,
            membership.Id,
            membership.Id,
            CreatedAt);
        var calendarEvent = new CalendarEvent(
            organization.Id,
            "Agenda Event",
            null,
            DateTimeOffset.Parse("2026-09-03T12:00:00Z"),
            DateTimeOffset.Parse("2026-09-03T13:00:00Z"),
            null,
            null,
            legalProcess.Id,
            membership.Id,
            membership.Id,
            CreatedAt);

        return new AgendaGraph(
            organization,
            user,
            membership,
            deadline,
            task,
            calendarEvent,
            [
                organization,
                user,
                membership,
                client,
                legalProcess,
                deadline,
                task,
                calendarEvent
            ]);
    }

    public enum InactiveState
    {
        User,
        Membership,
        Organization
    }

    private sealed record AgendaGraph(
        Organization Organization,
        User User,
        OrganizationMembership Membership,
        LegalDeadline Deadline,
        LegalTask Task,
        CalendarEvent CalendarEvent,
        IReadOnlyList<object> Entities);

    private sealed class ReaderCommandInterceptor : DbCommandInterceptor
    {
        private readonly List<string> _commandTexts = [];

        public IReadOnlyList<string> CommandTexts => _commandTexts;

        public override ValueTask<InterceptionResult<DbDataReader>>
            ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _commandTexts.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
