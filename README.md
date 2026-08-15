# Kkdev92.HealthData

[![NuGet](https://img.shields.io/nuget/v/Kkdev92.HealthData)](https://www.nuget.org/packages/Kkdev92.HealthData)
[![CI](https://github.com/kkdev92/health-data-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/kkdev92/health-data-dotnet/actions)
[![OpenSSF Best Practices](https://www.bestpractices.dev/projects/14038/badge)](https://www.bestpractices.dev/projects/14038)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/)

An unofficial .NET SDK for the Google Health API. The wire contract is taken in
as a committed specification snapshot and turned into C# by a deterministic
offline generator, so the client and the API cannot quietly drift apart.
_Built for applications that read or write a person's health data and would
rather the SDK be boring about it._

> **Status:** `0.1.0-alpha`. The client is complete and covered by tests, and the
> OAuth flow and `dataPoints.list` have been exercised against the live service —
> a full authorization, and thousands of real data points read back with
> pagination following the cursor. Most other operations have only been run
> against fixtures.
>
> Expect the rough edges of a first release. Two bugs found by actually using it
> are fixed and regression-tested: `GetKind()` mis-resolving every real
> `DataPoint`, and a rejected token request arriving with no diagnosis at all —
> the code Google sent now reaches `ErrorCode`, and the server's own wording
> reaches `Error`, deliberately not the exception message. Both bugs were
> invisible to fixtures, which is a fair warning about what else the fixtures may
> be hiding.

---

## Table of Contents

- [Features](#features)
- [Installation](#installation)
- [Quick Start](#quick-start)
- [Why Kkdev92.HealthData](#why-kkdev92healthdata)
- [Usage](#usage)
- [What Is Guaranteed](#what-is-guaranteed)
- [Known Limitations](#known-limitations)
- [How It Works](#how-it-works)
- [Platform Requirements](#platform-requirements)
- [Security and Privacy](#security-and-privacy)
- [Documentation](#documentation)
- [Contributing](#contributing)
- [Support & Maintenance Policy](#support--maintenance-policy)
- [License](#license)
- [Acknowledgments](#acknowledgments)

---

## Features

- **Generated From a Committed Snapshot**: 25 operations, 138 models and 58 open enums are emitted from `spec/v4`, and CI compares the checked-in sources byte for byte
- **Zero Third-Party Runtime Dependencies**: the core, authentication and webhook packages resolve to the BCL and nothing else
- **Reflection-Free Serialization**: `System.Text.Json` source generation only, with reflection disabled, so a missing contract fails loudly instead of at run time under AOT
- **Native AOT and Trimming**: a real consumer application is published with `PublishAot=true` in CI, because a library that merely builds proves nothing
- **The Wire Contract Is Never Reshaped**: `pageSize` stays `pageSize`; scopes, JSON names and path templates are exactly Google's
- **Unknown Values Survive**: an enum member or a union member added after this contract was generated round-trips intact rather than throwing
- **Safe to Share Across Users**: the client owns no credentials; a delegating handler resolves a token per request from the operation descriptor
- **Privacy by Default**: a generated model holds the data you asked for, and its `ToString()` does not print it; no exception message and no SDK-owned trace carries a payload, a token or a user identifier. Tests enforce those. Two things sit outside that boundary and are yours: `HttpClient`'s own instrumentation records request URIs, and `HealthDataRequestBuilder.Build()` returns an expanded one because that is its job — [SECURITY.md](SECURITY.md) has both

---

## Installation

```bash
# --prerelease, because 0.1.0-alpha is the only version so far and the CLI does not
# consider pre-release versions unless asked.
dotnet add package Kkdev92.HealthData --prerelease

# The Quick Start below also uses the authentication package.
dotnet add package Kkdev92.HealthData.Authentication --prerelease
```

| Package | Purpose |
| --- | --- |
| `Kkdev92.HealthData` | REST client, generated contract, serialization, errors, pagination |
| `Kkdev92.HealthData.Authentication` | OAuth helpers and the per-request token provider |
| `Kkdev92.HealthData.DependencyInjection` | `IServiceCollection` and `IHttpClientFactory` wiring |
| `Kkdev92.HealthData.Webhooks` | Webhook signature verification and receiver helpers |

Take only what you need. The core package has no dependency on the other three.

---

## Quick Start

```csharp
using Kkdev92.HealthData;
using Kkdev92.HealthData.Authentication;
using Kkdev92.HealthData.Requests;

// The client holds no credentials. A delegating handler resolves a token per request from the
// operation descriptor, which is what makes one client safe to share across users.
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
    new GetProfileRequest { Name = "users/me/profile" },
    cancellationToken);
```

With dependency injection:

```csharp
services.AddHealthData();

// Scoped, so the delegate reads whatever per-request context the application already has.
// request carries the operation id, its scopes, and whether it needs project credentials.
services.AddHealthDataAccessToken(async (provider, request, cancellationToken) =>
    request.RequiresProjectCredentials
        ? await projectCredentials.GetAsync(cancellationToken)
        : await userTokens.GetAsync(provider.GetRequiredService<ICurrentUser>().Id, cancellationToken));
```

No token provider is registered by default. Resolving `HealthDataClient` without one throws while
the client is being composed, which points at the mistake instead of surfacing later as a 401.

---

## Why Kkdev92.HealthData

Google publishes a .NET client for this API, generated from the same Discovery document this
package reads. It works. The reason this exists is not that it does not.

A generated client that targets many frameworks at once has to hold its runtime still: reflection
stays available because something might need it, the JSON layer stays general because it serves
every Google API, and the `HttpClient` pipeline belongs to a shared runtime rather than to your
application. Those are reasonable choices for a client library covering hundreds of services. They
are the wrong ones if what you want is a `net10.0` package that publishes under Native AOT with no
third-party dependencies.

So the split here is deliberate. **Google decides the wire contract; this package decides the
.NET shape.** Endpoints, query names, JSON names and scopes come from a committed snapshot and are
reproduced exactly. Everything above that line — the serializer, the retry policy, the pagination
helper, the error abstraction, the diagnostics, the dependency count — is chosen for modern .NET
and owned here.

- The contract is a file you can read, diff and review, not a call made during your build
- Regenerating from that file must reproduce the checked-in C# byte for byte, on Linux and Windows
- An operation is not public because Discovery contains it; an allowlist decides, and a new one
  fails the build until somebody classifies it
- Where Google's own documents disagree with each other, the conflict is recorded and pinned by a
  test rather than silently resolved

---

## Usage

### Read a page, or stream across pages

```csharp
var page = await client.Users.DataPoints.ListAsync(
    new ListDataPointsRequest
    {
        Parent = "users/me/dataTypes/heart-rate",
        // The resource name is kebab-case; the filter prefix is the type's snake_case filter
        // name, and heart rate is sample-timed rather than interval-timed.
        Filter = "heart_rate.sample_time.physical_time >= \"2026-08-01T00:00:00Z\"",
        PageSize = 1000,
    },
    cancellationToken);
```

Pages are fetched lazily, so stopping early costs nothing:

```csharp
await foreach (var point in client.Users.DataPoints.EnumerateAsync(
    new ListDataPointsRequest { Parent = "users/me/dataTypes/heart-rate" },
    cancellationToken))
{
    if (point.HeartRate is { BeatsPerMinute: { } bpm })
    {
        Console.WriteLine(bpm);
    }
}
```

### Work with the `DataPoint` union

A data point carries exactly one of 42 measurement members, so a generated helper tells you which:

```csharp
var value = point.GetKind() switch
{
    DataPointKind.HeartRate => $"{point.HeartRate!.BeatsPerMinute} bpm",
    DataPointKind.Steps     => $"{point.Steps!.Count} steps",
    DataPointKind.Unknown   => "(a member added after this contract was generated)",
    _                       => "(other)",
};
```

### Download a TCX export as a stream

```csharp
await using var file = File.Create("exercise.tcx");

await client.Users.DataPoints.ExportExerciseTcxAsync(
    new ExportExerciseTcxRequest { Name = exerciseResourceName },
    file,
    cancellationToken);
```

### Handle errors

```csharp
catch (HealthDataApiException ex) when (ex.Reason == HealthDataErrorReasons.MissingOauthScope)
{
    // ex.Message carries the operation id, status and reason. It never carries the payload.
}
catch (HealthDataApiException ex) when (ex.IsRateLimited)
{
    await Task.Delay(ex.RetryAfter ?? TimeSpan.FromSeconds(30), cancellationToken);
}
```

### Receive a webhook

```csharp
// The raw bytes, before any model binding. Re-serializing changes whitespace and key order,
// and the signature will never match.
var result = await receiver.HandleAsync(rawBody, signatureHeader, authorizationHeader, ct);

if (result.Kind is WebhookRequestKind.Notification)
{
    await queue.EnqueueAsync(result.Notification!, ct);
}

return Results.StatusCode((int)result.StatusCode);
```

---

## What Is Guaranteed

- **The checked-in generated sources match the specification.** `codegen verify` regenerates and
  compares byte for byte, on both Linux and Windows, so a hand-edit or a stale checkout fails CI
- **The public surface does not move by accident.** An approved API snapshot covers all four
  shipping assemblies; any addition, removal or rename shows up as a diff a reviewer has to accept
- **Unknown enum and union members round-trip.** An unknown enum value is the string it arrived
  as, so it costs nothing; an unknown union member is held in a dictionary and costs an
  allocation, which is the price of not deleting it from someone's record
- **Nothing is retried unless you ask.** Retry is opt-in, and even then a write is never resent
- **Nothing identifying reaches diagnostics.** The activity tag list is short by design and has no
  URL tag at all, because a resource name embeds both the user and the data type
- **A published package contains only what it should.** Its entries are asserted against an
  allowlist, and scanned — binaries included — for local paths

---

## Known Limitations

- **Mostly unexercised against the live service.** The OAuth flow and `dataPoints.list` have run
  against Google and returned real data; everything else is verified against the committed
  specification and Google's published documentation only
- **Access may be gated.** `API_PRIVATE_PREVIEW_ACCESS_DENIED` exists in the errors catalogue
- **`net10.0` only.** There is no multi-targeting and none is planned
- **Two operations are not exposed.** The SMART Health Links pair is excluded on purpose; see
  [`docs/operations.md`](docs/operations.md)
- **No long-running operation polling.** Google's API declares no operations resource, so this
  package does not invent one that would have to guess a URL
- **No token storage.** Persistence, encryption and key management stay with your application
- **`dataPoints.dailyRollUp` has no enumeration helper.** Its request accepts a page token but its
  response returns none, so there is no cursor to follow

---

## How It Works

```text
fetch official Discovery          network, human-initiated, never during a build
        v
canonicalized snapshot in spec/   keys sorted, so a diff shows contract changes only
        v
commit and review                 the contract is a reviewable file
        v
offline generator                 allowlist, semantic overrides, reachability pruning
        v
generated C# (committed)          237 files, compared byte for byte in CI
        v
handwritten runtime               transport, serialization, errors, pagination, diagnostics
```

The Discovery endpoint returns the same document with randomized object key order on every
request — four consecutive fetches produced four different SHA-256 values at an identical byte
length. The snapshot is therefore stored canonicalized, which is what makes its recorded hash
meaningful and keeps a contract update readable as a diff.

Full detail is in [`docs/code-generation.md`](docs/code-generation.md).

---

## Platform Requirements

|  |  |
| --- | --- |
| .NET | `net10.0` — single target, no multi-targeting |
| Language | C# 14; `LangVersion` is never `latest` or `preview` |
| Runtime dependencies | none in `Kkdev92.HealthData`, `.Authentication` and `.Webhooks`; `Microsoft.Extensions.*` in `.DependencyInjection` only |
| Native AOT | supported and exercised in CI on a real consumer application |
| Trimming | `IsAotCompatible`; the trim and AOT analyzers report no IL warnings |
| SDK (to build) | the floor in `global.json`; `rollForward: latestFeature` takes a newer one |
| Google Health API | `v4`, Discovery revision `20260805`, snapshot verified 2026-08-09 |

The Google Health API version and this package's version are **independent axes**. A new Google
API version does not by itself cause a major bump.

---

## Security and Privacy

- **No Telemetry**: the packages collect no usage data, and open no connection that a call of
  yours does not map to. They do make network requests — an API client is nothing else — but only
  these: the API request itself, Google's token endpoint when you call `GoogleOAuthClient`, and
  Google's published keyset when you verify a webhook signature. Nothing goes anywhere on a timer
- **Credentials Only Go to Google**: an access token is attached only for
  `health.googleapis.com` or a loopback address, and `GoogleOAuthClient` refuses a token endpoint
  that is neither Google's nor loopback. HTTPS alone is not treated as sufficient, because it
  bounds who can read a credential in transit and not who receives it. Proxies and emulators are
  still supported — through `AdditionalTrustedOrigins` and `AllowCustomCredentialEndpoints`, so
  that trusting another host is something someone chose
- **No Credentials of Their Own**: `HealthDataClient` holds none — a delegating handler resolves a
  token per request — and nothing anywhere is written to disk. `GoogleOAuthClient` is the exception
  worth naming: it keeps the client secret you gave it in memory for as long as you keep the
  client, because it has to send it to the token endpoint
- **Payloads Stay Out of Diagnostics**: exception messages carry the operation id, HTTP status and
  machine-readable reason, and nothing else — the parsed envelope stays on `Error` for callers
  with somewhere safe to put it
- **Models Do Not Print Themselves**: they are classes rather than records precisely so that
  interpolating one does not spell out a person's measurements
- **Webhook Payloads Are Untrusted Input**: signatures are verified against the raw received
  bytes before parsing, and a notification is only ever populated after that succeeds
- **Bounded Reads**: an error body is read under a byte limit rather than buffered freely

Using this SDK does not by itself make an application compliant with Google's
[Health API Developer and User Data Policy](https://developers.google.com/health/policies/health-api-developer-user-data-policy).
Privacy policy, scope minimisation, encryption at rest and key management remain your
responsibility.

For vulnerability reporting, see [SECURITY.md](SECURITY.md).

---

## Documentation

|  |  |
| --- | --- |
| [Architecture](docs/architecture.md) | Design intent, package boundaries, rules that must not regress |
| [Operations](docs/operations.md) | Every exposed operation, with scope, retry class and pagination |
| [Data points](docs/data-points.md) | The measurement union, timestamps, filters, roll-ups, data types |
| [Runtime behaviour](docs/runtime.md) | Errors, retry, long-running operations, diagnostics, AOT |
| [Authentication](docs/authentication.md) | OAuth, project credentials, token providers, scopes |
| [Webhooks](docs/webhooks.md) | Signature verification, endpoint challenges, notifications |
| [Compatibility](docs/compatibility.md) | Versioning policy and how a contract change is caught |
| [Code generation](docs/code-generation.md) | Spec files, generator pipeline, determinism |
| [Decision records](docs/adr/README.md) | Decisions that are expensive to reverse |
| [Primary sources](docs/references.md) | Where every factual claim in this repository comes from |

The index, with a reading order for each kind of reader, is in [`docs/`](docs/README.md).

---

## Contributing

```bash
dotnet restore HealthData.slnx
dotnet build   HealthData.slnx -c Release
dotnet test    HealthData.slnx -c Release --filter "Category!=Integration&Category!=Package"
```

Read [CONTRIBUTING.md](CONTRIBUTING.md) first — particularly the rules that are not negotiable,
and the note on never hand-editing anything under `Generated/`.

Helpful things when reporting bugs:

- The package and version, `dotnet --version`, and whether you publish with Native AOT
- The exception type, `ex.Message`, `OperationId` and the HTTP status — the message is filtered,
  which is why it is the one to paste
- `Reason` only when it looks like `SOMETHING_LIKE_THIS`. It is unfiltered on purpose, so anything
  else in it arrived off the wire as sent
- Whether it fails while composing the client, while building the request, or on the response

**Never paste health data, tokens or resource names into an issue.** A resource name embeds both
the user and the data type. A redacted reproduction is always enough.

The same goes for your own OAuth client id and client secret, and for a webhook endpoint secret.
Those identify a project rather than a person, so they do not look like personal data and are the
easiest thing to paste without noticing — out of an authorization URL or an `appsettings` fragment.
An issue is public and stays that way.

---

## Support & Maintenance Policy

This is a personal project maintained in spare time. It is active, but support is best-effort:
I'll do my best to review issues and PRs, and releases may be a bit slow sometimes — thank you for
your patience.

The `0.x` line is pre-release. Breaking changes are expected before `1.0.0` and are listed in the
[CHANGELOG](CHANGELOG.md). From `1.0.0` onward the public API follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Really appreciate you using it 💛

---

## License

The original code is [MIT](LICENSE).

What is generated from Google's Health API Discovery document is not: the doc comments reproduce
Google's wording, and the contract shape derives from the same document. That material is Google's
under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/), and the attribution is
[NOTICE](NOTICE), which ships inside every package — keep it with anything you redistribute.
[docs/references.md](docs/references.md#licensing-of-the-material-used) records where the
permission comes from and which alternative readings were rejected.

---

## Acknowledgments

- Built against the [Google Health API](https://developers.google.com/health); this is a
  third-party project, not affiliated with, endorsed by or sponsored by Google. "Google" and
  "Google Health" are trademarks of Google LLC, used here only to identify the API this software
  interoperates with
- Google's own client for this API is
  [`Google.Apis.GoogleHealthAPI.v4`](https://www.nuget.org/packages/Google.Apis.GoogleHealthAPI.v4)
- Webhook signatures follow [Tink](https://github.com/tink-crypto/tink-java)'s format, verified
  against its source, though no Tink dependency is taken
- Tested with [xUnit](https://xunit.net/) and measured with
  [BenchmarkDotNet](https://benchmarkdotnet.org/)
