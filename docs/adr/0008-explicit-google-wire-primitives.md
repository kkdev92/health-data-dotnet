# ADR-0008: Explicit Google wire primitives

- Status: Accepted
- Date: 2026-08-09

## Context

Discovery revision 20260826 uses `google-datetime` in 29 places, `google-duration` in 28, and
`int64` in 36 (always transmitted as a JSON string). Civil time appears as a `CivilDateTime`
message, and `Date` as a year/month/day message.

Mapping these onto `DateTime`, `TimeSpan`, and `long` directly would lose information or
silently mis-serialize.

## Decision

Handwritten primitives exist only for Discovery **string formats**, which have no schema of their
own: `GoogleTimestamp` (`google-datetime`), `GoogleDuration` (`google-duration`) and
`GoogleFieldMask` (`google-fieldmask`), plus an `Int64StringConverter` for `int64`.

`CivilDateTime` and `Date` are **not** primitives. They are ordinary object schemas in Discovery
(`CivilDateTime` wraps a `Date` plus a time; `Date` is year/month/day), so they are generated
models like any other and keep their wire names. An earlier draft of this ADR listed them as
handwritten primitives; that was wrong and was corrected while implementing the type mapper.

Two refinements from verification:

- `google-fieldmask` occurs **only** as the `updateMask` query parameter, never inside a
  schema, so `GoogleFieldMask` is a query-serialization concern.
- Health records carry a consistent triple: `physicalTime` (`google-datetime`) plus
  `utcOffset` (`google-duration`) plus a read-only `civilTime` (`CivilDateTime`). No helper
  fuses the first two: both are independently optional, so a combining API would have to invent
  a policy for the cases where one is absent. Callers combine them explicitly — see
  [data-points.md](../data-points.md#time-comes-in-three-parts).

## Consequences

- No precision beyond what the documents state is assumed for `google-datetime`.
- `google-duration` retains fractional seconds losslessly.
- RFC 3339 validation and canonicalisation are owned by the SDK and are testable.
