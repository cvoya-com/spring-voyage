// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Observability;

/// <summary>
/// Looks up a single message by id (#1209). Like
/// <see cref="IThreadQueryService"/> the default OSS implementation
/// projects from the activity-event store — each <c>MessageReceived</c>
/// event stamps the message envelope (id / from / to / payload) on its
/// <c>Details</c> JSON. Cloud overlays can swap the implementation to read
/// from a dedicated message store without touching call sites.
/// </summary>
public interface IMessageQueryService
{
    /// <summary>
    /// Returns the message detail (envelope + body) for <paramref name="messageId"/>,
    /// or <c>null</c> when no event carrying that id has been observed.
    /// </summary>
    /// <param name="messageId">The message identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<MessageDetail?> GetAsync(Guid messageId, CancellationToken cancellationToken);
}