$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixture = Join-Path $projectRoot ('artifacts/soak-status-tests-' + [Guid]::NewGuid().ToString('N'))
$results = Join-Path $fixture 'results'; New-Item -ItemType Directory -Path $results -Force | Out-Null
$exe = Join-Path $fixture 'fixture.exe'; Set-Content -LiteralPath $exe -Value 'Synthetic status fixture; never executed.'
$runPath = Join-Path $fixture 'run.json'; $resultPath = Join-Path $results 'result.json'
$started = [DateTime]::UtcNow.AddMinutes(-1)
$run = [ordered]@{ pid=2147483647;startedUtc=$started.ToString('o');minutes=1;executable=$exe;sha256=(Get-FileHash -LiteralPath $exe).Hash;resultRoot=$results;version='fixture';commit='fixture' }
$result = [ordered]@{ passed=$true;pid=$run.pid;minutes=1;activeSeconds=60;version='fixture';commit='fixture' }
$checks = [Collections.Generic.List[string]]::new()
function Check-Status($Expected, $Name) {
    $run | ConvertTo-Json | Set-Content -LiteralPath $runPath
    $result | ConvertTo-Json | Set-Content -LiteralPath $resultPath
    $observed = & (Join-Path $PSScriptRoot 'check-soak.ps1') -RunPath $runPath | ConvertFrom-Json
    if ($observed.status -ne $Expected) { throw "$Name : expected $Expected, got $($observed.status)" }
    $checks.Add($Name)
}
Check-Status 'passed' 'Matching completed fixture is accepted'
$result.pid = 0; Check-Status 'incomplete' 'Unrelated process result is rejected'; $result.pid = $run.pid
$result.minutes = 2; Check-Status 'incomplete' 'Different duration result is rejected'; $result.minutes = 1
$result.activeSeconds = 59; Check-Status 'incomplete' 'Insufficient active duration is rejected'; $result.activeSeconds = 60
$result.commit = 'different'; Check-Status 'incomplete' 'Different binary commit is rejected'; $result.commit = 'fixture'
$run.sha256 = 'different'; Check-Status 'invalid' 'Changed binary hash invalidates acceptance'; $run.sha256 = (Get-FileHash -LiteralPath $exe).Hash
$result.passed = $false; Check-Status 'failed' 'Matching failure is reported'; $result.passed = $true
$run | ConvertTo-Json | Set-Content -LiteralPath $runPath
Set-Content -LiteralPath $resultPath -Value '{partial'
$partial = & (Join-Path $PSScriptRoot 'check-soak.ps1') -RunPath $runPath | ConvertFrom-Json
if ($partial.status -ne 'incomplete') { throw 'A partial JSON report must not pass.' }; $checks.Add('Partial report is tolerated and not accepted')
$result | ConvertTo-Json | Set-Content -LiteralPath $resultPath
(Get-Item -LiteralPath $resultPath).LastWriteTimeUtc = $started.AddSeconds(-1)
$old = & (Join-Path $PSScriptRoot 'check-soak.ps1') -RunPath $runPath | ConvertFrom-Json
if ($old.status -ne 'incomplete') { throw 'A previous run result must not pass.' }; $checks.Add('Previous run report is rejected')
$report = [ordered]@{ passed=$true;checks=$checks.ToArray();fixture=$fixture;scope='synthetic status fixtures; no executable launched; no user process changed' }
$report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $projectRoot 'artifacts/soak-status-result.json')
$report | ConvertTo-Json -Depth 4
