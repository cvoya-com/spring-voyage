// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using System.Runtime.Serialization;

/// <summary>
/// Represents a per-thread read cursor — the last timestamp at which a human
/// marked the given thread as read.
/// </summary>
/// <remarks>
/// Travels across the Dapr Actor remoting boundary as an element of the array
/// returned by <see cref="IHumanActor.GetLastReadAtAsync"/>. <c>[DataContract]</c>
/// + <c>[DataMember]</c> let <c>DataContractSerializer</c> marshal the
/// positional record without a <c>KnownTypeAttribute</c> declaration (#319).
/// </remarks>
/// <param name="ThreadId">The thread that was read.</param>
/// <param name="LastReadAt">The timestamp recorded as the read cursor.</param>
[DataContract]
public record ThreadReadEntry(
    [property: DataMember(Order = 0)] string ThreadId,
    [property: DataMember(Order = 1)] DateTimeOffset LastReadAt);