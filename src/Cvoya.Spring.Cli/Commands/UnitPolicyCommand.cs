// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// Builds the <c>spring unit policy &lt;dimension&gt; get|set|clear</c> subtree
/// (#453). The CLI exposes one verb group per <c>UnitPolicy</c> dimension —
/// <c>skill</c>, <c>model</c>, <c>cost</c>, <c>execution-mode</c>,
/// <c>initiative</c> — and wires each triple to the unified server surface
/// <c>GET/PUT /api/v1/units/{id}/policy</c>. Per-dimension verbs never mint
/// a per-dimension endpoint; instead, <c>set</c> / <c>clear</c> read the
/// current policy, mutate only the target slot, and PUT the merged result.
/// </summary>
/// <remarks>
/// <para>
/// Effective / inheritance display: <c>get</c> prints both the raw policy on
/// this unit and a small "effective policy" chain footer. Today the chain has
/// a single hop ("this unit") because unit-to-unit policy inheritance is
/// still tracked under #414; the footer is written so that when parent-unit
/// policy overlay arrives the rendering slots in without a CLI reshape.
/// </para>
/// <para>
/// Label-routing coordination with PR-PLAT-ORCH-1 (#389): the sixth
/// dimension — <c>label-routing</c> — hangs off <see cref="UnitPolicy.LabelRouting"/>
/// and ships under this same verb group. <c>spring unit policy label-routing
/// set</c> takes repeatable <c>--trigger label=member-path</c> pairs plus
/// optional <c>--add-on-assign</c> / <c>--remove-on-assign</c> roundtrip
/// label lists; the wire shape round-trips through the same
/// <c>/api/v1/units/{id}/policy</c> endpoint and the merge / clear
/// semantics are identical to the first five dimensions.
/// </para>
/// </remarks>
public static class UnitPolicyCommand
{
    /// <summary>
    /// Canonical set of dimension tokens accepted on the CLI. Kept stable
    /// across PRs so <c>spring unit policy skill get eng-team</c> means the
    /// same thing in the docs and the help output.
    /// </summary>
    internal static readonly IReadOnlyList<string> DimensionTokens =
        new[] { "skill", "model", "cost", "execution-mode", "initiative", "label-routing" };

    /// <summary>
    /// Entry point. Returns the <c>policy</c> subcommand tree for attachment
    /// under <c>unit</c>.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var policyCommand = new Command(
            "policy",
            "Manage a unit's governance policy across the five UnitPolicy dimensions. " +
            "Each dimension has its own get/set/clear triple; 'get' also prints the effective / merged policy.");

        foreach (var dim in DimensionTokens)
        {
            policyCommand.Subcommands.Add(CreateDimensionCommand(dim, outputOption));
        }

        return policyCommand;
    }

    private static Command CreateDimensionCommand(string dimension, Option<string> outputOption)
    {
        var help = dimension switch
        {
            "skill" => "Skill (tool) allow/block list.",
            "model" => "LLM model allow/block list.",
            "cost" => "Per-invocation / per-hour / per-day spend caps.",
            "execution-mode" => "Pinned or whitelisted execution mode (Auto / OnDemand).",
            "initiative" => "Unit-level deny overlay on allowed / blocked reflection actions.",
            "label-routing" => "Label -> member routing map consumed by the label-routed orchestration strategy.",
            _ => "Unit policy dimension.",
        };

        var dimCommand = new Command(dimension, help);
        dimCommand.Subcommands.Add(CreateGetCommand(dimension, outputOption));
        dimCommand.Subcommands.Add(CreateSetCommand(dimension, outputOption));
        dimCommand.Subcommands.Add(CreateClearCommand(dimension, outputOption));
        return dimCommand;
    }

    // ---- get ---------------------------------------------------------------

    private static Command CreateGetCommand(string dimension, Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier" };
        var command = new Command(
            "get",
            $"Print the {dimension} policy currently persisted on this unit plus the effective / merged view.");
        command.Arguments.Add(unitArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var policy = await client.GetUnitPolicyAsync(unitId, ct);
            var slot = ExtractSlot(dimension, policy);

            if (output == "json")
            {
                // JSON shape mirrors the effective-policy block for scripted
                // consumers. Adding the `effective` wrapper keeps the chain
                // observable even when callers pipe into jq.
                var payload = new
                {
                    unit = unitId,
                    dimension,
                    policy = slot,
                    effective = new
                    {
                        // Today the chain contains exactly one hop because
                        // unit-to-unit policy inheritance is not yet shipped
                        // (tracked under #414). Writing the chain as a list
                        // lets us extend without changing the JSON contract.
                        chain = new[] { new { source = "unit", id = unitId, policy = slot } },
                        merged = slot,
                    },
                };
                Console.WriteLine(OutputFormatter.FormatJsonPlain(payload));
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Unit:      {unitId}");
            sb.AppendLine($"Dimension: {dimension}");
            sb.AppendLine();
            sb.AppendLine("Current policy:");
            sb.AppendLine(FormatSlotForHumans(dimension, slot, indent: "  "));
            sb.AppendLine();
            sb.AppendLine("Effective policy (inheritance chain):");
            sb.AppendLine($"  1. unit://{unitId}");
            sb.AppendLine(FormatSlotForHumans(dimension, slot, indent: "     "));
            sb.AppendLine();
            sb.AppendLine(
                "  note: parent-unit policy inheritance is planned for a later release; " +
                "today the chain has a single hop.");
            Console.Write(sb.ToString());
        });

        return command;
    }

    // ---- set ---------------------------------------------------------------

    private static Command CreateSetCommand(string dimension, Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier" };
        // A YAML fragment file for this dimension (e.g. `skill: { allowed:
        // [...], blocked: [...] }`). Keeps parity with `spring apply -f` and
        // lets authors keep the policy canonically defined alongside the
        // unit manifest without the per-flag vocabulary.
        var fileOption = new Option<string?>("--file", "-f")
        {
            Description =
                "YAML fragment describing this dimension's policy; read from disk. " +
                "Mutually exclusive with the typed flags below.",
        };

        // Per-dimension typed flags. Each maps directly to a field on the
        // matching sub-record in Cvoya.Spring.Core.Policies. Declared per
        // dimension so help text is precise instead of a catch-all JSON blob.
        var allowedOption = new Option<string[]?>("--allowed")
        {
            Description = dimension switch
            {
                "skill" => "Comma/space-separated list of allowed tool names. Omit to leave the allow-list unchanged.",
                "model" => "Comma/space-separated list of allowed model identifiers.",
                "execution-mode" => "Comma/space-separated list of allowed execution modes (Auto, OnDemand).",
                "initiative" => "Comma/space-separated list of allowed reflection-action types.",
                _ => "Allowed list (not applicable to this dimension).",
            },
            AllowMultipleArgumentsPerToken = true,
        };
        var blockedOption = new Option<string[]?>("--blocked")
        {
            Description = dimension switch
            {
                "skill" => "Comma/space-separated list of blocked tool names.",
                "model" => "Comma/space-separated list of blocked model identifiers.",
                "initiative" => "Comma/space-separated list of blocked reflection-action types.",
                _ => "Blocked list (not applicable to this dimension).",
            },
            AllowMultipleArgumentsPerToken = true,
        };

        // Cost dimension flags. The wire type is double (generated from the
        // OpenAPI `number` schema); the core record uses decimal but Kiota
        // emits double for unqualified numbers. Use double here so options
        // map straight to the generated CostPolicy fields.
        var maxPerInvocationOption = new Option<double?>("--max-per-invocation")
        {
            Description = "Per-invocation absolute cost cap (USD).",
        };
        var maxPerHourOption = new Option<double?>("--max-per-hour")
        {
            Description = "Rolling per-hour cost cap (USD).",
        };
        var maxPerDayOption = new Option<double?>("--max-per-day")
        {
            Description = "Rolling per-24h cost cap (USD).",
        };

        // Execution-mode flag
        var forcedOption = new Option<string?>("--forced")
        {
            Description = "Force every agent in the unit to this execution mode (Auto or OnDemand).",
        };
        forcedOption.AcceptOnlyFromAmong("Auto", "OnDemand");

        // Initiative flags beyond allowed/blocked actions
        var maxLevelOption = new Option<string?>("--max-level")
        {
            Description = "Maximum initiative level (Passive, Attentive, Proactive, Autonomous).",
        };
        maxLevelOption.AcceptOnlyFromAmong(
            "Passive", "Attentive", "Proactive", "Autonomous");
        var requireUnitApprovalOption = new Option<bool?>("--require-unit-approval")
        {
            Description = "Whether agent-initiated actions require unit-level approval.",
        };

        // Label-routing flags (#389). `--trigger` carries a list of
        // `label=member-path` pairs — the simplest form that covers the
        // acceptance criteria without inventing a DSL. `--add-on-assign` /
        // `--remove-on-assign` are plain string lists consumed by connectors
        // after a successful routing decision.
        var triggerOption = new Option<string[]?>("--trigger")
        {
            Description =
                "Repeatable label->member mapping, e.g. --trigger agent:backend=backend-engineer " +
                "--trigger agent:qa=qa-engineer. Comma-separated mappings are also accepted. " +
                "The target is the member's Address.Path — bare agent / sub-unit name, no scheme.",
            AllowMultipleArgumentsPerToken = true,
        };
        var addOnAssignOption = new Option<string[]?>("--add-on-assign")
        {
            Description = "Labels the upstream connector should apply after a successful assignment (e.g. in-progress).",
            AllowMultipleArgumentsPerToken = true,
        };
        var removeOnAssignOption = new Option<string[]?>("--remove-on-assign")
        {
            Description = "Labels the upstream connector should strip after a successful assignment — typically the trigger labels themselves.",
            AllowMultipleArgumentsPerToken = true,
        };

        var command = new Command("set", $"Upsert the {dimension} policy on this unit.");
        command.Arguments.Add(unitArg);
        command.Options.Add(fileOption);

        // Attach only the flags relevant to this dimension so help output is
        // not polluted with irrelevant options.
        switch (dimension)
        {
            case "skill":
            case "model":
                command.Options.Add(allowedOption);
                command.Options.Add(blockedOption);
                break;
            case "cost":
                command.Options.Add(maxPerInvocationOption);
                command.Options.Add(maxPerHourOption);
                command.Options.Add(maxPerDayOption);
                break;
            case "execution-mode":
                command.Options.Add(allowedOption);
                command.Options.Add(forcedOption);
                break;
            case "initiative":
                command.Options.Add(allowedOption);
                command.Options.Add(blockedOption);
                command.Options.Add(maxLevelOption);
                command.Options.Add(requireUnitApprovalOption);
                break;
            case "label-routing":
                command.Options.Add(triggerOption);
                command.Options.Add(addOnAssignOption);
                command.Options.Add(removeOnAssignOption);
                break;
        }

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var file = parseResult.GetValue(fileOption);
            var client = ClientFactory.Create();

            var current = await client.GetUnitPolicyAsync(unitId, ct);

            object? newSlot;
            if (!string.IsNullOrWhiteSpace(file))
            {
                if (!File.Exists(file))
                {
                    await Console.Error.WriteLineAsync($"File not found: {file}");
                    Environment.Exit(1);
                    return;
                }
                var yamlText = await File.ReadAllTextAsync(file, ct);
                try
                {
                    newSlot = ParseSlotFromYaml(dimension, yamlText);
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync($"Failed to parse YAML: {ex.Message}");
                    Environment.Exit(1);
                    return;
                }
            }
            else
            {
                newSlot = BuildSlotFromFlags(
                    dimension,
                    parseResult.GetValue(allowedOption),
                    parseResult.GetValue(blockedOption),
                    parseResult.GetValue(maxPerInvocationOption),
                    parseResult.GetValue(maxPerHourOption),
                    parseResult.GetValue(maxPerDayOption),
                    parseResult.GetValue(forcedOption),
                    parseResult.GetValue(maxLevelOption),
                    parseResult.GetValue(requireUnitApprovalOption),
                    parseResult.GetValue(triggerOption),
                    parseResult.GetValue(addOnAssignOption),
                    parseResult.GetValue(removeOnAssignOption));
            }

            if (newSlot is null)
            {
                await Console.Error.WriteLineAsync(
                    $"No values supplied for dimension '{dimension}'. " +
                    "Pass at least one flag or use -f <file>; use 'clear' to unset this dimension.");
                Environment.Exit(1);
                return;
            }

            var merged = MergeSlot(dimension, current, newSlot);
            var stored = await client.SetUnitPolicyAsync(unitId, merged, ct);

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                {
                    unit = unitId,
                    dimension,
                    policy = ExtractSlot(dimension, stored),
                }));
            }
            else
            {
                Console.WriteLine($"Unit '{unitId}' {dimension} policy updated.");
                Console.Write(FormatSlotForHumans(dimension, ExtractSlot(dimension, stored), indent: "  "));
            }
        });

        return command;
    }

    // ---- clear -------------------------------------------------------------

    private static Command CreateClearCommand(string dimension, Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier" };
        var command = new Command(
            "clear",
            $"Remove the {dimension} dimension from this unit's policy (leaves other dimensions untouched).");
        command.Arguments.Add(unitArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var current = await client.GetUnitPolicyAsync(unitId, ct);
            var cleared = MergeSlot(dimension, current, null);
            var stored = await client.SetUnitPolicyAsync(unitId, cleared, ct);

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                {
                    unit = unitId,
                    dimension,
                    policy = (object?)null,
                    stored = ExtractSlot(dimension, stored),
                }));
            }
            else
            {
                Console.WriteLine(
                    $"Unit '{unitId}' {dimension} policy cleared.");
            }
        });

        return command;
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Extracts the wire slot for a given dimension from a
    /// <see cref="UnitPolicyResponse"/>. The Kiota generator wraps each slot
    /// in a composed-type container so we unwrap here and return the typed
    /// sub-record (or <c>null</c> when absent).
    /// </summary>
    private static object? ExtractSlot(string dimension, UnitPolicyResponse policy) => dimension switch
    {
        "skill" => policy.Skill?.SkillPolicy,
        "model" => policy.Model?.ModelPolicy,
        "cost" => policy.Cost?.CostPolicy,
        "execution-mode" => policy.ExecutionMode?.ExecutionModePolicy,
        "initiative" => policy.Initiative?.InitiativePolicy,
        "label-routing" => policy.LabelRouting?.LabelRoutingPolicy,
        _ => null,
    };

    /// <summary>
    /// Produces a new <see cref="UnitPolicyResponse"/> where <paramref name="slot"/>
    /// replaces the target dimension and every other dimension is carried through
    /// from <paramref name="current"/> verbatim. Passing <c>null</c> clears the
    /// dimension.
    /// </summary>
    private static UnitPolicyResponse MergeSlot(
        string dimension,
        UnitPolicyResponse current,
        object? slot)
    {
        var merged = new UnitPolicyResponse
        {
            Skill = current.Skill,
            Model = current.Model,
            Cost = current.Cost,
            ExecutionMode = current.ExecutionMode,
            Initiative = current.Initiative,
            LabelRouting = current.LabelRouting,
        };

        switch (dimension)
        {
            case "skill":
                merged.Skill = slot is null
                    ? null
                    : new UnitPolicyResponse.UnitPolicyResponse_skill { SkillPolicy = (SkillPolicy)slot };
                break;
            case "model":
                merged.Model = slot is null
                    ? null
                    : new UnitPolicyResponse.UnitPolicyResponse_model { ModelPolicy = (ModelPolicy)slot };
                break;
            case "cost":
                merged.Cost = slot is null
                    ? null
                    : new UnitPolicyResponse.UnitPolicyResponse_cost { CostPolicy = (CostPolicy)slot };
                break;
            case "execution-mode":
                merged.ExecutionMode = slot is null
                    ? null
                    : new UnitPolicyResponse.UnitPolicyResponse_executionMode { ExecutionModePolicy = (ExecutionModePolicy)slot };
                break;
            case "initiative":
                merged.Initiative = slot is null
                    ? null
                    : new UnitPolicyResponse.UnitPolicyResponse_initiative { InitiativePolicy = (InitiativePolicy)slot };
                break;
            case "label-routing":
                merged.LabelRouting = slot is null
                    ? null
                    : new UnitPolicyResponse.UnitPolicyResponse_labelRouting { LabelRoutingPolicy = (LabelRoutingPolicy)slot };
                break;
        }

        return merged;
    }

    /// <summary>
    /// Builds a typed slot for <paramref name="dimension"/> from the per-flag
    /// inputs. Returns <c>null</c> when no flag was supplied — that tells
    /// <c>set</c> to fail loud rather than silently clear the dimension
    /// (use <c>clear</c> for that).
    /// </summary>
    private static object? BuildSlotFromFlags(
        string dimension,
        string[]? allowed,
        string[]? blocked,
        double? maxPerInvocation,
        double? maxPerHour,
        double? maxPerDay,
        string? forced,
        string? maxLevel,
        bool? requireUnitApproval,
        string[]? trigger,
        string[]? addOnAssign,
        string[]? removeOnAssign)
    {
        switch (dimension)
        {
            case "skill":
                if (allowed is null && blocked is null)
                {
                    return null;
                }
                return new SkillPolicy
                {
                    Allowed = NormaliseList(allowed),
                    Blocked = NormaliseList(blocked),
                };
            case "model":
                if (allowed is null && blocked is null)
                {
                    return null;
                }
                return new ModelPolicy
                {
                    Allowed = NormaliseList(allowed),
                    Blocked = NormaliseList(blocked),
                };
            case "cost":
                if (maxPerInvocation is null && maxPerHour is null && maxPerDay is null)
                {
                    return null;
                }
                return new CostPolicy
                {
                    MaxCostPerInvocation = maxPerInvocation,
                    MaxCostPerHour = maxPerHour,
                    MaxCostPerDay = maxPerDay,
                };
            case "execution-mode":
                if (allowed is null && string.IsNullOrEmpty(forced))
                {
                    return null;
                }
                // Wire type for the allowed list is List<AgentExecutionMode?>,
                // matching the Kiota enum-nullability idiom for `oneOf` sets.
                var allowedModes = NormaliseList(allowed)
                    ?.Select(value => ParseExecutionMode(value))
                    .Where(m => m.HasValue)
                    .Cast<AgentExecutionMode?>()
                    .ToList();
                return new ExecutionModePolicy
                {
                    Forced = string.IsNullOrEmpty(forced)
                        ? null
                        : new ExecutionModePolicy.ExecutionModePolicy_forced
                        {
                            AgentExecutionMode = ParseExecutionMode(forced!),
                        },
                    Allowed = allowedModes is null || allowedModes.Count == 0 ? null : allowedModes,
                };
            case "initiative":
                if (allowed is null && blocked is null
                    && string.IsNullOrEmpty(maxLevel) && requireUnitApproval is null)
                {
                    return null;
                }
                return new InitiativePolicy
                {
                    AllowedActions = NormaliseList(allowed),
                    BlockedActions = NormaliseList(blocked),
                    MaxLevel = string.IsNullOrEmpty(maxLevel)
                        ? null
                        : ParseInitiativeLevel(maxLevel!),
                    RequireUnitApproval = requireUnitApproval,
                };
            case "label-routing":
                if (trigger is null && addOnAssign is null && removeOnAssign is null)
                {
                    return null;
                }
                var triggerMap = ParseTriggerMap(trigger);
                return new LabelRoutingPolicy
                {
                    TriggerLabels = triggerMap is null || triggerMap.Count == 0
                        ? null
                        : new LabelRoutingPolicy_triggerLabels
                        {
                            AdditionalData = triggerMap.ToDictionary(
                                kvp => kvp.Key,
                                kvp => (object)kvp.Value),
                        },
                    AddOnAssign = NormaliseList(addOnAssign),
                    RemoveOnAssign = NormaliseList(removeOnAssign),
                };
            default:
                return null;
        }
    }

    /// <summary>
    /// Parses repeated <c>label=target</c> tokens. Accepts comma-separated
    /// pairs inside a single token too, matching the ergonomics of
    /// <see cref="NormaliseList"/>. Returns <c>null</c> if the caller passed
    /// nothing so <c>set</c> can distinguish "no flag" from "empty map".
    /// </summary>
    private static Dictionary<string, string>? ParseTriggerMap(string[]? values)
    {
        if (values is null || values.Length == 0)
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in values)
        {
            foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = token.IndexOf('=');
                if (eq <= 0 || eq == token.Length - 1)
                {
                    throw new InvalidOperationException(
                        $"Invalid --trigger value '{token}'. Expected 'label=member-path'.");
                }
                var key = token[..eq].Trim();
                var value = token[(eq + 1)..].Trim();
                map[key] = value;
            }
        }
        return map;
    }

    private static List<string>? NormaliseList(string[]? values)
    {
        if (values is null || values.Length == 0)
        {
            return null;
        }
        // Allow callers to pass either `--allowed a b c` (multi-token) OR
        // `--allowed a,b,c` (comma-delimited). Keeps the shell ergonomics
        // consistent with other collection-style flags in the CLI.
        var expanded = new List<string>();
        foreach (var value in values)
        {
            expanded.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        return expanded.Count == 0 ? null : expanded;
    }

    private static AgentExecutionMode? ParseExecutionMode(string value) => value switch
    {
        "Auto" => AgentExecutionMode.Auto,
        "OnDemand" => AgentExecutionMode.OnDemand,
        _ => null,
    };

    private static InitiativeLevel? ParseInitiativeLevel(string value) => value switch
    {
        "Passive" => InitiativeLevel.Passive,
        "Attentive" => InitiativeLevel.Attentive,
        "Proactive" => InitiativeLevel.Proactive,
        "Autonomous" => InitiativeLevel.Autonomous,
        _ => null,
    };

    /// <summary>
    /// Parses a YAML fragment describing a single dimension's policy. Accepts
    /// either a bare dimension map (<c>allowed: [...]</c>) or a wrapped map
    /// keyed by the dimension token (<c>skill: { allowed: [...] }</c>), so
    /// the same file works when piped into <c>spring unit policy skill set</c>
    /// or alongside a full unit manifest.
    /// </summary>
    private static object? ParseSlotFromYaml(string dimension, string yamlText)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        // YamlDotNet returns Dictionary<string, object>; project to the
        // nullable-value shape the rest of this helper works in so our
        // readers can express "absent" vs "null value" uniformly.
        var rawRoot = deserializer.Deserialize<Dictionary<string, object>>(yamlText)
            ?? new Dictionary<string, object>();
        var root = rawRoot.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value);

        // If the file has a top-level dimension key, unwrap it.
        if (root.TryGetValue(dimension, out var inner) && inner is Dictionary<object, object> innerDict)
        {
            root = innerDict.ToDictionary(kvp => kvp.Key.ToString()!, kvp => (object?)kvp.Value);
        }
        else if (dimension == "execution-mode"
                 && root.TryGetValue("executionMode", out var compact)
                 && compact is Dictionary<object, object> compactDict)
        {
            // accept `executionMode:` too
            root = compactDict.ToDictionary(kvp => kvp.Key.ToString()!, kvp => (object?)kvp.Value);
        }
        else if (dimension == "label-routing"
                 && root.TryGetValue("labelRouting", out var labelCompact)
                 && labelCompact is Dictionary<object, object> labelCompactDict)
        {
            // accept `labelRouting:` too
            root = labelCompactDict.ToDictionary(kvp => kvp.Key.ToString()!, kvp => (object?)kvp.Value);
        }

        return dimension switch
        {
            "skill" => new SkillPolicy
            {
                Allowed = ReadList(root, "allowed"),
                Blocked = ReadList(root, "blocked"),
            },
            "model" => new ModelPolicy
            {
                Allowed = ReadList(root, "allowed"),
                Blocked = ReadList(root, "blocked"),
            },
            "cost" => new CostPolicy
            {
                MaxCostPerInvocation = ReadDouble(root, "maxCostPerInvocation")
                    ?? ReadDouble(root, "max_cost_per_invocation"),
                MaxCostPerHour = ReadDouble(root, "maxCostPerHour")
                    ?? ReadDouble(root, "max_cost_per_hour"),
                MaxCostPerDay = ReadDouble(root, "maxCostPerDay")
                    ?? ReadDouble(root, "max_cost_per_day"),
            },
            "execution-mode" => new ExecutionModePolicy
            {
                Forced = ReadString(root, "forced") is { Length: > 0 } forced
                    ? new ExecutionModePolicy.ExecutionModePolicy_forced
                    {
                        AgentExecutionMode = ParseExecutionMode(forced),
                    }
                    : null,
                Allowed = ReadList(root, "allowed")
                    ?.Select(value => ParseExecutionMode(value))
                    .Where(m => m.HasValue)
                    .Cast<AgentExecutionMode?>()
                    .ToList(),
            },
            "initiative" => new InitiativePolicy
            {
                AllowedActions = ReadList(root, "allowedActions") ?? ReadList(root, "allowed_actions"),
                BlockedActions = ReadList(root, "blockedActions") ?? ReadList(root, "blocked_actions"),
                MaxLevel = ReadString(root, "maxLevel") is { Length: > 0 } level
                    ? ParseInitiativeLevel(level)
                    : null,
                RequireUnitApproval = ReadBool(root, "requireUnitApproval")
                    ?? ReadBool(root, "require_unit_approval"),
            },
            "label-routing" => BuildLabelRoutingFromYaml(root),
            _ => null,
        };
    }

    /// <summary>
    /// Builds a <see cref="LabelRoutingPolicy"/> from a parsed YAML map.
    /// Accepts both camelCase (<c>triggerLabels</c>, <c>addOnAssign</c>,
    /// <c>removeOnAssign</c>) and snake_case aliases for operator
    /// ergonomics.
    /// </summary>
    private static LabelRoutingPolicy BuildLabelRoutingFromYaml(Dictionary<string, object?> root)
    {
        var triggerMap = ReadStringMap(root, "triggerLabels") ?? ReadStringMap(root, "trigger_labels");
        var addOn = ReadList(root, "addOnAssign") ?? ReadList(root, "add_on_assign");
        var removeOn = ReadList(root, "removeOnAssign") ?? ReadList(root, "remove_on_assign");

        return new LabelRoutingPolicy
        {
            TriggerLabels = triggerMap is null || triggerMap.Count == 0
                ? null
                : new LabelRoutingPolicy_triggerLabels
                {
                    AdditionalData = triggerMap.ToDictionary(
                        kvp => kvp.Key,
                        kvp => (object)kvp.Value),
                },
            AddOnAssign = addOn,
            RemoveOnAssign = removeOn,
        };
    }

    private static Dictionary<string, string>? ReadStringMap(Dictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }
        if (value is Dictionary<object, object> dict)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in dict)
            {
                var k = kvp.Key?.ToString();
                var v = kvp.Value?.ToString();
                if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(v))
                {
                    result[k] = v;
                }
            }
            return result;
        }
        return null;
    }

    private static List<string>? ReadList(Dictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }
        if (value is IEnumerable<object?> list)
        {
            return list.Where(v => v is not null).Select(v => v!.ToString()!).ToList();
        }
        return new List<string> { value.ToString()! };
    }

    private static double? ReadDouble(Dictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }
        if (double.TryParse(value.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return null;
    }

    private static bool? ReadBool(Dictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }
        if (bool.TryParse(value.ToString(), out var parsed))
        {
            return parsed;
        }
        return null;
    }

    private static string? ReadString(Dictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }
        return value.ToString();
    }

    /// <summary>
    /// Renders a typed policy slot for the table / default text output.
    /// </summary>
    private static string FormatSlotForHumans(string dimension, object? slot, string indent)
    {
        if (slot is null)
        {
            return $"{indent}(none — no {dimension} constraint on this unit)\n";
        }

        var sb = new StringBuilder();
        switch (slot)
        {
            case SkillPolicy skill:
                sb.AppendLine($"{indent}allowed: {FormatList(skill.Allowed)}");
                sb.AppendLine($"{indent}blocked: {FormatList(skill.Blocked)}");
                break;
            case ModelPolicy model:
                sb.AppendLine($"{indent}allowed: {FormatList(model.Allowed)}");
                sb.AppendLine($"{indent}blocked: {FormatList(model.Blocked)}");
                break;
            case CostPolicy cost:
                sb.AppendLine($"{indent}maxCostPerInvocation: {cost.MaxCostPerInvocation?.ToString() ?? "(unset)"}");
                sb.AppendLine($"{indent}maxCostPerHour:       {cost.MaxCostPerHour?.ToString() ?? "(unset)"}");
                sb.AppendLine($"{indent}maxCostPerDay:        {cost.MaxCostPerDay?.ToString() ?? "(unset)"}");
                break;
            case ExecutionModePolicy mode:
                sb.AppendLine($"{indent}forced:  {mode.Forced?.AgentExecutionMode?.ToString() ?? "(none)"}");
                sb.AppendLine($"{indent}allowed: {FormatEnumList(mode.Allowed)}");
                break;
            case InitiativePolicy init:
                sb.AppendLine($"{indent}maxLevel:           {init.MaxLevel?.ToString() ?? "(unset)"}");
                sb.AppendLine($"{indent}requireUnitApproval: {init.RequireUnitApproval?.ToString() ?? "(unset)"}");
                sb.AppendLine($"{indent}allowedActions:     {FormatList(init.AllowedActions)}");
                sb.AppendLine($"{indent}blockedActions:     {FormatList(init.BlockedActions)}");
                break;
            case LabelRoutingPolicy label:
                sb.AppendLine($"{indent}triggerLabels:   {FormatLabelMap(label.TriggerLabels)}");
                sb.AppendLine($"{indent}addOnAssign:     {FormatList(label.AddOnAssign)}");
                sb.AppendLine($"{indent}removeOnAssign:  {FormatList(label.RemoveOnAssign)}");
                break;
        }
        return sb.ToString();
    }

    private static string FormatLabelMap(LabelRoutingPolicy_triggerLabels? labels)
    {
        if (labels is null || labels.AdditionalData is null || labels.AdditionalData.Count == 0)
        {
            return "(none)";
        }
        var entries = labels.AdditionalData
            .Select(kvp => $"{kvp.Key}={kvp.Value}")
            .ToList();
        return "{" + string.Join(", ", entries) + "}";
    }

    private static string FormatList(IReadOnlyList<string>? values)
        => values is null || values.Count == 0 ? "(none)" : "[" + string.Join(", ", values) + "]";

    private static string FormatEnumList(IReadOnlyList<AgentExecutionMode?>? values)
        => values is null || values.Count == 0
            ? "(none)"
            : "[" + string.Join(", ", values.Where(v => v.HasValue).Select(v => v!.ToString())) + "]";
}