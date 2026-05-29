// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using System.Text.Json;

/// <summary>
/// An address represented as a DTO for API requests and responses.
/// </summary>
/// <param name="Scheme">The address scheme (e.g., "agent", "unit", "connector").</param>
/// <param name="Path">The path identifying the specific instance.</param>
public record AddressDto(string Scheme, string Path);

/// <summary>
/// Request body for sending a message. Supply exactly one of <see cref="To"/>
/// (a single recipient — a 1-1 send or a reply on an existing thread) or
/// <see cref="Recipients"/> (a multi-party send). A multi-party send resolves
/// ONE shared thread from <c>{sender} ∪ recipients</c> so every recipient
/// lands on the same conversation (#2887 / ADR-0064), mirroring the agent's
/// <c>sv.messaging.send</c>.
/// </summary>
/// <param name="To">
/// The single destination address. Mutually exclusive with
/// <see cref="Recipients"/>; supply one or the other.
/// </param>
/// <param name="Type">The message type.</param>
/// <param name="ThreadId">An optional thread identifier.</param>
/// <param name="Payload">The message payload as a JSON element.</param>
/// <param name="From">
/// Optional explicit "speaking-as" Human id (ADR-0062 § 3). When supplied,
/// the API validates that the named Human is bound to the caller's
/// <c>TenantUser</c> and stamps it on <see cref="Cvoya.Spring.Core.Messaging.Message.From"/>.
/// When omitted, the API resolves the default Hat per the resolution
/// order (thread-pinned reply hat → <c>TenantUser.PrimaryHumanId</c> →
/// any bound Human). An invalid or unbound id returns 400 with the
/// <c>NoBoundHuman</c> code.
/// </param>
/// <param name="Recipients">
/// The full recipient set for a multi-party send. Mutually exclusive with
/// <see cref="To"/>. The server resolves a single shared thread from
/// <c>{sender} ∪ recipients</c> and delivers the message to every recipient
/// on it, so all recipients see each other and replies stay on the one
/// thread (#2887). Duplicates and the sender are collapsed.
/// </param>
public record SendMessageRequest(
    AddressDto? To,
    string Type,
    string? ThreadId,
    JsonElement Payload,
    Guid? From = null,
    IReadOnlyList<AddressDto>? Recipients = null);

/// <summary>
/// Response body after sending a message.
/// </summary>
/// <param name="MessageId">The unique identifier of the sent message.</param>
/// <param name="ThreadId">
/// The thread identifier the message was routed under. If the caller supplied
/// one on <see cref="SendMessageRequest.ThreadId"/>, it is echoed back; if the
/// caller omitted it on a <c>Domain</c> send, the server resolves the
/// participant set (caller + destination) through <c>IThreadRegistry</c> and
/// surfaces the stable id here so follow-up sends thread under the same
/// conversation. See ADR-0030 (thread model) and #2047.
/// </param>
/// <param name="ResponsePayload">The response payload from the target, if any.</param>
public record MessageResponse(
    Guid MessageId,
    string? ThreadId,
    JsonElement? ResponsePayload);
