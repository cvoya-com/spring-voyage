// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;
using System.Text.Json;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;
using Cvoya.Spring.Cli.Utilities;

/// <summary>
/// Builds the expertise command surface used by both <c>spring agent</c>
/// and <c>spring unit</c> (#412). The two subcommand trees share the same
/// get / set / aggregate shape, differing only in the entity they target.
/// </summary>
public static class ExpertiseCommand
{
    private sealed record ExpertiseRow(string Name, string? Level, string Description);

    private static readonly OutputFormatter.Column<ExpertiseRow>[] ExpertiseColumns =
    {
        new("name", r => r.Name),
        new("level", r => r.Level ?? string.Empty),
        new("description", r => r.Description),
    };

    private sealed record AggregatedRow(string Domain, string? Level, string Origin, int Depth);

    private static readonly OutputFormatter.Column<AggregatedRow>[] AggregatedColumns =
    {
        new("domain", r => r.Domain),
        new("level", r => r.Level ?? string.Empty),
        new("origin", r => r.Origin),
        new("depth", r => r.Depth.ToString(System.Globalization.CultureInfo.InvariantCulture)),
    };

    /// <summary>
    /// Builds the "expertise" subcommand added under <c>spring agent</c>:
    /// <c>get</c> and <c>set</c> subcommands targeting the supplied agent id.
    /// </summary>
    public static Command CreateAgentSubcommand(Option<string> outputOption)
    {
        var cmd = new Command("expertise", "View or replace an agent's configured expertise domains");
        cmd.Subcommands.Add(CreateGetCommand(outputOption, isAgent: true));
        cmd.Subcommands.Add(CreateSetCommand(outputOption, isAgent: true));
        return cmd;
    }

    /// <summary>
    /// Builds the "expertise" subcommand added under <c>spring unit</c>:
    /// <c>get</c> (the unit's own expertise), <c>set</c> (replace it), and
    /// <c>aggregated</c> (the recursive composition to the leaves).
    /// </summary>
    public static Command CreateUnitSubcommand(Option<string> outputOption)
    {
        var cmd = new Command("expertise", "View or replace a unit's own expertise; view the aggregated directory");
        cmd.Subcommands.Add(CreateGetCommand(outputOption, isAgent: false));
        cmd.Subcommands.Add(CreateSetCommand(outputOption, isAgent: false));
        cmd.Subcommands.Add(CreateAggregatedCommand(outputOption));
        return cmd;
    }

    private static Command CreateGetCommand(Option<string> outputOption, bool isAgent)
    {
        var idArg = new Argument<string>("id") { Description = isAgent ? "The agent id" : "The unit id" };
        var command = new Command("get", isAgent
            ? "Show the agent's configured expertise domains"
            : "Show the unit's own (non-aggregated) expertise domains");
        command.Arguments.Add(idArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var domains = isAgent
                ? await client.GetAgentExpertiseAsync(id, ct)
                : await client.GetUnitOwnExpertiseAsync(id, ct);

            var rows = domains
                .Select(d => new ExpertiseRow(d.Name ?? string.Empty, d.Level, d.Description ?? string.Empty))
                .ToList();

            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJsonPlain(rows)
                : OutputFormatter.FormatTable(rows, ExpertiseColumns));
        });

        return command;
    }

    private static Command CreateSetCommand(Option<string> outputOption, bool isAgent)
    {
        var idArg = new Argument<string>("id") { Description = isAgent ? "The agent id" : "The unit id" };
        var domainOption = new Option<string[]>("--domain")
        {
            Description = "Repeat for each domain. Format: name[:level[:description]] (e.g. 'python/fastapi:expert:Server-side async APIs').",
            AllowMultipleArgumentsPerToken = false,
        };
        var fileOption = new Option<string?>("--file", "-f")
        {
            Description = "Read the full domain list from a JSON file (array of { name, level?, description? }). Overrides --domain if both are supplied.",
        };
        var clearOption = new Option<bool>("--clear")
        {
            Description = "Clear all configured expertise (equivalent to PUTting an empty list).",
        };

        var command = new Command("set", isAgent
            ? "Replace the agent's expertise domains in full"
            : "Replace the unit's own expertise domains in full");
        command.Arguments.Add(idArg);
        command.Options.Add(domainOption);
        command.Options.Add(fileOption);
        command.Options.Add(clearOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            IReadOnlyList<ExpertiseDomainDto> body;
            if (parseResult.GetValue(clearOption))
            {
                body = Array.Empty<ExpertiseDomainDto>();
            }
            else if (parseResult.GetValue(fileOption) is { Length: > 0 } path)
            {
                var json = await File.ReadAllTextAsync(path, ct);
                body = JsonSerializer.Deserialize<List<ExpertiseDomainDto>>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new List<ExpertiseDomainDto>();
            }
            else
            {
                var domains = parseResult.GetValue(domainOption) ?? Array.Empty<string>();
                body = domains.Select(ParseDomainSpec).ToList();
            }

            var updated = isAgent
                ? await client.SetAgentExpertiseAsync(id, body, ct)
                : await client.SetUnitOwnExpertiseAsync(id, body, ct);

            var rows = updated
                .Select(d => new ExpertiseRow(d.Name ?? string.Empty, d.Level, d.Description ?? string.Empty))
                .ToList();

            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJsonPlain(rows)
                : OutputFormatter.FormatTable(rows, ExpertiseColumns));
        });

        return command;
    }

    private static Command CreateAggregatedCommand(Option<string> outputOption)
    {
        var idArg = new Argument<string>("id") { Description = "The unit id" };
        var command = new Command("aggregated",
            "Show the unit's effective expertise: its own domains plus every descendant's, with origin + path");
        command.Arguments.Add(idArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var aggregated = await client.GetUnitAggregatedExpertiseAsync(id, ct);

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJson(aggregated));
                return;
            }

            var rows = (aggregated.Entries ?? new List<AggregatedExpertiseEntryDto>())
                .Select(e =>
                {
                    var domain = e.Domain;
                    var origin = e.Origin is null
                        ? string.Empty
                        : $"{e.Origin.Scheme}://{e.Origin.Path}";
                    var depth = e.Path?.Count is int count and > 0 ? count - 1 : 0;
                    return new AggregatedRow(
                        domain?.Name ?? string.Empty,
                        domain?.Level,
                        origin,
                        depth);
                })
                .ToList();

            Console.WriteLine(OutputFormatter.FormatTable(rows, AggregatedColumns));
            Console.WriteLine();
            Console.WriteLine($"Depth: {UntypedNodeFormatter.FormatScalar(aggregated.Depth)}   Computed: {aggregated.ComputedAt:O}");
        });

        return command;
    }

    /// <summary>
    /// Parses a <c>name[:level[:description]]</c> specifier. Level must be
    /// one of <c>beginner | intermediate | advanced | expert</c> when
    /// supplied; anything else is rejected so the server never sees a
    /// mis-spelled level.
    /// </summary>
    public static ExpertiseDomainDto ParseDomainSpec(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            throw new ArgumentException("Domain spec cannot be empty.", nameof(spec));
        }

        var parts = spec.Split(':', 3);
        var name = parts[0].Trim();
        string? level = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])
            ? parts[1].Trim().ToLowerInvariant()
            : null;
        string description = parts.Length > 2 ? parts[2].Trim() : string.Empty;

        if (level is not null
            && level != "beginner"
            && level != "intermediate"
            && level != "advanced"
            && level != "expert")
        {
            throw new ArgumentException(
                $"Invalid level '{parts[1]}'. Must be one of: beginner, intermediate, advanced, expert.",
                nameof(spec));
        }

        return new ExpertiseDomainDto
        {
            Name = name,
            Level = level,
            Description = description,
        };
    }
}