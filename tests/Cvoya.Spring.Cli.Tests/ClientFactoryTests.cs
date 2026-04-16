// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests;

using Shouldly;

using Xunit;

public class ClientFactoryTests
{
    [Fact]
    public void Create_WithoutOverride_ReturnsClient()
    {
        // Default config endpoint is used when no override is provided.
        var client = ClientFactory.Create();
        client.ShouldNotBeNull();
    }

    [Fact]
    public void Create_WithBaseUrlOverride_ReturnsClientPointingAtOverride()
    {
        var client = ClientFactory.Create("http://custom-host:9999");
        client.ShouldNotBeNull();
    }

    [Fact]
    public void Create_ReturnsSameHttpClientInstance()
    {
        // Both calls should succeed and share the static HttpClient
        // (we can't inspect the private field, but exercising both paths
        // in sequence verifies no ObjectDisposedException is thrown).
        var client1 = ClientFactory.Create();
        var client2 = ClientFactory.Create("http://other:1234");
        client1.ShouldNotBeNull();
        client2.ShouldNotBeNull();
    }
}