// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;

using Cvoya.Spring.Cli.Output;
using Cvoya.Spring.Cli.Utilities;
using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Builds the <c>spring user</c> verb tree. Owns the <c>TenantUser</c>
/// side of the principal model introduced by ADR-0047 §1 — the platform's
/// authenticated principal, distinct from the <c>Human</c> configuration
/// entities that populate unit member rows.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the pre-ADR-0047 <c>spring human identity {set,list,remove}</c>
/// verbs. v0.1 is the freezing release; the old verbs were deleted in
/// Phase A+B and this command re-lands the surface on the new tenant-user
/// principal. There is no shim — <c>spring human identity ...</c> fails at
/// parse time.
/// </para>
/// <para>
/// Sub-verbs:
/// <list type="bullet">
///   <item><description><c>spring user identity set</c> — upsert the calling tenant user's connector display identity.</description></item>
///   <item><description><c>spring user identity list</c> — list every connector identity mapped to the calling tenant user.</description></item>
///   <item><description><c>spring user identity remove</c> — remove a single connector identity row.</description></item>
///   <item><description><c>spring user identity authorize-github</c> — drive the OAuth flow (ADR-0047 §13); persists the issued token as a tenant secret and refreshes the GitHub display identity.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Verb-name choice (Phase G).</b> <c>authorize-github</c> sits inside
/// <c>spring user identity</c> rather than under a separate <c>spring
/// auth</c> tree, so the operator-facing model stays "everything about
/// my identity is under one verb". The compound name is consistent with
/// the connector being explicit in the verb (the CLI's other verbs that
/// fan out by connector — e.g. <c>spring connector github label-rules
/// set</c> — name the connector inline rather than dispatching at a
/// parent level). When a second connector grows an OAuth path (Slack,
/// Linear), the symmetric verb will be
/// <c>spring user identity authorize-slack</c> etc. — easier to discover
/// than splitting <c>spring auth slack ...</c> off the
/// authentication-tokens surface owned by <c>spring auth</c>.
/// </para>
/// <para>
/// <b>Default <c>--user</c>.</b> The OSS deployment ships with exactly
/// one tenant user — the operator, pinned to
/// <see cref="OssTenantUserIds.Operator"/>. The CLI defaults
/// <c>--user</c> to that UUID when omitted; a cloud deployment with
/// real tenant users will plug a <c>/me</c>-equivalent surface in front
/// (umbrella #2487 OUT1).
/// </para>
/// </remarks>
public static class UserCommand
{
    private static readonly OutputFormatter.Column<global::Cvoya.Spring.Cli.Generated.Models.TenantUserConnectorIdentityResponse>[] IdentityColumns =
    {
        new("connector", r => r.ConnectorId),
        new("username", r => r.Username),
        new("displayHandle", r => r.DisplayHandle),
        new("updatedAt", r => r.UpdatedAt?.ToString("u")),
    };

    /// <summary>Entry point — returns the <c>user</c> command root.</summary>
    public static Command Create(Option<string> outputOption)
    {
        var command = new Command(
            "user",
            "Manage the calling TenantUser's per-connector display identity (ADR-0047 §§ 1–2, 12). " +
            "Replaces the pre-ADR-0047 'spring human identity ...' verb tree.");

        var identityCommand = new Command(
            "identity",
            "Per-connector display identity (e.g. your GitHub login). Display-only — outbound credentials live on the unit binding (ADR-0047 §§ 5–6).");

        identityCommand.Subcommands.Add(CreateSetCommand(outputOption));
        identityCommand.Subcommands.Add(CreateListCommand(outputOption));
        identityCommand.Subcommands.Add(CreateRemoveCommand());
        identityCommand.Subcommands.Add(CreateAuthorizeGitHubCommand(outputOption));

        command.Subcommands.Add(identityCommand);

        return command;
    }

    private static Command CreateSetCommand(Option<string> outputOption)
    {
        var userOption = new Option<string?>("--user")
        {
            Description =
                "Stable TenantUser UUID. Defaults to the OSS operator UUID (OssTenantUserIds.Operator) when omitted; cloud deployments override via a future /me extension (#2487 OUT1).",
        };
        var connectorOption = new Option<string>("--connector")
        {
            Description = "Connector slug the identity belongs to (e.g. 'github').",
            Required = true,
        };
        var usernameOption = new Option<string>("--username")
        {
            Description = "Connector-side login (e.g. GitHub 'octocat'). No leading '@'.",
            Required = true,
        };
        var displayHandleOption = new Option<string?>("--display-handle")
        {
            Description = "Optional human-friendly rendering (e.g. 'Alice Smith (@alice)'). Falls back to --username when omitted.",
        };

        var command = new Command(
            "set",
            "Create or update the calling TenantUser's display identity for a connector (ADR-0047 §2). Re-running with a different --username on the same connector replaces the row in place.");
        command.Options.Add(userOption);
        command.Options.Add(connectorOption);
        command.Options.Add(usernameOption);
        command.Options.Add(displayHandleOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var tenantUserArg = parseResult.GetValue(userOption);
            var connectorId = parseResult.GetValue(connectorOption)!;
            var username = parseResult.GetValue(usernameOption)!;
            var displayHandle = parseResult.GetValue(displayHandleOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (!TryResolveTenantUserId(tenantUserArg, out var tenantUserId, out var error))
            {
                await Console.Error.WriteLineAsync(error);
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();
            try
            {
                var response = await client.UpsertTenantUserConnectorIdentityAsync(
                    tenantUserId, connectorId, username, displayHandle, ct);

                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJson(response));
                }
                else
                {
                    Console.WriteLine(
                        $"Tenant user '{tenantUserId:N}' identity for connector '{connectorId}' set to '{username}'.");
                }
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await Console.Error.WriteLineAsync(
                    $"Failed to set identity for tenant user '{tenantUserId:N}' on connector '{connectorId}': " +
                    ProblemDetailsTranslator.Format(ex));
                Environment.Exit(1);
            }
        });

        return command;
    }

    private static Command CreateListCommand(Option<string> outputOption)
    {
        var userOption = new Option<string?>("--user")
        {
            Description =
                "Stable TenantUser UUID. Defaults to the OSS operator UUID when omitted.",
        };

        var command = new Command(
            "list",
            "List every connector identity mapped to the calling TenantUser (ADR-0047 §2).");
        command.Options.Add(userOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var tenantUserArg = parseResult.GetValue(userOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (!TryResolveTenantUserId(tenantUserArg, out var tenantUserId, out var error))
            {
                await Console.Error.WriteLineAsync(error);
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();
            var rows = await client.ListTenantUserConnectorIdentitiesAsync(tenantUserId, ct);

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJson(rows));
            }
            else if (rows.Count == 0)
            {
                Console.WriteLine(
                    $"Tenant user '{tenantUserId:N}' has no connector identities configured.");
            }
            else
            {
                Console.WriteLine(OutputFormatter.FormatTable(rows, IdentityColumns));
            }
        });

        return command;
    }

    private static Command CreateRemoveCommand()
    {
        var userOption = new Option<string?>("--user")
        {
            Description =
                "Stable TenantUser UUID. Defaults to the OSS operator UUID when omitted.",
        };
        var connectorOption = new Option<string>("--connector")
        {
            Description = "Connector slug the identity belongs to (e.g. 'github').",
            Required = true,
        };
        var usernameOption = new Option<string>("--username")
        {
            Description = "Connector-side login of the row to remove. No leading '@'.",
            Required = true,
        };

        var command = new Command(
            "remove",
            "Remove a single connector identity row from the calling TenantUser (ADR-0047 §2). Idempotent — removing a row that does not exist still exits zero.");
        command.Options.Add(userOption);
        command.Options.Add(connectorOption);
        command.Options.Add(usernameOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var tenantUserArg = parseResult.GetValue(userOption);
            var connectorId = parseResult.GetValue(connectorOption)!;
            var username = parseResult.GetValue(usernameOption)!;

            if (!TryResolveTenantUserId(tenantUserArg, out var tenantUserId, out var error))
            {
                await Console.Error.WriteLineAsync(error);
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();
            try
            {
                await client.RemoveTenantUserConnectorIdentityAsync(
                    tenantUserId, connectorId, username, ct);
                Console.WriteLine(
                    $"Identity '{connectorId}:{username}' removed for tenant user '{tenantUserId:N}'.");
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await Console.Error.WriteLineAsync(
                    $"Failed to remove identity '{connectorId}:{username}' for tenant user " +
                    $"'{tenantUserId:N}': {ProblemDetailsTranslator.Format(ex)}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    private static Command CreateAuthorizeGitHubCommand(Option<string> outputOption)
    {
        var userOption = new Option<string?>("--user")
        {
            Description =
                "Stable TenantUser UUID whose identity to refresh. Defaults to the OSS operator UUID when omitted.",
        };
        var noBrowserOption = new Option<bool>("--no-browser")
        {
            Description =
                "Print the authorize URL to stdout instead of launching the system browser. Useful for headless / CI flows where the operator copies the URL into a separate session.",
        };
        var portOption = new Option<int>("--callback-port")
        {
            Description =
                "Local loopback port the CLI listens on for the OAuth callback. Defaults to 0 (let the OS choose). The chosen port must match the OAuth App's registered redirect URI.",
            DefaultValueFactory = _ => 0,
        };
        var timeoutOption = new Option<int>("--timeout-seconds")
        {
            Description = "How long to wait for the browser callback before aborting.",
            DefaultValueFactory = _ => 300,
        };

        var command = new Command(
            "authorize-github",
            "Run a GitHub OAuth authorization flow for the calling TenantUser (ADR-0047 §13). " +
            "Opens the browser, listens on a loopback URL for the callback, and persists the " +
            "OAuth-issued token as a tenant secret under the §5 binding-scoped name. The persisted " +
            "secret name is printed so subsequent 'spring connector bind --pat-secret-name <name>' " +
            "calls can wire the binding directly. Also refreshes the calling TenantUser's " +
            "GitHub 'username' from the OAuth user-info response.");
        command.Options.Add(userOption);
        command.Options.Add(noBrowserOption);
        command.Options.Add(portOption);
        command.Options.Add(timeoutOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var tenantUserArg = parseResult.GetValue(userOption);
            var noBrowser = parseResult.GetValue(noBrowserOption);
            var port = parseResult.GetValue(portOption);
            var timeout = parseResult.GetValue(timeoutOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (!TryResolveTenantUserId(tenantUserArg, out var tenantUserId, out var error))
            {
                await Console.Error.WriteLineAsync(error);
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();

            // The CLI does not own the OAuth App's redirect URI — it is
            // registered with GitHub on the OAuth App config and stored in
            // GitHub:OAuth:RedirectUri on the API server. The server bakes
            // that URI into the authorize URL; the CLI's loopback listener
            // is only used in deployments where the OAuth App's redirect
            // URI points at the local CLI (e.g. operator-laptop dev
            // installs). In that mode the CLI's --callback-port must match
            // the registered port. The server-side callback page is what
            // surfaces the post-message handoff for browser-based clients;
            // here we read the post-callback session by id off the wire
            // after the browser redirects.
            //
            // ADR-0047 §13 manual-paste alternative: when GitHub:OAuth is
            // not configured, callers paste a token through
            // `spring secret set` directly under the §5 binding-scoped
            // name. That path skips this verb entirely.

            global::Cvoya.Spring.Cli.Generated.Models.OAuthAuthorizeResponse authorizeResponse;
            try
            {
                // ADR-0047 §13: request the minimum scopes for an
                // operator-driven SV unit against a repo. `repo` covers
                // read + write on private / public repos the user has
                // access to; `read:user` powers the user-info call that
                // refreshes the calling tenant user's GitHub display
                // identity. Server-side default is empty so the CLI
                // overrides explicitly rather than relying on
                // configuration on every install.
                authorizeResponse = await client.BeginGitHubOAuthAuthorizationAsync(
                    intent: "user-identity",
                    tenantUserId: tenantUserId,
                    bindingId: null,
                    scopes: new[] { "repo", "read:user" },
                    clientState: null,
                    ct: ct);
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await Console.Error.WriteLineAsync(
                    "Failed to begin GitHub OAuth authorization: " + ProblemDetailsTranslator.Format(ex));
                Environment.Exit(1);
                return;
            }

            var url = authorizeResponse.AuthorizeUrl
                ?? throw new InvalidOperationException("Server returned no authorize URL.");
            var serverState = authorizeResponse.State
                ?? throw new InvalidOperationException("Server returned no OAuth state token.");

            // Inform the operator of the flow so they can copy / paste if
            // the browser does not open automatically.
            Console.WriteLine("Opening GitHub authorize URL in your browser…");
            Console.WriteLine($"If the browser does not open, paste the URL manually:\n  {url}\n");

            if (!noBrowser)
            {
                TryOpenBrowser(url);
            }

            // Listen on a loopback port for the OAuth callback. The server
            // serves the actual callback page; this listener exists so the
            // CLI can extract `sessionId` from the redirect's query string
            // when the OAuth App's redirect URI points at the CLI itself
            // (operator-laptop dev mode). In hosted deployments where the
            // redirect points at the API server, the listener never fires;
            // the CLI falls through to a polling loop on
            // GetGitHubOAuthSessionAsync(state) but state isn't a session
            // id, so the practical UX for hosted is "complete the link in
            // the browser, then re-run with --user to refresh locally."
            // The loopback listener covers the common operator-laptop case.
            var sessionId = await WaitForCallbackAsync(
                serverState,
                port,
                TimeSpan.FromSeconds(timeout),
                ct);

            if (sessionId is null)
            {
                await Console.Error.WriteLineAsync(
                    "Timed out waiting for the GitHub OAuth callback. If your OAuth App's redirect URI points at the API server (not this CLI), complete the link in the browser — the server-side callback already persisted the secret and refreshed your identity.");
                Environment.Exit(1);
                return;
            }

            var session = await client.GetGitHubOAuthSessionAsync(sessionId, ct);
            if (session is null)
            {
                await Console.Error.WriteLineAsync(
                    $"OAuth callback completed but session '{sessionId}' was not found on the server.");
                Environment.Exit(1);
                return;
            }

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJson(session));
            }
            else
            {
                Console.WriteLine($"GitHub account linked as @{session.Login} (user id {session.UserId}).");
                if (!string.IsNullOrWhiteSpace(session.PatSecretName))
                {
                    Console.WriteLine($"Tenant secret persisted: {session.PatSecretName}");
                    Console.WriteLine(
                        "Wire a unit binding to use this credential with:\n" +
                        $"  spring connector bind --unit <id> --type github --repo <owner/repo> " +
                        $"--pat-secret-name {session.PatSecretName}");
                }
            }
        });

        return command;
    }

    /// <summary>
    /// Resolves a <c>--user</c> CLI argument to a tenant-user UUID. Accepts
    /// either a parseable Guid (dashed or no-dash) or — when omitted —
    /// defaults to the OSS operator's pinned id (ADR-0047 §3).
    /// </summary>
    internal static bool TryResolveTenantUserId(string? input, out Guid tenantUserId, out string error)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            tenantUserId = OssTenantUserIds.Operator;
            error = string.Empty;
            return true;
        }

        if (!Guid.TryParse(input, out tenantUserId) || tenantUserId == Guid.Empty)
        {
            error =
                $"--user '{input}' is not a valid TenantUser UUID. Pass the dashed or no-dash hex form, " +
                "or omit the flag to default to the OSS operator UUID.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd",
                    Arguments = $"/c start \"\" \"{url}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", url);
            }
        }
        catch
        {
            // Best-effort: if launching the browser fails the operator can
            // still copy the URL from stdout. The OS-specific commands above
            // are intentionally tried in order rather than wrapped in a single
            // ShellExecute (UseShellExecute=true is unreliable on macOS in
            // self-contained CLI distributions).
        }
    }

    /// <summary>
    /// Listens on a loopback HTTP port for the OAuth callback redirect and
    /// returns the <c>sessionId</c> query parameter when GitHub bounces
    /// the browser back. Returns <c>null</c> if the wait times out or the
    /// callback's state value does not match the one issued at authorize.
    /// </summary>
    /// <remarks>
    /// The CLI's loopback listener handles the operator-laptop dev mode
    /// where the OAuth App's redirect URI points at <c>http://localhost:&lt;port&gt;/</c>.
    /// The server-side <c>GitHubOAuthEndpoints.CallbackAsync</c> still owns
    /// the token exchange + secret persistence; if the redirect points at
    /// the server (hosted mode), GitHub fires the server callback and the
    /// browser is closed before our listener sees anything — that branch
    /// surfaces as a timeout and we instruct the operator to re-run
    /// the verb after the link completes.
    /// </remarks>
    internal static async Task<string?> WaitForCallbackAsync(
        string expectedState,
        int port,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var listener = new HttpListener();
        var actualPort = port > 0 ? port : 0;
        var prefix = $"http://localhost:{actualPort}/";
        listener.Prefixes.Add(prefix);

        try
        {
            listener.Start();
        }
        catch (HttpListenerException)
        {
            // Loopback prefix already bound by another process — the
            // caller's --callback-port collides with a running listener.
            // Fall through to "no listener", which prints the timeout
            // hint after the configured wait.
            return null;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            var contextTask = listener.GetContextAsync();
            var winner = await Task.WhenAny(contextTask, Task.Delay(timeout, timeoutCts.Token))
                .ConfigureAwait(false);
            if (winner != contextTask)
            {
                return null;
            }

            var context = await contextTask.ConfigureAwait(false);
            var query = context.Request.QueryString;
            var receivedState = query["state"];
            var sessionId = query["sessionId"];

            // Write a tiny acknowledgement page so the browser tab closes
            // cleanly. The server-side callback owns the rich postMessage
            // page; here we only see the redirect if the OAuth App points
            // at the CLI.
            var body = sessionId is not null && string.Equals(receivedState, expectedState, StringComparison.Ordinal)
                ? "<!doctype html><html><body><p>GitHub account linked. You may close this tab.</p></body></html>"
                : "<!doctype html><html><body><p>GitHub authorization failed. Return to the CLI for details.</p></body></html>";
            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.LongLength;
            await context.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
            context.Response.Close();

            if (!string.Equals(receivedState, expectedState, StringComparison.Ordinal))
            {
                return null;
            }

            return sessionId;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            try
            {
                listener.Stop();
            }
            catch
            {
                // Best-effort.
            }
        }
    }
}
