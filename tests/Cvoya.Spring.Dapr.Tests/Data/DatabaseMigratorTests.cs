// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Data;

using System.Data;
using System.Data.Common;

using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Dapr.Tenancy;

using global::Dapr.Actors.Client;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="DatabaseMigrator"/>. The migrator is expected
/// to be a no-op in test scenarios: either because the AutoMigrate
/// flag is off, because no connection string is configured, or
/// because the in-memory EF Core provider is not relational.
/// </summary>
public class DatabaseMigratorTests
{
    [Fact]
    public async Task StartAsync_NonRelationalProvider_DoesNotThrow()
    {
        // InMemory provider + a bogus connection string so we get past
        // the connection-string guard and exercise the IsRelational
        // check against the actual configured provider.
        await using var provider = BuildProvider();
        var migrator = CreateMigrator(
            provider,
            new DatabaseOptions { AutoMigrate = true },
            connectionString: "Host=localhost;Database=notused");

        await Should.NotThrowAsync(() => migrator.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StartAsync_AutoMigrateDisabled_DoesNotThrow()
    {
        await using var provider = BuildProvider();
        var migrator = CreateMigrator(
            provider,
            new DatabaseOptions { AutoMigrate = false },
            connectionString: "Host=localhost;Database=notused");

        await Should.NotThrowAsync(() => migrator.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StartAsync_NoConnectionString_DoesNotThrow()
    {
        await using var provider = BuildProvider();
        var migrator = CreateMigrator(
            provider,
            new DatabaseOptions { AutoMigrate = true },
            connectionString: null);

        await Should.NotThrowAsync(() => migrator.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StopAsync_CompletesImmediately()
    {
        await using var provider = BuildProvider();
        var migrator = CreateMigrator(
            provider,
            new DatabaseOptions { AutoMigrate = true },
            connectionString: null);

        await migrator.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Regression test for #1608. The Worker host invokes
    /// <see cref="DatabaseMigrator.StartAsync"/> directly via
    /// <c>IHost.MigrateSpringDatabaseAsync</c> between
    /// <c>app.Build()</c> and <c>app.RunAsync()</c>; the Generic Host
    /// then invokes the same method again as part of normal
    /// hosted-service start-up. The body must be idempotent — only the
    /// first call may apply migrations — so the second invocation does
    /// not re-enter <c>MigrateAsync</c> against an already-migrated
    /// database.
    /// </summary>
    [Fact]
    public async Task StartAsync_IsIdempotent_OnlyRunsOnce()
    {
        await using var provider = BuildProvider();
        var migrator = CreateMigrator(
            provider,
            // AutoMigrate=true + non-relational provider exercises the
            // earliest opportunity to set HasRun: the first call
            // returns at the IsRelational check, the second must
            // observe HasRun=true and short-circuit at the guard.
            new DatabaseOptions { AutoMigrate = true },
            connectionString: "Host=localhost;Database=notused");

        migrator.HasRun.ShouldBeFalse();

        await migrator.StartAsync(TestContext.Current.CancellationToken);
        migrator.HasRun.ShouldBeTrue("first call must set HasRun");

        // Second call: no throw, still HasRun.
        await Should.NotThrowAsync(
            () => migrator.StartAsync(TestContext.Current.CancellationToken));
        migrator.HasRun.ShouldBeTrue("second call must leave HasRun set");
    }

    /// <summary>
    /// The idempotency guard sits BEFORE the AutoMigrate / connection-
    /// string / IsRelational early-returns. A second invocation must
    /// short-circuit at the guard regardless of which downstream
    /// branch the first invocation took, so the host's later
    /// <see cref="IHostedService.StartAsync"/> never re-enters the
    /// migrator body.
    /// </summary>
    [Fact]
    public async Task StartAsync_IsIdempotent_EvenWhenAutoMigrateDisabled()
    {
        await using var provider = BuildProvider();
        var migrator = CreateMigrator(
            provider,
            new DatabaseOptions { AutoMigrate = false },
            connectionString: "Host=localhost;Database=notused");

        await migrator.StartAsync(TestContext.Current.CancellationToken);
        migrator.HasRun.ShouldBeTrue();

        await migrator.StartAsync(TestContext.Current.CancellationToken);
        migrator.HasRun.ShouldBeTrue();
    }

    /// <summary>
    /// Regression test for #305. <c>AddCvoyaSpringDapr</c> on its own MUST
    /// NOT register <see cref="DatabaseMigrator"/> as a hosted service —
    /// otherwise both the API and Worker hosts (which both call
    /// <c>AddCvoyaSpringDapr</c>) would race on <c>MigrateAsync</c> against
    /// the same database. The migrator is opt-in via
    /// <see cref="ServiceCollectionExtensions.AddCvoyaSpringDatabaseMigrator"/>
    /// from the single host that owns migrations (the Worker in the OSS
    /// deployment).
    /// </summary>
    [Fact]
    public void AddCvoyaSpringDapr_DoesNotRegisterDatabaseMigrator()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IActorProxyFactory>());
        // Pre-register an in-memory SpringDbContext so AddCvoyaSpringDapr
        // skips its mandatory connection-string check.
        services.AddDbContext<SpringDbContext>(options =>
            options.UseInMemoryDatabase($"DiTest_{Guid.NewGuid():N}"));

        services.AddCvoyaSpringDapr(new ConfigurationBuilder().Build());

        services.ShouldNotContain(d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(DatabaseMigrator));
    }

    /// <summary>
    /// The opt-in extension introduced for #305:
    /// <see cref="ServiceCollectionExtensions.AddCvoyaSpringDatabaseMigrator"/>
    /// MUST register <see cref="DatabaseMigrator"/> as a hosted service so
    /// the host that calls it (the Worker in the OSS deployment) actually
    /// applies pending migrations on startup.
    /// </summary>
    [Fact]
    public void AddCvoyaSpringDatabaseMigrator_RegistersDatabaseMigratorHostedService()
    {
        var services = new ServiceCollection();

        services.AddCvoyaSpringDatabaseMigrator();

        services.ShouldContain(d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(DatabaseMigrator));
    }

    [Fact]
    public async Task SeedMigrationHistory_SpringTableDoesNotExist_IsNoOp()
    {
        // Arrange: spring.__EFMigrationsHistory does not exist.
        var conn = CreateMockConnection([false]); // first query returns false
        var logger = NullLogger<DatabaseMigrator>.Instance;

        // Act — should return without error.
        await DatabaseMigrator.SeedMigrationHistoryFromPublicSchemaAsync(
            conn, logger, TestContext.Current.CancellationToken);

        // Assert: only the existence check was executed (1 command created).
        conn.Received(1).CreateCommand();
    }

    [Fact]
    public async Task SeedMigrationHistory_SpringTableHasRows_IsNoOp()
    {
        // Arrange: spring.__EFMigrationsHistory exists and has 3 rows.
        var conn = CreateMockConnection([true, 3L]);
        var logger = NullLogger<DatabaseMigrator>.Instance;

        await DatabaseMigrator.SeedMigrationHistoryFromPublicSchemaAsync(
            conn, logger, TestContext.Current.CancellationToken);

        // Assert: existence check + count check = 2 commands.
        conn.Received(2).CreateCommand();
    }

    [Fact]
    public async Task SeedMigrationHistory_PublicTableDoesNotExist_IsNoOp()
    {
        // Arrange: spring table exists and is empty; public table does not exist.
        var conn = CreateMockConnection([true, 0L, false]);
        var logger = NullLogger<DatabaseMigrator>.Instance;

        await DatabaseMigrator.SeedMigrationHistoryFromPublicSchemaAsync(
            conn, logger, TestContext.Current.CancellationToken);

        // spring exists + spring count + public exists = 3 commands, no copy.
        conn.Received(3).CreateCommand();
    }

    [Fact]
    public async Task SeedMigrationHistory_PublicTableEmpty_IsNoOp()
    {
        // Arrange: spring table exists/empty, public table exists/empty.
        var conn = CreateMockConnection([true, 0L, true, 0L]);
        var logger = NullLogger<DatabaseMigrator>.Instance;

        await DatabaseMigrator.SeedMigrationHistoryFromPublicSchemaAsync(
            conn, logger, TestContext.Current.CancellationToken);

        // spring exists + spring count + public exists + public count = 4 commands.
        conn.Received(4).CreateCommand();
    }

    [Fact]
    public async Task SeedMigrationHistory_CopiesRowsWhenConditionsMet()
    {
        // Arrange: spring table exists/empty, public table exists with 5 rows.
        var conn = CreateMockConnection([true, 0L, true, 5L, 5]);
        var logger = NullLogger<DatabaseMigrator>.Instance;

        await DatabaseMigrator.SeedMigrationHistoryFromPublicSchemaAsync(
            conn, logger, TestContext.Current.CancellationToken);

        // All 5 commands created: spring exists, spring count, public exists,
        // public count, copy INSERT.
        conn.Received(5).CreateCommand();
    }

    /// <summary>
    /// Creates a mocked <see cref="DbConnection"/> that returns a sequence of
    /// scalar results from successive <c>CreateCommand()</c> calls. The last
    /// value in the sequence is used for <c>ExecuteNonQueryAsync</c> if it is
    /// an <see cref="int"/>; otherwise all values feed
    /// <c>ExecuteScalarAsync</c>.
    /// </summary>
    private static DbConnection CreateMockConnection(object[] scalarResults)
    {
        var conn = Substitute.For<DbConnection>();
        var callIndex = 0;

        conn.CreateCommand().Returns(_ =>
        {
            var cmd = Substitute.For<DbCommand>();
            var idx = callIndex++;
            if (idx < scalarResults.Length)
            {
                var val = scalarResults[idx];
                if (idx == scalarResults.Length - 1 && val is int intVal)
                {
                    // Last value and it's an int — it's for ExecuteNonQueryAsync.
                    cmd.ExecuteNonQueryAsync(Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult(intVal));
                }
                else
                {
                    cmd.ExecuteScalarAsync(Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult<object?>(val));
                }
            }

            return cmd;
        });

        return conn;
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<SpringDbContext>(o =>
            o.UseInMemoryDatabase($"MigratorTest_{Guid.NewGuid()}"));
        return services.BuildServiceProvider();
    }

    private static DatabaseMigrator CreateMigrator(
        IServiceProvider serviceProvider,
        DatabaseOptions options,
        string? connectionString)
    {
        var configBuilder = new ConfigurationBuilder();
        if (!string.IsNullOrEmpty(connectionString))
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SpringDb"] = connectionString,
            });
        }
        var configuration = configBuilder.Build();

        return new DatabaseMigrator(
            serviceProvider,
            configuration,
            Options.Create(options),
            new TenantScopeBypass(NullLogger<TenantScopeBypass>.Instance),
            NullLogger<DatabaseMigrator>.Instance);
    }
}