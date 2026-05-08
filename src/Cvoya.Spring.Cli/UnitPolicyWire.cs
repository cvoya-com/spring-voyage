// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli;

using System.Collections.Generic;
using System.Text.Json.Serialization;

/// <summary>
/// CLI-local wire shape for the unified <c>/api/v1/units/{id}/policy</c>
/// endpoint. Mirrors the server's <c>UnitPolicyResponse</c> (six optional
/// dimension slots) with plain nullable references so <see cref="System.Text.Json"/>
/// round-trips cleanly.
/// </summary>
/// <remarks>
/// <para>
/// Kiota generates each nullable slot as an <c>IComposedTypeWrapper</c>
/// (<c>oneOf [null, SkillPolicy]</c>) whose <c>CreateFromDiscriminatorValue</c>
/// reads an empty-string discriminator and never populates the inner typed
/// sub-record. That dropped every slot's fields on read and crashed on the
/// subsequent PUT's serialization — see issue #999. Bypassing the generated
/// client for just this endpoint keeps the rest of the CLI on Kiota while
/// giving <c>spring unit policy</c> a wire shape that actually round-trips.
/// </para>
/// </remarks>
public sealed class UnitPolicyWire
{
    /// <summary>Optional skill (tool) allow/block list.</summary>
    [JsonPropertyName("skill")]
    public SkillPolicyWire? Skill { get; set; }

    /// <summary>Optional LLM model allow/block list.</summary>
    [JsonPropertyName("model")]
    public ModelPolicyWire? Model { get; set; }

    /// <summary>Optional per-invocation / per-hour / per-day cost caps.</summary>
    [JsonPropertyName("cost")]
    public CostPolicyWire? Cost { get; set; }

    /// <summary>Optional pinned / whitelisted execution mode.</summary>
    [JsonPropertyName("executionMode")]
    public ExecutionModePolicyWire? ExecutionMode { get; set; }

    /// <summary>Optional unit-level initiative deny-overlay.</summary>
    [JsonPropertyName("initiative")]
    public InitiativePolicyWire? Initiative { get; set; }

    /// <summary>Optional label-routing map + status-label hooks.</summary>
    [JsonPropertyName("labelRouting")]
    public LabelRoutingPolicyWire? LabelRouting { get; set; }
}

/// <summary>Skill (tool) allow/block list.</summary>
public sealed class SkillPolicyWire
{
    /// <summary>Allowed tool names; <c>null</c> means no whitelist.</summary>
    [JsonPropertyName("allowed")]
    public List<string>? Allowed { get; set; }

    /// <summary>Blocked tool names; precedence over <see cref="Allowed"/>.</summary>
    [JsonPropertyName("blocked")]
    public List<string>? Blocked { get; set; }
}

/// <summary>Model allow/block list.</summary>
public sealed class ModelPolicyWire
{
    /// <summary>Allowed model identifiers.</summary>
    [JsonPropertyName("allowed")]
    public List<string>? Allowed { get; set; }

    /// <summary>Blocked model identifiers.</summary>
    [JsonPropertyName("blocked")]
    public List<string>? Blocked { get; set; }
}

/// <summary>Per-invocation / per-hour / per-day cost caps (USD).</summary>
public sealed class CostPolicyWire
{
    /// <summary>Per-invocation absolute cost cap.</summary>
    [JsonPropertyName("maxCostPerInvocation")]
    public double? MaxCostPerInvocation { get; set; }

    /// <summary>Rolling per-hour cost cap.</summary>
    [JsonPropertyName("maxCostPerHour")]
    public double? MaxCostPerHour { get; set; }

    /// <summary>Rolling per-24h cost cap.</summary>
    [JsonPropertyName("maxCostPerDay")]
    public double? MaxCostPerDay { get; set; }
}

/// <summary>Pinned or whitelisted agent execution mode.</summary>
public sealed class ExecutionModePolicyWire
{
    /// <summary>
    /// Pinned execution mode ("Auto" / "OnDemand"). When set, overrides
    /// per-membership / agent-global values on dispatch.
    /// </summary>
    [JsonPropertyName("forced")]
    public string? Forced { get; set; }

    /// <summary>Whitelist of permitted execution modes.</summary>
    [JsonPropertyName("allowed")]
    public List<string>? Allowed { get; set; }
}

/// <summary>Unit-level initiative deny-overlay.</summary>
public sealed class InitiativePolicyWire
{
    /// <summary>Maximum initiative level (Passive/Attentive/Proactive/Autonomous).</summary>
    [JsonPropertyName("maxLevel")]
    public string? MaxLevel { get; set; }

    /// <summary>Whether agent-initiated actions require unit approval.</summary>
    [JsonPropertyName("requireUnitApproval")]
    public bool? RequireUnitApproval { get; set; }

    /// <summary>Allowed reflection-action types.</summary>
    [JsonPropertyName("allowedActions")]
    public List<string>? AllowedActions { get; set; }

    /// <summary>Blocked reflection-action types.</summary>
    [JsonPropertyName("blockedActions")]
    public List<string>? BlockedActions { get; set; }
}

/// <summary>Label -> member routing map plus optional round-trip label hooks.</summary>
public sealed class LabelRoutingPolicyWire
{
    /// <summary>Case-insensitive map from label name to target member path.</summary>
    [JsonPropertyName("triggerLabels")]
    public Dictionary<string, string>? TriggerLabels { get; set; }

    /// <summary>Labels to apply after a successful assignment.</summary>
    [JsonPropertyName("addOnAssign")]
    public List<string>? AddOnAssign { get; set; }

    /// <summary>Labels to remove after a successful assignment.</summary>
    [JsonPropertyName("removeOnAssign")]
    public List<string>? RemoveOnAssign { get; set; }
}