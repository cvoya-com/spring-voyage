// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Collections.Concurrent;

using Cvoya.Spring.Core.Execution;

using global::Dapr.Client;

using Microsoft.Extensions.Logging;

/// <summary>
/// Thread-safe conversation-level cancellation manager. Registers a
/// <see cref="CancellationTokenSource"/> per conversation and, when configured with
/// a <see cref="DaprClient"/>, propagates cancellation to delegated execution
/// containers via a pub/sub topic.
/// </summary>
public class CancellationManager : ICancellationManager
{
    /// <summary>
    /// Pub/sub component name used to propagate cancellation events to delegated
    /// execution containers.
    /// </summary>
    public const string PubSubName = "spring-pubsub";

    /// <summary>
    /// Pub/sub topic name used for cancellation propagation.
    /// </summary>
    public const string CancelTopic = "execution.cancel";

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _sources = new();
    private readonly DaprClient? _daprClient;
    private readonly ILogger<CancellationManager> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CancellationManager"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="daprClient">Optional Dapr client used to publish cancellation events.
    /// When <c>null</c>, cancellation is not propagated beyond the local process.</param>
    public CancellationManager(ILogger<CancellationManager> logger, DaprClient? daprClient = null)
    {
        _logger = logger;
        _daprClient = daprClient;
    }

    /// <inheritdoc />
    public CancellationTokenSource Register(string conversationId)
    {
        return _sources.GetOrAdd(conversationId, _ => new CancellationTokenSource());
    }

    /// <inheritdoc />
    public CancellationToken GetToken(string conversationId)
    {
        return _sources.TryGetValue(conversationId, out var cts)
            ? cts.Token
            : CancellationToken.None;
    }

    /// <inheritdoc />
    public async Task CancelAsync(string conversationId, CancellationToken cancellationToken)
    {
        if (_sources.TryGetValue(conversationId, out var cts))
        {
            await cts.CancelAsync();
        }
        else
        {
            _logger.LogDebug(
                "CancelAsync called for conversation {ConversationId} with no registered source; propagating only.",
                conversationId);
        }

        if (_daprClient is null)
        {
            return;
        }

        try
        {
            var payload = new CancellationRequest(conversationId);
            await _daprClient.PublishEventAsync(PubSubName, CancelTopic, payload, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to publish cancellation event for conversation {ConversationId} to pubsub {PubSub}/{Topic}.",
                conversationId,
                PubSubName,
                CancelTopic);
        }
    }

    /// <inheritdoc />
    public void Unregister(string conversationId)
    {
        if (_sources.TryRemove(conversationId, out var cts))
        {
            cts.Dispose();
        }
    }

    /// <summary>
    /// Payload shape for cancellation events published to delegated execution containers.
    /// </summary>
    /// <param name="ConversationId">The conversation whose execution should be cancelled.</param>
    internal record CancellationRequest(string ConversationId);
}