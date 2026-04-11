// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

/// <summary>
/// Configuration options for the AI provider.
/// </summary>
public class AiProviderOptions
{
    /// <summary>
    /// The configuration section name.
    /// </summary>
    public const string SectionName = "AiProvider";

    /// <summary>
    /// The API key for authenticating with the AI provider.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// The model identifier to use for completions.
    /// </summary>
    public string Model { get; set; } = "claude-sonnet-4-20250514";

    /// <summary>
    /// The maximum number of tokens to generate in a response.
    /// </summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    /// The base URL for the AI provider API.
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.anthropic.com";
}