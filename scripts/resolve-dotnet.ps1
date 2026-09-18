param([string]$DotnetPath = 'dotnet')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$required = (Get-Content (Join-Path $root 'global.json') -Raw | ConvertFrom-Json).sdk.version
$candidates = @($DotnetPath)
if ($DotnetPath -eq 'dotnet') {
    $candidates += (Join-Path $root '.tools/dotnet/dotnet.exe')
    if ($env:DOTNET_ROOT) { $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet.exe') }
}
foreach ($candidate in $candidates) {
    if (!(Get-Command $candidate -ErrorAction SilentlyContinue)) { continue }
    $sdks = & $candidate --list-sdks
    if ($LASTEXITCODE -eq 0 -and ($sdks -match ('^' + [regex]::Escape($required) + ' '))) { return (Get-Command $candidate).Source }
}
throw "未找到 global.json 指定的 .NET SDK $required。请安装该版本或使用 -DotnetPath 指向 dotnet.exe。"
