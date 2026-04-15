// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

using System.Runtime.Serialization;

/// <summary>
/// Result of a unit lifecycle transition attempt.
/// </summary>
/// <remarks>
/// Travels across the Dapr Actor remoting boundary as the return type of
/// <c>IUnitActor.TransitionAsync</c>. <c>[DataContract]</c> + <c>[DataMember]</c>
/// allow <c>DataContractSerializer</c> to marshal the positional record (#319).
/// </remarks>
/// <param name="Success">True if the transition was permitted and applied; false if it was rejected.</param>
/// <param name="CurrentStatus">The unit's status after the attempt. On rejection, this is the unchanged prior status.</param>
/// <param name="RejectionReason">Human-readable reason when <paramref name="Success"/> is false; <c>null</c> on success.</param>
[DataContract]
public record TransitionResult(
    [property: DataMember(Order = 0)] bool Success,
    [property: DataMember(Order = 1)] UnitStatus CurrentStatus,
    [property: DataMember(Order = 2)] string? RejectionReason);