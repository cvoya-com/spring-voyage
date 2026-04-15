// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.GraphQL;

/// <summary>
/// Projects raw Projects v2 GraphQL DTOs (<see cref="ProjectV2Item"/>,
/// <see cref="ProjectV2FieldValueConnection"/>) into the stable, snake-cased
/// JSON shapes the Projects v2 skills expose. Kept in one place so the list
/// and get variants stay byte-for-byte identical for a given item.
/// </summary>
internal static class ProjectV2Projection
{
    /// <summary>
    /// Flattens a project item to a plain object suitable for anonymous-type
    /// serialization.
    /// </summary>
    public static object ProjectItem(ProjectV2Item item) => new
    {
        item_id = item.Id,
        type = item.Type,
        is_archived = item.IsArchived ?? false,
        created_at = item.CreatedAt,
        updated_at = item.UpdatedAt,
        content = ProjectContent(item.Content),
        field_values = ProjectFieldValues(item.FieldValues),
    };

    /// <summary>
    /// Projects the polymorphic <c>content</c> union (Issue / PullRequest /
    /// DraftIssue) to a small object tagged with a <c>kind</c> discriminator.
    /// </summary>
    public static object? ProjectContent(JsonElement? content)
    {
        if (content is not { } el || el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? typeName = el.TryGetProperty("__typename", out var tn) && tn.ValueKind == JsonValueKind.String
            ? tn.GetString()
            : null;

        return typeName switch
        {
            "Issue" or "PullRequest" => new
            {
                kind = typeName,
                id = GetString(el, "id"),
                number = GetInt(el, "number"),
                title = GetString(el, "title"),
                url = GetString(el, "url"),
                state = GetString(el, "state"),
                repository = GetRepoNameWithOwner(el),
            },
            "DraftIssue" => new
            {
                kind = "DraftIssue",
                id = GetString(el, "id"),
                number = (int?)null,
                title = GetString(el, "title"),
                url = (string?)null,
                state = (string?)null,
                repository = (string?)null,
            },
            _ => new
            {
                kind = typeName ?? "Unknown",
                id = (string?)null,
                number = (int?)null,
                title = (string?)null,
                url = (string?)null,
                state = (string?)null,
                repository = (string?)null,
            },
        };
    }

    /// <summary>
    /// Projects the polymorphic <c>fieldValues</c> list to an array of
    /// plain objects. Each entry is tagged with the field's <c>data_type</c>
    /// plus the value in whichever type best fits (string / number / bool);
    /// unused type-specific fields are emitted as null so downstream
    /// JSON consumers see a stable shape regardless of value kind.
    /// </summary>
    public static object[] ProjectFieldValues(ProjectV2FieldValueConnection? connection)
    {
        if (connection?.Nodes is null)
        {
            return [];
        }

        var results = new List<object>(connection.Nodes.Count);
        foreach (var node in connection.Nodes)
        {
            if (node.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? typeName = node.TryGetProperty("__typename", out var tn) && tn.ValueKind == JsonValueKind.String
                ? tn.GetString()
                : null;

            var field = node.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.Object ? f : (JsonElement?)null;

            results.Add(new
            {
                kind = typeName,
                field_id = field is { } fe ? GetString(fe, "id") : null,
                field_name = field is { } fe2 ? GetString(fe2, "name") : null,
                data_type = field is { } fe3 ? GetString(fe3, "dataType") : null,
                text = typeName == "ProjectV2ItemFieldTextValue" ? GetString(node, "text") : null,
                number = typeName == "ProjectV2ItemFieldNumberValue" && node.TryGetProperty("number", out var num) && num.ValueKind == JsonValueKind.Number
                    ? num.GetDouble()
                    : (double?)null,
                date = typeName == "ProjectV2ItemFieldDateValue" ? GetString(node, "date") : null,
                option_id = typeName == "ProjectV2ItemFieldSingleSelectValue" ? GetString(node, "optionId") : null,
                option_name = typeName == "ProjectV2ItemFieldSingleSelectValue" ? GetString(node, "name") : null,
                iteration_id = typeName == "ProjectV2ItemFieldIterationValue" ? GetString(node, "iterationId") : null,
                iteration_title = typeName == "ProjectV2ItemFieldIterationValue" ? GetString(node, "title") : null,
                iteration_start_date = typeName == "ProjectV2ItemFieldIterationValue" ? GetString(node, "startDate") : null,
                iteration_duration = typeName == "ProjectV2ItemFieldIterationValue" && node.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number
                    ? dur.GetInt32()
                    : (int?)null,
            });
        }
        return [.. results];
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }
        return prop.ValueKind == JsonValueKind.Number ? prop.GetInt32() : null;
    }

    private static string? GetRepoNameWithOwner(JsonElement element)
    {
        if (!element.TryGetProperty("repository", out var repo) || repo.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return GetString(repo, "nameWithOwner");
    }
}