param(
    [Parameter(Mandatory)][string]$WorkingDirectory,
    [Parameter(Mandatory)][string]$Command,
    [Parameter(Mandatory)][string]$FailureMessage
)
$ErrorActionPreference = 'Stop'
$nativeOutput = [Collections.Generic.List[object]]::new()
$invocationError = $null
$nativeExitCode = $null
Push-Location -LiteralPath $WorkingDirectory
try {
    & $env:ComSpec /d /c $Command 2>&1 | ForEach-Object { $nativeOutput.Add($_) }
    $nativeExitCode = $LASTEXITCODE
}
catch { $invocationError = $_; $nativeExitCode = $LASTEXITCODE }
finally { Pop-Location }
# Pipeline consumers resolve relative paths in the caller's location.
# Preserve compiler output even when native errors terminate the invocation.
foreach ($line in $nativeOutput) { Write-Output $line }
if ($invocationError) { throw ($FailureMessage + ' ' + $invocationError.Exception.Message) }
if ($nativeExitCode -ne 0) { throw ($FailureMessage + '（退出码 ' + $nativeExitCode + '）') }
