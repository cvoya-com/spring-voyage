// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Runtime;

/// <summary>
/// JWT claim names used by Spring Voyage per-invocation callback tokens.
/// </summary>
public static class CallbackTokenClaimNames
{
    /// <summary>Claim name carrying the tenant id (canonical no-dash hex).</summary>
    public const string TenantId = "sv_tid";

    /// <summary>Claim name carrying the canonical agent / unit address.</summary>
    public const string AgentAddress = "sv_addr";

    /// <summary>Claim name carrying the thread id (canonical no-dash hex).</summary>
    public const string ThreadId = "sv_thread";

    /// <summary>Claim name carrying the inbound message id (canonical no-dash hex).</summary>
    public const string MessageId = "sv_msg";
}
