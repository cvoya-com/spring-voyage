// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;

/// <summary>
/// #2342: <c>spring agent memory &lt;verb&gt;</c> and
/// <c>spring unit memory &lt;verb&gt;</c> verb trees. Read-only in v0.1
/// — operator write parity (add / update / delete) lives in v0.2 under
/// #2357. Three verbs each: <c>list</c>, <c>get</c>, <c>search</c>. All
/// calls flow through the generated <c>SpringApiClient</c>; the CLI
/// never builds an HTTP request by hand.
/// </summary>
public static class MemoryCommand
{
    /// <summary>Allowed values for the <c>--kind</c> filter flag.</summary>
    public static readonly string[] KindKeys = ["long_term", "short_term"];

    private static readonly OutputFormatter.Column<MemoryRow>[] MemoryColumns =
    {
        new("id", r => r.Id),
        new("kind", r => r.Kind),
        new("content", r => r.Content),
        new("source", r => r.Source),
        new("threadId", r => r.ThreadId),
        new("createdAt", r => r.CreatedAt),
        new("updatedAt", r => r.UpdatedAt),
    };

    private sealed record MemoryRow(
        string Id,
        string Kind,
        string Content,
        string? Source,
        string? ThreadId,
        string CreatedAt,
        string UpdatedAt);

    /// <summary>
    /// <c>spring agent memory</c> verb tree. Subject is the agent id /
    /// display name. Mirrors <see cref="CreateUnitSubcommand"/> for
    /// shape parity.
    /// </summary>
    public static Command CreateAgentSubcommand(Option<string> outputOption)
    {
        var command = new Command(
            "memory",
            "Read the agent's memory entries (#2342). Read-only in v0.1; " +
            "add / update / delete affordances land in v0.2 (#2357).");

        command.Subcommands.Add(CreateAgentListCommand(outputOption));
        command.Subcommands.Add(CreateAgentGetCommand(outputOption));
        command.Subcommands.Add(CreateAgentSearchCommand(outputOption));
        return command;
    }

    /// <summary>
    /// <c>spring unit memory</c> verb tree. Subject is the unit id /
    /// display name.
    /// </summary>
    public static Command CreateUnitSubcommand(Option<string> outputOption)
    {
        var command = new Command(
            "memory",
            "Read the unit's memory entries (#2342). Read-only in v0.1; " +
            "add / update / delete affordances land in v0.2 (#2357).");

        command.Subcommands.Add(CreateUnitListCommand(outputOption));
        command.Subcommands.Add(CreateUnitGetCommand(outputOption));
        command.Subcommands.Add(CreateUnitSearchCommand(outputOption));
        return command;
    }

    // ----- Agent verbs ---------------------------------------------------

    private static Command CreateAgentListCommand(Option<string> outputOption)
    {
        var agentArg = new Argument<string>("agent") { Description = "Agent id or display_name." };
        var kindOption = KindOption();
        var limitOption = LimitOption();
        var offsetOption = OffsetOption();

        var command = new Command(
            "list",
            "List the agent's memory entries, most-recent first. Filter by --kind; " +
            "page with --limit and --offset.");
        command.Arguments.Add(agentArg);
        command.Options.Add(kindOption);
        command.Options.Add(limitOption);
        command.Options.Add(offsetOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idOrName = parseResult.GetValue(agentArg)!;
            var kind = parseResult.GetValue(kindOption);
            var limit = parseResult.GetValue(limitOption);
            var offset = parseResult.GetValue(offsetOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            var client = ClientFactory.Create();
            var id = await ResolveAgentAsync(client, idOrName, ct);
            if (id is null) return;

            var view = await client.GetAgentMemoriesAsync(id, kind, limit, offset, ct);
            PrintMemoriesList(view, output);
        });

        return command;
    }

    private static Command CreateAgentGetCommand(Option<string> outputOption)
    {
        var agentArg = new Argument<string>("agent") { Description = "Agent id or display_name." };
        var idOption = new Option<string>("--id")
        {
            Description = "Memory entry id (32-char no-dash hex or dashed Guid).",
            Required = true,
        };

        var command = new Command(
            "get",
            "Fetch a single memory entry by id. Exits non-zero with a 404 message when the id is not owned by the agent.");
        command.Arguments.Add(agentArg);
        command.Options.Add(idOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idOrName = parseResult.GetValue(agentArg)!;
            var memoryIdRaw = parseResult.GetValue(idOption)!;
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (!Guid.TryParse(memoryIdRaw, out var memoryId))
            {
                await Console.Error.WriteLineAsync($"--id '{memoryIdRaw}' is not a parseable Guid.");
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();
            var id = await ResolveAgentAsync(client, idOrName, ct);
            if (id is null) return;

            try
            {
                var entry = await client.GetAgentMemoryAsync(id, memoryId, ct);
                if (entry is null)
                {
                    await Console.Error.WriteLineAsync(
                        $"Memory '{memoryIdRaw}' not found for agent '{idOrName}'.");
                    Environment.Exit(1);
                    return;
                }
                PrintSingleMemory(entry, output);
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
            {
                await Console.Error.WriteLineAsync(
                    $"Memory '{memoryIdRaw}' not found for agent '{idOrName}'.");
                Environment.Exit(1);
            }
        });

        return command;
    }

    private static Command CreateAgentSearchCommand(Option<string> outputOption)
    {
        var agentArg = new Argument<string>("agent") { Description = "Agent id or display_name." };
        var queryOption = new Option<string>("--query")
        {
            Description = "Free-text search query. Postgres FTS — results are ordered by relevance.",
            Required = true,
        };
        var kindOption = KindOption();
        var limitOption = LimitOption();

        var command = new Command(
            "search",
            "Free-text search the agent's memory entries. Backed by Postgres full-text search; " +
            "results are ordered by relevance (highest first).");
        command.Arguments.Add(agentArg);
        command.Options.Add(queryOption);
        command.Options.Add(kindOption);
        command.Options.Add(limitOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idOrName = parseResult.GetValue(agentArg)!;
            var query = parseResult.GetValue(queryOption)!;
            var kind = parseResult.GetValue(kindOption);
            var limit = parseResult.GetValue(limitOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            var client = ClientFactory.Create();
            var id = await ResolveAgentAsync(client, idOrName, ct);
            if (id is null) return;

            var view = await client.SearchAgentMemoriesAsync(id, query, kind, limit, ct);
            PrintMemoriesList(view, output);
        });

        return command;
    }

    // ----- Unit verbs ----------------------------------------------------

    private static Command CreateUnitListCommand(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "Unit id or display_name." };
        var kindOption = KindOption();
        var limitOption = LimitOption();
        var offsetOption = OffsetOption();

        var command = new Command(
            "list",
            "List the unit's memory entries, most-recent first. Filter by --kind; " +
            "page with --limit and --offset.");
        command.Arguments.Add(unitArg);
        command.Options.Add(kindOption);
        command.Options.Add(limitOption);
        command.Options.Add(offsetOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idOrName = parseResult.GetValue(unitArg)!;
            var kind = parseResult.GetValue(kindOption);
            var limit = parseResult.GetValue(limitOption);
            var offset = parseResult.GetValue(offsetOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            var client = ClientFactory.Create();
            var id = await ResolveUnitAsync(client, idOrName, ct);
            if (id is null) return;

            var view = await client.GetUnitMemoriesAsync(id, kind, limit, offset, ct);
            PrintMemoriesList(view, output);
        });

        return command;
    }

    private static Command CreateUnitGetCommand(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "Unit id or display_name." };
        var idOption = new Option<string>("--id")
        {
            Description = "Memory entry id (32-char no-dash hex or dashed Guid).",
            Required = true,
        };

        var command = new Command(
            "get",
            "Fetch a single memory entry by id. Exits non-zero with a 404 message when the id is not owned by the unit.");
        command.Arguments.Add(unitArg);
        command.Options.Add(idOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idOrName = parseResult.GetValue(unitArg)!;
            var memoryIdRaw = parseResult.GetValue(idOption)!;
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (!Guid.TryParse(memoryIdRaw, out var memoryId))
            {
                await Console.Error.WriteLineAsync($"--id '{memoryIdRaw}' is not a parseable Guid.");
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();
            var id = await ResolveUnitAsync(client, idOrName, ct);
            if (id is null) return;

            try
            {
                var entry = await client.GetUnitMemoryAsync(id, memoryId, ct);
                if (entry is null)
                {
                    await Console.Error.WriteLineAsync(
                        $"Memory '{memoryIdRaw}' not found for unit '{idOrName}'.");
                    Environment.Exit(1);
                    return;
                }
                PrintSingleMemory(entry, output);
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
            {
                await Console.Error.WriteLineAsync(
                    $"Memory '{memoryIdRaw}' not found for unit '{idOrName}'.");
                Environment.Exit(1);
            }
        });

        return command;
    }

    private static Command CreateUnitSearchCommand(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "Unit id or display_name." };
        var queryOption = new Option<string>("--query")
        {
            Description = "Free-text search query. Postgres FTS — results are ordered by relevance.",
            Required = true,
        };
        var kindOption = KindOption();
        var limitOption = LimitOption();

        var command = new Command(
            "search",
            "Free-text search the unit's memory entries. Backed by Postgres full-text search; " +
            "results are ordered by relevance (highest first).");
        command.Arguments.Add(unitArg);
        command.Options.Add(queryOption);
        command.Options.Add(kindOption);
        command.Options.Add(limitOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idOrName = parseResult.GetValue(unitArg)!;
            var query = parseResult.GetValue(queryOption)!;
            var kind = parseResult.GetValue(kindOption);
            var limit = parseResult.GetValue(limitOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            var client = ClientFactory.Create();
            var id = await ResolveUnitAsync(client, idOrName, ct);
            if (id is null) return;

            var view = await client.SearchUnitMemoriesAsync(id, query, kind, limit, ct);
            PrintMemoriesList(view, output);
        });

        return command;
    }

    // ----- Shared option factories --------------------------------------

    private static Option<string?> KindOption()
    {
        var option = new Option<string?>("--kind")
        {
            Description = "Filter to a single kind. Allowed: " + string.Join(", ", KindKeys) + ".",
        };
        option.AcceptOnlyFromAmong(KindKeys);
        return option;
    }

    private static Option<int?> LimitOption() => new("--limit")
    {
        Description = "Maximum number of entries to return (default 50, max 500).",
    };

    private static Option<int?> OffsetOption() => new("--offset")
    {
        Description = "Offset into the result set, used with --limit for paging.",
    };

    // ----- Shared subject resolution / printing -------------------------

    private static async Task<string?> ResolveAgentAsync(
        SpringApiClient client, string idOrName, CancellationToken ct)
    {
        var resolver = new CliResolver(client);
        try
        {
            return await resolver.ResolveAgentIdAsync(idOrName, unitContext: null, ct);
        }
        catch (CliResolutionException ex)
        {
            CliResolutionPrinter.Write(Console.Error, ex);
            Environment.Exit(1);
            return null;
        }
    }

    private static async Task<string?> ResolveUnitAsync(
        SpringApiClient client, string idOrName, CancellationToken ct)
    {
        var resolver = new CliResolver(client);
        try
        {
            return await resolver.ResolveUnitIdAsync(idOrName, parentContext: null, ct);
        }
        catch (CliResolutionException ex)
        {
            CliResolutionPrinter.Write(Console.Error, ex);
            Environment.Exit(1);
            return null;
        }
    }

    private static void PrintMemoriesList(MemoriesResponse view, string output)
    {
        if (output == "json")
        {
            Console.WriteLine(OutputFormatter.FormatJson(view));
            return;
        }

        // Combine the two axes into a single ordered table — most operators
        // want a single view of "what does this subject remember?" rather
        // than two parallel sections. Sort by createdAt desc to match the
        // server's ListAsync ordering.
        var rows = ((view.ShortTerm ?? new List<MemoryEntry>())
                .Concat(view.LongTerm ?? new List<MemoryEntry>()))
            .OrderByDescending(e => e.CreatedAt ?? DateTimeOffset.MinValue)
            .Select(ToRow)
            .ToList();

        if (rows.Count == 0)
        {
            Console.WriteLine("No memory entries.");
            return;
        }

        Console.WriteLine(OutputFormatter.FormatTable(rows, MemoryColumns));
    }

    private static void PrintSingleMemory(MemoryEntry entry, string output)
    {
        if (output == "json")
        {
            Console.WriteLine(OutputFormatter.FormatJson(entry));
            return;
        }
        Console.WriteLine(OutputFormatter.FormatTable(ToRow(entry), MemoryColumns));
    }

    private static MemoryRow ToRow(MemoryEntry entry) =>
        new(
            Id: entry.Id ?? string.Empty,
            Kind: entry.Kind ?? string.Empty,
            Content: entry.Content ?? string.Empty,
            Source: entry.Source,
            ThreadId: entry.ThreadId,
            CreatedAt: entry.CreatedAt is { } c
                ? c.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty,
            UpdatedAt: entry.UpdatedAt is { } u
                ? u.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty);
}
