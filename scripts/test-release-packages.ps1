$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixture = Join-Path $root ('artifacts/release-package-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$version = '0.2.0-test'; $source = 'a' * 40
$cases = @(
    @{ Name='Clean full package is accepted'; Accept=$true },
    @{ Name='Clean lite package is accepted'; Lite=$true; Accept=$true },
    @{ Name='Matching local files are accepted'; Directory=$true; Accept=$true },
    @{ Name='Changed local binary is rejected'; Directory=$true; ChangedLocal=$true },
    @{ Name='Missing local binary is rejected'; Directory=$true; OmitLocal=$true },
    @{ Name='Unexpected local file is rejected'; Directory=$true; ExtraLocal=$true },
    @{ Name='Verification build is rejected'; Field='verification'; Value=$true },
    @{ Name='Dirty build is rejected'; Field='dirty'; Value=$true },
    @{ Name='String verification is rejected'; Field='verification'; Value='false' },
    @{ Name='String dirty is rejected'; Field='dirty'; Value='false' },
    @{ Name='String runtime choice is rejected'; Field='selfContained'; Value='true' },
    @{ Name='Missing verification marker is rejected'; Field='verification'; Value=$null },
    @{ Name='Wrong runtime choice is rejected'; Field='selfContained'; Value=$false },
    @{ Name='Wrong source commit is rejected'; Field='commit'; Value=('b' * 40) },
    @{ Name='Wrong version is rejected'; Field='version'; Value='0.2.0-other' },
    @{ Name='Wrong platform is rejected'; Field='runtimeIdentifier'; Value='win-arm64' },
    @{ Name='Missing SDK is rejected'; Field='sdk'; Value=$null },
    @{ Name='Array build information is rejected'; Json='array' },
    @{ Name='Primitive build information is rejected'; Json='true' },
    @{ Name='Null build information is rejected'; Json='null' },
    @{ Name='Truncated build information is rejected'; Json='{' },
    @{ Name='Oversized metadata is rejected'; Json=(' ' * 16385) },
    @{ Name='File outside manifest is rejected'; Extra='unexpected.txt' },
    @{ Name='Missing manifest entry is rejected'; ListedMissing=$true },
    @{ Name='Duplicate entry is rejected'; Extra='LICENSE' },
    @{ Name='Case collision is rejected'; Extra='license' },
    @{ Name='Parent path is rejected'; Extra='../outside.txt' },
    @{ Name='Nested Windows path is rejected'; Extra='nested\file.txt' },
    @{ Name='Debug symbols are rejected'; Extra='Lume.pdb' },
    @{ Name='Missing recovery guard is rejected'; Remove='Lume.Guard.exe' },
    @{ Name='Missing Shell extension is rejected'; Remove='Lume.Shell.abc123.dll' },
    @{ Name='Missing runtime license is rejected'; Remove='Microsoft.NETCore.App.Runtime.win-x64-LICENSE.TXT' },
    @{ Name='Missing lite requirements are rejected'; Lite=$true; Remove='运行要求.txt' },
    @{ Name='Missing manifest is rejected'; Remove='release-files.txt' },
    @{ Name='Wrong checksum is rejected'; BadHash=$true },
    @{ Name='Wrong checksum filename is rejected'; BadFilename=$true }
)
$checks = foreach ($case in $cases) {
    $info = [ordered]@{ version=$version; commit=$source; runtimeIdentifier='win-x64'; sdk='10.0.400'; dirty=$false; verification=$false; selfContained=(!$case.Lite) }
    if ($case.Field) { $info[$case.Field] = $case.Value }
    $json = $info | ConvertTo-Json -Compress
    if ($case.Json) { $json = if ($case.Json -eq 'array') { '[' + $json + ']' } else { $case.Json } }
    $entries = [ordered]@{}
    foreach ($name in @('Lume.exe','Lume.Guard.exe','LICENSE','shell-extension.txt','Lume.Shell.abc123.dll','使用说明.md','升级与回退.md','支持与已知问题.md','验证下载.md','Shell请求隔离.md','过期引用清理.md','快速操作与搜索.md')) { $entries[$name] = 'Synthetic text only, not an executable.' }
    $entries['build-info.json'] = $json
    if ($case.Lite) { $entries['运行要求.txt'] = 'synthetic' }
    else { $entries['Microsoft.NETCore.App.Runtime.win-x64-LICENSE.TXT'] = 'synthetic'; $entries['Microsoft.WindowsDesktop.App.Runtime.win-x64-LICENSE'] = 'synthetic' }
    if ($case.Remove -and $case.Remove -ne 'release-files.txt') { $entries.Remove($case.Remove) }
    $manifest = @($entries.Keys)
    if ($case.ListedMissing) { $manifest += 'missing.md' }
    if ($case.Remove -ne 'release-files.txt') { $entries['release-files.txt'] = ($manifest -join "`n") + "`n" }
    $path = Join-Path $fixture ([guid]::NewGuid().ToString('N') + '.zip')
    $archive = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $entries.Keys) {
            $writer = [IO.StreamWriter]::new($archive.CreateEntry($name).Open(), [Text.UTF8Encoding]::new($false))
            try { $writer.Write($entries[$name]) } finally { $writer.Dispose() }
        }
        if ($case.Extra) { $writer = [IO.StreamWriter]::new($archive.CreateEntry($case.Extra).Open()); try { $writer.Write('synthetic') } finally { $writer.Dispose() } }
    } finally { $archive.Dispose() }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($case.BadHash) { $hash = '0' * 64 }
    $filename = if ($case.BadFilename) { 'different.zip' } else { [IO.Path]::GetFileName($path) }
    [IO.File]::WriteAllText($path + '.sha256', $hash + '  ' + $filename, [Text.Encoding]::ASCII)
    $directory = $null
    if ($case.Directory) {
        $directory = $path + '-files'; New-Item -ItemType Directory -Path $directory | Out-Null
        foreach ($name in $entries.Keys) { if (!$case.OmitLocal -or $name -ne 'Lume.exe') { [IO.File]::WriteAllText((Join-Path $directory $name), $entries[$name], [Text.UTF8Encoding]::new($false)) } }
        if ($case.ChangedLocal) { [IO.File]::WriteAllText((Join-Path $directory 'Lume.exe'), 'Changed synthetic text, not an executable.') }
        if ($case.ExtraLocal) { [IO.File]::WriteAllText((Join-Path $directory 'unexpected.txt'), 'synthetic') }
    }
    $accepted = $false
    try { $null = & (Join-Path $PSScriptRoot 'read-release-package.ps1') -ZipPath $path -Version $version -Source $source -SelfContained (!$case.Lite) -Directory $directory; $accepted = $true } catch { if ($case.Accept) { throw } }
    if ($accepted -ne [bool]$case.Accept) { throw ('发布包校验回归失败：' + $case.Name) }
    $case.Name
}
$result = [ordered]@{ passed=$true; checks=@($checks); scope='synthetic ZIP and JSON only; binaries are text and are never executed' }
$result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'artifacts/release-package-validation-result.json') -Encoding utf8
$result | ConvertTo-Json -Depth 4
