param(
    [string]$DotnetPath = 'dotnet',
    [string]$OutputPath,
    [switch]$FrameworkDependent,
    [switch]$Verification
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $projectRoot 'src/Lume.Desktop/Lume.Desktop.csproj'
$DotnetPath = & (Join-Path $PSScriptRoot 'resolve-dotnet.ps1') -DotnetPath $DotnetPath
$version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if (!$OutputPath) {
    $suffix = if ($Verification) { 'verification-app' } elseif ($FrameworkDependent) { "Lume-$version-win-x64-lite" } else { "Lume-$version-win-x64" }
    $OutputPath = Join-Path $projectRoot "artifacts/$suffix"
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
# Build into a new directory. Existing running releases and rollback files remain intact.
$staging = Join-Path $projectRoot ('artifacts/publish-' + [guid]::NewGuid().ToString('N'))
& (Join-Path $projectRoot 'scripts/build-shell.ps1') -OutputPath $staging
$publishArgs = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', $(if ($FrameworkDependent) {'false'} else {'true'}),
    '-p:PublishSingleFile=true', '-p:DebugType=none', '-p:DebugSymbols=false', '-p:SatelliteResourceLanguages=zh-Hans',
    "-p:EnableVerification=$($Verification.IsPresent.ToString().ToLowerInvariant())", '-o', $staging, '--nologo')
if (!$FrameworkDependent) { $publishArgs += @('-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true') }
& $DotnetPath @publishArgs
if ($LASTEXITCODE -ne 0) { throw "构建失败，日志对应产物保留在 $staging" }
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/使用说明.md') -Destination (Join-Path $staging '使用说明.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $staging 'LICENSE')
if ($FrameworkDependent) { Set-Content -LiteralPath (Join-Path $staging '运行要求.txt') -Value '需要安装 .NET 10 Windows Desktop Runtime x64。没有运行时请使用完整便携版。' -Encoding utf8 }
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
Get-ChildItem -LiteralPath $staging -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $OutputPath -Force }
$files = Get-ChildItem -LiteralPath $staging -File | Select-Object -ExpandProperty Name
Set-Content -LiteralPath (Join-Path $OutputPath 'release-files.txt') -Value $files -Encoding utf8
# The verified staging path is inside this project's artifacts directory and was created by this run.
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (!$staging.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) { throw '发布暂存路径超出工作区。' }
Remove-Item -LiteralPath $staging -Recurse -Force
$exe = Get-Item -LiteralPath (Join-Path $OutputPath 'Lume.exe')
Write-Output ('可运行程序：' + $exe.FullName)
Write-Output ('程序体积：{0:N2} MiB' -f ($exe.Length / 1MB))
