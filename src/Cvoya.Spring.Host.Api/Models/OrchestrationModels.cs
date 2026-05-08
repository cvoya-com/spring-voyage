// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Wire-level representation of a unit's persisted <c>orchestration.strategy</c>
/// key (#606) for the dedicated
/// <c>GET/PUT /api/v1/units/{id}/orchestration</c> endpoint. Shipped as a
/// dedicated model rather than a bare string so follow-up work (per-strategy
/// options; see ADR-0010 revisit criteria) can grow optional fields without
/// reshaping the wire contract.
/// </summary>
/// <param name="Strategy">
/// The DI key naming the
/// <see cref="Cvoya.Spring.Core.Orchestration.IOrchestrationStrategy"/>
/// implementation this unit should resolve on every domain message.
/// Platform-offered values: <c>ai</c>, <c>workflow</c>,
/// <c>label-routed</c>. <c>null</c> means the unit has no manifest
/// directive and the resolver falls back to policy inference /
/// the unkeyed platform default (ADR-0010).
/// </param>
public record UnitOrchestrationResponse(string? Strategy = null);