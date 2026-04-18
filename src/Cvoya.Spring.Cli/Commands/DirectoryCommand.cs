// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;
using System.Globalization;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;

/// <summary>
/// Builds the <c>spring directory</c> command tree. Currently exposes a
/// single <c>search</c> verb backed by the expertise-directory search
/// endpoint (#542). Sibling verbs (listing, ownership lookups) are tracked
/// separately under #528.
/// </summary>
public static class DirectoryCommand
{
    private sealed record SearchRow(
        string Slug,
        string Name,
        string Level,
        string Owner,
        string Typed,
        string Aggregating,
        string Score);

    private static readonly OutputFormatter.Column<SearchRow>[] SearchColumns =
    {
        new("slug", r => r.Slug),
        new("name", r => r.Name),
        new("level", r => r.Level),
        new("owner", r => r.Owner),
        new("typed", r => r.Typed),
        new("aggregating", r => r.Aggregating),
        new("score", r => r.Score),
    };

    /// <summary>
    /// Creates the <c>spring directory</c> command. Wires in the search verb;
    /// additional verbs slot in as separate subcommands.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var cmd = new Command("directory", "Query the tenant-wide expertise directory");
        cmd.Subcommands.Add(CreateSearchCommand(outputOption));
        return cmd;
    }

    private static Command CreateSearchCommand(Option<string> outputOption)
    {
        var queryArg = new Argument<string?>("query")
        {
            Description = "Free-text query. Matches slug, display name, description, and tags.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var domainOption = new Option<string[]>("--domain", "-d")
        {
            Description = "Restrict to the given domain or slug (repeatable).",
            AllowMultipleArgumentsPerToken = false,
        };
        var ownerOption = new Option<string?>("--owner")
        {
            Description = "Restrict to entries contributed by this owner (scheme://path).",
        };
        var typedOption = new Option<bool>("--typed-only")
        {
            Description = "Only return typed-contract (skill-callable) entries.",
        };
        var insideOption = new Option<bool>("--inside")
        {
            Description = "Request the inside-the-unit boundary view (full scope).",
        };
        var limitOption = new Option<int?>("--limit")
        {
            Description = "Maximum results to return (default 50, cap 200).",
        };
        var offsetOption = new Option<int?>("--offset")
        {
            Description = "Pagination offset (default 0).",
        };

        var command = new Command("search",
            "Search the expertise directory by free-text query and/or structured filters");
        command.Arguments.Add(queryArg);
        command.Options.Add(domainOption);
        command.Options.Add(ownerOption);
        command.Options.Add(typedOption);
        command.Options.Add(insideOption);
        command.Options.Add(limitOption);
        command.Options.Add(offsetOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var query = parseResult.GetValue(queryArg);
            var domains = parseResult.GetValue(domainOption) ?? Array.Empty<string>();
            var owner = parseResult.GetValue(ownerOption);
            var typedOnly = parseResult.GetValue(typedOption);
            var inside = parseResult.GetValue(insideOption);
            var limit = parseResult.GetValue(limitOption);
            var offset = parseResult.GetValue(offsetOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            string? ownerScheme = null;
            string? ownerPath = null;
            if (!string.IsNullOrWhiteSpace(owner))
            {
                var parsed = ParseAddress(owner!);
                if (parsed is null)
                {
                    Console.Error.WriteLine($"Invalid --owner address '{owner}'. Expected 'scheme://path'.");
                    return;
                }
                (ownerScheme, ownerPath) = parsed.Value;
            }

            var client = ClientFactory.Create();
            var response = await client.SearchDirectoryAsync(
                text: query,
                ownerScheme: ownerScheme,
                ownerPath: ownerPath,
                domains: domains.Length == 0 ? null : domains,
                typedOnly: typedOnly,
                insideUnit: inside,
                limit: limit,
                offset: offset,
                ct: ct);

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJson(response));
                return;
            }

            var hits = response.Hits ?? new List<DirectorySearchHitResponse>();
            var rows = hits
                .Select(h =>
                {
                    var aggregating = h.AggregatingUnit?.AddressDto;
                    return new SearchRow(
                        Slug: h.Slug ?? string.Empty,
                        Name: h.Domain?.Name ?? string.Empty,
                        Level: h.Domain?.Level ?? string.Empty,
                        Owner: h.Owner is null ? string.Empty : $"{h.Owner.Scheme}://{h.Owner.Path}",
                        Typed: h.TypedContract == true ? "yes" : "no",
                        Aggregating: aggregating is null
                            ? string.Empty
                            : $"{aggregating.Scheme}://{aggregating.Path}",
                        Score: KiotaConversions.ToDouble(h.Score)
                            .ToString("F1", CultureInfo.InvariantCulture));
                })
                .ToList();

            Console.WriteLine(OutputFormatter.FormatTable(rows, SearchColumns));
            Console.WriteLine();
            Console.WriteLine(
                $"Showing {hits.Count} of {KiotaConversions.ToInt(response.TotalCount)}   " +
                $"(limit={KiotaConversions.ToInt(response.Limit)}, offset={KiotaConversions.ToInt(response.Offset)})");
        });

        return command;
    }

    private static (string Scheme, string Path)? ParseAddress(string address)
    {
        var sep = address.IndexOf("://", StringComparison.Ordinal);
        if (sep <= 0 || sep >= address.Length - 3)
        {
            return null;
        }
        return (address[..sep], address[(sep + 3)..]);
    }
}