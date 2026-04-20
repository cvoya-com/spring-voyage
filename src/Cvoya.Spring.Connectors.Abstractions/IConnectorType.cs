// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connectors;

using System.Text.Json;

using Cvoya.Spring.Core.AgentRuntimes;

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
/// <para>
/// Optional health hooks (<see cref="ValidateCredentialAsync"/> /
/// <see cref="VerifyContainerBaselineAsync"/>) let connectors that carry
/// authentication or rely on host-side tooling report current health to the
/// platform. Both default to a no-op (returning <c>null</c>) so connectors
/// that do not carry auth (Arxiv, WebSearch) inherit a "nothing to check"
/// signal without any extra code. Connectors that DO carry auth (GitHub
/// App credentials, OAuth tokens) should override
/// <see cref="ValidateCredentialAsync"/>; connectors that depend on a host
/// binary or network reachability beyond outbound HTTP should override
/// <see cref="VerifyContainerBaselineAsync"/>.
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

    /// <summary>
    /// Validates the connector's stored credential against the backing
    /// service. The default implementation returns <c>null</c> — connectors
    /// that do not carry authentication (e.g. Arxiv, WebSearch) inherit it
    /// untouched, signalling "nothing to validate" to the credential-health
    /// store. Connectors that DO carry auth (e.g. GitHub App credentials,
    /// OAuth tokens) override this hook to perform a cheap end-to-end
    /// round-trip against the backing service and translate the response
    /// into a <see cref="CredentialValidationResult"/>.
    /// </summary>
    /// <remarks>
    /// Implementations must surface transport-level failures as
    /// <see cref="CredentialValidationStatus.NetworkError"/> rather than
    /// throwing. Authentication failures (401/403 from the backing service)
    /// translate to <see cref="CredentialValidationStatus.Invalid"/>. Empty
    /// or absent stored credentials should return a result with
    /// <see cref="CredentialValidationStatus.Unknown"/> rather than null,
    /// so the caller can distinguish "this connector cannot validate" from
    /// "this connector has nothing configured yet".
    /// </remarks>
    /// <param name="credential">
    /// The candidate credential to validate. May be empty when the
    /// connector authenticates from its own multi-part configuration
    /// (e.g. GitHub App ID + private key) rather than a single token. In
    /// that case implementations may ignore the parameter and validate
    /// against their stored configuration directly.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the validation.</param>
    /// <returns>
    /// A <see cref="CredentialValidationResult"/> describing the outcome,
    /// or <c>null</c> when this connector does not require credentials and
    /// has nothing to check.
    /// </returns>
    Task<CredentialValidationResult?> ValidateCredentialAsync(
        string credential,
        CancellationToken cancellationToken = default)
        => Task.FromResult<CredentialValidationResult?>(null);

    /// <summary>
    /// Probes the host process / container for any baseline tooling the
    /// connector requires beyond outbound HTTP — for example a CLI binary
    /// on PATH or a reachable side-car. The default implementation returns
    /// <c>null</c> — connectors that have no host-side baseline (every
    /// current connector talks straight to a remote API) inherit it
    /// untouched, signalling "nothing to verify" to the install / wizard
    /// flow.
    /// </summary>
    /// <remarks>
    /// Implementations should never throw; surface every failed check as
    /// an entry in <see cref="ContainerBaselineCheckResult.Errors"/> with a
    /// human-readable explanation the operator can act on. A connector that
    /// genuinely has nothing to verify (e.g. the GitHub connector, which
    /// only needs outbound HTTPS) may explicitly return a passing result so
    /// the install flow surfaces "checked, OK" instead of "skipped".
    /// </remarks>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <returns>
    /// A <see cref="ContainerBaselineCheckResult"/> describing the outcome,
    /// or <c>null</c> when this connector has no baseline to verify.
    /// </returns>
    Task<ContainerBaselineCheckResult?> VerifyContainerBaselineAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult<ContainerBaselineCheckResult?>(null);
}