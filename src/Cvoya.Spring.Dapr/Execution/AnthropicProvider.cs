// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// AI provider implementation that calls the Anthropic Messages API.
/// </summary>
public class AnthropicProvider(
    HttpClient httpClient,
    IOptions<AiProviderOptions> options,
    ILoggerFactory loggerFactory) : IAiProvider
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<AnthropicProvider>();
    private readonly AiProviderOptions _options = options.Value;

    private const string AnthropicVersion = "2023-06-01";
    private const int MaxRetries = 3;

    /// <inheritdoc />
    public async Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var requestBody = new
        {
            model = _options.Model,
            max_tokens = _options.MaxTokens,
            messages = new[]
            {
                new { role = "user", content = prompt }
            }
        };

        var attempt = 0;
        while (true)
        {
            attempt++;
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl}/v1/messages");
            request.Headers.Add("x-api-key", _options.ApiKey);
            request.Headers.Add("anthropic-version", AnthropicVersion);
            request.Content = JsonContent.Create(requestBody);

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                if (attempt >= MaxRetries)
                {
                    throw new SpringException($"Anthropic API request failed after {MaxRetries} attempts.", ex);
                }

                _logger.LogWarning(ex, "Anthropic API request failed on attempt {Attempt}, retrying.", attempt);
                await DelayBeforeRetryAsync(attempt, cancellationToken);
                continue;
            }

            if (response.StatusCode is HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                if (attempt >= MaxRetries)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new SpringException(
                        $"Anthropic API returned {response.StatusCode} after {MaxRetries} attempts. Response: {errorBody}");
                }

                _logger.LogWarning("Anthropic API returned {StatusCode} on attempt {Attempt}, retrying.",
                    response.StatusCode, attempt);
                response.Dispose();
                await DelayBeforeRetryAsync(attempt, cancellationToken);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new SpringException(
                    $"Anthropic API returned {response.StatusCode}. Response: {errorBody}");
            }

            return await ParseResponseAsync(response, cancellationToken);
        }
    }

    private async Task<string> ParseResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        if (json.TryGetProperty("usage", out var usage))
        {
            var stats = new UsageStats(
                usage.GetProperty("input_tokens").GetInt32(),
                usage.GetProperty("output_tokens").GetInt32());
            _logger.LogInformation("Anthropic usage — input: {InputTokens}, output: {OutputTokens}",
                stats.InputTokens, stats.OutputTokens);
        }

        if (json.TryGetProperty("content", out var content) &&
            content.GetArrayLength() > 0)
        {
            return content[0].GetProperty("text").GetString()
                   ?? throw new SpringException("Anthropic API returned null text in content.");
        }

        throw new SpringException("Anthropic API response did not contain expected content.");
    }

    private static async Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));
        await Task.Delay(delay, cancellationToken);
    }
}
