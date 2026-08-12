# ADR-0004: Version-neutral package identity

- Status: Accepted
- Date: 2026-08-09

## Context

The current public contract is v4, but naming the package after it would force a rename the
first time Google ships v5.

## Decision

The package is `Kkdev92.HealthData`, with no version in its identity. The Google Health API
version and the NuGet SemVer version are independent axes. The runtime does not special-case
`v4`; the versioned path segment belongs to the generated contract.

## Consequences

- A new Google API version does not by itself cause a NuGet major bump. Only a breaking change
  to *our* public API does.
- `HealthDataApiMetadata` carries only version-neutral values; the API version and revision
  are emitted by the generator from the specification snapshot.
- Internal namespaces such as `...Generated.V4` may coexist temporarily during a migration.
