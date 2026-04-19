// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Configuration;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Configuration;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Options;

/// <summary>
/// Optional tier-1 requirement: the Ollama server (configured via the
/// <c>LanguageModel:Ollama:*</c> section) is reachable at host startup.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the one-shot <c>OllamaHealthCheck</c> hosted service — this
/// requirement runs the same probe through the framework so the portal and
/// CLI can surface the outcome consistently with every other subsystem.
/// Registration is gated on the same <c>LanguageModel:Ollama:Enabled</c>
/// flag that controls whether the Ollama provider is installed; nothing
/// probes the server when Ollama isn't in play.
/// </para>
/// <para>
/// <b>Mandatory flag.</b> Mirrors
/// <see cref="OllamaOptions.RequireHealthyAtStartup"/> — operators who want a
/// healthy Ollama at boot (production deployments that depend on it) flip
/// the flag; dev defaults leave it false so the host boots even when
/// Ollama is still warming up.
/// </para>
/// </remarks>
public sealed class OllamaConfigurationRequirement(
    IOptions<OllamaOptions> optionsAccessor,
    IHttpClientFactory httpClientFactory) : IConfigurationRequirement
{
    private readonly OllamaOptions _options = optionsAccessor.Value;

    /// <inheritdoc />
    public string RequirementId => "ollama-endpoint";

    /// <inheritdoc />
    public string DisplayName => "Ollama endpoint reachability";

    /// <inheritdoc />
    public string SubsystemName => "Ollama";

    /// <inheritdoc />
    public bool IsMandatory => _options.RequireHealthyAtStartup;

    /// <inheritdoc />
    public IReadOnlyList<string> EnvironmentVariableNames { get; } =
        new[] { "LanguageModel__Ollama__BaseUrl", "LanguageModel__Ollama__RequireHealthyAtStartup" };

    /// <inheritdoc />
    public string? ConfigurationSectionPath => OllamaOptions.SectionName;

    /// <inheritdoc />
    public string Description =>
        "Local-first LLM provider. The base URL is probed with GET /api/tags at startup; a failure is fatal when LanguageModel:Ollama:RequireHealthyAtStartup is true.";

    /// <inheritdoc />
    public Uri? DocumentationUrl { get; } =
        new Uri("https://github.com/cvoya-com/spring-voyage/blob/main/docs/developer/local-ai-ollama.md", UriKind.Absolute);

    /// <inheritdoc />
    public async Task<ConfigurationRequirementStatus> ValidateAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            var reason = "LanguageModel:Ollama:BaseUrl is empty.";
            var suggestion =
                "Set LanguageModel:Ollama:BaseUrl to the Ollama server URL (default http://spring-ollama:11434).";
            return _options.RequireHealthyAtStartup
                ? ConfigurationRequirementStatus.Invalid(reason, suggestion,
                    new InvalidOperationException(reason + " " + suggestion))
                : ConfigurationRequirementStatus.Disabled(reason, suggestion);
        }

        var endpoint = $"{_options.BaseUrl.TrimEnd('/')}/api/tags";

        using var httpClient = httpClientFactory.CreateClient(nameof(OllamaConfigurationRequirement));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.HealthCheckTimeoutSeconds)));

        try
        {
            using var response = await httpClient.GetAsync(endpoint, cts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return ConfigurationRequirementStatus.Met();
            }

            return BuildFailureStatus(
                $"Ollama /api/tags returned HTTP {(int)response.StatusCode} {response.StatusCode} at {endpoint}.",
                $"Check that the Ollama server at {_options.BaseUrl} is reachable and not degraded.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BuildFailureStatus(
                $"Ollama health check timed out after {_options.HealthCheckTimeoutSeconds}s at {endpoint}.",
                $"Raise LanguageModel:Ollama:HealthCheckTimeoutSeconds if your server is slow to start, or verify the endpoint is reachable.");
        }
        catch (HttpRequestException ex)
        {
            return BuildFailureStatus(
                $"Ollama health check could not reach {endpoint}: {ex.Message}",
                $"Confirm the Ollama server is running and reachable at {_options.BaseUrl}. On macOS with host-installed Ollama use http://host.containers.internal:11434.");
        }
    }

    private ConfigurationRequirementStatus BuildFailureStatus(string reason, string suggestion)
    {
        if (_options.RequireHealthyAtStartup)
        {
            return ConfigurationRequirementStatus.Invalid(
                reason,
                suggestion,
                new InvalidOperationException(reason + " " + suggestion));
        }

        return ConfigurationRequirementStatus.Disabled(reason, suggestion);
    }
}