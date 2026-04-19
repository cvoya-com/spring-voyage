// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System;
using System.CommandLine;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
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