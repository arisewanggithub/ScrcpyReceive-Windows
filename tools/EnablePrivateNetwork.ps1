[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [string]$ReportPath
)
$ErrorActionPreference = 'Stop'
try {
    $program = (Resolve-Path -LiteralPath $ExecutablePath).Path
    if ((Split-Path -Leaf $program) -ne 'ScrcpyReceive.App.exe') { throw 'Only the receiver executable is permitted.' }
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this optional, explicitly requested rule correction from an elevated PowerShell. No firewall changes were made.'
    }
    $blocks = @(Get-NetFirewallRule -DisplayName '*ScrcpyReceive*' |
        Where-Object { $_.Direction -eq 'Inbound' -and $_.Action -eq 'Block' -and $_.Enabled -eq 'True' -and
            ($_.Profile -eq 'Private') -and
            (($_ | Get-NetFirewallApplicationFilter).Program -eq $program) })
    foreach ($rule in $blocks) {
        $protocol = ($rule | Get-NetFirewallPortFilter).Protocol
        if ($rule.PolicyStoreSourceType -ne 'Local' -or $protocol -notin @('TCP', 'UDP')) {
            throw 'A matching block is not a local TCP/UDP rule. It was not changed.'
        }
    }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $identity = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($program.ToLowerInvariant())))).Replace('-', '').Substring(0, 16) }
    finally { $sha.Dispose() }
    $tcpName = "ScrcpyReceive-Windows-Private-TCP-$identity"
    $udpName = "ScrcpyReceive-Windows-Private-UDP-$identity"
    foreach ($name in @($tcpName, $udpName)) {
        $existing = Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue
        if ($existing -and ($existing | Get-NetFirewallApplicationFilter).Program -ne $program) {
            throw 'An existing managed rule refers to a different application; refusing to overwrite it.'
        }
    }
    if ($PSCmdlet.ShouldProcess($program, 'Allow inbound TCP 27184 and outbound discovery UDP 27183 on Private/LocalSubnet only; remove only matching local Private inbound TCP/UDP blocks')) {
        if (Get-NetFirewallRule -Name $tcpName -ErrorAction SilentlyContinue) {
            Set-NetFirewallRule -Name $tcpName -Program $program -Direction Inbound -Protocol TCP -LocalPort 27184 `
                -RemoteAddress LocalSubnet -Profile Private -Action Allow -Enabled True | Out-Null
        } else {
            New-NetFirewallRule -Name $tcpName -DisplayName 'ScrcpyReceive-Windows Private LAN control/media' `
                -Program $program -Direction Inbound -Protocol TCP -LocalPort 27184 `
                -RemoteAddress LocalSubnet -Profile Private -Action Allow -Enabled True | Out-Null
        }
        if (Get-NetFirewallRule -Name $udpName -ErrorAction SilentlyContinue) {
            Set-NetFirewallRule -Name $udpName -Program $program -Direction Outbound -Protocol UDP -RemotePort 27183 `
                -RemoteAddress LocalSubnet -Profile Private -Action Allow -Enabled True | Out-Null
        } else {
            New-NetFirewallRule -Name $udpName -DisplayName 'ScrcpyReceive-Windows Private LAN discovery' `
                -Program $program -Direction Outbound -Protocol UDP -RemotePort 27183 `
                -RemoteAddress LocalSubnet -Profile Private -Action Allow -Enabled True | Out-Null
        }
        foreach ($rule in $blocks) { Remove-NetFirewallRule -Name $rule.Name }
        $result = @{success = $true; executable = $program; removedLocalPrivateBlocks = $blocks.Count; profile = 'Private'; remoteAddress = 'LocalSubnet'}
        if ($ReportPath) { $result | ConvertTo-Json | Set-Content -LiteralPath $ReportPath -Encoding UTF8 }
        $result | ConvertTo-Json
    }
} catch {
    if ($ReportPath) { @{success = $false; error = $_.Exception.Message; detail = $_.ScriptStackTrace} | ConvertTo-Json | Set-Content -LiteralPath $ReportPath -Encoding UTF8 }
    throw
}
