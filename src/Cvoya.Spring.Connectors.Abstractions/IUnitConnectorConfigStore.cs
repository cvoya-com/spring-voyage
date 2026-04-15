// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connectors;

using System.Runtime.Serialization;
using System.Text.Json;

/// <summary>
/// Persistence port for per-unit connector bindings. Connector packages
/// consume this abstraction to read and write their typed config without
/// caring where the bytes land (unit actor state today; possibly a tenant
/// database later). The store is intentionally type-agnostic — bindings
/// are persisted as <c>(TypeId, JsonElement)</c> so adding a new connector
/// requires no changes to the store implementation.
/// </summary>
public interface IUnitConnectorConfigStore
{
    /// <summary>
    /// Returns the active binding for the given unit, or <c>null</c> when
    /// the unit is not bound to any connector.
    /// </summary>
    /// <param name="unitId">The unit id (the directory path segment).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The binding, or <c>null</c> when unbound.</returns>
    Task<UnitConnectorBinding?> GetAsync(string unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts the unit's connector binding atomically. If the unit was
    /// previously bound to a different connector type, that binding is
    /// replaced.
    /// </summary>
    /// <param name="unitId">The unit id.</param>
    /// <param name="typeId">The connector type id from <see cref="IConnectorType.TypeId"/>.</param>
    /// <param name="config">The serialized typed config payload.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task SetAsync(string unitId, Guid typeId, JsonElement config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the unit's binding if present. No-op if the unit is not bound.
    /// </summary>
    /// <param name="unitId">The unit id.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task ClearAsync(string unitId, CancellationToken cancellationToken = default);
}

/// <summary>
/// A persisted connector binding — the <see cref="TypeId"/> that identifies
/// which connector owns this unit plus the serialized config payload whose
/// shape is defined by that connector's <see cref="IConnectorType.ConfigType"/>.
/// </summary>
/// <remarks>
/// Bug #319: this type travels across the Dapr Actor remoting boundary as the
/// return value of <c>IUnitActor.GetConnectorBindingAsync</c> and the argument
/// to <c>IUnitActor.SetConnectorBindingAsync</c>. Dapr remoting uses
/// <c>DataContractSerializer</c>, which cannot serialize a positional record
/// without a parameterless constructor unless it is explicitly opted in with
/// <c>[DataContract]</c> + <c>[DataMember]</c>. Without these, the connector
/// config store calls failed at the actor boundary with
/// <c>InvalidDataContractException</c>.
/// </remarks>
/// <param name="TypeId">The connector type id.</param>
/// <param name="Config">The serialized typed config; opaque to the store.</param>
[DataContract]
public record UnitConnectorBinding(
    [property: DataMember(Order = 0)] Guid TypeId,
    [property: DataMember(Order = 1)] JsonElement Config);

/// <summary>
/// Connector-owned, per-unit runtime metadata storage. Lets a connector
/// persist small bits of state it produced (e.g. a webhook id created at
/// unit start and required at unit stop) without having to reach into the
/// unit actor directly. The payload is an opaque <see cref="JsonElement"/>
/// so the host has no knowledge of any connector's shape.
/// </summary>
public interface IUnitConnectorRuntimeStore
{
    /// <summary>
    /// Returns the runtime metadata stored for the unit, or <c>null</c>
    /// when nothing has been persisted.
    /// </summary>
    /// <param name="unitId">The unit id.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<JsonElement?> GetAsync(string unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts the runtime metadata for the unit.
    /// </summary>
    /// <param name="unitId">The unit id.</param>
    /// <param name="metadata">The metadata to persist.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task SetAsync(string unitId, JsonElement metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the runtime metadata for the unit if present.
    /// </summary>
    /// <param name="unitId">The unit id.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task ClearAsync(string unitId, CancellationToken cancellationToken = default);
}