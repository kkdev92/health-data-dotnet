# Primary sources

Every factual claim in this repository is meant to be traceable to one of these. If you find a
statement in the docs or a comment in the code that you cannot check against a source below,
that is a bug worth reporting.

The ranking that decides which one wins when they disagree is in
[architecture.md](architecture.md#which-google-source-wins).

## Google Health API

| Source | URL |
|---|---|
| REST reference | <https://developers.google.com/health/reference/rest> |
| Discovery document | <https://health.googleapis.com/$discovery/rest?version=v4> |
| Data types | <https://developers.google.com/health/data-types> |
| Endpoints | <https://developers.google.com/health/endpoints> |
| OAuth scopes | <https://developers.google.com/health/scopes> |
| Setup and consent | <https://developers.google.com/health/setup> |
| Error catalogue | <https://developers.google.com/health/reference/rest/v4/errors> |
| Webhooks | <https://developers.google.com/health/webhooks> |
| Rate limits | <https://developers.google.com/health/rate-limits> |
| Release notes | <https://developers.google.com/health/release-notes> |
| Developer and user data policy | <https://developers.google.com/health/policies/health-api-developer-user-data-policy> |

Three of these are pinned in the repository with a verification date, because a committed
specification file was derived from them:

| Spec file | Derived from | Verified |
|---|---|---|
| `spec/v4/discovery.json` | The Discovery endpoint | Revision `20260805`, SHA-256 recorded in `metadata.json` |
| `spec/v4/data-types.json` | The Data types page | 2026-08-09 |
| `spec/v4/errors.json` | The error catalogue | 2026-08-09 |

`public-surface.json` and `semantics.json` have no single upstream source: they are this SDK's
decisions, and each entry carries its own provenance note.

## Google API design

| Source | URL | Used for |
|---|---|---|
| AIP-160, filtering | <https://google.aip.dev/160> | The `filter` expression syntax on list calls |
| AIP-193, errors | <https://google.aip.dev/193> | The error envelope shape, including `ErrorInfo.reason` |
| Discovery type and format | <https://developers.google.com/discovery/v1/type-format> | The type and format mapping in [ADR-0008](adr/0008-explicit-google-wire-primitives.md) |
| `google/api/http.proto` | <https://github.com/googleapis/googleapis/blob/master/google/api/http.proto> | Path template escaping for `{var}` and `{+var}` |
| Google API error model | <https://cloud.google.com/apis/design/errors> | The canonical status codes |

## Other implementations, read for comparison rather than authority

| Source | URL |
|---|---|
| Google API .NET client | <https://github.com/googleapis/google-api-dotnet-client> |
| Tink | <https://github.com/tink-crypto/tink-java> |

Google's own .NET client is the **lowest-ranked** source here, and one specific behaviour was
verified against it and then deliberately not copied: it skips percent-encoding entirely for
`{+var}`, which produces a malformed request for a resource id containing a reserved character.
See [code-generation.md](code-generation.md#path-template-escaping).

Tink is read to establish what the webhook signature actually covers — specifically that the
`0x00` message suffix applies only to the `LEGACY` output prefix, not to the `TINK` keys Google
publishes. No Tink dependency is taken; see [webhooks.md](webhooks.md#wire-format).

## .NET

| Source | URL |
|---|---|
| Native AOT deployment | <https://learn.microsoft.com/dotnet/core/deploying/native-aot/> |
| `System.Text.Json` source generation | <https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/source-generation> |
| `IHttpClientFactory` | <https://learn.microsoft.com/dotnet/core/extensions/httpclient-factory> |
| `HttpCompletionOption` | <https://learn.microsoft.com/dotnet/api/system.net.http.httpcompletionoption> |
| C# language versioning | <https://learn.microsoft.com/dotnet/csharp/language-reference/language-versioning> |
| Package validation | <https://learn.microsoft.com/dotnet/fundamentals/apicompat/package-validation/overview> |
| Central package management | <https://learn.microsoft.com/nuget/consume-packages/central-package-management> |

## Sources outside Google

The table above is Google's documentation, which is where the contract comes from. Several
decisions rest on specifications instead, and those are cited where the decision is made rather
than listed here — the RFC number is in the comment or the doc comment that relies on it:

| | Where it is relied on |
|---|---|
| RFC 6749, RFC 8628 (OAuth 2.0, device flow) | `GoogleOAuthError`, `GoogleOAuthException` |
| RFC 7636 (PKCE) | `PkceCodeChallenge` |
| RFC 9110 (`Retry-After`) | `HealthDataRetryHandler` |
| Semantic Versioning 2.0.0 | `release.yml`, [compatibility.md](compatibility.md) |
| Google's OAuth 2.0 best practices | [authentication.md](authentication.md) |
| `IHttpClientFactory` guidance | [webhooks.md](webhooks.md), [ADR-0007](adr/0007-authentication-via-request-pipeline.md) |

## Licensing of the material used

Google Developers documentation is licensed under
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/), which permits reuse and modification
with attribution. This repository relies on that permission in two places:

| Where | What is reproduced |
|---|---|
| `spec/v4/discovery.json` | The Discovery document, verbatim apart from canonicalization |
| `Generated/**/*.g.cs` and the shipped XML documentation | Doc comments generated from that document's `description` fields, so Google's wording is reproduced |

### Where that permission actually comes from

Worth being precise, because **the Discovery document carries no license of its own**: it has no
`license` field, and the endpoint sends no such header. What settles it is that the text is the
same text. The `description` on `Altitude`, for instance — "Captures the altitude gain (i.e.
deltas), and not level above sea, for a user in millimeters." — appears verbatim on
<https://developers.google.com/health/reference/rest/v4/users.dataTypes.dataPoints>, and that page
carries the CC BY 4.0 notice. Checked 2026-08-12.

### An alternative that was considered and rejected

The same Discovery document is cached in eight Google client-library repositories. Seven are
Apache-2.0; the Go client is BSD-3-Clause. Reading the repository license as the document's
license would make one file two licenses at once, so it cannot be the right reading — and Google
describes those copies as a "local cache of Discovery docs from the API Discovery Service" rather
than as work of its own. Picking the single BSD-3-Clause outlier and calling it the provenance
would be choosing an answer rather than following one.

The attribution here therefore follows the text to the page that licenses it, not a repository
that happens to hold a copy.

Attribution is carried in [`NOTICE`](../NOTICE), which is packed into every NuGet package, and in
the provenance header of every generated file. Google's trademarks are excluded from the CC BY
license and are used descriptively only.

## What is not a source

Sample code in a guide is not a contract. Neither is a screenshot, a blog post, or the shape of a
response someone observed once. Where documents alone cannot settle a question — whether
`Operation.done` is ever `false`, for instance — it is recorded as needing an integration test
rather than resolved by assumption.
