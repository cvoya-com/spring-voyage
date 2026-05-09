// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Orchestration;

using System.Runtime.Serialization;
using System.Text.Json;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Rich descriptor for a unit's direct child, returned by
/// <see cref="OrchestrationToolHandlers.HandleListChildrenAsync"/> per the
/// <c>list_children.output.schema.json</c> shape advertised by ADR-0039
/// §3.
/// </summary>
/// <remarks>
/// <para>
/// Crosses the Dapr actor remoting boundary as part of
/// <c>IUnitActor.GetChildDescriptorsAsync</c>; the
/// <see cref="DataContractAttribute"/> opt-in is required for the
/// <c>DataContractSerializer</c> Dapr remoting uses (see bug #261 /
/// #319 for the same constraint on every other actor-public record).
/// </para>
/// <para>
/// <see cref="ExecutionConfig"/> is the persisted on-disk
/// <c>execution:</c> block for the child as a JSON object — explicitly
/// "opaque to callers" per the schema. Callers that need typed,
/// post-inheritance access call <c>inspect_child</c> instead.
/// </para>
/// </remarks>
/// <param name="Address">The child's address.</param>
/// <param name="DisplayName">The directory-resolved display name. Empty when no directory entry is present.</param>
/// <param name="Kind">The structural kind: <c>"agent"</c> or <c>"unit"</c>.</param>
/// <param name="ExecutionConfig">The child's persisted execution block as JSON; <c>null</c> when no block is declared.</param>
[DataContract]
public sealed record OrchestrationChildDescriptor(
    [property: DataMember] Address Address,
    [property: DataMember] string DisplayName,
    [property: DataMember] string Kind,
    [property: DataMember] JsonElement? ExecutionConfig);
