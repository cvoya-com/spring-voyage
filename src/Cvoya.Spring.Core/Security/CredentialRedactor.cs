// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Security;

using System;

/// <summary>
/// Redacts a known credential value out of arbitrary text — probe stdout/stderr,
/// validation error messages, activity-event summaries — before it is persisted
/// or logged. Keyed on the exact credential value used for the probe, not on
/// pattern matching: the caller owns the secret and tells the redactor what to
/// strip, so there is no risk of a heuristic missing a novel token shape or
/// leaking partial matches for lookalike substrings.
/// </summary>
public static class CredentialRedactor
{
    private const string Replacement = "***";

    /// <summary>
    /// Returns <paramref name="text"/> with every literal occurrence of
    /// <paramref name="credentialValue"/> replaced by <c>***</c>. Uses ordinal,
    /// case-sensitive string matching — credential values are compared byte-for-byte
    /// with what the caller passed to the probe, so case-folding or any
    /// unicode-normalization would widen the match and risk false positives.
    /// </summary>
    /// <param name="text">The text to scan. Must not be null.</param>
    /// <param name="credentialValue">
    /// The exact credential value used by the probe. A null or empty value short-circuits
    /// the redactor — an empty string would otherwise match between every character
    /// and produce an all-<c>***</c> output, which is both useless and destructive.
    /// </param>
    /// <returns>
    /// The redacted text. Returns <paramref name="text"/> unchanged when
    /// <paramref name="credentialValue"/> is null or empty. Returns an empty string
    /// when <paramref name="text"/> is empty.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="text"/> is null.</exception>
    public static string Redact(string text, string credentialValue)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (string.IsNullOrEmpty(credentialValue))
        {
            return text;
        }

        if (text.Length == 0)
        {
            return text;
        }

        return text.Replace(credentialValue, Replacement, StringComparison.Ordinal);
    }
}