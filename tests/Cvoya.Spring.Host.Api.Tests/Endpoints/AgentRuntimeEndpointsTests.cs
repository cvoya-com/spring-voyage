// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Host.Api.Models;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for <c>/api/v1/agent-runtimes</c> — install,
/// uninstall, list, get, config patch, and model enumeration. The test
/// host registers every OSS runtime (Claude, OpenAI, Google, Ollama)
/// via <c>Program.cs</c> so these tests hit real runtime descriptors;
/// the tenant install store is fresh per test via the in-memory
/// <c>SpringDbContext</c>.
/// </summary>
public class AgentRuntimeEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public AgentRuntimeEndpointsTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task List_Returns200WithParseableArray()
    {
        // Smoke: every test in this file shares the factory's in-memory
        // DB via IClassFixture, so prior installs may be present here.
        // Assert the envelope, not the contents — later tests cover the
        // install-then-list round-trip against a known slug.
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.GetAsync("/api/v1/agent-runtimes", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<InstalledAgentRuntimeResponse[]>(ct);
        body.ShouldNotBeNull();
    }

    [Fact]
    public async Task Install_UnknownRuntime_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.PostAsJsonAsync(
            "/api/v1/agent-runtimes/not-a-real-runtime/install",
            new AgentRuntimeInstallRequest(null, null, null),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Install_Claude_SurfacesInList()
    {
        var ct = TestContext.Current.CancellationToken;
        var install = await _client.PostAsJsonAsync(
            "/api/v1/agent-runtimes/claude/install",
            new AgentRuntimeInstallRequest(null, null, null),
            ct);
        install.StatusCode.ShouldBe(HttpStatusCode.OK);

        var listResponse = await _client.GetAsync("/api/v1/agent-runtimes", ct);
        var list = await listResponse.Content.ReadFromJsonAsync<InstalledAgentRuntimeResponse[]>(ct);
        list.ShouldNotBeNull();
        list.ShouldContain(r => r.Id == "claude");
    }

    [Fact]
    public async Task List_Surfaces_CredentialSecretName_From_Runtime()
    {
        // #742: the CLI wizard reads `credentialSecretName` off the
        // agent-runtime payload instead of hardcoding the provider →
        // secret-name map, so the field must round-trip verbatim from
        // each `IAgentRuntime.CredentialSecretName`.
        var ct = TestContext.Current.CancellationToken;
        await _client.PostAsJsonAsync(
            "/api/v1/agent-runtimes/claude/install",
            new AgentRuntimeInstallRequest(null, null, null),
            ct);
        await _client.PostAsJsonAsync(
            "/api/v1/agent-runtimes/ollama/install",
            new AgentRuntimeInstallRequest(null, null, null),
            ct);

        var listResponse = await _client.GetAsync("/api/v1/agent-runtimes", ct);
        var list = await listResponse.Content.ReadFromJsonAsync<InstalledAgentRuntimeResponse[]>(ct);
        list.ShouldNotBeNull();

        var claude = list!.Single(r => r.Id == "claude");
        claude.CredentialSecretName.ShouldBe("anthropic-api-key");

        // Ollama declares no credential — the contract is an empty
        // string (which downstream consumers, including the CLI, treat
        // as "no credential to write").
        var ollama = list!.Single(r => r.Id == "ollama");
        ollama.CredentialSecretName.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task GetModels_AfterInstallWithDefaults_ReturnsSeedCatalog()
    {
        var ct = TestContext.Current.CancellationToken;
        await _client.PostAsJsonAsync(
            "/api/v1/agent-runtimes/claude/install",
            new AgentRuntimeInstallRequest(null, null, null),
            ct);

        var response = await _client.GetAsync("/api/v1/agent-runtimes/claude/models", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var models = await response.Content.ReadFromJsonAsync<AgentRuntimeModelResponse[]>(ct);
        models.ShouldNotBeNull();
        models.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Get_Uninstalled_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        // Pre-clean because tests share the factory's in-memory DB;
        // another test may have installed the runtime we're probing.
        await _client.DeleteAsync("/api/v1/agent-runtimes/ollama", ct);
        var response = await _client.GetAsync("/api/v1/agent-runtimes/ollama", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Uninstall_RemovesFromList()
    {
        var ct = TestContext.Current.CancellationToken;
        await _client.PostAsJsonAsync(
            "/api/v1/agent-runtimes/openai/install",
            new AgentRuntimeInstallRequest(null, null, null),
            ct);

        var uninstall = await _client.DeleteAsync("/api/v1/agent-runtimes/openai", ct);
        uninstall.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var getResponse = await _client.GetAsync("/api/v1/agent-runtimes/openai", ct);
        getResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateConfig_PatchesStoredConfig()
    {
        var ct = TestContext.Current.CancellationToken;
        await _client.PostAsJsonAsync(
            "/api/v1/agent-runtimes/google/install",
            new AgentRuntimeInstallRequest(null, null, null),
            ct);

        var newConfig = new
        {
            Models = new[] { "gemini-2.0-flash" },
            DefaultModel = "gemini-2.0-flash",
            BaseUrl = (string?)null,
        };
        var patch = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/agent-runtimes/google/config")
        {
            Content = JsonContent.Create(newConfig),
        };
        var patchResponse = await _client.SendAsync(patch, ct);
        patchResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var getResponse = await _client.GetAsync("/api/v1/agent-runtimes/google", ct);
        var body = await getResponse.Content.ReadFromJsonAsync<InstalledAgentRuntimeResponse>(ct);
        body.ShouldNotBeNull();
        body!.DefaultModel.ShouldBe("gemini-2.0-flash");
        body.Models.ShouldBe(new[] { "gemini-2.0-flash" });
    }
}