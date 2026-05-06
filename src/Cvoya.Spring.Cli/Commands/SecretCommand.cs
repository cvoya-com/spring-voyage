// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;
using Cvoya.Spring.Cli.Utilities;

/// <summary>
/// Builds the <c>spring secret &lt;verb&gt;</c> subtree (#432). The CLI
/// surfaces seven verbs — <c>create | list | get | rotate | versions |
/// prune | delete</c> — that map 1:1 to the scope-keyed HTTP endpoints
/// documented on
/// <see href="https://github.com/cvoya-com/spring-voyage/blob/main/src/Cvoya.Spring.Host.Api/Endpoints/SecretEndpoints.cs"/>.
/// Every verb takes a required <c>--scope {unit|agent|tenant|platform}</c>
/// flag; <c>--unit</c> is mandatory when scope is <c>unit</c>,
/// <c>--agent</c> is mandatory when scope is <c>agent</c> (#1741),
/// otherwise both are ignored. Plaintext <b>flows in only on
/// <c>create</c> and <c>rotate</c></b> — the server never returns a
/// value on any response, list entry, or log line; <c>spring secret
/// get</c> therefore surfaces metadata and version information, not
/// plaintext.
///
/// <para>
/// <b>Propagation flag (#1741).</b> <c>spring secret create</c> and
/// <c>spring secret rotate</c> accept <c>--propagate</c> /
/// <c>--no-propagate</c>. The flag is meaningful only at
/// <c>--scope unit</c>: it controls whether descendant scopes inherit
/// the value through the resolver chain (Agent → Unit → ParentUnit →
/// Tenant). Tenant secrets always propagate; agent secrets have no
/// descendants. The CLI rejects the flag at non-unit scopes rather
/// than silently ignoring it. On <c>rotate</c> the flag is sticky —
/// the server preserves the previous version's propagate value and
/// the CLI documents the field as accepted-but-not-applied.
/// </para>
/// </summary>
public static class SecretCommand
{
    private static readonly string[] Scopes = new[] { "unit", "agent", "tenant", "platform" };

    private static readonly OutputFormatter.Column<SecretMetadata>[] ListColumns =
    {
        new("name", m => m.Name),
        new("scope", m => m.Scope?.ToString()),
        new("createdAt", m => m.CreatedAt?.ToString("O")),
    };

    private static readonly OutputFormatter.Column<SecretVersionEntry>[] VersionColumns =
    {
        new("version", e => (e.Version ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new("origin", e => e.Origin?.ToString()),
        new("createdAt", e => e.CreatedAt?.ToString("O")),
        new("isCurrent", e => e.IsCurrent?.ToString().ToLowerInvariant()),
    };

    /// <summary>
    /// Entry point — builds the <c>secret</c> command subtree for attachment
    /// under the root command.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var cmd = new Command(
            "secret",
            "Manage unit / tenant / platform secrets. Plaintext is accepted only on " +
            "create/rotate bodies and never returned by list/get/versions/prune/delete.");

        cmd.Subcommands.Add(CreateCreateCommand(outputOption));
        cmd.Subcommands.Add(CreateListCommand(outputOption));
        cmd.Subcommands.Add(CreateGetCommand(outputOption));
        cmd.Subcommands.Add(CreateRotateCommand(outputOption));
        cmd.Subcommands.Add(CreateVersionsCommand(outputOption));
        cmd.Subcommands.Add(CreatePruneCommand(outputOption));
        cmd.Subcommands.Add(CreateDeleteCommand());
        return cmd;
    }

    // ---- shared scope option ------------------------------------------------

    private static Option<string> BuildScopeOption()
    {
        var opt = new Option<string>("--scope")
        {
            Description = "Target scope: unit, agent, tenant, or platform.",
            Required = true,
        };
        opt.AcceptOnlyFromAmong(Scopes);
        return opt;
    }

    private static Option<string?> BuildUnitOption()
        => new("--unit")
        {
            Description = "Unit identifier (required when --scope unit).",
        };

    private static Option<string?> BuildAgentOption()
        => new("--agent")
        {
            Description = "Agent identifier — Guid or no-dash form (required when --scope agent). #1741.",
        };

    private static string? ValidateScopeInputs(string scope, string? unit, string? agent = null)
    {
        if (scope == "unit" && string.IsNullOrWhiteSpace(unit))
        {
            return "--unit is required when --scope unit.";
        }
        if (scope == "agent" && string.IsNullOrWhiteSpace(agent))
        {
            return "--agent is required when --scope agent.";
        }
        return null;
    }

    private static void DieWith(string message)
    {
        Console.Error.WriteLine(message);
        Environment.Exit(1);
    }

    // ---- create -------------------------------------------------------------

    private static Command CreateCreateCommand(Option<string> outputOption)
    {
        var scopeOption = BuildScopeOption();
        var unitOption = BuildUnitOption();
        var agentOption = BuildAgentOption();
        var nameArg = new Argument<string>("name")
        {
            Description = "Secret name (case-sensitive; chosen by the operator).",
        };
        var valueOption = new Option<string?>("--value")
        {
            Description =
                "Plaintext to write through to the platform store. Mutually exclusive with " +
                "--from-file and --external-store-key.",
        };
        var fileOption = new Option<string?>("--from-file")
        {
            Description =
                "Read the plaintext from a file (the file's raw bytes become the value). " +
                "Mutually exclusive with --value and --external-store-key.",
        };
        var externalOption = new Option<string?>("--external-store-key")
        {
            Description =
                "Bind an existing external reference (e.g. 'kv://prod/github-app-privatekey') " +
                "instead of writing plaintext. The platform never mutates the external slot.",
        };
        // Tri-state propagate flag (#1741): null = unspecified (server
        // default = inherit), true = explicitly propagate, false =
        // isolate. Only meaningful at unit scope; warned when supplied
        // at tenant / agent / platform scope. System.CommandLine maps
        // `--propagate` (no value) to true and `--no-propagate` to
        // false through the inverted alias.
        var propagateOption = new Option<bool?>("--propagate")
        {
            Description =
                "Whether descendants inherit this secret. Default: inherit. Use --no-propagate " +
                "to isolate the value to this exact unit so child / agent scopes never see it. " +
                "Meaningful only at --scope unit (tenants always propagate; agents have no " +
                "descendants — the flag is rejected at non-unit scopes). #1741.",
        };
        var noPropagateOption = new Option<bool>("--no-propagate")
        {
            Description = "Shortcut for --propagate=false. See --propagate.",
        };

        var command = new Command(
            "create",
            "Register a new secret. Provide exactly one of --value / --from-file / --external-store-key. " +
            "Use --scope agent --agent <id> for per-agent overrides (#1741).");
        command.Arguments.Add(nameArg);
        command.Options.Add(scopeOption);
        command.Options.Add(unitOption);
        command.Options.Add(agentOption);
        command.Options.Add(valueOption);
        command.Options.Add(fileOption);
        command.Options.Add(externalOption);
        command.Options.Add(propagateOption);
        command.Options.Add(noPropagateOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var scope = parseResult.GetValue(scopeOption)!;
            var unit = parseResult.GetValue(unitOption);
            var agent = parseResult.GetValue(agentOption);
            var valueFlag = parseResult.GetValue(valueOption);
            var file = parseResult.GetValue(fileOption);
            var external = parseResult.GetValue(externalOption);
            var propagateFlag = parseResult.GetValue(propagateOption);
            var noPropagateFlag = parseResult.GetValue(noPropagateOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (ValidateScopeInputs(scope, unit, agent) is { } scopeErr)
            {
                DieWith(scopeErr);
                return;
            }

            // Tri-state collapse: --no-propagate wins if both are set;
            // null otherwise leaves the flag unspecified so the server
            // applies the inherit-by-default behaviour.
            bool? propagate = noPropagateFlag ? false : propagateFlag;

            // Reject --propagate / --no-propagate at non-unit scopes.
            // Document why: tenant secrets always propagate (the whole
            // point of tenant defaults), agent secrets have no
            // descendants, platform secrets are global. Silent ignore
            // would let an operator believe the flag took effect.
            if (propagate is not null && scope != "unit")
            {
                DieWith(
                    $"--propagate / --no-propagate is only meaningful with --scope unit; " +
                    $"got --scope {scope}. Tenant defaults always propagate; agent secrets " +
                    $"have no descendants; platform secrets are global.");
                return;
            }

            var resolvedValue = await ResolveValueAsync(valueFlag, file, ct);
            var sources = new[]
            {
                resolvedValue is not null,
                !string.IsNullOrWhiteSpace(external),
            };
            var supplied = sources.Count(s => s);
            if (supplied != 1)
            {
                DieWith(
                    "Provide exactly one of --value / --from-file / --external-store-key.");
                return;
            }

            var client = ClientFactory.Create();
            try
            {
                CreateSecretResponse response = scope switch
                {
                    "unit" => await client.CreateUnitSecretAsync(unit!, name, resolvedValue, external, propagate, ct),
                    "agent" => await client.CreateAgentSecretAsync(agent!, name, resolvedValue, external, ct),
                    "tenant" => await client.CreateTenantSecretAsync(name, resolvedValue, external, ct),
                    "platform" => await client.CreatePlatformSecretAsync(name, resolvedValue, external, ct),
                    _ => throw new InvalidOperationException($"Unknown scope '{scope}'."),
                };

                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJson(response));
                }
                else
                {
                    Console.WriteLine(
                        $"Secret '{response.Name}' created ({response.Scope}). " +
                        $"createdAt={response.CreatedAt?.ToString("O")}");
                }
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                DieWith($"Failed to create secret '{name}': {ProblemDetailsFormatter.Format(ex)}");
            }
        });

        return command;
    }

    // ---- list ---------------------------------------------------------------

    private static Command CreateListCommand(Option<string> outputOption)
    {
        var scopeOption = BuildScopeOption();
        var unitOption = BuildUnitOption();
        var agentOption = BuildAgentOption();

        var command = new Command(
            "list",
            "List secret metadata for the target scope. Never returns plaintext or store keys.");
        command.Options.Add(scopeOption);
        command.Options.Add(unitOption);
        command.Options.Add(agentOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var scope = parseResult.GetValue(scopeOption)!;
            var unit = parseResult.GetValue(unitOption);
            var agent = parseResult.GetValue(agentOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (ValidateScopeInputs(scope, unit, agent) is { } scopeErr)
            {
                DieWith(scopeErr);
                return;
            }

            var client = ClientFactory.Create();
            try
            {
                IReadOnlyList<SecretMetadata> entries = scope switch
                {
                    "unit" => await client.ListUnitSecretsAsync(unit!, ct),
                    "agent" => await client.ListAgentSecretsAsync(agent!, ct),
                    "tenant" => await client.ListTenantSecretsAsync(ct),
                    "platform" => await client.ListPlatformSecretsAsync(ct),
                    _ => throw new InvalidOperationException($"Unknown scope '{scope}'."),
                };

                Console.WriteLine(output == "json"
                    ? OutputFormatter.FormatJson(entries)
                    : OutputFormatter.FormatTable(entries, ListColumns));
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                DieWith($"Failed to list secrets for scope '{scope}': {ProblemDetailsFormatter.Format(ex)}");
            }
        });

        return command;
    }

    // ---- get ----------------------------------------------------------------
    //
    // The server never returns plaintext on any path — `get` therefore
    // surfaces metadata + version summary for the named secret. When
    // --version is supplied, the CLI pins the lookup to that version and
    // reports its per-row metadata; omitting --version highlights the
    // current version. This matches the issue's ask ("spring secret get
    // --scope ... <name> [--version <n>]") while respecting the security
    // contract that plaintext is only resolvable server-side.

    private static Command CreateGetCommand(Option<string> outputOption)
    {
        var scopeOption = BuildScopeOption();
        var unitOption = BuildUnitOption();
        var agentOption = BuildAgentOption();
        var nameArg = new Argument<string>("name") { Description = "Secret name." };
        var versionOption = new Option<int?>("--version")
        {
            Description = "Pin the lookup to a specific version number (defaults to the current version).",
        };

        var command = new Command(
            "get",
            "Print metadata for a secret (plaintext is never returned). " +
            "Shows per-version metadata; defaults to the current version unless --version is supplied.");
        command.Arguments.Add(nameArg);
        command.Options.Add(scopeOption);
        command.Options.Add(unitOption);
        command.Options.Add(agentOption);
        command.Options.Add(versionOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var scope = parseResult.GetValue(scopeOption)!;
            var unit = parseResult.GetValue(unitOption);
            var agent = parseResult.GetValue(agentOption);
            var version = parseResult.GetValue(versionOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (ValidateScopeInputs(scope, unit, agent) is { } scopeErr)
            {
                DieWith(scopeErr);
                return;
            }

            var client = ClientFactory.Create();
            try
            {
                var versions = scope switch
                {
                    "unit" => await client.ListUnitSecretVersionsAsync(unit!, name, ct),
                    "agent" => await client.ListAgentSecretVersionsAsync(agent!, name, ct),
                    "tenant" => await client.ListTenantSecretVersionsAsync(name, ct),
                    "platform" => await client.ListPlatformSecretVersionsAsync(name, ct),
                    _ => throw new InvalidOperationException($"Unknown scope '{scope}'."),
                };

                var rows = versions.Versions ?? new List<SecretVersionEntry>();
                SecretVersionEntry? selected;
                if (version is int pin)
                {
                    selected = rows.FirstOrDefault(v => (v.Version ?? 0) == pin);
                    if (selected is null)
                    {
                        DieWith($"Secret '{name}' has no version {pin}.");
                        return;
                    }
                }
                else
                {
                    selected = rows.FirstOrDefault(v => v.IsCurrent == true)
                        ?? rows.FirstOrDefault();
                }

                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                    {
                        name = versions.Name,
                        scope = versions.Scope?.ToString(),
                        version = selected?.Version,
                        origin = selected?.Origin?.ToString(),
                        createdAt = selected?.CreatedAt,
                        isCurrent = selected?.IsCurrent ?? false,
                        totalVersions = rows.Count,
                    }));
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine($"Name:    {versions.Name}");
                sb.AppendLine($"Scope:   {versions.Scope?.ToString()}");
                if (selected is not null)
                {
                    sb.AppendLine($"Version: {selected.Version ?? 0}" +
                        (selected.IsCurrent == true ? " (current)" : string.Empty));
                    sb.AppendLine($"Origin:  {selected.Origin?.ToString()}");
                    sb.AppendLine($"Created: {selected.CreatedAt?.ToString("O")}");
                }
                sb.AppendLine($"Total versions retained: {rows.Count}");
                sb.AppendLine();
                sb.AppendLine(
                    "Plaintext is never returned by any CLI surface. Agents and connectors " +
                    "read the value through the server-side resolver.");
                Console.Write(sb.ToString());
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                DieWith($"Failed to get secret '{name}': {ProblemDetailsFormatter.Format(ex)}");
            }
        });

        return command;
    }

    // ---- rotate -------------------------------------------------------------

    private static Command CreateRotateCommand(Option<string> outputOption)
    {
        var scopeOption = BuildScopeOption();
        var unitOption = BuildUnitOption();
        var agentOption = BuildAgentOption();
        var nameArg = new Argument<string>("name") { Description = "Secret name." };
        var valueOption = new Option<string?>("--value")
        {
            Description = "New plaintext. Mutually exclusive with --from-file and --external-store-key.",
        };
        var fileOption = new Option<string?>("--from-file")
        {
            Description = "Read the new plaintext from a file.",
        };
        var externalOption = new Option<string?>("--external-store-key")
        {
            Description =
                "Swap to an external reference (new or changed). Flips the origin to ExternalReference; " +
                "the old version stays resolvable by pin until pruned.",
        };
        // Same tri-state shape as the create command. Note: rotate
        // carries the propagate flag forward from the previous version
        // (#1741); the field is accepted for shape symmetry but the
        // server does not change the stored flag on rotation. To
        // change propagation, delete + re-create the secret.
        var propagateOption = new Option<bool?>("--propagate")
        {
            Description =
                "Sticky: rotate carries the previous version's propagate flag forward unchanged. " +
                "To flip propagation, delete + re-create the secret. Accepted at --scope unit only " +
                "for shape symmetry with create. #1741.",
        };
        var noPropagateOption = new Option<bool>("--no-propagate")
        {
            Description = "Shortcut for --propagate=false. See --propagate.",
        };

        var command = new Command(
            "rotate",
            "Append a new version of an existing secret. Provide exactly one of " +
            "--value / --from-file / --external-store-key. Old versions remain resolvable " +
            "by pin until pruned.");
        command.Arguments.Add(nameArg);
        command.Options.Add(scopeOption);
        command.Options.Add(unitOption);
        command.Options.Add(agentOption);
        command.Options.Add(valueOption);
        command.Options.Add(fileOption);
        command.Options.Add(externalOption);
        command.Options.Add(propagateOption);
        command.Options.Add(noPropagateOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var scope = parseResult.GetValue(scopeOption)!;
            var unit = parseResult.GetValue(unitOption);
            var agent = parseResult.GetValue(agentOption);
            var valueFlag = parseResult.GetValue(valueOption);
            var file = parseResult.GetValue(fileOption);
            var external = parseResult.GetValue(externalOption);
            var propagateFlag = parseResult.GetValue(propagateOption);
            var noPropagateFlag = parseResult.GetValue(noPropagateOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (ValidateScopeInputs(scope, unit, agent) is { } scopeErr)
            {
                DieWith(scopeErr);
                return;
            }

            bool? propagate = noPropagateFlag ? false : propagateFlag;

            if (propagate is not null && scope != "unit")
            {
                DieWith(
                    $"--propagate / --no-propagate is only meaningful with --scope unit; " +
                    $"got --scope {scope}.");
                return;
            }

            var resolvedValue = await ResolveValueAsync(valueFlag, file, ct);
            var sources = new[]
            {
                resolvedValue is not null,
                !string.IsNullOrWhiteSpace(external),
            };
            var supplied = sources.Count(s => s);
            if (supplied != 1)
            {
                DieWith(
                    "Provide exactly one of --value / --from-file / --external-store-key.");
                return;
            }

            var client = ClientFactory.Create();
            try
            {
                RotateSecretResponse response = scope switch
                {
                    "unit" => await client.RotateUnitSecretAsync(unit!, name, resolvedValue, external, propagate, ct),
                    "agent" => await client.RotateAgentSecretAsync(agent!, name, resolvedValue, external, ct),
                    "tenant" => await client.RotateTenantSecretAsync(name, resolvedValue, external, ct),
                    "platform" => await client.RotatePlatformSecretAsync(name, resolvedValue, external, ct),
                    _ => throw new InvalidOperationException($"Unknown scope '{scope}'."),
                };

                var newVersion = response.Version ?? 0;
                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                    {
                        name = response.Name,
                        scope = response.Scope?.ToString(),
                        version = newVersion,
                    }));
                }
                else
                {
                    Console.WriteLine(
                        $"Secret '{response.Name}' rotated ({response.Scope}); new version = {newVersion}.");
                }
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                DieWith($"Failed to rotate secret '{name}': {ProblemDetailsFormatter.Format(ex)}");
            }
        });

        return command;
    }

    // ---- versions -----------------------------------------------------------

    private static Command CreateVersionsCommand(Option<string> outputOption)
    {
        var scopeOption = BuildScopeOption();
        var unitOption = BuildUnitOption();
        var agentOption = BuildAgentOption();
        var nameArg = new Argument<string>("name") { Description = "Secret name." };

        var command = new Command(
            "versions",
            "List every retained version of a secret (metadata only; plaintext never returned).");
        command.Arguments.Add(nameArg);
        command.Options.Add(scopeOption);
        command.Options.Add(unitOption);
        command.Options.Add(agentOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var scope = parseResult.GetValue(scopeOption)!;
            var unit = parseResult.GetValue(unitOption);
            var agent = parseResult.GetValue(agentOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (ValidateScopeInputs(scope, unit, agent) is { } scopeErr)
            {
                DieWith(scopeErr);
                return;
            }

            var client = ClientFactory.Create();
            try
            {
                var response = scope switch
                {
                    "unit" => await client.ListUnitSecretVersionsAsync(unit!, name, ct),
                    "agent" => await client.ListAgentSecretVersionsAsync(agent!, name, ct),
                    "tenant" => await client.ListTenantSecretVersionsAsync(name, ct),
                    "platform" => await client.ListPlatformSecretVersionsAsync(name, ct),
                    _ => throw new InvalidOperationException($"Unknown scope '{scope}'."),
                };

                var rows = response.Versions ?? new List<SecretVersionEntry>();
                Console.WriteLine(output == "json"
                    ? OutputFormatter.FormatJson(response)
                    : OutputFormatter.FormatTable(rows, VersionColumns));
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                DieWith($"Failed to list versions for secret '{name}': {ProblemDetailsFormatter.Format(ex)}");
            }
        });

        return command;
    }

    // ---- prune --------------------------------------------------------------

    private static Command CreatePruneCommand(Option<string> outputOption)
    {
        var scopeOption = BuildScopeOption();
        var unitOption = BuildUnitOption();
        var agentOption = BuildAgentOption();
        var nameArg = new Argument<string>("name") { Description = "Secret name." };
        var keepOption = new Option<int>("--keep")
        {
            Description = "Retain this many of the most-recent versions (must be >= 1; current is always kept).",
            Required = true,
        };

        var command = new Command(
            "prune",
            "Prune older versions of a secret, retaining the N most-recent. Platform-owned store slots " +
            "are reclaimed; external-reference versions leave the upstream store untouched.");
        command.Arguments.Add(nameArg);
        command.Options.Add(scopeOption);
        command.Options.Add(unitOption);
        command.Options.Add(agentOption);
        command.Options.Add(keepOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var scope = parseResult.GetValue(scopeOption)!;
            var unit = parseResult.GetValue(unitOption);
            var agent = parseResult.GetValue(agentOption);
            var keep = parseResult.GetValue(keepOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (ValidateScopeInputs(scope, unit, agent) is { } scopeErr)
            {
                DieWith(scopeErr);
                return;
            }

            if (keep < 1)
            {
                DieWith("--keep must be a positive integer (>= 1).");
                return;
            }

            var client = ClientFactory.Create();
            try
            {
                var response = scope switch
                {
                    "unit" => await client.PruneUnitSecretAsync(unit!, name, keep, ct),
                    "agent" => await client.PruneAgentSecretAsync(agent!, name, keep, ct),
                    "tenant" => await client.PruneTenantSecretAsync(name, keep, ct),
                    "platform" => await client.PrunePlatformSecretAsync(name, keep, ct),
                    _ => throw new InvalidOperationException($"Unknown scope '{scope}'."),
                };

                var prunedCount = response.Pruned ?? 0;
                var keepCount = response.Keep ?? 0;
                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                    {
                        name = response.Name,
                        scope = response.Scope?.ToString(),
                        keep = keepCount,
                        pruned = prunedCount,
                    }));
                }
                else
                {
                    Console.WriteLine(
                        $"Secret '{response.Name}' ({response.Scope}) pruned: " +
                        $"keep={keepCount}, versionsRemoved={prunedCount}.");
                }
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                DieWith($"Failed to prune secret '{name}': {ProblemDetailsFormatter.Format(ex)}");
            }
        });

        return command;
    }

    // ---- delete -------------------------------------------------------------

    private static Command CreateDeleteCommand()
    {
        var scopeOption = BuildScopeOption();
        var unitOption = BuildUnitOption();
        var agentOption = BuildAgentOption();
        var nameArg = new Argument<string>("name") { Description = "Secret name." };

        var command = new Command(
            "delete",
            "Delete every version of a secret. Platform-owned store slots are reclaimed; external " +
            "references leave the upstream store untouched (deleting a Spring Voyage pointer never " +
            "destroys a customer-owned secret).");
        command.Arguments.Add(nameArg);
        command.Options.Add(scopeOption);
        command.Options.Add(unitOption);
        command.Options.Add(agentOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var scope = parseResult.GetValue(scopeOption)!;
            var unit = parseResult.GetValue(unitOption);
            var agent = parseResult.GetValue(agentOption);

            if (ValidateScopeInputs(scope, unit, agent) is { } scopeErr)
            {
                DieWith(scopeErr);
                return;
            }

            var client = ClientFactory.Create();
            try
            {
                switch (scope)
                {
                    case "unit":
                        await client.DeleteUnitSecretAsync(unit!, name, ct);
                        break;
                    case "agent":
                        await client.DeleteAgentSecretAsync(agent!, name, ct);
                        break;
                    case "tenant":
                        await client.DeleteTenantSecretAsync(name, ct);
                        break;
                    case "platform":
                        await client.DeletePlatformSecretAsync(name, ct);
                        break;
                }
                Console.WriteLine($"Secret '{name}' ({scope}) deleted.");
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                DieWith($"Failed to delete secret '{name}': {ProblemDetailsFormatter.Format(ex)}");
            }
        });

        return command;
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>
    /// Resolves the plaintext value from <c>--value</c> or <c>--from-file</c>
    /// (mutually exclusive). Returns <c>null</c> when neither is supplied so
    /// callers can tell "no value flag" from "empty value". File reads use
    /// the raw UTF-8 bytes verbatim — trailing newlines are preserved so a
    /// caller can write the exact payload they intend (e.g. PEM blocks).
    /// </summary>
    private static async Task<string?> ResolveValueAsync(
        string? valueFlag, string? filePath, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(valueFlag) && !string.IsNullOrWhiteSpace(filePath))
        {
            DieWith("--value and --from-file are mutually exclusive.");
            return null;
        }
        if (!string.IsNullOrEmpty(valueFlag))
        {
            return valueFlag;
        }
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            if (!File.Exists(filePath))
            {
                DieWith($"File not found: {filePath}");
                return null;
            }
            return await File.ReadAllTextAsync(filePath, ct);
        }
        return null;
    }
}