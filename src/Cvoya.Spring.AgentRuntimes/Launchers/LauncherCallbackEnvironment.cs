// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Launchers;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;

internal static class LauncherCallbackEnvironment
{
    public static void Add(
        IAgentCallbackEnvironmentBuilder? builder,
        AgentLaunchContext context,
        IDictionary<string, string> envVars)
    {
        if (builder is null)
        {
            throw new SpringException(
                "IAgentCallbackEnvironmentBuilder is not registered; launchers cannot inject " +
                $"{AgentCallbackEnvironmentContract.CallbackUrlEnvVar} and " +
                $"{AgentCallbackEnvironmentContract.CallbackTokenEnvVar}.");
        }

        builder.AddCallbackEnvironment(context, envVars);
    }

    public static string BuildOrchestrationMcpUrl(IReadOnlyDictionary<string, string> envVars)
    {
        var callbackBaseUrl = envVars[AgentCallbackEnvironmentContract.CallbackUrlEnvVar];

        if (!Uri.TryCreate(callbackBaseUrl, UriKind.Absolute, out var baseUri) ||
            (!string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new SpringException(
                $"{AgentCallbackEnvironmentContract.CallbackUrlEnvVar} must be an absolute http(s) URL.");
        }

        var normalizedBase = baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? baseUri
            : new Uri(baseUri.AbsoluteUri + "/");
        var relativePrefix = AgentCallbackEnvironmentContract.OrchestrationRoutePrefix.TrimStart('/');

        return new Uri(normalizedBase, relativePrefix).ToString().TrimEnd('/');
    }
}