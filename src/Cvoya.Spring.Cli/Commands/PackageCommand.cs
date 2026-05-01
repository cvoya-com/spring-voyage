// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;

/// <summary>
/// Builds the <c>spring package</c> verb family (#395 / PR-PLAT-PKG-1).
/// Mirrors the portal's Packages page:
/// <list type="bullet">
///   <item><description><c>spring package list</c> — lists installed packages with content counts.</description></item>
///   <item><description><c>spring package show &lt;name&gt;</c> — prints templates / agents / skills / connectors / workflows in the package.</description></item>
/// </list>
/// The companion <c>spring template show</c> verb (see
/// <see cref="TemplateCommand"/>) lives on its own root verb to match
/// #395's acceptance list so a user can <c>spring template show
/// software-engineering/engineering-team</c> to see the exact YAML a
/// unit create flow would instantiate.
/// </summary>
public static class PackageCommand
{
    private static readonly OutputFormatter.Column<PackageSummary>[] ListColumns =
    {
        new("name", p => p.Name),
        new("units", p => p.UnitTemplateCount?.ToString()),
        new("agents", p => p.AgentTemplateCount?.ToString()),
        new("skills", p => p.SkillCount?.ToString()),
        new("connectors", p => p.ConnectorCount?.ToString()),
        new("workflows", p => p.WorkflowCount?.ToString()),
        new("description", p => p.Description),
    };

    private static readonly OutputFormatter.Column<UnitTemplateSummary>[] UnitTemplateColumns =
    {
        new("name", t => t.Name),
        new("description", t => t.Description),
        new("path", t => t.Path),
    };

    private static readonly OutputFormatter.Column<AgentTemplateSummary>[] AgentTemplateColumns =
    {
        new("name", t => t.Name),
        new("role", t => t.Role),
        new("displayName", t => t.DisplayName),
        new("description", t => t.Description),
    };

    private static readonly OutputFormatter.Column<SkillSummary>[] SkillColumns =
    {
        new("name", s => s.Name),
        new("tools", s => s.HasTools == true ? "yes" : "no"),
        new("path", s => s.Path),
    };

    private static readonly OutputFormatter.Column<ConnectorSummary>[] ConnectorColumns =
    {
        new("name", c => c.Name),
        new("path", c => c.Path),
    };

    private static readonly OutputFormatter.Column<WorkflowSummary>[] WorkflowColumns =
    {
        new("name", w => w.Name),
        new("path", w => w.Path),
    };

    /// <summary>
    /// Creates the <c>package</c> command root with the <c>list</c> /
    /// <c>show</c> subcommands.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var packageCommand = new Command("package", "Browse installed packages and their contents");

        packageCommand.Subcommands.Add(CreateListCommand(outputOption));
        packageCommand.Subcommands.Add(CreateShowCommand(outputOption));

        return packageCommand;
    }

    private static Command CreateListCommand(Option<string> outputOption)
    {
        var command = new Command(
            "list",
            "List installed packages with content counts. Mirrors the portal's /packages card grid.");

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var result = await client.ListPackagesAsync(ct);

            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(result, ListColumns));
        });

        return command;
    }

    private static Command CreateShowCommand(Option<string> outputOption)
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "Package name (e.g. 'software-engineering'). Run 'spring package list' for available names.",
        };

        var command = new Command(
            "show",
            "Show the contents of a package — unit templates, agent templates, skills, connectors, and workflows.");
        command.Arguments.Add(nameArgument);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var name = parseResult.GetValue(nameArgument)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var detail = await client.GetPackageAsync(name, ct);
            if (detail is null)
            {
                await Console.Error.WriteLineAsync(
                    $"Package '{name}' not found. Run 'spring package list' to see installed packages.");
                Environment.Exit(1);
                return;
            }

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJson(detail));
                return;
            }

            Console.WriteLine($"Package: {detail.Name}");
            if (!string.IsNullOrWhiteSpace(detail.Description))
            {
                Console.WriteLine($"  {detail.Description}");
            }

            WriteSection("Unit templates", detail.UnitTemplates, UnitTemplateColumns);
            WriteSection("Agent templates", detail.AgentTemplates, AgentTemplateColumns);
            WriteSection("Skills", detail.Skills, SkillColumns);
            WriteSection("Connectors", detail.Connectors, ConnectorColumns);
            WriteSection("Workflows", detail.Workflows, WorkflowColumns);
        });

        return command;
    }

    private static void WriteSection<T>(
        string title,
        IReadOnlyList<T>? rows,
        IReadOnlyList<OutputFormatter.Column<T>> columns)
    {
        Console.WriteLine();
        // Mirror the package show section header used by the portal's
        // tabs so table output and the portal stay visually coherent.
        Console.WriteLine($"{title} ({rows?.Count ?? 0}):");
        if (rows is null || rows.Count == 0)
        {
            Console.WriteLine("  (none)");
            return;
        }
        Console.WriteLine(OutputFormatter.FormatTable(rows, columns));
    }
}