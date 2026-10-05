$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixture = Join-Path $projectRoot ('artifacts/native-pipeline-tests-' + [Guid]::NewGuid().ToString('N'))
$compilerDirectory = Join-Path $fixture 'compiler'
New-Item -ItemType Directory -Path $compilerDirectory -Force | Out-Null
$success = Join-Path $compilerDirectory 'success.cmd'; $failure = Join-Path $compilerDirectory 'failure.cmd'
Set-Content -LiteralPath $success -Encoding ascii -Value @('@echo off', 'echo native-pipeline-stdout', 'echo native-pipeline-stderr 1>&2', 'exit /b 0')
Set-Content -LiteralPath $failure -Encoding ascii -Value @('@echo off', 'echo native-pipeline-failed-stdout', 'echo native-pipeline-failed-stderr 1>&2', 'exit /b 7')
$helper = Join-Path $PSScriptRoot 'invoke-native-build.ps1'
$checks = [Collections.Generic.List[string]]::new()
function Check($Condition, $Name) { if (!$Condition) { throw $Name }; $checks.Add($Name) }
$originalLocation = Get-Location
Push-Location -LiteralPath $fixture
try {
    & $helper -WorkingDirectory $compilerDirectory -Command ('call "' + $success + '"') -FailureMessage 'fixture failed' | Tee-Object -FilePath 'success.log' | Out-Null
    Check ((Get-Location).Path -eq $fixture -and (Test-Path -LiteralPath (Join-Path $fixture 'success.log'))) 'Successful output resolves relative log in caller directory'
    $log = Get-Content -LiteralPath (Join-Path $fixture 'success.log') -Raw
    Check ($log.Contains('native-pipeline-stdout') -and $log.Contains('native-pipeline-stderr')) 'Success preserves stdout and stderr'
    foreach ($useNativeErrors in @($false, $true)) {
        # Exercise both supported PowerShell native-exit policies inside this fixture only.
        & {
            param($UseNativeErrors, $FixtureRoot, $CompilerRoot, $FailureBatch, $HelperPath)
            $PSNativeCommandUseErrorActionPreference = $UseNativeErrors
            $failed = $false; $failureLog = 'failure-' + $UseNativeErrors + '.log'
            try { & $HelperPath -WorkingDirectory $CompilerRoot -Command ('call "' + $FailureBatch + '"') -FailureMessage 'fixture failed' | Tee-Object -FilePath $failureLog | Out-Null }
            catch { $failed = $_.Exception.Message.Contains('fixture failed') }
            Check ($failed -and (Get-Location).Path -eq $FixtureRoot) ('Native failure is propagated and caller directory restored: ' + $UseNativeErrors)
            $log = Get-Content -LiteralPath (Join-Path $FixtureRoot $failureLog) -Raw
            Check ($log.Contains('native-pipeline-failed-stdout') -and $log.Contains('native-pipeline-failed-stderr')) ('Native failure preserves both output streams: ' + $UseNativeErrors)
        } $useNativeErrors $fixture $compilerDirectory $failure $helper
    }
}
finally { Pop-Location }
Check ((Get-Location).Path -eq $originalLocation.Path) 'Fixture restores original caller location'
$report = [ordered]@{ passed=$true;checks=$checks.ToArray();fixture=$fixture;scope='synthetic cmd output and failure; no desktop or user data operations' }
$report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $projectRoot 'artifacts/native-pipeline-result.json')
$report | ConvertTo-Json -Depth 4
