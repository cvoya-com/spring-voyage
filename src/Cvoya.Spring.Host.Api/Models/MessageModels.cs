/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

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
/// <param name="ConversationId">An optional conversation identifier.</param>
/// <param name="Payload">The message payload as a JSON element.</param>
public record SendMessageRequest(
    AddressDto To,
    string Type,
    string? ConversationId,
    JsonElement Payload);

/// <summary>
/// Response body after sending a message.
/// </summary>
/// <param name="MessageId">The unique identifier of the sent message.</param>
/// <param name="ResponsePayload">The response payload from the target, if any.</param>
public record MessageResponse(
    Guid MessageId,
    JsonElement? ResponsePayload);
