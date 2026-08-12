#!/usr/bin/env bash
# Fail if the checked-in generated sources are stale relative to the snapshot.
set -euo pipefail
VERSION="${1:-v4}"
dotnet run --project "$(dirname "$0")/../tools/Kkdev92.HealthData.CodeGen" -- verify --version "$VERSION"
