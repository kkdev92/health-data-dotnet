# ADR-0005: Open enums for wire values

- Status: Accepted
- Date: 2026-08-09

## Context

Discovery revision 20260826 declares 58 enum-typed properties. `Exercise.exerciseType` alone
has roughly 180 values, and every enum is protobuf-derived with an `*_UNSPECIFIED` member.
Google adds values additively.

A closed C# `enum` would turn any new server-side value into a deserialization failure.

## Decision

Wire enums are generated as `readonly partial record struct` wrappers over the string value,
with named static members for the values known at generation time. Unknown values round-trip
unchanged.

## Consequences

- Adding an enum value on the server is a non-breaking change for consumers.
- Callers cannot rely on exhaustive `switch` over the type.
- The same treatment applies to error reasons, which are generated as `const string`
  constants rather than an enum.
