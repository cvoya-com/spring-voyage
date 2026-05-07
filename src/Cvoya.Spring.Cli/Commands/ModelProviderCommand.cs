// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Generated.Api.V1.Tenant.ModelProviders.Installs.Item.Install;
using Cvoya.Spring.Cli.Generated.Api.V1.Tenant.ModelProviders.Installs.Item.RefreshModels;
using Cvoya.Spring.Cli.Generated.Api.V1.Tenant.ModelProviders.Installs.Item.ValidateCredential;
using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;

/// <summary>
/// Builds the <c>spring model-provider</c> verb tree (ADR-0038 PR-2).
/// Replaces the legacy <c>spring agent-runtime</c> tree wholesale —
/// ADR-0038 §7 makes the rejection strict: there is no
/// <c>agent-runtime</c> alias. Operators who type the old verb get the
/// parser's standard "unknown command" error.
/// </summary>
/// <remarks>
/// CLI-only admin surface per <c>CONVENTIONS.md § "UI / CLI Feature
/// Parity"</c> operator carve-out — the portal exposes read-only views,
/// every mutation goes through these verbs.
/// </remarks>
public static class ModelProviderCommand
{
    /// <summary>
    /// Stderr message emitted when <c>spring model-provider credentials
    /// status &lt;id&gt;</c> is run against a provider that has no
    /// credential-health row recorded yet. Exposed as <c>internal</c>
    /// so the unit tests in <c>tests/Cvoya.Spring.Cli.Tests</c> can pin
    /// the wording — the message points operators at the matching
    /// <c>validate-credential</c> verb.
    /// </summary>
    /// <remarks>
    /// The <c>{0}</c> placeholder is the provider id supplied on the
    /// command line — substituted via <see cref="string.Format(string, object?)"/>
    /// at the call site. Keep the placeholder explicit so call sites
    /// don't accidentally interpolate without it and silently lose the
    /// id from the message.
    /// </remarks>
    public const string CredentialsStatusMissingRowHintFormat =
        "No credential-health row recorded for model provider '{0}'. " +
        "Run 'spring model-provider validate-credential {0} --credential <key>' " +
        "(or use the portal at /settings/model-providers) to prime the row.";

    private static readonly OutputFormatter.Column<InstalledModelProviderResponse>[] ListColumns =
    {
        new("id", r => r.Id),
        new("displayName", r => r.DisplayName),
        new("defaultModel", r => r.DefaultModel),
        new("models", r => r.Models is null ? "" : string.Join(",", r.Models)),
    };

    private static readonly OutputFormatter.Column<ModelProviderModelResponse>[] ModelColumns =
    {
        new("id", m => m.Id),
        new("displayName", m => m.DisplayName),
        new("contextWindow", m => m.ContextWindow?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty),
    };

    /// <summary>
    /// Creates the <c>model-provider</c> command root with list / show /
    /// install / uninstall / config / credentials / validate-credential /
    /// refresh-models subcommands.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var root = new Command(
            "model-provider",
            "Manage tenant-scoped model-provider installs (ADR-0038).");
        root.Subcommands.Add(CreateListCommand(outputOption));
        root.Subcommands.Add(CreateShowCommand(outputOption));
        root.Subcommands.Add(CreateInstallCommand(outputOption));
        root.Subcommands.Add(CreateUninstallCommand());
        root.Subcommands.Add(CreateConfigCommand(outputOption));
        root.Subcommands.Add(CreateCredentialsCommand(outputOption));
        root.Subcommands.Add(CreateValidateCredentialCommand(outputOption));
        root.Subcommands.Add(CreateRefreshModelsCommand(outputOption));
        return root;
    }

    private static Command CreateListCommand(Option<string> outputOption)
    {
        // Example: `spring model-provider list -o json`
        var command = new Command(
            "list",
            "List every model provider installed on the current tenant.");
        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            var result = await client.ListModelProvidersAsync(ct);
            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(result, ListColumns));
        });
        return command;
    }

    private static Command CreateShowCommand(Option<string> outputOption)
    {
        // Example: `spring model-provider show anthropic`
        var idArg = new Argument<string>("id")
        {
            Description = "Provider id (e.g. 'anthropic', 'openai', 'google', 'ollama').",
        };
        var command = new Command(
            "show",
            "Show an installed provider's metadata and configured models.");
        command.Arguments.Add(idArg);
        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            var result = await client.GetModelProviderAsync(id, ct);
            if (result is null)
            {
                await Console.Error.WriteLineAsync(
                    $"Provider '{id}' is not installed on the current tenant. Run 'spring model-provider install {id}' first.");
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
        // Example: `spring model-provider install anthropic --model claude-opus-4-7`
        var idArg = new Argument<string>("id") { Description = "Provider id to install." };
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
            "Install (or refresh) the model provider on the current tenant. " +
            "Idempotent — re-running preserves operator-edited config unless flags override it.");
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
                var result = await client.InstallModelProviderAsync(
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
                    $"Provider '{id}' is not registered with the host. Supported provider ids are listed in platform/runtime-catalog.yaml.");
                Environment.Exit(1);
            }
        });
        return command;
    }

    private static Command CreateUninstallCommand()
    {
        // Example: `spring model-provider uninstall anthropic --force`
        var idArg = new Argument<string>("id") { Description = "Provider id to uninstall." };
        var forceOption = new Option<bool>("--force")
        {
            Description = "Skip the confirmation prompt.",
        };
        var command = new Command("uninstall", "Uninstall the provider from the current tenant.");
        command.Arguments.Add(idArg);
        command.Options.Add(forceOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var force = parseResult.GetValue(forceOption);
            if (!force)
            {
                Console.Write($"Uninstall provider '{id}' from the current tenant? [y/N]: ");
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
            await client.UninstallModelProviderAsync(id, ct);
            Console.WriteLine($"Uninstalled provider '{id}'.");
        });
        return command;
    }

    private static Command CreateConfigCommand(Option<string> outputOption)
    {
        var root = new Command(
            "config",
            "Tenant-scoped provider configuration (default model, base URL, model list).");
        root.Subcommands.Add(CreateConfigGetCommand(outputOption));
        root.Subcommands.Add(CreateConfigSetCommand(outputOption));
        return root;
    }

    private static Command CreateConfigGetCommand(Option<string> outputOption)
    {
        // Read-only sibling of `config set` that renders ONLY the
        // configurable fields (default-model / base-URL / models). The
        // existing `model-provider show` command renders the full install
        // metadata table, which is noisy when an operator only wants to
        // confirm the live config slot before/after a `config set`.
        var idArg = new Argument<string>("id") { Description = "Provider id." };
        var command = new Command(
            "get",
            "Show the tenant-scoped configuration slot for an installed provider " +
            "(default-model / base-URL / models). Lighter-weight read counterpart to " +
            "'model-provider show'.");
        command.Arguments.Add(idArg);
        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            var config = await client.GetModelProviderConfigAsync(id, ct);
            if (config is null)
            {
                await Console.Error.WriteLineAsync(
                    $"Provider '{id}' is not installed on the current tenant. Run 'spring model-provider install {id}' first.");
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
        // Example: `spring model-provider config set anthropic defaultModel=claude-opus-4-7`
        var idArg = new Argument<string>("id") { Description = "Provider id." };
        var kvArg = new Argument<string>("key=value")
        {
            Description = "Supported keys: 'defaultModel', 'baseUrl', 'models' (comma-separated). Empty value clears the field.",
        };
        var command = new Command(
            "set",
            "Set a single config field on an installed provider (default-model / base-URL / models).");
        command.Arguments.Add(idArg);
        command.Arguments.Add(kvArg);
        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var kv = parseResult.GetValue(kvArg)!;
            var (key, value) = SplitKeyValue(kv);
            var client = ClientFactory.Create();
            var existing = await client.GetModelProviderAsync(id, ct)
                ?? throw new InvalidOperationException($"Provider '{id}' is not installed.");
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
                case "models":
                    models = SplitCsv(value).ToArray();
                    break;
                default:
                    await Console.Error.WriteLineAsync(
                        $"Unknown config key '{key}'. Supported: defaultModel, baseUrl, models.");
                    Environment.Exit(1);
                    return;
            }
            var result = await client.UpdateModelProviderConfigAsync(id, models, defaultModel, baseUrl, ct);
            var output = parseResult.GetValue(outputOption) ?? "table";
            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(new[] { result }, ListColumns));
        });
        return command;
    }

    private static Command CreateCredentialsCommand(Option<string> outputOption)
    {
        var root = new Command("credentials", "Read credential-health state for an installed provider.");
        root.Subcommands.Add(CreateCredentialsStatusCommand(outputOption));
        return root;
    }

    private static Command CreateCredentialsStatusCommand(Option<string> outputOption)
    {
        // Example: `spring model-provider credentials status anthropic`
        var idArg = new Argument<string>("id") { Description = "Provider id." };
        var secretOption = new Option<string?>("--secret-name")
        {
            Description = "Secret name within the provider (defaults to 'default'). " +
                "Multi-credential providers store one row per credential.",
        };
        var command = new Command(
            "status",
            "Show the current credential-health status for a provider. " +
            "Sourced from the shared credential_health store.");
        command.Arguments.Add(idArg);
        command.Options.Add(secretOption);
        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var secretName = parseResult.GetValue(secretOption);
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            var result = await client.GetModelProviderCredentialHealthAsync(id, secretName, ct);
            if (result is null)
            {
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

    private static Command CreateValidateCredentialCommand(Option<string> outputOption)
    {
        // Probe verb. Distinct from `refresh-models` in two ways:
        //  1. It does NOT touch the tenant's stored model list. The host
        //     endpoint records the outcome in the credential_health store
        //     ONLY — the model catalogue is the responsibility of
        //     `refresh-models`.
        //  2. The success-vs-failure axis is the response body's `Ok`
        //     field, not the HTTP status. A 200 OK with `Ok=false` (i.e.
        //     the provider rejected the credential) still results in a
        //     non-zero exit so scripts can distinguish "could not reach
        //     the host" from "host reached, credential rejected".
        //
        // Examples:
        //   spring model-provider validate-credential anthropic --credential sk-ant-api-...
        //   spring model-provider validate-credential ollama                    # no credential needed
        var idArg = new Argument<string>("id")
        {
            Description = "Provider id to probe (e.g. 'anthropic', 'openai', 'google', 'ollama').",
        };
        var credentialOption = new Option<string?>("--credential")
        {
            Description =
                "Credential to present to the backing service for the probe. " +
                "Omit for credential-less providers (e.g. local Ollama).",
        };
        var secretNameOption = new Option<string?>("--secret-name")
        {
            Description =
                "Secret name slot for the credential-health row (defaults to 'default'). " +
                "Multi-credential providers track one row per secret name.",
        };
        var command = new Command(
            "validate-credential",
            "Probe the provider with the supplied credential and update the credential-health row. " +
            "Does NOT rotate the model catalogue.");
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
                var result = await client.ValidateModelProviderCredentialAsync(id, credential, secretName, ct);
                if (string.Equals(output, "json", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(OutputFormatter.FormatJson(result));
                }
                else if (result.Ok == true)
                {
                    var when = result.ValidatedAt?.ToString("u") ?? "unknown time";
                    Console.WriteLine($"Credential for provider '{id}' is valid (validated at {when}).");
                }
                else
                {
                    var detail = string.IsNullOrWhiteSpace(result.Detail)
                        ? "no detail provided by the provider"
                        : result.Detail!;
                    await Console.Error.WriteLineAsync(
                        $"Credential for provider '{id}' was not accepted: {detail}");
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
                    $"Provider '{id}' is not registered with the host or is not installed on the current tenant. " +
                    $"Run 'spring model-provider install {id}' first.");
                Environment.Exit(1);
            }
            // Non-404 ApiExceptions escape to Program.Main where the
            // central ApiExceptionRenderer emits a status-aware envelope.
        });
        return command;
    }

    private static Command CreateRefreshModelsCommand(Option<string> outputOption)
    {
        // Examples:
        //   spring model-provider refresh-models anthropic --credential sk-ant-api-...
        //   spring model-provider refresh-models openai --credential sk-proj-...
        //   spring model-provider refresh-models ollama                    # no credential needed
        //
        // Replaces the tenant's configured model list with the live
        // catalogue published by the provider's /v1/models endpoint (or
        // equivalent).
        var idArg = new Argument<string>("id")
        {
            Description = "Provider id to refresh (e.g. 'anthropic', 'openai', 'google', 'ollama').",
        };
        var credentialOption = new Option<string?>("--credential")
        {
            Description =
                "Credential to present to the backing service for the live catalogue lookup. " +
                "Omit for credential-less providers (e.g. local Ollama).",
        };
        var command = new Command(
            "refresh-models",
            "Fetch the live model catalogue from the provider and replace the tenant's configured model list with it.");
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
                var result = await client.RefreshModelProviderModelsAsync(id, credential, ct);
                Console.WriteLine(output == "json"
                    ? OutputFormatter.FormatJson(result)
                    : OutputFormatter.FormatTable(new[] { result }, ListColumns));
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
            {
                await Console.Error.WriteLineAsync(
                    $"Provider '{id}' is not installed on the current tenant, or is not registered with the host. " +
                    $"Run 'spring model-provider install {id}' first.");
                Environment.Exit(1);
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 401)
            {
                await Console.Error.WriteLineAsync(
                    $"The provider rejected the supplied credential for '{id}'. Supply --credential with a live key.");
                Environment.Exit(1);
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 502)
            {
                await Console.Error.WriteLineAsync(
                    $"Could not refresh '{id}' — the provider did not return a live model catalogue. " +
                    "The provider may not expose /v1/models, or the backing service is unreachable.");
                Environment.Exit(1);
            }
        });
        return command;
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