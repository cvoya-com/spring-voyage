// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest.Tests;

using System;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="LocalSymbolMap"/>: the per-install
/// local-symbol → Guid book-keeping introduced by #1629 PR7.
/// </summary>
public class LocalSymbolMapTests
{
    [Fact]
    public void GetOrMint_FreshSymbol_ReturnsNewGuid()
    {
        var map = new LocalSymbolMap();

        var id = map.GetOrMint(ArtefactKind.Unit, "u_eng");

        id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void GetOrMint_SameSymbolTwice_ReturnsSameGuid()
    {
        var map = new LocalSymbolMap();

        var first = map.GetOrMint(ArtefactKind.Unit, "u_eng");
        var second = map.GetOrMint(ArtefactKind.Unit, "u_eng");

        second.ShouldBe(first);
    }

    [Fact]
    public void GetOrMint_DifferentKinds_DifferentGuids()
    {
        // Symbols are kind-scoped: a unit named `eng` and an agent named
        // `eng` are two distinct artefacts and get distinct Guids.
        var map = new LocalSymbolMap();

        var unitId = map.GetOrMint(ArtefactKind.Unit, "eng");
        var agentId = map.GetOrMint(ArtefactKind.Agent, "eng");

        agentId.ShouldNotBe(unitId);
    }

    [Fact]
    public void TryResolve_LocalSymbol_ReturnsMintedGuid()
    {
        var map = new LocalSymbolMap();
        var minted = map.GetOrMint(ArtefactKind.Unit, "u_eng");

        var ok = map.TryResolve(ArtefactKind.Unit, "u_eng", out var resolved);

        ok.ShouldBeTrue();
        resolved.ShouldBe(minted);
    }

    [Fact]
    public void TryResolve_GuidReference_ParsesAsCrossPackage()
    {
        // A reference shaped like a Guid is treated as cross-package even
        // when no local symbol matches — display-name lookup is gone, so
        // the only accepted cross-package form is a Guid.
        var map = new LocalSymbolMap();
        var crossPkgGuid = Guid.NewGuid();

        var ok = map.TryResolve(
            ArtefactKind.Agent, crossPkgGuid.ToString("N"), out var resolved);

        ok.ShouldBeTrue();
        resolved.ShouldBe(crossPkgGuid);
    }

    [Fact]
    public void TryResolve_GuidReference_DashedFormAccepted()
    {
        // GuidFormatter.TryParse accepts both no-dash and dashed forms so
        // copy-paste from logs / portal works either way.
        var map = new LocalSymbolMap();
        var id = Guid.NewGuid();

        var ok = map.TryResolve(
            ArtefactKind.Agent, id.ToString("D"), out var resolved);

        ok.ShouldBeTrue();
        resolved.ShouldBe(id);
    }

    [Fact]
    public void TryResolve_UnknownSymbol_ReturnsFalse()
    {
        var map = new LocalSymbolMap();
        map.GetOrMint(ArtefactKind.Unit, "u_eng");

        var ok = map.TryResolve(ArtefactKind.Unit, "u_unknown", out _);

        ok.ShouldBeFalse();
    }

    [Fact]
    public void TryResolve_NullOrEmpty_ReturnsFalse()
    {
        var map = new LocalSymbolMap();

        map.TryResolve(ArtefactKind.Unit, null, out _).ShouldBeFalse();
        map.TryResolve(ArtefactKind.Unit, string.Empty, out _).ShouldBeFalse();
        map.TryResolve(ArtefactKind.Unit, "   ", out _).ShouldBeFalse();
    }

    [Fact]
    public void Bind_OverridesMintedBinding()
    {
        // Bind is used by the retry path so the map reuses the staging
        // row's id rather than minting a fresh one. Subsequent reads return
        // the bound id.
        var map = new LocalSymbolMap();
        var stagingRowId = Guid.NewGuid();

        map.Bind(ArtefactKind.Unit, "u_eng", stagingRowId);
        var resolved = map.GetOrMint(ArtefactKind.Unit, "u_eng");

        resolved.ShouldBe(stagingRowId);
    }
}