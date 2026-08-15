# ADR-0009: Namespaces by kind, enums nested under their owners

- Status: Accepted
- Date: 2026-08-15

## Context

The first consumer built a real application against `0.1.0-alpha` and reported two findings about
the shape of the surface:

- 249 of the core assembly's 270 public types sat in the root namespace. Typing
  `Kkdev92.HealthData.` offered every measurement, request, response and enum at once.
- The 58 open enums were named by concatenating owner and property —
  `ActivityLevelRollupByActivityLevelTypeActivityLevelType` at the worst, 55 characters — and a
  reader looking at `device.DeviceType` could not guess the type name `PairedDeviceDeviceType`.
  They wrote `DeviceType.Tracker`, got CS0103, and had to hover to find out why.

The report proposed nesting each enum in its owner, as `PairedDevice.DeviceType`. C# refuses that
exact shape: a nested type may not share a name with a member of its enclosing class (CS0102), and
in all 58 cases the enum is named after the property that uses it. This was verified by compiling
both shapes.

Shortening names at namespace scope was measured as the alternative: only 33 of 58 suffixes are
unique across the public surface, so the rule would hold sometimes and not others, and it would
mint top-level types called `State`, `Result` and `Platform`.

## Decision

**Enums nest under a `Types` container, protobuf style.** `Settings.Types.DistanceUnit`,
`PairedDevice.Types.DeviceType` — the same convention Google's own protobuf codegen for C# uses to
answer the same CS0102 collision (`Person.Types.PhoneType`). The rule is uniform for all 58, the
type is one guess away from the property, and ownership is real: reflection over the built
assembly shows every enum is used by exactly one schema.

**Namespaces follow what a type is.**

| Namespace | Holds |
| --- | --- |
| `Kkdev92.HealthData` | The client, resources, options, operation and scope tables, error types, wire primitives |
| `Kkdev92.HealthData.Models` | Every Discovery schema, the nested enums, the union helpers |
| `Kkdev92.HealthData.Requests` | The request envelopes this SDK synthesizes per operation |
| `Kkdev92.HealthData.Http` / `.Serialization` / `.Pagination` | Unchanged |

A Discovery schema is something that crosses the wire; a request envelope is something this SDK
invented to carry parameters. Responses cross the wire, so they are Models. Nested namespaces see
the root without a `using`, so models refer to `GoogleTimestamp` freely; the reverse direction is
an explicit `using` per generated file, emitted only where the file actually needs it, because an
unused using is a build error here.

## Consequences

- Binary- and source-breaking against `0.1.0-alpha`, which is why it ships while the only
  published version is an alpha. The package-validation baseline is unpinned for the same pull
  request and returns with the next publish.
- Typing `Kkdev92.HealthData.` now offers roughly two dozen types, of which the first
  interesting one is the client.
- Consumers add `using Kkdev92.HealthData.Requests;` (and usually `.Models`) next to the existing
  root using. The README shows the pair.
- The serializer registers each enum explicitly with its pre-nesting flat name as
  `TypeInfoPropertyName`, because System.Text.Json derives metadata property names from simple
  type names and the nested leaves collide (three owners declare a property called `type`).
- A future Discovery revision could introduce a wire value whose normalized name equals its
  enum's leaf name, which would be CS0542 inside the container. The contract validator rejects
  that, and a property named `types`, `value`, `fromValue` or `toString`, with the culprit named.
