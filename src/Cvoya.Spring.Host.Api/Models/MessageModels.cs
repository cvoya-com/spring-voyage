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
/// Request body for sending a message.
/// </summary>
/// <param name="To">The destination address.</param>
/// <param name="Type">The message type.</param>
/// <param name="ThreadId">An optional thread identifier.</param>
/// <param name="Payload">The message payload as a JSON element.</param>
public record SendMessageRequest(
    AddressDto To,
    string Type,
    string? ThreadId,
    JsonElement Payload);

/// <summary>
/// Response body after sending a message.
/// </summary>
/// <param name="MessageId">The unique identifier of the sent message.</param>
/// <param name="ThreadId">
/// The thread identifier the message was routed under. If the caller supplied
/// one on <see cref="SendMessageRequest.ThreadId"/>, it is echoed back; if the
/// caller omitted it for a <c>Domain</c> message to an <c>agent://</c> target, the
/// server auto-generates a fresh UUID (per #985) and surfaces it here so follow-up
/// sends can thread under the same thread.
/// </param>
/// <param name="ResponsePayload">The response payload from the target, if any.</param>
public record MessageResponse(
    Guid MessageId,
    string? ThreadId,
    JsonElement? ResponsePayload);