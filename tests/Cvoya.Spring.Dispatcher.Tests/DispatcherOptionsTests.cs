// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher.Tests;

using System;
using System.IO;

using Shouldly;

using Xunit;

/// <summary>
/// Pins <see cref="DispatcherOptions.DefaultWorkspaceRoot"/> to the
/// user-home-relative path it must produce on every host. The dispatcher
/// runs as a host process across Linux/macOS/Windows (issue #1063); a
/// regression to the legacy Linux-FHS default
/// (<c>/var/lib/spring-workspaces</c>) would break out-of-the-box dev on
/// macOS / Windows because the directory is not writable without root.
/// </summary>
public class DispatcherOptionsTests
{
    [Fact]
    public void DefaultWorkspaceRoot_ResolvesUnderUserHome_WhenAvailable()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            // Service-account contexts may have no resolvable home — skip
            // the assertion rather than fail. The other test covers the
            // non-empty case which is the operator-machine scenario this
            // pin actually protects.
            return;
        }

        var expected = Path.Combine(home, ".spring-voyage", "workspaces");
        DispatcherOptions.DefaultWorkspaceRoot.ShouldBe(expected);
    }

    [Fact]
    public void DefaultWorkspaceRoot_IsNotTheLegacyContainerPath()
    {
        // Guard against an accidental revert to the pre-#1063 default.
        // The legacy path requires root on every supported OS and is not
        // a valid out-of-the-box default for a host-process service.
        DispatcherOptions.DefaultWorkspaceRoot.ShouldNotBe("/var/lib/spring-workspaces");
    }

    [Fact]
    public void NewInstance_WorkspaceRoot_DefaultsToDefaultWorkspaceRoot()
    {
        new DispatcherOptions().WorkspaceRoot.ShouldBe(DispatcherOptions.DefaultWorkspaceRoot);
    }
}