// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Orchestration;

/// <summary>
/// Turns a unit's declared orchestration-strategy intent — from the
/// manifest (<c>orchestration.strategy</c>, #491) and the unit policy
/// (<c>UnitPolicy.LabelRouting</c>, #389) — into a concrete
/// <see cref="IOrchestrationStrategy"/> instance ready to orchestrate a
/// single domain message. Consumed by <c>UnitActor.HandleDomainMessageAsync</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Selection precedence (highest wins):</b>
/// <list type="number">
///   <item>The manifest's explicit <c>orchestration.strategy</c> key, when
///         one is declared AND the DI container has a keyed
///         <see cref="IOrchestrationStrategy"/> registered under that key.</item>
///   <item><c>label-routed</c>, inferred when the unit has a non-null
///         <c>UnitPolicy.LabelRouting</c> slot but no manifest-declared
///         strategy (see ADR-0007 revisit criterion). Only applies when the
///         <c>label-routed</c> key is actually registered — a host that
///         removed the registration falls through to step 3.</item>
///   <item>The unkeyed default <see cref="IOrchestrationStrategy"/>
///         registration (the platform's <c>ai</c> strategy by default; a
///         private-cloud host can replace it via <c>TryAdd*</c>).</item>
/// </list>
/// </para>
/// <para>
/// Resolution is per-call (per domain message) so strategies registered
/// <c>Scoped</c> — like <c>LabelRoutedOrchestrationStrategy</c>, which
/// depends on the scoped <c>IUnitPolicyRepository</c> — pick up hot edits
/// without actor recycling. The resolver owns the scope lifetime internally
/// and disposes it when the returned <see cref="OrchestrationStrategyLease"/>
/// is disposed.
/// </para>
/// <para>
/// An unknown manifest strategy key (declared but not registered) is
/// treated as a <em>misconfiguration</em>, not a routing bug: the resolver
/// falls through to the policy inference and then the unkeyed default, and
/// logs a warning so operators can correct the manifest. This keeps a
/// rename or removal of a host-registered strategy from breaking every
/// message dispatched to the affected unit — degraded-but-alive beats
/// hard-fail for orchestration.
/// </para>
/// </remarks>
public interface IOrchestrationStrategyResolver
{
    /// <summary>
    /// Resolves the orchestration strategy that should handle a message for
    /// the given unit. The returned lease carries the resolved
    /// <see cref="IOrchestrationStrategy"/>, the key that won selection
    /// (useful for logging and tests), and the DI scope the strategy was
    /// resolved from — dispose the lease after the single
    /// <see cref="IOrchestrationStrategy.OrchestrateAsync"/> call completes.
    /// </summary>
    /// <param name="unitId">
    /// The unit identifier — typically the actor's <c>Id.GetId()</c>. Also
    /// accepted in the user-facing unit-name form; implementations are
    /// expected to resolve either shape to the same persisted row.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<OrchestrationStrategyLease> ResolveAsync(
        string unitId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A short-lived handle to an <see cref="IOrchestrationStrategy"/> resolved
/// for a single message dispatch. Owns the DI scope the strategy was
/// instantiated in; disposing releases the scope and any scoped dependencies
/// the strategy pulled in (e.g. <c>IUnitPolicyRepository</c>).
/// </summary>
public sealed class OrchestrationStrategyLease : IAsyncDisposable
{
    private readonly IAsyncDisposable? _scope;

    /// <summary>
    /// Creates a new <see cref="OrchestrationStrategyLease"/>.
    /// </summary>
    /// <param name="strategy">The resolved strategy to orchestrate with.</param>
    /// <param name="resolvedKey">
    /// The DI key that won selection (<c>ai</c>, <c>workflow</c>,
    /// <c>label-routed</c>, etc.) or <c>null</c> when the unkeyed default
    /// was used. Surfaced for logging and test assertions.
    /// </param>
    /// <param name="scope">
    /// The DI scope owning the strategy. <c>null</c> when the caller passed
    /// a strategy that was resolved out-of-band (e.g. a substitute in a unit
    /// test) and no scope cleanup is required.
    /// </param>
    public OrchestrationStrategyLease(
        IOrchestrationStrategy strategy,
        string? resolvedKey,
        IAsyncDisposable? scope = null)
    {
        Strategy = strategy;
        ResolvedKey = resolvedKey;
        _scope = scope;
    }

    /// <summary>The resolved orchestration strategy.</summary>
    public IOrchestrationStrategy Strategy { get; }

    /// <summary>
    /// The DI key that won selection, or <c>null</c> when the unkeyed
    /// default was used.
    /// </summary>
    public string? ResolvedKey { get; }

    /// <inheritdoc />
    public ValueTask DisposeAsync() =>
        _scope?.DisposeAsync() ?? ValueTask.CompletedTask;
}