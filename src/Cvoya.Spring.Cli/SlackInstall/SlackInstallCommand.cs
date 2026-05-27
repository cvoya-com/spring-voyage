// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.SlackInstall;

using System;
using System.CommandLine;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Net;

/// <summary>
/// Builds the <c>spring connector slack install</c> verb (#2839). Drives
/// Slack's
/// <see href="https://api.slack.com/reference/manifests">App Manifest API</see>
/// end-to-end: build manifest → validate → create → persist credentials
/// → print OAuth install URL. The whole flow finishes in ~30 seconds
/// without touching api.slack.com's admin UI.
/// </summary>
public static class SlackInstallCommand
{
    /// <summary>
    /// Environment variable callers can set instead of passing
    /// <c>--config-token</c>. Convenient for shell scripts that hold the
    /// token in a vault.
    /// </summary>
    public const string ConfigTokenEnvVar = "SV_SLACK_CONFIG_TOKEN";

    /// <summary>
    /// Builds the <c>install</c> verb. Wired into <c>spring connector
    /// slack</c> by <see cref="Commands.ConnectorCommand"/>.
    /// </summary>
    public static Command CreateInstallCommand()
    {
        var configTokenOption = new Option<string?>("--config-token")
        {
            Description =
                "Slack Configuration Token issued by a workspace admin on " +
                "Settings → Your Apps → 'Generate Configuration Tokens'. May " +
                $"also be supplied via the {ConfigTokenEnvVar} environment variable.",
        };
        var appNameOption = new Option<string?>("--app-name")
        {
            Description =
                "Display name for the new Slack app (defaults to " +
                "'Spring Voyage'). MUST be unique within the workspace.",
        };
        var svHostOption = new Option<string?>("--sv-host")
        {
            Description =
                "Override the Spring Voyage base URL the manifest embeds in " +
                "every redirect / event / slash-command URL. Defaults to " +
                "SPRING_API_URL, then the CLI config endpoint, then http://localhost:5000.",
        };
        var writeEnvOption = new Option<bool>("--write-env")
        {
            Description =
                "Append the resolved credentials to eng/config/spring.env " +
                "(the default persistence target).",
            DefaultValueFactory = _ => false,
        };
        var writeSecretsOption = new Option<bool>("--write-secrets")
        {
            Description =
                "Persist credentials via `spring secret --scope platform " +
                "create`. Writes are atomic — any failure rolls back every " +
                "secret written in this run (issue #2839).",
            DefaultValueFactory = _ => false,
        };
        var envPathOption = new Option<string?>("--env-path")
        {
            Description =
                "Override the spring.env path written to by --write-env. " +
                "Defaults to ./eng/config/spring.env relative to the current " +
                "working directory.",
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description =
                "Build the manifest + print the JSON without contacting " +
                "Slack or persisting anything. Useful for CI / air-gapped " +
                "inspection.",
            DefaultValueFactory = _ => false,
        };

        var command = new Command(
            "install",
            "Programmatically register a Slack app for this deployment via " +
            "Slack's App Manifest API. Replaces ~15 minutes of admin-console " +
            "clicking with a single command (ADR-0061 §2.5).");
        command.Options.Add(configTokenOption);
        command.Options.Add(appNameOption);
        command.Options.Add(svHostOption);
        command.Options.Add(writeEnvOption);
        command.Options.Add(writeSecretsOption);
        command.Options.Add(envPathOption);
        command.Options.Add(dryRunOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var configToken = parseResult.GetValue(configTokenOption);
            var appName = parseResult.GetValue(appNameOption);
            var svHostOverride = parseResult.GetValue(svHostOption);
            var writeEnv = parseResult.GetValue(writeEnvOption);
            var writeSecrets = parseResult.GetValue(writeSecretsOption);
            var envPathOverride = parseResult.GetValue(envPathOption);
            var dryRun = parseResult.GetValue(dryRunOption);

            try
            {
                await RunAsync(
                    configToken: configToken,
                    appName: appName,
                    svHostOverride: svHostOverride,
                    writeEnv: writeEnv,
                    writeSecrets: writeSecrets,
                    envFilePathOverride: envPathOverride,
                    dryRun: dryRun,
                    cancellationToken: ct).ConfigureAwait(false);
            }
            catch (SlackInstallException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.Exit(ex.ExitCode);
            }
            catch (SlackManifestException ex)
            {
                // Surface Slack's error body so operators see the
                // canonical Slack error string (e.g. invalid_manifest,
                // not_authed, token_revoked).
                Console.Error.WriteLine($"Slack rejected the request (HTTP {ex.StatusCode}).");
                if (!string.IsNullOrWhiteSpace(ex.ErrorCode))
                {
                    Console.Error.WriteLine($"  error: {ex.ErrorCode}");
                }
                if (!string.IsNullOrWhiteSpace(ex.ResponseBody))
                {
                    Console.Error.WriteLine(ex.ResponseBody);
                }
                Console.Error.WriteLine();
                Console.Error.WriteLine(
                    "Common causes: the Configuration Token is missing the right " +
                    "scopes, has expired, or the app name is already taken. " +
                    "Generate a fresh token from your workspace admin's " +
                    "'Your Apps' page and retry.");
                Environment.Exit(1);
            }
        });

        return command;
    }

    /// <summary>
    /// Executes the install flow. Exposed publicly so integration tests
    /// can drive it without the System.CommandLine plumbing — and so the
    /// production action callback stays a thin shell.
    /// </summary>
    public static async Task RunAsync(
        string? configToken,
        string? appName,
        string? svHostOverride,
        bool writeEnv,
        bool writeSecrets,
        string? envFilePathOverride,
        bool dryRun,
        CancellationToken cancellationToken,
        HttpClient? httpClientOverride = null,
        string? slackBaseUrlOverride = null,
        Func<SpringApiClient>? apiClientFactoryOverride = null,
        TextWriter? stdout = null)
    {
        stdout ??= Console.Out;

        if (writeEnv && writeSecrets)
        {
            throw new SlackInstallException(
                "--write-env and --write-secrets are mutually exclusive.");
        }

        var resolvedToken = ResolveConfigToken(configToken, dryRun);
        var resolvedAppName = ResolveAppName(appName);
        var resolvedHost = ResolveSvHost(svHostOverride);
        var persistence = ResolvePersistence(writeEnv, writeSecrets, dryRun);

        var manifestInputs = new SlackAppManifest.Inputs(
            AppName: resolvedAppName,
            SvHost: resolvedHost);
        var manifestJson = SlackAppManifest.BuildJson(manifestInputs);
        var redirectUri = resolvedHost.TrimEnd('/') + SlackAppManifest.OAuthCallbackPath;

        PrintPreamble(stdout, resolvedAppName, resolvedHost, persistence);

        if (dryRun)
        {
            stdout.WriteLine("--dry-run: no network calls, no persistence.");
            stdout.WriteLine();
            stdout.WriteLine("Manifest JSON:");
            stdout.WriteLine(manifestJson);
            return;
        }

        var http = httpClientOverride ?? CreateDefaultHttpClient();
        try
        {
            var slackClient = new SlackManifestApiClient(
                http,
                slackBaseUrlOverride ?? SlackManifestApiClient.DefaultSlackBaseUrl);

            stdout.WriteLine("Validating manifest with Slack...");
            await slackClient.ValidateAsync(manifestJson, resolvedToken, cancellationToken)
                .ConfigureAwait(false);
            stdout.WriteLine("  ok");

            stdout.WriteLine("Creating Slack app...");
            var createResult = await slackClient.CreateAsync(manifestJson, resolvedToken, cancellationToken)
                .ConfigureAwait(false);
            stdout.WriteLine($"  ok (app_id={createResult.AppId})");

            var credentials = new SlackCredentialWriter.CredentialBundle(
                AppId: createResult.AppId,
                ClientId: createResult.Credentials!.ClientId,
                ClientSecret: createResult.Credentials.ClientSecret!,
                SigningSecret: createResult.Credentials.SigningSecret!,
                VerificationToken: createResult.Credentials.VerificationToken,
                RedirectUri: redirectUri);

            SlackCredentialWriter.WriteOutcome outcome;
            if (persistence == Persistence.WriteSecrets)
            {
                var apiClient = apiClientFactoryOverride is null
                    ? ClientFactory.Create()
                    : apiClientFactoryOverride();
                outcome = await SlackCredentialWriter.WriteSecretsAsync(
                    credentials, apiClient, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                outcome = await SlackCredentialWriter.WriteEnvAsync(
                    credentials,
                    envFilePathOverride ?? DefaultEnvFilePath(),
                    cancellationToken).ConfigureAwait(false);
            }

            PrintSuccess(stdout, createResult, outcome, resolvedHost);
        }
        finally
        {
            if (httpClientOverride is null)
            {
                http.Dispose();
            }
        }
    }

    internal enum Persistence
    {
        WriteEnv,
        WriteSecrets,
    }

    private static string ResolveConfigToken(string? supplied, bool dryRun)
    {
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            return supplied;
        }

        var fromEnv = Environment.GetEnvironmentVariable(ConfigTokenEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        // Dry-run doesn't actually call Slack, so a missing token isn't
        // fatal — return a placeholder so the manifest builder can still
        // print the JSON.
        if (dryRun)
        {
            return "DRY_RUN_NO_TOKEN";
        }

        throw new SlackInstallException(
            "A Slack Configuration Token is required. Pass --config-token <token> " +
            $"or set the {ConfigTokenEnvVar} environment variable. Generate the " +
            "token from your workspace admin's 'Your Apps' page → 'Generate " +
            "Configuration Tokens'.",
            exitCode: 1);
    }

    private static string ResolveAppName(string? supplied)
    {
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            return supplied;
        }
        return "Spring Voyage";
    }

    private static string ResolveSvHost(string? supplied)
    {
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            return supplied;
        }

        var fromEnv = Environment.GetEnvironmentVariable("SPRING_API_URL");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        var fromConfig = CliConfig.Load().Endpoint;
        if (!string.IsNullOrWhiteSpace(fromConfig))
        {
            return fromConfig;
        }

        return "http://localhost:5000";
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

        // Dry-run doesn't persist; pick a placeholder so the preamble
        // still prints a sensible value.
        if (dryRun)
        {
            return Persistence.WriteEnv;
        }

        // Default to env file when running unattended — matches the GitHub
        // register command's behaviour. The portal-equivalent wizard
        // (#2820) takes a different default; this is the CLI path.
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

    private static string DefaultEnvFilePath()
    {
        return Path.Combine(Directory.GetCurrentDirectory(), "eng", "config", "spring.env");
    }

    private static void PrintPreamble(
        TextWriter stdout, string appName, string svHost, Persistence persistence)
    {
        stdout.WriteLine("spring connector slack install");
        stdout.WriteLine("==============================");
        stdout.WriteLine();
        stdout.WriteLine("About to register a new Slack app via the Manifest API.");
        stdout.WriteLine();
        stdout.WriteLine($"  App name:     {appName}");
        stdout.WriteLine($"  SV host:      {svHost}");
        stdout.WriteLine($"  Persistence:  {persistence}");
        stdout.WriteLine();
        stdout.WriteLine("Bot scopes requested:");
        foreach (var scope in SlackAppManifest.BotScopes)
        {
            stdout.WriteLine($"  - {scope}");
        }
        stdout.WriteLine();
        stdout.WriteLine("Slash commands:");
        foreach (var slash in SlackAppManifest.SlashCommands)
        {
            stdout.WriteLine($"  - {slash}");
        }
        stdout.WriteLine();
    }

    private static void PrintSuccess(
        TextWriter stdout,
        SlackManifestCreateResult result,
        SlackCredentialWriter.WriteOutcome outcome,
        string svHost)
    {
        stdout.WriteLine();
        stdout.WriteLine("Slack app created.");
        stdout.WriteLine($"  App ID:       {result.AppId}");
        stdout.WriteLine();
        stdout.WriteLine($"Credentials written to: {outcome.Target}");
        foreach (var key in outcome.WrittenKeys)
        {
            stdout.WriteLine($"  - {key}");
        }
        if (outcome.MissingFields.Count > 0)
        {
            stdout.WriteLine();
            stdout.WriteLine("WARNING: Slack omitted the following fields in its response:");
            foreach (var field in outcome.MissingFields)
            {
                stdout.WriteLine($"  - {field}");
            }
        }
        stdout.WriteLine();
        stdout.WriteLine("Next step: install the app on a workspace. Open:");
        if (!string.IsNullOrWhiteSpace(result.OAuthAuthorizeUrl))
        {
            // Slack's manifest.create returns the install URL directly
            // when the app is configured to support it; use it verbatim.
            stdout.WriteLine($"  {result.OAuthAuthorizeUrl}");
        }
        else
        {
            // Fall back to SV's OAuth authorize endpoint — the portal
            // starts the flow from there too (see #2815 / #2836).
            var fallback = UrlPath.Combine(
                svHost,
                "/api/v1/tenant/connectors/slack/oauth/authorize");
            stdout.WriteLine($"  {fallback}");
        }
        stdout.WriteLine();
        stdout.WriteLine("Restart any running services so they pick up the new Slack:OAuth:* config.");
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
}

/// <summary>
/// CLI-layer exception carrying an intended exit code. Distinct from
/// <see cref="SlackManifestException"/> which is Slack-side.
/// </summary>
public sealed class SlackInstallException : Exception
{
    public int ExitCode { get; }

    public SlackInstallException(string message, int exitCode = 1)
        : base(message)
    {
        ExitCode = exitCode;
    }
}
