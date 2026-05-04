// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System;
using System.Collections.Generic;

using Cvoya.Spring.Core.Identifiers;

/// <summary>
/// Per-install bookkeeping for the IaC-style local symbols introduced in
/// #1629 PR7. Each artefact in a package's manifest is identified by a
/// short author-supplied symbol (<c>u_eng</c>, <c>a_alice</c>, …) that is
/// scoped to the manifest file. At install time the platform mints a fresh
/// Guid per symbol; the map is used to:
/// <list type="bullet">
///   <item><description>
///     Resolve every <c>members[]</c> reference in a unit YAML to the Guid
///     of the corresponding peer artefact in the same package.
///   </description></item>
///   <item><description>
///     Wire <c>UnitDefinitionEntity.Id</c> / <c>AgentDefinitionEntity.Id</c>
///     to the same Guid the activator later registers in the directory, so
///     the staging row and the directory entry are the same row instead of
///     two near-duplicates with different ids.
///   </description></item>
/// </list>
/// References that parse as Guids (via <see cref="GuidFormatter.TryParse"/>)
/// are treated as cross-package — they bypass the map and are returned
/// verbatim, matching the "Guid-only cross-package" rule.
/// </summary>
/// <remarks>
/// The map is in-memory only: local symbols never persist past Phase 2.
/// Construct a fresh map per install batch.
/// </remarks>
public sealed class LocalSymbolMap
{
    private readonly Dictionary<string, Guid> _byKey = new(StringComparer.Ordinal);

    /// <summary>
    /// Reserves a fresh Guid for the local symbol at <paramref name="kind"/>
    /// + <paramref name="symbol"/>. Idempotent — calling twice with the same
    /// kind/symbol returns the previously-minted Guid so two passes over the
    /// same package produce stable identity.
    /// </summary>
    /// <param name="kind">The artefact kind (unit / agent / skill / workflow).</param>
    /// <param name="symbol">
    /// The local symbol — typically the artefact's <c>name</c> / <c>id</c>
    /// field, scoped to the package. Compared case-sensitively.
    /// </param>
    /// <returns>The Guid bound to the symbol for this install.</returns>
    public Guid GetOrMint(ArtefactKind kind, string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        var key = Key(kind, symbol);
        if (!_byKey.TryGetValue(key, out var existing))
        {
            existing = Guid.NewGuid();
            _byKey[key] = existing;
        }
        return existing;
    }

    /// <summary>
    /// Binds an explicit Guid to a local symbol — used by the retry path so
    /// the map reuses the staging row's id rather than minting a fresh one.
    /// Subsequent <see cref="GetOrMint(ArtefactKind, string)"/> calls for
    /// the same kind / symbol return this id. Calling with a different id
    /// for a previously-bound symbol overwrites the binding.
    /// </summary>
    public void Bind(ArtefactKind kind, string symbol, Guid id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        _byKey[Key(kind, symbol)] = id;
    }

    /// <summary>
    /// Resolves a manifest reference to the Guid identity of the target
    /// artefact. The reference may be either a local symbol (looked up in
    /// the map) or a Guid in any form <see cref="GuidFormatter.TryParse"/>
    /// accepts (treated as a cross-package reference).
    /// </summary>
    /// <param name="kind">
    /// The artefact kind the reference resolves through. Local-symbol
    /// lookups are kind-scoped; Guid references are cross-kind.
    /// </param>
    /// <param name="reference">The raw reference value from the manifest.</param>
    /// <param name="result">The resolved Guid on success.</param>
    /// <returns>
    /// <c>true</c> when the reference resolves to either a previously
    /// minted local symbol or a syntactically valid Guid; <c>false</c>
    /// when the symbol is unknown and the value does not parse as a Guid.
    /// </returns>
    public bool TryResolve(ArtefactKind kind, string? reference, out Guid result)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            result = Guid.Empty;
            return false;
        }

        // Cross-package Guid wins. We probe Guid form first so a symbol
        // shaped like a Guid (an unlikely author choice, but legal) does
        // not silently shadow a real cross-package Guid reference.
        if (GuidFormatter.TryParse(reference, out result))
        {
            return true;
        }

        if (_byKey.TryGetValue(Key(kind, reference), out result))
        {
            return true;
        }

        result = Guid.Empty;
        return false;
    }

    private static string Key(ArtefactKind kind, string symbol)
        => $"{kind}:{symbol}";
}