param([switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = [xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)
$version = $project.Project.PropertyGroup.Version | Select-Object -First 1
$name = "Lume-$version-win-x64" + $(if ($FrameworkDependent) { '-lite' } else { '' })
$directory = Join-Path $root "artifacts/$name"
$manifest = Join-Path $directory 'release-files.txt'
$files = @(Get-Content -LiteralPath $manifest) + @('release-files.txt')
$paths = foreach ($name in $files) {
    if ([IO.Path]::GetFileName($name) -ne $name -or [string]::IsNullOrWhiteSpace($name)) { throw '发布清单包含非法路径。' }
    $path = Join-Path $directory $name
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "缺少发布文件：$name" }
    $path
}
$zip = $directory + '.zip'
Compress-Archive -LiteralPath $paths -DestinationPath $zip -Force -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($zip + '.sha256') -Value ($hash + '  ' + [IO.Path]::GetFileName($zip)) -Encoding ascii
Write-Output $zip
