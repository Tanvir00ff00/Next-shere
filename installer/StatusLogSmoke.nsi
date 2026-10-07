Unicode true
!include "StatusLog.nsh"
Name "Next Share status protocol test"
OutFile "${OUTPUT_FILE}"
RequestExecutionLevel user
SilentInstall silent
Function .onInit
 !insertmacro InitStatus
FunctionEnd
Section
 SetErrors
 !insertmacro Stage 30
 !insertmacro Stage 60
 !insertmacro Stage 85
!ifdef FAIL_TEST
 !insertmacro Fail "Deliberate test failure.$\r$\nRetain the second error line."
!else
 !insertmacro Stage 100
 SetErrorLevel 0
!endif
SectionEnd
