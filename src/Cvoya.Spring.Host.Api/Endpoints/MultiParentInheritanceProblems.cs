// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Agents;

/// <summary>
/// Centralised <see cref="IResult"/> factory for the multi-parent
/// execution-config inheritance conflict surface (ADR-0039 §6, plan
/// task B1 / B2 / B3 / B4 / B5 / B6). When an agent (or unit-as-member)
/// inherits a field whose parents disagree, every endpoint that touches
/// the parent set rejects the operation with the same structured 422 so
/// the CLI and portal can pattern-match on a uniform shape regardless of
/// which surface emitted it.
/// </summary>
/// <remarks>
/// <para>
/// The body extends the problem-details shape used elsewhere in the API
/// (see <see cref="LegacyExecutionFieldProblems"/>) with two
/// platform-specific extensions at the root:
/// </para>
/// <list type="bullet">
///   <item><c>error</c>: stable string discriminator
///   (<see cref="MultiParentInheritanceConflictCode"/>).</item>
///   <item><c>conflictingFields</c>: per-field map of the diverging
///   parent values, projected from
///   <see cref="InheritanceResolution.ConflictingFields"/>.</item>
/// </list>
/// <para>
/// Wire shape (rendered by ASP.NET Core's
/// <c>ProblemDetails</c> serializer with extensions merged at the root):
/// </para>
/// <code>
/// {
///   "type": "https://docs.cvoya.com/spring/errors/multi-parent-inheritance-conflict",
///   "title": "Multi-parent inheritance conflict",
///   "status": 422,
///   "detail": "Inherited execution-config fields disagree across parent units...",
///   "error": "MultiParentInheritanceConflict",
///   "conflictingFields": {
///     "runtime": [
///       { "unitId": "...", "value": "claude-code" },
///       { "unitId": "...", "value": "spring-voyage" }
///     ]
///   }
/// }
/// </code>
/// </remarks>
internal static class MultiParentInheritanceProblems
{
    /// <summary>
    /// Stable URI placed into <c>type</c> on every multi-parent inheritance
    /// conflict response. The path follows the same convention as
    /// <see cref="LegacyExecutionFieldProblems"/> so clients can switch on
    /// the URI prefix.
    /// </summary>
    private const string ProblemType = "https://docs.cvoya.com/spring/errors/multi-parent-inheritance-conflict";

    private const string ProblemTitle = "Multi-parent inheritance conflict";

    /// <summary>
    /// Stable string discriminator for a multi-parent inheritance conflict
    /// (ADR-0039 §6). Pinned by the v0.1 plan (B1/B2/B3 acceptance).
    /// </summary>
    public const string MultiParentInheritanceConflictCode = "MultiParentInheritanceConflict";

    /// <summary>
    /// Migration-hint message returned in <c>detail</c>. Names the action
    /// the operator has to take to clear the conflict.
    /// </summary>
    public const string MultiParentInheritanceConflictDetail =
        "Inherited execution-config fields disagree across parent units. " +
        "Either remove a conflicting parent or set the field explicitly on the agent.";

    /// <summary>
    /// Returns a 422 problem-details response carrying the diverging fields
    /// from <paramref name="conflictingFields"/>. Each field's value list is
    /// projected to <c>{ unitId: "&lt;32-hex-no-dash&gt;", value: "..." }</c>
    /// objects so the CLI / portal can name the parent that contributed
    /// each value.
    /// </summary>
    public static IResult MultiParentInheritanceConflict(
        IReadOnlyDictionary<string, IReadOnlyList<ParentValue>> conflictingFields)
    {
        ArgumentNullException.ThrowIfNull(conflictingFields);

        // Project the resolver's conflict map onto the wire shape. The
        // unitId is rendered with the canonical 32-character no-dash hex
        // form per CONVENTIONS.md § Identifiers ("Wire form on URLs,
        // address strings, manifest references, CLI output, log lines").
        var projected = new Dictionary<string, IReadOnlyList<ConflictingParentValue>>(
            conflictingFields.Count);
        foreach (var (field, values) in conflictingFields)
        {
            var list = new List<ConflictingParentValue>(values.Count);
            foreach (var v in values)
            {
                list.Add(new ConflictingParentValue(
                    UnitId: Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(v.Source),
                    Value: v.Value));
            }
            projected[field] = list;
        }

        return Results.Problem(
            type: ProblemType,
            title: ProblemTitle,
            detail: MultiParentInheritanceConflictDetail,
            statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: new Dictionary<string, object?>
            {
                ["error"] = MultiParentInheritanceConflictCode,
                ["conflictingFields"] = projected,
            });
    }

    /// <summary>
    /// Wire-shape projection of a single <see cref="ParentValue"/>. The
    /// resolver carries the parent unit's <see cref="Guid"/> on
    /// <see cref="ParentValue.Source"/>; the wire form is the canonical
    /// 32-character no-dash hex.
    /// </summary>
    /// <param name="UnitId">
    /// The parent unit's id, formatted via
    /// <see cref="Cvoya.Spring.Core.Identifiers.GuidFormatter.Format"/>.
    /// </param>
    /// <param name="Value">The parent's contributed value for the conflicting field.</param>
    public sealed record ConflictingParentValue(string UnitId, string Value);
}