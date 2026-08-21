## Summary

Brief description of changes.

## Related Issue

Fixes #(issue number)

## Type of Change

- [ ] Bug fix (non-breaking change that fixes an issue)
- [ ] New feature (non-breaking change that adds functionality)
- [ ] Breaking change (fix or feature that would cause existing functionality to not work as expected)
- [ ] Specification change (refreshed snapshot or an edit under `spec/`)
- [ ] Documentation update

## Changes

-
-

## Checklist

- [ ] I have read the [CONTRIBUTING](../CONTRIBUTING.md) guidelines
- [ ] Build is warning-free (`dotnet build HealthData.slnx -c Release`)
- [ ] Tests pass (`dotnet test --solution HealthData.slnx -c Release -- --filter-not-trait Category=Package`)
- [ ] A change in behaviour brings a test; a fix brings one seen to fail without the fix
      (see [CONTRIBUTING.md](../CONTRIBUTING.md#what-a-change-has-to-bring-with-it)) — or the
      pull request says why the change cannot be tested
- [ ] Formatting passes (`dotnet format HealthData.slnx --verify-no-changes`)
- [ ] Generated sources are current (`dotnet run --project tools/Kkdev92.HealthData.CodeGen -- verify`)
- [ ] No file under `Generated/` was hand-edited
- [ ] Public API changes are reflected in `tests/PublicApi` and reviewed in this diff
- [ ] No health payload, token or user identifier reaches a log, an exception or `ToString()`
- [ ] I have updated documentation if needed
