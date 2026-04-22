// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;

/// <summary>
/// Builds the <c>spring unit execution get|set|clear</c> verb subtree
/// (#601 / #603 / #409 B-wide). Direct read/write access to the
/// manifest-persisted unit <c>execution:</c> block (image / runtime /
/// tool / provider / model) without needing a full <c>spring apply -f
/// unit.yaml</c> re-apply. Wraps
/// <see cref="SpringApiClient.GetUnitExecutionAsync(string, System.Threading.CancellationToken)"/>
/// et al so UI / CLI parity is identical to the Execution tab delivered
/// in the follow-up portal PR.
/// </summary>
/// <remarks>
/// <para>
/// Each field is independently settable and independently clearable.
/// <c>set</c> performs a partial update — pass only the flags you want
/// to change. <c>clear</c> strips the whole block; <c>clear --field X</c>
/// clears one field only.
/// </para>
/// <para>
/// <c>--provider</c> is meaningful only when <c>--tool dapr-agent</c>
/// is set on the unit (or the agent inheriting from it); the other
/// tools bake their provider in. <c>--model</c> is meaningful for every
/// tool that carries a known provider family — <c>claude-code</c>
/// (Anthropic), <c>codex</c> (OpenAI), <c>gemini</c> (Google), and
/// <c>dapr-agent</c> — and the CLI treats the value as opaque per the
/// #644 parity fix. The <c>set</c> verb does not enforce either rule
/// today (no whitelist on the server either) so the gating behaviour
/// lives in one place (<c>UnitCommand.ValidateProviderModelAgainstTool</c>);
/// see <c>docs/architecture/cli-and-web.md § Provider + Model flag
/// validation</c>.
/// </para>
/// </remarks>
public static class UnitExecutionCommand
{
    /// <summary>
    /// Launcher keys the unit execution block may reference as its
    /// default <c>tool</c>. Matches the <c>IAgentToolLauncher</c>
    /// registrations in
    /// <c>Cvoya.Spring.Dapr.DependencyInjection.ServiceCollectionExtensions</c>.
    /// <c>custom</c> is the escape hatch for host overlays that register
    /// additional launchers through DI — the server does not whitelist
    /// this field; the launcher lookup fails cleanly at dispatch time
    /// when no implementation is registered for the configured key.
    /// </summary>
    internal static readonly string[] ToolKeys =
    {
        "claude-code", "codex", "gemini", "dapr-agent", "custom",
    };

    /// <summary>Container runtime keys offered on <c>--runtime</c>.</summary>
    internal static readonly string[] RuntimeKeys = { "docker", "podman" };

    /// <summary>Field keys accepted on <c>clear --field</c>.</summary>
    internal static readonly string[] FieldKeys =
    {
        "image", "runtime", "tool", "provider", "model",
    };

    /// <summary>
    /// Entry point. Returns the <c>execution</c> subcommand tree for
    /// attachment under <c>unit</c>.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var command = new Command(
            "execution",
            "Read / write the unit's manifest-persisted execution defaults. " +
            "Fields: image, runtime, tool, provider, model. Inherited by member agents that " +
            "don't declare their own value per the agent → unit → fail resolution chain.");

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
            "Print the unit's persisted execution defaults. All-null fields indicate " +
            "the unit has no declared default and member agents must supply their own.");
        command.Arguments.Add(unitArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var defaults = await client.GetUnitExecutionAsync(unitId, ct);

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                {
                    unit = unitId,
                    image = defaults.Image,
                    runtime = defaults.Runtime,
                    tool = defaults.Tool,
                    provider = defaults.Provider,
                    model = defaults.Model,
                }));
                return;
            }

            Console.WriteLine($"Unit:     {unitId}");
            Console.WriteLine($"  image:    {defaults.Image ?? "(unset)"}");
            Console.WriteLine($"  runtime:  {defaults.Runtime ?? "(unset)"}");
            Console.WriteLine($"  tool:     {defaults.Tool ?? "(unset)"}");
            Console.WriteLine($"  provider: {defaults.Provider ?? "(unset)"}");
            Console.WriteLine($"  model:    {defaults.Model ?? "(unset)"}");
        });

        return command;
    }

    // ---- set ---------------------------------------------------------------

    private static Command CreateSetCommand(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier" };
        var imageOption = new Option<string?>("--image")
        {
            Description = "Default container image reference (e.g. ghcr.io/... or localhost/spring-voyage-agent:latest).",
        };
        var runtimeOption = new Option<string?>("--runtime")
        {
            Description = "Default container runtime. Allowed values: " + string.Join(", ", RuntimeKeys) + ".",
        };
        runtimeOption.AcceptOnlyFromAmong(RuntimeKeys);

        var toolOption = new Option<string?>("--tool")
        {
            Description = "Default external agent tool. Allowed values: " + string.Join(", ", ToolKeys) + ".",
        };
        toolOption.AcceptOnlyFromAmong(ToolKeys);

        var providerOption = new Option<string?>("--provider")
        {
            Description = "Default LLM provider (Dapr-Agent-tool-specific; e.g. ollama, openai, anthropic, googleai).",
        };
        var modelOption = new Option<string?>("--model")
        {
            Description =
                "Default model identifier. Meaningful for every tool that carries a known provider family " +
                "(claude-code, codex, gemini, dapr-agent); the value is accepted as opaque and validated at " +
                "unit activation.",
        };

        var command = new Command(
            "set",
            "Upsert one or more fields on the unit's execution defaults. Partial update — " +
            "pass only the flags you want to change; unlisted fields keep their current value.");
        command.Arguments.Add(unitArg);
        command.Options.Add(imageOption);
        command.Options.Add(runtimeOption);
        command.Options.Add(toolOption);
        command.Options.Add(providerOption);
        command.Options.Add(modelOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitArg)!;
            var image = parseResult.GetValue(imageOption);
            var runtime = parseResult.GetValue(runtimeOption);
            var tool = parseResult.GetValue(toolOption);
            var provider = parseResult.GetValue(providerOption);
            var model = parseResult.GetValue(modelOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (string.IsNullOrWhiteSpace(image) && string.IsNullOrWhiteSpace(runtime)
                && string.IsNullOrWhiteSpace(tool) && string.IsNullOrWhiteSpace(provider)
                && string.IsNullOrWhiteSpace(model))
            {
                await Console.Error.WriteLineAsync(
                    "Nothing to set. Pass at least one of --image, --runtime, --tool, --provider, --model. " +
                    "Use 'clear' to wipe the block or 'clear --field X' to clear one field.");
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();

            var stored = await client.SetUnitExecutionAsync(unitId, new UnitExecutionResponse
            {
                Image = image,
                Runtime = runtime,
                Tool = tool,
                Provider = provider,
                Model = model,
            }, ct);

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                {
                    unit = unitId,
                    image = stored.Image,
                    runtime = stored.Runtime,
                    tool = stored.Tool,
                    provider = stored.Provider,
                    model = stored.Model,
                }));
            }
            else
            {
                Console.WriteLine($"Unit '{unitId}' execution updated.");
                Console.WriteLine($"  image:    {stored.Image ?? "(unset)"}");
                Console.WriteLine($"  runtime:  {stored.Runtime ?? "(unset)"}");
                Console.WriteLine($"  tool:     {stored.Tool ?? "(unset)"}");
                Console.WriteLine($"  provider: {stored.Provider ?? "(unset)"}");
                Console.WriteLine($"  model:    {stored.Model ?? "(unset)"}");
            }
        });

        return command;
    }

    // ---- clear -------------------------------------------------------------

    private static Command CreateClearCommand(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier" };
        var fieldOption = new Option<string?>("--field")
        {
            Description = "Clear one field only. Allowed values: " + string.Join(", ", FieldKeys) + ". " +
                "When omitted, the entire execution block is stripped.",
        };
        fieldOption.AcceptOnlyFromAmong(FieldKeys);

        var command = new Command(
            "clear",
            "Remove the unit's execution defaults. Without --field the entire block is " +
            "stripped; with --field only that slot is cleared (the others keep their value).");
        command.Arguments.Add(unitArg);
        command.Options.Add(fieldOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitArg)!;
            var field = parseResult.GetValue(fieldOption);
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            if (string.IsNullOrWhiteSpace(field))
            {
                // Wipe the whole block.
                await client.ClearUnitExecutionAsync(unitId, ct);

                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                    {
                        unit = unitId,
                        image = (string?)null,
                        runtime = (string?)null,
                        tool = (string?)null,
                        provider = (string?)null,
                        model = (string?)null,
                    }));
                }
                else
                {
                    Console.WriteLine($"Unit '{unitId}' execution block cleared.");
                }
                return;
            }

            // Per-field clear: read current shape, rewrite every slot,
            // omitting the one the caller asked to clear. The server's
            // partial-update semantics require we resubmit the others so
            // the store doesn't drop them silently (a null field on PUT
            // means "leave alone", not "clear"). When every remaining
            // slot is null after this operation we fall through to the
            // full block-clear.
            var current = await client.GetUnitExecutionAsync(unitId, ct);
            var updated = new UnitExecutionResponse
            {
                Image = string.Equals(field, "image", StringComparison.OrdinalIgnoreCase) ? null : current.Image,
                Runtime = string.Equals(field, "runtime", StringComparison.OrdinalIgnoreCase) ? null : current.Runtime,
                Tool = string.Equals(field, "tool", StringComparison.OrdinalIgnoreCase) ? null : current.Tool,
                Provider = string.Equals(field, "provider", StringComparison.OrdinalIgnoreCase) ? null : current.Provider,
                Model = string.Equals(field, "model", StringComparison.OrdinalIgnoreCase) ? null : current.Model,
            };

            if (string.IsNullOrWhiteSpace(updated.Image) && string.IsNullOrWhiteSpace(updated.Runtime)
                && string.IsNullOrWhiteSpace(updated.Tool) && string.IsNullOrWhiteSpace(updated.Provider)
                && string.IsNullOrWhiteSpace(updated.Model))
            {
                // Remaining state after per-field clear is empty → strip
                // the block entirely so we don't persist an empty object.
                await client.ClearUnitExecutionAsync(unitId, ct);
            }
            else
            {
                // Clear by DELETEing and re-PUTing — the store's partial
                // update cannot distinguish "leave alone" from "clear"
                // for a single field.
                await client.ClearUnitExecutionAsync(unitId, ct);
                await client.SetUnitExecutionAsync(unitId, updated, ct);
            }

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                {
                    unit = unitId,
                    image = updated.Image,
                    runtime = updated.Runtime,
                    tool = updated.Tool,
                    provider = updated.Provider,
                    model = updated.Model,
                }));
            }
            else
            {
                Console.WriteLine($"Unit '{unitId}' execution.{field} cleared.");
            }
        });

        return command;
    }
}