#!/usr/bin/env bash
# Regenerate the C# API contract from the committed specification snapshot. Offline.
set -euo pipefail
VERSION="${1:-v4}"
dotnet run --project "$(dirname "$0")/../tools/Kkdev92.HealthData.CodeGen" -- generate --version "$VERSION"
