// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Ollama.Tests;

/// <summary>
/// Minimal <see cref="IHttpClientFactory"/> that hands out clients backed
/// by a single shared <see cref="HttpMessageHandler"/>. The runtime asks
/// for its named client via the factory; this stub returns the same
/// client regardless of the name so tests can pin behaviour with one
/// handler.
/// </summary>
internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}