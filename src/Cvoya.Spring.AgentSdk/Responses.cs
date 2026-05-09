// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk;

using System.Text.Json.Serialization;

public record DelegateResponse(
    [property: JsonPropertyName("resultMessageId")] string ResultMessageId,
    [property: JsonPropertyName("result")] string Result);

public record FanoutResponse(
    [property: JsonPropertyName("results")] IReadOnlyList<FanoutResult> Results);

public record FanoutResult(
    [property: JsonPropertyName("unitId")] string UnitId,
    [property: JsonPropertyName("resultMessageId")] string? ResultMessageId,
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("error")] string? Error);