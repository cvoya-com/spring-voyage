// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tests;

using System.IO;
using System.Runtime.Serialization;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;

using Shouldly;

using Xunit;

/// <summary>
/// Records that travel across the Dapr Actor remoting boundary must be
/// serializable by <c>DataContractSerializer</c>. Positional records lack a
/// parameterless constructor, so without explicit <c>[DataContract]</c> +
/// <c>[DataMember]</c> annotations the serializer throws
/// <see cref="InvalidDataContractException"/> at runtime — and that throw is
/// invisible in tests that mock the actor proxy.
///
/// These tests exercise the actual serializer so a missing annotation regresses
/// CI immediately. Bug #261: the wizard's "scratch + skip" path failed at
/// <c>IUnitActor.SetMetadataAsync</c> because <see cref="UnitMetadata"/> was
/// not annotated.
/// </summary>
public class DataContractSerializationTests
{
    [Fact]
    public void UnitMetadata_RoundTripsThroughDataContractSerializer()
    {
        var original = new UnitMetadata(
            DisplayName: "Engineering",
            Description: "An engineering team",
            Model: "claude-sonnet-4-6",
            Color: "#6366f1");

        var roundTripped = RoundTrip(original);

        roundTripped.ShouldBe(original);
    }

    [Fact]
    public void UnitMetadata_AllNullFields_RoundTrips()
    {
        // PATCH-style writes commonly send a sparse payload — ensure the
        // sparse case crosses the actor boundary too.
        var original = new UnitMetadata(null, null, null, null);

        var roundTripped = RoundTrip(original);

        roundTripped.ShouldBe(original);
    }

    [Fact]
    public void Address_RoundTripsThroughDataContractSerializer()
    {
        var original = new Address("unit", "engineering-team");

        var roundTripped = RoundTrip(original);

        roundTripped.ShouldBe(original);
    }

    private static T RoundTrip<T>(T value) where T : class
    {
        var serializer = new DataContractSerializer(typeof(T));
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, value);
        stream.Position = 0;
        return (T)serializer.ReadObject(stream)!;
    }
}