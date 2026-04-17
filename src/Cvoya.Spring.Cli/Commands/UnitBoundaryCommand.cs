// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;
using System.IO;
using System.Text;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// Builds the <c>spring unit boundary get|set|clear</c> subtree (#413).
/// Targets the unified server surface
/// <c>GET / PUT / DELETE /api/v1/units/{id}/boundary</c>. The CLI never mints
/// a per-dimension endpoint — <c>set</c> always PUTs the full boundary body
/// (the current boundary merged with any per-flag or YAML overlay), and
/// <c>clear</c> wipes every rule.
/// </summary>
public static class UnitBoundaryCommand
{
    /// <summary>
    /// Entry point — returns the <c>boundary</c> command tree for attachment
    /// under <c>spring unit</c>.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var cmd = new Command("boundary",
            "Manage a unit's boundary (opacity, projection, synthesis) — see #413.");
        cmd.Subcommands.Add(CreateGet(outputOption));
        cmd.Subcommands.Add(CreateSet(outputOption));
        cmd.Subcommands.Add(CreateClear(outputOption));
        return cmd;
    }

    // ---- get ---------------------------------------------------------------

    private static Command CreateGet(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier" };
        var command = new Command("get", "Print the boundary currently persisted on this unit.");
        command.Arguments.Add(unitArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var boundary = await client.GetUnitBoundaryAsync(unitId, ct);

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                {
                    unit = unitId,
                    opacities = boundary.Opacities,
                    projections = boundary.Projections,
                    syntheses = boundary.Syntheses,
                }));
                return;
            }

            Console.Write(FormatBoundaryForHumans(unitId, boundary));
        });

        return command;
    }

    // ---- set ---------------------------------------------------------------

    private static Command CreateSet(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier" };
        var fileOption = new Option<string?>("--file", "-f")
        {
            Description =
                "YAML fragment describing the full boundary (opacities / projections / syntheses). " +
                "Replaces the server-side boundary in full.",
        };
        var opaqueOption = new Option<string[]?>("--opaque")
        {
            Description =
                "Add an opacity rule. Format: 'domain=PATTERN' and/or 'origin=PATTERN' separated by commas " +
                "(e.g. --opaque 'domain=secret-*,origin=agent://internal-*'). Repeat for multiple rules.",
            AllowMultipleArgumentsPerToken = false,
        };
        var projectOption = new Option<string[]?>("--project")
        {
            Description =
                "Add a projection rule. Format: 'domain=PATTERN,origin=PATTERN,rename=NEW_NAME,retag=DESC,level=LEVEL' " +
                "(all parts optional; at least one key must be supplied). Repeat for multiple rules.",
            AllowMultipleArgumentsPerToken = false,
        };
        var synthesiseOption = new Option<string[]?>("--synthesise")
        {
            Description =
                "Add a synthesis rule. Format: 'name=NAME,domain=PATTERN,origin=PATTERN,description=TEXT,level=LEVEL' " +
                "(only 'name' is required). Repeat for multiple rules.",
            AllowMultipleArgumentsPerToken = false,
        };

        var command = new Command("set", "Replace the unit's boundary in full.");
        command.Arguments.Add(unitArg);
        command.Options.Add(fileOption);
        command.Options.Add(opaqueOption);
        command.Options.Add(projectOption);
        command.Options.Add(synthesiseOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var file = parseResult.GetValue(fileOption);
            var client = ClientFactory.Create();

            UnitBoundaryResponse body;
            if (!string.IsNullOrWhiteSpace(file))
            {
                if (!File.Exists(file))
                {
                    await Console.Error.WriteLineAsync($"File not found: {file}");
                    Environment.Exit(1);
                    return;
                }
                body = ParseBoundaryFromYaml(await File.ReadAllTextAsync(file, ct));
            }
            else
            {
                body = BuildFromFlags(
                    parseResult.GetValue(opaqueOption) ?? Array.Empty<string>(),
                    parseResult.GetValue(projectOption) ?? Array.Empty<string>(),
                    parseResult.GetValue(synthesiseOption) ?? Array.Empty<string>());

                if ((body.Opacities is null || body.Opacities.Count == 0)
                    && (body.Projections is null || body.Projections.Count == 0)
                    && (body.Syntheses is null || body.Syntheses.Count == 0))
                {
                    await Console.Error.WriteLineAsync(
                        "No rules supplied. Pass at least one of --opaque / --project / --synthesise, " +
                        "or use -f <file>; use 'clear' to remove every rule.");
                    Environment.Exit(1);
                    return;
                }
            }

            var stored = await client.SetUnitBoundaryAsync(unitId, body, ct);

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJsonPlain(stored));
            }
            else
            {
                Console.WriteLine($"Unit '{unitId}' boundary updated.");
                Console.Write(FormatBoundaryForHumans(unitId, stored));
            }
        });

        return command;
    }

    // ---- clear -------------------------------------------------------------

    private static Command CreateClear(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier" };
        var command = new Command("clear", "Remove every boundary rule from this unit.");
        command.Arguments.Add(unitArg);
        _ = outputOption; // wired for parity with other verbs; no output shape for clear

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitArg)!;
            var client = ClientFactory.Create();
            await client.ClearUnitBoundaryAsync(unitId, ct);
            Console.WriteLine($"Unit '{unitId}' boundary cleared.");
        });

        return command;
    }

    // ---- helpers -----------------------------------------------------------

    private static UnitBoundaryResponse BuildFromFlags(
        string[] opaque, string[] project, string[] synthesise)
    {
        var opacities = new List<BoundaryOpacityRuleDto>();
        foreach (var raw in opaque)
        {
            var kv = ParseKv(raw);
            opacities.Add(new BoundaryOpacityRuleDto
            {
                DomainPattern = kv.GetValueOrDefault("domain"),
                OriginPattern = kv.GetValueOrDefault("origin"),
            });
        }

        var projections = new List<BoundaryProjectionRuleDto>();
        foreach (var raw in project)
        {
            var kv = ParseKv(raw);
            projections.Add(new BoundaryProjectionRuleDto
            {
                DomainPattern = kv.GetValueOrDefault("domain"),
                OriginPattern = kv.GetValueOrDefault("origin"),
                RenameTo = kv.GetValueOrDefault("rename"),
                Retag = kv.GetValueOrDefault("retag"),
                OverrideLevel = kv.GetValueOrDefault("level"),
            });
        }

        var syntheses = new List<BoundarySynthesisRuleDto>();
        foreach (var raw in synthesise)
        {
            var kv = ParseKv(raw);
            var name = kv.GetValueOrDefault("name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }
            syntheses.Add(new BoundarySynthesisRuleDto
            {
                Name = name,
                DomainPattern = kv.GetValueOrDefault("domain"),
                OriginPattern = kv.GetValueOrDefault("origin"),
                Description = kv.GetValueOrDefault("description"),
                Level = kv.GetValueOrDefault("level"),
            });
        }

        return new UnitBoundaryResponse
        {
            Opacities = opacities.Count == 0 ? null : opacities,
            Projections = projections.Count == 0 ? null : projections,
            Syntheses = syntheses.Count == 0 ? null : syntheses,
        };
    }

    /// <summary>
    /// Parses a <c>k=v,k=v</c> spec into a dictionary. Empty values map to
    /// null; unknown keys are ignored at the caller level.
    /// </summary>
    internal static Dictionary<string, string?> ParseKv(string spec)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(spec))
        {
            return map;
        }
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            var key = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            map[key] = string.IsNullOrEmpty(value) ? null : value;
        }
        return map;
    }

    private static UnitBoundaryResponse ParseBoundaryFromYaml(string yamlText)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var parsed = deserializer.Deserialize<YamlBoundary>(yamlText) ?? new YamlBoundary();
        return new UnitBoundaryResponse
        {
            Opacities = parsed.Opacities?
                .Select(r => new BoundaryOpacityRuleDto
                {
                    DomainPattern = r.DomainPattern,
                    OriginPattern = r.OriginPattern,
                })
                .ToList(),
            Projections = parsed.Projections?
                .Select(r => new BoundaryProjectionRuleDto
                {
                    DomainPattern = r.DomainPattern,
                    OriginPattern = r.OriginPattern,
                    RenameTo = r.RenameTo,
                    Retag = r.Retag,
                    OverrideLevel = r.OverrideLevel,
                })
                .ToList(),
            Syntheses = parsed.Syntheses?
                .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                .Select(r => new BoundarySynthesisRuleDto
                {
                    Name = r.Name!,
                    DomainPattern = r.DomainPattern,
                    OriginPattern = r.OriginPattern,
                    Description = r.Description,
                    Level = r.Level,
                })
                .ToList(),
        };
    }

    private static string FormatBoundaryForHumans(string unitId, UnitBoundaryResponse boundary)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Unit: {unitId}");
        sb.AppendLine();

        sb.AppendLine("Opacities:");
        if (boundary.Opacities is null || boundary.Opacities.Count == 0)
        {
            sb.AppendLine("  (none)");
        }
        else
        {
            foreach (var rule in boundary.Opacities)
            {
                sb.AppendLine($"  - domain: {rule.DomainPattern ?? "(any)"}  origin: {rule.OriginPattern ?? "(any)"}");
            }
        }

        sb.AppendLine("Projections:");
        if (boundary.Projections is null || boundary.Projections.Count == 0)
        {
            sb.AppendLine("  (none)");
        }
        else
        {
            foreach (var rule in boundary.Projections)
            {
                sb.AppendLine(
                    $"  - domain: {rule.DomainPattern ?? "(any)"}  origin: {rule.OriginPattern ?? "(any)"}" +
                    $"  rename: {rule.RenameTo ?? "(no change)"}  retag: {rule.Retag ?? "(no change)"}" +
                    $"  level: {rule.OverrideLevel ?? "(no change)"}");
            }
        }

        sb.AppendLine("Syntheses:");
        if (boundary.Syntheses is null || boundary.Syntheses.Count == 0)
        {
            sb.AppendLine("  (none)");
        }
        else
        {
            foreach (var rule in boundary.Syntheses)
            {
                sb.AppendLine(
                    $"  - name: {rule.Name}  domain: {rule.DomainPattern ?? "(any)"}" +
                    $"  origin: {rule.OriginPattern ?? "(any)"}  level: {rule.Level ?? "(strongest seen)"}");
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// YAML-shaped mirror of <see cref="UnitBoundaryResponse"/>; YamlDotNet
    /// populates this from disk and the command layer projects it onto the
    /// Kiota wire type. Keeping a parallel shape avoids tight coupling
    /// between the YAML deserialiser and the generated client types.
    /// </summary>
    private sealed class YamlBoundary
    {
        public List<YamlOpacity>? Opacities { get; set; }
        public List<YamlProjection>? Projections { get; set; }
        public List<YamlSynthesis>? Syntheses { get; set; }
    }

    private sealed class YamlOpacity
    {
        public string? DomainPattern { get; set; }
        public string? OriginPattern { get; set; }
    }

    private sealed class YamlProjection
    {
        public string? DomainPattern { get; set; }
        public string? OriginPattern { get; set; }
        public string? RenameTo { get; set; }
        public string? Retag { get; set; }
        public string? OverrideLevel { get; set; }
    }

    private sealed class YamlSynthesis
    {
        public string? Name { get; set; }
        public string? DomainPattern { get; set; }
        public string? OriginPattern { get; set; }
        public string? Description { get; set; }
        public string? Level { get; set; }
    }
}