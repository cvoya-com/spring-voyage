// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.ErrorHandling;

using System.Collections.Generic;
using System.IO;

/// <summary>
/// Caller-supplied context that drives the rendering decisions in
/// <see cref="IApiExceptionRenderer.Render"/>. Carrying these as a single
/// record keeps the signature stable when the cloud host extends with
/// branding hooks, correlation ids, etc.
/// </summary>
/// <param name="OutputFormat">
/// "table" (or any non-"json" value) selects the prose renderer;
/// "json" selects the structured envelope. Mirrors the value bound to
/// the recursive root <c>--output</c> option.
/// </param>
/// <param name="Verbose">
/// When true, the renderer also dumps the underlying exception's
/// <c>ToString()</c> (stack trace included) to stderr. Bound from the
/// recursive root <c>--verbose</c> option, with
/// <c>SPRING_CLI_DEBUG=1</c> as an env-var equivalent honoured by the
/// default renderer.
/// </param>
/// <param name="OperationDescription">
/// Optional command-supplied prefix, e.g. <c>"Failed to purge unit 'eng-team'"</c>.
/// Used by the prose renderer to anchor the error in the user's intent;
/// surfaced under <c>error.operation</c> in JSON mode.
/// </param>
/// <param name="StdOut">
/// Optional output writer override; defaults to <see cref="System.Console.Out"/>.
/// Tests pass their own <see cref="System.IO.StringWriter"/> here to capture
/// the JSON envelope without mutating the static <c>Console</c> handles
/// (xUnit parallel-test isolation).
/// </param>
/// <param name="StdErr">
/// Optional error writer override; defaults to <see cref="System.Console.Error"/>.
/// Same rationale as <see cref="StdOut"/>.
/// </param>
public sealed record CliRenderContext(
    string OutputFormat,
    bool Verbose,
    string? OperationDescription = null,
    TextWriter? StdOut = null,
    TextWriter? StdErr = null);

/// <summary>
/// Structured representation of a Kiota <see cref="Microsoft.Kiota.Abstractions.ApiException"/>
/// after the renderer has classified it. Subclasses of
/// <see cref="ApiExceptionRenderer"/> may override
/// <see cref="ApiExceptionRenderer.BuildPayload"/> to emit a different
/// shape; the JSON envelope produced by the default emitter mirrors this
/// record one-for-one.
/// </summary>
public sealed record CliErrorPayload(
    int? Status,
    string? Title,
    string? Detail,
    IReadOnlyList<string> Next,
    IReadOnlyDictionary<string, string?> Extensions);

/// <summary>
/// Top-level shape emitted to stdout by the default JSON renderer:
/// <c>{ "error": { ... } }</c>. Wrapping the body in a single
/// <c>error</c> key keeps the envelope distinguishable from a successful
/// response (which is rendered directly as the model JSON).
/// </summary>
public sealed record CliErrorEnvelope(CliErrorBody Error);

/// <summary>
/// Inner payload of <see cref="CliErrorEnvelope"/>. Fields with no value
/// are omitted from the serialised JSON
/// (<see cref="System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull"/>)
/// so the envelope stays terse for the common case.
/// </summary>
public sealed record CliErrorBody(
    int? Status,
    string? Title,
    string? Detail,
    IReadOnlyList<string>? Next,
    IReadOnlyDictionary<string, string?>? Extensions,
    string? Operation);