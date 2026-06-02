// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Utilities;

using System;

/// <summary>
/// Formats <c>KEY=VALUE</c> lines for the deployment <c>spring.env</c> file
/// in a way that is safe both when the file is sourced by a shell
/// (<c>set -a; source spring.env</c>) and when it is fed verbatim to
/// <c>podman --env-file</c>.
///
/// <para>
/// Two consumers, two pitfalls (#1186, #2960):
/// </para>
/// <list type="bullet">
///   <item>
///     A shell that <c>source</c>s the file word-splits unquoted values, so a
///     PEM (<c>-----BEGIN RSA PRIVATE KEY-----</c>) makes bash try to run
///     <c>RSA</c> → <c>"RSA: command not found"</c>. The fix is to single-quote
///     any value carrying whitespace or shell metacharacters.
///   </item>
///   <item>
///     <c>podman --env-file</c> is literal-only: it keeps surrounding quotes as
///     part of the value and rejects multi-line values. So the PEM is encoded
///     as one line with literal <c>\n</c>, and the runtime's
///     <c>NormaliseInputKey</c> strips a single layer of surrounding quotes
///     before decoding <c>\n</c> back to real newlines.
///   </item>
/// </list>
///
/// <para>
/// Quoting is therefore <em>conditional</em>: a value is single-quoted only
/// when it needs it. A purely safe value — most importantly the numeric
/// <c>GitHub__AppId</c>, which the .NET binder rejects when wrapped in quotes
/// (<c>"12345"</c> fails to convert to <c>long</c>) — is written bare. This
/// matches the convention documented in <c>eng/config/spring.env.example</c>:
/// "Quote values that contain # or literal whitespace" and "AppId MUST be
/// unquoted".
/// </para>
/// </summary>
public static class EnvFileFormatting
{
    /// <summary>
    /// Builds a single <c>KEY=VALUE</c> env-file line. Embedded newlines in
    /// <paramref name="value"/> are first collapsed to the literal two-character
    /// sequence <c>\n</c> (neither <c>--env-file</c> reader supports multi-line
    /// values). The value is then single-quoted iff it contains a character that
    /// a sourcing shell would misinterpret.
    /// </summary>
    /// <param name="key">The env-var key, e.g. <c>GitHub__PrivateKeyPem</c>.</param>
    /// <param name="value">The raw value (may contain real newlines).</param>
    /// <returns>A shell-safe <c>KEY=VALUE</c> line (no trailing newline).</returns>
    public static string FormatLine(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        // PEM blocks contain embedded newlines. Neither Docker Compose's nor
        // Podman's --env-file syntax supports multi-line values, so convert
        // newlines to the literal two-character sequence "\n". The .NET host's
        // NormaliseInputKey decodes them back and the GitHub PEM round-trips
        // cleanly.
        var escaped = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

        return NeedsQuoting(escaped)
            ? $"{key}={SingleQuote(escaped)}"
            : $"{key}={escaped}";
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="value"/> contains a character
    /// that a sourcing shell (<c>set -a; source spring.env</c>) would treat
    /// specially — whitespace (word-splitting), a comment marker, a quote, or a
    /// substitution/glob/control character. Pure-token values (digits, slugs,
    /// base64, hex, plain URLs) need no quoting and are left bare so the .NET
    /// binder sees them verbatim.
    /// </summary>
    private static bool NeedsQuoting(string value)
    {
        if (value.Length == 0)
        {
            // KEY= with an empty RHS is already valid and unambiguous.
            return false;
        }

        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                return true;
            }

            switch (c)
            {
                case '#':
                case '\'':
                case '"':
                case '$':
                case '`':
                case '\\':
                case '!':
                case '&':
                case '|':
                case ';':
                case '<':
                case '>':
                case '(':
                case ')':
                case '{':
                case '}':
                case '[':
                case ']':
                case '*':
                case '?':
                case '~':
                    return true;
                default:
                    if (char.IsControl(c))
                    {
                        return true;
                    }

                    break;
            }
        }

        return false;
    }

    /// <summary>
    /// Wraps <paramref name="value"/> in POSIX single quotes, escaping any
    /// embedded single quote with the standard <c>'\''</c> idiom. Inside single
    /// quotes the shell performs no expansion, so a literal <c>\n</c> survives
    /// verbatim — exactly what the runtime expects to decode. The values written
    /// here (PEM/base64/hex/slugs/tokens) carry no single quotes in practice, so
    /// the escape is a safety net rather than a hot path.
    /// </summary>
    private static string SingleQuote(string value)
        => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
