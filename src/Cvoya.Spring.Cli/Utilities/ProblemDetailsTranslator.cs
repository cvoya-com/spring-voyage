// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Utilities;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

using Cvoya.Spring.Cli.Generated.Models;

using Microsoft.Kiota.Abstractions.Serialization;

/// <summary>
/// Translates API ProblemDetails envelopes into operator-facing CLI text.
/// The raw RFC-7807 body is still available to verbose renderers, but the
/// default command output stays focused on what happened and what to do next.
/// </summary>
public static class ProblemDetailsTranslator
{
    private static readonly HashSet<string> KnownCodes = new(StringComparer.Ordinal)
    {
        "ConnectorBindingMissing",
        "PackageNotFound",
        "UnitNotFound",
        "AgentNotFound",
        "LifecycleConflict",
        "InvalidState",
        "CredentialMissing",
        "CredentialsMissing",
        "CredentialInvalid",
        "ValidationFailed",
        "ConfigurationIncomplete",
        "UnknownConnectorSlug",
        "UnknownCredentialEdge",
        "MultiParentInheritanceConflict",
        "ImagePullFailed",
        "ImageStartFailed",
        "ToolMissing",
        "CredentialFormatRejected",
        "ModelNotFound",
        "ProbeTimeout",
        "ProbeInternalError",
    };

    private static readonly JsonSerializerOptions RawJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string TranslateProblemDetails(ProblemDetails problem)
    {
        var translated = Translate(problem);
        return translated.NextStep is null
            ? translated.Title
            : $"{translated.Title} {translated.NextStep}";
    }

    public static string Format(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is ProblemDetails problem
            ? TranslateProblemDetails(problem)
            : exception.Message;
    }

    public static TranslatedProblemDetails Translate(ProblemDetails problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var code = GetCode(problem);
        return code switch
        {
            "ConnectorBindingMissing" => ConnectorBindingMissing(problem),
            "PackageNotFound" => PackageNotFound(problem),
            "UnitNotFound" => new(
                "Unit not found.",
                "It may have been deleted. Refresh the page or pick another unit.",
                TraceId(problem)),
            "AgentNotFound" => new(
                "Agent not found.",
                "It may have been deleted. Refresh the page or pick another agent.",
                TraceId(problem)),
            "LifecycleConflict" or "InvalidState" => LifecycleConflict(problem),
            "CredentialMissing" => CredentialMissing(problem),
            "CredentialsMissing" => CredentialsMissing(problem),
            "CredentialInvalid" => CredentialInvalid(problem),
            "ValidationFailed" => new(
                "The request was invalid.",
                NullIfBlank(problem.Detail) ?? "Check the form for highlighted errors.",
                TraceId(problem)),
            "ConfigurationIncomplete" => ConfigurationIncomplete(problem),
            "UnknownConnectorSlug" => UnknownConnectorSlug(problem),
            "UnknownCredentialEdge" => UnknownCredentialEdge(problem),
            "MultiParentInheritanceConflict" => new(
                "Parent units disagree on inherited execution settings.",
                NullIfBlank(problem.Detail)
                    ?? "Remove a conflicting parent or set the inherited field explicitly.",
                TraceId(problem)),
            "ImagePullFailed" => ImagePullFailed(problem),
            "ImageStartFailed" => ImageStartFailed(problem),
            "ToolMissing" => ToolMissing(problem),
            "CredentialFormatRejected" => CredentialFormatRejected(problem),
            "ModelNotFound" => ModelNotFound(problem),
            "ProbeTimeout" => new(
                "The runtime probe timed out.",
                "Verify the agent host is responsive and retry; raise the probe timeout if this is expected.",
                TraceId(problem)),
            "ProbeInternalError" => new(
                "The runtime probe failed unexpectedly.",
                NullIfBlank(problem.Detail)
                    ?? "Check the host logs (`spring agent logs <id>` or `kubectl logs`) and retry.",
                TraceId(problem)),
            _ => new(
                NullIfBlank(problem.Title) ?? "Couldn't complete the request.",
                NullIfBlank(problem.Detail),
                TraceId(problem)),
        };
    }

    public static string RawEnvelopeJson(ProblemDetails problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var envelope = new Dictionary<string, object?>(StringComparer.Ordinal);
        AddIfPresent(envelope, "type", problem.Type);
        AddIfPresent(envelope, "title", problem.Title);
        if (problem.Status is int status)
        {
            envelope["status"] = status;
        }
        AddIfPresent(envelope, "detail", problem.Detail);
        AddIfPresent(envelope, "instance", problem.Instance);

        if (problem.AdditionalData is { Count: > 0 })
        {
            foreach (var (key, value) in problem.AdditionalData)
            {
                envelope[key] = ToSerializable(value);
            }
        }

        return JsonSerializer.Serialize(envelope, RawJsonOptions);
    }

    public static bool IsKnownCode(string? code)
        => !string.IsNullOrWhiteSpace(code) && KnownCodes.Contains(code);

    public static string? GetCode(ProblemDetails problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return GetString(problem, "code") ?? GetString(problem, "error");
    }

    public static string? GetTraceId(ProblemDetails problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return TraceId(problem);
    }

    private static TranslatedProblemDetails ConnectorBindingMissing(ProblemDetails problem)
    {
        var missing = FirstMissing(problem);
        var slug = GetValue(missing, "slug") ?? "required";
        var target = GetValue(missing, "unitName") ?? "the package";
        return new(
            $"This package needs a {slug} connector binding.",
            $"Open the {slug} step in the wizard and pick (or set up) a connector for {target}.",
            TraceId(problem));
    }

    private static TranslatedProblemDetails PackageNotFound(ProblemDetails problem)
    {
        var packageName =
            GetString(problem, "packageName")
            ?? GetString(problem, "name")
            ?? GetString(problem, "package")
            ?? ExtractPackageName(problem.Detail);
        return new(
            packageName is null
                ? "Couldn't find that package."
                : $"Couldn't find package `{packageName}`.",
            "Run `spring package list` (or refresh the catalog) to confirm the package name and version.",
            TraceId(problem));
    }

    private static TranslatedProblemDetails LifecycleConflict(ProblemDetails problem)
    {
        var action =
            GetString(problem, "action")
            ?? ActionFromDetail(problem.Detail)
            ?? "update";
        var state =
            GetString(problem, "currentStatus")
            ?? GetString(problem, "currentState")
            ?? GetString(problem, "state");
        var hint =
            GetString(problem, "hint")
            ?? GetString(problem, "forceHint")
            ?? GetString(problem, "next");

        return new(
            state is null
                ? $"Can't {action} this unit right now."
                : $"Can't {action} this unit while it's `{state}`.",
            hint ?? "Wait for the current operation to finish, then retry.",
            TraceId(problem));
    }

    private static TranslatedProblemDetails CredentialMissing(ProblemDetails problem)
    {
        var credential =
            GetString(problem, "credentialEnvVar")
            ?? GetString(problem, "credential")
            ?? GetString(problem, "secretName")
            ?? "the required credential";
        return new(
            $"Required credential `{credential}` isn't set.",
            "Set it in Config -> Secrets on this unit, on a parent unit, or on the tenant.",
            TraceId(problem));
    }

    private static TranslatedProblemDetails CredentialsMissing(ProblemDetails problem)
    {
        var missing = MissingArray(problem);
        if (missing.Count == 0)
        {
            return new(
                "This package needs at least one credential.",
                "Supply each one via `--oauth-token` / `--api-key` and retry the install.",
                TraceId(problem));
        }

        var first = missing[0];
        var envVar =
            GetValue(first, "credentialEnvVar")
            ?? GetValue(first, "credential")
            ?? GetValue(first, "secretName");
        var provider = GetValue(first, "provider");
        var label = envVar
            ?? (provider is not null ? $"{provider} credential" : "the required credential");

        if (missing.Count == 1)
        {
            return new(
                $"This package needs the `{label}` credential.",
                "Supply it via `--oauth-token` / `--api-key` (or set the tenant secret), then retry the install.",
                TraceId(problem));
        }
        return new(
            $"This package needs {missing.Count} credentials, including `{label}`.",
            "Supply each one via `--oauth-token` / `--api-key` (or set the tenant secrets), then retry the install.",
            TraceId(problem));
    }

    private static TranslatedProblemDetails UnknownCredentialEdge(ProblemDetails problem)
    {
        var provider = GetString(problem, "provider") ?? "that provider";
        var authMethod = GetString(problem, "authMethod") ?? "auth method";
        return new(
            $"No member unit consumes a `{provider}` / `{authMethod}` credential.",
            "Remove that credential entry, or pick a runtime/provider that consumes it.",
            TraceId(problem));
    }

    private static TranslatedProblemDetails CredentialInvalid(ProblemDetails problem)
    {
        var provider =
            GetString(problem, "provider")
            ?? GetString(problem, "modelProvider")
            ?? "this provider";
        return new(
            $"The configured credential for `{provider}` was rejected by the provider.",
            "Check the secret value and try again.",
            TraceId(problem));
    }

    private static TranslatedProblemDetails ConfigurationIncomplete(ProblemDetails problem)
    {
        var missing = FirstMissing(problem);
        var unitName = GetValue(missing, "unitName");
        var field = GetValue(missing, "field");
        return new(
            unitName is not null && field is not null
                ? $"Package configuration for {unitName} is missing {field}."
                : "This package is missing required configuration.",
            NullIfBlank(problem.Detail)
                ?? "Complete the missing configuration, then retry the install.",
            TraceId(problem));
    }

    private static TranslatedProblemDetails UnknownConnectorSlug(ProblemDetails problem)
    {
        var slug = GetString(problem, "slug") ?? "that";
        return new(
            $"This package doesn't declare a {slug} connector binding.",
            "Remove that connector binding or choose a connector required by this package.",
            TraceId(problem));
    }

    private static TranslatedProblemDetails ImagePullFailed(ProblemDetails problem)
    {
        return new(
            "Couldn't pull the agent image.",
            NullIfBlank(problem.Detail)
                ?? "Check that the image exists and the host can reach the registry.",
            TraceId(problem));
    }

    private static TranslatedProblemDetails ImageStartFailed(ProblemDetails problem)
    {
        return new(
            "Couldn't start the agent container.",
            NullIfBlank(problem.Detail)
                ?? "Check the agent image and host runtime logs.",
            TraceId(problem));
    }

    private static TranslatedProblemDetails ToolMissing(ProblemDetails problem)
    {
        var tool = GetString(problem, "tool") ?? "required";
        return new(
            $"The agent image is missing the {tool} CLI.",
            "Pick a different agent image or install the CLI before retrying.",
            TraceId(problem));
    }

    private static TranslatedProblemDetails CredentialFormatRejected(ProblemDetails problem)
    {
        var provider =
            GetString(problem, "provider")
            ?? GetString(problem, "modelProvider")
            ?? "this provider";
        return new(
            $"The configured credential's format isn't accepted by {provider}.",
            NullIfBlank(problem.Detail)
                ?? "Update the secret to a value of the right shape (see the provider's docs).",
            TraceId(problem));
    }

    private static TranslatedProblemDetails ModelNotFound(ProblemDetails problem)
    {
        var model = GetString(problem, "model") ?? "(unknown)";
        var provider =
            GetString(problem, "provider")
            ?? GetString(problem, "modelProvider")
            ?? "this provider";
        return new(
            $"Model `{model}` isn't available for {provider}.",
            "Pick a model from the provider's catalogue or update the install.",
            TraceId(problem));
    }

    private static string? TraceId(ProblemDetails problem) => GetString(problem, "traceId");

    private static string? GetString(ProblemDetails problem, string key)
        => problem.AdditionalData is { Count: > 0 }
            && problem.AdditionalData.TryGetValue(key, out var value)
                ? ToScalarString(value)
                : null;

    private static object? FirstMissing(ProblemDetails problem)
    {
        var array = MissingArray(problem);
        return array.Count > 0 ? array[0] : null;
    }

    private static IReadOnlyList<object?> MissingArray(ProblemDetails problem)
    {
        if (problem.AdditionalData is null
            || !problem.AdditionalData.TryGetValue("missing", out var value))
        {
            return Array.Empty<object?>();
        }

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.Array } element =>
                Enumerable.Range(0, element.GetArrayLength())
                    .Select(i => (object?)element[i])
                    .ToArray(),
            UntypedArray array => array.GetValue().ToArray(),
            IEnumerable enumerable and not string => enumerable.Cast<object?>().ToArray(),
            _ => Array.Empty<object?>(),
        };
    }

    private static string? GetValue(object? source, string key)
    {
        return source switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.Object } element
                when element.TryGetProperty(key, out var property) => ToScalarString(property),
            UntypedObject untyped
                when untyped.GetValue().TryGetValue(key, out var node) => ToScalarString(node),
            IReadOnlyDictionary<string, object?> dict
                when dict.TryGetValue(key, out var value) => ToScalarString(value),
            IReadOnlyDictionary<string, object> dict
                when dict.TryGetValue(key, out var value) => ToScalarString(value),
            IDictionary<string, object?> dict
                when dict.TryGetValue(key, out var value) => ToScalarString(value),
            IDictionary<string, object> dict
                when dict.TryGetValue(key, out var value) => ToScalarString(value),
            _ => null,
        };
    }

    private static string? ToScalarString(object? value)
    {
        return value switch
        {
            null => null,
            string s => NullIfBlank(s),
            JsonElement element => JsonElementToScalar(element),
            UntypedString s => NullIfBlank(s.GetValue()),
            UntypedInteger i => i.GetValue().ToString(CultureInfo.InvariantCulture),
            UntypedLong l => l.GetValue().ToString(CultureInfo.InvariantCulture),
            UntypedDouble d => d.GetValue().ToString(CultureInfo.InvariantCulture),
            UntypedFloat f => f.GetValue().ToString(CultureInfo.InvariantCulture),
            UntypedDecimal d => d.GetValue().ToString(CultureInfo.InvariantCulture),
            UntypedBoolean b => b.GetValue() ? "true" : "false",
            UntypedNull => null,
            bool b => b ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => NullIfBlank(value.ToString()),
        };
    }

    private static object? ToSerializable(object? value)
    {
        return value switch
        {
            null => null,
            JsonElement element => JsonSerializer.Deserialize<object>(element.GetRawText()),
            UntypedString s => s.GetValue(),
            UntypedInteger i => i.GetValue(),
            UntypedLong l => l.GetValue(),
            UntypedDouble d => d.GetValue(),
            UntypedFloat f => f.GetValue(),
            UntypedDecimal d => d.GetValue(),
            UntypedBoolean b => b.GetValue(),
            UntypedNull => null,
            UntypedArray array => array.GetValue().Select(ToSerializable).ToArray(),
            UntypedObject obj => obj.GetValue()
                .ToDictionary(kvp => kvp.Key, kvp => ToSerializable(kvp.Value), StringComparer.Ordinal),
            _ => value,
        };
    }

    private static string? JsonElementToScalar(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => NullIfBlank(element.GetString()),
            JsonValueKind.Number when element.TryGetInt64(out var value) =>
                value.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.Number when element.TryGetDouble(out var value) =>
                value.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static string? ExtractPackageName(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        var tickStart = detail.IndexOf('`', StringComparison.Ordinal);
        if (tickStart >= 0)
        {
            var tickEnd = detail.IndexOf('`', tickStart + 1);
            if (tickEnd > tickStart + 1)
            {
                return detail[(tickStart + 1)..tickEnd];
            }
        }

        const string marker = "package ";
        var markerIndex = detail.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }
        var start = markerIndex + marker.Length;
        var end = detail.IndexOfAny(new[] { ' ', '.', ',', ';' }, start);
        return end > start ? detail[start..end] : detail[start..];
    }

    private static string? ActionFromDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        var lower = detail.ToLowerInvariant();
        if (lower.Contains("revalidation", StringComparison.Ordinal)
            || lower.Contains("revalidate", StringComparison.Ordinal))
        {
            return "revalidate";
        }
        if (lower.Contains("start", StringComparison.Ordinal))
        {
            return "start";
        }
        if (lower.Contains("stop", StringComparison.Ordinal))
        {
            return "stop";
        }
        if (lower.Contains("delete", StringComparison.Ordinal)
            || lower.Contains("deleting", StringComparison.Ordinal))
        {
            return "delete";
        }
        return null;
    }

    private static void AddIfPresent(
        IDictionary<string, object?> envelope,
        string key,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            envelope[key] = value;
        }
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}

public sealed record TranslatedProblemDetails(string Title, string? NextStep, string? TraceId);
