// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Worker.Tests;

using System.Text.RegularExpressions;

using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Host.Worker.Composition;

using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// CI sync test for the platform-tools catalog doc (#2748).
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/reference/platform-tools.md</c> is the authoritative inventory
/// of every MCP tool the Spring Voyage platform exposes. This test parses
/// the doc, gathers every <c>ToolDefinition</c> registered through an
/// <see cref="ISkillRegistry"/> in the Worker's DI graph, and asserts the
/// two sets agree exactly. Adding, renaming, or removing a tool without
/// updating the doc fails the build.
/// </para>
/// <para>
/// Every platform <see cref="ISkillRegistry"/> now publishes a fixed tool
/// surface, so the equality check has no dynamic-registry exemption. (The
/// dynamic <c>sv.expertise.&lt;slug&gt;</c> surface was removed in #2989 —
/// expertise discovery is the caller-aware <c>sv.directory.*</c> tools.)
/// </para>
/// </remarks>
public class PlatformToolsCatalogDocTests
{
    /// <summary>
    /// Path to the canonical catalog doc, resolved from the repo root.
    /// The CI test runs from <c>tests/unit/Cvoya.Spring.Host.Worker.Tests/bin/...</c>;
    /// walk up to the repo root and dive into <c>docs/reference/</c>.
    /// </summary>
    private static readonly string CatalogDocPath = ResolveCatalogDocPath();

    [Fact]
    public void CatalogDoc_Exists()
    {
        File.Exists(CatalogDocPath).ShouldBeTrue(
            $"docs/reference/platform-tools.md must exist (looked at {CatalogDocPath}). " +
            "The doc is the canonical platform-tools inventory referenced from the " +
            "platform-prompt layer and the test below.");
    }

    [Fact]
    public void CatalogDoc_NamesEveryStaticallyRegisteredTool()
    {
        var documented = ParseToolNamesFromDoc();
        documented.ShouldNotBeEmpty(
            "The catalog doc must list at least one tool — an empty doc is a parse failure.");

        var registered = ResolveStaticallyRegisteredToolNames();

        var documentedNotRegistered = documented.Except(registered).OrderBy(s => s).ToArray();
        var registeredNotDocumented = registered.Except(documented).OrderBy(s => s).ToArray();

        if (documentedNotRegistered.Length > 0)
        {
            // A tool listed in the doc but not registered by any ISkillRegistry
            // — either the doc has a stale entry to remove or a registry
            // failed to register a tool it advertised.
            throw new Xunit.Sdk.XunitException(
                "The catalog doc names tools that no statically-registered ISkillRegistry " +
                "exposes. Remove the stale doc rows or restore the missing registrations: " +
                string.Join(", ", documentedNotRegistered));
        }

        if (registeredNotDocumented.Length > 0)
        {
            // A registered tool not in the doc — almost always means a
            // tool was added without updating the doc. Add a row under the
            // owning registry's section in docs/reference/platform-tools.md.
            throw new Xunit.Sdk.XunitException(
                "The Worker registers tools the catalog doc does not document. " +
                "Add a row to docs/reference/platform-tools.md under the owning " +
                "registry's section: " + string.Join(", ", registeredNotDocumented));
        }
    }

    /// <summary>
    /// Reads every <c>`tool.name`</c>-shaped backtick code span from the
    /// doc's markdown tables. The doc consistently uses backticked tool
    /// names in its tables, so the lexer below — backticks around a
    /// dotted-snake identifier of two or more segments — is sufficient
    /// to identify documented tools without needing a full markdown
    /// parser.
    /// </summary>
    private static IReadOnlySet<string> ParseToolNamesFromDoc()
    {
        var text = File.ReadAllText(CatalogDocPath);

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in BacktickedToolNameRegex.Matches(text))
        {
            var name = match.Groups[1].Value;

            // Exclude prose mentions that are obviously not concrete
            // tool names — placeholders that appear in code spans for
            // the input-shape examples in the table headers, etc.
            if (name.EndsWith(".<slug>", StringComparison.Ordinal))
            {
                continue;
            }
            if (name.Contains("<", StringComparison.Ordinal) ||
                name.Contains(">", StringComparison.Ordinal))
            {
                continue;
            }

            found.Add(name);
        }

        return found;
    }

    /// <summary>
    /// Matches a backticked dotted-snake identifier of two or more
    /// segments — <c>`sv.messaging.send`</c>, <c>`github.get_installation_token`</c>.
    /// Anchors on the backticks so prose mentions like "the
    /// `sv.messaging.*` namespace" do not match (the trailing
    /// <c>.*</c> fails the pattern).
    /// </summary>
    private static readonly Regex BacktickedToolNameRegex = new(
        @"`([a-z][a-z0-9_]*\.[a-z][a-z0-9_]*(?:\.[a-z][a-z0-9_]*)*)`",
        RegexOptions.Compiled);

    /// <summary>
    /// Iterates the Worker's DI-resolved <see cref="ISkillRegistry"/>
    /// set, calls <see cref="ISkillRegistry.GetToolDefinitions"/>, and
    /// returns the union of every tool name.
    /// </summary>
    private static IReadOnlySet<string> ResolveStaticallyRegisteredToolNames()
    {
        using var provider = BuildWorkerServiceProvider();
        var registries = provider.GetServices<ISkillRegistry>().ToList();

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var registry in registries)
        {
            foreach (var tool in registry.GetToolDefinitions())
            {
                names.Add(tool.Name);
            }
        }

        return names;
    }

    private static ServiceProvider BuildWorkerServiceProvider()
    {
        var builder = WebApplication.CreateBuilder();

        // Satisfy the #261 fail-fast ConnectionStrings:SpringDb check
        // even though the in-memory swap below supersedes it — the
        // value just has to be non-empty.
        builder.Configuration["ConnectionStrings:SpringDb"] =
            "Host=test;Database=test;Username=test;Password=test";

        builder.Services.AddWorkerServices(builder.Configuration);

        // Swap Npgsql for an in-memory EF Core provider so the test
        // doesn't need an actual database connection. Mirrors the
        // pattern in WorkerCompositionTests.
        var dbContextDescriptors = builder.Services
            .Where(d => d.ServiceType == typeof(DbContextOptions<Cvoya.Spring.Dapr.Data.SpringDbContext>))
            .ToList();
        foreach (var descriptor in dbContextDescriptors)
        {
            builder.Services.Remove(descriptor);
        }
        builder.Services.AddDbContext<Cvoya.Spring.Dapr.Data.SpringDbContext>(opt =>
            opt.UseInMemoryDatabase($"catalog-doc-{Guid.NewGuid():N}"));

        return builder.Services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = false,
            ValidateOnBuild = false,
        });
    }

    /// <summary>
    /// Walks up from <see cref="AppContext.BaseDirectory"/> until a
    /// directory containing <c>docs/reference/platform-tools.md</c> is
    /// found. Falls back to a path relative to the test project's
    /// source tree if the walk fails (developer running the test from
    /// an unusual cwd).
    /// </summary>
    private static string ResolveCatalogDocPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "docs", "reference", "platform-tools.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        // Fall through: name the expected path so the existence test
        // (above) emits an actionable failure rather than an opaque
        // null reference.
        return Path.Combine("docs", "reference", "platform-tools.md");
    }
}
