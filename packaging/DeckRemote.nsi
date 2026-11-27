Unicode true
Name "Deck Remote"
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!cd "${__FILEDIR__}\.."

OutFile "dist\DeckRemote-${DECK_REMOTE_VERSION}-win-x64-setup.exe"
InstallDir "$PROGRAMFILES64\Deck Remote"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
!define MUI_ICON "src\DeckRemote\Assets\deck-remote.ico"

!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\DeckRemote"
!define PORT 39871
!define UDP_RULE "Deck Remote - UDP ${PORT}"
!define TCP_RULE "Deck Remote - TCP ${PORT}"

!macro CheckApplicationStopped
    System::Call 'kernel32::OpenMutexW(i 0x100000, i 0, w "DeckRemote-592ED17E") p.r0'
    ${If} $0 != 0
        System::Call 'kernel32::CloseHandle(p r0)'
        MessageBox MB_ICONEXCLAMATION|MB_OK "请从系统托盘退出 Deck Remote，再运行安装或卸载。" /SD IDOK
        Abort
    ${EndIf}
!macroend

Function .onInit
    StrCpy $INSTDIR "$PROGRAMFILES64\Deck Remote"
    ${IfNot} ${RunningX64}
        MessageBox MB_ICONSTOP|MB_OK "Deck Remote 需要 64 位 Windows。" /SD IDOK
        Abort
    ${EndIf}
    !insertmacro CheckApplicationStopped
FunctionEnd

Section "Deck Remote"
    SetRegView 64
    SetShellVarContext all
    SetOutPath "$INSTDIR\app"
    File /r /x "*.pdb" "dist\win-x64\*"
    WriteUninstaller "$INSTDIR\uninstall.exe"
    CreateShortcut "$SMPROGRAMS\Deck Remote.lnk" "$INSTDIR\app\DeckRemote.exe"
    WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayName" "Deck Remote"
    WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayVersion" "${DECK_REMOTE_VERSION}"
    WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\app\DeckRemote.exe"
    WriteRegStr HKLM "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKLM "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\uninstall.exe"'
    WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoModify" 1
    WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoRepair" 1

    nsExec::ExecToLog '"$SYSDIR\netsh.exe" advfirewall firewall delete rule name="${UDP_RULE}" program="$INSTDIR\app\DeckRemote.exe"'
    Pop $0
    nsExec::ExecToLog '"$SYSDIR\netsh.exe" advfirewall firewall delete rule name="${TCP_RULE}" program="$INSTDIR\app\DeckRemote.exe"'
    Pop $0
    nsExec::ExecToLog '"$SYSDIR\netsh.exe" advfirewall firewall add rule name="${UDP_RULE}" dir=in action=allow program="$INSTDIR\app\DeckRemote.exe" protocol=UDP localport=${PORT} profile=private remoteip=localsubnet'
    Pop $0
    nsExec::ExecToLog '"$SYSDIR\netsh.exe" advfirewall firewall add rule name="${TCP_RULE}" dir=in action=allow program="$INSTDIR\app\DeckRemote.exe" protocol=TCP localport=${PORT} profile=private remoteip=localsubnet'
    Pop $1
    ${If} $0 != 0
    ${OrIf} $1 != 0
        MessageBox MB_ICONEXCLAMATION|MB_OK "已安装 Deck Remote。Windows 防火墙规则创建失败，局域网连接可能受阻。" /SD IDOK
    ${EndIf}
SectionEnd

Section "Uninstall"
    !insertmacro CheckApplicationStopped
    SetRegView 64
    SetShellVarContext all
    StrCpy $INSTDIR "$PROGRAMFILES64\Deck Remote"
    nsExec::ExecToLog '"$SYSDIR\netsh.exe" advfirewall firewall delete rule name="${UDP_RULE}" program="$INSTDIR\app\DeckRemote.exe"'
    Pop $0
    nsExec::ExecToLog '"$SYSDIR\netsh.exe" advfirewall firewall delete rule name="${TCP_RULE}" program="$INSTDIR\app\DeckRemote.exe"'
    Pop $0
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "DeckRemote"
    DeleteRegKey HKLM "${UNINSTALL_KEY}"
    Delete "$SMPROGRAMS\Deck Remote.lnk"
    RMDir /r "$INSTDIR\app"
    Delete "$INSTDIR\uninstall.exe"
    RMDir "$INSTDIR"
SectionEnd
