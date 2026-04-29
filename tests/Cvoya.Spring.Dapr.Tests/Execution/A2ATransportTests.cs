// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for the D2 / Stage 2 A2A transport seam:
/// <see cref="DispatcherProxyA2ATransportFactory"/>,
/// <see cref="DispatcherProxyA2ATransport"/>, and
/// <see cref="DirectA2ATransport"/>.
/// </summary>
public class A2ATransportTests
{
    private static readonly Uri AgentEndpoint = new("http://localhost:8999/");
    private const string ContainerId = "test-container-42";

    // ---------------------------------------------------------------------------
    // DispatcherProxyA2ATransportFactory
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Factory_WithContainerId_ReturnsDispatcherProxyTransport()
    {
        // The factory must select the proxy transport whenever a container id
        // is supplied — that is the standard OSS topology path.
        var containerRuntime = Substitute.For<IContainerRuntime>();
        var factory = new DispatcherProxyA2ATransportFactory(containerRuntime);

        using var transport = factory.CreateTransport(ContainerId);

        // Verify the returned transport routes through the proxy by creating a
        // client, sending a POST, and asserting that SendHttpJsonAsync fired.
        containerRuntime.SendHttpJsonAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerHttpResponse(200, "{}"u8.ToArray()));

        using var client = transport.CreateHttpClient(AgentEndpoint);
        await client.PostAsync("/", new StringContent("{}", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        await containerRuntime.Received(1).SendHttpJsonAsync(
            ContainerId,
            Arg.Any<string>(),
            Arg.Any<byte[]>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Factory_WithNullContainerId_ReturnsDirectTransport()
    {
        // When no container id is available (test harness or future dual-homed
        // topology) the factory must fall back to the direct-HTTP transport so
        // the caller's request goes through a plain HttpClient, not the proxy.
        var containerRuntime = Substitute.For<IContainerRuntime>();
        var factory = new DispatcherProxyA2ATransportFactory(containerRuntime);

        using var transport = factory.CreateTransport(containerId: null);

        using var client = transport.CreateHttpClient(AgentEndpoint);

        // A DirectA2ATransport's client has no custom handler; its BaseAddress
        // must be the supplied endpoint.
        client.BaseAddress.ShouldBe(AgentEndpoint);

        // SendHttpJsonAsync must NOT have been invoked — the direct transport
        // does not touch IContainerRuntime.
        containerRuntime.DidNotReceive().SendHttpJsonAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<byte[]>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Factory_WithEmptyContainerId_ReturnsDirectTransport()
    {
        // Whitespace-only container id is treated the same as null — no proxy.
        var containerRuntime = Substitute.For<IContainerRuntime>();
        var factory = new DispatcherProxyA2ATransportFactory(containerRuntime);

        using var transport = factory.CreateTransport(containerId: "  ");

        using var client = transport.CreateHttpClient(AgentEndpoint);
        client.BaseAddress.ShouldBe(AgentEndpoint);

        containerRuntime.DidNotReceive().SendHttpJsonAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<byte[]>(),
            Arg.Any<CancellationToken>());
    }

    // ---------------------------------------------------------------------------
    // DispatcherProxyA2ATransport
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task DispatcherProxyTransport_CreateHttpClient_RoutesPostThroughContainerRuntime()
    {
        // The transport must produce a client whose POST calls flow through
        // IContainerRuntime.SendHttpJsonAsync for the named container.
        var containerRuntime = Substitute.For<IContainerRuntime>();
        containerRuntime.SendHttpJsonAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerHttpResponse(200, """{"ok":true}"""u8.ToArray()));

        using var transport = new DispatcherProxyA2ATransport(containerRuntime, ContainerId);
        using var client = transport.CreateHttpClient(AgentEndpoint);

        var body = new StringContent("{\"jsonrpc\":\"2.0\"}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/", body, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await containerRuntime.Received(1).SendHttpJsonAsync(
            ContainerId,
            Arg.Any<string>(),
            Arg.Any<byte[]>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void DispatcherProxyTransport_CreateHttpClient_SetsBaseAddress()
    {
        var containerRuntime = Substitute.For<IContainerRuntime>();
        using var transport = new DispatcherProxyA2ATransport(containerRuntime, ContainerId);
        using var client = transport.CreateHttpClient(AgentEndpoint);

        client.BaseAddress.ShouldBe(AgentEndpoint);
    }

    [Fact]
    public void DispatcherProxyTransport_Constructor_BlankContainerId_Throws()
    {
        var containerRuntime = Substitute.For<IContainerRuntime>();

        Should.Throw<ArgumentException>(
            () => new DispatcherProxyA2ATransport(containerRuntime, "  "));
    }

    [Fact]
    public void DispatcherProxyTransport_AfterDispose_ThrowsOnCreateHttpClient()
    {
        var containerRuntime = Substitute.For<IContainerRuntime>();
        var transport = new DispatcherProxyA2ATransport(containerRuntime, ContainerId);
        transport.Dispose();

        Should.Throw<ObjectDisposedException>(
            () => transport.CreateHttpClient(AgentEndpoint));
    }

    // ---------------------------------------------------------------------------
    // DirectA2ATransport
    // ---------------------------------------------------------------------------

    [Fact]
    public void DirectTransport_CreateHttpClient_SetsBaseAddress()
    {
        using var transport = new DirectA2ATransport();
        using var client = transport.CreateHttpClient(AgentEndpoint);

        client.BaseAddress.ShouldBe(AgentEndpoint);
    }

    [Fact]
    public void DirectTransport_AfterDispose_ThrowsOnCreateHttpClient()
    {
        var transport = new DirectA2ATransport();
        transport.Dispose();

        Should.Throw<ObjectDisposedException>(
            () => transport.CreateHttpClient(AgentEndpoint));
    }

    // ---------------------------------------------------------------------------
    // End-to-end: both paths satisfy the same A2A client contract
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task BothTransports_A2ACallSurface_IsEquivalent()
    {
        // Both transports must produce an HttpClient the A2AClient SDK can
        // drive without knowing which transport is in use. Verify that:
        //   - BaseAddress is set correctly.
        //   - A POST with a JSON body returns a usable HttpResponseMessage.
        // The proxy transport stubs the runtime; the direct transport hits
        // a local HttpMessageHandler stub wired via the test's HttpClient
        // constructor (since in the direct case the caller controls the
        // HttpClient's BaseAddress but we don't have a real A2A server here,
        // we only verify the surface shape, not actual HTTP).

        var containerRuntime = Substitute.For<IContainerRuntime>();
        containerRuntime.SendHttpJsonAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerHttpResponse(200, "{}"u8.ToArray()));

        // Proxy path
        using var proxyTransport = new DispatcherProxyA2ATransport(containerRuntime, ContainerId);
        using var proxyClient = proxyTransport.CreateHttpClient(AgentEndpoint);
        proxyClient.BaseAddress.ShouldBe(AgentEndpoint);

        var proxyResponse = await proxyClient.PostAsync("/", new StringContent("{}"), TestContext.Current.CancellationToken);
        proxyResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Direct path — we can't make a real HTTP call here, so only assert
        // the client is shaped correctly (BaseAddress set, no proxy handler).
        using var directTransport = new DirectA2ATransport();
        using var directClient = directTransport.CreateHttpClient(AgentEndpoint);
        directClient.BaseAddress.ShouldBe(AgentEndpoint);
    }
}