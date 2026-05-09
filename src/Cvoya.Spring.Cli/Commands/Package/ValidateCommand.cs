// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands.Package;

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

using Cvoya.Spring.Manifest.Validation;

/// <summary>
/// Implements <c>spring package validate &lt;path&gt;</c> (#1680). Walks a
/// package directory tree and verifies the package would install cleanly
/// without contacting the running platform. Exit codes:
/// <list type="bullet">
///   <item><description><c>0</c> — clean (no errors).</description></item>
///   <item><description><c>1</c> — validation errors (or warnings under <c>--strict</c>).</description></item>
///   <item><description><c>2</c> — flag-parse / I/O failure (invalid path, unreadable file, etc.).</description></item>
/// </list>
/// </summary>
public static class ValidateCommand
{
    /// <summary>
    /// Builds the <c>validate</c> subcommand under <c>spring package</c>.
    /// </summary>
    public static Command Create()
    {
        var pathArg = new Argument<string>("path")
        {
            Description =
                "Path to the package directory (must contain package.yaml) " +
                "or to a package.yaml file directly. The validator walks the " +
                "manifest tree and reports schema, required-field, " +
                "cross-reference, and connector-slug findings without " +
                "contacting the platform.",
        };

        var strictOption = new Option<bool>("--strict")
        {
            Description =
                "Promote warnings to errors. Use in CI so a new warning " +
                "blocks merge instead of silently aging.",
        };

        var formatOption = new Option<string>("--format")
        {
            Description = "Output format: 'table' (default, human-readable) or 'json' (CI-parseable).",
            DefaultValueFactory = _ => "table",
        };
        formatOption.AcceptOnlyFromAmong("table", "json");

        var command = new Command(
            "validate",
            "Validate a package directory offline. Walks every YAML file under " +
            "units/, agents/, connectors/, workflows/, skills/ and reports " +
            "schema, required-field, cross-reference, and connector-slug " +
            "findings without installing the package or contacting the " +
            "running platform. Exit codes: 0 = clean, 1 = errors, 2 = I/O failure.");
        command.Arguments.Add(pathArg);
        command.Options.Add(strictOption);
        command.Options.Add(formatOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var path = parseResult.GetValue(pathArg)!;
            var strict = parseResult.GetValue(strictOption);
            var format = parseResult.GetValue(formatOption) ?? "table";

            // Accept a directory or a direct package.yaml path. The latter
            // is convenient for editors that pass the file under the cursor.
            string packageRoot;
            if (Directory.Exists(path))
            {
                packageRoot = path;
            }
            else if (File.Exists(path))
            {
                packageRoot = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            }
            else
            {
                await Console.Error.WriteLineAsync(
                    $"Path not found: {path}");
                Environment.Exit(2);
                return;
            }

            PackageValidationResult result;
            try
            {
                var source = new DirectoryPackageSource(packageRoot);
                result = await PackageValidator.ValidateAsync(source, ct);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync(
                    $"Validation failed: {ex.Message}");
                Environment.Exit(2);
                return;
            }

            // --strict promotes warnings to errors before rendering, so the
            // human-facing report and the JSON shape both reflect the gate.
            if (strict)
            {
                result = PromoteWarningsToErrors(result);
            }

            if (format == "json")
            {
                RenderJson(result, packageRoot);
            }
            else
            {
                RenderTable(result, packageRoot);
            }

            Environment.Exit(result.IsClean ? 0 : 1);
        });

        return command;
    }

    private static PackageValidationResult PromoteWarningsToErrors(PackageValidationResult result)
    {
        var promoted = result.Diagnostics
            .Select(d => d.Severity == PackageValidationSeverity.Warning
                ? d with { Severity = PackageValidationSeverity.Error }
                : d)
            .ToList();
        return new PackageValidationResult
        {
            Files = result.Files,
            Diagnostics = promoted,
        };
    }

    private static void RenderTable(PackageValidationResult result, string packageRoot)
    {
        Console.WriteLine($"Validating package at {packageRoot} ...");

        // Group diagnostics by file so a file with multiple findings prints
        // each line separately under that file.
        var byFile = result.Diagnostics
            .GroupBy(d => d.File)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // Compute the column width for "file" once across both ok and bad
        // rows so the report aligns regardless of which paths produced
        // diagnostics.
        var maxFileWidth = result.Files.Count == 0 ? 0 : result.Files.Max(f => f.Length);

        foreach (var file in result.Files)
        {
            if (byFile.TryGetValue(file, out var fileDiags))
            {
                foreach (var diag in fileDiags)
                {
                    var label = diag.Severity == PackageValidationSeverity.Error ? "ERROR" : "WARN";
                    Console.WriteLine(
                        $"  {file.PadRight(maxFileWidth)}  {label,-5}  {diag.Message}");
                }
            }
            else
            {
                Console.WriteLine($"  {file.PadRight(maxFileWidth)}  ok");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Result: {result.ErrorCount} error{(result.ErrorCount == 1 ? string.Empty : "s")}, " +
            $"{result.WarningCount} warning{(result.WarningCount == 1 ? string.Empty : "s")}.");
    }

    private static void RenderJson(PackageValidationResult result, string packageRoot)
    {
        // Stable shape — matches the brief in #1680. CI annotation parsers
        // can group on `file` and key on `code` for grouping under a single
        // GitHub annotation per kind.
        var payload = new
        {
            packageRoot,
            isClean = result.IsClean,
            errorCount = result.ErrorCount,
            warningCount = result.WarningCount,
            files = result.Files,
            diagnostics = result.Diagnostics.Select(d => new
            {
                file = d.File,
                severity = d.Severity == PackageValidationSeverity.Error ? "error" : "warning",
                code = d.Code,
                message = d.Message,
            }),
        };

        Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }));
    }
}
