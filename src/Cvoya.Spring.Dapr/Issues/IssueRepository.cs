// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Issues;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Issues;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// EF-backed default implementation of <see cref="IIssueWriter"/> +
/// <see cref="IIssueReader"/> (#2160). One row per
/// <c>(TenantId, SubjectKind, SubjectId, Source, Code)</c> open
/// instance; producers re-firing the same code update the existing
/// row's <c>UpdatedAt</c> instead of creating a new one. Cleared rows
/// (<c>ClearedAt is not null</c>) stay around for short-term audit.
/// </summary>
public class IssueRepository : IIssueWriter, IIssueReader
{
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>Initialises a new <see cref="IssueRepository"/>.</summary>
    public IssueRepository(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    public async Task<Issue> UpsertAsync(
        IssueSubject subject,
        IssueSeverity severity,
        string source,
        string code,
        string title,
        string? detail,
        string? traceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var existing = await db.Issues
            .FirstOrDefaultAsync(
                e => e.SubjectKind == subject.Kind
                     && e.SubjectId == subject.Id
                     && e.Source == source
                     && e.Code == code
                     && e.ClearedAt == null,
                cancellationToken);

        var now = DateTimeOffset.UtcNow;
        if (existing is not null)
        {
            existing.Severity = severity;
            existing.Title = title;
            existing.Detail = detail;
            existing.TraceId = traceId;
            existing.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return ToIssue(existing);
        }

        var entity = new IssueEntity
        {
            Id = Guid.NewGuid(),
            SubjectKind = subject.Kind,
            SubjectId = subject.Id,
            Severity = severity,
            Source = source,
            Code = code,
            Title = title,
            Detail = detail,
            TraceId = traceId,
            CreatedAt = now,
            UpdatedAt = now,
            ClearedAt = null,
        };
        db.Issues.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return ToIssue(entity);
    }

    /// <inheritdoc />
    public async Task ClearAsync(
        IssueSubject subject,
        string source,
        string? code = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var query = db.Issues
            .Where(e => e.SubjectKind == subject.Kind
                        && e.SubjectId == subject.Id
                        && e.Source == source
                        && e.ClearedAt == null);
        if (!string.IsNullOrWhiteSpace(code))
        {
            query = query.Where(e => e.Code == code);
        }

        var rows = await query.ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var row in rows)
        {
            row.ClearedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Issue>> ListOpenAsync(
        IssueSubject subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var rows = await db.Issues
            .AsNoTracking()
            .Where(e => e.SubjectKind == subject.Kind
                        && e.SubjectId == subject.Id
                        && e.ClearedAt == null)
            .OrderBy(e => e.Severity)
            .ThenByDescending(e => e.UpdatedAt)
            .ToListAsync(cancellationToken);

        return rows.Select(ToIssue).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<IssueSubject, IssueCounts>> CountOpenAsync(
        IReadOnlyCollection<IssueSubject> subjects,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        if (subjects.Count == 0)
        {
            return new Dictionary<IssueSubject, IssueCounts>();
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        // Split by subject kind so we can index-bash against the unique
        // partial index. EF can't translate an `IN (subject) where
        // subject is a value tuple` cleanly across providers, so we
        // partition once on the .NET side.
        var unitIds = subjects
            .Where(s => s.Kind == IssueSubjectKind.Unit)
            .Select(s => s.Id)
            .ToList();
        var agentIds = subjects
            .Where(s => s.Kind == IssueSubjectKind.Agent)
            .Select(s => s.Id)
            .ToList();

        var rawCounts = new List<(IssueSubject Subject, IssueSeverity Severity, int Count)>();

        if (unitIds.Count > 0)
        {
            var unitRows = await db.Issues
                .AsNoTracking()
                .Where(e => e.SubjectKind == IssueSubjectKind.Unit
                            && unitIds.Contains(e.SubjectId)
                            && e.ClearedAt == null)
                .GroupBy(e => new { e.SubjectId, e.Severity })
                .Select(g => new { g.Key.SubjectId, g.Key.Severity, Count = g.Count() })
                .ToListAsync(cancellationToken);
            rawCounts.AddRange(unitRows.Select(r =>
                (new IssueSubject(IssueSubjectKind.Unit, r.SubjectId), r.Severity, r.Count)));
        }

        if (agentIds.Count > 0)
        {
            var agentRows = await db.Issues
                .AsNoTracking()
                .Where(e => e.SubjectKind == IssueSubjectKind.Agent
                            && agentIds.Contains(e.SubjectId)
                            && e.ClearedAt == null)
                .GroupBy(e => new { e.SubjectId, e.Severity })
                .Select(g => new { g.Key.SubjectId, g.Key.Severity, Count = g.Count() })
                .ToListAsync(cancellationToken);
            rawCounts.AddRange(agentRows.Select(r =>
                (new IssueSubject(IssueSubjectKind.Agent, r.SubjectId), r.Severity, r.Count)));
        }

        return rawCounts
            .GroupBy(r => r.Subject)
            .ToDictionary(
                g => g.Key,
                g => new IssueCounts(
                    ErrorCount: g.Where(r => r.Severity == IssueSeverity.Error).Sum(r => r.Count),
                    WarningCount: g.Where(r => r.Severity == IssueSeverity.Warning).Sum(r => r.Count)));
    }

    private static Issue ToIssue(IssueEntity e) =>
        new(
            Id: e.Id,
            Subject: new IssueSubject(e.SubjectKind, e.SubjectId),
            Severity: e.Severity,
            Source: e.Source,
            Code: e.Code,
            Title: e.Title,
            Detail: e.Detail,
            TraceId: e.TraceId,
            CreatedAt: e.CreatedAt,
            UpdatedAt: e.UpdatedAt);
}
