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
/// Unit tests for <see cref="DispatcherProxyHttpMessageHandler"/> — the
/// <see cref="HttpMessageHandler"/> that translates outbound A2A SDK HTTP
/// calls into <see cref="IContainerRuntime.SendHttpJsonAsync"/> hops on the
/// dispatcher (ADR 0028 / issue #1160). The behaviours pinned here are the
/// contract <see cref="A2AExecutionDispatcher.SendA2AMessageAsync"/> relies
/// on; if any of them changes the SDK roundtrip silently breaks.
/// </summary>
public class DispatcherProxyHttpMessageHandlerTests
{
    private const string ContainerId = "agent-container-x";

    [Fact]
    public async Task SendAsync_ForwardsRequestBodyToContainerRuntime()
    {
        var runtime = Substitute.For<IContainerRuntime>();
        runtime.SendHttpJsonAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerHttpResponse(200, "{\"ok\":true}"u8.ToArray()));

        using var handler = new DispatcherProxyHttpMessageHandler(runtime, ContainerId);
        using var client = new HttpClient(handler, disposeHandler: false);

        var requestBody = "{\"jsonrpc\":\"2.0\",\"method\":\"message/send\"}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:8999/")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var responseBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        responseBody.ShouldBe("{\"ok\":true}");

        await runtime.Received(1).SendHttpJsonAsync(
            ContainerId,
            "http://localhost:8999/",
            Arg.Is<byte[]>(b => Encoding.UTF8.GetString(b) == requestBody),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_NonPostMethod_Throws()
    {
        // The narrow contract is intentional: the proxy primitive only
        // supports POST + JSON body, and any caller that needs GET (today
        // only the readiness probe) must go through the dedicated
        // ProbeContainerHttpAsync path. Trip loudly if a future code path
        // accidentally widens it.
        var runtime = Substitute.For<IContainerRuntime>();

        using var handler = new DispatcherProxyHttpMessageHandler(runtime, ContainerId);
        using var client = new HttpClient(handler, disposeHandler: false);

        await Should.ThrowAsync<NotSupportedException>(async () =>
            await client.GetAsync("http://localhost:8999/", TestContext.Current.CancellationToken));

        await runtime.DidNotReceive().SendHttpJsonAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_RuntimeReturns502_PreservesStatusOnResponse()
    {
        // The dispatcher collapses every wget failure mode (DNS, connection
        // refused, missing wget, container gone) onto 502 with an empty
        // body. The proxy must hand that through verbatim so the A2A SDK's
        // existing retry/timeout policy sees a real HTTP failure rather
        // than e.g. a fabricated 200 with empty content.
        var runtime = Substitute.For<IContainerRuntime>();
        runtime.SendHttpJsonAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerHttpResponse(502, []));

        using var handler = new DispatcherProxyHttpMessageHandler(runtime, ContainerId);
        using var client = new HttpClient(handler, disposeHandler: false);

        using var response = await client.PostAsync(
            "http://localhost:8999/",
            new StringContent("{}", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        var body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        body.ShouldBeEmpty();
    }

    [Fact]
    public void Constructor_BlankContainerId_Throws()
    {
        var runtime = Substitute.For<IContainerRuntime>();

        Should.Throw<ArgumentException>(
            () => new DispatcherProxyHttpMessageHandler(runtime, "  "));
    }
}
