param([ValidateRange(1,1440)][int]$Minutes = 480)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = Join-Path $root 'artifacts/verification-app'
$destination = Join-Path $root ('artifacts/soak-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $destination | Out-Null
# Freeze this run's binaries, so later builds cannot silently change the running acceptance target.
foreach ($name in (Get-Content -LiteralPath (Join-Path $source 'release-files.txt'))) {
    if ([IO.Path]::GetFileName($name) -ne $name) { throw '验收清单包含非法文件名。' }
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination $destination
}
$exe = Join-Path $destination 'Lume.exe'
$buildInfo = Get-Content -LiteralPath (Join-Path $destination 'build-info.json') -Raw | ConvertFrom-Json
if (!$buildInfo.verification -or $buildInfo.dirty) { throw '长测必须使用已提交的专用验收构建。' }
$process = Start-Process -FilePath $exe -ArgumentList @('--performance-self-test','--soak-minutes',$Minutes) -WindowStyle Hidden -PassThru
$run = [ordered]@{ pid=$process.Id; startedUtc=$process.StartTime.ToUniversalTime().ToString('o'); minutes=$Minutes; executable=$exe; sha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash; resultRoot=(Join-Path $destination 'performance-verification'); version=$buildInfo.version; commit=$buildInfo.commit; desktopTakeover=$false; inputSimulation=$false }
$run | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'run.json') -Encoding utf8
$run | ConvertTo-Json
