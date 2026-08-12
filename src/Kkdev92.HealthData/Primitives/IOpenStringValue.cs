namespace Kkdev92.HealthData;

/// <summary>
/// Implemented by generated open enum wrappers over a wire string value.
/// </summary>
/// <typeparam name="TSelf">The implementing type.</typeparam>
/// <remarks>
/// Wire enums are never closed C# enums (ADR-0005). Discovery revision 20260805 declares 58
/// enum-typed properties, and <c>Exercise.exerciseType</c> alone has roughly 180 values, all
/// protobuf-derived and all additive over time. A closed enum would turn a new server-side value
/// into a deserialization failure.
/// </remarks>
public interface IOpenStringValue<out TSelf>
    where TSelf : struct, IOpenStringValue<TSelf>
{
    /// <summary>The wire value, preserved exactly, including values not known at generation time.</summary>
    string Value { get; }

    /// <summary>Creates an instance from a wire value.</summary>
    static abstract TSelf FromValue(string value);
}
