// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Utilities;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Cvoya.Spring.Cli.Generated.Models;

using Microsoft.Kiota.Abstractions.Serialization;

/// <summary>
/// Renders a Kiota-deserialised <see cref="ProblemDetails"/> as a single
/// human-readable string. The Kiota-generated <see cref="ProblemDetails"/>
/// does not override <see cref="Exception.Message"/>, so raw
/// <c>ex.Message</c> returns ".NET's default "Exception of type '…' was
/// thrown." — useless to operators. Call sites should route every
/// <see cref="Microsoft.Kiota.Abstractions.ApiException"/> / Kiota-derived
/// exception through <see cref="Format(Exception)"/> before printing.
/// </summary>
public static class ProblemDetailsFormatter
{
    /// <summary>
    /// Formats a <see cref="ProblemDetails"/> as <c>"{Title} — {Detail}"</c>
    /// with each scalar extension field on its own indented line
    /// (<c>"  {key}: {value}"</c>). Falls back to <c>"Status {Status}"</c>
    /// when <see cref="ProblemDetails.Title"/> is null; emits just the
    /// title line when <see cref="ProblemDetails.Detail"/> is null.
    /// </summary>
    public static string Format(ProblemDetails problem)
    {
        if (problem is null)
        {
            throw new ArgumentNullException(nameof(problem));
        }

        var header = BuildHeader(problem);
        var extensions = FormatExtensions(problem.AdditionalData);
        if (extensions.Count == 0)
        {
            return header;
        }

        var sb = new StringBuilder(header);
        foreach (var line in extensions)
        {
            sb.Append('\n').Append("  ").Append(line);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Convenience wrapper for catch sites that hold an
    /// <see cref="Exception"/> reference. Routes
    /// <see cref="ProblemDetails"/> through <see cref="Format(ProblemDetails)"/>
    /// and falls back to <see cref="Exception.Message"/> for every other
    /// exception type (including plain <see cref="Microsoft.Kiota.Abstractions.ApiException"/>
    /// with no structured body).
    /// </summary>
    public static string Format(Exception exception)
    {
        if (exception is null)
        {
            throw new ArgumentNullException(nameof(exception));
        }

        return exception is ProblemDetails problem
            ? Format(problem)
            : exception.Message;
    }

    private static string BuildHeader(ProblemDetails problem)
    {
        var title = NullIfBlank(problem.Title);
        var detail = NullIfBlank(problem.Detail);

        if (title is null)
        {
            var statusText = FormatStatus(problem.Status);
            var titleFallback = $"Status {statusText}";
            return detail is null ? titleFallback : $"{titleFallback} — {detail}";
        }

        return detail is null ? title : $"{title} — {detail}";
    }

    private static string FormatStatus(UntypedNode? status)
    {
        if (status is null)
        {
            return "unknown";
        }

        var scalar = TryAsScalar(status);
        return scalar ?? "unknown";
    }

    private static List<string> FormatExtensions(IDictionary<string, object>? data)
    {
        var result = new List<string>();
        if (data is null || data.Count == 0)
        {
            return result;
        }

        foreach (var kvp in data.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (kvp.Value is null)
            {
                continue;
            }

            var scalar = TryAsScalar(kvp.Value);
            if (scalar is null)
            {
                continue;
            }

            result.Add($"{kvp.Key}: {scalar}");
        }

        return result;
    }

    private static string? TryAsScalar(object value)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                return s;
            case bool b:
                return b ? "true" : "false";
            case UntypedString us:
                return us.GetValue();
            case UntypedBoolean ub:
                return ub.GetValue() ? "true" : "false";
            case UntypedInteger ui:
                return ui.GetValue().ToString(CultureInfo.InvariantCulture);
            case UntypedLong ul:
                return ul.GetValue().ToString(CultureInfo.InvariantCulture);
            case UntypedDouble ud:
                return ud.GetValue().ToString(CultureInfo.InvariantCulture);
            case UntypedFloat uf:
                return uf.GetValue().ToString(CultureInfo.InvariantCulture);
            case UntypedDecimal udc:
                return udc.GetValue().ToString(CultureInfo.InvariantCulture);
            case UntypedNull:
                return null;
            case UntypedObject:
            case UntypedArray:
                return null;
            case IFormattable formattable:
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            default:
                return value.ToString();
        }
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}