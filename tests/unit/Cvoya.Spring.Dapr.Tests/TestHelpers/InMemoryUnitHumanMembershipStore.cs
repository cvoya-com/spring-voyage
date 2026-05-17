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

    /// <summary>
    /// Test convenience: seed a membership row directly. Caller-supplied
    /// <paramref name="membershipId"/> defaults to a fresh Guid when
    /// omitted.
    /// </summary>
    public UnitHumanMembership Seed(
        Guid unitId,
        Guid humanId,
        string role,
        IReadOnlyList<string>? expertise = null,
        IReadOnlyList<string>? notifications = null,
        Guid? membershipId = null)
    {
        var membership = new UnitHumanMembership(
            MembershipId: membershipId ?? Guid.NewGuid(),
            HumanId: humanId,
            Role: role,
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
