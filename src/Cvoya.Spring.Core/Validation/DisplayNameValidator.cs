// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Validation;

/// <summary>
/// Validates user-supplied <c>display_name</c> values on entity create and
/// update operations. The single rule that gates everything else is "a
/// display_name MUST NOT parse as a Guid in any standard form" — that
/// would collide with the addressing surface defined by #1629, where the
/// CLI and API resolve a token as a Guid first and fall back to display
/// name search. A Guid-shaped display_name silently bypasses that fallback
/// (or worse, returns a different real entity whose Guid happens to match).
///
/// <para>
/// Rejecting the collision class at write time keeps the CLI's
/// <c>spring agent show &lt;X&gt;</c> resolution and the API's
/// <c>/api/v1/agents/by-id/{X}</c> route deterministic. Empty / whitespace
/// values and control-character payloads are also rejected so the
/// directory cache is not poisoned by a degenerate string.
/// </para>
/// </summary>
public static class DisplayNameValidator
{
    /// <summary>
    /// Structured error code returned when the display name parses as a
    /// Guid in any of the five standard forms (<c>N</c>, <c>D</c>, <c>B</c>,
    /// <c>P</c>, <c>X</c>) after surrounding whitespace is trimmed.
    /// </summary>
    public const string GuidShapeErrorCode = "display_name_is_guid_shape";

    /// <summary>
    /// Structured error code returned when the display name is null,
    /// empty, or contains only whitespace characters.
    /// </summary>
    public const string EmptyErrorCode = "display_name_is_empty";

    /// <summary>
    /// Structured error code returned when the display name contains any
    /// character classified as a control character by
    /// <see cref="char.IsControl(char)"/>. Catches accidental newlines,
    /// tab characters, and other terminal-injection-shaped payloads.
    /// </summary>
    public const string ControlCharsErrorCode = "display_name_contains_control_chars";

    /// <summary>
    /// Maximum length cap applied to display names. Chosen to match the
    /// underlying directory column and to keep UI rendering bounded; the
    /// vast majority of real names are well under this limit.
    /// </summary>
    public const int MaxLength = 256;

    /// <summary>
    /// Structured error code returned when the display name exceeds
    /// <see cref="MaxLength"/> characters.
    /// </summary>
    public const string TooLongErrorCode = "display_name_too_long";

    /// <summary>
    /// Validates a <c>display_name</c>. Returns <c>null</c> when the value
    /// is acceptable; otherwise returns one of the structured error codes
    /// declared on this type (<see cref="EmptyErrorCode"/>,
    /// <see cref="GuidShapeErrorCode"/>,
    /// <see cref="ControlCharsErrorCode"/>, or
    /// <see cref="TooLongErrorCode"/>).
    /// </summary>
    /// <param name="displayName">The candidate display name.</param>
    /// <returns>
    /// <c>null</c> when valid; otherwise a stable error code string that
    /// callers translate into a problem-details payload.
    /// </returns>
    public static string? Validate(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return EmptyErrorCode;
        }

        // Trim before the Guid check so a name like
        // "  8c5fab2a-8e7e-4b9c-92f1-d8a3b4c5d6e7  " is rejected.
        // Guid.TryParse also accepts surrounding whitespace, but we want
        // the order — empty after trim → empty, Guid-shaped after trim →
        // guid-shape — to be deterministic.
        var trimmed = displayName.Trim();

        if (trimmed.Length > MaxLength)
        {
            return TooLongErrorCode;
        }

        if (IsGuidShape(trimmed))
        {
            return GuidShapeErrorCode;
        }

        // Use the original (untrimmed) value for the control-character
        // check — a leading/trailing newline is just as much of a problem
        // as one in the middle.
        foreach (var ch in displayName)
        {
            if (char.IsControl(ch))
            {
                return ControlCharsErrorCode;
            }
        }

        return null;
    }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> with the structured error
    /// code as the message when the value is invalid. Convenience wrapper
    /// for call sites that want exception-shaped errors instead of an
    /// optional-return contract.
    /// </summary>
    /// <param name="displayName">The candidate display name.</param>
    /// <param name="parameterName">
    /// Argument name surfaced on the thrown exception. Defaults to
    /// <c>"displayName"</c> for the most common call site.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <see cref="Validate(string?)"/> returns a non-null
    /// error code.
    /// </exception>
    public static void ThrowIfInvalid(string? displayName, string parameterName = "displayName")
    {
        var code = Validate(displayName);
        if (code is not null)
        {
            throw new ArgumentException(code, parameterName);
        }
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="value"/> parses as a Guid
    /// in any standard form. <see cref="Guid.TryParse(string?, out Guid)"/>
    /// is lenient for <c>N</c>, <c>D</c>, <c>B</c>, and <c>P</c> but does
    /// not accept the hex-block <c>X</c> form, so we fan out across all
    /// five formats explicitly.
    /// </summary>
    private static bool IsGuidShape(string value)
    {
        return Guid.TryParseExact(value, "N", out _)
            || Guid.TryParseExact(value, "D", out _)
            || Guid.TryParseExact(value, "B", out _)
            || Guid.TryParseExact(value, "P", out _)
            || Guid.TryParseExact(value, "X", out _);
    }
}