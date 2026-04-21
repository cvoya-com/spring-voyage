// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;
using Cvoya.Spring.Cli.Utilities;

/// <summary>
/// Builds the <c>spring connector</c> verb family (#455 / Wave 1 CLI-parity
/// track). Mirrors the portal's connector chooser and unit Connector tab:
/// <list type="bullet">
///   <item><description><c>spring connector catalog</c> — list every registered connector type, matching the portal's connector list.</description></item>
///   <item><description><c>spring connector show --unit &lt;name&gt;</c> — show the unit's active binding plus connector-specific config (GitHub today).</description></item>
///   <item><description><c>spring connector bind --unit &lt;name&gt; --type &lt;type&gt;</c> — bind the unit to a connector and upsert its per-unit config.</description></item>
///   <item><description><c>spring connector bindings &lt;slugOrId&gt;</c> — list every unit bound to a connector type, mirroring the portal's <c>/connectors/{slug}</c> "Bound units" list (#520).</description></item>
/// </list>
/// </summary>
/// <remarks>
/// The CLI surfaces the same underlying service the portal consumes — the
/// generic <c>/api/v1/connectors</c> endpoint feeds both the CLI catalog
/// table and the portal's chooser. The <c>bind</c> verb targets the typed
/// per-connector PUT surface (today only GitHub ships a typed config PUT,
/// and that is exactly the connector the portal's unit Connector tab binds
/// through too — so the CLI stays at parity and rejects other <c>--type</c>
/// values with a clear message until their typed surfaces land).
/// </remarks>
public static class ConnectorCommand
{
    // #714: GET /api/v1/connectors now returns InstalledConnectorResponse
    // (tenant-installed connectors only). The CLI `catalog` verb prints the
    // same columns — slug/name/description — so the operator-facing table
    // shape is preserved across the pivot.
    private static readonly OutputFormatter.Column<InstalledConnectorResponse>[] CatalogColumns =
    {
        new("slug", c => c.TypeSlug),
        new("name", c => c.DisplayName),
        new("description", c => c.Description),
    };

    /// <summary>
    /// CLI-local row for the <c>show</c> verb. Joins the generic binding
    /// pointer with the typed config payload (for connectors the CLI knows
    /// how to decode) so the caller sees the full picture in one command —
    /// which is exactly what the portal's unit Connector tab does by
    /// merging the pointer response and the connector-specific
    /// <c>&lt;ConnectorCfg&gt;</c> panel.
    /// </summary>
    private sealed record ShowRow(
        string Unit,
        string? Slug,
        string? TypeId,
        string? ConfigUrl,
        string? ActionsBaseUrl,
        object? Config);

    private static readonly OutputFormatter.Column<ShowRow>[] ShowColumns =
    {
        new("unit", r => r.Unit),
        new("slug", r => r.Slug),
        new("typeId", r => r.TypeId),
    };

    private static readonly OutputFormatter.Column<ConnectorUnitBindingResponse>[] BindingsColumns =
    {
        new("unit", b => b.UnitName),
        new("displayName", b => b.UnitDisplayName),
        new("typeSlug", b => b.TypeSlug),
        new("typeId", b => b.TypeId?.ToString()),
    };

    /// <summary>
    /// Creates the <c>connector</c> command root with the catalog / show /
    /// bind subcommands.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var connectorCommand = new Command("connector", "Manage connector bindings for units");

        connectorCommand.Subcommands.Add(CreateCatalogCommand(outputOption));
        connectorCommand.Subcommands.Add(CreateUnitBindingCommand(outputOption));
        connectorCommand.Subcommands.Add(CreateBindCommand(outputOption));
        connectorCommand.Subcommands.Add(CreateBindingsCommand(outputOption));
        // #689 tenant-install verbs — sit alongside the per-unit binding
        // verbs above. Installs sit one level above unit bindings: a
        // connector must be installed on the tenant before any unit can
        // bind to it.
        connectorCommand.Subcommands.Add(CreateListInstalledCommand(outputOption));
        connectorCommand.Subcommands.Add(CreateShowInstallCommand(outputOption));
        connectorCommand.Subcommands.Add(CreateInstallCommand(outputOption));
        connectorCommand.Subcommands.Add(CreateUninstallCommand());
        connectorCommand.Subcommands.Add(CreateConfigCommand(outputOption));
        connectorCommand.Subcommands.Add(CreateCredentialsCommand(outputOption));

        return connectorCommand;
    }

    private static Command CreateBindingsCommand(Option<string> outputOption)
    {
        // Positional <slugOrId> keeps the verb ergonomic at the shell
        // (`spring connector bindings github`) and mirrors how the portal's
        // /connectors/{slug} detail page addresses the endpoint.
        var slugOrIdArg = new Argument<string>("slugOrId")
        {
            Description = "Connector type slug (e.g. 'github') or stable GUID type id. Matches the identifier 'spring connector catalog' prints.",
        };

        var command = new Command(
            "bindings",
            "List every unit bound to a connector type. Mirrors the portal's /connectors/{slug} 'Bound units' section so both surfaces round-trip the same data in one call.");
        command.Arguments.Add(slugOrIdArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var slugOrId = parseResult.GetValue(slugOrIdArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            try
            {
                var result = await client.ListConnectorBindingsAsync(slugOrId, ct);

                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJson(result));
                }
                else if (result.Count == 0)
                {
                    // Empty state mirrors the portal's "No units are
                    // currently bound to this connector" copy so the two
                    // surfaces converge on the same operator-facing
                    // guidance.
                    Console.WriteLine(
                        $"No units are currently bound to connector '{slugOrId}'.");
                }
                else
                {
                    Console.WriteLine(OutputFormatter.FormatTable(result, BindingsColumns));
                }
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
            {
                // The server returns 404 only when the slug/id resolves to no
                // registered connector; a connector that exists but has no
                // bindings returns an empty array, handled above.
                await Console.Error.WriteLineAsync(
                    $"Connector '{slugOrId}' is not registered. Run 'spring connector catalog' to see available types.");
                Environment.Exit(1);
            }
        });

        return command;
    }

    private static Command CreateCatalogCommand(Option<string> outputOption)
    {
        // Post-#714 `catalog` lists every connector installed on the
        // current tenant (same surface as `spring connector list`). A
        // connector registered with the host but not installed on the
        // tenant is invisible here and in the portal's connector chooser,
        // mirroring the agent-runtimes surface.
        var command = new Command(
            "catalog",
            "List every connector installed on the current tenant. Matches the data the web portal renders in its connector chooser.");

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var result = await client.ListConnectorsAsync(ct);

            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(result, CatalogColumns));
        });

        return command;
    }

    private static Command CreateUnitBindingCommand(Option<string> outputOption)
    {
        var unitOption = new Option<string>("--unit")
        {
            Description = "The unit name whose connector binding should be shown.",
            Required = true,
        };

        var command = new Command(
            "unit-binding",
            "Show the unit's active connector binding + config. Returns a 'no binding' message when the unit isn't wired to any connector. Renamed from `show` in #689 to free the `show` verb for tenant-install queries.");
        command.Options.Add(unitOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitOption)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var pointer = await client.GetUnitConnectorAsync(unitId, ct);
            if (pointer is null)
            {
                if (output == "json")
                {
                    // Emit a stable "no binding" shape so scripts can
                    // distinguish it from a CLI error without parsing
                    // stderr.
                    Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                    {
                        unit = unitId,
                        bound = false,
                    }));
                }
                else
                {
                    Console.WriteLine($"Unit '{unitId}' has no active connector binding.");
                }
                return;
            }

            // When the binding is a GitHub one we also pull the typed
            // config so `show` is a single-stop read — the portal's
            // Connector tab does the equivalent by rendering the typed
            // `<GitHubConnectorConfig>` panel alongside the binding
            // pointer.
            object? typedConfig = null;
            if (string.Equals(pointer.TypeSlug, "github", StringComparison.OrdinalIgnoreCase))
            {
                var config = await client.GetUnitGitHubConfigAsync(unitId, ct);
                if (config is not null)
                {
                    typedConfig = new
                    {
                        owner = config.Owner,
                        repo = config.Repo,
                        events = config.Events,
                        // AppInstallationId is an UntypedNode on the wire
                        // (the server accepts either a number or a
                        // string). Unwrap it so the CLI prints the
                        // underlying scalar rather than the
                        // UntypedInteger class name.
                        appInstallationId = UntypedNodeFormatter.FormatScalar(config.AppInstallationId),
                    };
                }
            }

            var row = new ShowRow(
                Unit: unitId,
                Slug: pointer.TypeSlug,
                TypeId: pointer.TypeId?.ToString(),
                ConfigUrl: pointer.ConfigUrl,
                ActionsBaseUrl: pointer.ActionsBaseUrl,
                Config: typedConfig);

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJsonPlain(row));
            }
            else
            {
                Console.WriteLine(OutputFormatter.FormatTable(row, ShowColumns));
                if (!string.IsNullOrEmpty(row.ConfigUrl))
                {
                    Console.WriteLine();
                    Console.WriteLine($"Config URL:      {row.ConfigUrl}");
                    Console.WriteLine($"Actions Base:    {row.ActionsBaseUrl}");
                }
                if (typedConfig is not null)
                {
                    Console.WriteLine();
                    Console.WriteLine("Config:");
                    Console.WriteLine(OutputFormatter.FormatJsonPlain(typedConfig));
                }
            }
        });

        return command;
    }

    private static Command CreateBindCommand(Option<string> outputOption)
    {
        var unitOption = new Option<string>("--unit")
        {
            Description = "The unit name to bind to a connector.",
            Required = true,
        };
        var typeOption = new Option<string>("--type")
        {
            Description = "The connector type slug (e.g. 'github'). Must match a slug reported by 'spring connector catalog'.",
            Required = true,
        };
        // GitHub-specific options below. They are declared on the generic
        // `bind` verb because `github` is the only connector type with a
        // typed PUT today — mirroring the portal's unit Connector tab,
        // which only ships a GitHub config form. Additional connectors
        // will either add their own flag sets here or land as dedicated
        // subcommands alongside their typed surfaces.
        var ownerOption = new Option<string?>("--owner")
        {
            Description = "GitHub repository owner (required when --type github).",
        };
        var repoOption = new Option<string?>("--repo")
        {
            Description = "GitHub repository name (required when --type github).",
        };
        var installationIdOption = new Option<string?>("--installation-id")
        {
            Description = "Optional GitHub App installation id. When omitted the server uses the App-level default.",
        };
        var eventsOption = new Option<string[]?>("--events")
        {
            Description = "Optional webhook events to subscribe to. Repeatable. When omitted the server falls back to the connector's default event set.",
            AllowMultipleArgumentsPerToken = true,
        };

        var command = new Command(
            "bind",
            "Bind a unit to a connector and upsert its per-unit config. Mirrors the portal's unit Connector tab.");
        command.Options.Add(unitOption);
        command.Options.Add(typeOption);
        command.Options.Add(ownerOption);
        command.Options.Add(repoOption);
        command.Options.Add(installationIdOption);
        command.Options.Add(eventsOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitOption)!;
            var type = parseResult.GetValue(typeOption)!;
            var owner = parseResult.GetValue(ownerOption);
            var repo = parseResult.GetValue(repoOption);
            var installationId = parseResult.GetValue(installationIdOption);
            var events = parseResult.GetValue(eventsOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (!string.Equals(type, "github", StringComparison.OrdinalIgnoreCase))
            {
                // We intentionally fail loud rather than silently binding
                // via the generic pointer surface — other connectors today
                // don't expose a typed PUT, and a generic bind wouldn't be
                // at parity with the portal (which also only binds GitHub
                // through its typed form).
                await Console.Error.WriteLineAsync(
                    $"Connector type '{type}' is not supported by 'spring connector bind' yet. " +
                    "Run 'spring connector catalog' to see available types; only 'github' has a typed bind surface today.");
                Environment.Exit(1);
                return;
            }

            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
            {
                await Console.Error.WriteLineAsync(
                    "--owner and --repo are required when --type github. Example: " +
                    "spring connector bind --unit eng --type github --owner acme --repo platform.");
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();

            try
            {
                var result = await client.PutUnitGitHubConfigAsync(
                    unitId,
                    owner!,
                    repo!,
                    installationId,
                    events,
                    ct);

                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJson(result));
                }
                else
                {
                    Console.WriteLine(
                        $"Unit '{unitId}' bound to connector 'github' ({result.Owner}/{result.Repo}).");
                }
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                // The server returns 400 with a problem+json body when the
                // binding request is malformed (e.g. unknown unit, invalid
                // events list). Surface the server's message verbatim so
                // the operator can fix the request without guessing.
                await Console.Error.WriteLineAsync(
                    $"Failed to bind unit '{unitId}' to connector '{type}': {ProblemDetailsFormatter.Format(ex)}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    // ---------- #689 tenant-install verbs ----------

    private static readonly OutputFormatter.Column<InstalledConnectorResponse>[] InstalledColumns =
    {
        new("slug", c => c.TypeSlug),
        new("name", c => c.DisplayName),
        new("installedAt", c => c.InstalledAt?.ToString("u")),
        new("updatedAt", c => c.UpdatedAt?.ToString("u")),
    };

    private static Command CreateListInstalledCommand(Option<string> outputOption)
    {
        // Example: `spring connector list -o json`. Post-#714 this shares
        // the underlying endpoint with the `catalog` verb — both now point
        // at tenant-installed connectors. `list` kept for muscle-memory
        // parity with `spring agent-runtime list`.
        var command = new Command(
            "list",
            "List every connector installed on the current tenant. Synonym of 'catalog' after the #714 pivot.");
        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            var result = await client.ListConnectorsAsync(ct);
            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(result, InstalledColumns));
        });
        return command;
    }

    private static Command CreateShowInstallCommand(Option<string> outputOption)
    {
        // Example: `spring connector show github`
        var idArg = new Argument<string>("slugOrId")
        {
            Description = "Connector slug (e.g. 'github') or stable GUID type id.",
        };
        var command = new Command("show", "Show the install metadata for a connector on the current tenant.");
        command.Arguments.Add(idArg);
        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var slugOrId = parseResult.GetValue(idArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            // Post-#714 the tenant install envelope rides on the pivoted
            // GET /api/v1/connectors/{slugOrId} endpoint (was
            // GET /api/v1/connectors/{slug}/install).
            var result = await client.GetConnectorAsync(slugOrId, ct);
            if (result is null)
            {
                await Console.Error.WriteLineAsync(
                    $"Connector '{slugOrId}' is not installed on the current tenant. Run 'spring connector install {slugOrId}' first.");
                Environment.Exit(1);
                return;
            }
            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(new[] { result }, InstalledColumns));
        });
        return command;
    }

    private static Command CreateInstallCommand(Option<string> outputOption)
    {
        // Example: `spring connector install github`
        var idArg = new Argument<string>("slugOrId") { Description = "Connector slug or type id." };
        var command = new Command(
            "install",
            "Install a connector on the current tenant (idempotent). No config flags — connector-specific config flows through the per-unit PUT endpoint each connector owns.");
        command.Arguments.Add(idArg);
        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var slugOrId = parseResult.GetValue(idArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            try
            {
                var result = await client.InstallConnectorAsync(slugOrId, ct);
                Console.WriteLine(output == "json"
                    ? OutputFormatter.FormatJson(result)
                    : OutputFormatter.FormatTable(new[] { result }, InstalledColumns));
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
            {
                await Console.Error.WriteLineAsync(
                    $"Connector '{slugOrId}' is not registered with the host. Run 'spring connector catalog' to see available types.");
                Environment.Exit(1);
            }
        });
        return command;
    }

    private static Command CreateUninstallCommand()
    {
        // Example: `spring connector uninstall github --force`
        var idArg = new Argument<string>("slugOrId") { Description = "Connector slug or type id." };
        var forceOption = new Option<bool>("--force")
        {
            Description = "Skip the confirmation prompt.",
        };
        var command = new Command("uninstall", "Uninstall a connector from the current tenant.");
        command.Arguments.Add(idArg);
        command.Options.Add(forceOption);
        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var slugOrId = parseResult.GetValue(idArg)!;
            var force = parseResult.GetValue(forceOption);
            if (!force)
            {
                Console.Write($"Uninstall connector '{slugOrId}' from the current tenant? [y/N]: ");
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
            await client.UninstallConnectorAsync(slugOrId, ct);
            Console.WriteLine($"Uninstalled connector '{slugOrId}'.");
        });
        return command;
    }

    private static Command CreateConfigCommand(Option<string> outputOption)
    {
        // `spring connector config set <id> <key=value>` — today the only
        // supported key is "config", which takes a raw JSON document. The
        // endpoint accepts an opaque JsonElement so per-connector config
        // shapes evolve without changing this CLI. For unit-scoped typed
        // config, use `spring connector bind` / the per-connector PUT.
        var root = new Command("config", "Tenant-scoped connector configuration.");
        root.Subcommands.Add(CreateConfigSetCommand(outputOption));
        return root;
    }

    private static Command CreateConfigSetCommand(Option<string> outputOption)
    {
        var idArg = new Argument<string>("slugOrId") { Description = "Connector slug or type id." };
        var kvArg = new Argument<string>("key=value")
        {
            Description = "Supported keys: 'config=<json>'. Empty value clears the payload.",
        };
        var command = new Command(
            "set",
            "Set a single config field on an installed connector. Only 'config=<json>' is supported today — connector-specific keys extend this as connectors publish typed schemas.");
        command.Arguments.Add(idArg);
        command.Arguments.Add(kvArg);
        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var slugOrId = parseResult.GetValue(idArg)!;
            var kv = parseResult.GetValue(kvArg)!;
            var eq = kv.IndexOf('=');
            if (eq < 0)
            {
                await Console.Error.WriteLineAsync($"Expected key=value, got '{kv}'.");
                Environment.Exit(1);
                return;
            }
            var key = kv[..eq].Trim();
            if (!string.Equals(key, "config", StringComparison.OrdinalIgnoreCase))
            {
                await Console.Error.WriteLineAsync(
                    $"Unknown config key '{key}'. Supported: config=<json>. Use 'spring connector bind' for per-unit typed config.");
                Environment.Exit(1);
                return;
            }

            // Deferred to a follow-up: wiring this through a typed Kiota
            // PATCH call. The endpoint exists (PATCH
            // /api/v1/connectors/{slugOrId}/install/config) but the Kiota
            // wrapper for opaque JsonElement bodies requires a small
            // helper that we'll land alongside the first connector that
            // ships a typed tenant-config schema. For V2, all OSS
            // connectors either carry no tenant-level config (Arxiv,
            // WebSearch) or rely on unit-level config (GitHub).
            await Console.Error.WriteLineAsync(
                $"'spring connector config set' is not yet wired to the PATCH endpoint (tracked as a follow-up to #689). Use the HTTP API directly to set tenant-scoped connector config for now.");
            Environment.Exit(1);
        });
        return command;
    }

    private static Command CreateCredentialsCommand(Option<string> outputOption)
    {
        var root = new Command("credentials", "Read credential-health state for an installed connector.");
        root.Subcommands.Add(CreateCredentialsStatusCommand(outputOption));
        return root;
    }

    private static Command CreateCredentialsStatusCommand(Option<string> outputOption)
    {
        // Example: `spring connector credentials status github`
        var idArg = new Argument<string>("slugOrId") { Description = "Connector slug or type id." };
        var secretOption = new Option<string?>("--secret-name")
        {
            Description = "Secret name within the connector (defaults to 'default'). Multi-credential connectors (e.g. GitHub App id + private key) store one row per credential.",
        };
        var command = new Command(
            "status",
            "Show the current credential-health status for a connector. Sourced from the shared credential_health store (#686).");
        command.Arguments.Add(idArg);
        command.Options.Add(secretOption);
        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var slugOrId = parseResult.GetValue(idArg)!;
            var secretName = parseResult.GetValue(secretOption);
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            var result = await client.GetConnectorCredentialHealthAsync(slugOrId, secretName, ct);
            if (result is null)
            {
                await Console.Error.WriteLineAsync(
                    $"No credential-health row recorded for connector '{slugOrId}'. Validate credentials via the portal or the HTTP API to prime the row.");
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
}