// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.GraphQL;

/// <summary>
/// GraphQL mutation that sets the value of a single field on a Projects v2
/// item. Projects v2 field values are a GraphQL union
/// (<c>ProjectV2FieldValue</c>): text, number, date, single-select option,
/// or iteration — exactly one variant must be set per call.
/// </summary>
/// <remarks>
/// GitHub's schema rejects an input object that sets more than one variant
/// simultaneously, so the skill constructs the input via
/// <see cref="TextInput"/> / <see cref="NumberInput"/> / etc. and the
/// polymorphism is resolved at the DTO layer rather than in the GraphQL
/// string.
/// </remarks>
public static class UpdateProjectV2ItemFieldValueMutation
{
    /// <summary>The GraphQL mutation text. Parameterized on $input (a <c>UpdateProjectV2ItemFieldValueInput</c>).</summary>
    public const string Mutation = """
        mutation UpdateProjectV2ItemFieldValue($input: UpdateProjectV2ItemFieldValueInput!) {
          updateProjectV2ItemFieldValue(input: $input) {
            projectV2Item {
              id
              type
              isArchived
              createdAt
              updatedAt
            }
          }
        }
        """;

    /// <summary>
    /// Tagged-union discriminator for the value variant the caller is
    /// setting. Matches the JSON input shape the skill receives from its
    /// caller so the registry layer can validate the tag before dispatching.
    /// </summary>
    public enum ValueKind
    {
        /// <summary>Plain-text value targeting a <c>ProjectV2Field</c> with <c>dataType = TEXT</c>.</summary>
        Text,

        /// <summary>Numeric value targeting a <c>ProjectV2Field</c> with <c>dataType = NUMBER</c>.</summary>
        Number,

        /// <summary>ISO-8601 date value targeting a <c>ProjectV2Field</c> with <c>dataType = DATE</c>.</summary>
        Date,

        /// <summary>Single-select option id targeting a <c>ProjectV2SingleSelectField</c>.</summary>
        SingleSelectOption,

        /// <summary>Iteration id targeting a <c>ProjectV2IterationField</c>.</summary>
        Iteration,
    }

    /// <summary>Builds the <c>input</c> variable for a text value.</summary>
    public static Dictionary<string, object?> TextInput(string projectId, string itemId, string fieldId, string text) =>
        WrapInput(projectId, itemId, fieldId, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["text"] = text,
        });

    /// <summary>Builds the <c>input</c> variable for a number value.</summary>
    public static Dictionary<string, object?> NumberInput(string projectId, string itemId, string fieldId, double number) =>
        WrapInput(projectId, itemId, fieldId, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["number"] = number,
        });

    /// <summary>
    /// Builds the <c>input</c> variable for a date value. The caller supplies
    /// the date as an ISO-8601 string (e.g. <c>"2026-04-13"</c>) — GraphQL's
    /// <c>Date</c> scalar is transport-serialized identically.
    /// </summary>
    public static Dictionary<string, object?> DateInput(string projectId, string itemId, string fieldId, string date) =>
        WrapInput(projectId, itemId, fieldId, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["date"] = date,
        });

    /// <summary>Builds the <c>input</c> variable for a single-select value (option id).</summary>
    public static Dictionary<string, object?> SingleSelectInput(string projectId, string itemId, string fieldId, string optionId) =>
        WrapInput(projectId, itemId, fieldId, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["singleSelectOptionId"] = optionId,
        });

    /// <summary>Builds the <c>input</c> variable for an iteration value (iteration id).</summary>
    public static Dictionary<string, object?> IterationInput(string projectId, string itemId, string fieldId, string iterationId) =>
        WrapInput(projectId, itemId, fieldId, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["iterationId"] = iterationId,
        });

    /// <summary>
    /// Packages the base input (projectId, itemId, fieldId) together with
    /// the variant-specific <c>value</c> sub-object into the shape GraphQL
    /// expects for <c>UpdateProjectV2ItemFieldValueInput</c>.
    /// </summary>
    public static Dictionary<string, object?> Variables(Dictionary<string, object?> input) =>
        new(StringComparer.Ordinal) { ["input"] = input };

    private static Dictionary<string, object?> WrapInput(string projectId, string itemId, string fieldId, Dictionary<string, object?> value) =>
        new(StringComparer.Ordinal)
        {
            ["projectId"] = projectId,
            ["itemId"] = itemId,
            ["fieldId"] = fieldId,
            ["value"] = value,
        };
}