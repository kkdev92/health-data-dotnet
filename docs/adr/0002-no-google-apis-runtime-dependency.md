# ADR-0002: No Google.Apis runtime dependency

- Status: Accepted
- Date: 2026-08-09

## Context

`Google.Apis.GoogleHealthAPI.v4` exists and targets `net6.0`, `netstandard2.0` and
`net462`, so it *can* be consumed from a .NET 10 application. "It does not work on .NET 10" would
be a false justification.

## Decision

The core package takes no runtime dependency on `Google.Apis.*`. The official SDK may be used
as an implementation reference and as a benchmark comparison.

## Consequences

- We control the JSON strategy, the `HttpClient` pipeline, AOT posture, allocation behaviour,
  unknown-value handling, and the diagnostics policy.
- We also own the maintenance cost of tracking contract changes, which is why the code
  generator and `spec-check` workflow are not optional extras.
- `Kkdev92.HealthData` targets zero third-party runtime dependencies.
