// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.TestHelpers;

using System.Collections.Concurrent;

using Cvoya.Spring.Core.Units;

/// <summary>
/// In-memory test double for <see cref="IUnitHumanMembershipStore"/>.
/// Stores membership rows in a thread-safe per-unit list so unit tests can
/// seed packages-declared human entries without standing up a Postgres
/// container. The EF-backed integration behaviour is covered separately
/// by tests that use a real <see cref="Cvoya.Spring.Dapr.Data.SpringDbContext"/>.
/// </summary>
public sealed class InMemoryUnitHumanMembershipStore : IUnitHumanMembershipStore
{
    private readonly ConcurrentDictionary<Guid, List<UnitHumanMembership>> _byUnit = new();

    /// <inheritdoc />
    public Task<IReadOnlyList<UnitHumanMembership>> ListByUnitAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        if (!_byUnit.TryGetValue(unitId, out var list))
        {
            return Task.FromResult<IReadOnlyList<UnitHumanMembership>>(Array.Empty<UnitHumanMembership>());
        }
        lock (list)
        {
            return Task.FromResult<IReadOnlyList<UnitHumanMembership>>(list.ToList());
        }
    }

    /// <inheritdoc />
    public Task<UnitHumanMembership?> GetAsync(
        Guid unitId,
        Guid humanId,
        CancellationToken cancellationToken = default)
    {
        if (!_byUnit.TryGetValue(unitId, out var list))
        {
            return Task.FromResult<UnitHumanMembership?>(null);
        }
        lock (list)
        {
            var match = list.FirstOrDefault(m => m.HumanId == humanId);
            return Task.FromResult<UnitHumanMembership?>(match);
        }
    }

    /// <inheritdoc />
    public Task<UnitHumanMembership> UpsertAsync(
        Guid unitId,
        Guid humanId,
        IReadOnlyList<string> roles,
        IReadOnlyList<string> expertise,
        IReadOnlyList<string> notifications,
        CancellationToken cancellationToken = default)
    {
        var list = _byUnit.GetOrAdd(unitId, _ => new List<UnitHumanMembership>());
        lock (list)
        {
            var existingIndex = list.FindIndex(m => m.HumanId == humanId);
            UnitHumanMembership updated;
            if (existingIndex >= 0)
            {
                var existing = list[existingIndex];
                updated = existing with
                {
                    Roles = roles.ToList(),
                    Expertise = expertise.ToList(),
                    Notifications = notifications.ToList(),
                };
                list[existingIndex] = updated;
            }
            else
            {
                updated = new UnitHumanMembership(
                    MembershipId: Guid.NewGuid(),
                    HumanId: humanId,
                    Roles: roles.ToList(),
                    Expertise: expertise.ToList(),
                    Notifications: notifications.ToList());
                list.Add(updated);
            }
            return Task.FromResult(updated);
        }
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(
        Guid unitId,
        Guid humanId,
        CancellationToken cancellationToken = default)
    {
        if (!_byUnit.TryGetValue(unitId, out var list))
        {
            return Task.FromResult(false);
        }
        lock (list)
        {
            var index = list.FindIndex(m => m.HumanId == humanId);
            if (index < 0)
            {
                return Task.FromResult(false);
            }
            list.RemoveAt(index);
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// Test convenience: seed a membership row directly. Caller-supplied
    /// <paramref name="membershipId"/> defaults to a fresh Guid when
    /// omitted.
    /// </summary>
    public UnitHumanMembership Seed(
        Guid unitId,
        Guid humanId,
        IReadOnlyList<string>? roles = null,
        IReadOnlyList<string>? expertise = null,
        IReadOnlyList<string>? notifications = null,
        Guid? membershipId = null)
    {
        var membership = new UnitHumanMembership(
            MembershipId: membershipId ?? Guid.NewGuid(),
            HumanId: humanId,
            Roles: roles ?? Array.Empty<string>(),
            Expertise: expertise ?? Array.Empty<string>(),
            Notifications: notifications ?? Array.Empty<string>());

        var list = _byUnit.GetOrAdd(unitId, _ => new List<UnitHumanMembership>());
        lock (list)
        {
            list.Add(membership);
        }
        return membership;
    }
}
