// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.State;

using Cvoya.Spring.Dapr.State;
using FluentAssertions;
using global::Dapr.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

/// <summary>
/// Unit tests for <see cref="DaprStateStore"/> verifying correct delegation to <see cref="DaprClient"/>.
/// </summary>
public class DaprStateStoreTests
{
    private const string StoreName = "test-store";
    private readonly DaprClient _daprClient = Substitute.For<DaprClient>();
    private readonly DaprStateStore _sut;

    public DaprStateStoreTests()
    {
        var options = Options.Create(new DaprStateStoreOptions { StoreName = StoreName });
        var logger = Substitute.For<ILogger<DaprStateStore>>();
        _sut = new DaprStateStore(_daprClient, options, logger);
    }

    [Fact]
    public async Task GetAsync_ExistingKey_ReturnsValue()
    {
        var expected = "hello";
        _daprClient.GetStateAsync<string>(StoreName, "key1", cancellationToken: Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _sut.GetAsync<string>("key1", TestContext.Current.CancellationToken);

        result.Should().Be(expected);
    }

    [Fact]
    public async Task GetAsync_MissingKey_ReturnsDefault()
    {
        _daprClient.GetStateAsync<string>(StoreName, "missing", cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string>(null!));

        var result = await _sut.GetAsync<string>("missing", TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_StoresValue()
    {
        await _sut.SetAsync("key1", 42, TestContext.Current.CancellationToken);

        await _daprClient.Received(1).SaveStateAsync(StoreName, "key1", 42, cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_DeletesKey()
    {
        await _sut.DeleteAsync("key1", TestContext.Current.CancellationToken);

        await _daprClient.Received(1).DeleteStateAsync(StoreName, "key1", cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ContainsAsync_ExistingKey_ReturnsTrue()
    {
        _daprClient.GetStateAsync<object>(StoreName, "key1", cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new object());

        var result = await _sut.ContainsAsync("key1", TestContext.Current.CancellationToken);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task ContainsAsync_MissingKey_ReturnsFalse()
    {
        _daprClient.GetStateAsync<object>(StoreName, "missing", cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<object>(null!));

        var result = await _sut.ContainsAsync("missing", TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetAsync_UsesConfiguredStoreName()
    {
        await _sut.GetAsync<string>("any-key", TestContext.Current.CancellationToken);

        await _daprClient.Received(1).GetStateAsync<string>(StoreName, "any-key", cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAsync_ComplexType_DelegatesToDaprClient()
    {
        var value = new TestRecord("test", 123);

        await _sut.SetAsync("complex-key", value, TestContext.Current.CancellationToken);

        await _daprClient.Received(1).SaveStateAsync(StoreName, "complex-key", value, cancellationToken: Arg.Any<CancellationToken>());
    }

    private record TestRecord(string Name, int Count);
}
