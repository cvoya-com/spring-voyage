// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Host.Api.Models;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for <c>/api/v1/tenant/model-providers/installs</c>
/// — install, uninstall, list, get, config patch, and model
/// enumeration. Per ADR-0038 the install table re-keys on provider id
/// (<c>anthropic</c>, <c>openai</c>, <c>google</c>, <c>ollama</c>) and
/// the wizard / CLI / refresh path consume the model-provider surface.
/// </summary>
/// <remarks>
/// File name retained as <c>AgentRuntimeEndpointsTests</c> to keep the
/// git diff focused on content; Chunk B / a follow-up test rename can
/// align the file name with the new endpoint surface.
/// </remarks>
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
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.GetAsync("/api/v1/tenant/model-providers/installs", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<InstalledModelProviderResponse[]>(ct);
        body.ShouldNotBeNull();
    }

    [Fact]
    public async Task Install_UnknownProvider_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.PostAsJsonAsync(
            "/api/v1/tenant/model-providers/installs/not-a-real-provider/install",
            new ModelProviderInstallRequest(null, null, null),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Install_Anthropic_SurfacesInList()
    {
        var ct = TestContext.Current.CancellationToken;
        var install = await _client.PostAsJsonAsync(
            "/api/v1/tenant/model-providers/installs/anthropic/install",
            new ModelProviderInstallRequest(null, null, null),
            ct);
        install.StatusCode.ShouldBe(HttpStatusCode.OK);

        var listResponse = await _client.GetAsync("/api/v1/tenant/model-providers/installs", ct);
        var list = await listResponse.Content.ReadFromJsonAsync<InstalledModelProviderResponse[]>(ct);
        list.ShouldNotBeNull();
        list.ShouldContain(r => r.Id == "anthropic");
    }

    [Fact]
    public async Task List_Surfaces_CredentialSecretName_From_Catalogue()
    {
        // ADR-0038 § "Credential identity": secret name is
        // {provider}-{authMethod-slug}. Anthropic's first declared auth
        // method in runtime-catalog.yaml is OAuth, so the surfaced name
        // is "anthropic-oauth".
        var ct = TestContext.Current.CancellationToken;
        await _client.PostAsJsonAsync(
            "/api/v1/tenant/model-providers/installs/anthropic/install",
            new ModelProviderInstallRequest(null, null, null),
            ct);

        var listResponse = await _client.GetAsync("/api/v1/tenant/model-providers/installs", ct);
        var list = await listResponse.Content.ReadFromJsonAsync<InstalledModelProviderResponse[]>(ct);
        list.ShouldNotBeNull();

        var anthropic = list!.Single(r => r.Id == "anthropic");
        anthropic.CredentialSecretName.ShouldBe("anthropic-oauth");
    }

    [Fact]
    public async Task GetModels_AfterInstallWithDefaults_ReturnsSeedCatalog()
    {
        var ct = TestContext.Current.CancellationToken;
        await _client.PostAsJsonAsync(
            "/api/v1/tenant/model-providers/installs/anthropic/install",
            new ModelProviderInstallRequest(null, null, null),
            ct);

        var response = await _client.GetAsync(
            "/api/v1/tenant/model-providers/installs/anthropic/models", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var models = await response.Content.ReadFromJsonAsync<ModelProviderModelResponse[]>(ct);
        models.ShouldNotBeNull();
        models.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Get_Uninstalled_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        await _client.DeleteAsync("/api/v1/tenant/model-providers/installs/google", ct);
        var response = await _client.GetAsync(
            "/api/v1/tenant/model-providers/installs/google", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Uninstall_RemovesFromList()
    {
        var ct = TestContext.Current.CancellationToken;
        await _client.PostAsJsonAsync(
            "/api/v1/tenant/model-providers/installs/openai/install",
            new ModelProviderInstallRequest(null, null, null),
            ct);

        var uninstall = await _client.DeleteAsync(
            "/api/v1/tenant/model-providers/installs/openai", ct);
        uninstall.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var getResponse = await _client.GetAsync(
            "/api/v1/tenant/model-providers/installs/openai", ct);
        getResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetConfig_UnknownProvider_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.GetAsync(
            "/api/v1/tenant/model-providers/installs/not-a-real-provider/config", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetConfig_ProviderNotInstalled_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        await _client.DeleteAsync("/api/v1/tenant/model-providers/installs/google", ct);
        var response = await _client.GetAsync(
            "/api/v1/tenant/model-providers/installs/google/config", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetConfig_AfterInstall_ReturnsStoredConfigSlot()
    {
        var ct = TestContext.Current.CancellationToken;
        var seedModels = new[] { "claude-opus-4-7", "claude-sonnet-4-6" };
        await _client.PostAsJsonAsync(
            "/api/v1/tenant/model-providers/installs/anthropic/install",
            new ModelProviderInstallRequest(seedModels, "claude-opus-4-7", null),
            ct);

        var response = await _client.GetAsync(
            "/api/v1/tenant/model-providers/installs/anthropic/config", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ModelProviderConfigResponse>(ct);
        body.ShouldNotBeNull();
        body!.Id.ShouldBe("anthropic");
        body.DefaultModel.ShouldBe("claude-opus-4-7");
        body.BaseUrl.ShouldBeNull();
        body.Models.ShouldBe(seedModels);
    }

    [Fact]
    public async Task UpdateConfig_PatchesStoredConfig()
    {
        var ct = TestContext.Current.CancellationToken;
        await _client.PostAsJsonAsync(
            "/api/v1/tenant/model-providers/installs/google/install",
            new ModelProviderInstallRequest(null, null, null),
            ct);

        var newConfig = new
        {
            Models = new[] { "gemini-2.0-flash" },
            DefaultModel = "gemini-2.0-flash",
            BaseUrl = (string?)null,
        };
        var patch = new HttpRequestMessage(HttpMethod.Patch,
            "/api/v1/tenant/model-providers/installs/google/config")
        {
            Content = JsonContent.Create(newConfig),
        };
        var patchResponse = await _client.SendAsync(patch, ct);
        patchResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var getResponse = await _client.GetAsync(
            "/api/v1/tenant/model-providers/installs/google", ct);
        var body = await getResponse.Content.ReadFromJsonAsync<InstalledModelProviderResponse>(ct);
        body.ShouldNotBeNull();
        body!.DefaultModel.ShouldBe("gemini-2.0-flash");
        body.Models.ShouldBe(new[] { "gemini-2.0-flash" });
    }
}
