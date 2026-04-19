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
/// Builds the <c>spring unit orchestration get|set|clear</c> verb subtree
/// (#606). Direct read/write access to the manifest-persisted
/// <c>orchestration.strategy</c> slot — the surface ADR-0010 deliberately
/// deferred — without needing a full <c>spring apply -f unit.yaml</c>
/// re-apply. Wraps
/// <see cref="SpringApiClient.GetUnitOrchestrationAsync(string, System.Threading.CancellationToken)"/>
/// et al so UI / CLI parity is identical to the Orchestration tab
/// delivered in PR #611.
/// </summary>
/// <remarks>
/// <para>
/// Strategy keys: only the platform-offered set (<c>ai</c>, <c>workflow</c>,
/// <c>label-routed</c>) is whitelisted on the <c>--strategy</c> option so
/// operators see a clear enumeration in <c>--help</c>. Custom strategy
/// keys registered by a host overlay are tracked under #605 and will
/// surface on this verb when that issue closes.
/// </para>
/// <para>
/// <c>set --label-routing &lt;file&gt;</c> is a UI-parity convenience that
/// also applies a <c>UnitPolicy.LabelRouting</c> update through the
/// existing <c>/api/v1/units/{id}/policy</c> endpoint (PR #493). This
/// mirrors the Orchestration portal tab where strategy + label-routing
/// are edited in adjacent cards — the CLI verb accepts both in one
/// invocation so a scripted operator does not have to chain two commands.
/// </para>
/// </remarks>
public static class UnitOrchestrationCommand
{
    /// <summary>
    /// Canonical platform-offered strategy keys. Must match the set
    /// documented on <c>docs/architecture/units.md § Manifest-driven
    /// strategy selection</c> and the portal's <c>ORCHESTRATION_STRATEGIES</c>
    /// whitelist so the three surfaces (portal / CLI / docs) stay in lock-
    /// step.
    /// </summary>
    internal static readonly string[] StrategyKeys = { "ai", "workflow", "label-routed" };

    /// <summary>
    /// Entry point. Returns the <c>orchestration</c> subcommand tree for
    /// attachment under <c>unit</c>.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var command = new Command(
            "orchestration",
            "Read / write the unit's manifest-persisted orchestration strategy. " +
            "Platform-offered keys: ai, workflow, label-routed.");

        command.Subcommands.Add(CreateGetCommand(outputOption));
        command.Subcommands.Add(CreateSetCommand(outputOption));
        command.Subcommands.Add(CreateClearCommand(outputOption));
        return command;
    }

    // ---- get ---------------------------------------------------------------

    private static Command CreateGetCommand(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier" };
        var command = new Command(
            "get",
            "Print the orchestration strategy persisted on this unit. Also surfaces the unit's " +
            "label-routing policy (if any) since that is what the label-routed strategy consumes.");
        command.Arguments.Add(unitArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            // Fetch both in parallel so a single CLI roundtrip reports the
            // entire orchestration surface — the dedicated /orchestration
            // slot plus the policy /label-routing dimension that the
            // label-routed strategy reads through the resolver's inference
            // hop (ADR-0010).
            var orchestrationTask = client.GetUnitOrchestrationAsync(unitId, ct);
            var policyTask = client.GetUnitPolicyAsync(unitId, ct);
            await Task.WhenAll(orchestrationTask, policyTask);

            var orchestration = orchestrationTask.Result;
            var labelRouting = policyTask.Result.LabelRouting?.LabelRoutingPolicy;

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                {
                    unit = unitId,
                    strategy = orchestration.Strategy,
                    labelRouting = labelRouting is null ? null : new
                    {
                        triggerLabels = labelRouting.TriggerLabels?.AdditionalData,
                        addOnAssign = labelRouting.AddOnAssign,
                        removeOnAssign = labelRouting.RemoveOnAssign,
                    },
                }));
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Unit:      {unitId}");
            sb.AppendLine($"Strategy:  {orchestration.Strategy ?? "(unset — inferred / default per ADR-0010)"}");
            sb.AppendLine();
            sb.AppendLine("Label routing policy:");
            if (labelRouting is null)
            {
                sb.AppendLine("  (unset)");
            }
            else
            {
                var triggers = labelRouting.TriggerLabels?.AdditionalData;
                if (triggers is null || triggers.Count == 0)
                {
                    sb.AppendLine("  triggerLabels:  (none)");
                }
                else
                {
                    sb.AppendLine("  triggerLabels:");
                    foreach (var kvp in triggers)
                    {
                        sb.AppendLine($"    {kvp.Key} -> {kvp.Value}");
                    }
                }
                sb.AppendLine($"  addOnAssign:    {FormatList(labelRouting.AddOnAssign)}");
                sb.AppendLine($"  removeOnAssign: {FormatList(labelRouting.RemoveOnAssign)}");
            }
            Console.Write(sb.ToString());
        });

        return command;
    }

    // ---- set ---------------------------------------------------------------

    private static Command CreateSetCommand(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier" };
        var strategyOption = new Option<string?>("--strategy")
        {
            Description = "Orchestration strategy key. Platform-offered values: " +
                string.Join(", ", StrategyKeys) + ".",
        };
        strategyOption.AcceptOnlyFromAmong(StrategyKeys);

        var labelRoutingOption = new Option<string?>("--label-routing")
        {
            Description =
                "Optional YAML file describing a UnitPolicy.LabelRouting block to apply alongside the strategy. " +
                "Accepts either a bare dimension map (triggerLabels / addOnAssign / removeOnAssign) or a top-level " +
                "`labelRouting:` / `label-routing:` wrapper. Routed through the existing PUT /api/v1/units/{id}/policy " +
                "endpoint so this verb keeps label-routing and strategy editable in one invocation — matching the " +
                "portal's Orchestration tab. When omitted, the unit's existing label-routing policy is left alone.",
        };

        var command = new Command(
            "set",
            "Upsert the orchestration strategy on this unit. Optionally co-apply a label-routing policy from a YAML file.");
        command.Arguments.Add(unitArg);
        command.Options.Add(strategyOption);
        command.Options.Add(labelRoutingOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitArg)!;
            var strategy = parseResult.GetValue(strategyOption);
            var labelRoutingFile = parseResult.GetValue(labelRoutingOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (string.IsNullOrWhiteSpace(strategy) && string.IsNullOrWhiteSpace(labelRoutingFile))
            {
                await Console.Error.WriteLineAsync(
                    "Nothing to set. Pass --strategy <ai|workflow|label-routed> and/or --label-routing <file>. " +
                    "Use 'clear' to unset the strategy.");
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();

            UnitOrchestrationResponse? orchestration = null;
            if (!string.IsNullOrWhiteSpace(strategy))
            {
                orchestration = await client.SetUnitOrchestrationAsync(unitId, strategy!, ct);
            }

            LabelRoutingPolicy? labelRouting = null;
            if (!string.IsNullOrWhiteSpace(labelRoutingFile))
            {
                if (!File.Exists(labelRoutingFile))
                {
                    await Console.Error.WriteLineAsync($"File not found: {labelRoutingFile}");
                    Environment.Exit(1);
                    return;
                }

                LabelRoutingPolicy newSlot;
                try
                {
                    var yamlText = await File.ReadAllTextAsync(labelRoutingFile, ct);
                    newSlot = ParseLabelRoutingYaml(yamlText);
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync(
                        $"Failed to parse label-routing YAML: {ex.Message}");
                    Environment.Exit(1);
                    return;
                }

                var currentPolicy = await client.GetUnitPolicyAsync(unitId, ct);
                currentPolicy.LabelRouting = new UnitPolicyResponse.UnitPolicyResponse_labelRouting
                {
                    LabelRoutingPolicy = newSlot,
                };
                var stored = await client.SetUnitPolicyAsync(unitId, currentPolicy, ct);
                labelRouting = stored.LabelRouting?.LabelRoutingPolicy;
            }

            // Re-read the orchestration slot if we only touched label-routing
            // so the JSON / table output still shows the current strategy.
            if (orchestration is null)
            {
                orchestration = await client.GetUnitOrchestrationAsync(unitId, ct);
            }

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                {
                    unit = unitId,
                    strategy = orchestration.Strategy,
                    labelRouting = labelRouting is null ? null : new
                    {
                        triggerLabels = labelRouting.TriggerLabels?.AdditionalData,
                        addOnAssign = labelRouting.AddOnAssign,
                        removeOnAssign = labelRouting.RemoveOnAssign,
                    },
                }));
            }
            else
            {
                Console.WriteLine($"Unit '{unitId}' orchestration updated.");
                Console.WriteLine($"  strategy: {orchestration.Strategy ?? "(unset)"}");
                if (labelRouting is not null)
                {
                    var triggers = labelRouting.TriggerLabels?.AdditionalData;
                    Console.WriteLine($"  labelRouting.triggerLabels: {FormatMap(triggers)}");
                    Console.WriteLine($"  labelRouting.addOnAssign:   {FormatList(labelRouting.AddOnAssign)}");
                    Console.WriteLine($"  labelRouting.removeOnAssign:{FormatList(labelRouting.RemoveOnAssign)}");
                }
            }
        });

        return command;
    }

    // ---- clear -------------------------------------------------------------

    private static Command CreateClearCommand(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier" };
        var command = new Command(
            "clear",
            "Remove the orchestration strategy on this unit. The unit falls back to the " +
            "policy-inferred / unkeyed default strategy per ADR-0010. Does NOT touch the " +
            "unit's label-routing policy — use `spring unit policy label-routing clear` for that.");
        command.Arguments.Add(unitArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            await client.ClearUnitOrchestrationAsync(unitId, ct);

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                {
                    unit = unitId,
                    strategy = (string?)null,
                }));
            }
            else
            {
                Console.WriteLine(
                    $"Unit '{unitId}' orchestration strategy cleared; resolver will pick via policy / default.");
            }
        });

        return command;
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Parses a YAML fragment describing a label-routing slot. Accepts
    /// either a bare dimension map or a <c>labelRouting:</c> /
    /// <c>label-routing:</c> wrapper — the same tolerance
    /// <see cref="UnitPolicyCommand"/> applies for <c>--file</c> input, so
    /// the same file works with either CLI surface.
    /// </summary>
    internal static LabelRoutingPolicy ParseLabelRoutingYaml(string yamlText)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var rawRoot = deserializer.Deserialize<Dictionary<string, object>>(yamlText)
            ?? new Dictionary<string, object>();
        var root = rawRoot.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value);

        // Allow `labelRouting:` or `label-routing:` wrapper for
        // operator ergonomics.
        if (root.TryGetValue("labelRouting", out var inner) && inner is Dictionary<object, object> innerDict)
        {
            root = innerDict.ToDictionary(kvp => kvp.Key.ToString()!, kvp => (object?)kvp.Value);
        }
        else if (root.TryGetValue("label-routing", out var hyphenInner) && hyphenInner is Dictionary<object, object> hyphenDict)
        {
            root = hyphenDict.ToDictionary(kvp => kvp.Key.ToString()!, kvp => (object?)kvp.Value);
        }

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

    private static string FormatList(IReadOnlyList<string>? values)
        => values is null || values.Count == 0 ? "(none)" : "[" + string.Join(", ", values) + "]";

    private static string FormatMap(IDictionary<string, object>? map)
    {
        if (map is null || map.Count == 0)
        {
            return "(none)";
        }
        return "{" + string.Join(", ", map.Select(kvp => $"{kvp.Key}={kvp.Value}")) + "}";
    }
}