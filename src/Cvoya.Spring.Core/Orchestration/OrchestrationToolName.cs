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
/// The names dropped the "child" / "children" framing in the 2026-05-19
/// ADR-0039 amendment: a caller may target any addressable entity in the
/// same tenant — peer, sibling, parent, or member — so "child" was a
/// structural assumption that is no longer enforced.
/// </remarks>
public enum OrchestrationToolName
{
    /// <summary>List the caller's own direct members (empty for leaf agents).</summary>
    [JsonStringEnumMemberName("list_members")]
    ListMembers,

    /// <summary>Inspect a single addressable target's metadata and current status.</summary>
    [JsonStringEnumMemberName("inspect")]
    Inspect,

    /// <summary>Delegate the in-flight work to a single addressable target.</summary>
    [JsonStringEnumMemberName("delegate_to")]
    DelegateTo,

    /// <summary>Fan the in-flight work out to multiple addressable targets in parallel.</summary>
    [JsonStringEnumMemberName("fanout_to")]
    FanoutTo,

    /// <summary>Query the current execution status of an addressable target.</summary>
    [JsonStringEnumMemberName("query_status")]
    QueryStatus
}
