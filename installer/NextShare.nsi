Unicode true
!include "MUI2.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!ifndef VERSION
 !define VERSION "0.4.7"
!endif
!ifndef RELEASE_DIR
 !error "Pass RELEASE_DIR to the compiler"
!endif
!ifndef OUTPUT_FILE
 !error "Pass OUTPUT_FILE to the compiler"
!endif
Name "Next Share"
OutFile "${OUTPUT_FILE}"
InstallDir "$PROGRAMFILES64\NextShare"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
SetCompressorDictSize 64
SetDatablockOptimize on
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "Next Share"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "FileDescription" "Next Share Windows Installer"
VIAddVersionKey "FileVersion" "${VERSION}.0"
VIAddVersionKey "LegalCopyright" "Next Share"
!define MUI_ICON "..\src\NextShare.App\Assets\nextshare.ico"
!define MUI_UNICON "..\src\NextShare.App\Assets\nextshare.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "Welcome to Next Share"
!define MUI_WELCOMEPAGE_TEXT "Receive and send files locally, at their original quality.$\r$\n$\r$\nSetup requests Administrator permission once to install the offline guard and local receiver rules.$\r$\n$\r$\nNext Share runs normally after installation and starts in the tray when you sign in, with receiving on.$\r$\n$\r$\nPlease exit any running Next Share instance before upgrading."
!define MUI_FINISHPAGE_TITLE "Next Share is ready"
!define MUI_FINISHPAGE_TEXT "Open Next Share from the desktop or Start menu shortcut.$\r$\n$\r$\nIt will start automatically when you sign in to Windows. Receiving is on by default.$\r$\n$\r$\nReceived files and preferences are kept when you upgrade or uninstall."
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"
!include "StatusLog.nsh"
Function .onInit
 !insertmacro InitStatus
 ${IfNot} ${RunningX64}
  !insertmacro Fail "Next Share requires 64-bit Windows 10 or Windows 11."
 ${EndIf}
 ${IfNot} ${AtLeastWin10}
  !insertmacro Fail "Next Share requires Windows 10 or Windows 11."
 ${EndIf}
 SetRegView 64
 ReadRegStr $0 HKLM "Software\Microsoft\Windows NT\CurrentVersion" "CurrentBuildNumber"
 IntCmp $0 19041 supported unsupported supported
 unsupported:
  !insertmacro Fail "Next Share requires Windows 10 version 2004 or newer."
 supported:
 SetShellVarContext all
 # Fixed protected directory: the privileged service must never execute user-writable files.
 StrCpy $INSTDIR "$PROGRAMFILES64\NextShare"
FunctionEnd
Function RunPrepare
 ${DisableX64FSRedirection}
 nsExec::ExecToStack '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\Configure-Installation.ps1" -InstallFolder "$INSTDIR" -Action Prepare'
 Pop $0
 Pop $1
 ${EnableX64FSRedirection}
 ${If} $0 != 0
  !insertmacro Fail "Setup could not prepare the installation.$\r$\n$\r$\n$1"
 ${EndIf}
FunctionEnd
Section "Next Share" SEC_MAIN
 SectionIn RO
 InitPluginsDir
 File /oname=$PLUGINSDIR\Configure-Installation.ps1 "Configure-Installation.ps1"
 Call RunPrepare
 !insertmacro Stage 30
 ClearErrors
 SetOutPath "$INSTDIR"
 File "${RELEASE_DIR}\NextShare.exe"
 File "${RELEASE_DIR}\nextshare-quickshare.exe"
 File "${RELEASE_DIR}\NextShare.OfflineGuard.exe"
 File "${RELEASE_DIR}\README.md"
 File "${RELEASE_DIR}\THIRD-PARTY-NOTICES.md"
 File "${RELEASE_DIR}\release-manifest.json"
 File "${RELEASE_DIR}\NextShare-${VERSION}-source.zip"
 File "Configure-Installation.ps1"
 SetOutPath "$INSTDIR\licenses"
 File "${RELEASE_DIR}\licenses\*"
 SetOutPath "$INSTDIR"
 WriteUninstaller "$INSTDIR\Uninstall.exe"
 WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\NextShare" "DisplayName" "Next Share"
 WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\NextShare" "DisplayVersion" "${VERSION}"
 WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\NextShare" "InstallLocation" "$INSTDIR"
 WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\NextShare" "DisplayIcon" "$INSTDIR\NextShare.exe"
 WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\NextShare" "Publisher" "Next Share"
 WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\NextShare" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
 WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\NextShare" "NoModify" 1
 WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\NextShare" "NoRepair" 1
 ${If} ${Errors}
  !insertmacro Fail "Could not copy or register Next Share. Run the installer again to repair the installation."
 ${EndIf}
 !insertmacro Stage 60
 ${DisableX64FSRedirection}
 nsExec::ExecToStack '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\Configure-Installation.ps1" -InstallFolder "$INSTDIR" -Action Install'
 Pop $0
 Pop $1
 ${EnableX64FSRedirection}
 ${If} $0 != 0
  !insertmacro Fail "Installation setup did not finish.$\r$\n$\r$\n$1$\r$\n$\r$\nRun this installer again to repair the installation."
 ${EndIf}
 !insertmacro Stage 85
 ClearErrors
 WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Run" "NextShare" '$\"$INSTDIR\NextShare.exe$\" --startup'
 CreateDirectory "$SMPROGRAMS\Next Share"
 CreateShortcut "$SMPROGRAMS\Next Share\Next Share.lnk" "$INSTDIR\NextShare.exe"
 CreateShortcut "$SMPROGRAMS\Next Share\Uninstall.lnk" "$INSTDIR\Uninstall.exe"
 CreateShortcut "$DESKTOP\Next Share.lnk" "$INSTDIR\NextShare.exe"
 ${If} ${Errors}
  !insertmacro Fail "Startup or shortcuts could not be created. Run the installer again to repair the installation."
 ${EndIf}
 !insertmacro Stage 100
 SetErrorLevel 0
SectionEnd
Function un.onInit
 SetRegView 64
 SetShellVarContext all
 ${If} $INSTDIR != "$PROGRAMFILES64\NextShare"
  MessageBox MB_ICONSTOP "Unexpected installation directory. Run the original installer to repair Next Share."
  Abort
 ${EndIf}
FunctionEnd
Section "Uninstall"
 ${DisableX64FSRedirection}
 nsExec::ExecToStack '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$INSTDIR\Configure-Installation.ps1" -InstallFolder "$INSTDIR" -Action Remove'
 Pop $0
 Pop $1
 ${EnableX64FSRedirection}
 ${If} $0 != 0
  MessageBox MB_ICONSTOP "Uninstallation could not finish.$\r$\n$\r$\n$1"
  Abort
 ${EndIf}
 DeleteRegValue HKLM "Software\Microsoft\Windows\CurrentVersion\Run" "NextShare"
 DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\NextShare"
 Delete "$DESKTOP\Next Share.lnk"
 Delete "$SMPROGRAMS\Next Share\Next Share.lnk"
 Delete "$SMPROGRAMS\Next Share\Uninstall.lnk"
 RMDir "$SMPROGRAMS\Next Share"
 # Delete only shipped app files. Never delete received files, user settings or arbitrary directories.
 Delete "$INSTDIR\NextShare.exe"
 Delete "$INSTDIR\nextshare-quickshare.exe"
 Delete "$INSTDIR\NextShare.OfflineGuard.exe"
 Delete "$INSTDIR\README.md"
 Delete "$INSTDIR\THIRD-PARTY-NOTICES.md"
 Delete "$INSTDIR\release-manifest.json"
 Delete "$INSTDIR\NextShare-*-source.zip"
 Delete "$INSTDIR\Configure-Installation.ps1"
 Delete "$INSTDIR\licenses\GPL-3.0.txt"
 Delete "$INSTDIR\licenses\NAudio-MIT.txt"
 Delete "$INSTDIR\licenses\DotNet-MIT.txt"
 Delete "$INSTDIR\licenses\DotNet-THIRD-PARTY.txt"
 Delete "$INSTDIR\licenses\NSIS-zlib.txt"
 RMDir "$INSTDIR\licenses"
 Delete "$INSTDIR\Uninstall.exe"
 RMDir "$INSTDIR"
SectionEnd
