param([Parameter(Mandatory)][string]$RunPath)
$ErrorActionPreference = 'Stop'
$run = Get-Content -LiteralPath $RunPath -Raw | ConvertFrom-Json
$startedUtc = ([DateTime]$run.startedUtc).ToUniversalTime()
$process = Get-Process -Id $run.pid -ErrorAction SilentlyContinue
$sameProcess = $false
try { $sameProcess = $process -and $process.Path -eq $run.executable -and [Math]::Abs(($process.StartTime.ToUniversalTime() - $startedUtc).TotalSeconds) -lt 1 } catch { }
function Read-Report($File) {
    if (!$File) { return $null }
    try { return Get-Content -LiteralPath $File.FullName -Raw | ConvertFrom-Json }
    catch { return $null } # A writer may be between truncating and replacing a progress snapshot.
}
$resultFile = Get-ChildItem -LiteralPath $run.resultRoot -Recurse -Filter result.json -ErrorAction SilentlyContinue | Where-Object LastWriteTimeUtc -ge $startedUtc | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$progressFile = Get-ChildItem -LiteralPath $run.resultRoot -Recurse -Filter progress.json -ErrorAction SilentlyContinue | Where-Object LastWriteTimeUtc -ge $startedUtc | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$result = Read-Report $resultFile
$progress = Read-Report $progressFile
if ($progress -and ($progress.pid -ne $run.pid -or ([DateTime]$progress.utc).ToUniversalTime() -lt $startedUtc)) { $progress = $null }
$heartbeatAge = if ($progress) { ([DateTime]::UtcNow - ([DateTime]$progress.utc).ToUniversalTime()).TotalSeconds } else { $null }
$progressFresh = $null -ne $heartbeatAge -and $heartbeatAge -ge -5 -and $heartbeatAge -le 90
$hashMatches = $false
try { $hashMatches = (Get-FileHash -LiteralPath $run.executable -Algorithm SHA256).Hash -eq $run.sha256 } catch { }
$requiredResultFields = @('passed', 'pid', 'minutes', 'activeSeconds', 'version', 'commit')
$resultHasIdentity = $result -and @($requiredResultFields | Where-Object { !$result.PSObject.Properties[$_] }).Count -eq 0
$resultMatches = $resultHasIdentity -and $result.passed -is [bool] -and $result.minutes -eq $run.minutes -and $result.pid -eq $run.pid `
    -and $result.commit -ceq $run.commit -and $result.version -ceq $run.version
if ($resultMatches) {
    $resultMatches = $result.activeSeconds -is [ValueType] -and $result.activeSeconds -isnot [bool] `
        -and [double]::IsFinite([double]$result.activeSeconds) -and [double]$result.activeSeconds -ge 0 `
        -and [double]$result.activeSeconds -le ([DateTime]::UtcNow - $startedUtc).TotalSeconds + 5
}
$status = if (!$hashMatches) { 'invalid' } elseif ($resultMatches -and !$result.passed) { 'failed' } elseif ($sameProcess) {
    if ($progressFresh) { 'running' } elseif (([DateTime]::UtcNow - $startedUtc).TotalSeconds -le 90) { 'starting' } else { 'stalled' }
} elseif ($resultMatches -and $result.passed -and $result.activeSeconds -ge $run.minutes*60) { 'passed' } else { 'incomplete' }
$samplesFile = if ($progressFile) { Get-Item -LiteralPath (Join-Path $progressFile.DirectoryName 'resources.jsonl') -ErrorAction SilentlyContinue } else { $null }
$samples = @(if ($samplesFile) {
    foreach ($line in Get-Content -LiteralPath $samplesFile.FullName) {
        try { $point = $line | ConvertFrom-Json; if ($point.pid -eq $run.pid -and ([DateTime]$point.utc).ToUniversalTime() -ge $startedUtc) { $point } } catch { }
    }
})
# Report trends after the first 15 minutes. These numbers are observations, not a leak verdict.
$steady = @($samples | Where-Object activeSeconds -ge 900)
function Get-Slope($Points, $Property, [double]$Scale) {
    if ($Points.Count -lt 3) { return $null }
    $origin = [double]$Points[0].activeSeconds; $sx = 0.0; $sy = 0.0; $sxx = 0.0; $sxy = 0.0
    foreach ($point in $Points) { $x = ([double]$point.activeSeconds - $origin) / 3600; $y = [double]$point.$Property / $Scale; $sx += $x; $sy += $y; $sxx += $x*$x; $sxy += $x*$y }
    $denominator = $Points.Count*$sxx - $sx*$sx
    if ($denominator -le 0) { return $null }
    return [Math]::Round(($Points.Count*$sxy - $sx*$sy) / $denominator, 3)
}
$trend = if ($steady.Count -ge 31 -and ([double]$steady[-1].activeSeconds - [double]$steady[0].activeSeconds) -ge 900) {
    $first = $steady[0]; $last = $steady[-1]; $duration = [double]$last.activeSeconds - [double]$first.activeSeconds
    $private = $steady | Measure-Object -Property privateBytes -Minimum -Maximum
    $handles = $steady | Measure-Object -Property handles -Minimum -Maximum
    [ordered]@{ windowMinutes=[Math]::Round($duration/60, 2); samples=$steady.Count; privateMiBMin=[Math]::Round($private.Minimum/1MB, 2); privateMiBMax=[Math]::Round($private.Maximum/1MB, 2);
        privateMiBPerHour=(Get-Slope $steady 'privateBytes' 1MB); handlesMin=$handles.Minimum; handlesMax=$handles.Maximum; handlesPerHour=(Get-Slope $steady 'handles' 1);
        cpuCorePercent=$(if($duration -gt 0) { [Math]::Round(([double]$last.cpuMs - [double]$first.cpuMs)/($duration*1000)*100, 3) } else { $null }) }
} else { $null }
[ordered]@{status=$status;pid=$run.pid;sameProcess=[bool]$sameProcess;hashMatches=$hashMatches;requestedMinutes=$run.minutes;version=$run.version;commit=$run.commit;
    resultMatches=[bool]$resultMatches;progressFresh=[bool]$progressFresh;lastHeartbeatAgeSeconds=$heartbeatAge;sampleCount=$samples.Count;resourceTrend=$trend;progress=$progress;result=$result} | ConvertTo-Json -Depth 6
