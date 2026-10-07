!macro InitStatus
 ClearErrors
 FileOpen $8 "$EXEDIR\setup-status.log" w
 IfErrors 0 +3
  SetErrorLevel 1
  Abort
 FileWriteUTF16LE /BOM $8 "10$\r$\n"
 FileClose $8
!macroend
!macro Stage NUMBER
 ClearErrors
 FileOpen $8 "$EXEDIR\setup-status.log" a
 IfErrors 0 +3
  SetErrorLevel 1
  Abort
 FileSeek $8 0 END
 FileWriteUTF16LE $8 "${NUMBER}$\r$\n"
 FileClose $8
!macroend
!macro Fail TEXT
 ClearErrors
 FileOpen $8 "$EXEDIR\setup-status.log" a
 FileSeek $8 0 END
 FileWriteUTF16LE $8 "ERROR|${TEXT}$\r$\n"
 FileClose $8
 SetErrorLevel 1
 MessageBox MB_ICONSTOP "${TEXT}" /SD IDOK
 Abort
!macroend
