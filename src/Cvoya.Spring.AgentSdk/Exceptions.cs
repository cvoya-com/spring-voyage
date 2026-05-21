// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk;

public sealed class MessagingAuthException : Exception
{
    public MessagingAuthException(string message, string? reason = null)
        : base(message)
    {
        Reason = reason;
    }

    public MessagingAuthException(
        string message,
        Exception innerException,
        string? reason = null)
        : base(message, innerException)
    {
        Reason = reason;
    }

    public string? Reason { get; }
}

public sealed class MessagingTransportException : Exception
{
    public MessagingTransportException(string message)
        : base(message)
    {
    }

    public MessagingTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Wrapper raised by <see cref="SpringAgent.RunWithResponseDisciplineAsync"/>
/// when the user's delegate threw — the inner exception carries the
/// original cause. The safety-net reply has already been posted by the
/// time this is thrown. Issue #2493.
/// </summary>
public sealed class SpringAgentHandlerException : Exception
{
    public SpringAgentHandlerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class MissingCallbackEnvironmentException : Exception
{
    public MissingCallbackEnvironmentException(string variableName)
        : base(CreateMessage(variableName))
    {
        VariableName = variableName;
    }

    public string VariableName { get; }

    private static string CreateMessage(string variableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variableName);

        return $"Required Spring Voyage callback environment variable '{variableName}' is missing.";
    }
}
