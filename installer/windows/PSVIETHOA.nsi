; PSVIETHOA FPKG Builder — bộ cài Windows x64 (NSIS 3, biên dịch được trên macOS/Linux bằng makensis).
; Cài ứng dụng + fpkg-cli vào Program Files, tạo lối tắt, đăng ký gỡ cài đặt và cài luôn driver Dokan kèm theo
; (msiexec im lặng) để ảnh .exfat/.ffpfsc được gắn trực tiếp — người dùng không phải cài gì thêm.
;
; Tham số biên dịch (scripts/publish-windows.sh truyền vào):
;   -DVERSION=2.1.6  -DDIST=<thư mục dist/win-x64>  -DOUTFILE=<đường dẫn Setup.exe>  -DICON=<app.ico>

Unicode true
!include "MUI2.nsh"
!include "x64.nsh"
!include "FileFunc.nsh"
!include "LogicLib.nsh"

!define APPNAME "PSVIETHOA FPKG Builder"
!define PUBLISHER "PSVIETHOA"
!define HOMEPAGE "https://github.com/thanhsondev/PSVIETHOA-FPKG-Builder"
!define REGKEY "Software\PSVIETHOA\FPKG Builder"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\PSVIETHOA FPKG Builder"

!ifndef VERSION
  !define VERSION "0.0.0"
!endif
!ifndef DIST
  !define DIST "../../dist/win-x64"
!endif
!ifndef OUTFILE
  !define OUTFILE "${DIST}/${APPNAME}-${VERSION}-win-x64-Setup.exe"
!endif
!ifndef ICON
  !define ICON "../../src/PsViethoa.FpkgBuilder.App/Assets/app.ico"
!endif

Name "${APPNAME} ${VERSION}"
OutFile "${OUTFILE}"
InstallDir "$PROGRAMFILES64\${APPNAME}"
InstallDirRegKey HKLM "${REGKEY}" "InstallDir"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
SetCompressorDictSize 64
BrandingText "PSVIETHOA — Nguyễn Thanh Sơn & Ngô Phi Phương · Main project: Drakmor"

; Phiên bản dạng số cho VIProductVersion (bản test có hậu tố như 2.2.0-test2 thì bỏ hậu tố).
!ifndef VERSION_NUMERIC
  !define VERSION_NUMERIC "${VERSION}"
!endif
VIProductVersion "${VERSION_NUMERIC}.0"
VIAddVersionKey "ProductName" "${APPNAME}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "CompanyName" "${PUBLISHER}"
VIAddVersionKey "LegalCopyright" "© 2026 PSVIETHOA — Nguyễn Thanh Sơn & Ngô Phi Phương · Main project: Drakmor (LibProsperoPkg)"
VIAddVersionKey "FileDescription" "${APPNAME} Setup"

; ---------- Giao diện ----------
!define MUI_ICON "${ICON}"
!define MUI_UNICON "${ICON}"
!define MUI_ABORTWARNING
!define MUI_LANGDLL_ALLLANGUAGES
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_FUNCTION LaunchApp
!define MUI_FINISHPAGE_RUN_TEXT "$(RunApp)"
!define MUI_UNCONFIRMPAGE_TEXT_TOP "$(UninstallNote)"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "Vietnamese"
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_RESERVEFILE_LANGDLL

LangString RunApp ${LANG_VIETNAMESE} "Mở ${APPNAME}"
LangString RunApp ${LANG_ENGLISH} "Launch ${APPNAME}"
LangString Need64 ${LANG_VIETNAMESE} "${APPNAME} cần Windows 64-bit."
LangString Need64 ${LANG_ENGLISH} "${APPNAME} requires 64-bit Windows."
LangString DokanPresent ${LANG_VIETNAMESE} "Driver Dokan đã có sẵn — bỏ qua bước cài driver."
LangString DokanPresent ${LANG_ENGLISH} "Dokan driver already present — skipping the driver step."
LangString DokanInstalling ${LANG_VIETNAMESE} "Đang bật gắn ảnh trực tiếp (cài driver Dokan kèm theo)…"
LangString DokanInstalling ${LANG_ENGLISH} "Enabling direct image mounting (installing the bundled Dokan driver)…"
LangString DokanFailed ${LANG_VIETNAMESE} "Không cài được driver Dokan (mã lỗi msiexec bên dưới). Ứng dụng vẫn chạy bình thường: ảnh sẽ được giải nén thay vì gắn; bạn có thể bật lại trong Tuỳ chọn nâng cao."
LangString DokanFailed ${LANG_ENGLISH} "The Dokan driver could not be installed (msiexec code below). The app still works: images will be extracted instead of mounted; you can enable it again in the advanced options."
LangString VcPresent ${LANG_VIETNAMESE} "Visual C++ 2015-2022 x64 đã có sẵn — bỏ qua."
LangString VcPresent ${LANG_ENGLISH} "Visual C++ 2015-2022 x64 already present — skipping."
LangString VcInstalling ${LANG_VIETNAMESE} "Đang cài Visual C++ 2015-2022 x64 cho SDK Sony…"
LangString VcInstalling ${LANG_ENGLISH} "Installing Visual C++ 2015-2022 x64 for the Sony SDK…"
LangString UninstallNote ${LANG_VIETNAMESE} "Sẽ gỡ ${APPNAME} khỏi máy. Driver Dokan (dùng chung cho nhiều ứng dụng) được giữ lại; gỡ riêng trong Ứng dụng & tính năng nếu không cần nữa."
LangString UninstallNote ${LANG_ENGLISH} "${APPNAME} will be removed. The Dokan driver (shared by other apps) is kept; remove it separately from Apps & features if you no longer need it."

; ---------- Khởi tạo ----------
Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "$(Need64)"
    Abort
  ${EndIf}
  !insertmacro MUI_LANGDLL_DISPLAY
FunctionEnd

Function un.onInit
  !insertmacro MUI_UNGETLANGUAGE
FunctionEnd

; Mở ứng dụng KHÔNG với quyền quản trị (qua explorer.exe) — app chạy elevated thì không kéo-thả được từ Explorer.
Function LaunchApp
  Exec '"$WINDIR\explorer.exe" "$INSTDIR\${APPNAME}.exe"'
FunctionEnd

; ---------- Cài đặt ----------
Section "-App"
  SetOutPath "$INSTDIR"
  File /r "${DIST}/app/"
  SetOutPath "$INSTDIR\fpkg-cli"
  File /r "${DIST}/fpkg-cli/"
  SetOutPath "$INSTDIR"

  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKLM "${REGKEY}" "InstallDir" "$INSTDIR"
  WriteRegStr HKLM "${REGKEY}" "Version" "${VERSION}"

  WriteRegStr HKLM "${UNINST_KEY}" "DisplayName" "${APPNAME}"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "${UNINST_KEY}" "Publisher" "${PUBLISHER}"
  WriteRegStr HKLM "${UNINST_KEY}" "URLInfoAbout" "${HOMEPAGE}"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\${APPNAME}.exe"
  WriteRegStr HKLM "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKLM "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKLM "${UNINST_KEY}" "EstimatedSize" "$0"

  CreateDirectory "$SMPROGRAMS\${APPNAME}"
  CreateShortcut "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk" "$INSTDIR\${APPNAME}.exe"
  CreateShortcut "$SMPROGRAMS\${APPNAME}\Gỡ cài đặt ${APPNAME}.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\${APPNAME}.exe"
SectionEnd

; Driver Dokan: cài im lặng nếu máy chưa có (dokan2.dll trong System32 thật, không qua chuyển hướng WOW64).
Section "-Dokan"
  ${DisableX64FSRedirection}
  ${If} ${FileExists} "$SYSDIR\dokan2.dll"
    DetailPrint "$(DokanPresent)"
  ${Else}
    DetailPrint "$(DokanInstalling)"
    ClearErrors
    ExecWait '"$SYSDIR\msiexec.exe" /i "$INSTDIR\redist\Dokan_x64.msi" /qn /norestart' $0
    ${If} $0 = 3010
    ${OrIf} $0 = 1641
      DetailPrint "Dokan: cần khởi động lại (msiexec $0)"
      SetRebootFlag true
    ${ElseIf} $0 != 0
      DetailPrint "Dokan: msiexec exit $0"
      MessageBox MB_ICONEXCLAMATION|MB_OK "$(DokanFailed)$\r$\n$\r$\nmsiexec: $0"
    ${Else}
      DetailPrint "Dokan: OK"
    ${EndIf}
  ${EndIf}
  ${EnableX64FSRedirection}
SectionEnd

; Visual C++ 2015-2022 x64 cho SDK Sony (prospero-pub-cmd.exe): cài im lặng nếu System32 thật chưa có msvcp140.dll / vcruntime140_1.dll.
Section "-VcRedist"
  ${DisableX64FSRedirection}
  ${If} ${FileExists} "$SYSDIR\msvcp140.dll"
  ${AndIf} ${FileExists} "$SYSDIR\vcruntime140_1.dll"
    DetailPrint "$(VcPresent)"
  ${ElseIf} ${FileExists} "$INSTDIR\redist\vc_redist.x64.exe"
    DetailPrint "$(VcInstalling)"
    ExecWait '"$INSTDIR\redist\vc_redist.x64.exe" /install /quiet /norestart' $0
    DetailPrint "vc_redist: exit $0"
    ${If} $0 = 3010
      SetRebootFlag true
    ${EndIf}
  ${EndIf}
  ${EnableX64FSRedirection}
SectionEnd

; ---------- Gỡ cài đặt ----------
Section "Uninstall"
  Delete "$DESKTOP\${APPNAME}.lnk"
  RMDir /r "$SMPROGRAMS\${APPNAME}"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKLM "${UNINST_KEY}"
  DeleteRegKey HKLM "${REGKEY}"
SectionEnd
