param([string]$DotnetPath = 'dotnet', [ValidatePattern('^[a-f0-9]{40}$')][string]$Source)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version = ([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if (!$Source) {
    $Source = & git -C $root rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $Source -notmatch '^[a-f0-9]{40}$') { throw '无法核对发布源码提交。' }
}
$DotnetPath = & (Join-Path $PSScriptRoot 'resolve-dotnet.ps1') -DotnetPath $DotnetPath
$previousRuntime = $env:DOTNET_ROOT_X64
$env:DOTNET_ROOT_X64 = Split-Path -Parent $DotnetPath
try {
    $packages = @(); $flags = @()
    foreach ($suffix in @('', '-lite')) {
        $name = "Lume-$version-win-x64$suffix"; $directory = Join-Path $root "artifacts/$name"
        $package = & (Join-Path $PSScriptRoot 'read-release-package.ps1') -ZipPath ($directory + '.zip') -Version $version -Source $Source -SelfContained ($suffix -eq '') -Directory $directory
        $packages += $package
        foreach ($flag in @('--card-query-self-test','--accessibility-self-test','--palette-self-test','--retention-self-test','--shell-worker-self-test','--performance-self-test','--smoke')) {
            $process = Start-Process -FilePath (Join-Path $directory 'Lume.exe') -ArgumentList $flag -WindowStyle Hidden -PassThru
            if (!$process.WaitForExit(30000)) { $process.Kill(); throw ('正式包验收入口未退出：' + $flag) }
            if ($process.ExitCode -ne 2) { throw ('正式包接受了验收入口：' + $flag) }
            $flags += [ordered]@{ package=$name; flag=$flag; exitCode=$process.ExitCode }
        }
    }
    $result = [ordered]@{ passed=$true; source=$Source; version=$version; packages=$packages; rejectedEntries=$flags; desktopTakeover=$false; inputSimulation=$false }
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $root 'artifacts/release-packages-result.json') -Encoding utf8
    $result | ConvertTo-Json -Depth 6
} finally { $env:DOTNET_ROOT_X64 = $previousRuntime }
