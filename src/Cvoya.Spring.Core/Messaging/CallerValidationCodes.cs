// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

/// <summary>
/// Stable, machine-readable sub-codes used by
/// <see cref="CallerValidationException"/> and surfaced on the RFC-7807
/// <c>code</c> extension field by the API boundary. New values appended
/// here are additive; clients are expected to treat unknown codes as a
/// generic caller-validation failure.
/// </summary>
public static class CallerValidationCodes
{
    /// <summary>
    /// The inbound <see cref="MessageType"/> is not one of the types the
    /// destination actor dispatches. Either the caller sent an unknown
    /// value, or it sent a value this actor kind does not accept (e.g.
    /// <c>Cancel</c> to a <c>human://</c>).
    /// </summary>
    public const string UnknownMessageType = "UNKNOWN_MESSAGE_TYPE";

    /// <summary>
    /// A Domain message was sent to an actor that requires a
    /// <see cref="Message.ThreadId"/> and the caller omitted it.
    /// Note the API endpoint auto-generates a thread id for
    /// <c>agent://</c> targets (#985), so this is primarily reached
    /// through the thread-threaded routes and direct actor calls.
    /// </summary>
    public const string MissingThreadId = "MISSING_THREAD_ID";
}