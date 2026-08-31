using System.Text.Json;
using Kkdev92.HealthData.Models;
using Kkdev92.HealthData.Serialization;

namespace Kkdev92.HealthData.Tests;

/// <summary>
/// The helpers on a long-running <see cref="Operation"/>.
/// </summary>
/// <remarks>
/// Six public members and, until this file, nothing reaching any of them. The generated model is
/// exercised elsewhere; what is pinned here is the reading this SDK puts on it — when an operation
/// counts as finished, and that a payload decodes only through the generated contract.
/// </remarks>
public sealed class OperationExtensionsTests
{
    [Theory]
    [InlineData("""{"done":true}""", true, false)]
    [InlineData("""{"done":true,"error":{"code":5,"message":"gone"}}""", false, true)]
    [InlineData("""{"done":false}""", false, false)]
    [InlineData("""{}""", false, false)]
    public void DoneAndErrorTogetherDecideTheOutcome(string json, bool succeeded, bool failed)
    {
        var operation = Read(json);

        // Done alone is not success: a finished operation can finish by failing. Not done is
        // neither, however it will end.
        Assert.Equal(succeeded, operation.IsSucceeded());
        Assert.Equal(failed, operation.IsFailed());
    }

    [Fact]
    public void TheResponseDecodesIntoTheExpectedModel()
    {
        var operation = Read("""
            {
              "done": true,
              "response": {
                "@type": "type.googleapis.com/google.health.v4.Subscriber",
                "name": "projects/p/subscribers/s",
                "endpointUri": "https://example.test/hook"
              }
            }
            """);

        var subscriber = operation.TryGetResponse<Subscriber>();

        // @type is google.protobuf.Any's discriminator. It has no C# counterpart and is ignored
        // rather than rejected, so the remaining fields still bind.
        Assert.NotNull(subscriber);
        Assert.Equal("projects/p/subscribers/s", subscriber.Name);
        Assert.Equal("https://example.test/hook", subscriber.EndpointUri);
    }

    [Fact]
    public void TheMetadataDecodesIntoTheExpectedModel()
    {
        var operation = Read("""{"done":false,"metadata":{"@type":"type.googleapis.com/google.protobuf.Empty"}}""");

        Assert.NotNull(operation.TryGetMetadata<Empty>());
    }

    [Fact]
    public void AMissingPayloadIsNullRatherThanAnException()
    {
        var operation = Read("""{"done":true}""");

        Assert.Null(operation.TryGetResponse<Subscriber>());
        Assert.Null(operation.TryGetMetadata<Empty>());
    }

    [Fact]
    public void AnExplicitContractIsHonoured()
    {
        var operation = Read("""{"done":true,"response":{"name":"projects/p/subscribers/s"}}""");

        // The overload for callers outside this assembly, which hand in their own JsonTypeInfo.
        var subscriber = operation.TryGetResponse(HealthDataJson.ReadInfo<Subscriber>());

        Assert.Equal("projects/p/subscribers/s", subscriber?.Name);
    }

    [Fact]
    public void ATypeOutsideTheContractFailsLoudly()
    {
        var operation = Read("""{"done":true,"response":{"anything":1}}""");

        // Reflection is disabled for the shipping assembly, so a type the generated context does
        // not know cannot be resolved. Under JIT with reflection it would quietly work and then
        // break only under Native AOT; refusing here keeps the two behaving the same.
        //
        // NotSupportedException, not InvalidOperationException: the contract is looked up through
        // JsonSerializerOptions.GetTypeInfo, and since .NET 7 that is the exception a source-generated
        // resolver raises for a type it was not given. The XML doc on ReadInfo said otherwise until
        // this test reached the line.
        Assert.Throws<NotSupportedException>(() => operation.TryGetResponse<NotInTheContract>());
    }

    [Fact]
    public void ANullOperationIsRefusedByEveryHelper()
    {
        Operation operation = null!;

        Assert.Throws<ArgumentNullException>(() => operation.IsSucceeded());
        Assert.Throws<ArgumentNullException>(() => operation.IsFailed());
        Assert.Throws<ArgumentNullException>(() => operation.TryGetResponse<Subscriber>());
        Assert.Throws<ArgumentNullException>(() => operation.TryGetMetadata<Empty>());
        Assert.Throws<ArgumentNullException>(() => operation.TryGetResponse(HealthDataJson.ReadInfo<Subscriber>()));
    }

    private static Operation Read(string json)
        => JsonSerializer.Deserialize(json, HealthDataJson.ReadInfo<Operation>())!;

    private sealed class NotInTheContract;
}
