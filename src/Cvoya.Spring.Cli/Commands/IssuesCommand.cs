// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;

/// <summary>
/// #2160: <c>spring unit issues &lt;name&gt;</c> and
/// <c>spring agent issues &lt;name&gt;</c> verbs. Print currently-open
/// operational issues for a subject plus, for units, the
/// transitively-aggregated descendant rollup.
/// </summary>
public static class IssuesCommand
{
    public static Command CreateUnitSubcommand(Option<string> outputOption)
    {
        var nameArg = new Argument<string>("name")
        {
            Description = "Unit name (display name) or canonical Guid id.",
        };
        var noDescendantsOption = new Option<bool>("--no-descendants")
        {
            Description =
                "Suppress the descendant rollup. Returns only this unit's own open issues.",
        };

        var command = new Command(
            "issues",
            "Show open operational Errors / Warnings against this unit, plus the rolled-up counts for member units and agents.");
        command.Arguments.Add(nameArg);
        command.Options.Add(noDescendantsOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var noDescendants = parseResult.GetValue(noDescendantsOption);
            var client = ClientFactory.Create();
            var view = await client.GetUnitIssuesAsync(name, includeDescendants: !noDescendants, ct);
            PrintIssues(view, output, includeRollup: !noDescendants);
        });

        return command;
    }

    public static Command CreateAgentSubcommand(Option<string> outputOption)
    {
        var nameArg = new Argument<string>("name")
        {
            Description = "Agent name (display name) or canonical Guid id.",
        };

        var command = new Command(
            "issues",
            "Show open operational Errors / Warnings against this agent.");
        command.Arguments.Add(nameArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            var view = await client.GetAgentIssuesAsync(name, ct);
            // Agents have no descendants — suppress the rollup table
            // unconditionally so the JSON / table outputs stay tight.
            PrintIssues(view, output, includeRollup: false);
        });

        return command;
    }

    private static void PrintIssues(
        IssuesViewResponse view, string output, bool includeRollup)
    {
        if (output == "json")
        {
            Console.WriteLine(OutputFormatter.FormatJson(view));
            return;
        }

        var ownRows = (view.Own ?? new List<IssueResponse>())
            .Select(IssueRow.From)
            .ToList();
        if (ownRows.Count == 0)
        {
            Console.WriteLine("No open issues.");
        }
        else
        {
            Console.WriteLine(OutputFormatter.FormatTable(ownRows, OwnColumns));
        }

        if (!includeRollup) return;
        var rollup = view.Descendants;
        if (rollup is null) return;
        var byChild = rollup.ByChild ?? new List<IssueChildSummaryResponse>();
        if (byChild.Count == 0) return;

        Console.WriteLine();
        Console.WriteLine($"Members with open issues: {rollup.ErrorCount} errors, {rollup.WarningCount} warnings");
        Console.WriteLine(OutputFormatter.FormatTable(
            byChild.Select(ChildRow.From).ToList(),
            ChildColumns));
    }

    private static readonly OutputFormatter.Column<IssueRow>[] OwnColumns = new[]
    {
        new OutputFormatter.Column<IssueRow>("SEVERITY", r => r.Severity),
        new OutputFormatter.Column<IssueRow>("SOURCE", r => r.Source),
        new OutputFormatter.Column<IssueRow>("CODE", r => r.Code),
        new OutputFormatter.Column<IssueRow>("TITLE", r => r.Title),
        new OutputFormatter.Column<IssueRow>("UPDATED", r => r.UpdatedAt),
    };

    private static readonly OutputFormatter.Column<ChildRow>[] ChildColumns = new[]
    {
        new OutputFormatter.Column<ChildRow>("KIND", r => r.Kind),
        new OutputFormatter.Column<ChildRow>("NAME", r => r.Name),
        new OutputFormatter.Column<ChildRow>("ERRORS", r => r.ErrorCount.ToString()),
        new OutputFormatter.Column<ChildRow>("WARNINGS", r => r.WarningCount.ToString()),
    };

    private sealed record IssueRow(string Severity, string Source, string Code, string Title, string UpdatedAt)
    {
        public static IssueRow From(IssueResponse r) =>
            new(
                Severity: (r.Severity ?? "?").ToUpperInvariant(),
                Source: r.Source ?? string.Empty,
                Code: r.Code ?? string.Empty,
                Title: r.Title ?? string.Empty,
                UpdatedAt: r.UpdatedAt?.ToString("u") ?? string.Empty);
    }

    private sealed record ChildRow(string Kind, string Name, int ErrorCount, int WarningCount)
    {
        public static ChildRow From(IssueChildSummaryResponse r) =>
            new(
                Kind: r.SubjectKind ?? "?",
                Name: r.Name ?? string.Empty,
                ErrorCount: r.ErrorCount ?? 0,
                WarningCount: r.WarningCount ?? 0);
    }
}
