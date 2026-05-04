// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Validation;

/// <summary>
/// Centralised <see cref="IResult"/> factory for display-name validation
/// failures. Issue #1632 added a single
/// <see cref="DisplayNameValidator"/> across every entity create / update
/// endpoint; this helper keeps the corresponding HTTP problem-details
/// shape uniform so the CLI and portal can pattern-match on the
/// structured <c>code</c> extension regardless of which endpoint emitted
/// the 400.
/// </summary>
internal static class DisplayNameProblems
{
    /// <summary>
    /// Stable URI placed into <c>type</c> on every display-name problem
    /// response. The path mirrors the issue rationale at #1632 and is
    /// the discriminator clients SHOULD switch on for "display name was
    /// rejected" errors.
    /// </summary>
    private const string ProblemType = "https://docs.cvoya.com/spring/errors/display-name-invalid";

    private const string ProblemTitle = "Invalid display name";

    /// <summary>
    /// Returns a 400 problem-details response carrying the structured
    /// <paramref name="errorCode"/> in the <c>code</c> extension. The
    /// <paramref name="errorCode"/> is one of the constants declared on
    /// <see cref="DisplayNameValidator"/> (e.g.
    /// <see cref="DisplayNameValidator.GuidShapeErrorCode"/>).
    /// </summary>
    public static IResult InvalidDisplayName(string errorCode)
    {
        return Results.Problem(
            type: ProblemType,
            title: ProblemTitle,
            detail: BuildDetail(errorCode),
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = errorCode,
            });
    }

    /// <summary>
    /// Runs <see cref="DisplayNameValidator.Validate(string?)"/> and
    /// returns the corresponding 400 problem-details response when the
    /// value is invalid. Returns <c>null</c> when the value is acceptable
    /// — call sites short-circuit on a non-null return.
    /// </summary>
    public static IResult? ValidateOrProblem(string? displayName)
    {
        var code = DisplayNameValidator.Validate(displayName);
        return code is null ? null : InvalidDisplayName(code);
    }

    private static string BuildDetail(string errorCode) => errorCode switch
    {
        DisplayNameValidator.EmptyErrorCode =>
            "Display name must not be empty or whitespace-only.",
        DisplayNameValidator.GuidShapeErrorCode =>
            "Display name must not parse as a Guid (any standard form). " +
            "A Guid-shaped display name collides with the Guid-first " +
            "addressing surface and would silently bypass display-name search.",
        DisplayNameValidator.ControlCharsErrorCode =>
            "Display name must not contain control characters " +
            "(newlines, tab, NUL, etc.).",
        DisplayNameValidator.TooLongErrorCode =>
            $"Display name must be {DisplayNameValidator.MaxLength} characters or fewer.",
        _ => "Display name failed validation.",
    };
}