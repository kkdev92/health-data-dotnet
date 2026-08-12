# ADR-0003: Discovery-driven deterministic generation

- Status: Accepted
- Date: 2026-08-09

## Context

Hand-writing DTOs for 147 schemas and 27 operations does not stay in sync. Generating at build
time from the live Discovery document would make builds depend on the network and on the day
they run.

## Decision

The Discovery document is committed as a versioned snapshot. Generation is an explicit,
offline CLI step. Generated sources are committed. The generator is never a Roslyn source
generator.

Determinism is a requirement: identical input must produce byte-identical output. No
timestamps, machine paths, locale-dependent formatting, or dictionary iteration order may
influence the result.

**The snapshot is stored canonicalized, not raw.** Object keys are sorted ordinally, indentation
is two spaces, line endings are LF, encoding is UTF-8 without BOM, and the file ends with a
newline. Array order, numbers, and string contents are preserved exactly, so the stored document
is semantically identical to the response.

### Why canonicalization is mandatory

Measured on 2026-08-09: the Discovery endpoint returns the **same document with randomized object
key order on every request**. Four consecutive fetches produced four different SHA-256 values at
an identical byte length (282,943 bytes), differing in roughly 250,000 byte positions, with zero
semantic difference.

Storing the raw response would therefore have broken both things this ADR exists to provide:

- **Provenance would be meaningless.** The recorded hash would change on every fetch, so it could
  never distinguish "Google changed the contract" from "Google shuffled the keys". A scheduled
  drift check would fire constantly and be ignored within a week.
- **Contract diffs would be unreviewable.** Every specification update would render as a
  whole-file rewrite, which defeats the entire reason for committing the snapshot.

Canonicalizing is not hand-editing. It is deterministic, lossless,
and applied by `codegen fetch`; `SpecLoader` refuses to run against a non-canonical snapshot.

## Consequences

- Builds work offline and reproducibly.
- Contract changes are reviewable as a diff in a pull request.
- `spec/v4/metadata.json` records the canonical SHA-256, and CI re-verifies it on Linux and
  Windows.
- `.gitattributes` must prevent git from rewriting snapshot bytes, or the hash breaks on
  Windows checkouts.
- Any JSON writer used for canonical output must have its newline pinned to LF.
  `Utf8JsonWriter` indents with `Environment.NewLine` by default, which silently produces a
  different hash on Windows than on Linux; a test asserts LF-only output.
- The raw response hash is deliberately **not** recorded, because it is not reproducible.
