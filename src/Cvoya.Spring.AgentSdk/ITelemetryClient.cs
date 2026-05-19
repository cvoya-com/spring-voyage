// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk;

/// <summary>
/// Runtime-author-facing telemetry primitives for the Spring Voyage
/// platform's OTLP plane (issue #2492 + #2493). Mirrors the Python
/// SDK's <c>RuntimeContext</c> surface.
/// </summary>
/// <remarks>
/// <para>
/// All emissions are best-effort: a tripped rate limiter, a disabled
/// emitter, or a transport failure must NOT block the agent's reply
/// path. Methods on this interface return without raising on transport
/// failure; the caller's logic flow does not need to handle telemetry
/// errors.
/// </para>
/// <para>
/// Emission shapes match the ingest contract defined by
/// <c>Cvoya.Spring.Host.Api.Endpoints.Otlp.OtlpEventMapper</c>:
/// <c>sv.progress</c> span events get promoted to
/// <c>RuntimeProgress</c> activity events; <c>sv.tool.call</c> and
/// <c>sv.llm.turn</c> spans get promoted to the corresponding typed
/// activity events.
/// </para>
/// </remarks>
public interface ITelemetryClient
{
    /// <summary>
    /// Emit a narrative progress event on the active turn span.
    /// </summary>
    /// <param name="text">Free-text message describing the progress beat.</param>
    /// <param name="kind">Optional event-kind discriminator surfaced as an attribute.</param>
    /// <param name="attributes">Optional structured attributes.</param>
    /// <returns><c>true</c> if the event was queued for emission, <c>false</c> if dropped (rate-limited / disabled).</returns>
    bool ReportProgress(string text, string? kind = null, IReadOnlyDictionary<string, object?>? attributes = null);

    /// <summary>
    /// Open a span for one tool / skill invocation. The returned
    /// disposable closes the span on dispose; before disposing, the
    /// caller may attach a result or exception via
    /// <see cref="IToolCallSpan.SetResult(object?)"/> /
    /// <see cref="IToolCallSpan.SetError(Exception)"/>.
    /// </summary>
    IToolCallSpan ToolCall(string name, object? arguments = null);

    /// <summary>
    /// Open a span for one LLM call. The caller attaches the completion
    /// + optional token counts via
    /// <see cref="ILlmTurnSpan.SetCompletion(string?, int?, int?)"/>.
    /// </summary>
    ILlmTurnSpan LlmTurn(string model, string? prompt = null);
}

/// <summary>Open tool-call span. Disposing the span flushes it to OTLP.</summary>
public interface IToolCallSpan : IDisposable
{
    /// <summary>Trace id of the parent turn — useful for correlation in logs.</summary>
    string TraceId { get; }

    /// <summary>Span id assigned to this tool call.</summary>
    string SpanId { get; }

    /// <summary>Records the tool's return value; attached to the span on dispose.</summary>
    void SetResult(object? result);

    /// <summary>Records an exception; attached to the span as an error status on dispose.</summary>
    void SetError(Exception error);
}

/// <summary>Open LLM-turn span. Disposing the span flushes it to OTLP.</summary>
public interface ILlmTurnSpan : IDisposable
{
    string TraceId { get; }

    string SpanId { get; }

    /// <summary>Records the completion + optional token counts.</summary>
    void SetCompletion(string? completion, int? tokensInput = null, int? tokensOutput = null);

    /// <summary>Records an exception; the span ends with an error status.</summary>
    void SetError(Exception error);
}
