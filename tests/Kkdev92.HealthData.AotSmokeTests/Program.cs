using System.Text.Json;
using Kkdev92.HealthData;
using Kkdev92.HealthData.Authentication;
using Kkdev92.HealthData.Authentication.OAuth;
using Kkdev92.HealthData.DependencyInjection;
using Kkdev92.HealthData.Http;
using Kkdev92.HealthData.Models;
using Kkdev92.HealthData.Names;
using Kkdev92.HealthData.Requests;
using Kkdev92.HealthData.Resilience;
using Kkdev92.HealthData.Serialization;
using Kkdev92.HealthData.Webhooks;
using Microsoft.Extensions.DependencyInjection;

// Native AOT smoke application.
// A library that merely compiles is not evidence of AOT compatibility. This is a real consumer
// that CI publishes with PublishAot=true, so the trimmer and ILCompiler see the same code paths
// an application would use.
// Covered here: source-generated deserialization, every custom converter, open enums including an
// unknown value, the write contract, union access, the OAuth request shapes, the container
// composing a client, and the webhook notification contract - all four shipping packages, because
// the package README makes this promise on behalf of all four.

var failures = 0;

void Check(string name, bool condition)
{
    Console.WriteLine($"{(condition ? "ok  " : "FAIL")}  {name}");

    if (!condition)
    {
        failures++;
    }
}

Console.WriteLine($"api version: {HealthDataGeneratedApi.ApiVersion}");
Console.WriteLine($"discovery revision: {HealthDataGeneratedApi.DiscoveryRevision}");
Console.WriteLine($"operations: {HealthDataGeneratedApi.OperationCount}");
Console.WriteLine($"base address: {HealthDataApiMetadata.DefaultBaseAddress}");
Console.WriteLine();

// Response deserialization, including int64-as-string and a nested union member.
const string listPayload = """
    {
      "dataPoints": [
        {
          "name": "users/me/dataTypes/heart-rate/dataPoints/1",
          "heartRate": { "beatsPerMinute": "58" }
        }
      ],
      "nextPageToken": "CBI"
    }
    """;

var page = JsonSerializer.Deserialize(listPayload, HealthDataJson.ReadInfo<ListDataPointsResponse>())!;

Check("list response deserializes", page.DataPoints is { Count: 1 });
Check("int64 arrives as long", page.DataPoints![0].HeartRate!.BeatsPerMinute == 58L);
Check("pagination token read", page.NextPageToken == "CBI");

// Timestamp and duration converters, open enum, and an output-only property.
const string sleepPayload = """
    {
      "type": "DEEP",
      "startTime": "2026-08-09T22:15:00Z",
      "startUtcOffset": "-14400s",
      "createTime": "2026-08-10T06:00:00Z"
    }
    """;

var stage = JsonSerializer.Deserialize(sleepPayload, HealthDataJson.ReadInfo<SleepStage>())!;

Check("open enum parses", stage.Type == SleepStage.Types.Type.Deep);
Check("timestamp parses", stage.StartTime!.Value.Value.Hour == 22);
Check("duration parses", stage.StartUtcOffset == new GoogleDuration(-14400, 0));
Check("output-only value is readable", stage.CreateTime is not null);

// An enum value that did not exist at generation time must survive untouched.
var future = JsonSerializer.Deserialize("""{"type":"MICRO_AROUSAL"}""", HealthDataJson.ReadInfo<SleepStage>())!;
Check("unknown enum value preserved", future.Type!.Value.Value == "MICRO_AROUSAL");

// Request serialization must never echo an output-only value back to the service.
var written = JsonSerializer.Serialize(stage, HealthDataJson.WriteInfo<SleepStage>());
Check("write contract drops output-only", !written.Contains("createTime", StringComparison.Ordinal));
Check("write contract keeps wire names", written.Contains("\"startUtcOffset\":\"-14400s\"", StringComparison.Ordinal));

// Scope and error constants are generated, not handwritten.
Check("scopes generated", HealthDataScopes.ProfileReadonly.EndsWith("googlehealth.profile.readonly", StringComparison.Ordinal));
Check("error reasons generated", HealthDataErrorReasons.AccountNotLinked == "ACCOUNT_NOT_LINKED");

// The full client path: request building, descriptor propagation, send, deserialize. A stub
// handler keeps this offline while still exercising every generated code path under AOT.
using var stubHandler = new StubHandler();
using var httpClient = new HttpClient(stubHandler) { BaseAddress = HealthDataApiMetadata.DefaultBaseAddress };
var client = new HealthDataClient(httpClient);

var listed = await client.Users.DataPoints.ListAsync(new ListDataPointsRequest
{
    Parent = UserName.Me.DataType("heart-rate"),
    PageSize = 2,
});

Check("resource client round-trips", listed.DataPoints is { Count: 1 });
Check(
    "request url is exact",
    stubHandler.LastUrl == "/v4/users/me/dataTypes/heart-rate/dataPoints?pageSize=2&prettyPrint=false");
Check("operation descriptor attached", stubHandler.LastOperationId == "health.users.dataTypes.dataPoints.list");


// Authentication: the authorization URL and PKCE are string work, and the handler is the piece
// that actually has to run under AOT for any call to be authorized.
using var oauthHttpClient = new HttpClient();

var oauth = new GoogleOAuthClient(
    oauthHttpClient,
    new GoogleOAuthOptions
    {
        ClientId = "client-123.apps.googleusercontent.com",
        RedirectUri = new Uri("https://example.test/callback"),
    });

var pkce = PkceCodeChallenge.Create();
var authorizationUrl = oauth.CreateAuthorizationUrl([HealthDataScopes.ProfileReadonly], state: "xyz", pkce: pkce);

Check("authorization url built", authorizationUrl.Query.Contains("code_challenge=", StringComparison.Ordinal));
Check("pkce challenge is unpadded base64url", !pkce.CodeChallenge.Contains('=', StringComparison.Ordinal));

using var authorized = new HttpClient(
    new HealthDataAuthorizationHandler(new StaticAccessTokenProvider("ya29.token"))
    {
        InnerHandler = new StubHandler(),
    })
{
    BaseAddress = HealthDataApiMetadata.DefaultBaseAddress,
};

var authorizedList = await new HealthDataClient(authorized).Users.DataPoints.ListAsync(new ListDataPointsRequest
{
    Parent = UserName.Me.DataType("heart-rate"),
});

Check("authorized call round-trips", authorizedList.DataPoints is { Count: 1 });

// Dependency injection: the container is where reflection usually hides. Resolving a real client
// through it exercises the whole registration, not just the extension method compiling.
var services = new ServiceCollection();
services.AddHealthDataAccessToken("ya29.token");
services.AddHealthData(options => options.Retry = new HealthDataRetryOptions { MaxAttempts = 2 });

using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();

Check("container resolves a client", scope.ServiceProvider.GetRequiredService<HealthDataClient>() is not null);

// Webhooks, end to end: a locally generated P-256 key, a keyset in Tink's published shape, and a
// signature over the raw bytes. Google's own keys can only verify Google's signatures, so this is
// the only way to reach the verified path — which is the path that runs the source-generated JSON
// context and the ECDSA import, and therefore the part worth proving under AOT.
const string notificationPayload =
    """{"data":{"version":"1","clientProvidedSubscriptionName":"sub","healthUserId":"user-1","operation":"UPSERT","dataType":"steps","intervals":[]}}""";

var notificationBytes = System.Text.Encoding.UTF8.GetBytes(notificationPayload);

using var webhookKey = new TinkSmokeKey(keyId: 1);
using var keyProvider = new HealthDataWebhookKeyProvider(new HttpClient(new KeysetStubHandler(webhookKey.ToKeysetJson())));

var receiver = new HealthDataWebhookReceiver(
    new HealthDataWebhookSignatureVerifier(keyProvider), endpointSecret: "smoke-secret");

var received = await receiver.HandleAsync(
    notificationBytes, webhookKey.Sign(notificationBytes), "smoke-secret");

Check("notification verifies", received.Kind == WebhookRequestKind.Notification);
Check("notification deserializes", received.Notification!.Data!.HealthUserId == "user-1");
Check("notification operation read", received.Notification.Data.IsUpsert);
Check(
    "challenge detection works",
    HealthDataWebhookReceiver.IsVerificationChallenge("""{"type":"verification"}"""u8)
        && !HealthDataWebhookReceiver.IsVerificationChallenge(notificationBytes));

Console.WriteLine();
Console.WriteLine(failures == 0 ? "AOT smoke: PASS" : $"AOT smoke: FAIL ({failures})");
return failures == 0 ? 0 : 1;

/// <summary>Serves a fixed keyset, so the verifier has something to fetch without a network.</summary>
internal sealed class KeysetStubHandler(string keyset) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(keyset, System.Text.Encoding.UTF8, "application/json"),
        });
}

/// <summary>
/// A P-256 key rendered the way Google publishes one, and signatures in the same layout.
/// </summary>
/// <remarks>
/// The keyset JSON and the signature framing here are byte-compatible with Google's: an
/// EcdsaPublicKey protobuf in Base64, and a 5-byte TINK prefix of 0x01 then the key id
/// big-endian. That compatibility is what makes verifying against it evidence of anything.
/// </remarks>
internal sealed class TinkSmokeKey(uint keyId) : IDisposable
{
    private readonly System.Security.Cryptography.ECDsa _ecdsa =
        System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);

    public string ToKeysetJson()
    {
        var parameters = _ecdsa.ExportParameters(includePrivateParameters: false);
        var material = new List<byte>();

        // field 2 (params): hash_type=3 (SHA256), curve=2 (NIST_P256), encoding=2 (DER)
        material.AddRange([0x12, 0x06, 0x08, 0x03, 0x10, 0x02, 0x18, 0x02]);

        AppendBytes(material, field: 3, [0x00, .. parameters.Q.X!]);
        AppendBytes(material, field: 4, [0x00, .. parameters.Q.Y!]);

        var value = Convert.ToBase64String([.. material]);

        return $$"""
            {"primaryKeyId":{{keyId}},"key":[{"keyData":{"typeUrl":"type.googleapis.com/google.crypto.tink.EcdsaPublicKey","value":"{{value}}","keyMaterialType":"ASYMMETRIC_PUBLIC"},"status":"ENABLED","keyId":{{keyId}},"outputPrefixType":"TINK"}]}
            """;

        static void AppendBytes(List<byte> target, int field, byte[] value)
        {
            target.Add((byte)((field << 3) | 2));
            target.Add((byte)value.Length);
            target.AddRange(value);
        }
    }

    public string Sign(byte[] payload)
    {
        var signature = _ecdsa.SignData(
            payload,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence);

        var prefixed = new byte[5 + signature.Length];
        prefixed[0] = 0x01;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(prefixed.AsSpan(1), keyId);
        signature.CopyTo(prefixed, 5);

        return Convert.ToBase64String(prefixed);
    }

    public void Dispose() => _ecdsa.Dispose();
}

internal sealed class StubHandler : HttpMessageHandler
{
    public string? LastUrl { get; private set; }

    public string? LastOperationId { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastUrl = request.RequestUri?.PathAndQuery;
        LastOperationId = request.GetHealthDataOperation()?.Id;

        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"dataPoints":[{"name":"users/me/dataTypes/heart-rate/dataPoints/1"}]}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        });
    }
}
