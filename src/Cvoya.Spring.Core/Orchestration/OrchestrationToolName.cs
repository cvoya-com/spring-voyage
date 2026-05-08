// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Orchestration;

using System.Text.Json.Serialization;

/// <summary>
/// Canonical names for the orchestration tools an agent may invoke against
/// its child composition. Wire form is snake_case (preserved via
/// <see cref="JsonStringEnumMemberNameAttribute"/>) so manifests, logs,
/// and runtime-side tool catalogues use the names declared in ADR-0039 §3.
/// </summary>
public enum OrchestrationToolName
{
    /// <summary>List the children currently composed under the agent.</summary>
    [JsonStringEnumMemberName("list_children")]
    ListChildren,

    /// <summary>Inspect a single child's metadata and current status.</summary>
    [JsonStringEnumMemberName("inspect_child")]
    InspectChild,

    /// <summary>Delegate the in-flight work to a single child.</summary>
    [JsonStringEnumMemberName("delegate_to_child")]
    DelegateToChild,

    /// <summary>Fan the in-flight work out to multiple children in parallel.</summary>
    [JsonStringEnumMemberName("fanout_to_children")]
    FanoutToChildren,

    /// <summary>Query the current execution status of a child.</summary>
    [JsonStringEnumMemberName("query_child_status")]
    QueryChildStatus
}