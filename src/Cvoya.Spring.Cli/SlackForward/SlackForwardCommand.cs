// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.SlackForward;

using System;
using System.CommandLine;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Builds the <c>spring connector slack forward</c> verb. Opens a Slack
/// Socket Mode WebSocket using an app-level (<c>xapp-…</c>) token and
/// replays every inbound event / slash command / interaction as a signed
/// HTTPS request to the local Spring Voyage API.
/// </summary>
/// <remarks>
/// <para>
/// The local-dev counterpart of <c>eng/deploy/gh-webhook-forward.sh</c> for
/// GitHub. Slack publishes no equivalent CLI (no <c>slack webhook
/// forward</c>), so the bridge lives in the CLI itself; the bash wrapper at
/// <c>eng/deploy/slack-events-forward.sh</c> is the operator-facing entry
/// point.
/// </para>
/// <para>
/// Signature minting matches the contract enforced by the connector's
/// <c>SlackSignatureValidator</c>: HMAC-SHA256 over
/// <c>v0:&lt;timestamp&gt;:&lt;raw-body&gt;</c> using the deployment's
/// signing secret. The bridge needs the same signing secret SV verifies
/// against — that is the secret Slack issued at app creation, persisted in
/// <c>Slack__OAuth__SigningSecret</c>.
/// </para>
/// </remarks>
public static class SlackForwardCommand
{
    /// <summary>Env var carrying the Slack app-level token.</summary>
    public const string AppTokenEnvVar = "Slack__SocketMode__AppToken";

    /// <summary>Env var carrying the Slack signing secret.</summary>
    public const string SigningSecretEnvVar = "Slack__OAuth__SigningSecret";

    /// <summary>Default local target the bridge replays inbound deliveries to.</summary>
    public const string DefaultTarget = "http://localhost:5000";

    /// <summary>
    /// Builds the <c>forward</c> verb. Wired into <c>spring connector
    /// slack</c> by <see cref="Commands.ConnectorCommand"/>.
    /// </summary>
    public static Command CreateForwardCommand()
    {
        var appTokenOption = new Option<string?>("--app-token")
        {
            Description =
                "Slack app-level token (xapp-…) with the 'connections:write' scope. " +
                $"Falls back to the {AppTokenEnvVar} environment variable.",
        };
        var signingSecretOption = new Option<string?>("--signing-secret")
        {
            Description =
                "Slack signing secret used to mint v0 signatures on every forwarded " +
                $"request. Falls back to the {SigningSecretEnvVar} environment variable. " +
                "Must match Slack__OAuth__SigningSecret on the SV deployment.",
        };
        var targetOption = new Option<string?>("--target")
        {
            Description =
                "Base URL of the Spring Voyage API to deliver to. Defaults to " +
                DefaultTarget + ".",
        };

        var command = new Command(
            "forward",
            "Bridge Slack Socket Mode → local Spring Voyage API for events, slash commands, " +
            "and interactions. The Slack equivalent of `gh webhook forward`. Requires the app " +
            "to have Socket Mode enabled (`spring connector slack install --socket-mode`).");
        command.Options.Add(appTokenOption);
        command.Options.Add(signingSecretOption);
        command.Options.Add(targetOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var appToken = parseResult.GetValue(appTokenOption)
                ?? Environment.GetEnvironmentVariable(AppTokenEnvVar);
            var signingSecret = parseResult.GetValue(signingSecretOption)
                ?? Environment.GetEnvironmentVariable(SigningSecretEnvVar);
            var target = parseResult.GetValue(targetOption) ?? DefaultTarget;

            if (string.IsNullOrWhiteSpace(appToken))
            {
                Console.Error.WriteLine(
                    $"--app-token (or {AppTokenEnvVar}) is required. Generate an " +
                    "app-level token with the 'connections:write' scope from " +
                    "https://api.slack.com/apps/<your-app>/general → 'App-Level Tokens'.");
                Environment.Exit(1);
                return;
            }
            if (string.IsNullOrWhiteSpace(signingSecret))
            {
                Console.Error.WriteLine(
                    $"--signing-secret (or {SigningSecretEnvVar}) is required so the " +
                    "bridge can mint v0 signatures the SV API will accept.");
                Environment.Exit(1);
                return;
            }
            if (!appToken.StartsWith("xapp-", StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    "Slack app-level tokens start with 'xapp-'. The supplied --app-token " +
                    "looks like a different credential — double-check you copied the " +
                    "right value from the 'App-Level Tokens' section.");
                Environment.Exit(1);
                return;
            }

            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
            http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("SpringVoyage-CLI-SlackForward", "1.0"));

            var bridge = new SocketModeBridge(
                http: http,
                appToken: appToken,
                signingSecret: signingSecret,
                target: target,
                stdout: Console.Out,
                stderr: Console.Error);
            try
            {
                await bridge.RunAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Normal Ctrl-C exit.
            }
        });

        return command;
    }
}
