// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.SlackInstall;

using System.IO;

using Cvoya.Spring.Cli.SlackInstall;

using Shouldly;

using Xunit;

/// <summary>
/// Covers the pre-flight host check that guards <c>spring connector slack
/// install</c> against the silently-broken-manifest class of bug
/// (issue #2868).
/// </summary>
public class SlackInstallHostValidationTests
{
    [Theory]
    [InlineData("http://localhost")]
    [InlineData("HTTP://localhost")]
    [InlineData("https://localhost")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://[::1]/")]
    public void ValidateSvHost_LoopbackWithoutExplicitPort_Throws(string host)
    {
        var stdout = new StringWriter();
        var ex = Should.Throw<SlackInstallException>(
            () => SlackInstallCommand.ValidateSvHost(host, socketMode: false, stdout));
        ex.Message.ShouldContain("specify the port explicitly");
    }

    [Theory]
    [InlineData("http://localhost:5000")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("https://localhost:5001")]
    public void ValidateSvHost_LoopbackWithExplicitPort_NoSocketMode_WarnsOnly(string host)
    {
        var stdout = new StringWriter();
        SlackInstallCommand.ValidateSvHost(host, socketMode: false, stdout);
        stdout.ToString().ShouldContain("Slack's servers cannot reach localhost");
    }

    [Theory]
    [InlineData("http://localhost:5000")]
    [InlineData("http://127.0.0.1:8080")]
    public void ValidateSvHost_LoopbackWithExplicitPort_SocketMode_Silent(string host)
    {
        var stdout = new StringWriter();
        SlackInstallCommand.ValidateSvHost(host, socketMode: true, stdout);
        stdout.ToString().ShouldNotContain("WARNING:");
    }

    [Fact]
    public void ValidateSvHost_NonLoopbackHttp_Warns()
    {
        var stdout = new StringWriter();
        SlackInstallCommand.ValidateSvHost("http://sv.example.com", socketMode: false, stdout);
        stdout.ToString().ShouldContain("not https://");
    }

    [Fact]
    public void ValidateSvHost_NonLoopbackHttps_Silent()
    {
        var stdout = new StringWriter();
        SlackInstallCommand.ValidateSvHost("https://sv.example.com", socketMode: false, stdout);
        stdout.ToString().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://sv.example.com")]
    [InlineData("file:///etc/passwd")]
    public void ValidateSvHost_NonHttpScheme_Throws(string host)
    {
        var stdout = new StringWriter();
        Should.Throw<SlackInstallException>(
            () => SlackInstallCommand.ValidateSvHost(host, socketMode: false, stdout));
    }
}
