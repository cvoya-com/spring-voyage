// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Threading.Channels;

using Microsoft.AspNetCore.Http;

/// <summary>
/// Server-Sent-Events keepalive helper shared by the activity and interactions
/// streams. A live-activity stream can sit idle for minutes between real
/// events; without periodic bytes the connection is torn down at a proxy /
/// client idle timeout (Caddy on the public edge, the Next.js relay's undici
/// fetch with its 300 s default body timeout, the browser EventSource), which
/// surfaced as 502/EOF at ~107–301 s (#3006 finding H). Emitting an SSE comment
/// line at a fixed cadence keeps the connection warm under every layer.
/// </summary>
internal static class SseKeepAlive
{
    /// <summary>
    /// Idle interval after which a keepalive comment is written. Chosen well
    /// below the observed ~107 s lower-bound cutoff so a single missed window
    /// still leaves ample margin under the shortest downstream timeout.
    /// </summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Waits for the channel to have at least one item to read, writing an SSE
    /// keepalive comment (<c>": keepalive\n\n"</c>) every <see cref="Interval"/>
    /// the stream stays idle. SSE comment lines start with <c>:</c> and are
    /// ignored by <c>EventSource</c> clients, so they keep bytes flowing without
    /// perturbing the event sequence. Returns <c>true</c> when data is ready to
    /// drain and <c>false</c> when the channel has completed (stream ended or
    /// faulted).
    /// </summary>
    internal static async Task<bool> WaitForDataOrKeepAliveAsync<T>(
        ChannelReader<T> reader,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var waitTask = reader.WaitToReadAsync(cancellationToken).AsTask();
            var delayTask = Task.Delay(Interval, delayCts.Token);
            var completed = await Task.WhenAny(waitTask, delayTask).ConfigureAwait(false);

            if (completed == waitTask)
            {
                delayCts.Cancel(); // stop the abandoned keepalive timer
                return await waitTask.ConfigureAwait(false);
            }

            // Idle interval elapsed with no event — send a comment to keep the
            // connection alive, then wait again.
            await response.WriteAsync(": keepalive\n\n", cancellationToken).ConfigureAwait(false);
            await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
