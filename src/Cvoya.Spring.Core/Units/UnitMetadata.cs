// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

using System.Runtime.Serialization;

using Cvoya.Spring.Core.Agents;

/// <summary>
/// Mutable display metadata for a unit. All fields are optional; callers set the
/// subset they want to update. Consumers of <c>SetMetadataAsync</c> treat a
/// <c>null</c> value as "leave the existing state untouched", enabling partial
/// updates from PATCH-style endpoints.
/// </summary>
/// <remarks>
/// Bug #261: this type travels across the Dapr Actor remoting boundary as the
/// argument to <c>IUnitActor.SetMetadataAsync</c>. Dapr remoting uses
/// <c>DataContractSerializer</c>, which can serialize a positional record only
/// when explicitly opted in with <c>[DataContract]</c> + <c>[DataMember]</c> on
/// every property — otherwise it requires a parameterless constructor, which
/// positional records don't synthesize. Without these annotations the
/// scratch + skip wizard path failed at the actor call with
/// <c>InvalidDataContractException</c>.
/// </remarks>
/// <param name="DisplayName">The human-readable display name, or <c>null</c> to leave unchanged.</param>
/// <param name="Description">The description, or <c>null</c> to leave unchanged.</param>
/// <param name="Model">An optional free-form model identifier (e.g., the LLM a unit defaults to), or <c>null</c> to leave unchanged.</param>
/// <param name="Color">An optional UI color hint used by the dashboard, or <c>null</c> to leave unchanged.</param>
/// <param name="Provider">Optional LLM provider identifier persisted on the unit-actor metadata, or <c>null</c> to leave unchanged.</param>
/// <param name="Hosting">Optional hosting hint persisted on the unit-actor metadata, or <c>null</c> to leave unchanged.</param>
/// <param name="Specialty">
/// Optional free-form specialty label (e.g. "reviewer", "implementer")
/// surfaced to runtimes and operators for unit selection; the platform
/// does not route on it. Mirrors
/// <see cref="AgentMetadata.Specialty"/>; <c>null</c> to leave unchanged.
/// Added in #2341 for unit/agent parity per <c>units-vs-agents.md</c>.
/// </param>
/// <param name="Enabled">
/// When <c>false</c>, the unit skips processing inbound messages.
/// Re-enabling is cheap. Mirrors <see cref="AgentMetadata.Enabled"/>;
/// <c>null</c> to leave unchanged. Added in #2341.
/// </param>
/// <param name="ExecutionMode">
/// How this unit participates in message dispatch. Mirrors
/// <see cref="AgentMetadata.ExecutionMode"/>; <c>null</c> to leave unchanged.
/// Added in #2341.
/// </param>
/// <remarks>
/// #1732: the standalone <c>Tool</c> slot was dropped — the execution tool
/// is derived from the runtime registry via the unit's
/// <see cref="Cvoya.Spring.Core.Execution.UnitExecutionDefaults.Agent"/>
/// slot at dispatch time.
/// </remarks>
[DataContract]
public record UnitMetadata(
    [property: DataMember] string? DisplayName,
    [property: DataMember] string? Description,
    [property: DataMember] string? Model,
    [property: DataMember] string? Color,
    [property: DataMember] string? Provider = null,
    [property: DataMember] string? Hosting = null,
    [property: DataMember] string? Specialty = null,
    [property: DataMember] bool? Enabled = null,
    [property: DataMember] AgentExecutionMode? ExecutionMode = null);
