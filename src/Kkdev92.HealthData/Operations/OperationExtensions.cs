using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Kkdev92.HealthData.Serialization;

namespace Kkdev92.HealthData;

/// <summary>
/// Helpers for the long-running <see cref="Operation"/> resource.
/// </summary>
/// <remarks>
/// <para>
/// Discovery revision 20260805 exposes no operations-polling resource, so this SDK does not
/// invent one. There is no <c>GetOperationAsync</c> and no <c>WaitAsync</c>: guessing a URL for
/// them would produce an SDK that appears to work and then 404s.
/// </para>
/// <para>
/// What the SDK does guarantee is that <c>response</c> and <c>metadata</c> survive intact, so a
/// caller can decode them when it knows the expected type.
/// </para>
/// </remarks>
public static class OperationExtensions
{
    /// <summary>True when the operation completed successfully.</summary>
    public static bool IsSucceeded(this Operation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return operation.Done == true && operation.Error is null;
    }

    /// <summary>True when the operation completed with an error.</summary>
    public static bool IsFailed(this Operation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return operation.Done == true && operation.Error is not null;
    }

    /// <summary>
    /// Decodes <c>response</c> into a generated model.
    /// </summary>
    /// <typeparam name="T">The expected response type.</typeparam>
    /// <returns>The decoded value, or <see langword="null"/> when there is no response payload.</returns>
    /// <remarks>
    /// The payload is a <c>google.protobuf.Any</c>, so it also carries an <c>@type</c> field that
    /// has no C# counterpart. It is ignored rather than rejected.
    /// </remarks>
    public static T? TryGetResponse<T>(this Operation operation)
        where T : class
        => Decode<T>(operation, o => o.Response);

    /// <summary>Decodes <c>metadata</c> into a generated model.</summary>
    /// <typeparam name="T">The expected metadata type.</typeparam>
    public static T? TryGetMetadata<T>(this Operation operation)
        where T : class
        => Decode<T>(operation, o => o.Metadata);

    /// <summary>Decodes a payload using an explicit contract, for callers outside this assembly.</summary>
    public static T? TryGetResponse<T>(this Operation operation, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(typeInfo);

        return operation.Response is { } response ? response.Deserialize(typeInfo) : null;
    }

    private static T? Decode<T>(Operation operation, Func<Operation, JsonElement?> select)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(operation);

        var payload = select(operation);

        // Resolved through the generated context: reflection is disabled for this assembly, so a
        // type outside the contract fails loudly instead of silently working in JIT and breaking
        // under Native AOT.
        return payload is { } value ? value.Deserialize(HealthDataJson.ReadInfo<T>()) : null;
    }
}
