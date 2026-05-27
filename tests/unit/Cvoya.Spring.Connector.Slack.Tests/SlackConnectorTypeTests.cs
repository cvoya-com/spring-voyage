// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.Slack;
using Cvoya.Spring.Connectors;

using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Pins the Slack connector's identity surface: stable TypeId, slug,
/// tenant binding scope, presence of the per-user config type.
/// </summary>
public class SlackConnectorTypeTests
{
    [Fact]
    public void TypeId_IsStable()
    {
        // Changing this value invalidates existing Slack bindings —
        // a deliberate drift guard.
        SlackConnectorType.SlackTypeId.ShouldBe(
            new Guid("2c8d5b1f-9a4e-4f8b-b7c3-3e1d4a5b6c70"));
    }

    [Fact]
    public void BindingScope_IsTenant()
    {
        // ADR-0061 §1: Slack is workspace-shaped — one binding per
        // tenant, no per-unit configuration. Distinct from the
        // historical per-unit connectors (GitHub / Arxiv / WebSearch).
        var sut = new SlackConnectorType(NullLoggerFactory.Instance);
        sut.BindingScope.ShouldBe(BindingScope.Tenant);
    }

    [Fact]
    public void Slug_IsStable()
    {
        var sut = new SlackConnectorType(NullLoggerFactory.Instance);
        sut.Slug.ShouldBe("slack");
    }

    [Fact]
    public void ConfigType_IsTenantSlackConfig()
    {
        var sut = new SlackConnectorType(NullLoggerFactory.Instance);
        sut.ConfigType.ShouldBe(typeof(TenantSlackConfig));
    }

    [Fact]
    public void UserConfigType_IsTenantSlackUserConfig()
    {
        // ADR-0047 §4: Slack contributes a strictly display-identity
        // user-config schema.
        var sut = new SlackConnectorType(NullLoggerFactory.Instance);
        sut.UserConfigType.ShouldBe(typeof(TenantSlackUserConfig));
    }

    [Fact]
    public void ConfigSchema_DescribesTeamIdAndBoundUsers()
    {
        var schema = SlackConnectorType.BuildConfigSchema();
        var properties = schema.GetProperty("properties");

        // Required fields per ADR-0061 §2 — every binding row carries
        // these.
        properties.TryGetProperty("team_id", out _).ShouldBeTrue();
        properties.TryGetProperty("bot_user_id", out _).ShouldBeTrue();
        properties.TryGetProperty("bot_token_secret_name", out _).ShouldBeTrue();
        properties.TryGetProperty("signing_secret_secret_name", out _).ShouldBeTrue();
        properties.TryGetProperty("installer_user_id", out _).ShouldBeTrue();
        properties.TryGetProperty("single_user_mode", out _).ShouldBeTrue();
        properties.TryGetProperty("mode", out _).ShouldBeTrue();

        // ADR-0061 §7.1: bound_users is a list, even in OSS.
        properties.TryGetProperty("bound_users", out var boundUsers).ShouldBeTrue();
        boundUsers.GetProperty("type").GetString().ShouldBe("array");
    }

    [Fact]
    public void UserConfigSchema_RequiresSlackUserId()
    {
        var schema = SlackConnectorType.BuildUserConfigSchema();

        // ADR-0047 §4: display-identity only — slack_user_id is the
        // anchor, display_name is optional.
        schema.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString())
            .ShouldContain("slack_user_id");
    }
}
