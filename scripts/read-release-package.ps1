param(
    [Parameter(Mandatory)][string]$ZipPath,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$Source,
    [Parameter(Mandatory)][bool]$SelfContained,
    [string]$Directory
)
$ErrorActionPreference = 'Stop'
$ZipPath = [IO.Path]::GetFullPath($ZipPath)
$checksum = (Get-Content -LiteralPath ($ZipPath + '.sha256') -Raw).Trim() -split '\s+', 2
$hash = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash
if ($checksum.Count -ne 2 -or $checksum[0] -notmatch '^[a-fA-F0-9]{64}$' -or $checksum[0] -ine $hash -or $checksum[1] -cne [IO.Path]::GetFileName($ZipPath)) { throw '发布包校验文件不匹配。' }
$archive = [IO.Compression.ZipFile]::OpenRead($ZipPath)
try {
    if ($archive.Entries.Count -gt 64) { throw '发布包文件数量异常。' }
    $names = @($archive.Entries.FullName)
    $unique = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $names) {
        if ([string]::IsNullOrWhiteSpace($name) -or $name.EndsWith('.') -or $name.EndsWith(' ') -or [IO.Path]::GetFileName($name) -cne $name -or $name.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or !$unique.Add($name)) { throw '发布包包含非法路径或重复文件名。' }
        if ($name -match '(?i)(\.pdb$|verification|result\.json$|progress\.json$|resources\.jsonl$)') { throw '发布包包含调试或验收文件。' }
    }
    function Read-PackageText([string]$Name) {
        $entry = $archive.GetEntry($Name)
        if (!$entry -or $entry.Length -gt 16384) { throw ('发布包缺少文本或文本过大：' + $Name) }
        $reader = [IO.StreamReader]::new($entry.Open(), [Text.UTF8Encoding]::new($false, $true))
        try { $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    $manifest = @((Read-PackageText 'release-files.txt').TrimEnd([char[]]"`r`n") -split '\r?\n')
    $listed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in $manifest) { if ($name -ceq 'release-files.txt' -or !$unique.Contains($name) -or !$archive.GetEntry($name) -or !$listed.Add($name)) { throw '发布清单包含重复、缺失或错误名称。' } }
    if ($manifest.Count + 1 -ne $names.Count) { throw '发布包包含清单之外的文件。' }
    foreach ($required in @('Lume.exe','Lume.Guard.exe','build-info.json','LICENSE','shell-extension.txt','使用说明.md','升级与回退.md','支持与已知问题.md','验证下载.md','Shell请求隔离.md','过期引用清理.md','快速操作与搜索.md')) {
        if (!$archive.GetEntry($required)) { throw ('发布包缺少文件：' + $required) }
    }
    if (@($names | Where-Object { $_ -cmatch '^Lume\.Shell\.[a-f0-9]+\.dll$' }).Count -ne 1) { throw '发布包缺少唯一的 Shell 扩展。' }
    if ($SelfContained) {
        foreach ($runtime in @('Microsoft.NETCore.App.Runtime.win-x64','Microsoft.WindowsDesktop.App.Runtime.win-x64')) {
            if (!@($names | Where-Object { $_ -cmatch ('^' + [regex]::Escape($runtime) + '-LICENSE(?:\.TXT)?$') }).Count) { throw ('完整包缺少运行时许可证：' + $runtime) }
        }
    } elseif (!$archive.GetEntry('运行要求.txt')) { throw '精简包缺少运行要求。' }
    $info = Read-PackageText 'build-info.json' | ConvertFrom-Json -NoEnumerate
    if ($null -eq $info -or $info.GetType() -ne [System.Management.Automation.PSCustomObject]) { throw '发布构建信息必须为 JSON 对象。' }
    if ($info.version -isnot [string] -or $info.version -cne $Version -or $info.commit -isnot [string] -or $info.commit -cne $Source -or $info.runtimeIdentifier -isnot [string] -or $info.runtimeIdentifier -cne 'win-x64' -or $info.sdk -isnot [string] -or $info.sdk -notmatch '^\d+\.\d+\.\d+$') { throw '发布构建版本、源码、平台或 SDK 不匹配。' }
    if ($info.dirty -isnot [bool] -or $info.dirty -or $info.verification -isnot [bool] -or $info.verification -or $info.selfContained -isnot [bool] -or $info.selfContained -ne $SelfContained) { throw '发布构建必须干净、正式且符合所选运行环境。' }
    if ($Directory) {
        $localNames = @(Get-ChildItem -LiteralPath $Directory -Force | ForEach-Object Name)
        if (@(Compare-Object $names $localNames -CaseSensitive).Count) { throw '本地发布目录与 ZIP 清单不同。' }
        foreach ($entry in $archive.Entries) {
            $stream = $entry.Open()
            try { $zipHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
            if ((Get-FileHash -LiteralPath (Join-Path $Directory $entry.FullName) -Algorithm SHA256).Hash -cne $zipHash) { throw ('本地文件与发布包不同：' + $entry.FullName) }
        }
    }
    [ordered]@{ path=$ZipPath; bytes=(Get-Item -LiteralPath $ZipPath).Length; sha256=$hash; files=$names.Count; source=$info.commit; version=$info.version; selfContained=$info.selfContained; verification=$info.verification; dirty=$info.dirty }
} finally { $archive.Dispose() }
