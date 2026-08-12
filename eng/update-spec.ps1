#!/usr/bin/env pwsh
# Refresh the Discovery snapshot, then report what changed. Requires network access.
# Never run as part of a build: review the diff before regenerating.
param([string]$Version = 'v4')
$ErrorActionPreference = 'Stop'
$codegen = "$PSScriptRoot/../tools/Kkdev92.HealthData.CodeGen"
dotnet run --project $codegen -- fetch --version $Version
dotnet run --project $codegen -- diff  --version $Version
