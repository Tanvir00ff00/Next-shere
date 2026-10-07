[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $taskRoot
try {
 New-Item -ItemType Directory -Path '.tools','artifacts','.cargo' -Force | Out-Null
 # Cargo.lock pins the registry and Git sources; generated dependencies stay out of Git.
 $taskConfig=@(& cargo vendor --locked --manifest-path src/NextShare.QuickShare.Native/Cargo.toml '.tools/native-vendor')
 if($LASTEXITCODE -ne 0){throw 'Native dependency download failed; no Cargo configuration was written.'}
 $taskText=($taskConfig -join [Environment]::NewLine)+[Environment]::NewLine
 if(!$taskText.Contains('[source.vendored-sources]')){throw 'Cargo did not return a vendored-source configuration.'}
 foreach($taskPath in @('.cargo/config.toml','artifacts/native-vendor-config.toml')) {
  [IO.File]::WriteAllText((Join-Path $taskRoot $taskPath),$taskText,[Text.UTF8Encoding]::new($false))
 }
 Write-Output 'Locked native dependencies are ready. Subsequent native builds can use --offline.'
} finally {Pop-Location}
