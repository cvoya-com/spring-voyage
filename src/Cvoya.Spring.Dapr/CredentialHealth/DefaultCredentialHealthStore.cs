// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.CredentialHealth;

using Cvoya.Spring.Core.CredentialHealth;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default EF Core-backed implementation of
/// <see cref="ICredentialHealthStore"/>. Persists rows to
/// <c>credential_health</c> (composite PK
/// <c>(tenant_id, kind, subject_id, secret_name)</c>).
/// </summary>
public sealed class DefaultCredentialHealthStore(
    SpringDbContext dbContext,
    ITenantContext tenantContext,
    ILogger<DefaultCredentialHealthStore> logger) : ICredentialHealthStore
{
    /// <inheritdoc />
    public async Task<Core.CredentialHealth.CredentialHealth> RecordAsync(
        CredentialHealthKind kind,
        string subjectId,
        string secretName,
        CredentialHealthStatus status,
        string? lastError,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);

        var tenantId = tenantContext.CurrentTenantId;
        var now = DateTimeOffset.UtcNow;

        var existing = await dbContext.CredentialHealth
            .FirstOrDefaultAsync(
                e => e.TenantId == tenantId
                    && e.Kind == kind
                    && e.SubjectId == subjectId
                    && e.SecretName == secretName,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            var entity = new CredentialHealthEntity
            {
                TenantId = tenantId,
                Kind = kind,
                SubjectId = subjectId,
                SecretName = secretName,
                Status = status,
                LastError = Truncate(lastError),
                LastChecked = now,
            };
            dbContext.CredentialHealth.Add(entity);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Credential-health: recorded new row for {Kind} '{SubjectId}' / '{SecretName}' — {Status}.",
                kind, subjectId, secretName, status);
            return Project(entity);
        }

        existing.Status = status;
        existing.LastError = Truncate(lastError);
        existing.LastChecked = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Credential-health: updated {Kind} '{SubjectId}' / '{SecretName}' — {Status}.",
            kind, subjectId, secretName, status);
        return Project(existing);
    }

    /// <inheritdoc />
    public async Task<Core.CredentialHealth.CredentialHealth?> GetAsync(
        CredentialHealthKind kind,
        string subjectId,
        string secretName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);

        var row = await dbContext.CredentialHealth
            .FirstOrDefaultAsync(
                e => e.Kind == kind
                    && e.SubjectId == subjectId
                    && e.SecretName == secretName,
                cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : Project(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Core.CredentialHealth.CredentialHealth>> ListAsync(
        CredentialHealthKind? kind,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.CredentialHealth.AsQueryable();
        if (kind is { } k)
        {
            query = query.Where(e => e.Kind == k);
        }

        var rows = await query
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.SubjectId)
            .ThenBy(e => e.SecretName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(Project).ToArray();
    }

    private static Core.CredentialHealth.CredentialHealth Project(CredentialHealthEntity row)
        => new(
            TenantId: row.TenantId,
            Kind: row.Kind,
            SubjectId: row.SubjectId,
            SecretName: row.SecretName,
            Status: row.Status,
            LastError: row.LastError,
            LastChecked: row.LastChecked);

    private static string? Truncate(string? value)
    {
        // Mirror the entity's HasMaxLength(2048) limit so a verbose
        // upstream error doesn't surface as a DB truncation failure on
        // SaveChangesAsync.
        const int MaxLength = 2048;
        if (value is null || value.Length <= MaxLength)
        {
            return value;
        }
        return value[..MaxLength];
    }
}