param([string]$Action = '')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$build = Join-Path $root 'artifacts/shell-build'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$source = Join-Path $root 'src/Lume.Shell/Probe.cpp'
$vcvars = Join-Path $vs 'VC/Auxiliary/Build/vcvars64.bat'
$command = 'call "{0}" >nul && cl /nologo /MT /utf-8 /I"{1}" "{2}" /Fe:Probe.exe /link shell32.lib ole32.lib user32.lib uuid.lib' -f $vcvars,$build,$source
Push-Location $build
try {
 & $env:ComSpec /d /c $command
 if ($LASTEXITCODE -ne 0) { throw '原生菜单验证器构建失败。' }
 if ($Action) { & './Probe.exe' --invoke $Action } else { & './Probe.exe' }
 if ($LASTEXITCODE -ne 0) { throw 'Windows 原生菜单未发现唯一 Lume 子菜单，或命令未执行。' }
} finally { Pop-Location }
