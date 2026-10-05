$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixture = Join-Path $root ('artifacts/verification-result-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$cases = @(
    @{ Name='Boolean success is accepted'; Json='{"passed":true}'; Accept=$true },
    @{ Name='Additional result details are preserved'; Json='{"passed":true,"checks":["a","b"],"version":"synthetic"}'; Accept=$true },
    @{ Name='Boolean failure is rejected'; Json='{"passed":false}'; Accept=$false },
    @{ Name='Missing success field is rejected'; Json='{}'; Accept=$false },
    @{ Name='String success is rejected'; Json='{"passed":"true"}'; Accept=$false },
    @{ Name='Numeric success is rejected'; Json='{"passed":1}'; Accept=$false },
    @{ Name='Null success is rejected'; Json='{"passed":null}'; Accept=$false },
    @{ Name='Array of success objects is rejected'; Json='[{"passed":true}]'; Accept=$false },
    @{ Name='Multiple success objects are rejected'; Json='[{"passed":true},{"passed":true}]'; Accept=$false },
    @{ Name='Null root report is rejected'; Json='null'; Accept=$false },
    @{ Name='Primitive success is rejected'; Json='true'; Accept=$false },
    @{ Name='Truncated report is rejected'; Json='{"passed":'; Accept=$false },
    @{ Name='Empty report is rejected'; Json=''; Accept=$false }
)
$checks = foreach ($case in $cases) {
    $path = Join-Path $fixture ('report-' + [guid]::NewGuid().ToString('N') + '.json')
    [IO.File]::WriteAllText($path, $case.Json)
    $accepted = $false; $report = $null
    try { $report = & (Join-Path $PSScriptRoot 'read-verification-result.ps1') -ResultPath $path -Stage 'synthetic'; $accepted = $true } catch { }
    if ($accepted -ne $case.Accept) { throw ('报告校验回归失败：' + $case.Name) }
    if ($case.Name -eq 'Additional result details are preserved' -and ($report.checks.Count -ne 2 -or $report.version -cne 'synthetic')) { throw '报告细节丢失。' }
    $case.Name
}
$result = [ordered]@{ passed=$true; checks=@($checks); scope='synthetic JSON only; no desktop or user data operations' }
$result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'artifacts/verification-result-validation-result.json') -Encoding utf8
$result | ConvertTo-Json -Depth 4
