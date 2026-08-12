# Contributing

## Prerequisites

- The .NET SDK version in [`global.json`](global.json) or newer. It is a floor rather than a pin:
  `rollForward: latestFeature` accepts any later feature band of the same major version
- Git configured so that the specification snapshot is not rewritten (see below)

```bash
dotnet restore HealthData.slnx
dotnet build   HealthData.slnx -c Release
dotnet test    HealthData.slnx -c Release --filter "Category!=Integration"
```

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
dotnet test tests/Kkdev92.HealthData.Tests -c Release --no-build --filter "Category=Package"
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
| `Kkdev92.HealthData.IntegrationTests` | Real API. `[Trait("Category", "Integration")]`, never a pull-request gate; its own scheduled and manual workflow runs it |
| `Kkdev92.HealthData.AotSmokeTests` | A console app that CI publishes with Native AOT |

Integration tests must **skip**, not fail, when credentials are absent.
