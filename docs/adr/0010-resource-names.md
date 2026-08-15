# ADR-0010: Resource names are types, generated from the patterns the contract states

- Status: accepted
- Date: 2026-08-15
- Supersedes: nothing
- Related: [ADR-0005](0005-open-enums.md), [ADR-0009](0009-surface-partition.md)

## Context

Every operation that names something took a `string`:

```csharp
public sealed class GetPairedDevicesRequest
{
    /// <remarks>The service requires this to match the pattern ^users/[^/]+/pairedDevices/[^/]+$.</remarks>
    public required string Name { get; init; }
}
```

The rule was there. It was one line above the property, addressed to a human, and the compiler could
not read it. Two failures came out of that, both reported from an application built on this SDK:

- a data point name assigned to `pairedDevices.get`, which compiled, was sent, and answered `400`;
- a name assembled with string interpolation, one segment wrong, which did the same.

Neither is a mistake a careful person avoids reliably: the properties are both called `Name`, both
typed `string`, and the difference between them is a regular expression in a doc comment.

The regular expression is the part worth noticing. Discovery carries a `pattern` on name
parameters, and this contract carries one on **every** name parameter — 25 of 25, 11 distinct
expressions. The generator was already reading them. It was rendering them as prose.

## Decision

**The pattern is the type.** Each distinct pattern becomes a generated type in
`Kkdev92.HealthData.Names`, and the request property takes that type instead of `string`.

```csharp
public sealed record GetPairedDevicesRequest
{
    public required PairedDeviceName Name { get; init; }
}
```

The failure that was a `400` is now this, at the line where it is written:

```text
error CS0029: cannot implicitly convert type
'Kkdev92.HealthData.Names.DataPointName' to 'Kkdev92.HealthData.Names.PairedDeviceName'
```

### What the pattern decides

Nothing about the eleven types is written down. The expression is split into segments — literals and
`[^/]+` — and everything follows from that:

| From the pattern | Becomes |
| --- | --- |
| the last collection, singularized | the type name: `pairedDevices/[^/]+` → `PairedDeviceName` |
| a trailing literal | a singleton: `users/[^/]+/profile` → `ProfileName` |
| each `[^/]+` | an id property, and an argument to the builder that supplies it |
| the longest pattern that is a strict prefix | the parent, and the type that builds this one |
| the expression itself | `[GeneratedRegex]`, used by `Parse` and by every builder |

So `users/{u}/dataTypes/{t}/dataPoints/{p}` descends from `DataTypeName`, not from `UserName`: the
nearest ancestor present in the contract wins, which keeps the type in between from disappearing.

### Building and parsing

```csharp
UserName.Me.DataType("steps").DataPoint("abc")   // users/me/dataTypes/steps/dataPoints/abc
PairedDeviceName.Parse(device.Name!)             // from a list response
PairedDeviceName.TryParse(fromConfiguration, out var name)
```

A builder runs the pattern over what it produced, so `UserName.Me.DataType("steps/dataPoints/x")`
throws rather than quietly rendering a name that reads like a data point. There is no implicit
conversion from `string` in either direction: adding one would restore exactly the hole this closes.
`ToString()` is the wire form, which is what `SetPath` receives.

### `users/me`

`UserName.Me` is a member because every call in a user-facing application uses it. It is not a
special case in the type: the pattern accepts `me` as an id like any other, and `Me.UserId` is
`"me"`.

### Where the documentation and the pattern disagree

`pairedDevices.get` is described as `Format: users/{user}/devices/{device}` and its pattern says
`pairedDevices`. **The pattern wins**, because the pattern is what the service applies — sending the
documented form answers `404`. `PairedDeviceName.TryParse("users/me/devices/abc")` is `false`, and a
test holds that.

## Consequences

### Requests became records

Typing the names was the reason to revisit the request types at all, and two things followed.

`with` is the answer to manual paging — "the same call, one page on" — and it needs a record. The
generated `WithPageToken` stays, now public rather than internal, because a caller who wants one
page at a time was previously left writing that copy by hand against an init-only type.

A record's generated `ToString` prints every property, and a request's properties are the name of a
person's record, the filter asked of it, and the body about to be written. Models are classes for
precisely this reason (ADR-0005 has the same concern for enums). A request cannot be a class without
losing the copy semantics, so each one overrides `ToString` to return its type name, and
`PrivacyGuardTests` holds that for every request type rather than for the three anybody remembered.

### Enumeration no longer depends on where the cursor travels

`dataPoints.rollUp` pages through its **body**, and had no `EnumerateAsync` for that reason alone.
Its body model now gets a generated `WithPageToken`, so the streaming overload is generated from the
same rule as everywhere else: *the response returns a next page token*. `dailyRollUp` is still the
one operation without one, and now says so on the property itself — it accepts a cursor and returns
none, so there is nothing to follow.

### Costs

- **Breaking.** Every `Name`/`Parent` assignment changes. In this repository that was 150 compile
  errors across 9 files, each one a place where a name was being assembled by hand.
- A name that does not match cannot be sent, even if a future service accepts it. That is the trade:
  the same trade the pattern itself makes, and the service applies the pattern first.
- Eleven more public types, and `<Clone>$()` on each from the record. Both are in the approved
  public API snapshot.

## Alternatives rejected

**Validate the string at send time.** Keeps one type and turns the `400` into a local exception —
but only at run time, on the line that sends rather than the line that is wrong, and it does nothing
about a data point name in a paired device request, which is a *valid* name of the wrong resource.

**One `ResourceName` type with the pattern as data.** Everything parses, nothing is confused for
anything else, and `GetPairedDevicesRequest.Name = <a data point name>` compiles again.

**Structs instead of classes.** A struct has a `default` no constructor ever saw: a name holding
nothing, assignable to a `required` property, rendering as empty and sent. Google's own generated
.NET resource names are classes, and this is why.

**An `Unparsed` escape hatch**, as `Google.Api.Gax` offers. Deferred rather than rejected: it exists
there so a name from a newer server survives a round trip, which is a real concern for a library
covering many services and a speculative one for a contract of eleven patterns that the service
enforces on the way in. If the service is ever seen returning a name its own pattern rejects, this is
the answer, and `TryParse` is where it would go.
