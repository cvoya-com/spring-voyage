// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Host.Api.Models;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for the system_prompt_mode plumbing on the Web API
/// (#2692 / #2691 / #2667). Covers PUT/GET round-trip on agent and unit
/// execution endpoints, the PATCH-agent tri-state, the resolved + declared
/// pair surfaced on <see cref="AgentResponse"/>, and the wire-literal
/// validation on every entry point.
/// </summary>
public class SystemPromptModeEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SystemPromptModeEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();

        // Fixture is shared across tests; reset substitutes so each test
        // starts from a clean slate. Mirrors AgentExecutionInheritanceTests.
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.AgentExecutionStore.ClearReceivedCalls();
        _factory.UnitExecutionStore.ClearReceivedCalls();

        _factory.UnitExecutionStore
            .GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UnitExecutionDefaults?>(null));
        _factory.AgentExecutionStore
            .GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentExecutionShape?>(null));
    }

    // ── PUT /agents/{id}/execution → GET round-trip ────────────────────────

    [Fact]
    public async Task PutAgentExecution_RoundTripsSystemPromptMode()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentGuid = Guid.NewGuid();

        ArrangeAgentDirectoryEntry(agentGuid);

        // Pre-arrange the post-write read so the response shape is
        // deterministic (the store substitute does not actually persist).
        _factory.AgentExecutionStore
            .GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentExecutionShape?>(
                new AgentExecutionShape(
                    Runtime: "claude-code",
                    SystemPromptMode: "replace")));

        var response = await _client.PutAsJsonAsync(
            $"/api/v1/tenant/agents/{agentGuid:N}/execution",
            new AgentExecutionResponse(
                Runtime: "claude-code",
                SystemPromptMode: "replace"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<AgentExecutionResponse>(cancellationToken: ct);
        payload.ShouldNotBeNull();
        payload!.SystemPromptMode.ShouldBe("replace");

        // Verify the store SetAsync received the canonical lower-case
        // literal (the validator normalises before the store call).
        await _factory.AgentExecutionStore.Received(1)
            .SetAsync(
                Arg.Any<string>(),
                Arg.Is<AgentExecutionShape>(s => s.SystemPromptMode == "replace"),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PutAgentExecution_UnknownSystemPromptMode_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentGuid = Guid.NewGuid();
        ArrangeAgentDirectoryEntry(agentGuid);

        var response = await _client.PutAsJsonAsync(
            $"/api/v1/tenant/agents/{agentGuid:N}/execution",
            new AgentExecutionResponse(
                Runtime: "claude-code",
                SystemPromptMode: "invalid"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(ct);
        body.ShouldContain("systemPromptMode");
        body.ShouldContain("invalid");

        await _factory.AgentExecutionStore.DidNotReceive()
            .SetAsync(Arg.Any<string>(), Arg.Any<AgentExecutionShape>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PutAgentExecution_CaseInsensitive_NormalisesToLowerCase()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentGuid = Guid.NewGuid();
        ArrangeAgentDirectoryEntry(agentGuid);

        _factory.AgentExecutionStore
            .GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentExecutionShape?>(
                new AgentExecutionShape(
                    Runtime: "claude-code",
                    SystemPromptMode: "append")));

        var response = await _client.PutAsJsonAsync(
            $"/api/v1/tenant/agents/{agentGuid:N}/execution",
            new AgentExecutionResponse(
                Runtime: "claude-code",
                SystemPromptMode: "APPEND"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.AgentExecutionStore.Received(1)
            .SetAsync(
                Arg.Any<string>(),
                Arg.Is<AgentExecutionShape>(s => s.SystemPromptMode == "append"),
                Arg.Any<CancellationToken>());
    }

    // ── PUT /units/{id}/execution → GET round-trip ─────────────────────────

    [Fact]
    public async Task PutUnitExecution_RoundTripsSystemPromptMode()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitGuid = Guid.NewGuid();
        ArrangeUnitDirectoryEntry(unitGuid);

        _factory.UnitExecutionStore
            .GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UnitExecutionDefaults?>(
                new UnitExecutionDefaults(
                    Runtime: "claude-code",
                    SystemPromptMode: SystemPromptMode.Replace)));

        var response = await _client.PutAsJsonAsync(
            $"/api/v1/tenant/units/{unitGuid:N}/execution",
            new UnitExecutionResponse(
                Runtime: "claude-code",
                SystemPromptMode: "replace"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<UnitExecutionResponse>(cancellationToken: ct);
        payload.ShouldNotBeNull();
        payload!.SystemPromptMode.ShouldBe("replace");

        await _factory.UnitExecutionStore.Received(1)
            .SetAsync(
                Arg.Any<string>(),
                Arg.Is<UnitExecutionDefaults>(d => d.SystemPromptMode == SystemPromptMode.Replace),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PutUnitExecution_OnlySystemPromptMode_AcceptedNot400()
    {
        // The "must carry at least one non-empty field" gate must treat
        // systemPromptMode as a first-class slot, mirroring runtime.
        var ct = TestContext.Current.CancellationToken;
        var unitGuid = Guid.NewGuid();
        ArrangeUnitDirectoryEntry(unitGuid);

        _factory.UnitExecutionStore
            .GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UnitExecutionDefaults?>(
                new UnitExecutionDefaults(SystemPromptMode: SystemPromptMode.Append)));

        var response = await _client.PutAsJsonAsync(
            $"/api/v1/tenant/units/{unitGuid:N}/execution",
            new UnitExecutionResponse(SystemPromptMode: "append"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.UnitExecutionStore.Received(1)
            .SetAsync(
                Arg.Any<string>(),
                Arg.Is<UnitExecutionDefaults>(d => d.SystemPromptMode == SystemPromptMode.Append),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PutUnitExecution_UnknownSystemPromptMode_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitGuid = Guid.NewGuid();
        ArrangeUnitDirectoryEntry(unitGuid);

        var response = await _client.PutAsJsonAsync(
            $"/api/v1/tenant/units/{unitGuid:N}/execution",
            new UnitExecutionResponse(
                Runtime: "claude-code",
                SystemPromptMode: "extend"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(ct);
        body.ShouldContain("systemPromptMode");

        await _factory.UnitExecutionStore.DidNotReceive()
            .SetAsync(Arg.Any<string>(), Arg.Any<UnitExecutionDefaults>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetUnitExecution_SurfacesSystemPromptMode()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitGuid = Guid.NewGuid();
        ArrangeUnitDirectoryEntry(unitGuid);

        _factory.UnitExecutionStore
            .GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UnitExecutionDefaults?>(
                new UnitExecutionDefaults(
                    Runtime: "claude-code",
                    SystemPromptMode: SystemPromptMode.Replace)));

        var response = await _client.GetAsync($"/api/v1/tenant/units/{unitGuid:N}/execution/", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<UnitExecutionResponse>(cancellationToken: ct);
        payload.ShouldNotBeNull();
        payload!.SystemPromptMode.ShouldBe("replace");
    }

    // ── PATCH /agents/{id} tri-state ───────────────────────────────────────

    [Fact]
    public async Task PatchAgent_UnknownSystemPromptMode_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentGuid = Guid.NewGuid();
        ArrangeAgentDirectoryEntry(agentGuid);

        var body = """
            {"systemPromptMode": "garbage"}
            """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _client.PatchAsync(
            $"/api/v1/tenant/agents/{agentGuid:N}", content, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        responseBody.ShouldContain("systemPromptMode");
        responseBody.ShouldContain("garbage");
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private void ArrangeAgentDirectoryEntry(Guid agentGuid)
    {
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == Address.AgentScheme && a.Id == agentGuid),
                Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address(Address.AgentScheme, agentGuid),
                agentGuid,
                "Test Agent",
                "Test agent",
                null,
                DateTimeOffset.UtcNow));
    }

    private void ArrangeUnitDirectoryEntry(Guid unitGuid)
    {
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == Address.UnitScheme && a.Id == unitGuid),
                Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address(Address.UnitScheme, unitGuid),
                unitGuid,
                "Test Unit",
                "Test unit",
                null,
                DateTimeOffset.UtcNow));
    }
}
