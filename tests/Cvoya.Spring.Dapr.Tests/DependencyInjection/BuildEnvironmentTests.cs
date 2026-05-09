// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.DependencyInjection;

using Cvoya.Spring.Dapr.DependencyInjection;

using Xunit;

/// <summary>
/// Verifies that <see cref="BuildEnvironment.IsDesignTimeTooling"/> does not
/// accidentally activate during normal test runs. The test runner entry
/// assembly is neither <c>GetDocument.Insider</c> nor <c>dotnet-ef</c>, so
/// the property must be <see langword="false"/>.
/// </summary>
public class BuildEnvironmentTests
{
    [Fact]
    public void IsDesignTimeTooling_WhenRunningUnderTestRunner_ReturnsFalse()
    {
        Assert.False(
            BuildEnvironment.IsDesignTimeTooling,
            "the test runner is not a design-time tool; the gate must not " +
            "activate during normal application or test execution");
    }
}
