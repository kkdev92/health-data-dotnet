---
name: Bug Report
about: Report a bug to help improve Kkdev92.HealthData
title: '[Bug] '
labels: bug
assignees: ''
---

> **Do not paste health data, access tokens, refresh tokens, webhook payloads or
> resource names.** A resource name embeds both the user and the data type. Redact
> them; a redacted reproduction is always enough.
>
> **Nor your own OAuth client id, client secret or webhook endpoint secret.** Those
> identify your project rather than a person, so they do not look like personal data
> and are the easiest thing to paste without noticing — out of an authorization URL,
> a `curl` command, or an `appsettings` fragment. An issue is public and stays that
> way; anything posted here should be assumed to be permanent.

## Environment

- **Package and version**: (e.g., Kkdev92.HealthData 0.1.0-alpha)
- **.NET SDK**: output of `dotnet --version`
- **OS**: (e.g., Windows 11, Ubuntu 24.04)
- **Native AOT / trimming**: (yes / no)

## Description

A clear description of the bug.

## Steps to Reproduce

1.
2.
3.

## Expected Behavior

What you expected to happen.

## Actual Behavior

What actually happened.

## Code Example

```csharp
// Minimal code to reproduce the issue
```

## Error Messages / Logs

The exception type, `ex.Message`, `OperationId` and the HTTP status are enough.
The SDK keeps payloads and response text out of the message, which is why the
message is the part to paste.

`Reason` and the parsed envelope on `Error` are not filtered — they are whatever
the service sent. Quote `Reason` only if it looks like `SOMETHING_LIKE_THIS`;
anything else may be text echoed back from your own request.

## Additional Context

Any other relevant information.
