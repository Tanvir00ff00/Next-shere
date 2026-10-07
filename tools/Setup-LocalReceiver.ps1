[CmdletBinding(SupportsShouldProcess = $true)]
param([string]$ReleaseFolder = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$receiverPath = (Resolve-Path -LiteralPath (Join-Path $ReleaseFolder 'nextshare-quickshare.exe')).Path
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script in Administrator PowerShell. It adds local-subnet firewall rules for this receiver only.'
}
$pathBytes = [Text.Encoding]::UTF8.GetBytes($receiverPath.ToUpperInvariant())
$hasher = [Security.Cryptography.SHA256]::Create()
try { $suffix = -join ($hasher.ComputeHash($pathBytes)[0..5] | ForEach-Object { $_.ToString('x2') }) }
finally { $hasher.Dispose() }
foreach ($protocol in @('TCP', 'UDP')) {
    $ruleName = "NextShare-$suffix-$protocol"
    if ($PSCmdlet.ShouldProcess($receiverPath, "Allow $protocol inbound from LocalSubnet")) {
        $existingRule = Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
        if ($existingRule) { $existingRule | Remove-NetFirewallRule }
        $ruleParameters = @{
            Name = $ruleName; DisplayName = "Next Share local receiver ($protocol)"
            Group = 'Next Share'; Direction = 'Inbound'; Action = 'Allow'; Enabled = 'True'
            Program = $receiverPath; Protocol = $protocol; Profile = 'Any'; RemoteAddress = 'LocalSubnet'
        }
        if ($protocol -eq 'UDP') { $ruleParameters.LocalPort = 5353 }
        New-NetFirewallRule @ruleParameters | Out-Null
    }
}
Write-Output 'Local receiver rules configured. Internet Sharing and router settings were not changed.'
