// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System;
using System.Collections.Generic;

using Cvoya.Spring.Core.Identifiers;

/// <summary>
/// Selects which manifest layer is reporting the grammar failure. Determines
/// which exception type <see cref="LocalSymbolValidator"/> raises so the
/// existing CLI / install error mapping (which catches
/// <see cref="ManifestParseException"/> at the unit layer and
/// <see cref="PackageParseException"/> at the package layer) keeps working.
/// </summary>
public enum GrammarLayer
{
    /// <summary>The unit-level manifest (a <c>units/foo.yaml</c> body).</summary>
    UnitManifest,

    /// <summary>The package-level manifest (the root <c>package.yaml</c>).</summary>
    PackageManifest,
}

/// <summary>
/// Validation helpers for the v0.1 manifest grammar (#1629 PR7):
/// <list type="bullet">
///   <item><description>
///     Within a manifest, references between artefacts use IaC-style local
///     symbols (Bicep / Terraform pattern). A local symbol is a short author-
///     supplied identifier that is scoped to the manifest file and never
///     persists in the database — at install time it is mapped to a
///     freshly-minted Guid for the corresponding artefact.
///   </description></item>
///   <item><description>
///     References to entities created by a <i>different</i> package use
///     32-char no-dash hex Guids (or any form <see cref="Guid.TryParse"/>
///     accepts). Display-name lookup across packages is gone; display names
///     aren't unique, so resolving by name would silently bind to the wrong
///     target.
///   </description></item>
///   <item><description>
///     Path-style references like <c>unit://eng/backend/alice</c> are
///     rejected with an actionable error pointing at the new grammar.
///   </description></item>
/// </list>
/// </summary>
/// <remarks>
/// This helper exists so the parser, the validator, and the install
/// activator agree on the exact grammar without each implementing its own
/// regex. Errors raised here surface as <see cref="ManifestParseException"/>
/// or <see cref="PackageParseException"/> at the call site.
/// </remarks>
public static class LocalSymbolValidator
{
    /// <summary>
    /// Inspects <paramref name="reference"/> and rejects path-style
    /// references (<c>scheme://path</c>) that the v0.1 grammar no longer
    /// supports. The check is purely syntactic — it does not attempt to
    /// resolve the reference. Use this on every user-facing reference field
    /// before further parsing.
    /// </summary>
    /// <param name="reference">The raw string from the manifest field.</param>
    /// <param name="fieldName">
    /// The grammar field carrying the reference (e.g. <c>members[].agent</c>,
    /// <c>members[].unit</c>). Surfaced in the error message so authors can
    /// locate the offending entry.
    /// </param>
    /// <param name="layer">
    /// Selects which exception type the failure surfaces as so callers in
    /// the unit-manifest and package-manifest layers each see the exception
    /// shape their existing handlers expect. Defaults to
    /// <see cref="GrammarLayer.UnitManifest"/>.
    /// </param>
    /// <exception cref="ManifestParseException">
    /// Thrown when <paramref name="reference"/> contains the <c>://</c>
    /// path-style sigil and <paramref name="layer"/> is
    /// <see cref="GrammarLayer.UnitManifest"/>. The message names the
    /// offending field and shows the canonical IaC-local-symbol replacement.
    /// </exception>
    /// <exception cref="PackageParseException">
    /// Thrown when <paramref name="reference"/> contains the <c>://</c>
    /// path-style sigil and <paramref name="layer"/> is
    /// <see cref="GrammarLayer.PackageManifest"/>.
    /// </exception>
    public static void RejectPathStyleReference(
        string? reference,
        string fieldName,
        GrammarLayer layer = GrammarLayer.UnitManifest)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return;
        }

        if (reference.Contains("://", StringComparison.Ordinal))
        {
            var message =
                $"Field '{fieldName}' uses the path-style reference '{reference}', " +
                "which is not supported in the v0.1 manifest grammar. " +
                "Within a manifest, reference peer artefacts by their local " +
                "symbol (e.g. 'agent: a_alice', 'unit: u_backend'). For " +
                "cross-package references, paste the target's 32-char " +
                "no-dash hex Guid (e.g. 'agent: 8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7').";

            throw layer == GrammarLayer.PackageManifest
                ? new PackageParseException(message)
                : new ManifestParseException(message);
        }
    }

    /// <summary>
    /// True when <paramref name="reference"/> parses as a Guid in any of the
    /// formats <see cref="GuidFormatter.TryParse"/> accepts (no-dash 32-char,
    /// dashed, braced). Use this to distinguish a cross-package Guid
    /// reference from a local symbol — Guid wins (treat as cross-package);
    /// otherwise the value is treated as a local symbol.
    /// </summary>
    public static bool IsGuidReference(string? reference)
        => GuidFormatter.TryParse(reference, out _);

    /// <summary>
    /// Validates that the supplied local symbols are unique within the
    /// manifest. Used during package-level parsing so a duplicate <c>ref:</c>
    /// or duplicate slot name fails fast with the exact offender named.
    /// </summary>
    /// <param name="symbols">
    /// Pairs of (symbol, fieldName) describing every local symbol in the
    /// manifest. Symbols are compared case-sensitively.
    /// </param>
    /// <exception cref="PackageParseException">
    /// Thrown when one or more symbols collide. The message lists every
    /// duplicate and its source field so authors can find both occurrences.
    /// </exception>
    public static void EnsureUniqueSymbols(IReadOnlyList<(string Symbol, string FieldName)> symbols)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var collisions = new List<string>();

        foreach (var (symbol, fieldName) in symbols)
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                continue;
            }

            if (seen.TryGetValue(symbol, out var firstField))
            {
                collisions.Add($"'{symbol}' (declared by both '{firstField}' and '{fieldName}')");
            }
            else
            {
                seen[symbol] = fieldName;
            }
        }

        if (collisions.Count > 0)
        {
            throw new PackageParseException(
                $"Local symbol(s) collide within the manifest: {string.Join(", ", collisions)}. " +
                "Each local symbol (the artefact's slot name or 'ref' field) must be unique " +
                "within a single manifest — local symbols are scoped to the file and mapped " +
                "to fresh Guids at install time.");
        }
    }
}