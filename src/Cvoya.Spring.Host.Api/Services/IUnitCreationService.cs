// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Centralised unit-creation pipeline used by <c>POST /api/v1/units</c> (direct
/// creation) and the package-install Phase-2 activator (<see cref="DefaultPackageArtefactActivator"/>).
/// Keeping the actor-create + directory-register + member-wiring logic in a single
/// place avoids duplication and gives downstream consumers (e.g. the private cloud
/// repo) a single extension point to wrap.
/// </summary>
public interface IUnitCreationService
{
    /// <summary>
    /// Creates a unit from the caller-supplied fields. No members are added —
    /// callers wire members up through the existing member endpoints.
    /// If <see cref="CreateUnitRequest.Connector"/> is supplied, the unit is
    /// bound to that connector atomically; a binding failure rolls back the
    /// partial unit and surfaces a <see cref="UnitCreationBindingException"/>.
    /// </summary>
    Task<UnitCreationResult> CreateAsync(
        CreateUnitRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates a unit from a parsed unit manifest, forwarding members declared
    /// in the manifest to the unit actor. Warnings for unsupported manifest
    /// sections are surfaced through <see cref="UnitCreationResult.Warnings"/>.
    /// The <paramref name="connector"/> parameter is optional and follows the
    /// same transactional semantics as <see cref="CreateAsync"/>.
    /// </summary>
    Task<UnitCreationResult> CreateFromManifestAsync(
        Manifest.UnitManifest manifest,
        UnitCreationOverrides overrides,
        CancellationToken cancellationToken,
        Models.UnitConnectorBindingRequest? connector = null);
}

/// <summary>
/// Optional caller-supplied overrides applied on top of values derived from a
/// manifest (YAML import or template). Each field is optional; <c>null</c> means
/// "use the manifest value".
/// </summary>
/// <param name="DisplayName">Override for the unit's display name.</param>
/// <param name="Color">Override for the unit's UI colour.</param>
/// <param name="Model">Override for the unit's default model hint.</param>
/// <param name="Name">
/// Override for the unit's canonical <c>name</c> (address path). When
/// non-empty the created unit uses this value instead of the manifest's
/// <c>name</c> field — lets callers instantiate the same template more than
/// once without colliding on the unique-name constraint. See #325.
/// </param>
public record UnitCreationOverrides(
    string? DisplayName = null,
    string? Color = null,
    string? Model = null,
    string? Name = null,
    string? Tool = null,
    string? Provider = null,
    string? Hosting = null,
    IReadOnlyList<string>? ParentUnitIds = null,
    bool? IsTopLevel = null);

/// <summary>
/// Outcome of a unit-creation call.
/// </summary>
/// <param name="Unit">The created unit's canonical response projection.</param>
/// <param name="Warnings">
/// Non-fatal warnings collected during creation (e.g. manifest sections parsed
/// but not yet applied, members skipped because their address was unresolvable).
/// Always non-null; empty when no warnings were produced.
/// </param>
/// <param name="MembersAdded">Number of members successfully added.</param>
public record UnitCreationResult(
    UnitResponse Unit,
    IReadOnlyList<string> Warnings,
    int MembersAdded);

/// <summary>
/// Thrown by <see cref="IUnitCreationService"/> when a caller-supplied
/// connector binding cannot be applied. The service rolls back the partial
/// unit (unregisters the directory entry) before throwing, so no residual
/// state remains to clean up. The <see cref="Reason"/> distinguishes between
/// validation problems (404 on an unknown type id, 400 on malformed bodies)
/// and downstream failures (502 on store errors).
/// </summary>
/// <param name="Reason">
/// Machine-readable classification of the failure, so the endpoint layer can
/// map it to a ProblemDetails status code without inspecting message text.
/// </param>
/// <param name="Message">Human-readable error detail.</param>
public class UnitCreationBindingException : System.Exception
{
    /// <summary>
    /// Initialises a new <see cref="UnitCreationBindingException"/>.
    /// </summary>
    public UnitCreationBindingException(UnitCreationBindingFailureReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>
    /// Initialises a new <see cref="UnitCreationBindingException"/> with an inner exception.
    /// </summary>
    public UnitCreationBindingException(UnitCreationBindingFailureReason reason, string message, System.Exception inner)
        : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>
    /// Why the binding failed.
    /// </summary>
    public UnitCreationBindingFailureReason Reason { get; }
}

/// <summary>
/// Thrown by <see cref="IUnitCreationService"/> when the caller asks to
/// attach the new unit to a parent unit that is not registered (or not
/// visible in the current tenant). Surfaces as ProblemDetails 404 from
/// the creation endpoints so the caller can correct the request. Mirrors
/// the unknown-unit 404 branch on the agent create path.
/// </summary>
public class UnknownParentUnitException : System.Exception
{
    /// <summary>
    /// Initialises a new <see cref="UnknownParentUnitException"/>.
    /// </summary>
    /// <param name="parentUnitId">The unresolved parent-unit id.</param>
    public UnknownParentUnitException(string parentUnitId)
        : base($"Parent unit '{parentUnitId}' not found")
    {
        ParentUnitId = parentUnitId;
    }

    /// <summary>The unit id that did not resolve.</summary>
    public string ParentUnitId { get; }
}

/// <summary>
/// Thrown by <see cref="IUnitCreationService"/> when the create request
/// fails the "every unit has a parent" invariant — either neither a
/// parent list nor the explicit <c>IsTopLevel</c> flag was supplied, or
/// both were. Surfaces as ProblemDetails 400. Distinct from
/// <see cref="Core.Units.UnitParentRequiredException"/>, which fires on
/// the removal side when the last parent edge would be stripped.
/// </summary>
public class InvalidUnitParentRequestException : System.Exception
{
    /// <summary>Initialises a new <see cref="InvalidUnitParentRequestException"/>.</summary>
    public InvalidUnitParentRequestException(string message) : base(message)
    {
    }
}

/// <summary>
/// Thrown when the requested unit name is already registered in the directory.
/// Surfaces as a ProblemDetails 400 from the creation endpoints. Introduced by
/// #325 to reject name collisions with a clear 400 response rather than silently
/// overwriting the existing directory entry (the in-memory directory service is a
/// last-writer-wins map; this check makes the collision an explicit error).
/// </summary>
public class DuplicateUnitNameException : System.Exception
{
    /// <summary>
    /// Initialises a new <see cref="DuplicateUnitNameException"/>.
    /// </summary>
    /// <param name="name">The unit name that collided with an existing entry.</param>
    public DuplicateUnitNameException(string name)
        : base($"A unit named '{name}' already exists.")
    {
        Name = name;
    }

    /// <summary>The unit name that collided with an existing entry.</summary>
    public string Name { get; }
}

/// <summary>
/// Classifies <see cref="UnitCreationBindingException"/> outcomes so
/// endpoints can translate them into ProblemDetails responses without
/// parsing strings.
/// </summary>
public enum UnitCreationBindingFailureReason
{
    /// <summary>
    /// The requested connector type id / slug is not registered. Map to
    /// HTTP 404 Not Found.
    /// </summary>
    UnknownConnectorType,

    /// <summary>
    /// The binding request is syntactically invalid (missing type id/slug,
    /// empty config). Map to HTTP 400 Bad Request.
    /// </summary>
    InvalidBindingRequest,

    /// <summary>
    /// The connector config store threw while persisting the binding. Map
    /// to HTTP 502 Bad Gateway — the downstream store is unreachable or
    /// unhealthy.
    /// </summary>
    StoreFailure,
}