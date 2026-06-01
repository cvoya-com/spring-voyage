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
    /// Pins the doc's "Category usage guidance" section against the
    /// single-source-of-truth <see cref="PlatformToolCatalog"/> (#2988):
    /// each category's summary and usage-guidance prose is reproduced
    /// verbatim in the doc (modulo whitespace / line-wrapping), so the
    /// user-facing copy cannot drift from the prose the runtime serves an
    /// agent from <c>sv.tools.list</c>. Editing the catalog without
    /// re-syncing this doc section fails the build.
    /// </summary>
    [Fact]
    public void CategoryGuidance_MatchesCatalog()
    {
        var docByToken = ParseCategoryGuidanceFromDoc();

        var failures = new List<string>();
        foreach (var category in PlatformToolCatalog.Categories)
        {
            if (!docByToken.TryGetValue(category.Token, out var docBlock))
            {
                failures.Add(
                    $"category '{category.Token}': no " +
                    $"<!-- platform-tool-catalog:{category.Token} --> block found in the doc.");
                continue;
            }

            var expected = Normalise(category.Summary + " " + category.UsageGuidance);
            var actual = Normalise(docBlock);
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                failures.Add(
                    $"category '{category.Token}': doc guidance does not match the catalog." +
                    Environment.NewLine + "  expected: " + expected +
                    Environment.NewLine + "  doc:      " + actual);
            }
        }

        failures.ShouldBeEmpty(
            "docs/reference/platform-tools.md \"Category usage guidance\" section is out of sync " +
            "with src/Cvoya.Spring.Core/Skills/PlatformToolCatalog.cs. Edit the catalog, then " +
            "reproduce its Summary + UsageGuidance verbatim in the matching " +
            "<!-- platform-tool-catalog:<token> --> block:" + Environment.NewLine +
            string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Extracts each category's doc prose from the
    /// <c>&lt;!-- platform-tool-catalog:&lt;token&gt; --&gt;</c> …
    /// <c>&lt;!-- /platform-tool-catalog:&lt;token&gt; --&gt;</c> markers,
    /// stripping markdown blockquote prefixes (<c>&gt; </c>) and the
    /// bold-token label line so the remainder is the summary + guidance
    /// text to compare. Whitespace is normalised by the caller.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ParseCategoryGuidanceFromDoc()
    {
        var text = File.ReadAllText(CatalogDocPath);

        var blocks = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in CategoryBlockRegex.Matches(text))
        {
            var token = match.Groups["token"].Value;
            var body = match.Groups["body"].Value;

            // Drop the blockquote markers so the prose compares cleanly.
            var stripped = body.Replace("\n>", "\n").Replace("> ", " ");
            blocks[token] = stripped;
        }

        return blocks;
    }

    /// <summary>
    /// Matches one category block delimited by the open/close HTML-comment
    /// markers and captures the token and inner body. The body includes
    /// the bold-token summary line and the blockquote guidance; both are
    /// folded into the compared text (the bold <c>**`token`** — summary</c>
    /// line carries the summary, which the catalog string also leads with).
    /// </summary>
    private static readonly Regex CategoryBlockRegex = new(
        @"<!-- platform-tool-catalog:(?<token>[a-z][a-z0-9_]*) -->" +
        @"(?<body>.*?)" +
        @"<!-- /platform-tool-catalog:\k<token> -->",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Collapses every run of whitespace to a single space, strips the
    /// markdown emphasis / code markers the doc adds around the token
    /// label, and trims — so a verbatim catalog string and its
    /// naturally-wrapped doc rendering compare equal.
    /// </summary>
    private static string Normalise(string s)
    {
        // The doc renders the summary line as "**`token`** — summary";
        // the catalog string is "summary". Drop the markdown emphasis,
        // the backticked token label, and the leading em-dash so only the
        // prose remains.
        var withoutMarkup = MarkdownLabelRegex.Replace(s, " ");
        return WhitespaceRegex.Replace(withoutMarkup, " ").Trim();
    }

    /// <summary>
    /// Strips the doc-only <c>**`&lt;token&gt;`** —</c> label prefix and
    /// stray emphasis markers so the compared text is prose only.
    /// </summary>
    private static readonly Regex MarkdownLabelRegex = new(
        @"\*\*`[a-z][a-z0-9_]*`\*\*\s*—",
        RegexOptions.Compiled);

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

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
