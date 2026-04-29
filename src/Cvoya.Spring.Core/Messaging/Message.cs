// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

using System.Runtime.Serialization;
using System.Text.Json;

/// <summary>
/// Represents an immutable message exchanged between addressable components
/// in the Spring Voyage platform.
/// </summary>
/// <remarks>
/// Travels across the Dapr Actor remoting boundary as the parameter and return
/// type of <c>IAgent.ReceiveAsync</c>. Positional records without a
/// parameterless constructor require explicit <c>[DataContract]</c> +
/// <c>[DataMember]</c> so <c>DataContractSerializer</c> can marshal them (#319).
/// </remarks>
/// <param name="Id">The unique identifier of the message.</param>
/// <param name="From">The address of the message sender.</param>
/// <param name="To">The address of the message recipient.</param>
/// <param name="Type">The type of message.</param>
/// <param name="ThreadId">An optional thread identifier for correlating related messages.</param>
/// <param name="Payload">The message payload as a JSON element.</param>
/// <param name="Timestamp">The timestamp when the message was created.</param>
[DataContract]
public record Message(
    [property: DataMember(Order = 0)] Guid Id,
    [property: DataMember(Order = 1)] Address From,
    [property: DataMember(Order = 2)] Address To,
    [property: DataMember(Order = 3)] MessageType Type,
    [property: DataMember(Order = 4)] string? ThreadId,
    [property: DataMember(Order = 5)] JsonElement Payload,
    [property: DataMember(Order = 6)] DateTimeOffset Timestamp);