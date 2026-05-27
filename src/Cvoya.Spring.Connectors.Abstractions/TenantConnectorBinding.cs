// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connectors;

using System.Runtime.Serialization;
using System.Text.Json;

/// <summary>
/// A persisted per-tenant connector binding (ADR-0061 §1). Parallels
/// <see cref="UnitConnectorBinding"/> but addresses the tenant rather
/// than a unit — currently used by the Slack connector (one Slack
/// workspace per tenant) and any future workspace-shaped connector.
/// </summary>
/// <remarks>
/// The payload is opaque: the platform stores the connector's
/// serialised config as <see cref="Config"/> and never deserialises it.
/// The shape is defined by the connector identified by <see cref="TypeId"/>.
///
/// <para>
/// <see cref="ConnectorSlug"/> is repeated alongside <see cref="TypeId"/>
/// so the binding store can dispatch (e.g. "find me Slack's binding")
/// without resolving the connector type from DI on every read.
/// </para>
/// </remarks>
/// <param name="ConnectorSlug">The connector slug (matches <see cref="IConnectorType.Slug"/>).</param>
/// <param name="TypeId">The connector type id (matches <see cref="IConnectorType.TypeId"/>).</param>
/// <param name="Config">The serialised typed config; opaque to the store.</param>
[DataContract]
public record TenantConnectorBinding(
    [property: DataMember(Order = 0)] string ConnectorSlug,
    [property: DataMember(Order = 1)] Guid TypeId,
    [property: DataMember(Order = 2)] JsonElement Config);
