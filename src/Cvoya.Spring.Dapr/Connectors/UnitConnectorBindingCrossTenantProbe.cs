// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Connectors;

using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IConnectorBindingCrossTenantProbe"/> backed by the
/// EF binding repository. Walks every <c>unit_connector_bindings</c> row
/// for the supplied connector type across <em>all</em> tenants
/// (<c>IgnoreQueryFilters</c>), filters out the current tenant, and
/// returns <c>true</c> when any remaining row's typed-config JSON exposes
/// a <c>repo</c> field equal to the supplied fingerprint (case-insensitive).
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-connector fingerprint shape.</b> The probe matches on a
/// <c>repo</c> string column inside the binding's typed-config JSON,
/// which is the canonical GitHub addressing fingerprint per ADR-0047 §11
/// (the qualified <c>owner/repo</c> form). Connectors that introduce
/// their own cross-tenant uniqueness requirement register their own
/// probe implementation and the host wires the appropriate one in DI.
/// The current OSS implementation is GitHub-only — calls with a
/// non-GitHub type id (or a payload whose JSON has no <c>repo</c> field)
/// return <c>false</c> trivially, because nothing about that type's
/// addressing collides cross-tenant under today's binding shapes.
/// </para>
/// <para>
/// <b>Race window.</b> The probe runs synchronously inside the binding-
/// create handler before the row is inserted. Hosts that require
/// stricter race protection register a probe implementation that wraps
/// the call in a SERIALIZABLE transaction or a SQL advisory lock; the
/// abstraction is the seam.
/// </para>
/// </remarks>
public class UnitConnectorBindingCrossTenantProbe(
    IServiceScopeFactory scopeFactory,
    ITenantContext tenantContext,
    ILogger<UnitConnectorBindingCrossTenantProbe> logger)
    : IConnectorBindingCrossTenantProbe
{
    private const string RepoJsonField = "Repo";
    private const string RepoJsonFieldLowerCase = "repo";

    /// <inheritdoc />
    public async Task<bool> HasCrossTenantBindingAsync(
        Guid connectorTypeId,
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        var currentTenantId = tenantContext.CurrentTenantId;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        // IgnoreQueryFilters is the only way to read across tenants — the
        // default query filter on UnitConnectorBindingEntity restricts to
        // the ambient tenant. We restore the tenant restriction in the
        // Where clause below ("not this tenant") so the probe sees only
        // other-tenant rows.
        var otherTenantRows = await db.UnitConnectorBindings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(b => b.ConnectorType == connectorTypeId && b.TenantId != currentTenantId)
            .Select(b => new { b.TenantId, b.Config })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (otherTenantRows.Count == 0)
        {
            return false;
        }

        foreach (var row in otherTenantRows)
        {
            if (!TryExtractRepoFingerprint(row.Config, out var rowFingerprint))
            {
                continue;
            }
            if (string.Equals(rowFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "Cross-tenant binding collision detected: connector={ConnectorTypeId} fingerprint={Fingerprint} " +
                    "claimed by tenant {OtherTenantId}; rejecting new binding for tenant {CurrentTenantId}.",
                    connectorTypeId, fingerprint, row.TenantId, currentTenantId);
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractRepoFingerprint(JsonElement config, out string fingerprint)
    {
        fingerprint = string.Empty;
        if (config.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // The JSON is serialised with the System.Text.Json web defaults
        // (camelCase + ordinal). UnitGitHubConfig's "Repo" PascalCase
        // property serialises to "repo"; check both casings defensively
        // in case a future serialisation tweak lands a different name.
        if (config.TryGetProperty(RepoJsonFieldLowerCase, out var repoEl)
            || config.TryGetProperty(RepoJsonField, out repoEl))
        {
            if (repoEl.ValueKind == JsonValueKind.String)
            {
                fingerprint = repoEl.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(fingerprint);
            }
        }

        return false;
    }
}
