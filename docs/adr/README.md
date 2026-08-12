# Architecture Decision Records

Decisions that are expensive to reverse. Each records the context that forced the choice, so a
future reader can tell whether the context still holds.

Write one when undoing the decision later would mean a breaking change, a regeneration of the
contract, or a new dependency. Anything reversible belongs in a code comment or in
[`../architecture.md`](../architecture.md) instead.

| ADR | Decision | Status |
|---|---|---|
| [0001](0001-rest-first.md) | REST-first transport | Accepted |
| [0002](0002-no-google-apis-runtime-dependency.md) | No `Google.Apis` runtime dependency | Accepted |
| [0003](0003-discovery-driven-deterministic-generation.md) | Discovery-driven deterministic generation | Accepted |
| [0004](0004-version-neutral-package-identity.md) | Version-neutral package identity | Accepted |
| [0005](0005-open-enums.md) | Open enums for wire values | Accepted |
| [0006](0006-write-contract-excludes-read-only-fields.md) | Read-only fields excluded from the write contract | Accepted |
| [0007](0007-authentication-via-request-pipeline.md) | Authentication via the request pipeline | Accepted |
| [0008](0008-explicit-google-wire-primitives.md) | Explicit Google wire primitives | Accepted |
