// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

using Cvoya.Spring.Core.Execution;

/// <summary>
/// <see cref="HttpMessageHandler"/> that adapts an
/// <see cref="ILlmDispatcher"/> behind the standard
/// <see cref="HttpClient"/> surface so existing
/// <see cref="IAiProvider"/> implementations
/// (<see cref="OllamaProvider"/>, <see cref="AnthropicProvider"/>) flow
/// through the LLM-dispatch seam without the providers having to know
/// about it. The <see cref="ILlmDispatcher"/> implementation underneath
/// — direct via <see cref="HttpClientLlmDispatcher"/> or proxied via
/// <see cref="DispatcherProxiedLlmDispatcher"/> — decides whether the
/// upstream call leaves the worker directly or through
/// <c>spring-dispatcher</c>. Closes #1168 / ADR 0028 Decision E.
/// </summary>
/// <remarks>
/// <para>
/// The handler always uses <see cref="ILlmDispatcher.SendStreamingAsync"/>
/// and surfaces the response body as a stream-backed
/// <see cref="HttpContent"/>. Non-streaming consumers (which call
/// <c>ReadFromJsonAsync</c> / <c>ReadAsStringAsync</c>) read to EOF and
/// see the full response; streaming consumers (which call
/// <c>ReadAsStreamAsync</c> on a <see cref="HttpCompletionOption.ResponseHeadersRead"/>
/// response) iterate chunks as they arrive. Going via the streaming
/// path uniformly lets us collapse two transport variants down to one
/// and avoids having the handler guess the streaming-or-not intent of
/// the request — a guess that depends on inspecting JSON bodies for
/// <c>"stream": true</c>, which is fragile (Anthropic and Ollama spell
/// it differently and the field is per-provider) and fundamentally
/// up to the caller, not the transport.
/// </para>
/// <para>
/// Failure shape: transport failures from
/// <see cref="ILlmDispatcher.SendStreamingAsync"/> propagate as
/// <see cref="HttpRequestException"/> on the first read, matching what
/// the providers see today when <see cref="HttpClient"/> is talking to
/// a dead upstream. The single-shot 502-on-failure behaviour of
/// <see cref="ILlmDispatcher.SendAsync"/> is intentionally not used
/// here because the provider layer already has its own retry / failover
/// classification keyed on <see cref="HttpRequestException"/>.
/// </para>
/// </remarks>
internal sealed class LlmHttpMessageHandler(ILlmDispatcher dispatcher) : HttpMessageHandler
{
    private readonly ILlmDispatcher _dispatcher = dispatcher
        ?? throw new ArgumentNullException(nameof(dispatcher));

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post)
        {
            throw new NotSupportedException(
                $"LlmHttpMessageHandler only supports POST; got {request.Method}. "
                + "All current IAiProvider implementations send POST /v1/chat/completions or POST /v1/messages; "
                + "any other verb must add an explicit ILlmDispatcher primitive.");
        }

        if (request.RequestUri is null || !request.RequestUri.IsAbsoluteUri)
        {
            throw new InvalidOperationException(
                "LLM request must have an absolute RequestUri. Configure the HttpClient with a BaseAddress "
                + "or pass an absolute URI to the IAiProvider.");
        }

        var bodyBytes = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(cancellationToken);

        var headers = ExtractHeaders(request);
        var dispatchRequest = new LlmDispatchRequest(
            Url: request.RequestUri.ToString(),
            Body: bodyBytes,
            Headers: headers);

        // Spin up the streaming enumerator before constructing the
        // response so any synchronous failure surfaces here rather than
        // on the first content read deep inside the provider's parsing
        // loop.
        var enumerator = _dispatcher.SendStreamingAsync(dispatchRequest, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new EnumerableHttpContent(enumerator),
            RequestMessage = request,
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };
        return response;
    }

    private static Dictionary<string, string>? ExtractHeaders(HttpRequestMessage request)
    {
        Dictionary<string, string>? headers = null;

        foreach (var (name, values) in request.Headers)
        {
            headers ??= new(StringComparer.OrdinalIgnoreCase);
            headers[name] = string.Join(',', values);
        }

        if (request.Content?.Headers is { } contentHeaders)
        {
            foreach (var (name, values) in contentHeaders)
            {
                headers ??= new(StringComparer.OrdinalIgnoreCase);
                headers[name] = string.Join(',', values);
            }
        }

        return headers;
    }

    /// <summary>
    /// <see cref="HttpContent"/> that drains an
    /// <see cref="IAsyncEnumerator{T}"/> of byte chunks as a stream.
    /// Owns the enumerator and disposes it when the content is disposed
    /// — covering both the success path and exceptions during read.
    /// </summary>
    private sealed class EnumerableHttpContent(IAsyncEnumerator<ReadOnlyMemory<byte>> enumerator) : HttpContent
    {
        private IAsyncEnumerator<ReadOnlyMemory<byte>>? _enumerator = enumerator;

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await SerializeToStreamAsync(stream, context, CancellationToken.None);
        }

        protected override async Task SerializeToStreamAsync(
            Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            if (_enumerator is null)
            {
                return;
            }

            while (await _enumerator.MoveNextAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = _enumerator.Current;
                await stream.WriteAsync(chunk, cancellationToken);
            }
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
            => CreateContentReadStreamAsync(CancellationToken.None);

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            if (_enumerator is null)
            {
                throw new InvalidOperationException("The LLM response stream has already been consumed or disposed.");
            }

            // Hand the enumerator over to the stream wrapper. The wrapper
            // becomes responsible for disposing it; we null out our
            // reference so a subsequent dispose on this content doesn't
            // try to dispose it a second time underneath the stream.
            var stream = new EnumerableReadStream(_enumerator);
            _enumerator = null;
            return Task.FromResult<Stream>(stream);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _enumerator is not null)
            {
                // Best-effort sync dispose so we don't strand the enumerator
                // when the consumer abandons the response (e.g. throws
                // mid-parse). DisposeAsync().AsTask() is fine here because
                // the enumerator's underlying resources (the dispatcher's
                // HttpClient response stream) are designed to be torn down
                // synchronously on cancellation.
                try
                {
                    _enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch
                {
                    // Swallow — disposal is best-effort; surfacing exceptions
                    // here would mask the original error that caused the
                    // consumer to bail.
                }
                _enumerator = null;
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// <see cref="Stream"/> facade over an
    /// <see cref="IAsyncEnumerator{T}"/> of byte chunks. Keeps a small
    /// in-flight buffer so callers reading less than a full upstream
    /// chunk's worth of bytes get the partial slice they asked for.
    /// </summary>
    private sealed class EnumerableReadStream(IAsyncEnumerator<ReadOnlyMemory<byte>> enumerator) : Stream
    {
        private readonly IAsyncEnumerator<ReadOnlyMemory<byte>> _enumerator = enumerator;
        private ReadOnlyMemory<byte> _current = ReadOnlyMemory<byte>.Empty;
        private bool _completed;
        private bool _disposed;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_current.Length == 0 && !_completed)
            {
                if (await _enumerator.MoveNextAsync())
                {
                    _current = _enumerator.Current;
                }
                else
                {
                    _completed = true;
                }
            }

            if (_current.Length == 0)
            {
                return 0;
            }

            var toCopy = Math.Min(buffer.Length, _current.Length);
            _current.Span[..toCopy].CopyTo(buffer.Span);
            _current = _current[toCopy..];
            return toCopy;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                try
                {
                    _enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch
                {
                    // Best-effort — see EnumerableHttpContent.Dispose.
                }
            }

            base.Dispose(disposing);
        }
    }
}