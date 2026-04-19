// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="IProviderCliInvoker"/> backed by the locally-installed
/// <c>claude</c> CLI. Validates a caller-supplied credential (API key
/// or Claude.ai OAuth token) by running <c>claude --bare -p
/// --output-format json "ping"</c> with the credential placed in the
/// appropriate env var; the CLI handles both formats transparently so
/// the portal never has to distinguish OAuth tokens from API keys
/// (#660).
/// </summary>
/// <remarks>
/// <para>
/// <b>Credential plumbing.</b> Two env vars are recognised by the CLI:
/// <c>ANTHROPIC_API_KEY</c> for Platform-plan API keys, and
/// <c>CLAUDE_CODE_OAUTH_TOKEN</c> for Claude.ai tokens produced by
/// <c>claude setup-token</c>. We pick based on the credential prefix
/// (<c>sk-ant-oat</c> signals OAuth; anything else falls through to the
/// API-key var, which the CLI then validates). The credential is never
/// echoed on the command line or written to disk.
/// </para>
/// <para>
/// <b>Availability probe.</b> <see cref="IsAvailableAsync"/> runs
/// <c>claude --version</c> once and caches the outcome for the lifetime
/// of the process — operators don't install/uninstall the CLI mid-run
/// and the spawn cost (~50ms) shouldn't multiply across wizard loads.
/// </para>
/// <para>
/// <b>No model enumeration.</b> The <c>claude</c> CLI does not expose
/// a <c>models</c> subcommand today, so this invoker reports
/// <c>Models = null</c> on success. The wizard's validator falls back
/// to <see cref="ModelCatalog"/>'s curated static list for Claude in
/// that case — live enumeration for CLI-validated credentials is
/// tracked as a follow-up.
/// </para>
/// </remarks>
public class ClaudeCliInvoker : IProviderCliInvoker
{
    /// <summary>
    /// Default executable name — discovered on <c>PATH</c>. Overridable
    /// via <see cref="ExecutablePath"/> for tests.
    /// </summary>
    public const string DefaultExecutable = "claude";

    /// <summary>Credential prefix that identifies a Claude.ai OAuth token (<c>claude setup-token</c>).</summary>
    internal const string OAuthTokenPrefix = "sk-ant-oat";

    /// <summary>Hard cap on CLI invocation duration — a hung CLI cannot stall the wizard indefinitely.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly IProcessRunner _processRunner;
    private readonly ILogger<ClaudeCliInvoker> _logger;
    private readonly SemaphoreSlim _availabilityLock = new(1, 1);
    private bool? _cachedAvailability;

    /// <summary>Constructs the invoker with the default process runner.</summary>
    public ClaudeCliInvoker(ILogger<ClaudeCliInvoker> logger)
        : this(DefaultProcessRunner.Instance, logger)
    {
    }

    /// <summary>Constructs the invoker with an injected process runner (used by tests).</summary>
    public ClaudeCliInvoker(IProcessRunner processRunner, ILogger<ClaudeCliInvoker> logger)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    /// <summary>
    /// Executable path / name. Defaults to <see cref="DefaultExecutable"/>
    /// which the OS resolves via <c>PATH</c>. Settable for tests.
    /// </summary>
    public string ExecutablePath { get; set; } = DefaultExecutable;

    /// <inheritdoc />
    public string ProviderId => "anthropic";

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedAvailability is { } cached) return cached;

        await _availabilityLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedAvailability is { } stillCached) return stillCached;

            try
            {
                var probe = await _processRunner.RunAsync(
                    ExecutablePath,
                    new[] { "--version" },
                    environment: new Dictionary<string, string>(),
                    timeout: TimeSpan.FromSeconds(5),
                    cancellationToken).ConfigureAwait(false);

                _cachedAvailability = probe.ExitCode == 0;
                if (_cachedAvailability == false)
                {
                    _logger.LogDebug(
                        "claude --version exited with code {ExitCode}; treating CLI as unavailable.",
                        probe.ExitCode);
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
            {
                _logger.LogDebug(ex, "claude CLI not found on host (PATH miss).");
                _cachedAvailability = false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "claude CLI availability probe failed.");
                _cachedAvailability = false;
            }

            return _cachedAvailability ?? false;
        }
        finally
        {
            _availabilityLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ProviderCliValidationResult> ValidateAsync(
        string credential,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credential))
        {
            return new ProviderCliValidationResult(
                ProviderCredentialValidationStatus.MissingKey,
                Models: null,
                ErrorMessage: "Supply an API key or Claude.ai token to validate.");
        }

        var isOAuth = credential.StartsWith(OAuthTokenPrefix, StringComparison.Ordinal);
        var environment = new Dictionary<string, string>
        {
            // Bare mode disables hooks, plugin sync, and auto-memory
            // lookup so the spawn is minimal and does not touch the
            // operator's on-disk Claude config. The CLI reads OAuth
            // tokens from CLAUDE_CODE_OAUTH_TOKEN and API keys from
            // ANTHROPIC_API_KEY — we set exactly one.
            [isOAuth ? "CLAUDE_CODE_OAUTH_TOKEN" : "ANTHROPIC_API_KEY"] = credential,
        };

        ProcessRunResult result;
        try
        {
            result = await _processRunner.RunAsync(
                ExecutablePath,
                new[] { "--bare", "-p", "--output-format", "json", "respond with OK" },
                environment,
                DefaultTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            _logger.LogWarning(ex, "claude CLI not found when validating Anthropic credential.");
            return new ProviderCliValidationResult(
                ProviderCredentialValidationStatus.ProviderError,
                Models: null,
                ErrorMessage: "The claude CLI was not found on the host. Install Claude Code to validate this credential format.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "claude CLI timed out while validating Anthropic credential.");
            return new ProviderCliValidationResult(
                ProviderCredentialValidationStatus.NetworkError,
                Models: null,
                ErrorMessage: "The claude CLI did not respond within the allowed time. Retry, or use an Anthropic API key instead.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "claude CLI invocation failed.");
            return new ProviderCliValidationResult(
                ProviderCredentialValidationStatus.ProviderError,
                Models: null,
                ErrorMessage: $"The claude CLI failed to run: {ex.Message}");
        }

        // The CLI emits one JSON object on stdout in --output-format
        // json mode. Non-zero exit + empty / unparseable stdout = treat
        // as a provider error; the credential itself might be valid
        // but we cannot tell.
        ClaudeCliResult? parsed = null;
        var stdoutTrimmed = result.StandardOutput.Trim();
        if (stdoutTrimmed.Length > 0)
        {
            try
            {
                parsed = (ClaudeCliResult?)JsonSerializer.Deserialize(
                    stdoutTrimmed,
                    typeof(ClaudeCliResult),
                    ClaudeCliJsonContext.Default);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "claude CLI produced non-JSON output on stdout.");
            }
        }

        if (parsed is null)
        {
            return new ProviderCliValidationResult(
                ProviderCredentialValidationStatus.ProviderError,
                Models: null,
                ErrorMessage: result.ExitCode == 0
                    ? "The claude CLI returned an empty response. Retry, or use an Anthropic API key instead."
                    : $"The claude CLI exited with code {result.ExitCode} and produced no parseable result.");
        }

        if (parsed.IsError == true)
        {
            // Status 401 / 403 = rejected credential. Anything else
            // (5xx, 429, network-layer) is a provider-side problem.
            if (parsed.ApiErrorStatus is 401 or 403)
            {
                return new ProviderCliValidationResult(
                    ProviderCredentialValidationStatus.Unauthorized,
                    Models: null,
                    ErrorMessage: string.IsNullOrWhiteSpace(parsed.Result)
                        ? "Anthropic rejected the credential. Check that the API key or Claude.ai token is live."
                        : $"Anthropic rejected the credential: {parsed.Result}");
            }
            return new ProviderCliValidationResult(
                ProviderCredentialValidationStatus.ProviderError,
                Models: null,
                ErrorMessage: string.IsNullOrWhiteSpace(parsed.Result)
                    ? $"Anthropic returned an error (HTTP {parsed.ApiErrorStatus?.ToString() ?? "?"})."
                    : $"Anthropic returned an error: {parsed.Result}");
        }

        // Success — the CLI returned a valid response for this
        // credential. The CLI does not expose a model-enumeration
        // subcommand today; the validator falls back to the static
        // curated list for Claude.
        return new ProviderCliValidationResult(
            ProviderCredentialValidationStatus.Valid,
            Models: null,
            ErrorMessage: null);
    }

}

/// <summary>
/// Shape of the <c>claude --output-format json</c> response. Only the
/// fields the validator reads are declared; others are ignored.
/// </summary>
internal sealed record ClaudeCliResult(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("subtype")] string? Subtype,
    [property: JsonPropertyName("is_error")] bool? IsError,
    [property: JsonPropertyName("api_error_status")] int? ApiErrorStatus,
    [property: JsonPropertyName("result")] string? Result);

[JsonSerializable(typeof(ClaudeCliResult))]
internal partial class ClaudeCliJsonContext : JsonSerializerContext;

/// <summary>
/// Abstraction over <see cref="Process"/> so the CLI invoker is
/// testable without shelling out. The default implementation
/// (<see cref="DefaultProcessRunner"/>) wraps <see cref="Process"/>
/// directly.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with the given arguments and
    /// environment overlay. Returns once the process exits or
    /// <paramref name="timeout"/> elapses.
    /// </summary>
    Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>Captures the outcome of an <see cref="IProcessRunner"/> call.</summary>
public record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Default <see cref="IProcessRunner"/>; wraps <see cref="Process"/>.</summary>
public sealed class DefaultProcessRunner : IProcessRunner
{
    /// <summary>Shared instance — stateless.</summary>
    public static readonly DefaultProcessRunner Instance = new();

    /// <inheritdoc />
    public async Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }
        foreach (var kv in environment)
        {
            psi.Environment[kv.Key] = kv.Value;
        }

        using var process = new Process { StartInfo = psi };
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutBuilder.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrBuilder.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"{fileName} did not exit within {timeout}.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new ProcessRunResult(
            ExitCode: process.ExitCode,
            StandardOutput: stdoutBuilder.ToString(),
            StandardError: stderrBuilder.ToString());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort — the process may have exited between the
            // HasExited check and the Kill call.
        }
    }
}