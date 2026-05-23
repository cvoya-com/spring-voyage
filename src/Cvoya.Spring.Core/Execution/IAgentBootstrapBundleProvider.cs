// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Assembles the <see cref="AgentBootstrapBundle"/> served by the worker's
/// bootstrap endpoint (ADR-0055 §2). The provider is the worker-side
/// authority on what configuration files an agent container materialises
/// on launch and verifies on every turn.
/// </summary>
/// <remarks>
/// <para>
/// Two callers in v0.1: the bootstrap endpoint (per-request, on the worker
/// HTTP plane) and the per-agent bootstrap-token issue path (called from
/// the same flow as <c>AgentVolumeManager.EnsureAsync</c> — see ADR-0055
/// §8). Implementations are expected to be inexpensive enough to invoke
/// per-request without caching; the worker's existing prompt assembler /
/// context builder are the most expensive inputs and they are not
/// per-turn here.
/// </para>
/// <para>
/// Implementations MUST produce a deterministic bundle for the same
/// inputs — the bundle's <see cref="AgentBootstrapBundle.Version"/> is
/// content-addressable (sha256), and the ETag-driven 304 path on the
/// endpoint depends on identical content producing identical hashes.
/// File ordering and dictionary serialisation are canonicalised by
/// <c>AgentBootstrapBundleHasher</c>; providers only need to emit the
/// same inputs.
/// </para>
/// </remarks>
public interface IAgentBootstrapBundleProvider
{
    /// <summary>
    /// Builds the bootstrap bundle for <paramref name="agentId"/>, or
    /// <c>null</c> when no addressable agent matches. Implementations
    /// MUST NOT throw for missing subjects.
    /// </summary>
    /// <param name="agentId">
    /// The agent identifier in canonical wire form (32-char lowercase
    /// no-dash hex per
    /// <see cref="Cvoya.Spring.Core.Identifiers.GuidFormatter.Format"/>).
    /// </param>
    /// <param name="cancellationToken">Cancels the build.</param>
    Task<AgentBootstrapBundle?> BuildAsync(
        string agentId,
        CancellationToken cancellationToken = default);
}
