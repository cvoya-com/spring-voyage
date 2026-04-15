// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

using System.Runtime.Serialization;

using Cvoya.Spring.Dapr.Actors;

/// <summary>
/// Represents a human's permission entry within a unit, including identity and notification preferences.
/// </summary>
/// <remarks>
/// Travels across the Dapr Actor remoting boundary as the parameter and
/// return-element type of <c>IUnitActor.SetHumanPermissionAsync</c> /
/// <c>GetHumanPermissionsAsync</c>. <c>[DataContract]</c> + <c>[DataMember]</c>
/// let <c>DataContractSerializer</c> marshal the positional record (#319).
/// </remarks>
/// <param name="HumanId">The unique identifier of the human.</param>
/// <param name="Permission">The permission level granted to this human within the unit.</param>
/// <param name="Identity">An optional display name or identity string for the human.</param>
/// <param name="Notifications">Whether this human receives notifications from the unit.</param>
[DataContract]
public record UnitPermissionEntry(
    [property: DataMember(Order = 0)] string HumanId,
    [property: DataMember(Order = 1)] PermissionLevel Permission,
    [property: DataMember(Order = 2)] string? Identity = null,
    [property: DataMember(Order = 3)] bool Notifications = true);