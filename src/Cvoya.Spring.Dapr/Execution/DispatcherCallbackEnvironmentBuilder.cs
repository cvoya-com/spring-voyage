// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Runtime;

using Microsoft.Extensions.Options;

/// <summary>
/// Builds the OTLP-ingest callback environment every launcher stamps onto an
/// agent runtime container.
/// </summary>
/// <remarks>
/// ADR-0054 retired the messaging callback surface and its per-turn JWT —
/// <c>sv.messaging.*</c> is served by the single platform MCP server. This
/// builder survives as the OTLP-ingest credential path: it stamps
/// <c>SPRING_CALLBACK_URL</c> (from <see cref="CallbackBaseUrlOptions.BaseUrl"/>)
/// and <c>SPRING_CALLBACK_TOKEN</c> (a JWT minted via <see cref="Core.Runtime.ICallbackTokenIssuer"/>),
/// which <c>LauncherOtelEnvironment</c> consumes to wire the <c>/otlp</c>
/// ingest endpoint and its bearer token.
/// </remarks>
public class DispatcherCallbackEnvironmentBuilder(
    IOptions<CallbackBaseUrlOptions> callbackOptions,
    ICallbackTokenIssuer callbackTokenIssuer) : IAgentCallbackEnvironmentBuilder
{
    private readonly CallbackBaseUrlOptions _callbackOptions = callbackOptions.Value;
    private readonly ICallbackTokenIssuer _callbackTokenIssuer = callbackTokenIssuer;

    /// <inheritdoc />
    public void AddCallbackEnvironment(
        AgentLaunchContext context,
        IDictionary<string, string> environmentVariables)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environmentVariables);

        var callbackBaseUrl = ResolveCallbackBaseUrl();
        var address = ResolveAgentAddress(context);
        var threadId = ResolveThreadId(context);
        var messageId = ResolveMessageId(context);

        var token = _callbackTokenIssuer.Issue(new CallbackToken(
            context.TenantId,
            address,
            threadId,
            messageId,
            ExpiresAt: default));

        environmentVariables[AgentCallbackEnvironmentContract.CallbackUrlEnvVar] = callbackBaseUrl;
        environmentVariables[AgentCallbackEnvironmentContract.CallbackTokenEnvVar] = token;
    }

    private string ResolveCallbackBaseUrl()
    {
        if (string.IsNullOrWhiteSpace(_callbackOptions.BaseUrl))
        {
            throw new SpringException(
                "CallbackBaseUrl:BaseUrl is required to inject SPRING_CALLBACK_URL for agent runtimes.");
        }

        if (!Uri.TryCreate(_callbackOptions.BaseUrl, UriKind.Absolute, out var baseUri) ||
            (!string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new SpringException(
                $"CallbackBaseUrl:BaseUrl '{_callbackOptions.BaseUrl}' is not a valid absolute http(s) URI.");
        }

        var normalizedBase = baseUri.AbsoluteUri.EndsWith('/')
            ? baseUri
            : new Uri(baseUri.AbsoluteUri + "/");

        return normalizedBase.ToString();
    }

    private static Address ResolveAgentAddress(AgentLaunchContext context)
    {
        if (context.AgentAddress is not null)
        {
            return context.AgentAddress;
        }

        if (!GuidFormatter.TryParse(context.AgentId, out var agentId))
        {
            throw new SpringException(
                $"AgentLaunchContext.AgentId '{context.AgentId}' is not a Guid and no AgentAddress was supplied.");
        }

        return new Address(Address.AgentScheme, agentId);
    }

    private static Guid ResolveThreadId(AgentLaunchContext context)
    {
        if (context.CallbackThreadId is { } callbackThreadId)
        {
            return callbackThreadId;
        }

        if (!GuidFormatter.TryParse(context.ThreadId, out var threadId))
        {
            throw new SpringException(
                $"AgentLaunchContext.ThreadId '{context.ThreadId}' is not a Guid and no CallbackThreadId was supplied.");
        }

        return threadId;
    }

    private static Guid ResolveMessageId(AgentLaunchContext context)
    {
        if (context.MessageId is { } messageId)
        {
            return messageId;
        }

        throw new SpringException(
            "AgentLaunchContext.MessageId is required to inject SPRING_CALLBACK_TOKEN.");
    }
}
