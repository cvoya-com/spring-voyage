// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Text.Json;

/// <summary>
/// Centralised <see cref="IResult"/> factory for legacy-field rejections on
/// the agent / unit execution wire surface. ADR-0039 §7 removes
/// <c>containerRuntime</c> from operator-facing surfaces — the container
/// runtime is platform configuration, picked once by the host process at
/// deploy time. Wire DTOs that still carry the field are rejected with the
/// structured <see cref="LegacyContainerRuntimeFieldCode"/> in the
/// <c>code</c> extension so the CLI and portal can pattern-match on the
/// same shape regardless of which endpoint emitted the 400.
/// </summary>
internal static class LegacyExecutionFieldProblems
{
    /// <summary>
    /// Stable URI placed into <c>type</c> on every legacy-execution-field
    /// problem response. The path mirrors ADR-0039 §9 and is the
    /// discriminator clients SHOULD switch on for "execution carries a
    /// removed field" errors.
    /// </summary>
    private const string ProblemType = "https://docs.cvoya.com/spring/errors/legacy-execution-field";

    private const string ProblemTitle = "Legacy execution field rejected";

    /// <summary>
    /// Structured <c>code</c> extension value for a request that still
    /// carries the removed <c>containerRuntime</c> key on the execution
    /// block. Pinned by ADR-0039 §9.
    /// </summary>
    public const string LegacyContainerRuntimeFieldCode = "LegacyContainerRuntimeField";

    /// <summary>
    /// Migration-hint message returned in <c>detail</c> for a
    /// <see cref="LegacyContainerRuntimeFieldCode"/> rejection. Verbatim
    /// from ADR-0039 §9 so the CLI / portal can render the same text the
    /// ADR pins.
    /// </summary>
    public const string LegacyContainerRuntimeFieldDetail =
        "containerRuntime is removed in ADR-0039; the container runtime is platform configuration.";

    /// <summary>
    /// Returns a 400 problem-details response for a request body that
    /// carries the removed <c>containerRuntime</c> key on its execution
    /// block.
    /// </summary>
    public static IResult LegacyContainerRuntimeField()
    {
        return Results.Problem(
            type: ProblemType,
            title: ProblemTitle,
            detail: LegacyContainerRuntimeFieldDetail,
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = LegacyContainerRuntimeFieldCode,
            });
    }

    /// <summary>
    /// Returns the legacy-field problem when <paramref name="root"/> is an
    /// object that contains a <c>containerRuntime</c> property at the top
    /// level. Returns <c>null</c> when the field is absent. The check is
    /// case-sensitive; the wire contract emits <c>camelCase</c>.
    /// </summary>
    /// <remarks>
    /// The execution-block PUT surface is shallow — <c>containerRuntime</c>
    /// historically lived at the same level as <c>image</c> /
    /// <c>runtime</c> / <c>model</c>. A single top-level scan matches the
    /// wire shape; nested occurrences (e.g. inside an
    /// <c>AgentDefinitionJson</c> document) are caught by the dedicated
    /// <see cref="LegacyContainerRuntimeFieldInDefinition(JsonElement)"/>
    /// helper.
    /// </remarks>
    public static IResult? LegacyContainerRuntimeFieldOrNull(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return root.TryGetProperty("containerRuntime", out _)
            ? LegacyContainerRuntimeField()
            : null;
    }

    /// <summary>
    /// Returns the legacy-field problem when the supplied agent-definition
    /// document carries an <c>execution.containerRuntime</c> key. Returns
    /// <c>null</c> when the field is absent or the document does not carry
    /// an <c>execution</c> object. The check is case-sensitive.
    /// </summary>
    public static IResult? LegacyContainerRuntimeFieldInDefinition(JsonElement definition)
    {
        if (definition.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!definition.TryGetProperty("execution", out var execution)
            || execution.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return execution.TryGetProperty("containerRuntime", out _)
            ? LegacyContainerRuntimeField()
            : null;
    }
}