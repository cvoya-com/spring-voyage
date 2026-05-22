// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk;

/// <summary>
/// Bundle of platform-supplied clients handed to a Spring Voyage agent
/// runtime by <see cref="SpringAgent.FromEnvironmentWithTelemetry"/> and
/// <see cref="SpringAgent.RunWithResponseDisciplineAsync"/>. Mirrors
/// the surface the Python SDK exposes via <c>RuntimeContext.current()</c>.
/// Issue #2493.
/// </summary>
/// <param name="Messaging">
/// Callback client for posting results / sending / multicasting messages.
/// </param>
/// <param name="Telemetry">
/// OTLP telemetry primitives — progress, tool-call spans, llm-turn
/// spans. All emissions are best-effort and never block the reply path.
/// </param>
public sealed record SpringAgentBundle(
    IMessagingClient Messaging,
    ITelemetryClient Telemetry);
