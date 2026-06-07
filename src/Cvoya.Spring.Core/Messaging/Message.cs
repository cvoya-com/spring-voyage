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
/// <param name="InReplyTo">
/// Optional id of the message this one replies to (ADR-0066 §5). Stamped by
/// <c>sv.messaging.respond_to</c> with its <c>message_id</c> argument and
/// surfaced on the recipient's inbound envelope as <c>in_reply_to</c>, so a
/// sender can match a reply to the specific message it answers — the
/// platform-native correlation a deterministic runtime needs without the
/// recipient echoing a token. <c>null</c> for an original (non-reply) message.
/// </param>
/// <param name="Provenance">
/// Where the message originated (issue #3075). Defaults to
/// <see cref="MessageProvenance.Direct"/>; the initiative (Tier-2 reflection)
/// loop stamps <see cref="MessageProvenance.Initiative"/> on the messages its
/// reflection actions produce so the dispatch coordinator can classify a
/// self-initiated turn's cost as <see cref="Costs.CostSource.Initiative"/>.
/// </param>
[DataContract]
public record Message(
    [property: DataMember(Order = 0)] Guid Id,
    [property: DataMember(Order = 1)] Address From,
    [property: DataMember(Order = 2)] Address To,
    [property: DataMember(Order = 3)] MessageType Type,
    [property: DataMember(Order = 4)] string? ThreadId,
    [property: DataMember(Order = 5)] JsonElement Payload,
    [property: DataMember(Order = 6)] DateTimeOffset Timestamp,
    [property: DataMember(Order = 7)] Guid? InReplyTo = null,
    [property: DataMember(Order = 8)] MessageProvenance Provenance = MessageProvenance.Direct);
