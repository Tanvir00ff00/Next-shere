[CmdletBinding()]
param([string]$NsisCompiler='C:\Program Files (x86)\NSIS\makensis.exe')
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $taskRoot
try {
 New-Item -ItemType Directory -Path artifacts -Force | Out-Null
 if(!(Test-Path -LiteralPath $NsisCompiler)){throw 'NSIS compiler not found.'}
 & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/Test-InstallerServiceSchema.ps1 -ReportPath artifacts/installer-service-schema.json
 if($LASTEXITCODE -ne 0){throw 'Installer service arguments do not match the live Windows CIM schema.'}
 python tools/test-receive-chime.py
 if($LASTEXITCODE -ne 0){throw 'Signature channel regression failed.'}
 & (Join-Path $taskRoot 'tools\Test-InstallerStatus.ps1') -NsisCompiler $NsisCompiler
 if(!$?){throw 'Native installer status tests failed.'}
 [xml]$taskProject=Get-Content src/NextShare.App/NextShare.App.csproj -Raw
 $taskVersion=$taskProject.Project.PropertyGroup.Version
 $taskStamp=Get-Date -Format yyyyMMdd-HHmmss
 $taskPayload=Join-Path $taskRoot "artifacts\installer-payload-$taskVersion-$taskStamp"
 $taskGuard=Join-Path $taskRoot "artifacts\installer-guard-$taskVersion-$taskStamp"
 $taskSmoke=Join-Path $taskRoot "artifacts\smoke-installer-$taskVersion-$taskStamp"
 $taskOutput=Join-Path $taskRoot "artifacts\NextShare-Engine-$taskVersion.exe"
 $taskBootstrap=Join-Path $taskRoot "artifacts\installer-bootstrap-$taskVersion-$taskStamp"
 $taskPreview=Join-Path $taskRoot "artifacts\installer-preview-$taskVersion-$taskStamp"
 if(!$env:PROTOC -and (Test-Path '.tools/protoc/bin/protoc.exe')){$env:PROTOC=Join-Path $taskRoot '.tools\protoc\bin\protoc.exe'}
 if(!(Test-Path '.tools/native-vendor') -or (!(Test-Path '.cargo/config.toml') -and !(Test-Path 'artifacts/native-vendor-config.toml'))) {
  & (Join-Path $taskRoot 'tools\Prepare-NativeDependencies.ps1')
 }
 $taskNativeConfig=if(Test-Path '.cargo/config.toml'){'.cargo/config.toml'}else{'artifacts/native-vendor-config.toml'}
 cargo build --release --offline --locked --manifest-path src/NextShare.QuickShare.Native/Cargo.toml --config $taskNativeConfig
 if($LASTEXITCODE -ne 0){throw 'Native receiver build failed. Install Rust/protoc and the vendored source configuration.'}
 dotnet run --project tests/NextShare.Tests -c Release -- --report "artifacts/core-tests-$taskVersion.json"
 if($LASTEXITCODE -ne 0){throw 'Core tests failed.'}
 dotnet run --project tests/NextShare.Windows.Tests -c Release
 if($LASTEXITCODE -ne 0){throw 'Windows guard tests failed.'}
 dotnet publish src/NextShare.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $taskPayload
 if($LASTEXITCODE -ne 0){throw 'Application publish failed.'}
 dotnet publish src/NextShare.OfflineGuard -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $taskGuard
 if($LASTEXITCODE -ne 0){throw 'Offline guard publish failed.'}
 Copy-Item -LiteralPath (Join-Path $taskGuard 'NextShare.OfflineGuard.exe') -Destination $taskPayload
 $taskApp=Start-Process -FilePath (Join-Path $taskPayload 'NextShare.exe') -ArgumentList '--startup','--smoke-ui-only','--smoke-native-transfer','--data-root',('"'+$taskSmoke+'"') -WindowStyle Hidden -PassThru
 if(!$taskApp.WaitForExit(90000)){throw "Startup smoke timed out (PID $($taskApp.Id)); inspect before stopping."}
 $taskApp.Refresh()
 if($taskApp.ExitCode -ne 0){throw 'Startup smoke process failed.'}
 $taskReport=Get-Content (Join-Path $taskSmoke 'smoke-report.json') -Raw | ConvertFrom-Json
 if(!$taskReport.StartupHiddenAndReceivingOn -or !$taskReport.DefaultEnabled -or !$taskReport.NativeProtocolTest.Passed -or !$taskReport.SendUiTest.Passed -or !$taskReport.MaterialUiTest.Passed -or !$taskReport.ReceiveFeedbackTest.Passed){throw 'Startup/receiver/UI validation failed.'}
 & (Join-Path $taskRoot 'installer\Configure-Installation.ps1') -InstallFolder $taskPayload -Action Validate
 if(!$?){throw 'Installer payload validation failed.'}
 python tools/package-release.py --output $taskPayload
 if($LASTEXITCODE -ne 0){throw 'Source packaging failed.'}
 & $NsisCompiler '/V3' "/DVERSION=$taskVersion" "/DRELEASE_DIR=$taskPayload" "/DOUTPUT_FILE=$taskOutput" 'installer/NextShare.nsi'
 if($LASTEXITCODE -ne 0){throw 'Installer compilation failed.'}
 $taskEngineHash=(Get-FileHash -LiteralPath $taskOutput -Algorithm SHA256).Hash.ToLowerInvariant()
 $taskHashFile="$taskOutput.sha256"
 $taskEngineHash | Set-Content $taskHashFile -Encoding ascii
 dotnet publish src/NextShare.Installer -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true "-p:EnginePath=$taskOutput" "-p:EngineHashPath=$taskHashFile" -o $taskBootstrap
 if($LASTEXITCODE -ne 0){throw 'Custom installer publish failed.'}
 # Invoke the DLL directly for an unprivileged, explicitly labeled preview: it never runs the engine.
 $taskPreviewApp=Start-Process dotnet -ArgumentList ('"'+(Join-Path $taskRoot 'src\NextShare.Installer\bin\Release\net8.0-windows\win-x64\NextShare.Setup.dll')+'"'),'--preview','--smoke-output',('"'+$taskPreview+'"') -WindowStyle Hidden -PassThru
 if(!$taskPreviewApp.WaitForExit(30000)){throw 'Custom installer preview timed out.'}
 $taskPreviewApp.Refresh()
 if($taskPreviewApp.ExitCode -ne 0 -or !(Get-Content (Join-Path $taskPreview 'installer-smoke.json') -Raw | ConvertFrom-Json).Passed){throw 'Custom installer preview validation failed.'}
 $taskOutput=Join-Path $taskRoot "artifacts\NextShare-Setup-$taskVersion.exe"
 Copy-Item -LiteralPath (Join-Path $taskBootstrap 'NextShare.Setup.exe') -Destination $taskOutput
 $taskInstaller=Get-Item -LiteralPath $taskOutput
 $taskHash=(Get-FileHash -LiteralPath $taskOutput -Algorithm SHA256).Hash.ToLowerInvariant()
 [pscustomobject]@{Version=$taskVersion;Installer=$taskOutput;Bytes=$taskInstaller.Length;Sha256=$taskHash;Payload=$taskPayload;Smoke=$taskSmoke;InstallerPreview=$taskPreview;EngineSha256=$taskEngineHash;CustomInstaller=$true;StartupHiddenAndReceivingOn=$true;ElevatedInstallTested=$false}|ConvertTo-Json | Set-Content "artifacts/installer-build-$taskVersion.json" -Encoding utf8
 Get-Content "artifacts/installer-build-$taskVersion.json" -Raw
}finally{Pop-Location}
