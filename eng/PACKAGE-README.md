# Kkdev92.HealthData

An **unofficial** .NET 10 SDK for the [Google Health API](https://developers.google.com/health),
generated from a committed Discovery snapshot by a deterministic offline code generator.

> **Not affiliated with, endorsed by, or supported by Google.**
> "Google" and "Google Health" are trademarks of Google LLC, used here only to identify the API
> this package interoperates with. Google's own client for this API is
> [`Google.Apis.GoogleHealthAPI.v4`](https://www.nuget.org/packages/Google.Apis.GoogleHealthAPI.v4).

> **Pre-release.** Every operation in the contract has been run against the live service and 24 of
> the 25 answered, including all five writes and the subscriber operations under their own
> `cloud-platform` credential. `subscriptions.patch` is the exception: the service refuses it with
> a bare `400` whatever it is sent. Google's error catalogue defines
> `API_PRIVATE_PREVIEW_ACCESS_DENIED`, so calling the real API may require access to be granted on
> Google's side regardless of your OAuth setup.
>
> Two things worth knowing before you hit them. `list` returns resource names carrying the numeric
> user id, and five of the six data point collection operations refuse that form as a `Parent` —
> build parents from `UserName.Me`. And a token cannot carry both scope families: adding
> `cloud-platform` to an end user's grant stops every user-facing operation working. A name inside
> a request body is fine as it arrived; it is only the parent that has to be rebuilt.

## Why this exists

- `net10.0` and C# 14, targeted directly
- **Zero third-party runtime dependencies** in the core package
- `System.Text.Json` source generation only — reflection is disabled, so a missing contract fails
  loudly instead of breaking under Native AOT
- Native AOT and trimming treated as a requirement, not an afterthought
- The wire contract is never reshaped: query names, JSON names and scopes are exactly Google's

## Install

```bash
# --prerelease, because every version so far is one and the CLI does not consider pre-release
# versions unless asked. Take only the packages you need; the table says which.
dotnet add package Kkdev92.HealthData --prerelease
dotnet add package Kkdev92.HealthData.Authentication --prerelease
```

This readme ships in all four packages, so the snippets below name the package each type comes
from rather than assuming you installed everything.

| Package | Purpose |
|---|---|
| `Kkdev92.HealthData` | REST client, generated contract, serialization, errors, pagination |
| `Kkdev92.HealthData.Authentication` | OAuth helpers and the per-request token provider |
| `Kkdev92.HealthData.DependencyInjection` | `IServiceCollection` and `IHttpClientFactory` wiring |
| `Kkdev92.HealthData.Webhooks` | Webhook signature verification and receiver helpers |

## Getting started

The client holds no credentials. A delegating handler resolves a token per request from the
operation descriptor, which is what makes one client safe to share across users in a server.

```csharp
using Kkdev92.HealthData;
using Kkdev92.HealthData.Authentication;
using Kkdev92.HealthData.Names;
using Kkdev92.HealthData.Requests;

var authorization = new HealthDataAuthorizationHandler(new StaticAccessTokenProvider(accessToken))
{
    InnerHandler = new HttpClientHandler(),
};

using var httpClient = new HttpClient(authorization)
{
    BaseAddress = HealthDataApiMetadata.DefaultBaseAddress,
};

var client = new HealthDataClient(httpClient);

var profile = await client.Users.GetProfileAsync(
    new GetProfileRequest { Name = UserName.Me.Profile },
    cancellationToken);
```

Stream across pages — they are fetched lazily, so stopping early costs nothing:

```csharp
await foreach (var point in client.Users.DataPoints.EnumerateAsync(
    new ListDataPointsRequest { Parent = UserName.Me.DataType("heart-rate") },
    cancellationToken))
{
    if (point.HeartRate is { BeatsPerMinute: { } bpm })
    {
        Console.WriteLine(bpm);
    }
}
```

`DataPoint` is a union of 42 measurement members, so a generated helper tells you which one is set:

```csharp
var value = point.GetKind() switch
{
    DataPointKind.HeartRate => $"{point.HeartRate!.BeatsPerMinute} bpm",
    DataPointKind.Steps     => $"{point.Steps!.Count} steps",
    DataPointKind.Unknown   => "(a member added after this contract was generated)",
    _                       => "(other)",
};
```

## Contract baseline

| | |
|---|---|
| Google Health API version | `v4` |
| Discovery revision | `20260826` |
| Operations exposed | 25 of 27 |

The Google Health API version and this package's version are **independent axes**. A new Google
API version does not by itself cause a major bump.

## Privacy

This SDK handles health data. A generated model holds what you asked the API for — that is what it
is — and printing one does not print its contents: `ToString()` is not overridden to dump
properties. No exception message and no activity this SDK starts carries a request body, a response
body, an access token, a refresh token, a user identifier or a webhook payload.

Exception messages carry the operation id, the HTTP status and a machine-readable reason, and
nothing else. Both exception types take that from a fixed list rather than from the shape of what
arrived: a secret is spelled like an identifier, so only a list of what the service actually says
can tell them apart.

Two things sit outside that boundary and are yours to handle. `HttpClient`'s own instrumentation
records request URIs, which for this API embed the user and the data type — turn it off for this
client or redact it. And `HealthDataRequestBuilder.Build()` returns the expanded path and query,
because that is what it is for.

`HealthDataClient` holds no credentials of its own, and nothing here writes one to disk.
`GoogleOAuthClient` keeps the client secret you give it in memory for as long as you keep the
client, because it has to send it to the token endpoint.

Using this SDK does not by itself make an application compliant with Google's
[Health API Developer and User Data Policy](https://developers.google.com/health/policies/health-api-developer-user-data-policy).
Privacy policy, scope minimisation, encryption at rest and key management remain the consuming
application's responsibility.

## Documentation

| | |
|---|---|
| [Architecture](https://github.com/kkdev92/health-data-dotnet/blob/main/docs/architecture.md) | Design intent, package boundaries, rules that must not regress |
| [Operations](https://github.com/kkdev92/health-data-dotnet/blob/main/docs/operations.md) | Every exposed operation, with scope, retry class and pagination |
| [Data points](https://github.com/kkdev92/health-data-dotnet/blob/main/docs/data-points.md) | The measurement union, timestamps, filters, roll-ups, data types |
| [Runtime behaviour](https://github.com/kkdev92/health-data-dotnet/blob/main/docs/runtime.md) | Errors, retry, diagnostics, Native AOT |
| [Authentication](https://github.com/kkdev92/health-data-dotnet/blob/main/docs/authentication.md) | OAuth, project credentials, token providers, scopes |
| [Webhooks](https://github.com/kkdev92/health-data-dotnet/blob/main/docs/webhooks.md) | Signature verification, endpoint challenges, notifications |
| [Compatibility](https://github.com/kkdev92/health-data-dotnet/blob/main/docs/compatibility.md) | Versioning policy and how a contract change is caught |
| [Changelog](https://github.com/kkdev92/health-data-dotnet/blob/main/CHANGELOG.md) | What changed, and against which Discovery revision |

Source, issues and discussion: <https://github.com/kkdev92/health-data-dotnet>

Author and other projects: <https://kkdev92.dev/>

## License

The original code is MIT, which is what the package metadata says.

Not all of what ships is original. This SDK is generated from Google's Health API Discovery
document: the `description` fields in it become the IntelliSense documentation in
`lib/net10.0/*.xml`, and the contract itself — the type and member names — derives from the same
document. That wording is Google's, published under
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) on the API reference pages, and CC BY
carries an attribution requirement.

The packaged `NOTICE` file is that attribution, so keep it with the package: redistributing the
package, or the assemblies and documentation taken out of it, means carrying `NOTICE` along.
Every package contains one, and a test fails if one does not.
