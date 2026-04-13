// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connectors;

using System.Text.Json;

using Microsoft.AspNetCore.Routing;

/// <summary>
/// Describes a connector type — a class of external system (GitHub, Slack,
/// Linear, ...) a unit can be bound to. The API layer consumes this
/// abstraction via DI and never imports any concrete connector package, so
/// a new connector lands by registering one more <see cref="IConnectorType"/>
/// implementation in DI and shipping its package alongside.
/// </summary>
/// <remarks>
/// <para>
/// Each connector is identified by both a stable <see cref="TypeId"/>
/// (persisted with every unit binding so a slug rename never breaks existing
/// data) and a human-readable <see cref="Slug"/> (used in URL paths for
/// readability). Endpoints that accept <c>{slugOrId}</c> try to parse the
/// argument as a <see cref="Guid"/> first and fall back to slug lookup.
/// </para>
/// <para>
/// Implementations attach their connector-specific routes (typed per-unit
/// config GET/PUT, typed actions, config-schema endpoint) by overriding
/// <see cref="MapRoutes(IEndpointRouteBuilder)"/>. The
/// <paramref name="group"/> argument passed in by the host is already
/// pre-scoped to <c>/api/v1/connectors/{slug}</c>, so implementations map
/// relative routes (e.g. <c>units/{unitId}/config</c>) and stay unaware of
/// the outer path structure.
/// </para>
/// <para>
/// Lifecycle hooks (<see cref="OnUnitStartingAsync"/> /
/// <see cref="OnUnitStoppingAsync"/>) let a connector react to unit start
/// and stop transitions — for example, the GitHub connector registers a
/// webhook on the configured repository when the unit transitions to
/// Running and tears it down on stop. The generic Host.Api lifecycle path
/// dispatches these hooks without knowing anything about the connector type.
/// </para>
/// </remarks>
public interface IConnectorType
{
    /// <summary>
    /// Stable identity for this connector type. Persisted on every unit
    /// binding so renames of <see cref="Slug"/> never break stored data.
    /// </summary>
    Guid TypeId { get; }

    /// <summary>
    /// URL-safe, human-readable identifier (e.g. <c>github</c>). Used in
    /// connector-owned route paths and as the registry key on the web side.
    /// </summary>
    string Slug { get; }

    /// <summary>
    /// Human-facing display name.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Short description used by the wizard and unit-config UI.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// The CLR type of the connector's per-unit config payload. Surfaced
    /// so the OpenAPI emitter can derive a JSON Schema when
    /// <see cref="GetConfigSchemaAsync"/> is not overridden.
    /// </summary>
    Type ConfigType { get; }

    /// <summary>
    /// Attaches connector-specific routes to the supplied group, which the
    /// host pre-scopes to <c>/api/v1/connectors/{slug}</c>. Implementations
    /// typically map:
    /// <list type="bullet">
    ///   <item><description><c>GET units/{unitId}/config</c> — typed per-unit config read</description></item>
    ///   <item><description><c>PUT units/{unitId}/config</c> — typed upsert (binds type and writes config atomically)</description></item>
    ///   <item><description><c>POST actions/{actionName}</c> — connector-scoped actions</description></item>
    ///   <item><description><c>GET config-schema</c> — JSON Schema describing the config shape</description></item>
    /// </list>
    /// </summary>
    /// <param name="group">The route group pre-scoped to <c>/api/v1/connectors/{slug}</c>.</param>
    void MapRoutes(IEndpointRouteBuilder group);

    /// <summary>
    /// Returns a JSON Schema describing <see cref="ConfigType"/>. Override
    /// to ship a hand-written schema; otherwise the host derives one from
    /// <see cref="ConfigType"/> via reflection / OpenAPI component emission.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<JsonElement?> GetConfigSchemaAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Called when a unit is transitioning to <c>Running</c> and is bound
    /// to this connector type. Implementations register any external-system
    /// resources (e.g. webhooks) the binding requires. Failures should be
    /// logged but must not throw — the unit lifecycle continues regardless.
    /// </summary>
    /// <param name="unitId">The id of the unit being started.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task OnUnitStartingAsync(string unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Called when a unit is transitioning to <c>Stopped</c> and was bound
    /// to this connector type. Implementations tear down any external-system
    /// resources created in <see cref="OnUnitStartingAsync"/>.
    /// </summary>
    /// <param name="unitId">The id of the unit being stopped.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task OnUnitStoppingAsync(string unitId, CancellationToken cancellationToken = default);
}