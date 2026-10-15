#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidateSet('Adapters','Capture','Plan','Apply','Restore','Status','Diagnose')][string]$Action = 'Adapters',
    [string]$Adapter,
    [string]$Mac,
    [ValidateRange(5,120)][int]$Seconds = 20,
    [switch]$AcknowledgeDisconnect,
    [switch]$Json,
    [string]$TSharkPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$stateRoot = Join-Path $env:LOCALAPPDATA 'PowerR'

function Normalize-Mac([string]$Value) {
    $compact = ($Value -replace '[-:\.]', '').ToUpperInvariant()
    if ($compact -notmatch '^[0-9A-F]{12}$') { throw 'MAC inválido: use 12 dígitos hexadecimais.' }
    $first = [Convert]::ToInt32($compact.Substring(0, 2), 16)
    if ($compact -eq '000000000000' -or ($first -band 1)) { throw 'Endereço multicast ou nulo não pode ser usado como MAC de origem.' }
    return $compact
}

function Get-Target([string]$Identity) {
    if ([string]::IsNullOrWhiteSpace($Identity)) { throw 'Informe -Adapter com nome ou GUID do adaptador.' }
    $matches = @(Get-NetAdapter -IncludeHidden | Where-Object { $_.Name -eq $Identity -or $_.InterfaceGuid.ToString().Trim('{}') -eq $Identity.Trim('{}') })
    if ($matches.Count -ne 1) { throw 'Adaptador ausente ou ambíguo. Use Adapters para conferir o GUID.' }
    return $matches[0]
}

function Get-BackupPath($Target) {
    return Join-Path $stateRoot ($Target.InterfaceGuid.ToString().Trim('{}') + '.json')
}

function Get-Override($Target) {
    $property = Get-NetAdapterAdvancedProperty -Name $Target.Name -RegistryKeyword 'NetworkAddress' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $property) { return $null }
    return [string]$property.RegistryValue
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Abra o PowerShell como administrador para alterar o adaptador.' }
}

function Wait-Mac([string]$Guid, [string]$Expected) {
    for ($i = 0; $i -lt 10; $i++) {
        Start-Sleep -Seconds 2
        $current = Get-Target $Guid
        if (($current.MacAddress -replace '[-:]', '').ToUpperInvariant() -eq $Expected) { return $current }
    }
    throw 'O driver não apresentou o MAC esperado após 20 segundos. Confira Status e restaure se necessário.'
}

function Get-TShark {
    if ($TSharkPath) {
        if (-not (Test-Path -LiteralPath $TSharkPath -PathType Leaf)) { throw 'TSharkPath não existe.' }
        return (Resolve-Path -LiteralPath $TSharkPath).Path
    }
    $command = Get-Command tshark.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $default = Join-Path $env:ProgramFiles 'Wireshark\tshark.exe'
    if (Test-Path -LiteralPath $default -PathType Leaf) { return $default }
    throw 'TShark não encontrado. Instale Wireshark/Npcap ou informe -TSharkPath.'
}

function Write-Result($Value) {
    if ($Json) { $Value | ConvertTo-Json -Depth 6 -Compress } else { $Value }
}

switch ($Action) {
    'Adapters' {
        $items = @(Get-NetAdapter | Select-Object Name, InterfaceGuid, Status, MacAddress, LinkSpeed, InterfaceDescription)
        if ($Json) { Write-Result ([pscustomobject]@{ Items = $items }) } else { $items | Format-Table -AutoSize }
    }
    'Capture' {
        $target = Get-Target $Adapter
        $tshark = Get-TShark
        $guid = $target.InterfaceGuid.ToString().Trim('{}')
        $device = '\Device\NPF_{' + $guid + '}'
        if (-not $Json) { Write-Host "Capturando $Seconds segundos em $($target.Name) ($device). Nenhum pacote bruto é salvo." }
        $rows = @(& $tshark -i $device -a "duration:$Seconds" -f 'arp or ether broadcast or ether multicast or udp port 67 or udp port 68' -Y 'arp or lldp or dhcp or bootp' -T fields -e frame.time_epoch -e eth.src -e _ws.col.Protocol 2>&1)
        if ($LASTEXITCODE -ne 0) { throw "TShark falhou: $($rows -join ' ')" }
        $own = ($target.MacAddress -replace '[-:]', '').ToUpperInvariant()
        $events = foreach ($row in $rows) {
            $parts = ([string]$row) -split "`t", 3
            if ($parts.Count -lt 2) { continue }
            try { $candidate = Normalize-Mac $parts[1] } catch { continue }
            if ($candidate -eq $own) { continue }
            [pscustomobject]@{ Mac = $candidate; Protocol = if ($parts.Count -ge 3) { $parts[2] } else { '' }; Time = $parts[0] }
        }
        $results = @($events | Group-Object Mac | Sort-Object Count -Descending | ForEach-Object {
            [pscustomobject]@{ Mac = $_.Name; Packets = $_.Count; Protocols = ($_.Group.Protocol | Sort-Object -Unique) -join ', '; FirstSeen = $_.Group[0].Time }
        })
        if ($Json) { Write-Result ([pscustomobject]@{ Items = $results; Adapter = $target.Name; Seconds = $Seconds }) } else {
            $results | Format-Table -AutoSize
            Write-Host 'A origem observada não comprova que o dispositivo está na porta do switch. Confirme a topologia antes de aplicar.'
        }
    }
    'Plan' {
        $target = Get-Target $Adapter
        $desired = Normalize-Mac $Mac
        $result = [pscustomobject]@{ Adapter = $target.Name; Guid = $target.InterfaceGuid; CurrentMac = $target.MacAddress; DesiredMac = $desired; CurrentOverride = Get-Override $target; Backup = Get-BackupPath $target; DisconnectExpected = $true }
        if ($Json) { Write-Result $result } else { $result | Format-List }
    }
    'Apply' {
        Test-Administrator
        if (-not $AcknowledgeDisconnect) { throw 'Use -AcknowledgeDisconnect após conferir o plano e ter acesso local de recuperação.' }
        $target = Get-Target $Adapter
        $desired = Normalize-Mac $Mac
        $guid = $target.InterfaceGuid.ToString().Trim('{}')
        $backup = Get-BackupPath $target
        if (Test-Path -LiteralPath $backup) { throw "Já existe uma sessão para este adaptador: $backup. Restaure antes de iniciar outra." }
        if ($PSCmdlet.ShouldProcess("$($target.Name) [$guid]", "Aplicar MAC $desired e reiniciar adaptador")) {
            New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
            $state = [pscustomobject]@{ Schema = 1; Guid = $guid; Name = $target.Name; OriginalMac = Normalize-Mac $target.MacAddress; OriginalOverride = Get-Override $target; AppliedMac = $desired; CreatedAt = (Get-Date).ToUniversalTime().ToString('o') }
            $temp = "$backup.tmp"
            $state | ConvertTo-Json | Set-Content -LiteralPath $temp -Encoding UTF8
            Move-Item -LiteralPath $temp -Destination $backup -ErrorAction Stop
            try {
                Set-NetAdapter -Name $target.Name -MacAddress $desired -NoRestart -Confirm:$false
                Restart-NetAdapter -Name $target.Name -Confirm:$false
                $verified = Wait-Mac $guid $desired
                if ($Json) { Write-Result ([pscustomobject]@{ Success = $true; Adapter = $verified.Name; Mac = $verified.MacAddress; Backup = $backup }) } else { Write-Host "MAC verificado: $($verified.MacAddress). Backup: $backup" }
            } catch {
                Write-Error "Alteração não verificada. Estado preservado em $backup. Execute Status e Restore localmente. $($_.Exception.Message)"
            }
        }
    }
    'Restore' {
        Test-Administrator
        if (-not $AcknowledgeDisconnect) { throw 'Use -AcknowledgeDisconnect após preparar acesso local de recuperação.' }
        $target = Get-Target $Adapter
        $backup = Get-BackupPath $target
        if (-not (Test-Path -LiteralPath $backup)) { throw 'Não há backup desta sessão. Nenhuma alteração foi feita.' }
        $state = Get-Content -LiteralPath $backup -Raw | ConvertFrom-Json
        if ($state.Schema -ne 1 -or $state.Guid -ne $target.InterfaceGuid.ToString().Trim('{}')) { throw 'Backup incompatível com o adaptador. Nenhuma alteração foi feita.' }
        if ($PSCmdlet.ShouldProcess($target.Name, "Restaurar configuração anterior e reiniciar adaptador")) {
            if ([string]::IsNullOrEmpty($state.OriginalOverride)) {
                Remove-NetAdapterAdvancedProperty -Name $target.Name -RegistryKeyword 'NetworkAddress' -NoRestart -Confirm:$false -ErrorAction SilentlyContinue
            } else {
                Set-NetAdapterAdvancedProperty -Name $target.Name -RegistryKeyword 'NetworkAddress' -RegistryValue $state.OriginalOverride -NoRestart -Confirm:$false
            }
            Restart-NetAdapter -Name $target.Name -Confirm:$false
            $verified = Wait-Mac $state.Guid $state.OriginalMac
            Remove-Item -LiteralPath $backup
            if ($Json) { Write-Result ([pscustomobject]@{ Success = $true; Adapter = $verified.Name; Mac = $verified.MacAddress }) } else { Write-Host "MAC restaurado e verificado: $($verified.MacAddress)" }
        }
    }
    'Status' {
        $target = Get-Target $Adapter
        $backup = Get-BackupPath $target
        $result = [pscustomobject]@{ Adapter = $target.Name; Guid = $target.InterfaceGuid; Status = $target.Status; CurrentMac = $target.MacAddress; Override = Get-Override $target; Backup = if (Test-Path -LiteralPath $backup) { $backup } else { $null } }
        if ($Json) { Write-Result $result } else { $result | Format-List }
    }
    'Diagnose' {
        $target = Get-Target $Adapter
        $config = Get-NetIPConfiguration -InterfaceIndex $target.ifIndex -ErrorAction SilentlyContinue
        $stats = Get-NetAdapterStatistics -Name $target.Name -ErrorAction SilentlyContinue
        $result = [pscustomobject]@{
            Adapter = $target.Name
            Guid = $target.InterfaceGuid
            Link = $target.Status
            Mac = $target.MacAddress
            IPv4 = @($config.IPv4Address | ForEach-Object IPAddress) -join ', '
            Gateway = @($config.IPv4DefaultGateway | ForEach-Object NextHop) -join ', '
            DnsServers = @($config.DNSServer.ServerAddresses) -join ', '
            ReceivedPackets = $stats.ReceivedUnicastPackets
            SentPackets = $stats.SentUnicastPackets
        }
        if ($Json) { Write-Result $result } else {
            $result | Format-List
            Write-Host 'Diagnóstico local: link/IP/pacotes não comprovam autorização no switch ou conectividade fim a fim.'
        }
    }
}
