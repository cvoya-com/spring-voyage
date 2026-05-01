// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;
using System.Globalization;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;

/// <summary>
/// Builds the <c>spring directory</c> command tree. Exposes the
/// expertise-directory browse verbs — <c>search</c>, <c>list</c>, and
/// <c>show</c> — that mirror the portal's <c>/directory</c> surface. All
/// three ride the same <c>POST /api/v1/directory/search</c> endpoint the
/// portal uses (#528, #542).
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

    // Listing reuses the search endpoint but strips the score column —
    // there is no free-text query to rank against, so every entry reports
    // the same aggregated-coverage base score and surfacing it would be
    // visual noise.
    private sealed record ListRow(
        string Slug,
        string Name,
        string Level,
        string Owner,
        string Typed,
        string Aggregating);

    private static readonly OutputFormatter.Column<ListRow>[] ListColumns =
    {
        new("slug", r => r.Slug),
        new("name", r => r.Name),
        new("level", r => r.Level),
        new("owner", r => r.Owner),
        new("typed", r => r.Typed),
        new("aggregating", r => r.Aggregating),
    };

    /// <summary>
    /// Creates the <c>spring directory</c> command with its three verbs.
    /// Additional verbs slot in as separate subcommands.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var cmd = new Command("directory", "Query the tenant-wide expertise directory");
        cmd.Subcommands.Add(CreateSearchCommand(outputOption));
        cmd.Subcommands.Add(CreateListCommand(outputOption));
        cmd.Subcommands.Add(CreateShowCommand(outputOption));
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
                        Score: (h.Score ?? 0d).ToString("F1", CultureInfo.InvariantCulture));
                })
                .ToList();

            Console.WriteLine(OutputFormatter.FormatTable(rows, SearchColumns));
            Console.WriteLine();
            Console.WriteLine(
                $"Showing {hits.Count} of {response.TotalCount ?? 0}   " +
                $"(limit={response.Limit ?? 0}, offset={response.Offset ?? 0})");
        });

        return command;
    }

    // The portal's /directory page rides the same search endpoint we call
    // from `search`; passing an empty text query is the server-side
    // convention for "enumerate everything" (subject to boundary + filters).
    // A dedicated bulk-list endpoint is worth revisiting if perf forces it,
    // but the search layer already clamps page size and the portal has been
    // using this approach since #530 without issue.
    private static Command CreateListCommand(Option<string> outputOption)
    {
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

        var command = new Command("list",
            "List expertise directory entries, with optional filters");
        command.Options.Add(domainOption);
        command.Options.Add(ownerOption);
        command.Options.Add(typedOption);
        command.Options.Add(insideOption);
        command.Options.Add(limitOption);
        command.Options.Add(offsetOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
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
                text: null,
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
                    return new ListRow(
                        Slug: h.Slug ?? string.Empty,
                        Name: h.Domain?.Name ?? string.Empty,
                        Level: h.Domain?.Level ?? string.Empty,
                        Owner: h.Owner is null ? string.Empty : $"{h.Owner.Scheme}://{h.Owner.Path}",
                        Typed: h.TypedContract == true ? "yes" : "no",
                        Aggregating: aggregating is null
                            ? string.Empty
                            : $"{aggregating.Scheme}://{aggregating.Path}");
                })
                .ToList();

            Console.WriteLine(OutputFormatter.FormatTable(rows, ListColumns));
            Console.WriteLine();
            Console.WriteLine(
                $"Showing {hits.Count} of {response.TotalCount ?? 0}   " +
                $"(limit={response.Limit ?? 0}, offset={response.Offset ?? 0})");
        });

        return command;
    }

    // The search endpoint's ranking (exact slug > tag/domain > text) means
    // passing the slug verbatim as the free-text query reliably pushes the
    // target entry to the top. We then match on slug exactly to avoid
    // surfacing a near-match as if it were the requested entry. The hit
    // payload carries the full owner chain + projection paths (#553) so
    // `show` renders both a breadcrumb ancestor trail and the set of
    // `projection/{slug}` surfaces the entry is reachable through.
    private static Command CreateShowCommand(Option<string> outputOption)
    {
        var slugArg = new Argument<string>("slug")
        {
            Description = "Directory-addressable slug (e.g. 'python/fastapi').",
        };
        var insideOption = new Option<bool>("--inside")
        {
            Description = "Request the inside-the-unit boundary view (full scope).",
        };

        var command = new Command("show", "Show a single expertise directory entry by slug");
        command.Arguments.Add(slugArg);
        command.Options.Add(insideOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var slug = parseResult.GetValue(slugArg) ?? string.Empty;
            var inside = parseResult.GetValue(insideOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (string.IsNullOrWhiteSpace(slug))
            {
                Console.Error.WriteLine("A slug argument is required.");
                return;
            }

            var client = ClientFactory.Create();
            // Pull a small page — the slug-exact match lands at rank 1 of
            // the server's lexical ranker, so five rows is plenty of
            // headroom. We then post-filter by exact slug equality so a
            // near-match (e.g. 'python' searched while 'python/fastapi'
            // exists) doesn't masquerade as the requested entry.
            var response = await client.SearchDirectoryAsync(
                text: slug,
                typedOnly: false,
                insideUnit: inside,
                limit: 5,
                offset: 0,
                ct: ct);

            var hits = response.Hits ?? new List<DirectorySearchHitResponse>();
            var hit = hits.FirstOrDefault(h =>
                string.Equals(h.Slug, slug, StringComparison.OrdinalIgnoreCase));

            if (hit is null)
            {
                if (output == "json")
                {
                    Console.WriteLine("null");
                }
                else
                {
                    Console.Error.WriteLine(
                        $"No directory entry with slug '{slug}' is visible in the current boundary.");
                }
                Environment.ExitCode = 1;
                return;
            }

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJson(hit));
                return;
            }

            RenderShow(hit);
        });

        return command;
    }

    /// <summary>
    /// Renders a single directory hit as a two-column key/value table
    /// plus a "Projected via" block for the projection-path list (#553).
    /// Public so tests can pin the layout without routing through the
    /// HTTP client.
    /// </summary>
    public static void RenderShow(DirectorySearchHitResponse hit)
    {
        // Detail view is two-column key/value — the same shape other
        // "show" commands use (e.g. unit/agent `get`) so operators see a
        // consistent layout across the CLI.
        var owner = hit.Owner is null
            ? string.Empty
            : $"{hit.Owner.Scheme}://{hit.Owner.Path}";
        var aggregating = hit.AggregatingUnit?.AddressDto;
        var aggregatingText = aggregating is null
            ? "(direct)"
            : $"{aggregating.Scheme}://{aggregating.Path}";

        // #553: AncestorChain is bottom-up (closest ancestor first) so we
        // render it as a breadcrumb-ish " -> " trail reading from the
        // direct owner's closest projecting ancestor up to the highest
        // projecting ancestor. Empty list = direct hit; render "(direct)"
        // so the operator sees the same affordance as AggregatingUnit.
        var chain = hit.AncestorChain ?? new List<AddressDto>();
        var chainText = chain.Count == 0
            ? "(direct)"
            : string.Join(
                " -> ",
                chain.Select(a => $"{a.Scheme}://{a.Path}"));

        var fields = new List<(string Key, string Value)>
        {
            ("Slug", hit.Slug ?? string.Empty),
            ("Name", hit.Domain?.Name ?? string.Empty),
            ("Level", hit.Domain?.Level ?? string.Empty),
            ("Description", hit.Domain?.Description ?? string.Empty),
            ("Owner", owner),
            ("Owner display", hit.OwnerDisplayName ?? string.Empty),
            ("Aggregating unit", aggregatingText),
            ("Ancestor chain", chainText),
            ("Typed contract", hit.TypedContract == true ? "yes" : "no"),
            ("Match reason", hit.MatchReason ?? string.Empty),
            ("Score",
                (hit.Score ?? 0d).ToString("F1", CultureInfo.InvariantCulture)),
        };

        var keyWidth = fields.Max(f => f.Key.Length);
        foreach (var (key, value) in fields)
        {
            Console.WriteLine($"{key.PadRight(keyWidth)}  {value}");
        }

        // Projection paths get their own block below the key/value table.
        // One path per line so long `projection/{slug}` strings don't
        // smash column alignment.
        var paths = hit.ProjectionPaths ?? new List<string>();
        if (paths.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Projected via:");
            foreach (var path in paths)
            {
                Console.WriteLine($"  {path}");
            }
        }
    }

    /// <summary>
    /// Parses an <c>scheme://path</c> address. Returns <c>null</c> when the
    /// input is malformed (either missing the <c>://</c> separator or with
    /// an empty scheme or path). Public so unit tests can pin the parser
    /// without routing through the whole command pipeline.
    /// </summary>
    public static (string Scheme, string Path)? ParseAddress(string address)
    {
        var sep = address.IndexOf("://", StringComparison.Ordinal);
        if (sep <= 0 || sep >= address.Length - 3)
        {
            return null;
        }
        return (address[..sep], address[(sep + 3)..]);
    }
}