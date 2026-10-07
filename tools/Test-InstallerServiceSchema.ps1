[CmdletBinding()]
param([string]$ReportPath)
$ErrorActionPreference='Stop'
# Parse production arguments without executing privileged setup. Read the local
# WMI method schema under the same Windows PowerShell 5.1 used by the installer.
$taskSource=Join-Path $PSScriptRoot '..\installer\Configure-Installation.ps1'
$taskTokens=$null;$taskParseErrors=$null
$taskAst=[Management.Automation.Language.Parser]::ParseFile($taskSource,[ref]$taskTokens,[ref]$taskParseErrors)
if($taskParseErrors.Count){throw 'Installer script does not parse.'}
$taskCommands=@($taskAst.FindAll({param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Invoke-CimMethod'},$true))
$taskClass=Get-CimClass -ClassName Win32_Service
$taskVariables=[Collections.Generic.List[Management.Automation.PSVariable]]::new()
$taskVariables.Add([Management.Automation.PSVariable]::new('taskServiceName','NextShare-Schema-Test-NoChanges'))
$taskVariables.Add([Management.Automation.PSVariable]::new('taskCommand','"C:\Program Files\NextShare\NextShare.OfflineGuard.exe"'))
$taskTypes=@{UInt8=[byte];UInt16=[uint16];UInt32=[uint32];String=[string];Boolean=[bool];StringArray=[string[]]}
$taskResults=@()
$taskOldFailures=@()
foreach($taskCommandAst in $taskCommands){
 $taskElements=$taskCommandAst.CommandElements
 $taskMethod=$null;$taskArgumentsAst=$null
 for($taskIndex=0;$taskIndex -lt $taskElements.Count-1;$taskIndex++){
  if($taskElements[$taskIndex] -is [Management.Automation.Language.CommandParameterAst]){
   if($taskElements[$taskIndex].ParameterName -eq 'MethodName'){$taskMethod=$taskElements[$taskIndex+1].Value}
   if($taskElements[$taskIndex].ParameterName -eq 'Arguments'){$taskArgumentsAst=$taskElements[$taskIndex+1]}
  }
 }
 if($taskMethod -notin @('Create','Change')){continue}
 if($taskArgumentsAst -isnot [Management.Automation.Language.HashtableAst]){throw 'Expected production service arguments.'}
 $taskArguments=[hashtable]([scriptblock]::Create($taskArgumentsAst.Extent.Text).InvokeWithContext($null,$taskVariables,@())[0])
 $taskParameters=$taskClass.CimClassMethods[$taskMethod].Parameters
 foreach($taskKey in $taskArguments.Keys){
  $taskParameter=$taskParameters[$taskKey]
  if(!$taskParameter){throw "Unknown $taskMethod parameter: $taskKey"}
  $taskExpectedType=$taskTypes[$taskParameter.CimType.ToString()]
  if(!$taskExpectedType -or $taskArguments[$taskKey].GetType() -ne $taskExpectedType){throw "Production $taskMethod.$taskKey does not match live CIM schema."}
 }
 if($taskMethod -eq 'Create'){
  foreach($taskKey in @('ServiceType','ErrorControl')){
   if($taskParameters[$taskKey].CimType.ToString() -ne 'UInt8'){throw 'Unexpected live Windows service method schema.'}
   $taskOldFailures+=($taskKey+': UInt32 differs from '+$taskParameters[$taskKey].CimType)
  }
  if($taskArguments.ServiceType -ne 16 -or $taskArguments.ErrorControl -ne 1 -or $taskArguments.DesktopInteract -or $taskArguments.StartName -ne 'LocalSystem' -or $taskArguments.StartMode -ne 'Automatic'){throw 'Service behavior changed unexpectedly.'}
  # WhatIf reaches client-side method validation and never creates a service.
  Invoke-CimMethod -CimClass $taskClass -MethodName Create -Arguments $taskArguments -WhatIf | Out-Null
 }
 $taskResults+=@{Method=$taskMethod;ArgumentsChecked=$taskArguments.Count;MatchesLiveSchema=$true}
}
if($taskResults.Count -ne 2 -or $taskOldFailures.Count -ne 2){throw 'Both production create/update paths must be checked.'}
$taskReport=[pscustomobject]@{Passed=$true;PowerShellVersion=$PSVersionTable.PSVersion.ToString();ActualWindowsSchema=$true;ProductionArgumentAst=$true;OldTypeMismatches=$taskOldFailures;Methods=$taskResults;SystemChanges=$false}
$taskJson=$taskReport | ConvertTo-Json -Depth 4
if($ReportPath){$taskJson | Set-Content -LiteralPath $ReportPath -Encoding UTF8}
$taskJson
