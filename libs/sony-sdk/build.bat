@echo off
setlocal EnableExtensions

if "%~1"=="" goto :usage
if "%~2"=="" goto :usage

set "SOURCE_FOLDER=%~f1"
set "OUTPUT_PACKAGE=%~f2"
set "FORCE_ARG="
set "KEEP_ARG="
set "KEYSTONE_ARG="
set "REFERENCE_PACKAGE="
set "COMPRESSION_LEVEL=7"

:parse_options
if "%~3"=="" goto :build
if /I "%~3"=="force" goto :option_force
if /I "%~3"=="keep" goto :option_keep
if /I "%~3"=="keystone" goto :option_keystone
if /I "%~3"=="reference" goto :option_reference
if /I "%~3"=="compression" goto :option_compression
set "COMPRESSION_LEVEL=%~3"
goto :next_option

:option_force
set "FORCE_ARG=-Force"
goto :next_option

:option_keep
set "KEEP_ARG=-KeepIntermediate"
goto :next_option

:option_keystone
set "KEYSTONE_ARG=-KeepKeystone"
goto :next_option

:option_reference
if "%~4"=="" goto :usage
set "REFERENCE_PACKAGE=%~4"
shift /3
goto :next_option

:option_compression
if "%~4"=="" goto :usage
set "COMPRESSION_LEVEL=%~4"
shift /3

:next_option
shift /3
goto :parse_options

:build
set "REFERENCE_ARG="
if defined REFERENCE_PACKAGE set REFERENCE_ARG=-ReferencePackage "%REFERENCE_PACKAGE%"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-from-folder.ps1" ^
  -SourceFolder "%SOURCE_FOLDER%" ^
  -OutputPackage "%OUTPUT_PACKAGE%" ^
  -CompressionLevel "%COMPRESSION_LEVEL%" ^
  %REFERENCE_ARG% %FORCE_ARG% %KEEP_ARG% %KEYSTONE_ARG%
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" echo Build failed with exit code %RESULT%.
exit /b %RESULT%

:usage
echo Usage: %~nx0 "PROJECT_FOLDER" "OUTPUT.pkg" [compression_level] [reference=BASE.pkg] [force] [keep] [keystone]
echo Compression level: -4 through 9; default is 7. Also accepted: compression=LEVEL
echo Set LIBPROSPERO_TEMP_DIR to place SDK/Python temporary workspace on another drive.
echo.
echo Examples:
echo   %~nx0 "C:\project\game" "D:\build\game.pkg"
echo   %~nx0 "C:\project\game" "D:\build\game.pkg" 9 force keep
echo   %~nx0 "C:\project\game" "D:\build\game.pkg" compression=-2 force
echo   %~nx0 "C:\project\game-patch" "D:\build\game-patch.pkg" "reference=D:\build\game.pkg" force
exit /b 2
