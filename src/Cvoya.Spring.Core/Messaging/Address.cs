// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

using System.Runtime.Serialization;

/// <summary>
/// Represents an addressable endpoint in the Spring Voyage platform.
/// The scheme identifies the type of addressable (e.g., "agent", "unit", "connector")
/// and the path identifies the specific instance (e.g., "engineering-team/ada").
/// </summary>
/// <remarks>
/// Travels across the Dapr Actor remoting boundary as the argument to
/// <c>IUnitActor.AddMemberAsync</c> / <c>RemoveMemberAsync</c>; the
/// <c>[DataContract]</c> annotations let <c>DataContractSerializer</c> handle
/// positional records that lack a parameterless constructor (#261).
/// </remarks>
/// <param name="Scheme">The address scheme identifying the type of addressable.</param>
/// <param name="Path">The path identifying the specific addressable instance.</param>
[DataContract]
public record Address(
    [property: DataMember] string Scheme,
    [property: DataMember] string Path)
{
    public sealed override string ToString() => $"{Scheme}:{Path}";
}