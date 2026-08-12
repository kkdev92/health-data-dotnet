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

## [Unreleased]

Nothing yet.

## [0.1.0-alpha] - unreleased

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

[Unreleased]: https://github.com/kkdev92/health-data-dotnet/compare/v0.1.0-alpha...HEAD
[0.1.0-alpha]: https://github.com/kkdev92/health-data-dotnet/releases/tag/v0.1.0-alpha
