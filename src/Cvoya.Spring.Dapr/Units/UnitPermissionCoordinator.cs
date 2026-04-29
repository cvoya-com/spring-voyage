// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Units;

using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default singleton implementation of <see cref="IUnitPermissionCoordinator"/>.
/// Owns the permission-management concern extracted from <c>UnitActor</c>:
/// granting, revoking, and querying direct human permission entries, and
/// reading / writing the <see cref="UnitPermissionInheritance"/> flag that
/// controls ADR-0013's hierarchy-aware resolution behaviour.
/// </summary>
/// <remarks>
/// <para>
/// The coordinator is stateless with respect to any individual unit — it
/// operates entirely through the per-call delegates and the injected logger.
/// This makes it safe to register as a singleton and share across all
/// <c>UnitActor</c> instances.
/// </para>
/// <para>
/// ADR-0013 establishes the inheritance model (nearest-grant-wins,
/// ancestor-cascade-by-default, fail-closed). The actual hierarchy walk lives
/// in <c>IPermissionService.ResolveEffectivePermissionAsync</c>; this
/// coordinator owns only the per-unit state mutations that feed that walk.
/// </para>
/// </remarks>
public class UnitPermissionCoordinator(
    ILogger<UnitPermissionCoordinator> logger) : IUnitPermissionCoordinator
{
    /// <inheritdoc />
    public async Task SetHumanPermissionAsync(
        string unitActorId,
        string humanId,
        UnitPermissionEntry entry,
        Func<CancellationToken, Task<Dictionary<string, UnitPermissionEntry>>> getPermissions,
        Func<Dictionary<string, UnitPermissionEntry>, CancellationToken, Task> persistPermissions,
        CancellationToken cancellationToken = default)
    {
        var permissions = await getPermissions(cancellationToken);
        permissions[humanId] = entry;
        await persistPermissions(permissions, cancellationToken);

        logger.LogInformation(
            "Unit {ActorId} set permission for human {HumanId} to {Permission}",
            unitActorId, humanId, entry.Permission);
    }

    /// <inheritdoc />
    public async Task<PermissionLevel?> GetHumanPermissionAsync(
        string unitActorId,
        string humanId,
        Func<CancellationToken, Task<Dictionary<string, UnitPermissionEntry>>> getPermissions,
        CancellationToken cancellationToken = default)
    {
        var permissions = await getPermissions(cancellationToken);
        return permissions.TryGetValue(humanId, out var entry) ? entry.Permission : null;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveHumanPermissionAsync(
        string unitActorId,
        string humanId,
        Func<CancellationToken, Task<Dictionary<string, UnitPermissionEntry>>> getPermissions,
        Func<Dictionary<string, UnitPermissionEntry>, CancellationToken, Task> persistPermissions,
        CancellationToken cancellationToken = default)
    {
        var permissions = await getPermissions(cancellationToken);
        if (!permissions.Remove(humanId))
        {
            // Idempotent: removing an entry that does not exist is a no-op.
            // The DELETE endpoint still returns 204 to match `spring unit
            // humans remove` ergonomics — the CLI should not have to branch
            // on 404 vs 204 when the desired end state is "no such entry".
            return false;
        }

        await persistPermissions(permissions, cancellationToken);

        logger.LogInformation(
            "Unit {ActorId} removed permission for human {HumanId}",
            unitActorId, humanId);

        return true;
    }

    /// <inheritdoc />
    public async Task<UnitPermissionEntry[]> GetHumanPermissionsAsync(
        string unitActorId,
        Func<CancellationToken, Task<Dictionary<string, UnitPermissionEntry>>> getPermissions,
        CancellationToken cancellationToken = default)
    {
        var permissions = await getPermissions(cancellationToken);
        return permissions.Values.ToArray();
    }

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