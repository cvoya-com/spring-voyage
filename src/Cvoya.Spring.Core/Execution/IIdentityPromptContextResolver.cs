// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Resolves the launch subject's identity (kind, address, display name,
/// declared role / expertise / parent units) into a pre-rendered
/// markdown fragment the prompt assembler renders as the
/// <c>## Who you are</c> section of the platform-instructions section
/// (#2680).
/// </summary>
/// <remarks>
/// <para>
/// The resolver runs at bundle build time, alongside the connector
/// prompt-context resolver, so the assembled prompt every runtime sees
/// names the agent before the platform contract refers to "your
/// assigned role" or "your unit". Without it the agent would have to
/// query <c>sv.directory.get_self</c> on every turn to learn who it is
/// — wasted budget when the platform already has the answer at launch
/// time.
/// </para>
/// <para>
/// Implementations return an already-rendered markdown fragment so the
/// resolver owns its layout choices (heading level, field ordering,
/// member-list rendering, etc.). Returning <c>null</c> means "no
/// identity to surface" and the assembler omits the section entirely
/// (synthetic launch paths, smoke tests, …).
/// </para>
/// <para>
/// Cloud overlays may register a richer resolver (one that walks the
/// unit-membership graph, surfaces tenant-specific labels, or pulls
/// expertise from a graph store) by registering before
/// <c>AddCvoyaSpringDapr()</c> — the OSS default ships a minimal
/// implementation that reads from <see cref="IAgentDefinitionProvider"/>
/// only, with no extra storage dependencies.
/// </para>
/// </remarks>
public interface IIdentityPromptContextResolver
{
    /// <summary>
    /// Resolves the identity fragment for the given launch subject.
    /// </summary>
    /// <param name="subject">
    /// The dispatch target. Must be an <c>agent:</c> or <c>unit:</c>
    /// address — implementations should return <c>null</c> for any
    /// other scheme.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the resolution.</param>
    /// <returns>
    /// A markdown fragment beginning with <c>## Who you are</c>, or
    /// <c>null</c> when the subject has no identity to surface.
    /// </returns>
    Task<string?> ResolveAsync(Address subject, CancellationToken cancellationToken = default);
}
