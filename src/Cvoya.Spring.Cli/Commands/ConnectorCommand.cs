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
        // Platform-level provision verb (PlatformOperator) — make a connector
        // type available platform-wide. Must be run before any tenant can bind.
        connectorCommand.Subcommands.Add(CreateProvisionCommand(outputOption));
        connectorCommand.Subcommands.Add(CreateDeprovisionCommand());
        // Tenant-level bind/unbind verbs (TenantOperator) — renamed from
        // install/uninstall in #1259 (C1.2c) to clarify the authz split.
        connectorCommand.Subcommands.Add(CreateBindCommand2(outputOption));
        connectorCommand.Subcommands.Add(CreateUnbindCommand());
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
                        appInstallationId = config.AppInstallationId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
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
        var reviewerOption = new Option<string?>("--reviewer")
        {
            Description = "Optional default reviewer (GitHub login) used for human-review handoffs on this unit. When omitted the connector falls back to its installation default.",
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
        command.Options.Add(reviewerOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitOption)!;
            var type = parseResult.GetValue(typeOption)!;
            var owner = parseResult.GetValue(ownerOption);
            var repo = parseResult.GetValue(repoOption);
            var installationId = parseResult.GetValue(installationIdOption);
            var events = parseResult.GetValue(eventsOption);
            var reviewer = parseResult.GetValue(reviewerOption);
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
                    reviewer,
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

    // ---- Platform-level provision / deprovision verbs (PlatformOperator) ----

    private static readonly OutputFormatter.Column<ProvisionedConnectorResponse>[] ProvisionedColumns =
    {
        new OutputFormatter.Column<ProvisionedConnectorResponse>("slug", c => c.TypeSlug),
        new OutputFormatter.Column<ProvisionedConnectorResponse>("name", c => c.DisplayName),
        new OutputFormatter.Column<ProvisionedConnectorResponse>("provisionedAt", c => c.ProvisionedAt?.ToString("u")),
        new OutputFormatter.Column<ProvisionedConnectorResponse>("updatedAt", c => c.UpdatedAt?.ToString("u")),
    };

    private static Command CreateProvisionCommand(Option<string> outputOption)
    {
        // Example: `spring connector provision github`
        var idArg = new Argument<string>("slug") { Description = "Connector slug (e.g. 'github')." };
        var command = new Command(
            "provision",
            "Provision a connector type platform-wide (PlatformOperator only; idempotent). " +
            "Makes the connector available for tenant operators to bind. " +
            "The connector package must already be installed on the Spring Voyage deployment.");
        command.Arguments.Add(idArg);
        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var slug = parseResult.GetValue(idArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            try
            {
                var result = await client.ProvisionConnectorAsync(slug, ct);
                Console.WriteLine(output == "json"
                    ? OutputFormatter.FormatJson(result)
                    : OutputFormatter.FormatTable(new[] { result }, ProvisionedColumns));
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
            {
                await Console.Error.WriteLineAsync(
                    $"Connector '{slug}' is not registered with the host. " +
                    "Only connectors whose package is installed on the deployment can be provisioned.");
                Environment.Exit(1);
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 403)
            {
                await Console.Error.WriteLineAsync(
                    $"Provisioning connectors requires PlatformOperator role. {ProblemDetailsFormatter.Format(ex)}");
                Environment.Exit(1);
            }
        });
        return command;
    }

    private static Command CreateDeprovisionCommand()
    {
        // Example: `spring connector deprovision github --force`
        var idArg = new Argument<string>("slug") { Description = "Connector slug (e.g. 'github')." };
        var forceOption = new Option<bool>("--force")
        {
            Description = "Skip the confirmation prompt.",
        };
        var command = new Command(
            "deprovision",
            "Deprovision a connector type platform-wide (PlatformOperator only). " +
            "Does not remove connector bindings from existing tenants — each tenant's bind " +
            "row remains until that tenant unbinds. After deprovisioning, no new tenants " +
            "can bind the connector type.");
        command.Arguments.Add(idArg);
        command.Options.Add(forceOption);
        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var slug = parseResult.GetValue(idArg)!;
            var force = parseResult.GetValue(forceOption);
            if (!force)
            {
                Console.Write($"Deprovision connector '{slug}' platform-wide? [y/N]: ");
                var answer = Console.ReadLine();
                if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
                {
                    await Console.Error.WriteLineAsync("Deprovision cancelled.");
                    Environment.Exit(1);
                    return;
                }
            }
            var client = ClientFactory.Create();
            try
            {
                await client.DeprovisionConnectorAsync(slug, ct);
                Console.WriteLine($"Deprovisioned connector '{slug}'.");
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
            {
                await Console.Error.WriteLineAsync(
                    $"Connector '{slug}' is not registered with the host. {ProblemDetailsFormatter.Format(ex)}");
                Environment.Exit(1);
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 403)
            {
                await Console.Error.WriteLineAsync(
                    $"Deprovisioning connectors requires PlatformOperator role. {ProblemDetailsFormatter.Format(ex)}");
                Environment.Exit(1);
            }
        });
        return command;
    }

    // ---- Tenant-level bind / unbind verbs (TenantOperator) ----
    // Renamed from install/uninstall in #1259 (C1.2c). The `bind` verb
    // targets POST /api/v1/tenant/connectors/{slug}/bind; `unbind` targets
    // DELETE /api/v1/tenant/connectors/{slug}.

    private static Command CreateBindCommand2(Option<string> outputOption)
    {
        // Example: `spring connector bind github`
        // Note: named CreateBindCommand2 to avoid conflict with the per-unit
        // `bind` verb (CreateBindCommand) defined earlier in the file.
        var idArg = new Argument<string>("slugOrId") { Description = "Connector slug or type id." };
        var command = new Command(
            "bind-tenant",
            "Bind (install) a connector on the current tenant (TenantOperator; idempotent). " +
            "The connector must first be provisioned platform-wide by a PlatformOperator. " +
            "No config flags — connector-specific config flows through the per-unit PUT endpoint each connector owns.");
        command.Arguments.Add(idArg);
        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var slugOrId = parseResult.GetValue(idArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            try
            {
                var result = await client.BindConnectorAsync(slugOrId, ct);
                Console.WriteLine(output == "json"
                    ? OutputFormatter.FormatJson(result)
                    : OutputFormatter.FormatTable(new[] { result }, InstalledColumns));
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
            {
                await Console.Error.WriteLineAsync(
                    $"Connector '{slugOrId}' is not registered with the host. " +
                    $"Run 'spring connector provision {slugOrId}' (PlatformOperator) first, " +
                    "then retry 'spring connector bind-tenant'.");
                Environment.Exit(1);
            }
        });
        return command;
    }

    private static Command CreateUnbindCommand()
    {
        // Example: `spring connector unbind github --force`
        var idArg = new Argument<string>("slugOrId") { Description = "Connector slug or type id." };
        var forceOption = new Option<bool>("--force")
        {
            Description = "Skip the confirmation prompt.",
        };
        var command = new Command("unbind", "Unbind (uninstall) a connector from the current tenant (TenantOperator).");
        command.Arguments.Add(idArg);
        command.Options.Add(forceOption);
        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var slugOrId = parseResult.GetValue(idArg)!;
            var force = parseResult.GetValue(forceOption);
            if (!force)
            {
                Console.Write($"Unbind connector '{slugOrId}' from the current tenant? [y/N]: ");
                var answer = Console.ReadLine();
                if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
                {
                    await Console.Error.WriteLineAsync("Unbind cancelled.");
                    Environment.Exit(1);
                    return;
                }
            }
            var client = ClientFactory.Create();
            await client.UnbindConnectorAsync(slugOrId, ct);
            Console.WriteLine($"Unbound connector '{slugOrId}'.");
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
            Description =
                "Supported keys: 'config=<json>'. The value is either inline JSON or '@path/to/file.json' to read from disk. " +
                "An empty value (e.g. 'config=') clears the payload.",
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
            var (parsed, error) = await ResolveConfigArgumentAsync(kv, ct);
            if (error is not null)
            {
                await Console.Error.WriteLineAsync(error);
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();
            try
            {
                var result = await client.UpdateConnectorInstallConfigAsync(slugOrId, parsed, ct);
                var output = parseResult.GetValue(outputOption) ?? "table";
                Console.WriteLine(output == "json"
                    ? OutputFormatter.FormatJson(result)
                    : OutputFormatter.FormatTable(new[] { result }, InstalledColumns));
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
            {
                await Console.Error.WriteLineAsync(
                    $"Connector '{slugOrId}' is not installed on the current tenant. " +
                    $"{ProblemDetailsFormatter.Format(ex)}");
                Environment.Exit(1);
            }
        });
        return command;
    }

    /// <summary>
    /// Resolves the <c>key=value</c> positional supplied to <c>spring
    /// connector config set</c> into a parsed <see cref="System.Text.Json.JsonElement"/>.
    /// Returns a non-null error string when validation fails so the caller
    /// can render it to stderr and exit non-zero. Lives at class scope (not
    /// inside the action lambda) so unit tests can drive each branch
    /// without spawning a process — the action wrapper would otherwise have
    /// to call <see cref="Environment.Exit"/> mid-test, which tears down the
    /// xUnit runner.
    /// </summary>
    /// <remarks>
    /// Accepts either inline JSON (e.g. <c>config={"foo":"bar"}</c>) or
    /// <c>config=@path/to/file.json</c>. An empty value
    /// (<c>config=</c>) is treated as an explicit JSON <c>null</c> so the
    /// server-side handler clears the stored payload. Unknown keys, missing
    /// <c>=</c>, malformed JSON, and IO failures all surface as a non-null
    /// error string.
    /// </remarks>
    internal static async Task<(System.Text.Json.JsonElement Parsed, string? Error)> ResolveConfigArgumentAsync(
        string kv,
        CancellationToken ct = default)
    {
        var eq = kv.IndexOf('=');
        if (eq < 0)
        {
            return (default, $"Expected key=value, got '{kv}'.");
        }
        var key = kv[..eq].Trim();
        if (!string.Equals(key, "config", StringComparison.OrdinalIgnoreCase))
        {
            return (default,
                $"Unknown config key '{key}'. Supported: config=<json>. Use 'spring connector bind' for per-unit typed config.");
        }

        // The raw value is either inline JSON or '@path/to/file.json'.
        // Empty string means "clear the payload" — send an explicit JSON
        // null so the server-side handler stores no config.
        var raw = kv[(eq + 1)..];
        string jsonText;
        if (string.IsNullOrEmpty(raw))
        {
            jsonText = "null";
        }
        else if (raw.StartsWith('@'))
        {
            var path = raw[1..];
            if (string.IsNullOrWhiteSpace(path))
            {
                return (default, "Expected a file path after '@'.");
            }
            try
            {
                jsonText = await File.ReadAllTextAsync(path, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return (default, $"Failed to read config file '{path}': {ex.Message}");
            }
        }
        else
        {
            jsonText = raw;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(jsonText);
            // JsonDocument owns the underlying buffer — clone the root so
            // the JsonElement stays valid after the document is disposed.
            return (doc.RootElement.Clone(), null);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return (default, $"Failed to parse config JSON: {ex.Message}");
        }
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