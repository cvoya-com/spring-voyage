// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Agents;

using System.Runtime.Serialization;

/// <summary>
/// Actor-remoting payload for an agent or unit actor's runtime-status
/// snapshot (#2100). Holds the in-flight + queue depths the actor's
/// per-thread channels report at the moment of the call, plus a
/// timestamp the API layer projects onto the wire as
/// <c>lastUpdated</c>.
/// </summary>
/// <remarks>
/// <para>
/// Crosses the Dapr actor-remoting boundary; per CONVENTIONS § 8 we keep
/// the record-with-positional-init-properties shape <c>DataContractSerializer</c>
/// can marshal natively (no custom collection types).
/// </para>
/// <para>
/// The API layer combines this with the <c>PersistentAgentRegistry</c>
/// health snapshot to project the final
/// <see cref="AgentRuntimeStatus"/>: <see cref="AgentRuntimeStatus.Unavailable"/>
/// always wins over the actor-derived state; otherwise the projection
/// reads in-flight + queue depths to choose between
/// <see cref="AgentRuntimeStatus.Busy"/>, <see cref="AgentRuntimeStatus.Queued"/>,
/// and <see cref="AgentRuntimeStatus.Idle"/>.
/// </para>
/// </remarks>
/// <param name="InFlightThreadCount">
/// Number of per-thread channels with a dispatcher currently running
/// (<c>Dispatching == true</c>). Zero for actors with no channels.
/// </param>
/// <param name="QueuedMessageCount">
/// Total messages queued behind the in-flight heads across every channel.
/// Zero when no channel exists or every channel is at depth 0/1.
/// </param>
/// <param name="ChannelCount">
/// Total per-thread channels the actor is currently tracking. Reported
/// for diagnostics; the API does not project it onto the wire today.
/// </param>
/// <param name="ObservedAt">
/// UTC timestamp the snapshot was taken. The API echoes this as
/// <c>lastUpdated</c> so polling clients can surface staleness.
/// </param>
[DataContract]
public record AgentRuntimeStatusReport(
    [property: DataMember(Order = 1)] int InFlightThreadCount,
    [property: DataMember(Order = 2)] int QueuedMessageCount,
    [property: DataMember(Order = 3)] int ChannelCount,
    [property: DataMember(Order = 4)] DateTimeOffset ObservedAt);
