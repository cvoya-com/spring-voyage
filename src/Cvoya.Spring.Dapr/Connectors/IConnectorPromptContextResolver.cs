// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Connectors;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Launch-path resolver that walks every connector binding applicable
/// to a dispatch subject (the agent or unit being launched), invokes
/// each connector's <see cref="Cvoya.Spring.Connectors.IConnectorPromptContextContributor"/>,
/// and returns the ordered list of markdown fragments the prompt
/// assembler renders under the platform-layer "Connector context"
/// subsection (#2442).
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the resolution semantics of
/// <see cref="IConnectorRuntimeContextResolver"/>: direct bindings on
/// the subject's unit win over inherited bindings of the same
/// connector type id; bindings without a registered prompt
/// contributor are silently skipped (legal config). Both resolvers
/// share the underlying binding walk via
/// <see cref="ConnectorBindingWalker"/>.
/// </para>
/// <para>
/// A contributor that returns <c>null</c> simply means "no hints for
/// this subject" — there is no fall-back to any legacy text path
/// (this is a new interface in v0.1 and no opt-in flag exists).
/// </para>
/// </remarks>
public interface IConnectorPromptContextResolver
{
    /// <summary>
    /// Builds the ordered list of platform-layer prompt fragments for
    /// the supplied subject. Returns an empty list when nothing
    /// applies (no bindings, no registered contributors, every
    /// contributor returned <c>null</c>, …) — the assembler then omits
    /// the "Connector context" subsection entirely.
    /// </summary>
    /// <param name="subject">
    /// The dispatch target. Must be an <c>agent:</c> or <c>unit:</c>
    /// address — any other scheme returns an empty list.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the resolution.</param>
    Task<IReadOnlyList<string>> ResolveAsync(
        Address subject,
        CancellationToken cancellationToken = default);
}
