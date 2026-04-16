// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

using System.Runtime.Serialization;

/// <summary>
/// Mutable display metadata for a unit. All fields are optional; callers set the
/// subset they want to update. Consumers of <c>SetMetadataAsync</c> treat a
/// <c>null</c> value as "leave the existing state untouched", enabling partial
/// updates from PATCH-style endpoints.
/// </summary>
/// <remarks>
/// Bug #261: this type travels across the Dapr Actor remoting boundary as the
/// argument to <c>IUnitActor.SetMetadataAsync</c>. Dapr remoting uses
/// <c>DataContractSerializer</c>, which can serialize a positional record only
/// when explicitly opted in with <c>[DataContract]</c> + <c>[DataMember]</c> on
/// every property — otherwise it requires a parameterless constructor, which
/// positional records don't synthesize. Without these annotations the
/// scratch + skip wizard path failed at the actor call with
/// <c>InvalidDataContractException</c>.
/// </remarks>
/// <param name="DisplayName">The human-readable display name, or <c>null</c> to leave unchanged.</param>
/// <param name="Description">The description, or <c>null</c> to leave unchanged.</param>
/// <param name="Model">An optional free-form model identifier (e.g., the LLM a unit defaults to), or <c>null</c> to leave unchanged.</param>
/// <param name="Color">An optional UI color hint used by the dashboard, or <c>null</c> to leave unchanged.</param>
[DataContract]
public record UnitMetadata(
    [property: DataMember] string? DisplayName,
    [property: DataMember] string? Description,
    [property: DataMember] string? Model,
    [property: DataMember] string? Color,
    [property: DataMember] string? Tool = null,
    [property: DataMember] string? Provider = null,
    [property: DataMember] string? Hosting = null);