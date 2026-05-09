// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Abstraction over lightweight AI model interactions — single-shot completion and
/// text streaming. Agentic work (multi-turn, tool use, planning) is delegated to
/// external agent runtimes launched via <see cref="IExecutionDispatcher"/>; this
/// provider is for utility calls such as routing decisions, classification, and
/// summarisation that don't warrant a full agent runtime.
/// </summary>
/// <remarks>
/// <para>
/// Each implementation declares a stable <see cref="Id"/> matching the manifest's
/// <c>execution.provider</c> slot value (e.g. <c>"anthropic"</c>, <c>"ollama"</c>).
/// <see cref="IAiProviderRegistry"/> enumerates every registered provider and
/// resolves callers' dispatches by that id, so a unit configured with
/// <c>provider: ollama</c> reaches <c>OllamaProvider</c> and <c>provider: anthropic</c>
/// reaches <c>AnthropicProvider</c>. The same enumerable also feeds the platform's
/// status / model-listing surfaces.
/// </para>
/// </remarks>
public interface IAiProvider
{
    /// <summary>
    /// Stable provider identifier matching the manifest's <c>execution.provider</c>
    /// slot value. Looked up by <see cref="IAiProviderRegistry"/>.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Sends a prompt to the AI model and returns the response.
    /// </summary>
    /// <param name="prompt">The prompt to send to the AI model.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The AI model's response.</returns>
    Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a prompt to the AI model and returns a stream of events as they are generated.
    /// </summary>
    /// <param name="prompt">The prompt to send to the AI model.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An asynchronous stream of <see cref="StreamEvent"/> instances.</returns>
    IAsyncEnumerable<StreamEvent> StreamCompleteAsync(string prompt, CancellationToken cancellationToken = default);
}
