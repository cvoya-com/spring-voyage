// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.GraphQL;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// DTOs covering the GraphQL-only Projects v2 surface. Hand-rolled per
/// query (matching the style of <see cref="ReviewThreadsResponse"/>) so we
/// stay off <c>Octokit.GraphQL</c>'s schema-first codegen dependency until
/// a compelling reason surfaces.
/// </summary>
/// <remarks>
/// Projects v2 is a heavily polymorphic schema — field definitions and
/// field values are GraphQL unions. We flatten both into discriminated
/// records keyed on a <c>dataType</c> / <c>kind</c> string so downstream
/// skills can project to a stable JSON shape without leaking GraphQL
/// union plumbing to callers.
/// </remarks>

// --- list projects ---

/// <summary>Top-level envelope for <c>{ owner { projectsV2 } }</c>.</summary>
public sealed record ListProjectsV2Response(
    [property: JsonPropertyName("repositoryOwner")] ProjectsV2Owner? RepositoryOwner);

/// <summary>Owner node (User or Organization) with a paged projectsV2 connection.</summary>
public sealed record ProjectsV2Owner(
    [property: JsonPropertyName("login")] string? Login,
    [property: JsonPropertyName("projectsV2")] ProjectV2Connection? ProjectsV2);

/// <summary>Paged <c>ProjectV2</c> connection.</summary>
public sealed record ProjectV2Connection(
    [property: JsonPropertyName("nodes")] IReadOnlyList<ProjectV2Summary> Nodes);

/// <summary>Minimal project summary returned by the list query.</summary>
public sealed record ProjectV2Summary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("closed")] bool Closed,
    [property: JsonPropertyName("public")] bool? Public,
    [property: JsonPropertyName("shortDescription")] string? ShortDescription,
    [property: JsonPropertyName("createdAt")] string? CreatedAt,
    [property: JsonPropertyName("updatedAt")] string? UpdatedAt);

// --- get single project (metadata + field definitions) ---

/// <summary>Top-level envelope for <c>{ owner { projectV2(number) } }</c>.</summary>
public sealed record GetProjectV2Response(
    [property: JsonPropertyName("repositoryOwner")] ProjectV2OwnerWithProject? RepositoryOwner);

/// <summary>Owner node with a single resolved project.</summary>
public sealed record ProjectV2OwnerWithProject(
    [property: JsonPropertyName("login")] string? Login,
    [property: JsonPropertyName("projectV2")] ProjectV2Detail? ProjectV2);

/// <summary>Project detail — summary plus field definitions.</summary>
public sealed record ProjectV2Detail(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("closed")] bool Closed,
    [property: JsonPropertyName("public")] bool? Public,
    [property: JsonPropertyName("shortDescription")] string? ShortDescription,
    [property: JsonPropertyName("readme")] string? Readme,
    [property: JsonPropertyName("createdAt")] string? CreatedAt,
    [property: JsonPropertyName("updatedAt")] string? UpdatedAt,
    [property: JsonPropertyName("fields")] ProjectV2FieldConnection? Fields);

/// <summary>Paged field-definition connection.</summary>
public sealed record ProjectV2FieldConnection(
    [property: JsonPropertyName("nodes")] IReadOnlyList<ProjectV2FieldDefinition> Nodes);

/// <summary>
/// Flattened field definition. Projects v2 exposes fields as a GraphQL
/// union of <c>ProjectV2Field</c>, <c>ProjectV2IterationField</c>, and
/// <c>ProjectV2SingleSelectField</c> — we pull the common parts plus any
/// type-specific <c>options</c> / <c>configuration</c>.
/// </summary>
public sealed record ProjectV2FieldDefinition(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("dataType")] string? DataType,
    [property: JsonPropertyName("options")] IReadOnlyList<ProjectV2SingleSelectOption>? Options,
    [property: JsonPropertyName("configuration")] ProjectV2IterationConfiguration? Configuration);

/// <summary>Single-select option.</summary>
public sealed record ProjectV2SingleSelectOption(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name);

/// <summary>Iteration-field configuration (active + completed iterations).</summary>
public sealed record ProjectV2IterationConfiguration(
    [property: JsonPropertyName("duration")] int? Duration,
    [property: JsonPropertyName("startDay")] int? StartDay,
    [property: JsonPropertyName("iterations")] IReadOnlyList<ProjectV2Iteration>? Iterations,
    [property: JsonPropertyName("completedIterations")] IReadOnlyList<ProjectV2Iteration>? CompletedIterations);

/// <summary>A single iteration.</summary>
public sealed record ProjectV2Iteration(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("startDate")] string? StartDate,
    [property: JsonPropertyName("duration")] int? Duration);

// --- list / get project items ---

/// <summary>Top-level envelope for <c>{ owner { projectV2(number) { items } } }</c>.</summary>
public sealed record ListProjectV2ItemsResponse(
    [property: JsonPropertyName("repositoryOwner")] ProjectV2OwnerWithItems? RepositoryOwner);

/// <summary>Owner node whose project exposes an <c>items</c> connection.</summary>
public sealed record ProjectV2OwnerWithItems(
    [property: JsonPropertyName("projectV2")] ProjectV2WithItems? ProjectV2);

/// <summary>Minimal project envelope carrying a paged items connection.</summary>
public sealed record ProjectV2WithItems(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("items")] ProjectV2ItemConnection? Items);

/// <summary>Paged <c>ProjectV2Item</c> connection with end-cursor / hasNextPage.</summary>
public sealed record ProjectV2ItemConnection(
    [property: JsonPropertyName("pageInfo")] ProjectV2PageInfo? PageInfo,
    [property: JsonPropertyName("nodes")] IReadOnlyList<ProjectV2Item> Nodes);

/// <summary>Relay-style page info.</summary>
public sealed record ProjectV2PageInfo(
    [property: JsonPropertyName("endCursor")] string? EndCursor,
    [property: JsonPropertyName("hasNextPage")] bool HasNextPage);

/// <summary>
/// A single project item — its content (Issue / PullRequest / DraftIssue)
/// plus its field values. <c>Content</c> carries the nested object as a
/// <see cref="JsonElement"/>; skills project the relevant subset into the
/// JSON response rather than baking the polymorphism into .NET types.
/// </summary>
public sealed record ProjectV2Item(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("isArchived")] bool? IsArchived,
    [property: JsonPropertyName("createdAt")] string? CreatedAt,
    [property: JsonPropertyName("updatedAt")] string? UpdatedAt,
    [property: JsonPropertyName("content")] JsonElement? Content,
    [property: JsonPropertyName("fieldValues")] ProjectV2FieldValueConnection? FieldValues);

/// <summary>Paged field-values connection on an item.</summary>
public sealed record ProjectV2FieldValueConnection(
    [property: JsonPropertyName("nodes")] IReadOnlyList<JsonElement> Nodes);

// --- get single item ---

/// <summary>Envelope for <c>{ node(id) }</c> resolving to a ProjectV2Item.</summary>
public sealed record GetProjectV2ItemResponse(
    [property: JsonPropertyName("node")] ProjectV2Item? Node);

// --- mutations ---

/// <summary>
/// Envelope for the <c>addProjectV2ItemById</c> mutation. GraphQL returns
/// <c>{ addProjectV2ItemById: { item: ProjectV2Item } }</c>; we flatten
/// the inner <c>item</c> onto the response record for ergonomic access.
/// </summary>
public sealed record AddProjectV2ItemResponse(
    [property: JsonPropertyName("addProjectV2ItemById")] AddProjectV2ItemPayload? AddProjectV2ItemById);

/// <summary>Payload carried by <c>addProjectV2ItemById</c>.</summary>
public sealed record AddProjectV2ItemPayload(
    [property: JsonPropertyName("item")] ProjectV2Item? Item);

/// <summary>
/// Envelope for the <c>updateProjectV2ItemFieldValue</c> mutation.
/// GraphQL returns <c>{ updateProjectV2ItemFieldValue: { projectV2Item } }</c>.
/// </summary>
public sealed record UpdateProjectV2ItemFieldValueResponse(
    [property: JsonPropertyName("updateProjectV2ItemFieldValue")] UpdateProjectV2ItemFieldValuePayload? UpdateProjectV2ItemFieldValue);

/// <summary>Payload carried by <c>updateProjectV2ItemFieldValue</c>.</summary>
public sealed record UpdateProjectV2ItemFieldValuePayload(
    [property: JsonPropertyName("projectV2Item")] ProjectV2Item? ProjectV2Item);

/// <summary>
/// Envelope for the <c>archiveProjectV2Item</c> mutation. GraphQL returns
/// <c>{ archiveProjectV2Item: { item: ProjectV2Item } }</c> — note that the
/// archived item remains queryable (soft-archive), just with
/// <c>isArchived = true</c>.
/// </summary>
public sealed record ArchiveProjectV2ItemResponse(
    [property: JsonPropertyName("archiveProjectV2Item")] ArchiveProjectV2ItemPayload? ArchiveProjectV2Item);

/// <summary>Payload carried by <c>archiveProjectV2Item</c>.</summary>
public sealed record ArchiveProjectV2ItemPayload(
    [property: JsonPropertyName("item")] ProjectV2Item? Item);

/// <summary>
/// Envelope for the <c>deleteProjectV2Item</c> mutation. Unlike the archive
/// variant, this mutation hard-deletes the item; GraphQL therefore returns
/// only the <c>deletedItemId</c> rather than a full <c>ProjectV2Item</c>
/// node (the record no longer exists to serialize).
/// </summary>
public sealed record DeleteProjectV2ItemResponse(
    [property: JsonPropertyName("deleteProjectV2Item")] DeleteProjectV2ItemPayload? DeleteProjectV2Item);

/// <summary>Payload carried by <c>deleteProjectV2Item</c>.</summary>
public sealed record DeleteProjectV2ItemPayload(
    [property: JsonPropertyName("deletedItemId")] string? DeletedItemId);