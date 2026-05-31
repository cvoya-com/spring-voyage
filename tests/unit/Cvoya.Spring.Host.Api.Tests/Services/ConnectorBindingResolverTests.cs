// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System;
using System.Collections.Generic;
using System.Text.Json;

using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Host.Api.Services;
using Cvoya.Spring.Manifest;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="ConnectorBindingResolver"/>. The behaviour
/// under test: an operator may bind a connector the package does not
/// declare (a registered-but-undeclared "extra"), which the resolver
/// attaches to the package's top-level unit(s). Only a slug matching no
/// registered connector type is rejected as unknown.
/// </summary>
public class ConnectorBindingResolverTests
{
    private static readonly IReadOnlySet<string> KnownSlugs =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "github", "web-search" };

    private static ConnectorBinding Binding(string slug)
    {
        using var doc = JsonDocument.Parse("{\"repo\":\"o/r\"}");
        return new ConnectorBinding(slug, doc.RootElement.Clone());
    }

    private static ResolvedArtefact Unit(string name, string? containing = null) =>
        new() { Name = name, Kind = ArtefactKind.Unit, ContainingArtefactName = containing };

    private static ResolvedPackage Package(
        IReadOnlyList<ResolvedArtefact> units,
        IReadOnlyList<string>? requiredSlugs = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? requiresByArtefact = null) =>
        new()
        {
            Name = "p",
            Kind = PackageKind.UnitPackage,
            InputValues = new Dictionary<string, string>(),
            Units = units,
            Agents = Array.Empty<ResolvedArtefact>(),
            Skills = Array.Empty<ResolvedArtefact>(),
            HumanTemplates = Array.Empty<ResolvedArtefact>(),
            RequiredConnectorSlugs = requiredSlugs ?? Array.Empty<string>(),
            ConnectorRequiresByArtefact =
                requiresByArtefact ?? new Dictionary<string, IReadOnlyList<string>>(),
        };

    [Fact]
    public void Accepts_undeclared_registered_connector_and_binds_it_to_the_top_level_unit()
    {
        // hello-world shape: declares no connector. Operator adds github anyway.
        var pkg = Package(new[] { Unit("hello-world") });
        var pkgBindings = new Dictionary<string, ConnectorBinding>(StringComparer.OrdinalIgnoreCase)
        {
            ["github"] = Binding("github"),
        };

        var res = ConnectorBindingResolver.Resolve(pkg, pkgBindings, null, KnownSlugs);

        res.UnknownSlugs.ShouldBeEmpty();
        res.Missing.ShouldBeEmpty();
        res.Bindings["hello-world"].ShouldContainKey("github");
    }

    [Fact]
    public void Rejects_a_slug_that_matches_no_registered_connector_type()
    {
        var pkg = Package(new[] { Unit("u") });
        var pkgBindings = new Dictionary<string, ConnectorBinding>(StringComparer.OrdinalIgnoreCase)
        {
            ["foobar"] = Binding("foobar"),
        };

        var res = ConnectorBindingResolver.Resolve(pkg, pkgBindings, null, KnownSlugs);

        res.UnknownSlugs.ShouldContain(e => e.Slug == "foobar" && e.Scope == "package");
        res.Bindings["u"].ShouldNotContainKey("foobar");
    }

    [Fact]
    public void Extra_connector_binds_to_top_level_units_only_not_nested_units()
    {
        var pkg = Package(new[] { Unit("top"), Unit("nested", containing: "top") });
        var pkgBindings = new Dictionary<string, ConnectorBinding>(StringComparer.OrdinalIgnoreCase)
        {
            ["github"] = Binding("github"),
        };

        var res = ConnectorBindingResolver.Resolve(pkg, pkgBindings, null, KnownSlugs);

        res.Bindings["top"].ShouldContainKey("github");
        res.Bindings["nested"].ShouldNotContainKey("github");
    }

    [Fact]
    public void Declared_connector_still_binds_to_declaring_unit_and_missing_when_unsupplied()
    {
        // OSS shape: the unit declares `requires: github`.
        var pkg = Package(
            new[] { Unit("oss") },
            requiredSlugs: new[] { "github" },
            requiresByArtefact: new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["oss"] = new[] { "github" },
            });

        // Unsupplied → Missing.
        var missingRes = ConnectorBindingResolver.Resolve(
            pkg, packageBindings: null, unitBindings: null, KnownSlugs);
        missingRes.Missing.ShouldContain(m => m.Slug == "github" && m.Scope == "package");

        // Supplied → applied, no missing, no unknown.
        var supplied = new Dictionary<string, ConnectorBinding>(StringComparer.OrdinalIgnoreCase)
        {
            ["github"] = Binding("github"),
        };
        var okRes = ConnectorBindingResolver.Resolve(pkg, supplied, null, KnownSlugs);
        okRes.Missing.ShouldBeEmpty();
        okRes.UnknownSlugs.ShouldBeEmpty();
        okRes.Bindings["oss"].ShouldContainKey("github");
    }
}
