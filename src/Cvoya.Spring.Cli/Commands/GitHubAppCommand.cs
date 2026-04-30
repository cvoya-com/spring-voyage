// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Cli.GitHubApp;

/// <summary>
/// Builds the <c>spring github-app</c> verb tree. Today the tree has a
/// single verb — <c>register</c> — that drives GitHub's App-from-manifest
/// flow end-to-end: build manifest → bind local callback listener → open
/// browser → receive conversion code → exchange it for the PEM + webhook
/// secret → persist credentials. See issue #631 for the flow spec.
/// </summary>
/// <remarks>
/// The verb is intentionally not behind the authenticated API client.
/// Its whole job is to get the OSS platform to a state where the API
/// client has something to authenticate against — asking the operator
/// to hand-register an App first is the friction we're removing.
/// </remarks>
public static class GitHubAppCommand
{
    /// <summary>
    /// Entry point — builds the <c>github-app</c> command subtree.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var cmd = new Command(
            "github-app",
            "Helpers for the GitHub App that backs the GitHub connector. " +
            "Run `spring github-app register` once per deployment to drop the ~10 " +
            "manual steps of the GitHub docs down to one browser click.");

        cmd.Subcommands.Add(CreateRegisterCommand(outputOption));
        cmd.Subcommands.Add(CreateRotateKeyCommand());
        cmd.Subcommands.Add(CreateRotateWebhookSecretCommand());

        return cmd;
    }

    // ------------------------------------------------------------------
    // register
    // ------------------------------------------------------------------

    private static Command CreateRegisterCommand(Option<string> outputOption)
    {
        var nameOption = new Option<string>("--name")
        {
            Description = "App name on github.com. MUST be globally unique.",
            Required = true,
        };
        var orgOption = new Option<string?>("--org")
        {
            Description =
                "Register under this GitHub organisation (slug) instead of the " +
                "authenticated user's personal account.",
        };
        var webhookOption = new Option<string?>("--webhook-url")
        {
            Description =
                "Override the webhook-receiver URL. Defaults to " +
                "<deployment-origin>/api/v1/webhooks/github, derived from the CLI's " +
                "configured endpoint.",
        };
        var writeEnvOption = new Option<bool>("--write-env")
        {
            Description =
                "Append the resolved credentials to deployment/spring.env (the " +
                "default persistence target — zero runtime dependencies).",
            DefaultValueFactory = _ => false,
        };
        var writeSecretsOption = new Option<bool>("--write-secrets")
        {
            Description =
                "Persist credentials via `spring secret --scope platform create` " +
                "instead of writing env vars.",
            DefaultValueFactory = _ => false,
        };
        var envPathOption = new Option<string?>("--env-path")
        {
            Description =
                "Override the spring.env path written to by --write-env. Defaults " +
                "to ./deployment/spring.env relative to the current working directory.",
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description =
                "Build the manifest + print the GitHub creation URL, but do not " +
                "open a browser, bind a listener, or hit the network. Useful for " +
                "CI/air-gapped inspection.",
            DefaultValueFactory = _ => false,
        };
        var timeoutOption = new Option<int>("--callback-timeout-seconds")
        {
            Description =
                "Seconds to wait for GitHub to redirect the browser back before " +
                "giving up (default 300s, matches GitHub's one-time-code TTL).",
            DefaultValueFactory = _ => 300,
        };

        var command = new Command(
            "register",
            "Register a new GitHub App for this deployment via the App-from-manifest flow.");
        command.Options.Add(nameOption);
        command.Options.Add(orgOption);
        command.Options.Add(webhookOption);
        command.Options.Add(writeEnvOption);
        command.Options.Add(writeSecretsOption);
        command.Options.Add(envPathOption);
        command.Options.Add(dryRunOption);
        command.Options.Add(timeoutOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var name = parseResult.GetValue(nameOption)!;
            var org = parseResult.GetValue(orgOption);
            var webhookOverride = parseResult.GetValue(webhookOption);
            var writeEnv = parseResult.GetValue(writeEnvOption);
            var writeSecrets = parseResult.GetValue(writeSecretsOption);
            var envPathOverride = parseResult.GetValue(envPathOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var timeoutSec = parseResult.GetValue(timeoutOption);

            try
            {
                await RunAsync(
                    name: name,
                    org: org,
                    webhookUrlOverride: webhookOverride,
                    writeEnv: writeEnv,
                    writeSecrets: writeSecrets,
                    envFilePathOverride: envPathOverride,
                    dryRun: dryRun,
                    callbackTimeout: TimeSpan.FromSeconds(Math.Max(1, timeoutSec)),
                    cancellationToken: ct).ConfigureAwait(false);
            }
            catch (GitHubAppRegisterException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.Exit(ex.ExitCode);
            }
            catch (ManifestConversionException ex)
            {
                // Surface GitHub's error body verbatim — the "name taken"
                // case is recognisable to the operator from the original
                // wording, and paraphrasing would only hide useful detail.
                Console.Error.WriteLine($"GitHub rejected the App creation (HTTP {ex.StatusCode}).");
                if (!string.IsNullOrWhiteSpace(ex.ResponseBody))
                {
                    Console.Error.WriteLine(ex.ResponseBody);
                }
                Console.Error.WriteLine();
                Console.Error.WriteLine(
                    "If the error is 'name is already taken', re-run with a different --name " +
                    "(e.g. add a unique suffix).");
                Environment.Exit(1);
            }
        });

        return command;
    }

    // ------------------------------------------------------------------
    // rotate-key
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds the <c>rotate-key</c> subcommand (#636). GitHub has no public API
    /// for rotating an App's own private key — the operator generates a new PEM
    /// on GitHub's App settings page and hands it to the CLI via
    /// <c>--from-file</c>. The CLI validates the PEM, persists it, and prints a
    /// restart reminder.
    /// </summary>
    private static Command CreateRotateKeyCommand()
    {
        var fromFileOption = new Option<string?>("--from-file")
        {
            Description =
                "Path to the new PEM file downloaded from " +
                "github.com/settings/apps/<slug>/keys. Required unless --dry-run.",
        };
        var slugOption = new Option<string?>("--slug")
        {
            Description =
                "GitHub App slug (the name shown in the App URL). Used to build " +
                "the settings deep-link printed in the preamble.",
        };
        var writeEnvOption = new Option<bool>("--write-env")
        {
            Description = "Persist the new key to deployment/spring.env (default when neither flag is set).",
            DefaultValueFactory = _ => false,
        };
        var writeSecretsOption = new Option<bool>("--write-secrets")
        {
            Description = "Persist the new key via `spring secret --scope platform create`.",
            DefaultValueFactory = _ => false,
        };
        var envPathOption = new Option<string?>("--env-path")
        {
            Description = "Override the spring.env path used by --write-env.",
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Validate the PEM and print what would be written without persisting anything.",
            DefaultValueFactory = _ => false,
        };

        var command = new Command(
            "rotate-key",
            "Replace the GitHub App's private key. " +
            "Because GitHub has no API to rotate a key, this is a guided flow: " +
            "generate a new key on GitHub's App settings page, download the PEM, " +
            "then pass it here via --from-file.");
        command.Options.Add(fromFileOption);
        command.Options.Add(slugOption);
        command.Options.Add(writeEnvOption);
        command.Options.Add(writeSecretsOption);
        command.Options.Add(envPathOption);
        command.Options.Add(dryRunOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var fromFile = parseResult.GetValue(fromFileOption);
            var slug = parseResult.GetValue(slugOption);
            var writeEnv = parseResult.GetValue(writeEnvOption);
            var writeSecrets = parseResult.GetValue(writeSecretsOption);
            var envPathOverride = parseResult.GetValue(envPathOption);
            var dryRun = parseResult.GetValue(dryRunOption);

            try
            {
                await RunRotateKeyAsync(
                    fromFile: fromFile,
                    slug: slug,
                    writeEnv: writeEnv,
                    writeSecrets: writeSecrets,
                    envFilePathOverride: envPathOverride,
                    dryRun: dryRun,
                    cancellationToken: ct).ConfigureAwait(false);
            }
            catch (GitHubAppRegisterException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.Exit(ex.ExitCode);
            }
        });

        return command;
    }

    /// <summary>
    /// Executes the guided private-key rotation flow. Exposed publicly for
    /// integration-test consumption.
    /// </summary>
    public static async Task RunRotateKeyAsync(
        string? fromFile,
        string? slug,
        bool writeEnv,
        bool writeSecrets,
        string? envFilePathOverride,
        bool dryRun,
        CancellationToken cancellationToken,
        TextWriter? stdout = null)
    {
        stdout ??= Console.Out;

        if (writeEnv && writeSecrets)
        {
            throw new GitHubAppRegisterException(
                "--write-env and --write-secrets are mutually exclusive.");
        }

        // Preamble — tell the operator what they need to do on the GitHub side.
        stdout.WriteLine("spring github-app rotate-key");
        stdout.WriteLine("============================");
        stdout.WriteLine();
        stdout.WriteLine("GitHub has no public API for rotating a private key.");
        stdout.WriteLine("Complete these steps BEFORE running this command (or use --dry-run to inspect):");
        stdout.WriteLine();
        stdout.WriteLine("  1. Open your GitHub App settings:");
        if (!string.IsNullOrWhiteSpace(slug))
        {
            stdout.WriteLine($"       https://github.com/settings/apps/{slug.Trim()}/keys");
        }
        else
        {
            stdout.WriteLine("       https://github.com/settings/apps/<your-app-slug>/keys");
            stdout.WriteLine("     (pass --slug <slug> to get a clickable URL above)");
        }
        stdout.WriteLine("  2. Click \"Generate a private key\". A .pem file downloads.");
        stdout.WriteLine("  3. Re-run this command with --from-file <path-to-downloaded.pem>.");
        stdout.WriteLine();

        if (!dryRun && string.IsNullOrWhiteSpace(fromFile))
        {
            throw new GitHubAppRegisterException(
                "--from-file <path> is required. Download the new PEM from GitHub's App settings page first.",
                exitCode: 1);
        }

        string pem = string.Empty;
        if (!string.IsNullOrWhiteSpace(fromFile))
        {
            if (!File.Exists(fromFile))
            {
                throw new GitHubAppRegisterException(
                    $"PEM file not found: {fromFile}",
                    exitCode: 1);
            }

            pem = await File.ReadAllTextAsync(fromFile, cancellationToken).ConfigureAwait(false);
            if (!pem.Contains("BEGIN RSA PRIVATE KEY", StringComparison.OrdinalIgnoreCase)
                && !pem.Contains("BEGIN PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
            {
                throw new GitHubAppRegisterException(
                    $"The file '{fromFile}' does not appear to be a valid PEM private key. " +
                    "Expected a file containing '-----BEGIN RSA PRIVATE KEY-----' or '-----BEGIN PRIVATE KEY-----'.",
                    exitCode: 1);
            }

            stdout.WriteLine($"PEM validated: {fromFile}");
        }

        if (dryRun)
        {
            stdout.WriteLine("--dry-run: no changes will be written.");
            if (!string.IsNullOrWhiteSpace(fromFile))
            {
                stdout.WriteLine($"Would write {GitHubApp.CredentialWriter.EnvKeys.PrivateKeyPem} to {ResolveEnvPath(envFilePathOverride)}.");
            }
            return;
        }

        // Determine persistence target (mirrors register flow).
        var persistence = writeSecrets ? Persistence.WriteSecrets : Persistence.WriteEnv;
        var envPath = ResolveEnvPath(envFilePathOverride);

        if (persistence == Persistence.WriteEnv)
        {
            // Update just the PEM key in the env file. Reuse the env-file
            // comment/overwrite convention from CredentialWriter.WriteEnvAsync
            // (comment out the old line, append the new one).
            var lines = File.Exists(envPath)
                ? (await File.ReadAllLinesAsync(envPath, cancellationToken).ConfigureAwait(false)).ToList()
                : new System.Collections.Generic.List<string>();

            var stamp = DateTimeOffset.UtcNow.ToString("O");
            var key = GitHubApp.CredentialWriter.EnvKeys.PrivateKeyPem;
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }
                var eq = line.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0 && line.AsSpan(0, eq).Trim().Equals(key.AsSpan(), StringComparison.Ordinal))
                {
                    lines[i] = $"# {line}  # overwritten by `spring github-app rotate-key` at {stamp}";
                }
            }

            var pemEscaped = pem
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);
            lines.Add($"# GitHub App private key — rotated by `spring github-app rotate-key` at {stamp}");
            lines.Add($"{key}={pemEscaped}");

            var dir = Path.GetDirectoryName(envPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            await File.WriteAllTextAsync(envPath, string.Join(Environment.NewLine, lines) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
            stdout.WriteLine($"New private key written to: {envPath}");
        }
        else
        {
            var apiClient = ClientFactory.Create();
            await apiClient.CreatePlatformSecretAsync(
                name: GitHubApp.CredentialWriter.SecretNames.PrivateKeyPem,
                value: pem,
                externalStoreKey: null,
                ct: cancellationToken).ConfigureAwait(false);
            stdout.WriteLine($"New private key written to platform secret: {GitHubApp.CredentialWriter.SecretNames.PrivateKeyPem}");
        }

        stdout.WriteLine();
        stdout.WriteLine("Rotation complete.");
        stdout.WriteLine("IMPORTANT: Restart any running services so they pick up the new key.");
        stdout.WriteLine("The old key remains active on GitHub until you revoke it from the App settings page.");
    }

    // ------------------------------------------------------------------
    // rotate-webhook-secret
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds the <c>rotate-webhook-secret</c> subcommand (#636). Generates a
    /// new cryptographically strong secret, walks the operator through updating
    /// it on GitHub, and persists the new value.
    /// </summary>
    private static Command CreateRotateWebhookSecretCommand()
    {
        var fromValueOption = new Option<string?>("--from-value")
        {
            Description =
                "Use a caller-supplied secret instead of generating one " +
                "(for scripting / HSM-backed secrets). Must be at least 16 characters.",
        };
        var writeEnvOption = new Option<bool>("--write-env")
        {
            Description = "Persist the new secret to deployment/spring.env (default when neither flag is set).",
            DefaultValueFactory = _ => false,
        };
        var writeSecretsOption = new Option<bool>("--write-secrets")
        {
            Description = "Persist the new secret via `spring secret --scope platform create`.",
            DefaultValueFactory = _ => false,
        };
        var envPathOption = new Option<string?>("--env-path")
        {
            Description = "Override the spring.env path used by --write-env.",
        };
        var slugOption = new Option<string?>("--slug")
        {
            Description = "GitHub App slug — used to build the settings deep-link in the preamble.",
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Generate and print the secret without persisting or prompting.",
            DefaultValueFactory = _ => false,
        };

        var command = new Command(
            "rotate-webhook-secret",
            "Replace the GitHub App's webhook HMAC secret. " +
            "Generates a new secret (or accepts one via --from-value), walks the operator " +
            "through updating it on GitHub's App settings page, then persists it.");
        command.Options.Add(fromValueOption);
        command.Options.Add(writeEnvOption);
        command.Options.Add(writeSecretsOption);
        command.Options.Add(envPathOption);
        command.Options.Add(slugOption);
        command.Options.Add(dryRunOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var fromValue = parseResult.GetValue(fromValueOption);
            var writeEnv = parseResult.GetValue(writeEnvOption);
            var writeSecrets = parseResult.GetValue(writeSecretsOption);
            var envPathOverride = parseResult.GetValue(envPathOption);
            var slug = parseResult.GetValue(slugOption);
            var dryRun = parseResult.GetValue(dryRunOption);

            try
            {
                await RunRotateWebhookSecretAsync(
                    fromValue: fromValue,
                    writeEnv: writeEnv,
                    writeSecrets: writeSecrets,
                    envFilePathOverride: envPathOverride,
                    slug: slug,
                    dryRun: dryRun,
                    cancellationToken: ct).ConfigureAwait(false);
            }
            catch (GitHubAppRegisterException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.Exit(ex.ExitCode);
            }
        });

        return command;
    }

    /// <summary>
    /// Executes the webhook-secret rotation flow. Exposed publicly for
    /// integration-test consumption.
    /// </summary>
    public static async Task RunRotateWebhookSecretAsync(
        string? fromValue,
        bool writeEnv,
        bool writeSecrets,
        string? envFilePathOverride,
        string? slug,
        bool dryRun,
        CancellationToken cancellationToken,
        TextWriter? stdout = null,
        Func<string, Task<bool>>? confirmationPrompt = null)
    {
        stdout ??= Console.Out;

        if (writeEnv && writeSecrets)
        {
            throw new GitHubAppRegisterException(
                "--write-env and --write-secrets are mutually exclusive.");
        }

        // Generate or use the caller-supplied secret.
        string newSecret;
        if (!string.IsNullOrWhiteSpace(fromValue))
        {
            if (fromValue!.Length < 16)
            {
                throw new GitHubAppRegisterException(
                    "--from-value must be at least 16 characters.",
                    exitCode: 1);
            }
            newSecret = fromValue;
        }
        else
        {
            // 32 random bytes → base64url (no padding) — matches GitHub's
            // "generate a strong random secret" guidance.
            var bytes = RandomNumberGenerator.GetBytes(32);
            newSecret = Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        stdout.WriteLine("spring github-app rotate-webhook-secret");
        stdout.WriteLine("=======================================");
        stdout.WriteLine();
        stdout.WriteLine("New webhook secret (copy this now — it will not be shown again after you confirm):");
        stdout.WriteLine();
        stdout.WriteLine($"  {newSecret}");
        stdout.WriteLine();

        if (dryRun)
        {
            stdout.WriteLine("--dry-run: secret generated but not persisted and no confirmation prompted.");
            return;
        }

        stdout.WriteLine("Next steps:");
        stdout.WriteLine("  1. Open your GitHub App settings:");
        if (!string.IsNullOrWhiteSpace(slug))
        {
            stdout.WriteLine($"       https://github.com/settings/apps/{slug.Trim()}");
        }
        else
        {
            stdout.WriteLine("       https://github.com/settings/apps/<your-app-slug>");
            stdout.WriteLine("     (pass --slug <slug> to get a clickable URL above)");
        }
        stdout.WriteLine("  2. Paste the secret above into the \"Webhook secret\" field and click Save.");
        stdout.WriteLine();

        // Prompt for confirmation (default implementation reads from stdin).
        var confirm = confirmationPrompt ?? DefaultConfirmSaved;
        var confirmed = await confirm(newSecret).ConfigureAwait(false);
        if (!confirmed)
        {
            stdout.WriteLine("Rotation aborted. The old webhook secret is still active.");
            Environment.Exit(1);
            return;
        }

        // Persist the new secret.
        var persistence = writeSecrets ? Persistence.WriteSecrets : Persistence.WriteEnv;
        var envPath = ResolveEnvPath(envFilePathOverride);

        if (persistence == Persistence.WriteEnv)
        {
            var lines = File.Exists(envPath)
                ? (await File.ReadAllLinesAsync(envPath, cancellationToken).ConfigureAwait(false)).ToList()
                : new System.Collections.Generic.List<string>();

            var stamp = DateTimeOffset.UtcNow.ToString("O");
            var key = GitHubApp.CredentialWriter.EnvKeys.WebhookSecret;
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }
                var eq = line.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0 && line.AsSpan(0, eq).Trim().Equals(key.AsSpan(), StringComparison.Ordinal))
                {
                    lines[i] = $"# {line}  # overwritten by `spring github-app rotate-webhook-secret` at {stamp}";
                }
            }

            lines.Add($"# GitHub App webhook secret — rotated by `spring github-app rotate-webhook-secret` at {stamp}");
            lines.Add($"{key}={newSecret}");

            var dir = Path.GetDirectoryName(envPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            await File.WriteAllTextAsync(envPath, string.Join(Environment.NewLine, lines) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
            stdout.WriteLine($"New webhook secret written to: {envPath}");
        }
        else
        {
            var apiClient = ClientFactory.Create();
            await apiClient.CreatePlatformSecretAsync(
                name: GitHubApp.CredentialWriter.SecretNames.WebhookSecret,
                value: newSecret,
                externalStoreKey: null,
                ct: cancellationToken).ConfigureAwait(false);
            stdout.WriteLine($"New webhook secret written to platform secret: {GitHubApp.CredentialWriter.SecretNames.WebhookSecret}");
        }

        stdout.WriteLine();
        stdout.WriteLine("Rotation complete.");
        stdout.WriteLine("IMPORTANT: Restart any running services so they pick up the new webhook secret.");
    }

    private static async Task<bool> DefaultConfirmSaved(string _)
    {
        Console.Write("Have you saved the new secret in GitHub's App settings? [y/N] ");
        var line = Console.ReadLine();
        await Task.CompletedTask.ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(line)
            && line.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveEnvPath(string? envFilePathOverride)
        => string.IsNullOrWhiteSpace(envFilePathOverride)
            ? Path.Combine(Directory.GetCurrentDirectory(), "deployment", "spring.env")
            : envFilePathOverride;

    // ------------------------------------------------------------------
    // Core flow. Extracted from the SetAction callback so integration
    // tests can drive it without instantiating System.CommandLine's
    // ParseResult plumbing.
    // ------------------------------------------------------------------

    /// <summary>
    /// Executes the manifest-flow registration. Public for test
    /// consumption; the shipping UX is the <c>register</c> verb above.
    /// </summary>
    public static async Task RunAsync(
        string name,
        string? org,
        string? webhookUrlOverride,
        bool writeEnv,
        bool writeSecrets,
        string? envFilePathOverride,
        bool dryRun,
        TimeSpan callbackTimeout,
        CancellationToken cancellationToken,
        // Seams for integration testing: point the HTTP client at a
        // stubbed GitHub instead of api.github.com. Production callers
        // pass null and get the defaults.
        HttpClient? httpClientOverride = null,
        string? githubApiBaseUrlOverride = null,
        Func<string, Task>? browserOpenerOverride = null,
        TextWriter? stdout = null)
    {
        stdout ??= Console.Out;

        if (writeEnv && writeSecrets)
        {
            throw new GitHubAppRegisterException(
                "--write-env and --write-secrets are mutually exclusive.");
        }

        // Default to --write-env when neither is specified AND we're not
        // attached to a TTY (CI). Interactive prompt otherwise.
        var persistence = ResolvePersistence(writeEnv, writeSecrets, dryRun);

        var resolvedWebhookUrl = ResolveWebhookUrl(webhookUrlOverride);
        PrintPreamble(stdout, name, org, resolvedWebhookUrl, persistence);

        // ------------------------------------------------------------
        // Dry-run short-circuit: build manifest, print URL, stop.
        // ------------------------------------------------------------
        if (dryRun)
        {
            // No listener = use a placeholder callback URL. The URL is
            // a no-op: the dry-run operator inspects the manifest, they
            // do not complete a flow.
            var dryInputs = new GitHubAppManifest.Inputs(
                Name: name,
                WebhookUrl: resolvedWebhookUrl,
                CallbackUrl: "http://127.0.0.1:0/");
            var dryUrl = GitHubAppManifest.BuildCreationUrl(dryInputs, org);
            stdout.WriteLine("--dry-run: no browser will open, no listener will bind, no network calls made.");
            stdout.WriteLine();
            stdout.WriteLine("Manifest JSON:");
            stdout.WriteLine(GitHubAppManifest.BuildJson(dryInputs));
            stdout.WriteLine();
            stdout.WriteLine("Creation URL:");
            stdout.WriteLine(dryUrl);
            return;
        }

        // ------------------------------------------------------------
        // Bind the loopback listener BEFORE we open the browser. The
        // ephemeral port is captured into the callback URL embedded in
        // the manifest we submit — no way to know it without binding
        // first.
        // ------------------------------------------------------------
        var (listener, port) = CallbackListener.BindHttpListenerWithRetry(
            maxAttempts: CallbackListener.DefaultMaxBindAttempts);
        var callbackUrl = $"http://127.0.0.1:{port}/";

        try
        {
            var manifestInputs = new GitHubAppManifest.Inputs(
                Name: name,
                WebhookUrl: resolvedWebhookUrl,
                CallbackUrl: callbackUrl);
            var creationUrl = GitHubAppManifest.BuildCreationUrl(manifestInputs, org);

            stdout.WriteLine($"Callback listener bound on 127.0.0.1:{port}.");
            stdout.WriteLine("Opening your browser at:");
            stdout.WriteLine($"  {creationUrl}");
            stdout.WriteLine();
            stdout.WriteLine("If the browser does not open automatically, paste the URL above.");
            stdout.WriteLine($"Waiting for the GitHub redirect (timeout {callbackTimeout.TotalSeconds:N0}s)...");

            var opener = browserOpenerOverride ?? DefaultBrowserOpener;
            // Kick off the browser opener WITHOUT awaiting it — the
            // production implementation ({ xdg-open, open, cmd /c start })
            // returns immediately, but test seams that drive a real HTTP
            // request back against the listener would deadlock if we
            // awaited the opener before we enter WaitForCallbackCodeAsync
            // (the test's GET blocks until the listener accepts, which
            // only happens inside WaitForCallbackCodeAsync).
            _ = Task.Run(async () =>
            {
                try
                {
                    await opener(creationUrl).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    stdout.WriteLine($"(Could not auto-open browser: {ex.Message})");
                }
            }, cancellationToken);

            var code = await CallbackListener.WaitForCallbackCodeAsync(
                listener, callbackTimeout, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(code))
            {
                throw new GitHubAppRegisterException(
                    $"Timed out after {callbackTimeout.TotalSeconds:N0}s waiting for GitHub to " +
                    "redirect back. Re-run `spring github-app register` to try again.",
                    exitCode: 2);
            }

            // ------------------------------------------------------------
            // Exchange the one-time code for credentials.
            // ------------------------------------------------------------
            var http = httpClientOverride ?? CreateDefaultHttpClient();
            try
            {
                var conversion = new ManifestConversionClient(
                    http,
                    githubApiBaseUrlOverride ?? ManifestConversionClient.DefaultGitHubBaseUrl);
                var result = await conversion.ExchangeCodeAsync(code, cancellationToken).ConfigureAwait(false);

                // ------------------------------------------------------------
                // Persist.
                // ------------------------------------------------------------
                var outcome = persistence switch
                {
                    Persistence.WriteEnv => await CredentialWriter.WriteEnvAsync(
                        result,
                        envFilePathOverride ?? DefaultEnvFilePath(),
                        cancellationToken).ConfigureAwait(false),
                    Persistence.WriteSecrets => await CredentialWriter.WriteSecretsAsync(
                        result,
                        ClientFactory.Create(),
                        cancellationToken).ConfigureAwait(false),
                    _ => throw new InvalidOperationException("Unreachable persistence target."),
                };

                PrintSuccess(stdout, result, outcome);
            }
            finally
            {
                if (httpClientOverride is null)
                {
                    http.Dispose();
                }
            }
        }
        finally
        {
            try { listener.Stop(); } catch { /* best-effort */ }
            try { ((IDisposable)listener).Dispose(); } catch { /* best-effort */ }
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    internal enum Persistence
    {
        WriteEnv,
        WriteSecrets,
    }

    private static Persistence ResolvePersistence(bool writeEnv, bool writeSecrets, bool dryRun)
    {
        if (writeEnv)
        {
            return Persistence.WriteEnv;
        }
        if (writeSecrets)
        {
            return Persistence.WriteSecrets;
        }

        // Dry-run doesn't persist, but we still thread a value through so
        // the preamble prints something sensible.
        if (dryRun)
        {
            return Persistence.WriteEnv;
        }

        // Interactive prompt when attached to a TTY; otherwise default to
        // --write-env. TTY detection avoids prompting inside CI pipelines.
        if (Console.IsInputRedirected || !Environment.UserInteractive)
        {
            return Persistence.WriteEnv;
        }

        Console.Write("Persist credentials to (e)nv file or platform (s)ecrets? [E/s] ");
        var line = Console.ReadLine();
        if (!string.IsNullOrWhiteSpace(line)
            && line.Trim().StartsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            return Persistence.WriteSecrets;
        }
        return Persistence.WriteEnv;
    }

    private static string ResolveWebhookUrl(string? webhookOverride)
    {
        if (!string.IsNullOrWhiteSpace(webhookOverride))
        {
            return webhookOverride;
        }

        // Derive from the configured deployment endpoint. We can't know
        // whether that endpoint is reachable from the public internet —
        // that's the operator's responsibility — but the default makes
        // local-dev + relay setups (deployment/relay.sh) work out of
        // the box.
        var config = CliConfig.Load();
        var endpoint = Environment.GetEnvironmentVariable("SPRING_API_URL")
            ?? config.Endpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            endpoint = "http://localhost:5000";
        }
        return $"{endpoint.TrimEnd('/')}/api/v1/webhooks/github";
    }

    private static string DefaultEnvFilePath()
    {
        // Conventional path relative to the repo root: operators run the
        // CLI from the repo root. If invoked elsewhere, they can pass
        // --env-path explicitly. We don't try to walk-up; guessing the
        // repo root opens us to subtle bugs when the CLI is installed
        // as a global tool.
        return Path.Combine(Directory.GetCurrentDirectory(), "deployment", "spring.env");
    }

    private static void PrintPreamble(
        TextWriter stdout, string name, string? org, string webhookUrl, Persistence persistence)
    {
        stdout.WriteLine("spring github-app register");
        stdout.WriteLine("==========================");
        stdout.WriteLine();
        stdout.WriteLine("About to register a new GitHub App for this deployment.");
        stdout.WriteLine("This drives GitHub's App-from-manifest flow:");
        stdout.WriteLine();
        stdout.WriteLine("  1. Your browser opens a GitHub 'create App' page.");
        stdout.WriteLine("  2. You click 'Create' (that's the only manual step).");
        stdout.WriteLine("  3. GitHub redirects back here with a one-time code.");
        stdout.WriteLine("  4. The CLI exchanges the code for the App ID, PEM, webhook secret.");
        stdout.WriteLine("  5. Credentials are persisted; install-URL is printed.");
        stdout.WriteLine();
        stdout.WriteLine($"  App name:     {name}");
        stdout.WriteLine($"  Owner:        {(string.IsNullOrWhiteSpace(org) ? "your user account" : $"org '{org}'")}");
        stdout.WriteLine($"  Webhook URL:  {webhookUrl}");
        stdout.WriteLine($"  Persistence:  {persistence}");
        stdout.WriteLine();
        stdout.WriteLine("Permissions requested (read):   issues, pull_requests, contents, metadata");
        stdout.WriteLine("Permissions requested (write):  issue_comment, statuses, checks");
        stdout.WriteLine("Webhook events:                 issues, pull_request, issue_comment, installation");
        stdout.WriteLine();
    }

    private static void PrintSuccess(
        TextWriter stdout, ManifestConversionResult result, CredentialWriter.WriteOutcome outcome)
    {
        stdout.WriteLine();
        stdout.WriteLine("GitHub App registered.");
        stdout.WriteLine($"  App ID:   {result.AppId}");
        stdout.WriteLine($"  App slug: {result.Slug}");
        stdout.WriteLine();
        stdout.WriteLine($"Credentials written to: {outcome.Target}");
        foreach (var key in outcome.WrittenKeys)
        {
            stdout.WriteLine($"  - {key}");
        }
        if (outcome.MissingFields.Count > 0)
        {
            stdout.WriteLine();
            stdout.WriteLine("WARNING: GitHub omitted the following fields in its response:");
            foreach (var field in outcome.MissingFields)
            {
                stdout.WriteLine($"  - {field}");
            }
            stdout.WriteLine("The remaining credentials were written. Rerun if this looks wrong.");
        }
        stdout.WriteLine();
        if (!string.IsNullOrWhiteSpace(result.HtmlUrl))
        {
            stdout.WriteLine("Next step: install the App on a repo or org. Open:");
            stdout.WriteLine($"  {result.HtmlUrl}/installations/new");
            stdout.WriteLine();
            stdout.WriteLine("Or run `spring connector bind --type github ...` once the App is installed.");
        }
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("SpringVoyage-CLI", "1.0"));
        return http;
    }

    private static Task DefaultBrowserOpener(string url)
    {
        // Best-effort per-platform shell-out. On headless dev containers
        // the call throws; we catch at the call site and log.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd",
                Arguments = $"/c start \"\" \"{url}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            })?.Dispose();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Process.Start("open", url)?.Dispose();
        }
        else
        {
            // Linux / BSD / everything else.
            Process.Start("xdg-open", url)?.Dispose();
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// CLI-layer exception carrying an intended exit code. Distinct from
/// <see cref="ManifestConversionException"/> which is GitHub-side.
/// </summary>
public sealed class GitHubAppRegisterException : Exception
{
    public int ExitCode { get; }

    public GitHubAppRegisterException(string message, int exitCode = 1)
        : base(message)
    {
        ExitCode = exitCode;
    }
}