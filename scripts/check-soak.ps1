param([Parameter(Mandatory)][string]$RunPath)
$ErrorActionPreference = 'Stop'
$run = Get-Content -LiteralPath $RunPath -Raw | ConvertFrom-Json
$startedUtc = ([DateTime]$run.startedUtc).ToUniversalTime()
$process = Get-Process -Id $run.pid -ErrorAction SilentlyContinue
$sameProcess = $process -and $process.Path -eq $run.executable -and [Math]::Abs(($process.StartTime.ToUniversalTime() - $startedUtc).TotalSeconds) -lt 1
$resultFile = Get-ChildItem -LiteralPath $run.resultRoot -Recurse -Filter result.json | Where-Object LastWriteTimeUtc -ge $startedUtc | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$progressFile = Get-ChildItem -LiteralPath $run.resultRoot -Recurse -Filter progress.json | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$result = if($resultFile) { Get-Content -LiteralPath $resultFile.FullName -Raw | ConvertFrom-Json } else { $null }
$progress = if($progressFile) { Get-Content -LiteralPath $progressFile.FullName -Raw | ConvertFrom-Json } else { $null }
$hashMatches = (Get-FileHash -LiteralPath $run.executable -Algorithm SHA256).Hash -eq $run.sha256
$status = if($sameProcess) { 'running' } elseif($result -and $result.passed -and $result.activeSeconds -ge $run.minutes*60 -and $hashMatches) { 'passed' } elseif($result -and !$result.passed) { 'failed' } else { 'incomplete' }
[ordered]@{status=$status;pid=$run.pid;sameProcess=[bool]$sameProcess;hashMatches=$hashMatches;requestedMinutes=$run.minutes;progress=$progress;result=$result} | ConvertTo-Json -Depth 6
