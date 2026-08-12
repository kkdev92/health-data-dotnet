#!/usr/bin/env pwsh
# Regenerate the C# API contract from the committed specification snapshot. Offline.
param([string]$Version = 'v4')
$ErrorActionPreference = 'Stop'
dotnet run --project "$PSScriptRoot/../tools/Kkdev92.HealthData.CodeGen" -- generate --version $Version
