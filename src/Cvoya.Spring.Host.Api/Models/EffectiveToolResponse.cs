// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// One row in the effective tool set attached to <see cref="AgentResponse"/>
/// / <see cref="UnitResponse"/> (#2337 Sub D). Wire shape for
/// <see cref="Cvoya.Spring.Core.Skills.EffectiveTool"/> — the resolver's
/// internal type stays in <c>Cvoya.Spring.Core</c> so the OpenAPI surface
/// does not leak the Core record directly.
/// </summary>
/// <param name="Name">
/// Canonical dotted tool id (e.g. <c>github.create_issue</c>).
/// </param>
/// <param name="Namespace">
/// Namespace segment of <paramref name="Name"/> — the portion before
/// the first <c>.</c>. Surfaced separately so the portal can group by
/// namespace without re-parsing.
/// </param>
/// <param name="Description">
/// Human-readable tool description. Empty when the tool came from the
/// image tier and has not yet been introspected.
/// </param>
/// <param name="Provenance">
/// Effective provenance after precedence resolution. One of
/// <c>"platform"</c>, <c>"explicit"</c>, or the prefixed forms
/// <c>"connector:&lt;slug&gt;"</c> / <c>"image:&lt;digest&gt;"</c>.
/// Mirrors <see cref="Cvoya.Spring.Core.Skills.ToolProvenance"/>.
/// </param>
/// <param name="InheritedFromUnitName">
/// Human-readable display name of the parent unit when the grant is
/// inherited up the unit chain. <c>null</c> when the grant is set
/// directly on the subject.
/// </param>
public sealed record EffectiveToolResponse(
    string Name,
    string Namespace,
    string Description,
    string Provenance,
    string? InheritedFromUnitName);
