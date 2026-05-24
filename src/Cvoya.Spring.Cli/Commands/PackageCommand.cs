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
using Cvoya.Spring.Cli.Utilities;

using Microsoft.Kiota.Abstractions;

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
        new("humanTemplates", p => p.HumanTemplateCount?.ToString()),
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

    /// <summary>
    /// Columns for the <c>humanTemplates:</c> list surfaced by
    /// <c>spring package show</c> (ADR-0046 §4). Each row describes one
    /// <c>HumanTemplate</c> shipped by the package; stamped via
    /// <c>- human: { from: &lt;name&gt; }</c> on a unit's <c>members:</c> list.
    /// </summary>
    private static readonly OutputFormatter.Column<HumanTemplateSummary>[] HumanTemplateColumns =
    {
        new("name", h => h.Name),
        new("displayName", h => h.DisplayName),
        new("description", h => h.Description),
        new("path", h => h.Path),
    };

    /// <summary>
    /// Columns for the <c>connectorDeclarations:</c> block surfaced by
    /// <c>spring package show</c>. Operators read this to discover which
    /// <c>--connector</c> flags they need at install time. ADR-0037 D3 —
    /// the slug list is the union of every artefact's <c>requires:</c>
    /// block; every entry is required (no optional flag).
    /// </summary>
    private static readonly OutputFormatter.Column<RequiredConnectorSummary>[] RequiredConnectorColumns =
    {
        new("type", c => c.Type),
        new("required", c => c.Required == true ? "yes" : "no"),
    };

    /// <summary>
    /// Columns for the manifest's <c>content:</c> list surfaced by
    /// <c>spring package show</c> (#1718 item 2). Replaces the old
    /// per-kind sections (unit templates, agent templates, skills,
    /// workflows) that the manifest no longer enumerates as flat lists —
    /// the wizard / operator now sees a single ordered "what gets
    /// installed" list with the artefact discriminator.
    /// </summary>
    private static readonly OutputFormatter.Column<PackageContentEntry>[] ContentColumns =
    {
        new("kind", c => c.Kind),
        new("name", c => c.Name),
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
        // #1680: offline validator for the in-tree CI gate and operator
        // pre-publish checks. No --output binding because the table/json
        // selector (--format) lives on the subcommand itself, mirroring the
        // dotnet CLI conventions for verb-specific output shapes.
        packageCommand.Subcommands.Add(Package.ValidateCommand.Create());

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

        // #1673: --connector flag — repeatable. Two forms:
        //   short:  github=owner/repo@installation-id           (binds at package scope)
        //           github:unit-name=other-org/other-repo@id    (per-unit override)
        //   long:   github.installation-id=12345                (long-form key=value)
        //           github.owner=acme                            (multiple invocations
        //           github.repo=spring-voyage                    build the same payload)
        // The flag binds to the single in-flight package by default; multi-
        // target installs use the same `<pkg>.connector=...` namespacing as
        // --input.
        var connectorOption = new Option<string[]>("--connector")
        {
            Description =
                "Connector binding for a required package connector, repeatable. " +
                "Short form (github only today): --connector github=owner/repo@installation-id. " +
                "Per-unit override: --connector github:unit-name=owner/repo@installation-id. " +
                "Long form (any connector): --connector github.installation-id=12345 " +
                "--connector github.owner=acme. " +
                "For multi-target installs, namespace by package: " +
                "--connector <pkg>.<slug>=... or <pkg>.<slug>.<key>=value.",
            AllowMultipleArgumentsPerToken = false,
        };

        var versionOption = new Option<string?>("--version")
        {
            Description =
                "Pin the install to a specific package version (ADR-0037 D5). " +
                "When omitted, the install resolves to the most recently installed " +
                "version of the package. For multi-target installs, supply once per " +
                "target name in declaration order.",
            AllowMultipleArgumentsPerToken = false,
        };

        // ADR-0043 §6: --into <unit-ref> binds the package's top-level
        // artefacts to the named unit instead of the tenant. Applies to
        // every target in a multi-target install. `--into tenant` is
        // the explicit form of the default.
        var intoOption = new Option<string?>("--into")
        {
            Description =
                "Bind the package's top-level artefacts to the named unit instead of the tenant " +
                "(ADR-0043 §6). Accepts a unit display name or Guid. " +
                "Use --into tenant for the explicit default. " +
                "Applies to every target in a multi-target install.",
        };

        // #2310: --as <display-name> overrides the display name of the
        // package's single top-level activatable. Useful when installing
        // the same package multiple times so the resulting units / agents
        // can be told apart in the UI. Rejected by the server with
        // `code: AmbiguousDisplayName` (400) when the package ships
        // multiple top-level activatables.
        var asOption = new Option<string?>("--as")
        {
            Description =
                "Override the display name of the package's single top-level activatable " +
                "(#2310). Useful when installing the same package multiple times. " +
                "Applies to every target in a multi-target install. " +
                "Rejected when any target's package ships more than one top-level activatable.",
        };

        // #2159: --secret flag — repeatable. Form:
        //   <provider>:<auth-method>=<value>                    (single-target install)
        //   <pkg>.<provider>:<auth-method>=<value>              (multi-target install)
        // The pre-flight derives required credentials from each unit's
        // (runtime, provider) edge and writes the supplied value as a
        // tenant-scoped secret keyed by `{provider}-{auth-method-slug}`.
        // Interactive TTY installs that omit a required credential are
        // prompted for it after the first failed attempt.
        var secretOption = new Option<string[]>("--secret")
        {
            Description =
                "LLM credential for the package's runtimes, repeatable. " +
                "Form: --secret <provider>:<auth-method>=<value> " +
                "(e.g. --secret anthropic:oauth=sk-ant-oat-... or --secret openai:api-key=sk-...). " +
                "For multi-target installs, namespace by package: --secret <pkg>.<provider>:<auth-method>=<value>. " +
                "Required credentials missing from the request prompt the operator " +
                "interactively when stdin is a TTY; otherwise the install fails with " +
                "a structured CredentialsMissing error listing each missing slot.",
            AllowMultipleArgumentsPerToken = false,
        };

        var command = new Command(
            "install",
            "Install one or more packages from the catalog (spring package install <name> [<name>...]) " +
            "or from a local file (spring package install --file <path>).\n\n" +
            "For single-target installs, supply inputs as bare key=value pairs: --input github_owner=acme.\n" +
            "For multi-target installs, namespace inputs by package: --input <pkg>.key=value.\n" +
            "Alternatively supply a YAML file via --input-file.\n\n" +
            "Required connectors: supply with --connector <slug>=<owner>/<repo>@<installation-id> " +
            "(github short form) or --connector <slug>.<key>=<value> (long form, generic). " +
            "Per-unit override: --connector <slug>:<unit-name>=...\n\n" +
            "Version pinning (ADR-0037 D5): use --version <v> to install a specific package version. " +
            "When omitted the install resolves to the most recently installed version.\n\n" +
            "Display name (#2310): use --as <name> to override the display name of the " +
            "package's single top-level activatable. Useful when installing the same package " +
            "multiple times.\n\n" +
            "Exit codes: 0 = success, 2 = bad request / dep-graph error, 1 = server error.");
        command.Arguments.Add(nameArg);
        command.Options.Add(fileOption);
        command.Options.Add(inputOption);
        command.Options.Add(inputFileOption);
        command.Options.Add(connectorOption);
        command.Options.Add(versionOption);
        command.Options.Add(secretOption);
        command.Options.Add(intoOption);
        command.Options.Add(asOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var names = parseResult.GetValue(nameArg) ?? Array.Empty<string>();
            var file = parseResult.GetValue(fileOption);
            var inputs = parseResult.GetValue(inputOption) ?? Array.Empty<string>();
            var inputFile = parseResult.GetValue(inputFileOption);
            var connectorTokens = parseResult.GetValue(connectorOption) ?? Array.Empty<string>();
            var secretTokens = parseResult.GetValue(secretOption) ?? Array.Empty<string>();
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
                catch (ApiException ex)
                {
                    await Console.Error.WriteLineAsync(
                        $"Install failed: {ProblemDetailsTranslator.Format(ex)}");
                    Environment.Exit(MapInstallException(ex));
                    return;
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

            // #1673: parse --connector flags into the wire connectorBindings
            // shape. Validation of slugs against the connector registry
            // happens server-side; the CLI only enforces well-formedness so
            // a quick local typo surfaces before the round-trip.
            Dictionary<string, SpringApiClient.PackageConnectorBindingsRequest>? perPackageBindings;
            try
            {
                perPackageBindings = ParseConnectorTokens(connectorTokens, names);
            }
            catch (ArgumentException ex)
            {
                await Console.Error.WriteLineAsync(ex.Message);
                Environment.Exit(2);
                return;
            }

            // #2159: parse --secret flags into per-package credential
            // payloads. Validation of (provider, authMethod) edges against
            // the runtime catalogue happens server-side; the CLI only
            // enforces well-formedness so a typo surfaces locally.
            Dictionary<string, List<SpringApiClient.CredentialBindingPayloadRequest>> perPackageSecrets;
            try
            {
                perPackageSecrets = ParseSecretTokens(secretTokens, names);
            }
            catch (ArgumentException ex)
            {
                await Console.Error.WriteLineAsync(ex.Message);
                Environment.Exit(2);
                return;
            }

            // ADR-0037 D5: optional --version pin applies to every named target.
            // For multi-target installs the same version flag scopes all targets;
            // when more granular per-target pinning is needed operators install
            // per-target with separate invocations.
            var versionPin = parseResult.GetValue(versionOption);

            // ADR-0043 §6: --into <unit-ref> applies to every target in
            // the install. There is one --into per `spring package install`
            // invocation; per-target overrides are not supported in v0.1.
            var intoRef = parseResult.GetValue(intoOption);

            // #2310: --as <name> applies to every target. The server
            // rejects the flag with code: AmbiguousDisplayName when any
            // target's package has more than one top-level activatable.
            var asName = parseResult.GetValue(asOption);

            List<SpringApiClient.PackageInstallTargetRequest> BuildTargets() => names
                .Select(n => new SpringApiClient.PackageInstallTargetRequest(
                    PackageName: n,
                    Inputs: perPackageInputs.TryGetValue(n, out var m) ? m : new Dictionary<string, string>(),
                    ConnectorBindings: perPackageBindings is not null
                        && perPackageBindings.TryGetValue(n, out var b) ? b : null,
                    Version: versionPin,
                    Credentials: perPackageSecrets.TryGetValue(n, out var s) && s.Count > 0
                        ? s.ToList()
                        : null,
                    IntoUnit: string.IsNullOrWhiteSpace(intoRef) ? null : intoRef,
                    DisplayName: string.IsNullOrWhiteSpace(asName) ? null : asName))
                .ToList();

            SpringApiClient.PackageInstallResponse catalogResult;
            try
            {
                catalogResult = await client.InstallPackagesAsync(BuildTargets(), ct);
            }
            catch (ProblemDetails problem) when (
                ProblemDetailsTranslator.GetCode(problem) == "CredentialsMissing"
                && !Console.IsInputRedirected)
            {
                // #2159: TTY recovery — prompt for each missing credential
                // and retry once. Non-TTY callers fall through to the
                // generic ApiException handler so scripts see the
                // structured CredentialsMissing error verbatim.
                if (!await TryPromptForMissingCredentialsAsync(problem, names, perPackageSecrets, ct))
                {
                    Environment.Exit(MapInstallException(problem));
                    return;
                }
                try
                {
                    catalogResult = await client.InstallPackagesAsync(BuildTargets(), ct);
                }
                catch (ApiException ex)
                {
                    await Console.Error.WriteLineAsync(
                        $"Install failed: {ProblemDetailsTranslator.Format(ex)}");
                    Environment.Exit(MapInstallException(ex));
                    return;
                }
            }
            catch (ApiException ex)
            {
                await Console.Error.WriteLineAsync(
                    $"Install failed: {ProblemDetailsTranslator.Format(ex)}");
                Environment.Exit(MapInstallException(ex));
                return;
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

        var versionOption = new Option<string?>("--version")
        {
            Description =
                "Show a specific package version (ADR-0037 D5). When omitted " +
                "the catalog returns the version it has on disk.",
            AllowMultipleArgumentsPerToken = false,
        };

        var command = new Command(
            "show",
            "Show the contents of a package: unit templates, agent templates, skills, connectors, and workflows. " +
            "Use --version <v> to address a specific package version (ADR-0037 D5).");
        command.Arguments.Add(nameArgument);
        command.Options.Add(versionOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var name = parseResult.GetValue(nameArgument)!;
            var version = parseResult.GetValue(versionOption);
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var detail = await client.GetPackageAsync(name, version, ct);
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
            if (!string.IsNullOrWhiteSpace(detail.Version))
            {
                Console.WriteLine($"  Version: {detail.Version}");
            }

            WriteSection("Required connectors", detail.ConnectorDeclarations, RequiredConnectorColumns);
            // #1718 item 2: the manifest's `content:` list is the
            // canonical "what gets installed" view. Render it first so
            // operators see the install footprint before the on-disk
            // template browse (which lists everything in units/ /
            // agents/ / skills/ / etc., not just what's declared).
            WriteSection("Content", detail.Content, ContentColumns);
            WriteSection("Unit templates", detail.UnitTemplates, UnitTemplateColumns);
            WriteSection("Agent templates", detail.AgentTemplates, AgentTemplateColumns);
            WriteSection("Skills", detail.Skills, SkillColumns);
            WriteSection("Human templates", detail.HumanTemplates, HumanTemplateColumns);
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

    /// <summary>
    /// Parses <c>--connector</c> tokens into a per-package connector-binding
    /// payload (#1673). Forms accepted:
    /// <list type="bullet">
    ///   <item>
    ///     Short github form: <c>github=owner/repo@installation-id</c> binds at
    ///     package scope; <c>github:unit-name=owner/repo@installation-id</c>
    ///     binds at unit scope.
    ///   </item>
    ///   <item>
    ///     Long form: <c>slug.key=value</c> — multiple invocations build the
    ///     same payload. <c>slug:unit.key=value</c> for unit-scope long form.
    ///   </item>
    ///   <item>
    ///     Multi-target prefix: <c>&lt;pkg&gt;.&lt;slug&gt;=...</c> or
    ///     <c>&lt;pkg&gt;.&lt;slug&gt;.&lt;key&gt;=value</c>.
    ///   </item>
    /// </list>
    /// Returns <c>null</c> when no tokens were supplied.
    /// </summary>
    public static Dictionary<string, SpringApiClient.PackageConnectorBindingsRequest>? ParseConnectorTokens(
        IReadOnlyList<string> tokens,
        IReadOnlyList<string> packageNames)
    {
        if (tokens.Count == 0)
        {
            return null;
        }

        // Per-package working state. Keys: pkg → slug → (config dict, unitName?).
        // For unit-scope, we collect into a separate map per (pkg, unit, slug).
        // The wire shape we emit at the end is package + units.
        var perPkg = new Dictionary<string, Dictionary<string, Dictionary<string, object>>>(StringComparer.Ordinal);
        var perPkgUnit = new Dictionary<string, Dictionary<string, Dictionary<string, Dictionary<string, object>>>>(StringComparer.Ordinal);

        foreach (var raw in tokens)
        {
            var token = raw;
            string? pkg = null;

            // Multi-target: peel off the package prefix when it matches.
            if (packageNames.Count > 1 || (packageNames.Count == 1
                && token.StartsWith(packageNames[0] + ".", StringComparison.Ordinal)
                && IsLikelyPackagePrefixed(token, packageNames)))
            {
                foreach (var p in packageNames)
                {
                    if (token.StartsWith(p + ".", StringComparison.Ordinal))
                    {
                        pkg = p;
                        token = token.Substring(p.Length + 1);
                        break;
                    }
                }
                if (pkg is null && packageNames.Count > 1)
                {
                    throw new ArgumentException(
                        $"--connector '{raw}': the package prefix does not match any package in the install batch. " +
                        $"Available packages: {string.Join(", ", packageNames)}");
                }
            }
            pkg ??= packageNames.Count > 0 ? packageNames[0] : string.Empty;

            // Split key=value.
            var eqIdx = token.IndexOf('=');
            if (eqIdx <= 0)
            {
                throw new ArgumentException(
                    $"--connector '{raw}' is not in <slug>[:<unit>][.<key>]=<value> form.");
            }
            var keyPart = token.Substring(0, eqIdx);
            var valuePart = token.Substring(eqIdx + 1);

            // Identify slug, optional unit, optional key path.
            // <slug>:<unit>(.<key>)? or <slug>(.<key>)?
            string slug;
            string? unitName = null;
            string? configKey = null;

            var dotIdx = keyPart.IndexOf('.');
            string slugPart = dotIdx > 0 ? keyPart.Substring(0, dotIdx) : keyPart;
            if (dotIdx > 0)
            {
                configKey = keyPart.Substring(dotIdx + 1);
            }

            var colonIdx = slugPart.IndexOf(':');
            if (colonIdx > 0)
            {
                slug = slugPart.Substring(0, colonIdx);
                unitName = slugPart.Substring(colonIdx + 1);
            }
            else
            {
                slug = slugPart;
            }

            if (string.IsNullOrWhiteSpace(slug))
            {
                throw new ArgumentException(
                    $"--connector '{raw}': missing connector slug.");
            }

            // Resolve the destination config dictionary.
            Dictionary<string, object> targetConfig;
            if (unitName is null)
            {
                if (!perPkg.TryGetValue(pkg, out var bySlug))
                {
                    bySlug = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                    perPkg[pkg] = bySlug;
                }
                if (!bySlug.TryGetValue(slug, out targetConfig!))
                {
                    targetConfig = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    bySlug[slug] = targetConfig;
                }
            }
            else
            {
                if (!perPkgUnit.TryGetValue(pkg, out var byUnit))
                {
                    byUnit = new Dictionary<string, Dictionary<string, Dictionary<string, object>>>(StringComparer.OrdinalIgnoreCase);
                    perPkgUnit[pkg] = byUnit;
                }
                if (!byUnit.TryGetValue(unitName, out var bySlug))
                {
                    bySlug = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                    byUnit[unitName] = bySlug;
                }
                if (!bySlug.TryGetValue(slug, out targetConfig!))
                {
                    targetConfig = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    bySlug[slug] = targetConfig;
                }
            }

            if (configKey is null)
            {
                // Short-form value. Today only github is recognised; fall
                // through to long form for any other slug to keep the parser
                // generic.
                if (string.Equals(slug, "github", StringComparison.OrdinalIgnoreCase))
                {
                    ParseGithubShortForm(valuePart, targetConfig, raw);
                }
                else
                {
                    throw new ArgumentException(
                        $"--connector '{raw}': short form is only defined for 'github'. Use --connector {slug}.<key>=<value> for other connectors.");
                }
            }
            else
            {
                // Long form: slug.key=value (or slug.key.subkey=...). Today
                // we treat the remainder as a flat key. Numbers convert to
                // long when parseable (so installation-id stays numeric in
                // the JSON payload).
                //
                // Issue #2563: array-shaped config keys (label / author /
                // path filters on the GitHub connector — and any future
                // connector that names its array fields the same way) are
                // detected by suffix and stored as a list. Repeating the
                // flag appends; a single comma-separated value splits.
                if (IsArrayConfigKey(configKey))
                {
                    var pieces = SplitArrayValue(valuePart);
                    if (!targetConfig.TryGetValue(configKey, out var existing)
                        || existing is not List<string> existingList)
                    {
                        existingList = new List<string>(pieces.Count);
                        targetConfig[configKey] = existingList;
                    }
                    foreach (var piece in pieces)
                    {
                        existingList.Add(piece);
                    }
                }
                else
                {
                    targetConfig[configKey] = ParseScalar(valuePart);
                }
            }
        }

        // Project to the wire shape.
        var result = new Dictionary<string, SpringApiClient.PackageConnectorBindingsRequest>(StringComparer.Ordinal);
        var allPkgs = new HashSet<string>(perPkg.Keys, StringComparer.Ordinal);
        allPkgs.UnionWith(perPkgUnit.Keys);

        foreach (var p in allPkgs)
        {
            Dictionary<string, SpringApiClient.ConnectorBindingPayloadRequest>? pkgScope = null;
            if (perPkg.TryGetValue(p, out var bySlug))
            {
                pkgScope = bySlug.ToDictionary(
                    kv => kv.Key,
                    kv => new SpringApiClient.ConnectorBindingPayloadRequest(kv.Value),
                    StringComparer.Ordinal);
            }

            Dictionary<string, IReadOnlyDictionary<string, SpringApiClient.ConnectorBindingPayloadRequest>>? unitScope = null;
            if (perPkgUnit.TryGetValue(p, out var byUnit))
            {
                unitScope = byUnit.ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyDictionary<string, SpringApiClient.ConnectorBindingPayloadRequest>)kv.Value.ToDictionary(
                        s => s.Key,
                        s => new SpringApiClient.ConnectorBindingPayloadRequest(s.Value),
                        StringComparer.Ordinal),
                    StringComparer.Ordinal);
            }

            result[p] = new SpringApiClient.PackageConnectorBindingsRequest(pkgScope, unitScope);
        }

        return result;
    }

    private static bool IsLikelyPackagePrefixed(string token, IReadOnlyList<string> packageNames)
    {
        foreach (var p in packageNames)
        {
            if (token.StartsWith(p + ".", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Parses the short github form <c>owner/repo@installation-id</c> into
    /// the canonical config keys (<c>owner</c>, <c>repo</c>,
    /// <c>installation-id</c>). Operators using the long form
    /// (<c>--connector github.owner=...</c>) get the same payload.
    /// </summary>
    private static void ParseGithubShortForm(
        string value,
        Dictionary<string, object> config,
        string rawToken)
    {
        var atIdx = value.LastIndexOf('@');
        if (atIdx <= 0)
        {
            throw new ArgumentException(
                $"--connector '{rawToken}': github short form is owner/repo@installation-id.");
        }
        var ownerRepo = value.Substring(0, atIdx);
        var idStr = value.Substring(atIdx + 1);
        var slashIdx = ownerRepo.IndexOf('/');
        if (slashIdx <= 0)
        {
            throw new ArgumentException(
                $"--connector '{rawToken}': github short form requires owner/repo.");
        }
        config["owner"] = ownerRepo.Substring(0, slashIdx);
        config["repo"] = ownerRepo.Substring(slashIdx + 1);
        if (!long.TryParse(idStr, out var id))
        {
            throw new ArgumentException(
                $"--connector '{rawToken}': installation-id '{idStr}' is not numeric.");
        }
        config["installation-id"] = id;
    }

    private static object ParseScalar(string value)
    {
        if (long.TryParse(value, out var l)) return l;
        if (bool.TryParse(value, out var b)) return b;
        return value;
    }

    /// <summary>
    /// Issue #2563: detect connector-config keys whose wire shape is an
    /// array. The matcher is intentionally suffix-based (and not GitHub-
    /// specific) — every connector's array slots in
    /// <c>UnitGitHubConfig</c>-shaped configs end in
    /// <c>_labels</c>, <c>_authors</c>, or <c>_paths</c>. Adding another
    /// array key on any connector just needs to match one of those
    /// suffixes for <c>--connector slug.key=...</c> to accept multi-
    /// value semantics.
    /// </summary>
    private static bool IsArrayConfigKey(string key)
    {
        return key.EndsWith("_labels", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_authors", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_paths", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Splits a <c>--connector slug.array_key=v1,v2,v3</c> value on
    /// commas, trims, and drops empties. Keeps each piece as a raw
    /// string — wildcard patterns (<c>*</c>, <c>prefix:*</c>) flow
    /// through untouched so the server-side evaluator sees what the
    /// operator typed.
    /// </summary>
    private static IReadOnlyList<string> SplitArrayValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Array.Empty<string>();
        }
        var out_ = new List<string>();
        foreach (var piece in value.Split(','))
        {
            var trimmed = piece.Trim();
            if (trimmed.Length > 0)
            {
                out_.Add(trimmed);
            }
        }
        return out_;
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
    /// Maps an install API exception to the documented exit code:
    /// 400 → 2, 404 → 3, 409 → 4, anything else → 1. Non-<see cref="ApiException"/>
    /// failures (cancellation, JSON-shape errors, …) collapse to 1.
    /// </summary>
    private static int MapInstallException(Exception ex)
        => ex is ApiException apiException ? MapInstallException(apiException) : 1;

    private static int MapInstallException(ApiException ex)
        => ex.ResponseStatusCode switch
        {
            400 => 2,
            404 => 3,
            409 => 4,
            _ => 1,
        };

    /// <summary>
    /// #2159: parse <c>--secret</c> tokens into per-package credential
    /// payloads. Forms accepted:
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>&lt;provider&gt;:&lt;auth-method&gt;=&lt;value&gt;</c> — single-target installs.
    ///   </description></item>
    ///   <item><description>
    ///     <c>&lt;pkg&gt;.&lt;provider&gt;:&lt;auth-method&gt;=&lt;value&gt;</c> — multi-target installs.
    ///   </description></item>
    /// </list>
    /// Throws <see cref="ArgumentException"/> on malformed tokens.
    /// </summary>
    public static Dictionary<string, List<SpringApiClient.CredentialBindingPayloadRequest>> ParseSecretTokens(
        IReadOnlyList<string> tokens,
        IReadOnlyList<string> names)
    {
        var perPackage = new Dictionary<string, List<SpringApiClient.CredentialBindingPayloadRequest>>(
            StringComparer.OrdinalIgnoreCase);
        if (tokens.Count == 0)
        {
            return perPackage;
        }

        var packageSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        if (packageSet.Count == 0)
        {
            throw new ArgumentException(
                "--secret requires at least one positional package name.");
        }

        foreach (var raw in tokens)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }
            var eqIndex = raw.IndexOf('=');
            if (eqIndex < 0)
            {
                throw new ArgumentException(
                    $"--secret '{raw}': missing '='. Form: <provider>:<auth-method>=<value>.");
            }
            var key = raw[..eqIndex];
            var value = raw[(eqIndex + 1)..];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    $"--secret '{raw}': value after '=' is empty.");
            }

            // Optional package prefix: <pkg>.<provider>:<auth>
            string targetPackage;
            string providerAuth;
            if (packageSet.Count > 1)
            {
                var dotIndex = key.IndexOf('.');
                if (dotIndex < 0 || !packageSet.Contains(key[..dotIndex]))
                {
                    throw new ArgumentException(
                        $"--secret '{raw}': multi-target installs require a package prefix " +
                        $"(<pkg>.<provider>:<auth-method>=<value>).");
                }
                targetPackage = names.First(n => string.Equals(n, key[..dotIndex], StringComparison.OrdinalIgnoreCase));
                providerAuth = key[(dotIndex + 1)..];
            }
            else
            {
                // Single target — accept either bare or namespaced.
                targetPackage = names[0];
                var dotIndex = key.IndexOf('.');
                providerAuth = dotIndex >= 0 && string.Equals(
                    key[..dotIndex], targetPackage, StringComparison.OrdinalIgnoreCase)
                    ? key[(dotIndex + 1)..]
                    : key;
            }

            var colonIndex = providerAuth.IndexOf(':');
            if (colonIndex <= 0 || colonIndex == providerAuth.Length - 1)
            {
                throw new ArgumentException(
                    $"--secret '{raw}': key must be '<provider>:<auth-method>' " +
                    $"(e.g. 'anthropic:oauth' or 'openai:api-key').");
            }
            var provider = providerAuth[..colonIndex].Trim();
            var authMethod = providerAuth[(colonIndex + 1)..].Trim();
            if (authMethod is not ("oauth" or "api-key"))
            {
                throw new ArgumentException(
                    $"--secret '{raw}': auth method must be 'oauth' or 'api-key' (got '{authMethod}').");
            }

            if (!perPackage.TryGetValue(targetPackage, out var list))
            {
                list = new List<SpringApiClient.CredentialBindingPayloadRequest>();
                perPackage[targetPackage] = list;
            }
            list.Add(new SpringApiClient.CredentialBindingPayloadRequest(provider, authMethod, value));
        }

        return perPackage;
    }

    /// <summary>
    /// #2159: TTY-only prompt for missing credentials. The structured
    /// `missing` list on a CredentialsMissing ProblemDetails tells us
    /// which (provider, authMethod) edges are unsatisfied. We prompt for
    /// each, append to the per-package payload, and let the caller retry
    /// the install once. Returns false when the user supplied no values
    /// (Ctrl-D / empty everywhere) so the caller surfaces the original
    /// structured error rather than retrying with a no-op delta.
    /// </summary>
    private static async Task<bool> TryPromptForMissingCredentialsAsync(
        ProblemDetails problem,
        IReadOnlyList<string> packageNames,
        Dictionary<string, List<SpringApiClient.CredentialBindingPayloadRequest>> perPackageSecrets,
        CancellationToken ct)
    {
        await Console.Error.WriteLineAsync(
            $"Install needs credentials: {ProblemDetailsTranslator.TranslateProblemDetails(problem)}");

        var missing = ExtractMissingCredentials(problem);
        if (missing.Count == 0)
        {
            return false;
        }

        var anySupplied = false;
        // For multi-target installs we don't know which package the
        // missing credential belongs to (the server aggregates across
        // every package in the batch); attach it to all targets so the
        // re-resolve picks it up.
        foreach (var entry in missing)
        {
            if (ct.IsCancellationRequested) return false;
            var label = entry.CredentialEnvVar ?? entry.SecretName ?? $"{entry.Provider}/{entry.AuthMethod}";
            await Console.Error.WriteAsync($"  paste value for {label}: ");
            var line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                await Console.Error.WriteLineAsync($"  (skipped — no value provided for {label})");
                continue;
            }
            anySupplied = true;
            var payload = new SpringApiClient.CredentialBindingPayloadRequest(
                entry.Provider, entry.AuthMethod, line);
            foreach (var pkg in packageNames)
            {
                if (!perPackageSecrets.TryGetValue(pkg, out var list))
                {
                    list = new List<SpringApiClient.CredentialBindingPayloadRequest>();
                    perPackageSecrets[pkg] = list;
                }
                list.Add(payload);
            }
        }
        return anySupplied;
    }

    private static IReadOnlyList<MissingCredentialEntry> ExtractMissingCredentials(ProblemDetails problem)
    {
        if (problem.AdditionalData is null
            || !problem.AdditionalData.TryGetValue("missing", out var raw)
            || raw is not System.Text.Json.JsonElement element
            || element.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return Array.Empty<MissingCredentialEntry>();
        }

        var result = new List<MissingCredentialEntry>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            var provider = item.TryGetProperty("provider", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String
                ? p.GetString() : null;
            var authMethod = item.TryGetProperty("authMethod", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.String
                ? a.GetString() : null;
            if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(authMethod)) continue;
            var secretName = item.TryGetProperty("secretName", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String
                ? s.GetString() : null;
            var envVar = item.TryGetProperty("credentialEnvVar", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.String
                ? e.GetString() : null;
            result.Add(new MissingCredentialEntry(provider!, authMethod!, secretName, envVar));
        }
        return result;
    }

    private sealed record MissingCredentialEntry(
        string Provider,
        string AuthMethod,
        string? SecretName,
        string? CredentialEnvVar);
}
