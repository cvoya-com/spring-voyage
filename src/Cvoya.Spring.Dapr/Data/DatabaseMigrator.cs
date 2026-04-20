// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using Cvoya.Spring.Core.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Hosted service that applies any pending EF Core migrations to the
/// <see cref="SpringDbContext"/> on host startup. Gated by
/// <see cref="DatabaseOptions.AutoMigrate"/> so operators who run
/// migrations out-of-band (CI/CD, scripted SQL dumps) can disable it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Single-owner invariant.</strong> This service must be
/// registered as a hosted service in <strong>exactly one</strong> host
/// of a given deployment. In the OSS topology that host is the Worker
/// (<c>Cvoya.Spring.Host.Worker</c>); the API host
/// (<c>Cvoya.Spring.Host.Api</c>) intentionally does not register it.
/// Registering it in two hosts that start concurrently against the same
/// PostgreSQL instance races on DDL and the loser crashes with
/// <c>42P07: relation "..." already exists</c> (issue #305). Use
/// <see cref="DependencyInjection.ServiceCollectionExtensions.AddCvoyaSpringDatabaseMigrator"/>
/// from the chosen host; do not call <c>AddHostedService&lt;DatabaseMigrator&gt;</c>
/// directly.
/// </para>
/// <para>
/// Multi-replica deployments (more than one Worker container running
/// simultaneously) need an external coordination primitive — for
/// example a Postgres advisory lock or a Kubernetes leader election —
/// before this service is safe. Single-replica Worker deployments are
/// safe by construction.
/// </para>
/// <para>
/// Only runs when the configured provider is relational. Non-relational
/// providers (for example <c>UseInMemoryDatabase</c> in tests) do not
/// support migrations; for those the schema is managed by the test
/// harness itself via <see cref="DatabaseFacade.EnsureCreatedAsync(System.Threading.CancellationToken)"/>.
/// </para>
/// </remarks>
public class DatabaseMigrator(
    IServiceProvider services,
    IConfiguration configuration,
    IOptions<DatabaseOptions> options,
    ITenantScopeBypass tenantScopeBypass,
    ILogger<DatabaseMigrator> logger) : IHostedService
{
    private readonly DatabaseOptions _options = options.Value;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.AutoMigrate)
        {
            logger.LogInformation(
                "Database:AutoMigrate disabled — skipping automatic EF Core migration. Operators must apply migrations out-of-band.");
            return;
        }

        // When no connection string is set the host is running without
        // a database (for example under build-time OpenAPI tooling).
        // There is nothing to migrate — and resolving the DbContext
        // and probing the provider can trip up assembly resolution in
        // the tooling process.
        var connectionString = configuration.GetConnectionString("SpringDb");
        if (string.IsNullOrEmpty(connectionString))
        {
            logger.LogDebug(
                "ConnectionStrings:SpringDb is not configured; skipping MigrateAsync.");
            return;
        }

        await MigrateCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Isolated so the JIT only pulls in Microsoft.EntityFrameworkCore.Relational
    // when we've already decided to run migrations. Build-time tooling that
    // loads the host without a connection string (for example
    // dotnet-getdocument during OpenAPI snapshot) never enters this method,
    // avoiding assembly-resolution issues in the tooling process.
    private async Task MigrateCoreAsync(CancellationToken cancellationToken)
    {
        // Migrations run before any tenant context exists and must be able
        // to read/write rows across every tenant (e.g. backfilling a new
        // TenantId column on existing business-data rows). The tenant-scope
        // bypass is the auditable escape hatch (#677) that the EF query
        // filter added in #675 consults — without it the migration would
        // silently see an empty database for any tenant-scoped entity.
        using var bypass = tenantScopeBypass.BeginBypass("database migration");

        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        if (!context.Database.IsRelational())
        {
            // In-memory / test providers don't support migrations. Test
            // harnesses seed schema via EnsureCreatedAsync themselves.
            logger.LogDebug(
                "SpringDbContext is configured with a non-relational provider; skipping MigrateAsync.");
            return;
        }

        // One-time: if spring.__EFMigrationsHistory is empty but
        // public.__EFMigrationsHistory has rows, copy them over. This handles
        // the transition from the old default-schema history location to the
        // pinned location (issue #363).
        var conn = context.Database.GetDbConnection();
        await SeedMigrationHistoryFromPublicSchemaAsync(conn, logger, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("Applying pending EF Core migrations to SpringDbContext.");
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("EF Core migrations applied successfully.");
    }

    /// <summary>
    /// Copies migration-history rows from <c>public.__EFMigrationsHistory</c>
    /// to <c>spring.__EFMigrationsHistory</c> when the latter is empty and the
    /// former has data. This handles existing databases created before the
    /// history table was pinned to the <c>spring</c> schema (issue #363).
    /// </summary>
    /// <remarks>
    /// Idempotent: once <c>spring.__EFMigrationsHistory</c> has rows the
    /// method is a no-op. Safe on fresh databases where neither table exists
    /// yet — both existence checks return <see langword="false"/> and the
    /// method exits immediately.
    /// </remarks>
    internal static async Task SeedMigrationHistoryFromPublicSchemaAsync(
        System.Data.Common.DbConnection conn,
        ILogger logger,
        CancellationToken cancellationToken)
    {

        // Ensure the connection is open so we can issue raw commands.
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Check whether spring.__EFMigrationsHistory exists.
        await using var springExistsCmd = conn.CreateCommand();
        springExistsCmd.CommandText =
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables " +
            "WHERE table_schema = 'spring' AND table_name = '__EFMigrationsHistory')";
        var springExists = (bool)(await springExistsCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

        if (!springExists)
        {
            // Table doesn't exist yet — MigrateAsync will create it.
            return;
        }

        // Check whether spring.__EFMigrationsHistory already has rows.
        await using var springCountCmd = conn.CreateCommand();
        springCountCmd.CommandText =
            "SELECT COUNT(*) FROM spring.\"__EFMigrationsHistory\"";
        var springCount = (long)(await springCountCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

        if (springCount > 0)
        {
            // Already seeded — nothing to do.
            return;
        }

        // Check whether public.__EFMigrationsHistory exists and has rows.
        await using var publicExistsCmd = conn.CreateCommand();
        publicExistsCmd.CommandText =
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables " +
            "WHERE table_schema = 'public' AND table_name = '__EFMigrationsHistory')";
        var publicExists = (bool)(await publicExistsCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

        if (!publicExists)
        {
            return;
        }

        await using var publicCountCmd = conn.CreateCommand();
        publicCountCmd.CommandText =
            "SELECT COUNT(*) FROM public.\"__EFMigrationsHistory\"";
        var publicCount = (long)(await publicCountCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

        if (publicCount == 0)
        {
            return;
        }

        // Copy rows from public to spring.
        await using var copyCmd = conn.CreateCommand();
        copyCmd.CommandText =
            "INSERT INTO spring.\"__EFMigrationsHistory\" " +
            "SELECT * FROM public.\"__EFMigrationsHistory\"";
        var copied = await copyCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Copied {Count} migration-history rows from public.__EFMigrationsHistory " +
            "to spring.__EFMigrationsHistory (one-time schema transition, issue #363).",
            copied);
    }
}