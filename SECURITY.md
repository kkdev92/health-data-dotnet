# Security Policy

## Reporting a vulnerability

Please report security issues privately through
[GitHub Security Advisories](https://github.com/kkdev92/health-data-dotnet/security/advisories/new)
rather than opening a public issue.

Do **not** include real health data, access tokens, refresh tokens, client secrets, or webhook
payloads in a report. A redacted reproduction is always sufficient.

## Scope

This project is an unofficial client SDK. Vulnerabilities in the Google Health API service
itself should go to Google, not here.

In scope:

- Webhook signature verification that accepts a payload it should reject
- Health data, tokens, or secrets leaking into logs, exception messages, or `ToString()`
- Credential handling flaws in `Kkdev92.HealthData.Authentication`
- Anything that causes a request to be sent to an unintended host

## Security posture

This SDK handles health data and applies stricter defaults than a general-purpose API client.

**Never recorded by default:** request and response bodies, TCX payloads, access tokens,
refresh tokens, authorization codes, client secrets, webhook raw payloads, webhook
authorization secrets, and any profile or measurement values.

**Webhook verification** is performed on the raw received bytes before parsing, and fails
closed: an unknown key ID triggers a keyset refresh, and if the key is still unknown the
payload is rejected.

**Token storage is out of scope.** `Kkdev92.HealthData.Authentication` deliberately ships no
production token vault. Persisting and encrypting tokens is the consuming application's
responsibility.

## Traces can leak resource names — action required

A Google Health resource name embeds both the user and the data type:

```text
users/1234/dataTypes/heart-rate/dataPoints/abc
```

This SDK's own activities never record a URL. **But .NET's built-in HTTP client
instrumentation does**: `System.Net.Http` sets `url.full` on every request span, and its
redaction covers only the query string, not the path. If you export traces from an application
that uses this SDK, that attribute will contain user identifiers and health categories.

If you collect traces, drop or redact `url.full` on spans from the `System.Net.Http` source.
With the OpenTelemetry SDK this is a processor that clears the attribute; the exact form depends
on your pipeline. This is not something a library can turn off on your behalf.

## Not a compliance guarantee

Using this SDK does not make an application compliant with Google's
[Health API Developer and User Data Policy](https://developers.google.com/health/policies/health-api-developer-user-data-policy).
Privacy policy, scope minimisation, secure transport, encryption at rest, and key management
remain the responsibility of the consuming application.
