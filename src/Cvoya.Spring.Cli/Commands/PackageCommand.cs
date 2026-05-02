// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;

/// <summary>
/// Builds the <c>spring package</c> verb family (ADR-0035 decision 4).
/// <list type="bullet">
///   <item><description><c>spring package install &lt;name&gt;...</c> — install one or more catalog packages as a batch.</description></item>
///   <item><description><c>spring package install --file &lt;path&gt;</c> — install from a local package YAML.</description></item>
///   <item><description><c>spring package status &lt;install-id&gt;</c> — inspect install phase and per-package state.</description></item>
///   <item><description><c>spring package retry &lt;install-id&gt;</c> — re-run Phase 2 after a transient failure.</description></item>
///   <item><description><c>spring package abort &lt;install-id&gt;</c> — discard staging rows for a failed install.</description></item>
///   <item><description><c>spring package export &lt;unit-name&gt;</c> — write the package.yaml back from an installed unit.</description></item>
///   <item><description><c>spring package list</c> — browse the catalog.</description></item>
///   <item><description><c>spring package show &lt;name&gt;</c> — package detail.</description></item>
/// </list>
/// </summary>
public static class PackageCommand
{
    private static readonly OutputFormatter.Column<PackageSummary>[] ListColumns =
    {
        new("name", p => p.Name),
        new("units", p => p.UnitTemplateCount?.ToString()),
        new("agents", p => p.AgentTemplateCount?.ToString()),
        new("skills", p => p.SkillCount?.ToString()),
        new("connectors", p => p.ConnectorCount?.ToString()),
        new("workflows", p => p.WorkflowCount?.ToString()),
        new("description", p => p.Description),
    };

    private static readonly OutputFormatter.Column<UnitTemplateSummary>[] UnitTemplateColumns =
    {
        new("name", t => t.Name),
        new("description", t => t.Description),
        new("path", t => t.Path),
    };

    private static readonly OutputFormatter.Column<AgentTemplateSummary>[] AgentTemplateColumns =
    {
        new("name", t => t.Name),
        new("role", t => t.Role),
        new("displayName", t => t.DisplayName),
        new("description", t => t.Description),
    };

    private static readonly OutputFormatter.Column<SkillSummary>[] SkillColumns =
    {
        new("name", s => s.Name),
        new("tools", s => s.HasTools == true ? "yes" : "no"),
        new("path", s => s.Path),
    };

    private static readonly OutputFormatter.Column<ConnectorSummary>[] ConnectorColumns =
    {
        new("name", c => c.Name),
        new("path", c => c.Path),
    };

    private static readonly OutputFormatter.Column<WorkflowSummary>[] WorkflowColumns =
    {
        new("name", w => w.Name),
        new("path", w => w.Path),
    };

    /// <summary>
    /// Creates the <c>package</c> command root with all subcommands.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var packageCommand = new Command(
            "package",
            "Browse installed packages, install from the catalog or a local file, " +
            "inspect install state, and export packages back to package.yaml. " +
            "To install a package: spring package install <name> [--input k=v]...");

        packageCommand.Subcommands.Add(CreateInstallCommand(outputOption));
        packageCommand.Subcommands.Add(CreateStatusCommand(outputOption));
        packageCommand.Subcommands.Add(CreateRetryCommand());
        packageCommand.Subcommands.Add(CreateAbortCommand());
        packageCommand.Subcommands.Add(CreateExportCommand(outputOption));
        packageCommand.Subcommands.Add(CreateListCommand(outputOption));
        packageCommand.Subcommands.Add(CreateShowCommand(outputOption));

        return packageCommand;
    }

    // ── install ──────────────────────────────────────────────────────────────

    private static Command CreateInstallCommand(Option<string> outputOption)
    {
        var nameArg = new Argument<string[]>("name")
        {
            Description =
                "One or more package names from the catalog. " +
                "Omit when --file is supplied.",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var fileOption = new Option<string?>("--file")
        {
            Description =
                "Path to a local package YAML file. " +
                "Installs the package from the file rather than the catalog " +
                "(one-shot upload; ADR-0035 decision 13). " +
                "Mutually exclusive with positional names.",
        };

        // --input k=v (bare, single-target only) or --input <pkg>.<k>=<v> (multi-target).
        // Repeatable. Convention:
        //   single-target: --input github_owner=acme  → applied to the one in-flight package.
        //   multi-target : --input spring-voyage-oss.github_owner=acme → namespaced by package.
        //   --input-file  : top-level keys are package names (multi-target) or input keys
        //                   (single-target); nested keys are input values.
        var inputOption = new Option<string[]>("--input")
        {
            Description =
                "Input value for a package, repeatable. " +
                "For a single-target install use bare key=value: --input github_owner=acme. " +
                "For a multi-target install namespace by package: --input <pkg>.key=value. " +
                "Mixing bare and namespaced forms in the same invocation is an error.",
            AllowMultipleArgumentsPerToken = false,
        };

        var inputFileOption = new Option<string?>("--input-file")
        {
            Description =
                "Path to a YAML file supplying package inputs. " +
                "For single-target installs the file's top-level keys are input names. " +
                "For multi-target installs the file's top-level keys are package names " +
                "and each nested map's keys are input names.",
        };

        var command = new Command(
            "install",
            "Install one or more packages from the catalog (spring package install <name> [<name>...]) " +
            "or from a local file (spring package install --file <path>).\n\n" +
            "For single-target installs, supply inputs as bare key=value pairs: --input github_owner=acme.\n" +
            "For multi-target installs, namespace inputs by package: --input <pkg>.key=value.\n" +
            "Alternatively supply a YAML file via --input-file.\n\n" +
            "Exit codes: 0 = success, 2 = bad request / dep-graph error, 4 = name collision, 1 = server error.");
        command.Arguments.Add(nameArg);
        command.Options.Add(fileOption);
        command.Options.Add(inputOption);
        command.Options.Add(inputFileOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var names = parseResult.GetValue(nameArg) ?? Array.Empty<string>();
            var file = parseResult.GetValue(fileOption);
            var inputs = parseResult.GetValue(inputOption) ?? Array.Empty<string>();
            var inputFile = parseResult.GetValue(inputFileOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            var client = ClientFactory.Create();

            // Mutual exclusivity: --file and positional names
            if (!string.IsNullOrWhiteSpace(file) && names.Length > 0)
            {
                await Console.Error.WriteLineAsync(
                    "--file and positional package names are mutually exclusive. " +
                    "Supply --file for a local-file install, or one or more names for a catalog install.");
                Environment.Exit(2);
                return;
            }

            if (string.IsNullOrWhiteSpace(file) && names.Length == 0)
            {
                await Console.Error.WriteLineAsync(
                    "Supply at least one package name, or --file <path> for a local-file install.");
                Environment.Exit(2);
                return;
            }

            // ── file path ──────────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(file))
            {
                if (!File.Exists(file))
                {
                    await Console.Error.WriteLineAsync(
                        $"File not found: {file}");
                    Environment.Exit(2);
                    return;
                }

                SpringApiClient.PackageInstallResponse result;
                try
                {
                    result = await client.InstallPackageFromFileAsync(file, ct);
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync(
                        $"Install failed: {ex.Message}");
                    Environment.Exit(MapInstallException(ex));
                    return;
                }

                PrintInstallResult(result, output);
                if (result.Status == "failed")
                {
                    Environment.Exit(1);
                }
                return;
            }

            // ── catalog path ───────────────────────────────────────────────
            // Parse --input values and resolve per-target input maps.
            Dictionary<string, Dictionary<string, string>> perPackageInputs;
            try
            {
                perPackageInputs = ParseInputs(inputs, names, inputFile);
            }
            catch (ArgumentException ex)
            {
                await Console.Error.WriteLineAsync(ex.Message);
                Environment.Exit(2);
                return;
            }

            var targets = names
                .Select(n => new SpringApiClient.PackageInstallTargetRequest(
                    PackageName: n,
                    Inputs: perPackageInputs.TryGetValue(n, out var m) ? m : new Dictionary<string, string>()))
                .ToList();

            SpringApiClient.PackageInstallResponse catalogResult;
            try
            {
                catalogResult = await client.InstallPackagesAsync(targets, ct);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync(
                    $"Install failed: {ex.Message}");
                Environment.Exit(MapInstallException(ex));
                return;
            }

            PrintInstallResult(catalogResult, output);
            if (catalogResult.Status == "failed")
            {
                Environment.Exit(1);
            }
        });

        return command;
    }

    // ── status ───────────────────────────────────────────────────────────────

    private static Command CreateStatusCommand(Option<string> outputOption)
    {
        var installIdArg = new Argument<string>("install-id")
        {
            Description = "The install id returned by 'spring package install'.",
        };

        var command = new Command(
            "status",
            "Show the status of an install: aggregate phase, per-package state, " +
            "and activation errors if Phase 2 failed.");
        command.Arguments.Add(installIdArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var installId = parseResult.GetValue(installIdArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var result = await client.GetInstallStatusAsync(installId, ct);
            if (result is null)
            {
                await Console.Error.WriteLineAsync(
                    $"Install '{installId}' not found.");
                Environment.Exit(3);
                return;
            }

            PrintInstallResult(result, output);
        });

        return command;
    }

    // ── retry ────────────────────────────────────────────────────────────────

    private static Command CreateRetryCommand()
    {
        var installIdArg = new Argument<string>("install-id")
        {
            Description = "The install id to retry Phase 2 for.",
        };

        var command = new Command(
            "retry",
            "Re-run Phase 2 activation for a failed install. " +
            "Fix the underlying issue (Dapr placement, image pull, model probe) " +
            "before retrying.");
        command.Arguments.Add(installIdArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var installId = parseResult.GetValue(installIdArg)!;
            var client = ClientFactory.Create();

            var result = await client.RetryInstallAsync(installId, ct);
            if (result is null)
            {
                await Console.Error.WriteLineAsync(
                    $"Install '{installId}' not found.");
                Environment.Exit(3);
                return;
            }

            PrintInstallResult(result, "table");
            if (result.Status == "failed")
            {
                Environment.Exit(1);
            }
        });

        return command;
    }

    // ── abort ────────────────────────────────────────────────────────────────

    private static Command CreateAbortCommand()
    {
        var installIdArg = new Argument<string>("install-id")
        {
            Description = "The install id to abort. All staging rows will be deleted.",
        };

        var command = new Command(
            "abort",
            "Discard all staging rows for a failed install. " +
            "Use when a Phase-2 failure cannot be retried (e.g. the package " +
            "itself needs to be fixed). After abort the install is gone.");
        command.Arguments.Add(installIdArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var installId = parseResult.GetValue(installIdArg)!;
            var client = ClientFactory.Create();

            var found = await client.AbortInstallAsync(installId, ct);
            if (!found)
            {
                await Console.Error.WriteLineAsync(
                    $"Install '{installId}' not found.");
                Environment.Exit(3);
                return;
            }

            Console.WriteLine($"Install '{installId}' aborted. All staging rows removed.");
        });

        return command;
    }

    // ── export ───────────────────────────────────────────────────────────────

    private static Command CreateExportCommand(Option<string> outputOption)
    {
        var unitNameArg = new Argument<string>("unit-name")
        {
            Description =
                "Name of an installed unit to export the package from. " +
                "The server looks up the install record for this unit and " +
                "returns the original package.yaml.",
        };

        var withValuesOption = new Option<bool>("--with-values")
        {
            Description =
                "Materialise resolved input values in the exported YAML. " +
                "Secret inputs are emitted as placeholder references, never as cleartext.",
        };

        var outputPathOption = new Option<string?>("--output-file")
        {
            Description =
                "Write the exported YAML to this file instead of stdout. " +
                "For multi-file packages (tarball responses) a file path is required.",
        };

        var command = new Command(
            "export",
            "Export an installed package back to its original package.yaml. " +
            "Without --output-file writes to stdout. " +
            "With --with-values materialises resolved inputs (secrets exported as placeholders).");
        command.Arguments.Add(unitNameArg);
        command.Options.Add(withValuesOption);
        command.Options.Add(outputPathOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitName = parseResult.GetValue(unitNameArg)!;
            var withValues = parseResult.GetValue(withValuesOption);
            var outputFile = parseResult.GetValue(outputPathOption);
            var client = ClientFactory.Create();

            var result = await client.ExportPackageAsync(unitName, withValues, ct);
            if (result is null)
            {
                await Console.Error.WriteLineAsync(
                    $"No installed package found for unit '{unitName}'. " +
                    "Run 'spring package list' to see installed packages.");
                Environment.Exit(3);
                return;
            }

            // Guard: if the caller named an output file ending in .yaml but the
            // server returned a tarball, fail early rather than writing corrupt output.
            var isTarball = result.ContentType.Contains("tar") || result.ContentType.Contains("zip");
            if (isTarball
                && !string.IsNullOrWhiteSpace(outputFile)
                && (outputFile.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                    || outputFile.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)))
            {
                await Console.Error.WriteLineAsync(
                    $"The server returned a multi-file tarball but --output-file ends in .yaml. " +
                    $"Use a .tar.gz or .zip extension instead.");
                Environment.Exit(2);
                return;
            }

            if (!string.IsNullOrWhiteSpace(outputFile))
            {
                await File.WriteAllBytesAsync(outputFile, result.Content, ct);
                Console.WriteLine($"Exported to {outputFile}");
            }
            else
            {
                using var stdout = Console.OpenStandardOutput();
                await stdout.WriteAsync(result.Content, ct);
            }
        });

        return command;
    }

    // ── list ─────────────────────────────────────────────────────────────────

    private static Command CreateListCommand(Option<string> outputOption)
    {
        var command = new Command(
            "list",
            "List available packages with content counts. Run 'spring package show <name>' for detail.");

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var result = await client.ListPackagesAsync(ct);

            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(result, ListColumns));
        });

        return command;
    }

    // ── show ─────────────────────────────────────────────────────────────────

    private static Command CreateShowCommand(Option<string> outputOption)
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "Package name. Run 'spring package list' for available names.",
        };

        var command = new Command(
            "show",
            "Show the contents of a package: unit templates, agent templates, skills, connectors, and workflows.");
        command.Arguments.Add(nameArgument);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var name = parseResult.GetValue(nameArgument)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var detail = await client.GetPackageAsync(name, ct);
            if (detail is null)
            {
                await Console.Error.WriteLineAsync(
                    $"Package '{name}' not found. Run 'spring package list' to see available packages.");
                Environment.Exit(3);
                return;
            }

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJson(detail));
                return;
            }

            Console.WriteLine($"Package: {detail.Name}");
            if (!string.IsNullOrWhiteSpace(detail.Description))
            {
                Console.WriteLine($"  {detail.Description}");
            }

            WriteSection("Unit templates", detail.UnitTemplates, UnitTemplateColumns);
            WriteSection("Agent templates", detail.AgentTemplates, AgentTemplateColumns);
            WriteSection("Skills", detail.Skills, SkillColumns);
            WriteSection("Connectors", detail.Connectors, ConnectorColumns);
            WriteSection("Workflows", detail.Workflows, WorkflowColumns);
        });

        return command;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static void WriteSection<T>(
        string title,
        IReadOnlyList<T>? rows,
        IReadOnlyList<OutputFormatter.Column<T>> columns)
    {
        Console.WriteLine();
        Console.WriteLine($"{title} ({rows?.Count ?? 0}):");
        if (rows is null || rows.Count == 0)
        {
            Console.WriteLine("  (none)");
            return;
        }
        Console.WriteLine(OutputFormatter.FormatTable(rows, columns));
    }

    /// <summary>
    /// Prints an install result to stdout. JSON mode outputs the raw response;
    /// table mode prints install id, aggregate status, and per-package rows.
    /// </summary>
    private static void PrintInstallResult(SpringApiClient.PackageInstallResponse result, string output)
    {
        if (output == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }));
            return;
        }

        Console.WriteLine($"install-id : {result.InstallId}");
        Console.WriteLine($"status     : {result.Status}");
        if (result.StartedAt.HasValue)
        {
            Console.WriteLine($"started-at : {result.StartedAt:yyyy-MM-dd HH:mm:ss UTC}");
        }
        if (result.CompletedAt.HasValue)
        {
            Console.WriteLine($"completed  : {result.CompletedAt:yyyy-MM-dd HH:mm:ss UTC}");
        }
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            Console.WriteLine($"error      : {result.Error}");
        }

        if (result.Packages is { Count: > 0 } packages)
        {
            Console.WriteLine();
            Console.WriteLine("packages:");
            foreach (var pkg in packages)
            {
                Console.WriteLine($"  {pkg.PackageName,-40}  {pkg.State}");
                if (!string.IsNullOrWhiteSpace(pkg.ErrorMessage))
                {
                    Console.WriteLine($"    error: {pkg.ErrorMessage}");
                }
            }
        }
    }

    /// <summary>
    /// Parses <c>--input</c> tokens into a per-package input map.
    ///
    /// Convention (ADR-0035 decision 4 / issue brief):
    /// <list type="bullet">
    ///   <item>Bare <c>key=value</c>: allowed only for single-target installs.
    ///     Applied to the one in-flight package.</item>
    ///   <item>Namespaced <c>pkg.key=value</c>: required when more than one
    ///     package is in the batch; rejected if <c>pkg</c> is not in the batch.</item>
    ///   <item>Mixing bare and namespaced tokens in the same invocation is an error.</item>
    ///   <item><c>--input-file path.yaml</c>: YAML where top-level keys are package names
    ///     (multi-target) or bare input keys (single-target).</item>
    /// </list>
    /// </summary>
    public static Dictionary<string, Dictionary<string, string>> ParseInputs(
        IReadOnlyList<string> inputTokens,
        IReadOnlyList<string> packageNames,
        string? inputFilePath)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        // Initialise an empty map per package so callers always see the key.
        foreach (var pkg in packageNames)
        {
            result[pkg] = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        // Merge --input-file first; explicit --input flags override afterwards.
        if (!string.IsNullOrWhiteSpace(inputFilePath))
        {
            if (!File.Exists(inputFilePath))
            {
                throw new ArgumentException($"--input-file: file not found: {inputFilePath}");
            }
            var yamlText = File.ReadAllText(inputFilePath);
            var fileInputs = ParseInputYaml(yamlText, packageNames);
            foreach (var (pkg, map) in fileInputs)
            {
                if (!result.ContainsKey(pkg))
                {
                    result[pkg] = new Dictionary<string, string>(StringComparer.Ordinal);
                }
                foreach (var (k, v) in map)
                {
                    result[pkg][k] = v;
                }
            }
        }

        if (inputTokens.Count == 0)
        {
            return result;
        }

        // Classify tokens as "namespaced" (starts with <pkg>. where <pkg> matches a
        // known package name) or "bare" (key=value with no matching package prefix).
        // A token where the key part (before '=') contains a dot but the prefix
        // doesn't match any known package is an error — it looks like a namespaced
        // form with a wrong package name.
        var hasNamespaced = inputTokens.Any(t => HasPackagePrefix(t, packageNames));
        var hasBare = inputTokens.Any(t => !HasPackagePrefix(t, packageNames));

        if (hasBare && hasNamespaced)
        {
            throw new ArgumentException(
                "--input tokens mix bare key=value and namespaced <pkg>.key=value forms. " +
                "Use one form consistently. For a multi-target install every --input must be namespaced.");
        }

        if (hasBare && packageNames.Count > 1)
        {
            throw new ArgumentException(
                "--input must be namespaced as <package>.key=value when installing more than one package. " +
                $"Example: --input {packageNames[0]}.my_key=my_value");
        }

        foreach (var token in inputTokens)
        {
            if (hasNamespaced)
            {
                // Namespaced: find the longest matching package prefix.
                var matched = false;
                foreach (var pkg in packageNames)
                {
                    var prefix = pkg + ".";
                    if (token.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        var remainder = token.Substring(prefix.Length);
                        var (key, value) = SplitKeyValue(remainder, token);
                        result[pkg][key] = value;
                        matched = true;
                        break;
                    }
                }
                if (!matched)
                {
                    throw new ArgumentException(
                        $"--input '{token}': the package prefix does not match any package in the install batch. " +
                        $"Available packages: {string.Join(", ", packageNames)}");
                }
            }
            else
            {
                // Bare: single-target (guaranteed here). Validate: if the key part
                // (before '=') contains a dot, it looks like a namespaced form with
                // an unknown package prefix — reject it with a descriptive error.
                var eqIdx = token.IndexOf('=');
                var keyPart = eqIdx > 0 ? token[..eqIdx] : token;
                var dotIdx = keyPart.IndexOf('.');
                if (dotIdx > 0)
                {
                    var prefix = keyPart[..dotIdx];
                    // Only reject if the prefix doesn't match the single package name;
                    // plain dotted key names (e.g. "db.host=localhost") are unlikely
                    // but allowed as bare input keys.
                    if (!packageNames.Contains(prefix, StringComparer.Ordinal))
                    {
                        throw new ArgumentException(
                            $"--input '{token}': the package prefix '{prefix}' does not match any package in the install batch. " +
                            $"Available packages: {string.Join(", ", packageNames)}. " +
                            $"If '{keyPart}' is a bare input key name, the server will reject it; rename the input to avoid dots.");
                    }
                }

                var (k, v) = SplitKeyValue(token, token);
                result[packageNames[0]][k] = v;
            }
        }

        return result;
    }

    private static bool HasPackagePrefix(string token, IReadOnlyList<string> packageNames)
    {
        foreach (var pkg in packageNames)
        {
            if (token.StartsWith(pkg + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static (string key, string value) SplitKeyValue(string token, string originalToken)
    {
        var eq = token.IndexOf('=');
        if (eq <= 0)
        {
            throw new ArgumentException(
                $"--input '{originalToken}' is not in key=value form.");
        }
        return (token[..eq], token[(eq + 1)..]);
    }

    /// <summary>
    /// Parses a simple YAML input-file.
    ///
    /// <para>Multi-target: top-level keys are package names; each value is a mapping.</para>
    /// <para>Single-target: top-level keys are input names; values are scalars.</para>
    /// <para>We use a minimal YAML parser (line-by-line) to avoid taking a
    /// heavy YAML library dependency in the CLI for this simple shape.</para>
    /// </summary>
    public static Dictionary<string, Dictionary<string, string>> ParseInputYaml(
        string yamlText,
        IReadOnlyList<string> packageNames)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(yamlText))
        {
            return result;
        }

        // Use System.Text.Json to parse a JSON-compatible representation if possible.
        // Fall back to simple line-by-line YAML for the common scalar-values case.
        // The input file format is intentionally simple (scalars only; ADR-0035 decision 8).
        var lines = yamlText.Split('\n');
        string? currentPkg = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // Skip blank lines and YAML comments.
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var indent = line.Length - line.TrimStart().Length;
            var trimmed = line.TrimStart();

            // Remove YAML inline comment.
            var commentIdx = trimmed.IndexOf(" #", StringComparison.Ordinal);
            if (commentIdx > 0)
            {
                trimmed = trimmed[..commentIdx].TrimEnd();
            }

            if (!trimmed.Contains(':'))
            {
                continue;
            }

            var colonIdx = trimmed.IndexOf(':');
            var rawKey = trimmed[..colonIdx].Trim();
            var rawValue = trimmed[(colonIdx + 1)..].Trim().Trim('"').Trim('\'');

            if (indent == 0)
            {
                // Top-level key: either a package name (multi-target) or an input key (single-target).
                if (packageNames.Contains(rawKey, StringComparer.Ordinal))
                {
                    // Multi-target: this key is a package name.
                    currentPkg = rawKey;
                    if (!result.ContainsKey(currentPkg))
                    {
                        result[currentPkg] = new Dictionary<string, string>(StringComparer.Ordinal);
                    }
                }
                else
                {
                    // Single-target: top-level key is an input name.
                    currentPkg = null;
                    if (packageNames.Count == 1)
                    {
                        var singlePkg = packageNames[0];
                        if (!result.ContainsKey(singlePkg))
                        {
                            result[singlePkg] = new Dictionary<string, string>(StringComparer.Ordinal);
                        }
                        if (!string.IsNullOrWhiteSpace(rawValue))
                        {
                            result[singlePkg][rawKey] = rawValue;
                        }
                    }
                }
            }
            else if (currentPkg is not null && !string.IsNullOrWhiteSpace(rawValue))
            {
                // Nested key under a package block.
                result[currentPkg][rawKey] = rawValue;
            }
        }

        return result;
    }

    /// <summary>
    /// Maps an exception from the install API methods to an exit code.
    /// 400 → 2, 409 → 4, 5xx → 1.
    /// </summary>
    private static int MapInstallException(Exception ex)
    {
        // The HttpClient-based install methods throw InvalidOperationException
        // with the status code in the message text (e.g. "Request failed with status 400:").
        var msg = ex.Message;
        if (msg.Contains("400") || msg.Contains("Bad request"))
        {
            return 2;
        }
        if (msg.Contains("404") || msg.Contains("Not Found"))
        {
            return 3;
        }
        if (msg.Contains("409") || msg.Contains("Conflict"))
        {
            return 4;
        }
        return 1;
    }
}