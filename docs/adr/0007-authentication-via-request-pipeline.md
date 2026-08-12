# ADR-0007: Authentication via the request pipeline

- Status: Accepted
- Date: 2026-08-09

## Context

The API has two distinct authentication contexts, confirmed against Discovery:

- `users.*` and `users.dataTypes.dataPoints.*` use end-user OAuth scopes (`googlehealth.*`)
- `projects.subscribers.*` uses project credentials (`cloud-platform`)

A single access-token field on the client cannot express this, and in a multi-user server a
token held on a singleton client is a data-leak hazard.

## Decision

The core client owns no credentials. Each generated operation attaches an operation descriptor
to `HttpRequestMessage.Options`; a delegating handler reads it and asks an
`IHealthDataAccessTokenProvider` for a token appropriate to that operation.

Scope requirements are modelled as `AnyOf` / `AllOf` rather than a flat `string[]`, because
Discovery expresses only `AnyOf` and some operations genuinely require a combination.

## Consequences

- Per-request, per-user tokens work without a client instance per user.
- Client construction does not demand the union of all scopes.
- No production token vault ships with this SDK; storage is the application's responsibility.
