// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

/// <summary>
/// Thrown by actor dispatch when the inbound message fails a
/// caller-side validation rule — the shape, type, or required fields
/// on the payload are inconsistent with what the actor will accept.
///
/// <para>
/// Operationally distinct from a generic <see cref="SpringException"/>:
/// the router inspects the exception type and wraps it in
/// <c>RoutingError.CallerValidation</c>, which API endpoints translate
/// into a <c>400 Bad Request</c> ProblemDetails. Generic exceptions
/// continue to flow through <c>RoutingError.DeliveryFailed</c> → 502,
/// so "the caller sent bad data" and "downstream blew up" are no
/// longer conflated.
/// </para>
///
/// <para>
/// Every throw site should supply a stable, machine-readable
/// <see cref="Code"/> (e.g. <c>"MISSING_CONVERSATION_ID"</c>) so
/// clients can switch on it without string-matching. The exception
/// <see cref="Exception.Message"/> encodes the code as
/// <c>[caller-validation:CODE] detail</c> so the classification
/// survives Dapr actor-remoting, which discards custom exception
/// properties and only preserves the message text. Use
/// <see cref="TryParseMessage"/> to recover the code on the caller
/// side without string-matching the detail.
/// </para>
/// </summary>
public class CallerValidationException : SpringException
{
    /// <summary>
    /// Prefix used to embed the <see cref="Code"/> in
    /// <see cref="Exception.Message"/> so the classification survives
    /// Dapr actor-remoting (which only preserves the message string).
    /// </summary>
    private const string MessagePrefix = "[caller-validation:";

    /// <summary>
    /// A stable, machine-readable error code clients can switch on.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Human-readable description (without the encoding prefix).
    /// Surfaces as the ProblemDetails <c>detail</c> at the API boundary.
    /// </summary>
    public string Detail { get; }

    /// <summary>
    /// Initializes a new instance with the specified code and
    /// human-readable detail. The exception message is encoded as
    /// <c>[caller-validation:CODE] detail</c> so the classification
    /// survives actor-remoting.
    /// </summary>
    /// <param name="code">Stable machine-readable error code.</param>
    /// <param name="detail">Human-readable description.</param>
    public CallerValidationException(string code, string detail)
        : base(EncodeMessage(code, detail))
    {
        Code = code;
        Detail = detail;
    }

    /// <summary>
    /// Initializes a new instance with the specified code, detail,
    /// and inner exception.
    /// </summary>
    public CallerValidationException(string code, string detail, Exception innerException)
        : base(EncodeMessage(code, detail), innerException)
    {
        Code = code;
        Detail = detail;
    }

    /// <summary>
    /// Attempts to parse a caller-validation message produced by this
    /// type back into its <c>(code, detail)</c> components. Returns
    /// <c>false</c> when <paramref name="message"/> does not carry the
    /// encoding prefix — in which case the caller should treat the
    /// underlying failure as a generic infrastructure error, not a
    /// caller-validation one.
    /// </summary>
    /// <param name="message">The <see cref="Exception.Message"/> to parse.</param>
    /// <param name="code">The recovered code.</param>
    /// <param name="detail">The recovered detail (without the prefix).</param>
    public static bool TryParseMessage(string? message, out string code, out string detail)
    {
        code = string.Empty;
        detail = string.Empty;

        if (string.IsNullOrEmpty(message) || !message.StartsWith(MessagePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var end = message.IndexOf(']', MessagePrefix.Length);
        if (end < 0)
        {
            return false;
        }

        code = message.Substring(MessagePrefix.Length, end - MessagePrefix.Length);
        // Skip the closing `]` and one optional separator space.
        var detailStart = end + 1;
        if (detailStart < message.Length && message[detailStart] == ' ')
        {
            detailStart++;
        }
        detail = message.Substring(detailStart);
        return true;
    }

    private static string EncodeMessage(string code, string detail) =>
        $"{MessagePrefix}{code}] {detail}";
}
