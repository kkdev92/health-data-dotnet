#!/usr/bin/env bash
# Refresh the Discovery snapshot, then report what changed. Requires network access.
# Never run as part of a build: review the diff before regenerating.
set -euo pipefail
VERSION="${1:-v4}"
CODEGEN="$(dirname "$0")/../tools/Kkdev92.HealthData.CodeGen"
dotnet run --project "$CODEGEN" -- fetch --version "$VERSION"
dotnet run --project "$CODEGEN" -- diff  --version "$VERSION"
