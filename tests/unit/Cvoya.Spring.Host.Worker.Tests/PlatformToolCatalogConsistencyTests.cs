// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Worker.Tests;

using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Skills;
using Cvoya.Spring.Host.Worker.Composition;

using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// Drift guard for the single-source-of-truth platform-tool catalog
/// (<see cref="PlatformToolCatalog"/>) — the #2988 acceptance test.
/// </summary>
/// <remarks>
/// <para>
/// The catalog's per-category <c>usage_guidance</c> is what an agent reads
/// from <c>sv.tools.list(category)</c> to decide <em>when</em> to reach for
/// a tool. The original gap (F3 of #2986) was that the guidance was
/// narrower than the category's tool list — the observability guidance
/// omitted <c>sv.runtime.report_decision</c> and the directory guidance
/// omitted the six <c>sv.directory.*</c> expansion tools. This test pins
/// that for every catalog category, every statically-registered tool in
/// one of the category's <see cref="PlatformToolCategory.OwnedNamespaces"/>
/// is referenced by name in that category's
/// <see cref="PlatformToolCategory.UsageGuidance"/>, so the guidance can
/// never silently fall behind the registered tool set again.
/// </para>
/// <para>
/// The required tool set is derived from the Worker's live DI registry
/// graph, not a hand-maintained list — so adding a tool to an owned
/// namespace, or removing one, automatically tightens or relaxes the
/// requirement. A tool grouped into a category from a namespace the
/// category does not own (e.g. the dynamic / transitional
/// <c>sv.expertise.*</c> tools grouped under <c>directory</c>, being
/// folded into directory discovery by #2989) is intentionally not
/// required, so the catalog tolerates that namespace's removal.
/// </para>
/// </remarks>
public class PlatformToolCatalogConsistencyTests
{
    [Fact]
    public void EveryOwnedNamespaceToolIsNamedInItsCategoryGuidance()
    {
        var toolsByCategory = ResolveStaticallyRegisteredToolsByCategory();

        var failures = new List<string>();
        foreach (var category in PlatformToolCatalog.Categories)
        {
            if (!toolsByCategory.TryGetValue(category.Token, out var tools))
            {
                continue;
            }

            foreach (var toolName in tools)
            {
                var ns = NamespaceOf(toolName);
                if (!category.OwnedNamespaces.Contains(ns, StringComparer.Ordinal))
                {
                    // Grouped into the category but not an owned namespace
                    // (e.g. sv.expertise.* under directory) — not required
                    // in the guidance.
                    continue;
                }

                if (!category.UsageGuidance.Contains(toolName, StringComparison.Ordinal))
                {
                    failures.Add(
                        $"category '{category.Token}': guidance does not name owned-namespace " +
                        $"tool '{toolName}'");
                }
            }
        }

        failures.ShouldBeEmpty(
            "PlatformToolCatalog usage_guidance must name every statically-registered tool in " +
            "each category's owned namespaces (#2988). Add a clause for each missing tool in " +
            "src/Cvoya.Spring.Core/Skills/PlatformToolCatalog.cs:" + Environment.NewLine +
            string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Sanity check that every catalog category that owns a namespace
    /// actually has at least one statically-registered tool in it — a
    /// catalog entry whose owned namespaces match nothing registered is a
    /// stale catalog entry. (The dynamic-only <c>expertise</c> category is
    /// deliberately absent from the catalog, so this does not flag it.)
    /// </summary>
    [Fact]
    public void EveryCatalogCategoryHasAtLeastOneRegisteredTool()
    {
        var toolsByCategory = ResolveStaticallyRegisteredToolsByCategory();

        foreach (var category in PlatformToolCatalog.Categories)
        {
            toolsByCategory.ShouldContainKey(
                category.Token,
                $"catalog category '{category.Token}' has no statically-registered tools — " +
                "remove the stale catalog entry or restore the registry.");
        }
    }

    private static string NamespaceOf(string toolName)
    {
        var lastDot = toolName.LastIndexOf('.');
        return lastDot < 0 ? toolName : toolName[..lastDot];
    }

    /// <summary>
    /// Groups every statically-registered <see cref="ToolDefinition"/> name
    /// by its <see cref="ToolDefinition.Category"/>, excluding tools with no
    /// category and the dynamic-by-design <see cref="ExpertiseSkillRegistry"/>
    /// surface (per-tenant <c>sv.expertise.&lt;slug&gt;</c> tools).
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>>
        ResolveStaticallyRegisteredToolsByCategory()
    {
        using var provider = BuildWorkerServiceProvider();
        var registries = provider.GetServices<ISkillRegistry>().ToList();

        var byCategory = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var registry in registries)
        {
            if (registry is ExpertiseSkillRegistry)
            {
                continue;
            }

            foreach (var tool in registry.GetToolDefinitions())
            {
                if (string.IsNullOrEmpty(tool.Category))
                {
                    continue;
                }
                if (!byCategory.TryGetValue(tool.Category, out var list))
                {
                    list = new List<string>();
                    byCategory[tool.Category] = list;
                }
                list.Add(tool.Name);
            }
        }

        return byCategory.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<string>)kvp.Value,
            StringComparer.Ordinal);
    }

    private static ServiceProvider BuildWorkerServiceProvider()
    {
        var builder = WebApplication.CreateBuilder();

        builder.Configuration["ConnectionStrings:SpringDb"] =
            "Host=test;Database=test;Username=test;Password=test";

        builder.Services.AddWorkerServices(builder.Configuration);

        var dbContextDescriptors = builder.Services
            .Where(d => d.ServiceType == typeof(DbContextOptions<Cvoya.Spring.Dapr.Data.SpringDbContext>))
            .ToList();
        foreach (var descriptor in dbContextDescriptors)
        {
            builder.Services.Remove(descriptor);
        }
        builder.Services.AddDbContext<Cvoya.Spring.Dapr.Data.SpringDbContext>(opt =>
            opt.UseInMemoryDatabase($"catalog-consistency-{Guid.NewGuid():N}"));

        return builder.Services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = false,
            ValidateOnBuild = false,
        });
    }
}
