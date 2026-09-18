param([string]$DotnetPath = 'dotnet')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$DotnetPath = & (Join-Path $PSScriptRoot 'resolve-dotnet.ps1') -DotnetPath $DotnetPath
New-Item -ItemType Directory -Path (Join-Path $root 'artifacts') -Force | Out-Null
& $DotnetPath run --project (Join-Path $root 'tests/Lume.Core.Tests') -c Release -- --benchmark (Join-Path $root 'artifacts/benchmark.json')
if ($LASTEXITCODE -ne 0) { throw '性能回归未通过，请检查 artifacts/benchmark.json。' }
