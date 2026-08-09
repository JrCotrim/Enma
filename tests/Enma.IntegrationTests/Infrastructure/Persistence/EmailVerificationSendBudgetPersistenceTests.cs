using Enma.Infrastructure.Email;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class EmailVerificationSendBudgetPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TryAcquireAsync_GlobalExactBoundary_AdmitsOnlyConfiguredLimit()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        PostgreSqlEmailVerificationSendBudget budget = CreateBudget(
            dbContext,
            globalLimit: 3,
            destinationLimit: 10);

        var admissions = new List<bool>();

        for (int index = 0; index < 4; index++)
        {
            admissions.Add(await budget.TryAcquireAsync(
                $"global-{index}@example.test"));
        }

        Assert.Equal([true, true, true, false], admissions);
    }

    [Fact]
    public async Task TryAcquireAsync_DestinationExactBoundary_AdmitsOnlyConfiguredLimit()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        PostgreSqlEmailVerificationSendBudget budget = CreateBudget(
            dbContext,
            globalLimit: 10,
            destinationLimit: 3);

        bool[] results =
        [
            await budget.TryAcquireAsync("same@example.test"),
            await budget.TryAcquireAsync("same@example.test"),
            await budget.TryAcquireAsync("same@example.test"),
            await budget.TryAcquireAsync("same@example.test")
        ];

        Assert.Equal([true, true, true, false], results);
    }

    [Fact]
    public async Task TryAcquireAsync_DestinationAtLimit_OtherDestinationRemainsIndependent()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        PostgreSqlEmailVerificationSendBudget budget = CreateBudget(
            dbContext,
            globalLimit: 10,
            destinationLimit: 1);

        Assert.True(await budget.TryAcquireAsync("first@example.test"));
        Assert.False(await budget.TryAcquireAsync("first@example.test"));
        Assert.True(await budget.TryAcquireAsync("second@example.test"));

        dbContext.ChangeTracker.Clear();
        EmailVerificationSendBudget global = await dbContext
            .EmailVerificationSendBudgets
            .AsNoTracking()
            .SingleAsync(row =>
                row.Scope == EmailVerificationSendBudgetScope.Global);
        Assert.Equal(2, global.Used);
        Assert.Equal(
            2,
            await dbContext.EmailVerificationSendBudgets.CountAsync(row =>
                row.Scope == EmailVerificationSendBudgetScope.Destination));
    }

    [Fact]
    public async Task TryAcquireAsync_DestinationDenied_RollsBackGlobalIncrement()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        PostgreSqlEmailVerificationSendBudget budget = CreateBudget(
            dbContext,
            globalLimit: 10,
            destinationLimit: 1);

        Assert.True(await budget.TryAcquireAsync("capped@example.test"));
        Assert.False(await budget.TryAcquireAsync("capped@example.test"));

        for (int index = 0; index < 9; index++)
        {
            Assert.True(await budget.TryAcquireAsync(
                $"unique-{index}@example.test"));
        }

        Assert.False(await budget.TryAcquireAsync("over-global@example.test"));

        dbContext.ChangeTracker.Clear();
        EmailVerificationSendBudget global = await dbContext
            .EmailVerificationSendBudgets
            .AsNoTracking()
            .SingleAsync(row =>
                row.Scope == EmailVerificationSendBudgetScope.Global);
        Assert.Equal(10, global.Used);
    }

    [Fact]
    public async Task TryAcquireAsync_OldHourlyAndDailyWindows_ResetBothBucketsToOne()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        PostgreSqlEmailVerificationSendBudget budget = CreateBudget(
            dbContext,
            globalLimit: 3,
            destinationLimit: 3);
        const string email = "rollover@example.test";

        Assert.True(await budget.TryAcquireAsync(email));
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE email_verification_send_budgets
            SET window_start = CASE scope
                    WHEN 1 THEN date_trunc('hour', transaction_timestamp(), 'UTC')
                        - interval '1 hour'
                    ELSE date_trunc('day', transaction_timestamp(), 'UTC')
                        - interval '1 day'
                END,
                used = 3
            """);

        Assert.True(await budget.TryAcquireAsync(email));

        DateTimeOffset currentHour = await dbContext.Database
            .SqlQueryRaw<DateTimeOffset>(
                "SELECT date_trunc('hour', transaction_timestamp(), 'UTC') AS \"Value\"")
            .SingleAsync();
        DateTimeOffset currentDay = await dbContext.Database
            .SqlQueryRaw<DateTimeOffset>(
                "SELECT date_trunc('day', transaction_timestamp(), 'UTC') AS \"Value\"")
            .SingleAsync();
        dbContext.ChangeTracker.Clear();
        EmailVerificationSendBudget[] rows = await dbContext
            .EmailVerificationSendBudgets
            .AsNoTracking()
            .OrderBy(row => row.Scope)
            .ToArrayAsync();

        Assert.Collection(
            rows,
            global =>
            {
                Assert.Equal(EmailVerificationSendBudgetScope.Global, global.Scope);
                Assert.Equal(currentHour, global.WindowStart);
                Assert.Equal(1, global.Used);
            },
            destination =>
            {
                Assert.Equal(
                    EmailVerificationSendBudgetScope.Destination,
                    destination.Scope);
                Assert.Equal(currentDay, destination.WindowStart);
                Assert.Equal(1, destination.Used);
            });
    }

    [Fact]
    public async Task TryAcquireAsync_EquivalentEmailSpellings_ShareDestinationBudget()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        PostgreSqlEmailVerificationSendBudget budget = CreateBudget(
            dbContext,
            globalLimit: 2,
            destinationLimit: 1);

        Assert.True(await budget.TryAcquireAsync("  Canonical@Example.Test  "));
        Assert.False(await budget.TryAcquireAsync("canonical@example.test"));

        dbContext.ChangeTracker.Clear();
        EmailVerificationSendBudget destination = await dbContext
            .EmailVerificationSendBudgets
            .AsNoTracking()
            .SingleAsync(row =>
                row.Scope == EmailVerificationSendBudgetScope.Destination);
        Assert.Equal(32, destination.KeyHash.Length);
        Assert.Equal(1, destination.Used);
    }

    [Fact]
    public async Task TryAcquireAsync_InvalidDestination_PropagatesProgrammingError()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        PostgreSqlEmailVerificationSendBudget budget = CreateBudget(
            dbContext,
            globalLimit: 3,
            destinationLimit: 3);

        await Assert.ThrowsAsync<ArgumentException>(
            () => budget.TryAcquireAsync("not-an-email"));

        Assert.Empty(await dbContext.EmailVerificationSendBudgets.ToArrayAsync());
    }

    [Theory]
    [InlineData(
        "3",
        "decode(repeat('00', 32), 'hex')",
        "transaction_timestamp()",
        "1",
        "ck_email_verification_send_budgets_scope")]
    [InlineData(
        "1",
        "decode(repeat('00', 31), 'hex')",
        "transaction_timestamp()",
        "1",
        "ck_email_verification_send_budgets_key_hash_length")]
    [InlineData(
        "1",
        "decode(repeat('00', 32), 'hex')",
        "transaction_timestamp()",
        "0",
        "ck_email_verification_send_budgets_used")]
    [InlineData(
        "1",
        "decode(repeat('00', 32), 'hex')",
        "'infinity'::timestamptz",
        "1",
        "ck_email_verification_send_budgets_window_start")]
    public async Task DatabaseConstraints_InvalidBudgetState_RejectsRow(
        string scopeSql,
        string keyHashSql,
        string windowStartSql,
        string usedSql,
        string expectedConstraint)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        string command =
            $"""
            INSERT INTO email_verification_send_budgets
                (scope, key_hash, window_start, used)
            VALUES ({scopeSql}, {keyHashSql}, {windowStartSql}, {usedSql})
            """;

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => dbContext.Database.ExecuteSqlRawAsync(command));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(expectedConstraint, exception.ConstraintName);
    }

    internal static PostgreSqlEmailVerificationSendBudget CreateBudget(
        EnmaDbContext dbContext,
        int globalLimit,
        int destinationLimit)
    {
        return new PostgreSqlEmailVerificationSendBudget(
            dbContext,
            Options.Create(new EmailVerificationSendBudgetOptions
            {
                GlobalHourlyLimit = globalLimit,
                DestinationDailyLimit = destinationLimit
            }));
    }
}
