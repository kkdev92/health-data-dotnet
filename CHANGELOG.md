# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
From 1.0.0 onward this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
Pre-1.0 releases follow it in spirit; their breaking changes are marked **Breaking**.

The Google Health API version and the package version are **independent axes**. A new Google API
version does not by itself cause a major bump — see
[docs/compatibility.md](docs/compatibility.md).

Each release records the Discovery revision it was generated from, because that is what actually
determines the wire contract.

Dates are UTC, taken from when the packages went to nuget.org.

## [Unreleased]

### Notes

- **The eight project operations have now been run against the live service**, so every operation
  in the contract has been. Seven of the eight succeed; `subscriptions.patch` is answered
  `400 INVALID_ARGUMENT` with no details for every body tried, including an empty one, and the
  same is true of patching a subscriber's `subscriberConfigs` — patching its `endpointUri` works.
  What they turned up is documented rather than worked around:

  - **A token cannot carry both scope families.** A grant that includes `cloud-platform` beside the
    `googlehealth.*` scopes is refused by every user-facing operation with
    `403 DISALLOWED_OAUTH_SCOPES`. The two credential contexts this SDK has always modelled are not
    a tidiness preference; the service enforces them. See
    [docs/authentication.md](docs/authentication.md#two-credential-contexts-not-one).
  - **`ProjectName` must be the project number.** The project id string is answered
    `403 PERMISSION_DENIED`, and the pattern cannot tell them apart.
  - **`Subscription.User` is `users/{healthUserId}`**, not the bare id `getIdentity` returns.
  - **Two more reasons that are not in the catalogue**: `IAM_PERMISSION_DENIED` when the scope is
    right and the caller has no role on the project, joining
    `ACCESS_TOKEN_SCOPE_INSUFFICIENT`. Both are refused before the Health service sees the request,
    which is why neither is in its error list. See
    [docs/runtime.md](docs/runtime.md#a-missing-scope-is-refused-in-two-places-and-only-one-of-them-is-in-the-catalogue).

- **Webhook delivery is verified end to end.** Creating a subscriber makes Google send the two
  verification challenges to the endpoint, and the receiver in `Kkdev92.HealthData.Webhooks`
  answered them `201` and `401` as the guide requires. Updating the endpoint verifies again.

- **The numbers a release has to move now check each other.** `VersionPrefix`, the newest changelog
  entry and its date, the readme's status line, and `PackageValidationBaselineVersion` are four
  hand-kept values that are each only correct relative to the others, and nothing was comparing
  them — which is how the changelog said `unreleased` for a version that was on nuget.org, and how
  the readme's status line went on describing the release before the current one. Tests read what
  is already written down rather than asking the network, so getting one wrong fails the build
  instead of shipping.

## [0.2.1-alpha] - 2026-08-16

Generated from Google Health API `v4`, Discovery revision `20260805` — the same contract as
0.2.0-alpha, and the same public surface. **No code changed in this release.** What ships is the
readme on nuget.org, the documentation comments the package carries, and one thing about how the
package is built.

### Changed

- **The package-validation baseline is back on**, pointing at `0.2.0-alpha`, so a binary breaking
  change now fails the pack again. It was left out of 0.2.0-alpha because that release reshaped
  the whole surface deliberately; this is the release to restore it in, because the surface here is
  identical and the comparison came back clean without a single suppression. Restoring it during a
  release that does change the surface would mean separating real breaks from the noise of turning
  the validator on, at the same time.

- **`HealthDataErrorDetail.Metadata` no longer claims to name the missing scope.** It said the
  metadata answers "which scope", which was asserted rather than measured. What a real refusal
  carries is `service` and `method`, naming the RPC. The remark now says the metadata narrows the
  reason without promising any particular key.

### Notes

Everything below is about the service rather than about this package. None of it is fixable here,
which is why it is written down.

- **A missing scope is refused in two places, and only one of them is in the catalogue.**
  `MISSING_OAUTH_SCOPE` is the service's and is documented. A token carrying none of an
  operation's accepted scopes never reaches the service: Google's front end refuses it with
  `PERMISSION_DENIED` and the reason `ACCESS_TOKEN_SCOPE_INSUFFICIENT`, which is not in the
  catalogue — so it is not a constant on `HealthDataErrorReasons` and does not reach the exception
  message. It does reach `Reason`, which is unfiltered on purpose. The catch clause in
  [docs/runtime.md](docs/runtime.md#a-missing-scope-is-refused-in-two-places-and-only-one-of-them-is-in-the-catalogue)
  compares both; the one printed there before this release compared only the first and matched
  neither refusal in practice.

- **Five of the thirteen operations 0.2.0-alpha recorded as never run have now been run.** All of
  the writes: a data point created, patched and deleted in a real account, and the two profile
  patches sent with the values they had just read back. Seventeen of the twenty-five have now been
  exercised against the live service. The eight that remain are the project operations, which need
  `cloud-platform`. Nothing in the package changed — the note in 0.2.0-alpha was true when it
  shipped and is left as it was.

- **A name the service returns is refused as a `parent` by most of the data point collection.**
  `list` returns names carrying the numeric user id, and `list`, `reconcile`, `rollUp`,
  `dailyRollUp` and `batchDelete` answer `400 INVALID_ARGUMENT` to that form, while `create` and
  `pairedDevices.list` accept it. Build parents from `UserName.Me`; a name inside a request body is
  fine as it arrived. Measured, not derivable from Discovery, and not something this SDK can
  paper over — `users/{id}` is correct when a subscription names a user other than the caller. See
  [docs/data-points.md](docs/data-points.md#use-usernameme-for-a-parent-even-when-you-have-a-name-from-the-service).

- **Following the cursor can return fewer records than exist.** The service drops records that
  share a timestamp with the last record of a page: no error, no duplicate, the enumeration simply
  ends short. How exposed you are depends on how tied the timestamps are rather than on how small
  the page is, so a data type whose entries all carry the same instant each day is the one to
  watch. `EnumerateAsync` returns exactly what the same cursor returns when it is walked by hand —
  the same records in the same order — so there is nothing in this package to fix. See
  [docs/operations.md](docs/operations.md#following-the-cursor-can-return-fewer-records-than-exist).

## [0.2.0-alpha] - 2026-08-15

Generated from Google Health API `v4`, Discovery revision `20260805` — the same contract as
0.1.0-alpha. Everything here is a change to the shape of the SDK, not to what it talks to.

The reason for all of it is one thing: 0.1.0-alpha was built without a consumer, and then an
application was built on it. Eleven pieces of friction came back, and this release is those eleven
answered. **Every one of them is breaking**, which is what an alpha is for.

### Breaking

- **Resource names are types.** `Name` and `Parent` no longer take `string`. Each of the eleven
  name shapes in the contract is a generated type in `Kkdev92.HealthData.Names`, built from the
  regular expression Discovery already states for that parameter — which the generator was reading
  and rendering as a doc comment above a `string`.

  ```csharp
  // before: compiles, is sent, answers 400
  new GetPairedDevicesRequest { Name = "users/me/dataTypes/steps/dataPoints/abc" }

  // now
  new GetPairedDevicesRequest { Name = UserName.Me.PairedDevice("abc") }
  ```

  Build a name from its parts — `UserName.Me.DataType("steps").DataPoint(id)` — or parse one that
  came from a response with `PairedDeviceName.Parse(point.Name!)`. There is no implicit conversion
  in either direction, deliberately.

- **Models moved to `Kkdev92.HealthData.Models`, request envelopes to `.Requests`.** 249 of the
  core assembly's 270 public types were in one namespace; typing `Kkdev92.HealthData.` offered every
  measurement, request, response and enum at once.

- **Open enums are nested inside the model that declares them.** `SettingsDistanceUnit` is
  `Settings.Types.DistanceUnit`, protobuf style — the same shape anyone who has consumed a Google
  API from C# has seen. They are still not C# enums, and their documentation now says so:
  `Enum.TryParse` compiles against them and throws at run time. Use `FromValue`.

- **Requests are records**, so `with` covers "the same call, one page on". `WithPageToken` is public
  rather than internal for the same reason. Every request overrides `ToString` to return its type
  name: a record prints every property, and a request's properties are whose record it is and what
  is being asked of it.

- **`ScopeCombination` exists once.** The duplicate in `Kkdev92.HealthData.Authentication` is gone;
  `Kkdev92.HealthData.Http.HealthDataScopeCombination` is the one. `ScopeRequirement.For(descriptor)`
  replaces the two-arm switch every caller was writing.

- **`CreateAuthorizationUrl` takes `GoogleAuthorizationUrlOptions`** instead of six parameters, two
  of them `bool` — the pair that decides whether the grant comes back with a refresh token.

- **An empty field mask is refused**, by `GoogleFieldMask.Parse("")` and by the request builder. Its
  wire meaning is undefined — `field_mask.proto` says implementations differ — and it used to be
  silently converted into "no mask", which AIP-134 defines as "replace the fields present in the
  body". `Parse` also stopped dropping empty segments (`"a,,b"` was becoming two paths) and now
  rejects anything that is not a field path, `*` excepted.

- **A union carrying two measurements is refused when the request is written.** `DataPoint` is a
  name plus forty-two mutually exclusive members and nothing stopped setting two; the service
  refuses it and says which operation failed, not which pair. The message names both. Reading two
  still works — refusing a response would drop a person's data over a client-side rule.

### Added

- **`HealthDataScopes.ReadOnly` / `.WriteOnly` / `.Project` / `.All`.** An application that reads
  and gates writes had no way to ask which scopes are which, and the first one built on this SDK
  matched on the string `".writeonly"`. The classification is declared in `semantics.json`: deriving
  it from the HTTP method is wrong, because `rollUp`, `dailyRollUp` and `reconcile` are POSTs that
  read — measured, the method disagrees with the scope in 5 of 19 cases.

- **`HealthDataGeneratedDataTypes`** — all 43 data types with the operations Google documents for
  each, and the snake_case name filters are written against. None of this is in Discovery, which is
  why asking `steps` for a `get` answers `400 UNSUPPORTED_DATA_TYPE_ACTION`. It is metadata and the
  SDK never consults it before sending: `Find` returning null means "not in the table", not "not
  supported".

- **`AddHealthDataWebhooks()`** in the dependency-injection package, which fixes the lifetimes the
  webhook constructors do not state — the key provider holds the cache, so it is a singleton, and
  its `HttpClient` has to outlive a request. No receiver is registered when no secret is configured,
  because one without a secret answers 401 to Google's verification challenge.

- **`HealthDataErrorDetail.Metadata`**, typed. It holds which scope is missing when the reason only
  says `MISSING_OAUTH_SCOPE`, and reaching it meant walking `Raw`. Still out of exception messages.

- **`HealthDataWebhookReceiver.VerificationChallengeBody`** — the body was documented in prose and
  nowhere reachable, so every test that posted one wrote the literal again.

- **`EnumerateAsync` for `dataPoints.rollUp`.** It pages through its body rather than the query and
  was excluded for that reason alone; what decides whether enumeration is possible is the response,
  and its response returns a cursor. `dailyRollUp` is still the one operation without one — it
  accepts a page token and returns none — and now says so on the property.

### Fixed

- The packaged XML documentation no longer describes private members. 134 entries, private fields
  included, were being shipped; Microsoft's own guidance on the switch is that documenting private
  members "exposes the inner (potentially confidential) workings of your library", and there is no
  compiler option for it.

### Notes

- **The package-validation baseline is absent for this release**, and for this release only.
  Comparing this surface against 0.1.0-alpha would produce hundreds of intentional CP0002s. The
  build now refuses to go past 0.2.0 without one, so the exemption cannot be inherited.

- **Thirteen of the twenty-five operations have never been run against the live service.** Five are
  writes, which need a write scope and would change a real person's health record; eight are the
  project operations, which need `cloud-platform`. Both are refused before a request is built when
  the corresponding gate is off. The other twelve were exercised end to end against Google.

- Webhook signature verification has been tested against crafted requests, not against a real
  delivery from Google — that needs a subscriber, which needs `cloud-platform`.

## [0.1.0-alpha] - 2026-08-12

First public build. Generated from Google Health API `v4`, Discovery revision `20260805`.

### Added

- **`Kkdev92.HealthData`** — resource clients for 25 of the 27 operations Discovery declares, 138
  generated models, 58 open enums, 19 scope constants and 58 error reasons. Zero third-party
  runtime dependencies.
- **`Kkdev92.HealthData.Authentication`** — authorization URL, authorization-code and refresh
  exchange, PKCE (S256), scope model, and the token provider abstraction the client resolves per
  request. No token storage; that stays with the application.
- **`Kkdev92.HealthData.DependencyInjection`** — `AddHealthData()`, `IHttpClientFactory` wiring and
  handler composition. The only package that references `Microsoft.Extensions.*`.
- **`Kkdev92.HealthData.Webhooks`** — Tink-format ECDSA signature verification against the raw
  request bytes, keyset fetch and cache, and endpoint verification. No Tink dependency.
- Pagination helpers (`EnumerateAsync`) for every operation whose response carries a page token.
- Opt-in retry with a classification per operation, full jitter, and `Retry-After` taking priority.
- `ActivitySource` diagnostics under `Kkdev92.HealthData`, with a tag list that carries no
  identifier.
- `NOTICE`, packed into every package, recording the CC BY 4.0 attribution for documentation text
  reproduced from Google's Discovery document.

### Notes

- Targets `net10.0` only.
- Retry is **off** unless a retry handler is added. A client that silently re-sends writes is a
  liability on an API that stores health data.
- Google's errors catalogue defines `API_PRIVATE_PREVIEW_ACCESS_DENIED`, so calling the live API
  may require access to be granted on Google's side regardless of OAuth setup.
- **Verified against the live service:** the full OAuth authorization flow, and
  `dataPoints.list` reading back thousands of real points with pagination following the cursor.
  The remaining operations are checked against the committed specification and fixtures only.
- Two bugs were found by using it against real data rather than fixtures, and are fixed here:
  `DataPoint.GetKind()` resolving every real data point to `DataSource` — `dataSource` is metadata
  carried by every point, not one of the things a point can be — and token-endpoint failures being
  reported as a bare status with the RFC 6749 reason discarded, which made the seven-day refresh
  expiry indistinguishable from a malformed request. Both were invisible to fixtures.

[Unreleased]: https://github.com/kkdev92/health-data-dotnet/compare/v0.2.1-alpha...HEAD
[0.2.1-alpha]: https://github.com/kkdev92/health-data-dotnet/releases/tag/v0.2.1-alpha
[0.2.0-alpha]: https://github.com/kkdev92/health-data-dotnet/releases/tag/v0.2.0-alpha
[0.1.0-alpha]: https://github.com/kkdev92/health-data-dotnet/releases/tag/v0.1.0-alpha
