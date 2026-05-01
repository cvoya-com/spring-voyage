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
    /// <summary>
    /// Stderr message emitted when <c>spring agent-runtime credentials
    /// status &lt;id&gt;</c> is run against a runtime that has no
    /// credential-health row recorded yet. Exposed as <c>internal</c>
    /// (rather than living inline at the call site) so the unit tests
    /// in <c>tests/Cvoya.Spring.Cli.Tests</c> can assert the wording —
    /// the previous text pointed operators at a non-existent
    /// <c>... validate-credential</c> verb (#1066), and the regression
    /// check guards against drift.
    /// </summary>
    /// <remarks>
    /// The <c>{0}</c> placeholder is the runtime id supplied on the
    /// command line — substituted via <see cref="string.Format(string, object?)"/>
    /// at the call site. Keep the placeholder explicit so call sites
    /// don't accidentally interpolate without it and silently lose the
    /// runtime id from the message.
    /// </remarks>
    public const string CredentialsStatusMissingRowHintFormat =
        "No credential-health row recorded for runtime '{0}'. " +
        "Run 'spring agent-runtime validate-credential {0} --credential <key>' " +
        "(or use the portal at /settings/agent-runtimes) to prime the row.";

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
        new("contextWindow", m => m.ContextWindow?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty),
    };

    /// <summary>
    /// Creates the <c>agent-runtime</c> command root with list / show /
    /// install / uninstall / models / config / credentials /
    /// refresh-models subcommands.
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
        root.Subcommands.Add(CreateRefreshModelsCommand(outputOption));
        root.Subcommands.Add(CreateValidateCredentialCommand(outputOption));
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
        // Example: `spring agent-runtime install claude --model claude-opus-4-7 --model claude-sonnet-4-6`
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
        // Example: `spring agent-runtime models set claude claude-opus-4-7,claude-sonnet-4-6,claude-haiku-4-5`
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
        root.Subcommands.Add(CreateConfigGetCommand(outputOption));
        root.Subcommands.Add(CreateConfigSetCommand(outputOption));
        return root;
    }

    private static Command CreateConfigGetCommand(Option<string> outputOption)
    {
        // #1066: read-only sibling of `config set` that renders ONLY the
        // configurable fields (default-model / base-URL / models). The
        // existing `agent-runtime show` command renders the full install
        // metadata table, which is noisy when an operator only wants to
        // confirm the live config slot before/after a `config set`.
        var idArg = new Argument<string>("id") { Description = "Runtime id." };
        var command = new Command(
            "get",
            "Show the tenant-scoped configuration slot for an installed runtime (default-model / base-URL / models). Lighter-weight read counterpart to 'agent-runtime show'.");
        command.Arguments.Add(idArg);
        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            var config = await client.GetAgentRuntimeConfigAsync(id, ct);
            if (config is null)
            {
                await Console.Error.WriteLineAsync(
                    $"Runtime '{id}' is not installed on the current tenant. Run 'spring agent-runtime install {id}' first.");
                Environment.Exit(1);
                return;
            }

            if (string.Equals(output, "json", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(OutputFormatter.FormatJson(config));
                return;
            }

            // Prose: stable two-column key/value list (key, value) so the
            // shape matches `connector show` and operators can grep cleanly.
            // We deliberately render the model list as a comma-separated
            // value rather than its own table so `config get` stays a
            // single-block read.
            Console.WriteLine($"id            {config.Id}");
            Console.WriteLine($"defaultModel  {config.DefaultModel ?? "(none)"}");
            Console.WriteLine($"baseUrl       {config.BaseUrl ?? "(none)"}");
            Console.WriteLine(
                $"models        {(config.Models is { Count: > 0 } ? string.Join(",", config.Models) : "(none)")}");
        });
        return command;
    }

    private static Command CreateConfigSetCommand(Option<string> outputOption)
    {
        // Example: `spring agent-runtime config set claude defaultModel=claude-opus-4-7`
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
                // #1066: prior text pointed at a non-existent
                // `... validate-credential` subcommand. The new
                // `spring agent-runtime validate-credential <id>` verb shipped
                // alongside this fix primes the row without rotating the
                // model catalog (the previous workaround had to use
                // `refresh-models` which ALSO rewrites the tenant's stored
                // model list as a side-effect — distinctly worse for
                // operators who only wanted to confirm a credential).
                await Console.Error.WriteLineAsync(
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        CredentialsStatusMissingRowHintFormat,
                        id));
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

    private static Command CreateRefreshModelsCommand(Option<string> outputOption)
    {
        // Examples:
        //   spring agent-runtime refresh-models claude --credential sk-ant-api-...
        //   spring agent-runtime refresh-models openai --credential sk-proj-...
        //   spring agent-runtime refresh-models ollama                    # no credential needed
        //
        // Replaces the tenant's configured model list with the live
        // catalog published by the provider's /v1/models endpoint (or
        // equivalent). Closes #720 — supersedes the ad-hoc
        // refresh-script carried in #671 Part 2.
        var idArg = new Argument<string>("id")
        {
            Description = "Runtime id to refresh (e.g. 'claude', 'openai', 'google', 'ollama').",
        };
        var credentialOption = new Option<string?>("--credential")
        {
            Description =
                "Credential to present to the backing service for the live catalog lookup. " +
                "Omit for credential-less runtimes (e.g. local Ollama).",
        };
        var command = new Command(
            "refresh-models",
            "Fetch the live model catalog from the runtime's provider and replace the tenant's configured model list with it.");
        command.Arguments.Add(idArg);
        command.Options.Add(credentialOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var credential = parseResult.GetValue(credentialOption);
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            try
            {
                var result = await client.RefreshAgentRuntimeModelsAsync(id, credential, ct);
                Console.WriteLine(output == "json"
                    ? OutputFormatter.FormatJson(result)
                    : OutputFormatter.FormatTable(new[] { result }, ListColumns));
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
            {
                await Console.Error.WriteLineAsync(
                    $"Runtime '{id}' is not installed on the current tenant, or is not registered with the host. " +
                    $"Run 'spring agent-runtime install {id}' first.");
                Environment.Exit(1);
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 401)
            {
                await Console.Error.WriteLineAsync(
                    $"The provider rejected the supplied credential for runtime '{id}'. Supply --credential with a live key.");
                Environment.Exit(1);
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 502)
            {
                await Console.Error.WriteLineAsync(
                    $"Could not refresh '{id}' — the provider did not return a live model catalog. " +
                    "The runtime may not expose /v1/models, or the backing service is unreachable.");
                Environment.Exit(1);
            }
        });
        return command;
    }

    private static Command CreateValidateCredentialCommand(Option<string> outputOption)
    {
        // #1066: dedicated credential-probe verb. Distinct from
        // `refresh-models` in two important ways:
        //  1. It does NOT touch the tenant's stored model list. The host
        //     endpoint records the outcome in the credential_health store
        //     ONLY — the model catalog is the responsibility of
        //     `refresh-models`.
        //  2. The success-vs-failure axis is the response body's `Ok`
        //     field, not the HTTP status. A 200 OK with `Ok=false` (i.e.
        //     the provider rejected the credential) still results in a
        //     non-zero exit so scripts can distinguish "could not reach
        //     the host" from "host reached, credential rejected".
        //
        // Examples:
        //   spring agent-runtime validate-credential claude --credential sk-ant-api-...
        //   spring agent-runtime validate-credential ollama                    # no credential needed
        var idArg = new Argument<string>("id")
        {
            Description = "Runtime id to probe (e.g. 'claude', 'openai', 'google', 'ollama').",
        };
        var credentialOption = new Option<string?>("--credential")
        {
            Description =
                "Credential to present to the backing service for the probe. " +
                "Omit for credential-less runtimes (e.g. local Ollama).",
        };
        var secretNameOption = new Option<string?>("--secret-name")
        {
            Description =
                "Secret name slot for the credential-health row (defaults to 'default'). " +
                "Multi-credential runtimes track one row per secret name.",
        };
        var command = new Command(
            "validate-credential",
            "Probe the runtime with the supplied credential and update the credential-health row. Does NOT rotate the model catalog (#1066).");
        command.Arguments.Add(idArg);
        command.Options.Add(credentialOption);
        command.Options.Add(secretNameOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var credential = parseResult.GetValue(credentialOption);
            var secretName = parseResult.GetValue(secretNameOption);
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            try
            {
                var result = await client.ValidateAgentRuntimeCredentialAsync(id, credential, secretName, ct);
                if (string.Equals(output, "json", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(OutputFormatter.FormatJson(result));
                }
                else if (result.Ok == true)
                {
                    var when = result.ValidatedAt?.ToString("u") ?? "unknown time";
                    Console.WriteLine($"Credential for runtime '{id}' is valid (validated at {when}).");
                }
                else
                {
                    var detail = string.IsNullOrWhiteSpace(result.Detail)
                        ? "no detail provided by the runtime"
                        : result.Detail!;
                    await Console.Error.WriteLineAsync(
                        $"Credential for runtime '{id}' was not accepted: {detail}");
                }

                // Non-zero exit when the credential isn't valid so scripts
                // can branch. We deliberately exit AFTER printing JSON so
                // callers using `spring ... -o json` still receive the full
                // payload before the non-zero exit.
                if (result.Ok != true)
                {
                    Environment.Exit(1);
                }
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
            {
                await Console.Error.WriteLineAsync(
                    $"Runtime '{id}' is not registered with the host or is not installed on the current tenant. " +
                    $"Run 'spring agent-runtime install {id}' first.");
                Environment.Exit(1);
            }
            // Non-404 ApiExceptions escape to Program.Main where the
            // central ApiExceptionRenderer (added in #1071) emits a
            // status-aware envelope. Per-call retries are not the right
            // place to second-guess that envelope.
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