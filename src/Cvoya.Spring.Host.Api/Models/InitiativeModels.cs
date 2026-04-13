// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using Cvoya.Spring.Core.Initiative;

/// <summary>
/// Response body for <c>GET /api/v1/agents/{id}/initiative/level</c>.
/// Exposes the agent's currently effective initiative level as resolved
/// by the initiative engine.
/// </summary>
public record InitiativeLevelResponse(InitiativeLevel Level);