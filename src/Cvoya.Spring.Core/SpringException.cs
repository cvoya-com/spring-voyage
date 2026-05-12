// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core;

/// <summary>
/// Base exception class for all Spring Voyage platform exceptions.
/// </summary>
public class SpringException : Exception
{
    /// <summary>
    /// #2189: <see cref="System.Exception.Data"/> key producers use to
    /// stamp the stable issue code (matches the
    /// <c>ProblemDetails.code</c> / <c>UnitValidationCodes</c> namespace
    /// — e.g. <c>"CredentialFormatRejected"</c>,
    /// <c>"CredentialMissing"</c>, <c>"ImagePullFailed"</c>) on a
    /// thrown <see cref="SpringException"/>. The
    /// <see cref="Cvoya.Spring.Dapr.Actors.AgentActor"/>'s
    /// runtime-issue catch reads this key first and falls back to the
    /// "<c>Code:</c>"-prefix heuristic only when absent.
    /// </summary>
    public const string IssueCodeDataKey = "Cvoya.Spring.IssueCode";

    /// <summary>
    /// #2189: <see cref="System.Exception.Data"/> key producers use to
    /// stamp the issue source bucket (e.g. <c>"credential"</c>,
    /// <c>"runtime"</c>, <c>"configuration"</c>). The actor catch
    /// reads this key first; the heuristic fallback always tags
    /// <c>"runtime"</c>, so producers that want a different bucket
    /// (credential rejection, configuration drift) MUST stamp this key.
    /// </summary>
    public const string IssueSourceDataKey = "Cvoya.Spring.IssueSource";

    /// <summary>
    /// Initializes a new instance of the <see cref="SpringException"/> class.
    /// </summary>
    public SpringException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SpringException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The error message.</param>
    public SpringException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SpringException"/> class with a specified error message
    /// and a reference to the inner exception.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public SpringException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>
    /// #2189 helper: stamps <see cref="IssueCodeDataKey"/> +
    /// <see cref="IssueSourceDataKey"/> on this exception's <see
    /// cref="System.Exception.Data"/> dictionary and returns the
    /// instance so producers can call <c>throw ex.WithIssue(...)</c>
    /// or <c>throw new SpringException(...).WithIssue(...)</c> in one
    /// statement.
    /// </summary>
    /// <param name="code">Stable code matching the translator catalogue.</param>
    /// <param name="source">Producer bucket — <c>"credential"</c>, <c>"runtime"</c>, <c>"configuration"</c>, …</param>
    public SpringException WithIssue(string code, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        Data[IssueCodeDataKey] = code;
        Data[IssueSourceDataKey] = source;
        return this;
    }
}
