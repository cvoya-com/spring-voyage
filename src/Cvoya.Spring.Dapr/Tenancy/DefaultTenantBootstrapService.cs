// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tenancy;

using System.Collections.Generic;
using System.Linq;

using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Hosted service that bootstraps the canonical <c>"default"</c> tenant
/// on host startup and invokes every registered
/// <see cref="ITenantSeedProvider"/> against it. Gated by
/// <see cref="TenancyOptions.BootstrapDefaultTenant"/>; defaults to on so
/// a fresh OSS deployment comes up with seed content present.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> The OSS schema treats tenant
/// identity as a value (the <c>tenant_id</c> column on every
/// <see cref="ITenantScopedEntity"/>) rather than a row in a tenants
/// table. There is therefore no "create the default tenant" SQL to
/// run — the literal <c>"default"</c> is materialised the moment the
/// first tenant-scoped row is written. What this service DOES do is
/// give the platform a uniform place to drive that first write per
/// subsystem: each subsystem registers an <see cref="ITenantSeedProvider"/>
/// and the bootstrap service iterates them in priority order, logging
/// every step so operators can see exactly what was seeded on the
/// first run.
/// </para>
/// <para>
/// <strong>Idempotency contract.</strong> The bootstrap runs on every
/// host startup. Each registered seed provider is responsible for
/// keeping its own work idempotent (upsert by
/// <c>(tenant_id, &lt;natural-key&gt;)</c>, never overwrite operator
/// edits). This service merely orders the call sequence, surfaces the
/// audit log, and aborts host start when a provider throws so the
/// failure is loud.
/// </para>
/// <para>
/// <strong>Lifecycle.</strong> Mirrors
/// <see cref="Cvoya.Spring.Dapr.Data.DatabaseMigrator"/>: runs once in
/// <c>StartAsync</c>, no-ops in <c>StopAsync</c>. Like the migrator it
/// is registered exactly once per deployment via the explicit
/// <see cref="DependencyInjection.ServiceCollectionExtensions.AddCvoyaSpringDefaultTenantBootstrap"/>
/// so a multi-host topology cannot run two bootstraps concurrently
/// against the same database.
/// </para>
/// <para>
/// <strong>Tenant-scope bypass.</strong> The bootstrap legitimately
/// needs to seed across the EF Core query filter even though it
/// reuses the ambient <see cref="ITenantContext"/>. To keep the audit
/// trail consistent with
/// <see cref="Cvoya.Spring.Dapr.Data.DatabaseMigrator"/> we open an
/// <see cref="ITenantScopeBypass"/> scope around the entire seed
/// pass. Seed providers that need cross-tenant reads (rare; almost
/// nothing should) inherit the bypass via async-flow.
/// </para>
/// </remarks>
public class DefaultTenantBootstrapService(
    IEnumerable<ITenantSeedProvider> seedProviders,
    IOptions<TenancyOptions> options,
    ITenantScopeBypass tenantScopeBypass,
    ILogger<DefaultTenantBootstrapService> logger) : IHostedService
{
    /// <summary>
    /// The canonical default tenant identifier seeded by this service.
    /// Mirrors <see cref="ConfiguredTenantContext.DefaultTenantId"/>
    /// so the bootstrap and the OSS tenant context cannot drift.
    /// </summary>
    public const string DefaultTenantId = ConfiguredTenantContext.DefaultTenantId;

    private readonly TenancyOptions _options = options.Value;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.BootstrapDefaultTenant)
        {
            logger.LogInformation(
                "Tenancy:BootstrapDefaultTenant disabled — skipping default-tenant bootstrap.");
            return;
        }

        // Order providers by Priority ascending. ToList() materialises
        // the enumeration once so a misconfigured provider that throws
        // on Priority is surfaced before the seed pass starts.
        var ordered = seedProviders
            .OrderBy(p => p.Priority)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToList();

        logger.LogInformation(
            "Bootstrapping default tenant '{TenantId}' with {ProviderCount} seed provider(s).",
            DefaultTenantId, ordered.Count);

        // Bootstrap runs before any per-request tenant context exists
        // and is conceptually the same kind of audited cross-tenant
        // operation the migrator runs. The bypass keeps the audit log
        // consistent and lets a private-cloud override (e.g. a
        // permission-checked TenantScopeBypass) gate the bootstrap
        // uniformly with the rest of the system-admin paths.
        using var bypass = tenantScopeBypass.BeginBypass("default-tenant bootstrap");

        foreach (var provider in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            logger.LogInformation(
                "Applying tenant seed provider '{ProviderId}' (priority {Priority}) to tenant '{TenantId}'.",
                provider.Id, provider.Priority, DefaultTenantId);

            try
            {
                await provider.ApplySeedsAsync(DefaultTenantId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Tenant seed provider '{ProviderId}' failed for tenant '{TenantId}'. Aborting bootstrap to surface the failure.",
                    provider.Id, DefaultTenantId);
                throw;
            }
        }

        logger.LogInformation(
            "Default-tenant bootstrap complete for tenant '{TenantId}' ({ProviderCount} provider(s) applied).",
            DefaultTenantId, ordered.Count);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}