// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest.Tests;

using System;
using System.Collections.Generic;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="LocalSymbolValidator"/>: the path-style
/// rejection sigil and the local-symbol-uniqueness invariant introduced by
/// #1629 PR7.
/// </summary>
public class LocalSymbolValidatorTests
{
    [Theory]
    [InlineData("unit://eng/backend/alice")]
    [InlineData("agent://alice")]
    [InlineData("human://owner")]
    [InlineData("scheme://anything-here")]
    public void RejectPathStyleReference_PathStyle_ThrowsManifestParseException(string pathRef)
    {
        Should.Throw<ManifestParseException>(() =>
                LocalSymbolValidator.RejectPathStyleReference(pathRef, "members[0].agent"))
            .Message.ShouldContain(pathRef);
    }

    [Fact]
    public void RejectPathStyleReference_PathStyle_PackageLayer_ThrowsPackageParseException()
    {
        Should.Throw<PackageParseException>(() =>
                LocalSymbolValidator.RejectPathStyleReference(
                    "unit://eng/backend",
                    "subUnits[0]",
                    GrammarLayer.PackageManifest))
            .Message.ShouldContain("subUnits[0]");
    }

    [Fact]
    public void RejectPathStyleReference_PathStyle_ErrorMessageNamesNewGrammar()
    {
        var ex = Should.Throw<ManifestParseException>(() =>
            LocalSymbolValidator.RejectPathStyleReference(
                "unit://eng/backend/alice",
                "members[0].unit"));

        // The error must point at the new grammar (local symbols + Guid)
        // so authors can immediately see how to fix the offending entry.
        ex.Message.ShouldContain("local symbol");
        ex.Message.ShouldContain("Guid");
    }

    [Theory]
    [InlineData("u_eng")]
    [InlineData("a_alice")]
    [InlineData("plain-symbol")]
    [InlineData("8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7")]            // Guid (no-dash)
    [InlineData("8c5fab2a-8e7e-4b9c-92f1-d8a3b4c5d6e7")]        // Guid (dashed)
    public void RejectPathStyleReference_NonPathRef_DoesNotThrow(string reference)
    {
        // Non-path-style references — local symbols and Guids alike — are
        // not the validator's concern; this method only rejects the
        // scheme-with-double-slash sigil.
        Should.NotThrow(() =>
            LocalSymbolValidator.RejectPathStyleReference(reference, "members[0].agent"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void RejectPathStyleReference_NullOrEmpty_DoesNotThrow(string? reference)
    {
        // Empty values mean the slot is unset; the validator must stay out
        // of the way so unrelated required-field validation can produce the
        // canonical error.
        Should.NotThrow(() =>
            LocalSymbolValidator.RejectPathStyleReference(reference, "members[0].agent"));
    }

    [Theory]
    [InlineData("8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7", true)]         // 32-char no-dash
    [InlineData("8c5fab2a-8e7e-4b9c-92f1-d8a3b4c5d6e7", true)]     // dashed
    [InlineData("u_eng", false)]
    [InlineData("a_alice", false)]
    [InlineData("not-a-guid", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsGuidReference_RecognisesGuidsAndLocalSymbols(string? reference, bool expected)
    {
        LocalSymbolValidator.IsGuidReference(reference).ShouldBe(expected);
    }

    [Fact]
    public void EnsureUniqueSymbols_NoCollisions_DoesNotThrow()
    {
        var symbols = new List<(string Symbol, string FieldName)>
        {
            ("u_eng", "units[0].name"),
            ("u_backend", "units[1].name"),
            ("a_alice", "agents[0].id"),
        };

        Should.NotThrow(() => LocalSymbolValidator.EnsureUniqueSymbols(symbols));
    }

    [Fact]
    public void EnsureUniqueSymbols_DuplicateSymbol_ThrowsPackageParseException()
    {
        var symbols = new List<(string Symbol, string FieldName)>
        {
            ("u_eng", "units[0].name"),
            ("u_eng", "units[1].name"),
        };

        var ex = Should.Throw<PackageParseException>(() =>
            LocalSymbolValidator.EnsureUniqueSymbols(symbols));

        ex.Message.ShouldContain("u_eng");
        ex.Message.ShouldContain("units[0].name");
        ex.Message.ShouldContain("units[1].name");
    }

    [Fact]
    public void EnsureUniqueSymbols_NullOrWhitespaceSymbols_Skipped()
    {
        var symbols = new List<(string Symbol, string FieldName)>
        {
            (string.Empty, "units[0].name"),
            ("   ", "units[1].name"),
            ("u_eng", "units[2].name"),
        };

        Should.NotThrow(() => LocalSymbolValidator.EnsureUniqueSymbols(symbols));
    }

    [Fact]
    public void EnsureUniqueSymbols_CaseSensitive()
    {
        // Local symbols are compared case-sensitively — `u_eng` and `U_Eng`
        // are different identifiers. This matches IaC conventions (Bicep/
        // Terraform) and keeps grep-ability strict.
        var symbols = new List<(string Symbol, string FieldName)>
        {
            ("u_eng", "units[0].name"),
            ("U_Eng", "units[1].name"),
        };

        Should.NotThrow(() => LocalSymbolValidator.EnsureUniqueSymbols(symbols));
    }
}