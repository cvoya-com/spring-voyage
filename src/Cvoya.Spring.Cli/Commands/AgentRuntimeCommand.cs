// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;

/// <summary>
/// Builds the <c>spring agent-runtime</c> verb family (#688 / #674
/// Phase 3). CLI-only admin surface per the #674 carve-out — the
/// portal may expose read-only views, but mutation goes through these
/// verbs.
/// </summary>
public static class AgentRuntimeCommand
{
    private static readonly OutputFormatter.Column<InstalledAgentRuntimeResponse>[] ListColumns =
    {
        new("id", r => r.Id),
        new("displayName", r => r.DisplayName),
        new("toolKind", r => r.ToolKind),
        new("defaultModel", r => r.DefaultModel),
        new("models", r => r.Models is null ? "" : string.Join(",", r.Models)),
    };

    private static readonly OutputFormatter.Column<AgentRuntimeModelResponse>[] ModelColumns =
    {
        new("id", m => m.Id),
        new("displayName", m => m.DisplayName),
        new("contextWindow", m => m.ContextWindow?.ToString()),
    };

    /// <summary>
    /// Creates the <c>agent-runtime</c> command root with list / show /
    /// install / uninstall / models / config / credentials /
    /// verify-baseline subcommands.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var root = new Command("agent-runtime", "Manage tenant-scoped agent runtime installs");
        root.Subcommands.Add(CreateListCommand(outputOption));
        root.Subcommands.Add(CreateShowCommand(outputOption));
        root.Subcommands.Add(CreateInstallCommand(outputOption));
        root.Subcommands.Add(CreateUninstallCommand());
        root.Subcommands.Add(CreateModelsCommand(outputOption));
        root.Subcommands.Add(CreateConfigCommand(outputOption));
        root.Subcommands.Add(CreateCredentialsCommand(outputOption));
        root.Subcommands.Add(CreateVerifyBaselineCommand(outputOption));
        return root;
    }

    private static Command CreateListCommand(Option<string> outputOption)
    {
        // Example: `spring agent-runtime list -o json`
        var command = new Command(
            "list",
            "List every agent runtime installed on the current tenant. Mirrors the tenant installs surface the wizard reads.");
        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            var result = await client.ListAgentRuntimesAsync(ct);
            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(result, ListColumns));
        });
        return command;
    }

    private static Command CreateShowCommand(Option<string> outputOption)
    {
        // Example: `spring agent-runtime show claude`
        var idArg = new Argument<string>("id")
        {
            Description = "Runtime id (e.g. 'claude', 'openai').",
        };
        var command = new Command("show", "Show an installed runtime's metadata and configured models.");
        command.Arguments.Add(idArg);
        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            var result = await client.GetAgentRuntimeAsync(id, ct);
            if (result is null)
            {
                await Console.Error.WriteLineAsync(
                    $"Runtime '{id}' is not installed on the current tenant. Run 'spring agent-runtime install {id}' first.");
                Environment.Exit(1);
                return;
            }
            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(new[] { result }, ListColumns));
        });
        return command;
    }

    private static Command CreateInstallCommand(Option<string> outputOption)
    {
        // Example: `spring agent-runtime install claude --model claude-sonnet-4-5 --model claude-opus-4-1`
        var idArg = new Argument<string>("id") { Description = "Runtime id to install." };
        var modelOption = new Option<string[]>("--model")
        {
            Description = "Seed the install with this model id. Repeatable (first value becomes --default-model if that flag is absent).",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = true,
        };
        var defaultModelOption = new Option<string?>("--default-model")
        {
            Description = "Preferred model id the wizard should pre-select.",
        };
        var baseUrlOption = new Option<string?>("--base-url")
        {
            Description = "Optional base URL override (Ollama / OpenAI-compatible gateways).",
        };

        var command = new Command(
            "install",
            "Install (or refresh) the runtime on the current tenant. Idempotent — re-running preserves operator-edited config unless flags override it.");
        command.Arguments.Add(idArg);
        command.Options.Add(modelOption);
        command.Options.Add(defaultModelOption);
        command.Options.Add(baseUrlOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var models = parseResult.GetValue(modelOption);
            var defaultModel = parseResult.GetValue(defaultModelOption);
            var baseUrl = parseResult.GetValue(baseUrlOption);
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            try
            {
                var result = await client.InstallAgentRuntimeAsync(
                    id,
                    models is { Length: > 0 } ? models : null,
                    defaultModel,
                    baseUrl,
                    ct);
                Console.WriteLine(output == "json"
                    ? OutputFormatter.FormatJson(result)
                    : OutputFormatter.FormatTable(new[] { result }, ListColumns));
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
            {
                await Console.Error.WriteLineAsync(
                    $"Runtime '{id}' is not registered with the host. Supported ids are listed by the runtime packages registered in Program.cs.");
                Environment.Exit(1);
            }
        });
        return command;
    }

    private static Command CreateUninstallCommand()
    {
        // Example: `spring agent-runtime uninstall claude --force`
        var idArg = new Argument<string>("id") { Description = "Runtime id to uninstall." };
        var forceOption = new Option<bool>("--force")
        {
            Description = "Skip the confirmation prompt.",
        };
        var command = new Command("uninstall", "Uninstall the runtime from the current tenant.");
        command.Arguments.Add(idArg);
        command.Options.Add(forceOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var force = parseResult.GetValue(forceOption);
            if (!force)
            {
                Console.Write($"Uninstall runtime '{id}' from the current tenant? [y/N]: ");
                var answer = Console.ReadLine();
                if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
                {
                    await Console.Error.WriteLineAsync("Uninstall cancelled.");
                    Environment.Exit(1);
                    return;
                }
            }
            var client = ClientFactory.Create();
            await client.UninstallAgentRuntimeAsync(id, ct);
            Console.WriteLine($"Uninstalled runtime '{id}'.");
        });
        return command;
    }

    private static Command CreateModelsCommand(Option<string> outputOption)
    {
        // `spring agent-runtime models` gets its own verb tree because
        // the model list is the most-edited piece of runtime config and
        // set/add/remove sugar is worth the surface.
        var root = new Command("models", "Manage the tenant's configured model list for an installed runtime.");
        root.Subcommands.Add(CreateModelsListCommand(outputOption));
        root.Subcommands.Add(CreateModelsSetCommand(outputOption));
        root.Subcommands.Add(CreateModelsAddCommand(outputOption));
        root.Subcommands.Add(CreateModelsRemoveCommand(outputOption));
        return root;
    }

    private static Command CreateModelsListCommand(Option<string> outputOption)
    {
        var idArg = new Argument<string>("id") { Description = "Runtime id." };
        var command = new Command("list", "List the tenant's configured models for a runtime.");
        command.Arguments.Add(idArg);
        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            var result = await client.GetAgentRuntimeModelsAsync(id, ct);
            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(result, ModelColumns));
        });
        return command;
    }

    private static Command CreateModelsSetCommand(Option<string> outputOption)
    {
        // Example: `spring agent-runtime models set claude claude-sonnet-4-5,claude-opus-4-1`
        var idArg = new Argument<string>("id") { Description = "Runtime id." };
        var modelsArg = new Argument<string>("models")
        {
            Description = "Comma-separated model ids to persist as the tenant's list. Replaces the existing list.",
        };
        var command = new Command("set", "Replace the tenant's configured model list for a runtime.");
        command.Arguments.Add(idArg);
        command.Arguments.Add(modelsArg);
        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var models = SplitCsv(parseResult.GetValue(modelsArg)!);
            await PatchModelsAsync(id, _ => models, parseResult.GetValue(outputOption) ?? "table", ct);
        });
        return command;
    }

    private static Command CreateModelsAddCommand(Option<string> outputOption)
    {
        var idArg = new Argument<string>("id") { Description = "Runtime id." };
        var modelArg = new Argument<string>("model") { Description = "Model id to append." };
        var command = new Command("add", "Append a model id to the tenant's configured list. No-op if already present.");
        command.Arguments.Add(idArg);
        command.Arguments.Add(modelArg);
        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var model = parseResult.GetValue(modelArg)!;
            await PatchModelsAsync(id, existing =>
            {
                var set = new List<string>(existing);
                if (!set.Contains(model, StringComparer.OrdinalIgnoreCase))
                {
                    set.Add(model);
                }
                return set;
            }, parseResult.GetValue(outputOption) ?? "table", ct);
        });
        return command;
    }

    private static Command CreateModelsRemoveCommand(Option<string> outputOption)
    {
        var idArg = new Argument<string>("id") { Description = "Runtime id." };
        var modelArg = new Argument<string>("model") { Description = "Model id to remove." };
        var command = new Command("remove", "Remove a model id from the tenant's configured list.");
        command.Arguments.Add(idArg);
        command.Arguments.Add(modelArg);
        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var model = parseResult.GetValue(modelArg)!;
            await PatchModelsAsync(id, existing =>
                existing.Where(m => !string.Equals(m, model, StringComparison.OrdinalIgnoreCase)).ToList(),
                parseResult.GetValue(outputOption) ?? "table", ct);
        });
        return command;
    }

    private static Command CreateConfigCommand(Option<string> outputOption)
    {
        var root = new Command("config", "Tenant-scoped runtime configuration (default model, base URL).");
        root.Subcommands.Add(CreateConfigSetCommand(outputOption));
        return root;
    }

    private static Command CreateConfigSetCommand(Option<string> outputOption)
    {
        // Example: `spring agent-runtime config set claude defaultModel=claude-sonnet-4-5`
        var idArg = new Argument<string>("id") { Description = "Runtime id." };
        var kvArg = new Argument<string>("key=value")
        {
            Description = "Supported keys: 'defaultModel', 'baseUrl'. Empty value clears the field.",
        };
        var command = new Command("set", "Set a single config field on an installed runtime.");
        command.Arguments.Add(idArg);
        command.Arguments.Add(kvArg);
        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var kv = parseResult.GetValue(kvArg)!;
            var (key, value) = SplitKeyValue(kv);
            var client = ClientFactory.Create();
            var existing = await client.GetAgentRuntimeAsync(id, ct)
                ?? throw new InvalidOperationException($"Runtime '{id}' is not installed.");
            var models = existing.Models?.ToArray() ?? Array.Empty<string>();
            var defaultModel = existing.DefaultModel;
            var baseUrl = existing.BaseUrl;
            switch (key.ToLowerInvariant())
            {
                case "defaultmodel":
                    defaultModel = string.IsNullOrWhiteSpace(value) ? null : value;
                    break;
                case "baseurl":
                    baseUrl = string.IsNullOrWhiteSpace(value) ? null : value;
                    break;
                default:
                    await Console.Error.WriteLineAsync(
                        $"Unknown config key '{key}'. Supported: defaultModel, baseUrl. Use 'spring agent-runtime models set' to rewrite the model list.");
                    Environment.Exit(1);
                    return;
            }
            var result = await client.UpdateAgentRuntimeConfigAsync(id, models, defaultModel, baseUrl, ct);
            var output = parseResult.GetValue(outputOption) ?? "table";
            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(new[] { result }, ListColumns));
        });
        return command;
    }

    private static Command CreateCredentialsCommand(Option<string> outputOption)
    {
        var root = new Command("credentials", "Read credential-health state for an installed runtime.");
        root.Subcommands.Add(CreateCredentialsStatusCommand(outputOption));
        return root;
    }

    private static Command CreateCredentialsStatusCommand(Option<string> outputOption)
    {
        // Example: `spring agent-runtime credentials status claude`
        var idArg = new Argument<string>("id") { Description = "Runtime id." };
        var secretOption = new Option<string?>("--secret-name")
        {
            Description = "Secret name within the runtime (defaults to 'default'). Multi-credential runtimes store one row per credential.",
        };
        var command = new Command(
            "status",
            "Show the current credential-health status for a runtime. Sourced from the shared credential_health store (#686).");
        command.Arguments.Add(idArg);
        command.Options.Add(secretOption);
        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var secretName = parseResult.GetValue(secretOption);
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            var result = await client.GetAgentRuntimeCredentialHealthAsync(id, secretName, ct);
            if (result is null)
            {
                await Console.Error.WriteLineAsync(
                    $"No credential-health row recorded for runtime '{id}'. Run 'spring agent-runtime ... validate-credential' (or use the portal) to prime the row.");
                Environment.Exit(1);
                return;
            }
            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : $"{result.SubjectId} / {result.SecretName} → {result.Status} (last checked {result.LastChecked:u})"
                    + (string.IsNullOrWhiteSpace(result.LastError) ? "" : $"\n  reason: {result.LastError}"));
        });
        return command;
    }

    private static Command CreateVerifyBaselineCommand(Option<string> outputOption)
    {
        // Example: `spring agent-runtime verify-baseline claude`
        var idArg = new Argument<string>("id") { Description = "Runtime id." };
        var command = new Command(
            "verify-baseline",
            "Invoke the runtime's container-baseline check (e.g. 'claude' CLI on PATH) and print the result.");
        command.Arguments.Add(idArg);
        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            try
            {
                var result = await client.VerifyAgentRuntimeBaselineAsync(id, ct);
                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJson(result));
                    return;
                }
                Console.WriteLine(result.Passed == true
                    ? $"Runtime '{id}' baseline: OK"
                    : $"Runtime '{id}' baseline: FAILED");
                if (result.Errors is { Count: > 0 })
                {
                    foreach (var err in result.Errors)
                    {
                        Console.WriteLine($"  - {err}");
                    }
                }
                if (result.Passed != true)
                {
                    Environment.Exit(1);
                }
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
            {
                await Console.Error.WriteLineAsync(
                    $"Runtime '{id}' is not registered with the host.");
                Environment.Exit(1);
            }
        });
        return command;
    }

    private static async Task PatchModelsAsync(
        string id,
        Func<IReadOnlyList<string>, IReadOnlyList<string>> mutate,
        string output,
        CancellationToken ct)
    {
        var client = ClientFactory.Create();
        var existing = await client.GetAgentRuntimeAsync(id, ct)
            ?? throw new InvalidOperationException($"Runtime '{id}' is not installed.");
        var nextModels = mutate(existing.Models ?? new List<string>());
        var result = await client.UpdateAgentRuntimeConfigAsync(
            id,
            nextModels,
            existing.DefaultModel,
            existing.BaseUrl,
            ct);
        Console.WriteLine(output == "json"
            ? OutputFormatter.FormatJson(result)
            : OutputFormatter.FormatTable(new[] { result }, ListColumns));
    }

    private static IReadOnlyList<string> SplitCsv(string csv)
        => csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static (string Key, string Value) SplitKeyValue(string kv)
    {
        var eq = kv.IndexOf('=');
        if (eq < 0)
        {
            throw new ArgumentException($"Expected key=value, got '{kv}'.");
        }
        return (kv[..eq].Trim(), kv[(eq + 1)..].Trim());
    }
}