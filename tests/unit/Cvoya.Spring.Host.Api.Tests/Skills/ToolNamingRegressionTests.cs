// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Skills;

using System.Collections.Generic;
using System.Linq;

using Cvoya.Spring.Core.Skills;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// Structural regression guard for the canonical tool-naming contract (#2334).
/// Enumerates every <see cref="ISkillRegistry"/> wired into the API host's DI
/// container and asserts:
/// <list type="bullet">
///   <item><description>Every <see cref="ToolDefinition.Name"/> matches
///     <see cref="ToolNaming.Pattern"/>.</description></item>
///   <item><description>No two registries declare overlapping tool names —
///     <see cref="Cvoya.Spring.Dapr.Mcp.McpServer"/> would already throw on
///     overlap at construction time, but the explicit assertion keeps the
///     failure surface inside the test suite so future authors see what broke.
///     </description></item>
/// </list>
/// This is the catches-all guard for the rename PR (#2334 / Sub A): any new
/// registry that re-introduces the old slash- or underscore-prefixed style
/// fails here before reviewer eyes touch it.
/// </summary>
public class ToolNamingRegressionTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ToolNamingRegressionTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void EveryRegisteredSkillRegistry_AdvertisesCanonicalToolIds()
    {
        var registries = ResolveRegistries();
        registries.ShouldNotBeEmpty(
            "the API host must register at least one ISkillRegistry — if this fires, " +
            "the DI graph drifted and the rest of the assertions are vacuous.");

        var violations = new List<string>();
        foreach (var registry in registries)
        {
            foreach (var tool in registry.GetToolDefinitions())
            {
                if (!ToolNaming.IsValid(tool.Name))
                {
                    violations.Add(
                        $"registry {registry.GetType().FullName} (Name='{registry.Name}') advertises "
                        + $"tool '{tool.Name}' which does not match {ToolNaming.Pattern}.");
                }
            }
        }

        violations.ShouldBeEmpty(
            "Every tool id must follow '<namespace>.<tool_name>' "
            + "(lowercase, dotted-snake). Offenders:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void NoTwoRegistries_DeclareOverlappingToolNames()
    {
        var registries = ResolveRegistries();

        var byTool = new Dictionary<string, List<string>>(System.StringComparer.Ordinal);
        foreach (var registry in registries)
        {
            foreach (var tool in registry.GetToolDefinitions())
            {
                if (!byTool.TryGetValue(tool.Name, out var owners))
                {
                    owners = new List<string>();
                    byTool[tool.Name] = owners;
                }
                owners.Add($"{registry.GetType().FullName} ({registry.Name})");
            }
        }

        var collisions = byTool
            .Where(kv => kv.Value.Count > 1)
            .Select(kv => $"tool '{kv.Key}' declared by [{string.Join(", ", kv.Value)}]")
            .ToList();

        collisions.ShouldBeEmpty(
            "Each tool id must belong to exactly one ISkillRegistry. McpServer "
            + "already throws on collision at startup; this test surfaces the same "
            + "error during the unit-test loop. Collisions:\n  "
            + string.Join("\n  ", collisions));
    }

    [Fact]
    public void GetToolsByNamespace_FiltersByLeadingSegment()
    {
        var registries = ResolveRegistries();
        var canonical = registries
            .SelectMany(r => r.GetToolDefinitions())
            .ToList();

        // Pick any namespace that exists in the live host so the test asserts
        // against real data rather than fixture state. Skip the assertion when
        // no namespaces are present (the empty-host shape is asserted elsewhere).
        var namespaces = canonical
            .Select(t => t.Namespace)
            .Distinct(System.StringComparer.Ordinal)
            .ToList();

        foreach (var ns in namespaces)
        {
            var expected = canonical
                .Where(t => string.Equals(t.Namespace, ns, System.StringComparison.Ordinal))
                .Select(t => t.Name)
                .OrderBy(n => n, System.StringComparer.Ordinal)
                .ToList();

            var actual = registries
                .SelectMany(r => r.GetToolsByNamespace(ns))
                .Select(t => t.Name)
                .OrderBy(n => n, System.StringComparer.Ordinal)
                .ToList();

            actual.ShouldBe(expected,
                $"GetToolsByNamespace('{ns}') aggregated across registries must match the filtered tool surface.");
        }
    }

    private IReadOnlyList<ISkillRegistry> ResolveRegistries()
    {
        // Spin a thin client just to materialise the host's DI container —
        // the factory is shared via IClassFixture so this is cheap.
        using var client = _factory.CreateClient();
        return _factory.Services.GetServices<ISkillRegistry>().ToList();
    }
}
