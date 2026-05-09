// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk;

using System.Text.Json;

/// <summary>
/// Entry point for runtime images running inside Spring Voyage.
/// </summary>
public static class SpringAgent
{
    private const string CallbackTokenPayloadField = "callbackToken";

    /// <summary>
    /// Creates an OrchestrationClient configured from the standard environment variables.
    /// Throws MissingCallbackEnvironmentException if SPRING_CALLBACK_URL or
    /// SPRING_CALLBACK_TOKEN are absent.
    /// </summary>
    public static IOrchestrationClient FromEnvironment() => FromEnvironment(inboundMessageBody: null);

    /// <summary>
    /// Creates an OrchestrationClient configured from the standard environment variables,
    /// preferring a per-message <c>message.metadata.callbackToken</c> from the inbound message body when present.
    /// </summary>
    public static IOrchestrationClient FromEnvironment(string? inboundMessageBody)
    {
        var callbackUrl = ReadRequiredEnvironmentVariable(
            AgentSdkEnvironmentContract.CallbackUrlEnvVar);
        var callbackToken = TryReadCallbackToken(inboundMessageBody) ?? ReadRequiredEnvironmentVariable(
            AgentSdkEnvironmentContract.CallbackTokenEnvVar);

        return new OrchestrationClient(callbackUrl, callbackToken);
    }

    /// <summary>
    /// Creates an OrchestrationClient configured from the standard environment variables,
    /// preferring a per-message <c>message.metadata.callbackToken</c> from the inbound message body when present.
    /// </summary>
    public static IOrchestrationClient FromEnvironment(JsonElement inboundMessageBody)
    {
        var callbackUrl = ReadRequiredEnvironmentVariable(
            AgentSdkEnvironmentContract.CallbackUrlEnvVar);
        var callbackToken = TryReadCallbackToken(inboundMessageBody) ?? ReadRequiredEnvironmentVariable(
            AgentSdkEnvironmentContract.CallbackTokenEnvVar);

        return new OrchestrationClient(callbackUrl, callbackToken);
    }

    private static string ReadRequiredEnvironmentVariable(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new MissingCallbackEnvironmentException(variableName);
        }

        return value;
    }

    private static string? TryReadCallbackToken(string? inboundMessageBody)
    {
        if (string.IsNullOrWhiteSpace(inboundMessageBody))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(inboundMessageBody);
            return TryReadCallbackToken(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryReadCallbackToken(JsonElement inboundMessageBody)
    {
        if (inboundMessageBody.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var payload = inboundMessageBody;
        if (inboundMessageBody.TryGetProperty("params", out var parameters))
        {
            payload = parameters;
        }

        if (!payload.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("metadata", out var metadata) ||
            metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty(CallbackTokenPayloadField, out var metadataToken) ||
            metadataToken.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var token = metadataToken.GetString();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }
}
