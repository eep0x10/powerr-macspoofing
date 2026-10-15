$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$script = Join-Path $root 'PowerR.ps1'
$tokens = $null
$errors = $null
[System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$tokens, [ref]$errors) | Out-Null
if ($errors.Count -gt 0) { throw ($errors | Out-String) }
$adapters = @(Get-NetAdapter)
if ($adapters.Count -eq 0) { throw 'Nenhum adaptador disponível para smoke test.' }
$inventory = & $script -Action Adapters -Json | ConvertFrom-Json
if (@($inventory.Items).Count -ne $adapters.Count) { throw 'Inventário JSON inconsistente.' }
$guid = $adapters[0].InterfaceGuid.ToString().Trim('{}')
$plan = & $script -Action Plan -Adapter $guid -Mac '02:11:22:33:44:55' | Out-String
if ($plan -notmatch '021122334455') { throw 'Plano não exibiu MAC normalizado.' }
$planJson = & $script -Action Plan -Adapter $guid -Mac '02:11:22:33:44:55' -Json | ConvertFrom-Json
if ($planJson.DesiredMac -ne '021122334455') { throw 'Plano JSON inválido.' }
foreach ($bad in @('01:11:22:33:44:55', '00:00:00:00:00:00', 'invalid')) {
    $caught = $false
    try { & $script -Action Plan -Adapter $guid -Mac $bad -ErrorAction Stop | Out-Null } catch { $caught = $true }
    if (-not $caught) { throw "MAC inválido aceito: $bad" }
}
Write-Host 'Smoke PASS: sintaxe, inventário, plano e MACs inválidos.'
