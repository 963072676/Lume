param([string]$DotnetPath = 'dotnet', [switch]$SkipBuild, [switch]$SkipDesktop, [switch]$SkipInteractive)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$DotnetPath = & (Join-Path $PSScriptRoot 'resolve-dotnet.ps1') -DotnetPath $DotnetPath
$version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$evidence = Join-Path $projectRoot "artifacts/verification-$version"
$appDirectory = Join-Path $projectRoot 'artifacts/verification-app'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
& $DotnetPath run --project (Join-Path $projectRoot 'tests/Lume.Core.Tests') -c Release 2>&1 | Tee-Object -FilePath (Join-Path $evidence 'core-tests.txt')
if ($LASTEXITCODE -ne 0) { throw '核心回归失败。' }
if (!$SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -DotnetPath $DotnetPath -Verification 2>&1 | Tee-Object -FilePath (Join-Path $evidence 'build.txt') }
$stages = @(
    @{ Flag='--menu-self-test'; Result='menu-verification/result.json'; Name='menu' },
    @{ Flag='--icons-self-test'; Result='icons-verification/result.json'; Name='icons' },
    @{ Flag=$(if ($SkipInteractive) { '--features-self-test --no-input' } else { '--features-self-test' }); Result='features-verification/result.json'; Name='features' },
    @{ Flag='--smoke'; Result='smoke/result.json'; Name='ui' }
)
if (!$SkipDesktop -and !$SkipInteractive) { $stages += @{ Flag='--desktop-smoke'; Result='demo-data/desktop-verification/result.json'; Name='desktop' } }
foreach ($stage in $stages) {
    $started = [DateTime]::UtcNow
    $process = Start-Process -FilePath (Join-Path $appDirectory 'Lume.exe') -ArgumentList $stage.Flag -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(60000)) { $process.Kill(); throw ($stage.Name + ' 验收超时；桌面保护进程负责恢复原图标。') }
    $resultPath = Join-Path $appDirectory $stage.Result
    if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $resultPath) -or (Get-Item -LiteralPath $resultPath).LastWriteTimeUtc -lt $started) { throw ($stage.Name + ' 验收失败，请检查 ' + $appDirectory + ' 中本次 error.txt。') }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if ($result.passed -eq $false) { throw ($stage.Name + ' 未通过。') }
    Copy-Item -LiteralPath $resultPath -Destination (Join-Path $evidence ($stage.Name + '-result.json'))
    Write-Output ('PASS ' + $stage.Name)
}
Get-FileHash -LiteralPath (Join-Path $appDirectory 'Lume.exe') -Algorithm SHA256 | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'verification-binary.json')
Write-Output ('验收完成：' + $evidence)
if ($SkipInteractive) { Write-Output '本次显式跳过真实鼠标悬停与桌面交互，不代表完整桌面验收通过。' }
