// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Resolves LLM provider credentials (tier-2 configuration) through the
/// canonical chain:
/// <list type="number">
///   <item><b>Agent-scoped secret</b> — per-agent override (#1714 step 0).
///   When a future <see cref="Cvoya.Spring.Core.Secrets.SecretScope.Agent"/>
///   tier exists, this is the highest-priority hit.</item>
///   <item><b>Unit-scoped secret</b> — per-unit override (e.g. a unit that
///   uses a different Anthropic account than the tenant default).</item>
///   <item><b>Tenant-scoped secret</b> — the tenant-wide default value for
///   the credential name. Unit resolves fall through to this when the
///   unit-scoped entry is absent, subject to <see cref="Cvoya.Spring.Core.Secrets.ISecretAccessPolicy"/>.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// The resolver deliberately does not implement any transformation on the
/// returned plaintext — it is the bare value the provider expects in its
/// <c>Authorization</c> / <c>x-api-key</c> header. Callers compose it into
/// the outbound request directly.
/// </para>
/// <para>
/// <b>Failure mode.</b> When no source in the chain has a value, the
/// resolver returns <c>null</c>. Callers that require a value to proceed
/// must produce an operator-facing error that mentions both tiers so
/// the operator knows how to set a tenant default or a unit override. The
/// canonical phrasing is documented in <c>docs/guide/secrets.md</c>.
/// </para>
/// <para>
/// <b>Extensibility.</b> The interface is the DI seam the private cloud
/// host replaces with a tenant-scoped credential provider (per-tenant Key
/// Vault, per-tenant BYOK). The OSS default composes
/// <see cref="Cvoya.Spring.Core.Secrets.ISecretResolver"/> — credentials
/// must be set at tenant or unit scope; there is no env-variable
/// fallback.
/// </para>
/// <para>
/// <b>Agent-scope tier (#1737).</b> The <c>agentId</c> parameter is
/// accepted today so launchers and the system-status endpoint can pass
/// it through without API churn when the agent-scope storage tier
/// lands. The OSS default ignores <c>agentId</c> and routes resolution
/// through the existing Unit→Tenant chain; subclasses (and the future
/// agent-scope implementation) consult <c>agentId</c> first.
/// </para>
/// </remarks>
public interface ILlmCredentialResolver
{
    /// <summary>
    /// Resolves the LLM provider credential for the given
    /// <paramref name="providerId"/> in the context of the optional
    /// <paramref name="agentId"/> and <paramref name="unitId"/>.
    /// </summary>
    /// <param name="providerId">
    /// Canonical provider identifier — <c>claude</c>, <c>openai</c>,
    /// <c>google</c>, <c>ollama</c>. Unknown providers return <c>null</c>.
    /// </param>
    /// <param name="agentId">
    /// Optional agent Guid id. When non-null the resolver consults the
    /// agent-scoped secret first (#1737 storage); when null the resolver
    /// starts at unit (or tenant) scope. Pass the agent id whenever the
    /// caller has it (launchers, dispatcher) so per-agent overrides resolve
    /// before unit / tenant defaults.
    /// </param>
    /// <param name="unitId">
    /// Optional unit Guid id. When non-null the resolver consults the
    /// unit-scoped secret next; when null the resolver starts at tenant
    /// scope. Pass the unit id whenever the caller has it (agent runtime,
    /// launcher) — omitting it skips the unit-scoped tier even when a unit
    /// override exists.
    /// </param>
    /// <param name="cancellationToken">Cancels the resolve.</param>
    /// <returns>
    /// A <see cref="LlmCredentialResolution"/> describing the resolved
    /// plaintext (may be <c>null</c>), the <see cref="LlmCredentialSource"/>
    /// that produced it, and the canonical secret name the resolver looked
    /// for. Never throws for a missing credential — the caller is
    /// responsible for surfacing "not configured" errors with the full
    /// operator guidance.
    /// </returns>
    Task<LlmCredentialResolution> ResolveAsync(
        string providerId,
        Guid? agentId,
        Guid? unitId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The outcome of an <see cref="ILlmCredentialResolver.ResolveAsync"/>
/// call. Exposes the resolved plaintext along with the source tier that
/// produced it so callers (and audit decorators) can surface whether the
/// agent ran on a unit override or inherited the tenant default.
/// </summary>
/// <param name="Value">
/// The resolved plaintext, or <c>null</c> when no tier produced a value.
/// </param>
/// <param name="Source">Which tier produced the value.</param>
/// <param name="SecretName">
/// The canonical secret name the resolver looked for (e.g.
/// <c>anthropic-api-key</c>). Always populated — even for
/// <see cref="LlmCredentialSource.NotFound"/> — so error messages and
/// audit records can point operators at the exact name they must create.
/// </param>
public record LlmCredentialResolution(
    string? Value,
    LlmCredentialSource Source,
    string SecretName);

/// <summary>
/// Which tier in the two-tier chain produced an LLM credential.
/// Recorded on every <see cref="LlmCredentialResolution"/> so the cloud
/// host's audit decorator can emit "which tier was hit" metrics.
/// </summary>
public enum LlmCredentialSource
{
    /// <summary>No tier produced a value.</summary>
    NotFound = 0,

    /// <summary>A unit-scoped secret produced the value.</summary>
    Unit = 1,

    /// <summary>A tenant-scoped secret produced the value.</summary>
    Tenant = 2,

    /// <summary>
    /// A slot exists (unit or tenant scope) but its ciphertext could
    /// not be decrypted — typically because the at-rest encryption key
    /// rotated between the write and the read. Operationally distinct
    /// from <see cref="NotFound"/>: the fix is "rotate the key or
    /// re-seed the slot", not "create the slot".
    /// </summary>
    Unreadable = 3,

    /// <summary>
    /// An agent-scoped secret produced the value (#1714 / #1737).
    /// Highest priority in the resolver chain — beats unit and tenant
    /// scopes. Reserved for the future agent-scope storage tier; the
    /// OSS default does not surface this value today.
    /// </summary>
    Agent = 4,

    /// <summary>
    /// A parent-unit-scoped secret produced the value (#1714 / #1737).
    /// Surfaced when the resolver walks the unit's parent chain and a
    /// non-direct ancestor unit holds the value with the inheritance
    /// flag enabled. Distinct from <see cref="Unit"/> so audit logs can
    /// distinguish "directly bound" from "inherited from parent".
    /// </summary>
    ParentUnit = 5,
}