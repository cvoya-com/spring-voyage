// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Secrets;

using Xunit;

/// <summary>
/// Serializes tests that mutate the process-wide
/// <c>SPRING_SECRETS_AES_KEY</c> environment variable. Without this
/// collection, xUnit runs test classes in parallel — so two classes
/// writing/reading the same env var race each other and the save/restore
/// in their <see cref="System.IDisposable.Dispose"/> clobbers whichever
/// value the sibling test expects to observe.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SecretsEnvironmentVariableCollection
{
    public const string Name = "SPRING_SECRETS_AES_KEY env var";
}