// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Orchestration;

using System.Text.Json;

/// <summary>
/// Describes a single platform messaging tool exposed to an agent runtime.
///
/// Returned by <see cref="IMessagingToolProvider.GetMessagingTools"/>;
/// each descriptor pairs the canonical tool name (<see cref="MessagingToolName"/>)
/// with the JSON Schemas advertised to the runtime for input and output.
/// </summary>
/// <param name="Name">The canonical messaging tool name.</param>
/// <param name="InputSchema">The JSON Schema describing the tool's input payload.</param>
/// <param name="OutputSchema">The JSON Schema describing the tool's output payload.</param>
public sealed record MessagingToolDescriptor(
    MessagingToolName Name,
    JsonElement InputSchema,
    JsonElement OutputSchema);
