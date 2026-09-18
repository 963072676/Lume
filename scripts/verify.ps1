param([string]$DotnetPath = 'dotnet', [switch]$SkipBuild, [switch]$SkipDesktop, [switch]$SkipInteractive, [switch]$SkipMedia)
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
& $DotnetPath run --project (Join-Path $projectRoot 'tests/Lume.Recovery.Tests') -c Release -- (Join-Path $appDirectory 'Lume.Guard.exe') (Join-Path $evidence 'native-recovery')
if ($LASTEXITCODE -ne 0) { throw '原生恢复保护验收失败。' }
$stages = @(
    @{ Flag='--menu-self-test'; Result='menu-verification/result.json'; Name='menu' },
    @{ Flag='--icons-self-test'; Result='icons-verification/result.json'; Name='icons' },
    @{ Flag=('--features-self-test' + $(if ($SkipInteractive) { ' --no-input' }) + $(if ($SkipMedia) { ' --no-media' })); Result='features-verification/result.json'; Name='features' },
    @{ Flag='--smoke'; Result='smoke/result.json'; Name='ui' }
)
if (!$SkipDesktop -and !$SkipInteractive) { $stages += @{ Flag='--desktop-smoke'; Result='demo-data/desktop-verification/result.json'; Name='desktop' } }
foreach ($stage in $stages) {
    $started = [DateTime]::UtcNow
    $process = Start-Process -FilePath (Join-Path $appDirectory 'Lume.exe') -ArgumentList $stage.Flag -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(60000)) { $process.Kill(); throw ($stage.Name + ' 验收超时；桌面保护进程负责恢复原图标。') }
    $resultPath = Join-Path $appDirectory $stage.Result
    if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $resultPath) -or (Get-Item -LiteralPath $resultPath).LastWriteTimeUtc -lt $started) {
        Get-ChildItem -LiteralPath $appDirectory -Recurse -Filter '*error.txt' | Where-Object LastWriteTimeUtc -ge $started | ForEach-Object { Get-Content -LiteralPath $_.FullName }
        Get-ChildItem -LiteralPath $appDirectory -Recurse -Filter 'progress.txt' | Where-Object LastWriteTimeUtc -ge $started | ForEach-Object { Get-Content -LiteralPath $_.FullName -Tail 15 }
        if ($env:GITHUB_ACTIONS -eq 'true') {
            Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $started.ToLocalTime(); Id = 1000,1001,1026 } -ErrorAction SilentlyContinue |
                Where-Object Message -Match 'Lume.exe' | Select-Object -First 3 -ExpandProperty Message | Write-Output
        }
        throw ($stage.Name + ' 验收失败，退出码：' + $process.ExitCode)
    }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if ($result.passed -eq $false) { throw ($stage.Name + ' 未通过。') }
    Copy-Item -LiteralPath $resultPath -Destination (Join-Path $evidence ($stage.Name + '-result.json'))
    Write-Output ('PASS ' + $stage.Name)
}
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath (Join-Path $appDirectory 'Lume.exe') -ArgumentList '--performance-self-test' -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(60000)) { $process.Kill(); throw '性能与目录恢复验收超时。' }
$result = Get-ChildItem -LiteralPath (Join-Path $appDirectory 'performance-verification') -Recurse -Filter result.json | Where-Object LastWriteTimeUtc -ge $started | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if ($process.ExitCode -ne 0 -or !$result -or !(Get-Content -LiteralPath $result.FullName -Raw | ConvertFrom-Json).passed) { throw '性能与目录恢复验收失败。' }
Copy-Item -LiteralPath $result.FullName -Destination (Join-Path $evidence 'performance-result.json')
Get-FileHash -LiteralPath (Join-Path $appDirectory 'Lume.exe') -Algorithm SHA256 | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'verification-binary.json')
Write-Output ('验收完成：' + $evidence)
if ($SkipInteractive) { Write-Output '本次显式跳过真实鼠标悬停与桌面交互，不代表完整桌面验收通过。' }
if ($SkipMedia) { Write-Output '本次显式跳过媒体控件生命周期，需在桌面 Windows 上单独验收。' }
