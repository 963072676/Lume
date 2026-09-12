param([string]$DotnetPath = 'dotnet')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$candidates = @($DotnetPath)
if ($DotnetPath -eq 'dotnet') {
    $candidates += (Join-Path $root '.tools/dotnet/dotnet.exe')
    if ($env:DOTNET_ROOT) { $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet.exe') }
}
foreach ($candidate in $candidates) {
    if (!(Get-Command $candidate -ErrorAction SilentlyContinue)) { continue }
    $sdks = & $candidate --list-sdks
    if ($LASTEXITCODE -eq 0 -and ($sdks -match '^10\.')) { return (Get-Command $candidate).Source }
}
throw '未找到 .NET 10 SDK。请使用 -DotnetPath 指向 dotnet.exe。'
