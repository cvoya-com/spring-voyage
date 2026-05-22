// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk;

/// <summary>
/// Wraps an <see cref="IMessagingClient"/> and remembers whether the
/// user's delegate has called <see cref="PostResultAsync"/>. Used by
/// <see cref="SpringAgent.RunWithResponseDisciplineAsync"/> to drive the
/// response-discipline safety net (issue #2493).
/// </summary>
/// <remarks>
/// <para>
/// Idempotent on the tracking side — multiple posts only set the flag
/// once. The wrapped client's behaviour is preserved verbatim;
/// <c>SendAsync</c> / <c>MulticastAsync</c> are pass-through and do not
/// satisfy the response-discipline contract (they deliver a message, they
/// don't return a final reply to the requester).
/// </para>
/// </remarks>
internal sealed class ResponseDisciplineTrackingClient : IMessagingClient
{
    private readonly IMessagingClient _inner;
    private int _postedFlag;

    public ResponseDisciplineTrackingClient(IMessagingClient inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public bool ResultPosted => System.Threading.Volatile.Read(ref _postedFlag) != 0;

    public async Task PostResultAsync(string threadId, string result, CancellationToken cancellationToken = default)
    {
        await _inner.PostResultAsync(threadId, result, cancellationToken).ConfigureAwait(false);
        System.Threading.Interlocked.Exchange(ref _postedFlag, 1);
    }

    /// <summary>Issues the result post without flipping the tracker — used by the safety net.</summary>
    internal Task InnerPostResultAsync(string threadId, string result, CancellationToken cancellationToken)
        => _inner.PostResultAsync(threadId, result, cancellationToken);

    public Task<MessageSendResponse> SendAsync(
        string threadId,
        string targetUnitId,
        string prompt,
        CancellationToken cancellationToken = default)
        => _inner.SendAsync(threadId, targetUnitId, prompt, cancellationToken);

    public Task<MessageMulticastResponse> MulticastAsync(
        string threadId,
        IReadOnlyList<string> targetUnitIds,
        string prompt,
        CancellationToken cancellationToken = default)
        => _inner.MulticastAsync(threadId, targetUnitIds, prompt, cancellationToken);
}
