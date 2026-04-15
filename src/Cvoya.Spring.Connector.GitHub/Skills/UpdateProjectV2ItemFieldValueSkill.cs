// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Caching;
using Cvoya.Spring.Connector.GitHub.GraphQL;

using Microsoft.Extensions.Logging;

/// <summary>
/// Sets a single field value on a Projects v2 item via the
/// <c>updateProjectV2ItemFieldValue</c> GraphQL mutation. Values are a
/// tagged union — callers pass one of <c>text</c>, <c>number</c>,
/// <c>date</c>, <c>singleSelectOptionId</c>, or <c>iterationId</c>
/// together with the discriminating <c>valueType</c>.
/// </summary>
public class UpdateProjectV2ItemFieldValueSkill(
    IGitHubGraphQLClient graphQLClient,
    IGitHubResponseCache responseCache,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<UpdateProjectV2ItemFieldValueSkill>();

    /// <summary>Sets <paramref name="fieldId"/> on <paramref name="itemId"/> to the supplied value.</summary>
    /// <param name="projectId">The GraphQL node id of the Projects v2 board.</param>
    /// <param name="itemId">The GraphQL node id of the item whose field is being updated.</param>
    /// <param name="fieldId">The GraphQL node id of the field to set.</param>
    /// <param name="valueType">
    /// The variant tag: <c>text</c>, <c>number</c>, <c>date</c>,
    /// <c>single_select</c>, or <c>iteration</c>. Case-insensitive.
    /// </param>
    /// <param name="textValue">Required when <paramref name="valueType"/> is <c>text</c>.</param>
    /// <param name="numberValue">Required when <paramref name="valueType"/> is <c>number</c>.</param>
    /// <param name="dateValue">ISO-8601 date string when <paramref name="valueType"/> is <c>date</c>.</param>
    /// <param name="singleSelectOptionId">Option id when <paramref name="valueType"/> is <c>single_select</c>.</param>
    /// <param name="iterationId">Iteration id when <paramml name="valueType"/> is <c>iteration</c>.</param>
    /// <param name="owner">Optional owner login for board-level cache invalidation.</param>
    /// <param name="number">Optional project number for board-level cache invalidation.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public async Task<JsonElement> ExecuteAsync(
        string projectId,
        string itemId,
        string fieldId,
        string valueType,
        string? textValue = null,
        double? numberValue = null,
        string? dateValue = null,
        string? singleSelectOptionId = null,
        string? iterationId = null,
        string? owner = null,
        int? number = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Updating field {FieldId} on item {ItemId} (project {ProjectId}) to {ValueType} value",
            fieldId, itemId, projectId, valueType);

        var input = BuildInput(
            projectId, itemId, fieldId, valueType,
            textValue, numberValue, dateValue, singleSelectOptionId, iterationId);

        var response = await graphQLClient.MutateAsync<UpdateProjectV2ItemFieldValueResponse>(
            UpdateProjectV2ItemFieldValueMutation.Mutation,
            UpdateProjectV2ItemFieldValueMutation.Variables(input),
            cancellationToken);

        var updated = response.UpdateProjectV2ItemFieldValue?.ProjectV2Item;

        // Both the item-level cache and (if we have the board slug) the
        // list-level cache may now hold stale field-summary data.
        await responseCache.InvalidateByTagAsync(
            CacheTags.ProjectV2Item(itemId),
            cancellationToken);
        if (owner is not null && number is not null)
        {
            await responseCache.InvalidateByTagAsync(
                CacheTags.ProjectV2(owner, number.Value),
                cancellationToken);
        }

        return JsonSerializer.SerializeToElement(new
        {
            project_id = projectId,
            item_id = itemId,
            field_id = fieldId,
            value_type = NormalizeValueType(valueType),
            updated = updated is not null,
            item = updated is null ? null : ProjectV2Projection.ProjectItem(updated),
        });
    }

    private static Dictionary<string, object?> BuildInput(
        string projectId,
        string itemId,
        string fieldId,
        string valueType,
        string? textValue,
        double? numberValue,
        string? dateValue,
        string? singleSelectOptionId,
        string? iterationId)
    {
        return NormalizeValueType(valueType) switch
        {
            "text" => UpdateProjectV2ItemFieldValueMutation.TextInput(
                projectId, itemId, fieldId,
                textValue ?? throw new ArgumentException("text value requires 'textValue'.", nameof(textValue))),
            "number" => UpdateProjectV2ItemFieldValueMutation.NumberInput(
                projectId, itemId, fieldId,
                numberValue ?? throw new ArgumentException("number value requires 'numberValue'.", nameof(numberValue))),
            "date" => UpdateProjectV2ItemFieldValueMutation.DateInput(
                projectId, itemId, fieldId,
                dateValue ?? throw new ArgumentException("date value requires 'dateValue'.", nameof(dateValue))),
            "single_select" => UpdateProjectV2ItemFieldValueMutation.SingleSelectInput(
                projectId, itemId, fieldId,
                singleSelectOptionId ?? throw new ArgumentException("single_select value requires 'singleSelectOptionId'.", nameof(singleSelectOptionId))),
            "iteration" => UpdateProjectV2ItemFieldValueMutation.IterationInput(
                projectId, itemId, fieldId,
                iterationId ?? throw new ArgumentException("iteration value requires 'iterationId'.", nameof(iterationId))),
            _ => throw new ArgumentException(
                $"Unknown valueType '{valueType}'. Expected one of: text, number, date, single_select, iteration.",
                nameof(valueType)),
        };
    }

    private static string NormalizeValueType(string valueType)
    {
        var v = valueType.Trim().ToLowerInvariant();
        return v switch
        {
            "singleselect" or "single-select" or "single_select" or "single_select_option" => "single_select",
            _ => v,
        };
    }
}