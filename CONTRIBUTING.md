# Contributing

## Prerequisites

- The .NET SDK version in [`global.json`](global.json) or newer. It is a floor rather than a pin:
  `rollForward: latestFeature` accepts any later feature band of the same major version
- Git configured so that the specification snapshot is not rewritten (see below)

```bash
dotnet restore HealthData.slnx
dotnet build   HealthData.slnx -c Release
dotnet test --solution HealthData.slnx -c Release -- --filter-not-trait Category=Package
```

Tests run on Microsoft.Testing.Platform, so filters are its options rather than VSTest's and
belong after the `--`. Only the package tests are excluded here: they need `dotnet pack` to have
run first. The integration tests are included and skip themselves for want of a credential —
excluding them would leave that assembly matching nothing, which MTP treats as a failed run.

The build treats warnings as errors. A pull request that introduces a warning does not pass.

## Line endings matter here

`spec/v4/discovery.json` is byte-exact provenance: its SHA-256 is recorded in
`spec/v4/metadata.json` and asserted by `SpecSnapshotTests`.

Git on Windows defaults to `core.autocrlf=true`, which would rewrite that file on checkout and
break the hash. [`.gitattributes`](.gitattributes) pins `spec/**/*.json` to `-text` to prevent
this. **Do not remove those rules**, and never hand-edit `discovery.json`.

If the hash test fails, restore the snapshot from git:

```bash
git checkout -- spec
```

Renormalising is not the fix, for two reasons: `git add --renormalize` writes the index and the
test reads the working tree, and `spec/**/*.json` is marked `-text`, so line-ending normalisation
would not have touched it in the first place. **That command discards any local edit under
`spec/`** — if you meant to change the snapshot, run `codegen fetch` and update `metadata.json`
instead of restoring it.

## Native AOT on Windows

`dotnet publish -p:PublishAot=true` needs the MSVC linker. The ILCompiler itself runs fine
without it — only the final native link step fails, with:

```text
error MSB3073: 'vswhere.exe' is not recognized as an internal or external command
```

Either run from a **Developer PowerShell for Visual Studio**, or put `vswhere` on `PATH`:

```powershell
$env:PATH += ";C:\Program Files (x86)\Microsoft Visual Studio\Installer"
```

CI is the authoritative Native AOT gate; local failures of this kind are environmental.

## Repository rules that are not negotiable

These exist to stop the implementation drifting back to a worse design. Where a rule was a
judgement call rather than a constraint, the reasoning is in [`docs/adr/`](docs/adr/README.md).
Before changing anything under `Generated/`, read
[`docs/code-generation.md`](docs/code-generation.md) — those files are emitted, and CI verifies
them byte for byte.

1. `src/Kkdev92.HealthData` takes **zero third-party runtime dependencies**.
2. No `LangVersion` of `latest` or `preview`.
3. Wire names are never reshaped by a naming convention. `pageSize` never becomes `page_size`.
4. An operation is not public just because Discovery contains it — `public-surface.json` decides.
5. Health payloads and tokens never reach logs, exception messages, or `ToString()`.
6. Webhook signatures are verified against the **raw received bytes**, before parsing.
7. No public `OperationsResource` is invented; the API exposes no polling resource.
8. Nothing generated may embed a timestamp, machine path, or locale-dependent formatting.

## Changing the API contract

Never hand-edit generated sources or `discovery.json`. The flow is:

```text
codegen fetch     refresh the snapshot and metadata.json   (network)
codegen diff      review what changed                      (human review)
codegen generate  regenerate C#                            (offline)
codegen verify    prove the checked-in sources are current
```

Changes to `public-surface.json` always require human review: they widen or narrow the
public API surface.

## Packing

**Never publish a package produced by a plain local `dotnet pack`.**

Without `ContinuousIntegrationBuild`, the compiler records the absolute path of the pdb in the
assembly's debug directory, so the package carries your machine's directory layout. The property
is set from the `CI` environment variable, which the workflows export, so a release built by CI is
clean and a local one is not.

If you need to inspect a package locally, build it the way CI does:

```bash
CI=true dotnet build HealthData.slnx -c Release
CI=true dotnet pack  HealthData.slnx -c Release --no-build -o artifacts
dotnet test --project tests/Kkdev92.HealthData.Tests -c Release --no-build -- --filter-trait Category=Package
```

That last step is the guard. `.gitignore` has no bearing on what gets packed — NuGet packs MSBuild
items — so `PackageContentTests` asserts the package contains only an allowlisted set of entries
and mentions no local-only path. It runs in CI immediately after packing, and is excluded from the
ordinary test run because it needs the packed output to exist.

## Tests

| Suite | Purpose |
|---|---|
| `Kkdev92.HealthData.Tests` | Runtime unit tests |
| `Kkdev92.HealthData.ContractTests` | Exact HTTP wire contract, via a fake `HttpMessageHandler` |
| `Kkdev92.HealthData.CodeGen.Tests` | Spec integrity and generator golden tests |
| `Kkdev92.HealthData.IntegrationTests` | Real API. `[Trait("Category", "Integration")]`, never a pull-request gate; `integration.yml` runs it on demand |
| `Kkdev92.HealthData.AotSmokeTests` | A console app that CI publishes with Native AOT |
| `Kkdev92.HealthData.TestSupport` | Fakes the suites share. Not a test project, and deliberately free of xUnit, because the AOT smoke app uses it too |

Integration tests must **skip**, not fail, when credentials are absent.

### What a change has to bring with it

**A change in behaviour comes with a test, and a fix comes with a test that fails without it.**

The second half is the part worth insisting on. A test written after a fix, and never seen to
fail, proves only that the code compiles — and this project has shipped exactly that mistake more
than once: a webhook secret checked on one request kind out of two, a package-contents test that
inspected an empty directory and reported success, a scheduled job that skipped when its
credentials were missing and stayed green for months. Each passed continuously while guarding
nothing.

So, before opening the pull request:

1. Write the test.
2. Undo the fix and watch the test fail. If it passes, it is not testing the fix.
3. Redo the fix and watch it pass.

Say in the pull request that you did it. If a change genuinely cannot be tested — an
infrastructure or documentation change — say that instead, and why.

Behaviour that depends on Google's contract belongs in `spec/v4/semantics.json` with its source
and the date it was read, not in a comment. Behaviour that depends on untrusted input deserves a
test over generated input rather than a handful of examples: see `GeneratedInputTests`.

## Releasing

1. Version bump, `CHANGELOG.md` and the README status line, in a pull request. Merge it.
2. Dispatch `release.yml` by hand. A manual run is always a dry run — it builds, verifies and
   packs, and stops before every publishing job. Tags here are immutable, so a pipeline that
   fails after the tag exists costs a version number.
3. Dispatch `integration.yml` by hand, with a credential. This is the only check that talks to
   the live API.
4. Tag `vX.Y.Z` and push it.
5. Approve the `release` environment.

Step 3 is the one that looks skippable. Everything else in CI reads what Google *says* — the
committed `spec/v4` snapshot, and `spec-check.yml` watching Discovery and the reference pages for
drift. None of it can see a field that is documented as one thing and arrives as another, which
has happened. That gap is only visible from a real request, and a release is when it is worth
the credential: the smoke test needs one, and a consent screen still in Testing issues refresh
tokens that expire in seven days, so there is no standing credential to keep warm. Mint one when
you are about to ship.
