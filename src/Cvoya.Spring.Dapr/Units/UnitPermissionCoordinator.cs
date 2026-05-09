// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Units;

using Cvoya.Spring.Dapr.Auth;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default singleton implementation of <see cref="IUnitPermissionCoordinator"/>.
/// Owns the unit's permission-inheritance flag — the per-unit
/// <see cref="UnitPermissionInheritance"/> value that decides whether
/// ancestor grants cascade through this unit.
/// </summary>
/// <remarks>
/// Pre-#2044 this coordinator also owned the per-unit (humanId → entry) map.
/// After #2044 / ADR-0040 those grants moved to the
/// <c>unit_human_permissions</c> EF table; the coordinator's role is now
/// limited to the inheritance flag (which stays on actor state for v0.1 and
/// moves to <c>unit_live_config</c> in #2049). The coordinator is stateless
/// with respect to any individual unit and is safe to register as a
/// singleton.
/// </remarks>
public class UnitPermissionCoordinator(
    ILogger<UnitPermissionCoordinator> logger) : IUnitPermissionCoordinator
{
    /// <inheritdoc />
    public async Task<UnitPermissionInheritance> GetPermissionInheritanceAsync(
        string unitActorId,
        Func<CancellationToken, Task<UnitPermissionInheritance?>> getInheritance,
        CancellationToken cancellationToken = default)
    {
        var value = await getInheritance(cancellationToken);

        // ADR-0013: absent state key means Inherit — ancestor grants cascade
        // by default; only explicit Isolated opts out.
        return value ?? UnitPermissionInheritance.Inherit;
    }

    /// <inheritdoc />
    public async Task SetPermissionInheritanceAsync(
        string unitActorId,
        UnitPermissionInheritance inheritance,
        Func<UnitPermissionInheritance, CancellationToken, Task> persistInheritance,
        Func<CancellationToken, Task> removeInheritance,
        CancellationToken cancellationToken = default)
    {
        if (inheritance == UnitPermissionInheritance.Inherit)
        {
            // Represent the default as an absent row so clearing the flag
            // returns to the default without leaving a no-op state entry.
            await removeInheritance(cancellationToken);
        }
        else
        {
            await persistInheritance(inheritance, cancellationToken);
        }

        logger.LogInformation(
            "Unit {ActorId} permission inheritance set to {Inheritance}",
            unitActorId, inheritance);
    }
}
