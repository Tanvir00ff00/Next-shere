[CmdletBinding()]
param([string]$NsisCompiler='C:\Program Files (x86)\NSIS\makensis.exe')
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskTests=Join-Path $taskRoot ('artifacts\installer-status-'+(Get-Date -Format yyyyMMdd-HHmmss))
foreach($taskCase in @('success','failure','old-stale')){New-Item -ItemType Directory -Path (Join-Path $taskTests $taskCase) -Force | Out-Null}
Push-Location $taskRoot
try {
 foreach($taskCase in @('success','failure')){
  $taskArgs=@('/V2',('/DOUTPUT_FILE='+ (Join-Path $taskTests "$taskCase\test.exe")))
  if($taskCase -eq 'failure'){$taskArgs+='/DFAIL_TEST'}
  & $NsisCompiler @taskArgs installer/StatusLogSmoke.nsi
  if($LASTEXITCODE -ne 0){throw 'NSIS status test compilation failed.'}
 }
 $taskOld=Join-Path $taskTests 'old-stale\old.nsi'
 @'
Unicode true
Name "Old status regression reproduction"
OutFile "test.exe"
RequestExecutionLevel user
SilentInstall silent
Section
 FileOpen $8 "$EXEDIR\setup-status.log" w
 FileWriteUTF16LE /BOM $8 "10$\r$\n"
 FileClose $8
 SetErrors
 FileOpen $8 "$EXEDIR\setup-status.log" a
 IfErrors 0 +3
  SetErrorLevel 1
  Abort
 FileWriteUTF16LE $8 "30$\r$\n"
 FileClose $8
SectionEnd
'@ | Set-Content $taskOld -Encoding utf8
 & $NsisCompiler /V2 $taskOld
 if($LASTEXITCODE -ne 0){throw 'Old status reproduction compilation failed.'}
 $taskCodes=@{}
 foreach($taskCase in @('success','failure','old-stale')){
  $taskProcess=Start-Process (Join-Path $taskTests "$taskCase\test.exe") -WindowStyle Hidden -PassThru
  if(!$taskProcess.WaitForExit(10000)){throw 'Status test timed out.'}
  $taskProcess.Refresh();$taskCodes[$taskCase]=$taskProcess.ExitCode
 }
 $taskLines=[IO.File]::ReadAllLines((Join-Path $taskTests 'success\setup-status.log'))
 $taskError=[IO.File]::ReadAllText((Join-Path $taskTests 'failure\setup-status.log'))
 if(($taskLines -join ',') -ne '10,30,60,85,100' -or $taskCodes['success'] -ne 0 -or $taskCodes['failure'] -eq 0 -or $taskCodes['old-stale'] -ne 1 -or !$taskError.Contains('Retain the second error line.')){throw 'NSIS status regression failed.'}
 [pscustomobject]@{Passed=$true;ActualNsisFileIoTested=$true;OldStaleFlagReproducesCode1=$true;StaleErrorCleared=$true;Stages=$taskLines;MultilineErrorPreserved=$true;SystemChanges=$false;Folder=$taskTests}|ConvertTo-Json | Tee-Object -FilePath (Join-Path $taskTests 'report.json')
}finally{Pop-Location}
