// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Capabilities;

using System.Runtime.Serialization;

/// <summary>
/// Represents a specific domain of expertise that a component possesses.
/// </summary>
/// <remarks>
/// Crosses the Dapr Actor remoting boundary as the payload for
/// <c>IAgentActor.GetExpertiseAsync</c> / <c>SetExpertiseAsync</c> and the
/// unit equivalents, so it must be <see cref="DataContractSerializer"/>-safe
/// (#319). <c>[DataContract]</c> + <c>[DataMember]</c> opts the positional
/// record into the serializer.
/// </remarks>
/// <param name="Name">The name of the expertise domain (e.g. <c>python/fastapi</c>).</param>
/// <param name="Description">A description of the expertise domain.</param>
/// <param name="Level">
/// Optional proficiency level. Matches the <c>level</c> field in the agent
/// YAML schema (<c>beginner | intermediate | advanced | expert</c>). Null when
/// the source (e.g. a legacy config) did not supply a level.
/// </param>
/// <param name="InputSchemaJson">
/// Optional JSON-schema document (serialised as a string so the record stays
/// <see cref="DataContractSerializer"/>-safe across Dapr actor remoting —
/// <c>JsonElement</c> is not DataContract-serialisable). When non-null this
/// entry has a <em>typed contract</em> and the platform considers the
/// capability skill-callable — the agents-as-skills rework (#359) projects
/// these onto the skill surface through
/// <c>ExpertiseSkillRegistry</c>. Consultative-only expertise (free-form
/// advice, no structured request shape) leaves this <c>null</c> and stays
/// message-only. See <c>docs/architecture/agent-runtime.md</c> § Skill registries.
/// </param>
[DataContract]
public record ExpertiseDomain(
    [property: DataMember(Order = 0)] string Name,
    [property: DataMember(Order = 1)] string Description,
    [property: DataMember(Order = 2)] ExpertiseLevel? Level = null,
    [property: DataMember(Order = 3)] string? InputSchemaJson = null);
