# ADR-0001: REST-first transport

- Status: Accepted
- Date: 2026-08-09

## Context

The Google Health API publishes a REST/JSON contract and a Discovery document. gRPC surface is
not part of the public contract we verified on 2026-08-09.

## Decision

Version 1 supports REST only. gRPC is out of scope.

## Consequences

- The generator only has to understand Discovery, not protobuf service definitions.
- Media download (`exportExerciseTcx`) is handled as an HTTP concern.
- Should Google publish a gRPC surface later, it would be an additive decision, not a rewrite.
