# ADR-0006: Read-only fields are excluded from the write contract

- Status: Accepted
- Date: 2026-08-09

## Context

27 of the 147 schemas declare `readOnly` properties. Sending them back to the server is at best
ignored and at worst rejected.

The original proposal was to generate a separate input type per affected schema
(`Profile` / `ProfileInput`, `DataPoint` / `DataPointInput`, and so on). Measured against the
actual contract, that costs **55 additional generated types**: 23 schemas reachable from a
request body declare `readOnly` properties, and every container that transitively holds one
would need its own input variant as well.

An alternative was prototyped: keep one C# type and remove read-only properties from the
*serialization contract* used for writing, via a `System.Text.Json` contract modifier.

```csharp
var write = new JsonSerializerOptions
{
    TypeInfoResolver = HealthDataJsonContext.Default.WithAddedModifier(StripOutputOnly),
};
```

Verified on 2026-08-09 with .NET SDK 10.0.302: reading retains `createTime`, writing emits
`{"name":"..."}` only, and ILCompiler produced no IL2026 or IL3050 warnings.

## Decision

One generated type per schema. Read-only properties are readable but are removed from the
write contract by a generated modifier table. No separate input types.

This supersedes the earlier "response/input separation" formulation.

## Consequences

- The public API surface is 55 types smaller and there is no `XInput` / `X` choice for callers.
- Wire correctness is preserved: read-only fields are never transmitted.
- The compiler no longer prevents *assigning* a read-only property; it is silently dropped on
  send. A contract test asserts the omission for every affected schema.
- Reversible: if compile-time prevention is later judged necessary, the generator can emit
  input types without changing the runtime.
