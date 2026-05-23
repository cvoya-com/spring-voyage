// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using Cvoya.Spring.Dapr.Execution;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="InMemoryAgentBootstrapAuthStore"/> — the per-agent
/// bootstrap-token authority introduced by ADR-0055 §8.
/// </summary>
public class InMemoryAgentBootstrapAuthStoreTests
{
    [Fact]
    public void Issue_Returns64HexCharToken()
    {
        var store = new InMemoryAgentBootstrapAuthStore();

        var token = store.Issue("agent-1");

        token.ShouldNotBeNullOrEmpty();
        token.Length.ShouldBe(64);
        token.ShouldMatch("^[0-9a-f]+$");
    }

    [Fact]
    public void Issue_Idempotent_ReturnsSameTokenAcrossCalls()
    {
        var store = new InMemoryAgentBootstrapAuthStore();

        var first = store.Issue("agent-1");
        var second = store.Issue("agent-1");

        second.ShouldBe(first);
    }

    [Fact]
    public void Issue_DifferentAgents_GetDifferentTokens()
    {
        var store = new InMemoryAgentBootstrapAuthStore();

        var a = store.Issue("agent-a");
        var b = store.Issue("agent-b");

        a.ShouldNotBe(b);
    }

    [Fact]
    public void Validate_AcceptsLiveToken()
    {
        var store = new InMemoryAgentBootstrapAuthStore();
        var token = store.Issue("agent-1");

        store.Validate("agent-1", token).ShouldBeTrue();
    }

    [Fact]
    public void Validate_RejectsTokenForDifferentAgent()
    {
        // Tenant isolation per ADR-0055 §8: a token is bound to (agentId, token).
        // Presenting a valid token for the wrong agent must return false.
        var store = new InMemoryAgentBootstrapAuthStore();
        var aToken = store.Issue("agent-a");
        store.Issue("agent-b");

        store.Validate("agent-b", aToken).ShouldBeFalse();
    }

    [Fact]
    public void Validate_RejectsWrongToken()
    {
        var store = new InMemoryAgentBootstrapAuthStore();
        store.Issue("agent-1");

        store.Validate("agent-1", "not-the-real-token").ShouldBeFalse();
    }

    [Fact]
    public void Validate_RejectsUnknownAgent()
    {
        var store = new InMemoryAgentBootstrapAuthStore();

        store.Validate("never-issued", "any-token").ShouldBeFalse();
    }

    [Fact]
    public void Validate_RejectsEmptyToken()
    {
        var store = new InMemoryAgentBootstrapAuthStore();
        store.Issue("agent-1");

        store.Validate("agent-1", string.Empty).ShouldBeFalse();
    }

    [Fact]
    public void Revoke_InvalidatesPriorToken()
    {
        var store = new InMemoryAgentBootstrapAuthStore();
        var token = store.Issue("agent-1");

        store.Revoke("agent-1");

        store.Validate("agent-1", token).ShouldBeFalse();
    }

    [Fact]
    public void Revoke_FollowedByIssue_MintsFreshToken()
    {
        var store = new InMemoryAgentBootstrapAuthStore();
        var first = store.Issue("agent-1");

        store.Revoke("agent-1");
        var second = store.Issue("agent-1");

        second.ShouldNotBe(first);
        store.Validate("agent-1", second).ShouldBeTrue();
    }

    [Fact]
    public void Revoke_Idempotent()
    {
        var store = new InMemoryAgentBootstrapAuthStore();

        // Two no-op revokes followed by a third for a real agent — none
        // throw and the final state is consistent.
        Should.NotThrow(() => store.Revoke("never-issued"));
        Should.NotThrow(() => store.Revoke("never-issued"));

        store.Issue("agent-1");
        Should.NotThrow(() => store.Revoke("agent-1"));
        Should.NotThrow(() => store.Revoke("agent-1"));
    }
}
