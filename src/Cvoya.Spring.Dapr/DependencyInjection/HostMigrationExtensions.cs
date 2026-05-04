// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.DependencyInjection;

using Cvoya.Spring.Dapr.Data;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Host-side extensions that close intra-process startup-ordering races
/// between <see cref="DatabaseMigrator"/> and other
/// <see cref="IHostedService"/> registrations (#1608).
/// </summary>
/// <remarks>
/// <para>
/// .NET's Generic Host invokes <see cref="IHostedService.StartAsync"/> in
/// registration order. <see cref="DatabaseMigrator"/> is registered last
/// in the Worker composition, but several other hosted services in the
/// shared <c>AddCvoyaSpringDapr</c> graph register earlier — and on a
/// fresh PostgreSQL volume one of them queries
/// <c>spring.unit_definitions</c> before the migrator has had a chance
/// to create the table, logging a single
/// <c>42P01: relation "spring.unit_definitions" does not exist</c>
/// per cold start. The cross-process counterpart was fixed in
/// <see href="https://github.com/cvoya-com/spring-voyage/pull/1607">#1607</see>;
/// this extension fixes the intra-process counterpart.
/// </para>
/// <para>
/// The fix is order-independent: hosts call
/// <see cref="MigrateSpringDatabaseAsync(IHost, CancellationToken)"/>
/// between <c>app.Build()</c> and <c>app.RunAsync()</c> to drive the
/// migrator to completion before the Generic Host starts any hosted
/// service. The migrator's hosted-service registration stays in place
/// — its <see cref="IHostedService.StartAsync"/> is idempotent
/// (<see cref="DatabaseMigrator.HasRun"/>) and a subsequent invocation
/// from the host is a no-op.
/// </para>
/// </remarks>
public static class HostMigrationExtensions
{
    /// <summary>
    /// Resolves the <see cref="DatabaseMigrator"/> hosted-service
    /// registration on <paramref name="host"/> (if any) and runs it
    /// synchronously to completion, ensuring every pending EF Core
    /// migration is applied before the host's other hosted services
    /// start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No-op when the host did not register the migrator (the API host
    /// in the OSS topology, every test harness that strips the
    /// migrator before <c>BuildServiceProvider</c>). Idempotent on the
    /// migrator side: the Generic Host's later
    /// <see cref="IHostedService.StartAsync"/> invocation short-
    /// circuits via <see cref="DatabaseMigrator.HasRun"/>.
    /// </para>
    /// <para>
    /// Migration failure is fatal — the exception propagates so the
    /// host fails fast. <c>Program.cs</c> catches it, logs, and exits
    /// the process so the container orchestrator can restart.
    /// </para>
    /// </remarks>
    /// <param name="host">The built host.</param>
    /// <param name="cancellationToken">Token forwarded to the migrator.</param>
    public static async Task MigrateSpringDatabaseAsync(
        this IHost host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        // Iterate every registered IHostedService and pick the migrator.
        // Resolving DatabaseMigrator directly would miss a
        // pre-registered private-cloud subclass; matching by runtime
        // type covers both shapes without forcing an extra registration.
        var migrators = host.Services
            .GetServices<IHostedService>()
            .OfType<DatabaseMigrator>()
            .ToList();

        // Zero or one is the only contract the OSS topology has ever
        // honoured (the API host omits the migrator; the Worker
        // registers it exactly once via AddCvoyaSpringDatabaseMigrator).
        // If a downstream host wires more than one we still drive each
        // to completion — the per-instance HasRun flag keeps the body
        // idempotent against the host's later StartAsync calls.
        foreach (var migrator in migrators)
        {
            await migrator.StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}