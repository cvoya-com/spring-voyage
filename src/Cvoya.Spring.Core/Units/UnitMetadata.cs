// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

/// <summary>
/// Mutable display metadata for a unit. All fields are optional; callers set the
/// subset they want to update. Consumers of <c>SetMetadataAsync</c> treat a
/// <c>null</c> value as "leave the existing state untouched", enabling partial
/// updates from PATCH-style endpoints.
/// </summary>
/// <param name="DisplayName">The human-readable display name, or <c>null</c> to leave unchanged.</param>
/// <param name="Description">The description, or <c>null</c> to leave unchanged.</param>
/// <param name="Model">An optional free-form model identifier (e.g., the LLM a unit defaults to), or <c>null</c> to leave unchanged.</param>
/// <param name="Color">An optional UI color hint used by the dashboard, or <c>null</c> to leave unchanged.</param>
public record UnitMetadata(
    string? DisplayName,
    string? Description,
    string? Model,
    string? Color);