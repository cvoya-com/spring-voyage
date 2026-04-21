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
/// Every verb takes a required <c>--scope {unit|tenant|platform}</c>
/// flag; <c>--unit</c> is mandatory when scope is <c>unit</c>, ignored
/// otherwise. Plaintext <b>flows in only on <c>create</c> and
/// <c>rotate</c></b> — the server never returns a value on any response,
/// list entry, or log line; <c>spring secret get</c> therefore surfaces
/// metadata and version information, not plaintext.
/// </summary>
public static class SecretCommand
{
    private static readonly string[] Scopes = new[] { "unit", "tenant", "platform" };

    private static readonly OutputFormatter.Column<SecretMetadata>[] ListColumns =
    {
        new("name", m => m.Name),
        new("scope", m => m.Scope?.ToString()),
        new("createdAt", m => m.CreatedAt?.ToString("O")),
    };

    private static readonly OutputFormatter.Column<SecretVersionEntry>[] VersionColumns =
    {
        new("version", e => KiotaConversions.ToInt(e.Version).ToString(System.Globalization.CultureInfo.InvariantCulture)),
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
            Description = "Target scope: unit, tenant, or platform.",
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

    private static string? ValidateScopeInputs(string scope, string? unit)
    {
        if (scope == "unit" && string.IsNullOrWhiteSpace(unit))
        {
            return "--unit is required when --scope unit.";
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

        var command = new Command(
            "create",
            "Register a new secret. Provide exactly one of --value / --from-file / --external-store-key.");
        command.Arguments.Add(nameArg);
        command.Options.Add(scopeOption);
        command.Options.Add(unitOption);
        command.Options.Add(valueOption);
        command.Options.Add(fileOption);
        command.Options.Add(externalOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var scope = parseResult.GetValue(scopeOption)!;
            var unit = parseResult.GetValue(unitOption);
            var valueFlag = parseResult.GetValue(valueOption);
            var file = parseResult.GetValue(fileOption);
            var external = parseResult.GetValue(externalOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (ValidateScopeInputs(scope, unit) is { } scopeErr)
            {
                DieWith(scopeErr);
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
                    "unit" => await client.CreateUnitSecretAsync(unit!, name, resolvedValue, external, ct),
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

        var command = new Command(
            "list",
            "List secret metadata for the target scope. Never returns plaintext or store keys.");
        command.Options.Add(scopeOption);
        command.Options.Add(unitOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var scope = parseResult.GetValue(scopeOption)!;
            var unit = parseResult.GetValue(unitOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (ValidateScopeInputs(scope, unit) is { } scopeErr)
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
        command.Options.Add(versionOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var scope = parseResult.GetValue(scopeOption)!;
            var unit = parseResult.GetValue(unitOption);
            var version = parseResult.GetValue(versionOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (ValidateScopeInputs(scope, unit) is { } scopeErr)
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
                    "tenant" => await client.ListTenantSecretVersionsAsync(name, ct),
                    "platform" => await client.ListPlatformSecretVersionsAsync(name, ct),
                    _ => throw new InvalidOperationException($"Unknown scope '{scope}'."),
                };

                var rows = versions.Versions ?? new List<SecretVersionEntry>();
                SecretVersionEntry? selected;
                if (version is int pin)
                {
                    selected = rows.FirstOrDefault(v => KiotaConversions.ToInt(v.Version) == pin);
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
                        version = selected is null ? (int?)null : KiotaConversions.ToInt(selected.Version),
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
                    sb.AppendLine($"Version: {KiotaConversions.ToInt(selected.Version)}" +
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

        var command = new Command(
            "rotate",
            "Append a new version of an existing secret. Provide exactly one of " +
            "--value / --from-file / --external-store-key. Old versions remain resolvable " +
            "by pin until pruned.");
        command.Arguments.Add(nameArg);
        command.Options.Add(scopeOption);
        command.Options.Add(unitOption);
        command.Options.Add(valueOption);
        command.Options.Add(fileOption);
        command.Options.Add(externalOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var scope = parseResult.GetValue(scopeOption)!;
            var unit = parseResult.GetValue(unitOption);
            var valueFlag = parseResult.GetValue(valueOption);
            var file = parseResult.GetValue(fileOption);
            var external = parseResult.GetValue(externalOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (ValidateScopeInputs(scope, unit) is { } scopeErr)
            {
                DieWith(scopeErr);
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
                    "unit" => await client.RotateUnitSecretAsync(unit!, name, resolvedValue, external, ct),
                    "tenant" => await client.RotateTenantSecretAsync(name, resolvedValue, external, ct),
                    "platform" => await client.RotatePlatformSecretAsync(name, resolvedValue, external, ct),
                    _ => throw new InvalidOperationException($"Unknown scope '{scope}'."),
                };

                var newVersion = KiotaConversions.ToInt(response.Version);
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
        var nameArg = new Argument<string>("name") { Description = "Secret name." };

        var command = new Command(
            "versions",
            "List every retained version of a secret (metadata only; plaintext never returned).");
        command.Arguments.Add(nameArg);
        command.Options.Add(scopeOption);
        command.Options.Add(unitOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var scope = parseResult.GetValue(scopeOption)!;
            var unit = parseResult.GetValue(unitOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (ValidateScopeInputs(scope, unit) is { } scopeErr)
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
        command.Options.Add(keepOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var scope = parseResult.GetValue(scopeOption)!;
            var unit = parseResult.GetValue(unitOption);
            var keep = parseResult.GetValue(keepOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (ValidateScopeInputs(scope, unit) is { } scopeErr)
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
                    "tenant" => await client.PruneTenantSecretAsync(name, keep, ct),
                    "platform" => await client.PrunePlatformSecretAsync(name, keep, ct),
                    _ => throw new InvalidOperationException($"Unknown scope '{scope}'."),
                };

                var prunedCount = KiotaConversions.ToInt(response.Pruned);
                var keepCount = KiotaConversions.ToInt(response.Keep);
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
        var nameArg = new Argument<string>("name") { Description = "Secret name." };

        var command = new Command(
            "delete",
            "Delete every version of a secret. Platform-owned store slots are reclaimed; external " +
            "references leave the upstream store untouched (deleting a Spring Voyage pointer never " +
            "destroys a customer-owned secret).");
        command.Arguments.Add(nameArg);
        command.Options.Add(scopeOption);
        command.Options.Add(unitOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var scope = parseResult.GetValue(scopeOption)!;
            var unit = parseResult.GetValue(unitOption);

            if (ValidateScopeInputs(scope, unit) is { } scopeErr)
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