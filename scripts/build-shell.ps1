param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$build = Join-Path $root 'artifacts/shell-build'
New-Item -ItemType Directory -Path $build -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw '原生右键菜单需要 Visual Studio C++ x64 编译工具。' }
# 菜单命令沿用 C# 的唯一清单，避免原生扩展与驻留进程偏离。
$source = Get-Content -LiteralPath (Join-Path $root 'src/Lume.Desktop/DesktopMenu.cs') -Raw
$items = [regex]::Matches($source, 'new\("([a-z-]+)", "([^"]+)"(, true)?\)')
# 只解析一级菜单数组；Commands 白名单是纯字符串，不会被这条正则命中。
if ($items.Count -ne 7) { throw "桌面菜单清单解析失败（期望 7 项，实际 $($items.Count) 项），请同步构建脚本。" }
$rows = foreach ($item in $items) { '    { L"' + $item.Groups[1].Value + '", L"' + $item.Groups[2].Value + '", ' + $(if ($item.Groups[3].Success) {'true'} else {'false'}) + ' },' }
Set-Content -LiteralPath (Join-Path $build 'commands.g.h') -Value (@('static const Command commands[] = {') + $rows + @('};')) -Encoding UTF8
$cpp = Join-Path $root 'src/Lume.Shell/LumeShell.cpp'
$vcvars = Join-Path $vs 'VC/Auxiliary/Build/vcvars64.bat'
$definition = Join-Path $root 'src/Lume.Shell/LumeShell.def'
$command = 'call "{0}" >nul && cl /nologo /LD /MT /O2 /W4 /WX /EHsc /utf-8 /std:c++17 /I"{1}" "{2}" /Fe:Lume.Shell.dll /link shell32.lib shlwapi.lib ole32.lib user32.lib gdi32.lib advapi32.lib uuid.lib /Brepro /DEF:"{3}"' -f $vcvars,$build,$cpp,$definition
Push-Location $build
try { & $env:ComSpec /d /c $command; if ($LASTEXITCODE -ne 0) { throw '原生菜单编译失败。' } } finally { Pop-Location }
$dll = Join-Path $build 'Lume.Shell.dll'
$hash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.Substring(0,16).ToLowerInvariant()
$name = 'Lume.Shell.' + $hash + '.dll'
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
$target = Join-Path $OutputPath $name
if (!(Test-Path -LiteralPath $target)) { Copy-Item -LiteralPath $dll -Destination $target }
Set-Content -LiteralPath (Join-Path $OutputPath 'shell-extension.txt') -Value $name -Encoding UTF8
Write-Output ('原生菜单：' + $target)
$guard = Join-Path $root 'src/Lume.Shell/LumeGuard.cpp'
$guardCommand = 'call "{0}" >nul && cl /nologo /MT /O2 /W4 /WX /EHsc /utf-8 /std:c++17 "{1}" /Fe:Lume.Guard.exe /link shell32.lib user32.lib /SUBSYSTEM:WINDOWS /Brepro' -f $vcvars,$guard
Push-Location $build
try { & $env:ComSpec /d /c $guardCommand; if ($LASTEXITCODE -ne 0) { throw '原生恢复保护进程编译失败。' } } finally { Pop-Location }
Copy-Item -LiteralPath (Join-Path $build 'Lume.Guard.exe') -Destination $OutputPath -Force
Write-Output ('原生恢复保护：' + (Join-Path $OutputPath 'Lume.Guard.exe'))
