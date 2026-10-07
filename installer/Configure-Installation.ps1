[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][string]$InstallFolder,
 [ValidateSet('Prepare','Install','Remove','Validate')][string]$Action='Validate'
)
$ErrorActionPreference='Stop'
$taskFolder=[IO.Path]::GetFullPath($InstallFolder).TrimEnd('\')
$taskExpected=Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) 'NextShare'
$taskApp=Join-Path $taskFolder 'NextShare.exe'
$taskBackend=Join-Path $taskFolder 'nextshare-quickshare.exe'
$taskGuard=Join-Path $taskFolder 'NextShare.OfflineGuard.exe'
$taskServiceName='NextShareOfflineGuard'
$taskRules=@('NextShare-Installed-TCP','NextShare-Installed-mDNS')
if($Action -eq 'Validate') {
 foreach($taskFile in @($taskApp,$taskBackend,$taskGuard)){if(!(Test-Path -LiteralPath $taskFile -PathType Leaf)){throw "Missing package file: $taskFile"}}
 [pscustomobject]@{Folder=$taskFolder;FilesPresent=$true;Service=$taskServiceName;ServiceAccount='LocalSystem';ServiceAccess='Read-only Internet Sharing check';Startup=('"'+$taskApp+'" --startup');FirewallScope='LocalSubnet';AppRunsAs='Invoker';WouldChangeSystem=$false}|ConvertTo-Json
 exit 0
}
$taskPrincipal=[Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if(![Environment]::Is64BitProcess){throw 'Installation setup must use 64-bit PowerShell.'}
if(!$taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Installation setup requires Administrator access once.'}
if(![string]::Equals($taskFolder,$taskExpected,[StringComparison]::OrdinalIgnoreCase)){throw 'Installation must use the protected Program Files\NextShare directory.'}
if(Test-Path -LiteralPath $taskFolder){
 if((Get-Item -LiteralPath $taskFolder -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'The installation directory cannot be a link.'}
 foreach($taskChild in (Get-ChildItem -LiteralPath $taskFolder -Recurse -Force)){if($taskChild.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'The installation cannot contain links.'}}
}
$taskService=Get-CimInstance Win32_Service -Filter "Name='$taskServiceName'"
if($taskService -and $taskService.PathName.Trim('"') -ne $taskGuard){throw 'A different service already uses the Next Share offline guard name.'}
function Stop-OwnedGuard {
 if($taskService -and $taskService.State -ne 'Stopped'){
  Stop-Service -Name $taskServiceName -ErrorAction Stop
  (Get-Service -Name $taskServiceName).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped,[TimeSpan]::FromSeconds(15))
 }
}
if($Action -eq 'Prepare') {
 $taskRunning=@(Get-CimInstance Win32_Process -Filter "Name='NextShare.exe'" | Where-Object {$_.ExecutablePath -and [IO.Path]::GetDirectoryName($_.ExecutablePath) -eq $taskFolder})
 if($taskRunning.Count){throw 'Exit Next Share using its system tray menu before upgrading, then run setup again. Active transfers will not be interrupted.'}
 Stop-OwnedGuard
 Write-Output 'Upgrade preparation completed.'
 exit 0
}
if($Action -eq 'Remove') {
 $taskRunning=@(Get-CimInstance Win32_Process -Filter "Name='NextShare.exe'" | Where-Object {$_.ExecutablePath -and [IO.Path]::GetDirectoryName($_.ExecutablePath) -eq $taskFolder})
 if($taskRunning.Count){throw 'Exit Next Share using its system tray menu before uninstalling. Saved files are kept.'}
 Stop-OwnedGuard
 if($taskService){$taskDelete=Invoke-CimMethod -InputObject $taskService -MethodName Delete;if($taskDelete.ReturnValue -ne 0){throw "Service removal failed: $($taskDelete.ReturnValue)"}}
 foreach($taskName in $taskRules){Get-NetFirewallRule -Name $taskName -ErrorAction SilentlyContinue | Remove-NetFirewallRule}
 Write-Output 'Offline guard and owned firewall rules removed. Received files and user preferences are kept.'
 exit 0
}
foreach($taskFile in @($taskApp,$taskBackend,$taskGuard)){
 $taskItem=Get-Item -LiteralPath $taskFile -Force
 if($taskItem.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Application executables cannot be links.'}
}
# Replace the ACL rather than retaining pre-existing broad explicit permissions.
foreach($taskChild in (Get-ChildItem -LiteralPath $taskFolder -Recurse -Force)){
 if($taskChild.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'The installation cannot contain links.'}
}
$taskAcl=New-Object Security.AccessControl.DirectorySecurity
$taskAcl.SetAccessRuleProtection($true,$false)
$taskAcl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
foreach($taskPair in @(@('S-1-5-18','FullControl'),@('S-1-5-32-544','FullControl'),@('S-1-5-32-545','ReadAndExecute'))){
 $taskRule=[Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($taskPair[0]),[Security.AccessControl.FileSystemRights]$taskPair[1],([Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit),[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow)
 $taskAcl.AddAccessRule($taskRule)
}
Set-Acl -LiteralPath $taskFolder -AclObject $taskAcl
& "$env:SystemRoot\System32\icacls.exe" (Join-Path $taskFolder '*') '/reset' '/T' '/Q' | Out-Null
if($LASTEXITCODE -ne 0){throw 'Cannot protect application files.'}
& "$env:SystemRoot\System32\icacls.exe" $taskFolder '/setowner' '*S-1-5-32-544' '/T' '/Q' | Out-Null
if($LASTEXITCODE -ne 0){throw 'Cannot protect application ownership.'}
$taskCommand='"'+$taskGuard+'"'
if($taskService){
 $taskChange=Invoke-CimMethod -InputObject $taskService -MethodName Change -Arguments @{PathName=$taskCommand;StartMode='Automatic';StartName='LocalSystem'}
 if($taskChange.ReturnValue -ne 0){throw "Service update failed: $($taskChange.ReturnValue)"}
}else{
 # Create requires CIM UInt8; UInt32 arguments are rejected rather than narrowed.
 $taskCreate=Invoke-CimMethod -CimClass (Get-CimClass -ClassName Win32_Service) -MethodName Create -Arguments @{Name=$taskServiceName;DisplayName='Next Share Offline Guard';PathName=$taskCommand;ServiceType=[byte]16;ErrorControl=[byte]1;StartMode='Automatic';DesktopInteract=$false;StartName='LocalSystem'}
 if($taskCreate.ReturnValue -ne 0){throw "Service installation failed: $($taskCreate.ReturnValue)"}
}
& "$env:SystemRoot\System32\sc.exe" description $taskServiceName 'Read-only local check that Internet Sharing is disabled for Next Share offline Wi-Fi.' | Out-Null
if($LASTEXITCODE -ne 0){throw 'Cannot set offline guard description.'}
& "$env:SystemRoot\System32\sc.exe" failure $taskServiceName 'reset=' '86400' 'actions=' 'restart/5000/restart/15000/restart/30000' | Out-Null
if($LASTEXITCODE -ne 0){throw 'Cannot configure offline guard recovery.'}
foreach($taskName in $taskRules){Get-NetFirewallRule -Name $taskName -ErrorAction SilentlyContinue | Remove-NetFirewallRule}
New-NetFirewallRule -Name $taskRules[0] -DisplayName 'Next Share local file receiver' -Group 'Next Share Installer' -Direction Inbound -Action Allow -Enabled True -Program $taskBackend -Protocol TCP -Profile Any -RemoteAddress LocalSubnet | Out-Null
New-NetFirewallRule -Name $taskRules[1] -DisplayName 'Next Share nearby discovery' -Group 'Next Share Installer' -Direction Inbound -Action Allow -Enabled True -Program $taskBackend -Protocol UDP -LocalPort 5353 -Profile Any -RemoteAddress LocalSubnet | Out-Null
Start-Service -Name $taskServiceName
(Get-Service -Name $taskServiceName).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running,[TimeSpan]::FromSeconds(15))
Write-Output 'Protected offline guard installed and started; local-subnet receiver rules configured. Internet Sharing was not enabled or modified.'
