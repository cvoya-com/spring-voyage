// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.ErrorHandling;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.Cli;
using Cvoya.Spring.Cli.Generated.Models;

using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;

/// <summary>
/// Renders a Kiota <see cref="ApiException"/> as a friendly, status-aware
/// message in either prose or JSON form. Centralises the mapping that used
/// to live (and was missing) at every CLI command site so non-2xx HTTP
/// responses no longer surface as raw .NET stack traces (#1071).
///
/// The renderer is open for extension: derive from
/// <see cref="ApiExceptionRenderer"/> and replace
/// <see cref="Instance"/> at startup to swap the cloud-host's branded
/// error format in. Per the OSS extensibility contract
/// (<c>AGENTS.md § "Open-Source Platform &amp; Extensibility"</c>) this
/// type stays unsealed and exposes its decision points as
/// <c>protected virtual</c> hooks.
/// </summary>
public class ApiExceptionRenderer : IApiExceptionRenderer
{
    /// <summary>
    /// Currently installed renderer instance. Defaults to
    /// <see cref="ApiExceptionRenderer"/>; the cloud host (or tests) may
    /// swap in a custom implementation before <c>Program.Main</c> dispatches
    /// the parsed command.
    /// </summary>
    public static IApiExceptionRenderer Instance { get; set; } = new ApiExceptionRenderer();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <inheritdoc />
    public virtual int Render(ApiException exception, CliRenderContext context)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(context);

        var payload = BuildPayload(exception);

        if (string.Equals(context.OutputFormat, "json", StringComparison.OrdinalIgnoreCase))
        {
            EmitJson(payload, exception, context);
        }
        else
        {
            EmitProse(payload, exception, context);
        }

        return DetermineExitCode(exception);
    }

    /// <summary>
    /// Builds the structured error payload from the exception. Subclasses
    /// may override to enrich extensions (e.g. correlation ids), tweak
    /// status-code mappings, or pull additional context from cloud-only
    /// exception types.
    /// </summary>
    protected virtual CliErrorPayload BuildPayload(ApiException exception)
    {
        var status = exception.ResponseStatusCode > 0
            ? exception.ResponseStatusCode
            : (int?)null;

        string? title = null;
        string? detail = null;
        var extensions = new Dictionary<string, string?>(StringComparer.Ordinal);

        if (exception is ProblemDetails problem)
        {
            title = NullIfBlank(problem.Title);
            detail = NullIfBlank(problem.Detail);

            if (status is null)
            {
                var fromBody = TryParseStatus(problem.Status);
                if (fromBody is int parsed)
                {
                    status = parsed;
                }
            }

            CollectExtensions(problem.AdditionalData, extensions);
        }

        title ??= MapStatusTitle(status);
        var next = ExtractNextHints(extensions);

        return new CliErrorPayload(status, title, detail, next, extensions);
    }

    /// <summary>
    /// Emits the structured JSON envelope for <c>--output json</c>. Goes to
    /// stdout so scripted callers can pipe it into <c>jq</c> straight from
    /// the failed invocation. Override to alter the envelope shape.
    /// </summary>
    protected virtual void EmitJson(CliErrorPayload payload, ApiException exception, CliRenderContext context)
    {
        var envelope = new CliErrorEnvelope(new CliErrorBody(
            payload.Status,
            payload.Title,
            payload.Detail,
            payload.Next.Count == 0 ? null : payload.Next,
            payload.Extensions.Count == 0 ? null : payload.Extensions,
            context.OperationDescription));

        var stdout = context.StdOut ?? Console.Out;
        stdout.WriteLine(JsonSerializer.Serialize(envelope, JsonOptions));

        if (context.Verbose)
        {
            (context.StdErr ?? Console.Error).WriteLine(exception.ToString());
        }
    }

    /// <summary>
    /// Emits the human-readable prose form to stderr. Override to brand the
    /// formatting (colour, prefix, etc.).
    /// </summary>
    protected virtual void EmitProse(CliErrorPayload payload, ApiException exception, CliRenderContext context)
    {
        var sb = new StringBuilder();
        var prefix = NullIfBlank(context.OperationDescription);
        var headline = ComposeHeadline(payload, prefix);
        sb.Append(headline);

        if (payload.Next.Count > 0)
        {
            sb.AppendLine();
            sb.Append("next:");
            foreach (var hint in payload.Next)
            {
                sb.AppendLine();
                sb.Append("  ").Append(hint);
            }
        }

        var stderr = context.StdErr ?? Console.Error;
        stderr.WriteLine(sb.ToString());

        if (context.Verbose || ShouldDumpStackTrace())
        {
            stderr.WriteLine();
            stderr.WriteLine(exception.ToString());
        }
    }

    /// <summary>
    /// Maps the HTTP status code to a default headline used when the server
    /// did not supply a <see cref="ProblemDetails.Title"/>. Override to
    /// localise.
    /// </summary>
    protected virtual string MapStatusTitle(int? status) => status switch
    {
        400 => "Bad request.",
        401 => "Authentication required.",
        403 => "Operation is not allowed in this scope.",
        404 => "Resource not found.",
        405 => "Method not allowed.",
        409 => "Request conflicts with the current resource state.",
        422 => "Request payload was rejected by the server.",
        429 => "Too many requests; please retry after backoff.",
        500 => "Server error.",
        502 => "Upstream service returned a bad response.",
        503 => "Service is temporarily unavailable.",
        504 => "Upstream service timed out.",
        null => "Server returned an error without a status code.",
        _ => $"Server returned status {status.Value}.",
    };

    /// <summary>
    /// Returns the process exit code for <paramref name="exception"/>. When
    /// the exception is a <see cref="ProblemDetails"/> that carries a
    /// <c>code</c> extension value matching a known validation code (see
    /// <see cref="UnitValidationExitCodes.ForCode"/>), the mapped 20–27
    /// range is returned so operator scripts can branch on the specific
    /// validation failure. Falls back to <c>1</c> (UnknownError) for all
    /// other exceptions. (#990)
    /// </summary>
    protected virtual int DetermineExitCode(ApiException exception)
    {
        if (exception is ProblemDetails problem
            && problem.AdditionalData is { Count: > 0 } data)
        {
            // The server emits the code in AdditionalData["code"]. Kiota
            // deserialises string scalar extensions as UntypedString; plain
            // string handles the rare case where the runtime holds a boxed
            // string directly. CollectExtensions normalises the key to
            // camelCase but the code field is already camelCase on the wire.
            if (data.TryGetValue("code", out var raw))
            {
                var codeString = raw switch
                {
                    string s => s,
                    Microsoft.Kiota.Abstractions.Serialization.UntypedString us => us.GetValue(),
                    _ => null,
                };

                if (!string.IsNullOrEmpty(codeString))
                {
                    var mapped = UnitValidationExitCodes.ForCode(codeString);
                    if (mapped != UnitValidationExitCodes.UnknownError)
                    {
                        return mapped;
                    }
                }
            }
        }

        return UnitValidationExitCodes.UnknownError;
    }

    /// <summary>
    /// Set to <see langword="true"/> when <c>SPRING_CLI_DEBUG=1</c> is in the
    /// environment so renderers honour the debug flag without coupling to
    /// the parser. Subclasses may override to short-circuit the env lookup.
    /// </summary>
    protected virtual bool ShouldDumpStackTrace()
    {
        var debug = Environment.GetEnvironmentVariable("SPRING_CLI_DEBUG");
        return !string.IsNullOrEmpty(debug) && debug != "0";
    }

    private static string ComposeHeadline(CliErrorPayload payload, string? prefix)
    {
        var statusFragment = payload.Status is int s ? $" [{s}]" : string.Empty;
        var title = payload.Title ?? "Server returned an error.";
        var headline = prefix is null
            ? $"error{statusFragment}: {title}"
            : $"{prefix}{statusFragment}: {title}";

        if (!string.IsNullOrWhiteSpace(payload.Detail))
        {
            headline = $"{headline} — {payload.Detail}";
        }

        return headline;
    }

    private static IReadOnlyList<string> ExtractNextHints(IDictionary<string, string?> extensions)
    {
        var hints = new List<string>();
        // Surface the canonical operator-recovery extension keys used by
        // the API host today (#1068): the unit-purge gate ships
        // `forceHint` and `hint` URLs in the conflict response so scripts
        // can auto-recover. Order them from "least invasive" to "most
        // invasive" so the prose renderer leads with the safe option.
        TryConsume(extensions, "hint", hints);
        TryConsume(extensions, "forceHint", hints);

        if (extensions.TryGetValue("next", out var nextRaw) && !string.IsNullOrWhiteSpace(nextRaw))
        {
            hints.Add(nextRaw);
            extensions.Remove("next");
        }

        return hints;
    }

    private static void TryConsume(IDictionary<string, string?> extensions, string key, List<string> hints)
    {
        if (extensions.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            hints.Add(value!);
            extensions.Remove(key);
        }
    }

    private static void CollectExtensions(IDictionary<string, object>? data, IDictionary<string, string?> sink)
    {
        if (data is null || data.Count == 0)
        {
            return;
        }

        foreach (var kvp in data)
        {
            // Skip non-scalar additions (UntypedObject / UntypedArray) — the
            // CLI envelope is a flat dictionary today and we don't want to
            // smuggle nested JSON in via a stringified form.
            var scalar = TryAsScalar(kvp.Value);
            if (scalar is null)
            {
                continue;
            }

            // Normalise the casing — Kiota preserves the wire-format key
            // verbatim, but the API host emits PascalCase for anonymous
            // payloads (see UnitEndpoints' ForceHint/Hint) and camelCase
            // for ProblemDetails extensions. Lowercase the first letter so
            // the renderer's lookup table stays single-cased.
            var key = NormaliseKey(kvp.Key);
            sink[key] = scalar;
        }
    }

    private static string NormaliseKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return key;
        }
        if (char.IsUpper(key[0]))
        {
            return char.ToLowerInvariant(key[0]) + key.Substring(1);
        }
        return key;
    }

    private static int? TryParseStatus(UntypedNode? node)
    {
        if (node is null)
        {
            return null;
        }

        switch (node)
        {
            case UntypedInteger ui:
                return ui.GetValue();
            case UntypedLong ul:
                {
                    var value = ul.GetValue();
                    return value is >= int.MinValue and <= int.MaxValue
                        ? (int)value
                        : null;
                }
            case UntypedString us:
                {
                    var raw = us.GetValue();
                    return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                        ? parsed
                        : null;
                }
            default:
                return null;
        }
    }

    private static string? TryAsScalar(object? value)
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