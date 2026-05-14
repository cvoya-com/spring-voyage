// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests;

using Xunit;

/// <summary>
/// Serialises CLI test classes that swap <see cref="System.Console.Out"/>
/// (or otherwise rely on the default stdout sink). xUnit runs test classes
/// in parallel by default; multiple classes calling
/// <c>Console.SetOut(myWriter)</c> at the same time race over a
/// process-wide handle, which has produced three different symptoms in
/// CI:
/// <list type="bullet">
///   <item>captured stdout in one test contains output rendered by
///   another test (bleed-through);</item>
///   <item>captured stdout is empty because a peer restored
///   <c>Console.Out</c> mid-write;</item>
///   <item>writes throw <see cref="System.ObjectDisposedException"/>
///   because a peer's <c>StringWriter</c> was disposed under us.</item>
/// </list>
/// All three were observed across multiple PRs in the v2 wave (see
/// issue #1111). Marking the offending classes with
/// <see cref="CollectionAttribute"/> keyed on this collection forces them
/// to run sequentially, so the racy <c>Console.SetOut</c> windows can
/// never overlap.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleRedirectionCollection
{
    public const string Name = "ConsoleRedirection";
}
