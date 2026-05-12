// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;

using Shouldly;

using Xunit;

/// <summary>
/// Guards the Dapr-actor remoting boundary against <c>DataContractSerializer</c>
/// regressions (#319). Dapr actor proxies marshal every parameter and return
/// type through <c>DataContractSerializer</c>, which requires either primitive
/// types, arrays of primitives, or types explicitly opted in with
/// <c>[DataContract]</c> + <c>[DataMember]</c>.
///
/// The runtime failures that motivated these tests were a
/// <c>ReadOnlyCollection&lt;Address&gt;</c> being returned via an
/// <c>IReadOnlyList&lt;T&gt;</c>-typed actor method (the wrapper type is not
/// a known type to the serializer) and a positional
/// <see cref="UnitConnectorBinding"/> record lacking data-contract annotations
/// (serializer could not construct it without a parameterless constructor).
/// </summary>
public class ActorInterfaceSerializationTests
{
    /// <summary>
    /// The set of Dapr actor interfaces whose surface must be serializable
    /// by <c>DataContractSerializer</c>. Adding a new actor interface?
    /// Append it here so the reflection scan covers it.
    /// </summary>
    private static readonly Type[] ActorInterfaces =
    [
        typeof(IAgent),
        typeof(IAgentActor),
        typeof(IUnitActor),
        typeof(IConnectorActor),
        typeof(IHumanActor),
    ];

    [Fact]
    public void UnitConnectorBinding_RoundTripsThroughDataContractSerializer()
    {
        // ADR-0040 / #2050: UnitConnectorBinding no longer crosses the
        // Dapr actor remoting boundary — bindings live on the
        // unit_connector_bindings EF table and cross via plain DI.
        // The [DataContract] / [DataMember] annotations on the record
        // are kept defensively because the type is still public surface
        // for connector packages and a future caller might re-introduce
        // a remoting hop. This test pins the round-trip so a regression
        // (someone removes the annotations and a future caller re-adds
        // an actor-boundary use site) fails fast.
        using var configDoc = JsonDocument.Parse("""{"owner":"acme","repo":"spring-voyage"}""");
        var original = new UnitConnectorBinding(
            TypeId: Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Config: configDoc.RootElement.Clone());

        var roundTripped = RoundTrip(original);

        roundTripped.ShouldNotBeNull();
        roundTripped.TypeId.ShouldBe(original.TypeId);
    }

    [Fact]
    public void AddressArray_RoundTripsThroughDataContractSerializer()
    {
        // Arrays of [DataContract] records must cross the boundary without
        // any KnownType annotation. This test is the positive counterpart
        // to #319 Bug 1 — the failing case used a ReadOnlyCollection wrapper.
        var original = new[]
        {
            Address.For("agent", TestSlugIds.HexFor("ada")),
            Address.For("unit", TestSlugIds.HexFor("engineering")),
        };

        var roundTripped = RoundTrip(original);

        roundTripped.ShouldNotBeNull();
        roundTripped!.Length.ShouldBe(2);
        roundTripped[0].ShouldBe(original[0]);
        roundTripped[1].ShouldBe(original[1]);
    }

    [Fact]
    public void AllActorInterfaceTypes_AreDataContractSerializable()
    {
        // Reflection scan: for every method on every actor interface, require
        // that every parameter type and return type is either a primitive,
        // a well-known serializable type, an array of serializable elements,
        // or an explicit [DataContract] record. Collections (IReadOnlyList,
        // IEnumerable, IReadOnlyDictionary, etc.) are rejected because their
        // runtime wrappers (ReadOnlyCollection<T>, Dictionary<,>.ValueCollection,
        // etc.) are not DataContract known types and fail at runtime.
        var failures = new List<string>();

        foreach (var iface in ActorInterfaces)
        {
            foreach (var method in iface.GetMethods())
            {
                var ctx = $"{iface.Name}.{method.Name}";

                foreach (var p in method.GetParameters())
                {
                    CheckType(p.ParameterType, $"{ctx} param '{p.Name}'", failures);
                }

                var ret = UnwrapTask(method.ReturnType);
                if (ret == typeof(void))
                {
                    continue;
                }
                CheckType(ret, $"{ctx} return", failures);
            }
        }

        failures.ShouldBeEmpty(
            "Actor-interface types must be DataContractSerializer-safe " +
            "(primitives, arrays, or [DataContract] records). See #319.");
    }

    /// <summary>
    /// #2199: every parameter on every actor-interface method must be
    /// non-optional, EXCEPT a trailing <see cref="CancellationToken"/>
    /// (which Dapr's actor proxy generator special-cases). Any other
    /// optional parameter — even with <c>null</c> as the default —
    /// makes Dapr throw <c>ArgumentException</c> at startup with
    /// "<i>The actor interface methods must not have out, ref or
    /// optional parameters.</i>", which kills the worker process before
    /// it can serve a single request. The runtime check happens inside
    /// <c>ActorRuntime</c> construction so the failure surfaces as a
    /// fatal startup crash with no request-time signal — pinning it
    /// here costs one reflection scan and prevents the recurrence.
    /// </summary>
    [Fact]
    public void AllActorInterfaceMethods_Reject_OptionalParameters_Except_TrailingCancellationToken()
    {
        var failures = new List<string>();

        foreach (var iface in ActorInterfaces)
        {
            foreach (var method in iface.GetMethods())
            {
                var parameters = method.GetParameters();
                for (var i = 0; i < parameters.Length; i++)
                {
                    var p = parameters[i];
                    if (!p.IsOptional)
                    {
                        continue;
                    }
                    var isTrailingCancellationToken =
                        i == parameters.Length - 1
                        && p.ParameterType == typeof(CancellationToken);
                    if (isTrailingCancellationToken)
                    {
                        continue;
                    }
                    failures.Add(
                        $"{iface.Name}.{method.Name} param '{p.Name}' is optional " +
                        $"(only a trailing CancellationToken may be optional on a Dapr actor interface).");
                }

                if (method.GetParameters().Any(p => p.IsOut || p.ParameterType.IsByRef))
                {
                    failures.Add(
                        $"{iface.Name}.{method.Name} has an out / ref parameter " +
                        $"(Dapr actor proxies cannot marshal these).");
                }
            }
        }

        failures.ShouldBeEmpty(
            "Dapr actor proxy generation rejects out/ref/optional parameters " +
            "at ActorRuntime construction time, killing the host process. " +
            "Make the parameter required (callers can pass null/default explicitly).");
    }

    private static Type UnwrapTask(Type t)
    {
        if (t == typeof(Task))
        {
            return typeof(void);
        }
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Task<>))
        {
            return t.GetGenericArguments()[0];
        }
        return t;
    }

    private static void CheckType(Type t, string context, List<string> failures)
    {
        // Unwrap Nullable<T>
        var underlying = Nullable.GetUnderlyingType(t);
        if (underlying is not null)
        {
            CheckType(underlying, context, failures);
            return;
        }

        if (IsAllowedLeafType(t))
        {
            return;
        }

        // CancellationToken is allowed (not transmitted, but appears on the
        // interface; Dapr's proxy omits it from the wire payload).
        if (t == typeof(CancellationToken))
        {
            return;
        }

        // Arrays of allowed types are the preferred collection shape — both
        // the element type must be serializable and the array itself is
        // natively handled by DataContractSerializer.
        if (t.IsArray)
        {
            var elem = t.GetElementType()!;
            CheckType(elem, $"{context} (array element)", failures);
            return;
        }

        // Reject rope-like collection interfaces: runtime wrappers are not
        // DataContract known types. Require T[] instead.
        if (IsForbiddenCollectionType(t))
        {
            failures.Add(
                $"{context}: collection type '{FormatTypeName(t)}' is not " +
                "DataContractSerializer-safe. Use an array (T[]) instead.");
            return;
        }

        // Positional records and other complex types must opt in with
        // [DataContract]. Without it, the serializer throws
        // InvalidDataContractException at runtime.
        if (!t.IsDefined(typeof(DataContractAttribute), inherit: false))
        {
            failures.Add(
                $"{context}: type '{FormatTypeName(t)}' is not marked " +
                "[DataContract] and is not a primitive or array. " +
                "Add [DataContract] + [DataMember] on its members.");
        }
    }

    private static bool IsAllowedLeafType(Type t)
    {
        // Primitives, strings, common value types the serializer handles
        // natively — enough to cover the actor-surface shapes we care about.
        if (t.IsPrimitive)
        {
            return true;
        }
        if (t.IsEnum)
        {
            return true;
        }
        return t == typeof(string)
            || t == typeof(Guid)
            || t == typeof(DateTime)
            || t == typeof(DateTimeOffset)
            || t == typeof(TimeSpan)
            || t == typeof(decimal)
            || t == typeof(Uri)
            || t == typeof(JsonElement);
    }

    private static bool IsForbiddenCollectionType(Type t)
    {
        // Generic collection interfaces that, at runtime, get resolved to
        // wrapper types the serializer can't handle without [KnownType].
        if (!t.IsGenericType)
        {
            // Non-generic IEnumerable / IList etc. would be equally bad but
            // never appear on our actor surfaces; no need to list them.
            return typeof(IEnumerable).IsAssignableFrom(t) && t != typeof(string);
        }
        var def = t.GetGenericTypeDefinition();
        return def == typeof(IEnumerable<>)
            || def == typeof(IReadOnlyList<>)
            || def == typeof(IReadOnlyCollection<>)
            || def == typeof(ICollection<>)
            || def == typeof(IList<>)
            || def == typeof(IReadOnlyDictionary<,>)
            || def == typeof(IDictionary<,>)
            || def == typeof(List<>)
            || def == typeof(Dictionary<,>);
    }

    private static string FormatTypeName(Type t)
    {
        if (!t.IsGenericType)
        {
            return t.Name;
        }
        var def = t.GetGenericTypeDefinition().Name;
        var args = string.Join(",", t.GetGenericArguments().Select(FormatTypeName));
        return $"{def.Split('`')[0]}<{args}>";
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
