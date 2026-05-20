// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Orchestration;

using System.Text.Json.Serialization;

/// <summary>
/// Canonical names for the orchestration tools an agent may invoke against
/// any addressable target. Wire form is snake_case (preserved via
/// <see cref="JsonStringEnumMemberNameAttribute"/>) so manifests, logs,
/// and runtime-side tool catalogues use the names declared in ADR-0039 §3.
/// </summary>
/// <remarks>
/// The orchestration surface is the two action verbs only — discovery,
/// inspection, and status queries live on the <c>sv.*</c> directory tool
/// surface exposed by <c>SvDirectorySkillRegistry</c>. The 2026-05-19
/// ADR-0039 amendment (#2536) removed the "child" / "children" framing
/// (a caller may target any addressable entity in the same tenant), and
/// the subsequent shrink (#2537) dropped <c>list_members</c>,
/// <c>inspect</c>, and <c>query_status</c> from this surface because the
/// <c>sv.list_members</c> / <c>sv.get_member</c> / <c>sv.get_status</c>
/// tools already cover them.
/// </remarks>
public enum OrchestrationToolName
{
    /// <summary>Delegate the in-flight work to a single addressable target.</summary>
    [JsonStringEnumMemberName("delegate_to")]
    DelegateTo,

    /// <summary>Fan the in-flight work out to multiple addressable targets in parallel.</summary>
    [JsonStringEnumMemberName("fanout_to")]
    FanoutTo
}
