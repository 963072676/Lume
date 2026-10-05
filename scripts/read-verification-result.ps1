param([Parameter(Mandatory)][string]$ResultPath, [string]$Stage = '验收')
$ErrorActionPreference = 'Stop'
$result = Get-Content -LiteralPath $ResultPath -Raw | ConvertFrom-Json -NoEnumerate
# Arrays can also carry a PSObject wrapper; require the JSON object's underlying type.
if ($null -eq $result -or $result.GetType() -ne [System.Management.Automation.PSCustomObject] -or $result.passed -isnot [bool] -or !$result.passed) {
    throw [IO.InvalidDataException]::new("$Stage 结果必须是含布尔值 passed=true 的 JSON 对象。")
}
return $result
