using System.Net;
using System.Security.Cryptography;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgreSqlLeastPrivilegeCollection
{
    public const string Name = "PostgreSQL least privilege";
}

// One dedicated database that logs every statement, migrated by the
// bootstrap superuser and given the D33 identities by
// deploy/postgres/enma-roles.sql, shared by the tests of the class below. Every
// test that changes it restores it and compares against Baseline.
public sealed class PostgreSqlLeastPrivilegeFixture : IAsyncLifetime
{
    public const string BootstrapUser = "enma_privilege_admin";
    public const string MigratorRole = "enma_privilege_migrator";
    public const string AppRole = "enma_privilege_app";

    public PostgreSqlLeastPrivilegeFixture()
    {
        Container = new PostgreSqlBuilder(TestContainerImages.PostgreSql)
            .WithDatabase("enma_privileges")
            .WithUsername(BootstrapUser)
            .WithPassword(CreateSecret())
            .WithCommand("-c", "log_statement=all")
            .WithCreateParameterModifier(LoopbackPortBindings.Apply)
            .Build();
    }

    public PostgreSqlContainer Container { get; }

    public string MigratorPassword { get; } = CreateSecret();

    public string AppPassword { get; } = CreateSecret();

    public IReadOnlyList<string> Baseline { get; private set; } = [];

    public string AdminConnectionString => Container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Container.StartAsync();
        await using (var dbContext = new EnmaDbContext(
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(AdminConnectionString)
                .Options))
        {
            await dbContext.Database.MigrateAsync();
        }

        await ApplyRolesAsync();
        await ApplyGrantsAsync();
        Baseline = await ReadSecuritySnapshotAsync();
    }

    public Task DisposeAsync()
    {
        return Container.DisposeAsync().AsTask();
    }

    public Task ApplyRolesAsync()
    {
        return PostgreSqlRoles.ApplyRolesAsync(
            AdminConnectionString, MigratorRole, MigratorPassword, AppRole, AppPassword);
    }

    public Task ApplyGrantsAsync()
    {
        return PostgreSqlRoles.ApplyGrantsAsync(AdminConnectionString, MigratorRole, AppRole);
    }

    // Owners, ACLs, attributes and memberships of everything the script
    // manages; role passwords are excluded (each run re-salts them).
    public Task<IReadOnlyList<string>> ReadSecuritySnapshotAsync()
    {
        return PostgreSqlLeastPrivilegeTests.QueryStringsAsync(
            AdminConnectionString,
            """
            SELECT format('role %s super=%s inherit=%s createrole=%s createdb=%s login=%s replication=%s bypassrls=%s limit=%s until=%s',
                          rolname, rolsuper, rolinherit, rolcreaterole, rolcreatedb, rolcanlogin,
                          rolreplication, rolbypassrls, rolconnlimit, rolvaliduntil)
            FROM pg_roles WHERE rolname IN (@migrator, @app)
            UNION ALL
            SELECT format('membership %s in %s', pg_get_userbyid(member), pg_get_userbyid(roleid))
            FROM pg_auth_members
            WHERE member IN (@migrator::regrole, @app::regrole) OR roleid IN (@migrator::regrole, @app::regrole)
            UNION ALL
            SELECT format('database owner=%s acl=%s', pg_get_userbyid(datdba), datacl)
            FROM pg_database WHERE datname = current_database()
            UNION ALL
            SELECT format('schema %s owner=%s acl=%s', nspname, pg_get_userbyid(nspowner), nspacl)
            FROM pg_namespace
            WHERE nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast')
              AND nspname !~ '^pg_(temp|toast_temp)_[0-9]+$'
            UNION ALL
            SELECT format('relation %s kind=%s owner=%s acl=%s', relname, relkind, pg_get_userbyid(relowner), relacl)
            FROM pg_class WHERE relnamespace = 'public'::regnamespace
            UNION ALL
            SELECT format('column %s.%s acl=%s', c.relname, a.attname, a.attacl)
            FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid
            WHERE c.relnamespace = 'public'::regnamespace AND a.attacl IS NOT NULL
            UNION ALL
            SELECT format('function %s owner=%s acl=%s definer=%s', oid::regprocedure, pg_get_userbyid(proowner), proacl, prosecdef)
            FROM pg_proc WHERE pronamespace = 'public'::regnamespace
            UNION ALL
            SELECT format('type %s owner=%s', typname, pg_get_userbyid(typowner))
            FROM pg_type WHERE typnamespace = 'public'::regnamespace
            UNION ALL
            SELECT format('default %s %s %s %s', pg_get_userbyid(defaclrole), defaclnamespace, defaclobjtype, defaclacl)
            FROM pg_default_acl
            ORDER BY 1
            """);
    }

    private static string CreateSecret()
    {
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
    }
}

// D33-D37: the privilege map of the application role per table, object
// ownership, the operations the application role is denied, readiness as the
// application role, idempotency of deploy/postgres/enma-roles.sql (also across
// migrations applied by the migrator), its fail-closed checks, and the
// password guard.
[Collection(PostgreSqlLeastPrivilegeCollection.Name)]
public sealed class PostgreSqlLeastPrivilegeTests(PostgreSqlLeastPrivilegeFixture fixture)
    : IClassFixture<PostgreSqlLeastPrivilegeFixture>
{
    private const string BootstrapUser = PostgreSqlLeastPrivilegeFixture.BootstrapUser;
    private const string MigratorRole = PostgreSqlLeastPrivilegeFixture.MigratorRole;
    private const string AppRole = PostgreSqlLeastPrivilegeFixture.AppRole;
    private const string MigrationBeforeTableCreations =
        "20260913161005_AddNotificationDismissal";

    private const TablePrivileges Dml =
        TablePrivileges.Select | TablePrivileges.Insert |
        TablePrivileges.Update | TablePrivileges.Delete;

    // D37: every table in schema public, classified. A migration that creates
    // a table updates this map in the same slice; an unclassified table, or
    // an entry for a table that no longer exists, fails.
    private static readonly IReadOnlyDictionary<string, TablePrivileges> PrivilegeMap =
        new Dictionary<string, TablePrivileges>(StringComparer.Ordinal)
        {
            ["__EFMigrationsHistory"] = TablePrivileges.Select,
            ["audit_logs"] = TablePrivileges.Select | TablePrivileges.Insert,
            ["authentication_sessions"] = Dml,
            ["calendar_events"] = Dml,
            ["client_payment_plans"] = Dml,
            ["clients"] = Dml,
            ["email_verification_challenges"] = Dml,
            ["email_verification_send_budgets"] = Dml,
            ["external_identities"] = Dml,
            ["legal_deadlines"] = Dml,
            ["legal_documents"] = Dml,
            ["legal_processes"] = Dml,
            ["legal_tasks"] = Dml,
            ["notifications"] = Dml,
            ["organization_invitations"] = Dml,
            ["organization_memberships"] = Dml,
            ["organizations"] = Dml,
            ["password_recovery_challenges"] = Dml,
            ["payment_installments"] = Dml,
            ["user_credentials"] = Dml,
            ["users"] = Dml
        };

    [Flags]
    private enum TablePrivileges
    {
        None = 0,
        Select = 1,
        Insert = 2,
        Update = 4,
        Delete = 8,
        Truncate = 16,
        References = 32,
        Trigger = 64,
        Maintain = 128
    }

    private string AdminConnectionString => fixture.AdminConnectionString;

    private string MigratorConnectionString =>
        WithIdentity(MigratorRole, fixture.MigratorPassword);

    private string AppConnectionString => WithIdentity(AppRole, fixture.AppPassword);

    [Fact]
    public async Task AppRole_TablePrivileges_MatchTheExplicitMapForEveryTable()
    {
        Dictionary<string, TablePrivileges> actual = [];
        await using (NpgsqlConnection admin = await OpenAsync(AdminConnectionString))
        await using (NpgsqlCommand command = new(
            """
            SELECT c.relname,
                   has_any_column_privilege(@app, c.oid, 'SELECT'),
                   has_any_column_privilege(@app, c.oid, 'INSERT'),
                   has_any_column_privilege(@app, c.oid, 'UPDATE'),
                   has_table_privilege(@app, c.oid, 'DELETE'),
                   has_table_privilege(@app, c.oid, 'TRUNCATE'),
                   has_any_column_privilege(@app, c.oid, 'REFERENCES'),
                   has_table_privilege(@app, c.oid, 'TRIGGER'),
                   has_table_privilege(@app, c.oid, 'MAINTAIN')
            FROM pg_class c
            WHERE c.relnamespace = 'public'::regnamespace
              AND c.relkind NOT IN ('i', 'I', 'c', 't')
            """,
            admin))
        {
            command.Parameters.AddWithValue("app", AppRole);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                TablePrivileges privileges = TablePrivileges.None;
                TablePrivileges[] columns =
                [
                    TablePrivileges.Select, TablePrivileges.Insert,
                    TablePrivileges.Update, TablePrivileges.Delete,
                    TablePrivileges.Truncate, TablePrivileges.References,
                    TablePrivileges.Trigger, TablePrivileges.Maintain
                ];
                for (int index = 0; index < columns.Length; index++)
                {
                    if (reader.GetBoolean(index + 1))
                    {
                        privileges |= columns[index];
                    }
                }

                actual[reader.GetString(0)] = privileges;
            }
        }

        string[] unclassified = actual.Keys
            .Except(PrivilegeMap.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] stale = PrivilegeMap.Keys
            .Except(actual.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            unclassified.Length == 0,
            "Unclassified relations; add them to PrivilegeMap (D37): " +
            string.Join(", ", unclassified));
        Assert.True(
            stale.Length == 0,
            "PrivilegeMap lists relations that do not exist: " +
            string.Join(", ", stale));
        foreach ((string table, TablePrivileges expected) in PrivilegeMap)
        {
            Assert.True(
                expected == actual[table],
                $"{table}: expected {expected}, found {actual[table]}.");
        }

        Assert.Empty(await QueryStringsAsync(
            AdminConnectionString,
            """
            SELECT c.relname || ' ' || CASE x.grantee WHEN 0 THEN 'PUBLIC' ELSE pg_get_userbyid(x.grantee) END
            FROM pg_class c, aclexplode(c.relacl) x
            WHERE c.relnamespace = 'public'::regnamespace
              AND x.grantee NOT IN (@migrator::regrole, @app::regrole)
            UNION ALL
            SELECT c.relname || '.' || a.attname
            FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid
            WHERE c.relnamespace = 'public'::regnamespace AND a.attacl IS NOT NULL
            """));

        await using NpgsqlConnection app = await OpenAsync(AppConnectionString);
        foreach (string table in PrivilegeMap.Keys)
        {
            await using NpgsqlCommand read = new(
                $"SELECT count(*) FROM public.\"{table}\"",
                app);
            Assert.True((long)(await read.ExecuteScalarAsync())! >= 0);
        }
    }

    [Fact]
    public async Task Ownership_EveryRelationFunctionAndType_BelongsToTheMigrator()
    {
        Assert.Empty(await QueryStringsAsync(
            AdminConnectionString,
            """
            SELECT 'relation ' || relname || ' owned by ' || pg_get_userbyid(relowner)
            FROM pg_class
            WHERE relnamespace = 'public'::regnamespace AND relowner <> @migrator::regrole
            UNION ALL
            SELECT 'function ' || oid::regprocedure || ' owned by ' || pg_get_userbyid(proowner)
            FROM pg_proc
            WHERE pronamespace = 'public'::regnamespace AND proowner <> @migrator::regrole
            UNION ALL
            SELECT 'type ' || typname || ' owned by ' || pg_get_userbyid(typowner)
            FROM pg_type
            WHERE typnamespace = 'public'::regnamespace AND typowner <> @migrator::regrole
            """));

        // Not vacuous: the database, the schema, the tables with their
        // indexes and the trigger functions exist, and the app owns nothing.
        IReadOnlyList<string> summary = await QueryStringsAsync(
            AdminConnectionString,
            """
            SELECT format(
                'database=%s schema=%s tables=%s indexes>0=%s functions>0=%s app-owned=%s',
                (SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = current_database()),
                (SELECT pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname = 'public'),
                (SELECT count(*) FROM pg_class
                 WHERE relnamespace = 'public'::regnamespace AND relkind = 'r'),
                (SELECT count(*) > 0 FROM pg_class
                 WHERE relnamespace = 'public'::regnamespace AND relkind = 'i'),
                (SELECT count(*) > 0 FROM pg_proc WHERE pronamespace = 'public'::regnamespace),
                (SELECT count(*) FROM pg_class WHERE relowner = @app::regrole) +
                (SELECT count(*) FROM pg_proc WHERE proowner = @app::regrole) +
                (SELECT count(*) FROM pg_type WHERE typowner = @app::regrole) +
                (SELECT count(*) FROM pg_namespace WHERE nspowner = @app::regrole) +
                (SELECT count(*) FROM pg_database WHERE datdba = @app::regrole))
            """);

        Assert.Equal(
            [
                $"database={MigratorRole} schema=pg_database_owner " +
                $"tables={PrivilegeMap.Count} indexes>0=t functions>0=t app-owned=0"
            ],
            summary);
    }

    [Theory]
    [InlineData("CREATE TABLE public.enma_probe (id integer)")]
    [InlineData("ALTER TABLE public.clients ADD COLUMN enma_probe integer")]
    [InlineData("DROP TABLE public.clients")]
    [InlineData("CREATE INDEX enma_probe ON public.clients (id)")]
    [InlineData("CREATE SCHEMA enma_probe")]
    [InlineData("CREATE FUNCTION public.enma_probe() RETURNS integer LANGUAGE sql AS 'SELECT 1'")]
    [InlineData("ALTER TABLE public.audit_logs DISABLE TRIGGER trg_audit_logs_append_only")]
    [InlineData("UPDATE public.audit_logs SET id = id")]
    [InlineData("DELETE FROM public.audit_logs")]
    [InlineData("TRUNCATE public.audit_logs")]
    [InlineData("TRUNCATE public.clients")]
    [InlineData("INSERT INTO public.\"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('99999999999999_Probe', '0')")]
    [InlineData("UPDATE public.\"__EFMigrationsHistory\" SET \"ProductVersion\" = \"ProductVersion\"")]
    [InlineData("DELETE FROM public.\"__EFMigrationsHistory\"")]
    [InlineData("CREATE TEMPORARY TABLE enma_probe (id integer)")]
    public async Task AppRole_PrivilegedOperation_IsDeniedWithInsufficientPrivilege(
        string sql)
    {
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(AppConnectionString, sql));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        Assert.Equal(fixture.Baseline, await fixture.ReadSecuritySnapshotAsync());
    }

    [Fact]
    public async Task Readiness_AsTheApplicationRole_IsHealthy()
    {
        using ReadinessApiFactory factory = new(AppConnectionString);
        using HttpClient client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });

        using HttpResponseMessage response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Script_RunTwiceAndAcrossMigrationsByTheMigrator_ChangesNothing()
    {
        await fixture.ApplyRolesAsync();
        await fixture.ApplyGrantsAsync();
        Assert.Equal(fixture.Baseline, await fixture.ReadSecuritySnapshotAsync());

        // The deploy order: roles, migrations as the migrator, grants. Going
        // down and up again drops and re-creates two tables as the migrator.
        await fixture.ApplyRolesAsync();
        await using (EnmaDbContext dbContext = CreateDbContext(MigratorConnectionString))
        {
            IMigrator migrator = dbContext.GetService<IMigrator>();
            await migrator.MigrateAsync(MigrationBeforeTableCreations);
            await migrator.MigrateAsync();
        }

        await fixture.ApplyGrantsAsync();
        Assert.Equal(fixture.Baseline, await fixture.ReadSecuritySnapshotAsync());
    }

    public static TheoryData<string, string, string> UnexpectedObjects => new()
    {
        {
            "CREATE SCHEMA enma_probe",
            "DROP SCHEMA enma_probe",
            "unexpected schema outside public: enma_probe"
        },
        {
            "CREATE SEQUENCE public.enma_probe",
            "DROP SEQUENCE public.enma_probe",
            "unexpected relation kind in public: enma_probe (relkind S)"
        },
        {
            "CREATE VIEW public.enma_probe AS SELECT 1 AS id",
            "DROP VIEW public.enma_probe",
            "unexpected relation kind in public: enma_probe (relkind v)"
        },
        {
            "CREATE FUNCTION public.enma_probe() RETURNS integer LANGUAGE sql SECURITY DEFINER AS 'SELECT 1'",
            "DROP FUNCTION public.enma_probe()",
            "unexpected routine in public (only SECURITY INVOKER functions): public.enma_probe()"
        },
        {
            "CREATE TYPE public.enma_probe AS ENUM ('a')",
            "DROP TYPE public.enma_probe",
            "unexpected type in public:"
        },
        {
            $"GRANT {MigratorRole} TO {AppRole}",
            $"REVOKE {MigratorRole} FROM {AppRole}",
            $"unexpected role membership: {AppRole} in {MigratorRole}"
        },
        {
            $"ALTER DEFAULT PRIVILEGES FOR ROLE {BootstrapUser} GRANT SELECT ON TABLES TO {AppRole}",
            $"ALTER DEFAULT PRIVILEGES FOR ROLE {BootstrapUser} REVOKE SELECT ON TABLES FROM {AppRole}",
            "unexpected database object: default privileges"
        },
        {
            "CREATE POLICY enma_probe ON public.clients USING (true)",
            "DROP POLICY enma_probe ON public.clients",
            "unexpected database object: policy enma_probe"
        }
    };

    [Theory]
    [MemberData(nameof(UnexpectedObjects))]
    public async Task Script_WithAnUnexpectedObject_FailsClosedAndChangesNothing(
        string setup,
        string cleanup,
        string expectedMessage)
    {
        await ExecuteAsync(AdminConnectionString, setup);
        try
        {
            PostgresException roles = await Assert.ThrowsAsync<PostgresException>(fixture.ApplyRolesAsync);
            PostgresException grants = await Assert.ThrowsAsync<PostgresException>(fixture.ApplyGrantsAsync);

            Assert.StartsWith(expectedMessage, roles.MessageText, StringComparison.Ordinal);
            Assert.StartsWith(expectedMessage, grants.MessageText, StringComparison.Ordinal);
        }
        finally
        {
            await ExecuteAsync(AdminConnectionString, cleanup);
        }

        Assert.Equal(fixture.Baseline, await fixture.ReadSecuritySnapshotAsync());
    }

    public static TheoryData<string, string, string> InvalidIdentities => new()
    {
        { "Enma-Migrator", AppRole, "invalid migrator role name" },
        { MigratorRole, "enma app", "invalid application role name" },
        { MigratorRole, "pg_monitor", "invalid application role name" },
        { MigratorRole, MigratorRole, "the migrator and application roles must differ" },
        { BootstrapUser, AppRole, "the bootstrap superuser cannot be the migrator or application role" },
        { MigratorRole, BootstrapUser, "the bootstrap superuser cannot be the migrator or application role" }
    };

    [Theory]
    [MemberData(nameof(InvalidIdentities))]
    public async Task Script_WithAnInvalidIdentity_FailsClosedAndChangesNothing(
        string migratorRole,
        string appRole,
        string expectedMessage)
    {
        PostgresException roles = await Assert.ThrowsAsync<PostgresException>(
            () => PostgreSqlRoles.ApplyRolesAsync(
                AdminConnectionString, migratorRole, fixture.MigratorPassword, appRole, fixture.AppPassword));
        PostgresException grants = await Assert.ThrowsAsync<PostgresException>(
            () => PostgreSqlRoles.ApplyGrantsAsync(
                AdminConnectionString, migratorRole, appRole));

        Assert.Contains(expectedMessage, roles.MessageText, StringComparison.Ordinal);
        Assert.Contains(expectedMessage, grants.MessageText, StringComparison.Ordinal);
        Assert.Equal(fixture.Baseline, await fixture.ReadSecuritySnapshotAsync());
    }

    [Fact]
    public async Task Script_WithAnotherSuperuserAsARole_FailsClosedAndChangesNothing()
    {
        const string superuser = "enma_privilege_superuser";
        await ExecuteAsync(AdminConnectionString, $"CREATE ROLE {superuser} SUPERUSER");
        try
        {
            PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
                () => PostgreSqlRoles.ApplyRolesAsync(
                    AdminConnectionString, MigratorRole, fixture.MigratorPassword, superuser, fixture.AppPassword));

            Assert.Equal(
                $"role {superuser} is a superuser; refusing to use it",
                exception.MessageText);
        }
        finally
        {
            await ExecuteAsync(AdminConnectionString, $"DROP ROLE {superuser}");
        }

        Assert.Equal(fixture.Baseline, await fixture.ReadSecuritySnapshotAsync());
    }

    [Theory]
    [InlineData("short", "enma-app-password-long-enough", "at least 16 characters")]
    [InlineData("enma-same-password-value", "enma-same-password-value", "passwords must differ")]
    public async Task Script_WithWeakOrSharedPasswords_FailsClosed(
        string migrator,
        string app,
        string expectedMessage)
    {
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => PostgreSqlRoles.ApplyRolesAsync(
                AdminConnectionString, MigratorRole, migrator, AppRole, app));

        Assert.Contains(expectedMessage, exception.MessageText, StringComparison.Ordinal);
        Assert.Equal(fixture.Baseline, await fixture.ReadSecuritySnapshotAsync());
    }

    // The scripts send the passwords as literals of the final call (psql over
    // stdin). With every statement logged by the server, neither a successful
    // run nor a password change that fails (here: the application role's row
    // locked by another session) may put a password in the server log or in
    // the error the caller prints.
    [Fact]
    public async Task Script_PasswordsAsLiterals_NeverReachTheServerLogOrTheError()
    {
        string marker = $"enma-log-marker-{Guid.NewGuid():N}";
        await ExecuteAsync(AdminConnectionString, $"SELECT '{marker}'");
        string script = await PostgreSqlRoles.ReadScriptAsync();
        string call =
            $"SELECT pg_temp.enma_apply_roles('{MigratorRole}', '{AppRole}', " +
            $"'{fixture.MigratorPassword}', '{fixture.AppPassword}')";

        await using (NpgsqlConnection session = await OpenAsync(AdminConnectionString))
        {
            await ExecuteAsync(session, script);
            await ExecuteAsync(session, call);
        }

        PostgresException failure;
        await using (NpgsqlConnection blocker = await OpenAsync(AdminConnectionString))
        await using (NpgsqlTransaction transaction = await blocker.BeginTransactionAsync())
        {
            await ExecuteAsync(blocker, $"ALTER ROLE {AppRole} CONNECTION LIMIT -1");
            await using NpgsqlConnection session = await OpenAsync(AdminConnectionString);
            await ExecuteAsync(session, script);
            await ExecuteAsync(session, "SET lock_timeout = '1s'");
            failure = await Assert.ThrowsAsync<PostgresException>(
                () => ExecuteAsync(session, call));
            await transaction.RollbackAsync();
        }

        Assert.Equal(PostgresErrorCodes.LockNotAvailable, failure.SqlState);
        Assert.Equal(
            "could not set the application role login (SQLSTATE 55P03)",
            failure.MessageText);
        string reported = string.Join(
            '\n',
            failure.MessageText,
            failure.Detail,
            failure.Hint,
            failure.Where,
            failure.InternalQuery);
        Assert.DoesNotContain(fixture.AppPassword, reported, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.MigratorPassword, reported, StringComparison.Ordinal);

        string log = await ReadServerLogAsync(marker);
        Assert.Contains("enma_privilege_app", log, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.AppPassword, log, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.MigratorPassword, log, StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"enma_apply_roles('{MigratorRole}'", log, StringComparison.Ordinal);
        Assert.Equal(fixture.Baseline, await fixture.ReadSecuritySnapshotAsync());
    }

    private async Task<string> ReadServerLogAsync(string marker)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            (string stdout, string stderr) = await fixture.Container.GetLogsAsync();
            string log = stdout + stderr;
            if (log.Contains(marker, StringComparison.Ordinal))
            {
                return log;
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException(
            "The server log does not show the logged marker statement.");
    }

    private string WithIdentity(string username, string password)
    {
        return new NpgsqlConnectionStringBuilder(AdminConnectionString)
        {
            Username = username,
            Password = password,
            Pooling = false
        }.ConnectionString;
    }

    private static EnmaDbContext CreateDbContext(string connectionString)
    {
        return new EnmaDbContext(new DbContextOptionsBuilder<EnmaDbContext>()
            .UseNpgsql(connectionString)
            .Options);
    }

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using NpgsqlConnection connection = await OpenAsync(connectionString);
        await ExecuteAsync(connection, sql);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task<IReadOnlyList<string>> QueryStringsAsync(
        string connectionString,
        string sql)
    {
        await using NpgsqlConnection connection = await OpenAsync(connectionString);
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("migrator", MigratorRole);
        command.Parameters.AddWithValue("app", AppRole);
        List<string> rows = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private sealed class ReadinessApiFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Database", connectionString);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Database"] = connectionString,
                        [TestHostIsolation.DevelopmentSmtpPortKey] =
                            TestHostIsolation.UnusedSmtpPort.ToString()
                    }));
            builder.ConfigureTestServices(TestHostIsolation.RemoveBackgroundWorkers);
        }
    }
}
