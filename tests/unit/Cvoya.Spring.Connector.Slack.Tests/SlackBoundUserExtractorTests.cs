// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.Slack;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Tenancy;

using Shouldly;

using Xunit;

/// <summary>
/// Pins ADR-0061 §7.1 — bound users surface as a list, even in OSS
/// where the list has length 1. The extractor is the seam the platform
/// dispatches through to keep slack-specific JSON knowledge out of
/// the generic binding store.
/// </summary>
public class SlackBoundUserExtractorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Handles_SlackSlug_True()
    {
        var sut = new SlackBoundUserExtractor();
        sut.Handles("slack").ShouldBeTrue();
    }

    [Fact]
    public void Handles_OtherSlug_False()
    {
        var sut = new SlackBoundUserExtractor();
        sut.Handles("github").ShouldBeFalse();
        sut.Handles("calendar").ShouldBeFalse();
    }

    [Fact]
    public void Extract_OssBinding_ReturnsSingletonList()
    {
        // OSS-shaped binding: one bound user (the OAuth installer
        // mapped to OssTenantUserIds.Operator).
        var config = new TenantSlackConfig(
            TeamId: "T123",
            TeamName: "Workspace",
            BotUserId: "U-bot",
            BotTokenSecretName: "slack/T123/bot-token",
            SigningSecretSecretName: "slack/T123/signing-secret",
            InstallerUserId: "U-installer",
            SingleUserMode: true,
            Mode: SlackBindingMode.Workspace,
            BoundUsers: new[]
            {
                new TenantSlackBoundUser("U-installer", OssTenantUserIds.Operator),
            });

        var binding = BindingFor(config);
        var sut = new SlackBoundUserExtractor();

        var users = sut.Extract(binding);

        users.Count.ShouldBe(1);
        users[0].ExternalUserId.ShouldBe("U-installer");
        users[0].TenantUserId.ShouldBe(OssTenantUserIds.Operator);
    }

    [Fact]
    public void Extract_MultiUserBinding_ReturnsListOfAll()
    {
        // ADR-0061 §7.1: cloud growth scenario — multiple bound users
        // map to multiple TenantUsers. The extractor must not collapse.
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var config = new TenantSlackConfig(
            TeamId: "T456",
            TeamName: "Workspace",
            BotUserId: "U-bot",
            BotTokenSecretName: "slack/T456/bot-token",
            SigningSecretSecretName: "slack/T456/signing-secret",
            InstallerUserId: "U-alice",
            SingleUserMode: false,
            Mode: SlackBindingMode.Workspace,
            BoundUsers: new[]
            {
                new TenantSlackBoundUser("U-alice", alice),
                new TenantSlackBoundUser("U-bob", bob),
            });

        var binding = BindingFor(config);
        var sut = new SlackBoundUserExtractor();

        var users = sut.Extract(binding);

        users.Count.ShouldBe(2);
        users.ShouldContain(u => u.ExternalUserId == "U-alice" && u.TenantUserId == alice);
        users.ShouldContain(u => u.ExternalUserId == "U-bob" && u.TenantUserId == bob);
    }

    [Fact]
    public void Extract_EmptyBoundUsers_ReturnsEmptyList()
    {
        var config = new TenantSlackConfig(
            TeamId: "T789",
            TeamName: null,
            BotUserId: "U-bot",
            BotTokenSecretName: "slack/T789/bot-token",
            SigningSecretSecretName: "slack/T789/signing-secret",
            InstallerUserId: "U-empty",
            SingleUserMode: true,
            Mode: SlackBindingMode.Workspace,
            BoundUsers: Array.Empty<TenantSlackBoundUser>());

        var binding = BindingFor(config);
        var sut = new SlackBoundUserExtractor();

        sut.Extract(binding).Count.ShouldBe(0);
    }

    private static TenantConnectorBinding BindingFor(TenantSlackConfig config)
    {
        var json = JsonSerializer.SerializeToElement(config, JsonOptions);
        return new TenantConnectorBinding("slack", SlackConnectorType.SlackTypeId, json);
    }
}
