using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Enma.Domain.Users;
using Enma.Infrastructure.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace Enma.Infrastructure.Persistence;

public sealed class PostgreSqlEmailVerificationSendBudget
    : IEmailVerificationSendBudget
{
    private const string AcquireBucketSql =
        """
        INSERT INTO email_verification_send_budgets
            (scope, key_hash, window_start, used)
        VALUES
            (@scope, @key_hash,
             date_trunc(@window_unit, transaction_timestamp(), 'UTC'), 1)
        ON CONFLICT (scope, key_hash) DO UPDATE
        SET window_start = EXCLUDED.window_start,
            used = CASE
                WHEN email_verification_send_budgets.window_start = EXCLUDED.window_start
                    THEN email_verification_send_budgets.used + 1
                ELSE 1
            END
        WHERE email_verification_send_budgets.window_start <> EXCLUDED.window_start
           OR email_verification_send_budgets.used < @permit_limit
        RETURNING used;
        """;

    private static readonly byte[] GlobalKeyHash = SHA256.HashData(
        Encoding.UTF8.GetBytes("ENMA:EMAIL_VERIFICATION:SEND_BUDGET:GLOBAL"));

    private readonly EnmaDbContext dbContext;
    private readonly EmailVerificationSendBudgetOptions options;

    public PostgreSqlEmailVerificationSendBudget(
        EnmaDbContext dbContext,
        IOptions<EmailVerificationSendBudgetOptions> options)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(options);

        this.dbContext = dbContext;
        this.options = options.Value;
    }

    public async Task<bool> TryAcquireAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        string canonicalEmail = User.NormalizeEmail(email);
        byte[] destinationKeyHash = SHA256.HashData(
            Encoding.UTF8.GetBytes(canonicalEmail));

        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        bool globalAcquired = await TryAcquireBucketAsync(
            EmailVerificationSendBudgetScope.Global,
            GlobalKeyHash,
            "hour",
            options.GlobalHourlyLimit,
            transaction,
            cancellationToken);

        if (!globalAcquired)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        bool destinationAcquired = await TryAcquireBucketAsync(
            EmailVerificationSendBudgetScope.Destination,
            destinationKeyHash,
            "day",
            options.DestinationDailyLimit,
            transaction,
            cancellationToken);

        if (!destinationAcquired)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<bool> TryAcquireBucketAsync(
        EmailVerificationSendBudgetScope scope,
        byte[] keyHash,
        string windowUnit,
        int permitLimit,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        DbConnection connection = dbContext.Database.GetDbConnection();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = AcquireBucketSql;
        command.Transaction = transaction.GetDbTransaction();
        AddParameter(command, "scope", (short)scope);
        AddParameter(command, "key_hash", keyHash);
        AddParameter(command, "window_unit", windowUnit);
        AddParameter(command, "permit_limit", permitLimit);

        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null and not DBNull;
    }

    private static void AddParameter(
        DbCommand command,
        string name,
        object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
