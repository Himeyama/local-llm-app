Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"

Name "Local LLM"
OutFile "LocalLlmGui-${APP_VERSION}-win-x64-Setup.exe"
InstallDir "$LOCALAPPDATA\Programs\LocalLlmGui"
RequestExecutionLevel user
VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey /LANG=1041 "ProductName" "Local LLM"
VIAddVersionKey /LANG=1041 "FileDescription" "Local LLM セットアップ"
VIAddVersionKey /LANG=1041 "FileVersion" "${APP_VERSION}"
SetCompressor /SOLID lzma
!define MUI_FINISHPAGE_RUN "$INSTDIR\app\LocalLlm.Gui.exe"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_LANGUAGE "Japanese"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "Windows x64 が必要です。"
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastBuild} 22000
    MessageBox MB_ICONSTOP "Windows 11 以降が必要です。"
    Abort
  ${EndIf}
FunctionEnd

Section "Local LLM"
  SetOutPath "$INSTDIR\app"
  File /r "${SOURCE_DIR}\*"
  CreateShortcut "$SMPROGRAMS\Local LLM.lnk" "$INSTDIR\app\LocalLlm.Gui.exe"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\LocalLlmGui" "DisplayName" "Local LLM"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\LocalLlmGui" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\LocalLlmGui" "DisplayIcon" "$INSTDIR\app\LocalLlm.Gui.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\LocalLlmGui" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\LocalLlmGui" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\LocalLlmGui" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\LocalLlmGui" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\LocalLlmGui" "NoRepair" 1
SectionEnd

Section "Uninstall"
  Delete "$SMPROGRAMS\Local LLM.lnk"
  !include "${UNINSTALL_FILE}"
  Delete "$INSTDIR\Uninstall.exe"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\LocalLlmGui"
  RMDir "$INSTDIR"
SectionEnd
