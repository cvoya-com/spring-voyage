// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System.Collections.Generic;

/// <summary>
/// Thrown when a <c>package.yaml</c> cannot be parsed or fails structural
/// validation (malformed YAML, missing required fields, name collisions, etc.).
/// </summary>
public class PackageParseException : Exception
{
    /// <summary>Creates a new <see cref="PackageParseException"/>.</summary>
    public PackageParseException(string message) : base(message) { }

    /// <summary>Creates a new <see cref="PackageParseException"/> with an inner cause.</summary>
    public PackageParseException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown when an artefact reference in a <c>package.yaml</c> cannot be
/// resolved — either the target package is unknown or the named artefact
/// does not exist within a known package. The <see cref="Reference"/>
/// property carries the exact string from the manifest so callers can
/// surface an actionable error.
/// </summary>
public class PackageReferenceNotFoundException : Exception
{
    /// <summary>Creates a new <see cref="PackageReferenceNotFoundException"/>.</summary>
    /// <param name="reference">The artefact reference that could not be resolved.</param>
    /// <param name="hint">Optional diagnostic hint (expected path, catalog search key, etc.).</param>
    public PackageReferenceNotFoundException(string reference, string? hint = null)
        : base(BuildMessage(reference, hint))
    {
        Reference = reference;
        Hint = hint;
    }

    /// <summary>The raw reference string from the manifest.</summary>
    public string Reference { get; }

    /// <summary>Diagnostic hint describing where the resolver searched.</summary>
    public string? Hint { get; }

    private static string BuildMessage(string reference, string? hint)
    {
        var msg = $"Artefact reference '{reference}' could not be resolved.";
        if (!string.IsNullOrWhiteSpace(hint))
        {
            msg += $" {hint}";
        }
        return msg;
    }
}

/// <summary>
/// Thrown when cycle detection finds a circular reference in the package's
/// artefact graph. The <see cref="CyclePath"/> property lists the
/// reference chain that forms the cycle, end-to-end, so operators can see
/// the exact offending edges.
/// </summary>
public class PackageCycleException : Exception
{
    /// <summary>Creates a new <see cref="PackageCycleException"/>.</summary>
    /// <param name="cyclePath">
    /// Ordered list of artefact names forming the cycle. The last element
    /// is the one that closes the cycle back to the first.
    /// </param>
    public PackageCycleException(IReadOnlyList<string> cyclePath)
        : base(BuildMessage(cyclePath))
    {
        CyclePath = cyclePath;
    }

    /// <summary>The ordered cycle path (the back-edge closes cycle[^1] → cycle[0]).</summary>
    public IReadOnlyList<string> CyclePath { get; }

    private static string BuildMessage(IReadOnlyList<string> path)
        => $"Circular artefact reference detected: {string.Join(" → ", path)} → {path[0]}";
}

/// <summary>
/// Thrown when package input validation fails: a required input is missing,
/// a supplied value does not match the declared type, etc.
/// </summary>
public class PackageInputValidationException : Exception
{
    /// <summary>Creates a new <see cref="PackageInputValidationException"/>.</summary>
    /// <param name="inputName">The name of the offending input.</param>
    /// <param name="message">Human-readable description of the failure.</param>
    public PackageInputValidationException(string inputName, string message)
        : base(message)
    {
        InputName = inputName;
    }

    /// <summary>The name of the input that caused the validation failure.</summary>
    public string InputName { get; }
}